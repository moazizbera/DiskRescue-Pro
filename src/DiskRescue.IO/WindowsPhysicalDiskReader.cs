using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskRescue.IO;

/// <summary>
/// Metadata for a physical disk observed by the Windows read-only discovery layer.
/// The disk number is a session locator, not a durable hardware identity.
/// </summary>
public sealed record PhysicalDiskDescriptor(
    int DiskNumber,
    string DevicePath,
    long LengthBytes,
    uint BytesPerSector,
    string? Vendor = null,
    string? Model = null,
    string? SerialNumber = null,
    uint? BusType = null,
    bool? RemovableMedia = null)
{
    public bool IsReadOnly => true;

    public string DisplayIdentity =>
        !string.IsNullOrWhiteSpace(Model) ? Model! :
        !string.IsNullOrWhiteSpace(Vendor) ? Vendor! : "Hardware identity unavailable";
}

/// <summary>Result of probing Windows physical-disk device paths.</summary>
public sealed record PhysicalDiskDiscoveryResult(
    IReadOnlyList<PhysicalDiskDescriptor> Disks,
    IReadOnlyList<string> Issues);

/// <summary>
/// Windows-only physical disk discovery. Probes \.PhysicalDriveN using GENERIC_READ only.
/// This class does not enumerate volumes, mount filesystems, or expose write operations.
/// </summary>
public static class WindowsPhysicalDiskDiscovery
{
    private const int DefaultMaximumDiskNumberExclusive = 32;
    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint IoctlDiskGetDriveGeometry = 0x00070000;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDescriptorBufferSize = 64 * 1024;

    /// <summary>
    /// Probes disk numbers from zero up to (but excluding) maxDiskNumberExclusive.
    /// Each handle is opened with read access only and closed immediately after metadata queries.
    /// </summary>
    public static PhysicalDiskDiscoveryResult Enumerate(int maxDiskNumberExclusive = DefaultMaximumDiskNumberExclusive)
    {
        if (maxDiskNumberExclusive is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(maxDiskNumberExclusive), "Probe limit must be between 1 and 128.");
        EnsureWindows();

        var disks = new List<PhysicalDiskDescriptor>();
        var issues = new List<string>();

        for (var diskNumber = 0; diskNumber < maxDiskNumberExclusive; diskNumber++)
        {
            var path = $@"\\.\PhysicalDrive{diskNumber}";
            using var handle = NativeMethods.CreateFile(
                path,
                GenericRead,
                ShareRead | ShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileAttributeNormal,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                // Missing device paths are normal while probing a bounded index range.
                if (error is 5 or 32)
                    issues.Add($"PhysicalDrive{diskNumber}: access denied or device is busy (Win32 {error}).");
                else if (error is not (2 or 3 or 21))
                    issues.Add($"PhysicalDrive{diskNumber}: device could not be queried (Win32 {error}).");
                continue;
            }

            try
            {
                var descriptor = ReadDescriptor(handle, diskNumber, path);
                disks.Add(descriptor);
            }
            catch (Exception exception) when (exception is IOException or Win32Exception or InvalidDataException)
            {
                issues.Add($"PhysicalDrive{diskNumber}: {exception.Message}");
            }
        }

        return new PhysicalDiskDiscoveryResult(disks, issues);
    }

