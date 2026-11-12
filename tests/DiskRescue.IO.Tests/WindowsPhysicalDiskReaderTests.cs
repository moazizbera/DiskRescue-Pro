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
        data[40] = 0;
        "DiskModel"u8.CopyTo(data.AsSpan(42));
        data[51] = 0;
        "SERIAL123"u8.CopyTo(data.AsSpan(53));
        data[62] = 0;

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
    public void StorageDescriptorParserIgnoresStringOffsetsIntoFixedHeader()
    {
        var data = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 35);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24, 4), 1);
        data[36] = (byte)'V';

        var result = WindowsPhysicalDiskDiscovery.ParseStorageDeviceDescriptor(data);

        Assert.Null(result.Vendor);
        Assert.Null(result.Model);
        Assert.Null(result.SerialNumber);
    }

    [Fact]
    public void StorageDescriptorParserIgnoresUnterminatedIdentityStrings()
    {
        var data = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24, 4), 36);
        Array.Fill(data, (byte)'X', 36, data.Length - 36);

        var result = WindowsPhysicalDiskDiscovery.ParseStorageDeviceDescriptor(data);

        Assert.Null(result.Vendor);
        Assert.Null(result.Model);
        Assert.Null(result.SerialNumber);
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
    public void BootPartitionParserRecognizesGptEfiSystemPartition()
    {
        var data = new byte[144];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 1);
        new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B").ToByteArray().CopyTo(data, 32);

        Assert.True(WindowsBootDeviceMap.IsFirmwareSystemPartition(data));
    }

    [Theory]
    [InlineData(0xEF, 0, true)]
    [InlineData(0x07, 1, true)]
    [InlineData(0x07, 0, false)]
    public void BootPartitionParserConservativelyRecognizesMbrSystemPartitions(
        byte partitionType, byte active, bool expected)
    {
        var data = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0);
        data[32] = partitionType;
        data[33] = active;

        Assert.Equal(expected, WindowsBootDeviceMap.IsFirmwareSystemPartition(data));
    }

    [Fact]
    public void BootPartitionParserRejectsUnknownPartitionStyle()
    {
        var data = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 99);

        Assert.Throws<InvalidDataException>(() => WindowsBootDeviceMap.IsFirmwareSystemPartition(data));
    }

    [Fact]
    public void BootPartitionParserRejectsTruncatedGptPartitionTypeGuid()
    {
        var data = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 1);

        Assert.Throws<InvalidDataException>(() => WindowsBootDeviceMap.IsFirmwareSystemPartition(data));
    }

    [Fact]
    public void PhysicalPreflightUsesCompleteBootMapToIdentifyBootCriticalDisk()
    {
        var disk = new PhysicalDiskDescriptor(
            3, @"\\.\\PhysicalDrive3", 4L * 1024 * 1024 * 1024, 512,
            "ACME", "Model Z", "SERIAL-Z", 17, false);
        var inventory = new PhysicalDiskDiscoveryResult(new[] { disk }, Array.Empty<string>());
        var volumes = new PhysicalVolumeMappingResult(
            true, new Dictionary<int, IReadOnlyList<string>>(), Array.Empty<string>());
        var bootMap = new WindowsBootDeviceMappingResult(true, new HashSet<int> { 4 }, Array.Empty<string>());

        var result = PhysicalDiskSafety.Assess(disk, inventory, volumes, bootMap);

        Assert.DoesNotContain(result.Blockers,
            x => x.Contains("boot-device exclusion", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Blockers,
            x => x.Contains("verified sector image", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PhysicalPreflightBlocksDiskContainingFirmwareSystemPartition()
    {
        var disk = new PhysicalDiskDescriptor(
            3, @"\\.\\PhysicalDrive3", 4L * 1024 * 1024 * 1024, 512,
            "ACME", "Model Z", "SERIAL-Z", 17, false);
        var inventory = new PhysicalDiskDiscoveryResult(new[] { disk }, Array.Empty<string>());
        var volumes = new PhysicalVolumeMappingResult(
            true, new Dictionary<int, IReadOnlyList<string>>(), Array.Empty<string>());
        var bootMap = new WindowsBootDeviceMappingResult(true, new HashSet<int> { 3 }, Array.Empty<string>());

        var result = PhysicalDiskSafety.Assess(disk, inventory, volumes, bootMap);

        Assert.Contains(result.Blockers,
            x => x.Contains("firmware system partition", StringComparison.OrdinalIgnoreCase));
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
        var changedLocator = expected with { DiskNumber = 5, DevicePath = @"\\.\PhysicalDrive5" };
        var changedCapacity = expected with { LengthBytes = expected.LengthBytes + 512 };

        Assert.True(WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(expected, same));
        Assert.False(WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(expected, changedSerial));
        Assert.False(WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(expected, changedModel));
        Assert.False(WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(expected, changedLocator));
        Assert.False(WindowsPhysicalDiskDiscovery.MatchesIdentitySnapshot(expected, changedCapacity));
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
    public void PhysicalDiskPreflightBlocksVolumeMapDiagnosticsEvenIfMarkedComplete()
    {
        var disk = new PhysicalDiskDescriptor(
            3, @"\\.\PhysicalDrive3", 4L * 1024 * 1024 * 1024, 512,
            "ACME", "Model Z", "SERIAL-Z", 17, false);
        var inventory = new PhysicalDiskDiscoveryResult(new[] { disk }, Array.Empty<string>());
        var inconsistentMap = new PhysicalVolumeMappingResult(
            true, new Dictionary<int, IReadOnlyList<string>>(),
            new[] { "A volume extent query reported an error." });

        var result = PhysicalDiskSafety.Assess(disk, inventory, inconsistentMap);

        Assert.Contains(result.Blockers, x => x.Contains("diagnostics", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PhysicalDiskPreflightBlocksDiskKeyWithEmptyVolumeList()
    {
        var disk = new PhysicalDiskDescriptor(
            3, @"\\.\PhysicalDrive3", 4L * 1024 * 1024 * 1024, 512,
            "ACME", "Model Z", "SERIAL-Z", 17, false);
        var inventory = new PhysicalDiskDiscoveryResult(new[] { disk }, Array.Empty<string>());
        var ambiguousMap = new PhysicalVolumeMappingResult(
            true,
            new Dictionary<int, IReadOnlyList<string>> { [3] = Array.Empty<string>() },
            Array.Empty<string>());

        var result = PhysicalDiskSafety.Assess(disk, inventory, ambiguousMap);

        Assert.Contains(result.Blockers, x => x.Contains("associated with an enumerated Windows volume", StringComparison.OrdinalIgnoreCase));
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

        var result = PhysicalDiskSafety.Assess(disk, inventory, volumes);

        Assert.Contains(result.Blockers, x => x.Contains("duplicated", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Blockers, x => x.Contains("layered/virtual", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DestinationDiskMappingAllowsOnlyMatchingSeparatePhysicalDisks()
    {
        var issues = PhysicalImageDestinationSafety.ValidateDiskMappings(
            2, new[] { 5, 7 }, new[] { 7, 5 });

        Assert.Empty(issues);
    }

    [Fact]
    public void DestinationDiskMappingRejectsAnySourceDiskOverlap()
    {
        var issues = PhysicalImageDestinationSafety.ValidateDiskMappings(
            2, new[] { 2, 5 }, new[] { 5, 2 });

        Assert.Contains(issues, issue => issue.Contains("shares the source physical disk", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DestinationDiskMappingRejectsDisagreementWithWindowsVolumeMap()
    {
        var issues = PhysicalImageDestinationSafety.ValidateDiskMappings(
            2, new[] { 5 }, new[] { 6 });

        Assert.Contains(issues, issue => issue.Contains("do not agree", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DestinationDiskMappingRejectsEmptyDuplicateAndInvalidMappings()
    {
        Assert.NotEmpty(PhysicalImageDestinationSafety.ValidateDiskMappings(
            2, Array.Empty<int>(), Array.Empty<int>()));
        Assert.NotEmpty(PhysicalImageDestinationSafety.ValidateDiskMappings(
            2, new[] { 5, 5 }, new[] { 5 }));
        Assert.NotEmpty(PhysicalImageDestinationSafety.ValidateDiskMappings(
            2, new[] { -1 }, new[] { -1 }));
    }

    [Fact]
    public void VolumeExtentParserRejectsEmptyExtentSet()
    {
        var data = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0);

        Assert.Throws<InvalidDataException>(() => WindowsPhysicalDiskVolumeMap.ParseDiskNumbers(data));
    }


    [Fact]
    public void DestinationCapacityBlocksWhenFreeSpaceIsInsufficient()
    {
        var issues = PhysicalImageDestinationSafety.ValidateFilesystemCapacity(
            "NTFS", availableBytes: 10_000, requiredBytes: 10_001);

        Assert.Contains(issues, issue => issue.Contains("insufficient free space", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("FAT", 2_147_483_648L)]
    [InlineData("FAT32", 4_294_967_296L)]
    public void DestinationCapacityBlocksFilesystemSingleFileLimit(string fileSystem, long requiredBytes)
    {
        var issues = PhysicalImageDestinationSafety.ValidateFilesystemCapacity(
            fileSystem, availableBytes: long.MaxValue, requiredBytes: requiredBytes);

        Assert.Contains(issues, issue => issue.Contains("single image file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DestinationCapacityAllowsLargeImageOnExfatWhenSpaceIsAvailable()
    {
        var issues = PhysicalImageDestinationSafety.ValidateFilesystemCapacity(
            "exFAT", availableBytes: 8L * 1024 * 1024 * 1024, requiredBytes: 4L * 1024 * 1024 * 1024);

        Assert.Empty(issues);
    }

}
