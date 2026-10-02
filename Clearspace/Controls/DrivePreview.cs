using System.Windows.Media;
using Clearspace.Services;

namespace Clearspace.Controls;

// NEW (round 48): a picture of another drive's folders and files, for the drives shown beside the current
// one when the map is zoomed all the way out. It is the same squarified layout the map uses, a few levels
// deep and with a fixed number of tiles, in coordinates from 0 to 1 across the drive's files - small
// enough to keep for every drive (a few hundred kilobytes each) once the snapshot it came from is gone.
internal sealed record DrivePreview(long Bytes, PreviewTile[] Tiles, PreviewLabel[] Labels);

/// <summary>One block, in the unit square of the drive's files. Parents come before their contents.</summary>
internal readonly record struct PreviewTile(float X, float Y, float W, float H, uint Color);

/// <summary>A named item directly on the drive, for its caption, hover card and click.</summary>
internal readonly record struct PreviewLabel(string Name, long Bytes, bool IsFolder, float X, float Y, float W, float H, uint Color);

internal static class DrivePreviewBuilder
{
    private const int TileBudget = 12_000;
    // CHANGED (round 49): no "smaller items" group. Grey blocks inside folders read as holes in a picture
    // this small, so every child is laid out (up to this many, largest first); the ones too small to see
    // are simply left out and their parent's colour shows through where they would be.
    private const int ChildLimit = 4_000;
    private const int MaxDepth = 6;
    private const double ExpandSide = .018;    // a folder at least this big on both sides (of the unit height) shows its contents
    private const double MinSide = .0015;
    private const int LabelCount = 40;

    /// <summary>Lays out a drive's snapshot at the given width-to-height ratio.</summary>
    public static DrivePreview Build(DiskUsageSnapshot snapshot, double aspect, CancellationToken token)
    {
        var palette = DiskUsagePalette.Branches.Select(hex => Pack((Color)ColorConverter.ConvertFromString(hex))).ToArray();
        var group = Pack((Color)ColorConverter.ConvertFromString(DiskUsagePalette.GroupColor));
        var tiles = new List<(double X, double Y, double W, double H, uint Color, int Depth, int Id, bool Folder)>(TileBudget);
        var labels = new List<PreviewLabel>();
        // Folders open largest first, so the tiles go where the eye goes - the same rule as the 3D view.
        var queue = new PriorityQueue<int, double>();
        Expand(0, 0, 0, aspect, 1, 0, 0);
        while (queue.TryDequeue(out var index, out _) && tiles.Count < TileBudget)
        {
            token.ThrowIfCancellationRequested();
            var tile = tiles[index];
            var inset = Math.Min(tile.W, tile.H) * .04;
            Expand(tile.Id, tile.X + inset, tile.Y + inset, tile.W - inset * 2, tile.H - inset * 2, tile.Depth + 1, tile.Color);
        }

        var result = new PreviewTile[tiles.Count];
        for (var i = 0; i < tiles.Count; i++)
        {
            var t = tiles[i];
            result[i] = new PreviewTile((float)(t.X / aspect), (float)t.Y, (float)(t.W / aspect), (float)t.H, t.Color);
        }
        return new DrivePreview(snapshot.BytesOf(0), result, [.. labels]);

        void Expand(int id, double x, double y, double w, double h, int depth, uint parentColor)
        {
            if (w < MinSide || h < MinSide) return;
            var ids = new List<int>();
            snapshot.ChildIds(id, ids);
            var sorted = ids.Where(child => snapshot.BytesOf(child) > 0).OrderByDescending(snapshot.BytesOf).ToArray();
            if (sorted.Length == 0) return;
            var shown = sorted.Take(ChildLimit).ToArray();
            var rest = sorted.Skip(ChildLimit).Sum(snapshot.BytesOf);
            // The rest still take their share of the space, as an empty area in the parent's colour.
            var weights = shown.Select(snapshot.BytesOf).Append(rest).Where(bytes => bytes > 0).ToArray();
            foreach (var tile in SquarifiedTreemap.Layout(weights, w, h))
            {
                var isRest = tile.ItemIndex >= shown.Length;
                if (isRest) continue;
                var child = isRest ? -1 : shown[tile.ItemIndex];
                var gap = Math.Min(Math.Min(tile.Width, tile.Height) * .06, .004);
                double tx = x + tile.X + gap / 2, ty = y + tile.Y + gap / 2, tw = tile.Width - gap, th = tile.Height - gap;
                if (tw < MinSide || th < MinSide) continue;
                var folder = !isRest && snapshot.IsFolderEntry(child);
                // Each item on the drive takes the hue its rank gives it in the list; everything inside it
                // is a shade of that hue, varied per name so neighbours stay apart.
                var color = isRest ? group
                    : depth == 0 ? palette[tile.ItemIndex % palette.Length]
                    : Shade(parentColor, (1 - .06 * Math.Min(depth, 4)) * (.84 + .22 * Spread(snapshot.NameOf(child))));
                tiles.Add((tx, ty, tw, th, color, depth, child, folder));
                if (depth == 0 && !isRest && labels.Count < LabelCount)
                    labels.Add(new PreviewLabel(snapshot.NameOf(child).ToString(), snapshot.BytesOf(child), folder,
                        (float)(tx / aspect), (float)ty, (float)(tw / aspect), (float)th, color));
                if (folder && depth + 1 < MaxDepth && tw >= ExpandSide && th >= ExpandSide)
                    queue.Enqueue(tiles.Count - 1, -(tw * th));
                if (tiles.Count >= TileBudget) return;
            }
        }
    }

    private static uint Pack(Color color) => (uint)color.R << 16 | (uint)color.G << 8 | color.B;

    private static uint Shade(uint color, double factor)
    {
        var r = (uint)Math.Clamp((color >> 16 & 255) * factor, 0, 255);
        var g = (uint)Math.Clamp((color >> 8 & 255) * factor, 0, 255);
        var b = (uint)Math.Clamp((color & 255) * factor, 0, 255);
        return r << 16 | g << 8 | b;
    }

    private static double Spread(ReadOnlySpan<char> name)
    {
        var hash = 2166136261u;
        foreach (var character in name) hash = (hash ^ character) * 16777619u;
        return ((hash >> 8) & 1023) / 1023d;
    }
}
