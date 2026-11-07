using System.Globalization;
using System.Security.Cryptography;
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

    if (string.Equals(args[0], "discover-ntfs", StringComparison.OrdinalIgnoreCase))
        return RunNtfsDiscovery(args);

    if (string.Equals(args[0], "scan-ntfs", StringComparison.OrdinalIgnoreCase))
        return RunNtfsScan(args);

    if (string.Equals(args[0], "extract-ntfs-file", StringComparison.OrdinalIgnoreCase))
        return RunNtfsExtract(args);

    if (string.Equals(args[0], "image-copy", StringComparison.OrdinalIgnoreCase))
        return RunImageCopy(args);

    if (string.Equals(args[0], "list-physical-disks", StringComparison.OrdinalIgnoreCase))
        return RunPhysicalDiskInventory(args);

    if (string.Equals(args[0], "acquire-physical", StringComparison.OrdinalIgnoreCase))
        return RunPhysicalDiskAcquisition(args);

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


static int RunImageCopy(string[] args)
{
    if (args.Length != 3)
    {
        Console.Error.WriteLine("Usage: diskrescue image-copy <source-image> <new-destination-image>");
        return 2;
    }

    try
    {
        using var source = FileImageReader.Open(args[1]);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            var progress = new Progress<DiskImageProgress>(value =>
            {
                var percent = value.Percent.ToString("F1", CultureInfo.InvariantCulture);
                Console.Write($"\\rImaging: {percent}% · {value.BytesCopied:N0}/{value.TotalBytes:N0} bytes");
            });
            var result = DiskImager.CreateImageAsync(
                source, args[2], progress, cancellation.Token).GetAwaiter().GetResult();
            Console.WriteLine();
            Console.WriteLine($"Image created: {result.ImagePath}");
            Console.WriteLine($"Bytes copied: {result.BytesCopied:N0}");
            Console.WriteLine($"SHA-256: {result.Sha256}");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("\\nImaging cancelled. The incomplete temporary image was removed when possible.");
        return 130;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
    {
        Console.Error.WriteLine($"Imaging stopped safely: {exception.Message}");
        return 1;
    }
}

static int RunNtfsDiscovery(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: diskrescue discover-ntfs <image-file> [--sector-size <bytes>] [--max-scan-bytes <bytes>]");
        return 2;
    }

    var sectorSize = 512;
    long? maximumScanBytes = null;
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
            case "--sector-size" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSectorSize):
                sectorSize = parsedSectorSize;
                break;
            case "--max-scan-bytes" when long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedMaximum):
                maximumScanBytes = parsedMaximum;
                break;
            default:
                Console.Error.WriteLine($"Unknown or invalid option: {option} {value}");
                return 2;
        }
    }

    if (sectorSize is not (512 or 1024 or 2048 or 4096) ||
        (maximumScanBytes.HasValue && maximumScanBytes.Value <= 0))
    {
        Console.Error.WriteLine("Sector size must be 512, 1024, 2048, or 4096; scan limit must be positive.");
        return 2;
    }

    try
    {
        using var image = FileImageReader.Open(args[1]);
        var mbr = MbrParser.Parse(image, sectorSize);
        var gpt = GptParser.ParsePrimaryAndBackup(image, checked((uint)sectorSize));
        var known = new List<KnownPartitionRange>();
        for (var i = 0; i < mbr.Partitions.Count; i++)
            known.Add(new("MBR", i + 1, mbr.Partitions[i].ByteOffset, mbr.Partitions[i].ByteLength));

        var selectedGpt = gpt.Primary.HeaderValid ? gpt.Primary : gpt.Backup;
        for (var i = 0; i < selectedGpt.Partitions.Count; i++)
            known.Add(new("GPT", i + 1, selectedGpt.Partitions[i].ByteOffset, selectedGpt.Partitions[i].ByteLength));

        var result = NtfsPartitionScanner.Scan(image, sectorSize, known,
            maximumScanBytes ?? image.Length);
        var report = new
        {
            product = "DiskRescue Pro",
            reportVersion = 1,
            mode = "read-only-ntfs-partition-discovery",
            source = new { fileName = Path.GetFileName(args[1]), lengthBytes = image.Length, logicalSectorSizeBytes = sectorSize },
            safety = new
            {
                sourceOpenedReadOnly = true,
                physicalDevicesSupported = false,
                sourceWriteOperationsAvailable = false,
                partitionTableChangesAvailable = false,
                fileRecoveryAvailable = false
            },
            partitionDiscovery = result
        };
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return result.WasCancelled ? 130 : 0;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
    {
        Console.Error.WriteLine($"NTFS partition discovery stopped safely: {exception.Message}");
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

        var continuation = mftResult.Reader.ExtendUsingResidentAttributeList();
        var scanReader = continuation.IsValid && continuation.Reader is not null
            ? continuation.Reader
            : mftResult.Reader;
        var scan = scanReader.ScanRecords(maximumRecords);
        var tree = NtfsDirectoryTreeBuilder.Build(scan.Entries);
        var report = new
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
                directoryTreePreviewGenerated = true,
                completeDirectoryTreeAvailable = false,
                fileRecoveryAvailable = false,
                mftContinuationDiscovery = new
                {
                    continuation.IsValid,
                    continuation.Extended,
                    usedExtendedReader = !ReferenceEquals(scanReader, mftResult.Reader),
                    issues = continuation.Issues
                }
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
                scope = continuation.IsValid && continuation.Extended
                    ? "Validated resident $ATTRIBUTE_LIST continuation extents are included when referenced FILE records are reachable through the initial MFT map; non-resident attribute lists, recursive bootstrap discovery, and directory indexes are not followed."
                    : "Initial validated $MFT::$DATA extent only; continuation discovery was unavailable or unnecessary, and directory indexes are not followed."
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

static int RunNtfsExtract(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: diskrescue extract-ntfs-file <image> --offset <bytes> --length <bytes> --record <number> --output <new-file> --max-bytes <bytes>");
        return 2;
    }

    var imagePath = args[1];
    long? volumeOffset = null;
    long? volumeLength = null;
    ulong? recordNumber = null;
    ulong? maximumBytes = null;
    string? outputPath = null;
    var streamName = string.Empty;
    var sectorSize = 512;

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
            case "--record" when ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedRecord):
                recordNumber = parsedRecord;
                break;
            case "--max-bytes" when ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedMaximum):
                maximumBytes = parsedMaximum;
                break;
            case "--output" when !string.IsNullOrWhiteSpace(value):
                outputPath = value;
                break;
            case "--stream-name":
                streamName = value;
                break;
            case "--sector-size" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSectorSize):
                sectorSize = parsedSectorSize;
                break;
            default:
                Console.Error.WriteLine($"Unknown or invalid option: {option} {value}");
                return 2;
        }
    }

    if (volumeOffset is null || volumeLength is null || volumeOffset < 0 || volumeLength <= 0 ||
        recordNumber is null || maximumBytes is null || outputPath is null)
    {
        Console.Error.WriteLine("Provide --offset, --length, --record, --output, and an explicit --max-bytes limit.");
        return 2;
    }
    if (sectorSize is not (512 or 1024 or 2048 or 4096))
    {
        Console.Error.WriteLine("NTFS sector size must be 512, 1024, 2048, or 4096 bytes.");
        return 2;
    }

    string fullImagePath;
    string fullOutputPath;
    string? temporaryPath = null;
    try
    {
        fullImagePath = Path.GetFullPath(imagePath);
        fullOutputPath = Path.GetFullPath(outputPath);
        if (string.Equals(fullImagePath, fullOutputPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Recovery output must not overwrite the source image.");
        var destinationDirectory = Path.GetDirectoryName(fullOutputPath);
        if (string.IsNullOrEmpty(destinationDirectory) || !Directory.Exists(destinationDirectory))
            throw new DirectoryNotFoundException("Recovery output directory must already exist.");
        if (File.Exists(fullOutputPath) || Directory.Exists(fullOutputPath))
            throw new IOException("Recovery output already exists; existing files are never overwritten.");

        using var image = FileImageReader.Open(fullImagePath);
        var boot = NtfsBootSectorParser.Parse(image, volumeOffset.Value, volumeLength.Value);
        if (!boot.IsValid || boot.BootSector is null)
            throw new IOException("NTFS boot-sector validation failed: " + string.Join("; ", boot.Issues));
        if (boot.BootSector.BytesPerSector != sectorSize)
            throw new IOException($"Requested sector size {sectorSize} does not match NTFS BPB value {boot.BootSector.BytesPerSector}.");

        var opened = NtfsMftReader.Open(image, volumeOffset.Value, volumeLength.Value, boot.BootSector);
        if (!opened.IsValid || opened.Reader is null)
            throw new IOException("NTFS MFT validation failed: " + string.Join("; ", opened.Issues));
        var continuation = opened.Reader.ExtendUsingResidentAttributeList();
        if (!continuation.IsValid || continuation.Reader is null)
            throw new IOException("MFT continuation validation failed: " + string.Join("; ", continuation.Issues));

        var stream = continuation.Reader.OpenDataStream(recordNumber.Value, streamName);
        if (!stream.IsValid || stream.Reader is null)
            throw new IOException("NTFS file-data stream validation failed: " + string.Join("; ", stream.Issues));
        if (stream.Length > maximumBytes.Value)
            throw new IOException($"File stream length {stream.Length} exceeds --max-bytes {maximumBytes.Value}.");

        var fileName = Path.GetFileName(fullOutputPath);
        temporaryPath = Path.Combine(destinationDirectory, $".{fileName}.{Guid.NewGuid():N}.partial");
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            {
                var progress = new Progress<ulong>(bytes => Console.Write($"\rRecovered: {bytes:N0}/{stream.Length:N0} bytes"));
                var copied = stream.Reader.CopyTo(destination, maximumBytes.Value, cancellation.Token, progress);
                if (!copied.IsValid || copied.BytesWritten != stream.Length)
                    throw new IOException("File-data export failed: " + string.Join("; ", copied.Issues));
                destination.Flush(true);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            string sha256;
            using (var verification = File.OpenRead(temporaryPath))
                sha256 = Convert.ToHexString(SHA256.HashData(verification));
            cancellation.Token.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullOutputPath, overwrite: false);
            temporaryPath = null;
            Console.WriteLine();
            Console.WriteLine($"Recovered file: {fullOutputPath}");
            Console.WriteLine($"Bytes written: {stream.Length:N0}");
            Console.WriteLine($"SHA-256: {sha256}");
            Console.WriteLine($"MFT record: {recordNumber.Value}");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("\nFile recovery cancelled; incomplete output was discarded when possible.");
        return 130;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
    {
        Console.Error.WriteLine($"File recovery stopped safely: {exception.Message}");
        return 1;
    }
    finally
    {
        if (temporaryPath is not null)
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}


static int RunPhysicalDiskInventory(string[] args)
{
    if (args.Length != 1)
    {
        Console.Error.WriteLine("Usage: diskrescue list-physical-disks");
        return 2;
    }

    try
    {
        var inventory = WindowsPhysicalDiskDiscovery.Enumerate();
        var volumeMap = WindowsPhysicalDiskVolumeMap.Enumerate();
        var bootMap = WindowsBootDeviceMap.Enumerate(volumeMap);
        Console.WriteLine("Physical disks · metadata only · no sector reads");
        foreach (var disk in inventory.Disks)
        {
            var assessment = PhysicalDiskImageAcquisition.Assess(disk, inventory, volumeMap, bootMap);
            Console.WriteLine();
            Console.WriteLine($"PhysicalDrive{disk.DiskNumber} · {disk.DisplayIdentity}");
            Console.WriteLine($"  Capacity: {disk.LengthBytes:N0} bytes · Sector: {disk.BytesPerSector:N0} bytes");
            Console.WriteLine($"  Serial: {disk.SerialNumber ?? "not reported"} · Bus: {disk.BusType?.ToString() ?? "unknown"} · Removable: {disk.RemovableMedia?.ToString() ?? "unknown"}");
            Console.WriteLine($"  Acquisition preflight: {(assessment.CanAcquire ? "SOURCE CHECKS PASSED" : "BLOCKED")}");
            foreach (var blocker in assessment.Blockers) Console.WriteLine("  BLOCKER: " + blocker);
            foreach (var warning in assessment.Warnings) Console.WriteLine("  WARNING: " + warning);
        }
        foreach (var issue in inventory.Issues) Console.WriteLine("DISK ENUMERATION: " + issue);
        foreach (var issue in volumeMap.Issues) Console.WriteLine("VOLUME MAP: " + issue);
        foreach (var issue in bootMap.Issues) Console.WriteLine("BOOT MAP: " + issue);
        Console.WriteLine();
        Console.WriteLine("Inventory is not an acquisition. A read-only handle cannot prevent Windows or other processes writing through mounted volumes.");
        return inventory.Issues.Count == 0 && volumeMap.IsComplete && bootMap.IsComplete ? 0 : 1;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine($"Physical-disk inventory failed safely: {exception.Message}");
        return 1;
    }
}

static int RunPhysicalDiskAcquisition(string[] args)
{
    if (args.Length != 4 ||
        !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var diskNumber) ||
        diskNumber < 0 ||
        string.IsNullOrWhiteSpace(args[2]) ||
        string.IsNullOrWhiteSpace(args[3]))
    {
        Console.Error.WriteLine("Usage: diskrescue acquire-physical <disk-number> <exact-serial> <new-destination-image>");
        return 2;
    }

    try
    {
        var inventory = WindowsPhysicalDiskDiscovery.Enumerate();
        var selected = inventory.Disks.SingleOrDefault(d => d.DiskNumber == diskNumber);
        if (selected is null)
            throw new IOException($"PhysicalDrive{diskNumber} is not present in the current inventory.");
        if (!string.Equals(selected.SerialNumber?.Trim(), args[2].Trim(), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The supplied serial does not exactly match the currently reported source serial. Re-run list-physical-disks.");

        var volumeMap = WindowsPhysicalDiskVolumeMap.Enumerate();
        var bootMap = WindowsBootDeviceMap.Enumerate(volumeMap);
        var assessment = PhysicalDiskImageAcquisition.Assess(selected, inventory, volumeMap, bootMap);
        if (!assessment.CanAcquire)
            throw new IOException("Source preflight blocked acquisition: " + string.Join(" ", assessment.Blockers));

        var destination = PhysicalImageDestinationSafety.Assess(diskNumber, args[3], volumeMap);
        if (!destination.CanUseAsSeparateDestination)
            throw new IOException("Destination preflight blocked acquisition: " + string.Join(" ", destination.Blockers));

        Console.WriteLine("SOURCE DEVICE");
        Console.WriteLine($"  PhysicalDrive{selected.DiskNumber} · {selected.DisplayIdentity}");
        Console.WriteLine($"  Capacity: {selected.LengthBytes:N0} bytes");
        Console.WriteLine($"  Serial: {selected.SerialNumber}");
        Console.WriteLine($"  Destination: {Path.GetFullPath(args[3])}");
        Console.WriteLine($"  Destination volume: {destination.VolumeGuidPath}");
        Console.WriteLine($"  Destination physical disk(s): {string.Join(", ", destination.DestinationDiskNumbers)}");
        foreach (var warning in assessment.Warnings) Console.WriteLine("WARNING: " + warning);
        Console.WriteLine();
        Console.WriteLine("This copies every readable sector to a new image. It does not repair the source.");
        Console.WriteLine("A read-only handle does not prevent writes through mounted volumes. A hardware write blocker is recommended.");
        Console.WriteLine("Read errors abort the copy; no unreadable sectors are silently replaced. Resume is not supported.");
        Console.WriteLine();
        Console.Write($"Type ACQUIRE PHYSICALDRIVE{diskNumber} to continue: ");
        var confirmation = Console.ReadLine();
        if (!string.Equals(confirmation, $"ACQUIRE PHYSICALDRIVE{diskNumber}", StringComparison.Ordinal))
        {
            Console.WriteLine("Confirmation did not match. No source handle was opened and no image was created.");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            var progress = new Progress<DiskImageProgress>(value =>
            {
                Console.Write($"\rAcquiring: {value.Percent:F1}% · {value.BytesCopied:N0}/{value.TotalBytes:N0} bytes");
            });
            var result = PhysicalDiskImageAcquisition.AcquireAsync(
                selected, args[3], progress, cancellation.Token).GetAwaiter().GetResult();
            Console.WriteLine();
            Console.WriteLine($"Image: {result.Image.ImagePath}");
            Console.WriteLine($"Bytes copied: {result.Image.BytesCopied:N0}");
            Console.WriteLine($"SHA-256: {result.Image.Sha256}");
            Console.WriteLine($"Manifest: {result.ManifestPath}");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("\nPhysical acquisition cancelled. The incomplete temporary image was removed when possible.");
        return 130;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine($"Physical acquisition stopped safely: {exception.Message}");
        return 1;
    }
}

static void PrintUsage()
{
    Console.WriteLine("DiskRescue Pro — read-only disk-image inspector");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  diskrescue inspect <image-file> [--sector-size <bytes>]");
    Console.WriteLine("  diskrescue image-copy <source-image> <new-destination-image>");
    Console.WriteLine("  diskrescue list-physical-disks");
    Console.WriteLine("  diskrescue acquire-physical <disk-number> <exact-serial> <new-destination-image>");
    Console.WriteLine("  diskrescue discover-ntfs <image-file> [--sector-size <bytes>] [--max-scan-bytes <bytes>]");
    Console.WriteLine("  diskrescue scan-ntfs <image-file> --offset <bytes> --length <bytes> [--sector-size <bytes>] [--max-records <count>]");
    Console.WriteLine("  diskrescue extract-ntfs-file <image-file> --offset <bytes> --length <bytes> --record <number> --output <new-file> --max-bytes <bytes> [--stream-name <name>]");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  diskrescue inspect sample.img");
    Console.WriteLine("  diskrescue discover-ntfs lost-partition.img");
    Console.WriteLine("  diskrescue inspect sample-4kn.img --sector-size 4096");
    Console.WriteLine("  diskrescue scan-ntfs sample.img --offset 1048576 --length 1073741824 --max-records 5000");
    Console.WriteLine();
    Console.WriteLine("image-copy copies a regular image file to a new destination and prints SHA-256.");
    Console.WriteLine("inspect, image-copy, discover-ntfs, scan-ntfs, and extract-ntfs-file operate on regular image files.");
    Console.WriteLine("list-physical-disks performs metadata-only Windows discovery. acquire-physical is experimental, guarded, and Windows-only.");
    Console.WriteLine("NTFS scan returns a bounded partial MFT and directory-tree preview, not file recovery.");
    Console.WriteLine("extract-ntfs-file exports one validated resident or single-extent data stream to a new file; multi-extent, compressed, and encrypted streams are unsupported.");
}
