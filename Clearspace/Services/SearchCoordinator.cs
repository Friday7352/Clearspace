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

    // NEW (search trace): what the last IndexAsync and FinishIndexAsync spent their time on, in words; null when not known.
    string? IndexDetail => null;
    string? FinishDetail => null;
}

// CHANGED (search trace): `trace` is given one line for every search that took SearchTrace.Slow or longer,
// saying how long each stage took (see Services/SearchTrace.cs). Null: nothing is recorded.
internal sealed class SearchCoordinator(ISearchSources sources, Action<SearchUpdate> publish,
    Func<string, SearchQuery>? parse = null, Action<string>? trace = null) : IDisposable
{
    internal const int MaxResults = 10_000;

    // NEW (search fix): the list is left alone until typing has stopped for this long. Replacing the list
    // makes the window build every row on screen again, and the letter just typed is not drawn until that
    // is done; doing it on each key press (and three more times as the results came in) is what made
    // typing stutter. Typing faster than this now costs nothing; the results follow the last letter.
    internal static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(150);

    // NEW (search fix): how long this folder's own matches are held back for the index to answer, so that
    // both go into the list together (one rebuild, not two). The index usually answers well inside it.
    internal static readonly TimeSpan HoldForIndex = TimeSpan.FromMilliseconds(120);
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
        var found = new List<FileSystemItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var capped = false;
        var sourceFailed = false;
        var timer = Stopwatch.StartNew();
        var query = SearchQuery.Empty;

        // NEW (search trace): how long each stage took, for the one line a slow search leaves behind.
        long showMs = 0, localMs = 0, waitAsked = 0, waitMs = 0, placesMs = 0, indexMs = 0, sortMs = 0,
            checksMs = 0, windowsMs = -1, crawlMs = -1;
        var updates = 0;
        var places = 0;
        var crawled = 0;
        string? indexDetail = null;
        string? finishDetail = null;

        bool Current() => !token.IsCancellationRequested && version == _version && !_disposed;

        // CHANGED (search fix): when the rows are the same ones in the same order as the last time, the
        // same array is handed over again. The window sees that its list has not changed and leaves it
        // alone; a new array, even with the same contents, made it build every row again. A finished
        // search hands over results up to four times and most of those repeat the one before.
        FileSystemItem[]? shown = null;
        var published = false;
        void Publish(string? status, bool searching)
        {
            if (!Current()) return;
            var before = timer.ElapsedMilliseconds;
            SearchTrace.Step($"handing {found.Count:N0} results to the window");
            if (shown is null || !SameRows(shown, found))
                shown = found.ToArray();
            published = true;
            publish(new(shown, status, searching));
            SearchTrace.Step($"after handing {found.Count:N0} results to the window (the list is drawing them)");
            var took = timer.ElapsedMilliseconds - before;
            showMs += took;
            updates++;
            if (took >= 150)
                trace?.Invoke($"handing {found.Count:N0} results to the window took {took} ms");
        }
        void Add(IEnumerable<FileSystemItem> items)
        {
            foreach (var item in items)
            {
                if (!request.ShowHidden && (item.Attributes & (System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System)) != 0)
                    continue;
                if (!seen.Add(item.FullPath)) continue;
                found.Add(item);
                // CHANGED (search relevance): keep the best results, not the first to arrive. Trimming at
                // twice the limit keeps memory bounded without re-ranking on every item.
                if (found.Count >= 2 * MaxResults) RankAndTrim();
            }
        }
        void RankAndTrim()
        {
            SearchRanker.Rank(found, query, request.CurrentPath);
            if (found.Count <= MaxResults) return;
            found.RemoveRange(MaxResults, found.Count - MaxResults);
            capped = true; // dropped items stay in `seen`, so a later source cannot re-add them
        }

        try
        {
            // CHANGED: the query knows where the search started, for ranking.
            SearchTrace.Step("reading what was typed (tags are read here)");
            query = (parse ?? SearchQuery.Parse)(request.Text).At(request.CurrentPath);
            var parseMs = timer.ElapsedMilliseconds;
            if (query.IsEmpty)
            {
                if (Current()) publish(new(directoryItems,
                    updateStatus ? DescribeBrowsing(request.CurrentPath, directoryItems) : null, false));
                return;
            }
            SearchTrace.Step($"matching the {directoryItems.Count:N0} items of this folder");
            Add(directoryItems.Where(query.Matches));
            RankAndTrim();
            var matchMs = timer.ElapsedMilliseconds - parseMs;
            localMs = timer.ElapsedMilliseconds;

            // NEW (search trace): this part runs on the UI thread, once per key press. If it is slow, typing is.
            if (localMs >= 150)
                trace?.Invoke($"a key press took {localMs} ms to handle: reading the query {parseMs} ms, " +
                              $"matching {directoryItems.Count:N0} items {matchMs} ms");
            SearchTrace.Step("waiting for typing to stop");

            // CHANGED (search fix): nothing is handed to the window on the key press itself. This folder's
            // matches used to go to the list here, at once, and the list was rebuilt on every letter. They
            // now wait for TypingPause, and then (when the index covers the places searched) a little
            // longer for the index, so that a search normally changes the list once.
            // The index is asked after `delay`: the typing pause when it is live, longer when a drive has
            // to be read from the disk. If that is longer than the typing pause, this folder's matches are
            // shown first, as before.
            var delay = debounce ?? (sources.IsIndexLive ? TypingPause : TimeSpan.FromMilliseconds(350));
            var pause = delay < TypingPause ? delay : TypingPause;
            waitAsked = (long)delay.TotalMilliseconds;
            await Task.Delay(pause, token);
            if (delay > pause)
            {
                Publish(updateStatus ? DescribeLocal(query, request.Everywhere, found.Count) : null, false);
                await Task.Delay(delay - pause, token);
            }
            waitMs = timer.ElapsedMilliseconds - localMs; // far more than was asked for: the window or the thread pool was busy
            var lap = timer.ElapsedMilliseconds;

            // CHANGED (search fix): tagged items are read from disk (and get icons) on a worker, after the
            // debounce, instead of on the UI thread for every keystroke.
            if (request.Everywhere && query.HasIndexFilter)
            {
                Add(await Task.Run(() => sources.KnownItems(query), token));
                token.ThrowIfCancellationRequested();
            }
            SearchTrace.Step("finding the places to search");
            var roots = sources.ResolveRoots(request);
            if (roots.Count == 0)
            {
                // CHANGED (search fix): nowhere else to look (a page that is not a folder). This folder's
                // matches are the whole answer; they used to be on screen already at this point.
                Publish(updateStatus ? DescribeLocal(query, request.Everywhere, found.Count) : null, false);
                return;
            }
            // CHANGED: the private index now answers filter-only queries (ext:txt, is:folder, tag:work)
            // as well, so a covered root is never crawled.
            var crawlRoots = roots.Where(root => !sources.Covers(root)).ToArray();
            var covered = crawlRoots.Length == 0;
            var indexFailed = false;
            places = roots.Count;
            placesMs = timer.ElapsedMilliseconds - lap;
            lap = timer.ElapsedMilliseconds;
            var scope = request.Everywhere ? "across all drives" : "in this folder and subfolders";

            // CHANGED (search fix): with every place covered by the index, this folder's matches are not
            // shown yet; the index is asked, and they go to the list together with its answer. Only if the
            // index takes longer than HoldForIndex are they shown first. A place that has to be read from
            // the disk takes far longer, so there they are shown straight away, as before.
            if (!covered || published)
                Publish($"Searching {scope}… {found.Count:N0} found", !covered);

            try
            {
                var asking = sources.IndexAsync(query, roots, request.ShowHidden, MaxResults, token);
                if (!published && await Task.WhenAny(asking, Task.Delay(HoldForIndex, token)) != asking)
                    Publish($"Searching {scope}… {found.Count:N0} found", !covered);
                var indexed = await asking;
                token.ThrowIfCancellationRequested();
                indexMs = timer.ElapsedMilliseconds - lap;
                indexDetail = sources.IndexDetail;
                lap = timer.ElapsedMilliseconds;
                SearchTrace.Step($"taking in {indexed.Count:N0} results from the index");
                Add(indexed);
                await Task.Run(RankAndTrim, token); // CHANGED (search fix): up to 10,000 items, off the UI thread
                token.ThrowIfCancellationRequested();
                sortMs = timer.ElapsedMilliseconds - lap;
                Publish($"Searching {scope}… {found.Count:N0} found", !covered);
                lap = timer.ElapsedMilliseconds;
                var missing = await sources.FinishIndexAsync(indexed, token);
                token.ThrowIfCancellationRequested();
                checksMs = timer.ElapsedMilliseconds - lap;
                finishDetail = sources.FinishDetail;
                var gone = new HashSet<string>(missing.Select(item => item.FullPath), StringComparer.OrdinalIgnoreCase);
                found.RemoveAll(item => gone.Contains(item.FullPath));
                seen.ExceptWith(gone);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // A failed private index cannot be treated as complete coverage.
                covered = false;
                indexFailed = true;
                sourceFailed = true;
            }

            if (request.UseWindowsIndex && sources.WindowsIndexAvailable && (request.FileContents || !covered))
            {
                try
                {
                    lap = timer.ElapsedMilliseconds;
                    Add(await sources.WindowsAsync(query, roots, request.FileContents, request.ShowHidden, MaxResults, token));
                    token.ThrowIfCancellationRequested();
                    windowsMs = timer.ElapsedMilliseconds - lap;
                    Publish($"Searching {scope}… {found.Count:N0} found", !covered);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { sourceFailed = true; }
            }

            // Coverage can be lost while asynchronous index work is in flight.
            crawlRoots = roots.Where(root => indexFailed || !sources.Covers(root)).ToArray();
            covered = crawlRoots.Length == 0;
            crawled = crawlRoots.Length;
            lap = timer.ElapsedMilliseconds;
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
                        var crawlCapped = await sources.CrawlAsync(query, crawlRoots, request.ShowHidden, MaxResults,
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
                crawlMs = timer.ElapsedMilliseconds - lap;
            }

            token.ThrowIfCancellationRequested();
            await Task.Run(RankAndTrim, token); // CHANGED (search fix): off the UI thread
            token.ThrowIfCancellationRequested();
            var status = capped
                ? $"Best {found.Count:N0} matches {scope} · narrow the search to see fewer"
                : $"{found.Count:N0} match{(found.Count == 1 ? "" : "es")} {scope} · {timer.ElapsedMilliseconds} ms{(covered ? " · from index" : "")}";
            if (sourceFailed) status += " · a search source was unavailable; results may be incomplete";
            Publish(status, false);

            // NEW (search trace): one line for a search that took long, saying where the time went.
            if (trace is not null && timer.Elapsed >= SearchTrace.Slow)
            {
                static string Ms(long value) => value < 0 ? "not used" : $"{value} ms";
                trace($"search took {timer.ElapsedMilliseconds} ms: {request.Text.Length} characters, {found.Count:N0} results{(capped ? " (capped)" : "")}, " +
                      $"{places} places of which {crawled} crawled from the disk{(request.Everywhere ? ", everywhere" : "")}" +
                      $" | first results {localMs} ms | wait asked {waitAsked} got {waitMs} ms | places {placesMs} ms" +
                      $" | index {indexMs} ms [{indexDetail ?? "no detail"}] | sort {sortMs} ms | checks {checksMs} ms [{finishDetail ?? "no detail"}]" +
                      $" | Windows index {Ms(windowsMs)} | crawl {Ms(crawlMs)} | showing {showMs} ms in {updates} updates" +
                      $" | thread pool {ThreadPool.PendingWorkItemCount} queued, {ThreadPool.ThreadCount} threads");
            }
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

    // NEW (search fix): the same rows in the same order.
    private static bool SameRows(FileSystemItem[] shown, List<FileSystemItem> found)
    {
        if (shown.Length != found.Count)
            return false;

        for (var i = 0; i < shown.Length; i++)
            if (!ReferenceEquals(shown[i], found[i]))
                return false;

        return true;
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
