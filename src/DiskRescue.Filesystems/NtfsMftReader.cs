using DiskRescue.IO;

namespace DiskRescue.Filesystems;

public sealed record NtfsMftOpenResult(
    bool IsValid,
    NtfsMftReader? Reader,
    IReadOnlyList<string> Issues);

public sealed record NtfsMftRecordReadResult(
    bool IsValid,
    NtfsFileRecord? Record,
    IReadOnlyList<string> Issues);

/// <summary>
/// Reads fixed-size FILE records through the validated unnamed $MFT::$DATA runlist.
/// The source is borrowed, never disposed, and is accessed exclusively through bounded reads.
/// No directory-index traversal or writes are performed.
/// </summary>
public sealed partial class NtfsMftReader
{
    private const uint DataAttributeType = 0x80;
    private readonly IReadOnlyRandomAccessSource _source;
    private readonly long _volumeOffset;
    private readonly long _volumeLength;
    private readonly NtfsBootSector _boot;
    private readonly IReadOnlyList<NtfsDataRun> _runs;
    private readonly ulong _mftInitializedSize;
    private readonly ulong _mftMappedBytes;

    private NtfsMftReader(
        IReadOnlyRandomAccessSource source,
        long volumeOffset,
        long volumeLength,
        NtfsBootSector boot,
        IReadOnlyList<NtfsDataRun> runs,
        ulong initializedSize,
        ulong mappedBytes)
    {
        _source = source;
        _volumeOffset = volumeOffset;
        _volumeLength = volumeLength;
        _boot = boot;
        _runs = runs;
        _mftInitializedSize = initializedSize;
        _mftMappedBytes = mappedBytes;
    }

    public ulong InitializedSize => _mftInitializedSize;
    public uint FileRecordSizeBytes => _boot.FileRecordSizeBytes;

    public static NtfsMftOpenResult Open(
        IReadOnlyRandomAccessSource source,
        long volumeOffset,
        long volumeLength,
        NtfsBootSector boot)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(boot);
        if (volumeOffset < 0 || volumeLength <= 0)
            return InvalidOpen("The supplied NTFS volume range is invalid.");

        long volumeEnd;
        try { volumeEnd = checked(volumeOffset + volumeLength); }
        catch (OverflowException) { return InvalidOpen("The supplied NTFS volume range overflows."); }
        if (volumeEnd > source.Length)
            return InvalidOpen("The supplied NTFS volume range exceeds the image boundary.");
        if (boot.BytesPerSector is not (512 or 1024 or 2048 or 4096) || boot.ClusterSizeBytes == 0 ||
            boot.ClusterSizeBytes % boot.BytesPerSector != 0 || boot.FileRecordSizeBytes is < 512 or > 1024 * 1024 ||
            boot.MftByteOffset < volumeOffset || boot.MftByteOffset > volumeEnd - boot.FileRecordSizeBytes)
            return InvalidOpen("NTFS boot-sector metadata contains invalid MFT geometry.");

