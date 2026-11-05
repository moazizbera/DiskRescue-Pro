using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using DiskRescue.IO;
using DiskRescue.Partitions;

namespace DiskRescue.Desktop;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PartitionRow> _rows = new();
    private string? _lastReport;
    private string? _lastReportPath;

    public MainWindow()
    {
        InitializeComponent();
        PartitionsGrid.ItemsSource = _rows;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select a disk image (not a physical disk)", Filter = "Disk image files (*.img;*.bin;*.dd;*.raw)|*.img;*.bin;*.dd;*.raw|All files (*.*)|*.*", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) == true) { ImagePathBox.Text = dialog.FileName; ResetResults(); StatusText.Text = "Image selected · Ready for read-only inspection"; }
    }

    private async void Inspect_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ImagePathBox.Text) || !File.Exists(ImagePathBox.Text))
        {
            MessageBox.Show(this, "Choose an existing regular disk-image file first. Physical disk paths are not supported.", "Image required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var sectorSize = int.Parse(((ComboBoxItem)SectorSizeBox.SelectedItem).Tag.ToString()!);
        InspectButton.IsEnabled = false; ExportButton.IsEnabled = false; Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        StatusText.Text = "Inspecting image read-only…";
        try
        {
            var result = await Task.Run(() => InspectImage(ImagePathBox.Text, sectorSize));
            _lastReport = result.Json; _lastReportPath = ImagePathBox.Text;
            ImageSizeText.Text = FormatBytes(result.Length); PartitionCountText.Text = result.Rows.Count.ToString(); TableTypeText.Text = result.TableType;
            ValidationText.Text = result.HasWarnings ? "Review findings" : "Checks passed";
            ValidationText.Foreground = (System.Windows.Media.Brush)FindResource(result.HasWarnings ? "Warn" : "Accent");
            _rows.Clear(); foreach (var row in result.Rows) _rows.Add(row);
            FindingsBox.Text = result.Findings; ExportButton.IsEnabled = true;
            StatusText.Text = $"Inspection complete · {DateTime.Now:HH:mm:ss} · Source opened read-only";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            ResetResults(); FindingsBox.Text = "Inspection stopped safely.\n\n" + ex.Message + "\n\nNo repair or write operation was attempted.";
            StatusText.Text = "Inspection failed safely";
            MessageBox.Show(this, ex.Message, "Could not inspect image", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { InspectButton.IsEnabled = true; Mouse.OverrideCursor = null; }
    }

    private static InspectionResult InspectImage(string path, int sectorSize)
    {
        using var image = FileImageReader.Open(path);
        var mbr = MbrParser.Parse(image, sectorSize);
        var gpt = GptParser.ParsePrimaryAndBackup(image, checked((uint)sectorSize));
        var rows = new List<PartitionRow>();
        foreach (var p in mbr.Partitions) rows.Add(new(p.Index, "MBR", $"Type 0x{p.PartitionType:X2}", p.StartLba.ToString(), FormatBytes(p.ByteLength)));
        foreach (var p in gpt.Primary.Partitions) rows.Add(new(p.Index, "GPT", string.IsNullOrWhiteSpace(p.Name) ? p.TypeGuid.ToString() : p.Name, p.FirstLba.ToString(), FormatBytes(p.ByteLength)));
        var notes = new List<string> { $"Image: {Path.GetFileName(path)}", $"Length: {image.Length:N0} bytes", $"Logical sector size: {sectorSize:N0} bytes", "", $"MBR signature: {(mbr.HasValidSignature ? "Valid" : "Not detected")}", $"Protective GPT MBR: {(mbr.IsProtectiveMbr ? "Yes" : "No")}" };
        notes.AddRange(mbr.Issues.Select(x => "• MBR: " + x));
        notes.Add(""); notes.Add($"GPT primary header: {(gpt.Primary.HeaderValid ? "Valid" : "Not valid / not detected")}"); notes.Add($"GPT backup header: {(gpt.Backup.HeaderValid ? "Valid" : "Not valid / not detected")}");
        notes.AddRange(gpt.Primary.Issues.Select(x => "• GPT primary: " + x)); notes.AddRange(gpt.Backup.Issues.Select(x => "• GPT backup: " + x)); notes.AddRange(gpt.ConsistencyIssues.Select(x => "• GPT consistency: " + x));
        notes.Add(""); notes.Add("Safety boundary: image file only; no physical devices, source writes, repair, or recovery.");
        var hasGpt = gpt.Primary.HeaderValid || gpt.Backup.HeaderValid;
        var hasWarnings = mbr.Issues.Count > 0 || gpt.Primary.Issues.Count > 0 || gpt.Backup.Issues.Count > 0 || gpt.ConsistencyIssues.Count > 0 || (!mbr.HasValidSignature && !hasGpt);
        var report = new { product = "DiskRescue Pro", reportVersion = 1, mode = "read-only-image-inspection", source = new { fileName = Path.GetFileName(path), lengthBytes = image.Length, logicalSectorSizeBytes = sectorSize }, safety = new { sourceOpenedReadOnly = true, physicalDevicesSupported = false, sourceWriteOperationsAvailable = false, recoveryAvailable = false }, mbr = new { signatureValid = mbr.HasValidSignature, protectiveGptEntryDetected = mbr.IsProtectiveMbr, partitions = mbr.Partitions, diagnostics = mbr.Issues }, gpt = new { primaryHeaderValid = gpt.Primary.HeaderValid, primaryHeader = gpt.Primary.Header, primaryPartitions = gpt.Primary.Partitions, primaryDiagnostics = gpt.Primary.Issues, backupHeaderValid = gpt.Backup.HeaderValid, backupHeader = gpt.Backup.Header, backupPartitions = gpt.Backup.Partitions, backupDiagnostics = gpt.Backup.Issues, consistencyDiagnostics = gpt.ConsistencyIssues } };
        return new(image.Length, rows, hasGpt ? "GPT" : mbr.HasValidSignature ? "MBR" : "Unknown", hasWarnings, string.Join(Environment.NewLine, notes), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null) return;
        var dialog = new SaveFileDialog { Title = "Export inspection report", FileName = Path.GetFileNameWithoutExtension(_lastReportPath) + "-diskrescue-report.json", Filter = "JSON report (*.json)|*.json", AddExtension = true, DefaultExt = ".json" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, _lastReport); StatusText.Text = "JSON report exported · " + dialog.FileName; }
        catch (IOException ex) { MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ResetResults()
    {
        _rows.Clear(); _lastReport = null; _lastReportPath = null; ImageSizeText.Text = "—"; PartitionCountText.Text = "—"; TableTypeText.Text = "—";
        ValidationText.Text = "Not inspected"; ValidationText.Foreground = (System.Windows.Media.Brush)FindResource("Warn"); FindingsBox.Text = "No inspection has been run."; ExportButton.IsEnabled = false;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"]; double size = bytes; var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }

    private sealed record InspectionResult(long Length, List<PartitionRow> Rows, string TableType, bool HasWarnings, string Findings, string Json);
    public sealed record PartitionRow(int Index, string Table, string Name, string StartLba, string Size);
}