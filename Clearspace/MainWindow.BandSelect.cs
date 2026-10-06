// Clearspace | NEW (drag to select): drag a rectangle over the file list and everything it touches is
// selected, as in File Explorer.
//
// Where a drag starts decides what it does:
//   - On a file's icon or text (details view), or anywhere on a tile (tile view): the file is dragged, as
//     before.
//   - On empty space: the blank part of a row (to the right of the name or of any other text, or past the
//     last column), a group heading, the gaps between tiles, or the space below the files: a rectangle is
//     drawn.
// Ctrl adds the rectangle to the selection by flipping what it touches; Shift only adds; with neither, the
// rectangle replaces the selection. Holding the pointer at or past the top or bottom edge scrolls the
// list, faster the further out; the mouse wheel works during the drag. Esc puts the selection back as it
// was. A click on empty space (no drag) clears the selection.
//
// The details view finds the rows by their place in the list (Services/BandSelect.cs: Locate, Rows), so a
// rectangle that has scrolled thousands of rows selects all of them, including the ones no longer on
// screen, and group headings are allowed for. The tile view works from the tile grid's own measurements.
// Tests for the arithmetic: Clearspace.Tests/BandSelectTests.cs. The mouse handling is checked by hand.
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Clearspace.Controls;
using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace;

public partial class MainWindow
{
    private const double TileInset = 4; // the margin around a tile inside its cell (TileContainer in App.xaml)
    private const int ManyAtOnce = 64;  // more changes than this in one go are made as a single change (SelectExactly)

