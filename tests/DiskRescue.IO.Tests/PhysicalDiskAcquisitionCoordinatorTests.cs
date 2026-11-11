using System.Security.Cryptography;
using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class PhysicalDiskAcquisitionCoordinatorTests
{
    private const string DestinationVolumeGuid =
        @"\\?\Volume{00000000-0000-0000-0000-000000000007}\";
    private const string SourceVolumeGuid =
        @"\\?\Volume{00000000-0000-0000-0000-000000000002}\";

    private static PhysicalDiskDescriptor Descriptor() =>
        new(2, @"\\.\PhysicalDrive2", 128 * 1024, 512,
            "Example", "Test Disk", "COORDINATOR-002", 3, false);

    private static PhysicalDiskDescriptor DestinationDescriptor() =>
        new(7, @"\\.\PhysicalDrive7", 128 * 1024, 512,
            "Example", "Destination Disk", "COORDINATOR-007", 3, false);

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
    public async Task PinnedDestinationVolumeMismatchAbortsBeforeFirstSourceRead()
    {
        using var folder = new TemporaryFolder();
        var source = new MemorySource(new byte[128 * 1024]);
        var environment = new FakeEnvironment
        {
            Source = source,
            PinnedVolumeGuidPath = SourceVolumeGuid
        };

        var error = await Assert.ThrowsAsync<IOException>(() =>
            PhysicalDiskAcquisitionCoordinator.AcquireAsync(
                Descriptor(), folder.File("redirected-destination.img"), environment));

        Assert.Contains("Pinned destination volume", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, environment.DestinationSessionOpenCalls);
        Assert.Equal(0, source.ReadAttempts);
        Assert.Equal(1, source.DisposeCalls);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task DestinationPathReplacementAfterPinningAbortsBeforeFirstSourceRead()
    {
        using var folder = new TemporaryFolder();
        var source = new MemorySource(new byte[128 * 1024]);
        var environment = new FakeEnvironment
        {
            Source = source,
            PinnedDirectoryPath = Path.Combine(folder.Path, "replacement")
        };

        var error = await Assert.ThrowsAsync<IOException>(() =>
            PhysicalDiskAcquisitionCoordinator.AcquireAsync(
                Descriptor(), folder.File("replaced-directory.img"), environment));

        Assert.Contains("directory object pinned", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, environment.DestinationSessionOpenCalls);
        Assert.Equal(0, environment.OpenCalls);
        Assert.Equal(0, source.ReadAttempts);
        Assert.Equal(0, source.DisposeCalls);
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
        Assert.Equal(1, source.DisposeCalls);
    }

    [Fact]
    public async Task DestinationFlushFailureRemovesUnpublishedImageAndManifest()
    {
        using var folder = new TemporaryFolder();
        var source = new MemorySource(new byte[128 * 1024]);
        var environment = new FakeEnvironment
        {
            Source = source,
            ImageFlushException = new IOException("synthetic destination flush failure")
        };

        var error = await Assert.ThrowsAsync<IOException>(() =>
            PhysicalDiskAcquisitionCoordinator.AcquireAsync(
                Descriptor(), folder.File("flush-failure.img"), environment));

        Assert.Contains("synthetic destination flush failure", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(folder.Path));
        Assert.Equal(1, source.DisposeCalls);
    }

    [Fact]
    public async Task CleanupFailurePreservesBothAcquisitionAndCleanupErrors()
    {
        using var folder = new TemporaryFolder();
        using var cancellation = new CancellationTokenSource();
        var source = new MemorySource(new byte[128 * 1024]);
        var environment = new FakeEnvironment
        {
            Source = source,
            CleanupException = new IOException("synthetic destination cleanup failure")
        };
        var progress = new InlineProgress<DiskImageProgress>(_ => cancellation.Cancel());

        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            PhysicalDiskAcquisitionCoordinator.AcquireAsync(
                Descriptor(), folder.File("cleanup-failure.img"), environment,
                progress, cancellation.Token));

        Assert.Contains(error.InnerExceptions, exception => exception is OperationCanceledException);
        Assert.Contains(error.InnerExceptions, exception =>
            exception.Message.Contains("synthetic destination cleanup failure", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(folder.File("cleanup-failure.img")));
        Assert.False(File.Exists(folder.File("cleanup-failure.img.diskrescue.json")));
        Assert.Contains(Directory.GetFiles(folder.Path), path =>
            Path.GetFileName(path).Contains("partial", StringComparison.OrdinalIgnoreCase));
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
        Assert.Equal(imagePath + ".diskrescue.json", result.ManifestPath);
        var manifestText = await File.ReadAllTextAsync(result.ManifestPath);
        Assert.Contains(expectedSha256, manifestText, StringComparison.OrdinalIgnoreCase);
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

        var error = await Assert.ThrowsAsync<PhysicalAcquisitionManifestCommitException>(() =>
            PhysicalDiskAcquisitionCoordinator.AcquireAsync(
                Descriptor(), imagePath, environment));

        Assert.Contains(
            "synthetic manifest storage failure",
            error.InnerException?.Message ?? error.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("verified.img", error.ImageLeafName);
        Assert.Equal("verified.img.diskrescue.json", error.ManifestLeafName);
        Assert.True(File.Exists(imagePath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(imagePath));
        Assert.False(File.Exists(imagePath + ".diskrescue.json"));
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
        new(new[] { descriptor ?? Descriptor(), DestinationDescriptor() }, Array.Empty<string>());

    private sealed class FakeEnvironment : IPhysicalDiskAcquisitionEnvironment
    {
        public Func<int, PhysicalDiskDiscoveryResult>? InventoryFactory { get; init; }
        public PhysicalImageDestinationAssessment? DestinationAssessment { get; init; }
        public Func<int, PhysicalImageDestinationAssessment>? DestinationAssessmentFactory { get; init; }
        public int DestinationAssessmentCalls { get; private set; }
        public PhysicalDiskDescriptor? OpenedDescriptor { get; init; }
        public MemorySource? Source { get; init; }
        public Exception? ManifestException { get; init; }
        public Exception? CleanupException { get; init; }
        public Exception? ImageFlushException { get; init; }
        public string? PinnedVolumeGuidPath { get; init; }
        public string? PinnedDirectoryPath { get; init; }
        public int InventoryCalls { get; private set; }
        public int OpenCalls { get; private set; }
        public int DestinationSessionOpenCalls { get; private set; }

        public void EnsureSupportedPlatform() { }

        public PhysicalDiskDiscoveryResult EnumeratePhysicalDisks()
        {
            InventoryCalls++;
            return InventoryFactory?.Invoke(InventoryCalls) ?? Inventory();
        }

        public PhysicalVolumeMappingResult EnumerateVolumes() =>
            new(true, new Dictionary<int, IReadOnlyList<string>>
            {
                [2] = new[] { SourceVolumeGuid },
                [7] = new[] { DestinationVolumeGuid }
            }, Array.Empty<string>());

        public WindowsBootDeviceMappingResult EnumerateBootDevices(PhysicalVolumeMappingResult volumeMap) =>
            new(true, new HashSet<int>(), Array.Empty<string>());

        public PhysicalImageDestinationAssessment AssessDestination(
            int sourceDiskNumber, string destinationPath,
            PhysicalVolumeMappingResult volumeMap, long requiredBytes)
        {
            DestinationAssessmentCalls++;
            return DestinationAssessmentFactory?.Invoke(DestinationAssessmentCalls) ??
                DestinationAssessment ?? new(true, DestinationVolumeGuid,
                    new[] { 7 }, Array.Empty<string>());
        }

        public PhysicalDiskOpenedSource OpenReadOnly(PhysicalDiskDescriptor descriptor)
        {
            OpenCalls++;
            var source = Source ?? new MemorySource(new byte[descriptor.LengthBytes]);
            return new PhysicalDiskOpenedSource(source, OpenedDescriptor ?? descriptor);
        }

        public IPhysicalAcquisitionDestinationSession OpenDestinationSession(string destinationDirectoryPath)
        {
            DestinationSessionOpenCalls++;
            return new FakeDestinationSession(
                destinationDirectoryPath,
                ManifestException,
                CleanupException,
                ImageFlushException,
                PinnedVolumeGuidPath ?? DestinationVolumeGuid,
                PinnedDirectoryPath ?? destinationDirectoryPath);
        }
    }

    private sealed class FakeDestinationSession : IPhysicalAcquisitionDestinationSession
    {
        private readonly string _directory;
        private readonly Exception? _manifestException;
        private readonly Exception? _cleanupException;
        private readonly Exception? _imageFlushException;
        private readonly Dictionary<Stream, string> _paths = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Stream> _published = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Stream> _deleted = new(ReferenceEqualityComparer.Instance);
        private bool _disposed;

        private readonly string _reportedDirectoryPath;

        public FakeDestinationSession(
            string directory, Exception? manifestException, Exception? cleanupException, Exception? imageFlushException, string volumeGuidPath, string reportedDirectoryPath)
        {
            _directory = directory;
            _manifestException = manifestException;
            _cleanupException = cleanupException;
            _imageFlushException = imageFlushException;
            VolumeGuidPath = volumeGuidPath;
            _reportedDirectoryPath = reportedDirectoryPath;
            DirectoryIdentity = new PhysicalDestinationDirectoryIdentity("WINVOL:COORDINATOR", 0, 2);
        }

        public PhysicalDestinationDirectoryIdentity DirectoryIdentity { get; }
        public string VolumeGuidPath { get; }
        public string GetCurrentDirectoryPath() => _reportedDirectoryPath;

        public ValueTask<Stream> CreateNewFileAsync(string leafName, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            PhysicalDestinationLeafName.Validate(leafName, nameof(leafName));
            var path = Path.Combine(_directory, leafName);
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException("Destination entry already exists.");
            var stream = new FileStream(
                path, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None);
            _paths.Add(stream, path);
            return ValueTask.FromResult<Stream>(stream);
        }

        public ValueTask<PhysicalDestinationFileIdentity> GetFileIdentityAsync(
            Stream file, CancellationToken cancellationToken = default)
        {
            EnsureOwned(file);
            cancellationToken.ThrowIfCancellationRequested();
            var fileId = (ulong)_paths.Keys.ToList().IndexOf(file) + 1;
            return ValueTask.FromResult(new PhysicalDestinationFileIdentity(
                DirectoryIdentity.VolumeIdentity, 0, fileId));
        }

        public ValueTask FlushToDiskAsync(Stream file, CancellationToken cancellationToken = default)
        {
            EnsureOwned(file);
            cancellationToken.ThrowIfCancellationRequested();
            if (_imageFlushException is not null &&
                Path.GetFileName(_paths[file]).EndsWith(".image.partial", StringComparison.OrdinalIgnoreCase))
                throw _imageFlushException;
            ((FileStream)file).Flush(flushToDisk: true);
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishNoReplaceAsync(
            Stream file, string finalLeafName, CancellationToken cancellationToken = default)
        {
            EnsureOwned(file);
            cancellationToken.ThrowIfCancellationRequested();
            PhysicalDestinationLeafName.Validate(finalLeafName, nameof(finalLeafName));
            var target = Path.Combine(_directory, finalLeafName);
            if (File.Exists(target) || Directory.Exists(target))
                throw new IOException("Destination entry already exists.");
            if (_manifestException is not null &&
                finalLeafName.EndsWith(".diskrescue.json", StringComparison.OrdinalIgnoreCase))
                throw _manifestException;

            File.Move(_paths[file], target, overwrite: false);
            _paths[file] = target;
            _published.Add(file);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteUnpublishedAsync(Stream file, CancellationToken cancellationToken = default)
        {
            EnsureOwned(file);
            cancellationToken.ThrowIfCancellationRequested();
            if (_published.Contains(file))
                throw new InvalidOperationException("Published file cannot be deleted as an unpublished temporary.");
            if (_cleanupException is not null)
                throw _cleanupException;
            if (File.Exists(_paths[file]))
                File.Delete(_paths[file]);
            _deleted.Add(file);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;
            foreach (var stream in _paths.Keys)
                stream.Dispose();
            _paths.Clear();
            return ValueTask.CompletedTask;
        }

        private void EnsureOwned(Stream file)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(file);
            if (!_paths.ContainsKey(file) || _deleted.Contains(file) || !file.CanRead)
                throw new ArgumentException("Stream is not an active file owned by this test session.", nameof(file));
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
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
