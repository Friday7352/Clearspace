using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Clearspace.Services;
using Shapes = System.Windows.Shapes;

namespace Clearspace;

// NEW (edit in place): the window side of the editor.
// REWRITTEN (side panel): the window now only outlines the part under the pointer, picks a part with a click (its
// settings open in the side panel), moves a part when you drag it (the places it can go light up), and moves or
// resizes stickers. A right-click lists every color the spot under the pointer is drawn with.
// REMOVED (side panel): the brush rack (Pick, Fill, Stripes, Dots, Waves, Glow, Outline, Stamp, Eraser, the paint
// pot and recent colors) and the floating bar of small icons on a picked part. Their jobs are rows in the panel.
internal sealed partial class ThemeEditor
{
    // ---------------------------------------------------------------- the parts of the window

    // A part of the window: the element that shows it, the theme keys that style it, the switch that hides it,
    // where a pattern for it goes, and the places it can move to.
    private sealed record CanvasPart(string Id, string Label, string Element, string? Fill, string? Edge, string? EdgeWidth, string? Corner,
        string? Hide, string? PatternTarget, string[] Places);

    // In the order they are hit: a part inside another (the status bar in the file area, the tabs on the toolbar)
    // comes first. The window itself is last.
    private static readonly CanvasPart[] CanvasParts =
    [
        new("Keys", "Key bar", "KeyBar", "BadgeFill", null, null, null, "FunctionKeysVisibility", null, []),
        new("Status", "Status bar", "StatusBar", "Part.Status.Fill", "Part.Status.Edge", null, null, "Explorer.StatusBarVisibility", "Part.Status.Fill", ["Bottom", "Top"]),
        new("Tabs", "Tabs", "TabRow", "Part.Toolbar.Fill", null, null, null, "Explorer.TabsVisibility", "Part.Toolbar.Fill", ["Above", "Below"]),
        new("Toolbar", "Toolbar", "NavigationArea", "Part.Toolbar.Fill", "Part.Toolbar.Edge", "Part.Toolbar.EdgeWidth", "Part.Toolbar.Corner", null, "Part.Toolbar.Fill", ["Top", "Bottom"]),
        new("Preview", "Preview pane", "PreviewCard", "Part.Files.Fill", "Part.Files.Edge", "Part.Files.EdgeWidth", "Part.Files.Corner", null, "Part.Files.Fill", ["Right", "Left"]),
        new("Sidebar", "Sidebar", "SidebarPanel", "Part.Sidebar.Fill", "Part.Sidebar.Edge", "Part.Sidebar.EdgeWidth", "Part.Sidebar.Corner", "Explorer.SidebarVisibility", "Part.Sidebar.Fill", ["Left", "Right"]),
        new("Files", "File area", "ContentCard", "Part.Files.Fill", "Part.Files.Edge", "Part.Files.EdgeWidth", "Part.Files.Corner", null, "Part.Files.Fill", []),
        new("Window", "Window", "", "Base", null, null, null, null, "Base", [])
    ];

    private static CanvasPart PartById(string id) => CanvasParts.First(p => p.Id == id);
    private CanvasPart? SelectedPart() => _selected is { } id ? PartById(id) : null;
    /// <summary>The part picked on the window, or null. For tests.</summary>
    internal string? SelectedPartId => _selected;

