using System.Security.Cryptography;

namespace DiskRescue.IO;

public sealed record DiskImageProgress(long BytesCopied, long TotalBytes)
{
    public double Percent => TotalBytes == 0 ? 100d : (double)BytesCopied / TotalBytes * 100d;
}

public sealed record DiskImageResult(string ImagePath, long BytesCopied, string Sha256);

/// <summary>
/// Copies a bounded read-only source into a new image file. This class never writes to the source.
/// The caller remains responsible for proving that the destination is on separate physical storage.
/// </summary>
public static class DiskImager
{
    public const int DefaultChunkSize = 4 * 1024 * 1024;
    private const int MinimumChunkSize = 64 * 1024;
    private const int MaximumChunkSize = 16 * 1024 * 1024;

    public static async Task<DiskImageResult> CreateImageAsync(
        IReadOnlyRandomAccessSource source,
        string destinationPath,
        IProgress<DiskImageProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int chunkSize = DefaultChunkSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (source.Length <= 0) throw new ArgumentOutOfRangeException(nameof(source), "Source length must be positive.");
        if (chunkSize < MinimumChunkSize || chunkSize > MaximumChunkSize)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), $"Chunk size must be between {MinimumChunkSize} and {MaximumChunkSize} bytes.");

        RejectDeviceNamespace(destinationPath);
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new IOException("Destination directory is invalid.");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new IOException("Destination already exists; existing files are never overwritten.");

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.partial");
        var buffer = new byte[chunkSize];
        long copied = 0;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                chunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (copied < source.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(buffer.Length, source.Length - copied);
                    source.ReadExactlyAt(copied, buffer.AsSpan(0, count));
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    copied = checked(copied + count);
                    progress?.Report(new DiskImageProgress(copied, source.Length));
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var expectedSha256 = Convert.ToHexString(hash.GetHashAndReset());
            var verification = await DiskImageVerifier.VerifyAsync(
                tempPath, source.Length, expectedSha256, cancellationToken).ConfigureAwait(false);
            if (!verification.IsValid)
                throw new IOException("The completed image failed verification: " + string.Join("; ", verification.Issues));

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, fullPath, overwrite: false);
            return new DiskImageResult(fullPath, copied, expectedSha256);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (IOException) { /* Preserve the original acquisition error; partial file remains clearly marked. */ }
            catch (UnauthorizedAccessException) { /* Preserve the original acquisition error. */ }
            throw;
        }
    }

    private static void RejectDeviceNamespace(string path)
    {
        if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\\??\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Image destination must be a regular filesystem path.", nameof(path));
    }
}
