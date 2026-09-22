using System.Windows;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class NestedDiskMapTests
{
    [TestMethod]
    public void FilledMapsPreserveByteRatiosStayInBoundsAndNeverOverlap()
    {
        foreach (var weights in new long[][] { [9400, 300, 200, 100], [44, 30, 18, 10, 6, 2], [1, 1, 1, 1], [long.MaxValue - 1, 1] })
        {
            var (boxes, density) = NestedDiskMap.Pack(weights, cornerGroup: true);
            Assert.AreEqual(weights.Length, boxes.Length);
            var total = weights.Sum(weight => (double)weight);
            for (var i = 0; i < boxes.Length; i++)
            {
                Assert.IsTrue(new Rect(0, 0, 1, 1).Contains(boxes[i]));
                Assert.AreEqual(weights[i] / total, boxes[i].Width * boxes[i].Height / density, 1e-10);
                for (var j = i + 1; j < boxes.Length; j++)
                {
                    var intersection = Rect.Intersect(boxes[i], boxes[j]);
                    Assert.IsTrue(intersection.IsEmpty || intersection.Width * intersection.Height < 1e-12);
                }
            }
            Assert.AreEqual(1d, boxes.Sum(box => box.Width * box.Height), 1e-12, "The map must not leave packing gaps.");
        }
    }

    [TestMethod]
    public void CornerContainsActualColorsAndStableGeometryAtEveryZoomLevel()
    {
        var items = NestedDiskMap.Colorize(Enumerable.Range(1, 12)
            .Select(i => new DiskUsageItem(i, $"Item {i}", i == 1 ? 10000 : i * 4, 1, false)).ToArray());
        var map = NestedDiskMap.Create(items);
        var group = map.Tiles.Single(tile => tile.IsGroup);
        Assert.IsNotNull(group.Inside);
        foreach (var tile in group.Inside.Tiles.Where(tile => !tile.IsGroup))
            Assert.AreEqual(items.Single(item => item.Id == tile.Item.Id).ColorHex, tile.Item.ColorHex);
        var rebuilt = NestedDiskMap.Create(items).Tiles.Single(tile => tile.IsGroup).Inside!;
        CollectionAssert.AreEqual(group.Inside.Tiles.Select(tile => tile.Bounds).ToArray(), rebuilt.Tiles.Select(tile => tile.Bounds).ToArray());
        CollectionAssert.AreEqual(group.Inside.Tiles.Select(tile => tile.Item.ColorHex).ToArray(), rebuilt.Tiles.Select(tile => tile.Item.ColorHex).ToArray());
    }

    [TestMethod]
    public void DominantFoldersStayAtOneReadableLevelWithoutNestedPreviews()
    {
        var children = NestedDiskMap.Colorize([
            new DiskUsageItem(10, "Local", 6500, 10, false), new DiskUsageItem(11, "Roaming", 2900, 10, false)]);
        var map = NestedDiskMap.Create(NestedDiskMap.Colorize([
            new DiskUsageItem(1, "AppData", 9400, 20, true), new DiskUsageItem(2, "Other", 600, 1, false)]), _ => children);
        var dominant = map.Tiles.Single(tile => tile.Item.Id == 1);
        Assert.IsNull(dominant.Inside);
        Assert.AreEqual(.94, dominant.Area, 1e-12);
        Assert.IsNotNull(dominant.Cutout);
        var other = map.Tiles.Single(tile => tile.Item.Id == 2);
        Assert.AreEqual(other.Bounds.Width, other.Bounds.Height, 1e-12, "A dominant folder's neighbor should be compact instead of a full-height strip.");
        Assert.AreEqual(1d, map.Tiles.Sum(tile => tile.Area), 1e-12);
    }

    [TestMethod]
    public void DominantCornerPreservesAreasAndProducesCompactSmallTiles()
    {
        var map = NestedDiskMap.Create(NestedDiskMap.Colorize([
            new DiskUsageItem(1, "AppData", 933, 1, true), new DiskUsageItem(2, "Splice", 36, 1, true),
            new DiskUsageItem(3, ".nuget", 13, 1, true), new DiskUsageItem(4, ".codex", 11, 1, true),
            new DiskUsageItem(5, "Cache", 7, 1, false)]));
        var leaves = new List<(DiskUsageItem Item, Rect Box, Rect? Hole)>();
        void Visit(NestedDiskMap scene, Rect parent)
        {
            foreach (var tile in scene.Tiles)
            {
                Rect Transform(Rect rect) => new(parent.X + rect.X * parent.Width, parent.Y + rect.Y * parent.Height,
                    rect.Width * parent.Width, rect.Height * parent.Height);
                var box = Transform(tile.Bounds);
                if (tile.Inside is { } inside) Visit(inside, box);
                else leaves.Add((tile.Item, box, tile.Cutout is Rect hole ? Transform(hole) : null));
            }
        }
        Visit(map, new Rect(0, 0, 1, 1));
        Assert.AreEqual(5, leaves.Count);
        foreach (var leaf in leaves)
        {
            var area = leaf.Box.Width * leaf.Box.Height - (leaf.Hole is Rect hole ? hole.Width * hole.Height : 0);
            Assert.AreEqual(leaf.Item.Bytes / 1000d, area, 1e-10);
            if (leaf.Item.Id != 1)
                Assert.IsTrue(Math.Max(leaf.Box.Width / leaf.Box.Height, leaf.Box.Height / leaf.Box.Width) < 4,
                    $"{leaf.Item.Name} should not become a skinny ribbon.");
        }
    }

    [TestMethod]
    public void SkewedTinyFileGroupsNeverPaintOverTheirNeighbors()
    {
        var items = NestedDiskMap.Colorize(Enumerable.Range(1, 200)
            .Select(i => new DiskUsageItem(i, $"File {i}", i <= 50 ? 1000 : 1, 1, false)).ToArray());
        var map = NestedDiskMap.Create(items);
        var leaves = new List<(DiskUsageItem Item, System.Windows.Media.Geometry Shape)>();
        void Visit(NestedDiskMap scene, Rect parent)
        {
            foreach (var tile in scene.Tiles)
            {
                Rect Transform(Rect rect) => new(parent.X + rect.X * parent.Width, parent.Y + rect.Y * parent.Height,
                    rect.Width * parent.Width, rect.Height * parent.Height);
                var box = Transform(tile.Bounds);
                if (tile.IsGroup)
                {
                    Assert.IsNull(tile.Cutout, "A group cannot paint a rectangular child map across a cutout.");
                    Visit(tile.Inside!, box);
                    continue;
                }
                System.Windows.Media.Geometry shape = new System.Windows.Media.RectangleGeometry(box);
                if (tile.Cutout is Rect hole)
                    shape = new System.Windows.Media.CombinedGeometry(System.Windows.Media.GeometryCombineMode.Exclude,
                        shape, new System.Windows.Media.RectangleGeometry(Transform(hole)));
                leaves.Add((tile.Item, shape));
            }
        }
        Visit(map, new Rect(0, 0, 1, 1));
        Assert.AreEqual(items.Count, leaves.Count);
        var total = items.Sum(item => (double)item.Bytes);
        for (var i = 0; i < leaves.Count; i++)
        {
            Assert.AreEqual(leaves[i].Item.Bytes / total, leaves[i].Shape.GetArea(), 1e-9);
            for (var j = i + 1; j < leaves.Count; j++)
                Assert.IsTrue(System.Windows.Media.Geometry.Combine(leaves[i].Shape, leaves[j].Shape,
                    System.Windows.Media.GeometryCombineMode.Intersect, null).GetArea() < 1e-10,
                    $"{leaves[i].Item.Name} overlaps {leaves[j].Item.Name}.");
        }
    }

    [TestMethod]
    public void LargeEqualSizedFoldersUseBalancedGroupsAndCanceledBuildsStop()
    {
        var items = Enumerable.Range(1, 10000).Select(i => new DiskUsageItem(i, $"Item {i}", 1, 1, false)).ToArray();
        var map = NestedDiskMap.Create(items);
        Assert.AreEqual(4, map.Tiles.Count);
        Assert.IsTrue(map.Tiles.All(tile => tile.Inside!.Items.Count == 2500));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsException<OperationCanceledException>(() => NestedDiskMap.Create(items, token: cancellation.Token));
    }
}
