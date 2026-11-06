namespace DiskRescue.Filesystems;

public sealed record NtfsMftContinuationDiscoveryResult(
    bool IsValid,
    NtfsMftReader? Reader,
    bool Extended,
    IReadOnlyList<string> Issues);

/// <summary>
/// Extends an MFT reader only when every record referenced by record 0's resident
/// $ATTRIBUTE_LIST is already readable through the currently validated MFT map.
/// This deliberately fails closed when a referenced continuation record lies
/// outside that map; bootstrapping an unknown MFT extent is not attempted.
/// </summary>
public sealed partial class NtfsMftReader
{
    private const uint AttributeListType = 0x20;
    private const uint DataAttributeType = 0x80;
    private const int MaximumContinuationRecords = 4096;

    public NtfsMftContinuationDiscoveryResult ExtendUsingResidentAttributeList()
    {
        var baseRead = ReadRecord(0);
        if (!baseRead.IsValid || baseRead.Record is null)
            return ContinuationFailure("Unable to re-read validated MFT record 0: " + string.Join("; ", baseRead.Issues));

        var baseAttributes = NtfsAttributeParser.Parse(baseRead.Record);
        if (!baseAttributes.IsValid)
            return ContinuationFailure("MFT record 0 attributes are invalid: " + string.Join("; ", baseAttributes.Issues));

        var listAttributes = baseAttributes.Attributes
            .Where(attribute => attribute.Type == AttributeListType)
            .ToArray();
        if (listAttributes.Length == 0)
            return new(true, this, false, Array.Empty<string>());
        if (listAttributes.Length != 1 || listAttributes[0].IsNonResident)
            return ContinuationFailure("MFT record 0 must contain at most one resident $ATTRIBUTE_LIST; non-resident or ambiguous lists are unsupported.");

        var parsedList = NtfsAttributeListParser.Parse(listAttributes[0]);
        if (!parsedList.IsValid)
            return ContinuationFailure("MFT $ATTRIBUTE_LIST is invalid: " + string.Join("; ", parsedList.Issues));
        if (parsedList.Entries.Count > MaximumContinuationRecords)
            return ContinuationFailure($"MFT $ATTRIBUTE_LIST references more than the {MaximumContinuationRecords}-record discovery limit.");

        var recordsByNumber = new Dictionary<ulong, NtfsFileRecord>
        {
            [0] = baseRead.Record
        };
        foreach (var entry in parsedList.Entries)
        {
            if (recordsByNumber.ContainsKey(entry.FileRecordNumber))
                continue;
            if (entry.FileRecordNumber > uint.MaxValue)
                return ContinuationFailure("MFT $ATTRIBUTE_LIST references a record number outside the supported FILE-record range.");

            var referenced = ReadRecord(entry.FileRecordNumber);
            if (!referenced.IsValid || referenced.Record is null)
                return ContinuationFailure(
                    $"MFT continuation record {entry.FileRecordNumber} is not covered by the currently validated MFT map: " +
                    string.Join("; ", referenced.Issues));
            recordsByNumber.Add(entry.FileRecordNumber, referenced.Record);
        }

        var match = NtfsAttributeListMatcher.Match(parsedList, recordsByNumber.Values.ToArray(),
            new NtfsFileReference(0, baseRead.Record.SequenceNumber));
        if (!match.IsComplete)
            return ContinuationFailure("MFT $ATTRIBUTE_LIST identity validation was incomplete: " +
                string.Join("; ", match.Diagnostics.Select(diagnostic => diagnostic.Message)));

        var dataExtents = match.Matches
            .Where(item => item.Entry.Type == DataAttributeType &&
                           item.Entry.Name.Length == 0 &&
                           item.Attribute.Type == DataAttributeType &&
                           item.Attribute.IsNonResident &&
                           item.Attribute.Name.Length == 0)
            .Select(item => item.Attribute)
            .ToArray();
        if (dataExtents.Length == 0)
            return ContinuationFailure("Validated MFT $ATTRIBUTE_LIST contains no unnamed non-resident $DATA extents.");

        try
        {
            var volumeClusters = (ulong)(_volumeLength / _boot.ClusterSizeBytes);
            var assembled = NtfsMftExtentMap.Assemble(dataExtents, volumeClusters, _boot.ClusterSizeBytes);
            if (!assembled.IsValid)
                return ContinuationFailure("Validated MFT continuation extents could not be assembled: " +
                    string.Join("; ", assembled.Issues));
            if (assembled.InitializedSize != _mftInitializedSize)
                return ContinuationFailure("Assembled MFT initialized size does not match the base reader's validated size.");
            if (!assembled.Runs.Take(_runs.Count).SequenceEqual(_runs))
                return ContinuationFailure("Assembled MFT map does not preserve the original validated VCN-to-LCN mapping.");

            var extended = new NtfsMftReader(_source, _volumeOffset, _volumeLength, _boot,
                assembled.Runs, assembled.InitializedSize, assembled.MappedBytes);
            return new(true, extended, assembled.Runs.Count > _runs.Count, Array.Empty<string>());
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        {
            return ContinuationFailure("MFT continuation extent validation overflowed: " + ex.Message);
        }
    }

    private static NtfsMftContinuationDiscoveryResult ContinuationFailure(string issue) =>
        new(false, null, false, new[] { issue });
}
