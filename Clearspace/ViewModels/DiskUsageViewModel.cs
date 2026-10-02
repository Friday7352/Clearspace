using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace.ViewModels;

internal sealed record DiskUsageCrumb(int Id, string Name);
internal sealed record DiskUsageLocation(string Root, string Path);

internal sealed class DiskUsageViewModel : ObservableObject, IDisposable
{
    private readonly List<DiskUsageLocation> _history = [];
    private int _historyPosition = -1;
    private readonly Func<VolumeIndex[]> _capture;
    private readonly Func<IReadOnlyList<string>> _drives; // NEW: all indexable drives, indexed or not
    private readonly DiskUsageDeletionService _deletion;
    private bool _deleting;
    private IReadOnlyList<DiskUsageItem> _selection = [];
    private CancellationTokenSource? _work;
    private CancellationTokenSource? _focusWork; // NEW: navigation driven by zooming the map
    private DiskUsageSnapshot? _snapshot;
    private int _folder;
    private bool _disposed;
    private bool _busy;
    private string _status = "Choose an indexed drive to explore.";
    private string _details = "";

    public DiskUsageViewModel(Func<VolumeIndex[]>? capture = null, DiskUsageDeletionService? deletion = null)
    {
        // CHANGED: includes a drive that is still being indexed, so the map fills in as it scans.
        _capture = capture ?? FileIndexService.CaptureVolumesForDiskUsage;
        // NEW: list every drive in the picker, even ones not indexed yet (tests inject their own index).
        _drives = capture is null ? FileIndexService.IndexableRoots : () => [];
        _deletion = deletion ?? DiskUsageDeletionService.Shared;
    }
    public event EventHandler? FileOperationCompleted;
    public bool CanDelete => !IsBusy && _selection.Count > 0 && _selection.All(item => item.Id > 0);
    public bool CanCancel => IsBusy && !_deleting;
    public string DeleteLabel => _selection.Count > 1 ? $"Delete {_selection.Count:N0} items permanently…" : "Delete permanently…";
    public IReadOnlyList<string> Roots { get; private set; } = [];
    public string? SelectedRoot { get; private set; }
    // NEW: the chosen drive has no index yet; the view loads it as soon as indexing reaches it.
    internal string? WaitingRoot { get; private set; }
    public IReadOnlyList<DiskUsageItem> Items { get; private set; } = [];
    public IReadOnlyList<DiskUsageItem> MapItems { get; private set; } = [];
    // REMOVED: MapScene. The map now lays out the whole drive itself, lazily, from the snapshot.
    // NEW: raised (before MapItems) whenever a different snapshot is shown, so the map rebuilds.
    public int SnapshotVersion { get; private set; }
    // NEW: what the map needs to show the current folder.
    internal DiskUsageSnapshot? Snapshot => _snapshot;
    internal int FolderId => _folder;
    internal IReadOnlyList<int> FolderPath { get; private set; } = [];
    public string CurrentPath { get; private set; } = "";
    public string Summary { get; private set; } = "";
    public string SnapshotLabel { get; private set; } = "";
    public string FolderName { get; private set; } = "Choose a drive";
    public string TotalSize { get; private set; } = "—";
    public string ItemCountLabel { get; private set; } = "";
    public IReadOnlyList<DiskUsageCrumb> Breadcrumbs { get; private set; } = [];
    public bool CanGoBack => !IsBusy && _historyPosition > 0;
    public bool CanGoForward => !IsBusy && _historyPosition >= 0 && _historyPosition < _history.Count - 1;
    public bool IsEmpty => !IsBusy && MapItems.Count == 0;
    public bool IsReady => !IsBusy;
    public bool CanGoUp => !IsBusy && _snapshot is not null && _folder > 0;
    public bool CanGoRoot => CanGoUp;
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            SetProperty(ref _busy, value);
            OnPropertyChanged(nameof(IsReady)); OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(CanGoUp)); OnPropertyChanged(nameof(CanGoRoot));
            OnPropertyChanged(nameof(CanGoBack)); OnPropertyChanged(nameof(CanGoForward));
            OnPropertyChanged(nameof(CanDelete)); OnPropertyChanged(nameof(CanCancel));
        }
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Details { get => _details; private set => SetProperty(ref _details, value); }

    public Task LoadAsync(string? root = null, string? preferredPath = null)
        => LoadCoreAsync(root, preferredPath, null);

    private async Task LoadCoreAsync(string? root, string? preferredPath, int? historyTarget)
    {
        if (_disposed || _deleting) return;
        var work = BeginWork();
        try
        {
            var volumes = _capture();
            Roots = AllRoots(volumes); // CHANGED: indexed drives plus drives still waiting to be indexed
            var volume = volumes.FirstOrDefault(v => root is not null && v.Root.Equals(root, StringComparison.OrdinalIgnoreCase))
                ?? volumes.FirstOrDefault(v => preferredPath is not null &&
                    (preferredPath.TrimEnd('\\').Equals(v.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                     preferredPath.StartsWith(v.Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                ?? volumes.FirstOrDefault();
            OnPropertyChanged(nameof(Roots));
            if (historyTarget is not null && !volumes.Any(v => v.Root.Equals(root, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("That drive is no longer in the index.");
            // NEW: a drive that exists but isn't indexed yet: wait for it rather than showing another drive.
            var requested = root ?? (preferredPath is null ? null : System.IO.Path.GetPathRoot(preferredPath));
            if (historyTarget is null && requested is not null &&
                Roots.Contains(requested, StringComparer.OrdinalIgnoreCase) &&
                !volumes.Any(v => v.Root.Equals(requested, StringComparison.OrdinalIgnoreCase)))
            {
                ShowWaiting(Roots.First(r => r.Equals(requested, StringComparison.OrdinalIgnoreCase)));
                return;
            }
            WaitingRoot = null;
            if (volume is null)
            {
                Status = "No file index is available yet. Let Clearspace finish indexing, then choose Refresh.";
                return;
            }
            Status = "Calculating folder sizes…";
            var result = await Task.Run(() =>
            {
                // CHANGED (round 19): reuse the warm snapshot instead of aggregating the whole
                // volume again. Refresh and a finished index build are what throw it away.
                var snapshot = DiskUsageSnapshotCache.Get(volume, work.Token, _deletion.ExclusionsFor(volume));
                // CHANGED: history needs the exact folder; opening from Clearspace falls back to the
                // closest indexed folder above the requested one instead of the drive root.
                var folder = preferredPath is null ? 0
                    : historyTarget is not null ? snapshot.FindFolder(preferredPath, work.Token)
                    : FindNearestFolder(snapshot, preferredPath, work.Token);
                var found = folder >= 0 && (preferredPath is null ||
                    Normalize(snapshot.PathFor(folder)).Equals(Normalize(preferredPath), StringComparison.OrdinalIgnoreCase));
                if (folder < 0 && historyTarget is not null)
                    throw new InvalidOperationException("That folder is no longer in the index.");
                folder = Math.Max(0, folder);
                // CHANGED: no map preparation here; the map lays out folders on demand.
                // Round 3: list rows (shares, colors) are prepared here too, off the UI thread.
                var items = WithShares(snapshot.Children(folder, work.Token));
                return (Snapshot: snapshot, Folder: folder, Found: found, Items: items);
            }, work.Token);
            work.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(work)) return;
            _snapshot = result.Snapshot;
            SelectedRoot = result.Snapshot.Root;
            // CHANGED: the index is kept up to date live; BuiltUtc is the last full scan.
            SnapshotLabel = SnapshotLabelFor(result.Snapshot);
            Show(result.Folder, result.Items, snapshotChanged: true); // CHANGED
            RecordLocation(historyTarget);
            if (!result.Found)
                Status = result.Folder == 0
                    ? "This folder is not in the snapshot. Showing the indexed drive root."
                    : $"“{System.IO.Path.GetFileName(Normalize(preferredPath!))}” is not in the saved index yet, so this shows the closest indexed folder.";
        }
        catch (OperationCanceledException) { if (IsCurrent(work)) Status = "Calculation canceled. Choose Refresh to try again."; }
        catch (Exception exception)
        {
            if (IsCurrent(work)) Status = $"Could not load disk usage: {exception.Message}";
        }
        finally
        {
            if (IsCurrent(work)) OnPropertyChanged(nameof(SelectedRoot));
            FinishWork(work);
        }
    }

    private string[] AllRoots(VolumeIndex[] volumes) => volumes.Select(volume => volume.Root)
        .Concat(_drives()).Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(root => root, StringComparer.OrdinalIgnoreCase).ToArray();

    // NEW: nothing to show for this drive yet.
    private void ShowWaiting(string root)
    {
        _snapshot = null;
        _folder = 0;
        WaitingRoot = root;
        SelectedRoot = root;
        Items = [];
        MapItems = [];
        FolderPath = [];
        CurrentPath = root;
        FolderName = $"Drive {root.TrimEnd('\\')}";
        TotalSize = "—";
        ItemCountLabel = "";
        Breadcrumbs = [new DiskUsageCrumb(0, root)];
        SnapshotLabel = "";
        SetSelection([]);
        Status = FileIndexService.SkippedRoots.Contains(root, StringComparer.OrdinalIgnoreCase)
            ? $"{root} is too large for the index, so its sizes can't be shown."
            : $"{root} hasn't been indexed yet · it appears here as soon as indexing reaches it.";
        NotifyView(snapshotChanged: false);
        OnPropertyChanged(nameof(SelectedRoot));
        OnPropertyChanged(nameof(WaitingRoot));
    }

    // NEW: true once the index has a drive this view can open (used while indexing is running).
    internal bool HasIndexFor(string? preferredPath)
    {
        var volumes = _capture();
        if (volumes.Length == 0) return false;
        if (preferredPath is null) return true;
        return volumes.Any(v => preferredPath.StartsWith(v.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    // NEW: shown while no snapshot is loaded yet and the index is still being built.
    internal void ShowIndexProgress(string progress)
    {
        if (_snapshot is not null || IsBusy) return;
        Status = string.IsNullOrWhiteSpace(progress)
            ? "Waiting for the file index…"
            : $"{progress} · the map appears when this drive is indexed.";
    }

    // NEW: pick up drives the indexer published after this view opened.
    internal void RefreshRoots()
    {
        var roots = AllRoots(_capture());
        if (roots.SequenceEqual(Roots, StringComparer.OrdinalIgnoreCase)) return;
        Roots = roots;
        OnPropertyChanged(nameof(Roots));
        OnPropertyChanged(nameof(SelectedRoot));
    }

    // NEW: true while Show() runs for a live refresh, so the map resizes blocks in place
    // instead of rebuilding (see DiskUsageView.OnViewChanged).
    internal bool IsLiveRefresh { get; private set; }

    // NEW: something changed in the live index under the current folder. Rebuild the snapshot in
    // the background and show the same folder again, quietly: no busy state, no history entry.
    public async Task RefreshLiveAsync()
    {
        var snapshot = _snapshot;
        if (_disposed || IsBusy || _deleting || snapshot is null) return;
        var volume = Array.Find(_capture(), v => v.Root.Equals(snapshot.Root, StringComparison.OrdinalIgnoreCase));
        // CHANGED: a drive still being indexed grows without changing its version.
        if (volume is null || (ReferenceEquals(volume, snapshot.Source) && volume.Version == snapshot.SourceVersion &&
                               volume.Count == snapshot.Count)) return;
        _focusWork?.Cancel();
        var work = _focusWork = new CancellationTokenSource();
        var path = CurrentPath;
        try
        {
            var result = await Task.Run(() =>
            {
                // The live refresh is the one path that deliberately recomputes; it stores its
                // result so the next open starts from the newer numbers.
                var fresh = DiskUsageSnapshotCache.Rebuild(volume, work.Token, _deletion.ExclusionsFor(volume));
                var folder = Math.Max(0, FindNearestFolder(fresh, path, work.Token));
                return (Snapshot: fresh, Folder: folder, Items: WithShares(fresh.Children(folder, work.Token)));
            }, work.Token);
            if (!ReferenceEquals(_focusWork, work) || _disposed || IsBusy || !ReferenceEquals(snapshot, _snapshot)) return;
            _snapshot = result.Snapshot;
            SnapshotLabel = SnapshotLabelFor(result.Snapshot);
            IsLiveRefresh = true;
            try { Show(result.Folder, result.Items, snapshotChanged: true); }
            finally { IsLiveRefresh = false; }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Status = $"Could not update sizes: {exception.Message}"; }
        finally
        {
            if (ReferenceEquals(_focusWork, work)) _focusWork = null;
            work.Dispose();
        }
    }

    private static string SnapshotLabelFor(DiskUsageSnapshot snapshot)
        => FileIndexService.IsPartial(snapshot.Source)
            ? $"Indexing in progress · {snapshot.Count:N0} items so far"
            : $"Live index · last full scan {snapshot.BuiltUtc.ToLocalTime():g}";

    // NEW: something newer than the shown snapshot exists (live changes or indexing progress).
    internal bool HasNewerIndex()
    {
        var snapshot = _snapshot;
        if (snapshot is null) return false;
        var volume = Array.Find(_capture(), v => v.Root.Equals(snapshot.Root, StringComparison.OrdinalIgnoreCase));
        return volume is not null && (!ReferenceEquals(volume, snapshot.Source) ||
            volume.Version != snapshot.SourceVersion || volume.Count != snapshot.Count);
    }

    public Task NavigateAsync(int folder) => NavigateCoreAsync(folder, null);

    private async Task NavigateCoreAsync(int folder, int? historyTarget)
    {
        var snapshot = _snapshot;
        if (_disposed || IsBusy || snapshot is null || !snapshot.IsAvailable(folder) || !snapshot.Item(folder).IsFolder) return;
        if (folder == _folder && historyTarget is null) return;
        var work = BeginWork();
        Status = "Preparing folder…";
        try
        {
            // CHANGED: only the listing is prepared; the map already holds the geometry.
            var items = await Task.Run(() => WithShares(snapshot.Children(folder, work.Token)), work.Token);
            work.Token.ThrowIfCancellationRequested();
            if (IsCurrent(work)) { Show(folder, items, snapshotChanged: false); RecordLocation(historyTarget); }
        }
        catch (OperationCanceledException) { if (IsCurrent(work)) Status = "Navigation canceled."; }
        catch (Exception exception) { if (IsCurrent(work)) Status = $"Could not open this folder: {exception.Message}"; }
        finally { FinishWork(work); }
    }

    // NEW: the user zoomed or clicked into a folder on the map. The camera is already moving,
    // so this updates the list and history quietly: no busy state, no disabled controls, and a
    // newer focus change simply supersedes an older one.
    public async Task FocusFromMapAsync(int folder, Func<bool>? deferPresentation = null)
    {
        var snapshot = _snapshot;
        if (_disposed || IsBusy || snapshot is null ||
            !snapshot.IsAvailable(folder) || !snapshot.Item(folder).IsFolder) return;
        _focusWork?.Cancel();
        if (folder == _folder) return; // returning home also supersedes an unfinished focus change
        var work = _focusWork = new CancellationTokenSource();
        try
        {
            while (deferPresentation?.Invoke() == true) await Task.Delay(32, work.Token);
            var items = await Task.Run(() => WithShares(snapshot.Children(folder, work.Token)), work.Token);
            // The user may have resumed zooming while the listing was prepared.
            // Never replace a large ItemsSource in the middle of that gesture.
            while (deferPresentation?.Invoke() == true) await Task.Delay(32, work.Token);
            work.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_focusWork, work) || _disposed || IsBusy || !ReferenceEquals(snapshot, _snapshot)) return;
            Show(folder, items, snapshotChanged: false);
            RecordLocation(null);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (ReferenceEquals(_focusWork, work)) Status = $"Could not open this folder: {exception.Message}"; }
        finally
        {
            if (ReferenceEquals(_focusWork, work)) _focusWork = null;
            work.Dispose();
        }
    }

    public Task UpAsync() => _snapshot is null || _folder == 0 ? Task.CompletedTask : NavigateAsync(_snapshot.Parent(_folder));

    public Task BackAsync() => CanGoBack ? MoveHistoryAsync(_historyPosition - 1) : Task.CompletedTask;
    public Task ForwardAsync() => CanGoForward ? MoveHistoryAsync(_historyPosition + 1) : Task.CompletedTask;

    private Task MoveHistoryAsync(int target)
    {
        var location = _history[target];
        if (_snapshot is not null && _snapshot.Root.Equals(location.Root, StringComparison.OrdinalIgnoreCase))
        {
            var folder = _snapshot.FindFolder(location.Path);
            if (folder >= 0) return NavigateCoreAsync(folder, target);
            Status = "That folder is no longer in the index.";
            return Task.CompletedTask;
        }
        return LoadCoreAsync(location.Root, location.Path, target);
    }

    private void RecordLocation(int? historyTarget)
    {
        if (historyTarget is not null) _historyPosition = historyTarget.Value;
        else if (_historyPosition < 0 || !_history[_historyPosition].Path.Equals(CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            _history.RemoveRange(_historyPosition + 1, _history.Count - _historyPosition - 1);
            _history.Add(new DiskUsageLocation(_snapshot!.Root, CurrentPath));
            _historyPosition = _history.Count - 1;
        }
        OnPropertyChanged(nameof(CanGoBack)); OnPropertyChanged(nameof(CanGoForward));
    }

    public void Select(DiskUsageItem? item)
    {
        if (item is null || _snapshot is null) { Details = "Select a file to see its full path and exact size."; return; }
        var total = _snapshot.Item(_folder).Bytes;
        var percentage = total == 0 ? 0 : item.Bytes * 100d / total;
        var share = percentage > 0 && percentage < .1 ? "<0.1%" : $"{percentage:0.##}%";
        Details = $"{item.Name} · {item.Bytes:N0} bytes · {share} of this folder" +
            (item.Id < 0 ? " · Find these items in the list." : $"\n{_snapshot.PathFor(item.Id)}");
    }

    public void SetSelection(IEnumerable<DiskUsageItem> items)
    {
        // Round 3: clearing the selection no longer hashes every row of a huge folder.
        var requested = items as IReadOnlyCollection<DiskUsageItem> ?? items.ToArray();
        if (requested.Count == 0)
        {
            _selection = [];
            OnPropertyChanged(nameof(CanDelete)); OnPropertyChanged(nameof(DeleteLabel));
            Select(null);
            return;
        }
        items = requested;
        var available = Items.Select(item => item.Id).ToHashSet();
        _selection = items.Where(item => item.Id > 0 && available.Contains(item.Id)).DistinctBy(item => item.Id).ToArray();
        OnPropertyChanged(nameof(CanDelete)); OnPropertyChanged(nameof(DeleteLabel));
        if (_selection.Count <= 1) Select(_selection.FirstOrDefault());
        else Details = $"{_selection.Count:N0} items selected · {DiskUsageSnapshot.FormatBytes(_selection.Sum(item => item.Bytes))}";
    }

    public async Task DeletePermanentlyAsync(IntPtr owner, Func<DiskUsageDeleteRequest, bool> confirm)
    {
        var snapshot = _snapshot;
        if (_disposed || !CanDelete || snapshot is null) return;
        var request = DiskUsageDeletionService.CreateRequest(snapshot, _folder, _selection.Select(item => item.Id));
        if (request is null) { Status = "Select individual files or folders in this view. Drive roots and grouped blocks cannot be deleted."; return; }
        _deleting = true;
        var work = BeginWork();
        var attempted = false;
        try
        {
            if (!confirm(request) || _disposed)
            {
                if (!_disposed) Status = "Deletion canceled. No files were changed.";
                return;
            }
            Status = "Deleting permanently…";
            attempted = true;
            string message;
            var reportedSuccess = false;
            try
            {
                var outcome = _deletion.DeleteConfirmed(request, owner);
                reportedSuccess = outcome.Succeeded;
                message = outcome.Succeeded ? $"Permanently deleted {request.Targets.Count:N0} selected {(request.Targets.Count == 1 ? "item" : "items")}."
                    : outcome.Message;
            }
            catch (Exception exception) { message = $"Deletion could not finish: {exception.Message}"; }
            if (_disposed) return;
            Status = "Checking remaining files and updating sizes…";
            var refreshed = await Task.Run(() => _deletion.Reconcile(snapshot, request, work.Token), work.Token);
            if (!IsCurrent(work)) return;
            // Deleting changes the sizes: the reconciled snapshot becomes the warm one.
            DiskUsageSnapshotCache.Store(refreshed.Snapshot, _deletion.ExclusionsFor(refreshed.Snapshot.Source));
            _snapshot = refreshed.Snapshot;
            // CHANGED: only the listing is recomputed; the map rebuilds from the new snapshot.
            var children = await Task.Run(() => WithShares(refreshed.Snapshot.Children(_folder, work.Token)), work.Token);
            if (!IsCurrent(work)) return;
            Show(_folder, children, snapshotChanged: true);
            if (reportedSuccess && !request.Targets.All(target => refreshed.MissingPaths.Contains(target.Path, StringComparer.OrdinalIgnoreCase)))
                message = "Windows finished the delete request. Items still present or not verified remain shown.";
            // Drop deleted locations from history so Back never gets stuck on one.
            for (var i = _history.Count - 1; i >= 0; i--)
                if (refreshed.MissingPaths.Any(path => _history[i].Path.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                    _history[i].Path.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                {
                    _history.RemoveAt(i);
                    if (i <= _historyPosition) _historyPosition--;
                }
            Status = message + (refreshed.Incomplete ? " Some items could not be checked; sizes may be out of date." : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_disposed) Status = $"Files may have changed, but sizes could not be updated: {exception.Message}";
        }
        finally
        {
            _deleting = false;
            FinishWork(work);
            if (attempted) FileOperationCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Cancel() { if (!_deleting) _work?.Cancel(); }

    // NEW: shares within the folder plus each row's branch color, which is the hue its block
    // (and everything inside it) has on the map. Items arrive sorted largest first, as on the map.
    private static IReadOnlyList<DiskUsageItem> WithShares(IReadOnlyList<DiskUsageItem> items)
    {
        var total = items.Sum(item => (double)item.Bytes);
        return items.Select((item, rank) => item with
        {
            Share = total == 0 ? 0 : item.Bytes * 100 / total,
            ColorHex = DiskUsagePalette.ListColor(item, rank, items.Count)
        }).ToArray();
    }

    // CHANGED: no map argument; snapshotChanged tells the map to rebuild instead of fly.
    // Round 3: `items` arrive already prepared by WithShares on a background thread.
    private void Show(int folder, IReadOnlyList<DiskUsageItem> items, bool snapshotChanged)
    {
        _folder = folder;
        CurrentPath = _snapshot!.PathFor(folder);
        var item = _snapshot.Item(folder);
        Items = items;
        if (snapshotChanged) SnapshotVersion++;
        FolderPath = PathTo(_snapshot, folder); // NEW
        SetSelection([]);
        MapItems = DiskUsageSnapshot.MapItems(Items, 24).Select(child => child.Id >= 0 ? child : child with
        { Share = item.Bytes == 0 ? 0 : child.Bytes * 100d / item.Bytes }).ToArray();
        FolderName = folder == 0 ? $"Drive {_snapshot.Root.TrimEnd('\\')}" : item.Name;
        TotalSize = item.SizeText;
        var folders = items.Count(i => i.IsFolder);
        ItemCountLabel = $"{item.FileCount:N0} {(item.FileCount == 1 ? "file" : "files")} · {folders:N0} {(folders == 1 ? "folder" : "folders")} here";
        var crumbs = new List<DiskUsageCrumb>();
        var ancestor = folder;
        for (var i = 0; ancestor > 0 && i < 3; i++, ancestor = _snapshot.Parent(ancestor))
            crumbs.Add(new DiskUsageCrumb(ancestor, _snapshot.Item(ancestor).Name));
        if (ancestor > 0) crumbs.Add(new DiskUsageCrumb(ancestor, "…"));
        crumbs.Add(new DiskUsageCrumb(0, _snapshot.Root));
        crumbs.Reverse();
        Breadcrumbs = crumbs;
        Summary = $"{item.SizeText} · {item.FileCount:N0} files · {items.Count:N0} direct items";
        Details = "Select a file to see its full path and exact size.";
        Status = item.Bytes == 0 ? "No file sizes to show. Empty items are still listed."
            : "";
        NotifyView(snapshotChanged);
    }

    // NEW: the requested folder, or its closest indexed ancestor on the same drive.
    private static int FindNearestFolder(DiskUsageSnapshot snapshot, string path, CancellationToken token)
    {
        for (var current = Normalize(path); !string.IsNullOrEmpty(current); current = System.IO.Path.GetDirectoryName(current))
        {
            var id = snapshot.FindFolder(current, token);
            if (id >= 0) return id;
        }
        return -1;
    }

    private static string Normalize(string path)
    {
        try { path = System.IO.Path.GetFullPath(path); } catch (Exception) { }
        return path.Length > 3 ? path.TrimEnd('\\', '/') : path;
    }

    // NEW: folder ids from just below the drive root down to the folder.
    private static IReadOnlyList<int> PathTo(DiskUsageSnapshot snapshot, int folder)
    {
        var path = new List<int>();
        for (var current = folder; current > 0; current = snapshot.Parent(current)) path.Add(current);
        path.Reverse();
        return path;
    }

    private void NotifyView(bool snapshotChanged)
    {
        // CHANGED: SnapshotVersion goes first so the map has the new data before it is asked to move.
        if (snapshotChanged) OnPropertyChanged(nameof(SnapshotVersion));
        foreach (var name in new[] { nameof(Items), nameof(MapItems), nameof(CurrentPath), nameof(Summary),
                     nameof(SnapshotLabel), nameof(IsEmpty), nameof(CanGoUp), nameof(CanGoRoot),
                     nameof(FolderName), nameof(TotalSize), nameof(ItemCountLabel), nameof(Breadcrumbs) })
            OnPropertyChanged(name);
    }

    private CancellationTokenSource BeginWork()
    {
        _work?.Cancel();
        _focusWork?.Cancel(); // NEW: explicit navigation wins over a pending map focus change
        var work = new CancellationTokenSource();
        _work = work;
        IsBusy = true;
        return work;
    }
    private bool IsCurrent(CancellationTokenSource work) => !_disposed && ReferenceEquals(_work, work);
    private void FinishWork(CancellationTokenSource work)
    {
        if (IsCurrent(work)) { _work = null; IsBusy = false; }
        work.Dispose();
    }
    public void Dispose() { _disposed = true; _work?.Cancel(); _work = null; _focusWork?.Cancel(); _focusWork = null; }
}