    internal static PhysicalDiskDescriptor ReadDescriptor(SafeFileHandle handle, int diskNumber, string path)
    {
        if (!NativeMethods.DeviceIoControl(
                handle, IoctlDiskGetDriveGeometry, IntPtr.Zero, 0,
                out DiskGeometry geometry, Marshal.SizeOf<DiskGeometry>(), out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read disk geometry.");

        if (!NativeMethods.DeviceIoControl(
                handle, IoctlDiskGetLengthInfo, IntPtr.Zero, 0,
                out GetLengthInformation lengthInfo, Marshal.SizeOf<GetLengthInformation>(), out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read disk capacity.");

        var sectorSize = geometry.BytesPerSector;
        if (lengthInfo.Length <= 0)
            throw new InvalidDataException("The device reported a non-positive capacity.");
        if (sectorSize is < 512 or > 65536 || (sectorSize & (sectorSize - 1)) != 0)
            throw new InvalidDataException($"The device reported an unsupported logical sector size: {sectorSize}.");
        if (lengthInfo.Length % sectorSize != 0)
            throw new InvalidDataException("The reported disk capacity is not aligned to its logical sector size.");

        var identity = TryReadStorageIdentity(handle);
        return new PhysicalDiskDescriptor(
            diskNumber, path, lengthInfo.Length, sectorSize,
            identity.Vendor, identity.Model, identity.SerialNumber,
            identity.BusType, identity.RemovableMedia);
    }

    internal static (string? Vendor, string? Model, string? SerialNumber, uint? BusType, bool? RemovableMedia)
        ParseStorageDeviceDescriptor(ReadOnlySpan<byte> data)
    {
        // STORAGE_DEVICE_DESCRIPTOR fixed header is 36 bytes on Windows.
        if (data.Length < 36)
            throw new InvalidDataException("Storage device descriptor is shorter than its fixed header.");

        var version = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data);
        var declaredSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (version < 36 || declaredSize < 36 || declaredSize > data.Length)
            throw new InvalidDataException("Storage device descriptor reports an invalid size.");

        static string? ReadString(ReadOnlySpan<byte> bytes, uint offset, uint size)
        {
            // Offsets must point into the variable-length data after the fixed header.
            if (offset < 36 || offset >= size) return null;
            var start = checked((int)offset);
            var end = start;
            while (end < size && bytes[end] != 0) end++;
            var value = System.Text.Encoding.ASCII.GetString(bytes[start..end]).Trim();
            return value.Length == 0 ? null : value;
        }

        var vendor = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var product = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        var serial = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[24..]);
        var bus = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[28..]);
        return (
            ReadString(data, vendor, declaredSize),
            ReadString(data, product, declaredSize),
            ReadString(data, serial, declaredSize),
            bus,
            data[10] != 0);
    }

    private static (string? Vendor, string? Model, string? SerialNumber, uint? BusType, bool? RemovableMedia)
        TryReadStorageIdentity(SafeFileHandle handle)
    {
        var query = new uint[] { 0, 0, 0 }; // StorageDeviceProperty, PropertyStandardQuery, additional parameters.
        var output = new byte[StorageDescriptorBufferSize];
        if (!NativeMethods.DeviceIoControl(
                handle, IoctlStorageQueryProperty, query, sizeof(uint) * query.Length,
                output, output.Length, out var bytesReturned, IntPtr.Zero))
            return (null, null, null, null, null);

        try
        {
            if (bytesReturned < 36 || bytesReturned > output.Length)
                return (null, null, null, null, null);
            var parsed = ParseStorageDeviceDescriptor(output.AsSpan(0, bytesReturned));
            return parsed;
        }
        catch (InvalidDataException)
        {
            return (null, null, null, null, null);
        }
    }

    internal static bool MatchesIdentitySnapshot(PhysicalDiskDescriptor expected, PhysicalDiskDescriptor current)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(current);

        // Geometry is checked separately. Require a reported serial number;
        // matching only a disk number, capacity, vendor, or generic model is insufficient.
        if (expected.DiskNumber != current.DiskNumber ||
            !string.Equals(expected.DevicePath, current.DevicePath, StringComparison.OrdinalIgnoreCase) ||
            expected.LengthBytes != current.LengthBytes ||
            expected.BytesPerSector != current.BytesPerSector ||
            string.IsNullOrWhiteSpace(expected.SerialNumber) ||
            string.IsNullOrWhiteSpace(current.SerialNumber))
            return false;

        static bool MatchesKnown(string? before, string? after) =>
            string.IsNullOrWhiteSpace(before) ||
            (!string.IsNullOrWhiteSpace(after) &&
             string.Equals(before.Trim(), after.Trim(), StringComparison.OrdinalIgnoreCase));

        return MatchesKnown(expected.SerialNumber, current.SerialNumber) &&
            MatchesKnown(expected.Model, current.Model) &&
            MatchesKnown(expected.Vendor, current.Vendor) &&
            (!expected.BusType.HasValue || current.BusType == expected.BusType) &&
            (!expected.RemovableMedia.HasValue || current.RemovableMedia == expected.RemovableMedia);
    }

