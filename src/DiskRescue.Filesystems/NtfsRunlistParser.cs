using System.Buffers.Binary;

namespace DiskRescue.Filesystems;

public sealed record NtfsDataRun(ulong StartingVcn, ulong ClusterCount, ulong? StartingLcn)
{
    public bool IsSparse => StartingLcn is null;
}

public sealed record NtfsRunlistParseResult(
    bool IsValid,
    IReadOnlyList<NtfsDataRun> Runs,
    ulong TotalClusters,
    IReadOnlyList<string> Issues);

/// <summary>
/// Decodes NTFS mapping pairs with strict byte, integer, and volume-cluster bounds.
/// Sparse runs have no physical LCN. No reads or writes are performed.
/// </summary>
public static class NtfsRunlistParser
{
    private const int MaximumRuns = 1_000_000;

    public static NtfsRunlistParseResult Parse(ReadOnlySpan<byte> mappingPairs, ulong volumeClusterCount)
    {
        if (mappingPairs.IsEmpty)
            return Invalid("NTFS mapping-pairs data is empty.");
        if (volumeClusterCount == 0)
            return Invalid("NTFS volume cluster count is zero.");

        var runs = new List<NtfsDataRun>();
        var offset = 0;
        ulong currentVcn = 0;
        long currentLcn = 0;
        var terminated = false;

        while (offset < mappingPairs.Length)
        {
            var header = mappingPairs[offset++];
            if (header == 0)
            {
                terminated = true;
                break;
            }

            if (runs.Count >= MaximumRuns)
                return Invalid("NTFS runlist exceeds the one-million-run safety limit.");

            var lengthBytes = header & 0x0F;
            var offsetBytes = header >> 4;
            if (lengthBytes is < 1 or > 8 || offsetBytes > 8)
                return Invalid("NTFS mapping-pairs header has invalid length or offset widths.");
            if (lengthBytes + offsetBytes > mappingPairs.Length - offset)
                return Invalid("NTFS mapping-pairs entry is truncated.");

            var runLength = ReadUnsigned(mappingPairs.Slice(offset, lengthBytes));
            offset += lengthBytes;
            if (runLength == 0)
                return Invalid("NTFS runlist contains a zero-length extent.");

            ulong? startingLcn;
            if (offsetBytes == 0)
            {
                startingLcn = null;
            }
            else
            {
                var delta = ReadSigned(mappingPairs.Slice(offset, offsetBytes));
                offset += offsetBytes;
                try { currentLcn = checked(currentLcn + delta); }
                catch (OverflowException) { return Invalid("NTFS runlist LCN delta overflows."); }
                if (currentLcn < 0)
                    return Invalid("NTFS runlist resolves to a negative LCN.");
                ulong lcn = (ulong)currentLcn;
                if (lcn >= volumeClusterCount || runLength > volumeClusterCount - lcn)
                    return Invalid("NTFS runlist extent extends beyond the volume cluster range.");
                startingLcn = lcn;
            }

            try { currentVcn = checked(currentVcn + runLength); }
            catch (OverflowException) { return Invalid("NTFS runlist VCN range overflows."); }
            runs.Add(new NtfsDataRun(currentVcn - runLength, runLength, startingLcn));
        }

        if (!terminated)
            return Invalid("NTFS runlist has no zero terminator.");
        if (mappingPairs[(offset)..].IndexOfAnyExcept((byte)0) >= 0)
            return Invalid("NTFS runlist contains non-zero bytes after its terminator.");

        return new(true, runs, currentVcn, Array.Empty<string>());
    }

    private static ulong ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        for (var i = 0; i < bytes.Length; i++)
            value |= (ulong)bytes[i] << (8 * i);
        return value;
    }

    private static long ReadSigned(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (var i = 0; i < bytes.Length; i++)
            value |= (long)bytes[i] << (8 * i);
        var signBit = 1L << (bytes.Length * 8 - 1);
        if ((value & signBit) != 0 && bytes.Length < 8)
            value |= -1L << (bytes.Length * 8);
        return value;
    }

    private static NtfsRunlistParseResult Invalid(string issue) =>
        new(false, Array.Empty<NtfsDataRun>(), 0, new[] { issue });
}
