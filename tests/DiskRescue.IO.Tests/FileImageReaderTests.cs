using DiskRescue.IO;

namespace DiskRescue.IO.Tests;

public sealed class FileImageReaderTests
{
    [Fact]
    public void ReadsRequestedBytesAtOffset()
    {
        using var fixture = new TemporaryImage(new byte[] { 0, 1, 2, 3, 4, 5 });
        using var reader = FileImageReader.Open(fixture.Path);
        Span<byte> buffer = stackalloc byte[3];
        reader.ReadExactlyAt(2, buffer);
        Assert.Equal(new byte[] { 2, 3, 4 }, buffer.ToArray());
        Assert.Equal(6, reader.Length);
    }

    [Fact]
    public void RejectsNegativeOffset()
    {
        using var fixture = new TemporaryImage(new byte[] { 1, 2, 3 });
        using var reader = FileImageReader.Open(fixture.Path);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadExactlyAt(-1, new byte[1]));
    }

    [Fact]
    public void RejectsRangePastEnd()
    {
        using var fixture = new TemporaryImage(new byte[] { 1, 2, 3 });
        using var reader = FileImageReader.Open(fixture.Path);
        Assert.Throws<EndOfStreamException>(() => reader.ReadExactlyAt(2, new byte[2]));
    }

    [Fact]
    public void AllowsEmptyReadAtEnd()
    {
        using var fixture = new TemporaryImage(new byte[] { 1, 2, 3 });
        using var reader = FileImageReader.Open(fixture.Path);
        reader.ReadExactlyAt(3, Span<byte>.Empty);
    }

    private sealed class TemporaryImage : IDisposable
    {
        public TemporaryImage(byte[] data)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"diskrescue-{Guid.NewGuid():N}.img");
            using var stream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(data);
        }
        public string Path { get; }
        public void Dispose() => File.Delete(Path);
    }
}
