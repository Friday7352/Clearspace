using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Clearspace.Services;

namespace Clearspace.Controls;

public sealed class DiskUsageTreemap : Canvas
{
    private sealed record ZoomFrame(Rect Camera, Rect Region);
    private NestedDiskMap _map = NestedDiskMap.Create([]);
    private readonly Stack<ZoomFrame> _history = new();
    private readonly Dictionary<int, Button> _buttons = [];
    private readonly Dictionary<string, Brush> _colors = [];
    private Canvas? _layer;
    private bool _clusterPressed;
    private bool _animating;
    private int _version;
    private int? _highlighted;
    private string _folderPath = "";
    private Rect _camera = new(0, 0, 1, 1);
    private Rect _clusterBounds = Rect.Empty;
    private bool _wheelAnimating;
    private bool _wheelGesture;
    private ScaleTransform? _cameraScale;
    private TranslateTransform? _cameraTranslation;
    private Rect _renderCamera = new(0, 0, 1, 1);
    private int _aggregateCount;
    internal event Action? ParentRequested;
    internal bool IsZoomed => _history.Count > 0;
    internal bool IsAnimating => _animating;
    internal Rect ClusterBounds => _clusterBounds;
    internal Rect Camera => _camera;
    internal Rect DisplayedCamera => _wheelAnimating && _cameraScale is { } scale && _cameraTranslation is { } move && ActualWidth > 0
        ? new Rect(_renderCamera.X - move.X * _renderCamera.Width / (ActualWidth * scale.ScaleX),
            _renderCamera.Y - move.Y * _renderCamera.Height / (ActualHeight * scale.ScaleY),
            _renderCamera.Width / scale.ScaleX, _renderCamera.Height / scale.ScaleY) : _camera;
    internal IReadOnlyCollection<Button> TileButtons => _buttons.Values;
    internal IReadOnlyList<DiskUsageItem> VisibleItems => _map.Tiles.Select(tile => tile.Item).ToArray();
    internal NestedDiskMap CurrentMap => _map;
    internal event Action? ZoomChanged;
    internal event Action<DiskUsageItem>? ItemInvoked;
    internal event Action<DiskUsageItem>? DeleteRequested;

    public DiskUsageTreemap()
    {
        ClipToBounds = true;
        UseLayoutRounding = false;
        Background = Brushes.Transparent;
        SizeChanged += (_, _) => Draw();
    }

