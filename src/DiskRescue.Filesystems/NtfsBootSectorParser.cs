using System.Buffers.Binary;
using System.Text;
using DiskRescue.IO;

namespace DiskRescue.Filesystems;

public sealed record NtfsBootSector(
    ushort BytesPerSector,
    byte SectorsPerCluster,
    uint ClusterSizeBytes,
    ulong TotalSectors,
    ulong MftCluster,
    ulong MftMirrorCluster,
    uint FileRecordSizeBytes,
    uint IndexBufferSizeBytes,
    ulong VolumeSerialNumber,
    long MftByteOffset,
    long MftMirrorByteOffset);

public sealed record NtfsBootSectorParseResult(
    bool IsValid,
    NtfsBootSector? BootSector,
    IReadOnlyList<string> Issues);

/// <summary>
/// Conservative NTFS volume boot-sector validator. It reads one sector only and
/// never mounts the volume or follows metadata pointers beyond the supplied range.
/// </summary>
public static class NtfsBootSectorParser
{
    private const int MinimumBootSectorBytes = 512;
    private const uint MaximumRecordBytes = 1024 * 1024;
    private static ReadOnlySpan<byte> OemId => "NTFS    "u8;

    public static NtfsBootSectorParseResult Parse(
        IReadOnlyRandomAccessSource source,
        long volumeOffset,
        long volumeLength)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (volumeOffset < 0 || volumeLength < MinimumBootSectorBytes)
            return Invalid("The supplied volume range is too small or has a negative offset.");

        long volumeEnd;
        try { volumeEnd = checked(volumeOffset + volumeLength); }
        catch (OverflowException) { return Invalid("The supplied volume range overflows."); }
        if (volumeEnd > source.Length)
            return Invalid("The supplied volume range extends beyond the image.");

        Span<byte> boot = stackalloc byte[MinimumBootSectorBytes];
        source.ReadExactlyAt(volumeOffset, boot);
        if (!boot.Slice(3, 8).SequenceEqual(OemId))
            return Invalid("NTFS OEM identifier is missing.");
        if (boot[510] != 0x55 || boot[511] != 0xAA)
            return Invalid("NTFS boot-sector signature 0x55AA is missing.");

        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(11, 2));
        if (bytesPerSector is not (512 or 1024 or 2048 or 4096))
            return Invalid("NTFS bytes-per-sector value is unsupported.");
        var sectorsPerCluster = boot[13];
        if (sectorsPerCluster == 0 || sectorsPerCluster > 128 ||
            (sectorsPerCluster & (sectorsPerCluster - 1)) != 0)
            return Invalid("NTFS sectors-per-cluster must be a power of two from 1 to 128.");

        uint clusterSize;
        try { clusterSize = checked((uint)bytesPerSector * sectorsPerCluster); }
        catch (OverflowException) { return Invalid("NTFS cluster size overflows."); }

        var totalSectors = BinaryPrimitives.ReadUInt64LittleEndian(boot.Slice(40, 8));
        var mftCluster = BinaryPrimitives.ReadUInt64LittleEndian(boot.Slice(48, 8));
        var mirrorCluster = BinaryPrimitives.ReadUInt64LittleEndian(boot.Slice(56, 8));
        var fileRecordCode = unchecked((sbyte)boot[64]);
        var indexBufferCode = unchecked((sbyte)boot[68]);
        var serial = BinaryPrimitives.ReadUInt64LittleEndian(boot.Slice(72, 8));

        if (totalSectors == 0) return Invalid("NTFS total-sector count is zero.");
        ulong volumeBytes;
        try { volumeBytes = checked(totalSectors * bytesPerSector); }
        catch (OverflowException) { return Invalid("NTFS volume byte length overflows."); }
        if (volumeBytes > (ulong)volumeLength)
            return Invalid("NTFS total-sector count exceeds the supplied volume boundary.");

        var totalClusters = totalSectors / sectorsPerCluster;
        if (mftCluster >= totalClusters || mirrorCluster >= totalClusters)
            return Invalid("NTFS MFT or MFT mirror cluster lies outside the volume.");

        if (!TryDecodeRecordSize(fileRecordCode, clusterSize, out var fileRecordSize))
            return Invalid("NTFS file-record size encoding is invalid or exceeds 1 MiB.");
        if (!TryDecodeRecordSize(indexBufferCode, clusterSize, out var indexBufferSize))
            return Invalid("NTFS index-buffer size encoding is invalid or exceeds 1 MiB.");

        try
        {
            var mftOffset = checked(volumeOffset + checked((long)(mftCluster * clusterSize)));
            var mirrorOffset = checked(volumeOffset + checked((long)(mirrorCluster * clusterSize)));
            if (mftOffset > volumeEnd - fileRecordSize || mirrorOffset > volumeEnd - fileRecordSize)
                return Invalid("NTFS MFT or MFT mirror record would extend beyond the supplied volume.");
            var parsed = new NtfsBootSector(bytesPerSector, sectorsPerCluster, clusterSize,
                totalSectors, mftCluster, mirrorCluster, fileRecordSize, indexBufferSize,
                serial, mftOffset, mirrorOffset);
            return new(true, parsed, Array.Empty<string>());
        }
        catch (OverflowException)
        {
            return Invalid("NTFS metadata byte offset overflows.");
        }
    }

    private static bool TryDecodeRecordSize(sbyte encoded, uint clusterSize, out uint size)
    {
        size = 0;
        if (encoded == 0) return false;
        ulong decoded;
        if (encoded > 0) decoded = (ulong)encoded * clusterSize;
        else
        {
            var exponent = -encoded;
            if (exponent > 20) return false;
            decoded = 1UL << exponent;
        }
        if (decoded == 0 || decoded > MaximumRecordBytes) return false;
        size = (uint)decoded;
        return true;
    }

    private static NtfsBootSectorParseResult Invalid(string issue) =>
        new(false, null, new[] { issue });
}
