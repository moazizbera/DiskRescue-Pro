using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace DiskRescue.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// Drives the same Inspect and Discover UI handlers used by an operator,
    /// but only against an explicitly supplied synthetic regular image file.
    /// This is enabled only by the CI screenshot environment variable.
    /// </summary>
    public async Task CaptureDiscoveryScenarioAsync(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("A synthetic disk-image path is required.", nameof(imagePath));

        var fullPath = Path.GetFullPath(imagePath);
        if (fullPath.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Physical-device and GLOBALROOT paths are not allowed for screenshot scenarios.");

        var extension = Path.GetExtension(fullPath);
        if (!new[] { ".img", ".bin", ".dd", ".raw" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new IOException("The discovery screenshot input must be a supported regular disk-image file.");

        var file = new FileInfo(fullPath);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length == 0)
            throw new IOException("The discovery screenshot input must be an existing, non-empty regular file.");

        ImagePathBox.Text = fullPath;
        ResetResults();

        Inspect_Click(this, new RoutedEventArgs());
        await WaitForScenarioStatusAsync(
            status => status.StartsWith("Inspection complete", StringComparison.Ordinal),
            status => status.Contains("failed safely", StringComparison.OrdinalIgnoreCase),
            "Read-only image inspection");

        Discover_Click(this, new RoutedEventArgs());
        await WaitForScenarioStatusAsync(
            status => status.StartsWith("Partition search complete", StringComparison.Ordinal),
            status => status.Contains("failed safely", StringComparison.OrdinalIgnoreCase) ||
                      status.Contains("cancelled", StringComparison.OrdinalIgnoreCase),
            "Read-only lost-partition discovery");

        if (!FindingsBox.Text.Contains("FAT32 · Lost partition candidate", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The synthetic FAT32 discovery scenario did not produce the expected visible lost-partition candidate.");

        if (!_rows.Any(row => row.Table == "SCAN"))
            throw new InvalidOperationException(
                "The synthetic discovery scenario did not populate the partition candidate list.");
    }

    private async Task WaitForScenarioStatusAsync(
        Func<string, bool> completed,
        Func<string, bool> failed,
        string operation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var status = StatusText.Text;
            if (completed(status))
                return;
            if (failed(status))
                throw new InvalidOperationException($"{operation} did not complete successfully: {status}");
            await Task.Delay(100);
        }

        throw new TimeoutException($"{operation} did not finish within 90 seconds. Current status: {StatusText.Text}");
    }
}
