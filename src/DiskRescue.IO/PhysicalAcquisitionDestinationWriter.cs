namespace DiskRescue.IO;

/// <summary>
/// Result of writing and publishing an image plus its sidecar through one
/// acquisition-scoped destination session.
/// </summary>
internal sealed record PhysicalAcquisitionDestinationWriteResult(
    string ImageLeafName,
    long BytesCopied,
    string Sha256,
    string ManifestLeafName);

/// <summary>
/// Reports that the verified image was committed through the destination session,
/// but the manifest could not be committed. The image is deliberately preserved.
/// </summary>
internal sealed class PhysicalAcquisitionManifestCommitException : IOException
{
    public PhysicalAcquisitionManifestCommitException(
        string imageLeafName,
        string manifestLeafName,
        string sha256,
        Exception innerException)
        : base(
            $"The verified image '{imageLeafName}' was published (SHA-256 {sha256}), " +
            $"but manifest '{manifestLeafName}' could not be published. The image was preserved.",
            innerException)
    {
        ImageLeafName = imageLeafName;
        ManifestLeafName = manifestLeafName;
        Sha256 = sha256;
    }

    public string ImageLeafName { get; }
    public string ManifestLeafName { get; }
    public string Sha256 { get; }
}

/// <summary>
/// A destination entry was published, but the volume-wide namespace flush failed.
/// The published entry is preserved; power-loss durability is unconfirmed.
/// </summary>
internal sealed class PhysicalAcquisitionPublicationDurabilityException : IOException
{
    public PhysicalAcquisitionPublicationDurabilityException(string publishedLeafName, Exception innerException)
        : base(
            $"Destination entry '{publishedLeafName}' was published, but the destination volume flush failed. " +
            "The entry was preserved; namespace durability is unconfirmed.",
            innerException)
    {
        PublishedLeafName = publishedLeafName;
    }

    public string PublishedLeafName { get; }
}

/// <summary>
/// Coordinates image and manifest publication through the same pinned destination
/// session. This class accepts no destination paths; the session owns all filesystem
/// operations and stream lifetimes.
/// </summary>
internal static class PhysicalAcquisitionDestinationWriter
{
    public static Task<PhysicalAcquisitionDestinationWriteResult> WriteAndPublishAsync(
        IReadOnlyRandomAccessSource source,
        IPhysicalAcquisitionDestinationSession session,
        string imageTemporaryLeaf,
        string imageFinalLeaf,
        string manifestTemporaryLeaf,
        string manifestFinalLeaf,
        object manifest,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return WriteAndPublishAsync(
            source, session, imageTemporaryLeaf, imageFinalLeaf,
            manifestTemporaryLeaf, manifestFinalLeaf, _ => manifest,
            progress, cancellationToken);
    }

