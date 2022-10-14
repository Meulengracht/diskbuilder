using System;
using System.IO;
using System.Text;

namespace OSBuilder.FileSystems.MFS
{
    public class FileSystem : IFileSystem
    {
        public static readonly byte TYPE = 0x61;

        // File flags for mfs
        //uint32_t Flags;             // 0x00 - Record Flags
        //uint32_t StartBucket;       // 0x04 - First data bucket
        //uint32_t StartLength;       // 0x08 - Length of first data bucket
        //uint32_t RecordChecksum;        // 0x0C - Checksum of record excluding this entry + inline data
        //uint64_t DataChecksum;      // 0x10 - Checksum of data
        //DateTimeRecord_t CreatedAt;         // 0x18 - Created timestamp
        //DateTimeRecord_t ModifiedAt;            // 0x20 - Last modified timestamp
        //DateTimeRecord_t AccessedAt;            // 0x28 - Last accessed timestamp
        //uint64_t Size;              // 0x30 - Size of data (Set size if sparse)
        //uint64_t AllocatedSize;     // 0x38 - Actual size allocated
        //uint32_t SparseMap;         // 0x40 - Bucket of sparse-map
        //uint8_t Name[300];          // 0x44 - Record name (150 UTF16)
        //VersionRecord_t Versions[4];// 0x170 - Record Versions
        //uint8_t Integrated[512];	// 0x200

        private static readonly uint MFS_ENDOFCHAIN = 0xFFFFFFFF;
        private static readonly int  MFS_RECORDSIZE = 1024;
        private static readonly uint MFS_EXPANDSIZE = 8;

        private static readonly uint KILOBYTE = 1024;
        private static readonly uint MEGABYTE = (KILOBYTE * 1024);
        private static readonly ulong GIGABYTE = (MEGABYTE * 1024);

        private String _partitionName;
        private Guid _partitionGuid;
        private bool _bootable = false;
        private PartitionFlags _partitionFlags = 0;
        private IDisk _disk = null;
        private BucketMap _bucketMap = null;
        private ulong _sector = 0;
        private ulong _sectorCount = 0;
        private ushort _reservedSectorCount = 0;
        private ushort _bucketSize = 0;
        private string _vbrImage = null;
        private string _reservedSectorsImage = null;

        uint CalculateChecksum(Byte[] Data, int SkipIndex, int SkipLength)
        {
            uint Checksum = 0;
            for (int i = 0; i < Data.Length; i++) {
                if (i >= SkipIndex && i < (SkipIndex + SkipLength)) {
                    continue;
                }
                Checksum += Data[i];
            }
            return Checksum;
        }

        void FillBucketChain(uint Bucket, uint BucketLength, byte[] Data)
        {
            UInt32 BucketLengthItr = BucketLength;
            UInt32 BucketPtr = Bucket;
            Int64 Index = 0;

            // Iterate through the data and write it to the buckets
            while (Index < Data.LongLength) {
                Byte[] Buffer = new Byte[(_bucketSize * _disk.Geometry.BytesPerSector) * BucketLengthItr];

                // Copy the data to the buffer manually
                for (int i = 0; i < Buffer.Length; i++) {
                    if (Index + i >= Data.LongLength)
                        Buffer[i] = 0;
                    else
                        Buffer[i] = Data[Index + i];
                }

                // Increase the pointer
                Index += Buffer.Length;

                // Calculate which sector we should write the data too
                _disk.Write(Buffer, BucketToSector(BucketPtr), true);

                // Get next bucket cluster for writing
                BucketPtr = _bucketMap.GetBucketLengthAndLink(BucketPtr, out BucketLengthItr);

                // Are we at end of pointer?
                if (BucketPtr == MFS_ENDOFCHAIN) {
                    break;
                }

                // Get length of new bucket
                _bucketMap.GetBucketLengthAndLink(BucketPtr, out BucketLengthItr);
            }
        }

        private void SaveNextAvailableBucket()
        {
            byte[] bootsector = _disk.Read(_sector, 1);

            // Get relevant locations
            ulong MasterRecordSector = BitConverter.ToUInt64(bootsector, 28);
            ulong MasterRecordMirrorSector = BitConverter.ToUInt64(bootsector, 36);
            
            // Update the master-record to reflect the new index
            byte[] masterRecord = _disk.Read(_sector + MasterRecordSector, 1);
            masterRecord[76] = (Byte)(_bucketMap.NextFreeBucket & 0xFF);
            masterRecord[77] = (Byte)((_bucketMap.NextFreeBucket >> 8) & 0xFF);
            masterRecord[78] = (Byte)((_bucketMap.NextFreeBucket >> 16) & 0xFF);
            masterRecord[79] = (Byte)((_bucketMap.NextFreeBucket >> 24) & 0xFF);
            _disk.Write(masterRecord, _sector + MasterRecordSector, true);
            _disk.Write(masterRecord, _sector + MasterRecordMirrorSector, true);
        }

