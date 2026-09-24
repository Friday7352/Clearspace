using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Clearspace.Services;

namespace Clearspace.Controls;

// NEW (round 46): experimental (public only so XAML can create it). The folder you are in as a field of 3D blocks - the map's squarified layout
// extruded, with what the height means chosen by the viewer. It is a separate control over the map's
// area; the map underneath is untouched and keeps following navigation, so switching back is instant.
//
// Everything that reads the snapshot runs off the UI thread and produces plain block records; the UI
// thread only turns them into one mesh with one material. Colours come from a small palette texture that
// each block's vertices point into, so thousands of differently shaded blocks are still a single draw.
internal enum Height3DMode
{
    Stacked,    // every folder a slab, its contents standing on it: the nesting as a stepped city
    Size,       // columns as tall as they are big
    FileCount,  // columns as tall as the number of files inside
    FileType,   // coloured by file type, as tall as that type's share of the folder
}

public sealed class DiskBlocks3DView : Grid
{
    // ------------------------------------------------------------------ tuning
    private const int BlockBudget = 15_000;      // boxes; each is 20 vertices and 10 triangles
    private const int ChildLimit = 160;          // largest children shown per folder; the rest become one block
    private const int MaxDepth = 5;
    private const double MinSide = .0025;        // world units: smaller blocks are left to their parent
    private const double ExpandSide = .035;      // a folder at least this big on both sides shows its contents
    private const double Width3D = 1.6, Depth3D = 1.0;
    private const double Slab = .022, Plate = .004, Tallest = .55;

    public event Action<int>? FolderOpened;
    public event Action<int>? ItemSelected;

