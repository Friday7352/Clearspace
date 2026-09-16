// Clearspace | Search orchestration, independent of the main window.
using System.Diagnostics;
using System.Threading.Channels;
using Clearspace.Models;

namespace Clearspace.Services;

internal sealed record SearchRequest(string Text, string CurrentPath, bool Everywhere,
    bool ShowHidden, bool UseWindowsIndex, bool FileContents);

internal sealed record SearchUpdate(IReadOnlyList<FileSystemItem> Items, string? Status, bool IsSearching);

internal interface ISearchSources
{
    bool IsIndexLive { get; }
    bool WindowsIndexAvailable { get; }
    IReadOnlyList<string> ResolveRoots(SearchRequest request);
    bool Covers(string root);
    IReadOnlyList<FileSystemItem> KnownItems(SearchQuery query);
    Task<IReadOnlyList<FileSystemItem>> IndexAsync(SearchQuery query, IReadOnlyList<string> roots, bool hidden, int limit, CancellationToken token);
    Task<IReadOnlyList<FileSystemItem>> WindowsAsync(SearchQuery query, IReadOnlyList<string> roots, bool contents, bool hidden, int limit, CancellationToken token);
    Task<IReadOnlyList<FileSystemItem>> FinishIndexAsync(IReadOnlyList<FileSystemItem> items, CancellationToken token);
    Task<bool> CrawlAsync(SearchQuery query, IReadOnlyList<string> roots, bool hidden, int limit,
        IProgress<IReadOnlyList<FileSystemItem>> progress, CancellationToken token);
}

