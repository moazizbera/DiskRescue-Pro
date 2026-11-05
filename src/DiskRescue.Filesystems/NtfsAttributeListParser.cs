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
    private const int MinimumEntryLength = 26;
    private const int MaximumEntries = 65536;
    private const int MaximumValueBytes = 16 * 1024 * 1024;
    private const ulong FileReferenceMask = 0x0000_FFFF_FFFF_FFFF;

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

            var nameBytes = checked(nameLength * 2);
            string name = string.Empty;
            if (nameLength == 0)
            {
                if (nameOffset != 0)
                    return Invalid("$ATTRIBUTE_LIST unnamed entry has a non-zero name offset.");
            }
            else
            {
                if (nameOffset < MinimumEntryLength || nameOffset > length || nameBytes > length - nameOffset)
                    return Invalid("$ATTRIBUTE_LIST name range is outside its entry.");
                name = Encoding.Unicode.GetString(value.Slice(offset + nameOffset, nameBytes));
            }

            entries.Add(new NtfsAttributeListEntry(
                type,
                length,
                name,
                lowestVcn,
                fileReference & FileReferenceMask,
                (ushort)(fileReference >> 48),
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
