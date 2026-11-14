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


    [Fact]
    public void AfterImageNamespaceFlush_RequiresFinalImageAndNoManifestEntries()
    {
        var valid = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ImageNamespaceFlushed,
            Image(), null, null, null));
        var missingImage = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ImageNamespaceFlushed,
            null, null, null, null));
        var prematureManifest = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ImageNamespaceFlushed,
            Image(), Manifest(), null, null));

        Assert.True(valid.Passed);
        Assert.False(missingImage.Passed);
        Assert.False(prematureManifest.Passed);
    }

    [Fact]
    public void Sha256ComparisonIsCaseInsensitive()
    {
        var lowerCaseImage = new PublicationRecoveryFileEvidence(4096, ImageHash.ToLowerInvariant());
        var lowerCaseManifest = new PublicationRecoveryFileEvidence(512, ManifestHash.ToLowerInvariant());

        var result = PublicationRecoveryEvidenceVerifier.Verify(Expected, new(
            PhysicalAcquisitionPublicationCheckpoint.ManifestNamespaceFlushed,
            lowerCaseImage, lowerCaseManifest, null, null));

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }


    [Fact]
    public void NullExpectedArgumentThrows()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PublicationRecoveryEvidenceVerifier.Verify(null!, new(
                PhysicalAcquisitionPublicationCheckpoint.ManifestNamespaceFlushed,
                Image(), Manifest(), null, null)));
    }

    [Fact]
    public void NullObservedSnapshotThrows()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PublicationRecoveryEvidenceVerifier.Verify(Expected, null!));
    }

    [Fact]
    public void EvidenceFactory_UsesByteLengthAndSha256ForInMemoryContent()
    {
        var evidence = PublicationRecoveryEvidenceFactory.FromBytes("abc"u8);

        Assert.Equal(3, evidence.Length);
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", evidence.Sha256);
    }

    [Fact]
    public void EvidenceFactory_HandlesEmptyInMemoryContent()
    {
        var evidence = PublicationRecoveryEvidenceFactory.FromBytes(ReadOnlySpan<byte>.Empty);

        Assert.Equal(0, evidence.Length);
        Assert.Equal("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", evidence.Sha256);
    }




    [Fact]
    public void SnapshotFactory_UsesOnlyExplicitInMemoryEntries()
    {
        byte[] image = [1, 2, 3, 4];
        byte[] manifest = [5, 6];

        var snapshot = PublicationRecoveryEvidenceSnapshotFactory.FromInMemoryEntries(
            PhysicalAcquisitionPublicationCheckpoint.ManifestNamespaceFlushed,
            imageFinal: image,
            manifestFinal: manifest);

        Assert.Equal(4, snapshot.ImageFinal!.Length);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image)), snapshot.ImageFinal.Sha256);
        Assert.Equal(2, snapshot.ManifestFinal!.Length);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(manifest)), snapshot.ManifestFinal.Sha256);
        Assert.Null(snapshot.ImageTemporary);
        Assert.Null(snapshot.ManifestTemporary);
    }

    [Fact]
    public void SnapshotFactory_PreservesAbsentEntries()
    {
        var snapshot = PublicationRecoveryEvidenceSnapshotFactory.FromInMemoryEntries(
            PhysicalAcquisitionPublicationCheckpoint.ImageDataFlushedBeforeRename);

        Assert.Null(snapshot.ImageFinal);
        Assert.Null(snapshot.ManifestFinal);
        Assert.Null(snapshot.ImageTemporary);
        Assert.Null(snapshot.ManifestTemporary);
    }


    [Fact]
    public void SnapshotFactory_PreservesCheckpointAndHashesTemporaryEntries()
    {
        byte[] temporaryImage = [9, 8, 7];
        byte[] temporaryManifest = [6, 5];

        var snapshot = PublicationRecoveryEvidenceSnapshotFactory.FromInMemoryEntries(
            PhysicalAcquisitionPublicationCheckpoint.ManifestRenamedBeforeNamespaceFlush,
            imageFinal: new byte[] { 1 },
            imageTemporary: temporaryImage,
            manifestTemporary: temporaryManifest);

        Assert.Equal(PhysicalAcquisitionPublicationCheckpoint.ManifestRenamedBeforeNamespaceFlush, snapshot.Checkpoint);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(temporaryImage)), snapshot.ImageTemporary!.Sha256);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(temporaryManifest)), snapshot.ManifestTemporary!.Sha256);
        Assert.Null(snapshot.ManifestFinal);
    }

    [Fact]
    public void SnapshotFactory_EvidenceDoesNotChangeWhenCallerMutatesSourceBuffers()
    {
        byte[] image = [1, 2, 3];
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image));

        var snapshot = PublicationRecoveryEvidenceSnapshotFactory.FromInMemoryEntries(
            PhysicalAcquisitionPublicationCheckpoint.ImageRenamedBeforeNamespaceFlush,
            imageFinal: image);

        Array.Fill(image, (byte)0);

        Assert.Equal(expectedHash, snapshot.ImageFinal!.Sha256);
        Assert.Equal(3, snapshot.ImageFinal.Length);
    }


    [Theory]
    [InlineData(PhysicalAcquisitionPublicationCheckpoint.ImageDataFlushedBeforeRename)]
    [InlineData(PhysicalAcquisitionPublicationCheckpoint.ImageRenamedBeforeNamespaceFlush)]
    [InlineData(PhysicalAcquisitionPublicationCheckpoint.ImageNamespaceFlushed)]
    [InlineData(PhysicalAcquisitionPublicationCheckpoint.ManifestRenamedBeforeNamespaceFlush)]
    [InlineData(PhysicalAcquisitionPublicationCheckpoint.ManifestNamespaceFlushed)]
    public void SnapshotFactory_OutputVerifiesAcrossEveryPublicationCheckpoint(
        PhysicalAcquisitionPublicationCheckpoint checkpoint)
    {
        byte[] image = [10, 20, 30, 40];
        byte[] manifest = [50, 60, 70];
        var imageEvidence = PublicationRecoveryEvidenceFactory.FromBytes(image);
        var manifestEvidence = PublicationRecoveryEvidenceFactory.FromBytes(manifest);
        var expected = new PublicationRecoveryExpectation(
            imageEvidence.Length, imageEvidence.Sha256,
            manifestEvidence.Length, manifestEvidence.Sha256);

        var snapshot = checkpoint switch
        {
            PhysicalAcquisitionPublicationCheckpoint.ImageDataFlushedBeforeRename =>
                PublicationRecoveryEvidenceSnapshotFactory.FromInMemoryEntries(
                    checkpoint, imageTemporary: image),
            PhysicalAcquisitionPublicationCheckpoint.ImageRenamedBeforeNamespaceFlush =>
                PublicationRecoveryEvidenceSnapshotFactory.FromInMemoryEntries(
                    checkpoint, imageFinal: image),
            PhysicalAcquisitionPublicationCheckpoint.ImageNamespaceFlushed =>
                PublicationRecoveryEvidenceSnapshotFactory.FromInMemoryEntries(
                    checkpoint, imageFinal: image),
            PhysicalAcquisitionPublicationCheckpoint.ManifestRenamedBeforeNamespaceFlush =>
                PublicationRecoveryEvidenceSnapshotFactory.FromInMemoryEntries(
                    checkpoint, imageFinal: image, manifestTemporary: manifest),
            PhysicalAcquisitionPublicationCheckpoint.ManifestNamespaceFlushed =>
                PublicationRecoveryEvidenceSnapshotFactory.FromInMemoryEntries(
                    checkpoint, imageFinal: image, manifestFinal: manifest),
            _ => throw new ArgumentOutOfRangeException(nameof(checkpoint))
        };

        var result = PublicationRecoveryEvidenceVerifier.Verify(expected, snapshot);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Findings));
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void SnapshotFactory_OutputFailsVerificationWhenSuppliedBytesDifferFromExpectation()
    {
        byte[] expectedImage = [1, 2, 3];
        byte[] observedImage = [1, 2, 4];
        byte[] manifest = [5, 6];

        var imageExpectation = PublicationRecoveryEvidenceFactory.FromBytes(expectedImage);
        var manifestExpectation = PublicationRecoveryEvidenceFactory.FromBytes(manifest);
        var expected = new PublicationRecoveryExpectation(
            imageExpectation.Length, imageExpectation.Sha256,
            manifestExpectation.Length, manifestExpectation.Sha256);
        var snapshot = PublicationRecoveryEvidenceSnapshotFactory.FromInMemoryEntries(
            PhysicalAcquisitionPublicationCheckpoint.ManifestNamespaceFlushed,
            imageFinal: observedImage,
            manifestFinal: manifest);

        var result = PublicationRecoveryEvidenceVerifier.Verify(expected, snapshot);

        Assert.False(result.Passed);
        Assert.Contains(result.Findings, finding =>
            finding.Contains("SHA-256", StringComparison.OrdinalIgnoreCase));
    }

}
