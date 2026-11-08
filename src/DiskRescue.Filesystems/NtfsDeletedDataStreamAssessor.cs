namespace DiskRescue.Filesystems;

public enum NtfsDeletedDataStreamStatus
{
    ReadyForBoundedRead,
    MetadataRejected,
    StreamMissing,
    AmbiguousStream,
    UnsupportedStreamLayout,
    InvalidRunlist,
    AllocationUnknown,
    ClustersAllocated
}

public sealed record NtfsDeletedDataStreamAssessment(
    NtfsDeletedDataStreamStatus Status,
    NtfsDeletedFileAssessment Metadata,
    bool IsResident,
    ulong Length,
    IReadOnlyList<NtfsDataRun> Runs,
    NtfsClusterAllocationAssessment? ClusterAllocation,
    IReadOnlyList<string> Warnings)
{
    /// <summary>NTFS initialized-size boundary; bytes after this offset are zero-filled.</summary>
    public ulong InitializedSize { get; init; }

    public bool IsEligibleForBoundedRead => Status == NtfsDeletedDataStreamStatus.ReadyForBoundedRead;
}

/// <summary>
/// Performs a read-free preflight for one deleted file data stream. It deliberately
/// supports only a single, unnamed or named $DATA attribute extent. It never reads
/// file content and never claims that the old content is intact.
/// </summary>
public static class NtfsDeletedDataStreamAssessor
{
    private const uint DataAttributeType = 0x80;

    public static NtfsDeletedDataStreamAssessment Assess(
        NtfsMftEntry entry,
        NtfsFileRecord record,
        NtfsFileName fileName,
        NtfsFileRecord? parentRecord,
        IReadOnlyList<NtfsAttribute> attributes,
        ulong volumeClusterCount,
        uint clusterSizeBytes,
        NtfsClusterAllocationMap? allocationMap,
        string streamName = "")
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(streamName);

        var metadata = NtfsDeletedFileAssessor.Assess(entry, record, fileName, parentRecord);
        if (!metadata.MetadataVerified || (record.Flags & 0x0002) != 0)
            return Result(NtfsDeletedDataStreamStatus.MetadataRejected, metadata,
                warnings: new[] { "Deleted metadata is not verified as an independent file; no data stream may be read." });

        if (attributes.Any(attribute => attribute.Type == 0x20))
            return Result(NtfsDeletedDataStreamStatus.UnsupportedStreamLayout, metadata,
                warnings: new[] { "Deleted files with $ATTRIBUTE_LIST metadata require continuation-record validation, which this preflight does not yet implement." });

        var data = attributes.Where(attribute =>
            attribute.Type == DataAttributeType &&
            string.Equals(attribute.Name, streamName, StringComparison.Ordinal)).ToArray();
        if (data.Length == 0)
            return Result(NtfsDeletedDataStreamStatus.StreamMissing, metadata,
                warnings: new[] { "The requested $DATA stream was not found." });
        if (data.Length != 1)
            return Result(NtfsDeletedDataStreamStatus.AmbiguousStream, metadata,
                warnings: new[] { "Multiple matching $DATA attributes require validated $ATTRIBUTE_LIST support; this preflight fails closed." });

        var attribute = data[0];
        if (attribute.Flags != 0)
            return Result(NtfsDeletedDataStreamStatus.UnsupportedStreamLayout, metadata,
                warnings: new[] { "Compressed, encrypted, sparse, or otherwise flagged deleted data streams are not supported." });

        if (!attribute.IsNonResident)
        {
            if (attribute.ResidentValue is null ||
                attribute.ResidentValueLength is not uint residentLength ||
                residentLength != attribute.ResidentValue.Length)
                return Result(NtfsDeletedDataStreamStatus.UnsupportedStreamLayout, metadata,
                    warnings: new[] { "Resident $DATA value metadata is inconsistent." });

            return Result(NtfsDeletedDataStreamStatus.ReadyForBoundedRead, metadata,
                resident: true, length: residentLength, initializedSize: residentLength,
                warnings: new[]
                {
                    "Resident bytes remain inside the validated MFT record; this is only a bounded-read eligibility result.",
                    "Content integrity and overwrite history are not verified."
                });
        }

