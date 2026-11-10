using System.Security.Cryptography;
using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class PhysicalDiskAcquisitionCoordinatorTests
{
    private static PhysicalDiskDescriptor Descriptor() =>
        new(2, @"\\.\PhysicalDrive2", 128 * 1024, 512,
            "Example", "Test Disk", "COORDINATOR-002", 3, false);

    [Fact]
    public async Task SecondPreflightFailurePreventsSourceOpen()
    {
        using var folder = new TemporaryFolder();
        var environment = new FakeEnvironment
        {
            InventoryFactory = call => call == 1
                ? Inventory()
                : new PhysicalDiskDiscoveryResult(new[] { Descriptor() }, new[] { "inventory became incomplete" })
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PhysicalDiskAcquisitionCoordinator.AcquireAsync(
                Descriptor(), folder.File("disk.img"), environment));

        Assert.Contains("revalidation failed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, environment.InventoryCalls);
        Assert.Equal(0, environment.OpenCalls);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task ChangedDeviceIdentityOnSecondPreflightPreventsSourceOpen()
    {
        using var folder = new TemporaryFolder();
        var changed = Descriptor() with { SerialNumber = "DIFFERENT-SERIAL" };
        var environment = new FakeEnvironment
        {
            InventoryFactory = call => Inventory(call == 1 ? Descriptor() : changed)
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PhysicalDiskAcquisitionCoordinator.AcquireAsync(
                Descriptor(), folder.File("disk.img"), environment));

        Assert.Equal(0, environment.OpenCalls);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task OpenedSourceIdentityMismatchAbortsBeforeAnyRead()
    {
        using var folder = new TemporaryFolder();
        var source = new MemorySource(new byte[128 * 1024]);
        var environment = new FakeEnvironment
        {
            OpenedDescriptor = Descriptor() with { SerialNumber = "WRONG-SERIAL" },
            Source = source
        };

        await Assert.ThrowsAsync<IOException>(() =>
            PhysicalDiskAcquisitionCoordinator.AcquireAsync(
                Descriptor(), folder.File("disk.img"), environment));

        Assert.Equal(0, source.ReadAttempts);
        Assert.False(File.Exists(folder.File("disk.img")));
        Assert.Equal(1, source.DisposeCalls);
    }

    [Fact]
    public async Task FinalPreReadRevalidationFailureDisposesSourceBeforeAnyRead()
    {
        using var folder = new TemporaryFolder();
        var source = new MemorySource(new byte[128 * 1024]);
        var environment = new FakeEnvironment
        {
            Source = source,
            InventoryFactory = call => call == 3
                ? new PhysicalDiskDiscoveryResult(new[] { Descriptor() }, new[] { "destination/source inventory changed" })
                : Inventory()
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PhysicalDiskAcquisitionCoordinator.AcquireAsync(
                Descriptor(), folder.File("late-change.img"), environment));

        Assert.Contains("final pre-read revalidation failed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, environment.InventoryCalls);
        Assert.Equal(1, environment.OpenCalls);
        Assert.Equal(0, source.ReadAttempts);
        Assert.Equal(1, source.DisposeCalls);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task DestinationSeparationChangeOnFinalPreReadCheckPreventsAnyRead()
    {
        using var folder = new TemporaryFolder();
        var source = new MemorySource(new byte[128 * 1024]);
        var environment = new FakeEnvironment
        {
            Source = source,
            DestinationAssessmentFactory = call => call == 3
                ? new PhysicalImageDestinationAssessment(false, null, new[] { 2 }, new[] { "destination now overlaps source disk" })
                : new PhysicalImageDestinationAssessment(true, @"\\?\Volume{test-destination}\", new[] { 7 }, Array.Empty<string>())
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PhysicalDiskAcquisitionCoordinator.AcquireAsync(
                Descriptor(), folder.File("destination-shift.img"), environment));

        Assert.Contains("destination now overlaps source disk", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, environment.DestinationAssessmentCalls);
        Assert.Equal(0, source.ReadAttempts);
        Assert.Equal(1, source.DisposeCalls);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task SourceReadFailureAbortsAndDoesNotPublishImageOrManifest()
    {
        using var folder = new TemporaryFolder();
        var source = new MemorySource(new byte[128 * 1024], failOnRead: true);
        var environment = new FakeEnvironment { Source = source };

        var error = await Assert.ThrowsAsync<IOException>(() =>
            PhysicalDiskAcquisitionCoordinator.AcquireAsync(
                Descriptor(), folder.File("read-failure.img"), environment));

        Assert.Contains("byte offset 0", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not zero-filled", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(folder.Path));
        Assert.Equal(0, environment.ManifestCalls);
        Assert.Equal(1, source.DisposeCalls);
    }

    [Fact]
    public async Task CancellationRemovesPartialImageAndNeverPublishesManifest()
    {
        using var folder = new TemporaryFolder();
        using var cancellation = new CancellationTokenSource();
        var source = new MemorySource(new byte[128 * 1024]);
        var environment = new FakeEnvironment { Source = source };
        var progress = new InlineProgress<DiskImageProgress>(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PhysicalDiskAcquisitionCoordinator.AcquireAsync(
                Descriptor(), folder.File("cancelled.img"), environment,
                progress, cancellation.Token));

        Assert.Empty(Directory.GetFiles(folder.Path));
        Assert.Equal(0, environment.ManifestCalls);
        Assert.Equal(1, source.DisposeCalls);
    }

    [Fact]
    public async Task DestinationOverlapBlocksBeforeOpeningSource()
    {
        using var folder = new TemporaryFolder();
        var environment = new FakeEnvironment
        {
            DestinationAssessment = new PhysicalImageDestinationAssessment(
                false, null, new[] { 2 }, new[] { "destination overlaps source disk" })
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PhysicalDiskAcquisitionCoordinator.AcquireAsync(
                Descriptor(), folder.File("overlap.img"), environment));

        Assert.Contains("overlaps source disk", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, environment.OpenCalls);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task SuccessfulAcquisitionPublishesVerifiedImageAndManifestAndDisposesSource()
    {
        using var folder = new TemporaryFolder();
        var bytes = Enumerable.Range(0, 128 * 1024).Select(i => (byte)((i * 17) % 253)).ToArray();
        var source = new MemorySource(bytes);
        var environment = new FakeEnvironment { Source = source };
        var imagePath = folder.File("successful.img");

        var result = await PhysicalDiskAcquisitionCoordinator.AcquireAsync(
            Descriptor(), imagePath, environment);

        var expectedSha256 = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.True(File.Exists(imagePath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(imagePath));
        Assert.Equal(expectedSha256, result.Image.Sha256);
        Assert.Equal(expectedSha256, environment.ManifestSha256);
        Assert.Equal(imagePath + ".diskrescue.json", result.ManifestPath);
        Assert.Equal(1, environment.ManifestCalls);
        Assert.Equal(1, source.DisposeCalls);
    }

    [Fact]
    public async Task ManifestPublicationFailurePreservesVerifiedImageAndDigest()
    {
        using var folder = new TemporaryFolder();
        var bytes = Enumerable.Range(0, 128 * 1024).Select(i => (byte)(i % 251)).ToArray();
        var source = new MemorySource(bytes);
        var environment = new FakeEnvironment
        {
            Source = source,
            ManifestException = new IOException("synthetic manifest storage failure")
        };
        var imagePath = folder.File("verified.img");

        var error = await Assert.ThrowsAsync<IOException>(() =>
            PhysicalDiskAcquisitionCoordinator.AcquireAsync(
                Descriptor(), imagePath, environment));

        Assert.Contains("synthetic manifest storage failure", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(imagePath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(imagePath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), environment.ManifestSha256);
        Assert.Equal(1, environment.ManifestCalls);
        Assert.Equal(1, source.DisposeCalls);
    }

    [Fact]
    public async Task ExistingManifestCollisionPreventsPreflightAndSourceOpen()
    {
        using var folder = new TemporaryFolder();
        var imagePath = folder.File("collision.img");
        await File.WriteAllTextAsync(imagePath + ".diskrescue.json", "preserve");

        var environment = new FakeEnvironment();
        await Assert.ThrowsAsync<IOException>(() =>
            PhysicalDiskAcquisitionCoordinator.AcquireAsync(
                Descriptor(), imagePath, environment));

        Assert.Equal("preserve", await File.ReadAllTextAsync(imagePath + ".diskrescue.json"));
        Assert.Equal(0, environment.InventoryCalls);
        Assert.Equal(0, environment.OpenCalls);
    }

    private static PhysicalDiskDiscoveryResult Inventory(PhysicalDiskDescriptor? descriptor = null) =>
        new(new[] { descriptor ?? Descriptor() }, Array.Empty<string>());

    private sealed class FakeEnvironment : IPhysicalDiskAcquisitionEnvironment
    {
        public Func<int, PhysicalDiskDiscoveryResult>? InventoryFactory { get; init; }
        public PhysicalImageDestinationAssessment? DestinationAssessment { get; init; }
        public Func<int, PhysicalImageDestinationAssessment>? DestinationAssessmentFactory { get; init; }
        public int DestinationAssessmentCalls { get; private set; }
        public PhysicalDiskDescriptor? OpenedDescriptor { get; init; }
        public MemorySource? Source { get; init; }
        public Exception? ManifestException { get; init; }
        public int InventoryCalls { get; private set; }
        public int OpenCalls { get; private set; }
        public int ManifestCalls { get; private set; }
        public string? ManifestSha256 { get; private set; }

        public void EnsureSupportedPlatform() { }

        public PhysicalDiskDiscoveryResult EnumeratePhysicalDisks()
        {
            InventoryCalls++;
            return InventoryFactory?.Invoke(InventoryCalls) ?? Inventory();
        }

        public PhysicalVolumeMappingResult EnumerateVolumes() =>
            new(true, new Dictionary<int, IReadOnlyList<string>>(), Array.Empty<string>());

        public WindowsBootDeviceMappingResult EnumerateBootDevices(PhysicalVolumeMappingResult volumeMap) =>
            new(true, new HashSet<int>(), Array.Empty<string>());

        public PhysicalImageDestinationAssessment AssessDestination(
            int sourceDiskNumber, string destinationPath,
            PhysicalVolumeMappingResult volumeMap, long requiredBytes)
        {
            DestinationAssessmentCalls++;
            return DestinationAssessmentFactory?.Invoke(DestinationAssessmentCalls) ??
                DestinationAssessment ?? new(true, @"\\?\Volume{test-destination}\",
                    new[] { 7 }, Array.Empty<string>());
        }

        public PhysicalDiskOpenedSource OpenReadOnly(PhysicalDiskDescriptor descriptor)
        {
            OpenCalls++;
            var source = Source ?? new MemorySource(new byte[descriptor.LengthBytes]);
            return new PhysicalDiskOpenedSource(source, OpenedDescriptor ?? descriptor);
        }

        public Task PublishManifestAsync(string manifestPath, object manifest, string imagePath, string sha256)
        {
            ManifestCalls++;
            ManifestSha256 = sha256;
            return ManifestException is null ? Task.CompletedTask : Task.FromException(ManifestException);
        }
    }

    private sealed class MemorySource(byte[] data, bool failOnRead = false) : IReadOnlyRandomAccessSource
    {
        public long Length => data.Length;
        public int ReadAttempts { get; private set; }
        public int DisposeCalls { get; private set; }

        public void ReadExactlyAt(long offset, Span<byte> destination)
        {
            ReadAttempts++;
            if (failOnRead)
                throw new IOException("Synthetic source read failure.");
            if (offset < 0 || offset > data.Length || destination.Length > data.Length - offset)
                throw new EndOfStreamException();
            data.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
        }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"diskrescue-coordinator-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
