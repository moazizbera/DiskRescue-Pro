namespace DiskRescue.Filesystems;

public sealed record NtfsDeletedDataStreamCopyOperationResult(
    bool IsValid,
    NtfsDeletedDataStreamAssessment? Assessment,
    NtfsFileDataStreamCopyResult? Copy,
    IReadOnlyList<string> Issues);

/// <summary>
/// Performs a deleted-file data read only after a fresh, MFT-backed metadata and
/// $Bitmap preflight. It never changes the existing live-file OpenDataStream path.
/// The caller owns the destination stream and must select storage separate from
/// the source image. A successful copy is not proof of original-content integrity.
/// </summary>
public sealed partial class NtfsMftReader
{
    public NtfsDeletedDataStreamCopyOperationResult CopyDeletedDataStreamTo(
        ulong recordNumber,
        Stream destination,
        ulong maximumBytes,
        string streamName = "",
        CancellationToken cancellationToken = default,
        IProgress<ulong>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(streamName);
        if (!destination.CanWrite)
            return InvalidDeletedCopy("Destination stream is not writable.");

        cancellationToken.ThrowIfCancellationRequested();
        var preflight = AssessDeletedDataStream(recordNumber, streamName, cancellationToken);
        if (!preflight.IsValid || preflight.Assessment is null)
            return InvalidDeletedCopy("Deleted-stream preflight failed: " + string.Join("; ", preflight.Issues));

        var assessment = preflight.Assessment;
        if (!assessment.IsEligibleForBoundedRead)
            return new(false, assessment, null,
                new[] { "Deleted-stream preflight did not authorize a bounded read: " +
                        assessment.Status + ". " + string.Join("; ", assessment.Warnings) });

        if (assessment.Length > maximumBytes)
            return new(false, assessment, null,
                new[] { $"Deleted stream length {assessment.Length} exceeds the caller's {maximumBytes}-byte export limit." });

        // Revalidate the full deleted metadata chain for resident streams too. The
        // resident branch does not need $Bitmap, but it still depends on the parent
        // record identity and deleted-file state remaining valid at read time.
        cancellationToken.ThrowIfCancellationRequested();
        var refreshedPreflight = AssessDeletedDataStream(recordNumber, streamName, cancellationToken);
        if (!refreshedPreflight.IsValid || refreshedPreflight.Assessment is null ||
            !refreshedPreflight.Assessment.IsEligibleForBoundedRead)
            return InvalidDeletedCopy("Deleted-stream metadata failed immediate pre-read revalidation: " +
                string.Join("; ", refreshedPreflight.Issues.Concat(
                    refreshedPreflight.Assessment?.Warnings ?? Array.Empty<string>())),
                refreshedPreflight.Assessment ?? assessment);

        var refreshedAssessment = refreshedPreflight.Assessment;
        if (refreshedAssessment.Metadata.RecordNumber != assessment.Metadata.RecordNumber ||
            refreshedAssessment.Metadata.SequenceNumber != assessment.Metadata.SequenceNumber ||
            refreshedAssessment.Length != assessment.Length ||
            refreshedAssessment.InitializedSize != assessment.InitializedSize ||
            refreshedAssessment.IsResident != assessment.IsResident ||
            !refreshedAssessment.Runs.SequenceEqual(assessment.Runs))
            return InvalidDeletedCopy("Deleted stream identity, sizes, or extent map changed after initial preflight.",
                refreshedAssessment);
        assessment = refreshedAssessment;

        cancellationToken.ThrowIfCancellationRequested();
        var recordRead = ReadRecord(recordNumber);
        if (!recordRead.IsValid || recordRead.Record is null)
            return InvalidDeletedCopy("Unable to re-read the preflighted FILE record: " +
                                      string.Join("; ", recordRead.Issues), assessment);

        var record = recordRead.Record;
        if (record.RecordNumber != assessment.Metadata.RecordNumber ||
            record.SequenceNumber != assessment.Metadata.SequenceNumber ||
            (record.Flags & 0x0001) != 0 || !record.BaseFileReference.IsNull ||
            (record.Flags & 0x0002) != 0)
            return InvalidDeletedCopy("FILE record identity or deleted-file state changed after preflight.", assessment);

        var parsed = NtfsAttributeParser.Parse(record);
        if (!parsed.IsValid)
            return InvalidDeletedCopy("FILE record attributes could not be revalidated: " +
                                      string.Join("; ", parsed.Issues), assessment);

        var data = parsed.Attributes.Where(attribute =>
            attribute.Type == 0x80 &&
            string.Equals(attribute.Name, streamName, StringComparison.Ordinal)).ToArray();

        NtfsFileDataStreamReader reader;
        if (assessment.IsResident)
        {
            if (data.Length != 1)
                return InvalidDeletedCopy("The resident deleted $DATA stream is missing or ambiguous on re-read.", assessment);
            var attribute = data[0];
            if (attribute.Flags != 0 || attribute.IsNonResident || attribute.ResidentValue is null ||
                attribute.ResidentValueLength != attribute.ResidentValue.Length ||
                (ulong)attribute.ResidentValue.Length != assessment.Length ||
                assessment.InitializedSize != assessment.Length)
                return InvalidDeletedCopy("Resident deleted-stream metadata changed after preflight.", assessment);

            reader = new NtfsFileDataStreamReader(_source, _volumeOffset, _volumeLength,
                _boot.ClusterSizeBytes, attribute.ResidentValue);
        }
        else
        {
            // Re-run the complete MFT/attribute-list/extent/$Bitmap preflight immediately
            // before content I/O. This validates continuation FILE records as well as the
            // base record; parsing only the base record cannot validate a fragmented stream.
            var refreshed = AssessDeletedDataStream(recordNumber, streamName, cancellationToken);
            if (!refreshed.IsValid || refreshed.Assessment is null ||
                !refreshed.Assessment.IsEligibleForBoundedRead)
                return InvalidDeletedCopy("Deleted multi-extent stream failed immediate pre-read revalidation: " +
                    string.Join("; ", refreshed.Issues.Concat(refreshed.Assessment?.Warnings ?? Array.Empty<string>())),
                    refreshed.Assessment ?? assessment);

            var current = refreshed.Assessment;
            if (current.Metadata.RecordNumber != assessment.Metadata.RecordNumber ||
                current.Metadata.SequenceNumber != assessment.Metadata.SequenceNumber ||
                current.Length != assessment.Length ||
                current.InitializedSize != assessment.InitializedSize ||
                current.IsResident ||
                !current.Runs.SequenceEqual(assessment.Runs))
                return InvalidDeletedCopy("Deleted stream identity, sizes, or complete extent map changed after preflight.", current);

            reader = new NtfsFileDataStreamReader(_source, _volumeOffset, _volumeLength,
                _boot.ClusterSizeBytes, current.Runs, current.InitializedSize, current.Length);
            assessment = current;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var copy = reader.CopyTo(destination, maximumBytes, cancellationToken, progress);
        return new(copy.IsValid, assessment, copy,
            copy.Issues);
    }

    private static NtfsDeletedDataStreamCopyOperationResult InvalidDeletedCopy(
        string issue,
        NtfsDeletedDataStreamAssessment? assessment = null) =>
        new(false, assessment, null, new[] { issue });
}
