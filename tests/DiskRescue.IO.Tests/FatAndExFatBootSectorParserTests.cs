using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using DiskRescue.Filesystems;
using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class FatAndExFatBootSectorParserTests
{
    [Fact]
    public void AcceptsConsistentFat32Geometry()
    {
        const int bps = 512;
        const uint totalSectors = 70000;
        const uint sectorsPerFat = 600;
        var bytes = new byte[checked((int)totalSectors * bps)];
        var boot = bytes.AsSpan(0, bps);
        boot[0] = 0xEB; boot[2] = 0x90;
        BinaryPrimitives.WriteUInt16LittleEndian(boot.Slice(11, 2), bps);
        boot[13] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(boot.Slice(14, 2), 32);
        boot[16] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(32, 4), totalSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(36, 4), sectorsPerFat);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(44, 4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(boot.Slice(48, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(boot.Slice(50, 2), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(67, 4), 0x12345678);
        boot[510] = 0x55; boot[511] = 0xAA;

        using var source = new MemorySource(bytes);
        var result = Fat32BootSectorParser.Parse(source, 0, bytes.Length);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal(totalSectors, result.BootSector!.TotalSectors);
        Assert.True(result.BootSector.ClusterCount >= 65525);
    }

    [Fact]
    public void RejectsFat32FatTooSmallForDeclaredClusters()
    {
        var bytes = BuildFat32Image(sectorsPerFat: 1);
        using var source = new MemorySource(bytes);
        var result = Fat32BootSectorParser.Parse(source, 0, bytes.Length);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void AcceptsMainAndBackupExFatBootRegionsWithMatchingChecksums()
    {
        var bytes = BuildExFatImage();
        using var source = new MemorySource(bytes);
        var result = ExFatBootSectorParser.Parse(source, 0, bytes.Length);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.True(result.BootSector!.BackupBootRegionValid);
        Assert.Equal(512, result.BootSector.BytesPerSector);
        Assert.Equal(26UL, result.BootSector.VolumeLengthSectors);
    }

    [Fact]
    public void RejectsExFatBootRegionWithCorruptChecksum()
    {
        var bytes = BuildExFatImage();
        bytes[11 * 512] ^= 0x01;
        using var source = new MemorySource(bytes);
        var result = ExFatBootSectorParser.Parse(source, 0, bytes.Length);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void RejectsExFatClusterHeapOutsideVolume()
    {
        var bytes = BuildExFatImage();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(88, 4), 26);
        RecalculateBootChecksum(bytes, 0);
        using var source = new MemorySource(bytes);
        var result = ExFatBootSectorParser.Parse(source, 0, bytes.Length);
        Assert.False(result.IsValid);
    }

    private static byte[] BuildFat32Image(uint sectorsPerFat)
    {
        const uint total = 70000;
        var bytes = new byte[checked((int)total * 512)];
        var b = bytes.AsSpan(0, 512);
        b[0] = 0xEB; b[2] = 0x90;
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(11, 2), 512);
        b[13] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(14, 2), 32);
        b[16] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(32, 4), total);
        BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(36, 4), sectorsPerFat);
        BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(44, 4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(48, 2), 1);
        b[510] = 0x55; b[511] = 0xAA;
        return bytes;
    }

    private static byte[] BuildExFatImage()
    {
        const int bps = 512;
        var bytes = new byte[26 * bps];
        for (var copy = 0; copy < 2; copy++)
        {
            var start = copy * 12 * bps;
            var boot = bytes.AsSpan(start, bps);
            boot[0] = 0xEB; boot[1] = 0x76; boot[2] = 0x90;
            Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(boot.Slice(3, 8));
            BinaryPrimitives.WriteUInt64LittleEndian(boot.Slice(72, 8), 26);
            BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(80, 4), 24);
            BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(84, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(88, 4), 25);
            BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(92, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(96, 4), 2);
            boot[108] = 9; boot[109] = 0; boot[110] = 1; boot[112] = 0xFF;
            boot[510] = 0x55; boot[511] = 0xAA;
            for (var sector = 1; sector <= 8; sector++)
            {
                var s = bytes.AsSpan(start + sector * bps, bps);
                s[510] = 0x55; s[511] = 0xAA;
            }
            RecalculateBootChecksum(bytes, start);
        }
        return bytes;
    }

    private static void RecalculateBootChecksum(byte[] bytes, int start)
    {
        uint sum = 0;
        for (var i = 0; i < 11 * 512; i++)
        {
            if (i is 106 or 107 or 112) continue;
            sum = BitOperations.RotateRight(sum, 1) + bytes[start + i];
        }
        for (var i = 0; i < 512; i += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(start + 11 * 512 + i, 4), sum);
    }

    private sealed class MemorySource(byte[] data) : IReadOnlyRandomAccessSource
    {
        public long Length => data.Length;
        public void ReadExactlyAt(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset > data.Length || destination.Length > data.Length - offset)
                throw new EndOfStreamException();
            data.AsSpan((int)offset, destination.Length).CopyTo(destination);
        }
        public void Dispose() { }
    }
}
