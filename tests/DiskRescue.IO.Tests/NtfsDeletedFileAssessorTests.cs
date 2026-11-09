using DiskRescue.Filesystems;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class NtfsDeletedFileAssessorTests
{
    private static NtfsMftEntry Entry() =>
        new(42, 7, 5, 3, "Archive.zip", 1, false, 4096, 2048, 0x20, true);

    private static NtfsFileRecord Record(uint number = 42, ushort sequence = 7,
        ushort flags = 0, NtfsFileReference? baseReference = null) =>
        new(sequence, 1, flags, 128, 1024, 56, number, baseReference ?? new(0, 0), new byte[1024]);

    private static NtfsFileName Name(string name = "Archive.zip", ulong parent = 5,
        ushort parentSequence = 3, byte nameNamespace = 1) =>
        new(parent, parentSequence, 0, 0, 0, 0, 4096, 2048, 0x20, 0, nameNamespace, name);

    private static NtfsFileRecord Parent() => Record(5, 3, flags: 0x0003);

    [Fact]
    public void ConfirmsDeletedMetadataOnlyWhenRecordNameAndParentIdentityMatch()
    {
        var result = NtfsDeletedFileAssessor.Assess(Entry(), Record(), Name(), Parent());

        Assert.Equal(NtfsDeletedMetadataStatus.Verified, result.MetadataStatus);
        Assert.True(result.MetadataVerified);
        Assert.True(result.IsEligibleForContentAssessment);
        Assert.Equal(NtfsDeletedContentStatus.NotAssessed, result.ContentStatus);
        Assert.Contains(result.Warnings, warning => warning.Contains("not been assessed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FlagsReusedMftRecordSequenceAsIdentityMismatch()
    {
        var result = NtfsDeletedFileAssessor.Assess(Entry(), Record(sequence: 8), Name(), Parent());

        Assert.Equal(NtfsDeletedMetadataStatus.RecordIdentityMismatch, result.MetadataStatus);
        Assert.False(result.IsEligibleForContentAssessment);
    }

    [Fact]
    public void RejectsRecordThatIsNoLongerMarkedDeleted()
    {
        var result = NtfsDeletedFileAssessor.Assess(Entry(), Record(flags: 1), Name(), Parent());

        Assert.Equal(NtfsDeletedMetadataStatus.NotMarkedDeleted, result.MetadataStatus);
    }

    [Fact]
    public void KeepsParentUnverifiedWhenParentRecordWasNotProvided()
    {
        var result = NtfsDeletedFileAssessor.Assess(Entry(), Record(), Name());

        Assert.Equal(NtfsDeletedMetadataStatus.ParentReferenceUnverified, result.MetadataStatus);
        Assert.False(result.IsEligibleForContentAssessment);
    }

    [Fact]
    public void RejectsStaleOrReusedParentSequence()
    {
        var result = NtfsDeletedFileAssessor.Assess(Entry(), Record(), Name(), Parent() with { SequenceNumber = 4 });

        Assert.Equal(NtfsDeletedMetadataStatus.StaleParentReference, result.MetadataStatus);
    }

    [Fact]
    public void RejectsFilenameMetadataThatDoesNotMatchDiscoveredEntry()
    {
        var result = NtfsDeletedFileAssessor.Assess(Entry(), Record(), Name(name: "Different.zip"), Parent());

        Assert.Equal(NtfsDeletedMetadataStatus.FileNameMismatch, result.MetadataStatus);
    }

    [Fact]
    public void DoesNotTreatExtensionRecordAsAnIndependentDeletedFile()
    {
        var result = NtfsDeletedFileAssessor.Assess(Entry(), Record(baseReference: new(1, 7)), Name(), Parent());

        Assert.Equal(NtfsDeletedMetadataStatus.ExtensionRecordNotIndependent, result.MetadataStatus);
    }
}