        private void EnsureBucketSpace(MfsRecord record, ulong size)
        {
            if (size < record.AllocatedSize)
                return;

            // Calculate only the difference in allocation size
            ulong sectorCount = (size - record.AllocatedSize) / _disk.Geometry.BytesPerSector;
            if (((size - record.AllocatedSize) % _disk.Geometry.BytesPerSector) > 0)
                sectorCount++;
            uint bucketCount = (uint)(sectorCount / _bucketSize);
            if ((sectorCount % _bucketSize) > 0)
                bucketCount++;

            // Do the allocation
            Utils.Logger.Instance.Debug("  - allocating " + bucketCount.ToString() + " buckets");

            uint initialBucketSize = 0;
            uint bucketAllocation = _bucketMap.AllocateBuckets(bucketCount, out initialBucketSize);
            Utils.Logger.Instance.Debug("  - allocated bucket " + bucketAllocation.ToString());

            // Iterate to end of data chain, but keep a pointer to the previous
            uint BucketPtr = record.Bucket;
            uint bucketPrevPtr = MFS_ENDOFCHAIN;
            uint BucketLength = 0;
            while (BucketPtr != MFS_ENDOFCHAIN) {
                bucketPrevPtr = BucketPtr;
                BucketPtr = _bucketMap.GetBucketLengthAndLink(BucketPtr, out BucketLength);
            }

            // Update the last link to the newly allocated, we only do this if 
            // the previous one was not end of chain (none allocated for record)
            if (bucketPrevPtr != MFS_ENDOFCHAIN)
                _bucketMap.SetNextBucket(bucketPrevPtr, bucketAllocation);

            // Update the allocated size in cached
            record.AllocatedSize += (bucketCount * _bucketSize * _disk.Geometry.BytesPerSector);

            // Initiate the bucket in the record if it was new
            if (record.Bucket == MFS_ENDOFCHAIN)
            {
                record.Bucket = bucketAllocation;
                record.BucketLength = bucketCount;
            }
        }

        static private string SafePath(string path)
        {
            return path.Replace('\\', '/').Trim('/');
        }

        static private bool IsRecordInUse(MfsRecord record)
        {
            return record.Flags.HasFlag(RecordFlags.InUse);
        }

        static private int GetRecordNameLength(byte[] buffer, int offset)
        {
            int length = 0;
            while (buffer[offset + 68 + length] != 0)
                length++;
            return length;
        }

        static private string GetRecordName(byte[] buffer, int offset)
        {
            int length = GetRecordNameLength(buffer, offset);
            return Encoding.UTF8.GetString(buffer, offset + 68, length);
        }

        static private MfsRecord ParseRecord(byte[] buffer, int offset, uint directoryBucket, uint directoryBucketLength)
        {
            MfsRecord record = new MfsRecord();
            record.Name = GetRecordName(buffer, offset);
            record.Flags = (RecordFlags)BitConverter.ToUInt32(buffer, offset);
            record.Size = BitConverter.ToUInt64(buffer, offset + 48);
            record.AllocatedSize = BitConverter.ToUInt64(buffer, offset + 56);
            record.Bucket = BitConverter.ToUInt32(buffer, offset + 4);
            record.BucketLength = BitConverter.ToUInt32(buffer, offset + 8);

            record.DirectoryBucket = directoryBucket;
            record.DirectoryLength = directoryBucketLength;
            record.DirectoryIndex = (uint)(offset / MFS_RECORDSIZE);
            return record;
        }

        static private void WriteRecord(byte[] buffer, int offset, MfsRecord record)
        {
            byte[] name = Encoding.UTF8.GetBytes(record.Name);
            Array.Copy(name, 0, buffer, offset + 68, name.Length);
            Array.Copy(BitConverter.GetBytes((uint)record.Flags), 0, buffer, offset, 4);
            Array.Copy(BitConverter.GetBytes(record.Bucket), 0, buffer, offset + 4, 4);
            Array.Copy(BitConverter.GetBytes(record.BucketLength), 0, buffer, offset + 8, 4);
            Array.Copy(BitConverter.GetBytes(record.Size), 0, buffer, offset + 48, 8);
            Array.Copy(BitConverter.GetBytes(record.AllocatedSize), 0, buffer, offset + 56, 8);
        }

        private MfsRecord FindRecord(uint directoryBucket, string recordName)
        {
            uint bucketLength = 0;
            uint currentBucket = directoryBucket;
            while (true)
            {
                uint bucketLink = _bucketMap.GetBucketLengthAndLink(currentBucket, out bucketLength);
                var  bucketBuffer = _disk.Read(BucketToSector(currentBucket), _bucketSize * bucketLength);
                
                var bytesToIterate = _bucketSize * _disk.Geometry.BytesPerSector * bucketLength;
                for (int i = 0; i < bytesToIterate; i += MFS_RECORDSIZE)
                {
                    var record = ParseRecord(bucketBuffer, i, currentBucket, bucketLength);
                    if (!IsRecordInUse(record))
                        continue;

                    if (record.Name == recordName)
                        return record;
                }

                if (bucketLink == MFS_ENDOFCHAIN)
                    break;
                currentBucket = bucketLink;
            }
            return null;
        }

        private void InitiateDirectoryRecord(MfsRecord record)
        {
            uint initialBucketSize = 0;
            uint bucket = _bucketMap.AllocateBuckets(MFS_EXPANDSIZE, out initialBucketSize);

            // Wipe the new bucket to zeros
            byte[] wipeBuffer = new byte[_bucketSize * _disk.Geometry.BytesPerSector * initialBucketSize];
            _disk.Write(wipeBuffer, BucketToSector(bucket), true);

            record.Bucket = bucket;
            record.BucketLength = initialBucketSize;
        }

