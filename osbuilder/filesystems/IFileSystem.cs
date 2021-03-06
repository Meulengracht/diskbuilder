using System;

namespace OSBuilder.FileSystems
{
    public interface IFileSystem : IDisposable
    {
        void Initialize(IDisk disk, ulong startSector, ulong sectorCount, string vbrImage, string reservedSectorsImage);

        /**
         * Formats the partition with the filesystem - wipes all data from the partition
         */
        bool Format();

        /**
         * List's the contents of the given path - that must be a directory path
         */
        bool ListDirectory(string path);

        /** 
         * Creates a new file with the given path, flags and data
         */
        bool CreateFile(string localPath, FileFlags flags, byte[] buffer);
        bool CreateDirectory(string localPath, FileFlags flags);

        bool IsBootable();
        byte GetFileSystemType();
        Guid GetFileSystemTypeGuid();
        ulong GetSectorStart();
        ulong GetSectorCount();
        string GetName();
    }
}
