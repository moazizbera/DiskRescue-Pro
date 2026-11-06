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

    [Fact]
    public void IdentitySnapshotRequiresMatchingSerialAndKnownHardwareFields()
    {
        var expected = new PhysicalDiskDescriptor(
            4, @"\\.\PhysicalDrive4", 2L * 1024 * 1024 * 1024, 512,
            "ACME", "Model X", "SERIAL-001", 17, false);
        var same = expected with { DiskNumber = 4, DevicePath = @"\\.\PhysicalDrive4" };
        var changedSerial = expected with { SerialNumber = "SERIAL-002" };
        var changedModel = expected with { Model = "Model Y" };

        Assert.True(WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(expected, same));
        Assert.False(WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(expected, changedSerial));
        Assert.False(WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(expected, changedModel));
    }

    [Fact]
    public void IdentitySnapshotRejectsMissingSerialEvenWhenModelMatches()
    {
        var expected = new PhysicalDiskDescriptor(
            1, @"\\.\PhysicalDrive1", 1024 * 1024, 512, "ACME", "Generic Disk", null);
        var current = expected with { SerialNumber = null };

        Assert.False(WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(expected, current));
    }

    [Fact]
    public void IdentitySnapshotRejectsChangedBusOrRemovableFlag()
    {
        var expected = new PhysicalDiskDescriptor(
            2, @"\\.\PhysicalDrive2", 1024 * 1024, 512, "ACME", "Model", "SERIAL", 17, false);

        Assert.False(WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(expected, expected with { BusType = 7 }));
        Assert.False(WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(expected, expected with { RemovableMedia = true }));
    }

    [Fact]
    public void PhysicalDiskPreflightBlocksUntilBootExclusionAndVerifiedImageExist()
    {
        var disk = new PhysicalDiskDescriptor(
            3, @"\\.\PhysicalDrive3", 4L * 1024 * 1024 * 1024, 512,
            "ACME", "Model Z", "SERIAL-Z", 17, false);
        var inventory = new PhysicalDiskDiscoveryResult(new[] { disk }, Array.Empty<string>());
        var volumes = new PhysicalVolumeMappingResult(
            true, new Dictionary<int, IReadOnlyList<string>>(), Array.Empty<string>());

        var result = PhysicalDiskSafety.Assess(disk, inventory, volumes);

        Assert.False(result.CanStartSectorScan);
        Assert.Contains(result.Blockers, x => x.Contains("boot-device exclusion", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Blockers, x => x.Contains("sector image", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PhysicalDiskPreflightBlocksMountedVolumesAndIncompleteEnumeration()
    {
        var disk = new PhysicalDiskDescriptor(
            3, @"\\.\PhysicalDrive3", 4L * 1024 * 1024 * 1024, 512,
            "ACME", "Model Z", "SERIAL-Z", 17, false);
        var inventory = new PhysicalDiskDiscoveryResult(new[] { disk }, Array.Empty<string>());
        var mapped = new PhysicalVolumeMappingResult(
            true,
            new Dictionary<int, IReadOnlyList<string>> { [3] = new[] { @"\\?\Volume{test}\" } },
            Array.Empty<string>());
        var incomplete = new PhysicalVolumeMappingResult(
            false, new Dictionary<int, IReadOnlyList<string>>(), new[] { "Volume query failed." });

        Assert.Contains(PhysicalDiskSafety.Assess(disk, inventory, mapped).Blockers,
            x => x.Contains("mounted filesystems", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(PhysicalDiskSafety.Assess(disk, inventory, incomplete).Blockers,
            x => x.Contains("incomplete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PhysicalDiskPreflightRejectsDuplicateSerialAndLayeredBus()
    {
        var disk = new PhysicalDiskDescriptor(
            3, @"\\.\PhysicalDrive3", 4L * 1024 * 1024 * 1024, 512,
            "ACME", "Model Z", "DUPLICATE", 8, false);
        var other = new PhysicalDiskDescriptor(
            4, @"\\.\PhysicalDrive4", 4L * 1024 * 1024 * 1024, 512,
            "ACME", "Model Z", "DUPLICATE", 8, false);
        var inventory = new PhysicalDiskDiscoveryResult(new[] { disk, other }, Array.Empty<string>());
        var volumes = new PhysicalVolumeMappingResult(
            true, new Dictionary<int, IReadOnlyList<string>>(), Array.Empty<string>());

        var result = PhysicalDiskSafety.Assess(disk, inventory, volumes, true, true);

        Assert.Contains(result.Blockers, x => x.Contains("duplicated", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Blockers, x => x.Contains("layered/virtual", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VolumeExtentParserRejectsEmptyExtentSet()
    {
        var data = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0);

        Assert.Throws<InvalidDataException>(() => WindowsPhysicalDiskVolumeMap.ParseDiskNumbers(data));
    }

}
