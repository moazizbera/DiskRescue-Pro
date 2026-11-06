using System.Buffers.Binary;
using Xunit;

using DiskRescue.IO;

namespace DiskRescue.IO.Tests;

public sealed class WindowsPhysicalDiskReaderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(129)]
    public void DiscoveryRejectsOutOfRangeProbeLimit(int maximum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsPhysicalDiskDiscovery.Enumerate(maximum));
    }

    [Theory]
    [InlineData(0, 512)]
    [InlineData(1024 * 1024, 0)]
    [InlineData(1024 * 1024, 513)]
    [InlineData(1024 * 1024 + 1, 512)]
    public void ReaderRejectsInvalidExpectedGeometryBeforeOpeningDevice(long length, uint sectorSize)
    {
        var descriptor = new PhysicalDiskDescriptor(0, @"\\.\PhysicalDrive0", length, sectorSize);
        Assert.ThrowsAny<ArgumentException>(() => WindowsPhysicalDiskReader.Open(descriptor));
    }

    [Fact]
    public void DiscoveryDoesNotAttemptPhysicalDeviceAccessOnNonWindows()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Throws<PlatformNotSupportedException>(() => WindowsPhysicalDiskDiscovery.Enumerate());
    }

    [Fact]
    public void ReaderDoesNotAttemptPhysicalDeviceAccessOnNonWindows()
    {
        if (OperatingSystem.IsWindows())
            return;

        var descriptor = new PhysicalDiskDescriptor(0, @"\\.\PhysicalDrive0", 1024 * 1024, 512);
        Assert.Throws<PlatformNotSupportedException>(() => WindowsPhysicalDiskReader.Open(descriptor));
    }

    [Fact]
    public void ReaderRejectsInvalidDiskNumberBeforeOpeningDevice()
    {
        var descriptor = new PhysicalDiskDescriptor(-1, @"\\.\PhysicalDrive-1", 1024 * 1024, 512);
        if (OperatingSystem.IsWindows())
            Assert.Throws<ArgumentOutOfRangeException>(() => WindowsPhysicalDiskReader.Open(descriptor));
        else
            Assert.Throws<PlatformNotSupportedException>(() => WindowsPhysicalDiskReader.Open(descriptor));
    }

    [Fact]
    public void StorageDescriptorParserReadsBoundedHardwareIdentityFields()
    {
        var data = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 64);
        data[10] = 1; // RemovableMedia
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24, 4), 53);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28, 4), 7);
        "ACME"u8.CopyTo(data.AsSpan(36));
        "DiskModel"u8.CopyTo(data.AsSpan(42));
        "SERIAL123"u8.CopyTo(data.AsSpan(53));

        var result = WindowsPhysicalDiskDiscovery.ParseStorageDeviceDescriptor(data);

        Assert.Equal("ACME", result.Vendor);
        Assert.Equal("DiskModel", result.Model);
        Assert.Equal("SERIAL123", result.SerialNumber);
        Assert.Equal((uint)7, result.BusType);
        Assert.True(result.RemovableMedia);
    }

    [Fact]
    public void StorageDescriptorParserRejectsTruncatedHeader()
    {
        Assert.Throws<InvalidDataException>(() =>
            WindowsPhysicalDiskDiscovery.ParseStorageDeviceDescriptor(new byte[35]));
    }

    [Fact]
    public void StorageDescriptorParserRejectsOutOfBoundsDeclaredSize()
    {
        var data = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 128);

        Assert.Throws<InvalidDataException>(() =>
            WindowsPhysicalDiskDiscovery.ParseStorageDeviceDescriptor(data));
    }

    [Fact]
    public void StorageDescriptorParserIgnoresOutOfRangeStringOffsets()
    {
        var data = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 35);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), uint.MaxValue);

        var result = WindowsPhysicalDiskDiscovery.ParseStorageDeviceDescriptor(data);

        Assert.Null(result.Vendor);
        Assert.Null(result.Model);
    }

    [Fact]
    public void VolumeExtentParserReadsDistinctDiskNumbersFromMultiExtentVolume()
    {
        var data = new byte[8 + 2 * 24];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8, 4), 3);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16, 8), 1024);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(24, 8), 4096);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(32, 4), 7);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(40, 8), 8192);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(48, 8), 4096);

        var disks = WindowsPhysicalDiskVolumeMap.ParseDiskNumbers(data);

        Assert.Equal(new[] { 3, 7 }, disks);
    }

    [Fact]
    public void VolumeExtentParserRejectsTruncatedExtentArray()
    {
        var data = new byte[8 + 23];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 1);

        Assert.Throws<InvalidDataException>(() => WindowsPhysicalDiskVolumeMap.ParseDiskNumbers(data));
    }

    [Fact]
    public void VolumeExtentParserRejectsInvalidExtentMetadata()
    {
        var data = new byte[8 + 24];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8, 4), -1);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(24, 8), 4096);

        Assert.Throws<InvalidDataException>(() => WindowsPhysicalDiskVolumeMap.ParseDiskNumbers(data));
    }

    [Fact]
    public void VolumeMapNeverTreatsDiskAsUnmappedWhenEnumerationIsIncomplete()
    {
        var result = new PhysicalVolumeMappingResult(
            false,
            new Dictionary<int, IReadOnlyList<string>>(),
            new[] { "A volume could not be inspected." });

        Assert.False(result.CanTreatDiskAsUnmapped(12));
    }

    [Fact]
    public void VolumeMapTreatsDiskAsUnmappedOnlyAfterCompleteEnumeration()
    {
        var result = new PhysicalVolumeMappingResult(
            true,
            new Dictionary<int, IReadOnlyList<string>>(),
            Array.Empty<string>());

        Assert.True(result.CanTreatDiskAsUnmapped(12));
    }

}
