using System.Buffers.Binary;
using System.Text;
using DiskRescue.Filesystems;
using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class NtfsPartitionScannerTests
{
    [Fact]
    public void FindsValidatedNtfsVolumeOutsidePartitionTablesAsLostCandidate()
    {
        using var image = SyntheticImage.Create();
        using var reader = FileImageReader.Open(image.Path);

        var result = NtfsPartitionScanner.Scan(reader, 512, maximumScanBytes: reader.Length);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(image.VolumeOffset, candidate.ByteOffset);
        Assert.Equal(image.VolumeLength, candidate.ByteLength);
        Assert.Equal("Lost partition candidate", candidate.Status);
        Assert.True(candidate.BackupBootSectorValid);
        Assert.Equal(95, candidate.Confidence);
        Assert.False(result.WasCancelled);
        Assert.Equal(reader.Length, result.BytesScanned);
    }

    [Fact]
    public void ClassifiesCandidateCoveredByKnownPartitionAsExisting()
    {
        using var image = SyntheticImage.Create();
        using var reader = FileImageReader.Open(image.Path);
        var known = new[] { new KnownPartitionRange("GPT", 1, image.VolumeOffset, image.VolumeLength) };

        var result = NtfsPartitionScanner.Scan(reader, 512, known, reader.Length);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Existing partition", candidate.Status);
        Assert.Equal("GPT #1", candidate.MatchingPartition);
        Assert.Equal(100, candidate.Confidence);
    }

    [Fact]
    public void DoesNotCallOverlappingCandidateLost()
    {
        using var image = SyntheticImage.Create();
        using var reader = FileImageReader.Open(image.Path);
        var known = new[] { new KnownPartitionRange("MBR", 2, image.VolumeOffset - 512, image.VolumeLength + 1024) };

        var result = NtfsPartitionScanner.Scan(reader, 512, known, reader.Length);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Overlaps known partition", candidate.Status);
        Assert.Null(candidate.MatchingPartition);
    }

    [Fact]
    public void StopsCleanlyWhenCancellationIsAlreadyRequested()
    {
        using var image = SyntheticImage.Create();
        using var reader = FileImageReader.Open(image.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = NtfsPartitionScanner.Scan(reader, 512, maximumScanBytes: reader.Length,
            cancellationToken: cancellation.Token);

        Assert.True(result.WasCancelled);
        Assert.Empty(result.Candidates);
        Assert.Equal(0, result.BytesScanned);
    }

    [Fact]
    public void RejectsSectorGeometryThatDoesNotMatchScanAlignment()
    {
        using var image = SyntheticImage.Create();
        using var reader = FileImageReader.Open(image.Path);

        var result = NtfsPartitionScanner.Scan(reader, 4096, maximumScanBytes: reader.Length);

        Assert.Empty(result.Candidates);
    }

    private sealed class SyntheticImage(string path, long volumeOffset, long volumeLength) : IDisposable
    {
        public string Path { get; } = path;
        public long VolumeOffset { get; } = volumeOffset;
        public long VolumeLength { get; } = volumeLength;

        public static SyntheticImage Create()
        {
            const int sector = 512;
            const int volumeSectors = 128;
            const int volumeBytes = sector * volumeSectors;
            const int volumeOffset = 4096;
            var bytes = new byte[volumeOffset + volumeBytes + 4096];

            WriteBootSector(bytes.AsSpan(volumeOffset, sector), sector, volumeSectors);
            WriteBootSector(bytes.AsSpan(volumeOffset + volumeBytes - sector, sector), sector, volumeSectors);

            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"diskrescue-scan-{Guid.NewGuid():N}.img");
            File.WriteAllBytes(path, bytes);
            return new SyntheticImage(path, volumeOffset, volumeBytes);
        }

        private static void WriteBootSector(Span<byte> boot, int bytesPerSector, int totalSectors)
        {
            Encoding.ASCII.GetBytes("NTFS    ").CopyTo(boot.Slice(3, 8));
            BinaryPrimitives.WriteUInt16LittleEndian(boot.Slice(11, 2), (ushort)bytesPerSector);
            boot[13] = 1;
            BinaryPrimitives.WriteUInt64LittleEndian(boot.Slice(40, 8), (ulong)totalSectors);
            BinaryPrimitives.WriteUInt64LittleEndian(boot.Slice(48, 8), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(boot.Slice(56, 8), 2);
            boot[64] = unchecked((byte)(sbyte)-10);
            boot[68] = unchecked((byte)(sbyte)-10);
            BinaryPrimitives.WriteUInt64LittleEndian(boot.Slice(72, 8), 0xAABBCCDDEEFF0011);
            boot[510] = 0x55;
            boot[511] = 0xAA;
        }

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
