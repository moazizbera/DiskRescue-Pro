using System.Runtime.InteropServices;
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
            Assert.StartsWith(@"\\?\Volume{", session.VolumeGuidPath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("\\", session.VolumeGuidPath);
            var guidStart = session.VolumeGuidPath.IndexOf('{') + 1;
            var guidEnd = session.VolumeGuidPath.IndexOf('}');
            Assert.True(Guid.TryParse(session.VolumeGuidPath.AsSpan(guidStart, guidEnd - guidStart), out _));

            var stream = await session.CreateNewFileAsync("capture.partial");
            await stream.WriteAsync(new byte[] { 4, 8, 15, 16, 23, 42 });

            var identity = await session.GetFileIdentityAsync(stream);
            Assert.Equal(session.DirectoryIdentity.VolumeIdentity, identity.VolumeIdentity);
            Assert.NotEqual((0UL, 0UL), (identity.FileIdHigh, identity.FileIdLow));

            await session.FlushToDiskAsync(stream);
            await session.PublishNoReplaceAsync(stream, "capture.img");
            await session.DisposeAsync();

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
    public async Task NativeSessionDeniesCompetingWriteAndDeleteOpensForOwnedFiles()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = CreateTemporaryDirectory();
        var temporaryPath = Path.Combine(directory, "protected.partial");
        var renamedPath = Path.Combine(directory, "competing-rename.partial");
        try
        {
            await using var session = WindowsPhysicalAcquisitionDestinationSession.Open(directory);
            var stream = await session.CreateNewFileAsync("protected.partial");
            await stream.WriteAsync(new byte[] { 1, 2, 3 });

            Assert.Throws<IOException>(() =>
                new FileStream(
                    temporaryPath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete));

            Assert.Throws<IOException>(() =>
                File.Move(temporaryPath, renamedPath));

            // The owning stream remains writable and can still be durably published.
            stream.Position = stream.Length;
            await stream.WriteAsync(new byte[] { 4, 5, 6 });
            await session.FlushToDiskAsync(stream);
            await session.PublishNoReplaceAsync(stream, "protected.img");

            // Close the session-owned write/delete-capable handle before opening a
            // separate reader; Windows share checks are symmetric.
            await session.DisposeAsync();

            Assert.Equal(
                new byte[] { 1, 2, 3, 4, 5, 6 },
                await File.ReadAllBytesAsync(Path.Combine(directory, "protected.img")));
            Assert.False(File.Exists(renamedPath));
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
            Assert.Equal(Path.GetFullPath(original), session.GetCurrentDirectoryPath(), StringComparer.OrdinalIgnoreCase);

            // The open directory handle allows rename/delete sharing. Replacing the
            // textual path must not redirect subsequent relative creates or path reporting.
            Directory.Move(original, moved);
            Directory.CreateDirectory(original);
            Assert.Equal(Path.GetFullPath(moved), session.GetCurrentDirectoryPath(), StringComparer.OrdinalIgnoreCase);

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

    [Fact]
    public void NativeSessionRejectsDirectoryJunctionWithoutFollowingIt()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var parent = CreateTemporaryDirectory();
        var target = Path.Combine(parent, "junction-target");
        var junction = Path.Combine(parent, "junction");
        Directory.CreateDirectory(target);

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junction}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
                return;
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                return;
            }
            if (process.ExitCode != 0)
                return; // The runner does not permit creating directory junctions.

            Assert.Throws<NotSupportedException>(() =>
                WindowsPhysicalAcquisitionDestinationSession.Open(junction));
        }
        finally
        {
            DeleteTemporaryDirectory(parent);
        }
    }

    [Fact]
    public void NativeInteropStructuresMatchWindowsAbiLayout()
    {
        var unicodeString = GetNativeStruct("UnicodeString");
        var objectAttributes = GetNativeStruct("ObjectAttributes");
        var ioStatusBlock = GetNativeStruct("IoStatusBlock");
        var fileIdInfo = GetNativeStruct("FileIdInfo");
        var fileAttributeTagInfo = GetNativeStruct("FileAttributeTagInfo");

        if (IntPtr.Size == 8)
        {
            Assert.Equal(16, Marshal.SizeOf(unicodeString));
            AssertFieldOffset(unicodeString, "Length", 0);
            AssertFieldOffset(unicodeString, "MaximumLength", 2);
            AssertFieldOffset(unicodeString, "Buffer", 8);

            Assert.Equal(48, Marshal.SizeOf(objectAttributes));
            AssertFieldOffset(objectAttributes, "Length", 0);
            AssertFieldOffset(objectAttributes, "RootDirectory", 8);
            AssertFieldOffset(objectAttributes, "ObjectName", 16);
            AssertFieldOffset(objectAttributes, "Attributes", 24);
            AssertFieldOffset(objectAttributes, "SecurityDescriptor", 32);
            AssertFieldOffset(objectAttributes, "SecurityQualityOfService", 40);

            Assert.Equal(16, Marshal.SizeOf(ioStatusBlock));
            AssertFieldOffset(ioStatusBlock, "Status", 0);
            AssertFieldOffset(ioStatusBlock, "Information", 8);

            // FILE_RENAME_INFORMATION: BOOLEAN, alignment padding, HANDLE,
            // ULONG FileNameLength, then WCHAR FileName[1].
            Assert.Equal(8, IntPtr.Size);
            Assert.Equal(8, AlignUp(sizeof(byte), IntPtr.Size));
            Assert.Equal(16, AlignUp(sizeof(byte), IntPtr.Size) + IntPtr.Size);
            Assert.Equal(20, AlignUp(sizeof(byte), IntPtr.Size) + IntPtr.Size + sizeof(uint));
        }
        else
        {
            Assert.Equal(8, Marshal.SizeOf(unicodeString));
            AssertFieldOffset(unicodeString, "Length", 0);
            AssertFieldOffset(unicodeString, "MaximumLength", 2);
            AssertFieldOffset(unicodeString, "Buffer", 4);

            Assert.Equal(24, Marshal.SizeOf(objectAttributes));
            AssertFieldOffset(objectAttributes, "Length", 0);
            AssertFieldOffset(objectAttributes, "RootDirectory", 4);
            AssertFieldOffset(objectAttributes, "ObjectName", 8);
            AssertFieldOffset(objectAttributes, "Attributes", 12);
            AssertFieldOffset(objectAttributes, "SecurityDescriptor", 16);
            AssertFieldOffset(objectAttributes, "SecurityQualityOfService", 20);

            Assert.Equal(8, Marshal.SizeOf(ioStatusBlock));
            AssertFieldOffset(ioStatusBlock, "Status", 0);
            AssertFieldOffset(ioStatusBlock, "Information", 4);

            Assert.Equal(4, AlignUp(sizeof(byte), IntPtr.Size));
            Assert.Equal(8, AlignUp(sizeof(byte), IntPtr.Size) + IntPtr.Size);
            Assert.Equal(12, AlignUp(sizeof(byte), IntPtr.Size) + IntPtr.Size + sizeof(uint));
        }

        Assert.Equal(24, Marshal.SizeOf(fileIdInfo));
        AssertFieldOffset(fileIdInfo, "VolumeSerialNumber", 0);
        AssertFieldOffset(fileIdInfo, "FileId", 8);

        Assert.Equal(8, Marshal.SizeOf(fileAttributeTagInfo));
        AssertFieldOffset(fileAttributeTagInfo, "FileAttributes", 0);
        AssertFieldOffset(fileAttributeTagInfo, "ReparseTag", 4);
    }

    private static Type GetNativeStruct(string name) =>
        typeof(WindowsPhysicalAcquisitionDestinationSession).GetNestedType(
            name,
            System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Native interop structure '{name}' was not found.");

    private static void AssertFieldOffset(Type type, string fieldName, int expectedOffset)
    {
        Assert.Equal(
            new IntPtr(expectedOffset),
            Marshal.OffsetOf(type, fieldName));
    }

    private static int AlignUp(int value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);

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