    private readonly Canvas _canvas = new() { Background = Brushes.Transparent, ClipToBounds = true, AllowDrop = true };
    private readonly Shapes.Rectangle _hoverBox = Outline(1.5, dashed: true), _selectBox = Outline(2.5, dashed: false), _snapBox = Outline(2, dashed: true);
    private readonly Border _hoverTag = NameTag(muted: true), _selectTag = NameTag(muted: false), _snapTag = NameTag(muted: false), _ghost = NameTag(muted: false);
    private readonly Grid _stickerHandle = new() { Visibility = Visibility.Collapsed, Cursor = Cursors.SizeAll };
    private readonly Border _stickerGrip = new() { Width = 16, Height = 16, CornerRadius = new CornerRadius(3), Cursor = Cursors.SizeNWSE, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -8, -8) };

    private string? _selected, _sticker;
    private bool _pressed, _partDragging, _stickerDragging, _stickerResize;
    private Point _pressPoint, _dragStart;
    private CanvasPart? _pressPart;
    private Rect _dragBox, _dragStartBox;
    private (string Key, string Hex)? _colorPreview;
    private readonly DispatcherTimer _colorTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };

    // ---------------------------------------------------------------- small building blocks

    private static Shapes.Rectangle Outline(double width, bool dashed)
    {
        var box = new Shapes.Rectangle { StrokeThickness = width, RadiusX = 6, RadiusY = 6, IsHitTestVisible = false, Visibility = Visibility.Collapsed, SnapsToDevicePixels = true };
        if (dashed) box.StrokeDashArray = new DoubleCollection { 4, 3 };
        box.SetResourceReference(Shapes.Shape.StrokeProperty, "Accent");
        return box;
    }

    private static Border NameTag(bool muted)
    {
        var text = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold };
        var tag = new Border { Padding = new Thickness(8, 3, 8, 4), CornerRadius = new CornerRadius(4), Child = text, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        tag.SetResourceReference(Border.BackgroundProperty, muted ? "Overlay" : "Accent");
        text.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Ink" : "AccentInk");
        return tag;
    }

    private static void SetTag(Border tag, string text) => ((TextBlock)tag.Child).Text = text;

    private static void Place(FrameworkElement shape, Rect area)
    {
        Canvas.SetLeft(shape, area.X); Canvas.SetTop(shape, area.Y);
        shape.Width = Math.Max(0, area.Width); shape.Height = Math.Max(0, area.Height);
    }

    private static Border Dot(Brush fill, double size = 18)
    {
        var dot = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), BorderThickness = new Thickness(1.5), Background = fill };
        dot.SetResourceReference(Border.BorderBrushProperty, "Line");
        return dot;
    }

    // ---------------------------------------------------------------- building

    private void BuildCanvas()
    {
        foreach (var element in new FrameworkElement[] { _hoverBox, _hoverTag, _selectBox, _selectTag, _snapBox, _snapTag })
            _canvas.Children.Add(element);
        _snapBox.SetResourceReference(Shapes.Shape.FillProperty, "AccentWash");
        BuildStickerHandle();
        BuildWidthGrip();   // NEW (Arrange mode): the sidebar's edge, ThemeEditor.Arrange.cs
        _canvas.Children.Add(_ghost);
        _canvas.MouseLeftButtonDown += (_, e) => Down(e);
        _canvas.MouseMove += (_, e) => Move(e);
        _canvas.MouseLeftButtonUp += (_, e) => Up(e);
        _canvas.MouseLeave += (_, _) => { if (!_pressed) ShowHover(null); };
        _canvas.LostMouseCapture += (_, _) => { if (_pressed) CancelPress(); };
        _canvas.MouseRightButtonUp += (_, e) => { e.Handled = true; ColorsAt(e.GetPosition(_canvas)); };
        _canvas.DragOver += CardDragOver;    // ThemeEditor.Cards.cs
        _canvas.DragEnter += CardDragOver;
        _canvas.DragLeave += (_, _) => HideSnap();
        _canvas.Drop += CardDrop;
        Children.Add(_canvas);
        // The colour picker's live preview: the window follows the picker without an undo step per move.
        _colorTimer.Tick += (_, _) =>
        {
            _colorTimer.Stop();
            if (_colorPreview is { } preview)
                try { ThemeService.ShowDraft(SetColor(_document, preview.Key, preview.Hex)); }
                catch (Exception e) when (e is ArgumentException or ThemeFormatException) { }
        };
    }

    // ---------------------------------------------------------------- where things are

    private Rect Whole => new(0, 0, _canvas.ActualWidth, _canvas.ActualHeight);

    private Rect? ElementBounds(string name)
    {
        if (!IsVisible || _root.FindName(name) is not FrameworkElement element || !element.IsVisible || element.ActualWidth < 2 || element.ActualHeight < 2) return null;
        try { return element.TransformToVisual(_canvas).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight)); }
        catch (InvalidOperationException) { return null; }
    }

    private Rect? PartBounds(CanvasPart part) => part.Id == "Window" ? Whole : ElementBounds(part.Element);

    private CanvasPart? PartAt(Point point)
    {
        if (!Whole.Contains(point)) return null;
        foreach (var part in CanvasParts)
        {
            if (part.Id == "Window") return part;
            if (PartBounds(part) is { } bounds && bounds.Contains(point)) return part;
        }
        return null;
    }

    private Rect StickerRect(StudioSticker sticker)
    {
        var a = Whole;
        return new Rect(a.X + sticker.Box.X * a.Width, a.Y + sticker.Box.Y * a.Height, sticker.Box.Width * a.Width, sticker.Box.Height * a.Height);
    }

    private string? StickerAt(Point point, bool front)
    {
        foreach (var sticker in _document.Stickers().Where(s => s.Front == front).Reverse())
        {
            if (sticker.Cover || Same(sticker.Box, new Rect(0, 0, 1, 1))) continue;
            if (StickerRect(sticker).Contains(point)) return sticker.Key;
        }
        return null;
    }

    private static bool Same(Rect a, Rect b) => Math.Abs(a.X - b.X) < .005 && Math.Abs(a.Y - b.Y) < .005 && Math.Abs(a.Width - b.Width) < .005 && Math.Abs(a.Height - b.Height) < .005;

    private StudioSticker? CurrentSticker() => _sticker is { } key ? _document.Stickers().FirstOrDefault(s => s.Key == key) : null;

    private bool Movable(CanvasPart part) => _document.LayoutJson is null && part.Places.Length > 0;

    private string? PlaceOf(CanvasPart part) => part.Id switch
    {
        "Status" => On("Explorer.StatusBarTop") ? "Top" : "Bottom",
        "Tabs" => On("Explorer.TabsBelowToolbar") ? "Below" : "Above",
        "Toolbar" => On("Explorer.ToolbarBottom") ? "Bottom" : "Top",
        "Preview" => On("Explorer.PreviewOnLeft") ? "Left" : "Right",
        "Sidebar" => On("Explorer.SidebarOnRight") ? "Right" : "Left",
        _ => null
    };

    private static (string Key, double On)? PlaceSetting(string id, string place) => (id, place) switch
    {
        ("Status", "Top") => ("Explorer.StatusBarTop", 1d), ("Status", "Bottom") => ("Explorer.StatusBarTop", 0d),
        ("Tabs", "Below") => ("Explorer.TabsBelowToolbar", 1d), ("Tabs", "Above") => ("Explorer.TabsBelowToolbar", 0d),
        ("Toolbar", "Bottom") => ("Explorer.ToolbarBottom", 1d), ("Toolbar", "Top") => ("Explorer.ToolbarBottom", 0d),
        ("Preview", "Left") => ("Explorer.PreviewOnLeft", 1d), ("Preview", "Right") => ("Explorer.PreviewOnLeft", 0d),
        ("Sidebar", "Right") => ("Explorer.SidebarOnRight", 1d), ("Sidebar", "Left") => ("Explorer.SidebarOnRight", 0d),
        _ => null
    };

    private static string PlaceLabel(CanvasPart part, string place) => (part.Id, place) switch
    {
        ("Tabs", "Above") => "Above toolbar", ("Tabs", "Below") => "Below toolbar",
        ("Status", "Top") => "Over files", ("Status", "Bottom") => "Under files",
        _ => place
    };

    // ---------------------------------------------------------------- pointing and dragging

    private void Down(MouseButtonEventArgs e)
    {
        if (_finished) return;
        var point = e.GetPosition(_canvas);
        e.Handled = true;
        Focus();
        if (StickerAt(point, front: true) is { } key)
        {
            Select(null, key);
            BeginStickerDrag(point, resize: false);
            return;
        }
        _pressed = true; _pressPoint = point; _pressPart = PartAt(point);
        _canvas.CaptureMouse();
    }

    private void Move(MouseEventArgs e)
    {
        if (_finished) return;
        var point = e.GetPosition(_canvas);
        if (_stickerDragging) { DragSticker(point); return; }
        if (_pressed)
        {
            // CHANGED (Arrange mode): parts are dragged only in Arrange mode (in Style mode a click picks them).
            if (!_partDragging && _arranging && _pressPart is { } part && Movable(part) && (point - _pressPoint).Length > 4) StartPartDrag(part);
            if (_partDragging) UpdatePartDrag(point);
            return;
        }
        var sticker = StickerAt(point, front: true) is not null;
        var hit = sticker ? null : PartAt(point);
        ShowHover(hit?.Id == _selected ? null : hit);
        _canvas.Cursor = sticker ? Cursors.SizeAll : hit is null ? null : _arranging ? (hit.Id != "Window" && Movable(hit) ? Cursors.SizeAll : null) : Cursors.Hand;
    }

    private void Up(MouseButtonEventArgs e)
    {
        var point = e.GetPosition(_canvas);
        e.Handled = true;
        if (_stickerDragging) { EndStickerDrag(); return; }
        if (!_pressed) return;
        _pressed = false;
        var dragged = _partDragging;
        var part = _pressPart;
        _canvas.ReleaseMouseCapture();
        if (dragged && part is not null) { FinishPartDrag(); return; }
        if (_arranging) { ArrangeClick(part); return; }   // NEW (Arrange mode): ThemeEditor.Arrange.cs
        if (part is null) { Select(null); return; }
        if (part.Id == "Window" && StickerAt(point, front: false) is { } back) { Select(null, back); return; }
        Select(part.Id);
    }

    /// <summary>Lets go of a drag in progress. Returns whether there was one.</summary>
    private bool CancelPress()
    {
        if (!_pressed && !_stickerDragging) return false;
        _pressed = false; _stickerDragging = false;
        HideSnap();
        if (_partDragging) CancelPartDrag();   // CHANGED (precise dragging): puts the window back as it was
        if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
        if (_stickerHandle.IsMouseCaptured) _stickerHandle.ReleaseMouseCapture();
        RefreshCanvas();
        return true;
    }

    private void ShowHover(CanvasPart? part)
    {
        if (part is null || _pressed || PartBounds(part) is not { } bounds)
        {
            _hoverBox.Visibility = _hoverTag.Visibility = Visibility.Collapsed;
            return;
        }
        Place(_hoverBox, bounds);
        _hoverBox.Visibility = Visibility.Visible;
        SetTag(_hoverTag, part.Label);
        Canvas.SetLeft(_hoverTag, bounds.Left + 6); Canvas.SetTop(_hoverTag, bounds.Top + 6);
        _hoverTag.Visibility = Visibility.Visible;
    }

    private void ShowSnap(CanvasPart part, string text)
    {
        ShowHover(null);
        if (PartBounds(part) is not { } bounds) return;
        Place(_snapBox, bounds);
        _snapBox.Visibility = Visibility.Visible;
        SetTag(_snapTag, text);
        _snapTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_snapTag, Math.Max(4, bounds.Right - _snapTag.DesiredSize.Width - 8)); Canvas.SetTop(_snapTag, bounds.Top + 6);
        _snapTag.Visibility = Visibility.Visible;
    }

    private void HideSnap() => _snapBox.Visibility = _snapTag.Visibility = Visibility.Collapsed;

    // ---------------------------------------------------------------- colours, patterns, corners

    private static string Hex(Color color) => StudioUi.Hex(color, opaque: true);

    private static bool TryHex(string hex, out Color color)
    {
        color = default;
        try { if (ColorConverter.ConvertFromString(hex) is not Color parsed) return false; color = parsed; return true; }
        catch (Exception e) when (e is FormatException or NotSupportedException) { return false; }
    }

    private static ThemeStudioDocument SetColor(ThemeStudioDocument document, string key, string hex)
    {
        if (key != "Base") return document.Color(key, hex);
        var opaque = hex.Length == 9 ? "#FF" + hex[3..] : hex;
        return document.Color("Base", opaque).Color("Theme.BaseColor", opaque);
    }

    private void PickColor(string key, string label, Window? over = null)
    {
        var start = Value(key) switch { SolidColorBrush b => b.Color.ToString(), GradientBrush { GradientStops.Count: > 0 } g => g.GradientStops[0].Color.ToString(), Color c => c.ToString(), _ => Hex(Solid(key)) };
        var picker = new StudioColorWindow(over ?? _owner, start, key != "Base", hex => { _colorPreview = (key, hex); _colorTimer.Stop(); _colorTimer.Start(); }, label);
        var chosen = picker.ShowDialog() == true;
        _colorTimer.Stop(); _colorPreview = null;
        if (chosen) TryChange(() => SetColor(_document, key, picker.SelectedColor));
        else ShowDraft();
    }

    // What sits on a pattern place now, in a word.
    private string PatternName(string key) => Value(key) switch
    {
        ImageBrush => "Picture",
        DrawingBrush or VisualBrush => "Pattern",
        GradientBrush => "Blend",
        _ => "None"
    };

    private ThemeStudioDocument WithPattern(string key, System.Xml.Linq.XElement brush)
    {
        Color? under = key == "Base" ? Solid("Base") : key == "Part.Files.Fill" ? Solid("Surface") : null;
        var next = _document.Paint(key, brush);
        return key == "Base" && under is { } flat ? next.Color("Theme.BaseColor", Hex(flat)) : next;
    }

    private void PatternMenu(FrameworkElement target, string key)
    {
        var menu = Menu(target);
        Color? under = key == "Base" ? Solid("Base") : key == "Part.Files.Fill" ? Solid("Surface") : null;
        Item(menu, "None", () => TryChange(() => key == "Base" ? _document.Color("Base", Hex(Solid("Base"))) : _document.Without(key)));
        foreach (var kind in Enum.GetValues<StudioPattern>())
            Item(menu, StudioPatterns.Label(kind), () => TryChange(() => WithPattern(key, StudioPatterns.Pattern(kind, Solid("Ink"), Solid("Accent"), under, 1, .3))));
        var mine = StudioLibrary.Patterns();
        if (mine.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var pattern in mine)
                Item(menu, pattern.Name, () => TryChange(() => WithPattern(key, StudioPatterns.Custom(pattern, Solid("Ink"), Solid("Accent"), under, 1, .3))));
        }
        menu.Items.Add(new Separator());
        Item(menu, "Draw a pattern…", () => DrawPattern(key));
        menu.IsOpen = true;
    }

    // The pattern maker; what is drawn goes on the given place (the window when none).
    private void DrawPattern(string? key)
    {
        key ??= "Base";
        Color? under = key == "Base" ? Solid("Base") : key == "Part.Files.Fill" ? Solid("Surface") : null;
        var maker = new StudioPatternWindow(_owner, Solid("Ink"), Solid("Accent"), under ?? Solid("Surface"), null);
        if (maker.ShowDialog() != true || maker.Result is not { } drawn) return;
        TryChange(() => WithPattern(key, StudioPatterns.Custom(drawn, Solid("Ink"), Solid("Accent"), under, 1, .3)));
    }

    // A part back to the theme's own look (what the old Eraser brush did).
    private void ResetPart(CanvasPart part)
    {
        if (part.Id == "Window")
        {
            TryChange(() =>
            {
                var next = _document;
                if (Value("Base") is DrawingBrush or VisualBrush or GradientBrush or ImageBrush) next = next.Color("Base", Hex(Solid("Base")));
                return next.Without("EdgeShade");
            });
            return;
        }
        var keys = new[] { part.Fill, part.Edge, part.EdgeWidth, part.Corner }.OfType<string>().Where(k => k.StartsWith("Part.", StringComparison.Ordinal)).ToArray();
        if (keys.Length > 0) TryChange(() => _document.Without(keys));
    }

    /// <summary>For tests: what the panel's rows do to a part. "Fill" and "Outline" color it, "Stripes", "Dots"
    /// and "Waves" put a pattern on it, and "Eraser" puts it back to the theme's own look.</summary>
    internal void Paint(string partId, string brush, Color paint)
    {
        var part = PartById(partId);
        var hex = Hex(paint);
        switch (brush)
        {
            case "Fill" when part.Fill is { } fill: TryChange(() => SetColor(_document, fill, hex)); break;
            case "Outline" when part.Edge is { } edge:
                TryChange(() =>
                {
                    var next = _document.Color(edge, hex);
                    return part.EdgeWidth is { } width && !(Value(width) is Thickness t && t.Left > 0) ? next.Spacing(width, 2, 2, 2, 2) : next;
                });
                break;
            case "Stripes" or "Dots" or "Waves" when part.PatternTarget is { } target:
            {
                var kind = brush switch { "Stripes" => StudioPattern.Stripes, "Dots" => StudioPattern.Dots, _ => StudioPattern.Waves };
                Color? under = target == "Base" ? Solid("Base") : target == "Part.Files.Fill" ? Solid("Surface") : null;
                TryChange(() => WithPattern(target, StudioPatterns.Pattern(kind, paint, Solid("Accent"), under, 1, .35)));
                break;
            }
            case "Eraser": ResetPart(part); break;
        }
    }

    // Every theme colour and font the spot under the pointer is drawn with (matching the very brushes the theme
    // handed out), as a menu. A right-click anywhere, or "Every color in it" in the panel.
    private void ColorsAt(Point point)
    {
        if (_finished) return;
        var at = _canvas.TranslatePoint(point, _root);
        var found = new List<string>();
        DependencyObject? node = null;
        VisualTreeHelper.HitTest(_root, d => ReferenceEquals(d, this) ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            r => { node = r.VisualHit; return HitTestResultBehavior.Stop; }, new PointHitTestParameters(at));
        var own = ThemeService.Active;
        var below = Application.Current?.Resources.MergedDictionaries.Where(d => !ReferenceEquals(d, own)).ToArray() ?? [];
        for (var depth = 0; node is not null && depth < 16 && found.Count < 9; depth++)
        {
            foreach (var value in ValuesOn(node))
                foreach (var key in KeysFor(value, own, below))
                    if (!found.Contains(key)) found.Add(key);
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(new MenuItem { Header = found.Count == 0 ? "Nothing to change here" : "Change", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        foreach (var key in found)
        {
            ThemeKeys.TryGetKind(key, out var kind);
            var label = ThemeSettingsWindow.SettingLabel(key);
            if (kind == ThemeValue.Font) { Item(menu, label, () => FontsMenu(_canvas, key, atPointer: true)); continue; }
            var item = Item(menu, label, () => PickColor(key, label));
            item.Icon = Dot(Value(key) switch { Brush b => b, Color c => new SolidColorBrush(c), _ => Brushes.Transparent }, 14);
        }
        menu.IsOpen = true;
    }

    private void PartColors(CanvasPart part)
    {
        if (PartBounds(part) is not { } bounds) return;
        ColorsAt(new Point(bounds.Left + bounds.Width / 2, bounds.Top + Math.Min(bounds.Height / 2, 30)));
    }

    private static IEnumerable<object?> ValuesOn(DependencyObject node)
    {
        switch (node)
        {
            case Border b: yield return b.Background; yield return b.BorderBrush; break;
            case TextBlock t: yield return t.Foreground; yield return t.Background; yield return t.FontFamily; break;
            case Shapes.Shape shape: yield return shape.Fill; yield return shape.Stroke; break;
            case Control c: yield return c.Background; yield return c.Foreground; yield return c.BorderBrush; yield return c.FontFamily; break;
            case System.Windows.Controls.Panel p: yield return p.Background; break;
        }
    }

    private static IEnumerable<string> KeysFor(object? value, ResourceDictionary? own, IEnumerable<ResourceDictionary> below)
    {
        if (value is null) yield break;
        if (own is not null)
            foreach (var key in own.Keys.OfType<string>())
                if (ReferenceEquals(own[key], value) && ThemeKeys.TryGetKind(key, out var kind) && kind is not ThemeValue.Corner) yield return key;
        foreach (var dictionary in below)
            foreach (var inner in dictionary.MergedDictionaries.Prepend(dictionary))
                foreach (var key in inner.Keys.OfType<string>())
                    if ((own is null || !own.Contains(key)) && ThemeKeys.TryGetKind(key, out _) && ReferenceEquals(inner[key], value)) yield return key;
    }

    // ---------------------------------------------------------------- selecting

    /// <summary>Selects a part of the window by id ("Sidebar", "Toolbar", "Tabs", "Files", "Status", "Preview",
    /// "Keys", "Window"), or a sticker by key, or nothing. Its settings show in the panel's Part tab.</summary>
    internal void Select(string? partId, string? sticker = null)
    {
        if (partId is not null && CanvasParts.All(p => p.Id != partId)) return;
        _selected = partId;
        _sticker = partId is null ? sticker : null;
        if (_selected is not null || _sticker is not null)
        {
            _pickedCard = null;   // NEW (hand of cards): one thing picked at a time
            _iconForm = false;
            ShowTab("Cards");     // CHANGED (cards first): a part's box is at the top of the Cards tab
        }
        else SyncPanel();
        PaintHand();              // NEW (cards first): the cards the part's look is on rise in the hand
        RefreshCanvas();
    }

    private void SyncCanvas()
    {
        if (_sticker is not null && CurrentSticker() is null) _sticker = null;
        RefreshCanvas();
    }

    // ---------------------------------------------------------------- moving and hiding parts

    /// <summary>Moves a part to one of its places ("Left", "Top", "Below", …), or hides it ("Hidden").</summary>
    internal void PlacePart(string id, string place)
    {
        var part = PartById(id);
        if (place == "Hidden")
        {
            if (part.Hide is not { } hide) return;
            if (_document.LayoutJson is not null && hide.StartsWith("Explorer.", StringComparison.Ordinal)) { Report("This theme has a custom layout: change it on the Layout card.", true); return; }
            if (_selected == id) { _selected = null; }
            TryChange(() => _document.Layout(hide, 0));
            Report($"{part.Label} hidden. Pick a part (Cards tab) lists it to bring back.");
            return;
        }
        if (_document.LayoutJson is not null) { Report("This theme has a custom layout: change it on the Layout card.", true); return; }
        if (PlaceSetting(id, place) is not { } setting) return;
        TryChange(() =>
        {
            var next = _document.Layout(setting.Key, setting.On);
            return part.Hide is { } hide ? next.Layout(hide, 1) : next;
        });
    }

    /// <summary>Shows a hidden part again and selects it.</summary>
    internal void ShowPart(string id)
    {
        if (PartById(id).Hide is not { } hide) return;
        TryChange(() => _document.Layout(hide, 1));
        Select(id);
    }

    // REMOVED (precise dragging): StartPartDrag, UpdatePartDrag, FinishPartDrag, ClearPartDrag and PlaceZone,
    // which lit up guessed drop boxes. Parts are now dragged in Arrange mode: ThemeEditor.Arrange.cs.

    private void DeleteSelected()
    {
        if (CurrentSticker() is { } sticker) { RemoveSticker(sticker); return; }
        if (SelectedPart() is { Hide: not null } part) PlacePart(part.Id, "Hidden");
    }

    private void MakeCard(CanvasPart part)
    {
        var keys = new[] { part.Fill, part.Edge, part.EdgeWidth, part.Corner }.OfType<string>().ToArray();
        if (ThemeCards.FromKeys(_document, CardKind.Panels, part.Label + " style", "Fill, outline and corners", ThemeManifest.Tidy(_name.Text, 60), keys) is not { } card)
        { Report($"The {part.Label.ToLowerInvariant()} uses the theme's own look, so there is nothing to make a card of yet.", true); return; }
        try { ThemeCards.Keep(card); RefreshSavedCards(); Report($"Kept a card of the {part.Label.ToLowerInvariant()}'s look. It is in the Cards tab under My cards."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Report("Could not keep the card: " + e.Message, true); }
    }

    // ---------------------------------------------------------------- stickers

    private void BuildStickerHandle()
    {
        var outline = new Shapes.Rectangle { StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 }, RadiusX = 4, RadiusY = 4 };
        outline.SetResourceReference(Shapes.Shape.StrokeProperty, "Accent");
        outline.SetResourceReference(Shapes.Shape.FillProperty, "AccentWash");
        _stickerGrip.SetResourceReference(Border.BackgroundProperty, "Accent");
        _stickerGrip.ToolTip = "Drag to resize";
        _stickerHandle.Children.Add(outline);
        _stickerHandle.Children.Add(_stickerGrip);
        _stickerHandle.ToolTip = "Drag to move this sticker";
        _canvas.Children.Add(_stickerHandle);
        _stickerHandle.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            var p = e.GetPosition(_canvas);
            var grip = e.GetPosition(_stickerGrip);
            BeginStickerDrag(p, resize: grip.X >= -4 && grip.Y >= -4 && grip.X <= _stickerGrip.ActualWidth + 4 && grip.Y <= _stickerGrip.ActualHeight + 4);
        };
        _stickerHandle.MouseMove += (_, e) => { if (_stickerDragging) { e.Handled = true; DragSticker(e.GetPosition(_canvas)); } };
        _stickerHandle.MouseLeftButtonUp += (_, e) => { e.Handled = true; EndStickerDrag(); };
    }

    private void BeginStickerDrag(Point point, bool resize)
    {
        if (CurrentSticker() is not { } sticker) return;
        _stickerDragging = true; _stickerResize = resize;
        _dragStart = point;
        _dragStartBox = _dragBox = sticker.Box;
        PlaceStickerHandle();
        _stickerHandle.CaptureMouse();
    }

    private void DragSticker(Point point)
    {
        var a = Whole;
        if (a.Width <= 0 || a.Height <= 0) return;
        var dx = (point.X - _dragStart.X) / a.Width; var dy = (point.Y - _dragStart.Y) / a.Height;
        var b = _dragStartBox;
        _dragBox = _stickerResize ? new Rect(b.X, b.Y, Math.Max(.03, b.Width + dx), Math.Max(.03, b.Height + dy)) : new Rect(b.X + dx, b.Y + dy, b.Width, b.Height);
        ShowHandle(_dragBox);
    }

    private void EndStickerDrag()
    {
        if (!_stickerDragging) return;
        _stickerDragging = false;
        _stickerHandle.ReleaseMouseCapture();
        if (CurrentSticker() is { } sticker && !Same(_dragBox, _dragStartBox)) TryChange(() => _document.PlaceSticker(sticker.Key, _dragBox, sticker.Opacity));
        RefreshCanvas();
    }

    private void ShowHandle(Rect box)
    {
        var a = Whole;
        _stickerHandle.Visibility = Visibility.Visible;
        Canvas.SetLeft(_stickerHandle, a.X + box.X * a.Width);
        Canvas.SetTop(_stickerHandle, a.Y + box.Y * a.Height);
        _stickerHandle.Width = Math.Max(16, box.Width * a.Width);
        _stickerHandle.Height = Math.Max(16, box.Height * a.Height);
    }

    private void PlaceStickerHandle()
    {
        if (CurrentSticker() is not { } sticker || sticker.Cover) { if (!_stickerDragging) _stickerHandle.Visibility = Visibility.Collapsed; return; }
        if (!_stickerDragging) ShowHandle(sticker.Box);
    }

    private void Relayer(StudioSticker sticker)
    {
        var stickers = _document.Stickers();
        var target = (sticker.Front ? ThemeStudioDocument.BackStickers : ThemeStudioDocument.FrontStickers).FirstOrDefault(k => stickers.All(s => s.Key != k));
        if (target is null) { Report("There is no room there: four stickers are there already.", true); return; }
        var before = _document;
        TryChange(() => _document.MoveSticker(sticker.Key, target));
        if (_document != before) { _sticker = target; SyncPanel(); RefreshCanvas(); }
    }

    private void EditSticker(StudioSticker sticker)
    {
        if (_document.StickerImage(sticker.Image) is not { } bytes) return;
        try
        {
            var editor = new StudioImageWindow(_owner, bytes, sticker.Cover || Same(sticker.Box, new Rect(0, 0, 1, 1)), WindowAspect());
            if (editor.ShowDialog() != true || editor.Result is not { } edited) return;
            TryChange(() => _document.ReplaceStickerImage(sticker.Key, edited));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or FormatException or InvalidOperationException or IOException)
        { Report("That picture could not be edited: " + error.Message, true); }
    }

    private void RemoveSticker(StudioSticker sticker)
    {
        _sticker = null;
        TryChange(() => _document.Without(sticker.Key).DropUnusedImages());
    }

    // ---------------------------------------------------------------- keeping it all in place

    private void RefreshCanvas()
    {
        if (_partDragging) return;
        PlaceWidthGrip();   // NEW (Arrange mode): ThemeEditor.Arrange.cs
        PlaceStickerHandle();
        Rect? area = null; string? label = null;
        if (CurrentSticker() is { } sticker) area = StickerRect(sticker);
        else if (SelectedPart() is { } part)
        {
            area = PartBounds(part);
            label = part.Label;
        }
        if (label is not null && area is { } bounds)
        {
            Place(_selectBox, bounds);
            _selectBox.Visibility = Visibility.Visible;
            SetTag(_selectTag, label);
            _selectTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_selectTag, Math.Max(0, bounds.Left));
            Canvas.SetTop(_selectTag, bounds.Top >= _selectTag.DesiredSize.Height ? bounds.Top - _selectTag.DesiredSize.Height : bounds.Top + 2);
            _selectTag.Visibility = Visibility.Visible;
        }
        else _selectBox.Visibility = _selectTag.Visibility = Visibility.Collapsed;
    }
}
