using System.Diagnostics;
using System.IO;
using Clearspace.Models;
using Clearspace.Native;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class TraversalAndCoverageTests
{
    public TestContext TestContext { get; set; } = null!;

    private sealed class Fixture : IDisposable
    {
        private readonly string _base = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ClearspaceTraversalTests"));
        public string Root { get; }
        public Fixture()
        {
            Root = Path.Combine(_base, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }
        public string DeepFolder(int depth)
        {
            var path = Root;
            for (var i = 0; i < depth; i++) path = Path.Combine(path, "d");
            Directory.CreateDirectory(path);
            return path;
        }
        public void Dispose()
        {
            // Delete only this test's freshly created, absolute, GUID-named directory.
            var target = Path.GetFullPath(Root);
            if (!target.StartsWith(_base + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(target), "N", out _))
                throw new InvalidOperationException("Unsafe test cleanup target.");
            Directory.Delete(target, true);
        }
    }

    private static VolumeIndex Build(string root, CancellationToken token = default, Action<int>? progress = null)
        => FileIndexBuilder.Build(root, 1, 64 * 1024 * 1024, progress, token)!;

    private static string[] Indexed(VolumeIndex index) => index.Search(["needle"], true, false, true, 1000, default)
        .Select(index.GetPath).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string[] Crawled(string root, CancellationToken token = default)
    {
        var items = new List<FileSystemItem>();
        var gate = new object();
        FileSearchService.Run([root], true, item => item.Name.Contains("needle", StringComparison.Ordinal),
            new InlineProgress<IReadOnlyList<FileSystemItem>>(batch => { lock (gate) items.AddRange(batch); }), 1000, token);
        return items.Select(item => item.FullPath).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    [TestMethod]
    public void BothSearchPathsReachFortyEightLevelsAndTrackRenamesAndDeletions()
    {
        using var fixture = new Fixture();
        var folder = fixture.DeepFolder(48);
        var original = Path.Combine(folder, "needle.txt");
        File.WriteAllText(original, "deep contents");
        var clock = Stopwatch.StartNew();
        var index = Build(fixture.Root);
        var milliseconds = clock.Elapsed.TotalMilliseconds;
        var scan = index.ScanDetails!.Capture();
        Assert.AreEqual(49L, scan.FoldersVisited);
        Assert.AreEqual(0L, scan.SkippedFolders);
        Assert.IsNotNull(scan.CompletedUtc);
        CollectionAssert.AreEqual(new[] { original }, Indexed(index));
        CollectionAssert.AreEqual(Indexed(index), Crawled(fixture.Root));
        Assert.AreEqual(new FileInfo(original).Length, DiskUsageSnapshot.Build(index, default).Item(0).Bytes);

        var renamed = Path.Combine(folder, "needle-renamed.txt");
        File.Move(original, renamed);
        FileIndexUpdater.Replay(index, [(FileIndexUpdater.ChangeKind.Deleted, original), (FileIndexUpdater.ChangeKind.Created, renamed)], default);
        CollectionAssert.AreEqual(new[] { renamed }, Indexed(index));
        CollectionAssert.AreEqual(Indexed(index), Crawled(fixture.Root));
        File.Delete(renamed);
        FileIndexUpdater.Replay(index, [(FileIndexUpdater.ChangeKind.Deleted, renamed)], default);
        Assert.AreEqual(0, Indexed(index).Length);
        Assert.AreEqual(0, Crawled(fixture.Root).Length);
        TestContext.WriteLine($"48-level native traversal: {milliseconds:F2} ms; estimated index bytes {index.EstimatedBytes:N0}.");
    }

    [TestMethod]
    public void LiveSubtreeScanHasNoDepthCap()
    {
        using var fixture = new Fixture();
        var index = Build(fixture.Root);
        var folder = fixture.DeepFolder(48);
        var path = Path.Combine(folder, "needle.txt");
        File.WriteAllText(path, "new subtree");
        var movedIn = Path.Combine(fixture.Root, "d");
        FileIndexUpdater.Replay(index, [(FileIndexUpdater.ChangeKind.Created, movedIn)], default);
        CollectionAssert.AreEqual(new[] { path }, Indexed(index));
    }

    [TestMethod]
    public void IndexAndCrawlCancelDuringTraversal()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < 80; i++) Directory.CreateDirectory(Path.Combine(fixture.Root, $"folder{i}"));
        using var indexStop = new CancellationTokenSource();
        Assert.ThrowsException<OperationCanceledException>(() => Build(fixture.Root, indexStop.Token, _ => indexStop.Cancel()));
        using var crawlStop = new CancellationTokenSource();
        var examined = 0;
        Assert.ThrowsException<OperationCanceledException>(() => FileSearchService.Run([fixture.Root], true,
            _ => { Interlocked.Increment(ref examined); crawlStop.Cancel(); return false; },
            new InlineProgress<IReadOnlyList<FileSystemItem>>(_ => Assert.Fail("Canceled search must not publish results.")),
            1000, crawlStop.Token));
        Assert.IsTrue(examined > 0, "Cancel after traversal has begun.");
    }

    [TestMethod]
    public void UnavailableRootCannotPublishAnEmptySuccessfulIndex()
    {
        using var fixture = new Fixture();
        Assert.ThrowsException<IOException>(() => Build(Path.Combine(fixture.Root, "missing")));
        Assert.IsNull(FileIndexBuilder.Build(fixture.Root, 1, 0, null, default));
    }

    [DataTestMethod]
    [DataRow(0xA000000Cu, false)] // Symbolic link.
    [DataRow(0xA0000003u, false)] // Junction / mount point.
    [DataRow(0x9000001Au, true)]  // Cloud placeholder, not a directory alias.
    [DataRow(0x9000F01Au, true)]
    public void BothTraversalsUseTheSameReparsePolicy(uint tag, bool expected)
    {
        var data = new NativeMethods.WIN32_FIND_DATA
        {
            cFileName = "folder", dwFileAttributes = FileAttributes.Directory | FileAttributes.ReparsePoint, dwReserved0 = tag
        };
        var item = FileSystemItem.FromFindData(@"C:\", in data);
        Assert.AreEqual(expected, FileIndexBuilder.CanDescend(item.Attributes, item.ReparseTag));
        Assert.IsTrue(FileIndexBuilder.CanDescend(FileAttributes.Directory, 0));
        Assert.IsFalse(FileIndexBuilder.CanDescend(FileAttributes.Normal, tag));
    }

    [TestMethod]
    public void LostEventsAndRecoveryAreIsolatedAndGenerationChecked()
    {
        var overlay = new IndexOverlay();
        var requests = new List<string>();
        overlay.LostChanges += requests.Add;
        overlay.OnDeleted(@"D:\folder.with.dots");
        overlay.MarkOverflowed(@"C:\");
        var generation = overlay.Generation(@"C:\");
        Assert.IsFalse(overlay.IsHealthy(@"C:\"));
        Assert.IsTrue(overlay.IsHealthy(@"D:\"));
        Assert.IsTrue(overlay.IsRemoved(@"D:\folder.with.dots\child.txt"));
        Assert.IsFalse(overlay.IsRemoved(@"D:\folder.with.dots-extra\child.txt"));
        overlay.MarkOverflowed(@"C:\"); // Another lost batch while the first rescan was running.
        Assert.IsFalse(overlay.TryRecover(@"C:\", generation));
        Assert.IsFalse(overlay.IsHealthy(@"C:\"));
        Assert.IsTrue(overlay.TryRecover(@"C:\", overlay.Generation(@"C:\")));
        Assert.IsTrue(overlay.IsHealthy(@"C:\"));
        Assert.IsTrue(overlay.IsRemoved(@"D:\folder.with.dots\child.txt"), "Recovering C must retain D's deletions.");
        CollectionAssert.AreEqual(new[] { @"C:\", @"C:\" }, requests);
    }

    [TestMethod]
    public void OverlayCapacityFailureOnlyDisablesTheOffendingDrive()
    {
        var overlay = new IndexOverlay();
        overlay.OnCreated(@"D:\keep.txt");
        for (var i = 0; i < 100_001; i++) overlay.OnCreated($@"C:\item{i}.txt");
        Assert.IsFalse(overlay.IsHealthy(@"C:\"));
        Assert.IsTrue(overlay.IsHealthy(@"D:\"));
        Assert.AreEqual(1, overlay.Count);
    }

    [TestMethod]
    public async Task EventsDuringReplacementAreAppliedToThePublishedIndex()
    {
        using var fixture = new Fixture();
        var original = Build(fixture.Root);
        var current = original;
        using var updater = new FileIndexUpdater(() => [current]);
        updater.BeginRecording(fixture.Root);
        var replacement = Build(fixture.Root);
        var before = Path.Combine(fixture.Root, "needle-before.txt");
        File.WriteAllText(before, "before publication");
        updater.OnCreated(before);
        var after = Path.Combine(fixture.Root, "needle-after.txt");
        File.WriteAllText(after, "during publication");
        using var publishing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var applied = new ManualResetEventSlim();
        updater.Applied += paths => { if (paths.Contains(after)) applied.Set(); };
        var swap = Task.Run(() => updater.PublishReplacement(replacement, () =>
        {
            publishing.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            current = replacement;
        }, default));
        try
        {
            Assert.IsTrue(publishing.Wait(TimeSpan.FromSeconds(5)));
            var arriving = Task.Run(() => updater.OnCreated(after));
            release.Set();
            await Task.WhenAll(swap, arriving).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(applied.Wait(TimeSpan.FromSeconds(5)));
            CollectionAssert.AreEqual(new[] { after, before }.Order(StringComparer.OrdinalIgnoreCase).ToArray(), Indexed(current));
        }
        finally { release.Set(); await swap; }
    }

    [TestMethod]
    public void FailedWatcherMarksOnlyItsDriveAndRequestsRecovery()
    {
        using var fixture = new Fixture();
        var overlay = new IndexOverlay();
        var requests = new List<string>();
        overlay.LostChanges += requests.Add;
        using var watcher = new FileIndexWatcher(overlay);
        watcher.Watch(Path.Combine(fixture.Root, "not-present"));
        var root = Path.GetPathRoot(fixture.Root)!;
        Assert.IsFalse(overlay.IsHealthy(root));
        Assert.AreEqual(root, requests.Single());
        Assert.IsTrue(overlay.IsHealthy(@"Z:\"));
    }
}