    internal void SetItems(IReadOnlyList<DiskUsageItem> items, string folderPath = "", NestedDiskMap? prepared = null)
    {
        var next = prepared ?? NestedDiskMap.Create(items);
        Rect? transition = null;
        var zoomIn = true;
        if (_folderPath.Length > 0 && folderPath.Length > 0 &&
            !folderPath.Equals(_folderPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetPathRoot(folderPath), Path.GetPathRoot(_folderPath), StringComparison.OrdinalIgnoreCase))
        {
            zoomIn = !_folderPath.StartsWith(folderPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            var relative = Path.GetRelativePath(zoomIn ? _folderPath : folderPath, zoomIn ? folderPath : _folderPath).Split(Path.DirectorySeparatorChar)[0];
            var target = FindFolder(zoomIn ? _map : next, relative, new Rect(0, 0, 1, 1));
            if (target is Rect rect)
            {
                if (zoomIn) rect = new Rect((rect.X - _camera.X) / _camera.Width, (rect.Y - _camera.Y) / _camera.Height,
                    rect.Width / _camera.Width, rect.Height / _camera.Height);
                var side = Math.Min(1, Math.Min(rect.Width, rect.Height));
                transition = new Rect(Math.Clamp(rect.X + (rect.Width - side) / 2, 0, 1 - side),
                    Math.Clamp(rect.Y + (rect.Height - side) / 2, 0, 1 - side), side, side);
            }
            transition ??= new Rect(.4, .4, .2, .2);
        }
        _folderPath = folderPath;
        _history.Clear();
        _wheelGesture = false;
        _expanded.Clear();
        _camera = new Rect(0, 0, 1, 1);
        _map = next;
        Draw(transition, zoomIn);
        ZoomChanged?.Invoke();
    }

    private static Rect? FindFolder(NestedDiskMap map, string name, Rect bounds)
    {
        foreach (var tile in map.Tiles)
        {
            var box = Within(bounds, tile.Bounds);
            if (tile.Item.IsFolder && tile.Item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (tile.Cutout is Rect hole)
                {
                    var side = Math.Max(hole.X, hole.Y);
                    return Within(bounds, new Rect(0, 0, side, side));
                }
                return box;
            }
            if (tile.IsGroup && tile.Inside is { } inside && FindFolder(inside, name, box) is { } found) return found;
        }
        return null;
    }

    internal void Highlight(int? id)
    {
        _highlighted = id;
        foreach (var (key, button) in _buttons) button.BorderBrush = key == id ? Brushes.White : Brushes.Transparent;
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        _clusterPressed = !_animating && IsClusterHit(e.GetPosition(this));
        if (_clusterPressed) e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (!_clusterPressed) return;
        _clusterPressed = false;
        e.Handled = true;
        TryZoomCluster(e.GetPosition(this));
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        e.Handled = true;
        ZoomWithWheel(e.GetPosition(this), e.Delta);
    }

    internal bool ZoomWithWheel(Point point, int delta)
    {
        if (delta == 0 || ActualWidth <= 0 || ActualHeight <= 0 || (_animating && !_wheelAnimating)) return false;
        if (delta < 0 && _camera.Width >= 1 - 1e-10)
        {
            ParentRequested?.Invoke();
            return true;
        }
        var anchor = new Point(Math.Clamp(point.X / ActualWidth, 0, 1), Math.Clamp(point.Y / ActualHeight, 0, 1));
        var width = Math.Clamp(_camera.Width * Math.Pow(.78, delta / 120d), 1e-7, 1);
        var target = new Rect(Math.Clamp(_camera.X + anchor.X * (_camera.Width - width), 0, 1 - width),
            Math.Clamp(_camera.Y + anchor.Y * (_camera.Height - width), 0, 1 - width), width, width);
        if (!_wheelGesture)
        {
            _history.Push(new ZoomFrame(_camera, new Rect(0, 0, 1, 1)));
            _wheelGesture = true;
        }
        DiskUsageItem? enter = null;
        // Resolve against the actual displayed image, including clipped dominant tiles.
        var local = _layer?.RenderTransform.Inverse?.Transform(point) ?? point;
        var hovered = _buttons.Values.LastOrDefault(button =>
            new Rect(GetLeft(button), GetTop(button), button.Width, button.Height).Contains(local) &&
            (button.Clip is null || button.Clip.FillContains(new Point(local.X - GetLeft(button), local.Y - GetTop(button)))));
        if (delta > 0 && hovered?.Tag is DiskUsageItem { IsFolder: true } folder)
        {
            var folderWidth = Math.Min(hovered.Width / ActualWidth, hovered.Height / ActualHeight) * _renderCamera.Width;
            if (target.Width <= folderWidth * .85) enter = folder;
        }
        AnimateCamera(target, enter);
        if (width >= 1 - 1e-10) { _history.Clear(); _wheelGesture = false; }
        ZoomChanged?.Invoke();
        return true;
    }

    private void AnimateCamera(Rect target, DiskUsageItem? enter = null)
    {
        if (_layer is null) { _camera = target; Draw(); return; }
        var current = DisplayedCamera;
        // Include newly exposed surroundings before pulling the camera back.
        // Otherwise the old viewport's clipped edges become empty during motion.
        if (!_renderCamera.Contains(target))
        {
            var extent = Rect.Union(current, target);
            var side = Math.Min(1, Math.Max(extent.Width, extent.Height));
            _camera = new Rect(Math.Clamp(extent.X, 0, 1 - side), Math.Clamp(extent.Y, 0, 1 - side), side, side);
            Draw();
        }
        var version = ++_version;
        _camera = target;
        _animating = _wheelAnimating = true;
        var scale = _cameraScale = new ScaleTransform();
        var move = _cameraTranslation = new TranslateTransform();
        _layer.RenderTransform = new TransformGroup { Children = { scale, move } };
        double Factor(Rect camera) => _renderCamera.Width / camera.Width;
        double X(Rect camera) => (_renderCamera.X - camera.X) * ActualWidth / camera.Width;
        double Y(Rect camera) => (_renderCamera.Y - camera.Y) * ActualHeight / camera.Height;
        // Rebase from the current visual transform so a new wheel event never snaps.
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, CameraMotion(Factor(current), Factor(target)));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, CameraMotion(Factor(current), Factor(target)));
        move.BeginAnimation(TranslateTransform.XProperty, CameraMotion(X(current), X(target)));
        var motion = CameraMotion(Y(current), Y(target));
        motion.Completed += (_, _) =>
        {
            if (version != _version) return;
            Draw();
            if (enter is not null) ItemInvoked?.Invoke(enter);
        };
        move.BeginAnimation(TranslateTransform.YProperty, motion);
    }

    private static DoubleAnimation CameraMotion(double from, double to) => new(from, to, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

    internal bool IsClusterHit(Point point) => !_clusterBounds.IsEmpty && _clusterBounds.Contains(point) &&
        !_buttons.Values.Any(button => button.Content is not null &&
            new Rect(GetLeft(button), GetTop(button), button.Width, button.Height).Contains(point) &&
            (button.Clip is null || button.Clip.FillContains(new Point(point.X - GetLeft(button), point.Y - GetTop(button)))));

    internal bool TryZoomCluster(Point point)
    {
        if (_animating || !IsClusterHit(point)) return false;
        return ZoomRegion(_clusterBounds);
    }

    private bool ZoomRegion(Rect bounds)
    {
        if (ActualWidth <= 0 || _camera.Width < 1e-9) return false;
        var region = new Rect(bounds.X / ActualWidth, bounds.Y / ActualHeight, bounds.Width / ActualWidth, bounds.Height / ActualHeight);
        _history.Push(new ZoomFrame(_camera, region));
        _wheelGesture = false;
        AnimateCamera(Within(_camera, region));
        ZoomChanged?.Invoke();
        return true;
    }

    internal void ZoomOut()
    {
        if ((_animating && !_wheelAnimating) || !_history.TryPop(out var frame)) return;
        _wheelGesture = false;
        AnimateCamera(frame.Camera);
        ZoomChanged?.Invoke();
    }

    private Rect Pixels(Rect unit) => new(unit.X * ActualWidth, unit.Y * ActualHeight, unit.Width * ActualWidth, unit.Height * ActualHeight);
    private static Rect Within(Rect outer, Rect inner) => new(outer.X + inner.X * outer.Width, outer.Y + inner.Y * outer.Height,
        inner.Width * outer.Width, inner.Height * outer.Height);

    private void Draw(Rect? transition = null, bool zoomIn = true)
    {
        var version = ++_version;
        _wheelAnimating = false;
        _renderCamera = _camera;
        var oldLayer = transition is not null ? _layer : null;
        if (oldLayer is not null)
        {
            if (oldLayer.Parent is Panel parent) parent.Children.Remove(oldLayer);
            oldLayer.RenderTransform = Transform.Identity;
            oldLayer.BeginAnimation(OpacityProperty, null);
            foreach (var button in _buttons.Values)
                if (button.Content is FrameworkElement caption)
                    caption.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(70)));
        }
        Children.Clear();
        _buttons.Clear();
        _aggregateCount = 0;
        _clusterPressed = false;
        var layer = new Canvas { Width = ActualWidth, Height = ActualHeight, Background = BackgroundBrush, UseLayoutRounding = false };
        _layer = layer;
        Paint(_map, layer, new Rect(-_camera.X * ActualWidth / _camera.Width, -_camera.Y * ActualHeight / _camera.Height,
            ActualWidth / _camera.Width, ActualHeight / _camera.Height), true);
        BuildCluster();
        Highlight(_highlighted);
        if (_map.Tiles.Count == 0)
        {
            var message = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            message.Children.Add(new TextBlock { Text = "No file sizes to display", FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center });
            message.Children.Add(new TextBlock { Text = "Try another folder or refresh.", Margin = new Thickness(0, 6, 0, 0), Opacity = .65 });
            layer.Children.Add(new Border { Width = ActualWidth, Height = ActualHeight, Background = BackgroundBrush, Child = message });
        }
        if (oldLayer is null || transition is not Rect region || ActualWidth <= 0)
        {
            Children.Add(layer); _animating = false; return;
        }
        _animating = true;
        var scene = new Canvas { IsHitTestVisible = false };
        var inset = zoomIn ? layer : oldLayer;
        var factor = Math.Max(region.Width, .0000001);
        inset.RenderTransform = new TransformGroup { Children = {
            new ScaleTransform(factor, factor), new TranslateTransform(region.X * ActualWidth, region.Y * ActualHeight) } };
        if (zoomIn) { scene.Children.Add(oldLayer); scene.Children.Add(layer); }
        else { scene.Children.Add(layer); scene.Children.Add(oldLayer); }
        Children.Add(scene);
        // Children emerge inside their folder while the camera approaches it,
        // instead of appearing fully formed on the first animation frame.
        var reveal = new DoubleAnimation(zoomIn ? 0 : 1, zoomIn ? 1 : 0, TimeSpan.FromMilliseconds(220))
        { BeginTime = TimeSpan.FromMilliseconds(30), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        inset.Opacity = zoomIn ? 0 : 1;
        inset.BeginAnimation(OpacityProperty, reveal);
        var scale = new ScaleTransform();
        var translate = new TranslateTransform();
        scene.RenderTransform = new TransformGroup { Children = { scale, translate } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion(zoomIn ? 1 : 1 / factor, zoomIn ? 1 / factor : 1));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion(zoomIn ? 1 : 1 / factor, zoomIn ? 1 / factor : 1));
        translate.BeginAnimation(TranslateTransform.XProperty, Motion(zoomIn ? 0 : -region.X * ActualWidth / factor, zoomIn ? -region.X * ActualWidth / factor : 0));
        var movement = Motion(zoomIn ? 0 : -region.Y * ActualHeight / factor, zoomIn ? -region.Y * ActualHeight / factor : 0);
        movement.Completed += (_, _) =>
        {
            if (_version != version) return;
            scene.Children.Clear(); Children.Remove(scene);
            layer.RenderTransform = Transform.Identity;
            layer.BeginAnimation(OpacityProperty, null);
            layer.Opacity = 1;
            Children.Add(layer); _animating = false;
        };
        translate.BeginAnimation(TranslateTransform.YProperty, movement);
    }

    private Brush BackgroundBrush => TryFindResource("Base") as Brush ?? new SolidColorBrush(Color.FromRgb(26, 25, 22));

    private void BuildCluster()
    {
        _clusterBounds = Rect.Empty;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var small = _buttons.Values.Where(button => button.Content is null)
            .Select(button => new Rect(GetLeft(button), GetTop(button), button.Width, button.Height))
            .OrderByDescending(box => box.Bottom + box.Right).ToArray();
        if (small.Length == 0) return;
        var union = small[0];
        foreach (var box in small.Skip(1))
        {
            var combined = Rect.Union(union, box);
            var aspect = Math.Max(combined.Width, combined.Height) / Math.Max(.1, Math.Min(combined.Width, combined.Height));
            if (Math.Max(combined.Width, combined.Height) <= ActualWidth * .48 && aspect <= 1.8) union = combined;
        }
        _clusterBounds = FocusSquare(union);
    }

    private Rect FocusSquare(Rect box)
    {
        var side = Math.Min(ActualWidth * .55, Math.Max(Math.Min(48, ActualWidth * .25), Math.Max(box.Width, box.Height) * 1.08));
        return new Rect(Math.Clamp(box.X + box.Width / 2 - side / 2, 0, ActualWidth - side),
            Math.Clamp(box.Y + box.Height / 2 - side / 2, 0, ActualHeight - side), side, side);
    }

    private readonly Dictionary<NestedDiskMap, NestedDiskMap> _expanded = new();

    private void Paint(NestedDiskMap map, Canvas layer, Rect bounds, bool interactive)
    {
        var viewport = new Rect(0, 0, ActualWidth, ActualHeight);
        foreach (var tile in map.Tiles)
        {
            var whole = Within(bounds, tile.Bounds);
            var box = Rect.Intersect(whole, viewport);
            if (box.IsEmpty || box.Width < .1 || box.Height < .1) continue;
            Geometry? clip = null;
            if (tile.Cutout is Rect hole)
            {
                var excluded = Within(bounds, hole);
                if (excluded.Contains(box)) continue;
                excluded.Offset(-box.X, -box.Y);
                clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, box.Width, box.Height)),
                    new RectangleGeometry(excluded));
            }
            var aggregate = tile.IsGroup && box.Width * box.Height < Math.Max(2400, ActualWidth * ActualHeight / 64);
            if (!aggregate && tile.IsGroup && tile.Inside is { } inside && Math.Min(box.Width, box.Height) >= 3)
            {
                if (inside.Tiles.Count == 0 && box.Width >= 1 && box.Height >= 1)
                {
                    inside = Expand(inside, whole.Width, whole.Height);
                }
                if (inside.Tiles.Count > 0) { Paint(inside, layer, whole, interactive); continue; }
            }
            var item = tile.Item;
            var labeled = !tile.IsGroup && box.Width >= 72 && box.Height >= 68 &&
                (clip is null || clip.GetArea() >= box.Width * box.Height * .5);
            Brush brush = aggregate && tile.Inside is { } miniature
                ? Mosaic(miniature, whole.Size, new Rect((box.X - whole.X) / whole.Width, (box.Y - whole.Y) / whole.Height,
                    box.Width / whole.Width, box.Height / whole.Height))
                : ColorBrush(item.ColorHex);
            var button = new Button
            {
                Width = box.Width, Height = box.Height, MinWidth = 0, MinHeight = 0,
                UseLayoutRounding = false, SnapsToDevicePixels = false, Tag = item,
                Content = labeled ? Label(item, box.Width, box.Height) : null,
                Background = brush, Foreground = Brushes.White, BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(0), ClipToBounds = true, Clip = clip, Template = TileTemplate(aggregate ? 0 : Math.Min(box.Width, box.Height)),
                Cursor = Cursors.Hand,
                ToolTip = $"{item.Name}\n{item.SizeText} · {item.ShareText} of this folder\n" +
                    (!labeled ? "Zoom closer to these smaller items" : item.IsFolder ? "Zoom inside this folder" : "Click for details")
            };
            AutomationProperties.SetName(button, $"{item.Name}, {item.SizeText}, {item.Kind}");
            button.Click += (_, _) =>
            {
                if (_animating) return;
                if (!labeled) ZoomRegion(FocusSquare(box));
                else ItemInvoked?.Invoke(item);
            };
            if (item.Id > 0)
            {
                var menu = new ContextMenu();
                var delete = new MenuItem { Header = "Delete permanently…" };
                delete.Click += (_, _) => { if (!_animating) DeleteRequested?.Invoke(item); };
                menu.Items.Add(delete); button.ContextMenu = menu;
            }
            Place(layer, button, box); _buttons[tile.IsGroup ? -1 - _aggregateCount++ : item.Id] = button;
        }
    }

    private NestedDiskMap Expand(NestedDiskMap map, double width, double height)
    {
        if (map.Tiles.Count > 0) return map;
        if (!_expanded.TryGetValue(map, out var expanded))
            _expanded[map] = expanded = NestedDiskMap.Create(map.Items).Fit(width, height);
        return expanded;
    }

    private Brush ColorBrush(string color)
    {
        if (!_colors.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            _colors[color] = brush;
        }
        return brush;
    }

    private Brush Mosaic(NestedDiskMap map, Size size, Rect crop)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            void PaintMiniature(NestedDiskMap scene, Rect bounds, int depth)
            {
                if (scene.Tiles.Count == 0)
                {
                    scene = Expand(scene, bounds.Width * size.Width, bounds.Height * size.Height);
                }
                foreach (var tile in scene.Tiles)
                {
                    var box = Within(bounds, tile.Bounds);
                    if (tile.IsGroup && tile.Inside is { } child && depth < 2) PaintMiniature(child, box, depth + 1);
                    else
                    {
                        var side = Math.Min(box.Width * size.Width, box.Height * size.Height);
                        var gap = tile.IsGroup ? 0 : Math.Min(2, side * .06);
                        var fill = new Rect(box.X + gap / size.Width, box.Y + gap / size.Height,
                            Math.Max(0, box.Width - 2 * gap / size.Width), Math.Max(0, box.Height - 2 * gap / size.Height));
                        var radius = tile.IsGroup ? 0 : Math.Min(6, side * .12);
                        Geometry geometry = new RectangleGeometry(fill, radius / size.Width, radius / size.Height);
                        if (tile.Cutout is Rect cutout)
                            geometry = new CombinedGeometry(GeometryCombineMode.Exclude, geometry, new RectangleGeometry(Within(bounds, cutout)));
                        var color = tile.IsGroup ? tile.Inside?.Items.FirstOrDefault()?.ColorHex ?? tile.Item.ColorHex : tile.Item.ColorHex;
                        context.DrawGeometry(ColorBrush(color), null, geometry);
                    }
                }
            }
            PaintMiniature(map, new Rect(0, 0, 1, 1), 0);
        }
        drawing.Freeze();
        var brush = new DrawingBrush(drawing) { Viewbox = crop, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    private static void Place(Canvas layer, FrameworkElement element, Rect box)
    {
        SetLeft(element, box.X); SetTop(element, box.Y); layer.Children.Add(element);
    }

    private FrameworkElement Label(DiskUsageItem item, double width, double height)
    {
        var roomy = width >= 140 && height >= 125;
        var label = new Grid { Margin = new Thickness(roomy ? 14 : 8), IsHitTestVisible = false };
        label.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        label.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        label.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new DockPanel();
        if (width >= 110) heading.Children.Add(new TextBlock { Text = item.Glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, Margin = new Thickness(0, 3, 7, 0) });
        heading.Children.Add(new TextBlock { Text = item.Name, FontSize = roomy ? 15 : 12, FontWeight = FontWeights.SemiBold,
            TextWrapping = roomy ? TextWrapping.Wrap : TextWrapping.NoWrap, MaxHeight = roomy ? 42 : 20,
            TextTrimming = TextTrimming.CharacterEllipsis });
        label.Children.Add(heading);
        var size = new StackPanel();
        var share = item.ShareText;
        if (roomy) size.Children.Add(new TextBlock { Text = share, FontSize = 26, FontWeight = FontWeights.Light });
        size.Children.Add(new TextBlock { Text = roomy ? item.SizeText : $"{item.SizeText} · {share}",
            FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetRow(size, 2); label.Children.Add(size);
        return label;
    }

    private static DoubleAnimation Motion(double from, double to) => new(from, to, TimeSpan.FromMilliseconds(280))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };

    private static ControlTemplate TileTemplate(double side)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.MarginProperty, new Thickness(Math.Min(2, side * .06)));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Button.Background))
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(Math.Min(2, side * .06)));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(Math.Min(6, side * .12)));
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Button.BorderBrush))
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.Name = "TileBorder"; border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        foreach (var property in new[] { IsMouseOverProperty, IsKeyboardFocusedProperty })
        {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.White, "TileBorder")); template.Triggers.Add(trigger);
        }
        return template;
    }
}


