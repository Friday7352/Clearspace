// Clearspace | A treemap that holds still while its sizes change.
//
// NEW (round 60): the memory view updates every second. A squarified treemap laid out afresh each time
// reshuffles whenever two items trade places in size or a row breaks differently - tiles jump across the
// view once a second. This one lays out the way SquarifiedTreemap does, then keeps that arrangement: which
// items share a row, in what order, and which way each row runs. Later sizes only change how much of the
// room each row and item takes, so every tile grows and shrinks where it is. It lays out afresh only when
// it has to - a sizeable item appears, or a tile has become too long and thin to read. Small newcomers
// join the last row, among the smallest items; an item that goes away just leaves its row, and the rest of
// the row closes up over its room.

namespace Clearspace.Services;

internal readonly record struct StableTile(string Key, double X, double Y, double Width, double Height);

internal sealed class StableTreemap
{
    private const double Noticeable = .005;   // an item at least this share of the whole changes the arrangement
    private readonly List<(bool Vertical, List<string> Keys)> _rows = [];
    private double _freshWorst = 1;

    /// <summary>How many times the arrangement was made afresh (for diagnostics).</summary>
    public int Fresh { get; private set; }

    public List<StableTile> Arrange(IReadOnlyList<(string Key, long Bytes)> items, double x, double y, double width, double height)
    {
        var result = new List<StableTile>(items.Count);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return result;
        var weights = new Dictionary<string, double>(items.Count);
        foreach (var (key, bytes) in items)
            if (bytes > 0) weights[key] = weights.GetValueOrDefault(key) + bytes;
        var total = weights.Values.Sum();
        if (total <= 0) return result;

        var placed = _rows.SelectMany(row => row.Keys).ToHashSet();
        var fresh = _rows.Count == 0 || weights.Any(pair => !placed.Contains(pair.Key) && pair.Value / total >= Noticeable);
        if (fresh) LayOut(weights, total, width, height);
        else
        {
            // Small newcomers go at the end, among the smallest; items that went away leave their rows.
            foreach (var row in _rows) row.Keys.RemoveAll(key => !weights.ContainsKey(key));
            _rows.RemoveAll(row => row.Keys.Count == 0);
            var newcomers = weights.Where(pair => !placed.Contains(pair.Key)).OrderByDescending(pair => pair.Value).Select(pair => pair.Key).ToList();
            if (newcomers.Count > 0)
            {
                if (_rows.Count == 0) _rows.Add((width >= height, newcomers));
                else _rows[^1].Keys.AddRange(newcomers);
            }
        }
        Place(weights, total, x, y, width, height, result);
        // Kept too long: a tile that matters has become much thinner than a fresh arrangement would make it.
        if (!fresh && Worst(result, width * height) > Math.Max(6, _freshWorst * 1.8))
        {
            LayOut(weights, total, width, height);
            result.Clear();
            Place(weights, total, x, y, width, height, result);
        }
        return result;
    }

    // The squarified arrangement (Bruls, Huizing and van Wijk), largest first, remembering its rows.
    private void LayOut(Dictionary<string, double> weights, double total, double width, double height)
    {
        Fresh++;
        _rows.Clear();
        var ordered = weights.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        var scale = Math.Sqrt(width) * Math.Sqrt(height);
        double w = width / scale, h = height / scale;
        var start = 0;
        while (start < ordered.Length)
        {
            var side = Math.Min(w, h);
            var first = ordered[start].Value / total;
            double sum = first, max = first, min = first;
            var end = start + 1;
            while (end < ordered.Length)
            {
                var next = ordered[end].Value / total;
                if (Aspect(sum + next, max, next, side) > Aspect(sum, max, min, side)) break;
                sum += next;
                min = next;
                end++;
            }
            var vertical = w >= h;
            _rows.Add((vertical, ordered[start..end].Select(pair => pair.Key).ToList()));
            var strip = side > 0 ? sum / side : 0;
            if (vertical) w = Math.Max(0, w - strip); else h = Math.Max(0, h - strip);
            start = end;
        }
        var trial = new List<StableTile>(ordered.Length);
        Place(weights, total, 0, 0, width, height, trial);
        _freshWorst = Worst(trial, width * height);
    }

    // Each row takes its share of what is left, across the way it runs; its items share the row by size.
    private void Place(Dictionary<string, double> weights, double total, double x, double y, double width, double height, List<StableTile> result)
    {
        var last = _rows.FindLastIndex(row => row.Keys.Any(key => weights.GetValueOrDefault(key) > 0));
        var remaining = total;
        for (var r = 0; r <= last; r++)
        {
            var (vertical, keys) = _rows[r];
            var rowSum = keys.Sum(key => weights.GetValueOrDefault(key));
            if (rowSum <= 0) continue;
            var fraction = r == last || remaining <= 0 ? 1 : Math.Min(1, rowSum / remaining);
            var strip = (vertical ? width : height) * fraction;
            var along = 0d;
            var length = vertical ? height : width;
            foreach (var key in keys)
            {
                var weight = weights.GetValueOrDefault(key);
                if (weight <= 0) continue;
                var size = length * weight / rowSum;
                result.Add(vertical ? new StableTile(key, x, y + along, strip, size) : new StableTile(key, x + along, y, size, strip));
                along += size;
            }
            if (vertical) { x += strip; width -= strip; } else { y += strip; height -= strip; }
            remaining -= rowSum;
        }
    }

    // The longest-to-shortest side of the thinnest tile that is big enough to matter.
    private static double Worst(List<StableTile> tiles, double area)
    {
        var worst = 1d;
        foreach (var tile in tiles)
        {
            if (tile.Width <= 0 || tile.Height <= 0 || tile.Width * tile.Height < area * .003) continue;
            worst = Math.Max(worst, Math.Max(tile.Width / tile.Height, tile.Height / tile.Width));
        }
        return worst;
    }

    private static double Aspect(double sum, double max, double min, double side)
        => Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min));
}
