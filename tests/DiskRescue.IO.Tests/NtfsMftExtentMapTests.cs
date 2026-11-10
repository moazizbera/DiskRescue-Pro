using DiskRescue.Filesystems;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class NtfsMftExtentMapTests
{
    [Fact]
    public void AssemblesOutOfOrderExtentsIntoContinuousVcnMap()
    {
        var continuation = DataExtent(2, 3, [0x11, 0x02, 0x02, 0x00]);
        var initial = DataExtent(0, 1, [0x11, 0x02, 0x04, 0x00], sizes: true);

        var result = NtfsMftExtentMap.Assemble([continuation, initial], 128, 512);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal((ulong)4, result.TotalClusters);
        Assert.Equal((ulong)2048, result.MappedBytes);
        Assert.Equal(2, result.Runs.Count);
        Assert.Equal((ulong)0, result.Runs[0].StartingVcn);
        Assert.Equal((ulong)2, result.Runs[1].StartingVcn);
        Assert.Equal((ulong)4, result.Runs[0].StartingLcn);
        Assert.Equal((ulong)2, result.Runs[1].StartingLcn);
    }

    [Fact]
    public void AllowsAttributeIdsToDifferAcrossContinuationRecords()
    {
        var result = NtfsMftExtentMap.Assemble(
            [DataExtent(0, 1, [0x11, 0x02, 0x04, 0x00], sizes: true, attributeId: 3),
             DataExtent(2, 3, [0x11, 0x02, 0x02, 0x00], attributeId: 8)],
            128, 512);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal(2, result.Runs.Count);
    }

    [Fact]
    public void RejectsGapBetweenExtents()
    {
        var result = NtfsMftExtentMap.Assemble(
            [DataExtent(0, 1, [0x11, 0x02, 0x04, 0x00], sizes: true),
             DataExtent(3, 4, [0x11, 0x02, 0x02, 0x00])],
            128, 512);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("gap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsOverlappingExtents()
    {
        var result = NtfsMftExtentMap.Assemble(
            [DataExtent(0, 2, [0x11, 0x03, 0x04, 0x00], sizes: true),
             DataExtent(2, 3, [0x11, 0x02, 0x02, 0x00])],
            128, 512);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("overlap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsHighestVcnThatDoesNotMatchMappingPairs()
    {
        var result = NtfsMftExtentMap.Assemble(
            [DataExtent(0, 2, [0x11, 0x02, 0x04, 0x00], sizes: true)],
            128, 512);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("highest VCN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsSparseMftExtent()
    {
        var result = NtfsMftExtentMap.Assemble(
            [DataExtent(0, 1, [0x01, 0x02, 0x00], sizes: true)],
            128, 512);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("non-sparse", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsExtentOutsideVolumeClusterRange()
    {
        var result = NtfsMftExtentMap.Assemble(
            [DataExtent(0, 1, [0x11, 0x02, 0x7F, 0x00], sizes: true)],
            128, 512);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("volume", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsMissingBaseExtent()
    {
        var result = NtfsMftExtentMap.Assemble(
            [DataExtent(2, 3, [0x11, 0x02, 0x02, 0x00])],
            128, 512);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("VCN 0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsExcessiveExtentCount()
    {
        var extents = Enumerable.Range(0, NtfsMftExtentMap.MaximumExtents + 1)
            .Select(index => DataExtent((ulong)index, (ulong)index,
                [0x11, 0x01, 0x04, 0x00], sizes: index == 0))
            .ToArray();

        var result = NtfsMftExtentMap.Assemble(extents, 128, 512);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("extent count", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsPhysicalClusterOverlapAcrossMftExtents()
    {
        var result = NtfsMftExtentMap.Assemble(
            [DataExtent(0, 1, [0x11, 0x02, 0x04, 0x00], sizes: true),
             DataExtent(2, 3, [0x11, 0x02, 0x05, 0x00])],
            128, 512);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("physical runs overlap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsAggregateMappingPairsInputBeyondSafetyLimit()
    {
        var firstMappingPairs = new byte[NtfsMftExtentMap.MaximumMappingPairsBytes / 2 + 1];
        firstMappingPairs[0] = 0x11;
        firstMappingPairs[1] = 0x01;
        firstMappingPairs[2] = 0x04;
        var secondMappingPairs = new byte[NtfsMftExtentMap.MaximumMappingPairsBytes / 2 + 1];
        secondMappingPairs[0] = 0x11;
        secondMappingPairs[1] = 0x01;
        secondMappingPairs[2] = 0x02;
        var extents = new[]
        {
            DataExtent(0, 0, firstMappingPairs, sizes: true),
            DataExtent(1, 1, secondMappingPairs)
        };

        var result = NtfsMftExtentMap.Assemble(extents, 128, 512);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("aggregate safety limit", StringComparison.OrdinalIgnoreCase));
    }

    private static NtfsAttribute DataExtent(
        ulong lowestVcn,
        ulong highestVcn,
        byte[] mappingPairs,
        bool sizes = false,
        ushort attributeId = 7) =>
        new(
            0x80, 72, true, attributeId, 0, string.Empty,
            null, null, lowestVcn, highestVcn, 64,
            sizes ? 2048UL : 0UL,
            sizes ? 2048UL : 0UL,
            sizes ? 2048UL : 0UL,
            mappingPairs, null);
}
