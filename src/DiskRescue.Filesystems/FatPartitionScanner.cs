using System.Buffers.Binary;
using DiskRescue.IO;

namespace DiskRescue.Filesystems;

public sealed record DiscoveredFatPartitionCandidate(
    string FileSystem,
    long ByteOffset,
    long ByteLength,
    ushort BytesPerSector,
    uint ClusterSizeBytes,
    ulong TotalSectors,
    string Status,
    int Confidence,
    bool BackupBootRegionValid,
    string? MatchingPartition,
    IReadOnlyList<string> Evidence);

public sealed record FatPartitionDiscoveryResult(
    IReadOnlyList<DiscoveredFatPartitionCandidate> Candidates,
    long BytesScanned,
    long TotalBytes,
    bool WasCancelled,
    IReadOnlyList<string> Issues);

/// <summary>
/// Bounded, read-only FAT32/exFAT boot-region discovery for regular image sources.
/// Reports validated candidates only; it does not mount, repair, or modify a source.
/// </summary>
public static class FatPartitionScanner
{
    public const int MaximumCandidates = 512;
    public const int MaximumChunkBytes = 4 * 1024 * 1024;
    private static ReadOnlySpan<byte> ExFatOemId => "EXFAT   "u8;

    public static FatPartitionDiscoveryResult Scan(
        IReadOnlyRandomAccessSource source,
        int logicalSectorSize,
        IReadOnlyList<KnownPartitionRange>? knownPartitions = null,
        long maximumScanBytes = 4L * 1024 * 1024 * 1024,
        CancellationToken cancellationToken = default,
        IProgress<PartitionScanProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (logicalSectorSize is not (512 or 1024 or 2048 or 4096))
            throw new ArgumentOutOfRangeException(nameof(logicalSectorSize));
        if (maximumScanBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumScanBytes));

        var totalBytes = Math.Min(source.Length, maximumScanBytes);
        totalBytes -= totalBytes % logicalSectorSize;
        if (totalBytes < logicalSectorSize)
            return new(Array.Empty<DiscoveredFatPartitionCandidate>(), 0, totalBytes, false,
                new[] { "Image is too small for the selected logical sector size." });

        var partitions = knownPartitions ?? Array.Empty<KnownPartitionRange>();
        var candidates = new List<DiscoveredFatPartitionCandidate>();
        var issues = new List<string>();
        var chunkSize = MaximumChunkBytes - (MaximumChunkBytes % logicalSectorSize);
        var buffer = new byte[chunkSize];
        long scanned = 0;
        var cancelled = false;

