namespace DiskRescue.IO;

/// <summary>
/// Conservative preflight result for a future physical-disk scan session.
/// This assessment is advisory and deliberately does not open a device or start a scan.
/// </summary>
public sealed record PhysicalDiskSafetyAssessment(
    int DiskNumber,
    bool CanStartSectorScan,
    IReadOnlyList<string> Blockers);

public static class PhysicalDiskSafety
{
    // STORAGE_BUS_TYPE values that identify layered, virtual, or ambiguous devices.
    private const uint BusTypeUnknown = 0;
    private const uint BusTypeRaid = 8;
    private const uint BusTypeVirtual = 14;
    private const uint BusTypeFileBackedVirtual = 15;
    private const uint BusTypeStorageSpaces = 16;

    public static PhysicalDiskSafetyAssessment Assess(
        PhysicalDiskDescriptor selected,
        PhysicalDiskDiscoveryResult inventory,
        PhysicalVolumeMappingResult volumeMap) =>
        Assess(selected, inventory, volumeMap,
            new WindowsBootDeviceMappingResult(false, new HashSet<int>(),
                new[] { "Boot-device exclusion has not been independently verified." }));

    public static PhysicalDiskSafetyAssessment Assess(
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
        var current = inventory.Disks.FirstOrDefault(disk =>
            disk.DiskNumber == selected.DiskNumber &&
            string.Equals(disk.DevicePath, selected.DevicePath, StringComparison.OrdinalIgnoreCase));

        if (current is null)
            blockers.Add("Selected disk is not present in the latest physical-device inventory.");
        else if (current.LengthBytes != selected.LengthBytes ||
                 current.BytesPerSector != selected.BytesPerSector ||
                 !WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(selected, current))
            blockers.Add("Selected disk identity or geometry does not match the latest inventory snapshot.");

        if (inventory.Issues.Count > 0)
            blockers.Add("Physical-device enumeration reported issues; device inventory is not fully trustworthy.");

        if (!volumeMap.IsComplete || volumeMap.Issues.Count > 0)
            blockers.Add("Windows volume enumeration is incomplete or reported diagnostics; mounted-volume status is unknown.");
        else if (volumeMap.VolumePathsByDisk.ContainsKey(selected.DiskNumber))
            blockers.Add("The disk is associated with an enumerated Windows volume; raw scanning could interfere with mounted filesystems.");

        if (string.IsNullOrWhiteSpace(selected.SerialNumber))
            blockers.Add("A hardware serial number is unavailable; the physical device cannot be revalidated safely.");

        if (inventory.Disks.Count(disk =>
                !string.IsNullOrWhiteSpace(disk.SerialNumber) &&
                string.Equals(disk.SerialNumber.Trim(), selected.SerialNumber?.Trim(), StringComparison.OrdinalIgnoreCase)) > 1)
            blockers.Add("The reported serial number is duplicated across detected devices.");

        if (selected.RemovableMedia == true)
            blockers.Add("The device reports removable media; this workflow has not validated removable-device handling.");

        if (!selected.BusType.HasValue ||
            selected.BusType.Value is BusTypeUnknown or BusTypeRaid or BusTypeVirtual or BusTypeFileBackedVirtual or BusTypeStorageSpaces)
            blockers.Add("The storage bus type is unknown or layered/virtual; underlying-device identity is not verified.");

        if (!bootMap.IsComplete || bootMap.Issues.Count != 0)
            blockers.Add("System and firmware boot-device exclusion could not be verified completely.");
        else if (bootMap.BootCriticalDiskNumbers.Contains(selected.DiskNumber))
            blockers.Add("The selected disk contains the running Windows volume or a firmware system partition.");

        // A physical sector scan is not authorized by device classification alone.
        blockers.Add("A verified sector image on separate storage is required before any physical scan.");

        return new(selected.DiskNumber, blockers.Count == 0, blockers.AsReadOnly());
    }
}
