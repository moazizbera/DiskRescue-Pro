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
        if (OperatingSystem.IsWindows())
            Assert.Throws<ArgumentOutOfRangeException>(() => WindowsPhysicalDiskDiscovery.Enumerate(maximum));
        else
            Assert.Throws<ArgumentOutOfRangeException>(() => WindowsPhysicalDiskDiscovery.Enumerate(maximum));
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
