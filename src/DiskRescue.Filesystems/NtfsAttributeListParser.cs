using System.Buffers.Binary;
using System.Text;

namespace DiskRescue.Filesystems;

public sealed record NtfsAttributeListEntry(
    uint Type,
    ushort EntryLength,
    string Name,
    ulong LowestVcn,
    ulong FileRecordNumber,
    ushort FileSequenceNumber,
    ushort AttributeId);

public sealed record NtfsAttributeListParseResult(
    bool IsValid,
    IReadOnlyList<NtfsAttributeListEntry> Entries,
    IReadOnlyList<string> Issues);

/// <summary>
/// Parses an already-read NTFS $ATTRIBUTE_LIST value. This parser never follows
/// file references or reads additional MFT extents; callers must validate those
/// references independently before using them.
/// </summary>
public static class NtfsAttributeListParser
{
    public const uint AttributeListType = 0x20;
    public const int MaximumEntries = 65536;
    public const int MaximumValueBytes = 16 * 1024 * 1024;
    private const int MinimumEntryLength = 26;
    private const ulong FileReferenceMask = 0x0000_FFFF_FFFF_FFFF;
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);

    public static NtfsAttributeListParseResult Parse(NtfsAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        if (attribute.Type != AttributeListType)
            return Invalid("$ATTRIBUTE_LIST parser received an attribute of a different type.");
        if (attribute.IsNonResident)
            return Invalid("Non-resident $ATTRIBUTE_LIST streams are not supported.");
        if (attribute.ResidentValue is null || attribute.ResidentValueLength != attribute.ResidentValue.Length)
            return Invalid("$ATTRIBUTE_LIST resident value is missing.");
        return Parse(attribute.ResidentValue);
    }

    public static NtfsAttributeListParseResult Parse(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return Invalid("$ATTRIBUTE_LIST value is empty.");
        if (value.Length > MaximumValueBytes)
            return Invalid("$ATTRIBUTE_LIST value exceeds the 16 MiB safety limit.");

        var entries = new List<NtfsAttributeListEntry>();
        var offset = 0;
        while (offset < value.Length)
        {
            if (entries.Count >= MaximumEntries)
                return Invalid("$ATTRIBUTE_LIST exceeds the 65,536-entry safety limit.");
            if (value.Length - offset < MinimumEntryLength)
                return Invalid("$ATTRIBUTE_LIST ends with a truncated entry header.");

            var entry = value[offset..];
            var type = BinaryPrimitives.ReadUInt32LittleEndian(entry[..4]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(4, 2));
            var nameLength = entry[6];
            var nameOffset = entry[7];
            var lowestVcn = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(8, 8));
            var fileReference = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(16, 8));
            var attributeId = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(24, 2));

            if (length < MinimumEntryLength || (length & 7) != 0 || length > value.Length - offset)
                return Invalid("$ATTRIBUTE_LIST entry length is invalid, unaligned, or truncated.");
            if (type is 0 or uint.MaxValue)
                return Invalid("$ATTRIBUTE_LIST entry has an invalid attribute type.");

            var nameBytes = checked(nameLength * 2);
            string name = string.Empty;
            // Some writers leave a nonzero offset for the optional name when its length is zero.
            if (nameLength > 0)
            {
                if (nameOffset < MinimumEntryLength || (nameOffset & 1) != 0 || nameOffset > length || nameBytes > length - nameOffset)
                    return Invalid("$ATTRIBUTE_LIST name range is outside its entry.");
                try
                {
                    name = StrictUtf16.GetString(value.Slice(offset + nameOffset, nameBytes));
                }
                catch (DecoderFallbackException)
                {
                    return Invalid("$ATTRIBUTE_LIST name contains invalid UTF-16 data.");
                }
            }

            var fileRecordNumber = fileReference & FileReferenceMask;
            var fileSequenceNumber = (ushort)(fileReference >> 48);
            if (fileRecordNumber == 0 && fileSequenceNumber == 0)
                return Invalid("$ATTRIBUTE_LIST entry contains a null MFT file reference.");

            entries.Add(new NtfsAttributeListEntry(
                type,
                length,
                name,
                lowestVcn,
                fileRecordNumber,
                fileSequenceNumber,
                attributeId));
            offset = checked(offset + length);
        }

        return entries.Count == 0
            ? Invalid("$ATTRIBUTE_LIST contains no entries.")
            : new(true, entries, Array.Empty<string>());
    }

    private static NtfsAttributeListParseResult Invalid(string issue) =>
        new(false, Array.Empty<NtfsAttributeListEntry>(), new[] { issue });
}
