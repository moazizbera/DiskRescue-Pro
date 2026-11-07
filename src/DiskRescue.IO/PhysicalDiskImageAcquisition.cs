using System.Text.Json;

namespace DiskRescue.IO;

public sealed record PhysicalDiskAcquisitionAssessment(
    int DiskNumber,
    bool CanAcquire,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings);

public sealed record PhysicalDiskAcquisitionResult(
    DiskImageResult Image,
    string ManifestPath,
    PhysicalDiskDescriptor Source,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Coordinates a guarded, read-only physical-disk acquisition to a newly-created image
/// on a separately verified volume. This is an initial all-or-nothing acquisition path:
/// read errors abort the image; unreadable sectors are never silently replaced with zeroes.
/// </summary>
public static class PhysicalDiskImageAcquisition
{
    private const uint BusTypeUnknown = 0;
    private const uint BusTypeRaid = 8;
    private const uint BusTypeVirtual = 14;
    private const uint BusTypeFileBackedVirtual = 15;
    private const uint BusTypeStorageSpaces = 16;

    public static PhysicalDiskAcquisitionAssessment Assess(
        PhysicalDiskDescriptor selected,
        PhysicalDiskDiscoveryResult inventory,
        PhysicalVolumeMappingResult volumeMap,
        WindowsBootDeviceMappingResult bootMap)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(volumeMap);
        ArgumentNullException.ThrowIfNull(bootMap);

        var blockers = new List<string>();
        var warnings = new List<string>();
        var current = inventory.Disks.FirstOrDefault(d =>
            d.DiskNumber == selected.DiskNumber &&
            string.Equals(d.DevicePath, selected.DevicePath, StringComparison.OrdinalIgnoreCase));

        if (current is null)
            blockers.Add("Selected disk is absent from the current physical-device inventory.");
        else if (!WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(selected, current))
            blockers.Add("Selected disk identity or geometry changed since it was selected.");

        if (inventory.Issues.Count != 0)
            blockers.Add("Physical-device enumeration reported diagnostics; the inventory is not fully trustworthy.");

        if (!volumeMap.IsComplete || volumeMap.Issues.Count != 0)
            blockers.Add("Windows volume mapping is incomplete; mounted-volume and boot-device status cannot be trusted.");

        if (!bootMap.IsComplete || bootMap.Issues.Count != 0)
            blockers.Add("Windows boot-device exclusion is incomplete; acquisition is blocked.");

        if (bootMap.BootCriticalDiskNumbers.Contains(selected.DiskNumber))
            blockers.Add("The selected disk contains the running Windows volume or a recognized firmware system partition.");

        if (string.IsNullOrWhiteSpace(selected.SerialNumber))
            blockers.Add("A hardware serial number is unavailable; the source cannot be revalidated.");

        if (inventory.Disks.Count(d =>
                !string.IsNullOrWhiteSpace(d.SerialNumber) &&
                string.Equals(d.SerialNumber.Trim(), selected.SerialNumber?.Trim(), StringComparison.OrdinalIgnoreCase)) > 1)
            blockers.Add("The reported hardware serial number is duplicated across detected devices.");

        if (selected.RemovableMedia != false)
            blockers.Add("The device is removable or its removable-media status is unknown.");

        if (!selected.BusType.HasValue ||
            selected.BusType.Value is BusTypeUnknown or BusTypeRaid or BusTypeVirtual or BusTypeFileBackedVirtual or BusTypeStorageSpaces)
            blockers.Add("The storage bus is unknown, virtual, RAID, or layered; the underlying device identity is not verified.");

        if (volumeMap.VolumePathsByDisk.TryGetValue(selected.DiskNumber, out var volumes) && volumes.Count > 0)
            warnings.Add("The source has mounted Windows volume(s). The application opens the disk read-only, but Windows or another process may still write to those volumes during acquisition. A hardware write blocker is strongly recommended for evidentiary or irreplaceable media.");

        if (blockers.Count == 0)
            warnings.Add("This is a best-effort live acquisition, not a hardware write-blocked forensic image. Do not treat it as a point-in-time snapshot.");

        return new(selected.DiskNumber, blockers.Count == 0, blockers.AsReadOnly(), warnings.AsReadOnly());
    }

    /// <summary>
    /// Performs two independent preflights, then opens the source read-only and copies
    /// it to a new image on a verified separate physical volume. Requires Windows and
    /// usually an elevated process. Existing destination files are never overwritten.
    /// </summary>
    public static async Task<PhysicalDiskAcquisitionResult> AcquireAsync(
        PhysicalDiskDescriptor selected,
        string destinationImagePath,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationImagePath);
        WindowsPhysicalDiskDiscovery.EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();

        // Reject invalid or existing destinations before opening the physical source.
        if (destinationImagePath.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
            destinationImagePath.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase) ||
            destinationImagePath.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase) ||
            destinationImagePath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Image destination must be a regular filesystem path.", nameof(destinationImagePath));

        var fullImagePath = Path.GetFullPath(destinationImagePath);
        var imageDirectory = Path.GetDirectoryName(fullImagePath)
            ?? throw new IOException("Destination directory is invalid.");
        if (!Directory.Exists(imageDirectory))
            throw new DirectoryNotFoundException("Destination directory must already exist.");
        if (File.Exists(fullImagePath) || Directory.Exists(fullImagePath))
            throw new IOException("Destination image already exists; existing files are never overwritten.");

        var manifestPath = fullImagePath + ".diskrescue.json";
        if (File.Exists(manifestPath) || Directory.Exists(manifestPath))
            throw new IOException("Acquisition manifest already exists; existing files are never overwritten.");
        var first = RunPreflight(selected, fullImagePath);
        if (!first.Assessment.CanAcquire)
            throw new InvalidOperationException("Physical acquisition preflight failed: " +
                string.Join(" ", first.Assessment.Blockers));

        // Re-enumerate all safety-critical state immediately before opening the source.
        cancellationToken.ThrowIfCancellationRequested();
        var second = RunPreflight(selected, destinationImagePath);
        if (!second.Assessment.CanAcquire)
            throw new InvalidOperationException("Physical acquisition revalidation failed: " +
                string.Join(" ", second.Assessment.Blockers));

        using var source = WindowsPhysicalDiskReader.Open(second.CurrentDescriptor);
        if (!WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(second.CurrentDescriptor, source.Descriptor))
            throw new IOException("The opened physical device does not match the revalidated source identity.");

        var image = await DiskImager.CreateImageAsync(
            source, destinationImagePath, progress, cancellationToken).ConfigureAwait(false);

        var manifest = new
        {
            format = "DiskRescue.PhysicalAcquisitionManifest.v1",
            acquiredAtUtc = DateTimeOffset.UtcNow,
            source = new
            {
                source.Descriptor.DiskNumber,
                source.Descriptor.DevicePath,
                source.Descriptor.LengthBytes,
                source.Descriptor.BytesPerSector,
                source.Descriptor.Vendor,
                source.Descriptor.Model,
                source.Descriptor.SerialNumber,
                source.Descriptor.BusType,
                source.Descriptor.RemovableMedia
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

        var manifestTemp = manifestPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var stream = new FileStream(
                manifestTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, new JsonSerializerOptions
                {
                    WriteIndented = true
                }, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(manifestTemp, manifestPath, overwrite: false);
        }
        catch
        {
            try { if (File.Exists(manifestTemp)) File.Delete(manifestTemp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }

        return new(image, manifestPath, source.Descriptor, second.Assessment.Warnings);
    }

    private static (PhysicalDiskAcquisitionAssessment Assessment, PhysicalDiskDescriptor CurrentDescriptor)
        RunPreflight(PhysicalDiskDescriptor selected, string destinationPath)
    {
        var inventory = WindowsPhysicalDiskDiscovery.Enumerate();
        var volumeMap = WindowsPhysicalDiskVolumeMap.Enumerate();
        var bootMap = WindowsBootDeviceMap.Enumerate(volumeMap);
        var assessment = Assess(selected, inventory, volumeMap, bootMap);

        if (!assessment.CanAcquire)
            return (assessment, selected);

        var destination = PhysicalImageDestinationSafety.Assess(selected.DiskNumber, destinationPath, volumeMap);
        if (!destination.CanUseAsSeparateDestination)
        {
            var blockers = assessment.Blockers.Concat(destination.Blockers).ToArray();
            assessment = assessment with { CanAcquire = false, Blockers = blockers };
            return (assessment, selected);
        }

        var current = inventory.Disks.Single(d => d.DiskNumber == selected.DiskNumber);
        return (assessment, current);
    }
}
