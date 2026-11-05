using System.Buffers.Binary;
using System.Text;
using DiskRescue.IO;
using DiskRescue.Partitions;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class GptParserTests
{
    [Fact]
    public void ParsesPrimaryAndBackupHeadersAndPartition()
    {
        using var image = new GptImage();
        image.AddPartition(0, 34, 40, "Data");
        image.WriteBothCopies();
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
        using var image = new GptImage();
        image.AddPartition(0, 34, 40, "Data");
        image.WriteBothCopies();
        image.CorruptByte(512 + 16);
        using var reader = FileImageReader.Open(image.Path);
        var result = GptParser.ParseAtLba(reader, 512, 1);
        Assert.False(result.HeaderValid);
        Assert.Contains(result.Issues, issue => issue.Contains("header CRC32", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsPartitionArrayWithBadCrc()
    {
        using var image = new GptImage();
        image.AddPartition(0, 34, 40, "Data");
        image.WriteBothCopies();
        image.CorruptByte(2 * 512 + 10);
        using var reader = FileImageReader.Open(image.Path);
        var result = GptParser.ParseAtLba(reader, 512, 1);
        Assert.False(result.HeaderValid);
        Assert.Contains(result.Issues, issue => issue.Contains("array CRC32", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReportsOverlappingPartitions()
    {
        using var image = new GptImage();
        image.AddPartition(0, 34, 45, "First");
        image.AddPartition(1, 40, 50, "Second");
        image.WriteBothCopies();
        using var reader = FileImageReader.Open(image.Path);
        var result = GptParser.ParseAtLba(reader, 512, 1);
        Assert.Equal(2, result.Partitions.Count);
        Assert.Contains(result.Issues, issue => issue.Contains("overlap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsEntryCountAboveConfiguredLimit()
    {
        using var image = new GptImage();
        image.WriteBothCopies(entryCount: 16385);
        using var reader = FileImageReader.Open(image.Path);
        var result = GptParser.ParseAtLba(reader, 512, 1);
        Assert.False(result.HeaderValid);
        Assert.Contains(result.Issues, issue => issue.Contains("safe limits", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class GptImage : IDisposable
    {
        private const int SectorSize = 512;
        private const int TotalSectors = 100;
        private const int EntryCount = 4;
        private const int EntrySize = 128;
        private readonly byte[] _bytes = new byte[SectorSize * TotalSectors];
        private readonly byte[] _entries = new byte[EntryCount * EntrySize];
        private readonly Guid _diskGuid = Guid.NewGuid();

        public GptImage() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"diskrescue-gpt-{Guid.NewGuid():N}.img");
        public string Path { get; }

        public void AddPartition(int index, ulong first, ulong last, string name)
        {
            var entry = _entries.AsSpan(index * EntrySize, EntrySize);
            Guid.NewGuid().ToByteArray().AsSpan().CopyTo(entry[..16]);
            Guid.NewGuid().ToByteArray().AsSpan().CopyTo(entry.Slice(16, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(32, 8), first);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(40, 8), last);
            Encoding.Unicode.GetBytes(name).AsSpan().CopyTo(entry.Slice(56, 72));
        }

        public void WriteBothCopies(uint entryCount = EntryCount)
        {
            var entriesCrc = ComputeCrc32(_entries);
            _entries.CopyTo(_bytes, 2 * SectorSize);
            _entries.CopyTo(_bytes, 98 * SectorSize);
            WriteHeader(1, 99, 2, entriesCrc, entryCount);
            WriteHeader(99, 1, 98, entriesCrc, entryCount);
            File.WriteAllBytes(Path, _bytes);
        }

        private void WriteHeader(ulong current, ulong backup, ulong entriesLba, uint entriesCrc, uint entryCount)
        {
            var header = _bytes.AsSpan((int)current * SectorSize, SectorSize);
            Encoding.ASCII.GetBytes("EFI PART").AsSpan().CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), 0x00010000);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(12, 4), 92);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(24, 8), current);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(32, 8), backup);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(40, 8), 34);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(48, 8), 66);
            _diskGuid.ToByteArray().AsSpan().CopyTo(header.Slice(56, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(72, 8), entriesLba);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(80, 4), entryCount);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(84, 4), EntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(88, 4), entriesCrc);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), ComputeCrc32(header[..92]));
        }

        public void CorruptByte(int offset)
        {
            _bytes[offset] ^= 0x5A;
            File.WriteAllBytes(Path, _bytes);
        }

        public void Dispose() => File.Delete(Path);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
