using DiskRescue.IO;

namespace DiskRescue.Filesystems;

public sealed record NtfsFileDataStreamOpenResult(
    bool IsValid,
    NtfsFileDataStreamReader? Reader,
    bool IsResident,
    ulong Length,
    IReadOnlyList<string> Issues);

public sealed record NtfsFileDataStreamCopyResult(
    bool IsValid,
    ulong BytesWritten,
    IReadOnlyList<string> Issues);

/// <summary>
/// Copies one validated, unnamed or named NTFS data stream to a caller-owned
/// writable stream. Resident streams and single-extent, uncompressed,
/// unencrypted non-resident streams are supported. Source access is read-only.
/// </summary>
public sealed class NtfsFileDataStreamReader
{
    private const int CopyBufferBytes = 1024 * 1024;
    private readonly IReadOnlyRandomAccessSource _source;
    private readonly long _volumeOffset;
    private readonly long _volumeLength;
    private readonly uint _clusterSize;
    private readonly byte[]? _residentData;
    private readonly IReadOnlyList<NtfsDataRun>? _runs;
    private readonly ulong _initializedSize;
    private readonly ulong _length;

    internal NtfsFileDataStreamReader(
        IReadOnlyRandomAccessSource source,
        long volumeOffset,
        long volumeLength,
        uint clusterSize,
        byte[] residentData)
    {
        _source = source;
        _volumeOffset = volumeOffset;
        _volumeLength = volumeLength;
        _clusterSize = clusterSize;
        _residentData = residentData;
        _initializedSize = (ulong)residentData.Length;
        _length = (ulong)residentData.Length;
    }

    internal NtfsFileDataStreamReader(
        IReadOnlyRandomAccessSource source,
        long volumeOffset,
        long volumeLength,
        uint clusterSize,
        IReadOnlyList<NtfsDataRun> runs,
        ulong initializedSize,
        ulong length)
    {
        _source = source;
        _volumeOffset = volumeOffset;
        _volumeLength = volumeLength;
        _clusterSize = clusterSize;
        _runs = runs;
        _initializedSize = initializedSize;
        _length = length;
    }

    public ulong Length => _length;
    public bool IsResident => _residentData is not null;

    /// <summary>
    /// Copies the complete logical stream. The caller must provide an explicit
    /// maximum byte limit so corrupt metadata cannot trigger unbounded exports.
    /// Bytes beyond NTFS initialized size are emitted as zeros.
    /// </summary>
    public NtfsFileDataStreamCopyResult CopyTo(
        Stream destination,
        ulong maximumBytes,
        CancellationToken cancellationToken = default,
        IProgress<ulong>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            return InvalidCopy("Destination stream is not writable.");
        if (_length > maximumBytes)
            return InvalidCopy($"Stream length {_length} exceeds the caller's {maximumBytes}-byte export limit.");

        var buffer = new byte[CopyBufferBytes];
        ulong written = 0;
        while (written < _length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = checked((int)Math.Min((ulong)buffer.Length, _length - written));
            var chunk = buffer.AsSpan(0, count);

            if (_residentData is not null)
            {
                _residentData.AsSpan(checked((int)written), count).CopyTo(chunk);
            }
            else
            {
                var initializedCount = written < _initializedSize
                    ? checked((int)Math.Min((ulong)count, _initializedSize - written))
                    : 0;
                if (initializedCount > 0)
                    ReadNonResident(written, chunk[..initializedCount]);
                chunk[initializedCount..].Clear();
            }

            destination.Write(buffer, 0, count);
            written = checked(written + (uint)count);
            progress?.Report(written);
        }

        return new(true, written, Array.Empty<string>());
    }

    private void ReadNonResident(ulong logicalOffset, Span<byte> destination)
    {
        var runs = _runs ?? throw new IOException("Non-resident stream mapping is unavailable.");
        var completed = 0;
        var volumeEnd = checked((ulong)(_volumeOffset + _volumeLength));
        while (completed < destination.Length)
        {
            var current = checked(logicalOffset + (uint)completed);
            var vcn = current / _clusterSize;
            var clusterOffset = (uint)(current % _clusterSize);
            var run = runs.FirstOrDefault(item =>
                vcn >= item.StartingVcn && vcn - item.StartingVcn < item.ClusterCount)
                ?? throw new IOException("File data offset is not covered by its validated runlist.");

            var clustersRemaining = run.ClusterCount - (vcn - run.StartingVcn);
            var available = checked(clustersRemaining * _clusterSize - clusterOffset);
            var count = checked((int)Math.Min((ulong)(destination.Length - completed), available));
            var target = destination.Slice(completed, count);
            if (run.IsSparse)
            {
                target.Clear();
            }
            else
            {
                var lcn = checked(run.StartingLcn!.Value + (vcn - run.StartingVcn));
                var relative = checked(lcn * _clusterSize + clusterOffset);
                var physical = checked((ulong)_volumeOffset + relative);
                if (physical > volumeEnd || (ulong)count > volumeEnd - physical ||
                    physical > long.MaxValue || physical > (ulong)_source.Length ||
                    (ulong)count > (ulong)_source.Length - physical)
                    throw new IOException("File data extent exceeds the validated volume or source-image boundary.");
                _source.ReadExactlyAt((long)physical, target);
            }
            completed = checked(completed + count);
        }
    }

