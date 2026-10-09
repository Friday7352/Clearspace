using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Clearspace.Controls;
using Clearspace.Services;

namespace Clearspace;

// NEW (custom layouts): build the window's layout by dragging blocks around.
//   - Left: the blocks. Drag one onto the map: onto an edge of a part to put it beside that part (left, right,
//     above, below), onto the middle to swap it in. Parts already on the map can be dragged the same way.
//     Blocks that can only appear once are greyed out while they are on the map.
//   - Middle: a large workspace switching between the map and live Explorer preview.
//   - Right: the chosen part's settings: size, row or column and
//     draggable dividers (for a group), the folder a file pane opens on, text, picture, clock, and its look
//     (fill, outline, corners, margin, padding). Remove, wrap it in a row or column, or choose the group round it.
//   - Top: ready-made layouts, undo / redo, and "Edit as text" for writing layout.json by hand.
// The result is layout.json text (ThemeLayout.cs), or null for the usual layout.
internal sealed class StudioLayoutBuilderWindow : Window
{
    private const double MapWidth = 640, MapHeight = 440;
    private ThemeStudioDocument _document;
    private LayoutNode _root;
    private LayoutNode? _selected;
    private readonly Stack<string> _undo = new(), _redo = new();
    private readonly Canvas _map = new() { Width = MapWidth, Height = MapHeight, Background = Brushes.Transparent, ClipToBounds = true, AllowDrop = true };
    private readonly Dictionary<LayoutNode, Rect> _rects = new();
    private readonly StackPanel _palette = new();
    private readonly StackPanel _properties = new();
    private readonly WrapPanel _templates = new();
    private readonly TextBlock _message = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Button _undoButton = FileBackupUi.IconButton("", "Undo  ·  Ctrl+Z"), _redoButton = FileBackupUi.IconButton("", "Redo  ·  Ctrl+Y");
    private readonly TextBox _text = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Border _textView, _mapView;
    private readonly ExplorerThemePreview _preview = new();
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Rectangle _dropMark = new() { StrokeThickness = 2, RadiusX = 4, RadiusY = 4, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private LayoutNode? _dragging;       // a part being moved on the map
    private Point _pressed;
    private bool _textMode;

    internal string? Result { get; private set; }
    internal ThemeStudioDocument Document => _document;   // with any pictures added for picture blocks

    internal StudioLayoutBuilderWindow(Window owner, ThemeStudioDocument document)
    {
        if (owner.IsVisible) Owner = owner;
        _document = document;
        _root = ThemeLayout.TryParse(document.LayoutJson, out var parsed, out _) && parsed is not null ? parsed : ThemeLayout.Templates[0].Make();
        Title = "Layout builder";
        Width = 1340; Height = 860; MinWidth = 1100; MinHeight = 700;
        WindowState = WindowState.Maximized;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Base"); SetResourceReference(ForegroundProperty, "Ink"); SetResourceReference(FontFamilyProperty, "UIFont");
        FontSize = 13;
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);

        var page = new Grid();
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition());
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = page;

