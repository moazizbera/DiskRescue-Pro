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
    /// <summary>
    /// Physical acquisition remains disabled until image and manifest I/O are bound to a
    /// verified, handle-pinned Windows destination session. Do not enable by changing this
    /// flag alone; the native session implementation and its Windows race tests are required.
    /// </summary>
    public static bool IsAcquisitionEnabled => false;

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
    public static Task<PhysicalDiskAcquisitionResult> AcquireAsync(
        PhysicalDiskDescriptor selected,
        string destinationImagePath,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationImagePath);
        cancellationToken.ThrowIfCancellationRequested();

        // Fail closed at the public API boundary as well as in the desktop UI. The
        // current path-based image/manifest writers cannot prove that the destination
        // directory object remains the same object checked during preflight.
        return Task.FromException<PhysicalDiskAcquisitionResult>(
            new NotSupportedException(
                "Physical disk acquisition is disabled until a verified handle-pinned Windows destination session " +
                "is implemented and its directory-replacement, reparse-point, same-volume, and no-replace publication tests pass."));
    }

}
