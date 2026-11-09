using DiskRescue.Filesystems;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class NtfsDirectoryTreeBuilderTests
{
    [Fact]
    public void BuildsHierarchyOnlyWhenParentRecordAndSequenceMatch()
    {
        var entries = new[]
        {
            Entry(5, 7, 5, 7, "Root", true),
            Entry(10, 3, 5, 7, "Documents", true),
            Entry(11, 2, 10, 3, "Report.txt", false, 2048)
        };

        var result = NtfsDirectoryTreeBuilder.Build(entries);
        Assert.True(result.IsValid);
        Assert.Empty(result.Orphans);
        var root = Assert.Single(result.Roots);
        Assert.Equal("Root", root.Name);
        var directory = Assert.Single(root.Children);
        Assert.Equal("Documents", directory.Name);
        var file = Assert.Single(directory.Children);
        Assert.Equal("Report.txt", file.Name);
        Assert.Equal((ulong)2048, file.SizeBytes);
    }

    [Fact]
    public void KeepsEntriesWithStaleParentSequenceAsOrphans()
    {
        var entries = new[]
        {
            Entry(5, 7, 5, 7, "Root", true),
            Entry(11, 2, 5, 99, "Lost.txt", false)
        };

        var result = NtfsDirectoryTreeBuilder.Build(entries);
        Assert.True(result.IsValid);
        Assert.Empty(result.Roots[0].Children);
        var orphan = Assert.Single(result.Orphans);
        Assert.Equal("Lost.txt", orphan.Name);
        Assert.Contains(result.Issues, x => x.Contains("sequence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DetachesCyclicParentReferencesInsteadOfBuildingCycle()
    {
        var entries = new[]
        {
            Entry(5, 7, 5, 7, "Root", true),
            Entry(10, 1, 11, 1, "A", true),
            Entry(11, 1, 10, 1, "B", true),
            Entry(12, 1, 10, 1, "Child.txt", false)
        };

        var result = NtfsDirectoryTreeBuilder.Build(entries);
        Assert.True(result.IsValid);
        Assert.Equal(2, result.Orphans.Count);
        Assert.Contains(result.Orphans, x => x.Children.Any(child => child.Name == "Child.txt"));
        Assert.Contains(result.Issues, x => x.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PrefersWin32NameOverDosAlias()
    {
        var entries = new[]
        {
            Entry(5, 7, 5, 7, "Root", true),
            Entry(20, 4, 5, 7, "LONGFI~1.TXT", false, 1, 2),
            Entry(20, 4, 5, 7, "LongFileName.txt", false, 1, 1)
        };

        var result = NtfsDirectoryTreeBuilder.Build(entries);
        var root = Assert.Single(result.Roots);
        var file = Assert.Single(root.Children);
        Assert.Equal("LongFileName.txt", file.Name);
    }


    [Fact]
    public void ExcludesDeletedEntriesFromConfirmedDirectoryHierarchy()
    {
        var entries = new[]
        {
            Entry(5, 7, 5, 7, "Root", true),
            Entry(21, 6, 5, 7, "Deleted.txt", false, 128) with { IsDeleted = true },
            Entry(22, 2, 5, 7, "Live.txt", false, 256)
        };

        var result = NtfsDirectoryTreeBuilder.Build(entries);

        Assert.True(result.IsValid);
        var root = Assert.Single(result.Roots);
        var live = Assert.Single(root.Children);
        Assert.Equal("Live.txt", live.Name);
        Assert.Empty(result.Orphans);
        Assert.Contains(result.Issues, issue => issue.Contains("Excluded 1 deleted", StringComparison.Ordinal));
    }

    [Fact]
    public void PreservesDistinctHardLinkNamesForSameFileRecord()
    {
        var entries = new[]
        {
            Entry(5, 7, 5, 7, "Root", true),
            Entry(20, 4, 5, 7, "FirstName.txt", false, 1, 1),
            Entry(20, 4, 5, 7, "SecondName.txt", false, 1, 1)
        };

        var result = NtfsDirectoryTreeBuilder.Build(entries);
        var root = Assert.Single(result.Roots);
        Assert.Equal(2, root.Children.Count);
        Assert.Contains(root.Children, child => child.Name == "FirstName.txt");
        Assert.Contains(root.Children, child => child.Name == "SecondName.txt");
    }

    private static NtfsMftEntry Entry(
        ulong record,
        ushort sequence,
        ulong parent,
        ushort parentSequence,
        string name,
        bool directory,
        ulong size = 0,
        byte nameSpace = 1) =>
        new(record, sequence, parent, parentSequence, name, nameSpace, directory, size, size,
            directory ? 0x10u : 0x20u);
}
