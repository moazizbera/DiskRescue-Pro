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

    [Fact]
    public void RejectsNonZeroBytesAfterTerminator()
    {
        byte[] mappingPairs = [0x11, 0x01, 0x01, 0x00, 0x7F];
        var result = NtfsRunlistParser.Parse(mappingPairs, 100);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("after its terminator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsLcnDeltaThatOverflowsSignedAccumulator()
    {
        // First run sets the signed LCN accumulator to Int64.MaxValue.
        byte[] mappingPairs =
        [
            0x81, 0x01,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F,
            0x11, 0x01, 0x01,
            0x00
        ];

        var result = NtfsRunlistParser.Parse(mappingPairs, ulong.MaxValue);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("LCN delta overflows", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsEightByteNegativeLcnDelta()
    {
        byte[] mappingPairs =
        [
            0x81, 0x01,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80,
            0x00
        ];

        var result = NtfsRunlistParser.Parse(mappingPairs, ulong.MaxValue);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("negative LCN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsZeroLengthRunEvenWhenOtherFieldsArePresent()
    {
        byte[] mappingPairs = [0x11, 0x00, 0x01, 0x00];

        var result = NtfsRunlistParser.Parse(mappingPairs, 100);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("zero-length", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AcceptsMaximumWidthPositiveLcnAndLengthFieldsWithinVolume()
    {
        // Header 0x88 means an eight-byte run length and an eight-byte signed LCN delta.
        // Each field below is exactly eight bytes; the final byte is the zero terminator.
        byte[] mappingPairs =
        [
            0x88,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00
        ];

        var result = NtfsRunlistParser.Parse(mappingPairs, ulong.MaxValue);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Single(result.Runs);
        Assert.Equal((ulong)1, result.Runs[0].StartingLcn);
        Assert.Equal((ulong)1, result.TotalClusters);
    }
    [Fact]
    public void AcceptsPhysicalRunEndingExactlyAtVolumeBoundary()
    {
        // LCN 7 plus 3 clusters ends exactly at the 10-cluster volume boundary.
        byte[] mappingPairs = [0x11, 0x03, 0x07, 0x00];

        var result = NtfsRunlistParser.Parse(mappingPairs, 10);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Single(result.Runs);
        Assert.Equal((ulong)7, result.Runs[0].StartingLcn);
        Assert.Equal((ulong)3, result.Runs[0].ClusterCount);
    }

    [Fact]
    public void SparseRunDoesNotChangeLcnDeltaAccumulator()
    {
        // Physical run starts at LCN 10; sparse run has no LCN delta;
        // final physical run applies -1 to the previous physical LCN.
        byte[] mappingPairs = [0x11, 0x01, 0x0A, 0x01, 0x02, 0x11, 0x01, 0xFF, 0x00];

        var result = NtfsRunlistParser.Parse(mappingPairs, 100);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal(3, result.Runs.Count);
        Assert.True(result.Runs[1].IsSparse);
        Assert.Equal((ulong)10, result.Runs[0].StartingLcn);
        Assert.Equal((ulong)9, result.Runs[2].StartingLcn);
        Assert.Equal((ulong)4, result.TotalClusters);
    }

}
