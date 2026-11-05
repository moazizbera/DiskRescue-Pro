using System.Buffers.Binary;
using DiskRescue.Filesystems;
using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class NtfsMftReaderTests
{
    [Fact]
    public void OpensMftFromRecordZeroAndReadsAnotherRecordThroughValidatedRunlist()
    {
        using var image = CreateImage();
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());

        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));
        Assert.NotNull(opened.Reader);
        Assert.Equal((ulong)4096, opened.Reader.InitializedSize);

        var result = opened.Reader.ReadRecord(1);
        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.NotNull(result.Record);
        Assert.Equal((uint)1, result.Record.RecordNumber);
        Assert.Equal((ushort)9, result.Record.SequenceNumber);
    }

    [Fact]
    public void RejectsRecordOutsideInitializedMftData()
    {
        using var image = CreateImage();
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());
        Assert.True(opened.IsValid, string.Join("; ", opened.Issues));

        var result = opened.Reader!.ReadRecord(4);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("initialized", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsMftBaseRecordWithWrongRecordNumber()
    {
        using var image = CreateImage(recordZeroNumber: 99);
        var opened = NtfsMftReader.Open(image, 0, image.Length, CreateBootSector());

        Assert.False(opened.IsValid);
        Assert.Contains(opened.Issues, x => x.Contains("record 0", StringComparison.OrdinalIgnoreCase));
    }

    private static NtfsBootSector CreateBootSector() =>
        new(512, 1, 512, 128, 4, 8, 1024, 1024, 123, 2048, 4096);

    private static MemoryImage CreateImage(uint recordZeroNumber = 0)
    {
        var bytes = new byte[128 * 512];
        CreateFileRecord(recordZeroNumber, includeMftDataAttribute: true).CopyTo(bytes, 4 * 512);
        CreateFileRecord(1, includeMftDataAttribute: false).CopyTo(bytes, 6 * 512);
        CreateFileRecord(2, includeMftDataAttribute: false).CopyTo(bytes, 4 * 512 + 2 * 1024);
        CreateFileRecord(3, includeMftDataAttribute: false).CopyTo(bytes, 4 * 512 + 3 * 1024);
        return new MemoryImage(bytes);
    }

    private static byte[] CreateFileRecord(uint recordNumber, bool includeMftDataAttribute)
    {
        var raw = new byte[1024];
        "FILE"u8.CopyTo(raw);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(4, 2), 48);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(6, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(16, 2), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(18, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(20, 2), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(28, 4), 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(44, 4), recordNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(48, 2), 0x5A5A);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(50, 2), 0xAAAA);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(52, 2), 0xBBBB);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(510, 2), 0x5A5A);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(1022, 2), 0x5A5A);

        if (includeMftDataAttribute)
        {
            // Unnamed non-resident $DATA, one extent covering VCN 0..7 at LCN 4.
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(56, 4), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(60, 4), 72);
            raw[64] = 1;
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(72, 8), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(80, 8), 7);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(88, 2), 64);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(96, 8), 4096);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(104, 8), 4096);
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(112, 8), 4096);
            raw[120] = 0x11; raw[121] = 0x08; raw[122] = 0x04; raw[123] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(128, 4), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(24, 4), 132);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(56, 4), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(24, 4), 60);
        }
        return raw;
    }

    private sealed class MemoryImage(byte[] bytes) : IReadOnlyRandomAccessSource
    {
        public long Length => bytes.LongLength;
        public void ReadExactlyAt(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset > bytes.LongLength || destination.Length > bytes.LongLength - offset)
                throw new EndOfStreamException();
            bytes.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
        }
        public void Dispose() { }
    }
}
