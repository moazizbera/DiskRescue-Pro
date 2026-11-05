using System.Buffers.Binary;
using System.Text;
using DiskRescue.Filesystems;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class NtfsAttributeListParserTests
{
    [Fact]
    public void ParsesMultipleEntriesAndSplitsFileReference()
    {
        var value = new byte[72];
        WriteEntry(value.AsSpan(0, 40), type: 0x80, length: 40, name: "DATA",
            lowestVcn: 8, recordNumber: 0x123456789ABC, sequence: 0xBEEF, attributeId: 7);
        WriteEntry(value.AsSpan(40, 32), type: 0x80, length: 32, name: null,
            lowestVcn: 0, recordNumber: 42, sequence: 3, attributeId: 2);

        var result = NtfsAttributeListParser.Parse(value);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("DATA", result.Entries[0].Name);
        Assert.Equal((ulong)8, result.Entries[0].LowestVcn);
        Assert.Equal(0x123456789ABCUL, result.Entries[0].FileRecordNumber);
        Assert.Equal((ushort)0xBEEF, result.Entries[0].FileSequenceNumber);
        Assert.Equal((ushort)7, result.Entries[0].AttributeId);
        Assert.Equal((ulong)42, result.Entries[1].FileRecordNumber);
        Assert.Equal((ushort)3, result.Entries[1].FileSequenceNumber);
    }

    [Fact]
    public void RejectsTruncatedEntry()
    {
        var value = new byte[27];
        BinaryPrimitives.WriteUInt32LittleEndian(value, 0x80);
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(4), 32);
        var result = NtfsAttributeListParser.Parse(value);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsUnalignedEntryLength()
    {
        var value = new byte[32];
        WriteEntry(value, 0x80, 32, null, 0, 1, 1, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(4), 27);
        var result = NtfsAttributeListParser.Parse(value);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("unaligned", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsNameOutsideEntry()
    {
        var value = new byte[32];
        WriteEntry(value, 0x80, 32, null, 0, 1, 1, 1);
        value[6] = 4;
        value[7] = 30;
        var result = NtfsAttributeListParser.Parse(value);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("name range", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsOddUtf16NameOffset()
    {
        var value = new byte[32];
        WriteEntry(value, 0x80, 32, null, 0, 1, 1, 1);
        value[6] = 1;
        value[7] = 27;
        var result = NtfsAttributeListParser.Parse(value);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("name range", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsTruncatedTrailingHeader()
    {
        var value = new byte[33];
        WriteEntry(value.AsSpan(0, 32), 0x80, 32, null, 0, 1, 1, 1);
        var result = NtfsAttributeListParser.Parse(value);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("truncated entry header", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsOversizedValueBeforeParsing()
    {
        var value = new byte[16 * 1024 * 1024 + 1];
        var result = NtfsAttributeListParser.Parse(value);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("16 MiB", StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteEntry(
        Span<byte> target, uint type, ushort length, string? name,
        ulong lowestVcn, ulong recordNumber, ushort sequence, ushort attributeId)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target, type);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(4), length);
        var nameBytes = name is null ? Array.Empty<byte>() : Encoding.Unicode.GetBytes(name);
        target[6] = checked((byte)(nameBytes.Length / 2));
        target[7] = nameBytes.Length == 0 ? (byte)0 : (byte)26;
        BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(8), lowestVcn);
        var reference = ((ulong)sequence << 48) | (recordNumber & 0x0000_FFFF_FFFF_FFFF);
        BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(16), reference);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(24), attributeId);
        nameBytes.AsSpan().CopyTo(target.Slice(26));
    }
}
