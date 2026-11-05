namespace DiskRescue.IO;

/// <summary>Bounded random-access reads over an immutable source.</summary>
public interface IReadOnlyRandomAccessSource : IDisposable
{
    long Length { get; }
    void ReadExactlyAt(long offset, Span<byte> destination);
}
