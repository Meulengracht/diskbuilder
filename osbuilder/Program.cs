using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace OSBuilder
{
    class Program
    {
        static ulong GetSectorCountFromMB(int bytesPerSector, ulong mb)
        {
            return (mb * 1024 * 1024) / (ulong)bytesPerSector;
        }

        static byte[] GetFileData(string path)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                Utils.Logger.Instance.Error($"Failed to read file: {path} | {ex}");
                return null;
            }
        }

        static FileInfo GetFileInfo(string path)
        {
            try
            {
                return new FileInfo(path);
            }
            catch (Exception)
            {
                Utils.Logger.Instance.Error($"Failed to stat source: {path}");
                return null;
            }
        }

        static void InstallFile(FileSystems.IFileSystem fileSystem, ProjectSource source)
        {
            Console.WriteLine($"{nameof(Program)} | {nameof(InstallDirectory)} | Installing file: {source.Path}");

            FileInfo fileInfo = GetFileInfo(source.Path);
            if (fileInfo == null)
                throw new Exception($"{nameof(Program)} | {nameof(InstallFile)} | ERROR: Source is not a valid path: {source.Path}");
            if (!fileInfo.Attributes.HasFlag(FileAttributes.Normal))
                throw new Exception($"{nameof(Program)} | {nameof(InstallFile)} | ERROR: Source is not a directory: {source.Path}");

            var data = GetFileData(source.Path);
            if (data == null)
                throw new Exception($"{nameof(Program)} | {nameof(InstallFile)} | ERROR: Failed to read source: {source.Path}");
            
            var rootPath = Path.GetDirectoryName(source.Target);
            if (!string.IsNullOrEmpty(rootPath))
            {
                if (!fileSystem.CreateDirectory(rootPath, 0))
                    throw new Exception($"{nameof(Program)} | {nameof(InstallFile)} | ERROR: Failed to create directory: {rootPath}");
            }

            if (!fileSystem.CreateFile(source.Target, 0, data))
                throw new Exception($"{nameof(Program)} | {nameof(InstallFile)} | ERROR: Failed to write file: {source.Target}");
        }

        static void InstallDirectory(FileSystems.IFileSystem fileSystem, ProjectSource source)
        {
            Console.WriteLine($"{nameof(Program)} | {nameof(InstallDirectory)} | Installing directory: {source.Path}");

            FileInfo fileInfo = GetFileInfo(source.Path);
            if (fileInfo == null)
                throw new Exception($"{nameof(Program)} | {nameof(InstallDirectory)} | ERROR: Source is not a valid path: {source.Path}");
            if (!fileInfo.Attributes.HasFlag(FileAttributes.Directory))
                throw new Exception($"{nameof(Program)} | {nameof(InstallDirectory)} | ERROR: Source is not a directory: {source.Path}");
            
            
            // create target directory
            fileSystem.CreateDirectory(source.Target, 0);

            // create directory structure first
            string[] directories = Directory.GetDirectories(source.Path, "*", SearchOption.AllDirectories);
            foreach (string dir in directories) {
                // extract relative path from root to destination
                var relativePath = dir.Substring(source.Path.Length + 1).Replace('\\', '/');
                var dirToCreate = dir.Split(Path.DirectorySeparatorChar).Last();
                var targetPath = source.Target + relativePath;

                Console.WriteLine($"{nameof(Program)} | {nameof(InstallDirectory)} | Creating: " + targetPath + "  (" + dirToCreate + ")");
                if (!fileSystem.CreateDirectory(targetPath, 0)) {
                    Console.WriteLine($"{nameof(Program)} | {nameof(InstallDirectory)} | ERROR: Failed to create directory: " + dirToCreate);
                    return;
                }
            }

            // Iterate through deployment folder and install system files
            string[] installationFiles = Directory.GetFiles(source.Path, "*", SearchOption.AllDirectories);
            foreach (string file in installationFiles) {
                var relativePath = file.Substring(source.Path.Length + 1).Replace('\\', '/');
                var targetPath = source.Target + relativePath;
                
                Console.WriteLine($"{nameof(Program)} | {nameof(InstallDirectory)} | Installing file: " + targetPath);
                if (!fileSystem.CreateFile(targetPath, 0, File.ReadAllBytes(file))) {
                    throw new Exception($"{nameof(Program)} | {nameof(InstallDirectory)} | ERROR: Failed to install file: {file}");
                }
            }
        }

        static string GetCurrentPlatform()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                return "windows";
            else if (Environment.OSVersion.Platform == PlatformID.Unix)
                return "linux";
            else
                return "osx";
        }

        static string GetCurrentArchitecture()
        {
            PortableExecutableKinds peKind;
            ImageFileMachine machine;
            typeof(object).Module.GetPEKind(out peKind, out machine);
            switch (machine)
            {
                case ImageFileMachine.I386:
                    return Environment.Is64BitOperatingSystem ? "x64" : "x86";
                case ImageFileMachine.AMD64:
                    return "x64";
                case ImageFileMachine.ARM:
                    return Environment.Is64BitOperatingSystem ? "arm64" : "arm";
                case ImageFileMachine.IA64:
                    return "x64";
                default:
                    throw new Exception($"{nameof(Program)} | {nameof(InstallChefPackage)} | ERROR: Could not determine current architecture");
            }
        }

        static string GetPackageInstallPath(ProjectSource source)
        {
            // is a full path already provided?
            if (Path.HasExtension(source.Target))
                return source.Target;
            return Path.Combine(source.Target, source.Package.Replace("/", ".") + ".pack");
        }

        static async Task InstallChefPackage(FileSystems.IFileSystem fileSystem, ProjectSource source)
        {
            if (string.IsNullOrEmpty(source.Package))
                throw new Exception($"{nameof(Program)} | {nameof(InstallChefPackage)} | ERROR: Package name is not provided");
            
            var channel = string.IsNullOrEmpty(source.Channel) ? "stable" : source.Channel;
            var platform = string.IsNullOrEmpty(source.Platform) ? GetCurrentPlatform() : source.Platform;
            var arch = string.IsNullOrEmpty(source.Arch) ? GetCurrentArchitecture() : source.Arch;
            var temporaryFilePath = Path.GetTempFileName();

            Console.Write($"{nameof(Program)} | {nameof(InstallChefPackage)} | Downloading {source.Package}... ");
            try
            {
                await Integrations.ChefClient.DownloadPack(temporaryFilePath, source.Package, platform, arch, channel);
            }
            catch (Exception)
            {
                if (source.Required)
                    throw new Exception($"{nameof(Program)} | {nameof(InstallChefPackage)} | ERROR: Failed to download {source.Package}");
                else
                {
                    Utils.Logger.Instance.Error($"FAILED, SKIPPED");
                    return;
                }
            }

            // now install the file onto the disk, set the source to the file downloaded
            // and set target to the target path
            source.Path = temporaryFilePath;
            source.Target = GetPackageInstallPath(source);

            Console.WriteLine($"{nameof(Program)} | {nameof(InstallChefPackage)} | Installing: {source.Package}");
            InstallFile(fileSystem, source);

            // cleanup
            File.Delete(temporaryFilePath);
        }

        static void InstallSource(FileSystems.IFileSystem fileSystem, ProjectSource source)
        {
            if (fileSystem == null)
                return;
            
            // strip leading path
            if (source.Target.StartsWith("/"))
                source.Target = source.Target.Substring(1);

            switch (source.Type.ToLower())
            {
                case "file":
                    InstallFile(fileSystem, source);
                    break;
                case "dir":
                    InstallDirectory(fileSystem, source);
                    break;
                case "chef":
                    InstallChefPackage(fileSystem, source).Wait();
                    break;
                default:
                    throw new Exception($"{nameof(Program)} | {nameof(InstallSource)} | ERROR: Unknown source type: {source.Type}");
            }
        }

        static IDisk DetectDiskType(string diskPath)
        {
            if (diskPath.ToLower().EndsWith("img"))
                return new ImgDisk(diskPath);
            else if (diskPath.ToLower().EndsWith("vmdk"))
                return new VmdkDisk(diskPath);
            else
                return null;
        }

        static DiskLayouts.IDiskScheme DetectDiskScheme(IDisk disk)
        {
            byte[] sector = disk.Read(1, 1);
            DiskLayouts.IDiskScheme scheme;
            
            var gptSignature = System.Text.Encoding.ASCII.GetString(sector, 0, 8);
            if (gptSignature == "EFI PART")
                scheme = new DiskLayouts.GPT();
            else
                scheme = new DiskLayouts.MBR();
            return scheme;
        }

        /* LaunchCLI
         * Launches the CLI and provides commands for manipluating disks */
        static void LaunchCLI(Hashtable drives)
        {
            Console.WriteLine("\nAvailable Commands:");
            Console.WriteLine("open <path>");
            Console.WriteLine("select <filesystem>");
            Console.WriteLine("write <source> <target>");
            Console.WriteLine("ls <path>");
            Console.WriteLine("close");
            Console.WriteLine("quit");
            Console.WriteLine("");

            IDisk currentDisk = null;
            DiskLayouts.IDiskScheme currentScheme = null;
            FileSystems.IFileSystem currentFileSystem = null;

            while (true)
            {
                Console.Write(" $ ");

                String input = Console.ReadLine();
                String[] inputTokens = input.Split(new Char[] { ' ' });

                switch (inputTokens[0].ToLower())
                {
                    case "open":
                    {
                        string path = inputTokens[1];

                        // open disk
                        currentDisk = DetectDiskType(path);
                        if (!currentDisk.Open())
                        {
                            Utils.Logger.Instance.Error("Failed to open disk: " + path);
                            currentDisk = null;
                            break;
                        }
                        
                        Console.WriteLine("Opened disk: " + path);
                        
                        // open partitioning scheme
                        currentScheme = DetectDiskScheme(currentDisk);
                        if (!currentScheme.Open(currentDisk))
                        {
                            Utils.Logger.Instance.Error("Failed to open disk layout");
                            currentScheme = null;
                            currentDisk = null;
                        }
                        
                        var index = 0;
                        foreach (var fileSystem in currentScheme.GetFileSystems())
                        {
                            Console.WriteLine($"{index++}: " + fileSystem.GetName());
                        }
                    }
                    break;
                    case "select":
                    {
                        int index = int.Parse(inputTokens[1]);
                        if (index < 0 || index >= currentScheme.GetFileSystems().Count())
                        {
                            Utils.Logger.Instance.Error("Invalid filesystem index");
                            break;
                        }
                        
                        currentFileSystem = currentScheme.GetFileSystems().ElementAt(index);
                        Console.WriteLine("Selected filesystem: " + currentFileSystem.GetName());
                    } break;
                    case "write":
                    {
                        string sourcePath = inputTokens[1];
                        string targetPath = inputTokens[2];
                        if (currentFileSystem == null) {
                            Utils.Logger.Instance.Error("No filesystem selected");
                            continue;
                        }
                        currentFileSystem.CreateFile(targetPath, FileSystems.FileFlags.None, File.ReadAllBytes(sourcePath));
                    } break;
                    case "ls":
                    {
                        string path = inputTokens[1];
                        if (currentFileSystem == null) {
                            Utils.Logger.Instance.Error("No filesystem selected");
                            continue;
                        }
                        currentFileSystem.ListDirectory(path);
                    } break;
                    case "quit":
                        return;

                    default:
                        break;
                }
                GC.Collect();
            }
        }

        static Hashtable GetDrives()
        {
            Hashtable drives = new Hashtable();
            /*
            ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
            int diskIndex = 0;
            Console.WriteLine("Available Drives:");
            foreach (ManagementObject o in searcher.Get())
            {
                // Never take main-drive into account
                if (o["DeviceID"].ToString().Contains("PHYSICALDRIVE0"))
                    continue;

                // Debug
                Console.WriteLine(diskIndex.ToString() + ". " + o["Caption"] + " (DeviceID = " + o["DeviceID"] + ")");

                var bps = o.GetPropertyValue("BytesPerSector");
                var spt = o["SectorsPerTrack"];
                var tpc = o["TracksPerCylinder"];
                var ts = o["TotalSectors"];

                // Create and store the disk
                drives.Add(diskIndex, new Disk((String)o["DeviceID"], (UInt32)o["BytesPerSector"],
                    (UInt32)o["SectorsPerTrack"], (UInt32)o["TracksPerCylinder"], (UInt64)o["TotalSectors"]));
                diskIndex++;
            }
            */
            return drives;
        }

        static ulong GetMByteCountFromString(string size)
        {
            if (size.EndsWith("MB"))
                return ulong.Parse(size.Substring(0, size.Length - 2));
            else if (size.EndsWith("GB"))
                return ulong.Parse(size.Substring(0, size.Length - 2)) * 1024;
            else if (size.EndsWith("TB"))
                return ulong.Parse(size.Substring(0, size.Length - 2)) * 1024 * 1024;
            else
                return ulong.Parse(size); // assume MB
        }

        static FileSystems.FileSystemAttributes GetFileSystemAttributes(
            System.Collections.Generic.List<string> attributes)
        {
            FileSystems.FileSystemAttributes attr = FileSystems.FileSystemAttributes.None;
            if (attributes == null)
                return attr;

            foreach (var attribute in attributes)
            {
                switch (attribute.ToLower())
                {
                    case "boot":
                        attr |= FileSystems.FileSystemAttributes.Boot;
                        break;
                    case "hidden":
                        attr |= FileSystems.FileSystemAttributes.Hidden;
                        break;
                    case "readonly":
                        attr |= FileSystems.FileSystemAttributes.ReadOnly;
                        break;
                    case "noautomount":
                        attr |= FileSystems.FileSystemAttributes.NoAutoMount;
                        break;
                    default:
                        break;
                }
            }
            return attr;
        }

        static FileSystems.IFileSystem CreateFileSystem(ProjectPartition partition)
        {
            FileSystems.IFileSystem fileSystem;
            var attributes = GetFileSystemAttributes(partition.Attributes);
            Guid guid;

            // Assert that vbr image is specified in case of boot
            if (attributes.HasFlag(FileSystems.FileSystemAttributes.Boot) && string.IsNullOrEmpty(partition.VbrImage))
                throw new Exception($"{nameof(Program)} | {nameof(CreateDiskScheme)} | {partition.Label} | ERROR: No VBR image specified for boot partition");
            
            // If no guid is provided we would like to generate one
            if (!string.IsNullOrEmpty(partition.Guid))
                guid = Guid.Parse(partition.Guid);
            else
                guid = Guid.NewGuid();

            switch (partition.Type.ToLower())
            {
                case "fat":
                    fileSystem = new FileSystems.FAT.FileSystem(partition.Label, guid, attributes);
                    break;
                case "mfs":
                    fileSystem = new FileSystems.MFS.FileSystem(partition.Label, guid, attributes);
                    break;
                default:
                    return null;
            }
            return fileSystem;
        }

        static DiskLayouts.IDiskScheme CreateDiskScheme(IDisk disk, ProjectConfiguration config)
        {
            DiskLayouts.IDiskScheme scheme;

            // Which kind of disk-scheme?
            if (config.Scheme.ToLower() == "mbr")
                scheme = new DiskLayouts.MBR();
            else if (config.Scheme.ToLower() == "gpt")
                scheme = new DiskLayouts.GPT();
            else {
                throw new Exception($"{nameof(Program)} | {nameof(CreateDiskScheme)} | ERROR: Invalid schema specified in the model");
            }
            
            if (!scheme.Create(disk))
                throw new Exception($"{nameof(Program)} | {nameof(CreateDiskScheme)} | ERROR: Failed to create disk scheme");
            return scheme;
        }

        static int SilentInstall(Hashtable drives, string installationType, ProjectConfiguration config)
        {
            IDisk disk;
            
            ulong diskSizeInMBytes = GetMByteCountFromString(config.Size);
            ulong diskSectorCount = GetSectorCountFromMB(512, diskSizeInMBytes);
            Utils.Logger.Instance.Info($"{nameof(Program)} | {nameof(SilentInstall)} | Disk will be sized at " + diskSizeInMBytes.ToString() + "mb");
            Utils.Logger.Instance.Info($"{nameof(Program)} | {nameof(SilentInstall)} | Disk sector count " + diskSectorCount.ToString());
            if (diskSizeInMBytes < 64)
                throw new Exception($"{nameof(Program)} | {nameof(SilentInstall)} | ERROR: Disk size must be at least 64mb");

            // Which kind of target?
            if (installationType.ToLower() == "live" && drives.Count > 0)
                disk = (Win32Disk)drives[0];
            else if (installationType.ToLower() == "vmdk")
                disk = new VmdkDisk(512, diskSectorCount);
            else if (installationType.ToLower() == "img")
                disk = new ImgDisk(512, diskSectorCount);
            else
                throw new Exception($"{nameof(Program)} | {nameof(SilentInstall)} | ERROR: Invalid option for -target");


            // Create the disk
            if (!disk.Create())
                throw new Exception($"{nameof(Program)} | {nameof(SilentInstall)} | ERROR: Failed to create disk");

            using (var diskScheme = CreateDiskScheme(disk, config))
            {
                int i = 0;
                foreach (var partition in config.Partitions)
                {
                    var fileSystem = CreateFileSystem(partition);
                    if (diskScheme.GetFreeSectorCount() == 0)
                        throw new Exception($"{nameof(Program)} | {nameof(SilentInstall)} | ERROR: No free sectors left for partition {partition.Label}");

                    if (!string.IsNullOrEmpty(partition.Size))
                    {
                        var partitionSizeMb = GetMByteCountFromString(partition.Size);
                        var partitionSizeSectors = GetSectorCountFromMB((int)disk.Geometry.BytesPerSector, partitionSizeMb);

                        // Unless we are on the last partition, we need to make sure there is room for the partition
                        if (diskScheme.GetFreeSectorCount() < partitionSizeSectors && i != config.Partitions.Count - 1)
                            throw new Exception($"{nameof(Program)} | {nameof(SilentInstall)} | ERROR: Not enough free space for partition {partition.Label}");

                        diskScheme.AddPartition(fileSystem, partitionSizeSectors, partition.VbrImage, partition.ReservedSectorsImage);
                    }
                    else
                        diskScheme.AddPartition(fileSystem, diskScheme.GetFreeSectorCount(), partition.VbrImage, partition.ReservedSectorsImage);
                    
                    if (partition.Sources != null)
                    {
                        foreach (var source in partition.Sources)
                        {
                            InstallSource(fileSystem, source);
                        }
                    }
                    i++;
                }
            }
            disk.Close();
            return 0;
        }

        static void Main(string[] args)
        {
            var installationType = "live";
            var projectFile = "";

            // Initialize DiscUtils
            DiscUtils.Complete.SetupHelper.SetupComplete();

            // Debug print header
            Console.WriteLine("Disk Utility Software");
            Console.WriteLine(" - Tool for creating and reading disk images.");
            Console.WriteLine("Usage: osbuilder <projectfile> [options]");

            // Parse arguments
            if (args != null && args.Length > 0) {
                for (int i = 0; i < args.Length; i++) {
                    if (args[i].ToLower() == "--target")
                    {
                        if (i + 1 < args.Length)
                            installationType = args[++i];
                        else
                            throw new ArgumentException($"{nameof(Program)} | {nameof(Main)} | ERROR: Missing argument for --target");
                    }
                    else if (args[i].ToLower() == "--v")
                    {
                        Utils.Logger.Instance.SetLevel(Utils.LogLevel.DEBUG);
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(projectFile))
                            projectFile = args[i];
                        else
                            throw new ArgumentException($"{nameof(Program)} | {nameof(Main)} | ERROR: Only one project file can be specified");
                    }
                }
            }
            
            // get a list of available drives for installation
            var drives = GetDrives();
            var configuration = ProjectConfiguration.Parse(projectFile);
            if (configuration != null)
                SilentInstall(drives, installationType, configuration);
            else
                LaunchCLI(drives);
        }
    }
}
