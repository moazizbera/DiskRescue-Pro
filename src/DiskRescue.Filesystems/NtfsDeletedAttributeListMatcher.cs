using System.Buffers.Binary;

namespace DiskRescue.Filesystems;

public static class NtfsDeletedAttributeListMatcher
{
    public const int MaximumRecords = 4096;
    public const int MaximumAggregateRecordBytes = 16 * 1024 * 1024;
    public const int MaximumParsedAttributes = NtfsAttributeParser.MaximumAttributes;
    public const int MaximumAttributeComparisons = 1_000_000;
    private const int MaximumRecordBytes = 1024 * 1024;

    public static NtfsAttributeListMatchResult Match(
        NtfsAttributeListParseResult list,
        IReadOnlyList<NtfsFileRecord> records,
        NtfsFileReference expectedBaseReference)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(expectedBaseReference);

        if (!list.IsValid)
        {
            var diagnostics = (list.Issues ?? Array.Empty<string>())
                .Select(issue => new NtfsAttributeListDiagnostic(
                    NtfsAttributeListDiagnosticCode.InvalidAttributeList, null, issue))
                .ToArray();
            if (diagnostics.Length == 0)
                diagnostics = new[]
                {
                    new NtfsAttributeListDiagnostic(
                        NtfsAttributeListDiagnosticCode.InvalidAttributeList,
                        null,
                        "The parsed $ATTRIBUTE_LIST is invalid.")
                };
            return new(false, Array.Empty<NtfsMatchedAttribute>(), diagnostics);
        }
        if (list.Entries is null)
            return Failure(NtfsAttributeListDiagnosticCode.InvalidAttributeList,
                "Parsed $ATTRIBUTE_LIST entries are missing.");
        if (list.Entries.Count > NtfsAttributeListParser.MaximumEntries)
            return Failure(NtfsAttributeListDiagnosticCode.InvalidAttributeList,
                "Parsed $ATTRIBUTE_LIST exceeds its entry limit.");
        if (list.Entries.Count == 0)
            return Failure(NtfsAttributeListDiagnosticCode.InvalidAttributeList,
                "Parsed $ATTRIBUTE_LIST contains no entries.");
        for (var index = 0; index < list.Entries.Count; index++)
        {
            var entry = list.Entries[index];
            if (entry is null || entry.Name is null || entry.Name.Length > byte.MaxValue ||
                entry.Type is 0 or uint.MaxValue || entry.EntryLength < 26 || (entry.EntryLength & 7) != 0 ||
                entry.FileRecordNumber > 0x0000_FFFF_FFFF_FFFFUL ||
                (entry.FileRecordNumber == 0 && entry.FileSequenceNumber == 0))
                return Failure(NtfsAttributeListDiagnosticCode.InvalidAttributeList,
                    "Parsed $ATTRIBUTE_LIST contains a malformed or unsupported entry.", index);
        }
        if (expectedBaseReference.RecordNumber > uint.MaxValue)
            return Failure(NtfsAttributeListDiagnosticCode.InvalidExpectedBaseReference,
                "Expected base MFT record number exceeds the supported FILE-record number range.");
        if (records.Count > MaximumRecords)
            return Failure(NtfsAttributeListDiagnosticCode.RecordLimitExceeded,
                $"Candidate records exceed the {MaximumRecords}-record limit.");

