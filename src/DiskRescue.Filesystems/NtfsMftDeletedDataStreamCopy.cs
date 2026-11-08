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
        if (data.Length != 1)
            return InvalidDeletedCopy("The requested deleted $DATA stream is missing or ambiguous on re-read.", assessment);

        NtfsFileDataStreamReader reader;
        var attribute = data[0];
        if (attribute.Flags != 0)
            return InvalidDeletedCopy("Deleted $DATA stream flags changed or are unsupported.", assessment);

        if (assessment.IsResident)
        {
            if (attribute.IsNonResident || attribute.ResidentValue is null ||
                attribute.ResidentValueLength != attribute.ResidentValue.Length ||
                (ulong)attribute.ResidentValue.Length != assessment.Length ||
                assessment.InitializedSize != assessment.Length)
                return InvalidDeletedCopy("Resident deleted-stream metadata changed after preflight.", assessment);

            reader = new NtfsFileDataStreamReader(_source, _volumeOffset, _volumeLength,
                _boot.ClusterSizeBytes, attribute.ResidentValue);
        }
        else
        {
            if (!attribute.IsNonResident || attribute.LowestVcn != 0 ||
                attribute.HighestVcn is not ulong highestVcn ||
                attribute.MappingPairs is null ||
                attribute.InitializedSize is not ulong initializedSize ||
                attribute.RealSize is not ulong realSize ||
                initializedSize != assessment.InitializedSize ||
                realSize != assessment.Length ||
                initializedSize > realSize)
                return InvalidDeletedCopy("Non-resident deleted-stream metadata changed or is incomplete.", assessment);

            var volumeClusters = (ulong)(_volumeLength / _boot.ClusterSizeBytes);
            var runlist = NtfsRunlistParser.Parse(attribute.MappingPairs, volumeClusters);
            if (!runlist.IsValid || runlist.Runs.Any(run => run.IsSparse) ||
                highestVcn == ulong.MaxValue || runlist.TotalClusters != highestVcn + 1 ||
                !runlist.Runs.SequenceEqual(assessment.Runs))
                return InvalidDeletedCopy("Non-resident deleted-stream runlist changed or failed revalidation.", assessment);

            // The preflight has already checked every run against the current $Bitmap.
            // The existing reader enforces source and volume byte boundaries on every read.
            reader = new NtfsFileDataStreamReader(_source, _volumeOffset, _volumeLength,
                _boot.ClusterSizeBytes, assessment.Runs, initializedSize, realSize);
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
