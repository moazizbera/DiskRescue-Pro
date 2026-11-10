namespace DiskRescue.IO;

/// <summary>
/// A read-only source opened after the environment's second preflight and final pre-read revalidation. Ownership transfers
/// to the coordinator for the duration of the copy.
/// </summary>
internal sealed class PhysicalDiskOpenedSource : IDisposable
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
/// Injectable boundary around Windows hardware discovery, read-only source opening,
/// and pinned destination-session creation. Tests can supply deterministic snapshots
/// and disposable sources without weakening production policy.
/// </summary>
internal interface IPhysicalDiskAcquisitionEnvironment
{
    void EnsureSupportedPlatform();
    PhysicalDiskDiscoveryResult EnumeratePhysicalDisks();
    PhysicalVolumeMappingResult EnumerateVolumes();
    WindowsBootDeviceMappingResult EnumerateBootDevices(PhysicalVolumeMappingResult volumeMap);
    PhysicalImageDestinationAssessment AssessDestination(
        int sourceDiskNumber, string destinationPath,
        PhysicalVolumeMappingResult volumeMap, long requiredBytes);
    PhysicalDiskOpenedSource OpenReadOnly(PhysicalDiskDescriptor descriptor);
    IPhysicalAcquisitionDestinationSession OpenDestinationSession(string destinationDirectoryPath);
}

/// <summary>Real Windows implementation. No virtual-device or production policy bypasses are added.</summary>
internal sealed class WindowsPhysicalDiskAcquisitionEnvironment : IPhysicalDiskAcquisitionEnvironment
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

    public IPhysicalAcquisitionDestinationSession OpenDestinationSession(string destinationDirectoryPath) =>
        WindowsPhysicalAcquisitionDestinationSession.Open(destinationDirectoryPath);
}

/// <summary>
/// Orchestrates repeated fail-closed preflight, source identity revalidation, pinned
/// destination-volume reconciliation, read-only imaging, and handle-relative image/manifest
/// publication. No path-based image or manifest writer is used by this coordinator.
/// </summary>
internal static class PhysicalDiskAcquisitionCoordinator
{
    private sealed record PreflightSnapshot(
        PhysicalDiskAcquisitionAssessment Assessment,
        PhysicalDiskDescriptor CurrentDescriptor,
        PhysicalDiskDiscoveryResult Inventory,
        PhysicalVolumeMappingResult VolumeMap,
        PhysicalImageDestinationAssessment? Destination);

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
        var manifestPreflightPath = fullImagePath + ".diskrescue.json";
        if (File.Exists(manifestPreflightPath) || Directory.Exists(manifestPreflightPath))
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

        // Re-enumerate source, boot, volume, and path-based destination state after
        // opening the source but before reading its first byte.
        cancellationToken.ThrowIfCancellationRequested();
        var finalPreflight = RunPreflight(selected, fullImagePath, environment);
        if (!finalPreflight.Assessment.CanAcquire)
            throw new InvalidOperationException("Physical acquisition final pre-read revalidation failed: " +
                string.Join(" ", finalPreflight.Assessment.Blockers));
        if (!WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(second.CurrentDescriptor, finalPreflight.CurrentDescriptor) ||
            !WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(opened.Descriptor, finalPreflight.CurrentDescriptor))
            throw new IOException("The source device identity changed during final pre-read revalidation. No source sectors were read.");

        var destinationAssessment = finalPreflight.Destination
            ?? throw new IOException("Final preflight did not produce a destination assessment.");
        var destinationDirectory = Path.GetDirectoryName(fullImagePath)
            ?? throw new IOException("Destination directory is invalid.");

