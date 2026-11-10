using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class PhysicalDiskImageAcquisitionTests
{
    private static PhysicalDiskDescriptor Descriptor() =>
        new(2, @"\\.\PhysicalDrive2", 1024 * 1024, 512,
            "Example", "External Disk", "SERIAL-002", 3, false);

    private static PhysicalDiskDiscoveryResult Inventory(PhysicalDiskDescriptor? descriptor = null) =>
        new(new[] { descriptor ?? Descriptor() }, Array.Empty<string>());

    [Fact]
    public async Task PublicAcquisitionEntryPointFailsClosedUntilPinnedDestinationSessionExists()
    {
        Assert.False(PhysicalDiskImageAcquisition.IsAcquisitionEnabled);

        var destination = Path.Combine(
            Path.GetTempPath(),
            $"diskrescue-gated-{Guid.NewGuid():N}.img");

        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            PhysicalDiskImageAcquisition.AcquireAsync(Descriptor(), destination));

        Assert.Contains("handle-pinned Windows destination session", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".diskrescue.json"));
    }

    [Fact]
    public void PhysicalDiskReadAndCoordinatorTypesAreNotExposedAsPublicApi()
    {
        var exportedTypes = typeof(PhysicalDiskImageAcquisition).Assembly.GetExportedTypes();

        Assert.DoesNotContain(typeof(WindowsPhysicalDiskReader), exportedTypes);
        Assert.DoesNotContain(typeof(PhysicalDiskOpenedSource), exportedTypes);
        Assert.DoesNotContain(typeof(IPhysicalDiskAcquisitionEnvironment), exportedTypes);
        Assert.DoesNotContain(typeof(WindowsPhysicalDiskAcquisitionEnvironment), exportedTypes);
        Assert.DoesNotContain(typeof(PhysicalDiskAcquisitionCoordinator), exportedTypes);
    }

    [Fact]
    public void AssessmentAllowsVerifiedNonBootSourceWithSeparateVolumeMap()
    {
        var assessment = PhysicalDiskImageAcquisition.Assess(
            Descriptor(),
            Inventory(),
            new PhysicalVolumeMappingResult(true,
                new Dictionary<int, IReadOnlyList<string>>(), Array.Empty<string>()),
            new WindowsBootDeviceMappingResult(true, new HashSet<int>(), Array.Empty<string>()));

        Assert.True(assessment.CanAcquire);
        Assert.Empty(assessment.Blockers);
        Assert.Contains(assessment.Warnings, warning => warning.Contains("point-in-time", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssessmentBlocksBootCriticalDisk()
    {
        var assessment = PhysicalDiskImageAcquisition.Assess(
            Descriptor(),
            Inventory(),
            new PhysicalVolumeMappingResult(true,
                new Dictionary<int, IReadOnlyList<string>>(), Array.Empty<string>()),
            new WindowsBootDeviceMappingResult(true, new HashSet<int> { 2 }, Array.Empty<string>()));

        Assert.False(assessment.CanAcquire);
        Assert.Contains(assessment.Blockers, blocker => blocker.Contains("Windows volume", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssessmentBlocksIncompleteVolumeOrBootMaps()
    {
        var assessment = PhysicalDiskImageAcquisition.Assess(
            Descriptor(),
            Inventory(),
            new PhysicalVolumeMappingResult(false,
                new Dictionary<int, IReadOnlyList<string>>(), new[] { "volume query failed" }),
            new WindowsBootDeviceMappingResult(false, new HashSet<int>(), new[] { "boot map failed" }));

        Assert.False(assessment.CanAcquire);
        Assert.Contains(assessment.Blockers, blocker => blocker.Contains("volume mapping is incomplete", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(assessment.Blockers, blocker => blocker.Contains("boot-device exclusion is incomplete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssessmentWarnsWhenSourceHasMountedVolumesButDoesNotClaimSnapshotConsistency()
    {
        var assessment = PhysicalDiskImageAcquisition.Assess(
            Descriptor(),
            Inventory(),
            new PhysicalVolumeMappingResult(true,
                new Dictionary<int, IReadOnlyList<string>>
                {
                    [2] = new[] { @"\\?\Volume{source-volume}\" }
                }, Array.Empty<string>()),
            new WindowsBootDeviceMappingResult(true, new HashSet<int>(), Array.Empty<string>()));

        Assert.True(assessment.CanAcquire);
        Assert.Contains(assessment.Warnings, warning => warning.Contains("hardware write blocker", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(assessment.Warnings, warning => warning.Contains("point-in-time", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssessmentBlocksMissingOrAmbiguousHardwareIdentity()
    {
        var descriptor = Descriptor() with { SerialNumber = null };
        var assessment = PhysicalDiskImageAcquisition.Assess(
            descriptor,
            Inventory(descriptor),
            new PhysicalVolumeMappingResult(true,
                new Dictionary<int, IReadOnlyList<string>>(), Array.Empty<string>()),
            new WindowsBootDeviceMappingResult(true, new HashSet<int>(), Array.Empty<string>()));

        Assert.False(assessment.CanAcquire);
        Assert.Contains(assessment.Blockers, blocker => blocker.Contains("serial number is unavailable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssessmentBlocksDuplicateHardwareSerialNumbers()
    {
        var duplicate = Descriptor() with { DiskNumber = 3, DevicePath = @"\\.\PhysicalDrive3" };
        var inventory = new PhysicalDiskDiscoveryResult(
            new[] { Descriptor(), duplicate }, Array.Empty<string>());

        var assessment = PhysicalDiskImageAcquisition.Assess(
            Descriptor(),
            inventory,
            new PhysicalVolumeMappingResult(true,
                new Dictionary<int, IReadOnlyList<string>>(), Array.Empty<string>()),
            new WindowsBootDeviceMappingResult(true, new HashSet<int>(), Array.Empty<string>()));

        Assert.False(assessment.CanAcquire);
        Assert.Contains(assessment.Blockers, blocker => blocker.Contains("serial number is duplicated", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(true, 3u)]
    [InlineData(false, 0u)]
    [InlineData(false, 8u)]
    [InlineData(false, 14u)]
    [InlineData(false, 15u)]
    [InlineData(false, 16u)]
    public void AssessmentBlocksRemovableOrUnsupportedStorageTypes(bool removable, uint busType)
    {
        var descriptor = Descriptor() with { RemovableMedia = removable, BusType = busType };
        var assessment = PhysicalDiskImageAcquisition.Assess(
            descriptor,
            Inventory(descriptor),
            new PhysicalVolumeMappingResult(true,
                new Dictionary<int, IReadOnlyList<string>>(), Array.Empty<string>()),
            new WindowsBootDeviceMappingResult(true, new HashSet<int>(), Array.Empty<string>()));

        Assert.False(assessment.CanAcquire);
        Assert.Contains(assessment.Blockers, blocker =>
            blocker.Contains("removable", StringComparison.OrdinalIgnoreCase) ||
            blocker.Contains("storage bus", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssessmentBlocksAnyPhysicalInventoryDiagnostic()
    {
        var inventory = new PhysicalDiskDiscoveryResult(
            new[] { Descriptor() }, new[] { "PhysicalDrive7: access denied." });

        var assessment = PhysicalDiskImageAcquisition.Assess(
            Descriptor(),
            inventory,
            new PhysicalVolumeMappingResult(true,
                new Dictionary<int, IReadOnlyList<string>>(), Array.Empty<string>()),
            new WindowsBootDeviceMappingResult(true, new HashSet<int>(), Array.Empty<string>()));

        Assert.False(assessment.CanAcquire);
        Assert.Contains(assessment.Blockers, blocker => blocker.Contains("inventory is not fully trustworthy", StringComparison.OrdinalIgnoreCase));
    }

}
