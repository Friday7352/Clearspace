using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Clearspace.Services;
using Shapes = System.Windows.Shapes;

namespace Clearspace;

// NEW (Arrange mode): the editor does two jobs, and they are now two modes, switched on the bar at the top:
//   Style    the cards and the panel: colors, fonts, patterns… A click on the window picks a part to style.
//   Arrange  where things go. No cards. Drag any part of the window to move it; drag the sidebar's edge to make
//            it wider or narrower. The panel holds the ready layouts, which parts show, sizes, and the panels you
//            can add (ThemeEditor.Panels.cs).
// So a click means one thing and a drag means one thing, whichever mode you are in.
// Precise dragging (CHANGED from guessed drop boxes that did not always match where the part landed):
//   - The nearest side wins: the sidebar and the preview pane go to whichever half of the window the pointer is
//     over, the toolbar to the top or bottom half, the tabs above or below the middle of the toolbar, the status
//     bar over or under the middle of the file area. Letting go anywhere lands somewhere sensible. A small dead
//     zone in the middle stops it flickering between two places.
//   - The window really rearranges while you drag (a draft, not an undo step), and the outline is drawn around
//     where the part actually is afterwards, so what you see is what you get. The outline glides to its new
//     place in 150 ms.
//   - Letting go keeps it (one undo step); Esc, or letting go where it started, puts everything back.
internal sealed partial class ThemeEditor
{
    private bool _arranging;
    private Border? _styleButton, _arrangeButton;
    private CanvasPart? _dragPart;
    private string? _dragFrom, _dragTo;
    private int _dragVersion;
    private readonly Border _widthGrip = new() { Width = 10, Cursor = Cursors.SizeWE, Background = Brushes.Transparent, Visibility = Visibility.Collapsed, ToolTip = "Drag to make the sidebar wider or narrower" };
    private bool _gripDragging;

    private static readonly Duration SnapSpeed = new(TimeSpan.FromMilliseconds(150));
    private const double DeadZone = 14;

    /// <summary>Whether the editor is in Arrange mode. For tests.</summary>
    internal bool Arranging => _arranging;

    // ---------------------------------------------------------------- the switch on the bar

