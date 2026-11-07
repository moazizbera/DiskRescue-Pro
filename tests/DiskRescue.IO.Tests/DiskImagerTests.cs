using System.Security.Cryptography;
using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class DiskImagerTests
{
    [Fact]
    public async Task CreatesImageAndReturnsSha256WithoutModifyingSource()
    {
        var bytes = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        using var source = new MemorySource(bytes);
        using var folder = new TemporaryFolder();
        var destination = Path.Combine(folder.Path, "disk.img");

        var result = await DiskImager.CreateImageAsync(source, destination, chunkSize: 64 * 1024);

        Assert.Equal(bytes.Length, result.BytesCopied);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), result.Sha256);
        Assert.Equal(bytes, File.ReadAllBytes(destination));
        Assert.Equal(0, source.WriteAttempts);
    }

    [Fact]
    public async Task VerifierAcceptsExactLengthAndMatchingSha256()
    {
        var bytes = Enumerable.Range(0, 180_000).Select(i => (byte)(i % 239)).ToArray();
        using var folder = new TemporaryFolder();
        var path = Path.Combine(folder.Path, "verified.img");
        await File.WriteAllBytesAsync(path, bytes);

        var result = await DiskImageVerifier.VerifyAsync(
            path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));

        Assert.True(result.IsValid);
        Assert.Equal(bytes.Length, result.ActualLength);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task VerifierRejectsChangedImageContent()
    {
        using var folder = new TemporaryFolder();
        var path = Path.Combine(folder.Path, "changed.img");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4 });

        var result = await DiskImageVerifier.VerifyAsync(
            path, 4, Convert.ToHexString(SHA256.HashData(new byte[] { 4, 3, 2, 1 })));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("SHA-256", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifierRejectsLengthMismatchEvenWhenDigestMatches()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        using var folder = new TemporaryFolder();
        var path = Path.Combine(folder.Path, "wrong-length.img");
        await File.WriteAllBytesAsync(path, bytes);

        var result = await DiskImageVerifier.VerifyAsync(
            path, 5, Convert.ToHexString(SHA256.HashData(bytes)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Contains("length mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifierRejectsMalformedExpectedDigest()
    {
        using var folder = new TemporaryFolder();
        var path = Path.Combine(folder.Path, "digest.img");
        await File.WriteAllBytesAsync(path, new byte[] { 1 });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            DiskImageVerifier.VerifyAsync(path, 1, "not-a-sha256"));
    }

    [Fact]
    public async Task NeverOverwritesAnExistingDestination()
    {
        using var source = new MemorySource(new byte[128 * 1024]);
        using var folder = new TemporaryFolder();
        var destination = Path.Combine(folder.Path, "existing.img");
        await File.WriteAllTextAsync(destination, "keep-me");

        await Assert.ThrowsAsync<IOException>(() => DiskImager.CreateImageAsync(source, destination, chunkSize: 64 * 1024));

        Assert.Equal("keep-me", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task CancellationRemovesPartialImageAndDoesNotPublishDestination()
    {
        using var source = new MemorySource(new byte[256 * 1024]);
        using var folder = new TemporaryFolder();
        using var cancellation = new CancellationTokenSource();
        var destination = Path.Combine(folder.Path, "cancelled.img");
        var progress = new InlineProgress<DiskImageProgress>(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DiskImager.CreateImageAsync(source, destination, progress, cancellation.Token, 64 * 1024));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task SourceReadFailureDoesNotPublishPartialImage()
    {
        using var source = new MemorySource(new byte[128 * 1024], failAfterFirstRead: true);
        using var folder = new TemporaryFolder();
        var destination = Path.Combine(folder.Path, "failed.img");

        await Assert.ThrowsAsync<IOException>(() =>
            DiskImager.CreateImageAsync(source, destination, chunkSize: 64 * 1024));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1024)]
    [InlineData(32 * 1024 * 1024)]
    public async Task RejectsUnsafeChunkSizes(int chunkSize)
    {
        using var source = new MemorySource(new byte[128 * 1024]);
        using var folder = new TemporaryFolder();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            DiskImager.CreateImageAsync(source, Path.Combine(folder.Path, "bad.img"), chunkSize: chunkSize));
    }

    private sealed class MemorySource : IReadOnlyRandomAccessSource
    {
        private readonly byte[] _data;
        private readonly bool _failAfterFirstRead;
        private int _reads;
        public MemorySource(byte[] data, bool failAfterFirstRead = false)
        {
            _data = data;
            _failAfterFirstRead = failAfterFirstRead;
        }
        public long Length => _data.Length;
        public int WriteAttempts { get; private set; }
        public void ReadExactlyAt(long offset, Span<byte> destination)
        {
            if (_failAfterFirstRead && _reads++ > 0) throw new IOException("Synthetic read failure.");
            if (offset < 0 || offset > _data.Length || destination.Length > _data.Length - offset)
                throw new EndOfStreamException();
            _data.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
        }
        public void Dispose() { }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"diskrescue-imager-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
