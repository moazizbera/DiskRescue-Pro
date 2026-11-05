using System.Buffers.Binary;
using System.Text;
using DiskRescue.IO;

namespace DiskRescue.Partitions;

public sealed record GptPartition(
    int Index, Guid TypeGuid, Guid UniqueGuid, ulong FirstLba, ulong LastLba,
    ulong Attributes, string Name, long ByteOffset, long ByteLength);

public sealed record GptHeader(
    ulong CurrentLba, ulong BackupLba, ulong FirstUsableLba, ulong LastUsableLba,
    Guid DiskGuid, ulong PartitionEntryLba, uint PartitionEntryCount,
    uint PartitionEntrySize, uint PartitionEntryArrayCrc32, uint HeaderCrc32);

public sealed record GptParseResult(
    bool HeaderValid, GptHeader? Header, IReadOnlyList<GptPartition> Partitions,
    IReadOnlyList<string> Issues);

public sealed record GptDiskParseResult(
    GptParseResult Primary, GptParseResult Backup, IReadOnlyList<string> ConsistencyIssues);

/// <summary>Read-only GPT parser with bounded allocation, header/array CRC checks, and image-bound validation.</summary>
public static class GptParser
{
    private const int MinimumHeaderSize = 92;
    private const uint MaximumEntryCount = 16384;
    private const uint MaximumEntrySize = 4096;
    private const ulong MaximumEntryArrayBytes = 16 * 1024 * 1024;
    private static ReadOnlySpan<byte> Signature => "EFI PART"u8;

    public static GptDiskParseResult ParsePrimaryAndBackup(IReadOnlyRandomAccessSource source, uint logicalSectorSize = 512)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSectorSize(logicalSectorSize);
        if (source.Length <= 0 || source.Length % logicalSectorSize != 0)
        {
            var invalid = new GptParseResult(false, null, Array.Empty<GptPartition>(),
                new[] { "Image length is not a positive multiple of the logical sector size." });
            return new(invalid, invalid, new[] { "GPT copies could not be located because image geometry is invalid." });
        }

        var totalSectors = (ulong)source.Length / logicalSectorSize;
        var primary = ParseAtLba(source, logicalSectorSize, 1);
        var backupLba = primary.Header?.BackupLba ?? (totalSectors - 1);
        if (backupLba >= totalSectors) backupLba = totalSectors - 1;
        var backup = ParseAtLba(source, logicalSectorSize, backupLba);
        var issues = new List<string>();

        if (!primary.HeaderValid) issues.Add("Primary GPT header is invalid.");
        if (!backup.HeaderValid) issues.Add("Backup GPT header is invalid.");
        if (primary.Header is { } p && backup.Header is { } b)
        {
            if (p.DiskGuid != b.DiskGuid) issues.Add("Primary and backup GPT disk GUIDs disagree.");
            if (p.CurrentLba != b.BackupLba || p.BackupLba != b.CurrentLba)
                issues.Add("Primary and backup GPT header pointers are not reciprocal.");
            if (p.FirstUsableLba != b.FirstUsableLba || p.LastUsableLba != b.LastUsableLba)
                issues.Add("Primary and backup GPT usable-LBA ranges disagree.");
            if (p.PartitionEntryCount != b.PartitionEntryCount ||
                p.PartitionEntrySize != b.PartitionEntrySize ||
                p.PartitionEntryArrayCrc32 != b.PartitionEntryArrayCrc32)
                issues.Add("Primary and backup GPT partition-array metadata disagree.");
        }

