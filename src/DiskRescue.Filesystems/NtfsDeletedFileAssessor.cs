namespace DiskRescue.Filesystems;

public enum NtfsDeletedMetadataStatus
{
    Verified,
    NotMarkedDeleted,
    RecordIdentityMismatch,
    ExtensionRecordNotIndependent,
    FileNameMismatch,
    ParentReferenceUnverified,
    StaleParentReference
}

public enum NtfsDeletedContentStatus
{
    NotAssessed
}

public sealed record NtfsDeletedFileAssessment(
    NtfsDeletedMetadataStatus MetadataStatus,
    NtfsDeletedContentStatus ContentStatus,
    ulong RecordNumber,
    ushort SequenceNumber,
    string Name,
    ulong ParentRecordNumber,
    ushort ParentSequenceNumber,
    IReadOnlyList<string> Warnings)
{
    public bool MetadataVerified => MetadataStatus == NtfsDeletedMetadataStatus.Verified;
    public bool IsEligibleForContentAssessment => MetadataVerified;
}

/// <summary>
/// Performs metadata-only checks for an opt-in deleted MFT entry.
/// It deliberately does not infer that file content is recoverable or exportable.
/// Cluster allocation/reuse and data-stream validation are separate required stages.
/// </summary>
public static class NtfsDeletedFileAssessor
{
    public static NtfsDeletedFileAssessment Assess(
        NtfsMftEntry entry,
        NtfsFileRecord record,
        NtfsFileName fileName,
        NtfsFileRecord? parentRecord = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(fileName);

        var warnings = new List<string>
        {
            "File content has not been assessed. Metadata discovery is not proof of recoverability.",
            "Cluster allocation/reuse, runlist validity, and content integrity have not been checked."
        };

        NtfsDeletedMetadataStatus status;
        if (record.RecordNumber != entry.RecordNumber || record.SequenceNumber != entry.SequenceNumber)
        {
            status = NtfsDeletedMetadataStatus.RecordIdentityMismatch;
        }
        else if ((record.Flags & 0x0001) != 0 || !entry.IsDeleted)
        {
            status = NtfsDeletedMetadataStatus.NotMarkedDeleted;
        }
        else if (!record.BaseFileReference.IsNull)
        {
            status = NtfsDeletedMetadataStatus.ExtensionRecordNotIndependent;
        }
        else if (!string.Equals(fileName.Name, entry.Name, StringComparison.Ordinal) ||
                 fileName.ParentRecordNumber != entry.ParentRecordNumber ||
                 fileName.ParentSequenceNumber != entry.ParentSequenceNumber ||
                 fileName.Namespace != entry.Namespace)
        {
            status = NtfsDeletedMetadataStatus.FileNameMismatch;
        }
        else if (parentRecord is null)
        {
            status = NtfsDeletedMetadataStatus.ParentReferenceUnverified;
            warnings.Add("The parent MFT record was not supplied; the deleted entry remains detached from the confirmed directory tree.");
        }
        else if (parentRecord.RecordNumber != entry.ParentRecordNumber ||
                 parentRecord.SequenceNumber != entry.ParentSequenceNumber ||
                 (parentRecord.Flags & 0x0002) == 0 ||
                 (parentRecord.Flags & 0x0001) == 0)
        {
            status = NtfsDeletedMetadataStatus.StaleParentReference;
            warnings.Add("The referenced parent is missing, reused, stale, or is not an active directory.");
        }
        else
        {
            status = NtfsDeletedMetadataStatus.Verified;
        }

        if (status != NtfsDeletedMetadataStatus.Verified)
            warnings.Add("Do not present this entry as a confirmed deleted file or offer content extraction.");

        return new(status, NtfsDeletedContentStatus.NotAssessed,
            entry.RecordNumber, entry.SequenceNumber, entry.Name,
            entry.ParentRecordNumber, entry.ParentSequenceNumber, warnings);
    }
}
