using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskRescue.IO;

/// <summary>
/// Windows-only destination session. Every path component is opened relative to its
/// already-pinned parent handle with FILE_OPEN_REPARSE_POINT; reparse components are
/// rejected rather than followed. File creation, identity, flush, delete, and rename
/// operate on handles. This class is not wired into physical acquisition yet.
/// </summary>
internal sealed class WindowsPhysicalAcquisitionDestinationSession : IPhysicalAcquisitionDestinationSession
{
    private const uint FileListDirectory = 0x00000001;
    private const uint FileAddFile = 0x00000002;
    private const uint FileReadAttributes = 0x00000080;
    private const uint DeleteAccess = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileAttributeDirectory = 0x00000010;
    private const int FileDispositionInfo = 4;
    private const int FileAttributeTagInfoClass = 9;
    private const int FileIdInfoClass = 18;
    private const uint DriveFixed = 3;
    private const int FileRenameInformationClass = 10;
    private const uint StatusObjectNameCollision = 0xC0000035;

    private readonly SafeFileHandle _directoryHandle;
    private readonly Dictionary<Stream, OwnedFile> _files = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    private WindowsPhysicalAcquisitionDestinationSession(SafeFileHandle directoryHandle)
    {
        _directoryHandle = directoryHandle;
        VolumeGuidPath = QueryVolumeGuidPath(directoryHandle);
        DirectoryIdentity = QueryIdentity(directoryHandle);
        if (string.IsNullOrWhiteSpace(DirectoryIdentity.VolumeIdentity) ||
            (DirectoryIdentity.FileIdHigh == 0 && DirectoryIdentity.FileIdLow == 0))
        {
            directoryHandle.Dispose();
            throw new IOException("Windows did not provide a stable destination directory identity.");
        }
    }

    public PhysicalDestinationDirectoryIdentity DirectoryIdentity { get; }
    public string VolumeGuidPath { get; }

