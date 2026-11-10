namespace DiskRescue.Filesystems;

public sealed record NtfsDeletedRecoveryAssessment(
    NtfsDeletedFileAssessment Metadata,
    NtfsClusterAllocationAssessment ClusterAllocation,
    bool IsCandidateForFurtherValidation,
    bool ContentIntegrityVerified,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Combines verified deleted metadata with current $Bitmap allocation evidence.
/// It never declares a deleted file recoverable: a later data-integrity stage is mandatory.
/// </summary>
public static class NtfsDeletedRecoveryAssessor
{
    public static NtfsDeletedRecoveryAssessment Assess(
        NtfsDeletedFileAssessment metadata,
        NtfsClusterAllocationMap allocationMap,
        IReadOnlyList<NtfsDataRun> dataRuns)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(allocationMap);
        ArgumentNullException.ThrowIfNull(dataRuns);

        NtfsClusterAllocationAssessment allocation;
        if (!metadata.IsEligibleForContentAssessment)
        {
            allocation = new(NtfsClusterAllocationStatus.Unknown, 0, 0, 0,
                new[] { "Cluster allocation was not assessed because deleted metadata identity is not verified." });
        }
        else
        {
            allocation = allocationMap.Assess(dataRuns);
        }

        var candidate = metadata.MetadataVerified &&
            allocation.Status == NtfsClusterAllocationStatus.Free;
        var warnings = metadata.Warnings.Concat(allocation.Warnings).ToList();
        warnings.Add(candidate
            ? "Metadata and current allocation state passed preliminary checks only; content integrity and overwrite history remain unverified."
            : "This entry does not qualify for the next recovery-validation stage.");

        return new(metadata, allocation, candidate, false, warnings);
    }
}
