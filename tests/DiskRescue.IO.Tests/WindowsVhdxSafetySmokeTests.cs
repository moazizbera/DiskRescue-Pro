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
    public async Task ReadOnlyVhdxSourceCanBeCopiedAndVerifiedThroughNativeDestinationSession()
    {
        Assert.True(OperatingSystem.IsWindows(), "The VHDX integration test requires Windows.");
        var diskNumberText = Environment.GetEnvironmentVariable("DISKRESCUE_TEST_VHDX_DISK_NUMBER");
        Assert.True(int.TryParse(diskNumberText, out var diskNumber), "The VHDX disk number environment variable is invalid.");

        var inventory = WindowsPhysicalDiskDiscovery.Enumerate();
        var descriptor = Assert.Single(inventory.Disks, disk => disk.DiskNumber == diskNumber);
        Assert.True(descriptor.LengthBytes > 0);
        Assert.True(descriptor.BusType is 14 or 15, "This test must operate only on the disposable virtual source.");

        var outputDirectory = Path.Combine(
            Path.GetTempPath(), $"diskrescue-vhdx-writer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var imageLeaf = "vhdx-source-copy.img";
        var manifestLeaf = imageLeaf + ".diskrescue.json";
        var imagePath = Path.Combine(outputDirectory, imageLeaf);
        var manifestPath = Path.Combine(outputDirectory, manifestLeaf);

        try
        {
            using var source = new ReadOnlyPhysicalDriveSource(diskNumber, descriptor.LengthBytes);
            var sourceHashBefore = HashSource(source);
            PhysicalAcquisitionDestinationWriteResult result;

            // This deliberately exercises the native pinned-destination writer with a
            // read-only VHDX source, without bypassing the public acquisition policy or
            // claiming source/destination physical-volume separation. The destination
            // is a temporary CI folder on the runner volume.
            await using (var session = WindowsPhysicalAcquisitionDestinationSession.Open(outputDirectory))
            {
                result = await PhysicalAcquisitionDestinationWriter.WriteAndPublishAsync(
                    source,
                    session,
                    ".vhdx-source-copy.image.partial",
                    imageLeaf,
                    ".vhdx-source-copy.manifest.partial",
                    manifestLeaf,
                    image => new
                    {
                        format = "DiskRescue.VhdxWriterIntegrationManifest.v1",
                        source = new
                        {
                            diskNumber = descriptor.DiskNumber,
                            descriptor.DevicePath,
                            descriptor.LengthBytes,
                            descriptor.BytesPerSector,
                            descriptor.BusType
                        },
                        image = new
                        {
                            image.BytesCopied,
                            sha256 = image.Sha256,
                            verifiedBeforePublish = true
                        },
                        testScope = "read-only-disposable-vhdx-writer-integration-only",
                        physicalDestinationSeparationVerified = false
                    });
            }

            var sourceHashAfter = HashSource(source);
            Assert.Equal(sourceHashBefore, sourceHashAfter);
            Assert.Equal(descriptor.LengthBytes, result.BytesCopied);
            Assert.True(File.Exists(imagePath));
            Assert.True(File.Exists(manifestPath));
            Assert.Equal(descriptor.LengthBytes, new FileInfo(imagePath).Length);

            string destinationHash;
            await using (var imageStream = File.OpenRead(imagePath))
            {
                destinationHash = Convert.ToHexString(await SHA256.HashDataAsync(imageStream));
            }
            Assert.Equal(result.Sha256, destinationHash);

            using var manifestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var root = manifestDocument.RootElement;
            Assert.Equal("DiskRescue.VhdxWriterIntegrationManifest.v1", root.GetProperty("format").GetString());
            Assert.Equal(descriptor.LengthBytes, root.GetProperty("image").GetProperty("bytesCopied").GetInt64());
            Assert.Equal(result.Sha256, root.GetProperty("image").GetProperty("sha256").GetString());
            Assert.False(root.GetProperty("physicalDestinationSeparationVerified").GetBoolean());

            output.WriteLine($"Read-only VHDX source copied: {result.BytesCopied:N0} bytes.");
            output.WriteLine($"SHA-256 verified against the published image: {result.Sha256}.");
            output.WriteLine($"Source SHA-256 before and after imaging matched: {sourceHashAfter}.");
            output.WriteLine("Native pinned destination session published image and manifest without overwrite.");
            output.WriteLine("Scope limitation: this test does not invoke the coordinator and does not verify separate physical destination storage.");
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
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