        private uint ExpandDirectory(uint lastBucket)
        {
            uint initialBucketSize = 0;
            uint bucket = _bucketMap.AllocateBuckets(MFS_EXPANDSIZE, out initialBucketSize);
            _bucketMap.SetNextBucket(lastBucket, bucket);
            
            // Wipe the new bucket to zeros
            byte[] wipeBuffer = new byte[_bucketSize * _disk.Geometry.BytesPerSector * initialBucketSize];
            _disk.Write(wipeBuffer, BucketToSector(bucket), true);
            return bucket;
        }

        private void UpdateRecord(MfsRecord record)
        {
            Utils.Logger.Instance.Debug($"UpdateRecord(record={record.Name})");
            Utils.Logger.Instance.Debug($"UpdateRecord reading sector {BucketToSector(record.DirectoryBucket)}, length {_bucketSize * record.DirectoryLength}");
            var bucketBuffer = _disk.Read(BucketToSector(record.DirectoryBucket), _bucketSize * record.DirectoryLength);
            var offset = record.DirectoryIndex * MFS_RECORDSIZE;
            Utils.Logger.Instance.Debug($"UpdateRecord record offset at {offset}");
            WriteRecord(bucketBuffer, (int)offset, record);
            _disk.Write(bucketBuffer, BucketToSector(record.DirectoryBucket), true);
        }

        private MfsRecord CreateRecord(uint directoryBucket, string recordName, RecordFlags flags)
        {
            Utils.Logger.Instance.Debug("CreateRecord(" + directoryBucket.ToString() + ", " + recordName + ")");
            uint bucketLength = 0;
            uint currentBucket = directoryBucket;
            while (true)
            {
                Utils.Logger.Instance.Debug($"CreateRecord retrieving link and length of bucket {currentBucket}");
                uint bucketLink = _bucketMap.GetBucketLengthAndLink(currentBucket, out bucketLength);
                Utils.Logger.Instance.Debug($"CreateRecord reading sector {BucketToSector(currentBucket)}, count {_bucketSize * bucketLength}");
                var  bucketBuffer = _disk.Read(BucketToSector(currentBucket), _bucketSize * bucketLength);
                
                var bytesToIterate = _bucketSize * _disk.Geometry.BytesPerSector * bucketLength;
                for (int i = 0; i < bytesToIterate; i += MFS_RECORDSIZE)
                {
                    Utils.Logger.Instance.Debug($"CreateRecord parsing record {i}");
                    var record = ParseRecord(bucketBuffer, i, currentBucket, bucketLength);
                    if (IsRecordInUse(record))
                        continue;
                    
                    Utils.Logger.Instance.Debug($"CreateRecord record {i} was available");
                    record.Name = recordName;
                    record.Flags = flags | RecordFlags.InUse;
                    record.Bucket = MFS_ENDOFCHAIN;
                    record.BucketLength = 0;
                    record.AllocatedSize = 0;
                    record.Size = 0;
                    if (flags.HasFlag(RecordFlags.Directory))
                        InitiateDirectoryRecord(record);
                    UpdateRecord(record);
                    return record;
                }

                if (bucketLink == MFS_ENDOFCHAIN)
                    currentBucket = ExpandDirectory(currentBucket);
                else
                    currentBucket = bucketLink;
            }
        }

        private RecordFlags GetRecordFlags(FileFlags fileFlags)
        {
            RecordFlags recFlags = 0;

            if (fileFlags.HasFlag(FileFlags.Directory)) recFlags |= RecordFlags.Directory;
            if (fileFlags.HasFlag(FileFlags.System))    recFlags |= RecordFlags.System;

            return recFlags;
        }

        private MfsRecord CreatePath(uint directoryBucket, string path, FileFlags fileFlags)
        {
            var safePath = SafePath(path);
            Utils.Logger.Instance.Debug("CreatePath(" + directoryBucket.ToString() + ", " + safePath + ")");

            // split path into tokens
            var tokens = safePath.Split('/');

            uint startBucket = directoryBucket;
            for (int i = 0; i < tokens.Length; i++) {
                var token = tokens[i];
                var isLast = i == tokens.Length - 1;
                var flags = isLast ? GetRecordFlags(fileFlags) : RecordFlags.Directory;
                
                // skip empty tokens
                if (token == "") {
                    continue;
                }
                
                // find the token in the bucket
                var record = FindRecord(startBucket, token);
                if (record == null)
                {
                    record = CreateRecord(startBucket, token, flags);
                    if (record == null)
                    {
                        Utils.Logger.Instance.Error($"Failed to create record {token} in path {safePath}");
                        break;
                    }
                }

                // make sure record is a directory, should be if we just
                // created it tho
                if (!isLast && !record.Flags.HasFlag(RecordFlags.Directory))
                {
                    Utils.Logger.Instance.Error($"Record {token} in path {safePath} is not a directory");
                    break;
                }

                // successful termination condition
                if (isLast)
                    return record;
                
                startBucket = record.Bucket;
            }
            return null;
        }

