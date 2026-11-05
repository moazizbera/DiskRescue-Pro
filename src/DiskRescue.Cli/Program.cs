using System.Text.Json;
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
                consistencyDiagnostics = gpt.ConsistencyIssues
            }
        };

        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
    {
        Console.Error.WriteLine($"Inspection failed safely: {exception.Message}");
        return 1;
    }
}

static void PrintUsage()
{
    Console.WriteLine("DiskRescue Pro — read-only disk-image inspector");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  diskrescue inspect <image-file> [--sector-size <bytes>]");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  diskrescue inspect sample.img");
    Console.WriteLine("  diskrescue inspect sample-4kn.img --sector-size 4096");
    Console.WriteLine();
    Console.WriteLine("Only regular image files are supported. Physical disks are not opened.");
    Console.WriteLine("This preview does not repair partitions or recover files.");
}