    public static async Task<PhysicalAcquisitionDestinationWriteResult> WriteAndPublishAsync(
        IReadOnlyRandomAccessSource source,
        IPhysicalAcquisitionDestinationSession session,
        string imageTemporaryLeaf,
        string imageFinalLeaf,
        string manifestTemporaryLeaf,
        string manifestFinalLeaf,
        Func<DiskImageContentResult, object> manifestFactory,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(manifestFactory);

        var directoryIdentity = session.DirectoryIdentity;
        if (string.IsNullOrWhiteSpace(directoryIdentity.VolumeIdentity) ||
            (directoryIdentity.FileIdHigh == 0 && directoryIdentity.FileIdLow == 0))
        {
            throw new IOException("The destination session did not provide a verifiable directory identity.");
        }
        if (string.IsNullOrWhiteSpace(session.VolumeGuidPath))
            throw new IOException("The destination session did not provide the volume-GUID root resolved from its pinned handle.");

        PhysicalDestinationLeafName.Validate(imageTemporaryLeaf, nameof(imageTemporaryLeaf));
        PhysicalDestinationLeafName.Validate(imageFinalLeaf, nameof(imageFinalLeaf));
        PhysicalDestinationLeafName.Validate(manifestTemporaryLeaf, nameof(manifestTemporaryLeaf));
        PhysicalDestinationLeafName.Validate(manifestFinalLeaf, nameof(manifestFinalLeaf));

        var names = new[] { imageTemporaryLeaf, imageFinalLeaf, manifestTemporaryLeaf, manifestFinalLeaf };
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new ArgumentException("Image and manifest temporary/final names must all be distinct.");

        Stream? imageStream = null;
        var imagePublished = false;
        DiskImageContentResult image;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            imageStream = await session.CreateNewFileAsync(imageTemporaryLeaf, cancellationToken).ConfigureAwait(false);
            await EnsureOwnedVolume(session, imageStream, cancellationToken).ConfigureAwait(false);
            image = await DiskImager.WriteImageAsync(
                source, imageStream, progress, cancellationToken).ConfigureAwait(false);
            await session.FlushToDiskAsync(imageStream, cancellationToken).ConfigureAwait(false);
            await session.PublishNoReplaceAsync(
                imageStream, imageFinalLeaf, cancellationToken).ConfigureAwait(false);
            imagePublished = true;
            try
            {
                await session.FlushPublishedNamespaceAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception flushFailure)
            {
                throw new PhysicalAcquisitionPublicationDurabilityException(imageFinalLeaf, flushFailure);
            }
        }
        catch (Exception failure)
        {
            if (!imagePublished && imageStream is not null)
                await DeleteAfterFailureAsync(session, imageStream, failure).ConfigureAwait(false);
            throw;
        }

        // Once the verified image is published, finish sidecar publication without
        // cancellation, matching the existing acquisition commit semantics.
        Stream? manifestStream = null;
        var manifestPublished = false;
        try
        {
            manifestStream = await session.CreateNewFileAsync(
                manifestTemporaryLeaf, CancellationToken.None).ConfigureAwait(false);
            await EnsureOwnedVolume(session, manifestStream, CancellationToken.None).ConfigureAwait(false);
            var manifest = manifestFactory(image)
                ?? throw new InvalidOperationException("Manifest factory returned null after image publication.");
            await PhysicalDiskManifestPublisher.WriteManifestAsync(
                manifestStream, manifest, CancellationToken.None).ConfigureAwait(false);
            await session.FlushToDiskAsync(manifestStream, CancellationToken.None).ConfigureAwait(false);
            await session.PublishNoReplaceAsync(
                manifestStream, manifestFinalLeaf, CancellationToken.None).ConfigureAwait(false);
            manifestPublished = true;
            try
            {
                await session.FlushPublishedNamespaceAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception flushFailure)
            {
                throw new PhysicalAcquisitionPublicationDurabilityException(manifestFinalLeaf, flushFailure);
            }

            return new PhysicalAcquisitionDestinationWriteResult(
                imageFinalLeaf, image.BytesCopied, image.Sha256, manifestFinalLeaf);
        }
        catch (Exception failure)
        {
            if (manifestPublished)
                throw;

            if (manifestStream is not null)
                await DeleteAfterFailureAsync(session, manifestStream, failure).ConfigureAwait(false);

            throw new PhysicalAcquisitionManifestCommitException(
                imageFinalLeaf, manifestFinalLeaf, image.Sha256, failure);
        }
    }

    private static async Task EnsureOwnedVolume(
        IPhysicalAcquisitionDestinationSession session,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var identity = await session.GetFileIdentityAsync(stream, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(identity.VolumeIdentity) ||
            (identity.FileIdHigh == 0 && identity.FileIdLow == 0) ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                session.DirectoryIdentity.VolumeIdentity, identity.VolumeIdentity))
        {
            throw new IOException(
                "The newly created destination file is not on the pinned destination directory volume.");
        }
    }

    private static async Task DeleteAfterFailureAsync(
        IPhysicalAcquisitionDestinationSession session,
        Stream stream,
        Exception originalFailure)
    {
        try
        {
            await session.DeleteUnpublishedAsync(stream, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
        {
            throw new AggregateException(
                "Destination operation failed and its unpublished temporary file could not be removed.",
                originalFailure,
                cleanupFailure);
        }
    }
}
