using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskRescue.IO;

/// <summary>
/// Best-effort, fail-closed identification of disks containing the running Windows
/// installation volume or a firmware system partition (EFI System Partition / active MBR).
/// This is one safeguard only; it is not a complete firmware boot-chain attestation.
/// </summary>
public sealed record WindowsBootDeviceMappingResult(
    bool IsComplete,
    IReadOnlySet<int> BootCriticalDiskNumbers,
    IReadOnlyList<string> Issues);

public static class WindowsBootDeviceMap
{
    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 1;
    private const uint ShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private const uint IoctlDiskGetPartitionInfoEx = 0x00070048;
    private const int PartitionInformationSize = 144;
    private static readonly Guid EfiSystemPartitionType = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");

    public static WindowsBootDeviceMappingResult Enumerate(PhysicalVolumeMappingResult volumeMap)
    {
        ArgumentNullException.ThrowIfNull(volumeMap);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Boot-device mapping is available only on Windows.");

        var issues = new List<string>();
        var bootDisks = new HashSet<int>();
        if (!volumeMap.IsComplete || volumeMap.Issues.Count != 0)
        {
            issues.Add("A complete, diagnostic-free volume map is required for boot-device exclusion.");
            return new(false, bootDisks, issues.AsReadOnly());
        }

        var volumeToDisks = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in volumeMap.VolumePathsByDisk)
        {
            foreach (var path in pair.Value)
            {
                if (!volumeToDisks.TryGetValue(path, out var disks))
                    volumeToDisks[path] = disks = new HashSet<int>();
                disks.Add(pair.Key);
            }
        }

        var windowsVolume = ResolveWindowsVolumeGuid();
        if (windowsVolume is null)
        {
            issues.Add("The running Windows installation volume could not be identified.");
        }
        else if (!volumeToDisks.TryGetValue(windowsVolume, out var windowsDisks) || windowsDisks.Count == 0)
        {
            issues.Add("The Windows installation volume is absent from the complete physical-volume map.");
        }
        else
        {
            bootDisks.UnionWith(windowsDisks);
        }

        foreach (var volumePath in volumeToDisks.Keys)
        {
            try
            {
                using var handle = NativeMethods.CreateFile(
                    volumePath.TrimEnd('\\'), GenericRead, ShareRead | ShareWrite,
                    IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
                if (handle.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open volume for partition-type inspection.");

                var buffer = new byte[PartitionInformationSize];
                if (!NativeMethods.DeviceIoControl(
                        handle, IoctlDiskGetPartitionInfoEx, IntPtr.Zero, 0,
                        buffer, buffer.Length, out var returned, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to query partition type.");

                if (returned < 40 || returned > buffer.Length)
                    throw new InvalidDataException("Partition-information response is truncated or oversized.");

                if (IsFirmwareSystemPartition(buffer.AsSpan(0, returned)))
                    bootDisks.UnionWith(volumeToDisks[volumePath]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
            {
                issues.Add($"{volumePath}: {ex.Message}");
            }
        }

        return new(issues.Count == 0, bootDisks, issues.AsReadOnly());
    }

    internal static bool IsFirmwareSystemPartition(ReadOnlySpan<byte> data)
    {
        if (data.Length < 40)
            throw new InvalidDataException("Partition-information response is shorter than its fixed fields.");

        var style = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data);
        return style switch
        {
            0 => data[32] == 0xEF || data[33] != 0, // MBR EFI type or active/system partition.
            1 => data.Length >= 48 &&
                 new Guid(data.Slice(32, 16)) == EfiSystemPartitionType,
            2 => false, // RAW disk has no recognized partition.
            _ => throw new InvalidDataException("Partition-information response has an unknown partition style.")
        };
    }

    private static string? ResolveWindowsVolumeGuid()
    {
        var windowsDirectory = new StringBuilder(32768);
        var length = NativeMethods.GetWindowsDirectory(windowsDirectory, (uint)windowsDirectory.Capacity);
        if (length == 0 || length >= windowsDirectory.Capacity)
            return null;

        var mountPath = new StringBuilder(1024);
        if (!NativeMethods.GetVolumePathName(windowsDirectory.ToString(), mountPath, mountPath.Capacity))
            return null;

        var volumeGuid = new StringBuilder(1024);
        if (!NativeMethods.GetVolumeNameForVolumeMountPoint(mountPath.ToString(), volumeGuid, volumeGuid.Capacity))
            return null;

        return volumeGuid.ToString();
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetWindowsDirectory(StringBuilder buffer, uint size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName, int bufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, StringBuilder volumeName, int bufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
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
