namespace DiskRescue.Filesystems;
public enum NtfsDeletedDataStreamStatus { ReadyForBoundedRead, MetadataRejected, StreamMissing, AmbiguousStream, UnsupportedStreamLayout, InvalidRunlist, AllocationUnknown, ClustersAllocated }
public sealed record NtfsDeletedDataStreamAssessment(NtfsDeletedDataStreamStatus Status, NtfsDeletedFileAssessment Metadata, bool IsResident, ulong Length, IReadOnlyList<NtfsDataRun> Runs, NtfsClusterAllocationAssessment? ClusterAllocation, IReadOnlyList<string> Warnings)
{
    public ulong InitializedSize { get; init; }
    public bool IsEligibleForBoundedRead => Status == NtfsDeletedDataStreamStatus.ReadyForBoundedRead;
}
/// <summary>Read-free deleted stream preflight. Multiple non-resident extents require a complete deleted-specific attribute-list match.</summary>
public static class NtfsDeletedDataStreamAssessor
{
    private const uint DataAttributeType = 0x80;
    private const int MaximumRuns = 1_000_000;
    public static NtfsDeletedDataStreamAssessment Assess(NtfsMftEntry entry, NtfsFileRecord record, NtfsFileName fileName, NtfsFileRecord? parentRecord, IReadOnlyList<NtfsAttribute> attributes, ulong volumeClusterCount, uint clusterSizeBytes, NtfsClusterAllocationMap? allocationMap, string streamName = "", bool attributeListValidated = false)
    {
        ArgumentNullException.ThrowIfNull(entry); ArgumentNullException.ThrowIfNull(record); ArgumentNullException.ThrowIfNull(fileName); ArgumentNullException.ThrowIfNull(attributes); ArgumentNullException.ThrowIfNull(streamName);
        var metadata = NtfsDeletedFileAssessor.Assess(entry, record, fileName, parentRecord);
        if (!metadata.MetadataVerified || (record.Flags & 0x0002) != 0)
            return Result(NtfsDeletedDataStreamStatus.MetadataRejected, metadata, warnings: new[] { "Deleted metadata is not verified as an independent file; no data stream may be read." });
        if (attributes.Any(a => a.Type == 0x20))
            return Result(NtfsDeletedDataStreamStatus.UnsupportedStreamLayout, metadata, warnings: new[] { "A raw $ATTRIBUTE_LIST was supplied without deleted-specific continuation validation." });
        var data = attributes.Where(a => a.Type == DataAttributeType && string.Equals(a.Name, streamName, StringComparison.Ordinal)).ToArray();
        if (data.Length == 0) return Result(NtfsDeletedDataStreamStatus.StreamMissing, metadata, warnings: new[] { "The requested $DATA stream was not found." });
        if (data.Length > 1 && !attributeListValidated) return Result(NtfsDeletedDataStreamStatus.AmbiguousStream, metadata, warnings: new[] { "Multiple $DATA extents require a complete deleted-specific $ATTRIBUTE_LIST match." });
        if (data.Any(a => a.Flags != 0)) return Result(NtfsDeletedDataStreamStatus.UnsupportedStreamLayout, metadata, warnings: new[] { "Compressed, encrypted, sparse, or otherwise flagged deleted data streams are not supported." });
        if (data.Length == 1 && !data[0].IsNonResident)
        {
            var resident = data[0];
            if (resident.ResidentValue is null || resident.ResidentValueLength is not uint len || len != resident.ResidentValue.Length)
                return Result(NtfsDeletedDataStreamStatus.UnsupportedStreamLayout, metadata, warnings: new[] { "Resident $DATA value metadata is inconsistent." });
            return Result(NtfsDeletedDataStreamStatus.ReadyForBoundedRead, metadata, resident: true, length: len, initializedSize: len,
                warnings: new[] { "Resident bytes remain inside the validated MFT record; this is only a bounded-read eligibility result.", "Content integrity and overwrite history are not verified." });
        }
        if (data.Any(a => !a.IsNonResident)) return Result(NtfsDeletedDataStreamStatus.UnsupportedStreamLayout, metadata, warnings: new[] { "A deleted $DATA stream cannot mix resident and non-resident extents." });
        if (volumeClusterCount == 0 || clusterSizeBytes == 0) return Result(NtfsDeletedDataStreamStatus.UnsupportedStreamLayout, metadata, warnings: new[] { "Volume cluster geometry is invalid." });
        if (allocationMap is null || allocationMap.VolumeClusterCount != volumeClusterCount) return Result(NtfsDeletedDataStreamStatus.AllocationUnknown, metadata, warnings: new[] { "A matching, validated volume allocation map is unavailable; do not read deleted content." });
        var ordered = data.OrderBy(a => a.LowestVcn).ToArray(); var first = ordered[0];
        if (first.LowestVcn != 0 || first.AllocatedSize is not ulong allocated || first.RealSize is not ulong real || first.InitializedSize is not ulong initialized || initialized > real || real > allocated)
            return Result(NtfsDeletedDataStreamStatus.UnsupportedStreamLayout, metadata, warnings: new[] { "Base non-resident $DATA extent is missing VCN 0 or has inconsistent size metadata." });
        try
        {
            var allRuns = new List<NtfsDataRun>(); ulong nextVcn = 0;
            foreach (var extent in ordered)
            {
                if (extent.LowestVcn is not ulong lowest || extent.HighestVcn is not ulong highest || extent.MappingPairs is null || lowest != nextVcn || highest < lowest)
                    return Result(NtfsDeletedDataStreamStatus.InvalidRunlist, metadata, warnings: new[] { "Deleted $DATA extents contain missing metadata, a VCN gap, or an overlap." });
                var parsed = NtfsRunlistParser.Parse(extent.MappingPairs, volumeClusterCount);
                if (!parsed.IsValid) return Result(NtfsDeletedDataStreamStatus.InvalidRunlist, metadata, warnings: new[] { "Deleted $DATA mapping pairs are invalid: " + string.Join("; ", parsed.Issues) });
                var expected = checked(highest - lowest + 1);
                if (parsed.Runs.Count == 0 || parsed.Runs.Any(r => r.IsSparse) || parsed.TotalClusters != expected || parsed.Runs.Count > MaximumRuns - allRuns.Count)
                    return Result(NtfsDeletedDataStreamStatus.InvalidRunlist, metadata, warnings: new[] { "Deleted $DATA mapping pairs do not match the declared VCN extent or exceed the run limit." });
                foreach (var run in parsed.Runs) allRuns.Add(new NtfsDataRun(checked(lowest + run.StartingVcn), run.ClusterCount, run.StartingLcn));
                nextVcn = checked(highest + 1);
                if (nextVcn > volumeClusterCount) return Result(NtfsDeletedDataStreamStatus.InvalidRunlist, metadata, warnings: new[] { "Deleted $DATA extent map exceeds the volume cluster count." });
            }
            ulong previousEnd = 0; var hasPrevious = false;
            foreach (var run in allRuns.OrderBy(r => r.StartingLcn))
            {
                if (run.IsSparse || run.StartingLcn is not ulong start) return Result(NtfsDeletedDataStreamStatus.InvalidRunlist, metadata, warnings: new[] { "Sparse or invalid physical runs are not supported." });
                var end = checked(start + run.ClusterCount);
                if (end > volumeClusterCount || (hasPrevious && start < previousEnd)) return Result(NtfsDeletedDataStreamStatus.InvalidRunlist, metadata, warnings: new[] { "Deleted $DATA extents contain out-of-volume or overlapping physical clusters." });
                previousEnd = end; hasPrevious = true;
            }
            var mapped = checked(nextVcn * clusterSizeBytes);
            if (allocated > mapped || real > mapped) return Result(NtfsDeletedDataStreamStatus.InvalidRunlist, metadata, warnings: new[] { "Deleted $DATA stream sizes exceed the assembled extent map." });
            var allocation = allocationMap.Assess(allRuns);
            if (allocation.Status == NtfsClusterAllocationStatus.Unknown) return Result(NtfsDeletedDataStreamStatus.AllocationUnknown, metadata, runs: allRuns, allocation: allocation, warnings: new[] { "Cluster allocation could not be established; do not read deleted content." });
            if (allocation.Status != NtfsClusterAllocationStatus.Free) return Result(NtfsDeletedDataStreamStatus.ClustersAllocated, metadata, runs: allRuns, allocation: allocation, warnings: new[] { "One or more clusters are currently allocated and may have been reused; do not read deleted content." });
            return Result(NtfsDeletedDataStreamStatus.ReadyForBoundedRead, metadata, length: real, initializedSize: initialized, runs: allRuns, allocation: allocation,
                warnings: new[] { "The complete extent map and current allocation state passed preliminary checks only.", "A free-cluster result does not prove that deleted content is intact or was not overwritten." });
        }
        catch (OverflowException) { return Result(NtfsDeletedDataStreamStatus.InvalidRunlist, metadata, warnings: new[] { "Deleted $DATA extent geometry overflows supported integer bounds." }); }
    }
    private static NtfsDeletedDataStreamAssessment Result(NtfsDeletedDataStreamStatus status, NtfsDeletedFileAssessment metadata, bool resident = false, ulong length = 0, ulong initializedSize = 0, IReadOnlyList<NtfsDataRun>? runs = null, NtfsClusterAllocationAssessment? allocation = null, IReadOnlyList<string>? warnings = null) =>
        new NtfsDeletedDataStreamAssessment(status, metadata, resident, length, runs ?? Array.Empty<NtfsDataRun>(), allocation, metadata.Warnings.Concat(warnings ?? Array.Empty<string>()).ToArray()) { InitializedSize = initializedSize };
}
