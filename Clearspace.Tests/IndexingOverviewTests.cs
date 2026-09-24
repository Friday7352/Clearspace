using System.IO;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class IndexingOverviewTests
{
    [TestMethod]
    public void WaitingStatusExplainsCooldownAndWorkAheadWithoutAFakeProgressBar()
    {
        var retry = DateTime.UtcNow.AddMinutes(10);
        var reason = IndexingOverview.ExplainWait(retry, null, null);
        StringAssert.Contains(reason, "20 minutes");
        StringAssert.Contains(reason, "Scan now");
        StringAssert.Contains(IndexingOverview.ExplainWait(retry, @"D:\", null), "Waiting for D:");
        StringAssert.Contains(IndexingOverview.ExplainWait(null, null, "Saving index"), "Saving index");
        var row = new IndexedDrive(@"C:\", "Waiting to update", reason, 0, 0, null, null, null) { ScheduledUtc = retry };
        Assert.IsFalse(row.ShowProgress);
        StringAssert.Contains(row.ProgressText, "Retry at");
        StringAssert.Contains((row with { ScheduledUtc = null }).ProgressText, "Queued");
        var summary = new IndexingSummary([row], "", null, null, null, 0, null, "", 0, 0, false);
        Assert.AreEqual(reason, summary.ActivityDetail);
    }

    [TestMethod]
    public void WorkerWakesWhenRetryBecomesEligibleInsteadOfWaitingForNextSave()
    {
        var now = DateTime.UtcNow;
        Assert.AreEqual(TimeSpan.FromSeconds(15), FileIndexService.RescanWait(now, [now.AddSeconds(15)]));
        Assert.AreEqual(TimeSpan.Zero, FileIndexService.RescanWait(now, [now.AddSeconds(-1)]));
        Assert.AreEqual(TimeSpan.FromMinutes(5), FileIndexService.RescanWait(now, [now.AddMinutes(20)]));
        var root = @"Z:\test-request-only\";
        FileIndexService.ScanNow(root);
        CollectionAssert.Contains(FileIndexService.PendingRescans, root);
        Assert.IsNull(FileIndexService.RetryAt(root));
    }
    [TestMethod]
    public void ProgressCountsDiscoveredWorkAndReservesCompletionForFinishedScan()
    {
        var tracker = new IndexScanTracker();
        tracker.Visit(@"Z:\");
        tracker.DiscoverFolder();
        tracker.FinishFolder();
        var half = tracker.Capture();
        Assert.AreEqual(50d, half.ProgressPercent);
        Assert.AreEqual(1L, half.FoldersRemaining);
        tracker.Visit(@"Z:\child");
        tracker.DiscoverFolder();
        Assert.IsTrue(tracker.Capture().ProgressPercent < half.ProgressPercent);
        Assert.AreEqual(2L, half.FoldersDiscovered, "Captured progress must remain stable.");
        tracker.FinishFolder();
        tracker.Visit(@"Z:\child\nested");
        tracker.FinishFolder();
        Assert.AreEqual(99d, tracker.Capture().ProgressPercent);
        tracker.Complete();
        Assert.AreEqual(100d, tracker.Capture().ProgressPercent);
        Assert.AreEqual(0L, tracker.Capture().FoldersRemaining);
    }

    [TestMethod]
    public void DriveProgressDistinguishesRefreshCompletionAndMissingCoverage()
    {
        var index = new VolumeIndex(@"Z:\", 1);
        var scan = new IndexScanTracker();
        scan.DiscoverFolder(); scan.FinishFolder();
        var row = new IndexedDrive(index.Root, "Updating", "", 10, 20, DateTime.UtcNow, scan.Capture(), index);
        Assert.AreEqual(50d, row.ProgressPercent, "A prior saved index must not make an active refresh show complete.");
        StringAssert.Contains(row.ProgressDetail, "1 waiting");
        Assert.AreEqual(100d, (row with { State = "Up to date" }).ProgressPercent);
        Assert.AreEqual(0d, (row with { State = "Needs attention" }).ProgressPercent);
        Assert.AreEqual(0d, (row with { State = "Not included" }).ProgressPercent);
        Assert.AreEqual(0d, (row with { State = "Saved index", Source = null }).ProgressPercent);
    }

    [TestMethod]
    public void BrowserReadsOnlyIndexedChildrenAndOmitsDeletedEntries()
    {
        var index = new VolumeIndex(@"Z:\not-a-real-folder\", 1);
        index.Add(-1, index.Root, 0, 0, 0, FileAttributes.Directory);
        index.Add(0, "Nested", 0, 0, 0, FileAttributes.Directory);
        index.Add(1, "hidden.txt", 50, 0, 0, FileAttributes.Hidden);
        index.Add(0, "Removed", 5, 0, 0, VolumeIndex.RemovedFlag);
        var root = IndexingOverview.ReadFolder(index, 0, "", default);
        Assert.AreEqual("Nested", root.Entries.Single().Name);
        var nested = IndexingOverview.ReadFolder(index, 1, "HIDDEN", default);
        Assert.AreEqual(50L, nested.Entries.Single().Bytes);
        Assert.AreEqual(0, nested.Parent);
        Assert.AreEqual(@"Z:\not-a-real-folder\Nested", nested.Path);
        Assert.AreEqual(0, IndexingOverview.ReadFolder(index, 1, "unmatched", default).MatchingCount);
    }

    [TestMethod]
    public void LargeFolderCanBePagedWithoutLosingEntriesOrAllocatingTheWholeListing()
    {
        var index = new VolumeIndex(@"Z:\", 1);
        index.Add(-1, index.Root, 0, 0, 0, FileAttributes.Directory);
        for (var i = 0; i < 2500; i++) index.Add(0, $"file{i:D4}", 1, 0, 0, FileAttributes.Normal);
        var ids = new HashSet<int>();
        foreach (var offset in new[] { 0, 1000, 2000 })
        {
            var page = IndexingOverview.ReadFolder(index, 0, "", default, offset: offset);
            Assert.AreEqual(2500, page.MatchingCount);
            Assert.IsTrue(page.Entries.Count <= 1000);
            foreach (var item in page.Entries) Assert.IsTrue(ids.Add(item.Id));
        }
        Assert.AreEqual(2500, ids.Count);
        Assert.ThrowsException<OperationCanceledException>(() => IndexingOverview.ReadFolder(index, 0, "", new CancellationToken(true)));
    }

    [TestMethod]
    public void ScanDetailsAreBoundedAndDistinguishCurrentFolderFromCompletion()
    {
        var tracker = new IndexScanTracker();
        tracker.Visit(@"C:\Users");
        for (var i = 0; i < 300; i++) tracker.Skip($@"C:\link{i}", "Folder link not followed");
        var active = tracker.Capture();
        Assert.AreEqual(@"C:\Users", active.CurrentFolder);
        Assert.AreEqual(1L, active.FoldersVisited);
        Assert.AreEqual(300L, active.SkippedFolders);
        Assert.AreEqual(200, active.Examples.Count);
        Assert.IsNull(active.CompletedUtc);
        tracker.Complete();
        var completed = tracker.Capture();
        Assert.IsNotNull(completed.CompletedUtc);
        Assert.AreEqual("", completed.CurrentFolder);
        Assert.AreEqual(@"C:\Users", active.CurrentFolder, "Captured status must not change under the UI.");
    }

    [TestMethod]
    public void SavedIndexWithoutScanDetailsDoesNotClaimFullCoverage()
    {
        var row = new IndexedDrive(@"C:\", "Saved index", "", 10, 20, DateTime.UtcNow, null, null);
        StringAssert.Contains(row.CoverageText, "not saved");
        StringAssert.Contains(row.ScanText, "Scan started");
    }
}
