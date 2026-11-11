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
    public async Task AttachedReadOnlyVhdxIsDiscoveredAndPhysicalAcquisitionIsBlocked()
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

        // Exercise only the public hard-disabled boundary. AcquireAsync must reject
        // before opening a source handle, reading sectors, or creating either output.
        var blockedImagePath = Path.Combine(
            Path.GetTempPath(), $"diskrescue-vhdx-blocked-{Guid.NewGuid():N}.img");
        var disabledError = await Assert.ThrowsAsync<NotSupportedException>(() =>
            PhysicalDiskImageAcquisition.AcquireAsync(selected, blockedImagePath));
        Assert.Contains("remains disabled", disabledError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(blockedImagePath));
        Assert.False(File.Exists(blockedImagePath + ".diskrescue.json"));

        output.WriteLine($"Detected disposable PhysicalDrive{diskNumber} as bus type {selected.BusType}.");
        output.WriteLine("Physical acquisition was correctly blocked by the production safety policy.");
        output.WriteLine("The public acquisition API rejected the virtual source at its hard-disabled boundary.");
        output.WriteLine("No source sectors were read and no image or manifest was created.");
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
