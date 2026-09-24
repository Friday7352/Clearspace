using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Clearspace.Services;

namespace Clearspace;

public partial class IndexingView : UserControl, IDisposable
{
    private readonly Func<IndexingSummary> _capture;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _filterTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _disposed, _refreshing, _binding;
    private CancellationTokenSource? _folderStop;
    private VolumeIndex? _source;
    private int _folder, _parent = -1;
    private int _offset;
    public event EventHandler? CloseRequested;
    public event EventHandler? WindowsIndexingRequested;

    public IndexingView() : this(IndexingOverview.Capture) { }
    internal IndexingView(Func<IndexingSummary> capture)
    {
        _capture = capture;
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshAsync();
        _filterTimer.Tick += async (_, _) => { _filterTimer.Stop(); await LoadFolderAsync(_folder); };
        Loaded += async (_, _) => { if (!_disposed) { _timer.Start(); await RefreshAsync(); } };
        Unloaded += (_, _) => { _timer.Stop(); _filterTimer.Stop(); _folderStop?.Cancel(); };
    }

    internal async Task RefreshAsync()
    {
        if (_disposed || _refreshing) return;
        _refreshing = true;
        try
        {
            var summary = await Task.Run(_capture);
            if (_disposed) return;
            EntryCount.Text = $"{summary.IndexedItems:N0}";
            DiskSpace.Text = summary.DiskBytes is { } bytes ? DiskUsageSnapshot.FormatBytes(bytes) : "Not saved";
            SavedWhen.Text = summary.SavedUtc is { } saved ? $"Saved {saved.ToLocalTime():g}" : "No index file yet";
            MemorySpace.Text = DiskUsageSnapshot.FormatBytes(summary.MemoryBytes);
            ActivityTitle.Text = summary.ActivityTitle;
            ActivityDetail.Text = summary.ActivityDetail;
            CurrentFolder.Text = summary.ActiveFolder;
            CurrentFolder.ToolTip = summary.ActiveFolder;
            CurrentFolder.Visibility = summary.ActiveRoot is null ? Visibility.Collapsed : Visibility.Visible;
            ScanProgress.Visibility = summary.ActiveRoot is null ? Visibility.Collapsed : Visibility.Visible;
            StoragePath.Text = summary.IndexPath;
            StorageMessage.Text = summary.StorageError is { } error ? $"Could not read the saved file: {error}"
                : "One local file holds all indexed drives. It is saved periodically and on exit. Disk space and RAM are different measurements.";
            NetworkScope.Text = summary.NetworkEnabled ? "Mapped network drives are included when reachable. Removable and optical drives are not included."
                : "Network indexing is off. Network, removable and optical drives are not included.";
            var selected = (DriveList.SelectedItem as IndexedDrive)?.Root;
            _binding = true;
            DriveList.ItemsSource = summary.Drives;
            DriveList.SelectedItem = summary.Drives.FirstOrDefault(d => d.Root.Equals(selected, StringComparison.OrdinalIgnoreCase))
                ?? summary.Drives.FirstOrDefault(d => d.Source is not null) ?? summary.Drives.FirstOrDefault();
            _binding = false;
            await SelectDriveAsync();
        }
        catch (Exception exception) { if (!_disposed) Feedback.Text = $"Could not refresh indexing details: {exception.Message}"; }
        finally { _binding = false; _refreshing = false; }
    }

    private async Task SelectDriveAsync()
    {
        if (DriveList.SelectedItem is not IndexedDrive drive) return;
        DriveTitle.Text = $"{drive.Root}  ·  {drive.State}";
        DriveDetail.Text = drive.Detail;
        DriveProgress.Value = drive.ProgressPercent;
        DriveProgress.Visibility = drive.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        ScanNowButton.IsEnabled = drive.CanScan;
        ScanNowButton.Content = drive.IsScanning ? "Scanning…" : "Scan now";
        DriveProgressText.Text = drive.ProgressText;
        DriveProgressDetail.Text = drive.IsScanning || drive.IsScanComplete ? drive.ProgressDetail : "";
        CoverageText.Text = drive.CoverageText;
        ScanWhen.Text = drive.ScanText;
        SkippedList.ItemsSource = drive.Scan?.Examples ?? [];
        if (ReferenceEquals(_source, drive.Source) && FolderPath.Text != "Indexed entries") return;
        _source = drive.Source;
        _binding = true; Filter.Clear(); _binding = false;
        await LoadFolderAsync(0);
    }

