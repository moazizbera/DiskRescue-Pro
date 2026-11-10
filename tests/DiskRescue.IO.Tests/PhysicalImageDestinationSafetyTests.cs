using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class PhysicalImageDestinationSafetyTests
{
    [Fact]
    public void CapacityCheckReservesSpaceForManifestAndFilesystemMetadata()
    {
        var exactReserve = PhysicalImageDestinationSafety.ValidateFilesystemCapacity(
            "NTFS", 64 * 1024, 0);

        Assert.Empty(exactReserve);

        var insufficient = PhysicalImageDestinationSafety.ValidateFilesystemCapacity(
            "NTFS", 64 * 1024, 1);

        Assert.Contains(insufficient, issue => issue.Contains("insufficient free space", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CapacityCheckRejectsNegativeValuesAndArithmeticOverflow()
    {
        Assert.Contains(
            PhysicalImageDestinationSafety.ValidateFilesystemCapacity("NTFS", -1, 1),
            issue => issue.Contains("cannot be negative", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            PhysicalImageDestinationSafety.ValidateFilesystemCapacity("NTFS", long.MaxValue, long.MaxValue),
            issue => issue.Contains("arithmetic", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("FAT", 2147483648L, true)]
    [InlineData("FAT", 2147483647L, false)]
    [InlineData("FAT32", 4294967296L, true)]
    [InlineData("FAT32", 4294967295L, false)]
    public void CapacityCheckHonorsSingleFileFilesystemLimits(
        string fileSystem, long requiredBytes, bool shouldReject)
    {
        var issues = PhysicalImageDestinationSafety.ValidateFilesystemCapacity(
            fileSystem, long.MaxValue, requiredBytes);

        Assert.Equal(shouldReject, issues.Count != 0);
    }

    [Fact]
    public void DiskMappingRejectsDestinationThatSharesSourceDisk()
    {
        var issues = PhysicalImageDestinationSafety.ValidateDiskMappings(
            2, new[] { 1, 2 }, new[] { 1, 2 });

        Assert.Contains(issues, issue => issue.Contains("shares the source", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiskMappingRejectsIncompleteOrInconsistentMappings()
    {
        Assert.NotEmpty(PhysicalImageDestinationSafety.ValidateDiskMappings(
            0, Array.Empty<int>(), new[] { 1 }));

        Assert.Contains(
            PhysicalImageDestinationSafety.ValidateDiskMappings(0, new[] { 1 }, new[] { 2 }),
            issue => issue.Contains("do not agree", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiskMappingRejectsDuplicateAndInvalidDiskNumbers()
    {
        Assert.NotEmpty(PhysicalImageDestinationSafety.ValidateDiskMappings(
            0, new[] { 1, 1 }, new[] { 1, 1 }));

        Assert.NotEmpty(PhysicalImageDestinationSafety.ValidateDiskMappings(
            0, new[] { -1 }, new[] { -1 }));
    }

    [Fact]
    public void DiskMappingAcceptsCompleteSeparateDestination()
    {
        var issues = PhysicalImageDestinationSafety.ValidateDiskMappings(
            0, new[] { 1, 3 }, new[] { 3, 1 });

        Assert.Empty(issues);
    }

    [Fact]
    public void PinnedVolumeGuidResolvesAgainstCompletePhysicalVolumeMap()
    {
        const string destinationVolume = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";
        const string otherVolume = @"\\?\Volume{22222222-2222-2222-2222-222222222222}\";
        var map = new PhysicalVolumeMappingResult(
            true,
            new Dictionary<int, IReadOnlyList<string>>
            {
                [3] = new[] { destinationVolume },
                [5] = new[] { destinationVolume },
                [7] = new[] { otherVolume }
            },
            Array.Empty<string>());

        var disks = WindowsPhysicalDiskVolumeMap.ResolveDiskNumbersForVolumeGuidPath(
            map, destinationVolume.TrimEnd('\\'));

        Assert.Equal(new[] { 3, 5 }, disks);
    }

    [Fact]
    public void PinnedVolumeGuidResolutionRejectsIncompleteMapAndMalformedGuid()
    {
        var incomplete = new PhysicalVolumeMappingResult(
            false,
            new Dictionary<int, IReadOnlyList<string>>(),
            new[] { "enumeration incomplete" });

        Assert.Throws<InvalidOperationException>(() =>
            WindowsPhysicalDiskVolumeMap.ResolveDiskNumbersForVolumeGuidPath(
                incomplete, @"\\?\Volume{11111111-1111-1111-1111-111111111111}\"));

        var complete = new PhysicalVolumeMappingResult(
            true,
            new Dictionary<int, IReadOnlyList<string>>(),
            Array.Empty<string>());

        Assert.Throws<ArgumentException>(() =>
            WindowsPhysicalDiskVolumeMap.ResolveDiskNumbersForVolumeGuidPath(
                complete, @"\\?\Volume{not-a-guid}\"));
    }

    [Fact]
    public void PinnedDestinationMappingMustMatchPreflightAndExcludeSourceDisk()
    {
        const string destinationVolume = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";
        var map = new PhysicalVolumeMappingResult(
            true,
            new Dictionary<int, IReadOnlyList<string>>
            {
                [0] = new[] { @"\\?\Volume{22222222-2222-2222-2222-222222222222}\" },
                [2] = new[] { destinationVolume }
            },
            Array.Empty<string>());

        Assert.Empty(PhysicalImageDestinationSafety.ValidatePinnedDestinationVolume(
            0, destinationVolume, map, new[] { 2 }));

        Assert.Contains(
            PhysicalImageDestinationSafety.ValidatePinnedDestinationVolume(
                0, destinationVolume, map, new[] { 3 }),
            issue => issue.Contains("do not agree", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            PhysicalImageDestinationSafety.ValidatePinnedDestinationVolume(
                2, destinationVolume, map, new[] { 2 }),
            issue => issue.Contains("shares the source", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PinnedDestinationMappingFailsClosedWhenVolumeMapIsIncomplete()
    {
        var incomplete = new PhysicalVolumeMappingResult(
            false,
            new Dictionary<int, IReadOnlyList<string>>(),
            new[] { "volume enumeration incomplete" });

        Assert.Contains(
            PhysicalImageDestinationSafety.ValidatePinnedDestinationVolume(
                0, @"\\?\Volume{11111111-1111-1111-1111-111111111111}\", incomplete, new[] { 1 }),
            issue => issue.Contains("complete Windows volume map", StringComparison.OrdinalIgnoreCase));
    }

    private static PhysicalDiskDescriptor PhysicalDisk(int number, string? serial = null, uint? busType = 17) =>
        new(number, $@"\\.\PhysicalDrive{number}", 1024 * 1024, 512,
            SerialNumber: serial ?? $"SERIAL-{number}", BusType: busType, RemovableMedia: false);

    [Fact]
    public void PhysicalIdentityCheckAcceptsDistinctIdentifiedNvmeDisks()
    {
        var inventory = new PhysicalDiskDiscoveryResult(
            new[] { PhysicalDisk(0), PhysicalDisk(1) }, Array.Empty<string>());

        var issues = PhysicalImageDestinationSafety.ValidatePhysicalDiskIdentities(
            inventory, new[] { 0 }, new[] { 1 });

        Assert.Empty(issues);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(8u)]
    [InlineData(14u)]
    [InlineData(15u)]
    [InlineData(16u)]
    public void PhysicalIdentityCheckRejectsUnknownOrLayeredStorage(uint busType)
    {
        var inventory = new PhysicalDiskDiscoveryResult(
            new[] { PhysicalDisk(0, busType: busType), PhysicalDisk(1) }, Array.Empty<string>());

        var issues = PhysicalImageDestinationSafety.ValidatePhysicalDiskIdentities(
            inventory, new[] { 0 }, new[] { 1 });

        Assert.Contains(issues, issue => issue.Contains("unknown, RAID, virtual", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PhysicalIdentityCheckRejectsVirtualDeviceDescriptionEvenOnScsiBus()
    {
        var virtualDisk = new PhysicalDiskDescriptor(
            0, @"\\.\PhysicalDrive0", 1024 * 1024, 512,
            Vendor: "Microsoft", Model: "Msft Virtual Disk",
            SerialNumber: "VIRTUAL-0", BusType: 1, RemovableMedia: false);
        var inventory = new PhysicalDiskDiscoveryResult(
            new[] { virtualDisk, PhysicalDisk(1) }, Array.Empty<string>());

        var issues = PhysicalImageDestinationSafety.ValidatePhysicalDiskIdentities(
            inventory, new[] { 0 }, new[] { 1 });

        Assert.Contains(issues, issue => issue.Contains("virtualized or layered", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PhysicalIdentityCheckRejectsMissingSerialAndIncompleteInventory()
    {
        var missingSerial = new PhysicalDiskDiscoveryResult(
            new[] { PhysicalDisk(0, serial: ""), PhysicalDisk(1) }, Array.Empty<string>());
        Assert.Contains(
            PhysicalImageDestinationSafety.ValidatePhysicalDiskIdentities(
                missingSerial, new[] { 0 }, new[] { 1 }),
            issue => issue.Contains("no reported serial", StringComparison.OrdinalIgnoreCase));

        var incomplete = new PhysicalDiskDiscoveryResult(
            new[] { PhysicalDisk(0), PhysicalDisk(1) }, new[] { "device enumeration incomplete" });
        Assert.Contains(
            PhysicalImageDestinationSafety.ValidatePhysicalDiskIdentities(
                incomplete, new[] { 0 }, new[] { 1 }),
            issue => issue.Contains("enumeration reported diagnostics", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PhysicalIdentityCheckRejectsDuplicateSerialAcrossDifferentDisks()
    {
        var inventory = new PhysicalDiskDiscoveryResult(
            new[] { PhysicalDisk(0, "DUPLICATE"), PhysicalDisk(1, "DUPLICATE") },
            Array.Empty<string>());

        var issues = PhysicalImageDestinationSafety.ValidatePhysicalDiskIdentities(
            inventory, new[] { 0 }, new[] { 1 });

        Assert.Contains(issues, issue => issue.Contains("serial identity is duplicated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PhysicalIdentityCheckRejectsMissingDiskDescriptors()
    {
        var inventory = new PhysicalDiskDiscoveryResult(
            new[] { PhysicalDisk(0) }, Array.Empty<string>());

        var issues = PhysicalImageDestinationSafety.ValidatePhysicalDiskIdentities(
            inventory, new[] { 0 }, new[] { 1 });

        Assert.Contains(issues, issue => issue.Contains("does not have exactly one", StringComparison.OrdinalIgnoreCase));
    }

}
