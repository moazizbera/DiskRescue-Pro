using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using DiskRescue.Filesystems;
using DiskRescue.IO;
using DiskRescue.Partitions;

namespace DiskRescue.Desktop;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PartitionRow> _rows = new();
    private readonly ObservableCollection<CandidateComparisonRow> _candidateRows = new();
    private readonly ObservableCollection<NtfsEntryViewRow> _ntfsEntryRows = new();
    private List<NtfsEntryViewRow> _allNtfsEntryRows = new();
    private string? _lastReport;
    private string? _lastReportPath;
    private string? _lastImagePath;
    private string? _lastNtfsPreviewImagePath;
    private long? _lastNtfsPreviewPartitionOffset;
    private long? _lastNtfsPreviewPartitionLength;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _recoveryCancellation;
    private long _partitionMapLength;

    public MainWindow()
    {
        InitializeComponent();
        PartitionsGrid.ItemsSource = _rows;
        CandidatesGrid.ItemsSource = _candidateRows;
        NtfsEntriesGrid.ItemsSource = _ntfsEntryRows;
        ScanNtfsButton.IsEnabled = false;
    }

    private void PhysicalDiskInventory_Click(object sender, RoutedEventArgs e)
    {
        var window = new PhysicalAcquisitionWindow { Owner = this };
        if (window.ShowDialog() == true && !string.IsNullOrWhiteSpace(window.AcquiredImagePath))
        {
            ImagePathBox.Text = window.AcquiredImagePath;
            ResetResults();
            StatusText.Text = "Verified physical disk image selected · click Inspect image, then Find lost partitions";
        }
        else
        {
            StatusText.Text = "Physical acquisition window closed · source writes and repair remain unavailable";
        }
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
            _partitionMapLength = result.Length;
            PartitionCountText.Text = result.Rows.Count.ToString();
            TableTypeText.Text = result.TableType;
            ValidationText.Text = result.HasWarnings ? "Review findings" : "Checks passed";
            ValidationText.Foreground = (System.Windows.Media.Brush)FindResource(result.HasWarnings ? "Warn" : "Accent");
            _rows.Clear();
            foreach (var row in result.Rows) _rows.Add(row);
            DrawPartitionMap();
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

    private void PartitionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PartitionsGrid.SelectedItem is not PartitionRow selected || !IsCurrentNtfsPreview(selected))
        {
            _lastNtfsPreviewImagePath = null;
            _lastNtfsPreviewPartitionOffset = null;
            _lastNtfsPreviewPartitionLength = null;
            _allNtfsEntryRows.Clear();
            _ntfsEntryRows.Clear();
            if (NtfsEntriesGrid is not null) NtfsEntriesGrid.SelectedItem = null;
            if (NtfsEntryDetailsBox is not null)
                NtfsEntryDetailsBox.Text = "Select a partition and run the NTFS preview before reviewing file entries.";
        }
        UpdateNtfsButtonState();
    }

    private bool IsCurrentNtfsPreview(PartitionRow? partition) =>
        partition is not null && _lastNtfsPreviewImagePath is not null &&
        string.Equals(_lastNtfsPreviewImagePath, _lastImagePath, StringComparison.OrdinalIgnoreCase) &&
        _lastNtfsPreviewPartitionOffset == partition.ByteOffset &&
        _lastNtfsPreviewPartitionLength == partition.ByteLength;

    private void CandidatesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CandidatesGrid.SelectedItem is not CandidateComparisonRow candidate)
        {
            CandidateEvidenceBox.Text = "Select a candidate to inspect its validation evidence.";
            return;
        }

        CandidateEvidenceBox.Text = string.Join(Environment.NewLine, new[]
        {
            $"{candidate.FileSystem} · {candidate.Status}",
            $"Byte offset: {candidate.Offset} · Declared size: {candidate.Size}",
            $"Confidence: {candidate.Confidence}% · Backup corroboration: {candidate.BackupEvidence}",
            $"Known-partition relationship: {candidate.PartitionRelation}",
            "",
            "Evidence:",
            candidate.Evidence,
            "",
            "Advisory (no source changes):",
            candidate.Advisory
        });
    }

    private void UpdateNtfsButtonState()
    {
        ScanNtfsButton.IsEnabled = _lastImagePath is not null &&
            PartitionsGrid.SelectedItem is PartitionRow row &&
            row.ByteOffset >= 0 && row.ByteLength > 0;
        if (CheckRecoveryDestinationButton is not null)
            CheckRecoveryDestinationButton.IsEnabled = IsCurrentNtfsPreview(PartitionsGrid.SelectedItem as PartitionRow) &&
                NtfsEntriesGrid.SelectedItem is NtfsEntryViewRow entry && entry.EntryType == "File" && entry.IsDeleted;
    }

    private async void ScanNtfs_Click(object sender, RoutedEventArgs e)
    {
        if (_lastImagePath is null || PartitionsGrid.SelectedItem is not PartitionRow row)
            return;

        var imagePath = _lastImagePath;
        ScanNtfsButton.IsEnabled = false;
        _lastNtfsPreviewImagePath = null;
        _lastNtfsPreviewPartitionOffset = null;
        _lastNtfsPreviewPartitionLength = null;
        _allNtfsEntryRows.Clear();
        _ntfsEntryRows.Clear();
        NtfsEntriesGrid.SelectedItem = null;
        CheckRecoveryDestinationButton.IsEnabled = false;
        NtfsEntryDetailsBox.Text = "Scanning selected partition; previous file-entry results have been cleared.";
        InspectButton.IsEnabled = false;
        BrowseButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        NtfsTreeView.Items.Clear();
        StatusText.Text = $"Scanning {row.Table} partition {row.Index} · Read-only NTFS preview…";

        try
        {
            var result = await Task.Run(() => ScanNtfsPartition(imagePath, row.ByteOffset, row.ByteLength));
            _lastNtfsPreviewImagePath = imagePath;
            _lastNtfsPreviewPartitionOffset = row.ByteOffset;
            _lastNtfsPreviewPartitionLength = row.ByteLength;
            _allNtfsEntryRows = result.Scan.Entries.Select(entry => new NtfsEntryViewRow(
                entry.Name,
                entry.IsDirectory ? "Directory" : "File",
                entry.RecordNumber,
                $"{entry.ParentRecordNumber}:{entry.ParentSequenceNumber}",
                entry.RealSize,
                $"0x{entry.FileAttributes:X8}",
                entry.SequenceNumber,
                entry.Namespace,
                entry.AllocatedSize,
                entry.IsDeleted)).ToList();
            ApplyNtfsEntryFilter();

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
                result.UsedMftContinuation
                    ? "Validated resident $ATTRIBUTE_LIST continuation extents were included where reachable through the initial MFT map. Non-resident attribute lists, recursive bootstrap discovery, and directory indexes are not followed. No file contents are read or recovered."
                    : "Coverage is limited to the initial validated $MFT::$DATA extent when continuation discovery was unavailable or unnecessary. Non-resident attribute lists, recursive bootstrap discovery, and directory indexes are not followed. No file contents are read or recovered."
            };
            notes.AddRange(result.ContinuationIssues.Select(x => "• MFT continuation: " + x));
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
        var known = _rows.Where(row => row.Table != "SCAN")
            .Select(row => new KnownPartitionRange(row.Table, row.Index, row.ByteOffset, row.ByteLength)).ToArray();

        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        CancelScanButton.IsEnabled = true;
        DiscoverButton.IsEnabled = false;
        InspectButton.IsEnabled = false;
        BrowseButton.IsEnabled = false;
        SectorSizeBox.IsEnabled = false;
        ScanNtfsButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        ScanProgressBar.Value = 0;
        ScanProgressText.Text = "Starting scan…";
        Mouse.OverrideCursor = Cursors.Wait;
        StatusText.Text = "Searching image for validated NTFS, FAT32, and exFAT volumes · Read-only…";

        try
        {
            var progress = new Progress<PartitionScanProgress>(value =>
            {
                ScanProgressBar.Value = value.Percent;
                ScanProgressText.Text = $"{value.Percent}% · {value.CandidatesValidated} candidates";
            });
            var results = await Task.Run(() =>
            {
                using var image = FileImageReader.Open(imagePath);
                var ntfs = NtfsPartitionScanner.Scan(image, sectorSize, known,
                    maximumScanBytes: image.Length, cancellationToken: cancellation.Token, progress: progress);
                var fat = ntfs.WasCancelled
                    ? new FatPartitionDiscoveryResult(Array.Empty<DiscoveredFatPartitionCandidate>(),
                        0, image.Length, true,
                        new[] { "FAT32/exFAT scan was not started because cancellation was requested during NTFS scanning." })
                    : FatPartitionScanner.Scan(image, sectorSize, known,
                        maximumScanBytes: image.Length, cancellationToken: cancellation.Token, progress: progress);
                return (Ntfs: ntfs, Fat: fat);
            });

            var ntfsResult = results.Ntfs;
            var fatResult = results.Fat;
            for (var i = _rows.Count - 1; i >= 0; i--)
                if (_rows[i].Table == "SCAN") _rows.RemoveAt(i);

            var nextIndex = 1;
            _candidateRows.Clear();
            foreach (var candidate in ntfsResult.Candidates)
            {
                _candidateRows.Add(new CandidateComparisonRow(
                    "NTFS", candidate.Status, candidate.ByteOffset.ToString("N0"),
                    FormatBytes(candidate.ByteLength), candidate.Confidence,
                    candidate.BackupBootSectorValid ? "Validated" : "Not validated",
                    candidate.MatchingPartition ?? (candidate.Status == "Overlaps known partition" ? "Overlaps known partition" : "None"),
                    string.Join(Environment.NewLine, candidate.Evidence)));
                if (candidate.Status == "Lost partition candidate")
                    _rows.Add(new PartitionRow(nextIndex++, "SCAN",
                        $"Lost NTFS · {candidate.Confidence}% confidence",
                        (candidate.ByteOffset / sectorSize).ToString(),
                        FormatBytes(candidate.ByteLength),
                        candidate.ByteOffset, candidate.ByteLength));
            }
            foreach (var candidate in fatResult.Candidates)
            {
                _candidateRows.Add(new CandidateComparisonRow(
                    candidate.FileSystem, candidate.Status, candidate.ByteOffset.ToString("N0"),
                    FormatBytes(candidate.ByteLength), candidate.Confidence,
                    candidate.BackupBootRegionValid ? "Validated" : "Not validated",
                    candidate.MatchingPartition ?? (candidate.Status == "Overlaps known partition" ? "Overlaps known partition" : "None"),
                    string.Join(Environment.NewLine, candidate.Evidence)));
                if (candidate.Status == "Lost partition candidate")
                    _rows.Add(new PartitionRow(nextIndex++, "SCAN",
                        $"Lost {candidate.FileSystem} · {candidate.Confidence}% confidence",
                        (candidate.ByteOffset / sectorSize).ToString(),
                        FormatBytes(candidate.ByteLength),
                        candidate.ByteOffset, candidate.ByteLength));
            }
            CandidateEvidenceBox.Text = _candidateRows.Count == 0
                ? "No validated candidates were found."
                : $"Select one of {_candidateRows.Count} validated candidates to inspect its evidence.";

            using var reportDocument = JsonDocument.Parse(_lastReport ?? "{}");
            _lastReport = JsonSerializer.Serialize(new
            {
                inspection = reportDocument.RootElement.Clone(),
                partitionDiscovery = new { ntfs = ntfsResult, fat32ExFat = fatResult }
            }, new JsonSerializerOptions { WriteIndented = true });
            _lastReportPath = imagePath;
            ExportButton.IsEnabled = true;

            var notes = new List<string>
            {
                "Read-only lost-partition discovery · NTFS, FAT32, and exFAT",
                $"NTFS bytes scanned: {ntfsResult.BytesScanned:N0} / {ntfsResult.TotalBytes:N0}",
                $"FAT32/exFAT bytes scanned: {fatResult.BytesScanned:N0} / {fatResult.TotalBytes:N0}",
                $"Validated NTFS candidates: {ntfsResult.Candidates.Count:N0}",
                $"Validated FAT32/exFAT candidates: {fatResult.Candidates.Count:N0}",
                $"Lost candidates added to the partition list: {ntfsResult.Candidates.Count(candidate => candidate.Status == "Lost partition candidate") + fatResult.Candidates.Count(candidate => candidate.Status == "Lost partition candidate"):N0}",
                $"Existing partition matches: {ntfsResult.Candidates.Count(candidate => candidate.Status == "Existing partition") + fatResult.Candidates.Count(candidate => candidate.Status == "Existing partition"):N0}",
                $"Candidates overlapping known partitions: {ntfsResult.Candidates.Count(candidate => candidate.Status == "Overlaps known partition") + fatResult.Candidates.Count(candidate => candidate.Status == "Overlaps known partition"):N0}",
                $"Scan cancelled: {ntfsResult.WasCancelled || fatResult.WasCancelled}",
                "",
                "A candidate is not a recovered partition. It is a validated filesystem boot-region/geometry finding. No partition table changes or source writes are performed."
            };
            foreach (var candidate in ntfsResult.Candidates)
            {
                notes.Add("");
                notes.Add($"NTFS · {candidate.Status} · offset {candidate.ByteOffset:N0} · size {FormatBytes(candidate.ByteLength)} · confidence {candidate.Confidence}%");
                notes.AddRange(candidate.Evidence.Select(evidence => "• " + evidence));
            }
            foreach (var candidate in fatResult.Candidates)
            {
                notes.Add("");
                notes.Add($"{candidate.FileSystem} · {candidate.Status} · offset {candidate.ByteOffset:N0} · size {FormatBytes(candidate.ByteLength)} · confidence {candidate.Confidence}%");
                notes.AddRange(candidate.Evidence.Select(evidence => "• " + evidence));
            }
            notes.AddRange(ntfsResult.Issues.Select(issue => "• NTFS: " + issue));
            notes.AddRange(fatResult.Issues.Select(issue => "• FAT32/exFAT: " + issue));
            FindingsBox.Text = string.Join(Environment.NewLine, notes);
            PartitionCountText.Text = _rows.Count.ToString();
            DrawPartitionMap();
            var totalCandidates = ntfsResult.Candidates.Count + fatResult.Candidates.Count;
            var wasCancelled = ntfsResult.WasCancelled || fatResult.WasCancelled;
            StatusText.Text = wasCancelled
                ? $"Partition search cancelled · {totalCandidates:N0} validated candidates"
                : $"Partition search complete · {totalCandidates:N0} validated NTFS/FAT32/exFAT candidates";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Partition search cancelled safely";
            ScanProgressText.Text = "Cancelled";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            FindingsBox.Text = "Partition discovery stopped safely.\n\n" + ex.Message + "\n\nNo source writes, repairs, or recovery operations were attempted.";
            StatusText.Text = "Partition discovery failed safely";
            MessageBox.Show(this, ex.Message, "Partition scan failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            CancelScanButton.IsEnabled = false;
            _scanCancellation = null;
            DiscoverButton.IsEnabled = true;
            InspectButton.IsEnabled = true;
            BrowseButton.IsEnabled = true;
            SectorSizeBox.IsEnabled = true;
            Mouse.OverrideCursor = null;
            UpdateNtfsButtonState();
        }
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e)
    {
        _scanCancellation?.Cancel();
        ScanProgressText.Text = "Cancellation requested…";
    }

    private static NtfsScanUiResult ScanNtfsPartition(string path, long offset, long length)
    {
        using var image = FileImageReader.Open(path);
        var boot = NtfsBootSectorParser.Parse(image, offset, length);
        if (!boot.IsValid || boot.BootSector is null)
            throw new IOException("NTFS boot-sector validation failed: " + string.Join("; ", boot.Issues));

        var opened = NtfsMftReader.Open(image, offset, length, boot.BootSector);
        if (!opened.IsValid || opened.Reader is null)
            throw new IOException("NTFS MFT validation failed: " + string.Join("; ", opened.Issues));

        // Match the CLI's validated resident $ATTRIBUTE_LIST continuation handling
        // so the desktop preview does not silently show a narrower MFT range.
        var continuation = opened.Reader.ExtendUsingResidentAttributeList();
        var scanReader = continuation.IsValid && continuation.Reader is not null
            ? continuation.Reader
            : opened.Reader;
        var scan = scanReader.ScanRecords(maximumRecords: 10000);
        var tree = NtfsDirectoryTreeBuilder.Build(scan.Entries);
        if (!scan.IsValid || !tree.IsValid)
            throw new IOException("NTFS MFT scan could not produce a safe preview.");
        return new NtfsScanUiResult(boot.BootSector, scan, tree,
            continuation.IsValid && continuation.Extended,
            continuation.Issues);
    }

    private void NtfsEntrySearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyNtfsEntryFilter();

    private void ApplyNtfsEntryFilter()
    {
        if (NtfsEntriesGrid is null) return;
        var query = NtfsEntrySearchBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allNtfsEntryRows
            : _allNtfsEntryRows.Where(row =>
                row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.RecordNumber.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.ParentReference.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.Attributes.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.EntryType.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        _ntfsEntryRows.Clear();
        foreach (var row in filtered)
            _ntfsEntryRows.Add(row);
        NtfsEntryDetailsBox.Text = $"{_ntfsEntryRows.Count:N0} of {_allNtfsEntryRows.Count:N0} bounded MFT entries shown. Select a row for metadata.";
    }

    private void NtfsEntriesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateNtfsButtonState();
        if (NtfsEntriesGrid.SelectedItem is not NtfsEntryViewRow row)
            return;
        NtfsEntryDetailsBox.Text = string.Join(Environment.NewLine, new[]
        {
            row.Name,
            $"Type: {row.EntryType} · MFT record: {row.RecordNumber} · Sequence: {row.SequenceNumber}",
            $"Parent reference: {row.ParentReference} · Filename namespace: {row.Namespace}",
            $"Real size: {row.SizeBytes:N0} bytes · Allocated size: {row.AllocatedSize:N0} bytes",
            $"File attributes: {row.Attributes} · Deleted flag: {(row.IsDeleted ? "Yes" : "No")}",
            "Metadata comes from the scanned MFT filename entry; file content and original-byte integrity are not assessed."
        });
    }

    private void CheckRecoveryDestination_Click(object sender, RoutedEventArgs e)
    {
        if (_lastImagePath is null ||
            PartitionsGrid.SelectedItem is not PartitionRow partition ||
            NtfsEntriesGrid.SelectedItem is not NtfsEntryViewRow entry)
        {
            MessageBox.Show(this, "Select an inspected NTFS partition and a file entry first.", "Recovery preflight", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (partition.Table == "SCAN" || entry.EntryType != "File" || !entry.IsDeleted || !IsCurrentNtfsPreview(partition))
        {
            MessageBox.Show(this, "Preflight is limited to deleted file entries in a selected, existing NTFS partition. Lost-partition candidates and directories are not eligible.", "Recovery preflight", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (entry.SizeBytes > long.MaxValue)
        {
            MessageBox.Show(this, "The selected file size exceeds the supported destination-capacity check.", "Recovery preflight", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var safeName = string.Concat(entry.Name.Select(ch =>
            System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        if (string.IsNullOrWhiteSpace(safeName) || safeName is "." or "..")
            safeName = $"mft-record-{entry.RecordNumber}.bin";

        var dialog = new SaveFileDialog
        {
            Title = "Choose a new recovery output path (preflight only)",
            FileName = safeName,
            Filter = "All files (*.*)|*.*",
            AddExtension = false,
            OverwritePrompt = false,
            CheckPathExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            if (File.Exists(dialog.FileName) || Directory.Exists(dialog.FileName) ||
                File.Exists(dialog.FileName + ".recovery.json") ||
                Directory.Exists(dialog.FileName + ".recovery.json"))
                throw new IOException("The output or recovery-manifest path already exists. Existing files are never overwritten.");

            var map = WindowsPhysicalDiskVolumeMap.Enumerate();
            var assessment = PhysicalImageDestinationSafety.AssessForImageFile(
                _lastImagePath, dialog.FileName, map, checked((long)entry.SizeBytes));
            var lines = new List<string>
            {
                $"Selected file: {entry.Name}",
                $"MFT record: {entry.RecordNumber} · Sequence: {entry.SequenceNumber}",
                $"Expected logical size: {entry.SizeBytes:N0} bytes",
                $"Destination: {dialog.FileName}",
                "",
                assessment.CanUseAsSeparateDestination
                    ? "DESTINATION PREFLIGHT PASSED"
                    : "DESTINATION PREFLIGHT BLOCKED",
                assessment.VolumeGuidPath is null ? "Destination volume: not verified" : "Destination volume: " + assessment.VolumeGuidPath,
                assessment.DestinationDiskNumbers.Count == 0
                    ? "Physical disk mapping: not verified"
                    : "Destination physical disk(s): " + string.Join(", ", assessment.DestinationDiskNumbers)
            };
            lines.AddRange(assessment.Blockers.Select(issue => "BLOCKER: " + issue));
            NtfsEntryDetailsBox.Text = string.Join(Environment.NewLine, lines);
            StatusText.Text = assessment.CanUseAsSeparateDestination
                ? "Destination preflight passed · no data exported; recovery is not yet available in the desktop"
                : "Destination preflight blocked · no data exported";
            if (!assessment.CanUseAsSeparateDestination)
                MessageBox.Show(this, string.Join(Environment.NewLine, assessment.Blockers), "Destination blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            NtfsEntryDetailsBox.Text = "Destination preflight stopped safely.\\n\\n" + ex.Message;
            StatusText.Text = "Destination preflight failed safely";
            MessageBox.Show(this, ex.Message, "Destination preflight", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RecoverDeletedFile_Click(object sender, RoutedEventArgs e)
    {
        if (_lastImagePath is null ||
            PartitionsGrid.SelectedItem is not PartitionRow partition ||
            NtfsEntriesGrid.SelectedItem is not NtfsEntryViewRow entry ||
            !IsCurrentNtfsPreview(partition) || partition.Table == "SCAN" ||
            entry.EntryType != "File" || !entry.IsDeleted)
        {
            MessageBox.Show(this, "Select a deleted file from the current NTFS preview first.", "Recovery", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            MessageBox.Show(this, "Deleted-image export requires Windows physical-volume mapping.", "Recovery unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (entry.SizeBytes == 0 || entry.SizeBytes > long.MaxValue)
        {
            MessageBox.Show(this, "The selected entry has no usable bounded size for export.", "Recovery", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var safeName = string.Concat(entry.Name.Select(ch =>
            System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        if (string.IsNullOrWhiteSpace(safeName) || safeName is "." or "..")
            safeName = $"mft-record-{entry.RecordNumber}.bin";
        var dialog = new SaveFileDialog
        {
            Title = "Recover deleted file to a separate physical disk",
            FileName = safeName,
            Filter = "All files (*.*)|*.*",
            AddExtension = false,
            OverwritePrompt = false,
            CheckPathExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        var imagePath = _lastImagePath;
        var outputPath = dialog.FileName;
        var manifestPath = outputPath + ".recovery.json";
        var maximumBytes = entry.SizeBytes;
        var sectorSize = int.Parse(((ComboBoxItem)SectorSizeBox.SelectedItem).Tag.ToString()!);
        try
        {
            if (File.Exists(outputPath) || Directory.Exists(outputPath) ||
                File.Exists(manifestPath) || Directory.Exists(manifestPath))
                throw new IOException("Output or recovery manifest already exists. Existing files are never overwritten.");
            var preflight = PhysicalImageDestinationSafety.AssessForImageFile(
                imagePath, outputPath, WindowsPhysicalDiskVolumeMap.Enumerate(), checked((long)maximumBytes));
            if (!preflight.CanUseAsSeparateDestination)
                throw new IOException("Destination is not verified as physically separate: " + string.Join("; ", preflight.Blockers));

            var confirm = MessageBox.Show(this,
                $"Export deleted MFT record {entry.RecordNumber} ({entry.Name})?\n\nMaximum bytes: {maximumBytes:N0}\nDestination: {outputPath}\n\nThe output hash is not proof of original content integrity.",
                "Confirm bounded deleted-file export", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;

            _recoveryCancellation = new CancellationTokenSource();
            CancelRecoveryButton.IsEnabled = true;
            RecoverDeletedFileButton.IsEnabled = false;
            CheckRecoveryDestinationButton.IsEnabled = false;
            RecoveryProgressBar.Value = 0;
            RecoveryProgressText.Text = "Starting bounded export…";
            StatusText.Text = "Recovering deleted NTFS stream to verified separate storage…";
            var progress = new Progress<ulong>(bytes =>
            {
                RecoveryProgressBar.Value = maximumBytes == 0 ? 0 :
                    Math.Min(100, bytes * 100d / maximumBytes);
                RecoveryProgressText.Text = $"{bytes:N0} / {maximumBytes:N0} bytes";
            });
            var result = await Task.Run(() => ExportDeletedFile(
                imagePath, outputPath, partition.ByteOffset, partition.ByteLength,
                sectorSize, entry.RecordNumber, maximumBytes, _recoveryCancellation.Token, progress));
            NtfsEntryDetailsBox.Text = $"Export complete\nOutput: {result.OutputPath}\nManifest: {result.ManifestPath}\nBytes: {result.BytesWritten:N0}\nSHA-256: {result.Sha256}\nContent integrity verified: No";
            RecoveryProgressBar.Value = 100;
            RecoveryProgressText.Text = $"Completed · {result.BytesWritten:N0} bytes";
            StatusText.Text = "Deleted-file export completed · content integrity is not verified";
            MessageBox.Show(this, $"Export completed.\n\n{result.OutputPath}\n\nSHA-256: {result.Sha256}\n\nThis hash identifies exported bytes only.", "Recovery complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            RecoveryProgressText.Text = "Cancelled · temporary output discarded";
            StatusText.Text = "Deleted-file export cancelled safely";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            RecoveryProgressText.Text = "Failed safely";
            StatusText.Text = "Deleted-file export failed safely";
            MessageBox.Show(this, ex.Message, "Recovery stopped safely", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _recoveryCancellation?.Dispose();
            _recoveryCancellation = null;
            CancelRecoveryButton.IsEnabled = false;
            RecoverDeletedFileButton.IsEnabled = true;
            UpdateNtfsButtonState();
        }
    }

    private void CancelRecovery_Click(object sender, RoutedEventArgs e)
    {
        _recoveryCancellation?.Cancel();
        RecoveryProgressText.Text = "Cancellation requested…";
    }

    private static DeletedDesktopExportResult ExportDeletedFile(
        string imagePath, string outputPath, long volumeOffset, long volumeLength,
        int sectorSize, ulong recordNumber, ulong maximumBytes,
        CancellationToken cancellationToken, IProgress<ulong> progress)
    {
        var fullImagePath = Path.GetFullPath(imagePath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new IOException("Output directory could not be resolved.");
        var manifestPath = fullOutputPath + ".recovery.json";
        if (string.Equals(fullImagePath, fullOutputPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Output cannot overwrite the source image.");
        if (!Directory.Exists(directory) || File.Exists(fullOutputPath) ||
            Directory.Exists(fullOutputPath) || File.Exists(manifestPath) || Directory.Exists(manifestPath))
            throw new IOException("Destination directory must exist and output/manifest paths must be new.");

        var sourceInfo = new FileInfo(fullImagePath);
        var sourceWriteTime = sourceInfo.LastWriteTimeUtc;
        var initialAssessment = PhysicalImageDestinationSafety.AssessForImageFile(
            fullImagePath, fullOutputPath, WindowsPhysicalDiskVolumeMap.Enumerate(), checked((long)maximumBytes));
        if (!initialAssessment.CanUseAsSeparateDestination)
            throw new IOException("Initial destination safety check failed: " + string.Join("; ", initialAssessment.Blockers));

        using var image = FileImageReader.Open(fullImagePath);
        var boot = NtfsBootSectorParser.Parse(image, volumeOffset, volumeLength);
        if (!boot.IsValid || boot.BootSector is null || boot.BootSector.BytesPerSector != sectorSize)
            throw new IOException("NTFS boot-sector validation failed or sector size changed: " + string.Join("; ", boot.Issues));
        var opened = NtfsMftReader.Open(image, volumeOffset, volumeLength, boot.BootSector);
        if (!opened.IsValid || opened.Reader is null)
            throw new IOException("NTFS MFT validation failed: " + string.Join("; ", opened.Issues));

        using var lease = DeletedRecoveryTemporaryFileLease.Create(directory);
        NtfsDeletedDataStreamCopyOperationResult copied;
        ulong bytesWritten;
        using (var destination = new FileStream(lease.OutputPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
        {
            copied = opened.Reader.CopyDeletedDataStreamTo(
                recordNumber, destination, maximumBytes, string.Empty, cancellationToken, progress);
            if (!copied.IsValid || copied.Copy is null || !copied.Copy.IsValid)
                throw new IOException("Deleted-stream recovery was refused or failed: " +
                    string.Join("; ", copied.Issues.Concat(copied.Assessment?.Warnings ?? Array.Empty<string>())));
            bytesWritten = copied.Copy.BytesWritten;
            destination.Flush(true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        string sha256;
        using (var verify = File.OpenRead(lease.OutputPath))
            sha256 = Convert.ToHexString(SHA256.HashData(verify));
        cancellationToken.ThrowIfCancellationRequested();

        sourceInfo.Refresh();
        if (sourceInfo.Length != image.Length || sourceInfo.LastWriteTimeUtc != sourceWriteTime)
            throw new IOException("Source image changed during export; refusing publication.");

        var assessment = copied.Assessment!;
        var manifest = new
        {
            product = "DiskRescue Pro",
            manifestVersion = 1,
            operation = "bounded-deleted-ntfs-data-stream-export",
            createdAtUtc = DateTimeOffset.UtcNow,
            source = new { fileName = Path.GetFileName(fullImagePath), lengthBytes = image.Length,
                lastWriteTimeUtc = sourceWriteTime, volumeOffsetBytes = volumeOffset,
                volumeLengthBytes = volumeLength, logicalSectorSizeBytes = sectorSize },
            deletedFile = new { mftRecordNumber = recordNumber, metadataAssessment = assessment.Metadata.MetadataStatus.ToString(),
                preflightStatus = assessment.Status.ToString(),
                clusterAllocation = assessment.ClusterAllocation?.Status.ToString() ?? "NotApplicableResidentStream" },
            dataStream = new { isResident = assessment.IsResident, lengthBytes = assessment.Length,
                initializedSizeBytes = assessment.InitializedSize, clusterSizeBytes = boot.BootSector.ClusterSizeBytes,
                physicalRunCount = assessment.Runs.Count, physicalRuns = assessment.Runs.Select(run => new {
                    startingVcn = run.StartingVcn, clusterCount = run.ClusterCount, startingLcn = run.StartingLcn }) },
            export = new { fileName = Path.GetFileName(fullOutputPath), fullPath = fullOutputPath,
                bytesWritten, maximumBytes, sha256, contentIntegrityVerified = false },
            safety = new { sourceOpenedReadOnly = true, sourceWriteOperationsAvailable = false,
                sourceContentHashCompared = false, contentIntegrityVerified = false, physicalDiskRecoveryValidated = false },
            warnings = assessment.Warnings
        };
        using (var manifestStream = new FileStream(lease.ManifestPath, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.SequentialScan))
        {
            JsonSerializer.Serialize(manifestStream, manifest, new JsonSerializerOptions { WriteIndented = true });
            manifestStream.Flush(true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var finalAssessment = PhysicalImageDestinationSafety.AssessForImageFile(
            fullImagePath, fullOutputPath, WindowsPhysicalDiskVolumeMap.Enumerate(), checked((long)maximumBytes));
        if (!finalAssessment.CanUseAsSeparateDestination)
            throw new IOException("Final destination safety check failed: " + string.Join("; ", finalAssessment.Blockers));
        DeletedRecoveryOutputPublisher.PublishPair(lease.OutputPath, fullOutputPath, lease.ManifestPath, manifestPath);
        return new(fullOutputPath, manifestPath, bytesWritten, sha256);
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
            $"Image: {System.IO.Path.GetFileName(path)}",
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
            source = new { fileName = System.IO.Path.GetFileName(path), lengthBytes = image.Length, logicalSectorSizeBytes = sectorSize },
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
            FileName = System.IO.Path.GetFileNameWithoutExtension(_lastReportPath) + "-diskrescue-report.json",
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

    private void PartitionMapCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPartitionMap();

    private void DrawPartitionMap()
    {
        if (PartitionMapCanvas is null) return;
        PartitionMapCanvas.Children.Clear();
        var width = PartitionMapCanvas.ActualWidth;
        var height = PartitionMapCanvas.ActualHeight;
        if (_partitionMapLength <= 0 || width <= 1 || height <= 1) return;

        const double inset = 4;
        var usableWidth = Math.Max(1, width - inset * 2);
        var scale = usableWidth / _partitionMapLength;
        var cursor = 0L;
        var laneTop = 16d;
        var laneHeight = 38d;

        void AddSegment(long start, long length, string label, Brush fill, double top, double segmentHeight, string tooltip)
        {
            if (length <= 0) return;
            var left = inset + Math.Clamp(start * scale, 0, usableWidth);
            var right = inset + Math.Clamp((start + (double)length) * scale, 0, usableWidth);
            var segmentWidth = Math.Max(0, right - left);
            if (segmentWidth <= 0.5) return;

            var rectangle = new Rectangle
            {
                Width = segmentWidth,
                Height = segmentHeight,
                RadiusX = 3,
                RadiusY = 3,
                Fill = fill,
                Stroke = new SolidColorBrush(Color.FromRgb(11, 18, 32)),
                StrokeThickness = 1,
                ToolTip = tooltip
            };
            Canvas.SetLeft(rectangle, left);
            Canvas.SetTop(rectangle, top);
            PartitionMapCanvas.Children.Add(rectangle);

            if (segmentWidth >= 58 && !string.IsNullOrWhiteSpace(label))
            {
                var text = new TextBlock
                {
                    Text = label,
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false,
                    Width = Math.Max(0, segmentWidth - 8),
                    TextAlignment = TextAlignment.Center
                };
                Canvas.SetLeft(text, left + 4);
                Canvas.SetTop(text, top + (segmentHeight - 14) / 2);
                PartitionMapCanvas.Children.Add(text);
            }
        }

        var knownRows = _rows.Where(row => row.Table != "SCAN" && row.ByteOffset >= 0 && row.ByteLength > 0)
            .OrderBy(row => row.ByteOffset).ToArray();
        foreach (var row in knownRows)
        {
            var start = Math.Clamp(row.ByteOffset, 0, _partitionMapLength);
            // Use subtraction before addition so malformed ranges cannot overflow.
            var length = Math.Min(row.ByteLength, _partitionMapLength - start);
            var end = start + length;
            if (start > cursor)
                AddSegment(cursor, start - cursor, "", new SolidColorBrush(Color.FromRgb(37, 52, 72)), laneTop, laneHeight,
                    $"Unallocated image space · {FormatBytes(start - cursor)}");
            // Do not paint duplicate/overlapping ranges on top of earlier validated entries.
            var visibleStart = Math.Max(start, cursor);
            if (end > visibleStart)
            {
                var color = row.Table == "GPT"
                    ? new SolidColorBrush(Color.FromRgb(36, 139, 156))
                    : new SolidColorBrush(Color.FromRgb(55, 111, 176));
                AddSegment(visibleStart, end - visibleStart, row.Name, color, laneTop, laneHeight,
                    $"{row.Table} · {row.Name}\nStart: {FormatBytes(start)}\nSize: {FormatBytes(length)}");
            }
            cursor = Math.Max(cursor, end);
        }
        if (cursor < _partitionMapLength)
            AddSegment(cursor, _partitionMapLength - cursor, "", new SolidColorBrush(Color.FromRgb(37, 52, 72)), laneTop, laneHeight,
                $"Unallocated image space · {FormatBytes(_partitionMapLength - cursor)}");

        foreach (var candidate in _rows.Where(row => row.Table == "SCAN" && row.ByteOffset >= 0 && row.ByteLength > 0))
            AddSegment(candidate.ByteOffset, candidate.ByteLength, "", new SolidColorBrush(Color.FromRgb(242, 166, 90)),
                3, 9, $"Detected lost-partition candidate · {candidate.Name}\nStart: {FormatBytes(candidate.ByteOffset)}\nSize: {candidate.Size}");

        var legendItems = new (string Label, Color Color)[]
        {
            ("GPT", Color.FromRgb(36, 139, 156)),
            ("MBR", Color.FromRgb(55, 111, 176)),
            ("Lost candidate", Color.FromRgb(242, 166, 90)),
            ("Unallocated", Color.FromRgb(37, 52, 72))
        };
        var legend = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
        foreach (var item in legendItems)
        {
            legend.Children.Add(new Rectangle
            {
                Width = 7, Height = 7, RadiusX = 1, RadiusY = 1,
                Fill = new SolidColorBrush(item.Color),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });
            legend.Children.Add(new TextBlock
            {
                Text = item.Label,
                Foreground = (Brush)FindResource("Muted"),
                FontSize = 9,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
        }
        Canvas.SetRight(legend, 6);
        Canvas.SetTop(legend, height - 12);
        PartitionMapCanvas.Children.Add(legend);
    }

    private void ResetResults()
    {
        _rows.Clear();
        _partitionMapLength = 0;
        PartitionMapCanvas.Children.Clear();
        _lastReport = null;
        _lastReportPath = null;
        _lastImagePath = null;
        _lastNtfsPreviewImagePath = null;
        _lastNtfsPreviewPartitionOffset = null;
        _lastNtfsPreviewPartitionLength = null;
        PartitionsGrid.SelectedItem = null;
        NtfsTreeView.Items.Clear();
        _allNtfsEntryRows.Clear();
        _ntfsEntryRows.Clear();
        if (CheckRecoveryDestinationButton is not null)
            CheckRecoveryDestinationButton.IsEnabled = false;
        if (NtfsEntrySearchBox is not null)
            NtfsEntrySearchBox.Text = string.Empty;
        if (NtfsEntryDetailsBox is not null)
            NtfsEntryDetailsBox.Text = "Run an NTFS preview, then select an entry to inspect its metadata.";
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
    private sealed record DeletedDesktopExportResult(string OutputPath, string ManifestPath, ulong BytesWritten, string Sha256);\n\n    private sealed record NtfsScanUiResult(NtfsBootSector BootSector, NtfsMftScanResult Scan, NtfsDirectoryTreeResult Tree, bool UsedMftContinuation, IReadOnlyList<string> ContinuationIssues);
    public sealed record NtfsEntryViewRow(
    string Name,
    string EntryType,
    ulong RecordNumber,
    string ParentReference,
    ulong SizeBytes,
    string Attributes,
    ushort SequenceNumber,
    byte Namespace,
    ulong AllocatedSize,
    bool IsDeleted);

public sealed record CandidateComparisonRow(
    string FileSystem,
    string Status,
    string Offset,
    string Size,
    int Confidence,
    string BackupEvidence,
    string PartitionRelation,
    string Evidence)
{
    public string Advisory => Status switch
    {
        "Existing partition" =>
            "The candidate aligns with a known partition. Retain the existing partition-table interpretation; this scan provides corroboration, not a reason to rewrite metadata.",
        "Overlaps known partition" =>
            "Do not classify this as lost or act on it independently. Compare the overlapping partition's declared bounds and filesystem metadata; overlapping evidence may indicate a false start, nested structure, or damaged geometry.",
        _ when BackupEvidence == "Validated" && Confidence >= 95 =>
            "Strong filesystem and backup-region corroboration. Preserve the candidate for manual review and compare its exact bounds with independent partition-table evidence before any separate recovery plan.",
        _ =>
            "Primary filesystem geometry was validated, but independent backup corroboration is absent or incomplete. Treat the location and size as provisional; seek additional independent evidence before planning recovery."
    };
}

public sealed record PartitionRow(int Index, string Table, string Name, string StartLba, string Size, long ByteOffset, long ByteLength);
}