        private void ListRecords(uint directoryBucket)
        {
            uint bucketLength = 0;
            uint currentBucket = directoryBucket;
            while (true)
            {
                uint bucketLink = _bucketMap.GetBucketLengthAndLink(currentBucket, out bucketLength);
                var  bucketBuffer = _disk.Read(BucketToSector(currentBucket), _bucketSize * bucketLength);
                
                var bytesToIterate = _bucketSize * _disk.Geometry.BytesPerSector * bucketLength;
                for (int i = 0; i < bytesToIterate; i += MFS_RECORDSIZE)
                {
                    var record = ParseRecord(bucketBuffer, i, currentBucket, bucketLength);
                    if (!IsRecordInUse(record))
                        continue;
                    
                    if (record.Flags.HasFlag(RecordFlags.Directory))
                        Console.WriteLine("{0} (directory)", record.Name);
                    else
                        Console.WriteLine("{0} (file)", record.Name);
                }

                if (bucketLink == MFS_ENDOFCHAIN)
                    break;
                currentBucket = bucketLink;
            }
        }

        private void ListPath(uint directoryBucket, string path)
        {
            var safePath = SafePath(path);
            Utils.Logger.Instance.Debug("ListPath(" + directoryBucket.ToString() + ", " + safePath + ")");

            // split path into tokens
            var tokens = safePath.Split('/');

            uint startBucket = directoryBucket;
            for (int i = 0; i < tokens.Length; i++) {
                var token = tokens[i];
                
                // skip empty tokens
                if (token == "") {
                    continue;
                }
                
                // find the token in the bucket
                var record = FindRecord(directoryBucket, token);
                if (record == null)
                {
                    Utils.Logger.Instance.Error($"Failed to find record {token} in path {safePath}");
                    return;
                }

                // make sure record is a directory, should be if we just
                // created it tho
                if (!record.Flags.HasFlag(RecordFlags.Directory))
                {
                    Utils.Logger.Instance.Error($"Record {token} in path {safePath} is not a directory");
                    break;
                }
                startBucket = record.Bucket;
            }
            
            // now list the directory
            ListRecords(startBucket);
        }

        private MfsRecord CreateRootRecord()
        {
            return new MfsRecord {
                Name = "<root>",
                Flags = RecordFlags.Directory | RecordFlags.System,
            };
        }

        private MfsRecord FindPath(uint directoryBucket, string path)
        {
            var safePath = SafePath(path);
            Utils.Logger.Instance.Debug("FindPath(" + directoryBucket.ToString() + ", " + safePath + ")");

            // If the root path was specified (/ or empty), then we must fake the root
            // record for MFS
            if (string.IsNullOrWhiteSpace(safePath)) {
                return CreateRootRecord();
            }

            // split path into tokens
            var tokens = safePath.Split('/');

            uint startBucket = directoryBucket;
            MfsRecord record = null;
            for (int i = 0; i < tokens.Length; i++) {
                var token = tokens[i];
                
                // skip empty tokens
                if (token == "") {
                    continue;
                }
                
                // find the token in the bucket
                record = FindRecord(startBucket, token);
                if (record == null)
                {
                    Utils.Logger.Instance.Error($"Failed to find record {token} in path {safePath}");
                    return null;
                }

                // make sure record is a directory, should be if we just
                // created it tho
                if (!record.Flags.HasFlag(RecordFlags.Directory))
                {
                    Utils.Logger.Instance.Error($"Record {token} in path {safePath} is not a directory");
                    record = null;
                    break;
                }
                startBucket = record.Bucket;
            }
            return record;
        }

        public FileSystem(IDisk disk, ulong startSector, ulong sectorCount)
        {
            _disk = disk;
            _sector = startSector;

            // parse the virtual boot record for info we need
            // name, bootable, partition type, etc
            byte[] vbr = disk.Read(startSector, 1);

            _bootable = vbr[8] != 0;
            _sectorCount = BitConverter.ToUInt64(vbr, 16);
            _reservedSectorCount = BitConverter.ToUInt16(vbr, 24);
            _bucketSize = BitConverter.ToUInt16(vbr, 26);
            
            // parse the master boot record
            var masterRecordOffset = BitConverter.ToUInt64(vbr, 28);
            byte[] masterRecord = disk.Read(startSector + masterRecordOffset, 1);

            _partitionFlags = (PartitionFlags)BitConverter.ToUInt32(masterRecord, 4);
            _partitionName = Encoding.UTF8.GetString(masterRecord, 12, 64);

            var bucketMapOffset = BitConverter.ToUInt64(masterRecord, 92);
            var freeBucketIndex = BitConverter.ToUInt32(masterRecord, 76);

            _bucketMap = new BucketMap(_disk, 
                (_sector + _reservedSectorCount), 
                (_sectorCount - _reservedSectorCount),
                _bucketSize);
            _bucketMap.Open(bucketMapOffset, freeBucketIndex);
        }