    private async Task LoadFolderAsync(int folder, bool keepPage = false)
    {
        if (_disposed) return;
        if (!keepPage) _offset = 0;
        var offset = _offset;
        _folderStop?.Cancel();
        var stop = _folderStop = new CancellationTokenSource();
        var source = _source;
        var filter = Filter.Text.Trim();
        _folder = folder;
        EntryList.ItemsSource = null;
        EntryList.IsEnabled = false;
        NextPage.IsEnabled = PreviousPage.IsEnabled = false;
        if (source is null)
        {
            EntryList.ItemsSource = null; FolderPath.Text = "No indexed entries";
            ListStatus.Text = "This drive does not have an index to browse."; UpButton.IsEnabled = false;
            stop.Dispose(); if (ReferenceEquals(_folderStop, stop)) _folderStop = null;
            return;
        }
        ListStatus.Text = "Reading indexed entries…";
        try
        {
            var page = await Task.Run(() => IndexingOverview.ReadFolder(source, folder, filter, stop.Token, offset: offset), stop.Token);
            if (_disposed || stop.IsCancellationRequested || !ReferenceEquals(_folderStop, stop)) return;
            EntryList.ItemsSource = page.Entries;
            FolderPath.Text = page.Path; FolderPath.ToolTip = page.Path;
            _parent = page.Parent; UpButton.IsEnabled = _parent >= 0;
            PreviousPage.IsEnabled = offset > 0;
            NextPage.IsEnabled = offset + page.Entries.Count < page.MatchingCount;
            ListStatus.Text = page.MatchingCount == 0 ? "No indexed entries match this folder or filter."
                : page.MatchingCount > page.Entries.Count ? $"{offset + 1:N0}–{offset + page.Entries.Count:N0} of {page.MatchingCount:N0} entries"
                : $"{page.MatchingCount:N0} indexed entries · double-click a folder to browse";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_disposed && !stop.IsCancellationRequested) ListStatus.Text = $"Could not read this index: {exception.Message}"; }
        finally
        {
            if (ReferenceEquals(_folderStop, stop)) { _folderStop = null; EntryList.IsEnabled = true; }
            stop.Dispose();
        }
    }

    private async void OnDriveSelected(object sender, SelectionChangedEventArgs e) { if (!_binding) await SelectDriveAsync(); }
    private async void OnScanNow(object sender, RoutedEventArgs e)
    {
        if (DriveList.SelectedItem is not IndexedDrive { CanScan: true } drive) return;
        FileIndexService.ScanNow(drive.Root);
        Feedback.Text = $"Scan requested for {drive.Root}. It will run in the background after any current work finishes.";
        await RefreshAsync();
    }
    private void OnFilterChanged(object sender, TextChangedEventArgs e) { if (_binding || _disposed) return; _filterTimer.Stop(); _filterTimer.Start(); }
    private async void OnRefresh(object sender, RoutedEventArgs e) { await RefreshAsync(); if (!_disposed) await LoadFolderAsync(_folder); }
    private async void OnReloadFolder(object sender, RoutedEventArgs e) => await LoadFolderAsync(_folder);
    private async void OnNextPage(object sender, RoutedEventArgs e) { _offset += 1000; await LoadFolderAsync(_folder, true); }
    private async void OnPreviousPage(object sender, RoutedEventArgs e) { _offset = Math.Max(0, _offset - 1000); await LoadFolderAsync(_folder, true); }
    private async void OnUp(object sender, RoutedEventArgs e) { if (_parent >= 0) { _binding = true; Filter.Clear(); _binding = false; await LoadFolderAsync(_parent); } }
    private async void OnOpenEntry(object sender, MouseButtonEventArgs e) => await OpenSelectedAsync();
    private async void OnEntryKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await OpenSelectedAsync(); } }
    private async Task OpenSelectedAsync()
    {
        if (EntryList.SelectedItem is not IndexedEntry { IsFolder: true } entry) return;
        _binding = true; Filter.Clear(); _binding = false; await LoadFolderAsync(entry.Id);
    }
    private void OnEntrySelected(object sender, SelectionChangedEventArgs e)
    {
        if (EntryList.SelectedItem is IndexedEntry entry) { ListStatus.Text = entry.Path; ListStatus.ToolTip = entry.Path; }
    }
    private void OnBack(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void OnWindowsIndexing(object sender, RoutedEventArgs e) => WindowsIndexingRequested?.Invoke(this, EventArgs.Empty);
    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(StoragePath.Text); Feedback.Text = "Index file path copied."; }
        catch (Exception) { Feedback.Text = "The clipboard is busy. You can select and copy the path above."; }
    }
    private void OnOpenStorage(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.GetDirectoryName(StoragePath.Text)!;
            if (!Directory.Exists(folder)) { Feedback.Text = "The index folder will be created when the first index is saved."; return; }
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception exception) { Feedback.Text = $"Could not open the folder: {exception.Message}"; }
    }
    public void Dispose() { _disposed = true; _timer.Stop(); _filterTimer.Stop(); _folderStop?.Cancel(); }
}
