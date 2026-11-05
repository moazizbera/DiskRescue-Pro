using System.Buffers.Binary;
using System.Text;
using DiskRescue.Filesystems;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class NtfsFileNameParserTests
{
    [Fact]
    public void ParsesParentReferenceNameAndFileMetadata()
    {
        var value = new byte[66 + 12];
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0, 8), (2UL << 48) | 5UL);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8, 8), 1001);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(16, 8), 1002);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(24, 8), 1003);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(32, 8), 1004);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(40, 8), 4096);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(48, 8), 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(56, 4), 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(60, 4), 0);
        value[64] = 6;
        value[65] = 1;
        Encoding.Unicode.GetBytes("Report").CopyTo(value, 66);

        var result = NtfsFileNameParser.Parse(CreateAttribute(value));
        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.NotNull(result.FileName);
        Assert.Equal("Report", result.FileName.Name);
        Assert.Equal((ulong)5, result.FileName.ParentRecordNumber);
        Assert.Equal((ushort)2, result.FileName.ParentSequenceNumber);
        Assert.Equal((ulong)2048, result.FileName.RealSize);
        Assert.Equal((uint)0x20, result.FileName.FileAttributes);
        Assert.Equal((byte)1, result.FileName.Namespace);
    }

    [Fact]
    public void RejectsNameLengthMismatch()
    {
        var value = new byte[66 + 4];
        value[64] = 3;
        value[65] = 1;
        Encoding.Unicode.GetBytes("AB").CopyTo(value, 66);

        var result = NtfsFileNameParser.Parse(CreateAttribute(value));
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("length", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsInvalidNamespace()
    {
        var value = new byte[68];
        value[64] = 1;
        value[65] = 4;
        Encoding.Unicode.GetBytes("A").CopyTo(value, 66);

        var result = NtfsFileNameParser.Parse(CreateAttribute(value));
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("namespace", StringComparison.OrdinalIgnoreCase));
    }

    private static NtfsAttribute CreateAttribute(byte[] value) =>
        new(0x30, (uint)(24 + value.Length), false, 1, 0, string.Empty,
            (uint)value.Length, 24, null, null, null, null, null, null, null, value);
}
