using System.IO;
using Clearspace.Services;
using Clearspace.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class DiskUsageTests
{
    private static VolumeIndex Example()
    {
        var index = new VolumeIndex(@"C:\", 1);
        index.Add(-1, @"C:\", 0, 0, 0, FileAttributes.Directory);
        index.Add(0, "Photos", 999, 0, 0, FileAttributes.Directory);
        index.Add(0, "Empty", 0, 0, 0, FileAttributes.Directory);
        index.Add(1, "Nested", 0, 0, 0, FileAttributes.Directory);
        index.Add(1, "a.jpg", 100, 0, 0, FileAttributes.Normal);
        index.Add(3, "hidden.jpg", 300, 0, 0, FileAttributes.Hidden);
        index.Add(0, "file.bin", 600, 0, 0, FileAttributes.Normal);
        index.Add(0, "empty.txt", 0, 0, 0, FileAttributes.Normal);
        return index;
    }

    [TestMethod]
    public void AggregatesDescendantsExactlyOnceAndKeepsZeroSizeEntries()
    {
        var index = Example();
        var snapshot = DiskUsageSnapshot.Build(index, default);
        Assert.AreEqual(1000L, snapshot.Item(0).Bytes);
        Assert.AreEqual(400L, snapshot.Item(1).Bytes);
        Assert.AreEqual(300L, snapshot.Item(3).Bytes);
        Assert.AreEqual(4L, snapshot.Item(0).FileCount);
        Assert.AreEqual(0L, snapshot.Item(2).Bytes);
        CollectionAssert.AreEqual(new[] { 6, 1, 2, 7 }, snapshot.Children(0).Select(i => i.Id).ToArray());
        Assert.AreEqual(999L, index.Entry(1).Size, "Aggregation must not modify persisted index entries.");
        Assert.AreEqual(2, DiskUsageSnapshot.MapItems(snapshot.Children(0)).Count);
    }

    [TestMethod]
    public void FolderLookupHonorsPathBoundariesAndCase()
    {
        var snapshot = DiskUsageSnapshot.Build(Example(), default);
        Assert.AreEqual(3, snapshot.FindFolder(@"c:\PHOTOS\Nested\"));
        Assert.AreEqual(0, snapshot.FindFolder(@"C:\"));
        Assert.AreEqual(-1, snapshot.FindFolder(@"C:\Photos2"));
        Assert.AreEqual(-1, snapshot.FindFolder(@"D:\Photos"));
        Assert.AreEqual(-1, snapshot.FindFolder(@"C:\file.bin"));
        Assert.AreEqual(@"C:\Photos\Nested\hidden.jpg", snapshot.PathFor(5));
    }

    [TestMethod]
    public void DeepHierarchyUsesIterativeAggregationAndUntruncatedPaths()
    {
        var index = new VolumeIndex(@"C:\", 1);
        index.Add(-1, @"C:\", 0, 0, 0, FileAttributes.Directory);
        for (var i = 1; i <= 100_000; i++) index.Add(i - 1, "d", 0, 0, 0, FileAttributes.Directory);
        index.Add(100_000, "end", 42, 0, 0, FileAttributes.Normal);
        var snapshot = DiskUsageSnapshot.Build(index, default);
        Assert.AreEqual(42L, snapshot.Item(0).Bytes);
        Assert.AreEqual(1L, snapshot.Item(0).FileCount);
        Assert.AreEqual(100_000 * 2 + 6, snapshot.PathFor(100_001).Length);
    }

    [TestMethod]
    public void InvalidHierarchyAndOverflowFailExplicitly()
    {
        var broken = Example();
        broken.Entries[1].ParentIndex = 3;
        Assert.ThrowsException<InvalidDataException>(() => DiskUsageSnapshot.Build(broken, default));
        var overflow = Example();
        overflow.Entries[4].Size = long.MaxValue;
        Assert.ThrowsException<OverflowException>(() => DiskUsageSnapshot.Build(overflow, default));
        var empty = new VolumeIndex(@"C:\", 1);
        Assert.ThrowsException<InvalidDataException>(() => DiskUsageSnapshot.Build(empty, default));
    }

    [TestMethod]
    public void AggregationAndChildEnumerationHonorCancellation()
    {
        var token = new CancellationToken(true);
        Assert.ThrowsException<OperationCanceledException>(() => DiskUsageSnapshot.Build(Example(), token));
        var snapshot = DiskUsageSnapshot.Build(Example(), default);
        Assert.ThrowsException<OperationCanceledException>(() => snapshot.Children(0, token));
    }

    [TestMethod]
    public void GroupingPreservesTotalBytesAndFileCounts()
    {
        var items = Enumerable.Range(1, 1000).Select(i => new DiskUsageItem(i, $"File {i}", 1001 - i, 1, false)).ToArray();
        var map = DiskUsageSnapshot.MapItems(items);
        Assert.AreEqual(200, map.Count);
        Assert.AreEqual(items.Sum(i => i.Bytes), map.Sum(i => i.Bytes));
        Assert.AreEqual(1000L, map.Sum(i => i.FileCount));
        Assert.AreEqual(-1, map[^1].Id);
        Assert.AreEqual(801L, map[^1].FileCount);
    }

    [TestMethod]
    public void TreemapTilesAreProportionalBoundedAndNonOverlapping()
    {
        var random = new Random(499);
        foreach (var dimensions in new[] { (1200d, 600d), (300d, 900d), (1d, 1d), (800d, 800d) })
        {
            var weights = Enumerable.Range(0, 200).Select(_ => (long)random.Next(1, 1_000_000)).ToArray();
            CheckLayout(weights, dimensions.Item1, dimensions.Item2);
        }
        CheckLayout([1, 1, 1, 1], 800, 800);
        CheckLayout([long.MaxValue, long.MaxValue / 2, 10_000_000], 900, 500);
        CheckLayout([10, 0, -3, 90], 600, 300);
    }

    private static void CheckLayout(long[] weights, double width, double height)
    {
        var tiles = SquarifiedTreemap.Layout(weights, width, height);
        Assert.AreEqual(weights.Count(w => w > 0), tiles.Count);
        var total = weights.Where(w => w > 0).Sum(w => (double)w);
        Assert.AreEqual(width * height, tiles.Sum(t => t.Width * t.Height), width * height * 1e-9);
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            Assert.IsTrue(tile.X >= -1e-8 && tile.Y >= -1e-8 && tile.Width >= 0 && tile.Height >= 0);
            Assert.IsTrue(tile.X + tile.Width <= width + 1e-8 && tile.Y + tile.Height <= height + 1e-8);
            Assert.AreEqual(weights[tile.ItemIndex] / total, tile.Width * tile.Height / (width * height), 1e-9);
            for (var j = i + 1; j < tiles.Count; j++)
            {
                var other = tiles[j];
                var overlapW = Math.Min(tile.X + tile.Width, other.X + other.Width) - Math.Max(tile.X, other.X);
                var overlapH = Math.Min(tile.Y + tile.Height, other.Y + other.Height) - Math.Max(tile.Y, other.Y);
                Assert.IsTrue(overlapW <= 1e-8 || overlapH <= 1e-8, "Tiles overlap.");
            }
        }
    }

    [TestMethod]
    public void TreemapHandlesEmptyInvalidAndSingleItemInputs()
    {
        Assert.AreEqual(0, SquarifiedTreemap.Layout([], 100, 100).Count);
        Assert.AreEqual(0, SquarifiedTreemap.Layout([0, -1], 100, 100).Count);
        Assert.AreEqual(0, SquarifiedTreemap.Layout([1], 0, 100).Count);
        Assert.AreEqual(0, SquarifiedTreemap.Layout([1], double.NaN, 100).Count);
        Assert.AreEqual(0, SquarifiedTreemap.Layout([1], 100, double.PositiveInfinity).Count);
        var tile = SquarifiedTreemap.Layout([42], 100, 50).Single();
        Assert.AreEqual(100d, tile.Width, 1e-9);
        Assert.AreEqual(50d, tile.Height, 1e-9);
    }

    [TestMethod]
    public void EqualWeightsMakeSquareTilesInASquareViewport()
    {
        foreach (var tile in SquarifiedTreemap.Layout([1, 1, 1, 1], 800, 800))
        { Assert.AreEqual(400d, tile.Width, 1e-8); Assert.AreEqual(400d, tile.Height, 1e-8); }
    }

    [TestMethod]
    public async Task ViewModelSupportsInitialFolderDrillDownUpAndMissingFolder()
    {
        using var vm = new DiskUsageViewModel(() => [Example()]);
        await vm.LoadAsync(preferredPath: @"C:\Photos");
        Assert.AreEqual(@"C:\Photos", vm.CurrentPath);
        Assert.IsTrue(vm.CanGoUp);
        Assert.AreEqual(2, vm.Items.Count);
        await vm.NavigateAsync(3);
        Assert.AreEqual(@"C:\Photos\Nested", vm.CurrentPath);
        vm.Select(vm.Items.Single());
        StringAssert.Contains(vm.Details, "100%");
        await vm.UpAsync();
        Assert.AreEqual(@"C:\Photos", vm.CurrentPath);
        await vm.LoadAsync(preferredPath: @"C:\Missing");
        Assert.AreEqual(@"C:\", vm.CurrentPath);
        StringAssert.Contains(vm.Status, "not in the snapshot");
        Assert.IsFalse(vm.IsBusy);
    }

    [TestMethod]
    public async Task NoIndexAndInvalidIndexProduceActionableEmptyStates()
    {
        using var missing = new DiskUsageViewModel(() => []);
        await missing.LoadAsync();
        Assert.IsTrue(missing.IsEmpty);
        StringAssert.Contains(missing.Status, "Refresh");
        var broken = Example(); broken.Entries[2].ParentIndex = 2;
        using var invalid = new DiskUsageViewModel(() => [broken]);
        await invalid.LoadAsync();
        Assert.IsTrue(invalid.IsEmpty);
        Assert.IsFalse(invalid.IsBusy);
        StringAssert.Contains(invalid.Status, "Could not load");
    }

    [TestMethod]
    public async Task CanceledOrDisposedLoadCannotPublishResults()
    {
        DiskUsageViewModel? vm = null;
        vm = new DiskUsageViewModel(() => { vm!.Cancel(); return [Example()]; });
        using (vm)
        {
            await vm.LoadAsync();
            Assert.AreEqual(0, vm.Items.Count);
            StringAssert.Contains(vm.Status, "canceled");
            Assert.IsFalse(vm.IsBusy);
        }
        using var disposed = new DiskUsageViewModel(() => throw new AssertFailedException("Disposed model captured data."));
        disposed.Dispose();
        await disposed.LoadAsync();
        Assert.AreEqual(0, disposed.Items.Count);
    }

    [TestMethod]
    public async Task ReloadReplacesSnapshotAndDriveSelectionUsesItsOwnHierarchy()
    {
        var current = Example();
        var second = new VolumeIndex(@"D:\", 2);
        second.Add(-1, @"D:\", 0, 0, 0, FileAttributes.Directory);
        second.Add(0, "other.txt", 12, 0, 0, FileAttributes.Normal);
        using var vm = new DiskUsageViewModel(() => [current, second]);
        await vm.LoadAsync();
        current = Example();
        current.Add(0, "new.txt", 17, 0, 0, FileAttributes.Normal);
        Assert.IsFalse(vm.Items.Any(i => i.Name == "new.txt"));
        await vm.LoadAsync(@"C:\");
        Assert.IsTrue(vm.Items.Any(i => i.Name == "new.txt"));
        await vm.LoadAsync(@"D:\");
        Assert.AreEqual(@"D:\", vm.CurrentPath);
        Assert.AreEqual("other.txt", vm.Items.Single().Name);
        Assert.AreEqual(12L, vm.Items.Single().Bytes);
    }

    [TestMethod]
    public async Task SupersededLoadCannotReplaceANewerDrive()
    {
        var index = new VolumeIndex(@"C:\", 1);
        index.Add(-1, @"C:\", 0, 0, 0, FileAttributes.Directory);
        for (var i = 0; i < 100_000; i++) index.Add(0, "file", 1, 0, 0, FileAttributes.Normal);
        var second = new VolumeIndex(@"D:\", 2);
        second.Add(-1, @"D:\", 0, 0, 0, FileAttributes.Directory);
        using var vm = new DiskUsageViewModel(() => [index, second]);
        var older = vm.LoadAsync(@"C:\");
        var newer = vm.LoadAsync(@"D:\");
        await Task.WhenAll(older, newer);
        Assert.AreEqual(@"D:\", vm.CurrentPath);
        Assert.IsFalse(vm.IsBusy);
        Assert.AreEqual(0, vm.Items.Count);
    }

    [TestMethod]
    public async Task BackAndForwardFollowVisitedFoldersRatherThanParentFolders()
    {
        using var vm = new DiskUsageViewModel(() => [Example()]);
        await vm.LoadAsync(preferredPath: @"C:\Photos");
        Assert.IsFalse(vm.CanGoBack);
        await vm.NavigateAsync(3);
        await vm.UpAsync(); // Photos, Nested, Photos — Back should return to Nested.
        await vm.BackAsync();
        Assert.AreEqual(@"C:\Photos\Nested", vm.CurrentPath);
        Assert.IsTrue(vm.CanGoForward);
        await vm.BackAsync();
        Assert.AreEqual(@"C:\Photos", vm.CurrentPath);
        Assert.IsFalse(vm.CanGoBack);
        await vm.BackAsync(); // At the beginning: no change.
        await vm.ForwardAsync();
        Assert.AreEqual(@"C:\Photos\Nested", vm.CurrentPath);
        await vm.ForwardAsync();
        Assert.AreEqual(@"C:\Photos", vm.CurrentPath);
        Assert.IsFalse(vm.CanGoForward);
    }

    [TestMethod]
    public async Task NewNavigationTruncatesForwardHistoryButReloadAndSameFolderDoNot()
    {
        using var vm = new DiskUsageViewModel(() => [Example()]);
        await vm.LoadAsync();
        await vm.NavigateAsync(1);
        await vm.NavigateAsync(3);
        await vm.BackAsync();
        await vm.NavigateAsync(1);
        Assert.IsTrue(vm.CanGoForward);
        await vm.LoadAsync(@"C:\", vm.CurrentPath);
        Assert.IsTrue(vm.CanGoForward);
        await vm.NavigateAsync(2);
        Assert.IsFalse(vm.CanGoForward);
        await vm.BackAsync();
        Assert.AreEqual(@"C:\Photos", vm.CurrentPath);
        await vm.BackAsync();
        Assert.AreEqual(@"C:\", vm.CurrentPath);
        Assert.IsFalse(vm.CanGoBack);
    }

    [TestMethod]
    public async Task HistoryCrossesDrivesAndFailedOrCanceledLoadsPreserveCurrentView()
    {
        var first = Example();
        var second = new VolumeIndex(@"D:\", 2);
        second.Add(-1, @"D:\", 0, 0, 0, FileAttributes.Directory);
        var includeFirst = true;
        var cancel = false;
        DiskUsageViewModel? model = null;
        using var vm = model = new DiskUsageViewModel(() =>
        {
            if (cancel) model!.Cancel();
            return includeFirst ? [first, second] : [second];
        });
        await vm.LoadAsync(preferredPath: @"C:\Photos");
        await vm.LoadAsync(@"D:\");
        Assert.IsTrue(vm.CanGoBack);
        includeFirst = false;
        await vm.BackAsync();
        Assert.AreEqual(@"D:\", vm.CurrentPath);
        Assert.IsTrue(vm.CanGoBack);
        Assert.IsFalse(vm.CanGoForward);
        StringAssert.Contains(vm.Status, "no longer in the index");
        includeFirst = true;
        cancel = true;
        await vm.BackAsync();
        Assert.AreEqual(@"D:\", vm.CurrentPath);
        Assert.IsTrue(vm.CanGoBack);
        Assert.IsFalse(vm.CanGoForward);
        cancel = false;
        await vm.BackAsync();
        Assert.AreEqual(@"C:\Photos", vm.CurrentPath);
        Assert.AreEqual(@"C:\", vm.SelectedRoot);
        Assert.IsFalse(vm.CanGoBack);
        await vm.ForwardAsync();
        Assert.AreEqual(@"D:\", vm.CurrentPath);
    }

    [TestMethod]
    public async Task MissingFolderInReloadedHistoryDoesNotMoveHistoryPosition()
    {
        var current = Example();
        using var vm = new DiskUsageViewModel(() => [current]);
        await vm.LoadAsync();
        await vm.NavigateAsync(1);
        await vm.BackAsync();
        current = new VolumeIndex(@"C:\", 1);
        current.Add(-1, @"C:\", 0, 0, 0, FileAttributes.Directory);
        await vm.LoadAsync(@"C:\");
        await vm.ForwardAsync();
        Assert.AreEqual(@"C:\", vm.CurrentPath);
        Assert.IsTrue(vm.CanGoForward);
        Assert.IsFalse(vm.CanGoBack);
        StringAssert.Contains(vm.Status, "no longer in the index");
    }

    [TestMethod]
    public async Task DisplaySharesAndGroupedTilesPreserveTotalsAndMatchListColors()
    {
        var index = Example();
        for (var i = 0; i < 50; i++) index.Add(0, $"file-{i}.txt", 10, 0, 0, FileAttributes.Normal);
        using var vm = new DiskUsageViewModel(() => [index]);
        await vm.LoadAsync();
        Assert.AreEqual(24, vm.MapItems.Count);
        Assert.AreEqual(100d, vm.MapItems.Sum(i => i.Share), 1e-8);
        Assert.AreEqual(100d, vm.Items.Sum(i => i.Share), 1e-8);
        Assert.AreEqual(vm.Items.Sum(i => i.Bytes), vm.MapItems.Sum(i => i.Bytes));
        foreach (var tile in vm.MapItems.Where(i => i.Id >= 0))
            Assert.AreEqual(vm.Items.Single(i => i.Id == tile.Id).ColorHex, tile.ColorHex);
        await vm.NavigateAsync(3);
        Assert.AreEqual(100d, vm.Items.Single().Share);
        CollectionAssert.AreEqual(new[] { @"C:\", "Photos", "Nested" }, vm.Breadcrumbs.Select(c => c.Name).ToArray());
    }
}
