namespace Clearspace.Services;

internal readonly record struct TreemapTile(int ItemIndex, double X, double Y, double Width, double Height);

internal static class SquarifiedTreemap
{
    // O(k log k) sorting, O(k) row construction. Incremental row statistics keep
    // the greedy worst-aspect-ratio comparison constant time per candidate.
    public static IReadOnlyList<TreemapTile> Layout(IReadOnlyList<long> weights, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return [];
        var ordered = Enumerable.Range(0, weights.Count).Where(i => weights[i] > 0)
            .OrderByDescending(i => weights[i]).ThenBy(i => i).ToArray();
        if (ordered.Length == 0) return [];
        var total = ordered.Sum(i => (double)weights[i]);
        // Work in unit-area coordinates, preserving the viewport aspect ratio.
        var scale = Math.Sqrt(width) * Math.Sqrt(height);
        if (!double.IsFinite(scale) || scale <= 0) return [];
        var w = width / scale;
        var h = height / scale;
        var x = 0d;
        var y = 0d;
        var result = new List<TreemapTile>(ordered.Length);
        var start = 0;
        while (start < ordered.Length && w > 0 && h > 0)
        {
            var end = start + 1;
            var max = weights[ordered[start]] / total;
            var min = max;
            var sum = max;
            var side = Math.Min(w, h);
            while (end < ordered.Length)
            {
                var next = weights[ordered[end]] / total;
                if (Worst(sum + next, max, next, side) > Worst(sum, max, min, side)) break;
                sum += next;
                min = next;
                end++;
            }
            var vertical = w >= h;
            var strip = Math.Min(vertical ? w : h, sum / side);
            // The final row consumes residual space, absorbing floating-point drift.
            if (end == ordered.Length) strip = vertical ? w : h;
            var offset = 0d;
            for (var i = start; i < end; i++)
            {
                var length = i == end - 1 ? side - offset : side * (weights[ordered[i]] / total) / sum;
                length = Math.Clamp(length, 0, side - offset);
                result.Add(new TreemapTile(ordered[i], (x + (vertical ? 0 : offset)) * scale,
                    (y + (vertical ? offset : 0)) * scale,
                    (vertical ? strip : length) * scale, (vertical ? length : strip) * scale));
                offset += length;
            }
            if (vertical) { x += strip; w = Math.Max(0, w - strip); }
            else { y += strip; h = Math.Max(0, h - strip); }
            start = end;
        }
        return result;
    }

    private static double Worst(double sum, double max, double min, double side)
        => Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min));
}
