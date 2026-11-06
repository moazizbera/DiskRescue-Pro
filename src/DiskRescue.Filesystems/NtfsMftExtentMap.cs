namespace DiskRescue.Filesystems;

public sealed record NtfsMftExtentMapResult(
    bool IsValid,
    IReadOnlyList<NtfsDataRun> Runs,
    ulong TotalClusters,
    ulong MappedBytes,
    ulong InitializedSize,
    ulong RealSize,
    IReadOnlyList<string> Issues);

/// <summary>
/// Validates and assembles already-decoded unnamed $MFT::$DATA extent attributes.
/// This is deliberately an in-memory primitive: it does not locate, read, or
/// trust continuation FILE records. The caller must validate each source record
/// and attribute-list identity before supplying its attributes.
/// </summary>
public static class NtfsMftExtentMap
{
    public const int MaximumExtents = 4096;
    public const int MaximumRuns = 1_000_000;
    public const int MaximumMappingPairsBytes = 16 * 1024 * 1024;
    private const uint DataAttributeType = 0x80;

    public static NtfsMftExtentMapResult Assemble(
        IReadOnlyList<NtfsAttribute> extentAttributes,
        ulong volumeClusterCount,
        uint clusterSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(extentAttributes);

        if (extentAttributes.Count is < 1 or > MaximumExtents)
            return Invalid($"MFT extent count must be between 1 and {MaximumExtents}.");
        if (volumeClusterCount == 0 || clusterSizeBytes == 0)
            return Invalid("Volume cluster count and cluster size must be nonzero.");

        var ordered = extentAttributes
            .OrderBy(attribute => attribute?.LowestVcn ?? ulong.MaxValue)
            .ToArray();

        if (ordered.Any(attribute => attribute is null))
            return Invalid("MFT extent collection contains a null attribute.");

        var first = ordered[0];
        if (!IsSupportedMftDataAttribute(first) || first.LowestVcn != 0)
            return Invalid("MFT extent set must begin with an unnamed non-resident $DATA attribute at VCN 0.");
        if (first.AllocatedSize is null || first.RealSize is null || first.InitializedSize is null)
            return Invalid("The base MFT extent is missing stream size metadata.");
        if (first.InitializedSize.Value > first.RealSize.Value ||
            first.RealSize.Value > first.AllocatedSize.Value)
            return Invalid("Base MFT initialized, real, and allocated sizes are inconsistent.");

        var allRuns = new List<NtfsDataRun>();
        var totalMappingPairsBytes = 0;
        ulong nextVcn = 0;

        foreach (var attribute in ordered)
        {
            if (!IsSupportedMftDataAttribute(attribute))
                return Invalid("Every supplied extent must be an unnamed, non-resident, uncompressed $MFT::$DATA attribute.");
            // Attribute IDs are local to a FILE record and may differ between
            // continuation records. The attribute-list matcher validates each ID
            // against its own referenced record before this assembler is called.
            if (attribute.Flags != first.Flags)
                return Invalid("MFT extent attributes do not share the same flags.");
            if (attribute.LowestVcn is null || attribute.HighestVcn is null || attribute.MappingPairs is null)
                return Invalid("MFT extent is missing VCN or mapping-pairs metadata.");
            if (attribute.MappingPairs.Length > MaximumMappingPairsBytes - totalMappingPairsBytes)
                return Invalid($"MFT mapping-pairs data exceeds the {MaximumMappingPairsBytes}-byte aggregate safety limit.");
            totalMappingPairsBytes += attribute.MappingPairs.Length;
            if (attribute.LowestVcn.Value != nextVcn)
                return Invalid(attribute.LowestVcn.Value < nextVcn
                    ? "MFT extents overlap or duplicate a VCN range."
                    : "MFT extent map contains a VCN gap.");

            var runlist = NtfsRunlistParser.Parse(attribute.MappingPairs, volumeClusterCount);
            if (!runlist.IsValid)
                return Invalid("MFT extent mapping pairs are invalid: " + string.Join("; ", runlist.Issues));
            if (runlist.Runs.Count == 0 || runlist.Runs.Any(run => run.IsSparse))
                return Invalid("MFT extent mapping pairs must contain allocated, non-sparse runs.");
            if (runlist.Runs.Count > MaximumRuns - allRuns.Count)
                return Invalid($"MFT extent map exceeds the {MaximumRuns}-run safety limit.");

            ulong expectedExtentClusters;
            try { expectedExtentClusters = checked(attribute.HighestVcn.Value - attribute.LowestVcn.Value + 1); }
            catch (OverflowException) { return Invalid("MFT extent VCN range overflows."); }
            if (attribute.HighestVcn.Value < attribute.LowestVcn.Value ||
                runlist.TotalClusters != expectedExtentClusters)
                return Invalid("MFT extent highest VCN does not match its decoded mapping-pairs length.");

            foreach (var run in runlist.Runs)
            {
                ulong translatedVcn;
                try { translatedVcn = checked(attribute.LowestVcn.Value + run.StartingVcn); }
                catch (OverflowException) { return Invalid("MFT translated VCN overflows."); }
                allRuns.Add(new NtfsDataRun(translatedVcn, run.ClusterCount, run.StartingLcn));
            }

            try { nextVcn = checked(attribute.HighestVcn.Value + 1); }
            catch (OverflowException) { return Invalid("MFT extent end VCN overflows."); }
            if (nextVcn > volumeClusterCount)
                return Invalid("MFT extent map exceeds the volume cluster count.");
        }

        var physicalRuns = allRuns
            .OrderBy(run => run.StartingLcn)
            .ToArray();
        ulong previousPhysicalEnd = 0;
        var hasPreviousPhysicalRun = false;
        foreach (var run in physicalRuns)
        {
            if (run.StartingLcn is not ulong startingLcn)
                return Invalid("MFT extent map contains a sparse run.");
            ulong physicalEnd;
            try { physicalEnd = checked(startingLcn + run.ClusterCount); }
            catch (OverflowException) { return Invalid("MFT physical run range overflows."); }
            if (hasPreviousPhysicalRun && startingLcn < previousPhysicalEnd)
                return Invalid("MFT physical runs overlap or map the same volume clusters more than once.");
            previousPhysicalEnd = physicalEnd;
            hasPreviousPhysicalRun = true;
        }

        ulong mappedBytes;
        try { mappedBytes = checked(nextVcn * clusterSizeBytes); }
        catch (OverflowException) { return Invalid("MFT mapped byte length overflows."); }

        if (first.InitializedSize.Value > mappedBytes || first.RealSize.Value > mappedBytes ||
            first.AllocatedSize.Value > mappedBytes)
            return Invalid("Assembled MFT extent map does not cover the base stream size metadata.");

        return new(true, allRuns, nextVcn, mappedBytes,
            first.InitializedSize.Value, first.RealSize.Value, Array.Empty<string>());
    }

    private static bool IsSupportedMftDataAttribute(NtfsAttribute? attribute) =>
        attribute is not null &&
        attribute.Type == DataAttributeType &&
        attribute.IsNonResident &&
        attribute.Name is not null &&
        attribute.Name.Length == 0 &&
        attribute.Flags == 0;

    private static NtfsMftExtentMapResult Invalid(string issue) =>
        new(false, Array.Empty<NtfsDataRun>(), 0, 0, 0, 0, new[] { issue });
}
