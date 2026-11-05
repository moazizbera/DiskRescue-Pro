using System.Buffers.Binary;
using System.Text;
using DiskRescue.Filesystems;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class NtfsFileRecordParserTests
{
    [Fact]
    public void ParsesRecordAndAppliesSectorTrailerFixups()
    {
        var raw = CreateRecord();
        var result = NtfsFileRecordParser.Parse(raw, 512);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Record);
        Assert.Equal((ushort)7, result.Record.SequenceNumber);
        Assert.Equal((ushort)1, result.Record.LinkCount);
        Assert.Equal((ushort)1, result.Record.Flags);
        Assert.Equal((uint)1024, result.Record.BytesAllocated);
        Assert.Equal((uint)9, result.Record.RecordNumber);
        Assert.Equal((ushort)0xAAAA, BinaryPrimitives.ReadUInt16LittleEndian(result.Record.FixedUpBytes.AsSpan(510, 2)));
        Assert.Equal((ushort)0xBBBB, BinaryPrimitives.ReadUInt16LittleEndian(result.Record.FixedUpBytes.AsSpan(1022, 2)));
        Assert.Equal((ushort)0x5A5A, BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(510, 2)));
        Assert.Equal((ushort)0x5A5A, BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(1022, 2)));
    }

    [Fact]
    public void RejectsWrongFileSignature()
    {
        var raw = CreateRecord();
        raw[0] = (byte)'X';
        var result = NtfsFileRecordParser.Parse(raw, 512);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsIncorrectUpdateSequenceCount()
    {
        var raw = CreateRecord();
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(6, 2), 2);
        var result = NtfsFileRecordParser.Parse(raw, 512);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("count", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsSectorTrailerMismatchWithoutReturningPartiallyFixedRecord()
    {
        var raw = CreateRecord();
        raw[510] = 0x00;
        var result = NtfsFileRecordParser.Parse(raw, 512);
        Assert.False(result.IsValid);
        Assert.Null(result.Record);
        Assert.Contains(result.Issues, x => x.Contains("fixup validation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsUpdateSequenceArrayOutsideRecord()
    {
        var raw = CreateRecord();
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(4, 2), 1020);
        var result = NtfsFileRecordParser.Parse(raw, 512);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("extends beyond", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsInvalidFirstAttributeOffset()
    {
        var raw = CreateRecord();
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(20, 2), 48);
        var result = NtfsFileRecordParser.Parse(raw, 512);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("first-attribute", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] CreateRecord()
    {
        var raw = new byte[1024];
        Encoding.ASCII.GetBytes("FILE").CopyTo(raw, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(4, 2), 48);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(6, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(16, 2), 7);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(18, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(20, 2), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(24, 4), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(28, 4), 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(44, 4), 9);

        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(48, 2), 0x5A5A);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(50, 2), 0xAAAA);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(52, 2), 0xBBBB);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(510, 2), 0x5A5A);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(1022, 2), 0x5A5A);
        return raw;
    }
}
