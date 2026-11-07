namespace DiskRescue.IO;

/// <summary>
/// A read-only source opened after the environment's second preflight. Ownership transfers
/// to the coordinator for the duration of the copy.
/// </summary>
public sealed class PhysicalDiskOpenedSource : IDisposable
{
    public PhysicalDiskOpenedSource(IReadOnlyRandomAccessSource source, PhysicalDiskDescriptor descriptor)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    }

    public IReadOnlyRandomAccessSource Source { get; }
    public PhysicalDiskDescriptor Descriptor { get; }
    public void Dispose() => Source.Dispose();
}

/// <summary>
/// Injectable boundary around Windows hardware discovery and read-only device opening.
/// Production uses WindowsPhysicalDiskAcquisitionEnvironment; tests can supply deterministic
/// snapshots and disposable in-memory sources without weakening production policy.
/// </summary>
public interface IPhysicalDiskAcquisitionEnvironment
{
    void EnsureSupportedPlatform();
    PhysicalDiskDiscoveryResult EnumeratePhysicalDisks();
    PhysicalVolumeMappingResult EnumerateVolumes();
    WindowsBootDeviceMappingResult EnumerateBootDevices(PhysicalVolumeMappingResult volumeMap);
    PhysicalImageDestinationAssessment AssessDestination(
        int sourceDiskNumber, string destinationPath,
        PhysicalVolumeMappingResult volumeMap, long requiredBytes);
    PhysicalDiskOpenedSource OpenReadOnly(PhysicalDiskDescriptor descriptor);
}

/// <summary>Real Windows implementation. No virtual-device or production policy bypasses are added.</summary>
public sealed class WindowsPhysicalDiskAcquisitionEnvironment : IPhysicalDiskAcquisitionEnvironment
{
    public void EnsureSupportedPlatform() => WindowsPhysicalDiskDiscovery.EnsureWindows();

    public PhysicalDiskDiscoveryResult EnumeratePhysicalDisks() =>
        WindowsPhysicalDiskDiscovery.Enumerate();

    public PhysicalVolumeMappingResult EnumerateVolumes() =>
        WindowsPhysicalDiskVolumeMap.Enumerate();

    public WindowsBootDeviceMappingResult EnumerateBootDevices(PhysicalVolumeMappingResult volumeMap) =>
        WindowsBootDeviceMap.Enumerate(volumeMap);

    public PhysicalImageDestinationAssessment AssessDestination(
        int sourceDiskNumber, string destinationPath,
        PhysicalVolumeMappingResult volumeMap, long requiredBytes) =>
        PhysicalImageDestinationSafety.Assess(sourceDiskNumber, destinationPath, volumeMap, requiredBytes);

    public PhysicalDiskOpenedSource OpenReadOnly(PhysicalDiskDescriptor descriptor)
    {
        var reader = WindowsPhysicalDiskReader.Open(descriptor);
        return new PhysicalDiskOpenedSource(reader, reader.Descriptor);
    }
}

/// <summary>
/// Orchestrates a two-pass fail-closed preflight, identity revalidation, read-only imaging,
/// SHA-256 verification, and non-cancellable sidecar publication after image commit.
/// </summary>
public static class PhysicalDiskAcquisitionCoordinator
{
    public static async Task<PhysicalDiskAcquisitionResult> AcquireAsync(
        PhysicalDiskDescriptor selected,
        string destinationImagePath,
        IPhysicalDiskAcquisitionEnvironment environment,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationImagePath);

        environment.EnsureSupportedPlatform();
        cancellationToken.ThrowIfCancellationRequested();

        var fullImagePath = ValidateNewImageDestination(destinationImagePath);
        var manifestPath = fullImagePath + ".diskrescue.json";
        if (File.Exists(manifestPath) || Directory.Exists(manifestPath))
            throw new IOException("Acquisition manifest already exists; existing files are never overwritten.");

