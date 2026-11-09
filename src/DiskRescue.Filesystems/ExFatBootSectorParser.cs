using System.Buffers.Binary;
using System.Numerics;
using DiskRescue.IO;

namespace DiskRescue.Filesystems;

public sealed record ExFatBootSector(
    ushort BytesPerSector,
    uint BytesPerCluster,
    byte FatCount,
    ulong VolumeLengthSectors,
    uint FatOffset,
    uint FatLengthSectors,
    uint ClusterHeapOffset,
    uint ClusterCount,
    uint RootDirectoryCluster,
    byte PercentInUse,
    bool BackupBootRegionValid,
    uint BootChecksum,
    long VolumeLengthBytes);

public sealed record ExFatBootSectorParseResult(
    bool IsValid,
    ExFatBootSector? BootSector,
    IReadOnlyList<string> Issues);

/// <summary>Validates exFAT geometry and both checksummed boot regions without following filesystem metadata.</summary>
public static class ExFatBootSectorParser
{
    private static ReadOnlySpan<byte> OemId => "EXFAT   "u8;
    private const int BootRegionSectors = 12;

    public static ExFatBootSectorParseResult Parse(
        IReadOnlyRandomAccessSource source, long volumeOffset, long volumeLength)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (volumeOffset < 0 || volumeLength < 24L * 512)
            return Invalid("The supplied volume range is too small or has a negative offset.");
        long end;
        try { end = checked(volumeOffset + volumeLength); }
        catch (OverflowException) { return Invalid("The supplied volume range overflows."); }
        if (end > source.Length) return Invalid("The supplied volume range extends beyond the image.");

        Span<byte> first = stackalloc byte[512];
        source.ReadExactlyAt(volumeOffset, first);
        if (!first.Slice(3, 8).SequenceEqual(OemId))
            return Invalid("exFAT OEM identifier is missing.");
        if (first[510] != 0x55 || first[511] != 0xAA)
            return Invalid("exFAT boot-sector signature 0x55AA is missing.");

        var sectorShift = first[108];
        var clusterShift = first[109];
        var fatCount = first[110];
        if (sectorShift is < 9 or > 12 || clusterShift > 16 ||
            sectorShift + clusterShift > 25 || fatCount is not (1 or 2))
            return Invalid("exFAT sector, cluster, or FAT-count geometry is unsupported.");
        var bps = (ushort)(1 << sectorShift);
        var clusterBytes = 1U << (sectorShift + clusterShift);
        var volumeSectors = BinaryPrimitives.ReadUInt64LittleEndian(first.Slice(72, 8));
        var fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(first.Slice(80, 4));
        var fatLength = BinaryPrimitives.ReadUInt32LittleEndian(first.Slice(84, 4));
        var heapOffset = BinaryPrimitives.ReadUInt32LittleEndian(first.Slice(88, 4));
        var clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(first.Slice(92, 4));
        var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(first.Slice(96, 4));
        var percent = first[112];

        if (volumeSectors < 24 || fatOffset < BootRegionSectors * 2 ||
            fatLength == 0 || clusterCount == 0 || rootCluster < 2 ||
            rootCluster > (ulong)clusterCount + 1 || (percent > 100 && percent != 0xFF))
            return Invalid("exFAT volume, FAT, root-cluster, or percent-in-use fields are invalid.");

        try
        {
            var volumeBytes = checked(volumeSectors * bps);
            if (volumeBytes > (ulong)volumeLength || volumeBytes > long.MaxValue)
                return Invalid("exFAT volume length exceeds the supplied image boundary.");
            var fatEnd = checked((ulong)fatOffset + checked((ulong)fatCount * fatLength));
            var heapEnd = checked((ulong)heapOffset + checked((ulong)clusterCount * (1UL << clusterShift)));
            var fatCapacity = checked((ulong)fatLength * bps / 4);
            if (fatOffset < BootRegionSectors || fatEnd > heapOffset ||
                heapOffset >= volumeSectors || heapEnd > volumeSectors ||
                fatCapacity < (ulong)clusterCount + 2)
                return Invalid("exFAT FAT or cluster-heap boundaries are inconsistent.");

            var mainRegionValid = ValidateBootRegion(source, volumeOffset, bps, false, out var checksum);
            if (!mainRegionValid)
                return Invalid("exFAT main boot-region checksum or sector signatures are invalid.");
            var backupValid = ValidateBootRegion(source, volumeOffset, bps, true, out var backupChecksum);
            if (backupValid && backupChecksum != checksum)
                backupValid = false;

            // Backup boot region is corroborating evidence, not a prerequisite for a valid main region.
            var parsed = new ExFatBootSector(bps, clusterBytes, fatCount, volumeSectors,
                fatOffset, fatLength, heapOffset, clusterCount, rootCluster, percent,
                backupValid, checksum, (long)volumeBytes);
            return new(true, parsed, backupValid
                ? Array.Empty<string>()
                : new[] { "Main exFAT boot region is valid; backup boot region could not be corroborated." });
        }
        catch (OverflowException) { return Invalid("exFAT geometry arithmetic overflows."); }
    }

    private static bool ValidateBootRegion(
        IReadOnlyRandomAccessSource source, long volumeOffset, ushort bytesPerSector,
        bool backup, out uint checksum)
    {
        checksum = 0;
        var regionBytes = checked((int)bytesPerSector * BootRegionSectors);
        var region = new byte[regionBytes];
        var offset = checked(volumeOffset + (backup ? (long)bytesPerSector * BootRegionSectors : 0));
        source.ReadExactlyAt(offset, region);
        var boot = region.AsSpan(0, bytesPerSector);
        if (!boot.Slice(3, 8).SequenceEqual(OemId) ||
            boot[510] != 0x55 || boot[511] != 0xAA ||
            boot[108] != BitOperations.Log2(bytesPerSector))
            return false;
        for (var sector = 1; sector <= 8; sector++)
        {
            var sig = region.AsSpan(sector * bytesPerSector, bytesPerSector);
            if (sig[510] != 0x55 || sig[511] != 0xAA) return false;
        }

        uint sum = 0;
        for (var i = 0; i < bytesPerSector * 11; i++)
        {
            if (i is 106 or 107 or 112) continue;
            sum = ((sum << 31) | (sum >> 1)) + region[i];
        }
        var checksumSector = region.AsSpan(bytesPerSector * 11, bytesPerSector);
        for (var i = 0; i < bytesPerSector; i += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(checksumSector.Slice(i, 4)) != sum)
                return false;
        }
        checksum = sum;
        return true;
    }

    private static ExFatBootSectorParseResult Invalid(string issue) =>
        new(false, null, new[] { issue });
}