    // ListBox.SetSelectedItems sets the whole selection in one step. It is a protected method of the list;
    // asking for it this way saves replacing the ListView in the XAML with a class of our own. Adding or
    // removing selected rows one at a time makes the list go through its whole selection for each one.
    private static readonly MethodInfo? SetSelectedItemsMethod = typeof(ListBox).GetMethod(
        "SetSelectedItems", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(IEnumerable)]);

    private bool _bandPending;                       // the button went down on empty space; a drag from here draws a rectangle
    private bool _bandClears;                        // ... and letting go without dragging clears the selection
    private bool _bandActive;
    private bool _bandQuiet;                         // selection changes are not passed on one by one (see PushBandSelection)
    private bool _bandDirty;                         // the pointer or the list moved since the rectangle was last worked out
    private bool _bandChanged;                       // the selection changed since the rest of the window was last told
    private bool _bandScrolls;                       // the pointer has been clear of the edges once, or is outside the list
    private List<FileSystemItem> _bandBefore = [];   // the selection before the button went down
    private List<FileSystemItem> _bandOrder = [];    // the files in the order the list shows them
    private Dictionary<FileSystemItem, int> _bandIndex = [];
    private HashSet<FileSystemItem> _bandBase = [];  // what counts as "selected before" for Ctrl and Shift
    private HashSet<FileSystemItem> _bandSelected = [];
    private HashSet<int> _bandInside = [];
    private BandMode _bandMode;
    private BandPoint _bandAnchor;                   // details view: where the drag began, tied to a row
    private Point _bandAnchorAt;                     // where it began: x in the scrolling area; y too (tiles: with the scroll added)
    private Point _bandPointer;                      // where the pointer is, in the scrolling area (may be outside it)
    private double _bandScrollTarget;
    private long _bandTick;
    private long _bandPushed;
    private Canvas? _bandLayer;
    private Border? _bandBox;
    private DispatcherTimer? _bandTimer;

    private void InitializeBandSelect()
    {
        FileList.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnBandButtonUp), handledEventsToo: true);
        Deactivated += (_, _) => EndBand(keep: true);

        // The rectangle is drawn on a layer of its own over the list. The layer is always there (it has no
        // background, so the mouse goes straight through it); only the rectangle in it is shown and hidden.
        // That way it already has its place over the list when the first rectangle is drawn.
        if (FileList.Parent is not Panel host)
            return;

        _bandBox = new Border
        {
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
            Visibility = Visibility.Collapsed
        };
        _bandBox.SetResourceReference(Border.BorderBrushProperty, "Accent");
        var fill = new Border { Opacity = 0.16 };
        fill.SetResourceReference(Border.BackgroundProperty, "Accent");
        _bandBox.Child = fill;

        _bandLayer = new Canvas { ClipToBounds = true, Focusable = false };
        _bandLayer.Children.Add(_bandBox);
        _bandLayer.MouseMove += (_, e) =>
        {
            if (_bandActive && BandArea is { } area)
            {
                _bandPointer = e.GetPosition(area);
                _bandDirty = true;
            }
        };
        _bandLayer.MouseLeftButtonUp += (_, e) => { EndBand(keep: true); e.Handled = true; };
        _bandLayer.LostMouseCapture += (_, _) => EndBand(keep: true);
        _bandLayer.MouseWheel += OnBandWheel;

        Grid.SetRow(_bandLayer, Grid.GetRow(FileList));
        Grid.SetColumn(_bandLayer, Grid.GetColumn(FileList));
        Panel.SetZIndex(_bandLayer, 40);
        host.Children.Add(_bandLayer);
    }

    // The scrolling area of the file list: the part below the column headers, without the scroll bars.
    // Found from the rows' own presenter upwards. Searching downwards for the first ScrollContentPresenter
    // finds the wrong one in the details view, where the column headers sit in a small scroller of their
    // own that comes first.
    private ScrollContentPresenter? BandArea
        => FindAncestor<ScrollContentPresenter>(FindDescendant<ItemsPresenter>(FileListScroll));

    // Called when the left button goes down on the file list (OnFileListPreviewMouseLeftButtonDown), before
    // the list itself has reacted. `row` is the row or tile under the pointer, if any; `onFile` whether the
    // press is on the file itself (a drag from there moves the file).
    private void NoteBandPress(MouseButtonEventArgs e, ListViewItem? row, bool onFile)
    {
        _bandPending = false;
        _bandClears = false;

        var source = e.OriginalSource as DependencyObject;
        if (onFile || _bandActive || _bandLayer is null || e.ClickCount > 1 || BandArea is not { } area
            || FindAncestor<ScrollBar>(source) is not null
            || FindAncestor<GridViewColumnHeader>(source) is not null
            || FindAncestor<TextBoxBase>(source) is not null)
            return;

        var at = e.GetPosition(area);
        if (at.X < 0 || at.Y < 0 || at.X > area.ActualWidth || at.Y > area.ActualHeight)
            return;

        _bandPending = true;
        _bandPointer = at;
        _bandBefore = [.. FileList.SelectedItems.OfType<FileSystemItem>()];

        // A click that is not on a row clears the selection, unless Ctrl or Shift is held. The thin gaps
        // between two rows do not count: a click there was meant for one of the rows.
        _bandClears = row is null && Keyboard.Modifiers == ModifierKeys.None && !BetweenRows(area, at.Y);
    }

    private bool BetweenRows(ScrollContentPresenter area, double y)
    {
        if (FileList.View is null)
            return false;

        foreach (var row in RowsOnScreen(area))
            if (y >= row.Top - 2 && y <= row.Bottom + 2)
                return true;

        return false;
    }

    // Called while the pointer moves over the file list with the left button down. True once a rectangle
    // has been started (the caller then leaves the move alone).
    private bool TryBeginBand(MouseEventArgs e)
    {
        if (!_bandPending || _bandActive || BandArea is not { } area)
            return false;

        var at = e.GetPosition(area);
        if (Math.Abs(at.X - _bandPointer.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(at.Y - _bandPointer.Y) < SystemParameters.MinimumVerticalDragDistance)
            return false;

        try
        {
            BeginBand(area, _bandPointer, at);
        }
        catch (Exception)
        {
            EndBand(keep: true); // never leave a half-started rectangle behind: it would hold the selection and the keys
        }

        return true;
    }

    private void BeginBand(ScrollContentPresenter area, Point from, Point to)
    {
        _bandPending = false;
        _bandClears = false;

        // The files in the order they are shown: group by group when the list is grouped.
        _bandOrder = [];
        if (FileList.Items.Groups is { } groups)
            foreach (var group in groups)
                AddGroup(group);
        else
            _bandOrder.AddRange(FileList.Items.OfType<FileSystemItem>());

        _bandIndex = new Dictionary<FileSystemItem, int>(_bandOrder.Count);
        for (var i = 0; i < _bandOrder.Count; i++)
            _bandIndex[_bandOrder[i]] = i;

        _bandMode = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? BandMode.Toggle
            : (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? BandMode.Add
            : BandMode.Replace;

        _bandQuiet = true;
        _bandActive = true;
        _bandInside = [];
        _bandSelected = [.. FileList.SelectedItems.OfType<FileSystemItem>()];

        // The list has already reacted to the button going down (it selects, or with Ctrl flips, the row
        // that was pressed). The drag starts from the selection as it was before that.
        if (_bandMode == BandMode.Replace)
        {
            _bandBase = [];
            if (_bandSelected.Count > 0)
            {
                FileList.UnselectAll();
                _bandSelected.Clear();
            }
        }
        else
        {
            _bandBase = [.. _bandBefore.Where(_bandIndex.ContainsKey)];
            if (!_bandSelected.SetEquals(_bandBase))
                SelectExactly(_bandBase);
        }

        // Where the drag began. Details view: tied to the row there, so it stays put as the list scrolls.
        // Tile view: in pixels from the top of the whole grid.
        _bandAnchorAt = from;
        if (FileList.View is not null)
            _bandAnchor = BandSelect.Locate(RowsOnScreen(area), from.Y);
        else if (FindDescendant<VirtualizingWrapPanel>(area) is { } tiles)
            _bandAnchorAt = new Point(from.X, area.TranslatePoint(from, tiles).Y + tiles.VerticalOffset);

        _bandPointer = to;
        _bandScrolls = false;
        _bandScrollTarget = FileListScroll?.VerticalOffset ?? 0;
        _bandTick = Stopwatch.GetTimestamp();
        _bandPushed = _bandTick - Stopwatch.Frequency; // a second ago: the first change is passed on at once
        _bandChanged = true;

        FileList.Focus();
        _bandLayer!.CaptureMouse();

        _bandTimer ??= new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(15) };
        _bandTimer.Tick -= OnBandTick;
        _bandTimer.Tick += OnBandTick;
        _bandTimer.Start();

        RefreshBand();
    }

    private void AddGroup(object group)
    {
        if (group is not CollectionViewGroup collection)
            return;

        foreach (var entry in collection.Items)
        {
            if (entry is FileSystemItem item)
                _bandOrder.Add(item);
            else
                AddGroup(entry);
        }
    }

    private void OnBandWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (!_bandActive || FileListScroll is not { } viewer || e.Delta == 0)
            return;

        // The same step as an ordinary turn of the wheel (as MainWindow.DragScroll.cs does for a dragged file).
        if (FindDescendant<VirtualizingPanel>(viewer) is IScrollInfo scrolls)
        {
            if (e.Delta > 0) scrolls.MouseWheelUp(); else scrolls.MouseWheelDown();
        }
        else
        {
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta * 48.0 / 120);
        }

        _bandScrollTarget = double.NaN; // read back from the list at the next tick
        _bandDirty = true;
    }

    // About sixty times a second while the rectangle is out: scroll if the pointer is held at an edge, and
    // work the rectangle out again if anything moved.
    private void OnBandTick(object? sender, EventArgs e)
    {
        if (!_bandActive || BandArea is not { } area || FileListScroll is not { } viewer
            || Mouse.LeftButton != MouseButtonState.Pressed) // the button was let go somewhere that did not tell us
        {
            EndBand(keep: true);
            return;
        }

        if (_bandDirty)
        {
            _bandDirty = false;
            RefreshBand();
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_bandTick, now);
        _bandTick = now;

        // A drag that starts right at the top or bottom of the list does not scroll it at once: the
        // pointer has to have been clear of the edge first, or be outside the list altogether.
        var speed = viewer.ScrollableHeight <= 0 ? 0 : BandSelect.ScrollSpeed(_bandPointer.Y, 0, area.ActualHeight);
        if (speed == 0 || _bandPointer.Y < 0 || _bandPointer.Y > area.ActualHeight)
            _bandScrolls = true;

        if (speed == 0 || !_bandScrolls)
        {
            _bandScrollTarget = viewer.VerticalOffset;
            return;
        }

        if (double.IsNaN(_bandScrollTarget))
            _bandScrollTarget = viewer.VerticalOffset;

        // Never more than half the height of the list in one go: every row passes through the screen.
        var step = Math.Clamp(speed * Math.Clamp(elapsed.TotalSeconds, 0, 0.05), -area.ActualHeight / 2, area.ActualHeight / 2);
        var target = Math.Clamp(_bandScrollTarget + step, 0, viewer.ScrollableHeight);
        if (Math.Abs(target - _bandScrollTarget) < 0.01)
            return;

        _bandScrollTarget = target;
        viewer.ScrollToVerticalOffset(target);
        _bandDirty = true; // worked out again at the next tick, once the list has moved
    }

    // The rows of the details view that are on screen (and the few kept ready just off it).
    private List<BandRow> RowsOnScreen(ScrollContentPresenter area)
    {
        var rows = new List<BandRow>();
        var index = _bandActive ? _bandIndex : null;
        Collect(area);
        rows.Sort((left, right) => left.Index.CompareTo(right.Index));
        return rows;

        void Collect(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is not ListViewItem row)
                {
                    Collect(child);
                    continue;
                }

                // A row that is in use shows the file it was made for; one waiting to be reused does not.
                if (row.DataContext is not FileSystemItem item || !ReferenceEquals(row.Content, item)
                    || !row.IsVisible || row.ActualHeight <= 0)
                    continue;

                // Before a drag has started there is no order yet; the rows' own order on screen will do.
                var place = index is null ? rows.Count : index.GetValueOrDefault(item, -1);
                if (place < 0)
                    continue;

                var top = row.TranslatePoint(new Point(0, 0), area).Y;
                rows.Add(new BandRow(place, top, top + row.ActualHeight));
            }
        }
    }

    // Works out what the rectangle touches now, changes the selection to match, and draws the rectangle.
    private void RefreshBand()
    {
        if (!_bandActive || BandArea is not { } area || _bandLayer is null || _bandBox is null)
            return;

        // The pointer may be outside the list (the button is held); the rectangle ends at the list's edge.
        var pointer = new Point(Math.Clamp(_bandPointer.X, 0, area.ActualWidth), Math.Clamp(_bandPointer.Y, 0, area.ActualHeight));
        var inside = new HashSet<int>();
        var anchorY = _bandAnchorAt.Y;

        if (FileList.View is not null)
        {
            var rows = RowsOnScreen(area);
            var here = BandSelect.Locate(rows, pointer.Y);
            if (BandSelect.Rows(_bandAnchor.Position, here.Position, _bandOrder.Count) is { } touched)
                for (var i = touched.First; i <= touched.Last; i++)
                    inside.Add(i);

            if (rows.Count > 0) // an empty folder has no row to go by: the rectangle starts where the button went down
                anchorY = BandSelect.Where(rows, _bandAnchor, out var above) ?? (above ? -8 : area.ActualHeight + 8);
        }
        else if (FindDescendant<VirtualizingWrapPanel>(area) is { } tiles)
        {
            var at = area.TranslatePoint(pointer, tiles);
            var anchorX = area.TranslatePoint(new Point(_bandAnchorAt.X, 0), tiles).X;
            foreach (var i in BandSelect.Tiles(anchorX, _bandAnchorAt.Y, at.X, at.Y + tiles.VerticalOffset,
                         tiles.ItemWidth, tiles.ItemHeight, TileInset, tiles.Columns, _bandOrder.Count))
                inside.Add(i);

            anchorY = tiles.TranslatePoint(new Point(0, _bandAnchorAt.Y - tiles.VerticalOffset), area).Y;
        }

        // Only the files whose place in or out of the rectangle changed are touched.
        var changes = new List<(FileSystemItem Item, bool Selected)>();
        foreach (var i in inside)
            if (!_bandInside.Contains(i))
                Note(i, true);
        foreach (var i in _bandInside)
            if (!inside.Contains(i) && i < _bandOrder.Count)
                Note(i, false);
        _bandInside = inside;

        void Note(int i, bool isInside)
        {
            var item = _bandOrder[i];
            var wanted = BandSelect.Wanted(_bandBase.Contains(item), isInside, _bandMode);
            if (wanted != _bandSelected.Contains(item))
                changes.Add((item, wanted));
        }

        if (changes.Count > ManyAtOnce)
        {
            var all = new HashSet<FileSystemItem>(_bandSelected);
            foreach (var (item, selected) in changes)
                if (selected) all.Add(item); else all.Remove(item);
            SelectExactly(all);
        }
        else
        {
            foreach (var (item, selected) in changes)
                SetBandSelected(item, selected);
        }

        // The rest of the window (status line, toolbar, preview) hears about the selection a few times a
        // second while the rectangle moves, and once more when it is let go.
        if (_bandChanged && Stopwatch.GetElapsedTime(_bandPushed) >= TimeSpan.FromMilliseconds(120))
            PushBandSelection();

        // Draw it: from where the drag began (which may have scrolled out of sight) to the pointer.
        anchorY = Math.Clamp(anchorY, -8, area.ActualHeight + 8);
        var origin = area.TranslatePoint(new Point(0, 0), _bandLayer);
        _bandLayer.Clip = new RectangleGeometry(new Rect(origin.X, origin.Y, area.ActualWidth, area.ActualHeight));
        Canvas.SetLeft(_bandBox, origin.X + Math.Min(_bandAnchorAt.X, pointer.X));
        Canvas.SetTop(_bandBox, origin.Y + Math.Min(anchorY, pointer.Y));
        _bandBox.Width = Math.Abs(pointer.X - _bandAnchorAt.X);
        _bandBox.Height = Math.Abs(pointer.Y - anchorY);
        _bandBox.Visibility = Visibility.Visible;
    }

    private void SetBandSelected(FileSystemItem item, bool selected)
    {
        if (selected)
        {
            if (_bandSelected.Add(item))
                FileList.SelectedItems.Add(item);
        }
        else if (_bandSelected.Remove(item))
        {
            FileList.SelectedItems.Remove(item);
        }
    }

    // Makes the selection exactly these files, in one step where the list allows it.
    private void SelectExactly(IReadOnlyCollection<FileSystemItem> items)
    {
        SetSelection(items);
        _bandSelected = [.. items];
        _bandChanged = true;
    }

    // CHANGED (pre-release review): taken out of SelectExactly, so that Invert selection (MainWindow.xaml.cs)
    // can set a whole selection in one step too, without touching the rectangle's own bookkeeping.
    private void SetSelection(IReadOnlyCollection<FileSystemItem> items)
    {
        var done = false;
        if (SetSelectedItemsMethod is not null)
        {
            try
            {
                done = SetSelectedItemsMethod.Invoke(FileList, [items.ToList()]) is true;
            }
            catch (Exception)
            {
                done = false;
            }
        }

        if (!done)
        {
            FileList.UnselectAll();
            foreach (var item in items)
                FileList.SelectedItems.Add(item);
        }
    }

    // Tells the rest of the window what is selected (what OnSelectionChanged does for every change when no
    // rectangle is out). With thousands of rows going in and out as the list scrolls, doing it for each
    // row would make the drag itself lag.
    private void PushBandSelection()
    {
        _bandPushed = Stopwatch.GetTimestamp();
        _bandChanged = false;
        _viewModel.Context.SelectedItems = FileList.SelectedItems.Cast<FileSystemItem>().ToList();
    }

    // The button was let go, the window lost the mouse, the list got new contents, or Esc was pressed.
    // keep: the selection stays as the rectangle left it; otherwise it goes back to what it was before.
    private void EndBand(bool keep)
    {
        _bandPending = false;
        _bandTimer?.Stop();
        if (!_bandActive)
            return;

        _bandActive = false;

        try
        {
            if (!keep)
                SelectExactly([.. _bandBefore.Where(_bandIndex.ContainsKey)]);
        }
        catch (Exception)
        {
            // The selection stays as the rectangle left it.
        }
        finally
        {
            // Whatever happened above, the window hears about selection changes again from here on.
            _bandQuiet = false;

            if (_bandBox is not null)
                _bandBox.Visibility = Visibility.Collapsed;
            if (_bandLayer is { IsMouseCaptured: true })
                _bandLayer.ReleaseMouseCapture();

            _bandOrder = [];
            _bandIndex = [];
            _bandInside = [];
            _bandSelected = [];
            _bandBase = [];
        }

        PushBandSelection();
    }

    // The left button came up over the list: normally without a rectangle having been drawn (the
    // rectangle's own layer hears the button when there is one).
    private void OnBandButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_bandActive)
        {
            EndBand(keep: true);
            return;
        }

        var clear = _bandPending && _bandClears;
        _bandPending = false;
        _bandClears = false;

        if (clear && FileList.SelectedItems.Count > 0)
            FileList.UnselectAll();
    }
}
