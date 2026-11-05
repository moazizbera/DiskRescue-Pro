using System.Buffers.Binary;

namespace DiskRescue.Filesystems;

public sealed record NtfsFileRecord(
    ushort SequenceNumber,
    ushort LinkCount,
    ushort Flags,
    uint BytesInUse,
    uint BytesAllocated,
    ushort FirstAttributeOffset,
    uint RecordNumber,
    byte[] FixedUpBytes);

public sealed record NtfsFileRecordParseResult(
    bool IsValid,
    NtfsFileRecord? Record,
    IReadOnlyList<string> Issues);

/// <summary>
/// Validates and applies the NTFS update-sequence-array fixups to one FILE record.
/// This parser does not follow attributes or read any other disk structures.
/// </summary>
public static class NtfsFileRecordParser
{
    private const int MinimumRecordHeaderSize = 48;
    private const uint MaximumRecordSize = 1024 * 1024;
    private static ReadOnlySpan<byte> FileSignature => "FILE"u8;

    public static NtfsFileRecordParseResult Parse(ReadOnlySpan<byte> rawRecord, ushort bytesPerSector)
    {
        if (bytesPerSector is not (512 or 1024 or 2048 or 4096))
            return Invalid("NTFS sector size is unsupported.");
        if (rawRecord.Length < MinimumRecordHeaderSize || rawRecord.Length > MaximumRecordSize)
            return Invalid("NTFS FILE record length is outside the supported bounds.");
        if (rawRecord.Length % bytesPerSector != 0)
            return Invalid("NTFS FILE record length is not a multiple of the sector size.");
        if (!rawRecord[..4].SequenceEqual(FileSignature))
            return Invalid("NTFS FILE record signature is missing.");

        var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(rawRecord.Slice(4, 2));
        var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(rawRecord.Slice(6, 2));
        var expectedUsaCount = (rawRecord.Length / bytesPerSector) + 1;
        if (usaCount != expectedUsaCount)
            return Invalid("NTFS update-sequence-array count does not match the FILE record sector count.");
        if (usaOffset < MinimumRecordHeaderSize || (usaOffset & 1) != 0)
            return Invalid("NTFS update-sequence array overlaps the FILE record header or is not 2-byte aligned.");

        int usaLength;
        int usaEnd;
        try
        {
            usaLength = checked(usaCount * sizeof(ushort));
            usaEnd = checked(usaOffset + usaLength);
        }
        catch (OverflowException)
        {
            return Invalid("NTFS update-sequence-array range overflows.");
        }
        if (usaEnd > rawRecord.Length)
            return Invalid("NTFS update-sequence array extends beyond the FILE record.");

        var firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(rawRecord.Slice(20, 2));
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(rawRecord.Slice(22, 2));
        var bytesInUse = BinaryPrimitives.ReadUInt32LittleEndian(rawRecord.Slice(24, 4));
        var bytesAllocated = BinaryPrimitives.ReadUInt32LittleEndian(rawRecord.Slice(28, 4));
        var recordNumber = BinaryPrimitives.ReadUInt32LittleEndian(rawRecord.Slice(44, 4));

        if (bytesAllocated != rawRecord.Length)
            return Invalid("NTFS FILE record allocated-size field does not match the supplied record length.");
        if (bytesInUse < MinimumRecordHeaderSize || bytesInUse > bytesAllocated)
            return Invalid("NTFS FILE record bytes-in-use value is invalid.");
        if (firstAttributeOffset < usaEnd || firstAttributeOffset > bytesInUse - sizeof(uint) || (firstAttributeOffset & 7) != 0)
            return Invalid("NTFS first-attribute offset is invalid or overlaps the update-sequence array.");

        var fixedBytes = rawRecord.ToArray();
        var updateSequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(rawRecord.Slice(usaOffset, 2));
        for (var sectorIndex = 1; sectorIndex < usaCount; sectorIndex++)
        {
            var trailerOffset = checked(sectorIndex * bytesPerSector - sizeof(ushort));
            var trailer = BinaryPrimitives.ReadUInt16LittleEndian(rawRecord.Slice(trailerOffset, 2));
            if (trailer != updateSequenceNumber)
                return Invalid($"NTFS FILE record sector {sectorIndex} failed update-sequence fixup validation.");
            var replacementOffset = checked(usaOffset + sectorIndex * sizeof(ushort));
            var replacement = rawRecord.Slice(replacementOffset, sizeof(ushort));
            replacement.CopyTo(fixedBytes.AsSpan(trailerOffset, sizeof(ushort)));
        }

        var record = new NtfsFileRecord(
            BinaryPrimitives.ReadUInt16LittleEndian(rawRecord.Slice(16, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(rawRecord.Slice(18, 2)),
            flags,
            bytesInUse,
            bytesAllocated,
            firstAttributeOffset,
            recordNumber,
            fixedBytes);
        return new(true, record, Array.Empty<string>());
    }

    private static NtfsFileRecordParseResult Invalid(string issue) =>
        new(false, null, new[] { issue });
}
