namespace DiskRescue.Filesystems;

public sealed record NtfsDeletedDataStreamPreflightResult(
    bool IsValid,
    NtfsDeletedDataStreamAssessment? Assessment,
    IReadOnlyList<string> Issues);

/// <summary>
/// Builds a deleted-stream preflight from validated MFT records. Deleted continuation
/// records are resolved only through a resident, parsed attribute list and a dedicated
/// deleted-record matcher. No file content is read here.
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

        cancellationToken.ThrowIfCancellationRequested();
        var read = ReadRecord(recordNumber);
        if (!read.IsValid || read.Record is null)
            return InvalidDeletedPreflight("Unable to read deleted FILE record: " + string.Join("; ", read.Issues));

        var record = read.Record;
        if ((record.Flags & 0x0001) != 0 || !record.BaseFileReference.IsNull)
            return InvalidDeletedPreflight("Requested FILE record is not an independent deleted base record.");

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
            true);

        var parentRead = ReadRecord(fileName.ParentRecordNumber);
        var parent = parentRead.IsValid ? parentRead.Record : null;
        var issues = new List<string>();
        var assessmentAttributes = parsed.Attributes.Where(attribute => attribute.Type != 0x20 && attribute.Type != 0x80).ToList();
        var matchingData = parsed.Attributes
            .Where(attribute => attribute.Type == 0x80 &&
                                string.Equals(attribute.Name, streamName, StringComparison.Ordinal))
            .ToList();
        var attributeListValidated = false;

        var attributeLists = parsed.Attributes.Where(attribute => attribute.Type == 0x20).ToArray();
        if (attributeLists.Length > 1)
            return InvalidDeletedPreflight("Deleted FILE record has multiple $ATTRIBUTE_LIST attributes.");
        if (attributeLists.Length == 1)
        {
            var list = NtfsAttributeListParser.Parse(attributeLists[0]);
            if (!list.IsValid)
                return InvalidDeletedPreflight("Deleted $ATTRIBUTE_LIST is invalid or unsupported: " + string.Join("; ", list.Issues));
            if (list.Entries.Count > NtfsAttributeListMatcher.MaximumRecords)
                return InvalidDeletedPreflight("Deleted $ATTRIBUTE_LIST exceeds the continuation-record limit.");

            var baseReference = new NtfsFileReference(record.RecordNumber, record.SequenceNumber);
            var recordsByNumber = new Dictionary<ulong, NtfsFileRecord> { [record.RecordNumber] = record };
            foreach (var listEntry in list.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (listEntry.FileRecordNumber == record.RecordNumber)
                    continue;
                if (listEntry.FileRecordNumber > uint.MaxValue)
                    return InvalidDeletedPreflight("Deleted $ATTRIBUTE_LIST references an unsupported MFT record number.");
                if (recordsByNumber.ContainsKey(listEntry.FileRecordNumber))
                    continue;

                var referenced = ReadReferencedRecord(
                    new NtfsFileReference(listEntry.FileRecordNumber, listEntry.FileSequenceNumber), baseReference);
                if (!referenced.IsValid || referenced.Record is null)
                    return InvalidDeletedPreflight(
                        $"Deleted continuation record {listEntry.FileRecordNumber} failed identity validation: " +
                        string.Join("; ", referenced.Issues));
                recordsByNumber.Add(listEntry.FileRecordNumber, referenced.Record);
            }

            var match = NtfsDeletedAttributeListMatcher.Match(
                list, recordsByNumber.Values.ToArray(), baseReference);
            if (!match.IsComplete)
                return InvalidDeletedPreflight("Deleted $ATTRIBUTE_LIST matching failed: " +
                    string.Join("; ", match.Diagnostics.Select(diagnostic => diagnostic.Message)));

            matchingData = match.Matches
                .Where(item => item.Entry.Type == 0x80 &&
                               string.Equals(item.Entry.Name, streamName, StringComparison.Ordinal))
                .Select(item => item.Attribute)
                .ToList();
            attributeListValidated = true;
        }

        assessmentAttributes.AddRange(matchingData);
        var metadata = NtfsDeletedFileAssessor.Assess(entry, record, fileName, parent);

        NtfsClusterAllocationMap? allocationMap = null;
        if (metadata.MetadataVerified && matchingData.Count > 0 && matchingData.All(attribute => attribute.IsNonResident))
        {
            var volumeClusters = (ulong)(_volumeLength / _boot.ClusterSizeBytes);
            var bitmap = NtfsClusterAllocationMap.Read(this, volumeClusters, cancellationToken: cancellationToken);
            allocationMap = bitmap.Map;
            if (!bitmap.IsValid)
                issues.AddRange(bitmap.Issues);
        }

        var assessment = attributeListValidated
            ? NtfsDeletedDataStreamAssessor.AssessValidatedAttributeList(
            entry, record, fileName, parent, assessmentAttributes,
            (ulong)(_volumeLength / _boot.ClusterSizeBytes),
            _boot.ClusterSizeBytes, allocationMap, streamName)
            : NtfsDeletedDataStreamAssessor.Assess(
                entry, record, fileName, parent, assessmentAttributes,
                (ulong)(_volumeLength / _boot.ClusterSizeBytes),
                _boot.ClusterSizeBytes, allocationMap, streamName);

        return new(true, assessment, issues);
    }

    private static NtfsDeletedDataStreamPreflightResult InvalidDeletedPreflight(string issue) =>
        new(false, null, new[] { issue });
}
