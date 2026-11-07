using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskRescue.IO;
using Microsoft.Win32;

namespace DiskRescue.Desktop;

public partial class PhysicalAcquisitionWindow : Window
{
    private readonly ObservableCollection<DiskChoice> _disks = new();
    private PhysicalDiskDiscoveryResult? _inventory;
    private PhysicalVolumeMappingResult? _volumeMap;
    private WindowsBootDeviceMappingResult? _bootMap;
    private PhysicalDiskAcquisitionAssessment? _sourceAssessment;
    private PhysicalImageDestinationAssessment? _destinationAssessment;
    private CancellationTokenSource? _acquisitionCancellation;
    private bool _busy;

    public PhysicalAcquisitionWindow()
    {
        InitializeComponent();
        DiskCombo.ItemsSource = _disks;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) =>
        await RefreshInventoryAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshInventoryAsync();

    private async Task RefreshInventoryAsync()
    {
        if (_busy) return;
        RefreshButton.IsEnabled = false;
        DiskCombo.IsEnabled = false;
        AcquireButton.IsEnabled = false;
        PreflightBox.Text = "Enumerating physical disks and mapping volumes/boot partitions…";
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var result = await Task.Run(() =>
            {
                var inventory = WindowsPhysicalDiskDiscovery.Enumerate();
                var volumes = WindowsPhysicalDiskVolumeMap.Enumerate();
                var boot = WindowsBootDeviceMap.Enumerate(volumes);
                return (Inventory: inventory, Volumes: volumes, Boot: boot);
            });

            _inventory = result.Inventory;
            _volumeMap = result.Volumes;
            _bootMap = result.Boot;
            _disks.Clear();
            foreach (var disk in result.Inventory.Disks.OrderBy(d => d.DiskNumber))
                _disks.Add(new DiskChoice(disk));

            var notes = new List<string>
            {
                $"Physical disks detected: {_disks.Count}",
                $"Volume mapping: {(result.Volumes.IsComplete && result.Volumes.Issues.Count == 0 ? "COMPLETE" : "INCOMPLETE · acquisition blocked")}",
                $"Boot mapping: {(result.Boot.IsComplete && result.Boot.Issues.Count == 0 ? "COMPLETE · conservative" : "INCOMPLETE · acquisition blocked")}",
                $"Recognized boot-critical disks: {(result.Boot.IsComplete ? string.Join(", ", result.Boot.BootCriticalDiskNumbers.Order().Select(n => $"PhysicalDrive{n}")) : "UNKNOWN")}",
                ""
            };
            notes.AddRange(result.Inventory.Issues.Select(x => "DISK: " + x));
            notes.AddRange(result.Volumes.Issues.Select(x => "VOLUME: " + x));
            notes.AddRange(result.Boot.Issues.Select(x => "BOOT: " + x));
            notes.Add("");
            notes.Add("A reported serial/model is controller metadata, not a cryptographic identity. Boot mapping is conservative but cannot attest firmware boot order.");
            PreflightBox.Text = string.Join(Environment.NewLine, notes);
            if (_disks.Count > 0)
                DiskCombo.SelectedIndex = 0;
            else
                PreflightBox.Text += Environment.NewLine + "No physical disks were reported.";
            ProgressText.Text = "Inventory refreshed · no source sectors read";
            RefreshPreflight();
        }
        catch (Exception ex)
        {
            _inventory = null;
            _volumeMap = null;
            _bootMap = null;
            _sourceAssessment = null;
            _destinationAssessment = null;
            PreflightBox.Text = "Physical disk inventory failed safely. No source sectors were read.\n\n" + ex.Message;
            DestinationStatusText.Text = "Inventory unavailable; acquisition disabled.";
            ProgressText.Text = "Inventory failed";
            UpdateAcquireState();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            DiskCombo.IsEnabled = true;
            Mouse.OverrideCursor = null;
        }
    }

    private void DiskCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshPreflight();

    private void DestinationBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshPreflight();

    private void ConfirmationBox_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateAcquireState();

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Choose a new disk-image destination on separate physical storage",
            Filter = "Raw disk image (*.img)|*.img|DD image (*.dd)|*.dd|All files (*.*)|*.*",
            DefaultExt = ".img",
            AddExtension = true,
            OverwritePrompt = false,
            CheckPathExists = true,
            FileName = "physical-disk.img"
        };
        if (dialog.ShowDialog(this) == true)
            DestinationBox.Text = dialog.FileName;
    }

    private void RefreshPreflight()
    {
        if (_inventory is null || _volumeMap is null || _bootMap is null ||
            DiskCombo.SelectedItem is not DiskChoice choice)
        {
            _sourceAssessment = null;
            _destinationAssessment = null;
            UpdateAcquireState();
            return;
        }

        _sourceAssessment = PhysicalDiskImageAcquisition.Assess(
            choice.Descriptor, _inventory, _volumeMap, _bootMap);
        _destinationAssessment = null;

        var notes = new List<string>
        {
            $"SOURCE: PhysicalDrive{choice.Descriptor.DiskNumber}",
            $"Model: {choice.Descriptor.DisplayIdentity}",
            $"Capacity: {choice.Descriptor.LengthBytes:N0} bytes",
            $"Logical sector size: {choice.Descriptor.BytesPerSector:N0} bytes",
            $"Serial: {choice.Descriptor.SerialNumber ?? "Not reported"}",
            $"Bus type: {choice.Descriptor.BusType?.ToString() ?? "Unknown"} · Removable: {choice.Descriptor.RemovableMedia?.ToString() ?? "Unknown"}",
            $"Source preflight: {(_sourceAssessment.CanAcquire ? "PASSED" : "BLOCKED")}",
            ""
        };
        notes.AddRange(_sourceAssessment.Blockers.Select(x => "BLOCKER: " + x));
        notes.AddRange(_sourceAssessment.Warnings.Select(x => "WARNING: " + x));

        var destinationPath = DestinationBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(destinationPath))
        {
            try
            {
                var fullPath = Path.GetFullPath(destinationPath);
                var directory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    DestinationStatusText.Text = "BLOCKED · Destination directory must already exist.";
                }
                else if (File.Exists(fullPath) || Directory.Exists(fullPath) ||
                         File.Exists(fullPath + ".diskrescue.json") || Directory.Exists(fullPath + ".diskrescue.json"))
                {
                    DestinationStatusText.Text = "BLOCKED · Image or acquisition manifest already exists; nothing will be overwritten.";
                }
                else
                {
                    _destinationAssessment = PhysicalImageDestinationSafety.Assess(
                        choice.Descriptor.DiskNumber, fullPath, _volumeMap);
                    DestinationStatusText.Text = _destinationAssessment.CanUseAsSeparateDestination
                        ? $"SEPARATE DESTINATION VERIFIED · {_destinationAssessment.VolumeGuidPath} · Physical disk(s): {string.Join(", ", _destinationAssessment.DestinationDiskNumbers)}"
                        : "BLOCKED · " + string.Join(" ", _destinationAssessment.Blockers);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                DestinationStatusText.Text = "BLOCKED · " + ex.Message;
            }
        }
        else
        {
            DestinationStatusText.Text = "Choose a new image path on separate physical storage.";
        }

        if (_destinationAssessment is not null)
        {
            notes.Add("");
            notes.Add($"DESTINATION: {(_destinationAssessment.CanUseAsSeparateDestination ? "VERIFIED SEPARATE PHYSICAL STORAGE" : "BLOCKED")}");
            notes.AddRange(_destinationAssessment.Blockers.Select(x => "DESTINATION BLOCKER: " + x));
        }
        PreflightBox.Text = string.Join(Environment.NewLine, notes);
        ConfirmationHint.Text = $"To authorize this one-time copy, type exactly: ACQUIRE PHYSICALDRIVE{choice.Descriptor.DiskNumber}";
        UpdateAcquireState();
    }

    private void UpdateAcquireState()
    {
        var phrase = DiskCombo.SelectedItem is DiskChoice choice
            ? $"ACQUIRE PHYSICALDRIVE{choice.Descriptor.DiskNumber}"
            : string.Empty;
        AcquireButton.IsEnabled = !_busy &&
            _sourceAssessment?.CanAcquire == true &&
            _destinationAssessment?.CanUseAsSeparateDestination == true &&
            string.Equals(ConfirmationBox.Text, phrase, StringComparison.Ordinal);
        DiskCombo.IsEnabled = !_busy;
        RefreshButton.IsEnabled = !_busy;
    }

    private async void Acquire_Click(object sender, RoutedEventArgs e)
    {
        if (DiskCombo.SelectedItem is not DiskChoice choice ||
            _sourceAssessment?.CanAcquire != true ||
            _destinationAssessment?.CanUseAsSeparateDestination != true)
            return;

        var destination = DestinationBox.Text.Trim();
        var expected = $"ACQUIRE PHYSICALDRIVE{choice.Descriptor.DiskNumber}";
        if (!string.Equals(ConfirmationBox.Text, expected, StringComparison.Ordinal))
            return;

        var finalConfirmation = MessageBox.Show(
            this,
            $"You are about to read every sector from:\n\nPhysicalDrive{choice.Descriptor.DiskNumber}\n{choice.Descriptor.DisplayIdentity}\nSerial: {choice.Descriptor.SerialNumber}\nCapacity: {choice.Descriptor.LengthBytes:N0} bytes\n\nThe image will be written to:\n{Path.GetFullPath(destination)}\n\nThe source is opened read-only, but Windows may still write through mounted volumes. This is not hardware write-blocked. Continue only if you understand this risk.",
            "Confirm physical disk acquisition",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (finalConfirmation != MessageBoxResult.Yes)
            return;

        _busy = true;
        _acquisitionCancellation = new CancellationTokenSource();
        AcquireButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        RefreshButton.IsEnabled = false;
        DiskCombo.IsEnabled = false;
        DestinationBox.IsEnabled = false;
        ConfirmationBox.IsEnabled = false;
        BrowseDestinationButtonEnabled(false);
        AcquireProgress.Value = 0;
        ResultText.Text = string.Empty;
        ProgressText.Text = "Opening source read-only after fresh safety preflight…";
        Mouse.OverrideCursor = Cursors.Wait;

        var progress = new Progress<DiskImageProgress>(value =>
        {
            AcquireProgress.Value = value.Percent;
            ProgressText.Text = $"Acquiring and verifying image · {value.Percent:F1}% · {value.BytesCopied:N0}/{value.TotalBytes:N0} bytes";
        });

        try
        {
            var result = await Task.Run(async () =>
                await PhysicalDiskImageAcquisition.AcquireAsync(
                    choice.Descriptor, destination, progress, _acquisitionCancellation.Token));
            AcquireProgress.Value = 100;
            ProgressText.Text = "Acquisition completed · image read-back SHA-256 verified";
            ResultText.Text = $"IMAGE: {result.Image.ImagePath}\nBYTES: {result.Image.BytesCopied:N0}\nSHA-256: {result.Image.Sha256}\nMANIFEST: {result.ManifestPath}";
            MessageBox.Show(this, "The image was copied and its exact length and SHA-256 were verified before publication. Review the manifest and preserve both files.", "Acquisition complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Acquisition cancelled · incomplete temporary image removed when possible";
            ResultText.Text = "No recovery or repair was performed. If a partial file remains after an I/O or cleanup failure, do not treat it as a valid image.";
        }
        catch (Exception ex)
        {
            ProgressText.Text = "Acquisition stopped safely";
            ResultText.Text = ex.Message + "\n\nRead errors abort the copy; unreadable sectors are never silently replaced with zeros.";
            MessageBox.Show(this, ex.Message, "Physical acquisition stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _acquisitionCancellation.Dispose();
            _acquisitionCancellation = null;
            _busy = false;
            CancelButton.IsEnabled = false;
            DestinationBox.IsEnabled = true;
            ConfirmationBox.IsEnabled = true;
            BrowseDestinationButtonEnabled(true);
            Mouse.OverrideCursor = null;
            RefreshPreflight();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_acquisitionCancellation is null) return;
        CancelButton.IsEnabled = false;
        ProgressText.Text = "Cancellation requested · waiting for the current bounded read to stop…";
        _acquisitionCancellation.Cancel();
    }

    private void BrowseDestinationButtonEnabled(bool enabled)
    {
        foreach (var child in FindVisualChildren<Button>(this))
            if (child.Content?.ToString() == "Browse…")
                child.IsEnabled = enabled;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private sealed record DiskChoice(PhysicalDiskDescriptor Descriptor)
    {
        public override string ToString() =>
            $"PhysicalDrive{Descriptor.DiskNumber} · {Descriptor.DisplayIdentity} · {Descriptor.LengthBytes:N0} bytes · S/N {Descriptor.SerialNumber ?? "not reported"}";
    }
}