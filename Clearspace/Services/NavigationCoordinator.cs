// Clearspace | Folder-loading lifetime and background preparation.
using System.Diagnostics;
using Clearspace.Models;
using Clearspace.Native;

namespace Clearspace.Services;

// CHANGED (date order): + FoldersFirst. False for a list that is sorted and grouped by date (Downloads,
// Screenshots, Archives & Backups), where folders and files are listed together, newest first.
internal sealed record FolderLoadOptions(bool ShowHidden, SortColumn Column, bool Descending,
    bool GridFastPath, bool ShowPartial, bool FoldersFirst = true);

internal sealed class NavigationCoordinator : IDisposable
{
    private readonly Func<string, bool, CancellationToken, IEnumerable<FileSystemItem>> _enumerate;
    private readonly Action<IReadOnlyList<FileSystemItem>, bool> _prepare;
    private NavigationLoad? _current;
    private bool _disposed;

    public NavigationCoordinator(
        Func<string, bool, CancellationToken, IEnumerable<FileSystemItem>>? enumerate = null,
        Action<IReadOnlyList<FileSystemItem>, bool>? prepare = null)
    {
        _enumerate = enumerate ?? DirectoryEnumerator.Enumerate;
        _prepare = prepare ?? Prepare;
    }

    public NavigationLoad BeginLoad()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _current?.Cancel();
        return _current = new NavigationLoad(this);
    }

    public Task<IReadOnlyList<FileSystemItem>> LoadDirectoryAsync(NavigationLoad load, string path,
        FolderLoadOptions options, IProgress<IReadOnlyList<FileSystemItem>> partialProgress)
        => Task.Run<IReadOnlyList<FileSystemItem>>(() =>
        {
            var token = load.Token;
            token.ThrowIfCancellationRequested();
            var list = new List<FileSystemItem>();
            var firstBatchWatch = Stopwatch.StartNew();
            var firstBatchReported = false;
            foreach (var item in _enumerate(path, options.ShowHidden, token))
            {
                token.ThrowIfCancellationRequested();
                list.Add(item);
                if (options.ShowPartial && !firstBatchReported &&
                    (list.Count >= 256 || firstBatchWatch.ElapsedMilliseconds >= 25))
                {
                    var firstBatch = list.ToList();
                    firstBatch.Sort(new ItemComparer(options.Column, options.Descending, options.FoldersFirst));
                    _prepare(firstBatch, options.GridFastPath);
                    token.ThrowIfCancellationRequested();
                    partialProgress.Report(firstBatch);
                    firstBatchReported = true;
                }
            }
            list.Sort(new ItemComparer(options.Column, options.Descending, options.FoldersFirst));
            _prepare(list, options.GridFastPath);
            token.ThrowIfCancellationRequested();
            return list;
        }, load.Token);

    // NEW (live folder): reads the folder again and works out what changed since `current` was listed
    // (FolderMerge). Rows that have not changed are kept as they are; only the new ones are given their
    // icons and tags. Runs off the UI thread like a load, but is not a load: it does not cancel the one in
    // progress and shows nothing by itself. Sorting and ShowHidden come from `options`, as for a load.
    public Task<FolderChanges> ReadChangesAsync(string path, IReadOnlyList<FileSystemItem> current,
        FolderLoadOptions options, CancellationToken token)
        => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var fresh = new List<FileSystemItem>(current.Count + 16);
            foreach (var item in _enumerate(path, options.ShowHidden, token))
            {
                token.ThrowIfCancellationRequested();
                fresh.Add(item);
            }

            var changes = FolderMerge.Merge(current, fresh,
                new ItemComparer(options.Column, options.Descending, options.FoldersFirst), out var newcomers);
            if (newcomers.Count > 0)
                _prepare(newcomers, options.GridFastPath);

            token.ThrowIfCancellationRequested();
            return changes;
        }, token);

    private static void Prepare(IReadOnlyList<FileSystemItem> items, bool gridFastPath)
    {
        if (!gridFastPath)
        {
            IconService.Populate(items);
            IconService.PopulateTypeNames(items);
        }
        ScalableIconService.PopulateGridPlaceholders(items);
        // CHANGED (search fix): one read of the tags for the whole folder (it was one per row).
        var tags = TagService.Lookup();
        foreach (var item in items) item.RefreshTags(tags);
    }

    public void Dispose()
    {
        _disposed = true;
        _current?.Cancel();
        _current = null;
    }

    internal sealed class NavigationLoad(NavigationCoordinator owner) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private bool _disposed;
        public CancellationToken Token => _cancellation.Token;
        public bool IsCurrent => !_disposed && !owner._disposed && ReferenceEquals(owner._current, this);
        public void Cancel() { if (!_disposed) _cancellation.Cancel(); }
        public void Dispose()
        {
            if (_disposed) return;
            if (ReferenceEquals(owner._current, this)) owner._current = null;
            _disposed = true;
            _cancellation.Dispose();
        }
    }
}
