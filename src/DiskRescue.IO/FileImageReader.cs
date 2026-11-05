using Microsoft.Win32.SafeHandles;

namespace DiskRescue.IO;

/// <summary>Opens a regular image file for read-only random access; physical-device paths are rejected.</summary>
public sealed class FileImageReader : IReadOnlyRandomAccessSource
{
    private readonly SafeFileHandle _handle;
    private bool _disposed;

    private FileImageReader(SafeFileHandle handle)
    {
        _handle = handle;
        Length = RandomAccess.GetLength(handle);
    }

    public long Length { get; }

    public static FileImageReader Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RejectDeviceNamespace(path);

        var fullPath = Path.GetFullPath(path);
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Only regular, non-reparse-point image files are accepted.");

        var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try { return new FileImageReader(handle); }
        catch { handle.Dispose(); throw; }
    }

    private static void RejectDeviceNamespace(string path)
    {
        if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\\??\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Physical-device and Windows device-namespace paths are not accepted.", nameof(path));
    }

    public void ReadExactlyAt(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset > Length || destination.Length > Length - offset)
            throw new EndOfStreamException("Requested range extends beyond the image boundary.");

        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            var read = RandomAccess.Read(_handle, destination[totalRead..], checked(offset + totalRead));
            if (read == 0) throw new EndOfStreamException("Image ended before the requested range was read.");
            totalRead += read;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _handle.Dispose();
        _disposed = true;
    }
}
