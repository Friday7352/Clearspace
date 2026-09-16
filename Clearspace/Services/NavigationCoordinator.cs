// Clearspace | Folder-loading lifetime and background preparation.
using System.Diagnostics;
using Clearspace.Models;
using Clearspace.Native;

namespace Clearspace.Services;

internal sealed record FolderLoadOptions(bool ShowHidden, SortColumn Column, bool Descending,
    bool GridFastPath, bool ShowPartial);

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
                    firstBatch.Sort(new ItemComparer(options.Column, options.Descending));
                    _prepare(firstBatch, options.GridFastPath);
                    token.ThrowIfCancellationRequested();
                    partialProgress.Report(firstBatch);
                    firstBatchReported = true;
                }
            }
            list.Sort(new ItemComparer(options.Column, options.Descending));
            _prepare(list, options.GridFastPath);
            token.ThrowIfCancellationRequested();
            return list;
        }, load.Token);

    private static void Prepare(IReadOnlyList<FileSystemItem> items, bool gridFastPath)
    {
        if (!gridFastPath)
        {
            IconService.Populate(items);
            IconService.PopulateTypeNames(items);
        }
        ScalableIconService.PopulateGridPlaceholders(items);
        foreach (var item in items) item.RefreshTags();
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
