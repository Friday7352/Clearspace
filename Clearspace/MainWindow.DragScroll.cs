// Clearspace | NEW (scroll while dragging): scrolling a list while you are holding a dragged file.
// Two ways, both for the file list and the sidebar:
//   - Hold the dragged item near the top or bottom edge and the list scrolls that way, faster the closer
//     to the edge (Services/EdgeScroll.cs).
//   - Turn the mouse wheel. Windows does not pass the wheel on during a drag, so it is listened for
//     directly while the drag lasts (Services/DragWheel.cs) and scrolls the list under the pointer by the
//     same amount as an ordinary turn of the wheel.
// Both work for a drag that started in Clearspace and for one that came from another program (a file
// dragged in from the desktop). Nothing here decides where a drop goes; the list's and the sidebar's own
// handlers still do that, and they see the new rows as the list moves under the pointer.
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Clearspace.Services;

namespace Clearspace;

public partial class MainWindow
{
    private static MainWindow? s_dragWindow; // the window the drag was last over; only it answers the wheel
    private DragWheel? _dragWheel;
    private ScrollViewer? _edgeViewer;        // the list whose edge band the pointer is in
    private long _edgeSince;                  // when it entered the band
    private long _edgeLast;                   // the last time it was seen there
    private double _edgeTarget;               // where the list has been asked to scroll to

    private void InitializeDragScroll()
    {
        _dragWheel = new DragWheel(OnDragWheel, EndDragScroll);

        // The tunnelling events reach the window before the list's, the sidebar's and the tabs' own handlers.
        AddHandler(DragDrop.PreviewDragEnterEvent, new DragEventHandler(OnDragOverAnywhere), handledEventsToo: true);
        AddHandler(DragDrop.PreviewDragOverEvent, new DragEventHandler(OnDragOverAnywhere), handledEventsToo: true);
        AddHandler(DragDrop.PreviewDropEvent, new DragEventHandler((_, _) => EndDragScroll()), handledEventsToo: true);

        Closed += (_, _) =>
        {
            _dragWheel?.Dispose();
            if (ReferenceEquals(s_dragWindow, this))
                s_dragWindow = null;
        };
    }

    // A drag that starts in this window: called just before DragDrop.DoDragDrop, with EndDragScroll after it.
    private void BeginDragScroll()
    {
        s_dragWindow = this;
        _edgeViewer = null;
        _dragWheel?.On();
    }

    // The drag is over: dropped, cancelled, or the mouse button was let go somewhere else.
    private void EndDragScroll()
    {
        _edgeViewer = null;
        _dragWheel?.Off();
    }

    // Something is being dragged over this window (called many times a second while it is).
    private void OnDragOverAnywhere(object sender, DragEventArgs e)
    {
        s_dragWindow = this;
        _dragWheel?.On(); // for a drag that came from another program

        var (viewer, top) = ScrollerAt(scroller => e.GetPosition(scroller), out var position);
        var speed = viewer is null || viewer.ScrollableHeight <= 0 ? 0 : EdgeScroll.Speed(position.Y, top, viewer.ActualHeight);
        if (viewer is null || speed == 0)
        {
            _edgeViewer = null;
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (!ReferenceEquals(viewer, _edgeViewer) || Stopwatch.GetElapsedTime(_edgeLast, now) > TimeSpan.FromMilliseconds(400))
        {
            // Just arrived in the band (or back after a pause): start the short wait.
            _edgeViewer = viewer;
            _edgeSince = _edgeLast = now;
            _edgeTarget = viewer.VerticalOffset;
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_edgeLast, now);
        _edgeLast = now;
        if (Stopwatch.GetElapsedTime(_edgeSince, now) < EdgeScroll.Dwell)
        {
            _edgeTarget = viewer.VerticalOffset;
            return;
        }

        // The target is kept here, not read back from the list each time: the list only moves at its next
        // layout, and several of these calls can arrive before that.
        _edgeTarget = Math.Clamp(_edgeTarget + EdgeScroll.Step(speed, elapsed), 0, viewer.ScrollableHeight);
        viewer.ScrollToVerticalOffset(_edgeTarget);
    }

    // The wheel was turned during a drag. Called from the mouse hook, so nothing may be thrown from here
    // (DragWheel swallows it if something is) and it must be quick.
    private void OnDragWheel(int delta, int screenX, int screenY)
    {
        if (!ReferenceEquals(s_dragWindow, this) || delta == 0 || !IsVisible)
            return;

        var at = new Point(screenX, screenY);
        var (viewer, _) = ScrollerAt(scroller => scroller.PointFromScreen(at), out _);
        if (viewer is null)
            return;

        // The same step as an ordinary turn of the wheel: whatever does the scrolling for this list is asked
        // directly (the row panel for the file list, the content area for the sidebar).
        IScrollInfo? scrolls = viewer.CanContentScroll
            ? FindDescendant<VirtualizingPanel>(viewer) as IScrollInfo
            : FindDescendant<ScrollContentPresenter>(viewer);

        if (scrolls is null)
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset - delta * 48.0 / 120);
        else if (delta > 0)
            scrolls.MouseWheelUp();
        else
            scrolls.MouseWheelDown();

        _edgeViewer = null; // the edge band starts over from where the wheel left the list
    }

    // The scrolling list under a point: the file list or the sidebar, or null. `where` gives the point
    // relative to a list. top: the height of the file list's column headers, which do not scroll.
    private (ScrollViewer? Viewer, double Top) ScrollerAt(Func<ScrollViewer, Point> where, out Point position)
    {
        position = default;
        if (ExplorerShell.Visibility != Visibility.Visible)
            return (null, 0);

        if (FileListScroll is { IsVisible: true } list && Inside(list, position = where(list)))
        {
            var headers = _viewModel.IsDetails ? FindDescendant<GridViewHeaderRowPresenter>(list)?.ActualHeight ?? 0 : 0;
            return (list, headers);
        }

        if (SidebarScroll.IsVisible && Inside(SidebarScroll, position = where(SidebarScroll)))
            return (SidebarScroll, 0);

        return (null, 0);
    }

    private static bool Inside(FrameworkElement element, Point point)
        => point.X >= 0 && point.Y >= 0 && point.X <= element.ActualWidth && point.Y <= element.ActualHeight;
}
