using System.Buffers.Binary;
using DiskRescue.IO;

namespace DiskRescue.Filesystems;

public sealed record KnownPartitionRange(string Table, int Index, long ByteOffset, long ByteLength);

public sealed record PartitionScanProgress(long BytesScanned, long TotalBytes, int CandidatesValidated)
{
    public int Percent => TotalBytes <= 0 ? 0 : (int)Math.Clamp(BytesScanned * 100L / TotalBytes, 0, 100);
}

public sealed record DiscoveredPartitionCandidate(
    long ByteOffset,
    long ByteLength,
    ushort BytesPerSector,
    uint ClusterSizeBytes,
    ulong TotalSectors,
    ulong VolumeSerialNumber,
    string Status,
    int Confidence,
    bool BackupBootSectorValid,
    string? MatchingPartition,
    IReadOnlyList<string> Evidence);

public sealed record PartitionDiscoveryResult(
    IReadOnlyList<DiscoveredPartitionCandidate> Candidates,
    long BytesScanned,
    long TotalBytes,
    bool WasCancelled,
    IReadOnlyList<string> Issues);

/// <summary>
/// Performs a bounded, read-only NTFS boot-sector search over a regular image source.
/// A candidate is reported only after primary BPB validation; a matching backup boot
/// sector raises confidence. This does not mount volumes or modify the source.
/// </summary>
public static class NtfsPartitionScanner
{
    public const int MaximumCandidates = 512;
    public const int MaximumChunkBytes = 4 * 1024 * 1024;
    private static ReadOnlySpan<byte> OemId => "NTFS    "u8;

    public static PartitionDiscoveryResult Scan(
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
            return new(Array.Empty<DiscoveredPartitionCandidate>(), 0, totalBytes, false,
                new[] { "Image is too small for the selected logical sector size." });

        var partitions = knownPartitions ?? Array.Empty<KnownPartitionRange>();
        var candidates = new List<DiscoveredPartitionCandidate>();
        var issues = new List<string>();
        var chunkSize = MaximumChunkBytes - (MaximumChunkBytes % logicalSectorSize);
        var buffer = new byte[chunkSize + logicalSectorSize];
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
            var candidateLimit = Math.Max(0, bytesToRead - logicalSectorSize + 1);

            for (var localOffset = 0; localOffset < candidateLimit; localOffset += logicalSectorSize)
            {
                if (!buffer.AsSpan(localOffset + 3, 8).SequenceEqual(OemId))
                    continue;

                var absoluteOffset = checked(scanned + localOffset);
                if (candidates.Any(candidate => candidate.ByteOffset == absoluteOffset))
                    continue;

                var remaining = source.Length - absoluteOffset;
                var bootResult = NtfsBootSectorParser.Parse(source, absoluteOffset, remaining);
                if (!bootResult.IsValid || bootResult.BootSector is null)
                    continue;

                var boot = bootResult.BootSector;
                ulong volumeBytes;
                try { volumeBytes = checked(boot.TotalSectors * boot.BytesPerSector); }
                catch (OverflowException) { continue; }
                if (volumeBytes < boot.BytesPerSector || volumeBytes > (ulong)remaining)
                    continue;

                var backupValid = ValidateBackupBootSector(
                    source, absoluteOffset, (long)volumeBytes, boot.BytesPerSector,
                    boot.SectorsPerCluster, boot.TotalSectors, boot.VolumeSerialNumber);
                var match = partitions.FirstOrDefault(partition =>
                    partition.ByteOffset == absoluteOffset &&
                    partition.ByteLength >= checked((long)volumeBytes));
                var overlap = partitions.FirstOrDefault(partition =>
                    RangesOverlap(absoluteOffset, checked((long)volumeBytes),
                        partition.ByteOffset, partition.ByteLength));

                var status = match is not null ? "Existing partition"
                    : overlap is not null ? "Overlaps known partition"
                    : "Lost partition candidate";
                var confidence = match is not null ? 100 : backupValid ? 95 : 75;
                var evidence = new List<string>
                {
                    "NTFS OEM identifier and 0x55AA boot signature detected.",
                    "Primary NTFS BPB geometry and volume boundaries validated.",
                    backupValid
                        ? "Backup boot sector agrees on geometry and volume serial."
                        : "Backup boot sector could not be corroborated."
                };
                if (match is not null)
                    evidence.Add($"Matches {match.Table} partition {match.Index}.");
                else if (overlap is not null)
                    evidence.Add($"Overlaps {overlap.Table} partition {overlap.Index}; not classified as lost.");
                else
                    evidence.Add("No overlap with any partition found in the current MBR/GPT tables.");

                candidates.Add(new(
                    absoluteOffset, checked((long)volumeBytes), boot.BytesPerSector,
                    boot.ClusterSizeBytes, boot.TotalSectors, boot.VolumeSerialNumber,
                    status, confidence, backupValid,
                    match is null ? null : $"{match.Table} #{match.Index}", evidence));

                if (candidates.Count >= MaximumCandidates)
                {
                    issues.Add($"Candidate limit of {MaximumCandidates} reached; scan stopped early.");
                    scanned = Math.Min(totalBytes, absoluteOffset + logicalSectorSize);
                    progress?.Report(new(scanned, totalBytes, candidates.Count));
                    return new(candidates, scanned, totalBytes, false, issues);
                }
            }

            scanned += bytesToRead;
            progress?.Report(new(scanned, totalBytes, candidates.Count));
        }

        return new(candidates.OrderBy(candidate => candidate.ByteOffset).ToArray(),
            scanned, totalBytes, cancelled, issues);
    }

    private static bool ValidateBackupBootSector(
        IReadOnlyRandomAccessSource source,
        long volumeOffset,
        long volumeLength,
        ushort bytesPerSector,
        byte sectorsPerCluster,
        ulong totalSectors,
        ulong serial)
    {
        try
        {
            var backupOffset = checked(volumeOffset + volumeLength - bytesPerSector);
            Span<byte> backup = stackalloc byte[512];
            source.ReadExactlyAt(backupOffset, backup);
            return backup.Slice(3, 8).SequenceEqual(OemId) &&
                backup[510] == 0x55 && backup[511] == 0xAA &&
                BinaryPrimitives.ReadUInt16LittleEndian(backup.Slice(11, 2)) == bytesPerSector &&
                backup[13] == sectorsPerCluster &&
                BinaryPrimitives.ReadUInt64LittleEndian(backup.Slice(40, 8)) == totalSectors &&
                BinaryPrimitives.ReadUInt64LittleEndian(backup.Slice(72, 8)) == serial;
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
            var firstEnd = checked(firstOffset + firstLength);
            var secondEnd = checked(secondOffset + secondLength);
            return firstOffset < secondEnd && secondOffset < firstEnd;
        }
        catch (OverflowException)
        {
            return true;
        }
    }
}
