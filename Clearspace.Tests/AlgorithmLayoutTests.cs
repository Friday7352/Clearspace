using System.Diagnostics;
using System.IO;
using System.Windows;
using Clearspace.Controls;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class AlgorithmLayoutTests
{
    public TestContext TestContext { get; set; } = null!;

    [DataTestMethod]
    [DataRow(1000, 1.0)]
    [DataRow(30_000, 3.89)] // Exercise the parallel layout build, grouping and subtree offsets.
    public void FlatLayoutPreservesEveryFileAndProportionalNonOverlappingAreas(int count, double aspect)
    {
        var index = new VolumeIndex(@"Z:\layout-test\", 1);
        index.Add(-1, index.Root, 0, 0, 0, FileAttributes.Directory);
        var first = index.Add(0, "first", 0, 0, 0, FileAttributes.Directory);
        var second = index.Add(0, "second", 0, 0, 0, FileAttributes.Directory);
        for (var i = 0; i < count; i++) index.Add(i % 2 == 0 ? first : second, $"file{i:D6}", i % 101 + 1, 0, 0, FileAttributes.Normal);
        index.Add(0, "zero", 0, 0, 0, FileAttributes.Normal);
        var snapshot = DiskUsageSnapshot.Build(index, default);
        var watch = Stopwatch.StartNew();
        var flat = FlatTreemapLayout.Build(snapshot, aspect, default);
        var elapsed = watch.Elapsed.TotalMilliseconds;
        var seen = new HashSet<int>();
        var pending = new Stack<(int Entry, Rect Bounds)>();
        pending.Push((0, new Rect(0, 0, 1, 1)));
        while (pending.TryPop(out var parent))
        {
            var siblings = new List<Rect>();
            var area = 0d;
            for (var entry = parent.Entry + 1; entry < flat.End(parent.Entry); entry = flat.End(entry))
            {
                Assert.IsTrue(flat.End(entry) > entry && flat.End(entry) <= flat.End(parent.Entry));
                flat.Edges(entry, out var left, out var top, out var right, out var bottom);
                Assert.IsTrue(float.IsFinite(left) && float.IsFinite(top) && float.IsFinite(right) && float.IsFinite(bottom));
                Assert.IsTrue(left >= 0 && top >= 0 && right <= 1 && bottom <= 1 && right >= left && bottom >= top);
                var relative = new Rect(left, top, right - left, bottom - top);
                foreach (var other in siblings)
                {
                    var overlap = Rect.Intersect(other, relative);
                    Assert.IsTrue(overlap.IsEmpty || overlap.Width * overlap.Height < 1e-10);
                }
                siblings.Add(relative);
                area += relative.Width * relative.Height;
                var bounds = new Rect(parent.Bounds.X + left * parent.Bounds.Width, parent.Bounds.Y + top * parent.Bounds.Height,
                    relative.Width * parent.Bounds.Width, relative.Height * parent.Bounds.Height);
                if (!flat.IsGroup(entry))
                {
                    Assert.IsTrue(seen.Add(flat.Id(entry)), "Each source item must occur exactly once.");
                    Assert.AreEqual(snapshot.BytesOf(flat.Id(entry)) / (double)snapshot.BytesOf(0), bounds.Width * bounds.Height, 1e-6);
                }
                if (flat.IsContainer(entry)) pending.Push((entry, bounds));
            }
            Assert.AreEqual(1d, area, 1e-6, "Children must fill their parent's area.");
        }
        Assert.AreEqual(count + 2, seen.Count);
        Assert.AreEqual(flat.Count, flat.End(0));
        TestContext.WriteLine($"Flat layout: {count:N0} files, {flat.Count:N0} tiles, {elapsed:F2} ms, {flat.ApproximateBytes:N0} retained array bytes.");
    }

    [TestMethod]
    public void FlatLayoutIsDeterministicAndHonorsCancellation()
    {
        var index = new VolumeIndex(@"Z:\", 1);
        index.Add(-1, index.Root, 0, 0, 0, FileAttributes.Directory);
        for (var i = 0; i < 500; i++) index.Add(0, $"same-size-{i}", 10, 0, 0, FileAttributes.Normal);
        var source = DiskUsageSnapshot.Build(index, default);
        var a = FlatTreemapLayout.Build(source, 1, default);
        var b = FlatTreemapLayout.Build(source, 1, default);
        Assert.AreEqual(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            a.Edges(i, out var l, out var t, out var r, out var bottom);
            b.Edges(i, out var bl, out var bt, out var br, out var bb);
            Assert.AreEqual((a.Id(i), a.End(i), l, t, r, bottom, a.Spread(i)), (b.Id(i), b.End(i), bl, bt, br, bb, b.Spread(i)));
        }
        Assert.ThrowsException<OperationCanceledException>(() => FlatTreemapLayout.Build(source, 1, new CancellationToken(true)));
    }

    [TestMethod]
    public void StableLayoutKeepsOrderWhenSizesCrossAndHandlesInsertionsAndRemovals()
    {
        var layout = new StableTreemap();
        var before = layout.Arrange([("a", 501), ("b", 499)], 0, 0, 1000, 1000);
        var after = layout.Arrange([("b", 501), ("a", 499)], 0, 0, 1000, 1000);
        Assert.AreEqual(1, layout.Fresh, "A small size crossover must not reorder the map.");
        CollectionAssert.AreEqual(before.Select(t => t.Key).ToArray(), after.Select(t => t.Key).ToArray());
        CheckStable(after, [("a", 499), ("b", 501)]);
        var tiny = layout.Arrange([("a", 499), ("b", 501), ("tiny", 1)], 0, 0, 1000, 1000);
        Assert.AreEqual(1, layout.Fresh);
        CheckStable(tiny, [("a", 499), ("b", 501), ("tiny", 1)]);
        var removed = layout.Arrange([("a", 499), ("tiny", 1)], 0, 0, 1000, 1000);
        CheckStable(removed, [("a", 499), ("tiny", 1)]);
        Assert.IsFalse(removed.Any(t => t.Key == "b"));
    }

    private static void CheckStable(List<StableTile> tiles, (string Key, long Bytes)[] weights)
    {
        var total = weights.Sum(item => (double)item.Bytes);
        Assert.AreEqual(1_000_000d, tiles.Sum(tile => tile.Width * tile.Height), 1e-6);
        foreach (var tile in tiles)
        {
            Assert.AreEqual(weights.Single(item => item.Key == tile.Key).Bytes / total, tile.Width * tile.Height / 1_000_000, 1e-9);
            Assert.IsTrue(tile.X >= -1e-8 && tile.Y >= -1e-8 && tile.X + tile.Width <= 1000 + 1e-8 && tile.Y + tile.Height <= 1000 + 1e-8);
            foreach (var other in tiles.Where(other => string.CompareOrdinal(other.Key, tile.Key) > 0))
            {
                var overlap = Rect.Intersect(new(tile.X, tile.Y, tile.Width, tile.Height), new(other.X, other.Y, other.Width, other.Height));
                Assert.IsTrue(overlap.IsEmpty || overlap.Width * overlap.Height < 1e-6);
            }
        }
    }

    [TestMethod]
    public void SnapshotCacheReusesUnchangedSourceButRejectsReplacementAndChangedExclusions()
    {
        var root = $@"Z:\cache-test-{Guid.NewGuid():N}\";
        VolumeIndex Source(long size)
        {
            var index = new VolumeIndex(root, 1);
            index.Add(-1, root, 0, 0, 0, FileAttributes.Directory);
            index.Add(0, "file.txt", size, 0, 0, FileAttributes.Normal);
            return index;
        }
        try
        {
            var original = Source(10);
            var first = DiskUsageSnapshotCache.Get(original, default, []);
            Assert.AreSame(first, DiskUsageSnapshotCache.Get(original, default, []));
            var replacement = Source(20);
            var fresh = DiskUsageSnapshotCache.Get(replacement, default, []);
            Assert.AreNotSame(first, fresh);
            Assert.AreEqual(20L, fresh.Item(0).Bytes);
            var excluded = DiskUsageSnapshotCache.Get(replacement, default, [Path.Combine(root, "file.txt")]);
            Assert.AreEqual(0L, excluded.Item(0).Bytes);
            Assert.AreEqual(20L, DiskUsageSnapshotCache.Rebuild(replacement, default, []).Item(0).Bytes);
        }
        finally { DiskUsageSnapshotCache.Invalidate(root); }
    }
}