        if (volumeClusterCount == 0 || clusterSizeBytes == 0 ||
            attribute.LowestVcn != 0 || attribute.HighestVcn is not ulong highestVcn ||
            attribute.MappingPairs is null || attribute.RealSize is not ulong realSize ||
            attribute.InitializedSize is not ulong initializedSize ||
            attribute.AllocatedSize is not ulong allocatedSize ||
            initializedSize > realSize || realSize > allocatedSize)
            return Result(NtfsDeletedDataStreamStatus.UnsupportedStreamLayout, metadata,
                warnings: new[] { "Non-resident stream geometry or size metadata is missing or inconsistent." });

        if (allocationMap is null || allocationMap.VolumeClusterCount != volumeClusterCount)
            return Result(NtfsDeletedDataStreamStatus.AllocationUnknown, metadata,
                warnings: new[] { "A matching, validated volume allocation map is unavailable; do not read deleted content." });

        var parsedRuns = NtfsRunlistParser.Parse(attribute.MappingPairs, volumeClusterCount);
        if (!parsedRuns.IsValid || parsedRuns.Runs.Count == 0 ||
            parsedRuns.Runs.Any(run => run.IsSparse) ||
            highestVcn == ulong.MaxValue || parsedRuns.TotalClusters != highestVcn + 1)
            return Result(NtfsDeletedDataStreamStatus.InvalidRunlist, metadata,
                warnings: new[] { "Deleted $DATA mapping pairs are invalid, sparse, empty, or inconsistent with the declared VCN range." });

        ulong mappedBytes;
        try { mappedBytes = checked(parsedRuns.TotalClusters * clusterSizeBytes); }
        catch (OverflowException)
        {
            return Result(NtfsDeletedDataStreamStatus.InvalidRunlist, metadata,
                warnings: new[] { "Deleted $DATA extent size overflows supported bounds." });
        }
        if (allocatedSize > mappedBytes || realSize > mappedBytes)
            return Result(NtfsDeletedDataStreamStatus.InvalidRunlist, metadata,
                warnings: new[] { "Deleted $DATA size metadata exceeds the decoded extent map." });
        var allocation = allocationMap.Assess(parsedRuns.Runs);
        if (allocation.Status == NtfsClusterAllocationStatus.Unknown)
            return Result(NtfsDeletedDataStreamStatus.AllocationUnknown, metadata,
                runs: parsedRuns.Runs, allocation: allocation,
                warnings: new[] { "Cluster allocation could not be established; do not read deleted content." });
        if (allocation.Status != NtfsClusterAllocationStatus.Free)
            return Result(NtfsDeletedDataStreamStatus.ClustersAllocated, metadata,
                runs: parsedRuns.Runs, allocation: allocation,
                warnings: new[] { "One or more clusters are currently allocated and may have been reused; do not read deleted content." });

        return Result(NtfsDeletedDataStreamStatus.ReadyForBoundedRead, metadata,
            length: realSize, initializedSize: initializedSize, runs: parsedRuns.Runs, allocation: allocation,
            warnings: new[]
            {
                "The runlist and current allocation state passed preliminary checks only.",
                "A free-cluster result does not prove that deleted content is intact or was not overwritten."
            });
    }

    private static NtfsDeletedDataStreamAssessment Result(
        NtfsDeletedDataStreamStatus status,
        NtfsDeletedFileAssessment metadata,
        bool resident = false,
        ulong length = 0,
        ulong initializedSize = 0,
        IReadOnlyList<NtfsDataRun>? runs = null,
        NtfsClusterAllocationAssessment? allocation = null,
        IReadOnlyList<string>? warnings = null) =>
        new NtfsDeletedDataStreamAssessment(status, metadata, resident, length,
            runs ?? Array.Empty<NtfsDataRun>(), allocation,
            metadata.Warnings.Concat(warnings ?? Array.Empty<string>()).ToArray())
        { InitializedSize = initializedSize };
}
