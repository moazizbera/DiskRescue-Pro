using System.Numerics;

namespace DiskRescue.Filesystems;

public enum NtfsClusterAllocationStatus
{
    Free,
    Allocated,
    Mixed,
    Unknown
}

public sealed record NtfsClusterAllocationAssessment(
    NtfsClusterAllocationStatus Status,
    ulong ClustersChecked,
    ulong FreeClusters,
    ulong AllocatedClusters,
    IReadOnlyList<string> Warnings);

public sealed record NtfsClusterAllocationMapOpenResult(
    bool IsValid,
    NtfsClusterAllocationMap? Map,
    IReadOnlyList<string> Issues);

/// <summary>
/// Bounded, read-only view of the NTFS $Bitmap metadata stream (MFT record 6).
/// A free bit is not proof that old file content is intact or has not been overwritten.
/// </summary>
public sealed class NtfsClusterAllocationMap
{
    public const ulong DefaultMaximumBitmapBytes = 64UL * 1024 * 1024;
    public const ulong MaximumAssessmentClusters = 16UL * 1024 * 1024;

    private readonly byte[] _bitmap;
    private readonly ulong _volumeClusterCount;

    private NtfsClusterAllocationMap(byte[] bitmap, ulong volumeClusterCount)
    {
        _bitmap = bitmap;
        _volumeClusterCount = volumeClusterCount;
    }

    public ulong VolumeClusterCount => _volumeClusterCount;
    public ulong BitmapBytes => (ulong)_bitmap.Length;

    public static NtfsClusterAllocationMapOpenResult Read(
        NtfsMftReader reader,
        ulong volumeClusterCount,
        ulong maximumBitmapBytes = DefaultMaximumBitmapBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (volumeClusterCount == 0)
            return Invalid("NTFS volume cluster count is zero.");
        if (maximumBitmapBytes == 0 || maximumBitmapBytes > int.MaxValue)
            return Invalid("Bitmap memory limit must be between 1 byte and Int32.MaxValue.");

        var requiredBytes = RequiredBytes(volumeClusterCount);
        if (requiredBytes > maximumBitmapBytes)
            return Invalid($"NTFS $Bitmap requires {requiredBytes} bytes, exceeding the configured {maximumBitmapBytes}-byte safety limit.");

        var stream = reader.OpenDataStream(6);
        if (!stream.IsValid || stream.Reader is null)
            return Invalid("Unable to open the validated NTFS $Bitmap stream (MFT record 6): " +
                string.Join("; ", stream.Issues));
        if (stream.Length < requiredBytes || stream.Length > maximumBitmapBytes)
            return Invalid("NTFS $Bitmap stream length is smaller than the volume bitmap or exceeds the configured safety limit.");

        try
        {
            using var buffer = new MemoryStream(checked((int)stream.Length));
            var copied = stream.Reader.CopyTo(buffer, maximumBitmapBytes, cancellationToken);
            if (!copied.IsValid || copied.BytesWritten != stream.Length)
                return Invalid("Unable to read the complete NTFS $Bitmap stream: " + string.Join("; ", copied.Issues));
            return Create(buffer.ToArray(), volumeClusterCount);
        }
        catch (OperationCanceledException)
        {
            return Invalid("NTFS $Bitmap read was cancelled; no allocation map was returned.");
        }
        catch (Exception exception) when (exception is IOException or OverflowException or ArgumentOutOfRangeException)
        {
            return Invalid("NTFS $Bitmap read failed safely: " + exception.Message);
        }
    }

    /// <summary>Creates a bounded allocation map from bytes, primarily for synthetic tests.</summary>
    public static NtfsClusterAllocationMapOpenResult Create(byte[] bitmap, ulong volumeClusterCount)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (volumeClusterCount == 0)
            return Invalid("NTFS volume cluster count is zero.");
        var requiredBytes = RequiredBytes(volumeClusterCount);
        if (requiredBytes > (ulong)bitmap.Length)
            return Invalid("Bitmap data does not contain one bit for every volume cluster.");
        return new(true, new NtfsClusterAllocationMap(bitmap.ToArray(), volumeClusterCount),
            Array.Empty<string>());
    }

    public NtfsClusterAllocationAssessment Assess(IReadOnlyList<NtfsDataRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0)
            return Unknown("No validated data runs were supplied.");

        ulong total = 0;
        ulong free = 0;
        ulong allocated = 0;
        foreach (var run in runs)
        {
            if (run.ClusterCount == 0 || run.StartingVcn > ulong.MaxValue - run.ClusterCount)
                return Unknown("A data run contains an empty or overflowing VCN range.");
            if (run.IsSparse)
                return Unknown("Sparse runs cannot be mapped to physical cluster allocation bits.");
            var start = run.StartingLcn!.Value;
            if (start >= _volumeClusterCount || run.ClusterCount > _volumeClusterCount - start)
                return Unknown("A data run extends beyond the volume cluster range.");
            if (run.ClusterCount > MaximumAssessmentClusters - total)
                return Unknown($"Assessment exceeds the {MaximumAssessmentClusters}-cluster safety limit.");

            for (ulong i = 0; i < run.ClusterCount; i++)
            {
                var cluster = start + i;
                var byteIndex = checked((int)(cluster >> 3));
                var mask = (byte)(1 << (int)(cluster & 7));
                if ((_bitmap[byteIndex] & mask) == 0)
                    free++;
                else
                    allocated++;
            }
            total = checked(total + run.ClusterCount);
        }

        var status = allocated == 0 ? NtfsClusterAllocationStatus.Free
            : free == 0 ? NtfsClusterAllocationStatus.Allocated
            : NtfsClusterAllocationStatus.Mixed;
        var warnings = new List<string>
        {
            "NTFS $Bitmap reports current allocation state only; it cannot prove whether cluster contents were overwritten.",
            "A free-cluster result does not establish that a deleted file's runlist is current or its data is intact."
        };
        if (allocated > 0)
            warnings.Add("One or more referenced clusters are currently allocated and may have been reused; do not claim the old file content is recoverable.");
        return new(status, total, free, allocated, warnings);
    }

    private static ulong RequiredBytes(ulong clusters) => checked((clusters + 7) / 8);

    private static NtfsClusterAllocationMapOpenResult Invalid(string issue) =>
        new(false, null, new[] { issue });

    private static NtfsClusterAllocationAssessment Unknown(string issue) =>
        new(NtfsClusterAllocationStatus.Unknown, 0, 0, 0, new[] { issue });
}
