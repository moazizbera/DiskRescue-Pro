using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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
            var nativeStream = Assert.IsType<FileStream>(stream);
            var mode = new FileModeInformation();
            var modeStatus = NtQueryInformationFile(
                nativeStream.SafeFileHandle,
                out _,
                ref mode,
                (uint)Marshal.SizeOf<FileModeInformation>(),
                FileModeInformationClass);
            Assert.Equal(0, modeStatus);
            Assert.Equal(0u, mode.Mode & FileWriteThrough);
            await stream.WriteAsync(new byte[] { 4, 8, 15, 16, 23, 42 });

            var identity = await session.GetFileIdentityAsync(stream);
            Assert.Equal(session.DirectoryIdentity.VolumeIdentity, identity.VolumeIdentity);
            Assert.NotEqual((0UL, 0UL), (identity.FileIdHigh, identity.FileIdLow));

            await session.FlushToDiskAsync(stream);
            await session.PublishNoReplaceAsync(stream, "capture.img");

            // Write-through is enabled only at publication, after the sequential
            // image write and explicit content flush have completed.
            mode = new FileModeInformation();
            modeStatus = NtQueryInformationFile(
                nativeStream.SafeFileHandle,
                out _,
                ref mode,
                (uint)Marshal.SizeOf<FileModeInformation>(),
                FileModeInformationClass);
            Assert.Equal(0, modeStatus);
            Assert.NotEqual(0u, mode.Mode & FileWriteThrough);

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
    public async Task NativeSessionPreventsRenameOfFinalDirectoryAndAncestorUntilDisposed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var parent = CreateTemporaryDirectory();
        var ancestor = Path.Combine(parent, "level-one");
        var original = Path.Combine(ancestor, "destination");
        var movedFinal = Path.Combine(ancestor, "destination-moved");
        var movedAncestor = Path.Combine(parent, "level-one-moved");
        Directory.CreateDirectory(original);

        try
        {
            await using (var session = WindowsPhysicalAcquisitionDestinationSession.Open(original))
            {
                Assert.Equal(
                    Path.GetFullPath(original),
                    session.GetCurrentDirectoryPath(),
                    StringComparer.OrdinalIgnoreCase);

                // Every non-root component remains open without FILE_SHARE_DELETE.
                // Windows must reject renaming both the final directory and its ancestor.
                Assert.Throws<IOException>(() => Directory.Move(original, movedFinal));
                Assert.Throws<IOException>(() => Directory.Move(ancestor, movedAncestor));

                Assert.Equal(
                    Path.GetFullPath(original),
                    session.GetCurrentDirectoryPath(),
                    StringComparer.OrdinalIgnoreCase);

                var stream = await session.CreateNewFileAsync("pinned.bin");
                await stream.WriteAsync(new byte[] { 1, 2, 3 });
                await session.FlushToDiskAsync(stream);
                await session.PublishNoReplaceAsync(stream, "pinned.img");
            }

            Assert.True(File.Exists(Path.Combine(original, "pinned.img")));
            Assert.False(Directory.Exists(movedFinal));
            Assert.False(Directory.Exists(movedAncestor));

            // Once all retained component handles are closed, ordinary directory
            // rename becomes available again.
            Directory.Move(original, movedFinal);
            Assert.True(File.Exists(Path.Combine(movedFinal, "pinned.img")));
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
    public void NativeSessionReleasesPartiallyOpenedDirectoryChainWhenLaterComponentIsMissing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var parent = CreateTemporaryDirectory();
        var firstComponent = Path.Combine(parent, "opened-component");
        var movedComponent = Path.Combine(parent, "opened-component-moved");
        Directory.CreateDirectory(firstComponent);

        try
        {
            var missingDestination = Path.Combine(firstComponent, "missing", "destination");

            Assert.Throws<IOException>(() =>
                WindowsPhysicalAcquisitionDestinationSession.Open(missingDestination));

            // The first component was pinned before opening the missing component failed.
            // Failure cleanup must release it so a rename is immediately possible.
            Directory.Move(firstComponent, movedComponent);
            Assert.True(Directory.Exists(movedComponent));
        }
        finally
        {
            DeleteTemporaryDirectory(parent);
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

    [Fact]
    public async Task NativeSessionPublishesGeneratedImageAndManifestThroughRealWindowsHandles()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = CreateTemporaryDirectory();
        var bytes = Enumerable.Range(0, 384 * 1024)
            .Select(index => (byte)((index * 31 + 17) % 251))
            .ToArray();
        using var source = new GeneratedMemorySource(bytes);

        try
        {
            var imageLeaf = "native-session-fixture.img";
            var manifestLeaf = "native-session-fixture.img.diskrescue.json";
            var expectedSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes));
            var pinnedDirectory = directory;
            PhysicalAcquisitionDestinationWriteResult result;

            // The session intentionally keeps published file handles open with restrictive
            // sharing. Dispose it before independently reopening the outputs for assertions.
            await using (var session = WindowsPhysicalAcquisitionDestinationSession.Open(directory))
            {
                result = await PhysicalAcquisitionDestinationWriter.WriteAndPublishAsync(
                    source,
                    session,
                    ".native-session-fixture.image.partial",
                    imageLeaf,
                    ".native-session-fixture.manifest.partial",
                    manifestLeaf,
                    imageContent => new
                    {
                        format = "DiskRescue.NativeSessionFixture.v1",
                        imageContent.BytesCopied,
                        sha256 = imageContent.Sha256
                    });

                pinnedDirectory = session.GetCurrentDirectoryPath();
            }

            var imagePath = Path.Combine(pinnedDirectory, imageLeaf);
            var manifestPath = Path.Combine(pinnedDirectory, manifestLeaf);

            Assert.Equal(bytes.LongLength, result.BytesCopied);
            Assert.Equal(expectedSha256, result.Sha256);
            Assert.True(source.ReadAttempts > 0);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(imagePath));

            var manifest = await File.ReadAllTextAsync(manifestPath);
            Assert.Contains("DiskRescue.NativeSessionFixture.v1", manifest, StringComparison.Ordinal);
            Assert.Contains(expectedSha256, manifest, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(bytes.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture), manifest, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private sealed class GeneratedMemorySource(byte[] bytes) : IReadOnlyRandomAccessSource
    {
        private bool _disposed;

        public long Length => bytes.LongLength;
        public int ReadAttempts { get; private set; }

        public void ReadExactlyAt(long offset, Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (offset < 0 || offset > bytes.LongLength - destination.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            ReadAttempts++;
            bytes.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
        }

        public void Dispose() => _disposed = true;
    }

    [Fact]
    public async Task NativeSessionFailsClosedWhenExistingDirectoryHandleRequestsDeleteAccess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = CreateTemporaryDirectory();
        try
        {
            using (var conflictingHandle = CreateFileW(
                directory,
                DeleteAccess,
                ShareRead | ShareWrite | ShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero))
            {
                Assert.False(
                    conflictingHandle.IsInvalid,
                    $"Could not open the synthetic directory with DELETE access (Win32 error {Marshal.GetLastWin32Error()}).");

                // The pinned session deliberately omits FILE_SHARE_DELETE. An
                // existing DELETE-capable handle must therefore cause a fail-closed
                // sharing violation rather than a weaker fallback open.
                Assert.Throws<IOException>(() =>
                    WindowsPhysicalAcquisitionDestinationSession.Open(directory));
            }

            // Once the conflicting handle is released, the same directory can be pinned.
            await using var session = WindowsPhysicalAcquisitionDestinationSession.Open(directory);
            Assert.Equal(Path.GetFullPath(directory), session.GetCurrentDirectoryPath(), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
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

    [Fact]
    public void DisposeResourcesAttemptsEveryResourceAndPreservesAllFailures()
    {
        var firstFailure = new IOException("first close failed");
        var thirdFailure = new InvalidOperationException("third close failed");
        var first = new TrackingDisposable(firstFailure);
        var second = new TrackingDisposable();
        var third = new TrackingDisposable(thirdFailure);
        var fourth = new TrackingDisposable();

        var errors = WindowsPhysicalAcquisitionDestinationSession.DisposeResources(
            new IDisposable[] { first, second, third, fourth });

        Assert.Equal(4, new[] { first, second, third, fourth }.Count(resource => resource.DisposeCalled));
        Assert.Collection(
            errors,
            error => Assert.Same(firstFailure, error),
            error => Assert.Same(thirdFailure, error));
    }

    private const int FileModeInformationClass = 16;
    private const uint FileWriteThrough = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileModeInformation
    {
        public uint Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationFile(
        SafeFileHandle fileHandle,
        out IoStatusBlock ioStatusBlock,
        ref FileModeInformation fileInformation,
        uint length,
        int fileInformationClass);

    private const uint DeleteAccess = 0x00010000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    private sealed class TrackingDisposable(Exception? failure = null) : IDisposable
    {
        public bool DisposeCalled { get; private set; }

        public void Dispose()
        {
            DisposeCalled = true;
            if (failure is not null)
                throw failure;
        }
    }
}