        public FileSystem(string partitionName, Guid partitionGuid, FileSystemAttributes attributes)
        {
            _partitionName = partitionName;
            _partitionGuid = partitionGuid;
            if (attributes.HasFlag(FileSystemAttributes.Boot)) {
                _bootable = true;
            }
            
            if (partitionGuid == DiskLayouts.GPTGuids.ValiSystemPartition) {
                _partitionFlags |= PartitionFlags.SystemDrive;
            }
            if (partitionGuid == DiskLayouts.GPTGuids.ValiDataUserPartition ||
                partitionGuid == DiskLayouts.GPTGuids.ValiDataPartition) {
                _partitionFlags |= PartitionFlags.DataDrive;
            }
            if (partitionGuid == DiskLayouts.GPTGuids.ValiDataUserPartition ||
                partitionGuid == DiskLayouts.GPTGuids.ValiUserPartition) {
                _partitionFlags |= PartitionFlags.UserDrive;
            }
            if (attributes.HasFlag(FileSystemAttributes.Hidden)) {
                _partitionFlags |= PartitionFlags.HiddenDrive;
            }
        }

        public void Dispose()
        {
            SaveNextAvailableBucket();
        }

        public void Initialize(IDisk disk, ulong startSector, ulong sectorCount, string vbrImage, string reservedSectorsImage)
        {
            _disk = disk;
            _sector = startSector;
            _sectorCount = sectorCount;
            _vbrImage = vbrImage;
            _reservedSectorsImage = reservedSectorsImage;
            _reservedSectorCount = 1; // for the VBR

            // determine the size of the reserved sectors
            if (_bootable) {
                try {
                    if (!string.IsNullOrEmpty(reservedSectorsImage))
                    {
                        var fileInfo = new FileInfo(reservedSectorsImage);
                        _reservedSectorCount += (ushort)((fileInfo.Length / _disk.Geometry.BytesPerSector) + 1);
                    }
                }
                catch (Exception ex) {
                    throw new Exception($"{nameof(FileSystem)} | {nameof(Initialize)} | Failed to determine the size of the reserved sectors | {ex}");
                }
            }
        }

        private ulong BucketToSector(uint bucket)
        {
            return _sector + _reservedSectorCount + (ulong)(bucket * _bucketSize);
        }

        private ushort DetermineBucketSize(ulong driveSizeBytes)
        {
            if (driveSizeBytes <= GIGABYTE)
                return 8;
            else if (driveSizeBytes <= (64UL * GIGABYTE))
                return 16;
            else if (driveSizeBytes <= (256UL * GIGABYTE))
                return 32;
            else
                return 64;
        }
        
        private void BuildMasterRecord(uint rootBucket, uint journalBucket, uint badListBucket, 
            ulong masterRecordSector, ulong masterRecordMirrorSector)
        {
            // Build a new master-record structure
            //uint32_t Magic;
            //uint32_t Flags;
            //uint32_t Checksum;      // Checksum of the master-record
            //uint8_t PartitionName[64];

            //uint32_t FreeBucket;        // Pointer to first free index
            //uint32_t RootIndex;     // Pointer to root directory
            //uint32_t BadBucketIndex;    // Pointer to list of bad buckets
            //uint32_t JournalIndex;  // Pointer to journal file

            //uint64_t MapSector;     // Start sector of bucket-map_sector
            //uint64_t MapSize;		// Size of bucket map
            byte[] masterRecord = new byte[512];
            masterRecord[0] = 0x4D;
            masterRecord[1] = 0x46;
            masterRecord[2] = 0x53;
            masterRecord[3] = 0x31;

            // Initialize partition flags
            uint flagsAsUInt = (uint)_partitionFlags;
            masterRecord[4] = (byte)(flagsAsUInt & 0xFF);
            masterRecord[5] = (byte)((flagsAsUInt >> 8) & 0xFF);

            // Initialize partition name
            byte[] NameBytes = Encoding.UTF8.GetBytes(_partitionName);
            Array.Copy(NameBytes, 0, masterRecord, 12, NameBytes.Length);

            // Initialize free pointer
            masterRecord[76] = (Byte)(_bucketMap.NextFreeBucket & 0xFF);
            masterRecord[77] = (Byte)((_bucketMap.NextFreeBucket >> 8) & 0xFF);
            masterRecord[78] = (Byte)((_bucketMap.NextFreeBucket >> 16) & 0xFF);
            masterRecord[79] = (Byte)((_bucketMap.NextFreeBucket >> 24) & 0xFF);

            // Initialize root directory pointer
            masterRecord[80] = (Byte)(rootBucket & 0xFF);
            masterRecord[81] = (Byte)((rootBucket >> 8) & 0xFF);
            masterRecord[82] = (Byte)((rootBucket >> 16) & 0xFF);
            masterRecord[83] = (Byte)((rootBucket >> 24) & 0xFF);

            // Initialize bad bucket list pointer
            masterRecord[84] = (Byte)(badListBucket & 0xFF);
            masterRecord[85] = (Byte)((badListBucket >> 8) & 0xFF);
            masterRecord[86] = (Byte)((badListBucket >> 16) & 0xFF);
            masterRecord[87] = (Byte)((badListBucket >> 24) & 0xFF);

            // Initialize journal list pointer
            masterRecord[88] = (Byte)(journalBucket & 0xFF);
            masterRecord[89] = (Byte)((journalBucket >> 8) & 0xFF);
            masterRecord[90] = (Byte)((journalBucket >> 16) & 0xFF);
            masterRecord[91] = (Byte)((journalBucket >> 24) & 0xFF);

            // Initialize map sector pointer
            ulong offset = _bucketMap.MapStartSector - _sector;
            masterRecord[92] = (Byte)(offset & 0xFF);
            masterRecord[93] = (Byte)((offset >> 8) & 0xFF);
            masterRecord[94] = (Byte)((offset >> 16) & 0xFF);
            masterRecord[95] = (Byte)((offset >> 24) & 0xFF);
            masterRecord[96] = (Byte)((offset >> 32) & 0xFF);
            masterRecord[97] = (Byte)((offset >> 40) & 0xFF);
            masterRecord[98] = (Byte)((offset >> 48) & 0xFF);
            masterRecord[99] = (Byte)((offset >> 56) & 0xFF);

            // Initialize map size
            ulong mapSize = _bucketMap.GetSizeOfMap();
            masterRecord[100] = (Byte)(mapSize & 0xFF);
            masterRecord[101] = (Byte)((mapSize >> 8) & 0xFF);
            masterRecord[102] = (Byte)((mapSize >> 16) & 0xFF);
            masterRecord[103] = (Byte)((mapSize >> 24) & 0xFF);
            masterRecord[104] = (Byte)((mapSize >> 32) & 0xFF);
            masterRecord[105] = (Byte)((mapSize >> 40) & 0xFF);
            masterRecord[106] = (Byte)((mapSize >> 48) & 0xFF);
            masterRecord[107] = (Byte)((mapSize >> 56) & 0xFF);

            // Initialize checksum
            uint checksum = CalculateChecksum(masterRecord, 8, 4);
            masterRecord[8] = (Byte)(checksum & 0xFF);
            masterRecord[9] = (Byte)((checksum >> 8) & 0xFF);
            masterRecord[10] = (Byte)((checksum >> 16) & 0xFF);
            masterRecord[11] = (Byte)((checksum >> 24) & 0xFF);

            // Flush it to disk
            _disk.Write(masterRecord, masterRecordSector, true);
            _disk.Write(masterRecord, masterRecordMirrorSector, true);
        }

