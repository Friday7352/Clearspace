// Clearspace | NEW (drag to select): the arithmetic behind dragging a rectangle over the file list to
// select everything it touches, as in File Explorer. MainWindow.BandSelect.cs does the mouse, the drawing
// and the scrolling; this file answers, with no window involved:
//   - where in the list a point is (Locate), in a way that survives scrolling;
//   - which rows (details view) or tiles (tile view) a rectangle touches;
//   - whether a file ends up selected (Wanted), given Ctrl, Shift or neither;
//   - how fast the list scrolls while the pointer is held at or past its top or bottom edge.
// Tests: Clearspace.Tests/BandSelectTests.cs.
namespace Clearspace.Services;

// What the drag does to the selection there was before it.
internal enum BandMode
{
    Replace, // neither key: only what the rectangle touches is selected
    Add,     // Shift: what was selected stays, and what the rectangle touches is added
    Toggle   // Ctrl: what the rectangle touches is flipped (selected becomes unselected and back)
}

// One row of the details view that is on screen: its place in the list as shown (0 is the first row,
// counted through the groups when the list is grouped) and where it is drawn, top and bottom, in pixels
// from the top of the list's scrolling area.
internal readonly record struct BandRow(int Index, double Top, double Bottom);

// A point in the details view, tied to a row so that it stays put when the list scrolls: the row it is in
// (or the nearest one above it), and how far below that row's top it is. `Position` is the same thing as
// one number: 3.5 is halfway down the fourth row, 4 is the gap after it (a group heading, or the empty
// space below the last row).
internal readonly record struct BandPoint(int Row, double Offset, double Position);

internal static class BandSelect
{
    internal const double Edge = 16;          // pixels inside the top or bottom edge where scrolling starts
    internal const double PerPixel = 30;      // pixels per second of scrolling, for each pixel past that line
    internal const double Fastest = 3000;     // pixels per second

    // Where y is among the rows on screen. `rows` in list order. With no rows on screen the point is
    // before the first row.
    internal static BandPoint Locate(IReadOnlyList<BandRow> rows, double y)
    {
        BandRow? above = null;

        foreach (var row in rows)
        {
            if (y >= row.Top && y < row.Bottom)
            {
                var height = Math.Max(1, row.Bottom - row.Top);
                return new BandPoint(row.Index, y - row.Top, row.Index + (y - row.Top) / height);
            }

            if (row.Bottom <= y && (above is null || row.Index > above.Value.Index))
                above = row;
        }

        if (above is { } nearest)
            return new BandPoint(nearest.Index, y - nearest.Top, nearest.Index + 1); // in the gap after it

        if (rows.Count == 0)
            return new BandPoint(0, y, 0);

        var first = rows[0];
        foreach (var row in rows)
            if (row.Index < first.Index)
                first = row;

        return new BandPoint(first.Index, y - first.Top, first.Index); // above every row on screen
    }

    // Where a point found earlier is drawn now, after the list may have scrolled. Null when its row is no
    // longer on screen; `above` then says which way it went.
    internal static double? Where(IReadOnlyList<BandRow> rows, BandPoint point, out bool above)
    {
        above = true;
        var seen = false;
        var lowest = int.MaxValue;

        foreach (var row in rows)
        {
            if (row.Index == point.Row)
                return row.Top + point.Offset;

            seen = true;
            lowest = Math.Min(lowest, row.Index);
        }

        above = !seen || point.Row < lowest;
        return null;
    }

    // The rows a rectangle from one point to another touches, first to last, or null when it touches none
    // (both ends in the same gap). The two points may be given in either order.
    internal static (int First, int Last)? Rows(double from, double to, int count)
    {
        var low = Math.Min(from, to);
        var high = Math.Max(from, to);

        // Row d covers the positions d to d + 1. It is touched when the rectangle reaches into it; a
        // rectangle with no height (a press without a drag) touches the row it is in.
        var first = (int)Math.Floor(low);
        var last = high == Math.Floor(high) && high > low ? (int)high - 1 : (int)Math.Floor(high);
        if (low == high && low == Math.Floor(low))
            return null; // in a gap

        first = Math.Max(first, 0);
        last = Math.Min(last, count - 1);
        return first > last ? null : (first, last);
    }

    // The tiles a rectangle touches, in the tile view: `columns` tiles to a row, each in a cell of
    // cellWidth by cellHeight, drawn `inset` pixels inside its cell. The rectangle is in the same pixels,
    // measured from the top left of the first tile's cell (so scrolling is already taken out).
    internal static IEnumerable<int> Tiles(double left, double top, double right, double bottom,
        double cellWidth, double cellHeight, double inset, int columns, int count)
    {
        if (count <= 0 || columns <= 0 || cellWidth <= 0 || cellHeight <= 0)
            yield break;

        if (left > right) (left, right) = (right, left);
        if (top > bottom) (top, bottom) = (bottom, top);

        // A tile occupies [c * w + inset, (c + 1) * w - inset] across; it is touched when that overlaps the
        // rectangle. The first column whose right side is past `left`, to the last whose left side is
        // before `right`.
        var firstColumn = Math.Max(0, (int)Math.Ceiling((left + inset) / cellWidth) - 1);
        var lastColumn = Math.Min(columns - 1, (int)Math.Floor((right - inset) / cellWidth));
        var firstRow = Math.Max(0, (int)Math.Ceiling((top + inset) / cellHeight) - 1);
        var lastRow = Math.Min((count - 1) / columns, (int)Math.Floor((bottom - inset) / cellHeight));

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var index = row * columns + column;
                if (index < count)
                    yield return index;
            }
        }
    }

    // Whether a file is selected while the rectangle is being dragged: `was` is whether it was selected
    // before the drag began, `inside` whether the rectangle touches it now.
    internal static bool Wanted(bool was, bool inside, BandMode mode) => mode switch
    {
        BandMode.Add => was || inside,
        BandMode.Toggle => was != inside,
        _ => inside
    };

    // How fast the list scrolls while the pointer is held at y during the drag (pixels per second;
    // negative is up). y is measured from the top of the list's scrolling area and may be outside it, the
    // mouse being held down; top is the height of whatever does not scroll (the column headers); height is
    // the area's height. Nothing in the middle; from `Edge` pixels inside an edge the speed grows with
    // every pixel towards and past it, so holding the pointer further out scrolls faster.
    internal static double ScrollSpeed(double y, double top, double height)
    {
        var room = height - top;
        if (room <= 0)
            return 0;

        var edge = Math.Min(Edge, room / 3);
        var upper = top + edge;
        var lower = height - edge;

        if (y < upper)
            return -Math.Min(Fastest, (upper - y) * PerPixel);
        if (y > lower)
            return Math.Min(Fastest, (y - lower) * PerPixel);
        return 0;
    }
}