    private readonly Viewport3D _viewport = new() { ClipToBounds = true };
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 42, NearPlaneDistance = .01, FarPlaneDistance = 50 };
    private readonly ModelVisual3D _content = new();
    private readonly ScaleTransform3D _grow = new(1, 1, 1);
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _card = new();
    private readonly TextBlock _cardTitle = new() { FontWeight = FontWeights.SemiBold, FontSize = 13 };
    private readonly TextBlock _cardDetail = new() { FontSize = 11.5 };
    private readonly TextBlock _status = new() { FontSize = 11, Margin = new Thickness(12, 10, 12, 0) };
    private readonly StackPanel _legend = new() { Margin = new Thickness(12, 30, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly List<(TextBlock Label, Point3D Anchor)> _labels = [];

    // What was last asked for (a refreshed snapshot with the same folder, totals and mode is not rebuilt),
    // the newest snapshot (for navigation), and what is on screen (for names and hover).
    private (string Path, Height3DMode Mode, long Bytes, long Files)? _requested;
    private DiskUsageSnapshot? _latest;
    private Result? _shown;
    private CancellationTokenSource? _work;
    private Block[] _blocks = [];
    private double _labelsUntil;   // labels follow the grow animation until then

    // Orbit camera about a target on the ground.
    private double _yaw = -32, _pitch = 48, _distance = 2.3;
    private Point3D _target = new(Width3D / 2, 0, Depth3D / 2);
    private Point _press;
    private bool _orbiting, _panning, _moved;
    private int _hoverBlock = -1;

    private static readonly Brush Ink = Frozen(Color.FromRgb(0xEC, 0xE9, 0xE3));
    private static readonly Brush InkMuted = Frozen(Color.FromRgb(0x9C, 0x96, 0x8D));
    private static readonly Color Background3D = Color.FromRgb(0x1A, 0x19, 0x17);

    public DiskBlocks3DView()
    {
        Background = new SolidColorBrush(Background3D);
        ClipToBounds = true;
        _viewport.Camera = _camera;
        _viewport.Children.Add(new ModelVisual3D { Content = Lights() });
        _content.Transform = _grow;
        _viewport.Children.Add(_content);
        Children.Add(_viewport);
        _status.Foreground = InkMuted;
        _status.VerticalAlignment = VerticalAlignment.Top;
        _status.IsHitTestVisible = false;
        Children.Add(_status);
        _legend.IsHitTestVisible = false;
        _legend.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_legend);
        _cardTitle.Foreground = Ink;
        _cardDetail.Foreground = InkMuted;
        _card.Child = new StackPanel { Children = { _cardTitle, _cardDetail } };
        _card.Background = Frozen(Color.FromArgb(0xF4, 0x23, 0x22, 0x20));
        _card.BorderBrush = Frozen(Color.FromRgb(0x3A, 0x37, 0x32));
        _card.BorderThickness = new Thickness(1);
        _card.CornerRadius = new CornerRadius(8);
        _card.Padding = new Thickness(12, 9, 12, 10);
        _card.Visibility = Visibility.Collapsed;
        _overlay.Children.Add(_card);
        Children.Add(_overlay);
        UpdateCamera();
        Focusable = true;
        // Hidden mid-build: forget what was asked for, so showing it again builds it rather than skipping.
        IsVisibleChanged += (_, _) => { if (!IsVisible && _work is not null) { Cancel(); _requested = null; } };
    }

    /// <summary>Shows a folder of a snapshot. Rebuilds only if something that affects the blocks changed.</summary>
    internal void Show(DiskUsageSnapshot snapshot, int folder, Height3DMode mode)
    {
        _latest = snapshot;
        // Live refreshes replace the snapshot every few seconds while a drive is indexed; the same folder
        // with the same totals looks the same, so it keeps what is on screen (and the hover under the pointer).
        var key = (snapshot.PathFor(folder), mode, snapshot.BytesOf(folder), snapshot.FilesOf(folder));
        if (_requested is { } requested && requested.Mode == key.mode && requested.Bytes == key.Item3 && requested.Files == key.Item4 &&
            string.Equals(requested.Path, key.Item1, StringComparison.OrdinalIgnoreCase)) return;
        _requested = key;
        Cancel();
        var work = _work = new CancellationTokenSource();
        var token = work.Token;
        if (_shown is null) _status.Text = "Building 3D view…";
        Task.Run(() =>
        {
            var result = Layout(snapshot, folder, mode, token);
            // The mesh is built and frozen here too, so the UI thread only swaps it in.
            var (mesh, material) = BuildMesh(result.Blocks);
            return result with { Mesh = mesh, Material = material, Snapshot = snapshot, Folder = folder, Path = key.Item1, Mode = mode };
        }, token).ContinueWith(task => Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_work, work)) return;
            _work = null;
            if (task.Status == TaskStatus.RanToCompletion) Present(task.Result);
            else if (task.Exception is { } failure)
            {
                _requested = null;   // try again on the next Show
                _status.Text = $"The 3D view could not be built: {failure.GetBaseException().Message}";
            }
        }), TaskScheduler.Default);
    }

    /// <summary>Empties the view, with a message - a drive that is waiting for its index.</summary>
    internal void Clear(string? message)
    {
        Cancel();
        _requested = null;
        _shown = null;
        _blocks = [];
        _content.Content = null;
        foreach (var (label, _) in _labels) _overlay.Children.Remove(label);
        _labels.Clear();
        _legend.Children.Clear();
        _card.Visibility = Visibility.Collapsed;
        _status.Text = message ?? "";
    }

    private void Cancel()
    {
        _work?.Cancel();
        _work = null;
    }

    // ------------------------------------------------------------------ layout (thread pool)

    private record struct Block(int Id, int Parent, double X, double Z, double W, double D, int Depth, bool IsFolder,
        long Bytes, long Files, uint Color, int Kind, bool Expanded, double Y0, double H);

    private sealed record Result(Block[] Blocks, string Summary, (string Name, uint Color, double Share)[] Legend)
    {
        public MeshGeometry3D? Mesh { get; init; }
        public Material? Material { get; init; }
        public DiskUsageSnapshot? Snapshot { get; init; }
        public int Folder { get; init; }
        public string Path { get; init; } = "";
        public Height3DMode Mode { get; init; }
    }

    private static Result Layout(DiskUsageSnapshot snapshot, int folder, Height3DMode mode, CancellationToken token)
    {
        var blocks = new List<Block>(4096);
        var palette = DiskUsagePalette.Branches.Select(hex => Pack((Color)ColorConverter.ConvertFromString(hex))).ToArray();
        var group = Pack((Color)ColorConverter.ConvertFromString(DiskUsagePalette.GroupColor));
        var total = Math.Max(1, snapshot.BytesOf(folder));
        // Folders are expanded largest first, so the budget is spent where the eye goes.
        var queue = new PriorityQueue<int, double>();
        Expand(-1, folder, 0, 0, Width3D, Depth3D, 0, 0);
        while (queue.TryDequeue(out var index, out _) && blocks.Count < BlockBudget)
        {
            token.ThrowIfCancellationRequested();
            var block = blocks[index];
            var inset = Math.Min(block.W, block.D) * .045;
            if (Expand(index, block.Id, block.X + inset, block.Z + inset, block.W - inset * 2, block.D - inset * 2, block.Depth + 1, block.Color))
                blocks[index] = block with { Expanded = true };
        }

        // File types: shares over the whole folder, and each shown folder's dominant type.
        var kindShare = new double[DiskUsagePalette.KindCount];
        if (mode == Height3DMode.FileType)
        {
            var kindBytes = new long[DiskUsagePalette.KindCount];
            var stack = new Stack<int>();
            stack.Push(folder);
            var children = new List<int>();
            while (stack.TryPop(out var id))
            {
                token.ThrowIfCancellationRequested();
                children.Clear();
                snapshot.ChildIds(id, children);
                foreach (var child in children)
                    if (snapshot.IsFolderEntry(child)) stack.Push(child);
                    else if (snapshot.BytesOf(child) > 0) kindBytes[DiskUsagePalette.KindOf(snapshot.NameOf(child))] += snapshot.BytesOf(child);
            }
            var sum = Math.Max(1, kindBytes.Sum());
            for (var k = 0; k < kindShare.Length; k++) kindShare[k] = kindBytes[k] / (double)sum;
            for (var i = 0; i < blocks.Count; i++)
                if (!blocks[i].Expanded) blocks[i] = blocks[i] with { Kind = DominantKind(snapshot, blocks[i], token) };
        }

        // Heights, parents before children (they were created in that order).
        long maxBytes = 1, maxFiles = 1;
        foreach (var block in blocks)
            if (!block.Expanded) { maxBytes = Math.Max(maxBytes, block.Bytes); maxFiles = Math.Max(maxFiles, block.Files); }
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var y0 = block.Parent < 0 ? 0 : blocks[block.Parent].Y0 + blocks[block.Parent].H;
            double h;
            if (mode == Height3DMode.Stacked) h = Slab;
            else if (block.Expanded) h = Plate;
            else h = mode switch
            {
                Height3DMode.Size => Tallest * Math.Pow(block.Bytes / (double)maxBytes, .5),
                Height3DMode.FileCount => Tallest * Math.Log(1 + block.Files) / Math.Log(1 + maxFiles),
                _ => Tallest * Math.Sqrt(kindShare[block.Kind]),
            };
            var color = mode == Height3DMode.FileType && !block.Expanded ? KindColor(block.Kind, palette) : block.Color;
            blocks[i] = block with { Y0 = y0, H = Math.Max(h, Plate * .5), Color = color };
        }

        var legend = mode == Height3DMode.FileType
            ? Enumerable.Range(0, kindShare.Length).Where(k => kindShare[k] > .005).OrderByDescending(k => kindShare[k])
                .Select(k => (DiskUsagePalette.KindName(k), KindColor(k, palette), kindShare[k])).ToArray()
            : [];
        var summary = $"{blocks.Count:N0} blocks · {Describe(mode)}";
        return new Result([.. blocks], summary, legend);

        // Lays out a folder's children inside a rectangle; returns whether anything was added.
        bool Expand(int parent, int id, double x, double z, double w, double d, int depth, uint parentColor)
        {
            if (w < MinSide || d < MinSide) return false;
            var ids = new List<int>();
            snapshot.ChildIds(id, ids);
            var sorted = ids.Where(child => snapshot.BytesOf(child) > 0).OrderByDescending(snapshot.BytesOf).ToArray();
            if (sorted.Length == 0) return false;
            var shown = sorted.Take(ChildLimit).ToArray();
            var restBytes = sorted.Skip(ChildLimit).Sum(snapshot.BytesOf);
            var restFiles = sorted.Skip(ChildLimit).Sum(snapshot.FilesOf);
            var weights = shown.Select(snapshot.BytesOf).Append(restBytes).Where(bytes => bytes > 0).ToArray();
            var added = false;
            foreach (var tile in SquarifiedTreemap.Layout(weights, w, d))
            {
                var rest = tile.ItemIndex >= shown.Length;
                var child = rest ? -1 : shown[tile.ItemIndex];
                var bx = x + tile.X; var bz = z + tile.Y;
                var gap = Math.Min(Math.Min(tile.Width, tile.Height) * .06, .006);
                var bw = tile.Width - gap; var bd = tile.Height - gap;
                if (bw < MinSide || bd < MinSide) continue;
                var isFolder = !rest && snapshot.IsFolderEntry(child);
                var bytes = rest ? restBytes : snapshot.BytesOf(child);
                var files = rest ? restFiles : Math.Max(1, snapshot.FilesOf(child));
                var color = rest ? group
                    : depth == 0 ? palette[tile.ItemIndex % palette.Length]
                    : Shade(parentColor, (1 - .07 * Math.Min(depth, 4)) * (.84 + .22 * Spread(snapshot.NameOf(child))));
                blocks.Add(new Block(child, parent, bx + gap / 2, bz + gap / 2, bw, bd, depth, isFolder, bytes, files, color,
                    DiskUsagePalette.Other, false, 0, 0));
                added = true;
                if (isFolder && depth + 1 < MaxDepth && bw >= ExpandSide && bd >= ExpandSide)
                    queue.Enqueue(blocks.Count - 1, -(bw * bd));
                if (blocks.Count >= BlockBudget) break;
            }
            return added;
        }
    }

    private static int DominantKind(DiskUsageSnapshot snapshot, Block block, CancellationToken token)
    {
        if (block.Id < 0) return DiskUsagePalette.Other;
        if (!block.IsFolder) return DiskUsagePalette.KindOf(snapshot.NameOf(block.Id));
        var bytes = new long[DiskUsagePalette.KindCount];
        var stack = new Stack<int>();
        var children = new List<int>();
        stack.Push(block.Id);
        while (stack.TryPop(out var id))
        {
            token.ThrowIfCancellationRequested();
            children.Clear();
            snapshot.ChildIds(id, children);
            foreach (var child in children)
                if (snapshot.IsFolderEntry(child)) stack.Push(child);
                else bytes[DiskUsagePalette.KindOf(snapshot.NameOf(child))] += snapshot.BytesOf(child);
        }
        var best = DiskUsagePalette.Other;
        for (var k = 0; k < bytes.Length; k++) if (bytes[k] > bytes[best]) best = k;
        return best;
    }

    private static uint KindColor(int kind, uint[] palette)
        => kind >= DiskUsagePalette.Other ? Pack(Color.FromRgb(0x6E, 0x69, 0x62)) : palette[kind % palette.Length];

    private static string Describe(Height3DMode mode) => mode switch
    {
        Height3DMode.Stacked => "stacked by folder depth",
        Height3DMode.Size => "height is size",
        Height3DMode.FileCount => "height is number of files",
        _ => "coloured by file type · height is that type's share",
    };

    // ------------------------------------------------------------------ mesh (UI thread)

    // One mesh for every block: five faces each (the bottom is never seen), coloured through a palette
    // texture with one texel per distinct colour. Built and frozen off the UI thread.
    private static (MeshGeometry3D Mesh, Material Material) BuildMesh(Block[] blocks)
    {
        var colors = new Dictionary<uint, int>();
        foreach (var block in blocks) colors.TryAdd(block.Color, colors.Count);
        var width = Math.Max(1, colors.Count);
        var pixels = new int[width];
        foreach (var (color, index) in colors) pixels[index] = unchecked((int)(0xFF000000 | color));
        var texture = BitmapSource.Create(width, 1, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        texture.Freeze();
        var brush = new ImageBrush(texture) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();

        var positions = new Point3DCollection(blocks.Length * 20);
        var normals = new Vector3DCollection(blocks.Length * 20);
        var uvs = new PointCollection(blocks.Length * 20);
        var triangles = new Int32Collection(blocks.Length * 30);
        foreach (var block in blocks)
        {
            var u = (colors[block.Color] + .5) / width;
            double x0 = block.X, x1 = block.X + block.W, z0 = block.Z, z1 = block.Z + block.D, y0 = block.Y0, y1 = block.Y0 + block.H;
            Face(new(x0, y1, z0), new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0), new(0, 1, 0), u);   // top
            Face(new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1), new(0, 0, 1), u);   // front
            Face(new(x1, y0, z0), new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0), new(0, 0, -1), u);  // back
            Face(new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1), new(1, 0, 0), u);   // right
            Face(new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0), new(-1, 0, 0), u);  // left
        }
        var mesh = new MeshGeometry3D { Positions = positions, Normals = normals, TextureCoordinates = uvs, TriangleIndices = triangles };
        mesh.Freeze();
        return (mesh, material);

        void Face(Point3D a, Point3D b, Point3D c, Point3D d, Vector3D normal, double u)
        {
            var start = positions.Count;
            positions.Add(a); positions.Add(b); positions.Add(c); positions.Add(d);
            for (var i = 0; i < 4; i++) { normals.Add(normal); uvs.Add(new Point(u, .5)); }
            triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
            triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
        }
    }

    private void Present(Result result)
    {
        // A different folder than the one on screen grows out of the ground with the camera reset; the same
        // folder (a new height mode, or its sizes changed) reshapes in place and keeps the view.
        var newFolder = !string.Equals(result.Path, _shown?.Path, StringComparison.OrdinalIgnoreCase);
        _shown = result;
        _blocks = result.Blocks;
        _hoverBlock = -1;
        _card.Visibility = Visibility.Collapsed;
        var model = new Model3DGroup();
        model.Children.Add(Ground());
        model.Children.Add(new GeometryModel3D(result.Mesh, result.Material) { BackMaterial = result.Material });
        _content.Content = model;

        var grow = new DoubleAnimation(newFolder ? 0 : _grow.ScaleY, 1, TimeSpan.FromSeconds(.7))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _grow.BeginAnimation(ScaleTransform3D.ScaleYProperty, grow);
        // The names ride on top of the blocks while they grow.
        _labelsUntil = Environment.TickCount64 / 1000d + .8;
        CompositionTarget.Rendering -= OnGrowing;
        CompositionTarget.Rendering += OnGrowing;
        if (newFolder)
        {
            _target = new Point3D(Width3D / 2, 0, Depth3D / 2);
            _distance = 2.3;
            UpdateCamera();
        }

        _status.Text = $"3D (experimental) · {result.Summary}";
        _legend.Children.Clear();
        foreach (var (name, color, share) in result.Legend.Take(10))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            row.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = Frozen(Unpack(color)),
                Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = $"{name} · {share:P0}", Foreground = InkMuted, FontSize = 11 });
            _legend.Children.Add(row);
        }
        BuildLabels();
    }

    private void OnGrowing(object? sender, EventArgs e)
    {
        PlaceLabels();
        if (Environment.TickCount64 / 1000d > _labelsUntil || !IsVisible) CompositionTarget.Rendering -= OnGrowing;
    }

    private static Model3DGroup Lights()
    {
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0x62, 0x62, 0x62)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0xC4, 0xC2, 0xBC), new Vector3D(-.45, -1, -.3)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x38, 0x3A, 0x40), new Vector3D(.6, -.25, .8)));
        lights.Freeze();
        return lights;
    }

    private static GeometryModel3D Ground()
    {
        const double margin = .06;
        var mesh = new MeshGeometry3D
        {
            Positions = [new(-margin, -.001, -margin), new(-margin, -.001, Depth3D + margin),
                new(Width3D + margin, -.001, Depth3D + margin), new(Width3D + margin, -.001, -margin)],
            TriangleIndices = [0, 1, 2, 0, 2, 3],
            Normals = [new(0, 1, 0), new(0, 1, 0), new(0, 1, 0), new(0, 1, 0)],
        };
        var material = new DiffuseMaterial(Frozen(Color.FromRgb(0x2A, 0x28, 0x25)));
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    // Names on the largest blocks directly inside the folder, projected to the screen as the camera moves.
    private void BuildLabels()
    {
        foreach (var (label, _) in _labels) _overlay.Children.Remove(label);
        _labels.Clear();
        if (_shown?.Snapshot is not { } snapshot) return;
        foreach (var block in _blocks.Where(block => block.Depth == 0 && block.Id >= 0).OrderByDescending(block => block.W * block.D).Take(14))
        {
            var label = new TextBlock
            {
                Text = snapshot.Item(block.Id).Name, Foreground = Ink, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = .9 },
            };
            _labels.Add((label, new Point3D(block.X + block.W / 2, block.Y0 + block.H, block.Z + block.D / 2)));
            _overlay.Children.Insert(0, label);
        }
        PlaceLabels();
    }

    private void PlaceLabels()
    {
        foreach (var (label, anchor) in _labels)
        {
            if (Project(new Point3D(anchor.X, anchor.Y * _grow.ScaleY, anchor.Z)) is { } point)
            {
                label.Visibility = Visibility.Visible;
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, point.X - label.DesiredSize.Width / 2);
                Canvas.SetTop(label, point.Y - label.DesiredSize.Height - 4);
            }
            else label.Visibility = Visibility.Collapsed;
        }
    }

    // World point to control pixels through the perspective camera; null when behind the camera.
    private Point? Project(Point3D world)
    {
        if (ActualWidth < 1 || ActualHeight < 1) return null;
        var forward = _camera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, _camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        var relative = world - _camera.Position;
        var depth = Vector3D.DotProduct(relative, forward);
        if (depth <= _camera.NearPlaneDistance) return null;
        var scale = 1 / Math.Tan(_camera.FieldOfView * Math.PI / 360);   // horizontal field of view in WPF
        var x = Vector3D.DotProduct(relative, right) / depth * scale;
        var y = Vector3D.DotProduct(relative, up) / depth * scale * (ActualWidth / ActualHeight);
        return new Point((x + 1) / 2 * ActualWidth, (1 - y) / 2 * ActualHeight);
    }

    // ------------------------------------------------------------------ camera and input

    private void UpdateCamera()
    {
        var yaw = _yaw * Math.PI / 180;
        var pitch = _pitch * Math.PI / 180;
        var offset = new Vector3D(Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch), Math.Cos(pitch) * Math.Cos(yaw)) * _distance;
        _camera.Position = _target + offset;
        _camera.LookDirection = -offset;
        _camera.UpDirection = new Vector3D(0, 1, 0);
        PlaceLabels();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (e.ClickCount == 2 && BlockAt(e.GetPosition(this)) is { } index && _blocks[index] is { Id: >= 0 } block)
        {
            if (Resolve(block.Id, block.IsFolder) is { } id)
            {
                if (block.IsFolder) FolderOpened?.Invoke(id);
                else ItemSelected?.Invoke(id);
            }
            e.Handled = true;
            return;
        }
        _press = e.GetPosition(this);
        _orbiting = true;
        _panning = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _moved = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        _press = e.GetPosition(this);
        _orbiting = _panning = true;
        CaptureMouse();
        e.Handled = true;
    }

    // The blocks on screen may come from an older snapshot than the one the view now uses (a live refresh
    // that did not need a rebuild), and a rebuilt index numbers its entries afresh - so ids go by path.
    private int? Resolve(int id, bool isFolder)
    {
        if (_shown?.Snapshot is not { } shown || _latest is not { } latest) return null;
        if (ReferenceEquals(shown, latest)) return id;
        var path = shown.PathFor(id);
        if (isFolder) return latest.FindFolder(path) is var folder and >= 0 ? folder : null;
        if (System.IO.Path.GetDirectoryName(path) is not { } parent || latest.FindFolder(parent) is not (var inside and >= 0)) return null;
        var children = new List<int>();
        latest.ChildIds(inside, children);
        var name = System.IO.Path.GetFileName(path);
        foreach (var child in children)
            if (latest.NameOf(child).Equals(name, StringComparison.OrdinalIgnoreCase)) return child;
        return null;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _orbiting = _panning = false;   // Alt-Tab mid-drag must not leave the camera following the pointer
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_orbiting) return;
        _orbiting = _panning = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_orbiting)
        {
            var delta = point - _press;
            _press = point;
            _moved |= delta.Length > 1;
            if (_panning)
            {
                // Move the target across the ground, in the camera's own left-right and forward directions.
                var yaw = _yaw * Math.PI / 180;
                var right = new Vector3D(Math.Cos(yaw), 0, -Math.Sin(yaw));
                var forward = new Vector3D(-Math.Sin(yaw), 0, -Math.Cos(yaw));
                var speed = _distance * .0016;
                _target += (-right * delta.X + forward * delta.Y) * speed;
            }
            else
            {
                _yaw -= delta.X * .35;
                _pitch = Math.Clamp(_pitch + delta.Y * .3, 8, 88);
            }
            UpdateCamera();
            _card.Visibility = Visibility.Collapsed;
            return;
        }
        Hover(point);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _card.Visibility = Visibility.Collapsed;
        _hoverBlock = -1;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _distance = Math.Clamp(_distance * Math.Pow(.88, e.Delta / 120d), .15, 8);
        UpdateCamera();
        e.Handled = true;
    }

    // The block under a point: a ray from the camera through it, tested against every block's box. WPF's own
    // hit test would walk every triangle of the one big mesh on each mouse move; boxes are a few microseconds.
    private int? BlockAt(Point point)
    {
        if (_blocks.Length == 0 || ActualWidth < 1 || ActualHeight < 1) return null;
        var forward = _camera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, _camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        var scale = 1 / Math.Tan(_camera.FieldOfView * Math.PI / 360);
        var nx = point.X / ActualWidth * 2 - 1;
        var ny = 1 - point.Y / ActualHeight * 2;
        var ray = forward + right * (nx / scale) + up * (ny / (scale * ActualWidth / ActualHeight));   // Project, inverted
        var origin = _camera.Position;
        var grown = _grow.ScaleY;
        var nearest = double.MaxValue;
        int? found = null;
        for (var i = 0; i < _blocks.Length; i++)
        {
            var block = _blocks[i];
            if (Enter(origin, ray, block.X, block.X + block.W, block.Y0 * grown, (block.Y0 + block.H) * grown, block.Z, block.Z + block.D) is { } t && t < nearest)
            {
                nearest = t;
                found = i;
            }
        }
        return found;
    }

    // Where a ray enters a box (slab method), or null if it misses.
    private static double? Enter(Point3D o, Vector3D d, double x0, double x1, double y0, double y1, double z0, double z1)
    {
        double near = 0, far = double.MaxValue;
        return Slab(o.X, d.X, x0, x1) && Slab(o.Y, d.Y, y0, y1) && Slab(o.Z, d.Z, z0, z1) ? near : null;

        bool Slab(double start, double step, double low, double high)
        {
            if (Math.Abs(step) < 1e-12) return start >= low && start <= high;
            var a = (low - start) / step;
            var b = (high - start) / step;
            if (a > b) (a, b) = (b, a);
            near = Math.Max(near, a);
            far = Math.Min(far, b);
            return near <= far;
        }
    }

    private void Hover(Point point)
    {
        if (BlockAt(point) is not { } index || _shown?.Snapshot is not { } snapshot)
        {
            _card.Visibility = Visibility.Collapsed;
            _hoverBlock = -1;
            return;
        }
        if (index != _hoverBlock)
        {
            _hoverBlock = index;
            var block = _blocks[index];
            var total = Math.Max(1, snapshot.BytesOf(_shown.Folder));
            _cardTitle.Text = block.Id < 0 ? "Smaller items" : snapshot.Item(block.Id).Name;
            var kind = _shown.Mode == Height3DMode.FileType ? $" · mostly {DiskUsagePalette.KindName(block.Kind).ToLowerInvariant()}" : "";
            _cardDetail.Text = $"{DiskUsageSnapshot.FormatBytes(block.Bytes)} · {block.Bytes * 100d / total:0.#}% · " +
                $"{block.Files:N0} file{(block.Files == 1 ? "" : "s")}{kind}" +
                (block.IsFolder ? "\nDouble-click to open" : "");
        }
        _card.Visibility = Visibility.Visible;
        _card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var x = point.X + 16;
        var y = point.Y + 18;
        if (x + _card.DesiredSize.Width > ActualWidth - 6) x = point.X - 12 - _card.DesiredSize.Width;
        if (y + _card.DesiredSize.Height > ActualHeight - 6) y = point.Y - 12 - _card.DesiredSize.Height;
        Canvas.SetLeft(_card, Math.Max(6, x));
        Canvas.SetTop(_card, Math.Max(6, y));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        PlaceLabels();
    }

    // ------------------------------------------------------------------ colour helpers

    private static uint Pack(Color color) => (uint)color.R << 16 | (uint)color.G << 8 | color.B;
    private static Color Unpack(uint color) => Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);

    private static uint Shade(uint color, double factor)
    {
        var r = (uint)Math.Clamp((color >> 16 & 255) * factor, 0, 255);
        var g = (uint)Math.Clamp((color >> 8 & 255) * factor, 0, 255);
        var b = (uint)Math.Clamp((color & 255) * factor, 0, 255);
        // Quantised, so the palette texture stays a few hundred entries however many blocks there are.
        return (r & 0xFC) << 16 | (g & 0xFC) << 8 | (b & 0xFC);
    }

    private static double Spread(ReadOnlySpan<char> name)
    {
        var hash = 2166136261u;
        foreach (var character in name) hash = (hash ^ character) * 16777619u;
        return ((hash >> 8) & 1023) / 1023d;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
