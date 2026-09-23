using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

public partial class DiskUsageView : UserControl, IDisposable
{
    private readonly DiskUsageViewModel _viewModel;
    private readonly string? _preferredPath;
    private readonly Func<DiskUsageDeleteRequest, bool> _confirmDelete;
    public event EventHandler? FileOperationCompleted;
    public event EventHandler? CloseRequested;
    private bool _started;
    private bool _disposed;
    private System.Runtime.GCLatencyMode _previousLatency;
    private IntPtr OwnerHandle => Window.GetWindow(this) is { } owner ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;

    public DiskUsageView(string? preferredPath = null) : this(new DiskUsageViewModel(), preferredPath) { }

    internal DiskUsageView(DiskUsageViewModel viewModel, string? preferredPath = null,
        Func<DiskUsageDeleteRequest, bool>? confirmDelete = null)
    {
        _viewModel = viewModel;
        _preferredPath = preferredPath;
        _confirmDelete = confirmDelete ?? (request => MessageBox.Show(Window.GetWindow(this), request.ConfirmationMessage,
            "Delete permanently", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        InitializeComponent();
        DataContext = _viewModel;
        ApplyPerformanceOptions(); // NEW (round 18): saved GPU/detail choices, before the first frame
        // NEW (round 31): the map animates against a heap dominated by the file index - a couple of
        // gigabytes of live objects - and one blocking gen2 collection there is a quarter-second stall
        // however cheap the frame was. Sustained low latency keeps the collector off blocking gen2
        // while this view is open; it is restored on close so the rest of the app is unaffected.
        _previousLatency = System.Runtime.GCSettings.LatencyMode;
        try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; }
        catch (InvalidOperationException) { /* not available in every hosting mode */ }
        _viewModel.PropertyChanged += OnViewChanged;
        // CHANGED: the map now reports folder focus from zooming/clicking and file selection;
        // ItemInvoked and ParentRequested are gone because the map is one continuous space.
        Treemap.FolderFocused += OnMapFolderFocused;
        Treemap.ItemSelected += OnMapItemSelected;
        Treemap.DeleteRequested += OnTileDelete;
        Treemap.ZoomChanged += OnZoomChanged;
        _viewModel.FileOperationCompleted += OnFileOperationCompleted;
        PreviewMouseDown += OnNavigationMouseDown;
        PreviewKeyDown += OnNavigationKeyDown;
        // NEW: follow the indexer, so the view fills in by itself once indexing finishes
        // (e.g. after an index rebuild) instead of staying on "no file index".
        FileIndexService.Changed += OnIndexChanged;
        // NEW: sizes follow live changes to the index (debounced; see OnLiveChanges).
        FileIndexService.LiveChanged += OnLiveChanges;
        Loaded += async (_, _) =>
        {
            if (_started) return;
            _started = true;
            Treemap.Focus(); // NEW: keyboard shortcuts (F3, +/-, Esc) reach the map right away
            await _viewModel.LoadAsync(preferredPath: _preferredPath);
            if (_viewModel.Snapshot is null) OnIndexChanged(null, EventArgs.Empty); // NEW: show progress now
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FileIndexService.Changed -= OnIndexChanged;
        FileIndexService.LiveChanged -= OnLiveChanges;
        _liveTimer?.Stop();
        _viewModel.PropertyChanged -= OnViewChanged;
        _viewModel.FileOperationCompleted -= OnFileOperationCompleted;
        _viewModel.Dispose();
        Treemap.Dispose();
        try { System.Runtime.GCSettings.LatencyMode = _previousLatency; } catch (InvalidOperationException) { }
    }

    // NEW (round 18): map performance options. The map keeps working with Direct3D turned off - it
    // falls back to its own multi-threaded rasterizer - and low detail mode is the setting that makes
    // the biggest difference on integrated graphics, because traversal then costs one level.
    private bool _applyingOptions;

    private void ApplyPerformanceOptions()
    {
        _applyingOptions = true;
        GpuOption.IsChecked = SettingsService.GetDiskMapGpu();
        LowDetailOption.IsChecked = SettingsService.GetDiskMapLowDetail();
        ConserveOption.IsChecked = SettingsService.GetDiskMapConserveMemory();
        EverythingOption.IsChecked = SettingsService.GetDiskMapRenderEverything();
        BackgroundOption.IsChecked = SettingsService.GetDiskMapBackgroundBuilding();
        _applyingOptions = false;
        Treemap.GpuEnabled = GpuOption.IsChecked == true;
        Treemap.LowDetail = LowDetailOption.IsChecked == true;
        Treemap.ConserveMemory = ConserveOption.IsChecked == true;
        Treemap.RenderEverything = EverythingOption.IsChecked == true;
        Treemap.BackgroundBuilding = BackgroundOption.IsChecked == true;
    }

    private void OnToggleSettings(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = !SettingsPopup.IsOpen;

    // Which renderer is actually in use is only known once a frame has tried to create it.
    private void OnSettingsOpened(object? sender, EventArgs e) => RendererText.Text = Treemap.RendererLabel;

    private void OnPerformanceOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingOptions || _disposed) return;
        var gpu = GpuOption.IsChecked == true;
        var low = LowDetailOption.IsChecked == true;
        var lean = ConserveOption.IsChecked == true;
        var everything = EverythingOption.IsChecked == true;
        var background = BackgroundOption.IsChecked == true;
        SettingsService.SetDiskMapGpu(gpu);
        SettingsService.SetDiskMapLowDetail(low);
        SettingsService.SetDiskMapConserveMemory(lean);
        SettingsService.SetDiskMapRenderEverything(everything);
        SettingsService.SetDiskMapBackgroundBuilding(background);
        Treemap.GpuEnabled = gpu;
        Treemap.LowDetail = low;
        Treemap.ConserveMemory = lean;
        Treemap.RenderEverything = everything;
        Treemap.BackgroundBuilding = background;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => { if (!_disposed) RendererText.Text = Treemap.RendererLabel; }));
    }

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    // NEW: F3 performance readout, also reachable from the main window's key handling.
    internal void ToggleMapStats() => Treemap.ToggleStats();

    // NEW: live index changes. Only changes that are visible from the current folder (at most two
    // levels below it) refresh the view, 1.5 s after things go quiet, and never mid-gesture or
    // while list items are selected - deep churn such as browser caches doesn't keep redrawing.
    private System.Windows.Threading.DispatcherTimer? _liveTimer;
    private void OnLiveChanges(IReadOnlyList<string> paths)
    {
        var folder = _viewModel.CurrentPath;
        if (string.IsNullOrEmpty(folder) || !paths.Any(path => IsVisibleFrom(folder, path))) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => ScheduleLiveRefresh(debounce: true)));
    }

    // debounce: restart the wait on every change (live edits). Otherwise only start it if idle,
    // so a steady stream of indexing progress still refreshes every couple of seconds.
    private void ScheduleLiveRefresh(bool debounce)
    {
        if (_disposed) return;
        if (_liveTimer is null)
        {
            _liveTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
                { Interval = TimeSpan.FromSeconds(1.5) };
            _liveTimer.Tick += OnLiveTimer;
        }
        _liveTimer.Interval = TimeSpan.FromSeconds(FileIndexService.IsBuilding ? 2.5 : 1.5);
        if (debounce) _liveTimer.Stop();
        if (!_liveTimer.IsEnabled) _liveTimer.Start();
    }

    private static bool IsVisibleFrom(string folder, string path)
    {
        var root = folder.TrimEnd('\\');
        if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        return path.AsSpan(root.Length + 1).Count('\\') <= 2;
    }

    // CHANGED (round 10): refreshes rebuild a whole-drive snapshot, so they are rare - at most every
    // 10 s while a drive is being indexed and every 4 s otherwise - and only after the user has
    // left the map alone for 2 s.
    private DateTime _lastLiveRefresh = DateTime.MinValue;
    private async void OnLiveTimer(object? sender, EventArgs e)
    {
        _liveTimer?.Stop();
        if (_disposed) return;
        var minimum = TimeSpan.FromSeconds(FileIndexService.IsBuilding ? 10 : 4);
        if (_viewModel.IsBusy || Treemap.IsAnimating || Treemap.SecondsSinceInteraction < 2 ||
            ItemList.SelectedItems.Count > 0 || Mouse.LeftButton == MouseButtonState.Pressed ||
            DateTime.UtcNow - _lastLiveRefresh < minimum)
        {
            _liveTimer?.Start(); // try again shortly
            return;
        }
        _lastLiveRefresh = DateTime.UtcNow;
        await _viewModel.RefreshLiveAsync();
    }

    // NEW: the indexer reports progress from a background thread, often; coalesce onto the UI.
    private int _indexUpdateQueued;
    private void OnIndexChanged(object? sender, EventArgs e)
    {
        if (_disposed || Interlocked.Exchange(ref _indexUpdateQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(async () =>
        {
            Interlocked.Exchange(ref _indexUpdateQueued, 0);
            if (_disposed || !_started || _viewModel.IsBusy) return;
            if (_viewModel.Snapshot is not null)
            {
                _viewModel.RefreshRoots();
                // NEW: while a drive is being indexed (or a refreshed index lands), keep the map
                // filling in - throttled, not debounced, since progress events never stop.
                if (_viewModel.HasNewerIndex()) ScheduleLiveRefresh(debounce: false);
                return;
            }
            // NEW: a drive chosen in the picker is waiting for its index.
            if (_viewModel.WaitingRoot is { } waiting)
            {
                if (_viewModel.HasIndexFor(waiting))
                {
                    Treemap.SetMessage(null);
                    await _viewModel.LoadAsync(root: waiting,
                        preferredPath: _preferredPath?.StartsWith(waiting, StringComparison.OrdinalIgnoreCase) == true ? _preferredPath : null);
                    return;
                }
                var waitingProgress = FileIndexService.Status;
                if (!string.IsNullOrWhiteSpace(waitingProgress))
                    Treemap.SetMessage($"{waiting} hasn't been indexed yet · {waitingProgress}");
                return;
            }
            // Wait for the drive that was asked for, unless indexing has finished without it.
            if (_viewModel.HasIndexFor(_preferredPath) || (!FileIndexService.IsBuilding && _viewModel.HasIndexFor(null)))
            {
                Treemap.SetMessage(null);
                await _viewModel.LoadAsync(preferredPath: _preferredPath);
                return;
            }
            var progress = FileIndexService.Status;
            _viewModel.ShowIndexProgress(progress);
            Treemap.SetMessage(string.IsNullOrWhiteSpace(progress) ? "Waiting for the file index…" : progress);
        }));
    }

    private void OnFileOperationCompleted(object? sender, EventArgs e) => FileOperationCompleted?.Invoke(this, e);
    private async void OnTileDelete(DiskUsageItem item)
    {
        if (_viewModel.IsBusy) return;
        // CHANGED: match by id; the map's items are snapshot records, not the list's instances.
        var match = _viewModel.Items.FirstOrDefault(candidate => candidate.Id == item.Id);
        if (match is null) return;
        ItemList.SelectedItems.Clear();
        ItemList.SelectedItem = match;
        await _viewModel.DeletePermanentlyAsync(OwnerHandle, _confirmDelete);
    }
    private async void OnDeletePermanently(object sender, RoutedEventArgs e)
        => await _viewModel.DeletePermanentlyAsync(OwnerHandle, _confirmDelete);
    private void OnListRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ItemList, e.OriginalSource as DependencyObject) is ListViewItem row)
        {
            if (!row.IsSelected) { ItemList.SelectedItems.Clear(); row.IsSelected = true; }
            row.Focus();
        }
        else { ItemList.SelectedItems.Clear(); }
    }

    // CHANGED: a new snapshot rebuilds the map; any other navigation just moves its camera.
    private void OnViewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiskUsageViewModel.SnapshotVersion) && _viewModel.Snapshot is { } snapshot)
        {
            // CHANGED: a live refresh resizes the map in place; anything else rebuilds it.
            if (_viewModel.IsLiveRefresh)
                Treemap.RefreshSource(snapshot.Item(0), (id, token) => snapshot.Children(id, token), _viewModel.FolderPath, EmptyFolderName());
            else
                Treemap.SetSource(snapshot.Item(0), (id, token) => snapshot.Children(id, token), _viewModel.FolderPath,
                    EmptyFolderName(), snapshot.Item, snapshot);
        }
        else if (e.PropertyName == nameof(DiskUsageViewModel.MapItems))
            Treemap.ShowFolder(_viewModel.FolderPath, EmptyFolderName());
        // NEW: the chosen drive isn't indexed yet; clear the map and say so.
        if (e.PropertyName == nameof(DiskUsageViewModel.WaitingRoot) && _viewModel.WaitingRoot is not null)
            Treemap.ClearSource(_viewModel.Status);
        if (e.PropertyName is nameof(DiskUsageViewModel.CanGoBack) or nameof(DiskUsageViewModel.IsBusy)) UpdateZoomNavigation();
    }

    // NEW: an empty folder has no area on the map; the map says so over its parent.
    private string? EmptyFolderName() => _viewModel.MapItems.Count == 0 ? _viewModel.FolderName : null;

    private void UpdateZoomNavigation() => BackButton.SetCurrentValue(IsEnabledProperty,
        !_viewModel.IsBusy && (Treemap.IsZoomed || _viewModel.CanGoBack));

    private void OnZoomChanged()
    {
        UpdateZoomNavigation();
        ZoomOutButton.Visibility = Treemap.IsZoomed ? Visibility.Visible : Visibility.Collapsed;
    }

    // NEW: zooming or clicking into a folder on the map updates the list quietly.
    // The view model cancels superseded requests and waits both before preparing
    // and before publishing a listing. There is no timeout that forces a rebind
    // in the middle of a long zoom gesture.
    private async void OnMapFolderFocused(int folder)
    {
        if (_disposed) return;
        await _viewModel.FocusFromMapAsync(folder, () => !_disposed &&
            (Treemap.IsAnimating || Treemap.SecondsSinceInteraction < .18 || Mouse.LeftButton == MouseButtonState.Pressed));
    }

    // NEW: a file clicked on the map is selected in the list.
    private void OnMapItemSelected(DiskUsageItem item)
    {
        var match = _viewModel.Items.FirstOrDefault(candidate => candidate.Id == item.Id);
        if (match is null) return;
        ItemList.SelectedItems.Clear();
        ItemList.SelectedItem = match;
        ItemList.ScrollIntoView(match);
    }

    private async void OnNavigationMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2)) return;
        e.Handled = true;
        if (e.ChangedButton == MouseButton.XButton1 && Treemap.IsZoomed) Treemap.ZoomOut();
        else if (e.ChangedButton == MouseButton.XButton1) await _viewModel.BackAsync();
        else await _viewModel.ForwardAsync();
    }

    private async void OnNavigationKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F3) { e.Handled = true; Treemap.ToggleStats(); return; } // NEW: performance readout
        if (key == Key.Escape && Treemap.IsZoomed) { e.Handled = true; Treemap.ZoomOut(); return; }
        // NEW: Escape at the whole-folder view steps out to the parent, like zooming out.
        if (key == Key.Escape && _viewModel.CanGoUp) { e.Handled = true; await _viewModel.UpAsync(); return; }
        if (key == Key.Delete && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            e.Handled = true;
            await _viewModel.DeletePermanentlyAsync(OwnerHandle, _confirmDelete);
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Alt || key is not (Key.Left or Key.Right or Key.Up)) return;
        e.Handled = true;
        if (key == Key.Left && Treemap.IsZoomed) Treemap.ZoomOut();
        else if (key == Key.Left) await _viewModel.BackAsync();
        else if (key == Key.Right) await _viewModel.ForwardAsync();
        else await _viewModel.UpAsync();
    }

    private async void OnBack(object sender, RoutedEventArgs e)
    {
        if (Treemap.IsZoomed) Treemap.ZoomOut();
        else await _viewModel.BackAsync();
    }
    private async void OnForward(object sender, RoutedEventArgs e) => await _viewModel.ForwardAsync();
    private async void OnBreadcrumb(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DiskUsageCrumb crumb }) await _viewModel.NavigateAsync(crumb.Id);
    }
    private async void OnDriveChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_viewModel.IsBusy && Drives.SelectedItem is string root && root != _viewModel.SelectedRoot)
            await _viewModel.LoadAsync(root);
    }
    private async void OnReload(object sender, RoutedEventArgs e)
    {
        // NEW (round 19): Refresh is one of the two moments the warm snapshot is thrown away, so
        // this is the button that actually recomputes folder sizes from the index.
        if (_viewModel.SelectedRoot is { Length: > 0 } root) DiskUsageSnapshotCache.Invalidate(root);
        var path = _viewModel.CurrentPath;
        await _viewModel.LoadAsync(_viewModel.SelectedRoot, string.IsNullOrEmpty(path) ? _preferredPath : path);
    }
    private void OnCancel(object sender, RoutedEventArgs e) => _viewModel.Cancel();
    private async void OnUp(object sender, RoutedEventArgs e) => await _viewModel.UpAsync();
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = ItemList.SelectedItem as DiskUsageItem;
        _viewModel.SetSelection(ItemList.SelectedItems.Cast<DiskUsageItem>());
        Treemap.Highlight(item?.Id);
    }
    private async void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ItemList, e.OriginalSource as DependencyObject) is ListViewItem { Content: DiskUsageItem item } && item.IsFolder)
            await _viewModel.NavigateAsync(item.Id);
    }
    private async void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ItemList.SelectedItem is DiskUsageItem { IsFolder: true } item)
        { e.Handled = true; await _viewModel.NavigateAsync(item.Id); }
        else if (e.Key == Key.Back) { e.Handled = true; await _viewModel.UpAsync(); }
    }
}
