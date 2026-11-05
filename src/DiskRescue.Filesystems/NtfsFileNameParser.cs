using System.Buffers.Binary;
using System.Text;

namespace DiskRescue.Filesystems;

public sealed record NtfsFileName(
    ulong ParentRecordNumber,
    ushort ParentSequenceNumber,
    ulong CreationTime,
    ulong FileAlteredTime,
    ulong MftChangedTime,
    ulong AccessedTime,
    ulong AllocatedSize,
    ulong RealSize,
    uint FileAttributes,
    uint ReparseTag,
    byte Namespace,
    string Name);

public sealed record NtfsFileNameParseResult(
    bool IsValid,
    NtfsFileName? FileName,
    IReadOnlyList<string> Issues);

/// <summary>Parses the resident NTFS $FILE_NAME value without resolving parent records or indexes.</summary>
public static class NtfsFileNameParser
{
    private const uint FileNameAttributeType = 0x30;
    private const int FixedValueLength = 66;
    private const ulong ParentRecordMask = 0x0000FFFFFFFFFFFF;
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);

    public static NtfsFileNameParseResult Parse(NtfsAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        if (attribute.Type != FileNameAttributeType || attribute.IsNonResident)
            return Invalid("$FILE_NAME must be a resident NTFS attribute.");
        if (attribute.ResidentValue is null || attribute.ResidentValue.Length < FixedValueLength)
            return Invalid("$FILE_NAME value is truncated.");

        var value = attribute.ResidentValue.AsSpan();
        var nameLength = value[64];
        var nameNamespace = value[65];
        if (nameLength == 0)
            return Invalid("$FILE_NAME contains an empty name.");
        if (nameNamespace > 3)
            return Invalid("$FILE_NAME namespace value is unsupported.");

        int expectedLength;
        try { expectedLength = checked(FixedValueLength + nameLength * 2); }
        catch (OverflowException) { return Invalid("$FILE_NAME length overflows."); }
        if (value.Length != expectedLength)
            return Invalid("$FILE_NAME value length does not match its UTF-16 name length.");

        string name;
        try { name = StrictUtf16.GetString(value.Slice(FixedValueLength)); }
        catch (DecoderFallbackException) { return Invalid("$FILE_NAME contains invalid UTF-16 data."); }

        var parentReference = BinaryPrimitives.ReadUInt64LittleEndian(value[..8]);
        var parsed = new NtfsFileName(
            parentReference & ParentRecordMask,
            (ushort)(parentReference >> 48),
            BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(8, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(16, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(24, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(32, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(40, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(48, 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(56, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(60, 4)),
            nameNamespace,
            name);
        return new(true, parsed, Array.Empty<string>());
    }

    private static NtfsFileNameParseResult Invalid(string issue) =>
        new(false, null, new[] { issue });
}
