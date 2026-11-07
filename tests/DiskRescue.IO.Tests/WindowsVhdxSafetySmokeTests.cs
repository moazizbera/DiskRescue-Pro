using DiskRescue.IO;
using Xunit;
using Xunit.Abstractions;

namespace DiskRescue.IO.Tests;

/// <summary>
/// Opt-in host integration smoke test. Run only from Test-WindowsVhdxSafety.ps1,
/// which creates and attaches a disposable VHDX in read-only mode.
/// </summary>
public sealed class WindowsVhdxSafetySmokeTests(ITestOutputHelper output)
{
    [WindowsVhdxFact]
    [Trait("Category", "WindowsVhdxIntegration")]
    public void AttachedReadOnlyVhdxIsDiscoveredAndPhysicalAcquisitionIsBlocked()
    {
        var diskNumberText = Environment.GetEnvironmentVariable("DISKRESCUE_TEST_VHDX_DISK_NUMBER");

        Assert.True(OperatingSystem.IsWindows(), "The VHDX integration test requires Windows.");
        Assert.True(int.TryParse(diskNumberText, out var diskNumber), "The VHDX disk number environment variable is invalid.");

        var inventory = WindowsPhysicalDiskDiscovery.Enumerate();
        var selected = Assert.Single(inventory.Disks, disk => disk.DiskNumber == diskNumber);

        Assert.True(selected.BusType is 14 or 15,
            $"Expected a virtual or file-backed virtual storage bus (14 or 15), but observed {selected.BusType?.ToString() ?? "unknown"}.");
        Assert.True(selected.LengthBytes > 0);
        Assert.InRange(selected.BytesPerSector, 512u, 65536u);

        var volumes = WindowsPhysicalDiskVolumeMap.Enumerate();
        var boot = WindowsBootDeviceMap.Enumerate(volumes);
        var assessment = PhysicalDiskImageAcquisition.Assess(selected, inventory, volumes, boot);

        Assert.False(assessment.CanAcquire);
        Assert.Contains(assessment.Blockers, blocker =>
            blocker.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
            blocker.Contains("layered", StringComparison.OrdinalIgnoreCase));

        output.WriteLine($"Detected disposable PhysicalDrive{diskNumber} as bus type {selected.BusType}.");
        output.WriteLine("Physical acquisition was correctly blocked by the production safety policy.");
        output.WriteLine("No source sectors were read and no acquisition API was invoked.");
    }
}

/// <summary>
/// xUnit v2 has no runtime skip API, so set the inherited Skip property during
/// test discovery when the disposable VHDX harness has not supplied a disk number.
/// </summary>
public sealed class WindowsVhdxFactAttribute : FactAttribute
{
    public WindowsVhdxFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISKRESCUE_TEST_VHDX_DISK_NUMBER")))
        {
            Skip = "Run tools/Test-WindowsVhdxSafety.ps1 to create and attach a disposable read-only VHDX.";
        }
    }
}
