using System.Buffers.Binary;
using System.Text;

namespace DiskRescue.Filesystems;

public sealed record NtfsAttribute(
    uint Type,
    uint Length,
    bool IsNonResident,
    ushort AttributeId,
    string Name,
    uint? ResidentValueLength,
    ushort? ResidentValueOffset,
    ulong? LowestVcn,
    ulong? HighestVcn,
    ushort? MappingPairsOffset,
    ulong? AllocatedSize,
    ulong? RealSize,
    ulong? InitializedSize);

public sealed record NtfsAttributeParseResult(
    bool IsValid,
    IReadOnlyList<NtfsAttribute> Attributes,
    IReadOnlyList<string> Issues);

/// <summary>
/// Parses bounded NTFS attribute headers from an already validated and fixed-up FILE record.
/// Does not follow attribute lists, runlists, indexes, or external references.
/// </summary>
public static class NtfsAttributeParser
{
    private const uint EndMarker = 0xFFFFFFFF;
    private const int ResidentHeaderSize = 24;
    private const int NonResidentHeaderSize = 64;
    private const int MaximumAttributes = 4096;

    public static NtfsAttributeParseResult Parse(NtfsFileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var bytes = record.FixedUpBytes.AsSpan();
        if (bytes.Length < 48 || record.BytesInUse < 52 || record.BytesInUse > bytes.Length ||
            record.FirstAttributeOffset < 48 || record.FirstAttributeOffset > record.BytesInUse - 4)
            return Invalid("FILE record bounds or first-attribute offset are invalid.");

        var attributes = new List<NtfsAttribute>();
        var offset = (int)record.FirstAttributeOffset;
        while (offset <= record.BytesInUse - 4)
        {
            if (attributes.Count >= MaximumAttributes)
                return Invalid("FILE record contains more than 4096 attributes.");

            var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
            if (type == EndMarker)
                return new(true, attributes, Array.Empty<string>());

            if (record.BytesInUse - offset < ResidentHeaderSize)
                return Invalid("Attribute header is truncated.");

            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            var nonResidentFlag = bytes[offset + 8];
            var nameLength = bytes[offset + 9];
            var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset + 10, 2));
            var attributeId = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset + 14, 2));

            if (length < ResidentHeaderSize || (length & 7) != 0 || length > record.BytesInUse - offset)
                return Invalid("Attribute length is invalid, unaligned, or extends beyond bytes-in-use.");
            if (nonResidentFlag > 1)
                return Invalid("Attribute non-resident flag is invalid.");

            var attribute = bytes.Slice(offset, (int)length);
            var nameByteLength = checked(nameLength * 2);
            string name = string.Empty;
            if (nameLength > 0)
            {
                if (nameOffset < ResidentHeaderSize || nameOffset > length || nameByteLength > length - nameOffset)
                    return Invalid("Attribute name range is outside its attribute record.");
                name = Encoding.Unicode.GetString(attribute.Slice(nameOffset, nameByteLength));
            }

            if (nonResidentFlag == 0)
            {
                var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(attribute.Slice(16, 4));
                var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute.Slice(20, 2));
                if (valueOffset < ResidentHeaderSize || valueOffset > length || valueLength > length - valueOffset)
                    return Invalid("Resident attribute value range is outside its attribute record.");
                if (nameLength > 0 && RangesOverlap(nameOffset, nameByteLength, valueOffset, checked((int)valueLength)))
                    return Invalid("Resident attribute name overlaps its value.");
                attributes.Add(new(type, length, false, attributeId, name, valueLength, valueOffset,
                    null, null, null, null, null, null));
            }
            else
            {
                if (length < NonResidentHeaderSize)
                    return Invalid("Non-resident attribute header is truncated.");
                var lowestVcn = BinaryPrimitives.ReadUInt64LittleEndian(attribute.Slice(16, 8));
                var highestVcn = BinaryPrimitives.ReadUInt64LittleEndian(attribute.Slice(24, 8));
                var mappingPairsOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute.Slice(32, 2));
                var allocatedSize = BinaryPrimitives.ReadUInt64LittleEndian(attribute.Slice(40, 8));
                var realSize = BinaryPrimitives.ReadUInt64LittleEndian(attribute.Slice(48, 8));
                var initializedSize = BinaryPrimitives.ReadUInt64LittleEndian(attribute.Slice(56, 8));
                if (lowestVcn > highestVcn)
                    return Invalid("Non-resident attribute VCN range is reversed.");
                if (mappingPairsOffset < NonResidentHeaderSize || mappingPairsOffset >= length)
                    return Invalid("Non-resident mapping-pairs offset is outside the attribute record.");
                if (initializedSize > realSize || realSize > allocatedSize)
                    return Invalid("Non-resident attribute size fields are inconsistent.");
                if (nameLength > 0 && RangesOverlap(nameOffset, nameByteLength, mappingPairsOffset, (int)length - mappingPairsOffset))
                    return Invalid("Non-resident attribute name overlaps mapping pairs.");
                attributes.Add(new(type, length, true, attributeId, name, null, null,
                    lowestVcn, highestVcn, mappingPairsOffset, allocatedSize, realSize, initializedSize));
            }

            offset = checked(offset + (int)length);
        }

        return Invalid("FILE record has no attribute end marker within bytes-in-use.");
    }

    private static bool RangesOverlap(int aOffset, int aLength, int bOffset, int bLength) =>
        aLength > 0 && bLength > 0 && aOffset < bOffset + bLength && bOffset < aOffset + aLength;

    private static NtfsAttributeParseResult Invalid(string issue) =>
        new(false, Array.Empty<NtfsAttribute>(), new[] { issue });
}
