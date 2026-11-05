using DiskRescue.IO;
using DiskRescue.Partitions;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class MbrParserTests
{
    [Fact]
    public void ParsesValidPartitionAndCalculatesByteRange()
    {
        using var image = new SyntheticDiskImageBuilder(totalSectors: 8);
        image.AddMbrPartition(0, 0x83, 1, 4);
        image.WriteMbr();
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
    public void DoesNotTrustEntriesWhenSignatureIsMissing()
    {
        using var image = new SyntheticDiskImageBuilder(totalSectors: 8);
        image.AddMbrPartition(0, 0x83, 1, 2);
        image.WriteMbr(validSignature: false);
        using var reader = FileImageReader.Open(image.Path);
        var result = MbrParser.Parse(reader);
        Assert.False(result.HasValidSignature);
        Assert.Empty(result.Partitions);
    }

    [Fact]
    public void RejectsPartitionThatExtendsPastImage()
    {
        using var image = new SyntheticDiskImageBuilder(totalSectors: 8);
        image.AddMbrPartition(0, 0x07, 7, 2);
        image.WriteMbr();
        using var reader = FileImageReader.Open(image.Path);
        var result = MbrParser.Parse(reader);
        Assert.Empty(result.Partitions);
        Assert.Contains(result.Issues, issue => issue.Contains("image boundary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReportsInvalidBootIndicator()
    {
        using var image = new SyntheticDiskImageBuilder(totalSectors: 8);
        image.AddMbrPartition(0, 0x83, 1, 2, bootIndicator: 0x7F);
        image.WriteMbr();
        using var reader = FileImageReader.Open(image.Path);
        var result = MbrParser.Parse(reader);
        Assert.Empty(result.Partitions);
        Assert.Contains(result.Issues, issue => issue.Contains("boot indicator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DetectsProtectiveGptEntry()
    {
        using var image = new SyntheticDiskImageBuilder(totalSectors: 8);
        image.AddMbrPartition(0, 0xEE, 1, 7);
        image.WriteMbr();
        using var reader = FileImageReader.Open(image.Path);
        Assert.True(MbrParser.Parse(reader).IsProtectiveMbr);
    }

    [Fact]
    public void AcceptsSaturatedProtectiveMbrEntryWithoutTreatingItAsPartition()
    {
        using var image = new SyntheticDiskImageBuilder(totalSectors: 100);
        image.AddMbrPartition(0, 0xEE, 1, uint.MaxValue);
        image.WriteMbr();
        using var reader = FileImageReader.Open(image.Path);
        var result = MbrParser.Parse(reader);
        Assert.True(result.IsProtectiveMbr);
        Assert.Empty(result.Partitions);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void UsesLogicalSectorSizeForLbaConversion()
    {
        using var image = new SyntheticDiskImageBuilder(totalSectors: 8, sectorSize: 4096);
        image.AddMbrPartition(0, 0x83, 1, 1);
        image.WriteMbr();
        using var reader = FileImageReader.Open(image.Path);
        var result = MbrParser.Parse(reader, 4096);
        Assert.Equal(4096, Assert.Single(result.Partitions).ByteOffset);
    }

    [Fact]
    public void ReportsOverlappingPartitions()
    {
        using var image = new SyntheticDiskImageBuilder(totalSectors: 16);
        image.AddMbrPartition(0, 0x83, 1, 4);
        image.AddMbrPartition(1, 0x07, 3, 2);
        image.WriteMbr();
        using var reader = FileImageReader.Open(image.Path);
        var result = MbrParser.Parse(reader);
        Assert.Equal(2, result.Partitions.Count);
        Assert.Contains(result.Issues, issue => issue.Contains("overlap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsUnsupportedSectorSize()
    {
        using var image = new SyntheticDiskImageBuilder(totalSectors: 8);
        image.WriteMbr();
        using var reader = FileImageReader.Open(image.Path);
        Assert.Throws<ArgumentOutOfRangeException>(() => MbrParser.Parse(reader, 1000));
    }
}
