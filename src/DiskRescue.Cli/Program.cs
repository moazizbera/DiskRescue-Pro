using System.Globalization;
using System.Text.Json;
using DiskRescue.Filesystems;
using DiskRescue.IO;
using DiskRescue.Partitions;

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
    {
        PrintUsage();
        return args.Length == 0 ? 2 : 0;
    }

    if (string.Equals(args[0], "scan-ntfs", StringComparison.OrdinalIgnoreCase))
        return RunNtfsScan(args);

    if (!string.Equals(args[0], "inspect", StringComparison.OrdinalIgnoreCase) || args.Length < 2)
    {
        PrintUsage();
        return 2;
    }

    var imagePath = args[1];
    var sectorSize = 512;
    for (var i = 2; i < args.Length; i++)
    {
        if (args[i] == "--sector-size" && i + 1 < args.Length &&
            int.TryParse(args[++i], out var parsedSectorSize))
        {
            sectorSize = parsedSectorSize;
        }
        else
        {
            Console.Error.WriteLine($"Unknown or invalid argument: {args[i]}");
            PrintUsage();
            return 2;
        }
    }

    if (sectorSize < 512 || sectorSize > 65536 || (sectorSize & (sectorSize - 1)) != 0)
    {
        Console.Error.WriteLine("Sector size must be a power of two between 512 and 65536 bytes.");
        return 2;
    }

    try
    {
        using var image = FileImageReader.Open(imagePath);
        var mbr = MbrParser.Parse(image, sectorSize);
        var gpt = GptParser.ParsePrimaryAndBackup(image, checked((uint)sectorSize));
        var hasGpt = gpt.Primary.HeaderValid || gpt.Backup.HeaderValid;
        var selectedGpt = gpt.Primary.HeaderValid ? gpt.Primary : gpt.Backup;

        var report = new
        {
            product = "DiskRescue Pro",
            reportVersion = 1,
            mode = "read-only-image-inspection",
            source = new
            {
                fileName = Path.GetFileName(imagePath),
                lengthBytes = image.Length,
                logicalSectorSizeBytes = sectorSize,
                totalSectors = image.Length / sectorSize
            },
            safety = new
            {
                sourceOpenedReadOnly = true,
                physicalDevicesSupported = false,
                sourceWriteOperationsAvailable = false,
                recoveryAvailable = false
            },
            mbr = new
            {
                signatureValid = mbr.HasValidSignature,
                protectiveGptEntryDetected = mbr.IsProtectiveMbr,
                partitions = mbr.Partitions,
                diagnostics = mbr.Issues
            },
            gpt = new
            {
                primaryHeaderValid = gpt.Primary.HeaderValid,
                primaryHeader = gpt.Primary.Header,
                primaryPartitions = gpt.Primary.Partitions,
                primaryDiagnostics = gpt.Primary.Issues,
                backupHeaderValid = gpt.Backup.HeaderValid,
                backupHeader = gpt.Backup.Header,
                backupPartitions = gpt.Backup.Partitions,
                backupDiagnostics = gpt.Backup.Issues,
                consistencyDiagnostics = gpt.ConsistencyIssues,
                selectedPartitionSource = gpt.Primary.HeaderValid ? "primary" : gpt.Backup.HeaderValid ? "backup" : "none",
                selectedPartitions = selectedGpt.Partitions
            }
        };

        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
    {
        Console.Error.WriteLine($"Inspection failed safely: {exception.Message}");
        return 1;
    }
}

