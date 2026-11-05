using System.Buffers.Binary;
using DiskRescue.Filesystems;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class NtfsAttributeParserTests
{
    [Fact]
    public void ParsesResidentAttributeAndEndMarker()
    {
        var record = CreateRecord();
        var parsedRecord = NtfsFileRecordParser.Parse(record, 512);
        Assert.True(parsedRecord.IsValid);
        var result = NtfsAttributeParser.Parse(parsedRecord.Record!);
        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        var attribute = Assert.Single(result.Attributes);
        Assert.Equal(0x10u, attribute.Type);
        Assert.False(attribute.IsNonResident);
        Assert.Equal(4u, attribute.ResidentValueLength);
    }

    [Fact]
    public void RejectsAttributeLengthOutsideBytesInUse()
    {
        var record = CreateRecord();
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(60, 4), 0x1000);
        var parsedRecord = NtfsFileRecordParser.Parse(record, 512);
        Assert.True(parsedRecord.IsValid);
        var result = NtfsAttributeParser.Parse(parsedRecord.Record!);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("Attribute length", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsResidentValueOutsideAttribute()
    {
        var record = CreateRecord();
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(72, 4), 200);
        var parsedRecord = NtfsFileRecordParser.Parse(record, 512);
        Assert.True(parsedRecord.IsValid);
        var result = NtfsAttributeParser.Parse(parsedRecord.Record!);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("Resident attribute value", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] CreateRecord()
    {
        var raw = new byte[1024];
        "FILE"u8.CopyTo(raw);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(4, 2), 48);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(6, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(20, 2), 56);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(24, 4), 92);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(28, 4), 1024);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(48, 2), 0x5A5A);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(50, 2), 0xAAAA);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(52, 2), 0xBBBB);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(510, 2), 0x5A5A);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(1022, 2), 0x5A5A);

        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(56, 4), 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(60, 4), 32);
        raw[64] = 0; // resident
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(72, 4), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(76, 2), 24);
        raw[78] = 0;
        raw[79] = 0;
        "DATA"u8.CopyTo(raw.AsSpan(80, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(88, 4), 0xFFFFFFFF);
        return raw;
    }
}
