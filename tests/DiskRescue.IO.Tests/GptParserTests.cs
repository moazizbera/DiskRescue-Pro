using DiskRescue.IO;
using DiskRescue.Partitions;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class GptParserTests
{
    [Fact]
    public void ParsesPrimaryAndBackupHeadersAndPartition()
    {
        using var image = new SyntheticDiskImageBuilder();
        image.AddGptPartition(0, 34, 40, "Data");
        image.WriteGptCopies();
        using var reader = FileImageReader.Open(image.Path);
        var result = GptParser.ParsePrimaryAndBackup(reader);
        Assert.True(result.Primary.HeaderValid);
        Assert.True(result.Backup.HeaderValid);
        Assert.Empty(result.ConsistencyIssues);
        var partition = Assert.Single(result.Primary.Partitions);
        Assert.Equal("Data", partition.Name);
        Assert.Equal(34UL * 512, (ulong)partition.ByteOffset);
        Assert.Equal(7UL * 512, (ulong)partition.ByteLength);
    }

    [Fact]
    public void RejectsHeaderWithBadCrc()
    {
        using var image = new SyntheticDiskImageBuilder();
        image.AddGptPartition(0, 34, 40, "Data");
        image.WriteGptCopies();
        image.CorruptByte(512 + 16);
        using var reader = FileImageReader.Open(image.Path);
        var result = GptParser.ParseAtLba(reader, 512, 1);
        Assert.False(result.HeaderValid);
        Assert.Contains(result.Issues, issue => issue.Contains("header CRC32", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsPartitionArrayWithBadCrc()
    {
        using var image = new SyntheticDiskImageBuilder();
        image.AddGptPartition(0, 34, 40, "Data");
        image.WriteGptCopies();
        image.CorruptByte(2 * 512 + 10);
        using var reader = FileImageReader.Open(image.Path);
        var result = GptParser.ParseAtLba(reader, 512, 1);
        Assert.False(result.HeaderValid);
        Assert.Contains(result.Issues, issue => issue.Contains("array CRC32", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReportsOverlappingPartitions()
    {
        using var image = new SyntheticDiskImageBuilder();
        image.AddGptPartition(0, 34, 45, "First");
        image.AddGptPartition(1, 40, 50, "Second");
        image.WriteGptCopies();
        using var reader = FileImageReader.Open(image.Path);
        var result = GptParser.ParseAtLba(reader, 512, 1);
        Assert.Equal(2, result.Partitions.Count);
        Assert.Contains(result.Issues, issue => issue.Contains("overlap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsEntryCountAboveConfiguredLimit()
    {
        using var image = new SyntheticDiskImageBuilder();
        image.WriteGptCopies(declaredEntryCount: 16385);
        using var reader = FileImageReader.Open(image.Path);
        var result = GptParser.ParseAtLba(reader, 512, 1);
        Assert.False(result.HeaderValid);
        Assert.Contains(result.Issues, issue => issue.Contains("safe limits", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReportsPrimaryBackupDiskGuidMismatch()
    {
        using var image = new SyntheticDiskImageBuilder();
        image.WriteGptCopies();
        // Change the backup disk GUID, then recalculate its header CRC so this is a valid but inconsistent copy.
        var bytes = File.ReadAllBytes(image.Path);
        bytes.AsSpan(99 * 512 + 56, 16).Clear();
        bytes.AsSpan(99 * 512 + 56, 16)[0] = 0x42;
        var header = bytes.AsSpan(99 * 512, 92);
        header.Slice(16, 4).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), ComputeCrc32(header));
        File.WriteAllBytes(image.Path, bytes);
        using var reader = FileImageReader.Open(image.Path);
        var result = GptParser.ParsePrimaryAndBackup(reader);
        Assert.True(result.Primary.HeaderValid);
        Assert.True(result.Backup.HeaderValid);
        Assert.Contains(result.ConsistencyIssues, issue => issue.Contains("disk GUID", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsTruncatedAndNonSectorAlignedImages()
    {
        using var image = new SyntheticDiskImageBuilder(totalSectors: 8);
        image.WriteGptCopies();
        var bytes = File.ReadAllBytes(image.Path);
        File.WriteAllBytes(image.Path, bytes[..^1]);
        using var reader = FileImageReader.Open(image.Path);
        var result = GptParser.ParseAtLba(reader, 512, 1);
        Assert.False(result.HeaderValid);
        Assert.Contains(result.Issues, issue => issue.Contains("multiple", StringComparison.OrdinalIgnoreCase));
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