static int RunNtfsScan(string[] args)
{
    if (args.Length < 2)
    {
        PrintUsage();
        return 2;
    }

    var imagePath = args[1];
    long? volumeOffset = null;
    long? volumeLength = null;
    var sectorSize = 512;
    var maximumRecords = 1000;

    for (var i = 2; i < args.Length; i++)
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"Missing value for {args[i]}.");
            return 2;
        }

        var option = args[i];
        var value = args[++i];
        switch (option)
        {
            case "--offset" when long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset):
                volumeOffset = offset;
                break;
            case "--length" when long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length):
                volumeLength = length;
                break;
            case "--sector-size" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSectorSize):
                sectorSize = parsedSectorSize;
                break;
            case "--max-records" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedMaximum):
                maximumRecords = parsedMaximum;
                break;
            default:
                Console.Error.WriteLine($"Unknown or invalid option: {option} {value}");
                return 2;
        }
    }

    if (volumeOffset is null || volumeLength is null || volumeOffset < 0 || volumeLength <= 0)
    {
        Console.Error.WriteLine("Provide a non-negative --offset and positive --length in bytes.");
        return 2;
    }
    if (sectorSize is not (512 or 1024 or 2048 or 4096))
    {
        Console.Error.WriteLine("NTFS sector size must be 512, 1024, 2048, or 4096 bytes.");
        return 2;
    }
    if (maximumRecords is < 1 or > 65536)
    {
        Console.Error.WriteLine("--max-records must be between 1 and 65536.");
        return 2;
    }

    try
    {
        using var image = FileImageReader.Open(imagePath);
        var bootResult = NtfsBootSectorParser.Parse(image, volumeOffset.Value, volumeLength.Value);
        if (!bootResult.IsValid || bootResult.BootSector is null)
        {
            Console.Error.WriteLine("NTFS boot-sector validation failed: " + string.Join("; ", bootResult.Issues));
            return 1;
        }

        if (bootResult.BootSector.BytesPerSector != sectorSize)
        {
            Console.Error.WriteLine($"Requested sector size {sectorSize} does not match NTFS BPB value {bootResult.BootSector.BytesPerSector}.");
            return 1;
        }

        var mftResult = NtfsMftReader.Open(image, volumeOffset.Value, volumeLength.Value, bootResult.BootSector);
        if (!mftResult.IsValid || mftResult.Reader is null)
        {
            Console.Error.WriteLine("NTFS MFT validation failed: " + string.Join("; ", mftResult.Issues));
            return 1;
        }

        var scan = mftResult.Reader.ScanRecords(maximumRecords);
        var tree = NtfsDirectoryTreeBuilder.Build(scan.Entries);
        var report =
        {
            product = "DiskRescue Pro",
            reportVersion = 1,
            mode = "read-only-ntfs-mft-preview",
            source = new { fileName = Path.GetFileName(imagePath), lengthBytes = image.Length },
            volume = new
            {
                offsetBytes = volumeOffset.Value,
                lengthBytes = volumeLength.Value,
                bootResult.BootSector.BytesPerSector,
                bootResult.BootSector.SectorsPerCluster,
                bootResult.BootSector.ClusterSizeBytes,
                bootResult.BootSector.TotalSectors,
                bootResult.BootSector.FileRecordSizeBytes,
                bootResult.BootSector.MftCluster
            },
            safety = new
            {
                sourceOpenedReadOnly = true,
                physicalDevicesSupported = false,
                sourceWriteOperationsAvailable = false,
                directoryHierarchyBuilt = false,
                fileRecoveryAvailable = false
            },
            scan = new
            {
                maximumRecords,
                scan.RecordsExamined,
                scan.RecordsSkipped,
                scan.WasCancelled,
                scan.Entries,
                directoryTree = new { tree.Roots, tree.Orphans, diagnostics = tree.Issues },
                diagnostics = scan.Issues,
                scope = "Initial validated $MFT::$DATA extent only; additional extents and directory indexes are not followed."
            }
        };

        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return scan.IsValid ? 0 : 1;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
    {
        Console.Error.WriteLine($"NTFS scan stopped safely: {exception.Message}");
        return 1;
    }
}

static void PrintUsage()
{
    Console.WriteLine("DiskRescue Pro — read-only disk-image inspector");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  diskrescue inspect <image-file> [--sector-size <bytes>]");
    Console.WriteLine("  diskrescue scan-ntfs <image-file> --offset <bytes> --length <bytes> [--sector-size <bytes>] [--max-records <count>]");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  diskrescue inspect sample.img");
    Console.WriteLine("  diskrescue inspect sample-4kn.img --sector-size 4096");
    Console.WriteLine("  diskrescue scan-ntfs sample.img --offset 1048576 --length 1073741824 --max-records 5000");
    Console.WriteLine();
    Console.WriteLine("Only regular image files are supported. Physical disks are not opened.");
    Console.WriteLine("NTFS scan returns a bounded flat MFT preview, not a directory tree or file recovery.");
}
