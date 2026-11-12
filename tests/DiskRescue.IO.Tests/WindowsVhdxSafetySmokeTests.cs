using System.Security.Cryptography;
using System.Text.Json;
using DiskRescue.IO;
using Xunit;
using Xunit.Abstractions;

namespace DiskRescue.IO.Tests;

/// <summary>
/// Opt-in host integration tests. Run only from Test-WindowsVhdxSafety.ps1,
/// which creates and attaches a disposable RAW VHDX in read-only mode.
/// </summary>
public sealed class WindowsVhdxSafetySmokeTests(ITestOutputHelper output)
{
    [WindowsVhdxFact]
    [Trait("Category", "WindowsVhdxIntegration")]
    public async Task AttachedReadOnlyVhdxIsDiscoveredAndPhysicalAcquisitionIsBlocked()
    {
        var diskNumberText = Environment.GetEnvironmentVariable("DISKRESCUE_TEST_VHDX_DISK_NUMBER");

        Assert.True(OperatingSystem.IsWindows(), "The VHDX integration test requires Windows.");
        Assert.True(int.TryParse(diskNumberText, out var diskNumber), "The VHDX disk number environment variable is invalid.");

        var inventory = WindowsPhysicalDiskDiscovery.Enumerate();
        var selected = Assert.Single(inventory.Disks, disk => disk.DiskNumber == diskNumber);

        Assert.True(selected.BusType is 14 or 15,
            $"Expected a virtual or file-backed virtual storage bus (14 or 15), but observed {selected.BusType?.ToString() ?? "unknown"}.");
        Assert.True(selected.LengthBytes > 0);
        Assert.InRange(selected.BytesPerSector, 512u, 65536u);

        var volumes = WindowsPhysicalDiskVolumeMap.Enumerate();
        var boot = WindowsBootDeviceMap.Enumerate(volumes);
        var assessment = PhysicalDiskImageAcquisition.Assess(selected, inventory, volumes, boot);

        Assert.False(assessment.CanAcquire);
        Assert.Contains(assessment.Blockers, blocker =>
            blocker.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
            blocker.Contains("layered", StringComparison.OrdinalIgnoreCase));

        // Exercise only the public hard-disabled boundary. AcquireAsync must reject
        // before opening a source handle, reading sectors, or creating either output.
        var blockedImagePath = Path.Combine(
            Path.GetTempPath(), $"diskrescue-vhdx-blocked-{Guid.NewGuid():N}.img");
        var disabledError = await Assert.ThrowsAsync<NotSupportedException>(() =>
            PhysicalDiskImageAcquisition.AcquireAsync(selected, blockedImagePath));
        Assert.Contains("remains disabled", disabledError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(blockedImagePath));
        Assert.False(File.Exists(blockedImagePath + ".diskrescue.json"));

        output.WriteLine($"Detected disposable PhysicalDrive{diskNumber} as bus type {selected.BusType}.");
        output.WriteLine("Physical acquisition was correctly blocked by the production safety policy.");
        output.WriteLine("The public acquisition API rejected the virtual source at its hard-disabled boundary.");
        output.WriteLine("No source sectors were read by this policy test and no image or manifest was created.");
    }