internal sealed class SearchCoordinator(ISearchSources sources, Action<SearchUpdate> publish,
    Func<string, SearchQuery>? parse = null) : IDisposable
{
    internal const int MaxResults = 10_000;
    private CancellationTokenSource? _active;
    private long _version;
    private bool _disposed;

    public void Cancel()
    {
        _version++;
        _active?.Cancel();
        _active = null; // Each run disposes its own source after its workers finish.
    }

    public async Task SearchAsync(SearchRequest request, IReadOnlyList<FileSystemItem> directoryItems,
        bool updateStatus, TimeSpan? debounce = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Cancel();
        var version = _version;
        using var cancellation = new CancellationTokenSource();
        _active = cancellation;
        var token = cancellation.Token;
        var query = (parse ?? SearchQuery.Parse)(request.Text);
        var found = new List<FileSystemItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var capped = false;
        var sourceFailed = false;
        var timer = Stopwatch.StartNew();

        bool Current() => !token.IsCancellationRequested && version == _version && !_disposed;
        void Publish(string? status, bool searching)
        {
            if (Current()) publish(new(found.ToArray(), status, searching));
        }
        void Add(IEnumerable<FileSystemItem> items)
        {
            foreach (var item in items)
            {
                if (!request.ShowHidden && (item.Attributes & (System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System)) != 0)
                    continue;
                if (!seen.Add(item.FullPath)) continue;
                if (found.Count >= MaxResults) { capped = true; continue; }
                found.Add(item);
            }
        }

        try
        {
            if (query.IsEmpty)
            {
                if (Current()) publish(new(directoryItems,
                    updateStatus ? DescribeBrowsing(request.CurrentPath, directoryItems) : null, false));
                return;
            }
            Add(directoryItems.Where(query.Matches));
            if (request.Everywhere && query.HasIndexFilter)
                Add(sources.KnownItems(query));
            Publish(updateStatus ? DescribeLocal(query, request.Everywhere, found.Count) : null, false);

            await Task.Delay(debounce ?? TimeSpan.FromMilliseconds(sources.IsIndexLive ? 35 : 350), token);
            var roots = sources.ResolveRoots(request);
            if (roots.Count == 0) return;
            // The private index requires a filename term. Volume coverage alone
            // cannot answer filter-only queries such as ext:txt or is:folder.
            var covered = query.Terms.Count > 0 && roots.All(sources.Covers);
            var scope = request.Everywhere ? "across all drives" : "in this folder and subfolders";
            Publish($"Searching {scope}… {found.Count:N0} found", !covered);

            try
            {
                var indexed = await sources.IndexAsync(query, roots, request.ShowHidden, MaxResults, token);
                token.ThrowIfCancellationRequested();
                Add(indexed);
                SearchRanker.Rank(found, query.Terms, request.CurrentPath);
                Publish($"Searching {scope}… {found.Count:N0} found", !covered);
                var missing = await sources.FinishIndexAsync(indexed, token);
                token.ThrowIfCancellationRequested();
                var gone = new HashSet<string>(missing.Select(item => item.FullPath), StringComparer.OrdinalIgnoreCase);
                found.RemoveAll(item => gone.Contains(item.FullPath));
                seen.ExceptWith(gone);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // A failed private index cannot be treated as complete coverage.
                covered = false;
                sourceFailed = true;
            }

            if (request.UseWindowsIndex && sources.WindowsIndexAvailable && (request.FileContents || !covered))
            {
                try
                {
                    Add(await sources.WindowsAsync(query, roots, request.FileContents, request.ShowHidden, MaxResults, token));
                    token.ThrowIfCancellationRequested();
                    Publish($"Searching {scope}… {found.Count:N0} found", !covered);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { sourceFailed = true; }
            }

            if (!covered)
            {
                // The channel serializes worker batches with UI publication, avoiding races
                // between queued Progress callbacks and completion/cancellation.
                var batches = Channel.CreateUnbounded<IReadOnlyList<FileSystemItem>>(
                    new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
                async Task<(bool Capped, bool Failed)> ProduceAsync()
                {
                    try
                    {
                        var crawlCapped = await sources.CrawlAsync(query, roots, request.ShowHidden, MaxResults,
                            new InlineProgress<IReadOnlyList<FileSystemItem>>(batch => batches.Writer.TryWrite(batch)), token);
                        return (crawlCapped, false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return (false, false); }
                    catch (Exception) { return (false, true); }
                    finally { batches.Writer.TryComplete(); }
                }
                var producer = ProduceAsync();
                var lastPublish = timer.ElapsedMilliseconds;
                await foreach (var batch in batches.Reader.ReadAllAsync())
                {
                    if (!Current()) continue;
                    Add(batch);
                    if (timer.ElapsedMilliseconds - lastPublish >= 250)
                    {
                        Publish($"Searching {scope}… {found.Count:N0} found", true);
                        lastPublish = timer.ElapsedMilliseconds;
                    }
                }
                var completion = await producer;
                capped |= completion.Capped;
                sourceFailed |= completion.Failed;
            }

            token.ThrowIfCancellationRequested();
            SearchRanker.Rank(found, query.Terms, request.CurrentPath);
            var status = capped
                ? $"First {found.Count:N0} matches {scope} · narrow the search to see fewer"
                : $"{found.Count:N0} match{(found.Count == 1 ? "" : "es")} {scope} · {timer.ElapsedMilliseconds} ms{(covered ? " · from index" : "")}";
            if (sourceFailed) status += " · a search source was unavailable; results may be incomplete";
            Publish(status, false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            Publish("Search could not finish. Results may be incomplete.", false);
        }
        finally
        {
            if (ReferenceEquals(_active, cancellation)) _active = null;
        }
    }

    internal static string DescribeBrowsing(string path, IReadOnlyList<FileSystemItem> items)
    {
        var count = items.Count;
        if (path.Equals(ExplorerLocations.MyPcPath, StringComparison.OrdinalIgnoreCase))
            return $"{count:N0} drive{(count == 1 ? "" : "s")}";
        if (path.Equals(ExplorerLocations.NetworkPath, StringComparison.OrdinalIgnoreCase))
            return count == 0 ? "No mapped network locations"
                : $"{count:N0} network location{(count == 1 ? "" : "s")}";
        if (path.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase))
            return count > 0 ? $"{count:N0} location{(count == 1 ? "" : "s")}"
                : path.Equals(ExplorerLocations.PinnedPath, StringComparison.OrdinalIgnoreCase)
                    ? "No pinned directories yet" : "No locations available";

        var folders = items.Count(item => item.IsFolder);
        return count == 0 ? "This folder is empty" : $"{folders:N0} folders, {count - folders:N0} files";
    }

    internal static string DescribeLocal(SearchQuery query, bool everywhere, int count)
    {
        var scope = everywhere && query.HasIndexFilter ? "here and everywhere tagged" : "in this folder";
        return count == 0
            ? $"No matches {scope}{(query.Describe().Length == 0 ? "" : " for " + query.Describe())}"
            : $"{count:N0} match{(count == 1 ? "" : "es")} {scope}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        Cancel();
        _disposed = true;
    }
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