        var first = RunPreflight(selected, fullImagePath, environment);
        if (!first.Assessment.CanAcquire)
            throw new InvalidOperationException("Physical acquisition preflight failed: " +
                string.Join(" ", first.Assessment.Blockers));

        cancellationToken.ThrowIfCancellationRequested();
        var second = RunPreflight(selected, fullImagePath, environment);
        if (!second.Assessment.CanAcquire)
            throw new InvalidOperationException("Physical acquisition revalidation failed: " +
                string.Join(" ", second.Assessment.Blockers));

        cancellationToken.ThrowIfCancellationRequested();
        using var opened = environment.OpenReadOnly(second.CurrentDescriptor);
        if (!WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(second.CurrentDescriptor, opened.Descriptor))
            throw new IOException("The opened physical device does not match the revalidated source identity.");
        if (opened.Source.Length != opened.Descriptor.LengthBytes)
            throw new IOException("The opened read-only source length does not match its revalidated disk capacity.");

        var image = await DiskImager.CreateImageAsync(
            opened.Source, fullImagePath, progress, cancellationToken).ConfigureAwait(false);

        var manifest = new
        {
            format = "DiskRescue.PhysicalAcquisitionManifest.v1",
            acquiredAtUtc = DateTimeOffset.UtcNow,
            source = new
            {
                opened.Descriptor.DiskNumber,
                opened.Descriptor.DevicePath,
                opened.Descriptor.LengthBytes,
                opened.Descriptor.BytesPerSector,
                opened.Descriptor.Vendor,
                opened.Descriptor.Model,
                opened.Descriptor.SerialNumber,
                opened.Descriptor.BusType,
                opened.Descriptor.RemovableMedia
            },
            image = new
            {
                image.ImagePath,
                image.BytesCopied,
                sha256 = image.Sha256,
                verifiedBeforePublish = true
            },
            acquisition = new
            {
                mode = "read-only-live-copy",
                unreadableSectorPolicy = "abort-without-zero-fill",
                hardwareWriteBlocked = false,
                pointInTimeConsistencyGuaranteed = false,
                resumable = false
            },
            warnings = second.Assessment.Warnings
        };

        await PhysicalDiskManifestPublisher.PublishAsync(
            manifestPath, manifest, image.ImagePath, image.Sha256).ConfigureAwait(false);

        return new(image, manifestPath, opened.Descriptor, second.Assessment.Warnings);
    }

    private static string ValidateNewImageDestination(string destinationImagePath)
    {
        if (destinationImagePath.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
            destinationImagePath.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase) ||
            destinationImagePath.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase) ||
            destinationImagePath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Image destination must be a regular filesystem path.", nameof(destinationImagePath));

        var fullPath = Path.GetFullPath(destinationImagePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new IOException("Destination directory is invalid.");
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Destination directory must already exist.");
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new IOException("Destination image already exists; existing files are never overwritten.");
        return fullPath;
    }

    private static (PhysicalDiskAcquisitionAssessment Assessment, PhysicalDiskDescriptor CurrentDescriptor)
        RunPreflight(
            PhysicalDiskDescriptor selected,
            string destinationPath,
            IPhysicalDiskAcquisitionEnvironment environment)
    {
        var inventory = environment.EnumeratePhysicalDisks();
        var volumeMap = environment.EnumerateVolumes();
        var bootMap = environment.EnumerateBootDevices(volumeMap);
        var assessment = PhysicalDiskImageAcquisition.Assess(selected, inventory, volumeMap, bootMap);
        if (!assessment.CanAcquire)
            return (assessment, selected);

        var destination = environment.AssessDestination(
            selected.DiskNumber, destinationPath, volumeMap, selected.LengthBytes);
        if (!destination.CanUseAsSeparateDestination)
        {
            assessment = assessment with
            {
                CanAcquire = false,
                Blockers = assessment.Blockers.Concat(destination.Blockers).ToArray()
            };
            return (assessment, selected);
        }

        var current = inventory.Disks.Single(disk => disk.DiskNumber == selected.DiskNumber);
        return (assessment, current);
    }
}
