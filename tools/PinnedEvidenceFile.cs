using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskRescue.Preflight
{
    // Creates one new evidence file relative to a directory handle that was opened
    // component-by-component without following reparse points. This class does not
    // inspect or open any disk device; it operates only on a caller-selected host path.
    public static class PinnedEvidenceFile
    {
        private const uint FileListDirectory = 0x00000001;
        private const uint FileAddFile = 0x00000002;
        private const uint FileReadAttributes = 0x00000080;
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
        private const uint DriveFixed = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const int FileAttributeTagInfoClass = 9;

        public static FileStream CreateNew(string directoryPath, string leafName)
        {
            return CreateNewCore(directoryPath, leafName, null);
        }

        // Internal deterministic seam for the Windows regression test. Production
        // callers use CreateNew and never supply code to run while handles are pinned.
        internal static FileStream CreateNewForTest(string directoryPath, string leafName, Action afterDirectoryPinned)
        {
            if (afterDirectoryPinned == null) throw new ArgumentNullException(nameof(afterDirectoryPinned));
            return CreateNewCore(directoryPath, leafName, afterDirectoryPinned);
        }

        public static void WriteAndFlush(FileStream stream, byte[] bytes)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            WriteAndFlushCore(stream, bytes,
                (target, data) => target.Write(data, 0, data.Length),
                target => ((FileStream)target).Flush(true));
        }

        // Deterministic failure-injection seam. Production callers use WriteAndFlush.
        internal static void WriteAndFlushForTest(Stream stream, byte[] bytes,
            Action<Stream, byte[]> write, Action<Stream> flush)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (write == null) throw new ArgumentNullException(nameof(write));
            if (flush == null) throw new ArgumentNullException(nameof(flush));
            WriteAndFlushCore(stream, bytes, write, flush);
        }

        private static void WriteAndFlushCore(Stream stream, byte[] bytes,
            Action<Stream, byte[]> write, Action<Stream> flush)
        {
            try
            {
                write(stream, bytes);
                flush(stream);
            }
            finally
            {
                stream.Dispose();
            }
        }

        private static FileStream CreateNewCore(string directoryPath, string leafName, Action afterDirectoryPinned)
        {
            if (String.IsNullOrWhiteSpace(directoryPath)) throw new ArgumentException("Evidence directory is required.", nameof(directoryPath));
            if (String.IsNullOrWhiteSpace(leafName) || leafName == "." || leafName == ".." ||
                leafName.IndexOfAny(new[] {'\\', '/', ':'}) >= 0 ||
                leafName.EndsWith(".", StringComparison.Ordinal) || leafName.EndsWith(" ", StringComparison.Ordinal) ||
                leafName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Evidence filename must be one safe leaf component.", nameof(leafName));

            var full = Path.GetFullPath(directoryPath);
            var root = Path.GetPathRoot(full);
            if (root == null || root.Length != 3 || !Char.IsLetter(root[0]) || root[1] != ':' ||
                root[2] != Path.DirectorySeparatorChar || full.StartsWith(@"\\", StringComparison.Ordinal))
                throw new NotSupportedException("Evidence output supports local drive-letter directories only.");
            if (GetDriveTypeW(root) != DriveFixed)
                throw new NotSupportedException("Evidence output must be on a locally attached fixed drive.");

            var handles = new List<SafeFileHandle>();
            try
            {
                var rootHandle = CreateFileW(root,
                    FileListDirectory | FileAddFile | FileReadAttributes | Synchronize,
                    ShareRead | ShareWrite | ShareDelete, IntPtr.Zero, OpenExisting,
                    FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
                EnsureValid(rootHandle, "Could not pin evidence volume root");
                handles.Add(rootHandle);
                EnsureDirectory(rootHandle);

                SafeFileHandle current = rootHandle;
                var relative = full.Substring(root.Length);
                var components = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);
                foreach (var component in components)
                {
                    ValidateLeaf(component);
                    var next = OpenRelative(current, component,
                        FileListDirectory | FileReadAttributes | Synchronize |
                            (component == components[components.Length - 1] ? FileAddFile : 0),
                        ShareRead | ShareWrite, FileOpen,
                        FileDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint);
                    handles.Add(next);
                    EnsureDirectory(next);
                    current = next;
                }

                // The test seam runs only after every directory component is open
                // and validated, immediately before the handle-relative leaf create.
                afterDirectoryPinned?.Invoke();

                var file = OpenRelative(current, leafName,
                    0x80000000 | 0x40000000 | 0x00010000 | FileReadAttributes | Synchronize,
                    ShareRead, FileCreate,
                    FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint);
                try
                {
                    return new FileStream(file, FileAccess.Write, 4096, false);
                }
                catch { file.Dispose(); throw; }
            }
            finally
            {
                for (var i = handles.Count - 1; i >= 0; --i) handles[i].Dispose();
            }
        }

        private static void ValidateLeaf(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value == "." || value == ".." ||
                value.IndexOfAny(new[] {'\\', '/', ':'}) >= 0 ||
                value.EndsWith(".", StringComparison.Ordinal) || value.EndsWith(" ", StringComparison.Ordinal) ||
                value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Path component is not a valid leaf name.");
        }

        private static void EnsureValid(SafeFileHandle handle, string message)
        {
            if (handle == null || handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (handle != null) handle.Dispose();
                throw new IOException(message + " (Win32 error " + error + ").");
            }
        }

        private static void EnsureDirectory(SafeFileHandle handle)
        {
            var size = Marshal.SizeOf<FileAttributeTagInfo>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, buffer, (uint)size))
                    ThrowLastError("Could not inspect pinned evidence directory");
                var info = Marshal.PtrToStructure<FileAttributeTagInfo>(buffer);
                if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
                    throw new IOException("Evidence directory contains a reparse point; refusing to follow it.");
                if ((info.FileAttributes & FileAttributeDirectory) == 0)
                    throw new IOException("Evidence path component is not a directory.");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name,
            uint access, uint share, uint disposition, uint options)
        {
            var nameBytes = Encoding.Unicode.GetBytes(name);
            if (nameBytes.Length > UInt16.MaxValue - 2) throw new ArgumentException("Path component too long.");
            var nameBuffer = Marshal.AllocHGlobal(nameBytes.Length + 2);
            var unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            try
            {
                Marshal.Copy(nameBytes, 0, nameBuffer, nameBytes.Length);
                Marshal.WriteInt16(nameBuffer, nameBytes.Length, 0);
                Marshal.StructureToPtr(new UnicodeString {
                    Length = (ushort)nameBytes.Length,
                    MaximumLength = (ushort)(nameBytes.Length + 2),
                    Buffer = nameBuffer
                }, unicodeBuffer, false);

                var addRef = false;
                try
                {
                    parent.DangerousAddRef(ref addRef);
                    var attributes = new ObjectAttributes {
                        Length = Marshal.SizeOf<ObjectAttributes>(),
                        RootDirectory = parent.DangerousGetHandle(),
                        ObjectName = unicodeBuffer,
                        Attributes = ObjectCaseInsensitive
                    };
                    IntPtr raw;
                    IoStatusBlock io;
                    var status = NtCreateFile(out raw, access, ref attributes, out io, IntPtr.Zero,
                        0, share, disposition, options, IntPtr.Zero, 0);
                    if (status < 0)
                    {
                        if (raw != IntPtr.Zero && raw != new IntPtr(-1)) CloseHandle(raw);
                        throw new IOException("Handle-relative evidence path operation failed for '" + name +
                            "' (NTSTATUS 0x" + unchecked((uint)status).ToString("X8") + ").");
                    }
                    var result = new SafeFileHandle(raw, true);
                    EnsureValid(result, "Native evidence open returned an invalid handle");
                    return result;
                }
                finally { if (addRef) parent.DangerousRelease(); }
            }
            finally
            {
                Marshal.FreeHGlobal(unicodeBuffer);
                Marshal.FreeHGlobal(nameBuffer);
            }
        }

        private static void ThrowLastError(string message)
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(message + " (Win32 error " + error + ").");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes {
            public int Length; public IntPtr RootDirectory; public IntPtr ObjectName;
            public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInfo { public uint FileAttributes; public uint ReparseTag; }

        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true, EntryPoint="CreateFileW")]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern uint GetDriveTypeW(string root);
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, IntPtr info, uint size);
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("ntdll.dll")]
        private static extern int NtCreateFile(out IntPtr fileHandle, uint desiredAccess,
            ref ObjectAttributes objectAttributes, out IoStatusBlock ioStatusBlock, IntPtr allocationSize,
            uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions,
            IntPtr eaBuffer, uint eaLength);
    }
}
