using System.Buffers.Binary;
using System.Text;
using DiskRescue.Filesystems;
using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class NtfsBootSectorParserTests
{
    [Fact]
    public void ParsesValidNtfsBootSectorAndCalculatesMftOffsets()
    {
        using var image = CreateNtfsImage();
        using var reader = FileImageReader.Open(image.Path);
        var result = NtfsBootSectorParser.Parse(reader, 0, image.Length);
        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.BootSector);
        Assert.Equal((ushort)512, result.BootSector.BytesPerSector);
        Assert.Equal((byte)8, result.BootSector.SectorsPerCluster);
        Assert.Equal(4096u, result.BootSector.ClusterSizeBytes);
        Assert.Equal(1024u, result.BootSector.FileRecordSizeBytes);
        Assert.Equal(4L * 4096, result.BootSector.MftByteOffset);
        Assert.Equal(8L * 4096, result.BootSector.MftMirrorByteOffset);
    }

    [Fact]
    public void RejectsNonNtfsOemIdentifier()
    {
        using var image = CreateNtfsImage();
        var bytes = File.ReadAllBytes(image.Path);
        bytes[3] = (byte)'F';
        File.WriteAllBytes(image.Path, bytes);
        using var reader = FileImageReader.Open(image.Path);
        var result = NtfsBootSectorParser.Parse(reader, 0, reader.Length);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("OEM", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsInvalidClusterGeometry()
    {
        using var image = CreateNtfsImage();
        var bytes = File.ReadAllBytes(image.Path);
        bytes[13] = 3;
        File.WriteAllBytes(image.Path, bytes);
        using var reader = FileImageReader.Open(image.Path);
        var result = NtfsBootSectorParser.Parse(reader, 0, reader.Length);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("sectors-per-cluster", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsMftClusterOutsideVolume()
    {
        using var image = CreateNtfsImage();
        var bytes = File.ReadAllBytes(image.Path);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48, 8), 9999);
        File.WriteAllBytes(image.Path, bytes);
        using var reader = FileImageReader.Open(image.Path);
        var result = NtfsBootSectorParser.Parse(reader, 0, reader.Length);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("MFT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsVolumeRangeOutsideImage()
    {
        using var image = CreateNtfsImage();
        using var reader = FileImageReader.Open(image.Path);
        var result = NtfsBootSectorParser.Parse(reader, 4096, reader.Length);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Contains("beyond the image", StringComparison.OrdinalIgnoreCase));
    }

    private static SyntheticNtfsImage CreateNtfsImage()
    {
        const int bytesPerSector = 512;
        const int sectorsPerCluster = 8;
        const int totalSectors = 512;
        var bytes = new byte[bytesPerSector * totalSectors];
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(bytes, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(11, 2), bytesPerSector);
        bytes[13] = sectorsPerCluster;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40, 8), totalSectors);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48, 8), 4);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(56, 8), 8);
        bytes[64] = unchecked((byte)(sbyte)-10);
        bytes[68] = unchecked((byte)(sbyte)-10);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(72, 8), 0x123456789ABCDEF0);
        bytes[510] = 0x55;
        bytes[511] = 0xAA;
        var path = Path.Combine(Path.GetTempPath(), $"diskrescue-ntfs-{Guid.NewGuid():N}.img");
        File.WriteAllBytes(path, bytes);
        return new SyntheticNtfsImage(path, bytes.Length);
    }

    private sealed class SyntheticNtfsImage(string path, long length) : IDisposable
    {
        public string Path { get; } = path;
        public long Length { get; } = length;
        public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
    }
}