        while (scanned < totalBytes)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var bytesToRead = (int)Math.Min(chunkSize, totalBytes - scanned);
            source.ReadExactlyAt(scanned, buffer.AsSpan(0, bytesToRead));
            for (var localOffset = 0; localOffset + 512 <= bytesToRead; localOffset += logicalSectorSize)
            {
                var sector = buffer.AsSpan(localOffset, 512);
                var isExFat = sector.Slice(3, 8).SequenceEqual(ExFatOemId);
                var isFat32Shape = sector[0] == 0xE9 ||
                    (sector[0] == 0xEB && sector[2] == 0x90);
                if (!isExFat && !isFat32Shape)
                    continue;

                var absoluteOffset = checked(scanned + localOffset);
                if (candidates.Any(candidate =>
                    absoluteOffset >= candidate.ByteOffset &&
                    absoluteOffset < candidate.ByteOffset + candidate.ByteLength))
                    continue;
                var remaining = source.Length - absoluteOffset;
                if (remaining < 512) continue;

                string fileSystem;
                long volumeBytes;
                ushort bytesPerSector;
                uint clusterBytes;
                ulong totalSectors;
                bool backupValid;
                IReadOnlyList<string> parserIssues;

                if (isExFat)
                {
                    var parsed = ExFatBootSectorParser.Parse(source, absoluteOffset, remaining);
                    if (!parsed.IsValid || parsed.BootSector is null) continue;
                    var boot = parsed.BootSector;
                    fileSystem = "exFAT";
                    volumeBytes = boot.VolumeLengthBytes;
                    bytesPerSector = boot.BytesPerSector;
                    clusterBytes = boot.BytesPerCluster;
                    totalSectors = boot.VolumeLengthSectors;
                    backupValid = boot.BackupBootRegionValid;
                    parserIssues = parsed.Issues;
                }
                else
                {
                    var parsed = Fat32BootSectorParser.Parse(source, absoluteOffset, remaining);
                    if (!parsed.IsValid || parsed.BootSector is null) continue;
                    var boot = parsed.BootSector;
                    fileSystem = "FAT32";
                    volumeBytes = boot.VolumeLengthBytes;
                    bytesPerSector = boot.BytesPerSector;
                    clusterBytes = checked((uint)boot.BytesPerSector * boot.SectorsPerCluster);
                    totalSectors = boot.TotalSectors;
                    backupValid = ValidateFat32Backup(source, absoluteOffset, boot);
                    parserIssues = parsed.Issues;
                }

                if (bytesPerSector != logicalSectorSize || volumeBytes <= 0 ||
                    volumeBytes > remaining)
                    continue;

                var matching = partitions.FirstOrDefault(p =>
                    p.ByteOffset == absoluteOffset && p.ByteLength >= volumeBytes);
                var overlapping = partitions.FirstOrDefault(p =>
                    RangesOverlap(absoluteOffset, volumeBytes, p.ByteOffset, p.ByteLength));
                var status = matching is not null ? "Existing partition"
                    : overlapping is not null ? "Overlaps known partition"
                    : "Lost partition candidate";
                var confidence = matching is not null ? 100 : backupValid ? 95 : 75;
                var evidence = new List<string>
                {
                    $"{fileSystem} boot identifier and 0x55AA signature detected.",
                    $"{fileSystem} geometry, metadata bounds, and declared volume length validated.",
                    backupValid
                        ? "Backup boot region/sector independently corroborates the primary."
                        : "Backup boot region/sector could not be corroborated."
                };
                if (matching is not null)
                    evidence.Add($"Matches {matching.Table} partition {matching.Index}.");
                else if (overlapping is not null)
                    evidence.Add($"Overlaps {overlapping.Table} partition {overlapping.Index}; not classified as lost.");
                else
                    evidence.Add("No overlap with any partition found in the supplied partition-table ranges.");
                evidence.AddRange(parserIssues);

                candidates.Add(new(fileSystem, absoluteOffset, volumeBytes, bytesPerSector,
                    clusterBytes, totalSectors, status, confidence, backupValid,
                    matching is null ? null : $"{matching.Table} #{matching.Index}", evidence));

                if (candidates.Count >= MaximumCandidates)
                {
                    issues.Add($"Candidate limit of {MaximumCandidates} reached; scan stopped early.");
                    scanned = Math.Min(totalBytes, absoluteOffset + logicalSectorSize);
                    progress?.Report(new(scanned, totalBytes, candidates.Count));
                    return new(candidates.OrderBy(c => c.ByteOffset).ToArray(),
                        scanned, totalBytes, false, issues);
                }
            }

            scanned += bytesToRead;
            progress?.Report(new(scanned, totalBytes, candidates.Count));
        }

        return new(candidates.OrderBy(c => c.ByteOffset).ToArray(),
            scanned, totalBytes, cancelled, issues);
    }

    private static bool ValidateFat32Backup(
        IReadOnlyRandomAccessSource source, long volumeOffset, Fat32BootSector boot)
    {
        if (boot.BackupBootSector == 0 || boot.BackupBootSector == 0xFFFF ||
            boot.BackupBootSector >= boot.ReservedSectorCount)
            return false;

        try
        {
            var offset = checked(volumeOffset + (long)boot.BackupBootSector * boot.BytesPerSector);
            Span<byte> backup = stackalloc byte[512];
            source.ReadExactlyAt(offset, backup);
            return backup[510] == 0x55 && backup[511] == 0xAA &&
                (backup[0] == 0xE9 || (backup[0] == 0xEB && backup[2] == 0x90)) &&
                BinaryPrimitives.ReadUInt16LittleEndian(backup.Slice(11, 2)) == boot.BytesPerSector &&
                backup[13] == boot.SectorsPerCluster &&
                BinaryPrimitives.ReadUInt16LittleEndian(backup.Slice(14, 2)) == boot.ReservedSectorCount &&
                backup[16] == boot.FatCount &&
                BinaryPrimitives.ReadUInt32LittleEndian(backup.Slice(36, 4)) == boot.SectorsPerFat &&
                BinaryPrimitives.ReadUInt32LittleEndian(backup.Slice(32, 4)) == boot.TotalSectors &&
                BinaryPrimitives.ReadUInt32LittleEndian(backup.Slice(44, 4)) == boot.RootDirectoryCluster &&
                BinaryPrimitives.ReadUInt32LittleEndian(backup.Slice(67, 4)) == boot.VolumeSerialNumber;
        }
        catch (Exception exception) when (exception is IOException or ArgumentOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool RangesOverlap(long firstOffset, long firstLength, long secondOffset, long secondLength)
    {
        if (firstOffset < 0 || firstLength <= 0 || secondOffset < 0 || secondLength <= 0)
            return false;
        try
        {
            return firstOffset < checked(secondOffset + secondLength) &&
                secondOffset < checked(firstOffset + firstLength);
        }
        catch (OverflowException) { return true; }
    }
}
