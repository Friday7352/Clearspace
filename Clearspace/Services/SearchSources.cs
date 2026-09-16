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
                if (!drive.IsReady || drive.DriveType == DriveType.Network) continue;
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

    public Task<IReadOnlyList<FileSystemItem>> FinishIndexAsync(IReadOnlyList<FileSystemItem> items, CancellationToken token)
        => Task.Run<IReadOnlyList<FileSystemItem>>(() =>
        {
            token.ThrowIfCancellationRequested();
            Populate(items);
            return FileIndexService.PruneMissing(items);
        }, token);

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

    private static void Populate(IReadOnlyList<FileSystemItem> items)
    {
        IconService.Populate(items);
        IconService.PopulateTypeNames(items);
        foreach (var item in items) item.RefreshTags();
    }
}