    private FrameworkElement ModeSwitch()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _styleButton = BannerButton("Style", "Colors, fonts, patterns and cards. Click a part of the window to style it.", () => SetMode(false));
        _arrangeButton = BannerButton("Arrange", "Where things go. Drag any part of the window to move it.", () => SetMode(true));
        _styleButton.Margin = new Thickness(0); _arrangeButton.Margin = new Thickness(4, 0, 0, 0);
        // CHANGED (review): BannerButton clears the background when the pointer leaves, which took the lit mode's away.
        _styleButton.MouseLeave += (_, _) => PaintModeSwitch();
        _arrangeButton.MouseLeave += (_, _) => PaintModeSwitch();
        row.Children.Add(_styleButton);
        row.Children.Add(_arrangeButton);
        PaintModeSwitch();
        return row;
    }

    private void PaintModeSwitch()
    {
        foreach (var (button, on) in new[] { (_styleButton, !_arranging), (_arrangeButton, _arranging) })
        {
            if (button?.Child is not TextBlock words) continue;
            if (on) { button.SetResourceReference(Border.BackgroundProperty, "Base"); words.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); words.FontWeight = FontWeights.SemiBold; }
            else { button.Background = Brushes.Transparent; words.SetResourceReference(TextBlock.ForegroundProperty, "AccentInk"); words.FontWeight = FontWeights.Normal; }
        }
    }

    /// <summary>Switches between Style (false) and Arrange (true).</summary>
    internal void SetMode(bool arrange)
    {
        if (_arranging == arrange) return;
        CancelPress();
        _arranging = arrange;
        _selected = null; _sticker = null; _pickedCard = null; _iconForm = false;
        Hand.Visibility = arrange ? Visibility.Collapsed : Visibility.Visible;
        _tabsRow.Visibility = arrange ? Visibility.Collapsed : Visibility.Visible;
        PaintModeSwitch();
        PaintHand();
        SyncPanel();
        _scroll.ScrollToTop();
        RefreshCanvas();
        Report(arrange ? "Drag any part of the window to move it." : "Click a part of the window, or a card, to style it.");
    }

    // A click (not a drag) on the window in Arrange mode.
    private void ArrangeClick(CanvasPart? part)
    {
        if (part is null || part.Id == "Window") return;
        Report(Movable(part) ? $"Drag the {part.Label.ToLowerInvariant()} to move it." : _document.LayoutJson is not null
            ? "This theme has a custom layout: change it with the layout builder (in the panel)."
            : $"The {part.Label.ToLowerInvariant()} stays where it is; you can hide it in the panel.");
    }

    // ---------------------------------------------------------------- dragging a part

    private void StartPartDrag(CanvasPart part)
    {
        _partDragging = true;
        _dragPart = part;
        _dragFrom = _dragTo = PlaceOf(part);
        ShowHover(null);
        _selectBox.Visibility = _selectTag.Visibility = Visibility.Collapsed;
        _widthGrip.Visibility = Visibility.Collapsed;
        if (PartBounds(part) is { } now) MoveSnap(now, PlaceLabel(part, _dragFrom ?? ""), animate: false);
        SetTag(_ghost, part.Label);
        _ghost.Visibility = Visibility.Visible;
        _canvas.Cursor = Cursors.SizeAll;
    }

    private void UpdatePartDrag(Point point)
    {
        if (_dragPart is not { } part) return;
        Canvas.SetLeft(_ghost, point.X + 16); Canvas.SetTop(_ghost, point.Y + 16);
        var to = NearestPlace(part, point, _dragTo);
        if (to is null || to == _dragTo) return;
        _dragTo = to;
        SetTag(_ghost, to == _dragFrom ? part.Label : part.Label + "  →  " + PlaceLabel(part, to));
        PreviewPlace(part, to);
    }

    // Where the part would go with the pointer here: the nearest side, with a dead zone round the middle so it
    // does not flicker.
    private string? NearestPlace(CanvasPart part, Point point, string? now)
    {
        if (part.Places.Length < 2) return null;
        bool across = part.Places.Contains("Left");
        Rect? reference = part.Id switch
        {
            "Tabs" => ElementBounds("NavigationArea"),
            "Status" => ElementBounds("ContentCard"),
            _ => Whole
        };
        if (reference is not { } r || r.Width < 2 || r.Height < 2) return now;
        var offset = across ? point.X - (r.Left + r.Width / 2) : point.Y - (r.Top + r.Height / 2);
        if (Math.Abs(offset) < DeadZone) return now;
        var first = across ? "Left" : part.Places.Contains("Top") ? "Top" : "Above";
        var second = across ? "Right" : part.Places.Contains("Bottom") ? "Bottom" : "Below";
        return offset < 0 ? first : second;
    }

    // The theme with the part put there, and shown (as a draft, not an undo step); the outline follows the part
    // to where it really is once the window has rearranged.
    private void PreviewPlace(CanvasPart part, string place)
    {
        var version = ++_dragVersion;
        try
        {
            var next = Placed(part, place);
            if (ThemeService.ShowDraft(next) is { } problem) { Report(problem, true); return; }
        }
        catch (Exception error) when (error is ArgumentException or ThemeFormatException) { Report(error.Message, true); return; }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_partDragging || version != _dragVersion) return;
            if (PartBounds(part) is { } landed) MoveSnap(landed, PlaceLabel(part, place), animate: true);
        }), DispatcherPriority.Loaded);
    }

    private ThemeStudioDocument Placed(CanvasPart part, string place)
    {
        if (PlaceSetting(part.Id, place) is not { } setting) return _document;
        var next = _document.Layout(setting.Key, setting.On);
        return part.Hide is { } hide ? next.Layout(hide, 1) : next;
    }

    private void FinishPartDrag()
    {
        var part = _dragPart; var to = _dragTo; var from = _dragFrom;
        EndDrag();
        if (part is null || to is null || to == from) { ShowDraft(); return; }   // nothing moved: the window as it was
        PlacePart(part.Id, to);   // one undo step
        Report($"{part.Label}: {PlaceLabel(part, to).ToLowerInvariant()}.");
    }

    // Esc, or the mouse lost mid-drag: the window as it was.
    private void CancelPartDrag()
    {
        EndDrag();
        ShowDraft();
    }

    private void EndDrag()
    {
        _partDragging = false;
        _dragPart = null; _dragFrom = _dragTo = null;
        _dragVersion++;
        _ghost.Visibility = Visibility.Collapsed;
        HideSnap();
        // CHANGED (review): let go of the gliding outline's animations, which would otherwise hold it where the drag
        // left it and win over ShowSnap when a card is dragged onto the window later.
        foreach (var element in new FrameworkElement[] { _snapBox, _snapTag })
            foreach (var property in new[] { Canvas.LeftProperty, Canvas.TopProperty, WidthProperty, HeightProperty })
                element.BeginAnimation(property, null);
        _canvas.Cursor = null;
        RefreshCanvas();
    }

    // The outline (and its label) at a place, gliding there when animate is set.
    private void MoveSnap(Rect area, string label, bool animate)
    {
        SetTag(_snapTag, label);
        _snapBox.Visibility = _snapTag.Visibility = Visibility.Visible;
        void To(FrameworkElement element, DependencyProperty property, double value)
        {
            if (animate && !double.IsNaN((double)element.GetValue(property)))
                element.BeginAnimation(property, new DoubleAnimation(value, SnapSpeed) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else { element.BeginAnimation(property, null); element.SetValue(property, value); }
        }
        To(_snapBox, Canvas.LeftProperty, area.X);
        To(_snapBox, Canvas.TopProperty, area.Y);
        To(_snapBox, WidthProperty, Math.Max(0, area.Width));
        To(_snapBox, HeightProperty, Math.Max(0, area.Height));
        _snapTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        To(_snapTag, Canvas.LeftProperty, Math.Max(4, area.Left + (area.Width - _snapTag.DesiredSize.Width) / 2));
        To(_snapTag, Canvas.TopProperty, Math.Max(4, area.Top + (area.Height - _snapTag.DesiredSize.Height) / 2));
    }

    // ---------------------------------------------------------------- the sidebar's edge

    private void BuildWidthGrip()
    {
        var line = new Border { Width = 3, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center, Opacity = .0 };
        line.SetResourceReference(Border.BackgroundProperty, "Accent");
        _widthGrip.Child = line;
        _widthGrip.MouseEnter += (_, _) => line.Opacity = .9;
        _widthGrip.MouseLeave += (_, _) => { if (!_gripDragging) line.Opacity = .35; };
        _widthGrip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _gripDragging = true;
            BeginGesture();
            _widthGrip.CaptureMouse();
        };
        _widthGrip.MouseMove += (_, e) =>
        {
            if (!_gripDragging || ElementBounds("SidebarPanel") is not { } side) return;
            e.Handled = true;
            var x = e.GetPosition(_canvas).X;
            var scale = Value("InterfaceScale") is ScaleTransform s && s.ScaleX > 0 ? s.ScaleX : 1;
            var width = (On("Explorer.SidebarOnRight") ? side.Right - x : x - side.Left) / scale;
            TryChange(() => _document.Layout("Explorer.SidebarWidth", Math.Round(Math.Clamp(width, 170, 400))));
        };
        _widthGrip.MouseLeftButtonUp += (_, e) => { e.Handled = true; EndGrip(); };
        _widthGrip.LostMouseCapture += (_, _) => EndGrip();
        AutomationProperties.SetName(_widthGrip, "Sidebar width");
        _canvas.Children.Add(_widthGrip);
        line.Opacity = .35;
    }

    private void EndGrip()
    {
        if (!_gripDragging) return;
        _gripDragging = false;
        if (_widthGrip.IsMouseCaptured) _widthGrip.ReleaseMouseCapture();
        EndGesture();
    }

    // The grip sits on the sidebar's inner edge, in Arrange mode, when the usual layout is in use.
    private void PlaceWidthGrip()
    {
        if (!_arranging || _document.LayoutJson is not null || _partDragging || ElementBounds("SidebarPanel") is not { } side)
        {
            if (!_gripDragging) _widthGrip.Visibility = Visibility.Collapsed;
            return;
        }
        var edge = On("Explorer.SidebarOnRight") ? side.Left : side.Right;
        Canvas.SetLeft(_widthGrip, edge - _widthGrip.Width / 2);
        Canvas.SetTop(_widthGrip, side.Top);
        _widthGrip.Height = Math.Max(0, side.Height);
        _widthGrip.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- the panel in Arrange mode

    private void BuildArrangePanel()
    {
        Title("Arrange", "Drag any part of the window to move it. Drag the sidebar's edge to change its width.");
        var custom = _document.LayoutJson is not null;

        Heading("Layout");
        if (custom)
        {
            _body.Children.Add(Note("This theme has a custom layout, so its parts are placed by the layout builder."));
            Pills(Pill("Change it…", OpenLayoutBuilder), Pill("Back to the usual layout", () => TryChange(() => (_document with { LayoutJson = null }).DropUnusedImages())));
        }
        else
        {
            Choices(LayoutPresets.Select(p => (p.Name, false, (Action)(() => TryChange(() => WithLayout(_document, p.Layout))))));
            Pills(Pill("Build a custom layout…", OpenLayoutBuilder, tip: "Put any part anywhere: extra file panes side by side, a browser look, a dashboard, text, pictures, a clock"));
        }

        Heading("Show");
        foreach (var id in new[] { "Sidebar", "Tabs", "Status", "Keys" })
        {
            var part = PartById(id);
            if (part.Hide is not { } hide || custom && hide.StartsWith("Explorer.", StringComparison.Ordinal)) continue;
            Check(part.Label, On(hide), on => TryChange(() => _document.Layout(hide, on ? 1 : 0)));
        }
        ArrangePanelsSection();   // ThemeEditor.Panels.cs: the Shelf and the Inspector

        Heading("Sizes");
        if (!custom)
        {
            var width = Value("Explorer.SidebarWidth") is Thickness t ? t.Left : 228;
            PercentSlider("Sidebar width", Math.Round(width), 170, 400, " px", v => TryChange(() => _document.Layout("Explorer.SidebarWidth", Math.Round(v))));
        }
        var rows = Value("Explorer.RowPadding") is Thickness p ? p.Top : 4;
        PercentSlider("Space between rows", Math.Round(rows), 0, 14, " px", v => TryChange(() => _document.Layout("Explorer.RowPadding", Math.Round(v))));
        var scale = Value("InterfaceScale") is ScaleTransform st ? st.ScaleX : 1;
        _body.Children.Add(Label("Size of everything"));
        Choices(new[] { 90, 100, 110, 125, 150 }.Select(percent => (percent + "%", Math.Abs(scale * 100 - percent) < 1, (Action)(() => TryChange(() => _document.Scale(percent / 100d))))));
        Check("Fade the toolbar when idle", _document.Manifest.FadeToolbar, on => Change(_document with { Manifest = _document.Manifest with { FadeToolbar = on } }));
    }
}
