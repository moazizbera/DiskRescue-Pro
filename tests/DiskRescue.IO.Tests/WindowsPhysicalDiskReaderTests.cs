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
}
