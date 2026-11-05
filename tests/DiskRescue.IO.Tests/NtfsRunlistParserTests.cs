using DiskRescue.Filesystems;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class NtfsRunlistParserTests
{
    [Fact]
    public void DecodesPhysicalAndSparseRuns()
    {
        // Run 1: length 3, LCN delta +10. Run 2: length 2, sparse.
        byte[] mappingPairs = [0x11, 0x03, 0x0A, 0x01, 0x02, 0x00];
        var result = NtfsRunlistParser.Parse(mappingPairs, 100);
        Assert.True(result.IsValid);
        Assert.Equal((ulong)5, result.TotalClusters);
        Assert.Equal(2, result.Runs.Count);
        Assert.Equal((ulong)0, result.Runs[0].StartingVcn);
        Assert.Equal((ulong)3, result.Runs[0].ClusterCount);
        Assert.Equal((ulong)10, result.Runs[0].StartingLcn);
        Assert.True(result.Runs[1].IsSparse);
        Assert.Equal((ulong)3, result.Runs[1].StartingVcn);
    }

    [Fact]
    public void DecodesNegativeLcnDelta()
    {
        byte[] mappingPairs = [0x11, 0x01, 0x0A, 0x11, 0x01, 0xFE, 0x00];
        var result = NtfsRunlistParser.Parse(mappingPairs, 100);
        Assert.True(result.IsValid);
        Assert.Equal((ulong)10, result.Runs[0].StartingLcn);
        Assert.Equal((ulong)8, result.Runs[1].StartingLcn);
    }

    [Fact]
    public void RejectsTruncatedRun()
    {
        byte[] mappingPairs = [0x21, 0x03, 0x01];
        var result = NtfsRunlistParser.Parse(mappingPairs, 100);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsExtentOutsideVolume()
    {
        byte[] mappingPairs = [0x11, 0x05, 0x0A, 0x00];
        var result = NtfsRunlistParser.Parse(mappingPairs, 12);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("beyond the volume", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RequiresTerminator()
    {
        byte[] mappingPairs = [0x11, 0x01, 0x01];
        var result = NtfsRunlistParser.Parse(mappingPairs, 100);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("terminator", StringComparison.OrdinalIgnoreCase));
    }
}