        // Top: title, templates, undo / redo, text view.
        var top = new DockPanel { Margin = new Thickness(22, 16, 22, 12) };
        var title = new TextBlock { Text = "Layout builder", FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        DockPanel.SetDock(title, Dock.Left); top.Children.Add(title);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _undoButton.Click += (_, _) => History(_undo, _redo); _redoButton.Click += (_, _) => History(_redo, _undo);
        var textToggle = FileBackupUi.Chip("Edit as text", false, () => { });
        textToggle.Click += (_, _) => SetTextMode(!_textMode);
        textToggle.ToolTip = "Write layout.json by hand";
        tools.Children.Add(_undoButton); tools.Children.Add(_redoButton); tools.Children.Add(textToggle);
        DockPanel.SetDock(tools, Dock.Right); top.Children.Add(tools);
        foreach (var (name, tip, make) in ThemeLayout.Templates)
        {
            var chip = FileBackupUi.Chip(name, false, () => Edit(() => { _root = make(); _selected = null; }));
            chip.ToolTip = tip; _templates.Children.Add(chip);
        }
        _templates.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(_templates);
        page.Children.Add(top);

        // Body: palette | full-size map, text, or preview | settings.
        var body = new Grid { Margin = new Thickness(22, 0, 22, 12) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        Grid.SetRow(body, 1); page.Children.Add(body);

        body.Children.Add(new ScrollViewer { Content = _palette, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        BuildPalette();

        var middle = new Grid(); Grid.SetColumn(middle, 2); body.Children.Add(middle);
        middle.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        middle.RowDefinitions.Add(new RowDefinition());
        _dropMark.SetResourceReference(Shape.StrokeProperty, "Accent");
        _dropMark.SetResourceReference(Shape.FillProperty, "AccentSoft");
        _map.DragOver += OnMapDragOver;
        _map.DragLeave += (_, _) => _dropMark.Visibility = Visibility.Collapsed;
        _map.Drop += OnMapDrop;
        _map.MouseLeftButtonDown += OnMapDown;
        _map.MouseMove += OnMapMove;
        var mapStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        mapStack.Children.Add(new Border { Child = _map, BorderThickness = new Thickness(0) });
        var hint = FileBackupUi.Muted("Drag a block onto an edge of a part to put it beside it, or onto its middle to swap it in. Click a part to change it.");
        hint.FontSize = 12; hint.Margin = new Thickness(0, 10, 0, 0); hint.MaxWidth = MapWidth;
        mapStack.Children.Add(hint);
        _message.Margin = new Thickness(0, 6, 0, 0); _message.MaxWidth = MapWidth;
        mapStack.Children.Add(_message);
        _mapView = new Border { Child = new Viewbox { Child = mapStack, Stretch = Stretch.Uniform }, Padding = new Thickness(14), BorderThickness = new Thickness(1) };
        _mapView.SetResourceReference(Border.BackgroundProperty, "Surface");
        _mapView.SetResourceReference(Border.BorderBrushProperty, "LineFaint");
        _mapView.SetResourceReference(Border.CornerRadiusProperty, "R12");
        Grid.SetRow(_mapView, 1); middle.Children.Add(_mapView);

        _text.SetResourceReference(FontFamilyProperty, "MonoFont");
        _text.SetResourceReference(BackgroundProperty, "Base");
        _text.SetResourceReference(ForegroundProperty, "Ink");
        _text.FontSize = 12.5; _text.BorderThickness = new Thickness(0);
        var applyText = FileBackupUi.TextButton("Apply text", true);
        applyText.HorizontalAlignment = HorizontalAlignment.Left; applyText.Margin = new Thickness(0, 8, 0, 0);
        applyText.Click += (_, _) => ApplyText();
        var textStack = new DockPanel();
        DockPanel.SetDock(applyText, Dock.Bottom); textStack.Children.Add(applyText);
        textStack.Children.Add(_text);
        _textView = new Border { Child = textStack, Padding = new Thickness(14), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
        _textView.SetResourceReference(Border.BackgroundProperty, "Surface");
        _textView.SetResourceReference(Border.BorderBrushProperty, "LineFaint");
        _textView.SetResourceReference(Border.CornerRadiusProperty, "R12");
        Grid.SetRow(_textView, 1); middle.Children.Add(_textView);

        var right = new Grid(); Grid.SetColumn(right, 4); body.Children.Add(right);
        var previewFrame = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(6), ClipToBounds = true, Visibility = Visibility.Collapsed,
            Child = new Viewbox { Stretch = Stretch.Uniform, Child = _preview } };
        previewFrame.SetResourceReference(Border.BackgroundProperty, "Tint");
        previewFrame.SetResourceReference(Border.BorderBrushProperty, "LineFaint");
        previewFrame.SetResourceReference(Border.CornerRadiusProperty, "R12");
        Grid.SetRow(previewFrame, 1); middle.Children.Add(previewFrame);
        var workspaceTabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var arrange = FileBackupUi.TextButton("Arrange blocks", false);
        var preview = FileBackupUi.TextButton("Preview layout", false);
        void ShowWorkspace(bool showPreview)
        {
            previewFrame.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
            _mapView.Visibility = !showPreview && !_textMode ? Visibility.Visible : Visibility.Collapsed;
            _textView.Visibility = !showPreview && _textMode ? Visibility.Visible : Visibility.Collapsed;
            arrange.FontWeight = showPreview ? FontWeights.Normal : FontWeights.Bold;
            preview.FontWeight = showPreview ? FontWeights.Bold : FontWeights.Normal;
            if (showPreview) { _previewTimer.Stop(); ShowPreview(); }
        }
        arrange.Click += (_, _) => ShowWorkspace(false);
        preview.Click += (_, _) => ShowWorkspace(true);
        textToggle.Click += (_, _) => ShowWorkspace(false);
        arrange.FontWeight = FontWeights.Bold;
        workspaceTabs.Children.Add(arrange); workspaceTabs.Children.Add(preview); middle.Children.Add(workspaceTabs);
        var settings = new ScrollViewer { Content = _properties, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        right.Children.Add(settings);

        // Footer.
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(22, 12, 22, 14) };
        footer.SetResourceReference(Border.BorderBrushProperty, "LineFaint");
        Grid.SetRow(footer, 2); page.Children.Add(footer);
        var footerRow = new DockPanel(); footer.Child = footerRow;
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right); footerRow.Children.Add(actions);
        var usual = FileBackupUi.TextButton("Use the usual layout", false);
        usual.ToolTip = "Take the custom layout away; the Layout card's map and switches apply again";
        usual.Click += (_, _) => { Result = null; DialogResult = true; };
        var cancel = FileBackupUi.TextButton("Cancel", false); cancel.IsCancel = true; cancel.Click += (_, _) => Close();
        var use = FileBackupUi.TextButton("Use this layout", true);
        use.SetResourceReference(BackgroundProperty, "Accent"); use.SetResourceReference(ForegroundProperty, "AccentInk"); use.FontWeight = FontWeights.SemiBold;
        use.Click += (_, _) => Finish();
        actions.Children.Add(usual); actions.Children.Add(cancel); actions.Children.Add(use);
        footerRow.Children.Add(FileBackupUi.Muted("Saved with the theme as layout.json. Extra file panes open on their folders each time Clearspace starts."));

        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.FocusedElement is TextBox) return;
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { History(_undo, _redo); e.Handled = true; }
            else if (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control) { History(_redo, _undo); e.Handled = true; }
            else if (e.Key == Key.Delete && _selected is not null) { Remove(_selected); e.Handled = true; }
        };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); ShowPreview(); };
        Loaded += (_, _) => ShowPreview();
        Refresh();
    }

    // ---------------------------------------------------------------- palette

    private void BuildPalette()
    {
        _palette.Children.Clear();
        var heading = new TextBlock { Text = "Blocks", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 8) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        _palette.Children.Add(heading);
        var used = _root.All().Where(n => n.Block is not null).Select(n => n.Block!).ToHashSet();
        foreach (var block in ThemeLayout.Blocks)
        {
            var taken = ThemeLayout.OnlyOnce.Contains(block) && used.Contains(block);
            var glyph = new TextBlock { Text = ThemeLayout.Glyph(block), FontSize = 14, Width = 22, VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, taken ? "InkDisabled" : "Accent");
            var label = new TextBlock { Text = ThemeLayout.Label(block), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            if (taken) label.SetResourceReference(TextBlock.ForegroundProperty, "InkDisabled");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(glyph); row.Children.Add(label);
            var item = new Border { Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 4), BorderThickness = new Thickness(1), Child = row, Cursor = taken ? Cursors.No : Cursors.SizeAll };
            item.SetResourceReference(Border.BackgroundProperty, "Surface");
            item.SetResourceReference(Border.BorderBrushProperty, "LineFaint");
            item.SetResourceReference(Border.CornerRadiusProperty, "R8");
            item.ToolTip = taken ? "Already on the map (it can appear once)" : "Drag onto the map";
            AutomationProperties.SetName(item, ThemeLayout.Label(block));
            if (!taken)
                item.MouseMove += (_, e) =>
                {
                    if (e.LeftButton != MouseButtonState.Pressed) return;
                    DragDrop.DoDragDrop(item, new DataObject("clearspace-block", block), DragDropEffects.Copy);
                };
            _palette.Children.Add(item);
        }
    }

    // ---------------------------------------------------------------- the map

    private static string NodeLabel(LayoutNode node) => node.IsContainer ? (node.Split == "row" ? "Row" : "Column")
        : node.Block == "pane" ? "Pane · " + (string.IsNullOrWhiteSpace(node.Folder) ? "main list's folder" : node.Folder)
        : node.Block == "text" ? "Text · " + (node.Text ?? "")
        : ThemeLayout.Label(node.Block ?? "");

    // How much room a part takes along a row or column, in map pixels, for parts that are not "*".
    private static double Nominal(LayoutNode node, bool alongRow, double scale)
    {
        var length = ThemeLayout.Length(node);
        if (length.IsAbsolute) return length.Value * scale;
        if (node.IsContainer) return 60;
        return node.Block switch
        {
            "tabs" => alongRow ? 120 : 26, "toolbar" => alongRow ? 260 : 34, "status" or "keys" => alongRow ? 140 : 22,
            "text" or "clock" => alongRow ? 120 : 36, "image" => 80, "preview" => alongRow ? 150 : 90, _ => 60
        };
    }

    private void MeasureNode(LayoutNode node, Rect area, double scale)
    {
        _rects[node] = area;
        if (!node.IsContainer || node.Children is not { Count: > 0 } children) return;
        var row = node.Split == "row";
        var inner = new Rect(area.X + 4, area.Y + 4, Math.Max(1, area.Width - 8), Math.Max(1, area.Height - 8));
        var total = row ? inner.Width : inner.Height;
        var gap = 4d;
        var fixedSum = 0d; var stars = 0d;
        foreach (var child in children)
        {
            var length = ThemeLayout.Length(child);
            if (length.IsStar) stars += length.Value; else fixedSum += Nominal(child, row, scale);
        }
        var free = Math.Max(0, total - fixedSum - gap * (children.Count - 1));
        var at = row ? inner.X : inner.Y;
        foreach (var child in children)
        {
            var length = ThemeLayout.Length(child);
            var size = length.IsStar ? (stars > 0 ? free * length.Value / stars : 0) : Nominal(child, row, scale);
            size = Math.Max(14, size);
            MeasureNode(child, row ? new Rect(at, inner.Y, size, inner.Height) : new Rect(inner.X, at, inner.Width, size), scale);
            at += size + gap;
        }
    }

    private void Refresh()
    {
        _rects.Clear();
        _map.Children.Clear();
        MeasureNode(_root, new Rect(0, 0, MapWidth, MapHeight), MapWidth / 1264d);
        foreach (var (node, rect) in _rects.OrderBy(p => Depth(p.Key)))
        {
            if (rect.Width < 2 || rect.Height < 2) continue;
            var chosen = ReferenceEquals(node, _selected);
            var shape = new Rectangle { Width = rect.Width, Height = rect.Height, RadiusX = 5, RadiusY = 5, StrokeThickness = chosen ? 2.5 : 1 };
            if (node.IsContainer)
            {
                shape.StrokeDashArray = new DoubleCollection { 3, 3 };
                shape.SetResourceReference(Shape.StrokeProperty, chosen ? "Accent" : "Line");
                shape.Fill = Brushes.Transparent;
            }
            else
            {
                shape.SetResourceReference(Shape.FillProperty, node.Block is "files" or "pane" ? "Base" : "Overlay");
                shape.SetResourceReference(Shape.StrokeProperty, chosen ? "Accent" : "Line");
            }
            Canvas.SetLeft(shape, rect.X); Canvas.SetTop(shape, rect.Y);
            _map.Children.Add(shape);
            if (node.IsContainer) continue;
            var label = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
            var glyph = new TextBlock { Text = ThemeLayout.Glyph(node.Block!), FontSize = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            var words = new TextBlock { Text = NodeLabel(node), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = Math.Max(10, rect.Width - 34) };
            words.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            label.Children.Add(glyph); label.Children.Add(words);
            Canvas.SetLeft(label, rect.X + 8); Canvas.SetTop(label, rect.Y + Math.Max(2, Math.Min(8, rect.Height / 2 - 8)));
            _map.Children.Add(label);
        }
        _map.Children.Add(_dropMark);
        _undoButton.IsEnabled = _undo.Count > 0; _redoButton.IsEnabled = _redo.Count > 0;
        BuildPalette();
        BuildProperties();
        if (_textMode) _text.Text = ThemeLayout.Write(_root);
        _previewTimer.Stop(); _previewTimer.Start();
    }

    private int Depth(LayoutNode node)
    {
        var depth = 0;
        for (var parent = ParentOf(node); parent is not null; parent = ParentOf(parent)) depth++;
        return depth;
    }

    private LayoutNode? ParentOf(LayoutNode node) => _root.All().FirstOrDefault(n => n.Children?.Any(c => ReferenceEquals(c, node)) == true);

    // The deepest part under a point (a group is chosen by clicking its edge, between its parts).
    private LayoutNode? At(Point p) => _rects.Where(r => r.Value.Contains(p)).OrderByDescending(r => Depth(r.Key)).Select(r => (LayoutNode?)r.Key).FirstOrDefault();

    private void OnMapDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = e.GetPosition(_map);
        _selected = At(_pressed);
        _dragging = _selected is { IsContainer: false } ? _selected : null;
        Refresh();
    }

    private void OnMapMove(object sender, MouseEventArgs e)
    {
        if (_dragging is null || e.LeftButton != MouseButtonState.Pressed) return;
        if ((e.GetPosition(_map) - _pressed).Length < 6) return;
        var moving = _dragging; _dragging = null;
        DragDrop.DoDragDrop(_map, new DataObject("clearspace-move", "move"), DragDropEffects.Move);
        _dropMark.Visibility = Visibility.Collapsed;
        _ = moving;
    }

    private enum Zone { Left, Right, Top, Bottom, Middle }

    private (LayoutNode Target, Zone Zone, Rect Mark)? DropAt(Point p)
    {
        if (At(p) is not { } target || !_rects.TryGetValue(target, out var r)) return null;
        double fx = (p.X - r.X) / r.Width, fy = (p.Y - r.Y) / r.Height;
        var edge = Math.Min(Math.Min(fx, 1 - fx), Math.Min(fy, 1 - fy));
        if (edge > .28 && !target.IsContainer) return (target, Zone.Middle, new Rect(r.X + r.Width * .2, r.Y + r.Height * .2, r.Width * .6, r.Height * .6));
        if (fx == Math.Min(Math.Min(fx, 1 - fx), Math.Min(fy, 1 - fy))) return (target, Zone.Left, new Rect(r.X, r.Y, r.Width / 2, r.Height));
        if (1 - fx == edge) return (target, Zone.Right, new Rect(r.X + r.Width / 2, r.Y, r.Width / 2, r.Height));
        if (fy == edge) return (target, Zone.Top, new Rect(r.X, r.Y, r.Width, r.Height / 2));
        return (target, Zone.Bottom, new Rect(r.X, r.Y + r.Height / 2, r.Width, r.Height / 2));
    }

    private void OnMapDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var moving = e.Data.GetDataPresent("clearspace-move");
        if (!moving && !e.Data.GetDataPresent("clearspace-block")) { e.Effects = DragDropEffects.None; return; }
        if (DropAt(e.GetPosition(_map)) is not { } drop || (moving && _selected is not null && (ReferenceEquals(drop.Target, _selected) || Inside(drop.Target, _selected))))
        { e.Effects = DragDropEffects.None; _dropMark.Visibility = Visibility.Collapsed; return; }
        e.Effects = moving ? DragDropEffects.Move : DragDropEffects.Copy;
        _dropMark.Visibility = Visibility.Visible;
        _dropMark.Width = drop.Mark.Width; _dropMark.Height = drop.Mark.Height;
        Canvas.SetLeft(_dropMark, drop.Mark.X); Canvas.SetTop(_dropMark, drop.Mark.Y);
    }

    private bool Inside(LayoutNode node, LayoutNode group) => group.All().Any(n => ReferenceEquals(n, node));

    private void OnMapDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dropMark.Visibility = Visibility.Collapsed;
        if (DropAt(e.GetPosition(_map)) is not { } drop) return;
        if (e.Data.GetData("clearspace-block") is string block)
        {
            var node = new LayoutNode { Block = block };
            if (block == "pane") node.Folder = "Downloads";
            if (block == "text") node.Text = "Hello";
            Edit(() => { Place(node, drop.Target, drop.Zone); _selected = node; });
        }
        else if (e.Data.GetDataPresent("clearspace-move") && _selected is { } moving && !ReferenceEquals(moving, drop.Target) && !Inside(drop.Target, moving))
        {
            Edit(() =>
            {
                Detach(moving);
                if (_root.All().Any(n => ReferenceEquals(n, drop.Target))) Place(moving, drop.Target, drop.Zone);
                else Place(moving, _root, Zone.Bottom);
                _selected = moving;
            });
        }
    }

    // ---------------------------------------------------------------- editing the tree

    private void Place(LayoutNode node, LayoutNode target, Zone zone)
    {
        if (zone == Zone.Middle)
        {
            // Swap in: the new part takes the old one's place and size.
            node.Size ??= target.Size;
            Replace(target, node);
            return;
        }
        var axis = zone is Zone.Left or Zone.Right ? "row" : "column";
        var before = zone is Zone.Left or Zone.Top;
        if (ParentOf(target) is { } parent && parent.Split == axis)
        {
            var index = parent.Children!.FindIndex(c => ReferenceEquals(c, target));
            parent.Children.Insert(before ? index : index + 1, node);
            return;
        }
        var group = new LayoutNode { Split = axis, Size = target.Size, Splitters = true,
            Children = before ? new List<LayoutNode> { node, target } : new List<LayoutNode> { target, node } };
        if (ThemeLayout.Length(target).IsStar) target.Size = null; // the group takes its share; the part fills the group
        Replace(target, group);
    }

    private void Replace(LayoutNode old, LayoutNode with)
    {
        if (ReferenceEquals(old, _root)) { _root = with; return; }
        if (ParentOf(old) is { Children: { } siblings })
        {
            var index = siblings.FindIndex(c => ReferenceEquals(c, old));
            siblings[index] = with;
        }
    }

    // Takes a part out of the tree; a group left with one part is replaced by that part.
    private void Detach(LayoutNode node)
    {
        if (ParentOf(node) is not { Children: { } siblings } parent) return;
        siblings.RemoveAll(c => ReferenceEquals(c, node));
        if (siblings.Count == 1)
        {
            var only = siblings[0];
            only.Size ??= parent.Size;
            Replace(parent, only);
        }
        else if (siblings.Count == 0) Detach(parent);
    }

    private void Remove(LayoutNode node)
    {
        if (node.Block == "files" || node.All().Any(n => n.Block == "files")) { Say("The main file list has to stay. Move it instead, or swap another block in beside it."); return; }
        if (ReferenceEquals(node, _root)) return;
        Edit(() => { Detach(node); _selected = null; });
    }

    private void Wrap(LayoutNode node, string axis)
    {
        Edit(() =>
        {
            var group = new LayoutNode { Split = axis, Size = node.Size, Splitters = true, Children = [node] };
            node.Size = null;
            Replace(node, group);
            _selected = group;
        });
    }

    // Every change goes through here: the old layout is kept for undo, the change made and checked, and the
    // change taken back (with the reason shown) if the result is not a layout Clearspace can use.
    private void Edit(Action change)
    {
        var before = ThemeLayout.Write(_root);
        change();
        try { ThemeLayout.Check(_root); }
        catch (ThemeFormatException error)
        {
            _root = ThemeLayout.Parse(before)!; _selected = null;
            Say(error.Message);
            Refresh();
            return;
        }
        _undo.Push(before); _redo.Clear();
        Say("");
        Refresh();
    }

    private void History(Stack<string> from, Stack<string> to)
    {
        if (!from.TryPop(out var json)) return;
        to.Push(ThemeLayout.Write(_root));
        _root = ThemeLayout.Parse(json)!; _selected = null;
        Refresh();
    }

    private void Say(string text)
    {
        _message.Text = text;
        _message.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
    }

    // ---------------------------------------------------------------- the chosen part's settings

    private void BuildProperties()
    {
        _properties.Children.Clear();
        if (_selected is not { } node)
        {
            _properties.Children.Add(FileBackupUi.Muted("Click a part on the map to change it."));
            return;
        }
        var heading = new TextBlock { Text = NodeLabel(node), FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextTrimming = TextTrimming.CharacterEllipsis };
        _properties.Children.Add(heading);

        // Size.
        Field("Size", node.Size ?? "", "* fills the room left (2* takes twice as much), a number is pixels, auto fits its contents", text => node.Size = text.Length == 0 ? null : text);
        var sizes = new WrapPanel();
        foreach (var (label, value) in new[] { ("Fill", "*"), ("Twice", "2*"), ("Fit", "auto"), ("Default", "") })
            sizes.Children.Add(FileBackupUi.Chip(label, (node.Size ?? "") == value, () => Edit(() => node.Size = value.Length == 0 ? null : value)));
        _properties.Children.Add(sizes);

        if (node.IsContainer)
        {
            var axes = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            axes.Children.Add(FileBackupUi.Chip("Side by side (row)", node.Split == "row", () => Edit(() => node.Split = "row")));
            axes.Children.Add(FileBackupUi.Chip("Stacked (column)", node.Split == "column", () => Edit(() => node.Split = "column")));
            _properties.Children.Add(axes);
            var dividers = FileBackupUi.Tick("Draggable dividers", "Bars between the parts that you can drag to resize them");
            dividers.IsChecked = node.Splitters == true; dividers.Margin = new Thickness(6, 6, 0, 4);
            dividers.Click += (_, _) => Edit(() => node.Splitters = dividers.IsChecked == true ? true : null);
            _properties.Children.Add(dividers);
        }

        switch (node.Block)
        {
            case "pane":
                Section("Opens on");
                var folders = new WrapPanel();
                foreach (var folder in new[] { "Downloads", "Desktop", "Documents", "Pictures", "Music", "Videos", "Home", "This PC" })
                    folders.Children.Add(FileBackupUi.Chip(folder, string.Equals(node.Folder, folder, StringComparison.OrdinalIgnoreCase), () => Edit(() => node.Folder = folder)));
                folders.Children.Add(FileBackupUi.Chip("Main list's folder", string.IsNullOrWhiteSpace(node.Folder), () => Edit(() => node.Folder = null)));
                _properties.Children.Add(folders);
                var browse = FileBackupUi.TextButton("Choose a folder…", false);
                browse.HorizontalAlignment = HorizontalAlignment.Left; browse.Margin = new Thickness(0, 4, 0, 6);
                browse.Click += (_, _) =>
                {
                    var picker = new FolderPickerWindow(new FolderPickerOptions("Choose a folder for this pane", "The pane opens here each time Clearspace starts.", "Use this folder",
                        FilePane.Resolve(node.Folder) is { Length: > 0 } start ? start : KnownFolders.Documents)) { Owner = this };
                    if (picker.ShowDialog() == true && picker.Chosen is { } chosen) Edit(() => node.Folder = chosen);
                };
                _properties.Children.Add(browse);
                Field("Title", node.Title ?? "", "Shown at the top of the pane; empty follows the folder's name", text => node.Title = text.Length == 0 ? null : text);
                break;
            case "text":
                Field("Text", node.Text ?? "", "What it says", text => node.Text = text);
                FontAndAlign(node);
                break;
            case "clock":
                FontAndAlign(node);
                break;
            case "image":
                Section("Picture");
                var pictures = new WrapPanel();
                foreach (var name in _document.ImageNames)
                    pictures.Children.Add(FileBackupUi.Chip(name.Length > 22 ? name[..20] + "…" : name, node.Image == name, () => Edit(() => node.Image = name)));
                _properties.Children.Add(pictures);
                var add = FileBackupUi.TextButton("Add a picture…", false);
                add.HorizontalAlignment = HorizontalAlignment.Left; add.Margin = new Thickness(0, 4, 0, 6);
                add.Click += (_, _) => AddPicture(node);
                _properties.Children.Add(add);
                break;
        }

        // Look.
        Section("Look");
        BrushRow("Fill", node.Fill, value => node.Fill = value);
        BrushRow("Outline", node.Edge, value => node.Edge = value);
        AddSlider("Outline width", node.EdgeWidth ?? (node.Edge is null ? 0 : 1), 0, 12, value => node.EdgeWidth = value);
        AddSlider("Corners", node.Corner ?? 0, 0, 40, value => node.Corner = value);
        AddSlider("Margin", node.Margin ?? (node.Block is "files" or "pane" or "sidebar" ? 6 : 0), 0, 60, value => node.Margin = value);
        AddSlider("Padding", node.Padding ?? 0, 0, 60, value => node.Padding = value);

        // Actions.
        Section("Arrange");
        var arrange = new WrapPanel();
        arrange.Children.Add(FileBackupUi.Chip("Remove", false, () => Remove(node)));
        arrange.Children.Add(FileBackupUi.Chip("Put in a row", false, () => Wrap(node, "row")));
        arrange.Children.Add(FileBackupUi.Chip("Put in a column", false, () => Wrap(node, "column")));
        if (ParentOf(node) is { } parent) arrange.Children.Add(FileBackupUi.Chip("Choose the group round it", false, () => { _selected = parent; Refresh(); }));
        _properties.Children.Add(arrange);
    }

    private void Section(string title)
    {
        var heading = new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        _properties.Children.Add(heading);
    }

    private void Field(string label, string value, string tip, Action<string> set)
    {
        Section(label);
        var box = new TextBox { Text = value, ToolTip = tip };
        AutomationProperties.SetName(box, label);
        void Commit() { if (box.Text.Trim() != value) Edit(() => set(box.Text.Trim())); }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Commit(); } };
        box.LostFocus += (_, _) => Commit();
        _properties.Children.Add(FileBackupUi.Framed(box));
    }

    private void FontAndAlign(LayoutNode node)
    {
        AddSlider("Text size", node.FontSize ?? (node.Block == "clock" ? 22 : 16), 6, 96, value => node.FontSize = value);
        var align = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var side in new[] { "left", "center", "right" })
            align.Children.Add(FileBackupUi.Chip(char.ToUpperInvariant(side[0]) + side[1..], (node.Align ?? "left") == side, () => Edit(() => node.Align = side == "left" ? null : side)));
        _properties.Children.Add(align);
    }

    private void AddSlider(string label, double value, double low, double high, Action<double> set)
    {
        var top = new DockPanel { Margin = new Thickness(0, 8, 0, 2) };
        var shown = new TextBlock { Text = $"{value:0}", FontSize = 12 };
        shown.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        DockPanel.SetDock(shown, Dock.Right); top.Children.Add(shown);
        top.Children.Add(new TextBlock { Text = label, FontSize = 12.5 });
        _properties.Children.Add(top);
        var slider = new Slider { Minimum = low, Maximum = high, Value = Math.Clamp(value, low, high), TickFrequency = 1, IsSnapToTickEnabled = true };
        slider.SetResourceReference(StyleProperty, "MediaSlider");
        AutomationProperties.SetName(slider, label);
        slider.ValueChanged += (_, _) => shown.Text = $"{slider.Value:0}";
        // Applied when the drag ends (or a key is let go), so one drag is one undo step.
        void Commit() { if (Math.Abs(slider.Value - value) > .01) Edit(() => set(slider.Value)); }
        slider.PreviewMouseLeftButtonUp += (_, _) => Commit();
        slider.KeyUp += (_, _) => Commit();
        _properties.Children.Add(slider);
    }

    private static readonly string[] BrushKeys = ["Base", "Surface", "Overlay", "Tint", "Well", "Accent", "AccentSoft", "AccentWash", "Line", "LineFaint", "Selected", "CardFill"];

    private void BrushRow(string label, string? value, Action<string?> set)
    {
        var (outer, inner) = StudioUi.Swatch(22);
        inner.Background = value is null ? Brushes.Transparent : value.StartsWith('#')
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)!) : TryFindResource(value) as Brush ?? Brushes.Transparent;
        var words = new TextBlock { Text = label + "  ·  " + (value ?? "none"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var row = new DockPanel();
        DockPanel.SetDock(outer, Dock.Left); row.Children.Add(outer); row.Children.Add(words);
        Border? clickable = null;
        clickable = StudioUi.ClickRow(row, label, () =>
        {
            var menu = new ContextMenu { PlacementTarget = clickable, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var none = new MenuItem { Header = "None", IsCheckable = true, IsChecked = value is null };
            none.Click += (_, _) => Edit(() => set(null));
            menu.Items.Add(none);
            menu.Items.Add(new Separator());
            foreach (var key in BrushKeys)
            {
                var (swatchOuter, swatchInner) = StudioUi.Swatch(14);
                swatchInner.SetResourceReference(Border.BackgroundProperty, key);
                var item = new MenuItem { Header = StudioColorOptions.All.FirstOrDefault(o => o.Key == key).Label ?? key, Icon = swatchOuter, IsCheckable = true, IsChecked = value == key, ToolTip = "Follows the theme's " + key };
                item.Click += (_, _) => Edit(() => set(key));
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var custom = new MenuItem { Header = "A color of my own…" };
            custom.Click += (_, _) =>
            {
                var picker = new StudioColorWindow(this, value is { } v && v.StartsWith('#') ? (v.Length == 7 ? "#FF" + v[1..] : v) : "#FF808080", true, null, label);
                if (picker.ShowDialog() == true) Edit(() => set(picker.SelectedColor));
            };
            menu.Items.Add(custom);
            menu.IsOpen = true;
        });
        clickable.Padding = new Thickness(6, 5, 8, 5);
        _properties.Children.Add(clickable);
    }

    private void AddPicture(LayoutNode node)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose a picture", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new System.IO.FileInfo(picker.FileName).Length > 16 * 1024 * 1024) { Say("Choose a picture smaller than 16 MB."); return; }
            var editor = new StudioImageWindow(this, System.IO.File.ReadAllBytes(picker.FileName), fillsWindow: false, 1);
            if (editor.ShowDialog() != true || editor.Result is not { } edited) return;
            var (document, name) = _document.AddImage(edited);
            _document = document;
            Edit(() => node.Image = name);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException or InvalidOperationException)
        { Say("That picture could not be used: " + error.Message); }
    }

    // ---------------------------------------------------------------- text view, preview, finishing

    private void SetTextMode(bool on)
    {
        _textMode = on;
        _textView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _mapView.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on) _text.Text = ThemeLayout.Write(_root);
    }

    private void ApplyText()
    {
        if (!ThemeLayout.TryParse(_text.Text, out var parsed, out var problem) || parsed is null)
        {
            Say(problem ?? "There is no layout in the text.");
            return;
        }
        _undo.Push(ThemeLayout.Write(_root)); _redo.Clear();
        _root = parsed; _selected = null;
        Say("");
        Refresh();
    }

    private void ShowPreview()
    {
        try { _preview.Show(_document with { LayoutJson = ThemeLayout.Write(_root) }); }
        catch (Exception error) when (error is ThemeFormatException or ArgumentException) { Say(error.Message); }
    }

    private void Finish()
    {
        if (_textMode && !string.Equals(_text.Text, ThemeLayout.Write(_root), StringComparison.Ordinal))
        {
            ApplyText();
            if (!ThemeLayout.TryParse(_text.Text, out _, out _)) return;
        }
        try { ThemeLayout.Check(_root); }
        catch (ThemeFormatException error) { Say(error.Message); return; }
        Result = ThemeLayout.Write(_root);
        DialogResult = true;
    }
}
