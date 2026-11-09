using DiskRescue.Filesystems;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class NtfsDeletedDataStreamAssessorTests
{
    private static NtfsMftEntry Entry() =>
        new(42, 7, 5, 3, "Archive.zip", 1, false, 1024, 1024, 0x20, true);

    private static NtfsFileRecord Record(uint number = 42, ushort sequence = 7,
        ushort flags = 0, NtfsFileReference? baseReference = null) =>
        new(sequence, 1, flags, 128, 1024, 56, number,
            baseReference ?? new(0, 0), new byte[1024]);

    private static NtfsFileName Name() =>
        new(5, 3, 0, 0, 0, 0, 1024, 1024, 0x20, 0, 1, "Archive.zip");

    private static NtfsFileRecord Parent() => Record(5, 3, flags: 0x0003);

    private static NtfsAttribute NonResidentData(byte[]? mappingPairs = null,
        ulong highestVcn = 1, ulong allocatedSize = 1024, ulong realSize = 1024,
        ulong initializedSize = 1024) =>
        new(0x80, 80, true, 1, 0, "", null, null,
            0, highestVcn, 64, allocatedSize, realSize, initializedSize,
            mappingPairs ?? new byte[] { 0x11, 0x02, 0x01, 0x00 }, null);

    private static NtfsAttribute ResidentData() =>
        new(0x80, 32, false, 1, 0, "", 4, 24,
            null, null, null, null, null, null, null, new byte[] { 1, 2, 3, 4 });

    [Fact]
    public void AllowsResidentDeletedStreamOnlyAfterMetadataIdentityChecks()
    {
        var result = NtfsDeletedDataStreamAssessor.Assess(
            Entry(), Record(), Name(), Parent(), new[] { ResidentData() },
            8, 512, allocationMap: null);

        Assert.Equal(NtfsDeletedDataStreamStatus.ReadyForBoundedRead, result.Status);
        Assert.True(result.IsEligibleForBoundedRead);
        Assert.True(result.IsResident);
        Assert.Equal((ulong)4, result.Length);
        Assert.Empty(result.Runs);
    }

    [Fact]
    public void AllowsNonResidentStreamOnlyWhenEveryReferencedClusterIsFree()
    {
        var map = NtfsClusterAllocationMap.Create(new byte[1], 8).Map!;
        var result = NtfsDeletedDataStreamAssessor.Assess(
            Entry(), Record(), Name(), Parent(), new[] { NonResidentData() },
            8, 512, map);

        Assert.Equal(NtfsDeletedDataStreamStatus.ReadyForBoundedRead, result.Status);
        Assert.True(result.IsEligibleForBoundedRead);
        Assert.False(result.IsResident);
        Assert.Equal((ulong)1024, result.Length);
        Assert.Equal(NtfsClusterAllocationStatus.Free, result.ClusterAllocation!.Status);
    }

    [Fact]
    public void BlocksNonResidentStreamWhenAnyClusterIsAllocated()
    {
        var map = NtfsClusterAllocationMap.Create(new byte[] { 0b0000_0010 }, 8).Map!;
        var result = NtfsDeletedDataStreamAssessor.Assess(
            Entry(), Record(), Name(), Parent(), new[] { NonResidentData() },
            8, 512, map);

        Assert.Equal(NtfsDeletedDataStreamStatus.ClustersAllocated, result.Status);
        Assert.False(result.IsEligibleForBoundedRead);
        Assert.Equal(NtfsClusterAllocationStatus.Mixed, result.ClusterAllocation!.Status);
    }

    [Fact]
    public void BlocksNonResidentStreamWhenAllocationEvidenceIsMissing()
    {
        var result = NtfsDeletedDataStreamAssessor.Assess(
            Entry(), Record(), Name(), Parent(), new[] { NonResidentData() },
            8, 512, allocationMap: null);

        Assert.Equal(NtfsDeletedDataStreamStatus.AllocationUnknown, result.Status);
        Assert.False(result.IsEligibleForBoundedRead);
    }

    [Fact]
    public void RejectsStreamWhoseDeclaredSizeExceedsItsRunlist()
    {
        var map = NtfsClusterAllocationMap.Create(new byte[1], 8).Map!;
        var result = NtfsDeletedDataStreamAssessor.Assess(
            Entry(), Record(), Name(), Parent(),
            new[] { NonResidentData(allocatedSize: 4096, realSize: 2048, initializedSize: 2048) },
            8, 512, map);

        Assert.Equal(NtfsDeletedDataStreamStatus.InvalidRunlist, result.Status);
        Assert.False(result.IsEligibleForBoundedRead);
    }

    [Fact]
    public void RejectsFilesRequiringAttributeListContinuation()
    {
        var map = NtfsClusterAllocationMap.Create(new byte[1], 8).Map!;
        var attributeList = new NtfsAttribute(
            0x20, 32, false, 2, 0, "", 0, 24,
            null, null, null, null, null, null, null, Array.Empty<byte>());
        var result = NtfsDeletedDataStreamAssessor.Assess(
            Entry(), Record(), Name(), Parent(),
            new[] { NonResidentData(), attributeList },
            8, 512, map);

        Assert.Equal(NtfsDeletedDataStreamStatus.UnsupportedStreamLayout, result.Status);
        Assert.False(result.IsEligibleForBoundedRead);
    }

    [Fact]
    public void RejectsAmbiguousMultipleDataAttributes()
    {
        var result = NtfsDeletedDataStreamAssessor.Assess(
            Entry(), Record(), Name(), Parent(),
            new[] { ResidentData(), ResidentData() },
            8, 512, allocationMap: null);

        Assert.Equal(NtfsDeletedDataStreamStatus.AmbiguousStream, result.Status);
        Assert.False(result.IsEligibleForBoundedRead);
    }

    [Fact]
    public void RejectsSparseDeletedDataRuns()
    {
        var map = NtfsClusterAllocationMap.Create(new byte[1], 8).Map!;
        var result = NtfsDeletedDataStreamAssessor.Assess(
            Entry(), Record(), Name(), Parent(),
            new[] { NonResidentData(mappingPairs: new byte[] { 0x01, 0x02, 0x00 }) },
            8, 512, map);

        Assert.Equal(NtfsDeletedDataStreamStatus.InvalidRunlist, result.Status);
        Assert.False(result.IsEligibleForBoundedRead);
    }

    [Fact]
    public void RejectsMalformedMappingPairsWithoutTerminator()
    {
        var map = NtfsClusterAllocationMap.Create(new byte[1], 8).Map!;
        var result = NtfsDeletedDataStreamAssessor.Assess(
            Entry(), Record(), Name(), Parent(),
            new[] { NonResidentData(mappingPairs: new byte[] { 0x11, 0x02, 0x01 }) },
            8, 512, map);

        Assert.Equal(NtfsDeletedDataStreamStatus.InvalidRunlist, result.Status);
        Assert.False(result.IsEligibleForBoundedRead);
    }

    [Fact]
    public void RejectsDeletedRecordThatHasBecomeActive()
    {
        var result = NtfsDeletedDataStreamAssessor.Assess(
            Entry(), Record(flags: 0x0001), Name(), Parent(),
            new[] { ResidentData() }, 8, 512, allocationMap: null);

        Assert.Equal(NtfsDeletedDataStreamStatus.MetadataRejected, result.Status);
        Assert.False(result.IsEligibleForBoundedRead);
    }

    [Fact]
    public void ReportsMissingRequestedDataStream()
    {
        var result = NtfsDeletedDataStreamAssessor.Assess(
            Entry(), Record(), Name(), Parent(),
            Array.Empty<NtfsAttribute>(), 8, 512, allocationMap: null);

        Assert.Equal(NtfsDeletedDataStreamStatus.StreamMissing, result.Status);
        Assert.False(result.IsEligibleForBoundedRead);
    }

    [Fact]
    public void RejectsUnverifiedParentBeforeConsideringStream()
    {
        var result = NtfsDeletedDataStreamAssessor.Assess(
            Entry(), Record(), Name(), parentRecord: null, new[] { ResidentData() },
            8, 512, allocationMap: null);

        Assert.Equal(NtfsDeletedDataStreamStatus.MetadataRejected, result.Status);
        Assert.False(result.IsEligibleForBoundedRead);
    }

    private static NtfsAttribute NonResidentExtent(ulong lowestVcn, ulong highestVcn,
        byte[] mappingPairs, ulong allocatedSize = 2048, ulong realSize = 2048,
        ulong initializedSize = 2048) =>
        new(0x80, 80, true, 1, 0, "", null, null,
            lowestVcn, highestVcn, 64, allocatedSize, realSize, initializedSize,
            mappingPairs, null);

    [Fact]
    public void AllowsValidatedFragmentedMultiExtentStreamWhenEveryClusterIsFree()
    {
        var map = NtfsClusterAllocationMap.Create(new byte[1], 8).Map!;
        var result = NtfsDeletedDataStreamAssessor.AssessValidatedAttributeList(
            Entry(), Record(), Name(), Parent(),
            new[]
            {
                NonResidentExtent(0, 1, new byte[] { 0x11, 0x02, 0x01, 0x00 }),
                NonResidentExtent(2, 3, new byte[] { 0x11, 0x02, 0x05, 0x00 })
            },
            8, 512, map, "");

        Assert.Equal(NtfsDeletedDataStreamStatus.ReadyForBoundedRead, result.Status);
        Assert.True(result.IsEligibleForBoundedRead);
        Assert.Equal((ulong)2048, result.Length);
        Assert.Equal((ulong)2048, result.InitializedSize);
        Assert.Equal(2, result.Runs.Count);
        Assert.Equal((ulong)1, result.Runs[0].StartingLcn);
        Assert.Equal((ulong)5, result.Runs[1].StartingLcn);
        Assert.Equal(NtfsClusterAllocationStatus.Free, result.ClusterAllocation!.Status);
    }

    [Fact]
    public void RejectsValidatedMultiExtentStreamWithVcnGap()
    {
        var map = NtfsClusterAllocationMap.Create(new byte[1], 8).Map!;
        var result = NtfsDeletedDataStreamAssessor.AssessValidatedAttributeList(
            Entry(), Record(), Name(), Parent(),
            new[]
            {
                NonResidentExtent(0, 1, new byte[] { 0x11, 0x02, 0x01, 0x00 }),
                NonResidentExtent(3, 4, new byte[] { 0x11, 0x02, 0x05, 0x00 })
            },
            8, 512, map, "");

        Assert.Equal(NtfsDeletedDataStreamStatus.InvalidRunlist, result.Status);
        Assert.False(result.IsEligibleForBoundedRead);
    }

    [Fact]
    public void RejectsValidatedMultiExtentStreamWithPhysicalOverlap()
    {
        var map = NtfsClusterAllocationMap.Create(new byte[1], 8).Map!;
        var result = NtfsDeletedDataStreamAssessor.AssessValidatedAttributeList(
            Entry(), Record(), Name(), Parent(),
            new[]
            {
                NonResidentExtent(0, 1, new byte[] { 0x11, 0x02, 0x01, 0x00 }),
                NonResidentExtent(2, 3, new byte[] { 0x11, 0x02, 0x01, 0x00 })
            },
            8, 512, map, "");

        Assert.Equal(NtfsDeletedDataStreamStatus.InvalidRunlist, result.Status);
        Assert.False(result.IsEligibleForBoundedRead);
    }

    [Fact]
    public void BlocksValidatedMultiExtentStreamWhenContinuationClusterIsAllocated()
    {
        var map = NtfsClusterAllocationMap.Create(new byte[] { 0b0010_0000 }, 8).Map!;
        var result = NtfsDeletedDataStreamAssessor.AssessValidatedAttributeList(
            Entry(), Record(), Name(), Parent(),
            new[]
            {
                NonResidentExtent(0, 1, new byte[] { 0x11, 0x02, 0x01, 0x00 }),
                NonResidentExtent(2, 3, new byte[] { 0x11, 0x02, 0x05, 0x00 })
            },
            8, 512, map, "");

        Assert.Equal(NtfsDeletedDataStreamStatus.ClustersAllocated, result.Status);
        Assert.False(result.IsEligibleForBoundedRead);
        Assert.Equal(NtfsClusterAllocationStatus.Mixed, result.ClusterAllocation!.Status);
    }

}
