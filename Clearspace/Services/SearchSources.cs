// Clearspace | Adapters for the existing private index, Windows Search and crawler.
using System.IO;
using Clearspace.Models;

namespace Clearspace.Services;

internal sealed class SearchSources : ISearchSources
{
    public bool IsIndexLive => FileIndexService.IsLive;
    public bool WindowsIndexAvailable => WindowsSearchService.IsAvailable;
    public bool Covers(string root) => FileIndexService.Covers(root);

    public IReadOnlyList<string> ResolveRoots(SearchRequest request)
    {
        var roots = new List<string>();
        if (Directory.Exists(request.CurrentPath)) roots.Add(request.CurrentPath);
        if (!request.Everywhere) return roots;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                // CHANGED (search fix): the kind of drive is asked first. It is known without touching the
                // drive, whereas IsReady asks the drive itself - for a network drive that is a round trip to
                // the server, on the UI thread, at the start of every search.
                if (drive.DriveType == DriveType.Network || !drive.IsReady) continue;
                var root = drive.RootDirectory.FullName;
                if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase)) roots.Add(root);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return roots;
    }

    public IReadOnlyList<FileSystemItem> KnownItems(SearchQuery query)
    {
        var results = query.IndexCandidates().Select(path => FileSystemItem.FromLocation(path))
            .OfType<FileSystemItem>().Where(query.Matches).ToList();
        results.Sort(new ItemComparer(SortColumn.Name, descending: false));
        Populate(results);
        ScalableIconService.PopulateGridPlaceholders(results);
        return results;
    }

    public Task<IReadOnlyList<FileSystemItem>> IndexAsync(SearchQuery query, IReadOnlyList<string> roots,
        bool hidden, int limit, CancellationToken token)
        => Task.Run<IReadOnlyList<FileSystemItem>>(() => FileIndexService.Search(query, roots, hidden, limit, token), token);

    // CHANGED (search fix): stops as soon as the search has been replaced by a newer one. Each key press
    // starts a search, and a broad one has up to 10,000 results to give icons to and to check on the disk;
    // those jobs used to run to the end one after another behind the search you were waiting for.
    public Task<IReadOnlyList<FileSystemItem>> FinishIndexAsync(IReadOnlyList<FileSystemItem> items, CancellationToken token)
        => Task.Run<IReadOnlyList<FileSystemItem>>(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var rows = items as FileSystemItem[] ?? [.. items];
            var tags = TagService.Lookup();
            for (var start = 0; start < rows.Length; start += 256)
            {
                token.ThrowIfCancellationRequested();
                Populate(new ArraySegment<FileSystemItem>(rows, start, Math.Min(256, rows.Length - start)), tags);
            }

            var dressedMs = clock.ElapsedMilliseconds;
            clock.Restart();
            var missing = FileIndexService.PruneMissing(items, token);
            _finishDetail = $"icons, types and tags for {rows.Length:N0} rows {dressedMs} ms, on-disk checks {clock.ElapsedMilliseconds} ms, {missing.Count:N0} gone";
            return missing;
        }, token);

    private volatile string? _finishDetail;
    public string? IndexDetail => FileIndexService.LastSearchDetail; // NEW (search trace)
    public string? FinishDetail => _finishDetail;                    // NEW (search trace)

    public Task<IReadOnlyList<FileSystemItem>> WindowsAsync(SearchQuery query, IReadOnlyList<string> roots,
        bool contents, bool hidden, int limit, CancellationToken token)
        => Task.Run<IReadOnlyList<FileSystemItem>>(() =>
        {
            var items = new List<FileSystemItem>();
            foreach (var hit in WindowsSearchService.Search(query, roots, limit, contents, token))
            {
                token.ThrowIfCancellationRequested();
                var item = FileSystemItem.FromLocation(hit.Path);
                if (item is null || !query.MatchesStructural(item)) continue;
                if (!hidden && (item.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                items.Add(item);
            }
            Populate(items);
            return items;
        }, token);

    public Task<bool> CrawlAsync(SearchQuery query, IReadOnlyList<string> roots, bool hidden, int limit,
        IProgress<IReadOnlyList<FileSystemItem>> progress, CancellationToken token)
        => Task.Run(() => FileSearchService.Run(roots, hidden, query.Matches,
            new InlineProgress<IReadOnlyList<FileSystemItem>>(batch =>
            {
                token.ThrowIfCancellationRequested();
                Populate(batch);
                progress.Report(batch);
            }), limit, token), token);

    // CHANGED (search fix): one read of the tags for the whole list (it was one database round trip per row).
    private static void Populate(IReadOnlyList<FileSystemItem> items, TagStore.TagLookup? tags = null)
    {
        IconService.Populate(items);
        IconService.PopulateTypeNames(items);
        tags ??= TagService.Lookup();
        foreach (var item in items) item.RefreshTags(tags);
    }
}
