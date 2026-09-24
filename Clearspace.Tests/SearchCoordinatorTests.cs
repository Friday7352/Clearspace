using System.IO;
using Clearspace.Models;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class SearchCoordinatorTests
{
    private static SearchRequest Request(bool windows = true, bool contents = false)
        => new("ext:txt", @"C:\source", false, false, windows, contents);

    private static SearchCoordinator Create(FakeSources sources, List<SearchUpdate> updates)
    {
        var tags = new TagStore(() => null, _ => { });
        return new(sources, updates.Add, text => SearchQuery.Parse(text, tags));
    }

    internal static FileSystemItem Item(string path, FileAttributes attributes = FileAttributes.Normal)
        => new() { Name = Path.GetFileName(path), FullPath = path, Attributes = attributes };

    [TestMethod]
    public async Task MergesAllSourcesWithoutCaseInsensitiveDuplicates()
    {
        var sources = new FakeSources
        {
            Indexed = [Item(@"C:\source\SAME.txt"), Item(@"C:\index.txt")],
            Windows = [Item(@"C:\windows.txt"), Item(@"C:\INDEX.txt")],
            Crawled = [Item(@"C:\crawl.txt"), Item(@"C:\Windows.txt")]
        };
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(sources, updates);
        await coordinator.SearchAsync(Request(), [Item(@"C:\source\same.txt")], true, TimeSpan.Zero);
        Assert.AreEqual(4, updates[^1].Items.Count);
        Assert.AreEqual(1, sources.WindowsCalls);
        Assert.AreEqual(1, sources.CrawlCalls);
        Assert.IsFalse(updates[^1].IsSearching);
    }

    [TestMethod]
    public async Task CoveredIndexSkipsOtherSourcesUnlessContentSearchNeedsWindows()
    {
        var sources = new FakeSources { Covered = true, Indexed = [Item(@"C:\one.txt")] };
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(sources, updates);
        await coordinator.SearchAsync(Request() with { Text = "one" }, [], true, TimeSpan.Zero);
        Assert.AreEqual(0, sources.WindowsCalls);
        Assert.AreEqual(0, sources.CrawlCalls);
        await coordinator.SearchAsync(Request(contents: true) with { Text = "one" }, [], true, TimeSpan.Zero);
        Assert.AreEqual(1, sources.WindowsCalls);
        Assert.AreEqual(0, sources.CrawlCalls);
    }

    [DataTestMethod]
    [DataRow("ext:txt", FileAttributes.Normal)]
    [DataRow("is:folder", FileAttributes.Directory)]
    public async Task CoveredIndexStillCrawlsForFilterOnlyQueries(string text, FileAttributes attributes)
    {
        var nested = Item(@"C:\source\subfolder\nested.txt", attributes);
        var sources = new FakeSources { Covered = true, Crawled = [nested] };
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(sources, updates);
        await coordinator.SearchAsync(Request(windows: false) with { Text = text }, [], true, TimeSpan.Zero);
        Assert.AreEqual(1, sources.CrawlCalls);
        Assert.AreSame(nested, updates[^1].Items.Single());
        Assert.IsFalse(updates[^1].Status!.Contains("from index"));
        Assert.IsFalse(updates[^1].IsSearching);
    }

    [TestMethod]
    public async Task DisabledWindowsSettingIsRespectedEvenForContentSearch()
    {
        var sources = new FakeSources();
        using var coordinator = Create(sources, []);
        await coordinator.SearchAsync(Request(windows: false, contents: true), [], true, TimeSpan.Zero);
        Assert.AreEqual(0, sources.WindowsCalls);
        Assert.AreEqual(1, sources.CrawlCalls);
    }

    [TestMethod]
    public async Task FailedIndexFallsBackEvenWhenItClaimedCoverage()
    {
        var sources = new FakeSources
        {
            Covered = true, Index = _ => throw new IOException("Unavailable"),
            Crawled = [Item(@"C:\recovered.txt")]
        };
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(sources, updates);
        await coordinator.SearchAsync(Request(), [], true, TimeSpan.Zero);
        Assert.AreEqual(1, sources.CrawlCalls);
        Assert.AreEqual("recovered.txt", updates[^1].Items.Single().Name);
        StringAssert.Contains(updates[^1].Status!, "may be incomplete");
        Assert.IsFalse(updates[^1].IsSearching);
    }

    [TestMethod]
    public async Task SupersededRequestCannotPublishAfterNewSearch()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<FileSystemItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sources = new FakeSources { Covered = true };
        CancellationToken oldToken = default;
        sources.Index = token => { oldToken = token; entered.SetResult(); return release.Task; };
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(sources, updates);
        var first = coordinator.SearchAsync(Request(), [], true, TimeSpan.Zero);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sources.Index = _ => Task.FromResult<IReadOnlyList<FileSystemItem>>([Item(@"C:\new.txt")]);
        await coordinator.SearchAsync(Request(), [], true, TimeSpan.Zero);
        var count = updates.Count;
        release.SetResult([Item(@"C:\stale.txt")]);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(oldToken.IsCancellationRequested);
        Assert.AreEqual(count, updates.Count);
        Assert.AreEqual("new.txt", updates[^1].Items.Single().Name);
    }

    [TestMethod]
    public async Task EmptyQueryCancelsPendingSearchAndRestoresDirectoryItems()
    {
        var sources = new FakeSources();
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(sources, updates);
        var pending = coordinator.SearchAsync(Request(), [], true, TimeSpan.FromHours(1));
        var directory = new[] { Item(@"C:\photo.png") };
        await coordinator.SearchAsync(Request() with { Text = "" }, directory, true, TimeSpan.Zero);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreSame(directory, updates[^1].Items);
        Assert.AreEqual("0 folders, 1 files", updates[^1].Status);
        Assert.IsFalse(updates[^1].IsSearching);
        Assert.AreEqual(0, sources.CrawlCalls);
    }

    [DataTestMethod]
    [DataRow(@"C:\source", 0, "This folder is empty")]
    [DataRow(@"C:\source", 2, "2 folders, 0 files")]
    [DataRow(ExplorerLocations.MyPcPath, 1, "1 drive")]
    [DataRow(ExplorerLocations.NetworkPath, 0, "No mapped network locations")]
    [DataRow(ExplorerLocations.NetworkPath, 2, "2 network locations")]
    [DataRow(ExplorerLocations.PinnedPath, 0, "No pinned directories yet")]
    [DataRow(ExplorerLocations.CloudPath, 0, "No locations available")]
    [DataRow(ExplorerLocations.YourFilesPath, 1, "1 location")]
    [DataRow(ExplorerLocations.CategoryPathPrefix + "documents", 2, "2 locations")]
    public async Task ClearingCompletedSearchRestoresBrowsingStatus(string path, int count, string expected)
    {
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(new FakeSources(), updates);
        var directory = Enumerable.Range(0, count)
            .Select(i => Item($@"C:\source\folder{i}", FileAttributes.Directory)).ToArray();
        var request = Request() with { CurrentPath = path };
        await coordinator.SearchAsync(request, directory, true, TimeSpan.Zero);
        await coordinator.SearchAsync(request with { Text = "   " }, directory, true, TimeSpan.Zero);
        Assert.AreEqual(expected, updates[^1].Status);
        Assert.AreSame(directory, updates[^1].Items);
        Assert.IsFalse(updates[^1].IsSearching);
    }

    [TestMethod]
    public async Task EmptyQueryPreservesNavigationStatusWhenStatusUpdateIsNotRequested()
    {
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(new FakeSources(), updates);
        await coordinator.SearchAsync(Request() with { Text = "" }, [], false, TimeSpan.Zero);
        Assert.IsNull(updates[^1].Status);
    }

    [TestMethod]
    public async Task DrainsFinalCrawlBatchBeforeReportingCompletion()
    {
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(new FakeSources { Crawled = [Item(@"C:\last.txt")] }, updates);
        await coordinator.SearchAsync(Request(), [], true, TimeSpan.Zero);
        Assert.AreEqual("last.txt", updates[^1].Items.Single().Name);
        StringAssert.Contains(updates[^1].Status!, "1 match");
        Assert.IsFalse(updates[^1].IsSearching);
    }

    [TestMethod]
    public async Task MissingIndexedEntriesAreRemovedAndHiddenResultsFiltered()
    {
        var gone = Item(@"C:\gone.txt");
        var sources = new FakeSources
        {
            Indexed = [gone, Item(@"C:\visible.txt")], Missing = [gone],
            Windows = [Item(@"C:\hidden.txt", FileAttributes.Hidden), Item(@"C:\system.txt", FileAttributes.System)]
        };
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(sources, updates);
        await coordinator.SearchAsync(Request(), [], true, TimeSpan.Zero);
        Assert.AreEqual("visible.txt", updates[^1].Items.Single().Name);
    }

    [TestMethod]
    public async Task CombinedResultsRespectLimit()
    {
        var sources = new FakeSources
        {
            Indexed = Enumerable.Range(0, SearchCoordinator.MaxResults).Select(i => Item($@"C:\{i}.txt")).ToArray(),
            Crawled = [Item(@"C:\extra.txt")]
        };
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(sources, updates);
        await coordinator.SearchAsync(Request(), [], true, TimeSpan.Zero);
        Assert.AreEqual(SearchCoordinator.MaxResults, updates[^1].Items.Count);
        StringAssert.Contains(updates[^1].Status!, "First");
    }

    [TestMethod]
    public async Task DisposeCancelsDebounceWithoutCallingSources()
    {
        var sources = new FakeSources();
        var updates = new List<SearchUpdate>();
        var coordinator = Create(sources, updates);
        var task = coordinator.SearchAsync(Request(), [], true, TimeSpan.FromHours(1));
        var count = updates.Count;
        coordinator.Dispose();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(count, updates.Count);
        Assert.AreEqual(0, sources.CrawlCalls);
    }

    [TestMethod]
    public async Task HealthyDriveIsNotCrawledWhenAnotherDriveLosesCoverage()
    {
        var overlay = new IndexOverlay();
        overlay.MarkOverflowed(@"D:\");
        var sources = new FakeSources { Roots = [@"C:\", @"D:\"], Coverage = overlay.IsHealthy };
        using var coordinator = Create(sources, []);
        await coordinator.SearchAsync(Request(false) with { Text = "needle", Everywhere = true }, [], true, TimeSpan.Zero);
        CollectionAssert.AreEqual(new[] { @"D:\" }, sources.LastCrawlRoots!.ToArray());
        overlay.TryRecover(@"D:\", overlay.Generation(@"D:\"));
        await coordinator.SearchAsync(Request(false) with { Text = "needle", Everywhere = true }, [], true, TimeSpan.Zero);
        Assert.AreEqual(1, sources.CrawlCalls, "A recovered drive uses the index again.");
    }

    [TestMethod]
    public async Task CoverageLostDuringIndexLookupIsRechecked()
    {
        var healthy = true;
        var sources = new FakeSources
        {
            Roots = [@"C:\", @"D:\"], Coverage = root => root == @"C:\" || healthy,
            Index = _ => { healthy = false; return Task.FromResult<IReadOnlyList<FileSystemItem>>([]); }
        };
        using var coordinator = Create(sources, []);
        await coordinator.SearchAsync(Request(false) with { Text = "needle" }, [], true, TimeSpan.Zero);
        CollectionAssert.AreEqual(new[] { @"D:\" }, sources.LastCrawlRoots!.ToArray());
    }

    private sealed class FakeSources : ISearchSources
    {
        public bool IsIndexLive => true;
        public bool WindowsIndexAvailable => true;
        public bool Covered { get; init; }
        public int WindowsCalls { get; private set; }
        public int CrawlCalls { get; private set; }
        public IReadOnlyList<FileSystemItem> Indexed { get; init; } = [];
        public IReadOnlyList<FileSystemItem> Windows { get; init; } = [];
        public IReadOnlyList<FileSystemItem> Crawled { get; init; } = [];
        public IReadOnlyList<FileSystemItem> Missing { get; init; } = [];
        public Func<CancellationToken, Task<IReadOnlyList<FileSystemItem>>>? Index { get; set; }
        public IReadOnlyList<string>? Roots { get; init; }
        public Func<string, bool>? Coverage { get; init; }
        public IReadOnlyList<string>? LastCrawlRoots { get; private set; }
        public IReadOnlyList<string> ResolveRoots(SearchRequest request) => Roots ?? [request.CurrentPath];
        public bool Covers(string root) => Coverage?.Invoke(root) ?? Covered;
        public IReadOnlyList<FileSystemItem> KnownItems(SearchQuery query) => [];
        public Task<IReadOnlyList<FileSystemItem>> IndexAsync(SearchQuery query, IReadOnlyList<string> roots, bool hidden, int limit, CancellationToken token)
            => Index?.Invoke(token) ?? Task.FromResult(Indexed);
        public Task<IReadOnlyList<FileSystemItem>> WindowsAsync(SearchQuery query, IReadOnlyList<string> roots, bool contents, bool hidden, int limit, CancellationToken token)
        {
            WindowsCalls++;
            return Task.FromResult(Windows);
        }
        public Task<IReadOnlyList<FileSystemItem>> FinishIndexAsync(IReadOnlyList<FileSystemItem> items, CancellationToken token)
            => Task.FromResult(Missing);
        public Task<bool> CrawlAsync(SearchQuery query, IReadOnlyList<string> roots, bool hidden, int limit,
            IProgress<IReadOnlyList<FileSystemItem>> progress, CancellationToken token)
        {
            CrawlCalls++;
            LastCrawlRoots = roots;
            progress.Report(Crawled);
            return Task.FromResult(false);
        }
    }
}
