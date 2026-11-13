using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class PublicationRecoveryEvidenceVerifierTests
{
    private const string ImageHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ManifestHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private static readonly PublicationRecoveryExpectation Expected = new(
        4096, ImageHash, 512, ManifestHash);

    private static PublicationRecoveryFileEvidence Image() => new(4096, ImageHash);
    private static PublicationRecoveryFileEvidence Manifest() => new(512, ManifestHash);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BeforeImageRename_AllowsTemporaryImageOrNoSurvivingEntry(bool temporarySurvived)
    {
        var result = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ImageDataFlushedBeforeRename,
            null, null, temporarySurvived ? Image() : null, null));

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void BeforeImageRename_RejectsPublishedFinalImage()
    {
        var result = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ImageDataFlushedBeforeRename,
            Image(), null, null, null));

        Assert.False(result.Passed);
        Assert.Contains(result.Findings, finding => finding.Contains("final image", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AfterImageRename_AcceptsFinalImageOrTemporaryImage()
    {
        var final = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ImageRenamedBeforeNamespaceFlush,
            Image(), null, null, null));
        var temporary = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ImageRenamedBeforeNamespaceFlush,
            null, null, Image(), null));
        var absent = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ImageRenamedBeforeNamespaceFlush,
            null, null, null, null));

        Assert.True(final.Passed);
        Assert.True(temporary.Passed);
        Assert.True(absent.Passed);
    }

    [Fact]
    public void AfterImageRename_RejectsBothImageNamesPresent()
    {
        var result = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ImageRenamedBeforeNamespaceFlush,
            Image(), null, Image(), null));

        Assert.False(result.Passed);
        Assert.Contains(result.Findings, finding => finding.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AfterManifestRename_RequiresDurableImageAndAllowsManifestToBeAbsent(bool manifestSurvived)
    {
        var result = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ManifestRenamedBeforeNamespaceFlush,
            Image(), manifestSurvived ? Manifest() : null, null, null));

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void AfterManifestRename_AcceptsManifestInEitherNameButNotBoth()
    {
        var final = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ManifestRenamedBeforeNamespaceFlush,
            Image(), Manifest(), null, null));
        var temporary = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ManifestRenamedBeforeNamespaceFlush,
            Image(), null, null, Manifest()));
        var both = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ManifestRenamedBeforeNamespaceFlush,
            Image(), Manifest(), null, Manifest()));

        Assert.True(final.Passed);
        Assert.True(temporary.Passed);
        Assert.False(both.Passed);
    }

    [Fact]
    public void AfterManifestFlush_RequiresBothFinalEntriesAndNoTemporaryEntries()
    {
        var valid = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ManifestNamespaceFlushed,
            Image(), Manifest(), null, null));
        var missingManifest = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ManifestNamespaceFlushed,
            Image(), null, null, null));
        var temporaryImage = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ManifestNamespaceFlushed,
            Image(), Manifest(), Image(), null));

        Assert.True(valid.Passed);
        Assert.False(missingManifest.Passed);
        Assert.False(temporaryImage.Passed);
    }

    [Fact]
    public void AnySurvivingEntryMustMatchExpectedLengthAndSha256()
    {
        var wrongImage = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ImageRenamedBeforeNamespaceFlush,
            new PublicationRecoveryFileEvidence(4095, ImageHash), null, null, null));
        var wrongManifest = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ManifestRenamedBeforeNamespaceFlush,
            Image(), new PublicationRecoveryFileEvidence(512, ImageHash), null, null));

        Assert.False(wrongImage.Passed);
        Assert.False(wrongManifest.Passed);
        Assert.Contains(wrongImage.Findings, finding => finding.Contains("length", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wrongManifest.Findings, finding => finding.Contains("SHA-256", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidExpectedHashFailsClosed()
    {
        var invalid = Expected with { ImageSha256 = "not-a-hash" };
        var result = PublicationRecoveryEvidenceVerifier.Verify(invalid, new(
            PhysicalAcquisitionPublicationCheckpoint.ManifestNamespaceFlushed,
            Image(), Manifest(), null, null));

        Assert.False(result.Passed);
        Assert.Contains(result.Findings, finding => finding.Contains("64 hexadecimal", StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public void BeforeImageRename_RejectsTemporaryManifest()
    {
        var result = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ImageDataFlushedBeforeRename,
            null, null, Image(), Manifest()));

        Assert.False(result.Passed);
        Assert.Contains(result.Findings, finding => finding.Contains("temporary manifest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AfterManifestRename_RejectsTemporaryImageEvenWhenImageFinalExists()
    {
        var result = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ManifestRenamedBeforeNamespaceFlush,
            Image(), null, Image(), null));

        Assert.False(result.Passed);
        Assert.Contains(result.Findings, finding => finding.Contains("temporary image", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AfterManifestFlush_RejectsTemporaryManifest()
    {
        var result = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ManifestNamespaceFlushed,
            Image(), Manifest(), null, Manifest()));

        Assert.False(result.Passed);
        Assert.Contains(result.Findings, finding => finding.Contains("temporary manifest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SurvivingTemporaryEntriesMustAlsoMatchExpectedEvidence()
    {
        var wrongTemporaryImage = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ImageRenamedBeforeNamespaceFlush,
            null, null, new PublicationRecoveryFileEvidence(4096, ManifestHash), null));
        var wrongTemporaryManifest = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ManifestRenamedBeforeNamespaceFlush,
            Image(), null, null, new PublicationRecoveryFileEvidence(511, ManifestHash)));

        Assert.False(wrongTemporaryImage.Passed);
        Assert.False(wrongTemporaryManifest.Passed);
        Assert.Contains(wrongTemporaryImage.Findings, finding => finding.Contains("temporary image", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wrongTemporaryManifest.Findings, finding => finding.Contains("temporary manifest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NegativeExpectedLengthFailsClosed()
    {
        var invalid = Expected with { ImageLength = -1 };
        var result = PublicationRecoveryEvidenceVerifier.Verify(invalid, new(
            PhysicalAcquisitionPublicationCheckpoint.ManifestNamespaceFlushed,
            Image(), Manifest(), null, null));

        Assert.False(result.Passed);
        Assert.Contains(result.Findings, finding => finding.Contains("negative", StringComparison.OrdinalIgnoreCase));
    }


}
