using System.Text.Json;

namespace DiskRescue.IO;

/// <summary>
/// Indicates that the image was already committed and verified, but its sidecar
/// manifest could not be published. The image is intentionally preserved.
/// </summary>
public sealed class PhysicalDiskManifestPublicationException : IOException
{
    public PhysicalDiskManifestPublicationException(
        string imagePath,
        string manifestPath,
        string sha256,
        Exception innerException)
        : base(
            $"The disk image was committed and verified at '{imagePath}' (SHA-256 {sha256}), " +
            $"but its acquisition manifest could not be published at '{manifestPath}'. " +
            "The verified image has been preserved; do not repeat acquisition to the same path.",
            innerException)
    {
        ImagePath = imagePath;
        ManifestPath = manifestPath;
        Sha256 = sha256;
    }

    public string ImagePath { get; }
    public string ManifestPath { get; }
    public string Sha256 { get; }
}

/// <summary>
/// Publishes a sidecar manifest without overwriting existing data. Publication is
/// deliberately non-cancellable once the verified image has been committed.
/// </summary>
public static class PhysicalDiskManifestPublisher
{
    public static async Task PublishAsync(
        string manifestPath,
        object manifest,
        string imagePath,
        string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);

        var fullManifestPath = Path.GetFullPath(manifestPath);
        var directory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new IOException("Manifest directory is invalid.");
        var temporaryPath = fullManifestPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException("Manifest directory no longer exists.");

            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, new JsonSerializerOptions
                {
                    WriteIndented = true
                }, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullManifestPath, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            throw new PhysicalDiskManifestPublicationException(
                Path.GetFullPath(imagePath), fullManifestPath, sha256, exception);
        }
    }
}
