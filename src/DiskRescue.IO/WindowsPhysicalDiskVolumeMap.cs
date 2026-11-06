using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskRescue.IO;

/// <summary>
/// Maps every enumerated Windows volume to the physical disk numbers it occupies.
/// The result is fail-closed: a partial enumeration must never be interpreted as a disk
/// having no mounted volumes.
/// </summary>
public sealed record PhysicalVolumeMappingResult(
    bool IsComplete,
    IReadOnlyDictionary<int, IReadOnlyList<string>> VolumePathsByDisk,
    IReadOnlyList<string> Issues)
{
    public bool CanTreatDiskAsUnmapped(int diskNumber) =>
        IsComplete && !VolumePathsByDisk.ContainsKey(diskNumber);
}

public static class WindowsPhysicalDiskVolumeMap
{
    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FsctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorMoreData = 234;
    private const int MaximumVolumeCount = 4096;
    private const int ExtentBufferSize = 64 * 1024;

    public static PhysicalVolumeMappingResult Enumerate()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Volume-to-disk mapping is available only on Windows.");

        var byDisk = new Dictionary<int, List<string>>();
        var issues = new List<string>();
        var complete = true;
        var volumeName = new StringBuilder(1024);
        var findHandle = NativeMethods.FindFirstVolume(volumeName, volumeName.Capacity);
        if (findHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            findHandle.Dispose();
            return new(false, new Dictionary<int, IReadOnlyList<string>>(),
                new[] { $"Unable to begin volume enumeration (Win32 {error})." });
        }

        using (findHandle)
        {
            var count = 0;
            while (true)
            {
                var path = volumeName.ToString();
                count++;
                if (count > MaximumVolumeCount)
                {
                    complete = false;
                    issues.Add($"Volume enumeration exceeded the safety limit of {MaximumVolumeCount}.");
                    break;
                }

                try
                {
                    using var volume = NativeMethods.CreateFile(
                        path.TrimEnd('\\'), GenericRead, ShareRead | ShareWrite, IntPtr.Zero,
                        OpenExisting, FileAttributeNormal, IntPtr.Zero);
                    if (volume.IsInvalid)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open volume for extent inspection.");

                    var buffer = new byte[ExtentBufferSize];
                    if (!NativeMethods.DeviceIoControl(
                            volume, FsctlVolumeGetVolumeDiskExtents,
                            IntPtr.Zero, 0, buffer, buffer.Length,
                            out var returned, IntPtr.Zero))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == ErrorMoreData)
                            throw new InvalidDataException("Volume disk extents exceed the bounded inspection buffer.");
                        throw new Win32Exception(error, "Unable to query volume disk extents.");
                    }

                    foreach (var diskNumber in ParseDiskNumbers(buffer.AsSpan(0, returned)))
                    {
                        if (!byDisk.TryGetValue(diskNumber, out var paths))
                            byDisk[diskNumber] = paths = new List<string>();
                        if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                            paths.Add(path);
                    }
                }
                catch (Exception ex) when (ex is IOException or Win32Exception or InvalidDataException)
                {
                    complete = false;
                    issues.Add($"{path}: {ex.Message}");
                }

                volumeName.Clear();
                if (!NativeMethods.FindNextVolume(findHandle, volumeName, volumeName.Capacity))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorNoMoreFiles)
                    {
                        complete = false;
                        issues.Add($"Volume enumeration ended unexpectedly (Win32 {error}).");
                    }
                    break;
                }
            }
        }

        return new(complete,
            byDisk.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.AsReadOnly()),
            issues);
    }

    internal static IReadOnlyList<int> ParseDiskNumbers(ReadOnlySpan<byte> data)
    {
        // VOLUME_DISK_EXTENTS has a 4-byte count followed by aligned 24-byte DISK_EXTENT entries.
        if (data.Length < 4)
            throw new InvalidDataException("Volume disk-extents response is shorter than its count field.");

        var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (count > 1024)
            throw new InvalidDataException("Volume disk-extents count exceeds the safety limit.");
        const int headerSize = 8;
        const int extentSize = 24;
        var required = checked(headerSize + checked((int)count * extentSize));
        if (data.Length < required)
            throw new InvalidDataException("Volume disk-extents response is truncated.");

        var disks = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var offset = checked(headerSize + (int)i * extentSize);
            var diskNumber = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
            var startingOffset = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data[(offset + 8)..]);
            var extentLength = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data[(offset + 16)..]);
            if (diskNumber < 0 || startingOffset < 0 || extentLength <= 0)
                throw new InvalidDataException("Volume disk extent contains invalid disk or byte-range metadata.");
            _ = checked(startingOffset + extentLength);
            disks.Add(diskNumber);
        }
        return disks.Order().ToArray();
    }

    private sealed class SafeVolumeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeVolumeFindHandle() : base(true) { }
        protected override bool ReleaseHandle() => NativeMethods.FindVolumeClose(handle);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindVolumeClose(IntPtr findHandle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeVolumeFindHandle FindFirstVolume(StringBuilder volumeName, int bufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindNextVolume(SafeVolumeFindHandle findHandle, StringBuilder volumeName, int bufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle device, uint controlCode, IntPtr inputBuffer, int inputBufferSize,
            [Out] byte[] outputBuffer, int outputBufferSize, out int bytesReturned, IntPtr overlapped);
    }
}