    /// <summary>
    /// Opens a local drive-letter directory without following any reparse-point
    /// component. UNC paths, mapped/network drives, and volume mount-point components
    /// are deliberately unsupported in this first native adapter.
    /// </summary>
    public static WindowsPhysicalAcquisitionDestinationSession Open(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The pinned destination session is Windows-only.");

        var fullPath = Path.GetFullPath(directoryPath);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) ||
            root.Length != 3 ||
            !char.IsAsciiLetter(root[0]) ||
            root[1] != ':' ||
            root[2] != Path.DirectorySeparatorChar ||
            fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "Only local drive-letter destination paths are supported; UNC and volume-GUID paths fail closed.");
        }

        if (GetDriveTypeW(root) != DriveFixed)
            throw new NotSupportedException("The destination must be on a locally attached fixed drive.");

        var rootHandle = CreateFileW(
            root,
            FileListDirectory | FileAddFile | FileReadAttributes | Synchronize,
            ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (rootHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            rootHandle.Dispose();
            throw new IOException($"Could not pin destination volume root (Win32 error {error}).");
        }

        SafeFileHandle current = rootHandle;
        try
        {
            EnsureNotReparseDirectory(current);
            var relative = fullPath[root.Length..];
            var components = relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                PhysicalDestinationLeafName.Validate(component, nameof(directoryPath));
                var next = OpenRelativeDirectory(current, component, isFinalComponent: index == components.Length - 1);
                try
                {
                    EnsureNotReparseDirectory(next);
                }
                catch
                {
                    next.Dispose();
                    throw;
                }

                if (!ReferenceEquals(current, rootHandle))
                    current.Dispose();
                current = next;
            }

            if (!ReferenceEquals(current, rootHandle))
                rootHandle.Dispose();

            return new WindowsPhysicalAcquisitionDestinationSession(current);
        }
        catch
        {
            if (!ReferenceEquals(current, rootHandle))
                current.Dispose();
            rootHandle.Dispose();
            throw;
        }
    }

    public ValueTask<Stream> CreateNewFileAsync(
        string leafName,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        PhysicalDestinationLeafName.Validate(leafName, nameof(leafName));

        var handle = CreateRelativeFile(leafName);
        try
        {
            var stream = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false);
            _files.Add(stream, new OwnedFile(stream, handle));
            return ValueTask.FromResult<Stream>(stream);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public ValueTask<PhysicalDestinationFileIdentity> GetFileIdentityAsync(
        Stream file,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owned = GetOwned(file);
        return ValueTask.FromResult(QueryIdentityForFile(owned.Stream.SafeFileHandle));
    }

    public ValueTask FlushToDiskAsync(
        Stream file,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owned = GetOwned(file);
        owned.Stream.Flush(flushToDisk: true);
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishNoReplaceAsync(
        Stream file,
        string finalLeafName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owned = GetOwned(file);
        if (owned.Published)
            throw new InvalidOperationException("The destination file has already been published.");
        var leaf = PhysicalDestinationLeafName.Validate(finalLeafName, nameof(finalLeafName));
        RenameRelativeNoReplace(owned.Stream.SafeFileHandle, leaf);
        owned.Published = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteUnpublishedAsync(
        Stream file,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owned = GetOwned(file);
        if (owned.Published)
            throw new InvalidOperationException("A published destination file cannot be deleted as an unpublished temporary.");
        if (owned.DeletePending)
            return ValueTask.CompletedTask;

        var disposition = Marshal.AllocHGlobal(sizeof(byte));
        try
        {
            Marshal.WriteByte(disposition, 1);
            if (!SetFileInformationByHandle(
                    owned.Stream.SafeFileHandle, FileDispositionInfo, disposition, sizeof(byte)))
            {
                ThrowLastWin32IOException("Could not mark the unpublished destination file for deletion");
            }
            owned.DeletePending = true;
        }
        finally
        {
            Marshal.FreeHGlobal(disposition);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        foreach (var owned in _files.Values)
        {
            owned.Stream.Dispose();
            owned.Handle.Dispose();
        }
        _files.Clear();
        _directoryHandle.Dispose();
        return ValueTask.CompletedTask;
    }

    private OwnedFile GetOwned(Stream stream)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(stream);
        if (!_files.TryGetValue(stream, out var owned) || owned.DeletePending || !owned.Stream.CanRead)
            throw new ArgumentException("Stream is not an active file owned by this destination session.", nameof(stream));
        return owned;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private SafeFileHandle CreateRelativeFile(string leafName)
    {
        var handle = OpenRelative(
            _directoryHandle,
            leafName,
            GenericRead | GenericWrite | DeleteAccess | FileReadAttributes | Synchronize,
            FileCreate,
            FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint);
        return handle;
    }

    private static SafeFileHandle OpenRelativeDirectory(
        SafeFileHandle parent,
        string component,
        bool isFinalComponent) =>
        OpenRelative(
            parent,
            component,
            FileListDirectory | FileReadAttributes | Synchronize |
                (isFinalComponent ? FileAddFile : 0),
            FileOpen,
            FileDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint);

    private static SafeFileHandle OpenRelative(
        SafeFileHandle parent,
        string name,
        uint desiredAccess,
        uint disposition,
        uint options)
    {
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(name);
        if (nameBytes.Length > ushort.MaxValue - 2)
            throw new ArgumentException("Native path component is too long.", nameof(name));

        var nameBuffer = Marshal.AllocHGlobal(nameBytes.Length + 2);
        var unicodeStringBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.Copy(nameBytes, 0, nameBuffer, nameBytes.Length);
            Marshal.WriteInt16(nameBuffer, nameBytes.Length, 0);
            var unicodeString = new UnicodeString
            {
                Length = checked((ushort)nameBytes.Length),
                MaximumLength = checked((ushort)(nameBytes.Length + 2)),
                Buffer = nameBuffer
            };
            Marshal.StructureToPtr(unicodeString, unicodeStringBuffer, false);

            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodeStringBuffer,
                Attributes = ObjectCaseInsensitive
            };

            var addRef = false;
            try
            {
                parent.DangerousAddRef(ref addRef);
                var status = NtCreateFile(
                    out var rawHandle,
                    desiredAccess,
                    ref attributes,
                    out _,
                    IntPtr.Zero,
                    0,
                    ShareRead | ShareWrite | ShareDelete,
                    disposition,
                    options,
                    IntPtr.Zero,
                    0);

                if (status < 0)
                {
                    if (rawHandle != IntPtr.Zero && rawHandle != new IntPtr(-1))
                        CloseHandle(rawHandle);
                    throw new IOException(
                        $"Handle-relative NtCreateFile failed for '{name}' (NTSTATUS 0x{status:X8}).");
                }

                var result = new SafeFileHandle(rawHandle, ownsHandle: true);
                if (result.IsInvalid)
                {
                    result.Dispose();
                    throw new IOException("NtCreateFile returned an invalid handle.");
                }
                return result;
            }
            finally
            {
                if (addRef)
                    parent.DangerousRelease();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(unicodeStringBuffer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private void RenameRelativeNoReplace(SafeFileHandle fileHandle, string finalLeafName)
    {
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(finalLeafName);
        var rootOffset = IntPtr.Size;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var buffer = Marshal.AllocHGlobal(nameOffset + nameBytes.Length);
        var addRef = false;
        try
        {
            for (var i = 0; i < nameOffset + nameBytes.Length; i++)
                Marshal.WriteByte(buffer, i, 0);

            Marshal.WriteByte(buffer, 0, 0); // ReplaceIfExists = FALSE.
            _directoryHandle.DangerousAddRef(ref addRef);
            Marshal.WriteIntPtr(buffer, rootOffset, _directoryHandle.DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, IntPtr.Add(buffer, nameOffset), nameBytes.Length);

            var status = NtSetInformationFile(
                fileHandle,
                out _,
                buffer,
                checked((uint)(nameOffset + nameBytes.Length)),
                FileRenameInformationClass);
            if (status < 0)
            {
                var nativeStatus = unchecked((uint)status);
                if (nativeStatus == StatusObjectNameCollision)
                    throw new IOException($"Destination entry '{finalLeafName}' already exists.");
                throw new IOException(
                    $"Native handle-relative no-replace publication failed (NTSTATUS 0x{nativeStatus:X8}).");
            }
        }
        finally
        {
            if (addRef)
                _directoryHandle.DangerousRelease();
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string QueryVolumeGuidPath(SafeFileHandle handle)
    {
        const uint VolumeNameGuid = 0x00000001;
        var buffer = new StringBuilder(512);
        var required = GetFinalPathNameByHandleW(
            handle, buffer, checked((uint)buffer.Capacity), VolumeNameGuid);
        if (required == 0)
            ThrowLastWin32IOException("Could not resolve the pinned destination volume GUID");
        if (required >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)required + 1));
            required = GetFinalPathNameByHandleW(
                handle, buffer, checked((uint)buffer.Capacity), VolumeNameGuid);
            if (required == 0 || required >= buffer.Capacity)
                ThrowLastWin32IOException("Could not read the complete pinned destination volume path");
        }

        var finalPath = buffer.ToString();
        const string VolumePrefix = @"\\?\Volume{";
        if (!finalPath.StartsWith(VolumePrefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The pinned destination handle did not resolve to a volume-GUID path.");

        var closingBrace = finalPath.IndexOf('}');
        if (closingBrace <= VolumePrefix.Length ||
            closingBrace + 1 >= finalPath.Length ||
            finalPath[closingBrace + 1] != '\\' ||
            !Guid.TryParse(finalPath.AsSpan(VolumePrefix.Length, closingBrace - VolumePrefix.Length), out _))
        {
            throw new IOException("Windows returned a malformed volume-GUID path for the pinned destination handle.");
        }

        return finalPath[..(closingBrace + 2)];
    }

    private static PhysicalDestinationDirectoryIdentity QueryIdentity(SafeFileHandle handle)
    {
        var info = QueryFileIdInfo(handle);
        return new PhysicalDestinationDirectoryIdentity(
            $"WINVOL:{info.VolumeSerialNumber:X16}",
            BitConverter.ToUInt64(info.FileId, 0),
            BitConverter.ToUInt64(info.FileId, 8));
    }

    private static PhysicalDestinationFileIdentity QueryIdentityForFile(SafeFileHandle handle)
    {
        var info = QueryFileIdInfo(handle);
        return new PhysicalDestinationFileIdentity(
            $"WINVOL:{info.VolumeSerialNumber:X16}",
            BitConverter.ToUInt64(info.FileId, 0),
            BitConverter.ToUInt64(info.FileId, 8));
    }

    private static FileIdInfo QueryFileIdInfo(SafeFileHandle handle)
    {
        var size = Marshal.SizeOf<FileIdInfo>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, buffer, checked((uint)size)))
                ThrowLastWin32IOException("Could not query FILE_ID_INFO for destination handle");
            return Marshal.PtrToStructure<FileIdInfo>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void EnsureNotReparseDirectory(SafeFileHandle handle)
    {
        var size = Marshal.SizeOf<FileAttributeTagInfo>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, buffer, checked((uint)size)))
                ThrowLastWin32IOException("Could not query destination directory attributes");
            var info = Marshal.PtrToStructure<FileAttributeTagInfo>(buffer);
            if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
                throw new NotSupportedException("Destination path contains a reparse point; it is rejected without following it.");
            if ((info.FileAttributes & FileAttributeDirectory) == 0)
                throw new IOException("A destination path component is not a directory.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ThrowLastWin32IOException(string message)
    {
        var error = Marshal.GetLastWin32Error();
        throw new IOException($"{message} (Win32 error {error}).", new System.ComponentModel.Win32Exception(error));
    }

    private sealed class OwnedFile(FileStream stream, SafeFileHandle handle)
    {
        public FileStream Stream { get; } = stream;
        public SafeFileHandle Handle { get; } = handle;
        public bool Published { get; set; }
        public bool DeletePending { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDriveTypeW")]
    private static extern uint GetDriveTypeW(string rootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file, StringBuilder filePath, uint filePathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file, int fileInformationClass, IntPtr fileInformation, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file, int fileInformationClass, IntPtr fileInformation, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(
        SafeFileHandle fileHandle,
        out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }
}
