using System.Buffers.Binary;
using System.Text;

namespace DiskRescue.IO.Tests;

/// <summary>
/// Builds disposable, synthetic disk images for parser tests. Never opens physical devices.
/// </summary>
internal sealed class SyntheticDiskImageBuilder : IDisposable
{
    private readonly int _sectorSize;
    private readonly byte[] _image;
    private readonly byte[] _gptEntries;
    private readonly Guid _diskGuid = Guid.NewGuid();
    private bool _disposed;

    public SyntheticDiskImageBuilder(int totalSectors = 100, int sectorSize = 512, int gptEntryCount = 4, int gptEntrySize = 128)
    {
        if (totalSectors < 8) throw new ArgumentOutOfRangeException(nameof(totalSectors));
        if (sectorSize < 512 || (sectorSize & (sectorSize - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(sectorSize));
        if (gptEntryCount < 1 || gptEntrySize < 128 || gptEntrySize % 128 != 0) throw new ArgumentOutOfRangeException(nameof(gptEntryCount));
        _sectorSize = sectorSize;
        _image = new byte[checked(totalSectors * sectorSize)];
        _gptEntries = new byte[checked(gptEntryCount * gptEntrySize)];
        TotalSectors = totalSectors;
        GptEntryCount = gptEntryCount;
        GptEntrySize = gptEntrySize;
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"diskrescue-fixture-{Guid.NewGuid():N}.img");
    }

    public string Path { get; }
    public int TotalSectors { get; }
    public int SectorSize => _sectorSize;
    public int GptEntryCount { get; }
    public int GptEntrySize { get; }

    public void AddMbrPartition(int index, byte type, uint startLba, uint sectorCount, byte bootIndicator = 0)
    {
        if (index is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(index));
        var entry = _image.AsSpan(446 + index * 16, 16);
        entry.Clear();
        entry[0] = bootIndicator;
        entry[4] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(8, 4), startLba);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(12, 4), sectorCount);
    }

    public void WriteMbr(bool validSignature = true)
    {
        _image[510] = validSignature ? (byte)0x55 : (byte)0;
        _image[511] = validSignature ? (byte)0xAA : (byte)0;
        Persist();
    }

    public void AddGptPartition(int index, ulong firstLba, ulong lastLba, string name)
    {
        if (index < 0 || index >= GptEntryCount) throw new ArgumentOutOfRangeException(nameof(index));
        var entry = _gptEntries.AsSpan(checked(index * GptEntrySize), GptEntrySize);
        entry.Clear();
        Guid.NewGuid().TryWriteBytes(entry[..16]);
        Guid.NewGuid().TryWriteBytes(entry.Slice(16, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(32, 8), firstLba);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(40, 8), lastLba);
        var encodedName = Encoding.Unicode.GetBytes(name);
        encodedName.AsSpan(0, Math.Min(encodedName.Length, 72)).CopyTo(entry.Slice(56, Math.Min(encodedName.Length, 72)));
    }

    public void WriteGptCopies(uint? declaredEntryCount = null)
    {
        var count = declaredEntryCount ?? checked((uint)GptEntryCount);
        var entriesCrc = ComputeCrc32(_gptEntries);
        _gptEntries.CopyTo(_image, checked(2 * _sectorSize));
        _gptEntries.CopyTo(_image, checked((TotalSectors - 2) * _sectorSize));
        WriteGptHeader(1, checked((ulong)TotalSectors - 1), 2, entriesCrc, count);
        WriteGptHeader(checked((ulong)TotalSectors - 1), 1, checked((ulong)TotalSectors - 2), entriesCrc, count);
        Persist();
    }

    public void CorruptByte(int offset)
    {
        if ((uint)offset >= (uint)_image.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        _image[offset] ^= 0x5A;
        Persist();
    }

    private void WriteGptHeader(ulong currentLba, ulong backupLba, ulong entriesLba, uint entriesCrc, uint entryCount)
    {
        var header = _image.AsSpan(checked((int)currentLba * _sectorSize), _sectorSize);
        header.Clear();
        "EFI PART"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(12, 4), 92);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(24, 8), currentLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(32, 8), backupLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(40, 8), 34);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(48, 8), checked((ulong)TotalSectors - 34));
        _diskGuid.TryWriteBytes(header.Slice(56, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(72, 8), entriesLba);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(80, 4), entryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(84, 4), checked((uint)GptEntrySize));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(88, 4), entriesCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), ComputeCrc32(header[..92]));
    }

    private void Persist() => File.WriteAllBytes(Path, _image);

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (File.Exists(Path)) File.Delete(Path);
        _disposed = true;
    }
}
