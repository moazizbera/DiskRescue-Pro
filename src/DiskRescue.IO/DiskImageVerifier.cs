using System.Security.Cryptography;

namespace DiskRescue.IO;

public sealed record DiskImageVerificationResult(
    bool IsValid,
    long ExpectedLength,
    long ActualLength,
    string ExpectedSha256,
    string ActualSha256,
    IReadOnlyList<string> Issues);

/// <summary>
/// Re-reads a completed image and verifies its exact length and SHA-256 before it is trusted.
/// </summary>
public static class DiskImageVerifier
{
    private const int BufferSize = 4 * 1024 * 1024;

    /// <summary>
    /// Verifies an already-open seekable stream without closing it or resolving its file by path.
    /// The original stream position is restored before returning.
    /// </summary>
    public static async Task<DiskImageVerificationResult> VerifyAsync(
        Stream imageStream,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        ValidateExpectedImage(expectedLength, expectedSha256);
        if (!imageStream.CanRead || !imageStream.CanSeek)
            throw new ArgumentException("Image stream must be readable and seekable.", nameof(imageStream));

        var originalPosition = imageStream.Position;
        try
        {
            imageStream.Position = 0;
            var actualLength = imageStream.Length;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BufferSize];
            long totalRead = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await imageStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                hash.AppendData(buffer, 0, read);
                totalRead = checked(totalRead + read);
            }

            var actualSha256 = Convert.ToHexString(hash.GetHashAndReset());
            var issues = new List<string>();
            if (actualLength != expectedLength || totalRead != expectedLength)
                issues.Add($"Image length mismatch: expected {expectedLength} bytes, found {actualLength} bytes.");
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                issues.Add("Image SHA-256 does not match the acquisition digest.");

            return new DiskImageVerificationResult(
                issues.Count == 0, expectedLength, actualLength,
                expectedSha256.ToUpperInvariant(), actualSha256, issues.AsReadOnly());
        }
        finally
        {
            imageStream.Position = originalPosition;
        }
    }

    private static void ValidateExpectedImage(long expectedLength, string expectedSha256)
    {
        if (expectedLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedLength), "Expected image length must be positive.");
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        if (expectedSha256.Length != 64 || expectedSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Expected SHA-256 must contain exactly 64 hexadecimal characters.", nameof(expectedSha256));
    }

    public static async Task<DiskImageVerificationResult> VerifyAsync(
        string imagePath,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ValidateExpectedImage(expectedLength, expectedSha256);

        var issues = new List<string>();
        var fullPath = Path.GetFullPath(imagePath);
        await using var input = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var actualLength = input.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long totalRead = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
            totalRead = checked(totalRead + read);
        }

        var actualSha256 = Convert.ToHexString(hash.GetHashAndReset());
        if (actualLength != expectedLength || totalRead != expectedLength)
            issues.Add($"Image length mismatch: expected {expectedLength} bytes, found {actualLength} bytes.");
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            issues.Add("Image SHA-256 does not match the acquisition digest.");

        return new DiskImageVerificationResult(
            issues.Count == 0, expectedLength, actualLength,
            expectedSha256.ToUpperInvariant(), actualSha256, issues.AsReadOnly());
    }
}
