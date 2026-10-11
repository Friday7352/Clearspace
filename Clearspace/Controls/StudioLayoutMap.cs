using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Clearspace.Controls;

/// <summary>NEW (complex themes): where each part of the explorer goes, as the theme studio's mini map sets it.</summary>
internal sealed record StudioLayout(bool Sidebar, bool SidebarRight, bool ToolbarBottom, bool Tabs, bool TabsBelow,
    bool Status, bool StatusTop, bool PreviewLeft, bool Keys);

// NEW (complex themes): a small map of the Clearspace window in the theme studio. Drag a part (sidebar,
// toolbar, tabs, status bar, preview pane, key bar) to where it should go: the places it can go light up
// while it is dragged. Drop it on the shelf under the map to hide it; drag it back from the shelf to show
// it again. Clicking a part (or Enter on it) opens the same choices as a menu, for anyone who would rather
// not drag. The map only reports a new StudioLayout (Changed); the studio turns that into theme keys.
internal sealed class StudioLayoutMap : StackPanel
{
    private const double W = 300, H = 188;
    private readonly Canvas _map = new() { Width = W, Height = H, HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent };
    private readonly WrapPanel _tray = new();
    private readonly Border _trayFrame;
    private readonly List<(string Part, Rect Area, string Name, Func<StudioLayout, StudioLayout> Apply)> _zones = new();
    private readonly List<(Rectangle Shape, Rect Area)> _zoneShapes = new();
    private readonly Dictionary<string, FrameworkElement> _blocks = new();
    private StudioLayout _layout = new(true, false, false, true, false, true, false, false, false);
    private string? _dragging;
    private Point _start;
    private bool _moved;
    private FrameworkElement? _ghost;
    internal event Action<StudioLayout>? Changed;

    internal StudioLayoutMap()
    {
        Children.Add(_map);
        _trayFrame = new Border { Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(6, 5, 6, 1), MinHeight = 34, BorderThickness = new Thickness(1), Child = _tray, Background = Brushes.Transparent, Width = W, HorizontalAlignment = HorizontalAlignment.Left };
        _trayFrame.SetResourceReference(Border.BorderBrushProperty, "Line");
        _trayFrame.SetResourceReference(Border.CornerRadiusProperty, "R6");
        Children.Add(_trayFrame);
        AutomationProperties.SetName(this, "Layout map. Each part opens a menu of places it can go.");
        Render();
    }

    internal StudioLayout Value
    {
        get => _layout;
        set { if (_layout == value) return; _layout = value; if (_dragging is null) Render(); }
    }

    // ---------------------------------------------------------------- where things are drawn

    private sealed record Plan(Rect Nav, Rect Tabs, Rect Toolbar, Rect Sidebar, Rect Files, Rect Preview, Rect Status, Rect Keys, Rect Body);

    private static Plan Measure(StudioLayout l)
    {
        const double pad = 5, toolH = 20, sideW = 66, previewW = 56;
        var keys = l.Keys ? new Rect(pad, H - pad - 12, W - 2 * pad, 12) : Rect.Empty;
        var lowest = l.Keys ? keys.Top - 4 : H - pad;
        var tabsH = l.Tabs ? 15 : 0;
        var navH = tabsH + toolH;
        var nav = l.ToolbarBottom ? new Rect(pad, lowest - navH, W - 2 * pad, navH) : new Rect(pad, pad, W - 2 * pad, navH);
        var tabsY = l.TabsBelow ? nav.Top + toolH : nav.Top;
        var toolY = l.TabsBelow ? nav.Top : nav.Top + tabsH;
        var tabs = l.Tabs ? new Rect(nav.Left + 6, tabsY + 1, W * .42, tabsH - 3) : Rect.Empty;
        var toolbar = new Rect(nav.Left, toolY + 1, nav.Width, toolH - 2);
        var bodyTop = l.ToolbarBottom ? pad : nav.Bottom + 4;
        var bodyBottom = l.ToolbarBottom ? nav.Top - 4 : lowest;
        var body = new Rect(pad, bodyTop, W - 2 * pad, Math.Max(20, bodyBottom - bodyTop));
        double left = body.Left, right = body.Right;
        var sidebar = Rect.Empty;
        if (l.Sidebar)
        {
            sidebar = l.SidebarRight ? new Rect(right - sideW, body.Top, sideW, body.Height) : new Rect(left, body.Top, sideW, body.Height);
            if (l.SidebarRight) right -= sideW + 4; else left += sideW + 4;
        }
        var preview = l.PreviewLeft ? new Rect(left, body.Top, previewW, body.Height) : new Rect(right - previewW, body.Top, previewW, body.Height);
        if (l.PreviewLeft) left += previewW + 4; else right -= previewW + 4;
        var files = new Rect(left, body.Top, Math.Max(20, right - left), body.Height);
        var status = !l.Status ? Rect.Empty : l.StatusTop
            ? new Rect(files.Left + 3, files.Top + 3, files.Width - 6, 12)
            : new Rect(files.Left + 3, files.Bottom - 15, files.Width - 6, 12);
        return new Plan(nav, tabs, toolbar, sidebar, files, preview, status, keys, body);
    }

