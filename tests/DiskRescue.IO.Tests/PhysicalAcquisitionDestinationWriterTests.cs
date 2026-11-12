using System.Text.Json;
using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class PhysicalAcquisitionDestinationWriterTests
{
    [Fact]
    public async Task WriterPublishesImageAndManifestThroughOneSession()
    {
        var bytes = Enumerable.Range(0, 180_000).Select(i => (byte)(i % 251)).ToArray();
        using var source = new MemorySource(bytes);
        await using var session = new FakeDestinationSession();

        var result = await PhysicalAcquisitionDestinationWriter.WriteAndPublishAsync(
            source, session,
            "capture.img.partial", "capture.img",
            "capture.json.partial", "capture.json",
            new { format = "session-manifest", purpose = "test" });

        Assert.Equal(bytes.LongLength, result.BytesCopied);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), result.Sha256);
        Assert.True(session.IsPublished("capture.img"));
        Assert.True(session.IsPublished("capture.json"));
        Assert.Equal(2, session.FlushCount);
        Assert.Equal(2, session.NamespaceFlushCount);
        Assert.Equal(bytes, session.PublishedBytes("capture.img"));
        using var manifest = JsonDocument.Parse(session.PublishedBytes("capture.json"));
        Assert.Equal("session-manifest", manifest.RootElement.GetProperty("format").GetString());
        Assert.Equal(2, session.PublishedCount);
    }

    [Fact]
    public async Task WriterRejectsImageFileOnDifferentVolumeBeforeFirstSourceRead()
    {
        using var source = new MemorySource(new byte[128 * 1024]);
        await using var session = new FakeDestinationSession { FirstFileOnDifferentVolume = true };

        await Assert.ThrowsAsync<IOException>(() =>
            PhysicalAcquisitionDestinationWriter.WriteAndPublishAsync(
                source, session,
                "capture.img.partial", "capture.img",
                "capture.json.partial", "capture.json",
                new { format = "test" }));

        Assert.Equal(0, source.ReadAttempts);
        Assert.True(session.WasDeleted("capture.img.partial"));
        Assert.Equal(0, session.PublishedCount);
    }

    [Fact]
    public async Task WriterDeletesPartialImageWhenSourceReadFails()
    {
        using var source = new MemorySource(new byte[5 * 1024 * 1024], failAfterFirstRead: true);
        await using var session = new FakeDestinationSession();

        await Assert.ThrowsAsync<IOException>(() =>
            PhysicalAcquisitionDestinationWriter.WriteAndPublishAsync(
                source, session,
                "capture.img.partial", "capture.img",
                "capture.json.partial", "capture.json",
                new { format = "test" }));

        Assert.True(session.WasDeleted("capture.img.partial"));
        Assert.False(session.IsPublished("capture.img"));
        Assert.Equal(0, session.PublishedCount);
    }

    [Fact]
    public async Task WriterDeletesPartialImageWhenCancellationOccursDuringCopy()
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new MemorySource(
            new byte[5 * 1024 * 1024],
            afterRead: () => cancellation.Cancel());
        await using var session = new FakeDestinationSession();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PhysicalAcquisitionDestinationWriter.WriteAndPublishAsync(
                source, session,
                "capture.img.partial", "capture.img",
                "capture.json.partial", "capture.json",
                new { format = "test" },
                cancellationToken: cancellation.Token));

        Assert.Equal(1, source.ReadAttempts);
        Assert.True(session.WasDeleted("capture.img.partial"));
        Assert.False(session.IsPublished("capture.img"));
        Assert.False(session.IsPublished("capture.json"));
        Assert.Equal(0, session.PublishedCount);
    }

    [Fact]
    public async Task ManifestFailurePreservesPublishedImageAndRemovesManifestTemporary()
    {
        var bytes = new byte[96 * 1024];
        using var source = new MemorySource(bytes);
        await using var session = new FakeDestinationSession { FailManifestPublication = true };

        var error = await Assert.ThrowsAsync<PhysicalAcquisitionManifestCommitException>(() =>
            PhysicalAcquisitionDestinationWriter.WriteAndPublishAsync(
                source, session,
                "capture.img.partial", "capture.img",
                "capture.json.partial", "capture.json",
                new { format = "test" }));

        Assert.True(session.IsPublished("capture.img"));
        Assert.False(session.IsPublished("capture.json"));
        Assert.True(session.WasDeleted("capture.json.partial"));
        Assert.Equal(1, session.NamespaceFlushCount);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), error.Sha256);
        Assert.Equal("capture.img", error.ImageLeafName);
    }

    [Fact]
    public async Task WriterPreservesPublishedImageWhenImageNamespaceFlushFails()
    {
        using var source = new MemorySource(new byte[96 * 1024]);
        await using var session = new FakeDestinationSession { FailNamespaceFlushOnCall = 1 };

        var error = await Assert.ThrowsAsync<PhysicalAcquisitionPublicationDurabilityException>(() =>
            PhysicalAcquisitionDestinationWriter.WriteAndPublishAsync(
                source, session,
                "capture.img.partial", "capture.img",
                "capture.json.partial", "capture.json",
                new { format = "test" }));

        Assert.Equal("capture.img", error.PublishedLeafName);
        Assert.True(session.IsPublished("capture.img"));
        Assert.False(session.IsPublished("capture.json"));
        Assert.False(session.WasDeleted("capture.img.partial"));
        Assert.Equal(1, session.NamespaceFlushCount);
    }

    [Fact]
    public async Task WriterPreservesPublishedImageAndManifestWhenManifestNamespaceFlushFails()
    {
        using var source = new MemorySource(new byte[96 * 1024]);
        await using var session = new FakeDestinationSession { FailNamespaceFlushOnCall = 2 };

        var error = await Assert.ThrowsAsync<PhysicalAcquisitionPublicationDurabilityException>(() =>
            PhysicalAcquisitionDestinationWriter.WriteAndPublishAsync(
                source, session,
                "capture.img.partial", "capture.img",
                "capture.json.partial", "capture.json",
                new { format = "test" }));

        Assert.Equal("capture.json", error.PublishedLeafName);
        Assert.True(session.IsPublished("capture.img"));
        Assert.True(session.IsPublished("capture.json"));
        Assert.False(session.WasDeleted("capture.json.partial"));
        Assert.Equal(2, session.NamespaceFlushCount);
    }

    [Fact]
    public async Task WriterRejectsDirectoryWithoutStableIdentityBeforeCreatingFilesOrReadingSource()
    {
        using var source = new MemorySource(new byte[4096]);
        await using var session = new FakeDestinationSession { InvalidDirectoryIdentity = true };

        await Assert.ThrowsAsync<IOException>(() =>
            PhysicalAcquisitionDestinationWriter.WriteAndPublishAsync(
                source, session,
                "capture.img.partial", "capture.img",
                "capture.json.partial", "capture.json",
                new { format = "test" }));

        Assert.Equal(0, source.ReadAttempts);
        Assert.Equal(0, session.CreatedCount);
    }

    [Fact]
    public async Task WriterRejectsImageFileWithoutStableIdBeforeFirstSourceRead()
    {
        using var source = new MemorySource(new byte[128 * 1024]);
        await using var session = new FakeDestinationSession { FirstFileWithoutId = true };

        await Assert.ThrowsAsync<IOException>(() =>
            PhysicalAcquisitionDestinationWriter.WriteAndPublishAsync(
                source, session,
                "capture.img.partial", "capture.img",
                "capture.json.partial", "capture.json",
                new { format = "test" }));

        Assert.Equal(0, source.ReadAttempts);
        Assert.True(session.WasDeleted("capture.img.partial"));
        Assert.Equal(0, session.PublishedCount);
    }

    [Fact]
    public async Task WriterPreservesImageWhenManifestFileIdentityIsOnAnotherVolume()
    {
        using var source = new MemorySource(new byte[96 * 1024]);
        await using var session = new FakeDestinationSession { SecondFileOnDifferentVolume = true };

        await Assert.ThrowsAsync<PhysicalAcquisitionManifestCommitException>(() =>
            PhysicalAcquisitionDestinationWriter.WriteAndPublishAsync(
                source, session,
                "capture.img.partial", "capture.img",
                "capture.json.partial", "capture.json",
                new { format = "test" }));

        Assert.True(session.IsPublished("capture.img"));
        Assert.False(session.IsPublished("capture.json"));
        Assert.True(session.WasDeleted("capture.json.partial"));
    }

    [Fact]
    public async Task WriterRejectsDuplicateLeafNamesBeforeCreatingFilesOrReadingSource()
    {
        using var source = new MemorySource(new byte[4096]);
        await using var session = new FakeDestinationSession();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            PhysicalAcquisitionDestinationWriter.WriteAndPublishAsync(
                source, session,
                "capture.img.partial", "capture.img",
                "CAPTURE.IMG", "capture.json",
                new { format = "test" }));

        Assert.Equal(0, source.ReadAttempts);
        Assert.Equal(0, session.CreatedCount);
    }

    private sealed class FakeDestinationSession : IPhysicalAcquisitionDestinationSession
    {
        private readonly Dictionary<Stream, string> _owned = new();
        private readonly Dictionary<string, byte[]> _published = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _deleted = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Stream> _publishedStreams = new();
        private bool _disposed;

        public PhysicalDestinationDirectoryIdentity DirectoryIdentity =>
            InvalidDirectoryIdentity
                ? new("WINVOL:FAKE", 0, 0)
                : new("WINVOL:FAKE", 1, 2);
        public string VolumeGuidPath => @"\\?\Volume{00000000-0000-0000-0000-000000000001}\";
        public string GetCurrentDirectoryPath() => @"C:\fake-destination";
        public bool InvalidDirectoryIdentity { get; init; }
        public bool FirstFileOnDifferentVolume { get; init; }
        public bool FirstFileWithoutId { get; init; }
        public bool SecondFileOnDifferentVolume { get; init; }
        public bool FailManifestPublication { get; init; }
        public int FailNamespaceFlushOnCall { get; init; }
        public int CreatedCount => _owned.Count;
        public int FlushCount { get; private set; }
        public int NamespaceFlushCount { get; private set; }
        public int PublishedCount => _published.Count;

        public ValueTask<Stream> CreateNewFileAsync(string leafName, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            PhysicalDestinationLeafName.Validate(leafName, nameof(leafName));
            if (_owned.Values.Contains(leafName, StringComparer.OrdinalIgnoreCase) ||
                _published.ContainsKey(leafName))
                throw new IOException("Entry already exists.");
            var stream = new MemoryStream();
            _owned.Add(stream, leafName);
            return ValueTask.FromResult<Stream>(stream);
        }

        public ValueTask<PhysicalDestinationFileIdentity> GetFileIdentityAsync(Stream file, CancellationToken cancellationToken = default)
        {
            EnsureOwned(file);
            cancellationToken.ThrowIfCancellationRequested();
            var isFirst = ReferenceEquals(file, _owned.Keys.First());
            var volume = (FirstFileOnDifferentVolume && isFirst) ||
                (SecondFileOnDifferentVolume && !isFirst)
                ? @"\\?\Volume{unexpected}\"
                : DirectoryIdentity.VolumeIdentity;
            var fileIdLow = FirstFileWithoutId && isFirst ? 0UL : 1UL;
            return ValueTask.FromResult(new PhysicalDestinationFileIdentity(volume, 0, fileIdLow));
        }

        public ValueTask FlushToDiskAsync(Stream file, CancellationToken cancellationToken = default)
        {
            EnsureOwned(file);
            cancellationToken.ThrowIfCancellationRequested();
            FlushCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishNoReplaceAsync(Stream file, string finalLeafName, CancellationToken cancellationToken = default)
        {
            EnsureOwned(file);
            cancellationToken.ThrowIfCancellationRequested();
            PhysicalDestinationLeafName.Validate(finalLeafName, nameof(finalLeafName));
            if (FailManifestPublication && finalLeafName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Synthetic manifest publication failure.");
            if (_published.ContainsKey(finalLeafName) ||
                _owned.Values.Contains(finalLeafName, StringComparer.OrdinalIgnoreCase))
                throw new IOException("No-replace collision.");
            _published.Add(finalLeafName, ((MemoryStream)file).ToArray());
            _publishedStreams.Add(file);
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushPublishedNamespaceAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            NamespaceFlushCount++;
            if (NamespaceFlushCount == FailNamespaceFlushOnCall)
                throw new IOException("Synthetic destination volume flush failure.");
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteUnpublishedAsync(Stream file, CancellationToken cancellationToken = default)
        {
            EnsureOwned(file);
            cancellationToken.ThrowIfCancellationRequested();
            if (_publishedStreams.Contains(file))
                throw new InvalidOperationException("Published files cannot be deleted.");
            _deleted.Add(_owned[file]);
            _owned.Remove(file);
            return ValueTask.CompletedTask;
        }

        public bool IsPublished(string name) => _published.ContainsKey(name);
        public byte[] PublishedBytes(string name) => _published[name];
        public bool WasDeleted(string name) => _deleted.Contains(name);

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            foreach (var stream in _owned.Keys) stream.Dispose();
            return ValueTask.CompletedTask;
        }

        private void EnsureOwned(Stream stream)
        {
            ThrowIfDisposed();
            if (!_owned.ContainsKey(stream))
                throw new ArgumentException("Foreign or inactive stream.", nameof(stream));
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class MemorySource(
        byte[] data,
        bool failAfterFirstRead = false,
        Action? afterRead = null) : IReadOnlyRandomAccessSource
    {
        private int _readCount;
        public long Length => data.LongLength;
        public int ReadAttempts { get; private set; }
        public void ReadExactlyAt(long offset, Span<byte> destination)
        {
            ReadAttempts++;
            if (failAfterFirstRead && _readCount++ > 0)
                throw new IOException("Synthetic source read failure.");
            if (offset < 0 || offset > data.Length || destination.Length > data.Length - offset)
                throw new EndOfStreamException();
            data.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
            afterRead?.Invoke();
        }
        public void Dispose() { }
    }
}
