using System.IO;
using Clearspace.Models;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class SearchCoordinatorTests
{
    private readonly List<TagStore> _tags = [];
    [TestCleanup]
    public void Cleanup() { foreach (var tags in _tags) tags.Dispose(); }
    private static SearchRequest Request(bool windows = true, bool contents = false)
        => new("ext:txt", @"C:\source", false, false, windows, contents);

    private SearchCoordinator Create(FakeSources sources, List<SearchUpdate> updates)
    {
        var tags = new TagStore(":memory:");
        _tags.Add(tags);
        return new(sources, updates.Add, text => SearchQuery.Parse(text, tags));
    }

    internal static FileSystemItem Item(string path, FileAttributes attributes = FileAttributes.Normal)
        => new() { Name = Path.GetFileName(path), FullPath = path, Attributes = attributes };

    [TestMethod]
    public async Task TagDatabaseReadFailureReportsSearchFailure()
    {
        var updates = new List<SearchUpdate>();
        using var coordinator = new SearchCoordinator(new FakeSources(), updates.Add,
            _ => throw new InvalidOperationException("Tag database unavailable"));
        await coordinator.SearchAsync(Request(), [], true, TimeSpan.Zero);
        Assert.AreEqual(1, updates.Count);
        Assert.IsFalse(updates[0].IsSearching);
        StringAssert.Contains(updates[0].Status!, "Search could not finish");
    }

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

    // CHANGED (search relevance): the private index evaluates filters itself, so filter-only queries
    // on covered roots come from the index instead of a crawl.
    [DataTestMethod]
    [DataRow("ext:txt", FileAttributes.Normal)]
    [DataRow("is:folder", FileAttributes.Directory)]
    public async Task CoveredIndexAnswersFilterOnlyQueries(string text, FileAttributes attributes)
    {
        var nested = Item(@"C:\source\subfolder\nested.txt", attributes);
        var sources = new FakeSources { Covered = true, Indexed = [nested] };
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(sources, updates);
        await coordinator.SearchAsync(Request(windows: false) with { Text = text }, [], true, TimeSpan.Zero);
        Assert.AreEqual(0, sources.CrawlCalls);
        Assert.AreSame(nested, updates[^1].Items.Single());
        StringAssert.Contains(updates[^1].Status!, "from index");
        Assert.IsFalse(updates[^1].IsSearching);
    }

    // NEW (search relevance): a strong match that arrives after the limit is full still makes the cut.
    [TestMethod]
    public async Task BestResultArrivingLastSurvivesTheLimit()
    {
        var sources = new FakeSources
        {
            Indexed = Enumerable.Range(0, SearchCoordinator.MaxResults).Select(i => Item($@"C:\reports\old-report-{i}.txt")).ToArray(),
            Crawled = [Item(@"C:\report.txt")]
        };
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(sources, updates);
        await coordinator.SearchAsync(Request(windows: false) with { Text = "report" }, [], true, TimeSpan.Zero);
        Assert.AreEqual(SearchCoordinator.MaxResults, updates[^1].Items.Count);
        Assert.AreEqual("report.txt", updates[^1].Items[0].Name);
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
        StringAssert.Contains(updates[^1].Status!, "Best");
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

    // NEW (search trace): a search that takes long leaves one line saying where the time went; a quick one leaves none.
    [TestMethod]
    public async Task OnlyASlowSearchLeavesATraceLine()
    {
        var lines = new List<string>();
        var tags = new TagStore(":memory:");
        _tags.Add(tags);
        var sources = new FakeSources { Covered = true, Indexed = [Item(@"C:\source\needle.txt")] };
        using var coordinator = new SearchCoordinator(sources, _ => { }, text => SearchQuery.Parse(text, tags), lines.Add);

        await coordinator.SearchAsync(Request(false) with { Text = "needle" }, [], true, TimeSpan.Zero);
        Assert.AreEqual(0, lines.Count, "a quick search is not recorded");

        // The wait before the index is asked stands in for a slow stage.
        await coordinator.SearchAsync(Request(false) with { Text = "needle" }, [], true, SearchTrace.Slow + TimeSpan.FromMilliseconds(200));
        Assert.AreEqual(1, lines.Count);
        StringAssert.Contains(lines[0], "search took");
        StringAssert.Contains(lines[0], "6 characters");
        StringAssert.Contains(lines[0], "wait asked 1700");
        StringAssert.Contains(lines[0], "0 crawled from the disk");
        Assert.IsFalse(lines[0].Contains("needle"), "what was typed is never written down");
    }

    // NEW (search fix): results on a drive whose index is live are not checked on the disk; the rest are,
    // best first, up to a limit. `File.Exists` is the real one here, so the paths are ones that do not exist.
    [TestMethod]
    public void OnlyResultsFromADriveThatIsBehindAreCheckedOnTheDisk()
    {
        var items = new List<FileSystemItem>();
        for (var i = 0; i < 3000; i++)
        {
            items.Add(Item($@"C:\clearspace-test-nowhere\live{i}.txt"));
            items.Add(Item($@"D:\clearspace-test-nowhere\behind{i}.txt"));
        }

        var told = new List<string>();
        var missing = FileIndexService.PruneMissing(items, root => root.StartsWith('C'), CancellationToken.None, out var checkedOnDisk, told.Add);

        Assert.AreEqual(FileIndexService.MaxExistenceChecks, checkedOnDisk, "only D:, and only the top results");
        Assert.AreEqual(FileIndexService.MaxExistenceChecks, missing.Count);
        Assert.IsTrue(missing.All(item => item.FullPath.StartsWith(@"D:\")), "nothing on the live drive was asked about");
        Assert.AreEqual(@"D:\clearspace-test-nowhere\behind0.txt", missing[0].FullPath, "best first");
        Assert.AreEqual(missing.Count, told.Count, "each missing result is reported, to be hidden until the index catches up");

        var none = FileIndexService.PruneMissing(items, _ => true, CancellationToken.None, out checkedOnDisk, told.Add);
        Assert.AreEqual(0, checkedOnDisk);
        Assert.AreEqual(0, none.Count);

        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Assert.ThrowsException<OperationCanceledException>(() => FileIndexService.PruneMissing(items, _ => false, stopped.Token, out _, told.Add));
    }

    // NEW (search fix): the Windows folders (Desktop, Downloads...) are asked for once and remembered for a
    // short while. Every folder row asks for all of them when it gets its icon.
    [TestMethod]
    public void AKnownFolderIsAskedForOnceAndThenRemembered()
    {
        var store = new KnownFolders.Kept?[7];
        var asked = 0;
        string Ask() { asked++; return $@"C:\Users\someone\Downloads{asked}"; }
        var keep = (long)KnownFolders.KeepFor.TotalMilliseconds;

        Assert.AreEqual(@"C:\Users\someone\Downloads1", KnownFolders.Remember(6, Ask, 1_000, store));
        for (var i = 0; i < 5000; i++)
            Assert.AreEqual(@"C:\Users\someone\Downloads1", KnownFolders.Remember(6, Ask, 1_000 + i, store));
        Assert.AreEqual(1, asked, "thousands of rows, one question");

        Assert.AreEqual(@"C:\Users\someone\Downloads2", KnownFolders.Remember(6, Ask, 1_000 + keep, store), "a moved folder is noticed after a while");
        Assert.AreEqual(@"C:\Users\someone\Downloads3", KnownFolders.Remember(1, Ask, 1_000 + keep, store), "each folder has its own answer");
        Assert.AreEqual(3, asked);
    }

    // NEW (search fix): the list is not touched on the key press. Replacing it rebuilds every row on screen,
    // and that used to happen on each letter; now it waits for a pause in the typing.
    [TestMethod]
    public async Task NothingIsHandedToTheWindowWhileTypingContinues()
    {
        var sources = new FakeSources { Covered = true, Indexed = [Item(@"C:\elsewhere\found.txt")] };
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(sources, updates);
        var here = new[] { Item(@"C:\source\here.txt") };

        var first = coordinator.SearchAsync(Request(), here, true, TimeSpan.FromHours(1));
        Assert.AreEqual(0, updates.Count, "a letter was typed; the list stays as it is");
        var second = coordinator.SearchAsync(Request(), here, true, TimeSpan.FromHours(1));
        Assert.AreEqual(0, updates.Count, "and another");

        await coordinator.SearchAsync(Request(), here, true, TimeSpan.Zero); // typing stopped
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, updates[^1].Items.Count);
    }

    // NEW (search fix): this folder's matches and the index's answer go to the list together.
    [TestMethod]
    public async Task ThisFoldersMatchesAndTheIndexsAnswerArriveTogether()
    {
        var sources = new FakeSources { Covered = true, Indexed = [Item(@"C:\elsewhere\found.txt")] };
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(sources, updates);

        await coordinator.SearchAsync(Request(windows: false), [Item(@"C:\source\here.txt")], true, TimeSpan.Zero);

        Assert.AreEqual(2, updates[0].Items.Count, "the first update already has both");
        Assert.IsFalse(updates[^1].IsSearching);
    }

    // NEW (search fix): an update whose rows are the ones already shown hands over the same list. The window
    // sees that nothing changed and does not rebuild its rows; a search that the index answers changes the
    // list once, however many updates it sends.
    [TestMethod]
    public async Task AnUpdateWithTheSameRowsHandsOverTheSameList()
    {
        var sources = new FakeSources { Covered = true, Indexed = [Item(@"C:\elsewhere\found.txt")] };
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(sources, updates);

        await coordinator.SearchAsync(Request(windows: false), [], true, TimeSpan.Zero);

        Assert.IsTrue(updates.Count >= 2, "the results, then the finished status");
        Assert.AreEqual(1, updates[0].Items.Count);
        Assert.IsTrue(updates.All(update => ReferenceEquals(update.Items, updates[0].Items)));
        StringAssert.Contains(updates[^1].Status!, "from index");
        Assert.IsFalse(updates[^1].IsSearching);
    }

    // NEW (search fix): the wait for the index is short. If the index is slow, this folder's matches are
    // shown without it, and the rest follows.
    [TestMethod]
    public async Task ThisFoldersMatchesAreShownFirstWhenTheIndexIsSlow()
    {
        var release = new TaskCompletionSource<IReadOnlyList<FileSystemItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sources = new FakeSources { Covered = true, Index = _ => release.Task };
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(sources, updates);
        var here = Item(@"C:\source\here.txt");

        var search = coordinator.SearchAsync(Request(windows: false), [here], true, TimeSpan.Zero);
        Assert.AreEqual(0, updates.Count, "held back for the index");

        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (updates.Count == 0 && waited.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20);
        Assert.AreEqual(1, updates.Count, "the index has not answered; this folder's matches are shown");
        Assert.AreSame(here, updates[0].Items.Single());

        release.SetResult([Item(@"C:\elsewhere\found.txt")]);
        await search.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, updates[^1].Items.Count);
        Assert.IsFalse(updates[^1].IsSearching);
    }

    // NEW (search fix): a place the index does not cover is read from the disk, which takes long; this
    // folder's matches are shown before that starts.
    [TestMethod]
    public async Task ThisFoldersMatchesAreShownBeforeADriveIsReadFromTheDisk()
    {
        var sources = new FakeSources { Crawled = [Item(@"C:\elsewhere\found.txt")] };
        var updates = new List<SearchUpdate>();
        using var coordinator = Create(sources, updates);
        var here = Item(@"C:\source\here.txt");

        await coordinator.SearchAsync(Request(windows: false), [here], true, TimeSpan.Zero);

        Assert.AreSame(here, updates[0].Items.Single());
        Assert.IsTrue(updates[0].IsSearching);
        Assert.AreEqual(2, updates[^1].Items.Count);
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
