using Microsoft.Win32.SafeHandles;

namespace DiskRescue.IO;

/// <summary>Opens a regular image file for read-only random access; does not open physical devices.</summary>
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
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try { return new FileImageReader(handle); }
        catch { handle.Dispose(); throw; }
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