        // Pin the directory only after final preflight, then reconcile the volume GUID
        // returned by that handle against the complete volume map and the extent list
        // already inspected by destination preflight. A changed path cannot silently
        // redirect output to another volume.
        await using var destinationSession = environment.OpenDestinationSession(destinationDirectory);
        var requestedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        var pinnedDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(destinationSession.GetCurrentDirectoryPath()));
        if (!string.Equals(requestedDirectory, pinnedDirectory, StringComparison.OrdinalIgnoreCase))
            throw new IOException(
                "The destination directory path no longer resolves to the directory object pinned by the session. No source sectors were read.");

        var pinnedMappingIssues = PhysicalImageDestinationSafety.ValidatePinnedDestinationVolume(
            selected.DiskNumber,
            destinationSession.VolumeGuidPath,
            finalPreflight.VolumeMap,
            destinationAssessment.DestinationDiskNumbers);
        if (pinnedMappingIssues.Count != 0)
            throw new IOException("Pinned destination volume failed physical-disk reconciliation: " +
                string.Join(" ", pinnedMappingIssues));

        var physicalIdentityIssues = PhysicalImageDestinationSafety.ValidatePhysicalDiskIdentities(
            finalPreflight.Inventory,
            new[] { selected.DiskNumber },
            destinationAssessment.DestinationDiskNumbers);
        if (physicalIdentityIssues.Count != 0)
            throw new IOException("Pinned destination physical-disk identity check failed: " +
                string.Join(" ", physicalIdentityIssues));

        cancellationToken.ThrowIfCancellationRequested();

        var imageLeaf = Path.GetFileName(fullImagePath);
        var manifestLeaf = Path.GetFileName(manifestPreflightPath);
        if (string.IsNullOrWhiteSpace(imageLeaf) || string.IsNullOrWhiteSpace(manifestLeaf))
            throw new IOException("Image or manifest destination leaf name is invalid.");

        var writeResult = await PhysicalAcquisitionDestinationWriter.WriteAndPublishAsync(
            opened.Source,
            destinationSession,
            $".diskrescue-{Guid.NewGuid():N}.image.partial",
            imageLeaf,
            $".diskrescue-{Guid.NewGuid():N}.manifest.partial",
            manifestLeaf,
            imageContent =>
            {
                // Resolve the directory name again from the pinned handle after the image
                // commit; the original input path may have been renamed or replaced.
                var currentDirectoryPath = destinationSession.GetCurrentDirectoryPath();
                var currentImagePath = Path.Combine(currentDirectoryPath, imageLeaf);
                return new
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
                    destination = new
                    {
                        volumeGuidPath = destinationSession.VolumeGuidPath,
                        directoryPathAtManifestCreation = currentDirectoryPath,
                        directoryFileIdHigh = destinationSession.DirectoryIdentity.FileIdHigh,
                        directoryFileIdLow = destinationSession.DirectoryIdentity.FileIdLow
                    },
                    image = new
                    {
                        ImagePath = currentImagePath,
                        imageContent.BytesCopied,
                        sha256 = imageContent.Sha256,
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
                    warnings = finalPreflight.Assessment.Warnings
                };
            },
            progress,
            cancellationToken).ConfigureAwait(false);

        var committedDirectoryPath = destinationSession.GetCurrentDirectoryPath();
        var committedImagePath = Path.Combine(committedDirectoryPath, imageLeaf);
        var committedManifestPath = Path.Combine(committedDirectoryPath, manifestLeaf);
        var image = new DiskImageResult(
            committedImagePath, writeResult.BytesCopied, writeResult.Sha256);
        return new(image, committedManifestPath, opened.Descriptor, finalPreflight.Assessment.Warnings);
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

    private static PreflightSnapshot RunPreflight(
        PhysicalDiskDescriptor selected,
        string destinationPath,
        IPhysicalDiskAcquisitionEnvironment environment)
    {
        var inventory = environment.EnumeratePhysicalDisks();
        var volumeMap = environment.EnumerateVolumes();
        var bootMap = environment.EnumerateBootDevices(volumeMap);
        var assessment = PhysicalDiskImageAcquisition.Assess(selected, inventory, volumeMap, bootMap);
        if (!assessment.CanAcquire)
            return new(assessment, selected, inventory, volumeMap, null);

        var destination = environment.AssessDestination(
            selected.DiskNumber, destinationPath, volumeMap, selected.LengthBytes);
        if (!destination.CanUseAsSeparateDestination)
        {
            assessment = assessment with
            {
                CanAcquire = false,
                Blockers = assessment.Blockers.Concat(destination.Blockers).ToArray()
            };
            return new(assessment, selected, inventory, volumeMap, destination);
        }

        var current = inventory.Disks.Single(disk => disk.DiskNumber == selected.DiskNumber);
        return new(assessment, current, inventory, volumeMap, destination);
    }
}