        return new(primary, backup, issues);
    }

    public static GptParseResult ParseAtLba(IReadOnlyRandomAccessSource source, uint logicalSectorSize, ulong headerLba)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSectorSize(logicalSectorSize);
        var partitions = new List<GptPartition>();
        var issues = new List<string>();
        if (source.Length <= 0 || source.Length % logicalSectorSize != 0)
            return Invalid("Image length is not a positive multiple of the logical sector size.");
        var totalSectors = (ulong)source.Length / logicalSectorSize;
        if (headerLba >= totalSectors)
            return Invalid("GPT header LBA is outside the image.");

        ulong headerOffset;
        try { headerOffset = checked(headerLba * logicalSectorSize); }
        catch (OverflowException) { return Invalid("GPT header offset overflows."); }

        var sector = new byte[logicalSectorSize];
        source.ReadExactlyAt(checked((long)headerOffset), sector);
        if (!sector.AsSpan(0, 8).SequenceEqual(Signature))
            return Invalid("GPT signature 'EFI PART' is missing.");

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(12, 4));
        var storedHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(16, 4));
        var reserved = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(20, 4));
        if (headerSize < MinimumHeaderSize || headerSize > logicalSectorSize)
            return Invalid("GPT header size is outside the permitted bounds.");
        if (reserved != 0)
            return Invalid("GPT reserved header field is non-zero.");

        var headerBytes = sector.AsSpan(0, (int)headerSize).ToArray();
        headerBytes.AsSpan(16, 4).Clear();
        if (Crc32.Compute(headerBytes) != storedHeaderCrc)
            return Invalid("GPT header CRC32 does not match.");

        var currentLba = BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(24, 8));
        var backupLba = BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(32, 8));
        var firstUsable = BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(40, 8));
        var lastUsable = BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(48, 8));
        var diskGuid = new Guid(sector.AsSpan(56, 16));
        var entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(72, 8));
        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(80, 4));
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(84, 4));
        var entriesCrc = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(88, 4));

        if (currentLba != headerLba) return Invalid("GPT current-LBA field does not match the header location.");
        if (backupLba >= totalSectors || backupLba == currentLba) return Invalid("GPT backup-LBA is invalid.");
        if (diskGuid == Guid.Empty) return Invalid("GPT disk GUID is empty.");
        if (firstUsable > lastUsable || lastUsable >= totalSectors) return Invalid("GPT usable-LBA range is invalid.");
        if (entryCount == 0 || entryCount > MaximumEntryCount) return Invalid("GPT partition-entry count exceeds safe limits.");
        if (entrySize < 128 || entrySize > MaximumEntrySize || entrySize % 128 != 0)
            return Invalid("GPT partition-entry size is invalid or exceeds safe limits.");

        ulong arrayBytes;
        ulong arrayOffset;
        ulong arraySectors;
        try
        {
            arrayBytes = checked((ulong)entryCount * entrySize);
            arrayOffset = checked(entriesLba * logicalSectorSize);
            arraySectors = checked((arrayBytes + logicalSectorSize - 1) / logicalSectorSize);
        }
        catch (OverflowException) { return Invalid("GPT partition-entry array range overflows."); }

        if (arrayBytes > MaximumEntryArrayBytes) return Invalid("GPT partition-entry array exceeds the 16 MiB safety limit.");
        if (entriesLba >= totalSectors || arrayOffset > (ulong)source.Length || arrayBytes > (ulong)source.Length - arrayOffset)
            return Invalid("GPT partition-entry array extends beyond the image.");
        if (entriesLba <= lastUsable && entriesLba + arraySectors - 1 >= firstUsable)
            return Invalid("GPT partition-entry array overlaps the usable partition-LBA range.");

        var array = new byte[(int)arrayBytes];
        source.ReadExactlyAt(checked((long)arrayOffset), array);
        if (Crc32.Compute(array) != entriesCrc)
            return Invalid("GPT partition-entry array CRC32 does not match.");

        var seenGuids = new HashSet<Guid>();
        for (var i = 0; i < entryCount; i++)
        {
            var entry = array.AsSpan(checked((int)(i * entrySize)), (int)entrySize);
            var typeGuid = new Guid(entry[..16]);
            if (typeGuid == Guid.Empty) continue;
            var uniqueGuid = new Guid(entry.Slice(16, 16));
            var firstLba = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(32, 8));
            var lastLba = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(40, 8));
            var attributes = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(48, 8));
            var name = Encoding.Unicode.GetString(entry.Slice(56, 72)).TrimEnd('\0');

            if (uniqueGuid == Guid.Empty)
            {
                issues.Add($"GPT partition entry {i + 1} has an empty unique GUID.");
                continue;
            }
            if (!seenGuids.Add(uniqueGuid))
            {
                issues.Add($"GPT partition entry {i + 1} duplicates a unique GUID.");
                continue;
            }
            if (firstLba > lastLba || firstLba < firstUsable || lastLba > lastUsable)
            {
                issues.Add($"GPT partition entry {i + 1} has an invalid usable-LBA range.");
                continue;
            }

            try
            {
                var byteOffset = checked(firstLba * logicalSectorSize);
                var sectorCount = checked(lastLba - firstLba + 1);
                var byteLength = checked(sectorCount * logicalSectorSize);
                if (byteOffset > (ulong)source.Length || byteLength > (ulong)source.Length - byteOffset)
                {
                    issues.Add($"GPT partition entry {i + 1} extends beyond the image.");
                    continue;
                }
                partitions.Add(new GptPartition(i + 1, typeGuid, uniqueGuid, firstLba, lastLba,
                    attributes, name, checked((long)byteOffset), checked((long)byteLength)));
            }
            catch (OverflowException)
            {
                issues.Add($"GPT partition entry {i + 1} has an overflowing byte range.");
            }
        }

        var ordered = partitions.OrderBy(p => p.FirstLba).ToArray();
        for (var i = 1; i < ordered.Length; i++)
            if (ordered[i].FirstLba <= ordered[i - 1].LastLba)
                issues.Add($"GPT partition entries {ordered[i - 1].Index} and {ordered[i].Index} overlap.");

        var header = new GptHeader(currentLba, backupLba, firstUsable, lastUsable, diskGuid,
            entriesLba, entryCount, entrySize, entriesCrc, storedHeaderCrc);
        return new(true, header, partitions, issues);

        GptParseResult Invalid(string issue) => new(false, null, partitions, new[] { issue });
    }

    private static void ValidateSectorSize(uint sectorSize)
    {
        if (sectorSize < 512 || sectorSize > 65536 || (sectorSize & (sectorSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(sectorSize), "Sector size must be a power of two between 512 and 65536.");
    }

    private static GptParseResult Invalid(string issue) =>
        new(false, null, Array.Empty<GptPartition>(), new[] { issue });

    private static class Crc32
    {
        public static uint Compute(ReadOnlySpan<byte> data)
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
}