        private void BuildVBR(ulong masterBucketSector, ulong mirrorMasterBucketSector)
        {
            // Initialize the MBR
            //uint8_t JumpCode[3];
            //uint32_t Magic;
            //uint8_t Version;
            //uint8_t Flags;
            //uint8_t MediaType;
            //uint16_t SectorSize;
            //uint16_t SectorsPerTrack;
            //uint16_t HeadsPerCylinder;
            //uint64_t SectorCount;
            //uint16_t ReservedSectors;
            //uint16_t SectorsPerBucket;
            //uint64_t MasterRecordSector;
            //uint64_t MasterRecordMirror;
            byte[] bootsector = new byte[_disk.Geometry.BytesPerSector];

            // Initialize magic
            bootsector[3] = 0x4D;
            bootsector[4] = 0x46;
            bootsector[5] = 0x53;
            bootsector[6] = 0x31;

            // Initialize version
            bootsector[7] = 0x1;

            // Initialize flags
            // 0x1 - BootDrive
            // 0x2 - Encrypted
            bootsector[8] = _bootable ? (byte)0x1 : (byte)0x0;

            // Initialize disk metrics
            bootsector[9] = 0x80;
            bootsector[10] = (Byte)(_disk.Geometry.BytesPerSector & 0xFF);
            bootsector[11] = (Byte)((_disk.Geometry.BytesPerSector >> 8) & 0xFF);

            // Sectors per track
            bootsector[12] = (Byte)(_disk.Geometry.SectorsPerTrack & 0xFF);
            bootsector[13] = (Byte)((_disk.Geometry.SectorsPerTrack >> 8) & 0xFF);

            // Heads per cylinder
            bootsector[14] = (Byte)(_disk.Geometry.HeadsPerCylinder & 0xFF);
            bootsector[15] = (Byte)((_disk.Geometry.HeadsPerCylinder >> 8) & 0xFF);

            // Total sectors on partition
            bootsector[16] = (Byte)(_sectorCount & 0xFF);
            bootsector[17] = (Byte)((_sectorCount >> 8) & 0xFF);
            bootsector[18] = (Byte)((_sectorCount >> 16) & 0xFF);
            bootsector[19] = (Byte)((_sectorCount >> 24) & 0xFF);
            bootsector[20] = (Byte)((_sectorCount >> 32) & 0xFF);
            bootsector[21] = (Byte)((_sectorCount >> 40) & 0xFF);
            bootsector[22] = (Byte)((_sectorCount >> 48) & 0xFF);
            bootsector[23] = (Byte)((_sectorCount >> 56) & 0xFF);

            // Reserved sectors
            bootsector[24] = (Byte)(_reservedSectorCount & 0xFF);
            bootsector[25] = (Byte)((_reservedSectorCount >> 8) & 0xFF);

            // Size of an bucket in sectors
            bootsector[26] = (Byte)(_bucketSize & 0xFF);
            bootsector[27] = (Byte)((_bucketSize >> 8) & 0xFF);

            // Sector of master-record
            ulong offset = masterBucketSector - _sector;
            bootsector[28] = (Byte)(offset & 0xFF);
            bootsector[29] = (Byte)((offset >> 8) & 0xFF);
            bootsector[30] = (Byte)((offset >> 16) & 0xFF);
            bootsector[31] = (Byte)((offset >> 24) & 0xFF);
            bootsector[32] = (Byte)((offset >> 32) & 0xFF);
            bootsector[33] = (Byte)((offset >> 40) & 0xFF);
            bootsector[34] = (Byte)((offset >> 48) & 0xFF);
            bootsector[35] = (Byte)((offset >> 56) & 0xFF);

            // Sector of master-record mirror
            offset = masterBucketSector - _sector;
            bootsector[36] = (Byte)(offset & 0xFF);
            bootsector[37] = (Byte)((offset >> 8) & 0xFF);
            bootsector[38] = (Byte)((offset >> 16) & 0xFF);
            bootsector[39] = (Byte)((offset >> 24) & 0xFF);
            bootsector[40] = (Byte)((offset >> 32) & 0xFF);
            bootsector[41] = (Byte)((offset >> 40) & 0xFF);
            bootsector[42] = (Byte)((offset >> 48) & 0xFF);
            bootsector[43] = (Byte)((offset >> 56) & 0xFF);

            _disk.Write(bootsector, _sector, true);
        }

