using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class WindowsPhysicalAcquisitionDestinationSessionTests
{
    [Fact]
    public async Task NativeSessionCreatesIdentifiesFlushesAndPublishesThroughPinnedDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = CreateTemporaryDirectory();
        try
        {
            await using var session = WindowsPhysicalAcquisitionDestinationSession.Open(directory);
            var stream = await session.CreateNewFileAsync("capture.partial");
            await stream.WriteAsync(new byte[] { 4, 8, 15, 16, 23, 42 });

            var identity = await session.GetFileIdentityAsync(stream);
            Assert.Equal(session.DirectoryIdentity.VolumeIdentity, identity.VolumeIdentity);
            Assert.NotEqual((0UL, 0UL), (identity.FileIdHigh, identity.FileIdLow));

            await session.FlushToDiskAsync(stream);
            await session.PublishNoReplaceAsync(stream, "capture.img");
            await stream.DisposeAsync();

            Assert.Equal(
                new byte[] { 4, 8, 15, 16, 23, 42 },
                await File.ReadAllBytesAsync(Path.Combine(directory, "capture.img")));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task NativeSessionUsesPinnedDirectoryAfterItsPathIsReplaced()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var parent = CreateTemporaryDirectory();
        var original = Path.Combine(parent, "destination");
        var moved = Path.Combine(parent, "destination-pinned");
        Directory.CreateDirectory(original);

        try
        {
            await using var session = WindowsPhysicalAcquisitionDestinationSession.Open(original);

            // The open directory handle allows rename/delete sharing. Replacing the
            // textual path must not redirect subsequent relative creates.
            Directory.Move(original, moved);
            Directory.CreateDirectory(original);

            var stream = await session.CreateNewFileAsync("pinned.bin");
            await stream.WriteAsync(new byte[] { 1, 2, 3 });
            await session.FlushToDiskAsync(stream);

            Assert.True(File.Exists(Path.Combine(moved, "pinned.bin")));
            Assert.False(File.Exists(Path.Combine(original, "pinned.bin")));
        }
        finally
        {
            DeleteTemporaryDirectory(parent);
        }
    }

    [Fact]
    public async Task NativeSessionNeverOverwritesAnExistingPublicationTarget()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = CreateTemporaryDirectory();
        var target = Path.Combine(directory, "existing.img");
        await File.WriteAllTextAsync(target, "preserve-me");

        try
        {
            await using var session = WindowsPhysicalAcquisitionDestinationSession.Open(directory);
            var stream = await session.CreateNewFileAsync("new.partial");
            await stream.WriteAsync(new byte[] { 9, 9, 9 });

            await Assert.ThrowsAsync<IOException>(async () =>
                await session.PublishNoReplaceAsync(stream, "existing.img"));

            Assert.Equal("preserve-me", await File.ReadAllTextAsync(target));
            Assert.False(File.Exists(Path.Combine(directory, "new.img")));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task NativeSessionDeletesOnlyAnUnpublishedFileByItsOpenHandle()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = CreateTemporaryDirectory();
        var temporaryPath = Path.Combine(directory, "temporary.partial");
        try
        {
            await using var session = WindowsPhysicalAcquisitionDestinationSession.Open(directory);
            var stream = await session.CreateNewFileAsync("temporary.partial");
            await stream.WriteAsync(new byte[] { 7 });
            await session.DeleteUnpublishedAsync(stream);
            await stream.DisposeAsync();

            Assert.False(File.Exists(temporaryPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void NativeSessionRejectsDirectorySymbolicLinkWithoutFollowingIt()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var parent = CreateTemporaryDirectory();
        var target = Path.Combine(parent, "target");
        var link = Path.Combine(parent, "link");
        Directory.CreateDirectory(target);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return; // The runner does not permit creating symbolic links.
            }

            Assert.Throws<NotSupportedException>(() =>
                WindowsPhysicalAcquisitionDestinationSession.Open(link));
        }
        finally
        {
            DeleteTemporaryDirectory(parent);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DiskRescue-NativeSession-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A failed test may leave a handle open; never replace the primary assertion.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is best effort for an isolated generated test directory.
        }
    }
}