        try
        {
            var expectedMftOffset = checked(volumeOffset + checked((long)(boot.MftCluster * boot.ClusterSizeBytes)));
            if (expectedMftOffset != boot.MftByteOffset)
                return InvalidOpen("NTFS MFT byte offset does not match its validated cluster location.");

            var firstRecordBytes = new byte[checked((int)boot.FileRecordSizeBytes)];
            source.ReadExactlyAt(boot.MftByteOffset, firstRecordBytes);
            var firstRecordResult = NtfsFileRecordParser.Parse(firstRecordBytes, boot.BytesPerSector);
            if (!firstRecordResult.IsValid || firstRecordResult.Record is null)
                return InvalidOpen("MFT record 0 failed FILE-record validation: " + string.Join("; ", firstRecordResult.Issues));
            if (firstRecordResult.Record.RecordNumber != 0)
                return InvalidOpen("The FILE record at the MFT base does not identify itself as record 0.");

            var attributesResult = NtfsAttributeParser.Parse(firstRecordResult.Record);
            if (!attributesResult.IsValid)
                return InvalidOpen("MFT record 0 attributes are invalid: " + string.Join("; ", attributesResult.Issues));

            var dataAttributes = attributesResult.Attributes
                .Where(a => a.Type == DataAttributeType && a.IsNonResident && a.Name.Length == 0 && a.LowestVcn == 0)
                .ToArray();
            if (dataAttributes.Length != 1)
                return InvalidOpen("MFT record 0 must contain exactly one unnamed non-resident $DATA attribute starting at VCN 0.");

            var data = dataAttributes[0];
            if ((data.Flags & (0x0001 | 0x4000 | 0x8000)) != 0)
                return InvalidOpen("Compressed, encrypted, or sparse $MFT data attributes are not supported.");
            if (data.MappingPairs is null || data.RealSize is null || data.InitializedSize is null ||
                data.AllocatedSize is null || data.HighestVcn is null)
                return InvalidOpen("The unnamed $MFT::$DATA attribute is missing required size or mapping-pairs metadata.");

            var volumeClusters = (ulong)(volumeLength / boot.ClusterSizeBytes);
            var runlist = NtfsRunlistParser.Parse(data.MappingPairs, volumeClusters);
            if (!runlist.IsValid)
                return InvalidOpen("The $MFT mapping pairs are invalid: " + string.Join("; ", runlist.Issues));
            if (runlist.Runs.Count == 0 || runlist.TotalClusters == 0)
                return InvalidOpen("The $MFT mapping pairs contain no allocated extents.");
            if (runlist.TotalClusters - 1 != data.HighestVcn.Value)
                return InvalidOpen("$MFT highest VCN does not match the decoded mapping-pairs extent.");
            if (data.InitializedSize.Value > data.RealSize.Value || data.RealSize.Value > data.AllocatedSize.Value)
                return InvalidOpen("$MFT real and initialized size fields are inconsistent.");
            var mappedBytes = checked(runlist.TotalClusters * boot.ClusterSizeBytes);
            if (mappedBytes < boot.FileRecordSizeBytes)
                return InvalidOpen("The initial $MFT extent is too small to contain a complete FILE record.");

            return new(true,
                new NtfsMftReader(source, volumeOffset, volumeLength, boot, runlist.Runs, data.InitializedSize.Value, mappedBytes),
                Array.Empty<string>());
        }
        catch (Exception ex) when (ex is IOException or OverflowException or ArgumentOutOfRangeException)
        {
            return InvalidOpen("Unable to safely read the MFT base record: " + ex.Message);
        }
    }

    public NtfsMftRecordReadResult ReadRecord(ulong recordNumber)
    {
        ulong logicalOffset;
        try { logicalOffset = checked(recordNumber * _boot.FileRecordSizeBytes); }
        catch (OverflowException) { return InvalidRecord("Requested MFT record offset overflows."); }

        var readableBytes = Math.Min(_mftInitializedSize, _mftMappedBytes);
        if (logicalOffset > readableBytes ||
            _boot.FileRecordSizeBytes > readableBytes - logicalOffset)
            return InvalidRecord("Requested MFT record is beyond the initialized $MFT data range.");

        try
        {
            var raw = new byte[checked((int)_boot.FileRecordSizeBytes)];
            ReadMftBytes(logicalOffset, raw);
            var parsed = NtfsFileRecordParser.Parse(raw, _boot.BytesPerSector);
            if (!parsed.IsValid || parsed.Record is null)
                return InvalidRecord("MFT record failed FILE validation: " + string.Join("; ", parsed.Issues));
            if (parsed.Record.RecordNumber != recordNumber)
                return InvalidRecord("MFT record number does not match the requested record index.");
            return new(true, parsed.Record, Array.Empty<string>());
        }
        catch (Exception ex) when (ex is IOException or OverflowException or ArgumentOutOfRangeException)
        {
            return InvalidRecord("Unable to safely read requested MFT record: " + ex.Message);
        }
    }

    /// <summary>
    /// Reads an MFT reference only when its record is covered by the currently
    /// validated initial $MFT extent, then verifies sequence and base-record identity.
    /// This method does not resolve continuation extents.
    /// </summary>
    public NtfsMftRecordReadResult ReadReferencedRecord(
        NtfsFileReference reference,
        NtfsFileReference expectedBaseReference)
    {
        if (reference.IsNull)
            return InvalidRecord("A null MFT file reference cannot identify a continuation record.");
        if (reference.RecordNumber > uint.MaxValue)
            return InvalidRecord("Referenced MFT record number exceeds the supported FILE-record number range.");

        var result = ReadRecord(reference.RecordNumber);
        if (!result.IsValid || result.Record is null)
            return result;
        if (result.Record.SequenceNumber != reference.SequenceNumber)
            return InvalidRecord("Referenced MFT record sequence number does not match the attribute-list reference.");
        if (result.Record.BaseFileReference != expectedBaseReference)
            return InvalidRecord("Referenced MFT record base-file reference does not match the expected base record.");
        return result;
    }

    private void ReadMftBytes(ulong logicalOffset, Span<byte> destination)
    {
        var completed = 0;
        while (completed < destination.Length)
        {
            var current = checked(logicalOffset + (uint)completed);
            var vcn = current / _boot.ClusterSizeBytes;
            var clusterOffset = (uint)(current % _boot.ClusterSizeBytes);
            var run = _runs.FirstOrDefault(r => vcn >= r.StartingVcn && vcn - r.StartingVcn < r.ClusterCount)
                ?? throw new IOException("MFT logical offset is not covered by a validated data run.");

            var clustersRemaining = run.ClusterCount - (vcn - run.StartingVcn);
            var bytesAvailableInRun = checked(clustersRemaining * _boot.ClusterSizeBytes - clusterOffset);
            var count = checked((int)Math.Min((ulong)(destination.Length - completed), bytesAvailableInRun));
            var target = destination.Slice(completed, count);

            if (run.IsSparse)
            {
                target.Clear();
            }
            else
            {
                var lcn = checked(run.StartingLcn!.Value + (vcn - run.StartingVcn));
                var relative = checked(lcn * _boot.ClusterSizeBytes + clusterOffset);
                var physical = checked((ulong)_volumeOffset + relative);
                var volumeEnd = checked((ulong)(_volumeOffset + _volumeLength));
                if (physical > volumeEnd || (ulong)count > volumeEnd - physical || physical > long.MaxValue)
                    throw new IOException("MFT physical extent exceeds the supplied volume boundary.");
                _source.ReadExactlyAt((long)physical, target);
            }

            completed = checked(completed + count);
        }
    }

    private static NtfsMftOpenResult InvalidOpen(string issue) =>
        new(false, null, new[] { issue });

    private static NtfsMftRecordReadResult InvalidRecord(string issue) =>
        new(false, null, new[] { issue });
}