    private static NtfsFileDataStreamCopyResult InvalidCopy(string issue) =>
        new(false, 0, new[] { issue });
}

public sealed partial class NtfsMftReader
{
    private const uint FileDataAttributeType = 0x80;
    private const ushort CompressedAttributeFlag = 0x0001;
    private const ushort EncryptedAttributeFlag = 0x4000;

    /// <summary>
    /// Opens a file's single resident or single-extent $DATA stream. Extension
    /// records, multi-extent streams, compression, and encryption fail closed.
    /// </summary>
    public NtfsFileDataStreamOpenResult OpenDataStream(ulong recordNumber, string streamName = "")
    {
        ArgumentNullException.ThrowIfNull(streamName);
        if (streamName.Length > byte.MaxValue)
            return InvalidDataStream("NTFS stream name exceeds the supported 255-character limit.");

        var read = ReadRecord(recordNumber);
        if (!read.IsValid || read.Record is null)
            return InvalidDataStream("Unable to read file's MFT record: " + string.Join("; ", read.Issues));
        var record = read.Record;
        if ((record.Flags & 0x0001) == 0)
            return InvalidDataStream("MFT record is not marked in use.");
        if ((record.Flags & 0x0002) != 0)
            return InvalidDataStream("Directory records cannot be exported as file data streams.");
        if (!record.BaseFileReference.IsNull)
            return InvalidDataStream("An MFT extension record cannot be exported as an independent file.");

        var parsed = NtfsAttributeParser.Parse(record);
        if (!parsed.IsValid)
            return InvalidDataStream("File record attributes are invalid: " + string.Join("; ", parsed.Issues));

        var data = parsed.Attributes
            .Where(attribute => attribute.Type == FileDataAttributeType &&
                                string.Equals(attribute.Name, streamName, StringComparison.Ordinal))
            .ToArray();
        if (data.Length != 1)
            return InvalidDataStream(data.Length == 0
                ? "The requested $DATA stream was not found in this FILE record."
                : "The requested $DATA stream has multiple extents or ambiguous attributes; multi-extent file streams are not yet supported.");

        var attribute = data[0];
        if (attribute.Flags != 0)
            return InvalidDataStream("Compressed, encrypted, sparse-flagged, or otherwise flagged file streams are not yet supported.");

        if (!attribute.IsNonResident)
        {
            if (attribute.ResidentValue is null || attribute.ResidentValueLength != attribute.ResidentValue.Length)
                return InvalidDataStream("Resident $DATA value metadata is inconsistent.");
            var reader = new NtfsFileDataStreamReader(_source, _volumeOffset, _volumeLength,
                _boot.ClusterSizeBytes, attribute.ResidentValue);
            return new(true, reader, true, reader.Length, Array.Empty<string>());
        }

        if (attribute.LowestVcn != 0 || attribute.HighestVcn is null ||
            attribute.MappingPairs is null || attribute.AllocatedSize is null ||
            attribute.RealSize is null || attribute.InitializedSize is null)
            return InvalidDataStream("Non-resident $DATA stream is missing its base extent or size metadata.");
        if (attribute.InitializedSize.Value > attribute.RealSize.Value ||
            attribute.RealSize.Value > attribute.AllocatedSize.Value)
            return InvalidDataStream("Non-resident $DATA stream size metadata is inconsistent.");

        try
        {
            var volumeClusters = (ulong)(_volumeLength / _boot.ClusterSizeBytes);
            var runlist = NtfsRunlistParser.Parse(attribute.MappingPairs, volumeClusters);
            if (!runlist.IsValid)
                return InvalidDataStream("File $DATA mapping pairs are invalid: " + string.Join("; ", runlist.Issues));
            if (runlist.Runs.Count == 0 || runlist.TotalClusters == 0 ||
                runlist.TotalClusters - 1 != attribute.HighestVcn.Value)
                return InvalidDataStream("File $DATA runlist does not match its declared VCN range.");

            var mappedBytes = checked(runlist.TotalClusters * _boot.ClusterSizeBytes);
            if (attribute.AllocatedSize.Value > mappedBytes || attribute.RealSize.Value > mappedBytes)
                return InvalidDataStream("File $DATA stream sizes exceed the mapped extent.");

            var physicalRuns = runlist.Runs.Where(run => !run.IsSparse)
                .OrderBy(run => run.StartingLcn).ToArray();
            ulong previousEnd = 0;
            var hasPrevious = false;
            foreach (var run in physicalRuns)
            {
                var start = run.StartingLcn!.Value;
                var end = checked(start + run.ClusterCount);
                if (hasPrevious && start < previousEnd)
                    return InvalidDataStream("File $DATA mapping pairs contain overlapping physical runs.");
                previousEnd = end;
                hasPrevious = true;
            }

            var reader = new NtfsFileDataStreamReader(_source, _volumeOffset, _volumeLength,
                _boot.ClusterSizeBytes, runlist.Runs, attribute.InitializedSize.Value, attribute.RealSize.Value);
            return new(true, reader, false, reader.Length, Array.Empty<string>());
        }
        catch (OverflowException)
        {
            return InvalidDataStream("File $DATA stream geometry overflows supported integer bounds.");
        }
    }

    private static NtfsFileDataStreamOpenResult InvalidDataStream(string issue) =>
        new(false, null, false, 0, new[] { issue });
}
