// Clearspace | NEW (scroll while dragging): how fast a list scrolls while something is dragged near its
// top or bottom edge. Holding a dragged file within 44 pixels of an edge scrolls the list that way,
// slowly at the start of the band and faster the closer the pointer is to the edge, so a folder that is
// off screen can be reached without letting go. Scrolling starts after the pointer has stayed in the band
// for a moment (Dwell), so a drag that only passes through on its way out of the list does not move it.
// Just the arithmetic; MainWindow.DragScroll.cs applies it. Tests: Clearspace.Tests/DragScrollTests.cs.
namespace Clearspace.Services;

internal static class EdgeScroll
{
    internal const double Band = 44;       // pixels from an edge
    internal const double Slowest = 240;   // pixels per second at the inner side of the band
    internal const double Fastest = 2400;  // at the edge itself
    internal static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(220);

    // y: the pointer, in pixels from the top of the list's scrolling area. top: how much of that area is
    // covered by something that does not scroll (the column headers). height: the area's height.
    // Returns pixels per second: negative scrolls up, positive down, 0 when the pointer is in neither band.
    internal static double Speed(double y, double top, double height)
    {
        var room = height - top;
        if (room <= 0 || y < 0 || y > height)
            return 0;

        var band = Math.Min(Band, room / 3); // a very short list keeps a still middle third
        var upper = top + band;
        var lower = height - band;

        var pull = y < upper ? (upper - y) / band : y > lower ? (y - lower) / band : 0;
        if (pull <= 0)
            return 0;

        pull = Math.Min(pull, 1); // over the column headers counts as right at the edge
        var speed = Slowest + (Fastest - Slowest) * pull * pull;
        return y < upper ? -speed : speed;
    }

    // How far to move for the time since the last step. A long gap (the pointer was held still, or the
    // program was busy) counts as one frame, so the list never jumps.
    internal static double Step(double speed, TimeSpan elapsed)
        => speed * Math.Clamp(elapsed.TotalSeconds, 0, 0.05);
}
