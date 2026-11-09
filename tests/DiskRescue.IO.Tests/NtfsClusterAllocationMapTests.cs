using DiskRescue.Filesystems;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class NtfsClusterAllocationMapTests
{
    [Fact]
    public void ReportsAllFreeClustersWithoutClaimingContentIntegrity()
    {
        var created = NtfsClusterAllocationMap.Create(new byte[2], 16);
        Assert.True(created.IsValid);
        var result = created.Map!.Assess(new[] { new NtfsDataRun(0, 4, 2) });

        Assert.Equal(NtfsClusterAllocationStatus.Free, result.Status);
        Assert.Equal((ulong)4, result.FreeClusters);
        Assert.Equal((ulong)0, result.AllocatedClusters);
        Assert.Contains(result.Warnings, warning => warning.Contains("cannot prove", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReportsAllocatedClustersAsPotentiallyReused()
    {
        var bitmap = new byte[] { 0b0000_1100 };
        var created = NtfsClusterAllocationMap.Create(bitmap, 8);
        var result = created.Map!.Assess(new[] { new NtfsDataRun(0, 2, 2) });

        Assert.Equal(NtfsClusterAllocationStatus.Allocated, result.Status);
        Assert.Equal((ulong)2, result.AllocatedClusters);
        Assert.Contains(result.Warnings, warning => warning.Contains("may have been reused", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReportsMixedAllocationAcrossFragmentedRuns()
    {
        var bitmap = new byte[] { 0b0000_0010 };
        var created = NtfsClusterAllocationMap.Create(bitmap, 8);
        var result = created.Map!.Assess(new[]
        {
            new NtfsDataRun(0, 1, 0),
            new NtfsDataRun(1, 1, 1)
        });

        Assert.Equal(NtfsClusterAllocationStatus.Mixed, result.Status);
        Assert.Equal((ulong)1, result.FreeClusters);
        Assert.Equal((ulong)1, result.AllocatedClusters);
    }

    [Fact]
    public void RejectsBitmapTooShortForVolume()
    {
        var result = NtfsClusterAllocationMap.Create(new byte[1], 9);

        Assert.False(result.IsValid);
        Assert.Null(result.Map);
    }

    [Fact]
    public void RejectsRunOutsideVolumeBounds()
    {
        var created = NtfsClusterAllocationMap.Create(new byte[1], 8);
        var result = created.Map!.Assess(new[] { new NtfsDataRun(0, 2, 7) });

        Assert.Equal(NtfsClusterAllocationStatus.Unknown, result.Status);
    }

    [Fact]
    public void RefusesUnboundedClusterAssessment()
    {
        var created = NtfsClusterAllocationMap.Create(new byte[2], 16);
        var result = created.Map!.Assess(new[] { new NtfsDataRun(0, NtfsClusterAllocationMap.MaximumAssessmentClusters + 1, 0) });

        Assert.Equal(NtfsClusterAllocationStatus.Unknown, result.Status);
    }

    [Fact]
    public void RejectsNonContiguousVirtualClusterMap()
    {
        var created = NtfsClusterAllocationMap.Create(new byte[2], 16);
        var result = created.Map!.Assess(new[]
        {
            new NtfsDataRun(0, 1, 0),
            new NtfsDataRun(2, 1, 2)
        });

        Assert.Equal(NtfsClusterAllocationStatus.Unknown, result.Status);
        Assert.Contains(result.Warnings, warning => warning.Contains("contiguous", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsOverlappingPhysicalClusterRanges()
    {
        var created = NtfsClusterAllocationMap.Create(new byte[2], 16);
        var result = created.Map!.Assess(new[]
        {
            new NtfsDataRun(0, 2, 4),
            new NtfsDataRun(2, 2, 5)
        });

        Assert.Equal(NtfsClusterAllocationStatus.Unknown, result.Status);
        Assert.Contains(result.Warnings, warning => warning.Contains("overlapping", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SparseRunsRemainUnknown()
    {
        var created = NtfsClusterAllocationMap.Create(new byte[1], 8);
        var result = created.Map!.Assess(new[] { new NtfsDataRun(0, 1, null) });

        Assert.Equal(NtfsClusterAllocationStatus.Unknown, result.Status);
    }
}
