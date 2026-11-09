using System.Buffers.Binary;
using DiskRescue.IO;

namespace DiskRescue.Filesystems;

public sealed record Fat32BootSector(
    ushort BytesPerSector,
    byte SectorsPerCluster,
    ushort ReservedSectorCount,
    byte FatCount,
    uint SectorsPerFat,
    uint TotalSectors,
    uint RootDirectoryCluster,
    ushort FsInfoSector,
    ushort BackupBootSector,
    uint ClusterCount,
    uint VolumeSerialNumber,
    long VolumeLengthBytes);

public sealed record Fat32BootSectorParseResult(
    bool IsValid,
    Fat32BootSector? BootSector,
    IReadOnlyList<string> Issues);

/// <summary>Conservative, bounded FAT32 BPB validator. Does not follow FAT chains or modify the source.</summary>
public static class Fat32BootSectorParser
{
    public static Fat32BootSectorParseResult Parse(
        IReadOnlyRandomAccessSource source, long volumeOffset, long volumeLength)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (volumeOffset < 0 || volumeLength < 512)
            return Invalid("The supplied volume range is too small or has a negative offset.");
        long end;
        try { end = checked(volumeOffset + volumeLength); }
        catch (OverflowException) { return Invalid("The supplied volume range overflows."); }
        if (end > source.Length) return Invalid("The supplied volume range extends beyond the image.");

        Span<byte> b = stackalloc byte[512];
        source.ReadExactlyAt(volumeOffset, b);
        if (!(b[0] == 0xEB && b[2] == 0x90) && b[0] != 0xE9)
            return Invalid("FAT32 jump instruction is invalid.");
        if (b[510] != 0x55 || b[511] != 0xAA)
            return Invalid("FAT32 boot-sector signature 0x55AA is missing.");

        var bps = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(11, 2));
        var spc = b[13];
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(14, 2));
        var fats = b[16];
        var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(17, 2));
        var total16 = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(19, 2));
        var fat16 = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(22, 2));
        var total32 = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(32, 4));
        var fat32 = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(36, 4));
        var root = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(44, 4));
        var fsInfo = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(48, 2));
        var backup = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(50, 2));
        var serial = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(67, 4));

        if (bps is not (512 or 1024 or 2048 or 4096))
            return Invalid("FAT32 bytes-per-sector value is unsupported.");
        if (spc == 0 || spc > 128 || (spc & (spc - 1)) != 0)
            return Invalid("FAT32 sectors-per-cluster must be a power of two from 1 to 128.");
        if (reserved == 0 || fats is not (1 or 2))
            return Invalid("FAT32 reserved-sector or FAT-count geometry is invalid.");
        if (rootEntries != 0 || total16 != 0 || fat16 != 0 || fat32 == 0 || total32 == 0)
            return Invalid("BPB does not describe a FAT32 volume.");
        if (root < 2 || fsInfo >= reserved || (backup != 0xFFFF && backup >= reserved))
            return Invalid("FAT32 root cluster, FSInfo, or backup-boot-sector reference is invalid.");

        try
        {
            var rootDirSectors = 0UL;
            var firstDataSector = checked((ulong)reserved + checked((ulong)fats * fat32) + rootDirSectors);
            if (firstDataSector >= total32)
                return Invalid("FAT32 metadata consumes the entire volume.");
            var dataSectors = checked((ulong)total32 - firstDataSector);
            var clusters64 = dataSectors / spc;
            // FAT32 is defined by at least 65,525 data clusters.
            if (clusters64 < 65525 || clusters64 > uint.MaxValue)
                return Invalid("Cluster count is outside the FAT32 classification range.");
            if (root > clusters64 + 1)
                return Invalid("FAT32 root-directory cluster lies outside the data region.");
            var fatBytes = checked((ulong)fat32 * bps);
            if (fatBytes / 4 < clusters64 + 2)
                return Invalid("FAT32 allocation table is too small for the declared data clusters.");
            var volumeBytes = checked((ulong)total32 * bps);
            if (volumeBytes > (ulong)volumeLength || volumeBytes > long.MaxValue)
                return Invalid("FAT32 total-sector count exceeds the supplied volume boundary.");
            var parsed = new Fat32BootSector(bps, spc, reserved, fats, fat32, total32,
                root, fsInfo, backup, (uint)clusters64, serial, (long)volumeBytes);
            return new(true, parsed, Array.Empty<string>());
        }
        catch (OverflowException) { return Invalid("FAT32 geometry arithmetic overflows."); }
    }

    private static Fat32BootSectorParseResult Invalid(string issue) =>
        new(false, null, new[] { issue });
}