    [WindowsVhdxFact]
    [Trait("Category", "WindowsVhdxIntegration")]
    public async Task ReadOnlyVhdxSourceCompletesCoordinatorAcquisitionWithNativeDestinationSession()
    {
        Assert.True(OperatingSystem.IsWindows(), "The VHDX integration test requires Windows.");
        var diskNumberText = Environment.GetEnvironmentVariable("DISKRESCUE_TEST_VHDX_DISK_NUMBER");
        Assert.True(int.TryParse(diskNumberText, out var diskNumber), "The VHDX disk number environment variable is invalid.");

        var hostInventory = WindowsPhysicalDiskDiscovery.Enumerate();
        var virtualDescriptor = Assert.Single(hostInventory.Disks, disk => disk.DiskNumber == diskNumber);
        Assert.True(virtualDescriptor.LengthBytes > 0);
        Assert.True(virtualDescriptor.BusType is 14 or 15, "This test must operate only on the disposable virtual source.");

        var outputDirectory = Path.Combine(
            Path.GetTempPath(), $"diskrescue-vhdx-coordinator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var imagePath = Path.Combine(outputDirectory, "vhdx-coordinator-copy.img");
        var manifestPath = imagePath + ".diskrescue.json";

        try
        {
            string sourceHashBefore;
            using (var before = new ReadOnlyPhysicalDriveSource(diskNumber, virtualDescriptor.LengthBytes))
                sourceHashBefore = HashSource(before);

            // Synthetic inventory metadata lets the internal coordinator traverse
            // preflight gates; reads still come from the attached read-only VHDX.
            // The real Windows pinned destination adapter handles all output I/O.
            var syntheticSource = new PhysicalDiskDescriptor(
                2, @"\\.\PhysicalDrive2", virtualDescriptor.LengthBytes,
                virtualDescriptor.BytesPerSector, "DiskRescue Test", "Disposable VHDX Source",
                "DISKRESCUE-TEST-SOURCE-0002", 3, false);
            var environment = new VhdxCoordinatorTestEnvironment(
                syntheticSource, diskNumber, virtualDescriptor.LengthBytes);

            var result = await PhysicalDiskAcquisitionCoordinator.AcquireAsync(
                syntheticSource, imagePath, environment);

            string sourceHashAfter;
            using (var after = new ReadOnlyPhysicalDriveSource(diskNumber, virtualDescriptor.LengthBytes))
                sourceHashAfter = HashSource(after);

            Assert.Equal(sourceHashBefore, sourceHashAfter);
            Assert.Equal(virtualDescriptor.LengthBytes, result.Image.BytesCopied);
            Assert.Equal(imagePath, result.Image.ImagePath, ignoreCase: true);
            Assert.Equal(manifestPath, result.ManifestPath, ignoreCase: true);
            Assert.True(File.Exists(imagePath));
            Assert.True(File.Exists(manifestPath));
            Assert.Equal(virtualDescriptor.LengthBytes, new FileInfo(imagePath).Length);

            string destinationHash;
            await using (var imageStream = File.OpenRead(imagePath))
                destinationHash = Convert.ToHexString(await SHA256.HashDataAsync(imageStream));
            Assert.Equal(result.Image.Sha256, destinationHash);

            using var manifestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var root = manifestDocument.RootElement;
            Assert.Equal("DiskRescue.PhysicalAcquisitionManifest.v1", root.GetProperty("format").GetString());
            Assert.Equal(virtualDescriptor.LengthBytes, root.GetProperty("image").GetProperty("BytesCopied").GetInt64());
            Assert.Equal(result.Image.Sha256, root.GetProperty("image").GetProperty("sha256").GetString());
            Assert.True(root.GetProperty("image").GetProperty("verifiedBeforePublish").GetBoolean());

            output.WriteLine($"Internal coordinator copied {result.Image.BytesCopied:N0} bytes from read-only disposable PhysicalDrive{diskNumber}.");
            output.WriteLine($"Published image SHA-256: {result.Image.Sha256}.");
            output.WriteLine($"Source SHA-256 before and after acquisition matched: {sourceHashAfter}.");
            output.WriteLine("The real Windows pinned destination session published image and manifest.");
            output.WriteLine("Scope limitation: synthetic inventory metadata is injected; physical source identity and separate physical-disk mapping are not validated.");
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }


    [WindowsVhdxEnospcFact]
    [Trait("Category", "WindowsVhdxIntegration")]
    public async Task PinnedVolumeHandleFlushesPublishedEntryOnDisposableNtfsVhdx()
    {
        Assert.True(OperatingSystem.IsWindows(), "The volume-flush integration test requires Windows.");
        var destinationRoot = Environment.GetEnvironmentVariable("DISKRESCUE_TEST_DESTINATION_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(destinationRoot), "The disposable NTFS destination root was not supplied.");
        var fullDestinationRoot = Path.GetFullPath(destinationRoot!);
        var root = Path.GetPathRoot(fullDestinationRoot);
        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.Equal(root, fullDestinationRoot, StringComparer.OrdinalIgnoreCase);

        var destinationDrive = new DriveInfo(root!);
        Assert.True(destinationDrive.IsReady);
        Assert.Equal("NTFS", destinationDrive.DriveFormat, ignoreCase: true);
        Assert.Equal(DriveType.Fixed, destinationDrive.DriveType);

        var directory = Path.Combine(fullDestinationRoot, "DiskRescue-volume-flush-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var payload = Enumerable.Range(0, 64 * 1024).Select(index => (byte)((index * 17 + 3) % 251)).ToArray();
        try
        {
            await using (var session = WindowsPhysicalAcquisitionDestinationSession.Open(directory))
            {
                var stream = await session.CreateNewFileAsync("flush.partial");
                await stream.WriteAsync(payload);
                await session.FlushToDiskAsync(stream);
                await session.PublishNoReplaceAsync(stream, "published.bin");

                // This opens only the volume GUID associated with the pinned directory
                // on the disposable NTFS VHDX; it never opens a physical-disk handle.
                await session.FlushPublishedNamespaceAsync();
            }

            // Close the session first: its intentionally restrictive sharing mode
            // blocks independent read opens while the owned file handle remains live.
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(directory, "published.bin")));
            output.WriteLine("FlushFileBuffers succeeded on the verified disposable NTFS destination VHDX volume handle.");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsVhdxEnospcFact]
    [Trait("Category", "WindowsVhdxIntegration")]
    public async Task UndersizedNtfsDestinationVhdxProducesRealDiskFullAndCleansPartialImage()
    {
        Assert.True(OperatingSystem.IsWindows(), "The VHDX integration test requires Windows.");
        var diskNumberText = Environment.GetEnvironmentVariable("DISKRESCUE_TEST_VHDX_DISK_NUMBER");
        Assert.True(int.TryParse(diskNumberText, out var diskNumber), "The VHDX source disk number is invalid.");

        var destinationRoot = Environment.GetEnvironmentVariable("DISKRESCUE_TEST_DESTINATION_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(destinationRoot), "The disposable NTFS destination root was not supplied.");
        var fullDestinationRoot = Path.GetFullPath(destinationRoot!);
        var root = Path.GetPathRoot(fullDestinationRoot);
        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.Equal(root, fullDestinationRoot, StringComparer.OrdinalIgnoreCase);

        var destinationDrive = new DriveInfo(root!);
        Assert.True(destinationDrive.IsReady, "The disposable destination volume is not ready.");
        Assert.Equal("NTFS", destinationDrive.DriveFormat, ignoreCase: true);
        Assert.Equal(DriveType.Fixed, destinationDrive.DriveType);

        var inventory = WindowsPhysicalDiskDiscovery.Enumerate();
        var virtualDescriptor = Assert.Single(inventory.Disks, disk => disk.DiskNumber == diskNumber);
        Assert.True(virtualDescriptor.BusType is 14 or 15,
            "The source must be the disposable virtual VHDX.");
        Assert.True(virtualDescriptor.LengthBytes > 0);

        var availableBytesBefore = destinationDrive.AvailableFreeSpace;
        Assert.True(availableBytesBefore < virtualDescriptor.LengthBytes,
            $"The destination is not undersized: {availableBytesBefore:N0} free bytes for a {virtualDescriptor.LengthBytes:N0}-byte source.");

        var outputDirectory = Path.Combine(
            fullDestinationRoot, $"DiskRescue-Enospc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var imagePath = Path.Combine(outputDirectory, "must-not-publish.img");
        var manifestPath = imagePath + ".diskrescue.json";

        try
        {
            string sourceHashBefore;
            using (var before = new ReadOnlyPhysicalDriveSource(diskNumber, virtualDescriptor.LengthBytes))
                sourceHashBefore = HashSource(before);

            // This existing internal seam intentionally supplies synthetic inventory and
            // a permissive destination assessment. The actual output is a real, small NTFS
            // volume, so the native FileStream write must encounter the OS volume limit.
            var syntheticSource = new PhysicalDiskDescriptor(
                2, @"\\.\PhysicalDrive2", virtualDescriptor.LengthBytes,
                virtualDescriptor.BytesPerSector, "DiskRescue Test", "Disposable VHDX Source",
                "DISKRESCUE-TEST-SOURCE-0002", 3, false);
            var environment = new VhdxCoordinatorTestEnvironment(
                syntheticSource, diskNumber, virtualDescriptor.LengthBytes);

            var failure = await Record.ExceptionAsync(() =>
                PhysicalDiskAcquisitionCoordinator.AcquireAsync(
                    syntheticSource, imagePath, environment));

            Assert.NotNull(failure);
            Assert.True(IsDiskFullException(failure),
                $"Expected a genuine Windows disk-full error (112 or 39), but received {failure.GetType().FullName}, HResult=0x{failure.HResult:X8}: {failure}");

            string sourceHashAfter;
            using (var after = new ReadOnlyPhysicalDriveSource(diskNumber, virtualDescriptor.LengthBytes))
                sourceHashAfter = HashSource(after);

            Assert.Equal(sourceHashBefore, sourceHashAfter);
            Assert.False(File.Exists(imagePath));
            Assert.False(File.Exists(manifestPath));
            Assert.Empty(Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories));

            output.WriteLine($"Destination free bytes before copy: {availableBytesBefore:N0}.");
            output.WriteLine($"Source image length: {virtualDescriptor.LengthBytes:N0}.");
            output.WriteLine($"Native disk-full failure: HResult 0x{failure.HResult:X8}.");
            output.WriteLine($"Source SHA-256 remained unchanged: {sourceHashAfter}.");
            output.WriteLine("No final image, manifest, or temporary acquisition file remained after cleanup.");
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static bool IsDiskFullException(Exception? failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is System.ComponentModel.Win32Exception win32 &&
                win32.NativeErrorCode is 112 or 39)
                return true;

            var hresult = current.HResult;
            var lowWord = hresult & 0xFFFF;
            if ((hresult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) &&
                lowWord is 112 or 39)
                return true;
        }

        return false;
    }

    private sealed class VhdxCoordinatorTestEnvironment(
        PhysicalDiskDescriptor sourceDescriptor,
        int virtualDiskNumber,
        long sourceLength) : IPhysicalDiskAcquisitionEnvironment
    {
        private const string SourceVolume = @"\\?\Volume{DISKRESCUE-TEST-SOURCE}\";
        private IPhysicalAcquisitionDestinationSession? _destinationSession;

        public void EnsureSupportedPlatform() => WindowsPhysicalDiskDiscovery.EnsureWindows();

        public PhysicalDiskDiscoveryResult EnumeratePhysicalDisks() =>
            new(new[]
            {
                sourceDescriptor,
                new PhysicalDiskDescriptor(
                    7, @"\\.\PhysicalDrive7", sourceLength, sourceDescriptor.BytesPerSector,
                    "DiskRescue Test", "Synthetic Separate Destination",
                    "DISKRESCUE-TEST-DESTINATION-0007", 3, false)
            }, Array.Empty<string>());

        public PhysicalVolumeMappingResult EnumerateVolumes()
        {
            var destinationVolume = _destinationSession?.VolumeGuidPath
                ?? throw new InvalidOperationException("Destination session must be pinned before preflight.");
            return new(true, new Dictionary<int, IReadOnlyList<string>>
            {
                [sourceDescriptor.DiskNumber] = new[] { SourceVolume },
                [7] = new[] { destinationVolume }
            }, Array.Empty<string>());
        }

        public WindowsBootDeviceMappingResult EnumerateBootDevices(PhysicalVolumeMappingResult volumeMap) =>
            new(true, new HashSet<int>(), Array.Empty<string>());

        public PhysicalImageDestinationAssessment AssessDestination(
            int sourceDiskNumber, string destinationPath,
            PhysicalVolumeMappingResult volumeMap, long requiredBytes) =>
            new(true, _destinationSession?.VolumeGuidPath, new[] { 7 }, Array.Empty<string>());

        public PhysicalDiskOpenedSource OpenReadOnly(PhysicalDiskDescriptor descriptor)
        {
            if (descriptor.DiskNumber != sourceDescriptor.DiskNumber ||
                descriptor.LengthBytes != sourceLength)
                throw new IOException("The coordinator requested an unexpected synthetic source identity.");
            return new PhysicalDiskOpenedSource(
                new ReadOnlyPhysicalDriveSource(virtualDiskNumber, sourceLength),
                sourceDescriptor);
        }

        public IPhysicalAcquisitionDestinationSession OpenDestinationSession(string destinationDirectoryPath)
        {
            if (_destinationSession is not null)
                throw new InvalidOperationException("The destination session was opened more than once.");
            _destinationSession = WindowsPhysicalAcquisitionDestinationSession.Open(destinationDirectoryPath);
            return _destinationSession;
        }
    }

    private static string HashSource(IReadOnlyRandomAccessSource source)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[4 * 1024 * 1024];
        long offset = 0;
        while (offset < source.Length)
        {
            var count = (int)Math.Min(buffer.Length, source.Length - offset);
            source.ReadExactlyAt(offset, buffer.AsSpan(0, count));
            hash.AppendData(buffer, 0, count);
            offset += count;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class ReadOnlyPhysicalDriveSource : IReadOnlyRandomAccessSource
    {
        private readonly FileStream _stream;
        private bool _disposed;

        public ReadOnlyPhysicalDriveSource(int diskNumber, long length)
        {
            if (diskNumber < 0 || diskNumber > 127)
                throw new ArgumentOutOfRangeException(nameof(diskNumber));
            if (length <= 0)
                throw new ArgumentOutOfRangeException(nameof(length));

            Length = length;
            _stream = new FileStream(
                $@"\\.\PhysicalDrive{diskNumber}",
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                4096,
                FileOptions.RandomAccess);
        }

        public long Length { get; }

        public void ReadExactlyAt(long offset, Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (offset < 0 || offset > Length || destination.Length > Length - offset)
                throw new EndOfStreamException("Requested range exceeds the disposable VHDX source boundary.");

            var readTotal = 0;
            while (readTotal < destination.Length)
            {
                var read = RandomAccess.Read(
                    _stream.SafeFileHandle,
                    destination[readTotal..],
                    checked(offset + readTotal));
                if (read == 0)
                    throw new EndOfStreamException("The read-only VHDX device returned a short read.");
                readTotal += read;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _stream.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// xUnit v2 has no runtime skip API, so set the inherited Skip property during
/// test discovery when the disposable VHDX harness has not supplied a disk number.
/// </summary>
public sealed class WindowsVhdxEnospcFactAttribute : FactAttribute
{
    public WindowsVhdxEnospcFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISKRESCUE_TEST_VHDX_DISK_NUMBER")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISKRESCUE_TEST_DESTINATION_ROOT")))
        {
            Skip = "Run tools/Test-WindowsVhdxEnospc.ps1 to attach the read-only source and disposable NTFS destination VHDXs.";
        }
    }
}

public sealed class WindowsVhdxFactAttribute : FactAttribute
{
    public WindowsVhdxFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISKRESCUE_TEST_VHDX_DISK_NUMBER")))
        {
            Skip = "Run tools/Test-WindowsVhdxSafety.ps1 to create and attach a disposable read-only VHDX.";
        }
    }
}