    private void Render()
    {
        _map.Children.Clear(); _blocks.Clear(); _zones.Clear(); _zoneShapes.Clear(); _ghost = null;
        var plan = Measure(_layout);

        var frame = new Rectangle { Width = W, Height = H, RadiusX = 7, RadiusY = 7, StrokeThickness = 1 };
        frame.SetResourceReference(Shape.FillProperty, "Base");
        frame.SetResourceReference(Shape.StrokeProperty, "Line");
        _map.Children.Add(frame);

        Block("Files", plan.Files, "Files", "Surface", movable: false);
        Block("Preview", plan.Preview, "Preview", "Tint", dashed: true);
        if (!plan.Sidebar.IsEmpty) Block("Sidebar", plan.Sidebar, "Sidebar", "Tint");
        if (!plan.Status.IsEmpty) Block("Status", plan.Status, "Status bar", "Overlay");
        Block("Toolbar", plan.Toolbar, "Toolbar", "Overlay");
        if (!plan.Tabs.IsEmpty) Block("Tabs", plan.Tabs, "Tabs", "Overlay");
        if (!plan.Keys.IsEmpty) Block("Keys", plan.Keys, "Key bar", "Overlay");

        // The shelf of hidden parts.
        _tray.Children.Clear();
        foreach (var (part, hidden) in new[] { ("Sidebar", !_layout.Sidebar), ("Tabs", !_layout.Tabs), ("Status", !_layout.Status), ("Keys", !_layout.Keys) })
            if (hidden) _tray.Children.Add(Chip(part));
        if (_tray.Children.Count == 0)
        {
            var hint = new TextBlock { Text = "Drop a part here to hide it", FontSize = 11.5, Margin = new Thickness(2, 3, 0, 5) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
            _tray.Children.Add(hint);
        }

        // Where each part may be dropped. Drawn only while that part is being dragged.
        var body = plan.Body; var nav = plan.Nav; var files = plan.Files;
        Zone("Sidebar", new Rect(0, body.Top, W * .34, body.Height), "Left", l => l with { Sidebar = true, SidebarRight = false });
        Zone("Sidebar", new Rect(W * .66, body.Top, W * .34, body.Height), "Right", l => l with { Sidebar = true, SidebarRight = true });
        Zone("Toolbar", new Rect(0, 0, W, H * .3), "Top", l => l with { ToolbarBottom = false });
        Zone("Toolbar", new Rect(0, H * .7, W, H * .3), "Bottom", l => l with { ToolbarBottom = true });
        Zone("Tabs", new Rect(0, nav.Top - 10, W, nav.Height / 2 + 10), "Above the toolbar", l => l with { Tabs = true, TabsBelow = false });
        Zone("Tabs", new Rect(0, nav.Top + nav.Height / 2, W, nav.Height / 2 + 10), "Below the toolbar", l => l with { Tabs = true, TabsBelow = true });
        Zone("Status", new Rect(files.Left, files.Top, files.Width, files.Height / 2), "Top of the files", l => l with { Status = true, StatusTop = true });
        Zone("Status", new Rect(files.Left, files.Top + files.Height / 2, files.Width, files.Height / 2), "Under the files", l => l with { Status = true, StatusTop = false });
        Zone("Preview", new Rect(body.Left, body.Top, body.Width / 2, body.Height), "Left of the files", l => l with { PreviewLeft = true });
        Zone("Preview", new Rect(body.Left + body.Width / 2, body.Top, body.Width / 2, body.Height), "Right of the files", l => l with { PreviewLeft = false });
        Zone("Keys", new Rect(0, H * .78, W, H * .22), "Along the bottom", l => l with { Keys = true });
    }

    private void Zone(string part, Rect area, string name, Func<StudioLayout, StudioLayout> apply) => _zones.Add((part, area, name, apply));

    private void Block(string part, Rect r, string label, string fill, bool movable = true, bool dashed = false)
    {
        if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return;
        var shape = new Rectangle { RadiusX = 3, RadiusY = 3, StrokeThickness = 1 };
        shape.SetResourceReference(Shape.FillProperty, fill);
        shape.SetResourceReference(Shape.StrokeProperty, "Line");
        if (dashed) shape.StrokeDashArray = new DoubleCollection { 3, 2 };
        var text = new TextBlock
        {
            Text = label, FontSize = 9.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(3, 0, 3, 0)
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, part == "Files" ? "InkFaint" : "InkMuted");
        var block = new Grid { Width = r.Width, Height = r.Height, Background = Brushes.Transparent };
        block.Children.Add(shape); block.Children.Add(text);
        Canvas.SetLeft(block, r.X); Canvas.SetTop(block, r.Y);
        _map.Children.Add(block);
        if (!movable) return;
        _blocks[part] = block;
        Movable(block, part, shape);
    }

    private FrameworkElement Chip(string part)
    {
        var shape = new Rectangle { RadiusX = 3, RadiusY = 3, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 } };
        shape.SetResourceReference(Shape.FillProperty, "Tint");
        shape.SetResourceReference(Shape.StrokeProperty, "Line");
        var text = new TextBlock { Text = PartName(part), FontSize = 11, Margin = new Thickness(9, 3, 9, 3), VerticalAlignment = VerticalAlignment.Center };
        var chip = new Grid { Margin = new Thickness(0, 0, 6, 4), Background = Brushes.Transparent };
        chip.Children.Add(shape); chip.Children.Add(text);
        _blocks[part] = chip;
        Movable(chip, part, shape);
        chip.ToolTip = "Hidden. Drag it onto the map to show it, or click for choices";
        return chip;
    }

