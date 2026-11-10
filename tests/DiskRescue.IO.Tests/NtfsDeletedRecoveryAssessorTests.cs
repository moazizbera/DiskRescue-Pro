using DiskRescue.Filesystems;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class NtfsDeletedRecoveryAssessorTests
{
    private static NtfsDeletedFileAssessment VerifiedMetadata() =>
        new(NtfsDeletedMetadataStatus.Verified, NtfsDeletedContentStatus.NotAssessed,
            42, 7, "Archive.zip", 5, 3,
            new[] { "File content has not been assessed." });

    [Fact]
    public void FreeClustersOnlyQualifyForFurtherValidationNotRecovery()
    {
        var map = NtfsClusterAllocationMap.Create(new byte[1], 8).Map!;
        var result = NtfsDeletedRecoveryAssessor.Assess(VerifiedMetadata(), map,
            new[] { new NtfsDataRun(0, 2, 1) });

        Assert.True(result.IsCandidateForFurtherValidation);
        Assert.False(result.ContentIntegrityVerified);
        Assert.Equal(NtfsClusterAllocationStatus.Free, result.ClusterAllocation.Status);
        Assert.Contains(result.Warnings, warning => warning.Contains("overwrite history", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AllocatedClustersDoNotQualifyForFurtherValidation()
    {
        var map = NtfsClusterAllocationMap.Create(new byte[] { 0b0000_0010 }, 8).Map!;
        var result = NtfsDeletedRecoveryAssessor.Assess(VerifiedMetadata(), map,
            new[] { new NtfsDataRun(0, 1, 1) });

        Assert.False(result.IsCandidateForFurtherValidation);
        Assert.False(result.ContentIntegrityVerified);
        Assert.Equal(NtfsClusterAllocationStatus.Allocated, result.ClusterAllocation.Status);
    }

    [Fact]
    public void UnverifiedMetadataPreventsClusterAssessment()
    {
        var metadata = VerifiedMetadata() with { MetadataStatus = NtfsDeletedMetadataStatus.StaleParentReference };
        var map = NtfsClusterAllocationMap.Create(new byte[1], 8).Map!;
        var result = NtfsDeletedRecoveryAssessor.Assess(metadata, map,
            new[] { new NtfsDataRun(0, 1, 1) });

        Assert.False(result.IsCandidateForFurtherValidation);
        Assert.Equal(NtfsClusterAllocationStatus.Unknown, result.ClusterAllocation.Status);
    }
}