    internal static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Physical disk access is available only on Windows.");
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DiskGeometry
    {
        public long Cylinders;
        public int MediaType;
        public uint TracksPerCylinder;
        public uint SectorsPerTrack;
        public uint BytesPerSector;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GetLengthInformation
    {
        public long Length;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            [In] uint[] inputBuffer,
            int inputBufferSize,
            [Out] byte[] outputBuffer,
            int outputBufferSize,
            out int bytesReturned,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            IntPtr inputBuffer,
            int inputBufferSize,
            out DiskGeometry outputBuffer,
            int outputBufferSize,
            out int bytesReturned,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            IntPtr inputBuffer,
            int inputBufferSize,
            out GetLengthInformation outputBuffer,
            int outputBufferSize,
            out int bytesReturned,
            IntPtr overlapped);
    }
}

/// <summary>
/// Read-only random-access view over one Windows physical disk.
/// Opening requires an elevated process on many systems. The handle requests GENERIC_READ,
/// shares read/write access with other processes, and never requests write access.
/// </summary>
internal sealed class WindowsPhysicalDiskReader : IReadOnlyRandomAccessSource
{
    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    private readonly SafeFileHandle _handle;
    private bool _disposed;

    private WindowsPhysicalDiskReader(SafeFileHandle handle, PhysicalDiskDescriptor descriptor)
    {
        _handle = handle;
        Descriptor = descriptor;
        Length = descriptor.LengthBytes;
    }

    public PhysicalDiskDescriptor Descriptor { get; }
    public long Length { get; }

    /// <summary>
    /// Opens the disk identified by the current session descriptor and rechecks capacity/sector size.
    /// This is not a durable hardware-identity check; callers must display and reconfirm the disk
    /// immediately before opening it. No write access is requested.
    /// </summary>
    public static WindowsPhysicalDiskReader Open(PhysicalDiskDescriptor expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.DiskNumber < 0 || expected.DiskNumber > 127)
            throw new ArgumentOutOfRangeException(nameof(expected), "Invalid physical disk number.");

        var expectedPath = $@"\\.\PhysicalDrive{expected.DiskNumber}";
        if (!string.Equals(expected.DevicePath, expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The device path does not match the validated physical disk number.", nameof(expected));
        if (expected.LengthBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(expected), "Expected disk capacity must be positive.");
        if (expected.BytesPerSector is < 512 or > 65536 ||
            (expected.BytesPerSector & (expected.BytesPerSector - 1)) != 0 ||
            expected.LengthBytes % expected.BytesPerSector != 0)
            throw new ArgumentException("Expected disk geometry is invalid or not sector-aligned.", nameof(expected));

        WindowsPhysicalDiskDiscovery.EnsureWindows();

        var handle = WindowsNativeMethods.CreateFile(
            expectedPath,
            GenericRead,
            ShareRead | ShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Unable to open {expectedPath} for read-only access. Administrator privileges may be required.");
        }

        try
        {
            var current = WindowsPhysicalDiskDiscovery.ReadDescriptor(handle, expected.DiskNumber, expectedPath);
            if (current.LengthBytes != expected.LengthBytes || current.BytesPerSector != expected.BytesPerSector)
                throw new IOException("The physical disk geometry changed after discovery. Close the session and rediscover devices.");
            if (!WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(expected, current))
                throw new IOException("The physical disk identity is missing or differs from the discovery snapshot. Close the session and rediscover devices.");

            return new WindowsPhysicalDiskReader(handle, current);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void ReadExactlyAt(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset > Length || destination.Length > Length - offset)
            throw new EndOfStreamException("Requested range extends beyond the physical disk boundary.");
        if (destination.Length == 0) return;

        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            var read = RandomAccess.Read(_handle, destination[totalRead..], checked(offset + totalRead));
            if (read == 0)
                throw new EndOfStreamException("The physical disk returned a short read before the requested range was complete.");
            totalRead += read;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _handle.Dispose();
        _disposed = true;
    }

    private static class WindowsNativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);
    }
}
