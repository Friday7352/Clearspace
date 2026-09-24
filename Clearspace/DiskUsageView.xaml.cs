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
    public event EventHandler? IndexingRequested;
    private void OnIndexing(object sender, RoutedEventArgs e) => IndexingRequested?.Invoke(this, EventArgs.Empty);
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
        Treemap.DriveRequested += OnMapDriveRequested;   // NEW (round 47)
        // NEW (round 46): the experimental views navigate the same way the map does.
        Blocks3D.FolderOpened += OnExperimentFolderOpened;
        Blocks3D.ItemSelected += OnExperimentItemSelected;
        Platter.FolderOpened += OnExperimentFolderOpened;
        Platter.ItemSelected += OnExperimentItemSelected;
        // NEW (round 64): the 3D city navigates the same way; stepping in or out of a tower changes what Back does.
        City3D.FolderOpened += OnExperimentFolderOpened;
        City3D.ItemSelected += OnExperimentItemSelected;
        City3D.InsideChanged += UpdateZoomNavigation;
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
            FocusActiveView(); // CHANGED (round 46): the map, or the experimental view that is on
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
        _previewWork?.Cancel();   // NEW (round 48)
        _liveTimer?.Stop();
        _viewModel.PropertyChanged -= OnViewChanged;
        _viewModel.FileOperationCompleted -= OnFileOperationCompleted;
        _viewModel.Dispose();
        Treemap.Dispose();
        try { System.Runtime.GCSettings.LatencyMode = _previousLatency; } catch (InvalidOperationException) { }
        // NEW (round 40): closing the analyzer is the moment to give memory back to Windows. Everything the
        // map held is now garbage, so one full collection once the UI is idle frees it, instead of the
        // process keeping its peak size until something else runs. Not compacting: that would copy the
        // file index's large arrays, which stay alive, and stall the window for no gain.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false)));
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
        // NEW (round 46)
        NetworkOption.IsChecked = SettingsService.GetIndexNetworkDrives();   // NEW (round 50)
        ExperimentsOption.IsChecked = SettingsService.GetDiskMapExperimentalViews();
        ViewPicker.SelectedIndex = Math.Clamp(SettingsService.GetDiskMapExperimentalView(), 0, 3);   // CHANGED (round 64): 3 is the 3D city
        HeightPicker.SelectedIndex = Math.Clamp(SettingsService.GetDiskMap3DHeight(), 0, 3);
        PlatterPicker.SelectedIndex = Math.Clamp(SettingsService.GetDiskMapPlatterStyle(), 0, 1);
        _applyingOptions = false;
        ApplyViewMode();
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

    // NEW (round 46): experimental views. They are extra controls over the map's area; the map is only
    // hidden while one shows, and it keeps following navigation, so turning them off changes nothing.
    private int _viewMode;   // 0 map, 1 3D blocks, 2 disk layout, 3 3D city (NEW round 64)

    private void OnExperimentalViewChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingOptions || _disposed || !IsInitialized) return;
        SettingsService.SetDiskMapExperimentalViews(ExperimentsOption.IsChecked == true, Math.Max(0, ViewPicker.SelectedIndex),
            Math.Max(0, HeightPicker.SelectedIndex), Math.Max(0, PlatterPicker.SelectedIndex));
        ApplyViewMode();
    }

    private void OnExperimentalViewPicked(object sender, SelectionChangedEventArgs e) => OnExperimentalViewChanged(sender, e);

    // NEW (round 50): network drives. Turning it on starts checking which mapped drives answer and indexes
    // them in the background; they join the drive list as they are found. Turning it off drops their
    // indexes straight away.
    private void OnNetworkOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingOptions || _disposed || !IsInitialized) return;
        var enabled = NetworkOption.IsChecked == true;
        SettingsService.SetIndexNetworkDrives(enabled);
        if (enabled) { NetworkDrives.Start(); NetworkDrives.ProbeSoon(); FileIndexService.IndexNewDrives(); }
        else { NetworkDrives.Stop(); FileIndexService.DropNetworkDrives(); }
        _viewModel.RefreshRoots();
    }

    private void ApplyViewMode()
    {
        var enabled = ExperimentsOption.IsChecked == true;
        var mode = enabled ? Math.Max(0, ViewPicker.SelectedIndex) : 0;
        var changed = mode != _viewMode;
        _viewMode = mode;
        ViewPickerPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        Treemap.Visibility = mode == 0 ? Visibility.Visible : Visibility.Hidden;
        Blocks3D.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
        Platter.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
        City3D.Visibility = mode == 3 ? Visibility.Visible : Visibility.Collapsed;   // NEW (round 64)
        HeightPicker.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
        PlatterPicker.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
        MapHint.Text = mode switch
        {
            1 => "Drag to orbit · right-drag or Shift-drag to pan · scroll to zoom · double-click a folder to open it",
            2 => "Hover to see what is stored where · click to open · cluster 0 is at the outer edge, the end of the drive at the hub",
            // NEW (round 64)
            3 => "Click a tower to step inside · ↑/↓ or click a floor to ride the elevator · double-click a room to go through its door · Esc steps out · drag to orbit, right-drag to pan, scroll to zoom",
            _ => "Scroll to zoom · drag to pan · click to open · hover for details · Esc for the whole folder",
        };
        UpdateExperimentalView();
        OnZoomChanged();   // the map's zoom buttons only apply while the map shows
        if (changed && IsLoaded) FocusActiveView();
    }

    private void FocusActiveView()
    {
        if (_viewMode == 1) Blocks3D.Focus();
        else if (_viewMode == 2) Platter.Focus();
        else if (_viewMode == 3) City3D.Focus();   // NEW (round 64)
        else Treemap.Focus();
    }

    // Zoomed inside the map, and the map is what is showing: Back, Esc and the mouse's back button zoom
    // out of it first. With an experimental view on they navigate straight away instead.
    private bool MapZoomed => _viewMode == 0 && Treemap.IsZoomed;

    // NEW (round 64): inside a tower of the 3D city, Back, Esc and the mouse's back button step out of it first,
    // the way they zoom out of the map.
    private bool CityInside => _viewMode == 3 && City3D.IsInside;

    // Shows the current folder in whichever experimental view is on; the views skip work that has not changed.
    private void UpdateExperimentalView()
    {
        if (_disposed || _viewMode == 0 || _viewModel.Snapshot is not { } snapshot) return;
        if (_viewMode == 1) Blocks3D.Show(snapshot, _viewModel.FolderId, (Controls.Height3DMode)Math.Max(0, HeightPicker.SelectedIndex));
        else if (_viewMode == 2) Platter.Show(snapshot, _viewModel.FolderId, (Controls.PlatterStyle)Math.Max(0, PlatterPicker.SelectedIndex));
        else City3D.Show(snapshot, _viewModel.FolderId);   // NEW (round 64)
    }

    private async void OnExperimentFolderOpened(int folder)
    {
        if (_disposed || _viewModel.IsBusy) return;
        await _viewModel.NavigateAsync(folder);
    }

    // A file picked in an experimental view is selected in the list, opening its folder first when it is
    // deeper than the one shown.
    private async void OnExperimentItemSelected(int id)
    {
        if (_disposed || _viewModel.Snapshot is not { } snapshot) return;
        if (!_viewModel.Items.Any(item => item.Id == id))
        {
            if (_viewModel.IsBusy) return;
            await _viewModel.NavigateAsync(snapshot.Parent(id));
        }
        if (_viewModel.Items.FirstOrDefault(item => item.Id == id) is { } match) OnMapItemSelected(match);
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
        // CHANGED (round 39): "render everything" shows deeper levels, so changes up to six levels below
        // the current folder refresh the map (two otherwise). Not every depth: at a drive's root that
        // would include browser and app caches, which change constantly and would keep re-laying out
        // the whole drive every few seconds.
        var depth = Treemap.RenderEverything ? 6 : 2;
        if (string.IsNullOrEmpty(folder) || !paths.Any(path => IsVisibleFrom(folder, path, depth))) return;
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

    private static bool IsVisibleFrom(string folder, string path, int depth)
    {
        var root = folder.TrimEnd('\\');
        if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        return path.AsSpan(root.Length + 1).Count('\\') <= depth;
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
            Mouse.RightButton == MouseButtonState.Pressed ||   // NEW (round 46): a 3D pan
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

    // NEW (round 44): the size and free space of the drive the map shows, for zooming out past the files to
    // see them against the whole drive. Read off the UI thread (a slow or sleeping drive can take a while
    // to answer), and only for a drive's root - a map of a folder has no drive around it to show.
    // CHANGED (round 47): also the other drives in the drive list, which appear beside this one when you
    // zoom out further still, and open with a click.
    private void UpdateDriveSpace(string root)
    {
        var others = _viewModel.Roots.Where(other => !string.Equals(other, root, StringComparison.OrdinalIgnoreCase))
            .Select(other => (Root: other, Indexed: _viewModel.HasIndexFor(other))).ToArray();
        Task.Run(() =>
        {
            (long Total, long Free)? space = null;
            DriveHardware? hardware = null;   // NEW (round 51)
            var drives = new List<Controls.DiskUsageTreemap.OtherDrive>();
            try
            {
                // CHANGED (round 50): a network drive that is out of reach keeps its map (from its last index)
                // but has no drive around it - asking the server for its size could hang.
                if (string.Equals(System.IO.Path.GetPathRoot(root), root, StringComparison.OrdinalIgnoreCase) &&
                    (!NetworkDrives.IsNetworkRoot(root) || NetworkDrives.IsReady(root)) &&
                    new System.IO.DriveInfo(root) is { IsReady: true } drive)
                {
                    space = (drive.TotalSize, drive.TotalFreeSpace);
                }
                // NEW (round 51): what the drive is - for a share, from Windows' table of connections even
                // while it is out of reach; for a local drive, from the drive.
                if (string.Equals(System.IO.Path.GetPathRoot(root), root, StringComparison.OrdinalIgnoreCase))
                {
                    var network = NetworkDrives.IsNetworkRoot(root);
                    if (network || space is not null) hardware = DriveHardwareProbe.For(root, network);
                }
            }
            catch (Exception) { }
            foreach (var (other, indexed) in others)
            {
                try
                {
                    // CHANGED (round 50): network drives too, but only ones that answered their last check -
                    // asking a sleeping share could take many seconds.
                    var info = new System.IO.DriveInfo(other);
                    var reachable = info.DriveType == System.IO.DriveType.Network ? NetworkDrives.IsReady(other)
                        : info.DriveType is System.IO.DriveType.Fixed or System.IO.DriveType.Removable;
                    if (reachable && info.IsReady && info.TotalSize > 0)
                        drives.Add(new(other, info.VolumeLabel, info.TotalSize, info.TotalFreeSpace, indexed,
                            Hardware: DriveHardwareProbe.For(other, info.DriveType == System.IO.DriveType.Network)));   // CHANGED (round 51)
                }
                catch (Exception) { }
            }
            return (space, drives, hardware, pc: PcHardware.Read());   // NEW (round 54): the computer's parts, read once
        }).ContinueWith(task => Dispatcher.InvokeAsync(() =>
        {
            if (_disposed || !string.Equals(_viewModel.Snapshot?.Root, root, StringComparison.OrdinalIgnoreCase)) return;
            _otherDrives = task.Result.drives;
            Treemap.SetOtherDrives(WithPreviews(_otherDrives));
            Treemap.SetThisHardware(task.Result.hardware ?? default);   // NEW (round 51)
            Treemap.SetPcParts(task.Result.pc);                           // NEW (round 54)
            Treemap.SetDriveSpace(task.Result.space);
            UpdatePreviews(root);   // NEW (round 48)
        }), TaskScheduler.Default);
    }

    // NEW (round 48): pictures of the other drives' folders and files. Each is laid out once per index of
    // that drive and kept (a few hundred kilobytes); the folder sizes come from the snapshot the analyzer
    // already holds for it when there is one, otherwise from a snapshot built just for this and dropped
    // straight after. One drive at a time, on a low-priority thread, after the drive being viewed is up.
    private List<Controls.DiskUsageTreemap.OtherDrive> _otherDrives = [];
    // Kept by drive, with the version and entry count of the index they came from - not the index itself,
    // which would keep a replaced index alive. A failed build is remembered the same way (Preview null),
    // so a drive that cannot be laid out is not retried on every refresh.
    private readonly Dictionary<string, (DateTime Built, int Count, Controls.DrivePreview? Preview)> _previews =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _previewsPending = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _previewWork;

    private List<Controls.DiskUsageTreemap.OtherDrive> WithPreviews(List<Controls.DiskUsageTreemap.OtherDrive> drives)
        => drives.ConvertAll(drive => _previews.TryGetValue(drive.Root, out var kept) && kept.Preview is { } preview ? drive with { Preview = preview } : drive);

    private void UpdatePreviews(string root)
    {
        // Tiles are kept in unit coordinates, so a later change of the map's shape only stretches them a
        // little; it is not worth laying every drive out again.
        var aspect = Treemap.WorldAspect;
        var volumes = FileIndexService.CaptureVolumesForDiskUsage();
        var wanted = _otherDrives
            .Select(drive => Array.Find(volumes, volume => volume.Root.Equals(drive.Root, StringComparison.OrdinalIgnoreCase)))
            // A drive still being indexed for the first time keeps its used/free picture until it is done;
            // a picture of the first few percent would otherwise stay for the whole session.
            .Where(volume => volume is not null && !FileIndexService.IsPartial(volume) &&
                // CHANGED (round 49): redone for a new scan of that drive, or once it has grown or shrunk by a
                // few percent - not for every file written to it, which rebuilt it (and redrew the machine
                // view, which flickered) on every refresh.
                !(_previews.TryGetValue(volume.Root, out var kept) && kept.Built == volume.BuiltUtc &&
                  Math.Abs(kept.Count - volume.Count) <= Math.Max(1000, kept.Count / 50)))
            .Cast<VolumeIndex>().ToArray();
        if (wanted.Length == 0 || wanted.All(volume => _previewsPending.Contains(volume.Root))) return;   // already on it
        _previewWork?.Cancel();
        var work = _previewWork = new CancellationTokenSource();
        var token = work.Token;
        _previewsPending.Clear();
        foreach (var volume in wanted) _previewsPending.Add(volume.Root);
        var jobs = wanted.Select(volume => (Volume: volume, Built: volume.BuiltUtc, Count: volume.Count,
            Exclusions: DiskUsageDeletionService.Shared.ExclusionsFor(volume))).ToArray();
        Task.Factory.StartNew(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            Thread.Sleep(1500);   // let the drive being opened have the machine first
            foreach (var (volume, built, count, exclusions) in jobs)
            {
                if (token.IsCancellationRequested) return;
                Controls.DrivePreview? preview = null;
                try
                {
                    // The snapshot the analyzer already holds for that drive, or one built for this and dropped
                    // straight after - with the same exclusions, so items deleted this session stay gone.
                    var snapshot = DiskUsageSnapshotCache.Peek(volume) ?? DiskUsageSnapshot.Build(volume, token, exclusions);
                    preview = Controls.DrivePreviewBuilder.Build(snapshot, aspect, token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception exception) { System.Diagnostics.Trace.WriteLine($"Clearspace: drive preview failed. {exception.Message}"); }
                Dispatcher.InvokeAsync(() =>
                {
                    if (_disposed || token.IsCancellationRequested) return;
                    _previewsPending.Remove(volume.Root);
                    _previews[volume.Root] = (built, count, preview);
                    // _otherDrives is always the list for the drive on screen, even if that changed meanwhile.
                    if (preview is not null && _viewModel.Snapshot is not null) Treemap.SetOtherDrives(WithPreviews(_otherDrives));
                });
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    // NEW (round 47): a click on another drive, zoomed all the way out: open it, as the drive list would.
    // CHANGED (round 48): a click on a folder on it opens that folder there.
    private async void OnMapDriveRequested(string root, string? path)
    {
        if (_disposed || _viewModel.IsBusy || string.Equals(root, _viewModel.SelectedRoot, StringComparison.OrdinalIgnoreCase)) return;
        await _viewModel.LoadAsync(root, path);
    }

    private void OnFileOperationCompleted(object? sender, EventArgs e)
    {
        Platter.Recheck();   // NEW (round 46): deleted files leave the disk layout
        FileOperationCompleted?.Invoke(this, e);
    }
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
                // CHANGED (round 39): the snapshot is passed along, so the map keys its kept tree and its
                // flat whole-drive layout to the new one instead of the one it replaced.
                Treemap.RefreshSource(snapshot.Item(0), (id, token) => snapshot.Children(id, token), _viewModel.FolderPath,
                    EmptyFolderName(), snapshot, snapshot.Item);
            else
                Treemap.SetSource(snapshot.Item(0), (id, token) => snapshot.Children(id, token), _viewModel.FolderPath,
                    EmptyFolderName(), snapshot.Item, snapshot);
            UpdateDriveSpace(snapshot.Root);   // NEW (round 44)
            UpdateExperimentalView();          // NEW (round 46)
        }
        else if (e.PropertyName == nameof(DiskUsageViewModel.MapItems))
        {
            Treemap.ShowFolder(_viewModel.FolderPath, EmptyFolderName());
            UpdateExperimentalView();          // NEW (round 46)
        }
        // NEW: the chosen drive isn't indexed yet; clear the map and say so.
        if (e.PropertyName == nameof(DiskUsageViewModel.WaitingRoot) && _viewModel.WaitingRoot is not null)
        {
            Treemap.ClearSource(_viewModel.Status);
            Blocks3D.Clear(_viewModel.Status);   // NEW (round 46)
            Platter.Clear(_viewModel.Status);
            City3D.Clear(_viewModel.Status);     // NEW (round 64)
        }
        if (e.PropertyName is nameof(DiskUsageViewModel.CanGoBack) or nameof(DiskUsageViewModel.IsBusy)) UpdateZoomNavigation();
        // NEW (round 47): a drive plugged in or indexed shows up beside the current one.
        if (e.PropertyName == nameof(DiskUsageViewModel.Roots) && _viewModel.Snapshot is { } current) UpdateDriveSpace(current.Root);
    }

    // NEW: an empty folder has no area on the map; the map says so over its parent.
    private string? EmptyFolderName() => _viewModel.MapItems.Count == 0 ? _viewModel.FolderName : null;

    private void UpdateZoomNavigation() => BackButton.SetCurrentValue(IsEnabledProperty,
        !_viewModel.IsBusy && (MapZoomed || CityInside || _viewModel.CanGoBack));   // CHANGED (round 46, round 64: CityInside)

    private void OnZoomChanged()
    {
        UpdateZoomNavigation();
        ZoomOutButton.Visibility = MapZoomed ? Visibility.Visible : Visibility.Collapsed;   // CHANGED (round 46)
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
        if (e.ChangedButton == MouseButton.XButton1 && CityInside) City3D.Exit();   // NEW (round 64)
        else if (e.ChangedButton == MouseButton.XButton1 && MapZoomed) Treemap.ZoomOut();   // CHANGED (round 46)
        else if (e.ChangedButton == MouseButton.XButton1) await _viewModel.BackAsync();
        else await _viewModel.ForwardAsync();
    }

    private async void OnNavigationKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F3) { e.Handled = true; Treemap.ToggleStats(); return; } // NEW: performance readout
        if (key == Key.Escape && CityInside) { e.Handled = true; City3D.Exit(); return; }   // NEW (round 64)
        if (key == Key.Escape && MapZoomed) { e.Handled = true; Treemap.ZoomOut(); return; }   // CHANGED (round 46)
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
        if (key == Key.Left && CityInside) City3D.Exit();   // NEW (round 64)
        else if (key == Key.Left && MapZoomed) Treemap.ZoomOut();   // CHANGED (round 46)
        else if (key == Key.Left) await _viewModel.BackAsync();
        else if (key == Key.Right) await _viewModel.ForwardAsync();
        else await _viewModel.UpAsync();
    }

    private async void OnBack(object sender, RoutedEventArgs e)
    {
        if (CityInside) City3D.Exit();   // NEW (round 64)
        else if (MapZoomed) Treemap.ZoomOut();   // CHANGED (round 46)
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
        Platter.Forget();   // NEW (round 46): the disk layout is read again too
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
