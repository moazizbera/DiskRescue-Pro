using System.Buffers.Binary;
using DiskRescue.IO;

namespace DiskRescue.Partitions;

public sealed record MbrPartition(
    int Index,
    byte BootIndicator,
    byte PartitionType,
    uint StartLba,
    uint SectorCount,
    long ByteOffset,
    long ByteLength);

public sealed record MbrParseResult(
    bool HasValidSignature,
    bool IsProtectiveMbr,
    IReadOnlyList<MbrPartition> Partitions,
    IReadOnlyList<string> Issues);

/// <summary>Conservative, read-only parser for the legacy 512-byte MBR structure.</summary>
public static class MbrParser
{
    private const int MbrSize = 512;
    private const int PartitionTableOffset = 446;
    private const int EntrySize = 16;
    private const byte ProtectiveGptType = 0xEE;

    public static MbrParseResult Parse(IReadOnlyRandomAccessSource source, int logicalSectorSize = 512)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (logicalSectorSize < 512 || logicalSectorSize > 65536 ||
            (logicalSectorSize & (logicalSectorSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(logicalSectorSize), "Sector size must be a power of two between 512 and 65536.");

        var issues = new List<string>();
        var partitions = new List<MbrPartition>();
        if (source.Length < MbrSize)
            return new(false, false, partitions, new[] { "Image is shorter than the 512-byte MBR structure." });

        Span<byte> sector = stackalloc byte[MbrSize];
        source.ReadExactlyAt(0, sector);

        var signatureValid = sector[510] == 0x55 && sector[511] == 0xAA;
        if (!signatureValid)
            issues.Add("MBR signature 0x55AA is missing.");

        var protective = false;
        for (var i = 0; i < 4; i++)
        {
            var entry = sector.Slice(PartitionTableOffset + i * EntrySize, EntrySize);
            var boot = entry[0];
            var type = entry[4];
            var startLba = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..12]);
            var sectorCount = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..16]);

            if (type == 0 && startLba == 0 && sectorCount == 0 && boot == 0)
                continue;

            if (boot is not (0x00 or 0x80))
            {
                issues.Add($"Partition entry {i + 1} has invalid boot indicator 0x{boot:X2}.");
                continue;
            }

            if (type == 0 || sectorCount == 0)
            {
                issues.Add($"Partition entry {i + 1} is partially populated (type or sector count is zero).");
                continue;
            }

            if (type == ProtectiveGptType)
                protective = true;

            if (startLba == 0)
            {
                issues.Add($"Partition entry {i + 1} starts at LBA 0, which overlaps the MBR.");
                continue;
            }

            try
            {
                var byteOffset = checked((ulong)startLba * (uint)logicalSectorSize);
                var byteLength = checked((ulong)sectorCount * (uint)logicalSectorSize);
                if (byteOffset > (ulong)source.Length || byteLength > (ulong)source.Length - byteOffset)
                {
                    issues.Add($"Partition entry {i + 1} extends beyond the image boundary.");
                    continue;
                }

                partitions.Add(new MbrPartition(
                    i + 1, boot, type, startLba, sectorCount,
                    checked((long)byteOffset), checked((long)byteLength)));
            }
            catch (OverflowException)
            {
                issues.Add($"Partition entry {i + 1} has an overflowing byte range.");
            }
        }

        return new(signatureValid, protective, partitions, issues);
    }
}