    private void Movable(FrameworkElement element, string part, Rectangle shape)
    {
        element.Cursor = Cursors.SizeAll;
        element.Focusable = true;
        element.ToolTip ??= "Drag to move it, or click for choices";
        AutomationProperties.SetName(element, PartName(part));
        element.MouseEnter += (_, _) => { shape.SetResourceReference(Shape.StrokeProperty, "Accent"); shape.StrokeThickness = 1.5; };
        element.MouseLeave += (_, _) => { shape.SetResourceReference(Shape.StrokeProperty, "Line"); shape.StrokeThickness = 1; };
        element.MouseLeftButtonDown += (_, e) =>
        {
            _dragging = part; _start = e.GetPosition(this); _moved = false;
            CaptureMouse(); e.Handled = true;
        };
        element.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { e.Handled = true; ShowMenu(part, element); } };
    }

    private static string PartName(string part) => part switch
    {
        "Status" => "Status bar", "Keys" => "Key bar", "Preview" => "Preview pane", _ => part
    };

    // ---------------------------------------------------------------- dragging

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging is not { } part || !IsMouseCaptured) return;
        var p = e.GetPosition(this);
        if (!_moved && (p - _start).Length < 4) return;
        if (!_moved)
        {
            _moved = true;
            ShowZones(part);
            _ghost = Ghost(part);
            _map.Children.Add(_ghost);
        }
        var m = e.GetPosition(_map);
        Canvas.SetLeft(_ghost!, m.X - _ghost!.Width / 2); Canvas.SetTop(_ghost!, m.Y - _ghost!.Height / 2);
        foreach (var (shape, area) in _zoneShapes)
            shape.SetResourceReference(Shape.FillProperty, area.Contains(m) ? "AccentSoft" : "AccentWash");
        _trayFrame.SetResourceReference(Border.BorderBrushProperty, InTray(e) ? "Accent" : "Line");
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging is not { } part) return;
        var moved = _moved;
        _dragging = null; _moved = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _trayFrame.SetResourceReference(Border.BorderBrushProperty, "Line");
        if (!moved)
        {
            if (_blocks.TryGetValue(part, out var target)) ShowMenu(part, target);
            return;
        }
        StudioLayout? next = null;
        if (InTray(e)) next = Hide(part, _layout);
        else
        {
            var m = e.GetPosition(_map);
            foreach (var zone in _zones)
                if (zone.Part == part && zone.Area.Contains(m)) { next = zone.Apply(_layout); break; }
        }
        Commit(next);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging is null) return;
        _dragging = null; _moved = false;
        _trayFrame.SetResourceReference(Border.BorderBrushProperty, "Line");
        Render();
    }

    private void Commit(StudioLayout? next)
    {
        if (next is not null && next != _layout)
        {
            _layout = next;
            Render();
            Changed?.Invoke(next);
        }
        else Render();
    }

    private bool InTray(MouseEventArgs e)
    {
        var t = e.GetPosition(_trayFrame);
        return t.X >= 0 && t.Y >= -6 && t.X <= _trayFrame.ActualWidth && t.Y <= _trayFrame.ActualHeight + 6;
    }

    private void ShowZones(string part)
    {
        var bounds = new Rect(0, 0, W, H);
        foreach (var zone in _zones.Where(z => z.Part == part))
        {
            var area = Rect.Intersect(zone.Area, bounds);
            if (area.IsEmpty || area.Width < 2 || area.Height < 2) continue;
            var shape = new Rectangle { Width = area.Width, Height = area.Height, RadiusX = 4, RadiusY = 4, StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 4, 2 }, IsHitTestVisible = false };
            shape.SetResourceReference(Shape.FillProperty, "AccentWash");
            shape.SetResourceReference(Shape.StrokeProperty, "Accent");
            Canvas.SetLeft(shape, area.X); Canvas.SetTop(shape, area.Y);
            _map.Children.Add(shape);
            var label = new TextBlock { Text = zone.Name, FontSize = 10, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            Canvas.SetLeft(label, area.X + 5); Canvas.SetTop(label, area.Y + 3);
            _map.Children.Add(label);
            _zoneShapes.Add((shape, area));
        }
    }

    private FrameworkElement Ghost(string part)
    {
        var shape = new Rectangle { RadiusX = 3, RadiusY = 3, StrokeThickness = 1.5, Opacity = .9 };
        shape.SetResourceReference(Shape.FillProperty, "AccentSoft");
        shape.SetResourceReference(Shape.StrokeProperty, "Accent");
        var text = new TextBlock { Text = PartName(part), FontSize = 10, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var ghost = new Grid { Width = 78, Height = 22, IsHitTestVisible = false };
        ghost.Children.Add(shape); ghost.Children.Add(text);
        return ghost;
    }

    // ---------------------------------------------------------------- the same choices as a menu

    private static StudioLayout Hide(string part, StudioLayout l) => part switch
    {
        "Sidebar" => l with { Sidebar = false }, "Tabs" => l with { Tabs = false },
        "Status" => l with { Status = false }, "Keys" => l with { Keys = false }, _ => l
    };

    private (string Label, bool Current, StudioLayout Next)[] Choices(string part)
    {
        var l = _layout;
        return part switch
        {
            "Sidebar" => new (string, bool, StudioLayout)[] {
                ("Left", l.Sidebar && !l.SidebarRight, l with { Sidebar = true, SidebarRight = false }),
                ("Right", l.Sidebar && l.SidebarRight, l with { Sidebar = true, SidebarRight = true }),
                ("Hidden", !l.Sidebar, l with { Sidebar = false }) },
            "Toolbar" => new (string, bool, StudioLayout)[] {
                ("Top", !l.ToolbarBottom, l with { ToolbarBottom = false }),
                ("Bottom", l.ToolbarBottom, l with { ToolbarBottom = true }) },
            "Tabs" => new (string, bool, StudioLayout)[] {
                ("Above the toolbar", l.Tabs && !l.TabsBelow, l with { Tabs = true, TabsBelow = false }),
                ("Below the toolbar", l.Tabs && l.TabsBelow, l with { Tabs = true, TabsBelow = true }),
                ("Hidden", !l.Tabs, l with { Tabs = false }) },
            "Status" => new (string, bool, StudioLayout)[] {
                ("Under the files", l.Status && !l.StatusTop, l with { Status = true, StatusTop = false }),
                ("Top of the files", l.Status && l.StatusTop, l with { Status = true, StatusTop = true }),
                ("Hidden", !l.Status, l with { Status = false }) },
            "Preview" => new (string, bool, StudioLayout)[] {
                ("Right of the files", !l.PreviewLeft, l with { PreviewLeft = false }),
                ("Left of the files", l.PreviewLeft, l with { PreviewLeft = true }) },
            "Keys" => new (string, bool, StudioLayout)[] {
                ("Along the bottom", l.Keys, l with { Keys = true }),
                ("Hidden", !l.Keys, l with { Keys = false }) },
            _ => Array.Empty<(string, bool, StudioLayout)>()
        };
    }

    private void ShowMenu(string part, FrameworkElement target)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = PartName(part), IsEnabled = false, FontWeight = FontWeights.SemiBold });
        foreach (var (label, current, next) in Choices(part))
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = current };
            item.Click += (_, _) => Commit(next);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
