namespace DiskRescue.Filesystems;

public sealed record NtfsDirectoryNode(
    ulong RecordNumber,
    ushort SequenceNumber,
    string Name,
    bool IsDirectory,
    ulong SizeBytes,
    uint FileAttributes,
    IReadOnlyList<NtfsDirectoryNode> Children);

public sealed record NtfsDirectoryTreeResult(
    bool IsValid,
    IReadOnlyList<NtfsDirectoryNode> Roots,
    IReadOnlyList<NtfsDirectoryNode> Orphans,
    IReadOnlyList<string> Issues);

/// <summary>
/// Builds a conservative hierarchy from flat MFT filename entries.
/// Parent record numbers and sequence numbers must match; unresolved and cyclic
/// entries remain visible as orphans instead of being attached speculatively.
/// </summary>
public static class NtfsDirectoryTreeBuilder
{
    private const int MaximumEntries = 131072;

    public static NtfsDirectoryTreeResult Build(IReadOnlyList<NtfsMftEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > MaximumEntries)
            return Invalid("MFT tree input exceeds the 131072-entry safety limit.");

        var issues = new List<string>();
        var canonicalEntries = entries
            .GroupBy(e => (e.RecordNumber, e.ParentRecordNumber, e.ParentSequenceNumber, Name: e.Name.ToUpperInvariant()))
            .Select(g => g.OrderBy(e => NamespacePriority(e.Namespace)).First())
            .OrderBy(e => e.RecordNumber)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var nodes = canonicalEntries.Select(e => new NodeBuilder(e)).ToArray();
        var directoryByRecord = nodes
            .Where(n => n.Entry.IsDirectory)
            .GroupBy(n => n.Entry.RecordNumber)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => NamespacePriority(n.Entry.Namespace))
                .ThenBy(n => n.Entry.Name, StringComparer.OrdinalIgnoreCase).First());

        var roots = new List<NodeBuilder>();
        foreach (var node in nodes)
        {
            var entry = node.Entry;
            if (entry.RecordNumber == 5 && entry.IsDirectory)
            {
                if (entry.ParentRecordNumber == 5 && entry.ParentSequenceNumber == entry.SequenceNumber)
                {
                    roots.Add(node);
                    continue;
                }
                issues.Add("NTFS root-directory record 5 does not contain a matching self-parent reference.");
                node.IsOrphan = true;
                continue;
            }

            if (!directoryByRecord.TryGetValue(entry.ParentRecordNumber, out var parent))
            {
                issues.Add($"Record {entry.RecordNumber} has an unresolved parent directory record {entry.ParentRecordNumber}.");
                node.IsOrphan = true;
                continue;
            }
            if (parent.Entry.SequenceNumber != entry.ParentSequenceNumber)
            {
                issues.Add($"Record {entry.RecordNumber} parent sequence does not match directory record {entry.ParentRecordNumber}.");
                node.IsOrphan = true;
                continue;
            }
            node.Parent = parent;
        }

        var cyclicNodes = new HashSet<NodeBuilder>();
        foreach (var node in nodes)
        {
            var path = new HashSet<NodeBuilder>();
            var current = node;
            while (current.Parent is not null)
            {
                if (!path.Add(current))
                {
                    foreach (var member in path) cyclicNodes.Add(member);
                    break;
                }
                current = current.Parent;
            }
        }
        foreach (var node in cyclicNodes)
        {
            node.Parent = null;
            node.IsOrphan = true;
        }
        if (cyclicNodes.Count > 0)
            issues.Add($"Detected a parent-reference cycle involving {cyclicNodes.Count} MFT entries; cyclic links were detached.");

        foreach (var node in nodes)
        {
            if (node.Parent is not null)
                node.Parent.Children.Add(node);
        }

        foreach (var parentGroup in nodes.Where(n => n.Entry.IsDirectory).GroupBy(n => n.Entry.RecordNumber))
        {
            var duplicateNames = parentGroup.SelectMany(n => n.Children)
                .GroupBy(child => child.Entry.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);
            foreach (var duplicate in duplicateNames)
                issues.Add($"Directory record {parentGroup.Key} contains duplicate child name '{duplicate.Key}'.");
        }

        var rootResults = roots.Select(BuildImmutable).ToArray();
        var orphanResults = nodes.Where(n => n.IsOrphan && n.Parent is null)
            .Select(BuildImmutable).ToArray();
        return new(true, rootResults, orphanResults, issues);
    }

    private static int NamespacePriority(byte value) => value switch
    {
        1 => 0, // Win32
        3 => 1, // Win32 + DOS
        0 => 2, // POSIX
        2 => 3, // DOS short name
        _ => 4
    };

    private static NtfsDirectoryNode BuildImmutable(NodeBuilder node) =>
        new(node.Entry.RecordNumber, node.Entry.SequenceNumber, node.Entry.Name,
            node.Entry.IsDirectory, node.Entry.RealSize, node.Entry.FileAttributes,
            node.Children.Select(BuildImmutable).ToArray());

    private static NtfsDirectoryTreeResult Invalid(string issue) =>
        new(false, Array.Empty<NtfsDirectoryNode>(), Array.Empty<NtfsDirectoryNode>(), new[] { issue });

    private sealed class NodeBuilder(NtfsMftEntry entry)
    {
        public NtfsMftEntry Entry { get; } = entry;
        public NodeBuilder? Parent { get; set; }
        public bool IsOrphan { get; set; }
        public List<NodeBuilder> Children { get; } = new();
    }
}