        var recordsByNumber = new Dictionary<uint, List<NtfsFileRecord>>();
        var aggregateBytes = 0;
        foreach (var record in records)
        {
            if (record is null || record.FixedUpBytes is null ||
                record.FixedUpBytes.Length < 48 || record.FixedUpBytes.Length > MaximumRecordBytes)
                return Failure(NtfsAttributeListDiagnosticCode.InvalidCandidateRecord,
                    "Candidate set contains a FILE record with invalid fixed-up bytes.");
            if (record.FixedUpBytes.Length > MaximumAggregateRecordBytes - aggregateBytes)
                return Failure(NtfsAttributeListDiagnosticCode.RecordMemoryLimitExceeded,
                    $"Candidate records exceed the {MaximumAggregateRecordBytes}-byte aggregate limit.");
            aggregateBytes += record.FixedUpBytes.Length;

            var bytes = record.FixedUpBytes.AsSpan();
            var baseReferenceRaw = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(32, 8));
            var baseReference = new NtfsFileReference(
                baseReferenceRaw & 0x0000_FFFF_FFFF_FFFFUL,
                (ushort)(baseReferenceRaw >> 48));
            if (!bytes[..4].SequenceEqual("FILE"u8) ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(16, 2)) != record.SequenceNumber ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(18, 2)) != record.LinkCount ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(20, 2)) != record.FirstAttributeOffset ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(22, 2)) != record.Flags ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(24, 4)) != record.BytesInUse ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(28, 4)) != record.BytesAllocated ||
                record.BytesAllocated != record.FixedUpBytes.Length ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(44, 4)) != record.RecordNumber ||
                baseReference != record.BaseFileReference)
                return Failure(NtfsAttributeListDiagnosticCode.InvalidCandidateRecord,
                    "Candidate FILE record identity fields do not match its validated record metadata.");

            if (!recordsByNumber.TryGetValue(record.RecordNumber, out var sameNumber))
            {
                sameNumber = new List<NtfsFileRecord>(1);
                recordsByNumber.Add(record.RecordNumber, sameNumber);
            }
            sameNumber.Add(record);
        }

        if (!recordsByNumber.TryGetValue(checked((uint)expectedBaseReference.RecordNumber), out var baseRecords) ||
            baseRecords.Count != 1 ||
            baseRecords[0].SequenceNumber != expectedBaseReference.SequenceNumber ||
            (baseRecords[0].Flags & 0x0001) != 0 ||
            !baseRecords[0].BaseFileReference.IsNull)
            return Failure(NtfsAttributeListDiagnosticCode.InvalidExpectedBaseReference,
                "Deleted base FILE record is missing, ambiguous, marked in-use, or has a non-null base-file reference.");

        var duplicatedEntries = FindDuplicateEntries(list.Entries);
        var diagnosticsOut = new List<NtfsAttributeListDiagnostic>();
        var matches = new List<NtfsMatchedAttribute>();
        var attributesByRecord = new Dictionary<uint, NtfsAttributeParseResult>();
        var parsedAttributeCount = 0;
        var attributeComparisonCount = 0;

        for (var entryIndex = 0; entryIndex < list.Entries.Count; entryIndex++)
        {
            var entry = list.Entries[entryIndex];
            if (duplicatedEntries.Contains(entryIndex))
            {
                diagnosticsOut.Add(Diagnostic(NtfsAttributeListDiagnosticCode.DuplicateEntry, entryIndex,
                    "This $ATTRIBUTE_LIST entry duplicates the same referenced attribute."));
                continue;
            }
            if (entry.FileRecordNumber > uint.MaxValue)
            {
                diagnosticsOut.Add(Diagnostic(NtfsAttributeListDiagnosticCode.UnsupportedRecordNumber, entryIndex,
                    "Referenced MFT record number exceeds the supported FILE-record number range."));
                continue;
            }
            if (!recordsByNumber.TryGetValue((uint)entry.FileRecordNumber, out var referencedRecords))
            {
                diagnosticsOut.Add(Diagnostic(NtfsAttributeListDiagnosticCode.MissingRecord, entryIndex,
                    "Referenced MFT record is not in the supplied validated-record set."));
                continue;
            }
            if (referencedRecords.Count != 1)
            {
                diagnosticsOut.Add(Diagnostic(NtfsAttributeListDiagnosticCode.AmbiguousRecord, entryIndex,
                    "More than one supplied FILE record has the referenced MFT record number."));
                continue;
            }

            var record = referencedRecords[0];
            if (record.SequenceNumber != entry.FileSequenceNumber)
            {
                diagnosticsOut.Add(Diagnostic(NtfsAttributeListDiagnosticCode.SequenceMismatch, entryIndex,
                    "Referenced FILE record sequence number does not match the $ATTRIBUTE_LIST reference."));
                continue;
            }
            if ((record.Flags & 0x0001) != 0)
            {
                diagnosticsOut.Add(Diagnostic(NtfsAttributeListDiagnosticCode.RecordInUse, entryIndex,
                    "Deleted-file attribute list references a FILE record still marked in use."));
                continue;
            }
            if (record.RecordNumber == expectedBaseReference.RecordNumber)
            {
                if (record.SequenceNumber != expectedBaseReference.SequenceNumber)
                {
                    diagnosticsOut.Add(Diagnostic(NtfsAttributeListDiagnosticCode.BaseRecordSequenceMismatch, entryIndex,
                        "Reference to the base MFT record does not match the expected base sequence number."));
                    continue;
                }
                if (!record.BaseFileReference.IsNull)
                {
                    diagnosticsOut.Add(Diagnostic(NtfsAttributeListDiagnosticCode.BaseFileReferenceMismatch, entryIndex,
                        "The expected base FILE record has a non-null base-file reference."));
                    continue;
                }
            }
            else if (record.BaseFileReference != expectedBaseReference)
            {
                diagnosticsOut.Add(Diagnostic(NtfsAttributeListDiagnosticCode.BaseFileReferenceMismatch, entryIndex,
                    "Referenced FILE record does not point back to the expected base record and sequence."));
                continue;
            }

            if (!attributesByRecord.TryGetValue(record.RecordNumber, out var parsedAttributes))
            {
                var remainingAttributeBudget = MaximumParsedAttributes - parsedAttributeCount;
                parsedAttributes = NtfsAttributeParser.Parse(record, remainingAttributeBudget);
                attributesByRecord.Add(record.RecordNumber, parsedAttributes);
                if (parsedAttributes.LimitExceeded)
                {
                    diagnosticsOut.Add(Diagnostic(NtfsAttributeListDiagnosticCode.ParsingWorkLimitExceeded, entryIndex,
                        $"Matching exceeds the {MaximumParsedAttributes}-attribute parsing-work limit."));
                    break;
                }
                if (parsedAttributes.IsValid)
                {
                    parsedAttributeCount += parsedAttributes.Attributes.Count;
                }
            }
            if (!parsedAttributes.IsValid)
            {
                diagnosticsOut.Add(Diagnostic(NtfsAttributeListDiagnosticCode.MalformedRecordAttributes, entryIndex,
                    "Referenced FILE record attributes are malformed: " + string.Join("; ", parsedAttributes.Issues)));
                continue;
            }

            if (parsedAttributes.Attributes.Count > MaximumAttributeComparisons - attributeComparisonCount)
            {
                diagnosticsOut.Add(Diagnostic(NtfsAttributeListDiagnosticCode.ParsingWorkLimitExceeded, entryIndex,
                    $"Matching exceeds the {MaximumAttributeComparisons}-comparison attribute-work limit."));
                break;
            }
            attributeComparisonCount += parsedAttributes.Attributes.Count;

            var typeCount = 0;
            var nameCount = 0;
            var idCount = 0;
            var lowestVcnCount = 0;
            NtfsAttribute? matchingAttribute = null;
            foreach (var attribute in parsedAttributes.Attributes)
            {
                if (attribute.Type != entry.Type)
                    continue;
                typeCount++;
                if (!string.Equals(attribute.Name, entry.Name, StringComparison.Ordinal))
                    continue;
                nameCount++;
                if (attribute.AttributeId != entry.AttributeId)
                    continue;
                idCount++;
                if ((attribute.LowestVcn ?? 0) != entry.LowestVcn)
                    continue;
                lowestVcnCount++;
                matchingAttribute = attribute;
            }
            if (typeCount == 0)
            {
                diagnosticsOut.Add(Diagnostic(NtfsAttributeListDiagnosticCode.AttributeTypeMismatch, entryIndex,
                    "Referenced FILE record has no attribute with the listed type."));
                continue;
            }
            if (nameCount == 0)
            {
                diagnosticsOut.Add(Diagnostic(NtfsAttributeListDiagnosticCode.AttributeNameMismatch, entryIndex,
                    "Referenced FILE record has no attribute with the listed ordinal UTF-16 name."));
                continue;
            }
            if (idCount == 0)
            {
                diagnosticsOut.Add(Diagnostic(NtfsAttributeListDiagnosticCode.AttributeIdMismatch, entryIndex,
                    "Referenced FILE record has no attribute with the listed attribute ID."));
                continue;
            }
            if (lowestVcnCount == 0)
            {
                diagnosticsOut.Add(Diagnostic(NtfsAttributeListDiagnosticCode.LowestVcnMismatch, entryIndex,
                    "Referenced FILE record has no attribute with the listed lowest VCN."));
                continue;
            }
            if (lowestVcnCount != 1)
            {
                diagnosticsOut.Add(Diagnostic(NtfsAttributeListDiagnosticCode.AmbiguousAttributeMatch, entryIndex,
                    "More than one attribute matches the complete $ATTRIBUTE_LIST identity."));
                continue;
            }

            matches.Add(new NtfsMatchedAttribute(entryIndex, entry, record, matchingAttribute!));
        }

        return new(diagnosticsOut.Count == 0 && matches.Count == list.Entries.Count,
            matches, diagnosticsOut);
    }

    private static HashSet<int> FindDuplicateEntries(IReadOnlyList<NtfsAttributeListEntry> entries)
    {
        var indexesByEntry = new Dictionary<EntryIdentity, List<int>>();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var identity = new EntryIdentity(
                entry.Type,
                entry.Name,
                entry.AttributeId,
                entry.LowestVcn,
                entry.FileRecordNumber,
                entry.FileSequenceNumber);
            if (!indexesByEntry.TryGetValue(identity, out var indexes))
            {
                indexes = new List<int>(1);
                indexesByEntry.Add(identity, indexes);
            }
            indexes.Add(index);
        }

        var duplicateIndexes = new HashSet<int>();
        foreach (var indexes in indexesByEntry.Values)
        {
            if (indexes.Count > 1)
                duplicateIndexes.UnionWith(indexes);
        }
        return duplicateIndexes;
    }

    private static NtfsAttributeListMatchResult Failure(
        NtfsAttributeListDiagnosticCode code,
        string message,
        int? entryIndex = null) =>
        new(false, Array.Empty<NtfsMatchedAttribute>(), new[]
        {
            Diagnostic(code, entryIndex, message)
        });

    private static NtfsAttributeListDiagnostic Diagnostic(
        NtfsAttributeListDiagnosticCode code,
        int? entryIndex,
        string message) =>
        new(code, entryIndex, message);

    private readonly record struct EntryIdentity(
        uint Type,
        string Name,
        ushort AttributeId,
        ulong LowestVcn,
        ulong RecordNumber,
        ushort SequenceNumber);
}
