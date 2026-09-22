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
    private readonly DiskUsageDeletionService _deletion;
    private bool _deleting;
    private IReadOnlyList<DiskUsageItem> _selection = [];
    private CancellationTokenSource? _work;
    private DiskUsageSnapshot? _snapshot;
    private int _folder;
    private bool _disposed;
    private bool _busy;
    private string _status = "Choose an indexed drive to explore.";
    private string _details = "";

    public DiskUsageViewModel(Func<VolumeIndex[]>? capture = null, DiskUsageDeletionService? deletion = null)
    {
        _capture = capture ?? FileIndexService.CaptureVolumes;
        _deletion = deletion ?? DiskUsageDeletionService.Shared;
    }
    public event EventHandler? FileOperationCompleted;
    public bool CanDelete => !IsBusy && _selection.Count > 0 && _selection.All(item => item.Id > 0);
    public bool CanCancel => IsBusy && !_deleting;
    public string DeleteLabel => _selection.Count > 1 ? $"Delete {_selection.Count:N0} items permanently…" : "Delete permanently…";
    public IReadOnlyList<string> Roots { get; private set; } = [];
    public string? SelectedRoot { get; private set; }
    public IReadOnlyList<DiskUsageItem> Items { get; private set; } = [];
    public IReadOnlyList<DiskUsageItem> MapItems { get; private set; } = [];
    public NestedDiskMap MapScene { get; private set; } = NestedDiskMap.Create([]);
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
            Roots = volumes.Select(volume => volume.Root).ToArray();
            var volume = volumes.FirstOrDefault(v => root is not null && v.Root.Equals(root, StringComparison.OrdinalIgnoreCase))
                ?? volumes.FirstOrDefault(v => preferredPath is not null &&
                    (preferredPath.TrimEnd('\\').Equals(v.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                     preferredPath.StartsWith(v.Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                ?? volumes.FirstOrDefault();
            OnPropertyChanged(nameof(Roots));
            if (historyTarget is not null && !volumes.Any(v => v.Root.Equals(root, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("That drive is no longer in the index.");
            if (volume is null)
            {
                Status = "No file index is available yet. Let Clearspace finish indexing, then choose Refresh.";
                return;
            }
            Status = "Calculating folder sizes…";
            var result = await Task.Run(() =>
            {
                var snapshot = DiskUsageSnapshot.Build(volume, work.Token, _deletion.ExclusionsFor(volume));
                var folder = preferredPath is null ? 0 : snapshot.FindFolder(preferredPath, work.Token);
                var found = folder >= 0;
                if (!found && historyTarget is not null)
                    throw new InvalidOperationException("That folder is no longer in the index.");
                folder = Math.Max(0, folder);
                var items = snapshot.Children(folder, work.Token);
                return (Snapshot: snapshot, Folder: folder, Found: found, Items: items, Map: PrepareMap(snapshot, items, work.Token));
            }, work.Token);
            work.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(work)) return;
            _snapshot = result.Snapshot;
            SelectedRoot = result.Snapshot.Root;
            SnapshotLabel = $"Saved index · {result.Snapshot.BuiltUtc.ToLocalTime():g}";
            Show(result.Folder, result.Items, result.Map);
            RecordLocation(historyTarget);
            if (!result.Found) Status = "This folder is not in the snapshot. Showing the indexed drive root.";
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
            var result = await Task.Run(() =>
            {
                var items = snapshot.Children(folder, work.Token);
                return (Items: items, Map: PrepareMap(snapshot, items, work.Token));
            }, work.Token);
            work.Token.ThrowIfCancellationRequested();
            if (IsCurrent(work)) { Show(folder, result.Items, result.Map); RecordLocation(historyTarget); }
        }
        catch (OperationCanceledException) { if (IsCurrent(work)) Status = "Navigation canceled."; }
        catch (Exception exception) { if (IsCurrent(work)) Status = $"Could not open this folder: {exception.Message}"; }
        finally { FinishWork(work); }
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
            _snapshot = refreshed.Snapshot;
            var result = await Task.Run(() =>
            {
                var children = refreshed.Snapshot.Children(_folder, work.Token);
                return (Items: children, Map: PrepareMap(refreshed.Snapshot, children, work.Token));
            }, work.Token);
            if (!IsCurrent(work)) return;
            Show(_folder, result.Items, result.Map);
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

    private static NestedDiskMap PrepareMap(DiskUsageSnapshot snapshot, IReadOnlyList<DiskUsageItem> items, CancellationToken token)
        => NestedDiskMap.Create(NestedDiskMap.Colorize(items), id => snapshot.Children(id, token), token);

    private void Show(int folder, IReadOnlyList<DiskUsageItem> items, NestedDiskMap map)
    {
        _folder = folder;
        CurrentPath = _snapshot!.PathFor(folder);
        var item = _snapshot.Item(folder);
        Items = NestedDiskMap.Colorize(items);
        MapScene = map;
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
        NotifyView();
    }

    private void NotifyView()
    {
        foreach (var name in new[] { nameof(Items), nameof(MapItems), nameof(CurrentPath), nameof(Summary),
                     nameof(SnapshotLabel), nameof(IsEmpty), nameof(CanGoUp), nameof(CanGoRoot),
                     nameof(FolderName), nameof(TotalSize), nameof(ItemCountLabel), nameof(Breadcrumbs) })
            OnPropertyChanged(name);
    }

    private CancellationTokenSource BeginWork()
    {
        _work?.Cancel();
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
    public void Dispose() { _disposed = true; _work?.Cancel(); _work = null; }
}