        public void InstallBootloaders()
        {
            // Load up boot-sector
            Utils.Logger.Instance.Info($"{nameof(FileSystem)} | {nameof(InstallBootloaders)} | Loading stage1 bootloader ({_vbrImage})");
            byte[] bootsector = File.ReadAllBytes(_vbrImage);

            // Modify boot-sector by preserving the header 44
            byte[] existingSectorContent = _disk.Read(_sector, 1);
            Buffer.BlockCopy(existingSectorContent, 3, bootsector, 3, 41);

            // Mark the partition as os-partition
            bootsector[8] = 0x1;

            // Flush the modified sector back to disk
            Utils.Logger.Instance.Info($"{nameof(FileSystem)} | {nameof(InstallBootloaders)} | Writing stage1 bootloader");
            _disk.Write(bootsector, _sector, true);

            // Write stage2 to disk
            if (!string.IsNullOrEmpty(_reservedSectorsImage))
            {
                Utils.Logger.Instance.Info($"{nameof(FileSystem)} | {nameof(InstallBootloaders)} | Loading stage2 bootloader ({_reservedSectorsImage})");
                byte[] stage2Data = File.ReadAllBytes(_reservedSectorsImage);
                byte[] sectorAlignedBuffer = new Byte[((stage2Data.Length / _disk.Geometry.BytesPerSector) + 1) * _disk.Geometry.BytesPerSector];
                stage2Data.CopyTo(sectorAlignedBuffer, 0);

                // Make sure we allocate a sector-aligned buffer
                Utils.Logger.Instance.Info($"{nameof(FileSystem)} | {nameof(InstallBootloaders)} | Writing stage2 bootloader");
                _disk.Write(sectorAlignedBuffer, _sector + 1, true);
            }
        }

        public bool Format()
        {
            if (_disk == null)
                return false;

            // Sanitize that bootloaders are present if the partition is marked bootable
            if (_bootable)
            {
                if (!File.Exists(_vbrImage))
                {
                    Utils.Logger.Instance.Error($"{nameof(FileSystem)} | {nameof(Format)} | Bootloader {_vbrImage} is missing, cannot format partition");
                    return false;
                }
            }

            ulong partitionSizeBytes = _sectorCount * _disk.Geometry.BytesPerSector;
            Utils.Logger.Instance.Info("Format - size of partition " + partitionSizeBytes.ToString() + " bytes");

            _bucketSize = DetermineBucketSize(_sectorCount * _disk.Geometry.BytesPerSector);
            uint masterBucketSectorOffset = (uint)_reservedSectorCount;

            // round the number of reserved sectors up to a equal of buckets
            _reservedSectorCount = (ushort)((((_reservedSectorCount + 1) / _bucketSize) + 1) * _bucketSize);

            Utils.Logger.Instance.Info("Format - Bucket Size: " + _bucketSize.ToString());
            Utils.Logger.Instance.Info("Format - Reserved Sectors: " + _reservedSectorCount.ToString());

            _bucketMap = new BucketMap(_disk, 
                (_sector + _reservedSectorCount), 
                (_sectorCount - _reservedSectorCount),
                _bucketSize);
            var mapCreated = _bucketMap.Create();
            if (!mapCreated)
                return false;

            ulong masterBucketSector = _sector + masterBucketSectorOffset;
            ulong mirrorMasterBucketSector = _bucketMap.MapStartSector - 1;
            Utils.Logger.Instance.Debug("Format - Creating master-records");
            Utils.Logger.Instance.Debug("Format - Original: " + masterBucketSectorOffset.ToString());
            Utils.Logger.Instance.Debug("Format - Mirror: " + mirrorMasterBucketSector.ToString());

            // Allocate for:
            // - Root directory - 8 buckets
            // - Bad-bucket list - 1 bucket
            // - Journal list - 8 buckets
            uint initialBucketSize = 0;
            uint rootIndex = _bucketMap.AllocateBuckets(8, out initialBucketSize);
            uint journalIndex = _bucketMap.AllocateBuckets(8, out initialBucketSize);
            uint badBucketIndex = _bucketMap.AllocateBuckets(1, out initialBucketSize);
            Utils.Logger.Instance.Debug("Format - Free bucket pointer after setup: " + _bucketMap.NextFreeBucket.ToString());
            Utils.Logger.Instance.Debug("Format - Wiping root data");

            // Allocate a zero array to fill the allocated sectors with
            byte[] wipeBuffer = new byte[_bucketSize * _disk.Geometry.BytesPerSector];
            _disk.Write(wipeBuffer, BucketToSector(badBucketIndex), true);

            wipeBuffer = new Byte[(_bucketSize * _disk.Geometry.BytesPerSector) * 8];
            _disk.Write(wipeBuffer, BucketToSector(rootIndex), true);
            _disk.Write(wipeBuffer, BucketToSector(journalIndex), true);

            // build master record
            Utils.Logger.Instance.Info("Format - Installing Master Records");
            BuildMasterRecord(rootIndex, journalIndex, badBucketIndex, masterBucketSector, mirrorMasterBucketSector);

            // install vbr
            Utils.Logger.Instance.Info("Format - Installing VBR");
            BuildVBR(masterBucketSector, mirrorMasterBucketSector);

            // make bootable if requested
            if (_bootable)
            {
                InstallBootloaders();
            }
            return true;
        }


