using System.Buffers.Binary;
using DiskRescue.IO;
using DiskRescue.Partitions;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class MbrParserTests
{
    [Fact]
    public void ParsesValidPartitionAndCalculatesByteRange()
    {
        using var image = new MbrImage(4096);
        image.AddPartition(0, 0x83, 1, 4);
        using var reader = FileImageReader.Open(image.Path);

        var result = MbrParser.Parse(reader);

        Assert.True(result.HasValidSignature);
        var partition = Assert.Single(result.Partitions);
        Assert.Equal(0x83, partition.PartitionType);
        Assert.Equal(512, partition.ByteOffset);
        Assert.Equal(2048, partition.ByteLength);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void RejectsPartitionThatExtendsPastImage()
    {
        using var image = new MbrImage(4096);
        image.AddPartition(0, 0x07, 7, 2);
        using var reader = FileImageReader.Open(image.Path);

        var result = MbrParser.Parse(reader);

        Assert.Empty(result.Partitions);
        Assert.Contains(result.Issues, issue => issue.Contains("image boundary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReportsInvalidBootIndicator()
    {
        using var image = new MbrImage(4096);
        image.AddPartition(0, 0x83, 1, 2, boot: 0x7F);
        using var reader = FileImageReader.Open(image.Path);

        var result = MbrParser.Parse(reader);

        Assert.Empty(result.Partitions);
        Assert.Contains(result.Issues, issue => issue.Contains("boot indicator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DetectsProtectiveGptEntry()
    {
        using var image = new MbrImage(4096);
        image.AddPartition(0, 0xEE, 1, 7);
        using var reader = FileImageReader.Open(image.Path);

        var result = MbrParser.Parse(reader);

        Assert.True(result.IsProtectiveMbr);
    }

    [Fact]
    public void RejectsUnsupportedSectorSize()
    {
        using var image = new MbrImage(4096);
        using var reader = FileImageReader.Open(image.Path);

        Assert.Throws<ArgumentOutOfRangeException>(() => MbrParser.Parse(reader, 1000));
    }

    private sealed class MbrImage : IDisposable
    {
        private readonly byte[] _bytes;
        public MbrImage(int length)
        {
            _bytes = new byte[length];
            _bytes[510] = 0x55;
            _bytes[511] = 0xAA;
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"diskrescue-mbr-{Guid.NewGuid():N}.img");
            File.WriteAllBytes(Path, _bytes);
        }

        public string Path { get; }

        public void AddPartition(int index, byte type, uint startLba, uint sectors, byte boot = 0)
        {
            var offset = 446 + index * 16;
            _bytes[offset] = boot;
            _bytes[offset + 4] = type;
            BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(offset + 8, 4), startLba);
            BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(offset + 12, 4), sectors);
            File.WriteAllBytes(Path, _bytes);
        }

        public void Dispose() => File.Delete(Path);
    }
}
