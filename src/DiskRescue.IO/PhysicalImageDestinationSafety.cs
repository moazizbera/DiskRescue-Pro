using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskRescue.IO;

public sealed record PhysicalImageDestinationAssessment(
    bool CanUseAsSeparateDestination,
    string? VolumeGuidPath,
    IReadOnlyList<int> DestinationDiskNumbers,
    IReadOnlyList<string> Blockers);

/// <summary>
/// Verifies that an existing image destination resolves to storage that does not
/// overlap the selected physical source disk. This is a preflight only; it does
/// not create files or open the source device.
/// </summary>
public static class PhysicalImageDestinationSafety
{
    private const uint ShareRead = 1;
    private const uint ShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private const uint FsctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const int ErrorMoreData = 234;
    private const int ExtentBufferSize = 64 * 1024;

    public static PhysicalImageDestinationAssessment Assess(
        int sourceDiskNumber,
        string destinationPath,
        PhysicalVolumeMappingResult volumeMap,
        long requiredBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(volumeMap);
        var blockers = new List<string>();
        if (sourceDiskNumber < 0)
            blockers.Add("Source disk number is invalid.");
        if (string.IsNullOrWhiteSpace(destinationPath))
            blockers.Add("Destination path is empty.");
        if (requiredBytes < 0)
            blockers.Add("Required image size cannot be negative.");
        if (!volumeMap.IsComplete || volumeMap.Issues.Count != 0)
            blockers.Add("A complete, diagnostic-free Windows volume map is required.");

        if (blockers.Count != 0)
            return new(false, null, Array.Empty<int>(), blockers.AsReadOnly());
        if (!OperatingSystem.IsWindows())
            return new(false, null, Array.Empty<int>(),
                new[] { "Destination-volume resolution is available only on Windows." });

        try
        {
            var fullPath = Path.GetFullPath(destinationPath);
            var destinationDirectory = Directory.Exists(fullPath)
                ? fullPath
                : Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
                throw new DirectoryNotFoundException("Destination directory does not exist.");

            var driveRoot = Path.GetPathRoot(destinationDirectory);
            if (string.IsNullOrWhiteSpace(driveRoot))
                throw new IOException("Unable to resolve the destination filesystem root.");
            var destinationDrive = new DriveInfo(driveRoot);
            if (!destinationDrive.IsReady)
                throw new IOException("Destination filesystem is not ready.");
            if (requiredBytes > 0 && destinationDrive.AvailableFreeSpace < requiredBytes)
                blockers.Add($"Destination has insufficient free space: {destinationDrive.AvailableFreeSpace:N0} bytes available, {requiredBytes:N0} bytes required.");
            if (requiredBytes > uint.MaxValue &&
                (string.Equals(destinationDrive.DriveFormat, "FAT", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(destinationDrive.DriveFormat, "FAT32", StringComparison.OrdinalIgnoreCase)))
                blockers.Add($"Destination filesystem {destinationDrive.DriveFormat} cannot store a single image file larger than 4 GiB.");

            var mountPath = new StringBuilder(1024);
            if (!NativeMethods.GetVolumePathName(destinationDirectory, mountPath, mountPath.Capacity))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to resolve the destination mount point.");

            var volumeName = new StringBuilder(1024);
            if (!NativeMethods.GetVolumeNameForVolumeMountPoint(mountPath.ToString(), volumeName, volumeName.Capacity))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to resolve the destination volume GUID.");

            var guidPath = volumeName.ToString();
            var mappedDisks = volumeMap.VolumePathsByDisk
                .Where(pair => pair.Value.Contains(guidPath, StringComparer.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .Order()
                .ToArray();

            using var volume = NativeMethods.CreateFile(
                guidPath.TrimEnd('\\'), 0, ShareRead | ShareWrite, IntPtr.Zero,
                OpenExisting, FileAttributeNormal, IntPtr.Zero);
            if (volume.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open the destination volume for read-only extent inspection.");

            var buffer = new byte[ExtentBufferSize];
            if (!NativeMethods.DeviceIoControl(volume, FsctlVolumeGetVolumeDiskExtents,
                    IntPtr.Zero, 0, buffer, buffer.Length, out var returned, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorMoreData)
                    throw new InvalidDataException("Destination volume extents exceed the bounded inspection buffer.");
                throw new Win32Exception(error, "Unable to inspect destination volume extents.");
            }

            if (returned < 0 || returned > buffer.Length)
                throw new InvalidDataException("Destination volume returned an invalid extent-buffer length.");

            var actualDisks = WindowsPhysicalDiskVolumeMap.ParseDiskNumbers(buffer.AsSpan(0, returned));
            var mappingIssues = ValidateDiskMappings(sourceDiskNumber, mappedDisks, actualDisks);
            if (mappingIssues.Count != 0)
                blockers.AddRange(mappingIssues);

            return new(blockers.Count == 0, guidPath, actualDisks, blockers.AsReadOnly());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   Win32Exception or OverflowException)
        {
            blockers.Add("Destination separation could not be verified: " + ex.Message);
            return new(false, null, Array.Empty<int>(), blockers.AsReadOnly());
        }
    }


    internal static IReadOnlyList<string> ValidateDiskMappings(
        int sourceDiskNumber,
        IReadOnlyList<int> mappedDiskNumbers,
        IReadOnlyList<int> actualDiskNumbers)
    {
        ArgumentNullException.ThrowIfNull(mappedDiskNumbers);
        ArgumentNullException.ThrowIfNull(actualDiskNumbers);
        var issues = new List<string>();
        if (sourceDiskNumber < 0)
            issues.Add("Source disk number is invalid.");
        if (mappedDiskNumbers.Count == 0 || actualDiskNumbers.Count == 0)
            issues.Add("Destination volume has no verifiable physical-disk mapping.");
        if (mappedDiskNumbers.Any(number => number < 0) ||
            actualDiskNumbers.Any(number => number < 0))
            issues.Add("Destination volume mapping contains an invalid physical disk number.");
        if (mappedDiskNumbers.Distinct().Count() != mappedDiskNumbers.Count ||
            actualDiskNumbers.Distinct().Count() != actualDiskNumbers.Count)
            issues.Add("Destination volume mapping contains duplicate physical disk entries.");
        if (issues.Count != 0)
            return issues.AsReadOnly();

        if (!mappedDiskNumbers.Order().SequenceEqual(actualDiskNumbers.Order()))
            issues.Add("Destination volume extents do not agree with the complete Windows volume map.");
        if (actualDiskNumbers.Contains(sourceDiskNumber))
            issues.Add("Destination volume shares the source physical disk; it cannot be used for the disk image.");
        return issues.AsReadOnly();
    }

    private static class NativeMethods
    {
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