        /* ListDirectory
         * List's the contents of the given path - that must be a directory path */
        public bool ListDirectory(String Path)
        {
            // Sanitize variables
            if (_disk == null)
                return false;

            // Read bootsector
            Byte[] Bootsector = _disk.Read(_sector, 1);

            // Load some data (master-record and bucket-size)
            ulong MasterRecordSector = BitConverter.ToUInt64(Bootsector, 28);

            // Read master-record
            Byte[] MasterRecord = _disk.Read(_sector + MasterRecordSector, 1);
            uint RootBucket = BitConverter.ToUInt32(MasterRecord, 80);

            // Call our recursive function to list everything
            Console.WriteLine("Files in " + Path + ":");
            ListPath(RootBucket, Path);
            Console.WriteLine("");
            return true;
        }

        /* WriteFile 
         * Creates a new file or directory with the given path, flags and data */
        public bool CreateFile(string localPath, FileFlags fileFlags, byte[] fileContents)
        {
            if (_disk == null)
                return false;

            byte[] bootsector = _disk.Read(_sector, 1);

            // Load some data (master-record and bucket-size)
            ulong MasterRecordSector = BitConverter.ToUInt64(bootsector, 28);
            ulong MasterRecordMirrorSector = BitConverter.ToUInt64(bootsector, 36);

            // Read master-record
            byte[] masterRecord = _disk.Read(_sector + MasterRecordSector, 1);
            uint rootBucket = BitConverter.ToUInt32(masterRecord, 80);
            ulong sectorsRequired = 0;
            uint bucketsRequired = 0;

            if (fileContents != null) {
                // Calculate number of sectors required
                sectorsRequired = (ulong)fileContents.LongLength / _disk.Geometry.BytesPerSector;
                if (((ulong)fileContents.LongLength % _disk.Geometry.BytesPerSector) > 0)
                    sectorsRequired++;

                // Calculate the number of buckets required
                bucketsRequired = (uint)(sectorsRequired / _bucketSize);
                if ((sectorsRequired % _bucketSize) > 0)
                    bucketsRequired++;
            }

            // Locate the record
            var record = FindPath(rootBucket, localPath);
            if (record == null)
            {
                Utils.Logger.Instance.Info("/" + localPath + " is a new " 
                    + (fileFlags.HasFlag(FileFlags.Directory) ? "directory" : "file"));
                record = CreatePath(rootBucket, localPath, fileFlags);
                if (record == null) {
                    Utils.Logger.Instance.Error("The creation info returned null, somethings wrong");
                    return false;
                }
            }

            if (fileContents != null)
            {
                EnsureBucketSpace(record, (ulong)fileContents.LongLength);
                FillBucketChain(record.Bucket, record.BucketLength, fileContents);

                // Update the record
                record.Size = (ulong)fileContents.LongLength;
                UpdateRecord(record);
            }
            return true;
        }

        public bool CreateDirectory(string localPath, FileFlags flags)
        {
            return CreateFile(localPath, flags | FileFlags.Directory, null);
        }

        public bool IsBootable()
        {
            return _bootable;
        }

        public byte GetFileSystemType()
        {
            return TYPE;
        }
        
        public Guid GetFileSystemTypeGuid()
        {
            return _partitionGuid;
        }

        public ulong GetSectorStart()
        {
            return _sector;
        }

        public ulong GetSectorCount()
        {
            return _sectorCount;
        }
        
        public string GetName()
        {
            return _partitionName;
        }

        /* File record cache structure
         * Represenst a file-entry in cached format */
        class MfsRecord {
            public string Name;
            public RecordFlags Flags;
            public ulong Size;
            public ulong AllocatedSize;
            public uint Bucket;
            public uint BucketLength;

            public uint DirectoryBucket;
            public uint DirectoryLength;
            public uint DirectoryIndex;
        }
    }
}
