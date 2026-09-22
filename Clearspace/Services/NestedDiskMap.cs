using System.Windows;

namespace Clearspace.Services;

internal sealed record DiskMapTile(DiskUsageItem Item, Rect Bounds, NestedDiskMap? Inside, bool IsGroup, Rect? Cutout = null)
{
    public double Area => Bounds.Width * Bounds.Height - (Cutout is Rect hole ? hole.Width * hole.Height : 0);
}

// Geometry is stored in unit-square coordinates, independent of pixels and zoom.
// A child map is the exact same object before and after entering its square.
internal sealed class NestedDiskMap
{
    internal static readonly string[] Palette = ["#537B9B", "#4C8279", "#94704A", "#7A6697", "#A06465", "#75804D"];
    public IReadOnlyList<DiskUsageItem> Items { get; }
    public IReadOnlyList<DiskMapTile> Tiles { get; }
    public double Density { get; }

    private NestedDiskMap(IReadOnlyList<DiskUsageItem> items, IReadOnlyList<DiskMapTile> tiles, double density)
        => (Items, Tiles, Density) = (items, tiles, density);

    internal static IReadOnlyList<DiskUsageItem> Colorize(IReadOnlyList<DiskUsageItem> items)
    {
        var total = items.Sum(item => (double)item.Bytes);
        return items.Select((item, rank) => item with
        { Share = total == 0 ? 0 : item.Bytes * 100 / total, ColorHex = Palette[rank % Palette.Length] }).ToArray();
    }

    public static NestedDiskMap Create(IReadOnlyList<DiskUsageItem> items,
        Func<int, IReadOnlyList<DiskUsageItem>>? children = null, CancellationToken token = default)
    {
        var budget = 240;
        return Build(items, children, token, 0, ref budget);
    }

    private static NestedDiskMap Build(IReadOnlyList<DiskUsageItem> items,
        Func<int, IReadOnlyList<DiskUsageItem>>? children, CancellationToken token, int depth, ref int budget)
    {
        token.ThrowIfCancellationRequested();
        var positive = items.Where(item => item.Bytes > 0).OrderByDescending(item => item.Bytes).ToArray();
        var entries = new List<(DiskUsageItem Item, NestedDiskMap? Inside, bool Group)>();
        var total = positive.Sum(item => (double)item.Bytes);
        // Keep the small tail together even before labels run out of pixel space.
        var named = Math.Min(12, positive.TakeWhile(item => item.Bytes / total >= .025).Count());
        if (named == 0 && positive.Length > 12)
        {
            // Equal-sized directories split into balanced groups rather than a long chain of tails.
            var chunk = (positive.Length + 3) / 4;
            foreach (var members in positive.Chunk(chunk))
                entries.Add(Group(members, -1 - entries.Count, children, token, depth, ref budget));
            return Arrange(items, entries, token);
        }
        if (positive.Length <= 12 && named == 0) named = positive.Length;
        named = Math.Max(Math.Min(positive.Length, 1), named);
        if (positive.Length - named == 1) named++;
        for (var i = 0; i < named; i++)
        {
            var item = positive[i];
            NestedDiskMap? inside = null;
            entries.Add((item, inside, false));
        }
        if (named < positive.Length)
        {
            var tail = positive.Skip(named).ToArray();
            // Bound preview work for enormous folders; entering a group builds its next level on demand.
            entries.Add(Group(tail, -1, children, token, depth, ref budget));
        }
        return Arrange(items, entries, token);
    }

    private static (DiskUsageItem, NestedDiskMap?, bool) Group(DiskUsageItem[] members, int id,
        Func<int, IReadOnlyList<DiskUsageItem>>? children, CancellationToken token, int depth, ref int budget)
    {
        NestedDiskMap? inside = null;
        if (depth < 3 && budget > 0)
        {
            budget--;
            inside = Build(members, children, token, depth + 1, ref budget);
        }
        var group = new DiskUsageItem(id, $"{members.Length:N0} smaller items", members.Sum(item => item.Bytes), members.Sum(item => item.FileCount), false)
        { Share = members.Sum(item => item.Share), ColorHex = "#252421" };
        return (group, inside ?? new NestedDiskMap(members, [], 0), true);
    }

    private static NestedDiskMap Arrange(IReadOnlyList<DiskUsageItem> items,
        List<(DiskUsageItem Item, NestedDiskMap? Inside, bool Group)> entries, CancellationToken token,
        double width = 1, double height = 1)
    {
        token.ThrowIfCancellationRequested();
        var weights = entries.Select(entry => entry.Item.Bytes).ToArray();
        var rectangles = new Rect[weights.Length];
        Rect? cutout = null;
        var total = weights.Sum(weight => (double)weight);
        var remainder = weights.Skip(1).Sum(weight => (double)weight);
        var cornerSide = total > 0 ? Math.Sqrt(width * height * remainder / total) : 0;
        // A dominant item occupies the filled L around a square corner. Its visible
        // area is still exact, while smaller neighbors no longer become a ribbon.
        if (weights.Length > 1 && !entries[0].Group && weights[0] / total >= .8 && cornerSide <= Math.Min(width, height))
        {
            rectangles[0] = new Rect(0, 0, 1, 1);
            cutout = new Rect(1 - cornerSide / width, 1 - cornerSide / height, cornerSide / width, cornerSide / height);
            foreach (var tile in SquarifiedTreemap.Layout(weights.Skip(1).ToArray(), cornerSide, cornerSide))
                rectangles[tile.ItemIndex + 1] = new Rect((width - cornerSide + tile.X) / width,
                    (height - cornerSide + tile.Y) / height, tile.Width / width, tile.Height / height);
        }
        else
            foreach (var tile in SquarifiedTreemap.Layout(weights, width, height))
                rectangles[tile.ItemIndex] = new Rect(tile.X / width, tile.Y / height, tile.Width / width, tile.Height / height);

        var tiles = new List<DiskMapTile>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var rect = rectangles[i];
            var inside = entry.Inside;
            // Layout grouped members using the actual containing aspect ratio,
            // rather than stretching a square miniature into a narrow rectangle.
            if (inside is { Tiles.Count: > 0 })
                inside = Arrange(inside.Items, inside.Tiles.Select(tile => (tile.Item, tile.Inside, tile.IsGroup)).ToList(),
                    token, rect.Width * width, rect.Height * height);
            tiles.Add(new DiskMapTile(entry.Item, rect, inside, entry.Group, i == 0 ? cutout : null));
        }
        return new NestedDiskMap(items, tiles, entries.Count == 0 ? 0 : 1);
    }

    internal NestedDiskMap Fit(double width, double height)
        => Arrange(Items, Tiles.Select(tile => (tile.Item, tile.Inside, tile.IsGroup)).ToList(), default, width, height);

    // Fill every level; zooming crops this fixed geometry rather than repacking it.
    internal static (Rect[] Rectangles, double Density) Pack(IReadOnlyList<long> weights, bool cornerGroup, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var boxes = new Rect[weights.Count];
        foreach (var tile in SquarifiedTreemap.Layout(weights, 1, 1))
            boxes[tile.ItemIndex] = new Rect(tile.X, tile.Y, tile.Width, tile.Height);
        return (boxes, weights.Count == 0 ? 0 : 1);
    }
}
