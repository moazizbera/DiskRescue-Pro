using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DiskRescue.Filesystems;
using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class NtfsAttributeListMatcherTests
{
    private static readonly NtfsFileReference BaseReference = new(0, 7);

    [Fact]
    public void DeletedMatcherAcceptsDeletedBaseAndContinuationRecords()
    {
        var deletedBase = MarkDeleted(CreateRecord(0, 7, new NtfsFileReference(0, 0),
            new AttributeSpec(0x80, "Data", 1, 0)));
        var deletedContinuation = MarkDeleted(CreateRecord(12, 9, BaseReference,
            new AttributeSpec(0x80, "Data", 2, 2)));
        var list = ParseList(
            new EntrySpec(0x80, "Data", 0, 0, 7, 1),
            new EntrySpec(0x80, "Data", 2, 12, 9, 2));

        var result = NtfsDeletedAttributeListMatcher.Match(
            list, new[] { deletedBase, deletedContinuation }, BaseReference);

        Assert.True(result.IsComplete, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(2, result.Matches.Count);
    }

    [Fact]
    public void DeletedMatcherRejectsContinuationRecordStillMarkedInUse()
    {
        var deletedBase = MarkDeleted(CreateRecord(0, 7, new NtfsFileReference(0, 0),
            new AttributeSpec(0x80, "Data", 1, 0)));
        var activeContinuation = CreateRecord(12, 9, BaseReference,
            new AttributeSpec(0x80, "Data", 2, 2));
        var list = ParseList(
            new EntrySpec(0x80, "Data", 0, 0, 7, 1),
            new EntrySpec(0x80, "Data", 2, 12, 9, 2));

        var result = NtfsDeletedAttributeListMatcher.Match(
            list, new[] { deletedBase, activeContinuation }, BaseReference);

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.RecordInUse);
    }

    [Fact]
    public void DeletedMatcherRejectsActiveOrInvalidBaseRecord()
    {
        var activeBase = CreateRecord(0, 7, new NtfsFileReference(0, 0),
            new AttributeSpec(0x80, "Data", 1, 0));
        var deletedContinuation = MarkDeleted(CreateRecord(12, 9, BaseReference,
            new AttributeSpec(0x80, "Data", 2, 2)));
        var list = ParseList(
            new EntrySpec(0x80, "Data", 0, 0, 7, 1),
            new EntrySpec(0x80, "Data", 2, 12, 9, 2));

        var result = NtfsDeletedAttributeListMatcher.Match(
            list, new[] { activeBase, deletedContinuation }, BaseReference);

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.InvalidExpectedBaseReference);
    }

    [Fact]
    public void DeletedMatcherRejectsStaleContinuationSequence()
    {
        var deletedBase = MarkDeleted(CreateRecord(0, 7, new NtfsFileReference(0, 0),
            new AttributeSpec(0x80, "Data", 1, 0)));
        var deletedContinuation = MarkDeleted(CreateRecord(12, 9, BaseReference,
            new AttributeSpec(0x80, "Data", 2, 2)));
        var list = ParseList(
            new EntrySpec(0x80, "Data", 0, 0, 7, 1),
            new EntrySpec(0x80, "Data", 2, 12, 10, 2));

        var result = NtfsDeletedAttributeListMatcher.Match(
            list, new[] { deletedBase, deletedContinuation }, BaseReference);

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.SequenceMismatch);
    }

    [Fact]
    public void DeletedMatcherRejectsContinuationWithStaleBaseSequence()
    {
        var deletedBase = MarkDeleted(CreateRecord(0, 7, new NtfsFileReference(0, 0),
            new AttributeSpec(0x80, "Data", 1, 0)));
        var staleBaseReference = new NtfsFileReference(0, 8);
        var deletedContinuation = MarkDeleted(CreateRecord(12, 9, staleBaseReference,
            new AttributeSpec(0x80, "Data", 2, 2)));
        var list = ParseList(
            new EntrySpec(0x80, "Data", 0, 0, 7, 1),
            new EntrySpec(0x80, "Data", 2, 12, 9, 2));

        var result = NtfsDeletedAttributeListMatcher.Match(
            list, new[] { deletedBase, deletedContinuation }, BaseReference);

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.BaseFileReferenceMismatch);
    }

    [Fact]
    public void DeletedMatcherRejectsDuplicateContinuationEntries()
    {
        var deletedBase = MarkDeleted(CreateRecord(0, 7, new NtfsFileReference(0, 0),
            new AttributeSpec(0x80, "Data", 1, 0)));
        var deletedContinuation = MarkDeleted(CreateRecord(12, 9, BaseReference,
            new AttributeSpec(0x80, "Data", 2, 2)));
        var continuationEntry = new EntrySpec(0x80, "Data", 2, 12, 9, 2);
        var list = ParseList(
            new EntrySpec(0x80, "Data", 0, 0, 7, 1),
            continuationEntry, continuationEntry);

        var result = NtfsDeletedAttributeListMatcher.Match(
            list, new[] { deletedBase, deletedContinuation }, BaseReference);

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.DuplicateEntry);
    }
    private static NtfsFileRecord MarkDeleted(NtfsFileRecord record)
    {
        var bytes = record.FixedUpBytes.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 0);
        return record with { Flags = 0, FixedUpBytes = bytes };
    }

    [Fact]
    public void MatchesContinuationByFullAttributeIdentity()
    {
        var continuation = CreateRecord(12, 9, BaseReference,
            new AttributeSpec(0x80, "Data", 4, 8));
        var list = ParseList(new EntrySpec(0x80, "Data", 8, 12, 9, 4));

        var result = NtfsAttributeListMatcher.Match(list, new[] { continuation }, BaseReference);

        Assert.True(result.IsComplete);
        Assert.Empty(result.Diagnostics);
        var match = Assert.Single(result.Matches);
        Assert.Equal(0, match.EntryIndex);
        Assert.Equal(0x80u, match.Attribute.Type);
        Assert.Equal("Data", match.Attribute.Name);
        Assert.Equal((ushort)4, match.Attribute.AttributeId);
        Assert.Equal((ulong)8, match.Attribute.LowestVcn);
    }

    [Fact]
    public void MatchesMultipleEntriesAcrossMultipleContinuationRecords()
    {
        var first = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var second = CreateRecord(13, 10, BaseReference, new AttributeSpec(0x80, "Data", 5, 16));
        var result = NtfsAttributeListMatcher.Match(
            ParseList(
                new EntrySpec(0x80, "Data", 8, 12, 9, 4),
                new EntrySpec(0x80, "Data", 16, 13, 10, 5)),
            new[] { first, second },
            BaseReference);

        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Matches.Count);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ReportsPartialMatchesAsIncomplete()
    {
        var record = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var result = NtfsAttributeListMatcher.Match(
            ParseList(
                new EntrySpec(0x80, "Data", 8, 12, 9, 4),
                new EntrySpec(0x80, "Data", 16, 13, 9, 5)),
            new[] { record },
            BaseReference);

        Assert.False(result.IsComplete);
        Assert.Single(result.Matches);
        Assert.Contains(result.Diagnostics,
            diagnostic => diagnostic.Code == NtfsAttributeListDiagnosticCode.MissingRecord);
    }

    [Fact]
    public void RejectsWrongRecordSequence()
    {
        var record = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var result = Match(record, new EntrySpec(0x80, "Data", 8, 12, 8, 4));

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.SequenceMismatch);
    }

    [Fact]
    public void RejectsMissingRecordNumber()
    {
        var record = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var result = Match(record, new EntrySpec(0x80, "Data", 8, 13, 9, 4));

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.MissingRecord);
    }

    [Fact]
    public void RejectsReferencedRecordNotMarkedInUse()
    {
        var record = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var bytes = record.FixedUpBytes.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 0);
        var unusedRecord = record with { Flags = 0, FixedUpBytes = bytes };

        var result = Match(unusedRecord, new EntrySpec(0x80, "Data", 8, 12, 9, 4));

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.RecordNotInUse);
    }

    [Fact]
    public void RejectsWrongBaseFileReference()
    {
        var record = CreateRecord(12, 9, new NtfsFileReference(1, 7),
            new AttributeSpec(0x80, "Data", 4, 8));
        var result = Match(record, new EntrySpec(0x80, "Data", 8, 12, 9, 4));

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.BaseFileReferenceMismatch);
    }

    [Fact]
    public void RejectsMalformedUtf16AttributeName()
    {
        var record = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "A", 4, 8));
        var bytes = record.FixedUpBytes.ToArray();
        var firstAttributeOffset = record.FirstAttributeOffset;
        bytes[firstAttributeOffset + 64] = 0x00;
        bytes[firstAttributeOffset + 65] = 0xD8;
        var malformedRecord = record with { FixedUpBytes = bytes };

        var result = Match(malformedRecord, new EntrySpec(0x80, "A", 8, 12, 9, 4));

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.MalformedRecordAttributes);
    }

    [Fact]
    public void RejectsWrongAttributeType()
    {
        var record = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var result = Match(record, new EntrySpec(0x90, "Data", 8, 12, 9, 4));

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.AttributeTypeMismatch);
    }

    [Fact]
    public void RejectsWrongOrdinalAttributeName()
    {
        var record = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var result = Match(record, new EntrySpec(0x80, "data", 8, 12, 9, 4));

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.AttributeNameMismatch);
    }

    [Fact]
    public void RejectsWrongAttributeId()
    {
        var record = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var result = Match(record, new EntrySpec(0x80, "Data", 8, 12, 9, 5));

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.AttributeIdMismatch);
    }

    [Fact]
    public void RejectsWrongLowestVcn()
    {
        var record = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var result = Match(record, new EntrySpec(0x80, "Data", 9, 12, 9, 4));

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.LowestVcnMismatch);
    }

    [Fact]
    public void RejectsDuplicateContinuationReferenceWithoutReturningEitherMatch()
    {
        var record = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var entry = new EntrySpec(0x80, "Data", 8, 12, 9, 4);
        var result = NtfsAttributeListMatcher.Match(ParseList(entry, entry), new[] { record }, BaseReference);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Matches);
        Assert.Equal(2, result.Diagnostics.Count);
        Assert.All(result.Diagnostics, diagnostic =>
            Assert.Equal(NtfsAttributeListDiagnosticCode.DuplicateEntry, diagnostic.Code));
    }

    [Fact]
    public void ReturnsDiagnosticsInStableEntryOrder()
    {
        var record = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var list = ParseList(
            new EntrySpec(0x90, "Data", 8, 12, 9, 4),
            new EntrySpec(0x80, "data", 8, 12, 9, 4));

        var first = NtfsAttributeListMatcher.Match(list, new[] { record }, BaseReference);
        var second = NtfsAttributeListMatcher.Match(list, new[] { record }, BaseReference);

        Assert.Equal(first.Diagnostics, second.Diagnostics);
        Assert.Equal(
            new[]
            {
                NtfsAttributeListDiagnosticCode.AttributeTypeMismatch,
                NtfsAttributeListDiagnosticCode.AttributeNameMismatch
            },
            first.Diagnostics.Select(diagnostic => diagnostic.Code));
    }

    [Fact]
    public void RejectsAmbiguousAttributeMatch()
    {
        var record = CreateRecord(12, 9, BaseReference,
            new AttributeSpec(0x80, "Data", 4, 8),
            new AttributeSpec(0x80, "Data", 4, 8));
        var result = Match(record, new EntrySpec(0x80, "Data", 8, 12, 9, 4));

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.AmbiguousAttributeMatch);
    }

    [Fact]
    public void RejectsAmbiguousMftRecordNumber()
    {
        var first = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var second = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var result = NtfsAttributeListMatcher.Match(
            ParseList(new EntrySpec(0x80, "Data", 8, 12, 9, 4)),
            new[] { first, second },
            BaseReference);

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.AmbiguousRecord);
    }

    [Fact]
    public void RejectsInvalidUtf16ListName()
    {
        var value = CreateListBytes(new EntrySpec(0x80, "A", 0, 12, 9, 4));
        value[26] = 0x00;
        value[27] = 0xD8;

        var result = NtfsAttributeListParser.Parse(value);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Contains("UTF-16", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsEmptyAndTruncatedAttributeLists()
    {
        var empty = NtfsAttributeListParser.Parse(ReadOnlySpan<byte>.Empty);
        var truncated = NtfsAttributeListParser.Parse(new byte[25]);

        Assert.False(empty.IsValid);
        Assert.Contains(empty.Issues, issue => issue.Contains("empty", StringComparison.OrdinalIgnoreCase));
        Assert.False(truncated.IsValid);
        Assert.Contains(truncated.Issues, issue => issue.Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsUnsupportedNonResidentAttributeList()
    {
        var attribute = new NtfsAttribute(
            NtfsAttributeListParser.AttributeListType, 64, true, 0, 0, string.Empty,
            null, null, 0, 0, 64, 0, 0, 0, new byte[] { 0 }, null);

        var result = NtfsAttributeListParser.Parse(attribute);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Contains("Non-resident", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsExcessiveEntryCount()
    {
        var value = new byte[checked((NtfsAttributeListParser.MaximumEntries + 1) * 32)];
        for (var index = 0; index <= NtfsAttributeListParser.MaximumEntries; index++)
            WriteEntry(value.AsSpan(index * 32, 32), new EntrySpec(0x80, string.Empty, 0, 1, 1, 1));

        var result = NtfsAttributeListParser.Parse(value);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Contains("65,536-entry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsInvalidExpectedBaseReferenceAndUnsupportedRecordNumber()
    {
        var validRecord = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var validList = ParseList(new EntrySpec(0x80, "Data", 8, 12, 9, 4));
        var invalidBase = NtfsAttributeListMatcher.Match(
            validList, new[] { validRecord }, new NtfsFileReference(ulong.MaxValue, 7));
        var unsupportedRecord = NtfsAttributeListMatcher.Match(
            ParseList(new EntrySpec(0x80, "Data", 8, (ulong)uint.MaxValue + 1, 9, 4)),
            new[] { validRecord },
            BaseReference);

        AssertDiagnostic(invalidBase, NtfsAttributeListDiagnosticCode.InvalidExpectedBaseReference);
        AssertDiagnostic(unsupportedRecord, NtfsAttributeListDiagnosticCode.UnsupportedRecordNumber);
    }

    [Fact]
    public void AcceptsMaximumSupportedRecordNumber()
    {
        var record = CreateRecord(uint.MaxValue, 9, BaseReference,
            new AttributeSpec(0x80, "Data", 4, 8));
        var result = Match(record, new EntrySpec(0x80, "Data", 8, uint.MaxValue, 9, 4));

        Assert.True(result.IsComplete);
        Assert.Single(result.Matches);
    }

    [Fact]
    public void RejectsCandidateRecordCountAndAggregateMemoryLimit()
    {
        var record = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var oversizedCount = Enumerable.Repeat(record, NtfsAttributeListMatcher.MaximumRecords + 1).ToArray();
        var validList = ParseList(new EntrySpec(0x80, "Data", 8, 12, 9, 4));
        var countResult = NtfsAttributeListMatcher.Match(validList, oversizedCount, BaseReference);

        var largeBytes = new byte[1024 * 1024];
        "FILE"u8.CopyTo(largeBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(largeBytes.AsSpan(20, 2), 56);
        BinaryPrimitives.WriteUInt32LittleEndian(largeBytes.AsSpan(28, 4), 1024 * 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(largeBytes.AsSpan(44, 4), 12);
        var largeRecord = new NtfsFileRecord(
            0, 0, 0, 0, 1024 * 1024, 56, 12,
            new NtfsFileReference(0, 0), largeBytes);
        var oversizedBytes = Enumerable.Repeat(
            largeRecord, NtfsAttributeListMatcher.MaximumAggregateRecordBytes / largeBytes.Length + 1).ToArray();
        var memoryResult = NtfsAttributeListMatcher.Match(validList, oversizedBytes, BaseReference);

        AssertDiagnostic(countResult, NtfsAttributeListDiagnosticCode.RecordLimitExceeded);
        AssertDiagnostic(memoryResult, NtfsAttributeListDiagnosticCode.RecordMemoryLimitExceeded);
    }

    [Fact]
    public void AcceptsMaximumRecordCount()
    {
        var record = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var records = Enumerable.Repeat(record, NtfsAttributeListMatcher.MaximumRecords).ToArray();
        var result = NtfsAttributeListMatcher.Match(
            ParseList(new EntrySpec(0x80, "Data", 8, 12, 9, 4)), records, BaseReference);

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.AmbiguousRecord);
        Assert.DoesNotContain(result.Diagnostics,
            diagnostic => diagnostic.Code == NtfsAttributeListDiagnosticCode.RecordLimitExceeded);
    }

    [Fact]
    public void AcceptsMaximumAggregateRecordMemory()
    {
        var records = Enumerable.Range(0,
                NtfsAttributeListMatcher.MaximumAggregateRecordBytes / (16 * 1024))
            .Select(index => CreateLargeValidatedRecord(
                checked((uint)(12 + index)),
                9,
                BaseReference,
                index == 0 ? new AttributeSpec(0x80, "Data", 4, 8) : null))
            .ToArray();
        var result = NtfsAttributeListMatcher.Match(
            ParseList(new EntrySpec(0x80, "Data", 8, 12, 9, 4)), records, BaseReference);

        Assert.True(result.IsComplete, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Single(result.Matches);
    }

    [Fact]
    public void RejectsExcessiveAttributeParsingWork()
    {
        var attributes = Enumerable.Repeat(new AttributeSpec(0x80, string.Empty, 1, 0),
            NtfsAttributeParserMaximumAttributes).ToArray();
        var first = CreateRecordForAttributeWork(12, 9, BaseReference, attributes);
        var second = CreateRecordForAttributeWork(13, 9, BaseReference, attributes);
        var result = NtfsAttributeListMatcher.Match(
            ParseList(
                new EntrySpec(0x80, string.Empty, 0, 12, 9, 1),
                new EntrySpec(0x80, string.Empty, 0, 13, 9, 1)),
            new[] { first, second },
            BaseReference);

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.ParsingWorkLimitExceeded);
    }

    [Fact]
    public void AcceptsMaximumParsedAttributeCount()
    {
        var attributes = Enumerable.Range(0, NtfsAttributeListMatcher.MaximumParsedAttributes)
            .Select(index => new AttributeSpec(0x80, string.Empty, checked((ushort)index), 0, true))
            .ToArray();
        var record = CreateRecord(12, 9, BaseReference, attributes);
        var result = Match(record, new EntrySpec(
            0x80, string.Empty, 0, 12, 9,
            checked((ushort)(NtfsAttributeListMatcher.MaximumParsedAttributes - 1))));

        Assert.True(result.IsComplete, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Single(result.Matches);
    }

    [Fact]
    public void RejectsExcessiveAttributeComparisonWork()
    {
        var attributes = Enumerable.Repeat(new AttributeSpec(0x80, string.Empty, 1, 0),
            NtfsAttributeParserMaximumAttributes).ToArray();
        var record = CreateRecordForAttributeWork(12, 9, BaseReference, attributes);
        var entries = Enumerable.Range(
                0,
                NtfsAttributeListMatcher.MaximumAttributeComparisons /
                    NtfsAttributeParserMaximumAttributes + 2)
            .Select(lowestVcn => new EntrySpec(0x80, string.Empty, (ulong)lowestVcn, 12, 9, 1))
            .ToArray();

        var result = NtfsAttributeListMatcher.Match(ParseList(entries), new[] { record }, BaseReference);

        AssertDiagnostic(result, NtfsAttributeListDiagnosticCode.ParsingWorkLimitExceeded);
    }

    [Fact]
    public void AcceptsMaximumAttributeComparisonWork()
    {
        const int attributeCount = 1000;
        var attributes = Enumerable.Range(0, attributeCount)
            .Select(index => new AttributeSpec(0x80, string.Empty, checked((ushort)index), 0, true))
            .ToArray();
        var entries = Enumerable.Range(0, attributeCount)
            .Select(index => new EntrySpec(0x80, string.Empty, 0, 12, 9, checked((ushort)index)))
            .ToArray();
        Assert.Equal(NtfsAttributeListMatcher.MaximumAttributeComparisons,
            attributeCount * entries.Length);
        var record = CreateRecord(12, 9, BaseReference, attributes);

        var result = NtfsAttributeListMatcher.Match(ParseList(entries), new[] { record }, BaseReference);

        Assert.True(result.IsComplete, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(attributeCount, result.Matches.Count);
    }

    [Fact]
    public void DoesNotModifyValidatedRecordBytes()
    {
        var record = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var originalBytes = record.FixedUpBytes.ToArray();

        _ = Match(record, new EntrySpec(0x80, "Data", 8, 12, 9, 4));

        Assert.Equal(originalBytes, record.FixedUpBytes);
    }

    [Fact]
    public void DoesNotWriteToSyntheticImageDuringMatching()
    {
        var record = CreateRecord(12, 9, BaseReference, new AttributeSpec(0x80, "Data", 4, 8));
        var path = Path.Combine(Path.GetTempPath(), $"diskrescue-attribute-list-{Guid.NewGuid():N}.img");
        try
        {
            File.WriteAllBytes(path, SerializeDiskRecord(record));
            var before = SHA256.HashData(File.ReadAllBytes(path));
            using (var image = FileImageReader.Open(path))
            {
                var raw = new byte[checked((int)image.Length)];
                image.ReadExactlyAt(0, raw);
                var parsed = NtfsFileRecordParser.Parse(raw, 512);
                Assert.True(parsed.IsValid, string.Join("; ", parsed.Issues));
                var result = Match(parsed.Record!, new EntrySpec(0x80, "Data", 8, 12, 9, 4));
                Assert.True(result.IsComplete, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
            }

            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static NtfsAttributeListMatchResult Match(NtfsFileRecord record, EntrySpec entry) =>
        NtfsAttributeListMatcher.Match(ParseList(entry), new[] { record }, BaseReference);

    private static NtfsAttributeListParseResult ParseList(params EntrySpec[] entries) =>
        NtfsAttributeListParser.Parse(CreateListBytes(entries));

    private static byte[] CreateListBytes(params EntrySpec[] entries)
    {
        var encodedEntries = entries.Select(entry =>
        {
            var nameBytes = Encoding.Unicode.GetBytes(entry.Name);
            var length = (nameBytes.Length == 0 ? 32 : Align8(26 + nameBytes.Length));
            var bytes = new byte[length];
            WriteEntry(bytes, entry, nameBytes);
            return bytes;
        }).ToArray();
        var value = new byte[encodedEntries.Sum(bytes => bytes.Length)];
        var offset = 0;
        foreach (var bytes in encodedEntries)
        {
            bytes.CopyTo(value, offset);
            offset += bytes.Length;
        }
        return value;
    }

    private static void WriteEntry(Span<byte> target, EntrySpec entry) =>
        WriteEntry(target, entry, Encoding.Unicode.GetBytes(entry.Name));

    private static void WriteEntry(Span<byte> target, EntrySpec entry, byte[] nameBytes)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target, entry.Type);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(4, 2), checked((ushort)target.Length));
        target[6] = checked((byte)(nameBytes.Length / 2));
        target[7] = nameBytes.Length == 0 ? (byte)0 : (byte)26;
        BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(8, 8), entry.LowestVcn);
        var fileReference = ((ulong)entry.SequenceNumber << 48) |
            (entry.FileRecordNumber & 0x0000_FFFF_FFFF_FFFFUL);
        BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(16, 8), fileReference);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(24, 2), entry.AttributeId);
        nameBytes.CopyTo(target.Slice(26));
    }

    private static NtfsFileRecord CreateRecord(
        uint recordNumber,
        ushort sequenceNumber,
        NtfsFileReference baseReference,
        params AttributeSpec[] attributes)
    {
        var attributeBytes = attributes.Select(CreateAttribute).ToArray();
        var totalAttributeBytes = attributeBytes.Sum(attribute => attribute.Length);
        var rawLength = 1024;
        var firstAttributeOffset = 56;
        while (true)
        {
            var usaCount = rawLength / 512 + 1;
            firstAttributeOffset = Align8(48 + usaCount * sizeof(ushort));
            var requiredLength = Align512(checked(firstAttributeOffset + totalAttributeBytes + sizeof(uint)));
            if (requiredLength == rawLength)
                break;
            rawLength = requiredLength;
        }

        var raw = new byte[rawLength];
        "FILE"u8.CopyTo(raw);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(4, 2), 48);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(6, 2),
            checked((ushort)(rawLength / 512 + 1)));
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(16, 2), sequenceNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(20, 2), checked((ushort)firstAttributeOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(28, 4), checked((uint)rawLength));
        var baseReferenceRaw = ((ulong)baseReference.SequenceNumber << 48) | baseReference.RecordNumber;
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(32, 8), baseReferenceRaw);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(44, 4), recordNumber);
        const ushort updateSequenceNumber = 0x5A5A;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(48, 2), updateSequenceNumber);

        var offset = firstAttributeOffset;
        foreach (var bytes in attributeBytes)
        {
            bytes.CopyTo(raw, offset);
            offset += bytes.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offset, 4), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(24, 4), checked((uint)(offset + 4)));
        for (var sector = 1; sector <= rawLength / 512; sector++)
        {
            raw.AsSpan(sector * 512 - 2, 2).CopyTo(raw.AsSpan(48 + sector * 2, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(sector * 512 - 2, 2), updateSequenceNumber);
        }

        var parsed = NtfsFileRecordParser.Parse(raw, 512);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Issues));
        return parsed.Record!;
    }

    private static NtfsFileRecord CreateRecordForAttributeWork(
        uint recordNumber,
        ushort sequenceNumber,
        NtfsFileReference baseReference,
        AttributeSpec[] attributes)
    {
        var attributeBytes = attributes.Select(CreateAttribute).ToArray();
        var firstAttributeOffset = 56;
        var offset = firstAttributeOffset + attributeBytes.Sum(attribute => attribute.Length);
        var raw = new byte[Align512(checked(offset + sizeof(uint)))];
        "FILE"u8.CopyTo(raw);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(16, 2), sequenceNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(18, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(20, 2), checked((ushort)firstAttributeOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(24, 4), checked((uint)(offset + sizeof(uint))));
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(28, 4), checked((uint)raw.Length));
        var baseReferenceRaw = ((ulong)baseReference.SequenceNumber << 48) | baseReference.RecordNumber;
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(32, 8), baseReferenceRaw);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(44, 4), recordNumber);

        offset = firstAttributeOffset;
        foreach (var bytes in attributeBytes)
        {
            bytes.CopyTo(raw, offset);
            offset += bytes.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offset, 4), 0xFFFFFFFF);
        return new NtfsFileRecord(
            sequenceNumber, 1, 1, checked((uint)(offset + sizeof(uint))),
            checked((uint)raw.Length), checked((ushort)firstAttributeOffset), recordNumber,
            baseReference, raw);
    }

    private static NtfsFileRecord CreateLargeValidatedRecord(
        uint recordNumber,
        ushort sequenceNumber,
        NtfsFileReference baseReference,
        AttributeSpec? attribute)
    {
        var raw = new byte[16 * 1024];
        "FILE"u8.CopyTo(raw);
        const ushort usaOffset = 48;
        var usaCount = checked((ushort)(raw.Length / 512 + 1));
        var firstAttributeOffset = Align8(usaOffset + usaCount * sizeof(ushort));
        var bytesInUse = firstAttributeOffset;
        if (attribute is not null)
        {
            var attributeBytes = CreateAttribute(attribute);
            attributeBytes.CopyTo(raw, firstAttributeOffset);
            bytesInUse = checked(firstAttributeOffset + attributeBytes.Length);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(bytesInUse, 4), 0xFFFFFFFF);
        bytesInUse += sizeof(uint);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(4, 2), usaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(6, 2), usaCount);
        const ushort updateSequenceNumber = 0x5A5A;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(usaOffset, 2), updateSequenceNumber);
        for (var sector = 1; sector <= raw.Length / 512; sector++)
        {
            raw.AsSpan(sector * 512 - 2, 2).CopyTo(raw.AsSpan(usaOffset + sector * 2, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(sector * 512 - 2, 2), updateSequenceNumber);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(16, 2), sequenceNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(18, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(20, 2), checked((ushort)firstAttributeOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(24, 4), checked((uint)bytesInUse));
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(28, 4), checked((uint)raw.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(
            raw.AsSpan(32, 8),
            ((ulong)baseReference.SequenceNumber << 48) | baseReference.RecordNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(44, 4), recordNumber);
        var parsed = NtfsFileRecordParser.Parse(raw, 512);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Issues));
        return parsed.Record!;
    }

    private static byte[] CreateAttribute(AttributeSpec spec)
    {
        if (spec.IsResident)
        {
            var resident = new byte[24];
            BinaryPrimitives.WriteUInt32LittleEndian(resident.AsSpan(0, 4), spec.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(resident.AsSpan(4, 4), 24);
            BinaryPrimitives.WriteUInt16LittleEndian(resident.AsSpan(14, 2), spec.AttributeId);
            BinaryPrimitives.WriteUInt32LittleEndian(resident.AsSpan(16, 4), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(resident.AsSpan(20, 2), 24);
            return resident;
        }

        var nameBytes = Encoding.Unicode.GetBytes(spec.Name);
        var mappingPairsOffset = 64 + nameBytes.Length;
        var length = Align8(mappingPairsOffset + 1);
        var attribute = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(0, 4), spec.Type);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(4, 4), checked((uint)length));
        attribute[8] = 1;
        attribute[9] = checked((byte)(nameBytes.Length / 2));
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(10, 2),
            nameBytes.Length == 0 ? (ushort)0 : (ushort)64);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(14, 2), spec.AttributeId);
        BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(16, 8), spec.LowestVcn);
        BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(24, 8), spec.LowestVcn);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(32, 2), checked((ushort)mappingPairsOffset));
        BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(40, 8), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(48, 8), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(56, 8), 0);
        nameBytes.CopyTo(attribute, 64);
        return attribute;
    }

    private static byte[] SerializeDiskRecord(NtfsFileRecord record)
    {
        var bytes = record.FixedUpBytes.ToArray();
        var updateSequenceOffset = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        var updateSequenceCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2));
        var updateSequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(updateSequenceOffset, 2));
        for (var sector = 1; sector < updateSequenceCount; sector++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(sector * 512 - 2, 2), updateSequenceNumber);
        return bytes;
    }

    private static int Align8(int value) => checked((value + 7) & ~7);
    private static int Align512(int value) => checked((value + 511) & ~511);

    private static void AssertDiagnostic(
        NtfsAttributeListMatchResult result,
        NtfsAttributeListDiagnosticCode code)
    {
        Assert.False(result.IsComplete);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    private const int NtfsAttributeParserMaximumAttributes = 4095;

    private sealed record AttributeSpec(
        uint Type,
        string Name,
        ushort AttributeId,
        ulong LowestVcn,
        bool IsResident = false);

    private sealed record EntrySpec(
        uint Type,
        string Name,
        ulong LowestVcn,
        ulong FileRecordNumber,
        ushort SequenceNumber,
        ushort AttributeId);
}
