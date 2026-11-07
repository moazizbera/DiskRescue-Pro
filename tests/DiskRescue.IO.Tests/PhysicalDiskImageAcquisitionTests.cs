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
}
