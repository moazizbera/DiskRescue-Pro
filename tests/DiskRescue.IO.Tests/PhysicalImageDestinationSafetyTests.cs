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
}
