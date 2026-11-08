namespace DiskRescue.Filesystems;

public sealed record NtfsDeletedDataStreamPreflightResult(
    bool IsValid,
    NtfsDeletedDataStreamAssessment? Assessment,
    IReadOnlyList<string> Issues);

/// <summary>
/// Builds a deleted-stream preflight from records read through this validated MFT
/// reader. It does not return a content reader and performs no file-data reads.
/// </summary>
public sealed partial class NtfsMftReader
{
    public NtfsDeletedDataStreamPreflightResult AssessDeletedDataStream(
        ulong recordNumber,
        string streamName = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamName);
        if (streamName.Length > byte.MaxValue)
            return InvalidDeletedPreflight("NTFS stream name exceeds the supported 255-character limit.");

        var read = ReadRecord(recordNumber);
        if (!read.IsValid || read.Record is null)
            return InvalidDeletedPreflight("Unable to read deleted FILE record: " + string.Join("; ", read.Issues));

        var record = read.Record;
        var parsed = NtfsAttributeParser.Parse(record);
        if (!parsed.IsValid)
            return InvalidDeletedPreflight("Deleted FILE record attributes are invalid: " + string.Join("; ", parsed.Issues));

        var fileNameAttributes = parsed.Attributes
            .Where(attribute => attribute.Type == 0x30 && !attribute.IsNonResident)
            .ToArray();
        if (fileNameAttributes.Length != 1)
            return InvalidDeletedPreflight(
                fileNameAttributes.Length == 0
                    ? "Deleted FILE record has no resident $FILE_NAME attribute."
                    : "Deleted FILE record has multiple $FILE_NAME attributes; hard-link identity is ambiguous.");

        var parsedName = NtfsFileNameParser.Parse(fileNameAttributes[0]);
        if (!parsedName.IsValid || parsedName.FileName is null)
            return InvalidDeletedPreflight("$FILE_NAME validation failed: " + string.Join("; ", parsedName.Issues));

        var fileName = parsedName.FileName;
        var entry = new NtfsMftEntry(
            record.RecordNumber,
            record.SequenceNumber,
            fileName.ParentRecordNumber,
            fileName.ParentSequenceNumber,
            fileName.Name,
            fileName.Namespace,
            (record.Flags & 0x0002) != 0,
            fileName.AllocatedSize,
            fileName.RealSize,
            fileName.FileAttributes,
            (record.Flags & 0x0001) == 0);

        var parentRead = ReadRecord(fileName.ParentRecordNumber);
        var parent = parentRead.IsValid ? parentRead.Record : null;
        var metadata = NtfsDeletedFileAssessor.Assess(entry, record, fileName, parent);

        var matchingData = parsed.Attributes
            .Where(attribute => attribute.Type == 0x80 &&
                                string.Equals(attribute.Name, streamName, StringComparison.Ordinal))
            .ToArray();

        NtfsClusterAllocationMap? allocationMap = null;
        var issues = new List<string>();
        if (metadata.MetadataVerified && matchingData.Length == 1 && matchingData[0].IsNonResident)
        {
            var volumeClusters = (ulong)(_volumeLength / _boot.ClusterSizeBytes);
            var bitmap = NtfsClusterAllocationMap.Read(this, volumeClusters, cancellationToken: cancellationToken);
            allocationMap = bitmap.Map;
            if (!bitmap.IsValid)
                issues.AddRange(bitmap.Issues);
        }

        var assessment = NtfsDeletedDataStreamAssessor.Assess(
            entry, record, fileName, parent, parsed.Attributes,
            (ulong)(_volumeLength / _boot.ClusterSizeBytes),
            _boot.ClusterSizeBytes, allocationMap, streamName);

        return new(true, assessment, issues);
    }

    private static NtfsDeletedDataStreamPreflightResult InvalidDeletedPreflight(string issue) =>
        new(false, null, new[] { issue });
}
