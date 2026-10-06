// NEW (live folder): keeping the list on screen in step with the disk.
//  - FolderMerge: which rows are kept, added, dropped and replaced (no disk access).
//  - NavigationCoordinator.ReadChangesAsync: only the new rows are prepared (icons, tags).
//  - FolderWatcher: real files in a temporary folder that is removed again after each test. These wait
//    on the clock, so the limits are generous; they check that changes are reported, bunched together,
//    and stop being reported when asked.
using System.IO;
using Clearspace.Models;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class LiveFolderTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "Clearspace live folder");
    private static readonly DateTime Day = new(2026, 10, 2, 9, 0, 0);
    private static readonly ItemComparer ByName = new(SortColumn.Name, descending: false);

    private static FileSystemItem FileRow(string name, long size = 10, int minute = 0) => new()
    {
        Name = name,
        FullPath = Path.Combine(Folder, name),
        Size = size,
        DateModified = Day.AddMinutes(minute),
        Attributes = FileAttributes.Archive
    };

    private static FileSystemItem FolderRow(string name, int minute = 0) => new()
    {
        Name = name,
        FullPath = Path.Combine(Folder, name),
        DateModified = Day.AddMinutes(minute),
        Attributes = FileAttributes.Directory
    };

    private static string[] Names(IReadOnlyList<FileSystemItem> items) => items.Select(item => item.Name).ToArray();

    // ---- FolderMerge

    [TestMethod]
    public void AFolderThatDidNotChangeKeepsItsVeryList()
    {
        IReadOnlyList<FileSystemItem> current = [FolderRow("Photos"), FileRow("a.txt"), FileRow("b.txt")];
        FileSystemItem[] fresh = [FileRow("b.txt"), FolderRow("Photos"), FileRow("a.txt")]; // as read again: new objects, any order

        var changes = FolderMerge.Merge(current, fresh, ByName, out var newcomers);

        Assert.IsFalse(changes.Any);
        Assert.AreSame(current, changes.Items, "nothing to publish, so nothing on screen is touched");
        Assert.AreEqual(0, newcomers.Count);
    }

    [TestMethod]
    public void OnlyTheRowsThatChangedAreNewObjects()
    {
        var photos = FolderRow("Photos");
        var a = FileRow("a.txt");
        var b = FileRow("b.txt", size: 10);
        var c = FileRow("c.txt");
        IReadOnlyList<FileSystemItem> current = [photos, a, b, c];

        var grown = FileRow("b.txt", size: 999, minute: 5);        // a download that got bigger
        var arrived = FileRow("d.txt");                            // a new file
        FileSystemItem[] fresh = [arrived, FileRow("a.txt"), grown, FolderRow("Photos", minute: 30)]; // c.txt is gone

        var changes = FolderMerge.Merge(current, fresh, ByName, out var newcomers);

        Assert.AreEqual((1, 1, 1), (changes.Added, changes.Removed, changes.Updated));
        CollectionAssert.AreEqual(new[] { "Photos", "a.txt", "b.txt", "d.txt" }, Names(changes.Items), "sorted like the list: folders first, then by name");
        Assert.AreSame(photos, changes.Items[0], "a folder whose date moved on keeps its row (and its picture)");
        Assert.AreSame(a, changes.Items[1], "an unchanged file keeps its row, so it stays selected");
        Assert.AreSame(grown, changes.Items[2]);
        Assert.AreSame(arrived, changes.Items[3]);
        CollectionAssert.AreEquivalent(new[] { grown, arrived }, newcomers, "only these still need icons and tags");
        Assert.AreEqual(4, current.Count, "the list that was on screen is not edited in place");
    }

    [TestMethod]
    public void RenamesAndAttributeChangesReplaceTheRow()
    {
        var was = FileRow("readme.txt");
        Assert.IsTrue(FolderMerge.IsSame(was, FileRow("readme.txt")));
        Assert.IsFalse(FolderMerge.IsSame(was, FileRow("README.txt")), "only the capitals changed: still a different name to show");
        Assert.IsFalse(FolderMerge.IsSame(was, FileRow("readme.txt", minute: 1)));

        var hidden = new FileSystemItem
        {
            Name = "readme.txt", FullPath = was.FullPath, Size = was.Size, DateModified = was.DateModified,
            Attributes = FileAttributes.Archive | FileAttributes.Hidden
        };
        Assert.IsFalse(FolderMerge.IsSame(was, hidden));

        var changes = FolderMerge.Merge([was], [FileRow("README.txt")], ByName, out var newcomers);
        Assert.AreEqual((0, 0, 1), (changes.Added, changes.Removed, changes.Updated));
        Assert.AreEqual("README.txt", changes.Items[0].Name);
        Assert.AreEqual(1, newcomers.Count);
    }

    [TestMethod]
    public void NewRowsLandWhereTheSortPutsThem()
    {
        IReadOnlyList<FileSystemItem> current = [FileRow("old.zip", minute: 0)];
        FileSystemItem[] fresh = [FileRow("old.zip", minute: 0), FileRow("new.zip", minute: 50), FileRow("middle.zip", minute: 20)];

        // The Downloads folder type: newest first.
        var changes = FolderMerge.Merge(current, fresh, new ItemComparer(SortColumn.DateModified, descending: true), out _);

        CollectionAssert.AreEqual(new[] { "new.zip", "middle.zip", "old.zip" }, Names(changes.Items));
    }

    [TestMethod]
    public async Task ReadingChangesPreparesOnlyTheNewRows()
    {
        var kept = FileRow("kept.txt");
        var arrived = FileRow("arrived.txt");
        var prepared = new List<FileSystemItem>();
        bool? askedForHidden = null;

        using var coordinator = new NavigationCoordinator(
            (path, hidden, token) =>
            {
                askedForHidden = hidden;
                return [FileRow("kept.txt"), arrived];
            },
            (items, grid) => prepared.AddRange(items));

        var options = new FolderLoadOptions(ShowHidden: true, SortColumn.Name, Descending: false, GridFastPath: false, ShowPartial: false);
        var changes = await coordinator.ReadChangesAsync(Folder, [kept], options, CancellationToken.None);

        Assert.AreEqual((1, 0, 0), (changes.Added, changes.Removed, changes.Updated));
        Assert.AreSame(kept, changes.Items.Single(item => item.Name == "kept.txt"));
        CollectionAssert.AreEqual(new[] { arrived }, prepared, "the row already on screen has its icon and tags");
        Assert.AreEqual(true, askedForHidden);

        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            () => coordinator.ReadChangesAsync(Folder, [kept], options, stopped.Token));
    }

    // ---- FolderWatcher

    private string _root = string.Empty;

    [TestInitialize]
    public void MakeFolder()
    {
        _root = Path.Combine(Path.GetTempPath(), "Clearspace live folder " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void RemoveFolder()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    // Counts the calls and lets a test wait for one.
    private sealed class Calls
    {
        private readonly SemaphoreSlim _signal = new(0);
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Add()
        {
            Interlocked.Increment(ref _count);
            _signal.Release();
        }

        public bool WaitForOne(int milliseconds = 8000) => _signal.Wait(milliseconds);
    }

    [TestMethod]
    public void ABurstOfChangesIsReportedOnce()
    {
        var calls = new Calls();
        using var watcher = new FolderWatcher(calls.Add, quiet: TimeSpan.FromMilliseconds(400), longest: TimeSpan.FromSeconds(5));
        watcher.Watch(_root);
        Assert.IsTrue(watcher.IsWatching);

        for (var i = 0; i < 25; i++)
            File.WriteAllText(Path.Combine(_root, $"download {i}.part"), "x");

        Assert.IsTrue(calls.WaitForOne(), "the change is reported");
        Thread.Sleep(1200);
        Assert.IsTrue(calls.Count <= 2, $"25 files arriving together are reported together, not one by one (got {calls.Count} calls)");

        // Deleting and renaming count too.
        var before = calls.Count;
        File.Move(Path.Combine(_root, "download 0.part"), Path.Combine(_root, "download 0.zip"));
        Assert.IsTrue(calls.WaitForOne());
        File.Delete(Path.Combine(_root, "download 1.part"));
        Assert.IsTrue(calls.WaitForOne());
        Assert.IsTrue(calls.Count > before);
    }

    [TestMethod]
    public void AFileThatKeepsGrowingIsStillReportedAlongTheWay()
    {
        var calls = new Calls();
        using var watcher = new FolderWatcher(calls.Add, quiet: TimeSpan.FromMilliseconds(400), longest: TimeSpan.FromMilliseconds(900));
        watcher.Watch(_root);

        // Written to every 100 ms for 3 seconds: the changes never go quiet for 400 ms.
        var path = Path.Combine(_root, "big download.part");
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until)
        {
            File.AppendAllText(path, new string('x', 1024));
            Thread.Sleep(100);
        }

        Assert.IsTrue(calls.Count >= 2, $"reported while it was still growing, not only after it stopped (got {calls.Count} calls)");
    }

    [TestMethod]
    public void NothingIsReportedOnceItStops()
    {
        var calls = new Calls();
        var watcher = new FolderWatcher(calls.Add, quiet: TimeSpan.FromMilliseconds(150));
        watcher.Watch(_root);

        watcher.Watch(null); // the window went to a page that is not a folder
        Assert.IsFalse(watcher.IsWatching);
        File.WriteAllText(Path.Combine(_root, "after stop.txt"), "x");
        Assert.IsFalse(calls.WaitForOne(900));

        watcher.Watch(_root);
        watcher.Dispose();   // the window closed
        File.WriteAllText(Path.Combine(_root, "after close.txt"), "x");
        Assert.IsFalse(calls.WaitForOne(900));
        watcher.Watch(_root); // ignored
        Assert.IsFalse(watcher.IsWatching);
    }

    [TestMethod]
    public void TheNewestRequestWinsAndAMissingFolderIsNotAnError()
    {
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        Thread.Sleep(200);

        var calls = new Calls();
        using var watcher = new FolderWatcher(calls.Add, quiet: TimeSpan.FromMilliseconds(150));

        watcher.Watch(other, order: 2);  // where the window is now
        watcher.Watch(_root, order: 1);  // an earlier request that took longer to start: must not replace it

        File.WriteAllText(Path.Combine(other, "seen.txt"), "x");
        Assert.IsTrue(calls.WaitForOne(), "the folder on screen is the one being watched");

        watcher.Watch(Path.Combine(_root, "does not exist"), order: 3);
        Assert.IsFalse(watcher.IsWatching, "a folder that is gone is simply not watched");

        // Nudge asks for one more call, as when a change arrives during a load.
        watcher.Watch(other, order: 4);
        var before = calls.Count;
        watcher.Nudge();
        Assert.IsTrue(calls.WaitForOne());
        Assert.IsTrue(calls.Count > before);
    }
}
