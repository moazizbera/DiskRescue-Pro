using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using DiskRescue.Filesystems;
using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class FatPartitionScannerTests
{
    [Fact]
    public void DiscoversFat32CandidateWithCorroboratingBackupSector()
    {
        var image = BuildFat32Image();
        using var source = new MemorySource(image);
        var result = FatPartitionScanner.Scan(source, 512, maximumScanBytes: source.Length);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("FAT32", candidate.FileSystem);
        Assert.Equal(0L, candidate.ByteOffset);
        Assert.Equal(70000L * 512, candidate.ByteLength);
        Assert.Equal("Lost partition candidate", candidate.Status);
        Assert.True(candidate.BackupBootRegionValid);
        Assert.Equal(95, candidate.Confidence);
        Assert.Equal(source.Length, result.BytesScanned);
    }

    [Fact]
    public void DiscoversExFatCandidateAndReportsBackupEvidence()
    {
        var image = BuildExFatImage();
        using var source = new MemorySource(image);
        var result = FatPartitionScanner.Scan(source, 512, maximumScanBytes: source.Length);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("exFAT", candidate.FileSystem);
        Assert.Equal(0L, candidate.ByteOffset);
        Assert.Equal(26L * 512, candidate.ByteLength);
        Assert.True(candidate.BackupBootRegionValid);
        Assert.Equal(95, candidate.Confidence);
    }

    [Fact]
    public void DoesNotLabelOverlappingFatCandidateAsLost()
    {
        var image = BuildExFatImage();
        using var source = new MemorySource(image);
        var known = new[] { new KnownPartitionRange("GPT", 2, 512, 26L * 512) };

        var result = FatPartitionScanner.Scan(source, 512, known, source.Length);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Overlaps known partition", candidate.Status);
        Assert.Null(candidate.MatchingPartition);
    }

    [Fact]
    public void HonorsCancellationBeforeReadingImage()
    {
        var image = BuildExFatImage();
        using var source = new MemorySource(image);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = FatPartitionScanner.Scan(source, 512,
            maximumScanBytes: source.Length, cancellationToken: cancellation.Token);

        Assert.True(result.WasCancelled);
        Assert.Empty(result.Candidates);
        Assert.Equal(0, result.BytesScanned);
    }

    private static byte[] BuildFat32Image()
    {
        const int sectorSize = 512;
        const uint totalSectors = 70000;
        const int volumeBytes = checked((int)totalSectors * sectorSize);
        var bytes = new byte[volumeBytes + 4096];
        WriteFat32Boot(bytes.AsSpan(0, sectorSize));
        WriteFat32Boot(bytes.AsSpan(6 * sectorSize, sectorSize));
        return bytes;
    }

    private static void WriteFat32Boot(Span<byte> boot)
    {
        boot[0] = 0xEB; boot[2] = 0x90;
        BinaryPrimitives.WriteUInt16LittleEndian(boot.Slice(11, 2), 512);
        boot[13] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(boot.Slice(14, 2), 32);
        boot[16] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(32, 4), 70000);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(36, 4), 600);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(44, 4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(boot.Slice(48, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(boot.Slice(50, 2), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.Slice(67, 4), 0x12345678);
        boot[510] = 0x55; boot[511] = 0xAA;
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
                var extended = bytes.AsSpan(start + sector * bps, bps);
                extended[510] = 0x55; extended[511] = 0xAA;
            }
            RecalculateExFatChecksum(bytes, start);
        }
        return bytes;
    }

    private static void RecalculateExFatChecksum(byte[] bytes, int start)
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
            data.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
        }
        public void Dispose() { }
    }
}
