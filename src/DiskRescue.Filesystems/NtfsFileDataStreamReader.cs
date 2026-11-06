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
/// writable stream. Resident streams and attribute-list-validated, multi-extent
/// non-sparse, uncompressed, unencrypted streams are supported. Source access is read-only.
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

        var listAttributes = parsed.Attributes.Where(attribute => attribute.Type == 0x20).ToArray();
        if (listAttributes.Length > 1 || (listAttributes.Length == 1 && listAttributes[0].IsNonResident))
            return InvalidDataStream("File has an ambiguous or non-resident $ATTRIBUTE_LIST; this form is not supported.");

        NtfsAttribute[] data;
        if (listAttributes.Length == 0)
        {
            data = parsed.Attributes
                .Where(attribute => attribute.Type == FileDataAttributeType &&
                                    string.Equals(attribute.Name, streamName, StringComparison.Ordinal))
                .ToArray();
        }
        else
        {
            var parsedList = NtfsAttributeListParser.Parse(listAttributes[0]);
            if (!parsedList.IsValid)
                return InvalidDataStream("File $ATTRIBUTE_LIST is invalid: " + string.Join("; ", parsedList.Issues));
            if (parsedList.Entries.Count > NtfsAttributeListMatcher.MaximumRecords)
                return InvalidDataStream("File $ATTRIBUTE_LIST exceeds the continuation-record safety limit.");

            var baseReference = new NtfsFileReference(record.RecordNumber, record.SequenceNumber);
            var recordsByNumber = new Dictionary<ulong, NtfsFileRecord>
            {
                [record.RecordNumber] = record
            };
            foreach (var entry in parsedList.Entries)
            {
                if (recordsByNumber.ContainsKey(entry.FileRecordNumber))
                    continue;
                if (entry.FileRecordNumber > uint.MaxValue)
                    return InvalidDataStream("File $ATTRIBUTE_LIST references an unsupported MFT record number.");

                var referenced = ReadReferencedRecord(
                    new NtfsFileReference(entry.FileRecordNumber, entry.FileSequenceNumber), baseReference);
                if (!referenced.IsValid || referenced.Record is null)
                    return InvalidDataStream($"File continuation record {entry.FileRecordNumber} could not be validated: " +
                        string.Join("; ", referenced.Issues));
                recordsByNumber.Add(entry.FileRecordNumber, referenced.Record);
            }

            var matched = NtfsAttributeListMatcher.Match(parsedList, recordsByNumber.Values.ToArray(), baseReference);
            if (!matched.IsComplete)
                return InvalidDataStream("File $ATTRIBUTE_LIST identity validation was incomplete: " +
                    string.Join("; ", matched.Diagnostics.Select(diagnostic => diagnostic.Message)));

            data = matched.Matches
                .Where(item => item.Entry.Type == FileDataAttributeType &&
                               string.Equals(item.Entry.Name, streamName, StringComparison.Ordinal))
                .Select(item => item.Attribute)
                .ToArray();
        }

        if (data.Length == 0)
            return InvalidDataStream("The requested $DATA stream was not found in the validated FILE record set.");

        if (data.Any(attribute => attribute.Flags != 0))
            return InvalidDataStream("Compressed, encrypted, sparse, or otherwise flagged file data streams are not yet supported.");

        if (data.Length == 1 && !data[0].IsNonResident)
        {
            var resident = data[0];
            if (resident.ResidentValue is null || resident.ResidentValueLength != resident.ResidentValue.Length)
                return InvalidDataStream("Resident $DATA value metadata is inconsistent.");
            var reader = new NtfsFileDataStreamReader(_source, _volumeOffset, _volumeLength,
                _boot.ClusterSizeBytes, resident.ResidentValue);
            return new(true, reader, true, reader.Length, Array.Empty<string>());
        }

        if (data.Any(attribute => !attribute.IsNonResident))
            return InvalidDataStream("A $DATA stream cannot mix resident and non-resident extents.");

        try
        {
            var ordered = data.OrderBy(attribute => attribute.LowestVcn).ToArray();
            var first = ordered[0];
            if (first.LowestVcn != 0 || first.AllocatedSize is null ||
                first.RealSize is null || first.InitializedSize is null)
                return InvalidDataStream("Non-resident $DATA stream is missing its base extent or size metadata.");
            if (first.InitializedSize.Value > first.RealSize.Value ||
                first.RealSize.Value > first.AllocatedSize.Value)
                return InvalidDataStream("Non-resident $DATA stream size metadata is inconsistent.");

            var volumeClusters = (ulong)(_volumeLength / _boot.ClusterSizeBytes);
            var allRuns = new List<NtfsDataRun>();
            ulong nextVcn = 0;
            foreach (var extent in ordered)
            {
                if (extent.LowestVcn is not ulong lowest || extent.HighestVcn is not ulong highest ||
                    extent.MappingPairs is null || lowest != nextVcn || highest < lowest)
                    return InvalidDataStream("Non-resident $DATA extents contain missing metadata, a VCN gap, or an overlap.");

                var runlist = NtfsRunlistParser.Parse(extent.MappingPairs, volumeClusters);
                if (!runlist.IsValid)
                    return InvalidDataStream("File $DATA mapping pairs are invalid: " + string.Join("; ", runlist.Issues));
                ulong expectedClusters;
                try { expectedClusters = checked(highest - lowest + 1); }
                catch (OverflowException) { return InvalidDataStream("File $DATA extent VCN range overflows."); }
                if (runlist.Runs.Count == 0 || runlist.TotalClusters != expectedClusters ||
                    runlist.Runs.Count > 1_000_000 - allRuns.Count)
                    return InvalidDataStream("File $DATA mapping pairs do not match the declared VCN extent or exceed the run limit.");

                foreach (var run in runlist.Runs)
                {
                    var translatedVcn = checked(lowest + run.StartingVcn);
                    allRuns.Add(new NtfsDataRun(translatedVcn, run.ClusterCount, run.StartingLcn));
                }
                nextVcn = checked(highest + 1);
                if (nextVcn > volumeClusters)
                    return InvalidDataStream("File $DATA extent map exceeds the volume cluster count.");
            }

            var physicalRuns = allRuns.Where(run => !run.IsSparse)
                .OrderBy(run => run.StartingLcn).ToArray();
            ulong previousEnd = 0;
            var hasPrevious = false;
            foreach (var run in physicalRuns)
            {
                var startLcn = run.StartingLcn!.Value;
                var endLcn = checked(startLcn + run.ClusterCount);
                if (hasPrevious && startLcn < previousEnd)
                    return InvalidDataStream("File $DATA mapping pairs contain overlapping physical runs.");
                previousEnd = endLcn;
                hasPrevious = true;
            }

            var mappedBytes = checked(nextVcn * _boot.ClusterSizeBytes);
            if (first.AllocatedSize.Value > mappedBytes || first.RealSize.Value > mappedBytes)
                return InvalidDataStream("File $DATA stream sizes exceed the assembled extent map.");

            var reader = new NtfsFileDataStreamReader(_source, _volumeOffset, _volumeLength,
                _boot.ClusterSizeBytes, allRuns, first.InitializedSize.Value, first.RealSize.Value);
            return new(true, reader, false, reader.Length, Array.Empty<string>());
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
        {
            return InvalidDataStream("File $DATA stream geometry overflows supported integer bounds.");
        }
    }

    private static NtfsFileDataStreamOpenResult InvalidDataStream(string issue) =>
        new(false, null, false, 0, new[] { issue });
}
