using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class PhysicalAcquisitionDestinationSessionContractTests
{
    [Fact]
    public async Task SessionCreatesOnlyUniqueLeafNamesAndOwnsReturnedStreams()
    {
        await using var session = new FakeDestinationSession();

        var file = await session.CreateNewFileAsync("capture.partial");

        Assert.NotNull(file);
        await Assert.ThrowsAsync<IOException>(async () =>
            await session.CreateNewFileAsync("capture.partial"));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await session.CreateNewFileAsync("../escape.img"));
        Assert.Equal(1, session.CreatedCount);
    }

    [Fact]
    public async Task SessionRejectsForeignStreamsForIdentityFlushPublishAndDelete()
    {
        await using var session = new FakeDestinationSession();
        await using var foreign = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await session.GetFileIdentityAsync(foreign));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await session.FlushToDiskAsync(foreign));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await session.PublishNoReplaceAsync(foreign, "capture.img"));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await session.DeleteUnpublishedAsync(foreign));
    }

    [Fact]
    public async Task SessionReportsStableIdentityAndFlushesOwnedFile()
    {
        await using var session = new FakeDestinationSession();
        var file = await session.CreateNewFileAsync("capture.partial");
        await file.WriteAsync(new byte[] { 1, 2, 3 });

        var identity = await session.GetFileIdentityAsync(file);
        await session.FlushToDiskAsync(file);

        Assert.Equal(session.DirectoryIdentity.VolumeIdentity, identity.VolumeIdentity);
        Assert.Equal(1UL, identity.FileIdLow);
        Assert.Equal(1, session.FlushCount);
    }

    [Fact]
    public async Task SessionPublishesNoReplaceAndPreservesExistingTarget()
    {
        await using var session = new FakeDestinationSession();
        var first = await session.CreateNewFileAsync("first.partial");
        await first.WriteAsync(new byte[] { 1, 2, 3 });

        await session.PublishNoReplaceAsync(first, "capture.img");

        Assert.True(session.IsPublished("capture.img"));
        Assert.Throws<IOException>(() => session.SeedExisting("existing.img"));
        var second = await session.CreateNewFileAsync("second.partial");
        await Assert.ThrowsAsync<IOException>(async () =>
            await session.PublishNoReplaceAsync(second, "capture.img"));
        Assert.True(session.IsPublished("capture.img"));
        Assert.False(session.IsPublished("second.partial"));
    }

    [Fact]
    public async Task SessionDeletesOnlyUnpublishedOwnedFiles()
    {
        await using var session = new FakeDestinationSession();
        var temporary = await session.CreateNewFileAsync("temporary.partial");
        var published = await session.CreateNewFileAsync("published.partial");

        await session.PublishNoReplaceAsync(published, "published.img");
        await session.DeleteUnpublishedAsync(temporary);

        Assert.True(session.WasDeleted(temporary));
        Assert.False(session.WasDeleted(published));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.DeleteUnpublishedAsync(published));
    }

    [Fact]
    public async Task DisposingSessionDisposesAllOwnedStreams()
    {
        var session = new FakeDestinationSession();
        var first = await session.CreateNewFileAsync("one.partial");
        var second = await session.CreateNewFileAsync("two.partial");

        await session.DisposeAsync();

        Assert.False(first.CanRead);
        Assert.False(second.CanRead);
        Assert.True(session.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.CreateNewFileAsync("after-dispose.partial"));
    }

    private sealed class FakeDestinationSession : IPhysicalAcquisitionDestinationSession
    {
        private readonly Dictionary<Stream, string> _owned = new();
        private readonly HashSet<string> _createdNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _publishedNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Stream> _deleted = new();
        private bool _disposed;

        public PhysicalDestinationDirectoryIdentity DirectoryIdentity { get; } =
            new(@"\\?\Volume{fake-destination}\", 0xAUL, 0xBUL);

        public int CreatedCount => _owned.Count;
        public int FlushCount { get; private set; }
        public bool IsDisposed => _disposed;

        public ValueTask<Stream> CreateNewFileAsync(
            string leafName, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            PhysicalDestinationLeafName.Validate(leafName, nameof(leafName));
            if (!_createdNames.Add(leafName))
                throw new IOException("The destination entry already exists.");

            var stream = new MemoryStream();
            _owned.Add(stream, leafName);
            return ValueTask.FromResult<Stream>(stream);
        }

        public ValueTask<PhysicalDestinationFileIdentity> GetFileIdentityAsync(
            Stream file, CancellationToken cancellationToken = default)
        {
            EnsureOwned(file);
            cancellationToken.ThrowIfCancellationRequested();
            var index = (ulong)_owned.Keys.ToList().IndexOf(file) + 1;
            return ValueTask.FromResult(new PhysicalDestinationFileIdentity(
                DirectoryIdentity.VolumeIdentity, 0, index));
        }

        public ValueTask FlushToDiskAsync(Stream file, CancellationToken cancellationToken = default)
        {
            EnsureOwned(file);
            cancellationToken.ThrowIfCancellationRequested();
            FlushCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishNoReplaceAsync(
            Stream file, string finalLeafName, CancellationToken cancellationToken = default)
        {
            EnsureOwned(file);
            cancellationToken.ThrowIfCancellationRequested();
            PhysicalDestinationLeafName.Validate(finalLeafName, nameof(finalLeafName));
            if (_publishedNames.Contains(finalLeafName) || _createdNames.Contains(finalLeafName))
                throw new IOException("The destination entry already exists.");

            _publishedNames.Add(finalLeafName);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteUnpublishedAsync(Stream file, CancellationToken cancellationToken = default)
        {
            EnsureOwned(file);
            cancellationToken.ThrowIfCancellationRequested();
            if (_publishedNames.Contains(_owned[file]))
                throw new InvalidOperationException("A published file cannot be deleted as an unpublished temporary.");
            _deleted.Add(file);
            return ValueTask.CompletedTask;
        }

        public bool IsPublished(string name) => _publishedNames.Contains(name);
        public bool WasDeleted(Stream stream) => _deleted.Contains(stream);

        public void SeedExisting(string name)
        {
            PhysicalDestinationLeafName.Validate(name, nameof(name));
            if (!_createdNames.Add(name))
                throw new IOException("The destination entry already exists.");
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;
            foreach (var stream in _owned.Keys)
                stream.Dispose();
            return ValueTask.CompletedTask;
        }

        private void EnsureOwned(Stream stream)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(stream);
            if (!_owned.ContainsKey(stream) || _deleted.Contains(stream))
                throw new ArgumentException("Stream is not an active file owned by this session.", nameof(stream));
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
