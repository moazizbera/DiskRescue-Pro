using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using DiskRescue.Filesystems;
using DiskRescue.IO;
using DiskRescue.Partitions;

namespace DiskRescue.Desktop;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PartitionRow> _rows = new();
    private string? _lastReport;
    private string? _lastReportPath;
    private string? _lastImagePath;\n    private CancellationTokenSource? _scanCancellation;

    public MainWindow()
    {
        InitializeComponent();
        PartitionsGrid.ItemsSource = _rows;
        ScanNtfsButton.IsEnabled = false;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a disk image (not a physical disk)",
            Filter = "Disk image files (*.img;*.bin;*.dd;*.raw)|*.img;*.bin;*.dd;*.raw|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            ImagePathBox.Text = dialog.FileName;
            ResetResults();
            StatusText.Text = "Image selected · Ready for read-only inspection";
        }
    }

    private async void Inspect_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ImagePathBox.Text) || !File.Exists(ImagePathBox.Text))
        {
            MessageBox.Show(this, "Choose an existing regular disk-image file first. Physical disk paths are not supported.", "Image required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Capture all UI values on the dispatcher thread before entering Task.Run.
        var imagePath = ImagePathBox.Text;
        var sectorSize = int.Parse(((ComboBoxItem)SectorSizeBox.SelectedItem).Tag.ToString()!);
        InspectButton.IsEnabled = false;
        BrowseButton.IsEnabled = false;
        SectorSizeBox.IsEnabled = false;
        ScanNtfsButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        StatusText.Text = "Inspecting image read-only…";

        try
        {
            var result = await Task.Run(() => InspectImage(imagePath, sectorSize));
            _lastReport = result.Json;
            _lastReportPath = imagePath;
            _lastImagePath = imagePath;
            PartitionsGrid.SelectedItem = null;
            ImageSizeText.Text = FormatBytes(result.Length);
            PartitionCountText.Text = result.Rows.Count.ToString();
            TableTypeText.Text = result.TableType;
            ValidationText.Text = result.HasWarnings ? "Review findings" : "Checks passed";
            ValidationText.Foreground = (System.Windows.Media.Brush)FindResource(result.HasWarnings ? "Warn" : "Accent");
            _rows.Clear();
            foreach (var row in result.Rows) _rows.Add(row);
            NtfsTreeView.Items.Clear();
            FindingsBox.Text = result.Findings;
            ExportButton.IsEnabled = true;
            StatusText.Text = $"Inspection complete · {DateTime.Now:HH:mm:ss} · Source opened read-only";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            ResetResults();
            FindingsBox.Text = "Inspection stopped safely.\n\n" + ex.Message + "\n\nNo repair or write operation was attempted.";
            StatusText.Text = "Inspection failed safely";
            MessageBox.Show(this, ex.Message, "Could not inspect image", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            InspectButton.IsEnabled = true;
            BrowseButton.IsEnabled = true;
            SectorSizeBox.IsEnabled = true;
            Mouse.OverrideCursor = null;
            UpdateNtfsButtonState();
        }
    }

    private void PartitionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateNtfsButtonState();

    private void UpdateNtfsButtonState()
    {
        ScanNtfsButton.IsEnabled = _lastImagePath is not null &&
            PartitionsGrid.SelectedItem is PartitionRow row &&
            row.ByteOffset >= 0 && row.ByteLength > 0;
    }

    private async void ScanNtfs_Click(object sender, RoutedEventArgs e)
    {
        if (_lastImagePath is null || PartitionsGrid.SelectedItem is not PartitionRow row)
            return;

        var imagePath = _lastImagePath;
        ScanNtfsButton.IsEnabled = false;
        InspectButton.IsEnabled = false;
        BrowseButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        NtfsTreeView.Items.Clear();
        StatusText.Text = $"Scanning {row.Table} partition {row.Index} · Read-only NTFS preview…";

        try
        {
            var result = await Task.Run(() => ScanNtfsPartition(imagePath, row.ByteOffset, row.ByteLength));
            foreach (var root in result.Tree.Roots)
                NtfsTreeView.Items.Add(CreateTreeItem(root, 0));

            if (result.Tree.Orphans.Count > 0)
            {
                var orphanRoot = new TreeViewItem
                {
                    Header = $"Unlinked / orphaned entries ({result.Tree.Orphans.Count})",
                    IsExpanded = true
                };
                foreach (var orphan in result.Tree.Orphans)
                    orphanRoot.Items.Add(CreateTreeItem(orphan, 1));
                NtfsTreeView.Items.Add(orphanRoot);
            }

            if (result.Tree.Roots.Count == 0 && result.Tree.Orphans.Count == 0)
                NtfsTreeView.Items.Add(new TreeViewItem { Header = "No filename entries found in the scanned MFT range." });

            var notes = new List<string>
            {
                "Read-only NTFS MFT preview",
                $"Volume offset: {row.ByteOffset:N0} bytes",
                $"Volume length: {row.ByteLength:N0} bytes",
                $"Bytes per sector: {result.BootSector.BytesPerSector:N0}",
                $"Cluster size: {result.BootSector.ClusterSizeBytes:N0} bytes",
                $"FILE record size: {result.BootSector.FileRecordSizeBytes:N0} bytes",
                $"Records examined: {result.Scan.RecordsExamined:N0}",
                $"Records skipped: {result.Scan.RecordsSkipped:N0}",
                $"Filename entries: {result.Scan.Entries.Count:N0}",
                $"Tree roots: {result.Tree.Roots.Count:N0}",
                $"Orphan roots: {result.Tree.Orphans.Count:N0}",
                "",
                "Coverage is limited to the initial validated $MFT::$DATA extent. Additional MFT extents and directory indexes are not followed. No file contents are read or recovered."
            };
            notes.AddRange(result.Scan.Issues.Select(x => "• " + x));
            notes.AddRange(result.Tree.Issues.Select(x => "• Tree: " + x));
            FindingsBox.Text = string.Join(Environment.NewLine, notes);
            StatusText.Text = $"NTFS preview complete · {result.Scan.Entries.Count:N0} filename entries · Source remains read-only";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            NtfsTreeView.Items.Clear();
            FindingsBox.Text = "NTFS preview stopped safely.\n\n" + ex.Message + "\n\nNo source writes, repairs, or recovery operations were attempted.";
            StatusText.Text = "NTFS preview failed safely";
            MessageBox.Show(this, ex.Message, "Could not scan NTFS partition", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            InspectButton.IsEnabled = true;
            BrowseButton.IsEnabled = true;
            Mouse.OverrideCursor = null;
            UpdateNtfsButtonState();
        }
    }


    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ImagePathBox.Text) || !File.Exists(ImagePathBox.Text))
        {
            MessageBox.Show(this, "Choose a regular disk-image file first. Physical disks are not supported.", "Image required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var imagePath = ImagePathBox.Text;
        var sectorSize = int.Parse(((ComboBoxItem)SectorSizeBox.SelectedItem).Tag.ToString()!);
        var known = _rows.Where(row => row.Table != "SCAN").Select(row => new KnownPartitionRange(row.Table, row.Index, row.ByteOffset, row.ByteLength)).ToArray();

        DiscoverButton.IsEnabled = false;
        InspectButton.IsEnabled = false;
        BrowseButton.IsEnabled = false;
        SectorSizeBox.IsEnabled = false;
        ScanNtfsButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        ScanProgressBar.Value = 0;
        ScanProgressText.Text = "Starting scan…";
        Mouse.OverrideCursor = Cursors.Wait;
        StatusText.Text = "Searching image for validated NTFS volumes · Read-only…";

        try
        {
            var progress = new Progress<PartitionScanProgress>(value =>
            {
                ScanProgressBar.Value = value.Percent;
                ScanProgressText.Text = $"{value.Percent}% · {value.CandidatesValidated} candidates";
            });
            var result = await Task.Run(() =>
            {
                using var image = FileImageReader.Open(imagePath);
                return NtfsPartitionScanner.Scan(image, sectorSize, known,
                    maximumScanBytes: image.Length, cancellationToken: _scanCancellation.Token, progress: progress);
            });

            for (var i = _rows.Count - 1; i >= 0; i--)
                if (_rows[i].Table == "SCAN") _rows.RemoveAt(i);

            var nextIndex = 1;
            foreach (var candidate in result.Candidates.Where(candidate => candidate.Status == "Lost partition candidate"))
            {
                _rows.Add(new PartitionRow(nextIndex++, "SCAN",
                    $"Lost NTFS · {candidate.Confidence}% confidence",
                    (candidate.ByteOffset / sectorSize).ToString(),
                    FormatBytes(candidate.ByteLength),
                    candidate.ByteOffset, candidate.ByteLength));
            }

            using var reportDocument = JsonDocument.Parse(_lastReport ?? "{}");
            _lastReport = JsonSerializer.Serialize(new
            {
                inspection = reportDocument.RootElement.Clone(),
                partitionDiscovery = result
            }, new JsonSerializerOptions { WriteIndented = true });
            _lastReportPath = imagePath;
            ExportButton.IsEnabled = true;

            var notes = new List<string>
            {
                "Read-only NTFS lost-partition discovery",
                $"Bytes scanned: {result.BytesScanned:N0} / {result.TotalBytes:N0}",
                $"Validated NTFS candidates: {result.Candidates.Count:N0}",
                $"Lost candidates added to the partition list: {result.Candidates.Count(candidate => candidate.Status == "Lost partition candidate"):N0}",
                $"Existing partition matches: {result.Candidates.Count(candidate => candidate.Status == "Existing partition"):N0}",
                $"Candidates overlapping known partitions: {result.Candidates.Count(candidate => candidate.Status == "Overlaps known partition"):N0}",
                $"Scan cancelled: {result.WasCancelled}",
                "",
                "A candidate is not a recovered partition. It is a validated NTFS boot-sector/geometry finding. No partition table changes or source writes are performed."
            };
            foreach (var candidate in result.Candidates)
            {
                notes.Add("");
                notes.Add($"{candidate.Status} · offset {candidate.ByteOffset:N0} · size {FormatBytes(candidate.ByteLength)} · confidence {candidate.Confidence}%");
                notes.AddRange(candidate.Evidence.Select(evidence => "• " + evidence));
            }
            notes.AddRange(result.Issues.Select(issue => "• " + issue));
            FindingsBox.Text = string.Join(Environment.NewLine, notes);
            PartitionCountText.Text = _rows.Count.ToString();
            StatusText.Text = result.WasCancelled
                ? $"Partition search cancelled · {result.BytesScanned:N0} bytes scanned"
                : $"Partition search complete · {result.Candidates.Count:N0} validated NTFS candidates";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            FindingsBox.Text = "Partition discovery stopped safely.\n\n" + ex.Message + "\n\nNo source writes, repairs, or recovery operations were attempted.";
            StatusText.Text = "Partition discovery failed safely";
            MessageBox.Show(this, ex.Message, "Partition scan failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            DiscoverButton.IsEnabled = true;
            InspectButton.IsEnabled = true;
            BrowseButton.IsEnabled = true;
            SectorSizeBox.IsEnabled = true;
            Mouse.OverrideCursor = null;
            UpdateNtfsButtonState();
        }
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e)\n    {\n        _scanCancellation?.Cancel();\n        ScanProgressText.Text = "Cancellation requested…";\n    }\n\n    private static NtfsScanUiResult ScanNtfsPartition(string path, long offset, long length)
    {
        using var image = FileImageReader.Open(path);
        var boot = NtfsBootSectorParser.Parse(image, offset, length);
        if (!boot.IsValid || boot.BootSector is null)
            throw new IOException("NTFS boot-sector validation failed: " + string.Join("; ", boot.Issues));

        var opened = NtfsMftReader.Open(image, offset, length, boot.BootSector);
        if (!opened.IsValid || opened.Reader is null)
            throw new IOException("NTFS MFT validation failed: " + string.Join("; ", opened.Issues));

        var scan = opened.Reader.ScanRecords(maximumRecords: 10000);
        var tree = NtfsDirectoryTreeBuilder.Build(scan.Entries);
        if (!scan.IsValid || !tree.IsValid)
            throw new IOException("NTFS MFT scan could not produce a safe preview.");
        return new NtfsScanUiResult(boot.BootSector, scan, tree);
    }

    private static TreeViewItem CreateTreeItem(NtfsDirectoryNode node, int depth)
    {
        var label = node.IsDirectory
            ? $"📁 {node.Name}"
            : $"📄 {node.Name}  ({node.SizeBytes:N0} bytes)";
        var item = new TreeViewItem
        {
            Header = label,
            IsExpanded = depth == 0 && node.IsDirectory
        };
        if (depth >= 256)
        {
            if (node.Children.Count > 0)
                item.Items.Add(new TreeViewItem { Header = "Further nesting hidden by safety limit." });
            return item;
        }
        foreach (var child in node.Children)
            item.Items.Add(CreateTreeItem(child, depth + 1));
        return item;
    }

    private static InspectionResult InspectImage(string path, int sectorSize)
    {
        using var image = FileImageReader.Open(path);
        var mbr = MbrParser.Parse(image, sectorSize);
        var gpt = GptParser.ParsePrimaryAndBackup(image, checked((uint)sectorSize));
        var rows = new List<PartitionRow>();
        var hasGpt = gpt.Primary.HeaderValid || gpt.Backup.HeaderValid || mbr.IsProtectiveMbr;
        foreach (var p in mbr.Partitions)
        {
            // The 0xEE protective MBR entry describes GPT compatibility, not a user partition.
            if (hasGpt && p.PartitionType == 0xEE) continue;
            rows.Add(new(p.Index, "MBR", $"Type 0x{p.PartitionType:X2}", p.StartLba.ToString(), FormatBytes(p.ByteLength), p.ByteOffset, p.ByteLength));
        }
        var selectedGpt = gpt.Primary.HeaderValid ? gpt.Primary : gpt.Backup;
        if (hasGpt)
            foreach (var p in selectedGpt.Partitions)
                rows.Add(new(p.Index, "GPT", string.IsNullOrWhiteSpace(p.Name) ? p.TypeGuid.ToString() : p.Name, p.FirstLba.ToString(), FormatBytes(p.ByteLength), p.ByteOffset, p.ByteLength));

        var notes = new List<string>
        {
            $"Image: {Path.GetFileName(path)}",
            $"Length: {image.Length:N0} bytes",
            $"Logical sector size: {sectorSize:N0} bytes",
            "",
            $"MBR signature: {(mbr.HasValidSignature ? "Valid" : "Not detected")}",
            $"Protective GPT MBR: {(mbr.IsProtectiveMbr ? "Yes" : "No")}"
        };
        notes.AddRange(mbr.Issues.Select(x => "• MBR: " + x));
        notes.Add("");
        if (hasGpt)
        {
            notes.Add($"GPT primary header: {(gpt.Primary.HeaderValid ? "Valid" : "Not valid")}");
            notes.Add($"GPT backup header: {(gpt.Backup.HeaderValid ? "Valid" : "Not valid")}");
            notes.Add($"Partition entries displayed from: {(gpt.Primary.HeaderValid ? "primary GPT copy" : gpt.Backup.HeaderValid ? "backup GPT copy (primary unavailable)" : "no validated GPT copy")}");
            notes.AddRange(gpt.Primary.Issues.Select(x => "• GPT primary: " + x));
            notes.AddRange(gpt.Backup.Issues.Select(x => "• GPT backup: " + x));
            notes.AddRange(gpt.ConsistencyIssues.Select(x => "• GPT consistency: " + x));
        }
        else notes.Add("GPT metadata: not detected (expected for an MBR-only image).");
        notes.Add("");
        notes.Add("Safety boundary: image file only; no physical devices, source writes, repair, or recovery.");

        var hasWarnings = mbr.Issues.Count > 0 ||
            (hasGpt && (gpt.Primary.Issues.Count > 0 || gpt.Backup.Issues.Count > 0 || gpt.ConsistencyIssues.Count > 0)) ||
            (!mbr.HasValidSignature && !hasGpt);
        var report = new
        {
            product = "DiskRescue Pro",
            reportVersion = 1,
            mode = "read-only-image-inspection",
            source = new { fileName = Path.GetFileName(path), lengthBytes = image.Length, logicalSectorSizeBytes = sectorSize },
            safety = new { sourceOpenedReadOnly = true, physicalDevicesSupported = false, sourceWriteOperationsAvailable = false, recoveryAvailable = false },
            mbr = new { signatureValid = mbr.HasValidSignature, protectiveGptEntryDetected = mbr.IsProtectiveMbr, partitions = mbr.Partitions, diagnostics = mbr.Issues },
            gpt = new { primaryHeaderValid = gpt.Primary.HeaderValid, primaryHeader = gpt.Primary.Header, primaryPartitions = gpt.Primary.Partitions, primaryDiagnostics = gpt.Primary.Issues, backupHeaderValid = gpt.Backup.HeaderValid, backupHeader = gpt.Backup.Header, backupPartitions = gpt.Backup.Partitions, selectedPartitionSource = gpt.Primary.HeaderValid ? "primary" : gpt.Backup.HeaderValid ? "backup" : "none", selectedPartitions = selectedGpt.Partitions, backupDiagnostics = gpt.Backup.Issues, consistencyDiagnostics = gpt.ConsistencyIssues }
        };
        return new(image.Length, rows, hasGpt ? "GPT" : mbr.HasValidSignature ? "MBR" : "Unknown", hasWarnings, string.Join(Environment.NewLine, notes), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export inspection report",
            FileName = Path.GetFileNameWithoutExtension(_lastReportPath) + "-diskrescue-report.json",
            Filter = "JSON report (*.json)|*.json",
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, _lastReport);
            StatusText.Text = "JSON report exported · " + dialog.FileName;
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ResetResults()
    {
        _rows.Clear();
        _lastReport = null;
        _lastReportPath = null;
        _lastImagePath = null;
        PartitionsGrid.SelectedItem = null;
        NtfsTreeView.Items.Clear();
        ScanNtfsButton.IsEnabled = false;
        ImageSizeText.Text = "—";
        PartitionCountText.Text = "—";
        TableTypeText.Text = "—";
        ValidationText.Text = "Not inspected";
        ValidationText.Foreground = (System.Windows.Media.Brush)FindResource("Warn");
        FindingsBox.Text = "No inspection has been run.";
        ExportButton.IsEnabled = false;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }

    private sealed record InspectionResult(long Length, List<PartitionRow> Rows, string TableType, bool HasWarnings, string Findings, string Json);
    private sealed record NtfsScanUiResult(NtfsBootSector BootSector, NtfsMftScanResult Scan, NtfsDirectoryTreeResult Tree);
    public sealed record PartitionRow(int Index, string Table, string Name, string StartLba, string Size, long ByteOffset, long ByteLength);
}
