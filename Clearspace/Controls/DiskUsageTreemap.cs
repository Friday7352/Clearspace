using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clearspace.Services;

namespace Clearspace.Controls;

// REWRITTEN: the disk usage map is one continuous, zoomable surface.
//
//   * The whole drive is one nested treemap in fixed "world" coordinates. A folder's
//     children are laid out inside that folder's own rectangle, once, and never move.
//   * A single camera looks at the world. Opening a folder, Back/Up, breadcrumbs and
//     the wheel all just move the camera, so every transition is continuous by design.
//   * Camera flights interpolate around the zoom's fixed point (the one world point that
//     stays put on screen), so a folder grows straight out of where it sits.
//   * Deeper folders load on a background thread as they get big on screen and fade in.
//
// ROUND 2 (clarity and speed):
//   * Only one level is labeled: the items directly inside the folder you're in.
//   * Each of those items gets its own hue and everything inside it shares that hue, so the
//     color tells you which labeled block a small tile belongs to. Colors cross-fade when
//     you move into or out of a folder.
//   * Hover, outlines and the hover card live on their own layer; moving the mouse no longer
//     redraws thousands of tiles.
//   * No opacity layers (PushOpacity is expensive in WPF); fades are baked into brush alpha.
//   * Tiles under ~4 px aren't drawn individually - the parent's color shows through instead -
//     and folders open later, which cuts per-frame primitives by an order of magnitude.
//
// ROUND 3 (big folders):
//   * Tiles are batched into one StreamGeometry per (depth, color) and drawn with a single call
//     each - roughly 100 draw calls per frame instead of one per tile. Depth order is kept, so
//     children still paint over their parents; labels and tags are drawn after all tiles.
//   * Label text is only re-tinted or re-trimmed when its fade step or width actually changes.
//
// ROUND 9 (software rasterizer): WPF tessellates every changed shape into triangles on the CPU,
//   which made dense zooming slow. Tiles are now plain rectangles filled straight into a pixel
//   buffer by a parallel rasterizer (horizontal bands across CPU cores, fractional edge
//   coverage for subpixel-smooth motion) and shown through a WriteableBitmap. Cost scales with
//   pixels, not tiles, so frames stay full detail even mid-zoom (no reduced "motion" frames and
//   no reused-frame transforms any more). Labels, tags and the veil are a WPF layer on top.
//
// ROUND 8 (fractal): folders open from ~18 px and preload from ~10 px, so nearly every block shows
//   its inner structure and zooming only reveals finer detail. Frames and gaps get lighter below
//   the labeled level, and an open folder's backdrop is its own hue, so detail too small to draw
//   blends into its parent instead of showing dark. All loading and layout (folders and grouped
//   small items) runs off the UI thread; the per-frame tile budget, shared by screen area, keeps
//   the cost of a frame constant however deep the nesting goes.
//
// ROUND 4: the glowing traces/hover halo were tried and reverted (plain outline on hover).
//
// ROUND 5 (smooth zoom): while the camera moves, the last rendered scene is scaled and shifted
//   on the GPU (a transform on the scene layer) instead of being redrawn every frame. The scene
//   is redrawn only when the zoom has drifted ~10%, the view would leave the pre-drawn margin,
//   a few frames have passed during a fade, or the camera settles (one crisp final frame).
public sealed class DiskUsageTreemap : FrameworkElement
{
    // ---------------------------------------------------------------- tuning
    private const int DirectLimit = DiskUsagePalette.DirectLimit;
    private const int NamedLimit = DiskUsagePalette.NamedLimit;
    private const int TailGroupSize = 150;      // "smaller items" blocks hold about this many
    private const int MaxTailGroups = 100;
    // CHANGED (round 10): slightly calmer density - the fractal look at a fraction of the cost.
    private const double MinTile = 1.6;         // px: smaller tiles are represented by their parent
    private const double ExpandStart = 24;      // px: a folder's contents begin to fade in
    private const double ExpandFull = 56;       // px: contents fully shown
    private const double PrefetchSize = 10;     // px: begin loading contents early (was 40)
    private const double WheelStep = .8;        // camera width multiplier per wheel notch
    private const double WheelSmoothing = .045; // s: time constant of the wheel's easing (round 12: was .075, snappier)
    private const double FocusDebounce = .14;   // s: wheel/drag focus must settle this long before the list follows
    private const double FolderMargin = .02;    // fraction of breathing room around a fitted folder
    private const double DimLevel = .6;         // opacity of the veil over everything outside the current folder
    private const double ColorFade = .3;        // s: cross-fade of colors and labels between levels
    private const int MaxLoads = 6;             // concurrent background loads (CHANGED round 8: was 3)
    private const int TileBudget = 30000;       // tiles per frame, shared by screen area (round 10: was 60000)
    // CHANGED (round 13): full resolution always (the capped and motion buffers looked blurry).
    // The rasterizer now writes straight into the bitmap's memory, which removes a full-frame copy.
    private const double MaxPixels = 8_300_000; // only a safety cap (about a 4K screen)

    // ---------------------------------------------------------------- model
    private enum NodeState { Leaf, Collapsed, Pending, Ready }

    private record struct CachedText(FormattedText? Text, double Em, Brush? Tint = null, double Width = -1);

    private sealed class Node
    {
        public Node(DiskUsageItem item, Rect bounds, Node? parent, IReadOnlyList<DiskUsageItem>? members, long shareBase, int index)
        {
            Item = item;
            Bounds = bounds;
            Parent = parent;
            Members = members;
            Index = index;
            Depth = parent is null ? 0 : parent.Depth + 1;
            var share = shareBase <= 0 ? 0 : item.Bytes * 100d / shareBase;
            ShareText = share > 0 && share < .1 ? "<0.1%" : $"{share:0.#}%";
            State = item.Id < 0 ? (members is { Count: > 1 } ? NodeState.Collapsed : NodeState.Leaf)
                : item.IsFolder && item.Bytes > 0 ? NodeState.Collapsed : NodeState.Leaf;
        }

        public DiskUsageItem Item { get; }
        public Rect Bounds { get; }
        public Node? Parent { get; }
        public IReadOnlyList<DiskUsageItem>? Members { get; }
        public int Index { get; }                // rank among siblings (largest first)
        public int Depth { get; }
        public string ShareText { get; }
        public NodeState State { get; set; }
        public Node[] Children { get; set; } = [];
        public int FirstGroup { get; set; }      // NEW (round 15): index of the first "smaller items" child (named ones precede it)
        public double ReadyAt { get; set; } = double.NegativeInfinity;
        public bool IsGroup => Item.Id < 0;
        public bool IsFolder => Item.IsFolder && Item.Id >= 0;
        public bool IsContainer => IsGroup || IsFolder;
        public double Area => Bounds.Width * Bounds.Height;
        public CachedText LabelName, LabelDetail, PillName, PillSize;
        // Two-slot color cache: the level being faded from and the level being faded to.
        public Node? ColorKeyA, ColorKeyB;
        public Color ColorA, ColorB;
    }

    private readonly record struct Placement(DiskUsageItem Item, Rect Bounds, IReadOnlyList<DiskUsageItem>? Members);
    private readonly record struct Pill(Node Node, Rect Rect, FormattedText Name, FormattedText Size, Color Fill, double Alpha);

    private sealed class Flight(Rect[] points, double[] durations, double start)
    {
        public Rect[] Points { get; } = points;
        public double[] Durations { get; } = durations;
        public double Start { get; } = start;
    }

    // ---------------------------------------------------------------- state
    private Node? _root;
    private (DiskUsageItem Root, IReadOnlyList<int> Path, string? Missing)? _pending;
    private Func<int, CancellationToken, IReadOnlyList<DiskUsageItem>>? _children;
    private CancellationTokenSource _generation = new();
    private double _builtAspect = 1;
    private int _loads;

    private Rect _camera = new(0, 0, 1, 1);
    private Rect _target = new(0, 0, 1, 1);
    private Flight? _flight;
    private Node? _container;          // deepest folder or group the user is inside
    private Node? _focusNode;          // folder the view model is showing
    private int _reportedFocus;
    private Point? _focusPoint;        // world point the user is zooming toward (wheel), if any
    private Node? _candidate;
    private double _candidateSince;
    private bool _userMoved;           // only the user's own zooming may move the list
    private bool _wasZoomed;
    private Rect _dimWorld;
    private double _dimAlpha;
    private Node? _colorLevel;         // level whose children are colored and labeled
    private Node? _colorPrevious;      // level being faded out
    private double _colorSince = double.NegativeInfinity;
    private double _sourceFadeAt = double.NegativeInfinity;
    private string? _message;
    private string? _missingName;
    private int? _highlight;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _timeOffset;
    private double _lastFrame;
    private bool _hooked;
    private bool _sceneDirty = true;
    private bool _overlayDirty = true;
    private bool _needsFrame;

    // Two layers: the scene (tiles, labels, veil) and the overlay (hover, selection, card).
    private readonly DrawingVisual _scene = new();
    private readonly DrawingVisual _overlay = new();
    private readonly List<(Node Node, Rect Screen)> _drawn = [];
    // NEW (round 9): software-rendered tile layer.
    private readonly DrawingVisual _labels = new();
    internal readonly record struct TileCommand(float X0, float Y0, float X1, float Y1, uint Color, byte Alpha); // internal: shared with GpuTileRenderer

    // NEW (round 14): GPU rendering through Direct3D 9Ex + D3DImage; the CPU rasterizer is the fallback.
    private GpuTileRenderer? _gpu;
    private bool _gpuTried;
    private bool _gpuShown;
    private TileCommand[] _commands = new TileCommand[16384];
    private int _commandCount;
    private sealed class Surface
    {
        public WriteableBitmap? Bitmap;
        public double ScaleX = 1, ScaleY = 1;
    }
    private readonly Surface _full = new();
    private readonly Surface _motion = new();
    private Surface? _shown;          // the surface the scene layer currently displays
    private bool _renderMotion;       // the frame being rendered is a mid-motion one

    // NEW (round 12): F3 performance readout (milliseconds, smoothed).
    private bool _showStats;
    private double _statFrame, _statWalk, _statLabels, _statRaster, _statUpload, _statScene;
    private double _statWorstFrame, _statWorstWalk; // NEW (round 15): spikes, not just averages
    private int _statTiles, _statPixelsW, _statPixelsH, _gen2Start;
    private readonly Stopwatch _statClock = new();
    internal void ToggleStats()
    {
        _showStats = !_showStats;
        _gen2Start = GC.CollectionCount(2);
        _overlayDirty = true;
        RequestFrame();
    }
    private static void Smooth(ref double stat, double sample) => stat += (sample - stat) * .15;
    private readonly List<Action<DrawingContext>> _deferred = [];
    private bool _overlayAnimating;   // overlay-only frames (kept for future overlay fades)

    // NEW (round 5): scene reuse while the camera moves.
    private readonly MatrixTransform _sceneTransform = new();
    private Rect _sceneCamera = new(0, 0, 1, 1);  // camera the scene layer was last drawn with
    private int _framesSinceScene;
    private readonly List<(Node Node, double Area)> _wanted = [];
    private readonly HashSet<Node> _openPath = [];
    private Rect _view, _viewLoose;
    private int _tiles;
    private double _colorT = 1;
    private Node? _hover;
    private Point _mouse;
    private bool _mouseInside, _pressed, _dragging;
    private Point _press;
    private Rect _pressCamera;
    private readonly DispatcherTimer _rebuild;

    // ---------------------------------------------------------------- resources
    private static readonly Color[] BranchColors = DiskUsagePalette.Branches.Select(Parse).ToArray();
    private static readonly Color GroupColor = Parse(DiskUsagePalette.GroupColor);
    private static readonly Color White = Color.FromRgb(0xF6, 0xF3, 0xEE);
    private readonly Dictionary<uint, SolidColorBrush> _brushes = [];
    private Typeface _regular = new("Segoe UI");
    private Typeface _semibold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private double _pixelsPerDip = 1;
    private Color _baseColor = Color.FromRgb(0x1A, 0x19, 0x17);
    private SolidColorBrush _base = Frozen(Color.FromRgb(0x1A, 0x19, 0x17));
    private static readonly SolidColorBrush CardFill = Frozen(Color.FromArgb(0xF4, 0x23, 0x22, 0x20));
    private static readonly SolidColorBrush Ink = Frozen(Color.FromRgb(0xEC, 0xE9, 0xE3));
    private static readonly SolidColorBrush InkMuted = Frozen(Color.FromRgb(0x9C, 0x96, 0x8D));
    private static readonly SolidColorBrush InkFaint = Frozen(Color.FromRgb(0x6E, 0x68, 0x62));
    private static readonly Pen CardEdge = FrozenPen(Color.FromRgb(0x3A, 0x37, 0x32), 1);
    private static readonly Pen HoverEdge = FrozenPen(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF), 1.5);
    private static readonly Pen FolderEdge = FrozenPen(Color.FromArgb(0x60, 0xB0, 0xA9, 0x9E), 1);
    private static readonly Pen AccentEdge = FrozenPen(Color.FromRgb(0xD3, 0xA1, 0x5F), 2);

    // ---------------------------------------------------------------- events for the view
    internal event Action<int>? FolderFocused;              // the user zoomed/clicked into a different folder
    internal event Action<DiskUsageItem>? ItemSelected;     // a file in the current folder was clicked
    internal event Action<DiskUsageItem>? DeleteRequested;
    internal event Action? ZoomChanged;

    public DiskUsageTreemap()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = false;
        AddVisualChild(_scene);
        RenderOptions.SetBitmapScalingMode(_scene, BitmapScalingMode.Linear); // fast, smooth upscaling
        AddVisualChild(_labels); // NEW (round 9)
        AddVisualChild(_overlay);
        _scene.Transform = _sceneTransform; // NEW (round 5)
        _rebuild = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(220) };
        _rebuild.Tick += (_, _) => { _rebuild.Stop(); Rebuild(); };
        Loaded += (_, _) => { LoadResources(); RequestFrame(); };
        Unloaded += (_, _) =>
        {
            StopFrames();
            _rebuild.Stop();
            _gpu?.Dispose(); // NEW (round 14)
            _gpu = null;
            _gpuTried = _gpuShown = false;
            _shown = null;
        };
    }

    protected override int VisualChildrenCount => 3;
    protected override Visual GetVisualChild(int index) => index switch { 0 => _scene, 1 => _labels, _ => _overlay };

    // ================================================================= public surface

    /// <summary>Show a new index snapshot. <paramref name="path"/> lists folder ids below the root.</summary>
    internal void SetSource(DiskUsageItem root, Func<int, CancellationToken, IReadOnlyList<DiskUsageItem>> children,
        IReadOnlyList<int> path, string? missingName = null)
    {
        _generation.Cancel();
        _generation = new CancellationTokenSource();
        _loads = 0;
        _children = children;
        _pending = (root, path.ToArray(), missingName);
        _root = null;
        _flight = null;
        _hover = null;
        _focusPoint = null;
        _sourceFadeAt = Now;
        EnsureBuilt();
        RequestFrame();
    }

    /// <summary>Fly to a folder chosen outside the map (list, breadcrumbs, Back/Forward, Up).</summary>
    internal void ShowFolder(IReadOnlyList<int> path, string? missingName = null)
    {
        if (_root is null)
        {
            if (_pending is { } pending) _pending = (pending.Root, path.ToArray(), missingName);
            return;
        }
        var target = ExpandPath(path, out var complete);
        var id = path.Count == 0 ? _root.Item.Id : path[^1];
        _missingName = complete ? null : missingName;
        _message = complete ? null : $"“{missingName ?? "This folder"}” has no file sizes to show";
        _candidate = null;
        // A focus change the map reported itself comes back here; the camera is already there.
        if (complete && id == _reportedFocus && _focusNode == target) { RequestFrame(); return; }
        _reportedFocus = id;
        _focusNode = target;
        _userMoved = false;
        FlyTo(Fit(target));
    }

    // NEW: show nothing but a message (a drive that isn't indexed yet).
    internal void ClearSource(string? message)
    {
        _generation.Cancel();
        _generation = new CancellationTokenSource();
        _loads = 0;
        _pending = null;
        _root = null;
        _flight = null;
        _container = _focusNode = _colorLevel = _colorPrevious = null;
        _hover = null;
        _drawn.Clear();
        _message = message;
        RequestFrame();
    }

    // NEW: a message over the map while there is nothing to show yet (e.g. indexing).
    internal void SetMessage(string? message)
    {
        if (_root is not null && message is not null) return;
        _message = message;
        RequestFrame();
    }

    internal bool IsZoomed
    {
        get
        {
            if (_root is null || _focusNode is null) return false;
            var fit = Fit(_focusNode);
            return _target.Width < fit.Width * .97 ||
                   Math.Abs(Center(_target).X - Center(fit).X) > fit.Width * .03 ||
                   Math.Abs(Center(_target).Y - Center(fit).Y) > fit.Height * .03;
        }
    }

    /// <summary>Return to the whole current folder after zooming inside it.</summary>
    internal void ZoomOut()
    {
        if (_focusNode is null) return;
        _userMoved = false;
        FlyTo(Fit(_focusNode));
    }

    internal void Highlight(int? id)
    {
        _highlight = id;
        _overlayDirty = true;
        if (!_hooked) RenderOverlay();
    }

    // Test and diagnostics hooks.
    internal Rect Camera => _camera;
    // NEW (round 10): seconds since the user last zoomed, dragged or clicked (live refreshes wait for a pause).
    internal double SecondsSinceInteraction => Now - _lastInteraction;
    private double _lastInteraction = double.NegativeInfinity;
    internal bool IsAnimating => _flight is not null || !Near(_camera, _target);
    internal int FocusFolderId => _reportedFocus;
    internal int PendingLoads => _loads;
    internal string? CurrentContainerName => _container?.Item.Name;
    internal IReadOnlyList<(DiskUsageItem Item, Rect Screen)> VisibleTiles => _drawn.Select(d => (d.Node.Item, d.Screen)).ToArray();

    internal Rect? ScreenBoundsOf(int id)
    {
        var node = _root is null ? null : Find(_root, id);
        return node is null ? null : ToScreen(node.Bounds);
    }

    /// <summary>Advance the animation clock deterministically (tests, previews).</summary>
    internal void AdvanceTime(double seconds)
    {
        const double step = 1 / 60d;
        for (var elapsed = 0d; elapsed < seconds; elapsed += step)
        {
            _timeOffset += step;
            StepFrame(step);
        }
        _sceneDirty = _overlayDirty = true;
        InvalidateVisual();
    }

    internal bool ClickAt(Point point)
    {
        var hit = NodeAt(point);
        var target = hit is null ? null : ClickTarget(hit);
        if (target is null) return false;
        Activate(target);
        return true;
    }

    internal bool ZoomWithWheel(Point point, int delta)
    {
        if (_root is null || delta == 0 || ActualWidth < 1 || ActualHeight < 1) return false;
        _userMoved = true;
        _message = null;
        // Keep accumulating toward the running target, but anchor on what is on screen now,
        // so the point under the pointer stays locked even mid-animation.
        var basis = _flight is null ? _target : _camera;
        _flight = null;
        var u = Math.Clamp(point.X / ActualWidth, 0, 1);
        var v = Math.Clamp(point.Y / ActualHeight, 0, 1);
        var anchorX = _camera.X + u * _camera.Width;
        var anchorY = _camera.Y + v * _camera.Height;
        var factor = Math.Pow(WheelStep, delta / 120d);
        var width = basis.Width * factor;
        var height = basis.Height * factor;
        var next = Clamp(new Rect(anchorX - u * width, anchorY - v * height, width, height));
        _focusPoint = new Point(anchorX, anchorY);
        // Already showing as much as possible: scrolling out steps up to the parent folder,
        // so folders that span the whole drive's height can still be left with the wheel.
        if (delta < 0 && next.Width <= basis.Width * 1.0001 && _focusNode is { Parent: not null } focus)
            Report(FolderOf(focus.Parent));
        _target = next;
        RequestCameraFrame();
        return true;
    }

    // ================================================================= building the world

    private void EnsureBuilt()
    {
        if (_root is not null || _pending is not { } pending || ActualWidth < 1 || ActualHeight < 1) return;
        LoadResources();
        _builtAspect = ActualWidth / ActualHeight;
        _root = new Node(pending.Root, new Rect(0, 0, _builtAspect, 1), null, null, pending.Root.Bytes, 0);
        _pending = null;
        ExpandSync(_root);
        var focus = ExpandPath(pending.Path, out var complete);
        _missingName = complete ? null : pending.Missing;
        _message = _root.Item.Bytes == 0 ? "No file sizes to display"
            : complete ? null : $"“{pending.Missing ?? "This folder"}” has no file sizes to show";
        _focusNode = focus;
        _reportedFocus = pending.Path.Count == 0 ? _root.Item.Id : pending.Path[^1];
        _userMoved = false;
        _camera = _target = Fit(focus);
        _container = ComputeContainer();
        _dimWorld = _container.Bounds;
        _dimAlpha = _container == _root ? 0 : DimLevel;
        _colorLevel = _container;
        _colorPrevious = null;
        _colorSince = double.NegativeInfinity;
        _wasZoomed = false; // StepFrame reports later changes; no events from inside OnRender
    }

    private void Rebuild()
    {
        if (_root is null || _children is null) return;
        var path = PathOf(_focusNode ?? _root);
        var missing = _missingName;
        var root = _root.Item;
        var fade = _sourceFadeAt;
        SetSource(root, _children, path, missing);
        _sourceFadeAt = fade; // a resize is not a new source; don't flash
    }

    // Squarified layout of one level, in the given world rectangle. Thread-safe and pure.
    private static Placement[] LayoutLevel(IReadOnlyList<DiskUsageItem> items, Rect bounds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bounds.Width <= 0 || bounds.Height <= 0) return [];
        var positive = items.Where(item => item.Bytes > 0).ToArray();
        if (!IsDescending(positive))
            positive = positive.OrderByDescending(item => item.Bytes).ThenBy(item => item.Id).ToArray();
        if (positive.Length == 0) return [];

        var entries = new List<(DiskUsageItem Item, IReadOnlyList<DiskUsageItem>? Members)>(Math.Min(positive.Length, DirectLimit + 1));
        if (positive.Length <= DirectLimit)
            foreach (var item in positive) entries.Add((item, null));
        else
        {
            for (var i = 0; i < NamedLimit; i++) entries.Add((positive[i], null));
            var tail = positive.Length - NamedLimit;
            var groups = Math.Clamp((tail + TailGroupSize - 1) / TailGroupSize, 1, MaxTailGroups);
            var chunk = (tail + groups - 1) / groups;
            for (var start = NamedLimit; start < positive.Length; start += chunk)
            {
                token.ThrowIfCancellationRequested();
                var count = Math.Min(chunk, positive.Length - start);
                if (count == 1) { entries.Add((positive[start], null)); continue; }
                var members = new ArraySegment<DiskUsageItem>(positive, start, count);
                long bytes = 0, files = 0;
                foreach (var member in members) { bytes += member.Bytes; files += member.FileCount; }
                entries.Add((new DiskUsageItem(-1 - entries.Count, $"{count:N0} smaller items", bytes, files, false), members));
            }
        }

        var weights = entries.Select(entry => entry.Item.Bytes).ToArray();
        var result = new Placement[entries.Count];
        var placed = 0;
        foreach (var tile in SquarifiedTreemap.Layout(weights, bounds.Width, bounds.Height))
        {
            var entry = entries[tile.ItemIndex];
            result[tile.ItemIndex] = new Placement(entry.Item,
                new Rect(bounds.X + tile.X, bounds.Y + tile.Y, Math.Max(0, tile.Width), Math.Max(0, tile.Height)), entry.Members);
            placed++;
        }
        return placed == result.Length ? result : result.Where(p => p.Item is not null).ToArray();
    }

    private static bool IsDescending(DiskUsageItem[] items)
    {
        for (var i = 1; i < items.Length; i++)
            if (items[i].Bytes > items[i - 1].Bytes) return false;
        return true;
    }

    private void Apply(Node node, Placement[] placements, bool fade)
    {
        ApplyTo(node, placements);
        node.ReadyAt = fade ? Now : double.NegativeInfinity;
    }

    // Thread-safe (touches only the node): used by Apply and by the background live rebuild.
    private static void ApplyTo(Node node, Placement[] placements)
    {
        node.Children = BuildChildren(node, placements);
        node.State = node.Children.Length > 0 ? NodeState.Ready : NodeState.Leaf;
        node.ReadyAt = double.NegativeInfinity;
    }

    private static Node[] BuildChildren(Node node, Placement[] placements)
    {
        var shareBase = FolderOf(node).Item.Bytes;
        var children = new Node[placements.Length];
        var firstGroup = placements.Length;
        for (var i = 0; i < placements.Length; i++)
        {
            children[i] = new Node(placements[i].Item, placements[i].Bounds, node, placements[i].Members, shareBase, i);
            if (firstGroup == placements.Length && children[i].IsGroup) firstGroup = i;
        }
        node.FirstGroup = firstGroup; // named children (largest first) come before the groups
        return children;
    }

    // ---------------------------------------------------------------- NEW: live refresh

    /// <summary>
    /// The index changed under the current folder. Rebuild the tree from the new snapshot in the
    /// background - re-opening every folder that is open on screen now - then swap it in with the
    /// camera untouched, so blocks simply resize instead of the map reloading and fading.
    /// </summary>
    internal void RefreshSource(DiskUsageItem root, Func<int, CancellationToken, IReadOnlyList<DiskUsageItem>> children,
        IReadOnlyList<int> path, string? missingName = null)
    {
        if (_root is null || _root.Item.Id != root.Id || ActualWidth < 1 || ActualHeight < 1)
        {
            SetSource(root, children, path, missingName);
            return;
        }
        // CHANGED (round 10): every folder that is open now is re-opened in the new tree (not only
        // the ones drawn last frame), so nothing collapses and fades back in: no flicker.
        var folders = new HashSet<int>(path);
        var groups = new HashSet<(int Folder, string Key)>();
        var pendingOpen = new Stack<Node>();
        pendingOpen.Push(_root);
        while (pendingOpen.TryPop(out var open) && folders.Count < 6000)
        {
            if (open.State != NodeState.Ready) continue;
            if (open.IsFolder) folders.Add(open.Item.Id);
            else if (open.IsGroup) groups.Add((FolderOf(open).Item.Id, GroupKey(open)));
            foreach (var child in open.Children)
                if (child.IsContainer && child.State == NodeState.Ready) pendingOpen.Push(child);
        }
        var aspect = _builtAspect;
        var pathIds = path.ToArray();
        _generation.Cancel();
        var generation = _generation = new CancellationTokenSource();
        _loads = 0;
        var token = generation.Token;
        Task.Run(() => BuildTree(root, children, aspect, folders, groups, token), token).ContinueWith(task =>
            Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(generation, _generation) || task.Status != TaskStatus.RanToCompletion) return;
                _children = children;
                _root = task.Result;
                var focus = ExpandPath(pathIds, out var complete);
                _missingName = complete ? null : missingName;
                _message = _root.Item.Bytes == 0 ? "No file sizes to display"
                    : complete ? null : $"“{missingName ?? "This folder"}” has no file sizes to show";
                _focusNode = focus;
                _reportedFocus = pathIds.Length == 0 ? _root.Item.Id : pathIds[^1];
                _flight = null;
                _candidate = null;
                _camera = Clamp(_camera);
                _target = Clamp(_target);
                _container = ComputeContainer();
                _colorLevel = _container;
                _colorPrevious = null;
                _colorSince = double.NegativeInfinity;
                _dimWorld = _container.Bounds;
                _hover = null;
                RequestFrame();
            }), TaskScheduler.Default);
    }

    // Identifies a "smaller items" group by its position under its folder (ids of groups repeat).
    private static string GroupKey(Node node)
    {
        var parts = new List<int>();
        for (var current = node; current.IsGroup && current.Parent is not null; current = current.Parent)
            parts.Add(current.Index);
        parts.Reverse();
        return string.Join('/', parts);
    }

    private static Node BuildTree(DiskUsageItem rootItem, Func<int, CancellationToken, IReadOnlyList<DiskUsageItem>> children,
        double aspect, HashSet<int> folders, HashSet<(int Folder, string Key)> groups, CancellationToken token)
    {
        var root = new Node(rootItem, new Rect(0, 0, aspect, 1), null, null, rootItem.Bytes, 0);
        var pending = new Stack<Node>();
        pending.Push(root);
        var budget = 6000; // folder loads; anything beyond loads lazily as usual (round 10: was 800)
        while (pending.TryPop(out var node))
        {
            token.ThrowIfCancellationRequested();
            if (node.State != NodeState.Collapsed) continue;
            IReadOnlyList<DiskUsageItem> items;
            if (node.IsGroup)
            {
                if (!groups.Contains((FolderOf(node).Item.Id, GroupKey(node))) &&
                    !node.Members!.Any(member => folders.Contains(member.Id))) continue;
                items = node.Members!;
            }
            else
            {
                if (node != root && !folders.Contains(node.Item.Id)) continue;
                if (budget-- <= 0) continue;
                items = children(node.Item.Id, token);
            }
            ApplyTo(node, LayoutLevel(items, node.Bounds, token));
            foreach (var child in node.Children)
                if (child.IsContainer) pending.Push(child);
        }
        return root;
    }

    private void ExpandSync(Node node)
    {
        if (node.State is not (NodeState.Collapsed or NodeState.Pending)) return;
        IReadOnlyList<DiskUsageItem> items;
        if (node.IsGroup) items = node.Members!;
        else if (_children is null) return;
        else
        {
            try { items = _children(node.Item.Id, _generation.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception) { node.State = NodeState.Leaf; return; }
        }
        Apply(node, LayoutLevel(items, node.Bounds, CancellationToken.None), fade: false);
    }

    // CHANGED (round 8): folders *and* grouped small items load here, off the UI thread, and the
    // child nodes are built there too; the UI thread only attaches the finished array.
    private void StartLoad(Node node)
    {
        if (_children is null && !node.IsGroup) return;
        node.State = NodeState.Pending;
        _loads++;
        var generation = _generation;
        var token = generation.Token;
        var provider = _children;
        Task.Run(() =>
        {
            var items = node.IsGroup ? node.Members! : provider!(node.Item.Id, token);
            return BuildChildren(node, LayoutLevel(items, node.Bounds, token));
        }, token).ContinueWith(task =>
            Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(generation, _generation)) return;
                _loads--;
                if (node.State == NodeState.Pending)
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        node.Children = task.Result;
                        node.State = task.Result.Length > 0 ? NodeState.Ready : NodeState.Leaf;
                        node.ReadyAt = Now;
                    }
                    else node.State = NodeState.Leaf; // unreadable: show it as a plain block
                }
                // CHANGED (round 8): arriving detail doesn't force a full redraw mid-zoom; it is
                // picked up by the next scheduled redraw (immediately when the camera is still).
                _needsFrame = true;
                if (_hooked) _overlayDirty = true;
                else RequestFrame();
            }), TaskScheduler.Default);
    }

    private void PumpLoads()
    {
        if (_wanted.Count == 0) return;
        _wanted.Sort((a, b) => b.Area.CompareTo(a.Area));
        foreach (var (node, _) in _wanted)
        {
            if (_loads >= MaxLoads) break;
            if (node.State == NodeState.Collapsed) StartLoad(node); // largest on screen first
        }
        _wanted.Clear();
    }

    private Node ExpandPath(IReadOnlyList<int> path, out bool complete)
    {
        var node = _root!;
        complete = true;
        foreach (var id in path)
        {
            var next = FindChild(node, id, expand: true);
            if (next is null) { complete = false; break; }
            node = next;
        }
        return node;
    }

    private Node? FindChild(Node parent, int id, bool expand)
    {
        if (expand) ExpandSync(parent);
        if (parent.State != NodeState.Ready) return null;
        foreach (var child in parent.Children)
            if (child.Item.Id == id) return child;
        foreach (var child in parent.Children)
            if (child.IsGroup && (expand || child.State == NodeState.Ready) && child.Members!.Any(member => member.Id == id))
                return FindChild(child, id, expand);
        return null;
    }

    private static Node? Find(Node node, int id)
    {
        if (node.Item.Id == id) return node;
        if (node.State != NodeState.Ready) return null;
        foreach (var child in node.Children)
            if (Find(child, id) is { } found) return found;
        return null;
    }

    private IReadOnlyList<int> PathOf(Node node)
    {
        var path = new List<int>();
        for (var current = node; current is not null && current != _root; current = current.Parent)
            if (current.IsFolder) path.Add(current.Item.Id);
        path.Reverse();
        return path;
    }

    private static Node FolderOf(Node node)
    {
        while (node.IsGroup && node.Parent is not null) node = node.Parent;
        return node;
    }

    private static bool IsAncestorOf(Node ancestor, Node? node)
    {
        for (var current = node?.Parent; current is not null; current = current.Parent)
            if (current == ancestor) return true;
        return false;
    }

    // ================================================================= camera

    private double Now => _clock.Elapsed.TotalSeconds + _timeOffset;
    private double ViewAspect => ActualHeight > 0 && ActualWidth > 0 ? ActualWidth / ActualHeight : _builtAspect;
    private static Point Center(Rect rect) => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    private Rect Fit(Node node) => node == _root ? Clamp(FitRaw(node.Bounds, 0)) : Clamp(FitRaw(node.Bounds, FolderMargin));

    private Rect FitRaw(Rect rect, double margin)
    {
        var aspect = ViewAspect;
        double width = rect.Width, height = rect.Height;
        if (height <= 0 || width / height > aspect) height = width / aspect;
        else width = height * aspect;
        width *= 1 + margin * 2;
        height *= 1 + margin * 2;
        return new Rect(rect.X + rect.Width / 2 - width / 2, rect.Y + rect.Height / 2 - height / 2, width, height);
    }

    // Keep the camera on the drive and at the view's aspect ratio.
    private Rect Clamp(Rect camera)
    {
        if (_root is null) return camera;
        var world = _root.Bounds;
        var full = FitRaw(world, 0);
        var width = Math.Clamp(camera.Width, full.Width * 1e-9, full.Width);
        var height = width / ViewAspect;
        var center = Center(camera);
        var x = width >= world.Width ? world.X + world.Width / 2 : Math.Clamp(center.X, world.Left + width / 2, world.Right - width / 2);
        var y = height >= world.Height ? world.Y + world.Height / 2 : Math.Clamp(center.Y, world.Top + height / 2, world.Bottom - height / 2);
        return new Rect(x - width / 2, y - height / 2, width, height);
    }

    private Rect ToScreen(Rect world)
    {
        var scaleX = ActualWidth / _camera.Width;
        var scaleY = ActualHeight / _camera.Height;
        return new Rect((world.X - _camera.X) * scaleX, (world.Y - _camera.Y) * scaleY, world.Width * scaleX, world.Height * scaleY);
    }

    // Zoom about the fixed point of the two views: that point stays still on screen while the
    // scale changes exponentially, so the destination grows straight out of where it is.
    private static Rect Interpolate(Rect from, Rect to, double t)
    {
        if (t <= 0) return from;
        if (t >= 1) return to;
        var (x, width) = Axis(from.X, from.Width, to.X, to.Width, t);
        var (y, height) = Axis(from.Y, from.Height, to.Y, to.Height, t);
        return new Rect(x, y, width, height);

        static (double Position, double Size) Axis(double a, double aSize, double b, double bSize, double amount)
        {
            var log = Math.Log(bSize / aSize);
            if (!double.IsFinite(log) || Math.Abs(log) < 1e-4)
                return (a + (b - a) * amount, aSize + (bSize - aSize) * amount);
            var scale = Math.Exp(log * amount);
            var fixedPoint = (a * bSize - b * aSize) / (bSize - aSize);
            return (fixedPoint - (fixedPoint - a) * scale, aSize * scale);
        }
    }

    private static bool Near(Rect a, Rect b)
        => Math.Abs(Math.Log(b.Width / a.Width)) < 2e-4 &&
           Math.Abs(a.X - b.X) < b.Width * 2e-4 && Math.Abs(a.Y - b.Y) < b.Height * 2e-4;

    private void FlyTo(Rect target)
    {
        _focusPoint = null;
        target = Clamp(target);
        var from = _camera;
        _target = target;
        if (Near(from, target)) { _camera = target; _flight = null; RequestCameraFrame(); return; }
        var points = new List<Rect> { from };
        // Neither view contains the other (e.g. Back to a different branch): rise to an overview
        // showing both, then descend, instead of sliding sideways at close range.
        if (!Encloses(from, target) && !Encloses(target, from))
        {
            var overview = Clamp(FitRaw(Rect.Union(from, target), 0));
            if (overview.Width > Math.Max(from.Width, target.Width) * 1.15) points.Add(overview);
        }
        points.Add(target);
        var durations = new double[points.Count - 1];
        for (var i = 0; i < durations.Length; i++) durations[i] = FlightDuration(points[i], points[i + 1]);
        _flight = new Flight(points.ToArray(), durations, Now);
        RequestCameraFrame();
    }

    private static bool Encloses(Rect outer, Rect inner)
        => Rect.Inflate(outer, outer.Width * .02, outer.Height * .02).Contains(inner);

    private static double FlightDuration(Rect from, Rect to)
    {
        var zoom = Math.Abs(Math.Log2(to.Width / from.Width));
        var a = Center(from);
        var b = Center(to);
        var pan = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) / Math.Max(from.Width, to.Width);
        // CHANGED (round 12): shorter flights (were .32-.68 s) - responsive, like a spring.
        return Math.Clamp(.24 + .04 * zoom + .14 * pan, .24, .46);
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    // NEW (round 12): normalized critically damped spring, 0 -> 1 with no overshoot.
    private static double Spring(double t)
    {
        const double k = 7.5;
        var end = 1 - (1 + k) * Math.Exp(-k);
        return (1 - (1 + k * t) * Math.Exp(-k * t)) / end;
    }

    // CSS "ease", cubic-bezier(.25, .1, .25, 1): a soft start, quick middle and a long glide in.
    // Used for single flights; it avoids the first-frame lurch of a plain ease-out.
    private static double Glide(double t)
    {
        const double x1 = .25, y1 = .1, x2 = .25, y2 = 1;
        var s = t;
        for (var i = 0; i < 8; i++)
        {
            var error = Bezier(s, x1, x2) - t;
            var slope = BezierSlope(s, x1, x2);
            if (Math.Abs(error) < 1e-7 || Math.Abs(slope) < 1e-7) break;
            s = Clamp01(s - error / slope);
        }
        return Bezier(s, y1, y2);

        static double Bezier(double u, double p1, double p2) => 3 * (1 - u) * (1 - u) * u * p1 + 3 * (1 - u) * u * u * p2 + u * u * u;
        static double BezierSlope(double u, double p1, double p2)
            => 3 * (1 - u) * (1 - u) * p1 + 6 * (1 - u) * u * (p2 - p1) + 3 * u * u * (1 - p2);
    }
    private static double EaseInOut(double t) => t < .5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
    private static double SmoothStep(double t) { t = Clamp01(t); return t * t * (3 - 2 * t); }

    private bool StepCamera(double now, double dt)
    {
        if (_flight is { } flight)
        {
            var elapsed = now - flight.Start;
            var segment = 0;
            while (segment < flight.Durations.Length - 1 && elapsed >= flight.Durations[segment])
            {
                elapsed -= flight.Durations[segment];
                segment++;
            }
            var t = Clamp01(elapsed / flight.Durations[segment]);
            // CHANGED (round 12): start at full speed and settle smoothly (critically damped spring
            // shape) instead of easing in, so a click moves the camera on the very next frame.
            var eased = flight.Durations.Length == 1 ? Spring(t) : EaseInOut(t);
            _camera = Interpolate(flight.Points[segment], flight.Points[segment + 1], eased);
            if (segment == flight.Durations.Length - 1 && t >= 1)
            {
                _camera = _target = flight.Points[^1];
                _flight = null;
            }
            return true;
        }
        if (Near(_camera, _target)) { _camera = _target; return false; }
        _camera = Interpolate(_camera, _target, 1 - Math.Exp(-dt / WheelSmoothing));
        return true;
    }

    // Deepest folder or group the user is "inside". A child is entered only once the camera
    // is zoomed in past its parent and the child nearly fills the view around the point being
    // zoomed toward (the pointer while wheeling, otherwise the view's center). Folders the user
    // explicitly opened stay entered until the camera pulls well back out of them.
    private Node ComputeContainer()
    {
        var node = _root!;
        var point = _focusPoint ?? Center(_camera);
        while (node.State == NodeState.Ready)
        {
            Node? child = null;
            foreach (var candidate in node.Children)
                if (candidate.IsContainer && candidate.Bounds.Contains(point)) { child = candidate; break; }
            if (child is null) break;
            var opened = child == _focusNode || IsAncestorOf(child, _focusNode) ||
                         (child.IsGroup && (child == _container || IsAncestorOf(child, _container)));
            var fit = Fit(child).Width;
            var enter = opened
                ? _camera.Width <= fit * 1.25
                : _camera.Width <= fit * 1.12 && _camera.Width < Fit(node).Width * .9;
            if (!enter) break;
            node = child;
        }
        return node;
    }

    private bool StepFrame(double dt)
    {
        if (_root is null) return false;
        var now = Now;
        var moving = StepCamera(now, dt);
        _container = ComputeContainer();
        // Moving into or out of a level re-colors and re-labels it; cross-fade both.
        if (_container != _colorLevel)
        {
            _colorPrevious = _colorLevel;
            _colorLevel = _container;
            _colorSince = now;
        }
        var recoloring = now - _colorSince < ColorFade;

        // The veil over the rest of the drive glides to the new folder in world space.
        var dimTarget = _container == _root ? 0 : DimLevel;
        var blend = 1 - Math.Exp(-dt / .085);
        _dimAlpha += (dimTarget - _dimAlpha) * blend;
        _dimWorld = Interpolate(_dimWorld, _container.Bounds, blend);
        var settling = Math.Abs(_dimAlpha - dimTarget) > .004 || !Near(_dimWorld, _container.Bounds);
        if (!settling) { _dimAlpha = dimTarget; _dimWorld = _container.Bounds; }

        var reporting = UpdateReporting(now);
        var zoomed = IsZoomed;
        if (zoomed != _wasZoomed) { _wasZoomed = zoomed; ZoomChanged?.Invoke(); }
        return moving || settling || reporting || recoloring;
    }

    private bool UpdateReporting(double now)
    {
        if (!_userMoved || _flight is not null || _container is null) { _candidate = null; return false; }
        var folder = FolderOf(_container);
        if (folder.Item.Id == _reportedFocus) { _candidate = null; return false; }
        if (_candidate != folder) { _candidate = folder; _candidateSince = now; return true; }
        if (now - _candidateSince < FocusDebounce) return true;
        Report(folder);
        return false;
    }

    private void Report(Node folder)
    {
        _candidate = null;
        _message = null;
        if (folder.Item.Id == _reportedFocus) return;
        _reportedFocus = folder.Item.Id;
        _focusNode = folder;
        FolderFocused?.Invoke(folder.Item.Id);
    }

    // ================================================================= frame loop

    private void RequestFrame()
    {
        _sceneDirty = _overlayDirty = true;
        if (_hooked) return;
        if (!IsLoaded) { InvalidateVisual(); return; }
        HookFrames();
    }

    // NEW (round 5): the camera moved; frames will decide whether to redraw or just transform.
    private void RequestCameraFrame()
    {
        _overlayDirty = true;
        if (_hooked) return;
        if (!IsLoaded) { _sceneDirty = true; InvalidateVisual(); return; }
        HookFrames();
    }

    // Start the frame loop without forcing a scene redraw.
    private void HookFrames()
    {
        if (_hooked || !IsLoaded) return;
        _hooked = true;
        _lastFrame = Now;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopFrames()
    {
        if (!_hooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _hooked = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = Now;
        var dt = Math.Clamp(now - _lastFrame, 0, .1);
        if (_showStats)
        {
            var frameMs = (now - _lastFrame) * 1000;
            Smooth(ref _statFrame, frameMs);
            _statWorstFrame = Math.Max(frameMs, _statWorstFrame * .985); // NEW (round 15): recent worst, decays over ~1-2 s
        }
        _lastFrame = now;
        var busy = StepFrame(dt);
        var moving = _flight is not null || _dragging || !Near(_camera, _target);
        // CHANGED (round 9): full-detail frames are cheap now; redraw whenever anything changed.
        // CHANGED (round 11): low-resolution frames while moving, then one full-resolution frame.
        if (busy || moving || _sceneDirty || _needsFrame || !Near(_sceneCamera, _camera) || _shown == _motion)
        {
            _renderMotion = false; // REVERTED (round 13): no low-resolution motion frames
            try { RenderScene(); }
            finally { _renderMotion = false; }
        }
        if (busy || moving || _overlayDirty || _overlayAnimating) RenderOverlay();
        if (!busy && !moving && !_needsFrame && !_sceneDirty && !_overlayDirty && !_overlayAnimating && _shown != _motion)
            StopFrames();
    }

    // Scene coordinates (drawn with _sceneCamera) to current screen coordinates.
    private (double Scale, double OffsetX, double OffsetY) SceneMapping()
    {
        if (_camera.Width <= 0 || ActualWidth < 1) return (1, 0, 0);
        var scale = _sceneCamera.Width / _camera.Width;
        return (scale, (_sceneCamera.X - _camera.X) * ActualWidth / _camera.Width,
            (_sceneCamera.Y - _camera.Y) * ActualHeight / _camera.Height);
    }

    private Point ScreenToScene(Point point)
    {
        var (scale, offsetX, offsetY) = SceneMapping();
        return new Point((point.X - offsetX) / scale, (point.Y - offsetY) / scale);
    }

    private Rect SceneToScreen(Rect rect)
    {
        var (scale, offsetX, offsetY) = SceneMapping();
        return new Rect(rect.X * scale + offsetX, rect.Y * scale + offsetY, rect.Width * scale, rect.Height * scale);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (ActualWidth < 1 || ActualHeight < 1) return;
        UpdateClip();
        _gpuShown = false; // NEW (round 14): re-place the GPU image at the new size
        _shown = null;
        if (_root is null) { EnsureBuilt(); RequestFrame(); return; }
        _flight = null;
        _camera = _target = Clamp(_camera);
        if (Math.Abs(ActualWidth / ActualHeight / _builtAspect - 1) > .08) { _rebuild.Stop(); _rebuild.Start(); }
        RequestFrame();
        UpdateClip();
    }

    // Layout-driven renders (first show, resizes, tests) come through here; animation frames
    // render the two layers directly from OnRendering without involving layout.
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        // CHANGED (round 5): the background lives here, untransformed, under the scene layer.
        drawingContext.DrawRectangle(_base, null, new Rect(0, 0, ActualWidth, ActualHeight));
        RenderScene();
        RenderOverlay();
    }

    // NEW (round 5): rounded corners are clipped on the element, not inside the moving scene.
    private void UpdateClip()
    {
        var clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 10, 10);
        clip.Freeze();
        Clip = clip;
    }

    // ================================================================= scene layer

    private void RenderScene()
    {
        var sceneStart = _statClock.Elapsed.TotalMilliseconds;
        if (!_statClock.IsRunning) _statClock.Start();
        _sceneDirty = false;
        _needsFrame = false;
        _view = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));
        _viewLoose = Rect.Inflate(_view, 2, 2);
        _sceneCamera = _camera;
        _sceneTransform.Matrix = Matrix.Identity;
        _framesSinceScene = 0;
        _drawn.Clear();
        _wanted.Clear();
        _deferred.Clear();
        _commandCount = 0;
        _tiles = 0;
        _textBudget = TextPerFrame;
        if (ActualWidth < 1 || ActualHeight < 1) return;
        EnsureBuilt();
        using (var dc = _labels.RenderOpen())
        {
            if (_root is not null)
            {
                _container ??= ComputeContainer();
                _colorLevel ??= _container;
                _openPath.Clear();
                for (var node = _container; node is not null; node = node.Parent) _openPath.Add(node);
                _colorT = EaseOut(Clamp01((Now - _colorSince) / ColorFade));
                if (_colorT < 1) _needsFrame = true;
                var reveal = EaseOut(Clamp01((Now - _sourceFadeAt) / .24));
                if (reveal < 1) _needsFrame = true;
                var walkStart = _statClock.Elapsed.TotalMilliseconds;
                if (_root.Item.Bytes > 0)
                    DrawNode(_root, ToScreen(_root.Bounds), reveal, 1, Rect.Empty, TileBudget);
                var labelsStart = _statClock.Elapsed.TotalMilliseconds;
                if (_showStats)
                {
                    Smooth(ref _statWalk, labelsStart - walkStart);
                    _statWorstWalk = Math.Max(labelsStart - walkStart, _statWorstWalk * .985);
                }
                foreach (var draw in _deferred) draw(dc);
                if (_showStats) Smooth(ref _statLabels, _statClock.Elapsed.TotalMilliseconds - labelsStart);
                DrawVeil(dc);
            }
        }
        // CHANGED (round 14): draw on the GPU when possible, otherwise rasterize on the CPU.
        if (!PresentGpu()) Present(EnsureSurface(_full, MaxPixels));
        PumpLoads();
        if (_showStats)
        {
            Smooth(ref _statScene, _statClock.Elapsed.TotalMilliseconds - sceneStart);
            _statTiles = _commandCount;
        }
        if (_needsFrame && !_hooked) Dispatcher.InvokeAsync(RequestFrame, DispatcherPriority.Background);
    }

    // alpha: accumulated fade of this tile. labelWeight: 1 when this tile's parent is the level
    // being labeled (0..1 while cross-fading between levels). zone: an ancestor's name tag.
    // CHANGED (round 7): `budget` is how many tiles this subtree may draw. Children share their
    // parent's budget in proportion to their on-screen area (unused budget passes to later
    // siblings), so detail is spread evenly across the view instead of the first big folders
    // spending it all. A block with no budget left is drawn solid, so there are never holes.
    // Returns the number of tiles used.
    private int DrawNode(Node node, Rect screen, double alpha, double labelWeight, Rect zone, int budget)
    {
        if (!screen.IntersectsWith(_viewLoose)) return 0;
        var min = Math.Min(screen.Width, screen.Height);
        var isRoot = node == _root;
        if (!isRoot && min < MinTile) return 0;
        var overBudget = !isRoot && budget <= 1;
        _tiles++;
        // CHANGED (round 8): gaps shrink with tile size so dense detail reads as texture, not grid.
        var tile = isRoot ? screen : Deflate(screen, min >= 14 ? .75 : min >= 7 ? .45 : min >= 3 ? .25 : .12);
        var drawn = Rect.Intersect(tile, _viewLoose);
        if (drawn.IsEmpty || drawn.Width <= 0 || drawn.Height <= 0) return 0;
        var visible = Rect.Intersect(tile, _view);
        var hasVisible = !visible.IsEmpty && visible.Width > 1 && visible.Height > 1;
        _drawn.Add((node, drawn));
        if (node.State == NodeState.Collapsed && min >= PrefetchSize) _wanted.Add((node, drawn.Width * drawn.Height));

        var color = isRoot ? _baseColor : NodeColor(node);
        var open = overBudget ? 0 : OpenAmount(node, min);
        // An open folder becomes a frame of its own hue behind its contents: dark for the labeled
        // blocks (structure you read), barely darker deeper down (texture you zoom into), so
        // children too small to draw blend into it instead of showing as dark gaps.
        var shallow = _colorLevel is null || node.Depth - _colorLevel.Depth <= 1;
        var fill = open > 0 && !isRoot ? Mix(color, Shade(color, shallow ? .42 : .8), open) : color;
        Emit(drawn, fill, isRoot ? 1 : alpha); // CHANGED (round 9): a rectangle for the rasterizer
        // Collapsed label (fades out as the folder opens). Deferred, so drawing order is unaffected.
        if (!isRoot && open < 1 && labelWeight > 0 && hasVisible)
        {
            var labelAlpha = alpha * labelWeight * (1 - open);
            _deferred.Add(context => DrawLabel(context, node, visible, zone, labelAlpha));
        }

        if (open > 0)
        {
            var pad = isRoot ? 0 : (shallow ? Math.Clamp(min * .014, 1, 4) : Math.Clamp(min * .006, 0, 1.2)) * open;
            var inner = Deflate(tile, pad);
            // FIXED: every labeled open folder gets its name tag, even one spanning the whole view
            // height; only the folder you're inside (or its ancestors) goes without one.
            var pill = !isRoot && labelWeight > 0 && hasVisible && !_openPath.Contains(node)
                ? LayoutPill(node, visible, zone, color, alpha * labelWeight * open) : null;
            var childZone = pill is { Alpha: > .05 } shownPill ? shownPill.Rect : zone;
            var childLabels = LabelWeight(node);
            var childAlpha = alpha * open;
            var children = node.Children;
            // Pooled: with fractal depth many folders are open in every frame.
            var screens = ArrayPool<Rect>.Shared.Rent(children.Length);
            var areas = ArrayPool<double>.Shared.Rent(children.Length);
            var visited = ArrayPool<int>.Shared.Rent(children.Length);
            var visitedCount = 0;
            var areaLeft = 0d;
            // CHANGED (round 15): skip children too small to draw *before* computing anything for
            // them. Named children are sorted largest first, so the first one under the size limit
            // means the rest of the named ones are too - jump straight to the grouped blocks. This
            // used to compute a rectangle for every child of every open folder, every frame.
            var pixelsPerWorld = inner.Width * inner.Height / Math.Max(node.Area, double.Epsilon);
            var tooSmall = MinTile * MinTile;
            for (var i = 0; i < children.Length; i++)
            {
                var child = children[i];
                if (child.Area * pixelsPerWorld < tooSmall)
                {
                    if (i < node.FirstGroup) i = node.FirstGroup - 1; // skip the remaining named children
                    continue;
                }
                var rect = Within(inner, node.Bounds, child.Bounds);
                if (!rect.IntersectsWith(_viewLoose)) continue;
                var shown = Rect.Intersect(rect, _viewLoose);
                screens[visitedCount] = rect;
                areas[visitedCount] = shown.Width * shown.Height;
                visited[visitedCount++] = i;
                areaLeft += shown.Width * shown.Height;
            }
            var remaining = budget - 1;
            var used = 1;
            for (var k = 0; k < visitedCount; k++)
            {
                if (areas[k] <= 0) continue;
                var share = areaLeft <= 0 ? 0 : (int)Math.Round(remaining * areas[k] / areaLeft);
                var spent = DrawNode(children[visited[k]], screens[k], childAlpha, childLabels, childZone, Math.Max(1, share));
                remaining -= spent;
                used += spent;
                areaLeft -= areas[k];
            }
            ArrayPool<Rect>.Shared.Return(screens);
            ArrayPool<double>.Shared.Return(areas);
            ArrayPool<int>.Shared.Return(visited);
            if (pill is { } tag) _deferred.Add(context => DrawPill(context, tag));
            return used;
        }
        return 1;
    }

    // Maps a child's world rectangle into its parent's on-screen content rectangle.
    private static Rect Within(Rect inner, Rect parentWorld, Rect childWorld)
    {
        var scaleX = inner.Width / parentWorld.Width;
        var scaleY = inner.Height / parentWorld.Height;
        return new Rect(inner.X + (childWorld.X - parentWorld.X) * scaleX, inner.Y + (childWorld.Y - parentWorld.Y) * scaleY,
            childWorld.Width * scaleX, childWorld.Height * scaleY);
    }

    private double OpenAmount(Node node, double min)
    {
        if (node.State != NodeState.Ready) return 0;
        var zoom = _openPath.Contains(node) ? 1 : SmoothStep((min - ExpandStart) / (ExpandFull - ExpandStart));
        if (zoom <= 0) return 0;
        var fade = Clamp01((Now - node.ReadyAt) / .22);
        if (fade < 1) _needsFrame = true;
        return zoom * EaseOut(fade);
    }

    private static Rect Deflate(Rect rect, double by)
        => new(rect.X + by, rect.Y + by, Math.Max(0, rect.Width - by * 2), Math.Max(0, rect.Height - by * 2));

    private void Fill(DrawingContext dc, Color color, double alpha, Rect rect, double radius)
    {
        var brush = BrushFor(color, alpha);
        if (brush is null) return;
        if (radius > 0) dc.DrawRoundedRectangle(brush, null, rect, radius, radius);
        else dc.DrawRectangle(brush, null, rect);
    }

    // ---------------------------------------------------------------- NEW (round 9): rasterizer

    private void Emit(Rect rect, Color color, double alpha)
    {
        var a = (int)Math.Round(Clamp01(alpha) * 255);
        if (a <= 0 || rect.Width <= 0 || rect.Height <= 0) return;
        if (_commandCount == _commands.Length) Array.Resize(ref _commands, _commands.Length * 2);
        _commands[_commandCount++] = new TileCommand((float)rect.Left, (float)rect.Top, (float)rect.Right, (float)rect.Bottom,
            (uint)color.R << 16 | (uint)color.G << 8 | color.B, (byte)a);
    }

    // A bitmap in device pixels (at most maxPixels, scaled up by the GPU beyond that).
    private Surface EnsureSurface(Surface surface, double maxPixels)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = Math.Min(1, Math.Sqrt(maxPixels / Math.Max(1, ActualWidth * dpi.DpiScaleX * ActualHeight * dpi.DpiScaleY)));
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX * scale));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY * scale));
        if (surface.Bitmap is null || surface.Bitmap.PixelWidth != width || surface.Bitmap.PixelHeight != height)
        {
            surface.Bitmap = new WriteableBitmap(width, height, 96 * dpi.DpiScaleX * scale, 96 * dpi.DpiScaleY * scale,
                PixelFormats.Bgr32, null);
            surface.ScaleX = width / ActualWidth;
            surface.ScaleY = height / ActualHeight;
            if (_shown == surface) _shown = null; // re-attach below
        }
        return surface;
    }

    // CHANGED (round 13): rasterize directly into the WriteableBitmap's back buffer (locked on the
    // UI thread while worker threads fill their row bands), instead of a managed array + WritePixels.
    // NEW (round 14): Direct3D path. Only used while the control is in a live window (not for
    // off-screen captures such as tests, where D3DImage content isn't rendered).
    private bool PresentGpu()
    {
        if (!IsLoaded || PresentationSource.FromVisual(this) is null) return false;
        if (!_gpuTried)
        {
            _gpuTried = true;
            _gpu = GpuTileRenderer.TryCreate();
        }
        if (_gpu is not { IsAvailable: true } gpu) return false;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        var background = (uint)_baseColor.R << 16 | (uint)_baseColor.G << 8 | _baseColor.B;
        var start = _statClock.Elapsed.TotalMilliseconds;
        if (!gpu.Render(_commands.AsSpan(0, _commandCount), width, height, width / ActualWidth, height / ActualHeight, background))
            return false; // falls back to the CPU rasterizer (permanently if the device failed)
        if (!_gpuShown)
        {
            using var dc = _scene.RenderOpen();
            dc.DrawImage(gpu.Image, new Rect(0, 0, ActualWidth, ActualHeight));
            _gpuShown = true;
            _shown = null;
        }
        if (_showStats)
        {
            Smooth(ref _statRaster, _statClock.Elapsed.TotalMilliseconds - start);
            Smooth(ref _statUpload, 0);
            _statPixelsW = width;
            _statPixelsH = height;
        }
        return true;
    }

    private unsafe void Present(Surface surface)
    {
        var bitmap = surface.Bitmap;
        if (bitmap is null) return;
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        var commands = _commands;
        var count = _commandCount;
        var background = (uint)_baseColor.R << 16 | (uint)_baseColor.G << 8 | _baseColor.B;
        var scaleX = surface.ScaleX;
        var scaleY = surface.ScaleY;
        // Bands of rows are independent: each core fills its own rows, in command order
        // (parents before children), so no locking is needed.
        var bands = Math.Clamp(Environment.ProcessorCount, 1, 8);
        var bandHeight = (height + bands - 1) / bands;
        var rasterStart = _statClock.Elapsed.TotalMilliseconds;
        var uploadStart = rasterStart;
        bitmap.Lock();
        try
        {
            var buffer = bitmap.BackBuffer;
            var stride = bitmap.BackBufferStride / 4;
            Parallel.For(0, bands, band =>
            {
                var top = band * bandHeight;
                var bottom = Math.Min(height, top + bandHeight);
                if (top >= bottom) return;
                var pixels = new Span<uint>((void*)buffer, stride * height);
                for (var row = top; row < bottom; row++) pixels.Slice(row * stride, width).Fill(background);
                for (var i = 0; i < count; i++)
                    FillRect(pixels, stride, width, top, bottom, in commands[i], scaleX, scaleY);
            });
            uploadStart = _statClock.Elapsed.TotalMilliseconds;
            bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            bitmap.Unlock();
        }
        if (_showStats)
        {
            Smooth(ref _statRaster, uploadStart - rasterStart);
            Smooth(ref _statUpload, _statClock.Elapsed.TotalMilliseconds - uploadStart);
            _statPixelsW = width;
            _statPixelsH = height;
        }
        if (_shown != surface)
        {
            // Switch which buffer the scene layer shows (the GPU scales it to the element's size).
            using var dc = _scene.RenderOpen();
            dc.DrawImage(bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
            _shown = surface;
            _gpuShown = false;
        }
    }

    // Fills one rectangle within rows [top, bottom), with fractional coverage on its edges.
    private static void FillRect(Span<uint> pixels, int stride, int width, int top, int bottom, in TileCommand command, double scaleX, double scaleY)
    {
        var x0 = Math.Max(0, command.X0 * scaleX);
        var x1 = Math.Min(width, command.X1 * scaleX);
        var y0 = Math.Max(top, command.Y0 * scaleY);
        var y1 = Math.Min(bottom, command.Y1 * scaleY);
        if (x1 <= x0 || y1 <= y0) return;
        var color = command.Color;
        var alpha = command.Alpha / 255d;
        var columnFirst = (int)x0;
        var columnLast = Math.Min(width - 1, (int)Math.Ceiling(x1) - 1);
        var leftCoverage = columnFirst == columnLast ? x1 - x0 : columnFirst + 1 - x0;
        var rightCoverage = columnFirst == columnLast ? 0 : x1 - columnLast;
        var innerFirst = columnFirst + 1;
        var innerLast = columnLast - 1; // inclusive; may be < innerFirst
        var rowFirst = (int)y0;
        var rowLast = Math.Min(bottom - 1, (int)Math.Ceiling(y1) - 1);
        for (var row = rowFirst; row <= rowLast; row++)
        {
            var rowCoverage = Math.Min(row + 1, y1) - Math.Max(row, y0);
            if (rowCoverage <= 0) continue;
            var weight = rowCoverage * alpha;
            var line = row * stride;
            if (innerLast >= innerFirst)
            {
                var span = pixels.Slice(line + innerFirst, innerLast - innerFirst + 1);
                if (weight >= .996) span.Fill(color);
                else Blend(span, color, weight);
            }
            BlendPixel(ref pixels[line + columnFirst], color, weight * leftCoverage);
            if (columnLast != columnFirst) BlendPixel(ref pixels[line + columnLast], color, weight * rightCoverage);
        }
    }

    private static void Blend(Span<uint> span, uint color, double weight)
    {
        var w = (int)(weight * 256);
        if (w <= 0) return;
        var inverse = 256 - w;
        uint sr = (color >> 16) & 0xFF, sg = (color >> 8) & 0xFF, sb = color & 0xFF;
        for (var i = 0; i < span.Length; i++)
        {
            var d = span[i];
            var r = (((d >> 16) & 0xFF) * (uint)inverse + sr * (uint)w) >> 8;
            var g = (((d >> 8) & 0xFF) * (uint)inverse + sg * (uint)w) >> 8;
            var b = ((d & 0xFF) * (uint)inverse + sb * (uint)w) >> 8;
            span[i] = r << 16 | g << 8 | b;
        }
    }

    private static void BlendPixel(ref uint pixel, uint color, double weight)
    {
        if (weight <= .004) return;
        if (weight >= .996) { pixel = color; return; }
        Blend(new Span<uint>(ref pixel), color, weight);
    }

    // Frozen brushes keyed by color with alpha quantized to 32 steps: fades cost no layers.
    private SolidColorBrush? BrushFor(Color color, double alpha)
    {
        var a = (int)Math.Round(Clamp01(alpha) * color.A / 255d * 31) * 255 / 31;
        if (a <= 0) return null;
        var key = (uint)a << 24 | (uint)color.R << 16 | (uint)color.G << 8 | color.B;
        if (!_brushes.TryGetValue(key, out var brush))
        {
            if (_brushes.Count > 8192) _brushes.Clear();
            brush = Frozen(Color.FromArgb((byte)a, color.R, color.G, color.B));
            _brushes[key] = brush;
        }
        return brush;
    }

    // ---------------------------------------------------------------- colors and labels

    // CHANGED (round 13): O(1) - a cached set of each level's ancestors, instead of walking up the
    // tree for every tile (that walk was part of the hitch when entering a folder).
    private readonly Dictionary<Node, HashSet<Node>> _pathSets = [];
    private bool OnPath(Node node, Node? level)
    {
        if (level is null) return false;
        if (!_pathSets.TryGetValue(level, out var set))
        {
            if (_pathSets.Count > 8) _pathSets.Clear();
            set = [];
            for (var current = level; current is not null; current = current.Parent) set.Add(current);
            _pathSets[level] = set;
        }
        return set.Contains(node);
    }

    // Fades 0..1 as a parent's children gain or lose their labels when the level changes.
    private double LabelWeight(Node parent)
    {
        var current = OnPath(parent, _colorLevel) ? 1d : 0d;
        if (_colorT >= 1 || _colorPrevious is null) return current;
        var previous = OnPath(parent, _colorPrevious) ? 1d : 0d;
        return previous + (current - previous) * _colorT;
    }

    private Color NodeColor(Node node)
    {
        var current = ColorUnder(node, _colorLevel!);
        if (_colorT >= 1 || _colorPrevious is null) return current;
        return Mix(ColorUnder(node, _colorPrevious), current, _colorT);
    }

    private Color ColorUnder(Node node, Node level)
    {
        if (node.ColorKeyA == level) return node.ColorA;
        if (node.ColorKeyB == level) return node.ColorB;
        var color = ComputeColor(node, level);
        var keepA = node.ColorKeyA is not null && (node.ColorKeyA == _colorLevel || node.ColorKeyA == _colorPrevious);
        if (keepA) { node.ColorKeyB = level; node.ColorB = color; }
        else { node.ColorKeyA = level; node.ColorA = color; }
        return color;
    }

    // Each item directly inside the level (the "anchor") has its own hue; everything inside it
    // shares that hue, a little darker per level and varied between siblings.
    private Color ComputeColor(Node node, Node level)
    {
        var anchor = node;
        var depth = 0;
        while (anchor.Parent is not null && !OnPath(anchor.Parent, level)) { anchor = anchor.Parent; depth++; }
        if (anchor.Parent is null) return _baseColor;
        var hue = anchor.IsGroup ? GroupColor : BranchColors[anchor.Index % BranchColors.Length];
        if (depth == 0) return hue;
        return Shade(hue, 1 - .07 * Math.Min(depth, 4) - .05 * (node.Index % 3));
    }

    private static Color Shade(Color color, double factor) => Color.FromArgb(color.A,
        (byte)Math.Clamp(color.R * factor, 0, 255), (byte)Math.Clamp(color.G * factor, 0, 255), (byte)Math.Clamp(color.B * factor, 0, 255));

    private static Color Mix(Color from, Color to, double t) => Color.FromArgb(
        (byte)(from.A + (to.A - from.A) * t), (byte)(from.R + (to.R - from.R) * t),
        (byte)(from.G + (to.G - from.G) * t), (byte)(from.B + (to.B - from.B) * t));

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    // Name tag for an open folder at the labeled level (CHANGED: no longer hidden just because
    // the folder spans the view in one direction).
    private Pill? LayoutPill(Node node, Rect visible, Rect zone, Color color, double alpha)
    {
        alpha *= Clamp01((Math.Min(visible.Width, visible.Height) - 36) / 20);
        if (alpha <= .02) return null;
        var name = Text(ref node.PillName, node.Item.Name, 12, true);
        var size = Text(ref node.PillSize, node.Item.SizeText, 11, false);
        if (name is null || size is null) return null;
        const double padX = 7, padY = 3, spacing = 7;
        var sizeWidth = size.WidthIncludingTrailingWhitespace;
        var nameRoom = visible.Width - 10 - padX * 2 - spacing - sizeWidth;
        if (nameRoom < 28) return null;
        Trim(ref node.PillName, nameRoom);
        var height = Math.Max(name.Height, size.Height) + padY * 2;
        var width = padX * 2 + name.Width + spacing + sizeWidth;
        var x = visible.X + 5;
        var y = visible.Y + 5;
        if (!zone.IsEmpty && x < zone.Right && y < zone.Bottom + 3) y = zone.Bottom + 3;
        if (y + height > visible.Bottom - 5) return null;
        return new Pill(node, new Rect(x, y, width, height), name, size, Shade(color, .3), alpha);
    }

    private void DrawPill(DrawingContext dc, Pill pill)
    {
        Fill(dc, pill.Fill, pill.Alpha * .94, pill.Rect, 5);
        Tint(ref pill.Node.PillName, BrushFor(White, pill.Alpha) ?? Brushes.Transparent);
        Tint(ref pill.Node.PillSize, BrushFor(White, pill.Alpha * .7) ?? Brushes.Transparent);
        dc.DrawText(pill.Name, new Point(pill.Rect.X + 7, pill.Rect.Y + (pill.Rect.Height - pill.Name.Height) / 2));
        dc.DrawText(pill.Size, new Point(pill.Rect.Right - 7 - pill.Size.WidthIncludingTrailingWhitespace,
            pill.Rect.Y + (pill.Rect.Height - pill.Size.Height) / 2));
    }

    // Name and size in the top-left of a tile, in the part that's on screen, so labels of huge
    // tiles stay readable while zooming. Fades in as room appears.
    private void DrawLabel(DrawingContext dc, Node node, Rect visible, Rect zone, double alpha)
    {
        alpha *= Clamp01((visible.Width - 44) / 18) * Clamp01((visible.Height - 20) / 8);
        if (alpha <= .02) return;
        var big = visible.Width >= 160 && visible.Height >= 90;
        var twoLines = visible.Height >= 40 && visible.Width >= 60;
        var pad = big ? 9d : 6d;
        var room = Math.Max(1, visible.Width - pad * 2);
        var name = Text(ref node.LabelName, node.Item.Name, big ? 13.5 : 12, true);
        if (name is null) return;
        Trim(ref node.LabelName, room);
        var detail = twoLines ? Text(ref node.LabelDetail, $"{node.Item.SizeText} · {node.ShareText}", big ? 12 : 11, false) : null;
        if (twoLines && detail is null) return;
        if (detail is not null) Trim(ref node.LabelDetail, room);
        var x = visible.X + pad;
        var y = visible.Y + pad - 2;
        if (!zone.IsEmpty && x < zone.Right && y < zone.Bottom + 2) y = zone.Bottom + 3;
        var height = name.Height + (detail?.Height ?? 0);
        if (y + height > visible.Bottom - 2) return;
        Tint(ref node.LabelName, BrushFor(White, alpha) ?? Brushes.Transparent);
        dc.DrawText(name, new Point(x, y));
        if (detail is null) return;
        Tint(ref node.LabelDetail, BrushFor(White, alpha * .72) ?? Brushes.Transparent);
        dc.DrawText(detail, new Point(x, y + name.Height));
    }

    private void DrawVeil(DrawingContext dc)
    {
        var veil = BrushFor(_baseColor, _dimAlpha);
        if (veil is null) return;
        var focus = ToScreen(_dimWorld);
        var area = _viewLoose; // CHANGED (round 5): veil the pre-drawn margin too
        var inner = Rect.Intersect(focus, area);
        if (inner.IsEmpty) { dc.DrawRectangle(veil, null, area); return; }
        dc.DrawRectangle(veil, null, new Rect(area.Left, area.Top, area.Width, Math.Max(0, inner.Top - area.Top)));
        dc.DrawRectangle(veil, null, new Rect(area.Left, inner.Bottom, area.Width, Math.Max(0, area.Bottom - inner.Bottom)));
        dc.DrawRectangle(veil, null, new Rect(area.Left, inner.Top, Math.Max(0, inner.Left - area.Left), inner.Height));
        dc.DrawRectangle(veil, null, new Rect(inner.Right, inner.Top, Math.Max(0, area.Right - inner.Right), inner.Height));
        if (_dimAlpha > DimLevel * .4) dc.DrawRoundedRectangle(null, FolderEdge, Deflate(focus, -.5), 4, 4);
    }

    private void DrawMessage(DrawingContext dc, string message)
    {
        var text = Make(message, 14, false);
        text.SetForegroundBrush(Ink);
        text.MaxTextWidth = Math.Max(1, ActualWidth - 80);
        var card = new Rect(ActualWidth / 2 - text.Width / 2 - 18, ActualHeight / 2 - text.Height / 2 - 12,
            text.Width + 36, text.Height + 24);
        dc.DrawRoundedRectangle(CardFill, CardEdge, card, 8, 8);
        dc.DrawText(text, new Point(card.X + 18, card.Y + 12));
    }

    // ================================================================= overlay layer

    private void RenderOverlay()
    {
        _overlayDirty = false;
        _overlayAnimating = false;
        try { RenderOverlayCore(); }
        finally { if (_overlayAnimating && !_hooked) HookFrames(); }
    }

    private void RenderOverlayCore()
    {
        using var dc = _overlay.RenderOpen();
        if (ActualWidth < 1 || ActualHeight < 1) return;
        if (_message is not null) DrawMessage(dc, _message); // MOVED (round 5): stays put while zooming
        if (_root is null) return;
        if (_highlight is int id && _focusNode is not null && FindChild(_focusNode, id, expand: false) is { } selected)
            Outline(dc, selected, AccentEdge);
        _hover = _mouseInside && !_dragging ? NodeAt(_mouse) : null;
        var target = _hover is null ? null : ClickTarget(_hover);
        // REVERTED (round 4): plain outline on hover.
        if (target is not null && _flight is null) Outline(dc, target, HoverEdge);
        DrawHoverCard(dc);
        if (_showStats) DrawStats(dc);
    }

    // NEW (round 12): where each frame's time goes. Toggle with F3.
    private void DrawStats(DrawingContext dc)
    {
        var fps = _statFrame > 0 ? 1000 / _statFrame : 0;
        var text = Make(
            $"frame {_statFrame:0.0} ms ({fps:0} fps) · worst {_statWorstFrame:0} ms   scene {_statScene:0.0} ms · worst walk {_statWorstWalk:0} ms\n" +
            $"walk {_statWalk:0.0} · labels {_statLabels:0.0} · raster {_statRaster:0.0} · upload {_statUpload:0.0} ms\n" +
            $"{(_gpuShown ? (_gpu?.IsMultisampled == true ? "GPU 4×AA" : "GPU") : "CPU")} · {_statTiles:N0} tiles · {_statPixelsW}×{_statPixelsH} px · " +
            $"{_loads} loading · gen2 GCs {GC.CollectionCount(2) - _gen2Start}", 11, false);
        text.MaxLineCount = 3;
        text.SetForegroundBrush(Ink);
        var box = new Rect(10, 10, text.Width + 20, text.Height + 14);
        dc.DrawRoundedRectangle(CardFill, CardEdge, box, 6, 6);
        dc.DrawText(text, new Point(20, 17));
    }

    private void Outline(DrawingContext dc, Node node, Pen pen)
    {
        foreach (var (drawnNode, rect) in _drawn)
        {
            if (drawnNode != node) continue;
            var box = Rect.Intersect(SceneToScreen(rect), Rect.Inflate(_view, 2, 2));
            if (box.IsEmpty || box.Width < 2 || box.Height < 2) return;
            dc.DrawRectangle(null, pen, box); // CHANGED (round 9): tiles are square-cornered now
            return;
        }
    }

    private void DrawHoverCard(DrawingContext dc)
    {
        if (_hover is not { } hovered || hovered == _root || _dragging || _flight is not null) return;
        // CHANGED: describe the labeled block under the pointer (the item directly inside the
        // current folder), not the tiny tile within it. Zooming in moves the card a level deeper.
        if (ClickTarget(hovered) is not { } node) return;
        var target = node;
        var folder = node.Parent is null ? null : FolderOf(node.Parent);
        var title = Make(node.Item.Name, 13, true);
        title.SetForegroundBrush(Ink);
        var detail = Make($"{DiskUsagePalette.CategoryName(node.Item)} · {node.Item.SizeText}" +
            (folder is null ? "" : $" · {node.ShareText} of {(folder == _root ? folder.Item.Name.TrimEnd('\\') : folder.Item.Name)}"),
            11.5, false);
        detail.SetForegroundBrush(InkMuted);
        var hint = target is null ? null : Make(HintFor(target), 11, false);
        hint?.SetForegroundBrush(InkFaint);
        const double pad = 12, swatch = 9, maxText = 340;
        title.MaxTextWidth = maxText;
        detail.MaxTextWidth = maxText - swatch - 7;
        if (hint is not null) hint.MaxTextWidth = maxText;
        var width = Math.Max(title.Width, Math.Max(detail.Width + swatch + 7, hint?.Width ?? 0)) + pad * 2;
        var height = pad * 2 + title.Height + 3 + detail.Height + (hint is null ? 0 : 6 + hint.Height);
        var x = _mouse.X + 16;
        var y = _mouse.Y + 20;
        if (x + width > ActualWidth - 6) x = _mouse.X - 12 - width;
        if (y + height > ActualHeight - 6) y = _mouse.Y - 12 - height;
        x = Math.Max(6, x);
        y = Math.Max(6, y);
        dc.DrawRoundedRectangle(CardFill, CardEdge, new Rect(x, y, width, height), 8, 8);
        dc.DrawText(title, new Point(x + pad, y + pad));
        var line = y + pad + title.Height + 3;
        dc.DrawEllipse(BrushFor(NodeColor(node), 1), null, new Point(x + pad + swatch / 2, line + detail.Height / 2), swatch / 2, swatch / 2);
        dc.DrawText(detail, new Point(x + pad + swatch + 7, line));
        if (hint is not null) dc.DrawText(hint, new Point(x + pad, line + detail.Height + 6));
    }

    private string HintFor(Node target)
    {
        if (target.IsContainer) return target.IsGroup ? "Click to zoom in" : "Click to open";
        return target.Parent is not null && FolderOf(target.Parent) == _focusNode
            ? "Click to select · right-click for options" : "Click to open its folder";
    }

    // ---------------------------------------------------------------- text and resources

    // CHANGED (round 13): creating text is the costly part of a level change, so at most
    // TextPerFrame new labels are made per frame; the rest appear over the next frames (they
    // fade in anyway). Returns null when this frame's allowance is used up.
    private const int TextPerFrame = 24;
    private int _textBudget;
    private FormattedText? Text(ref CachedText cache, string text, double em, bool bold)
    {
        if (cache.Text is not null && cache.Em == em) return cache.Text;
        if (_textBudget <= 0) { _needsFrame = true; return null; }
        _textBudget--;
        cache = new CachedText(Make(text, em, bold), em);
        return cache.Text;
    }

    // NEW: re-tinting or re-trimming makes WPF lay the text out again, so only do it on change.
    private static void Tint(ref CachedText cache, Brush brush)
    {
        if (cache.Text is null || ReferenceEquals(cache.Tint, brush)) return;
        cache.Text.SetForegroundBrush(brush);
        cache.Tint = brush;
    }

    private static void Trim(ref CachedText cache, double width)
    {
        width = Math.Max(1, Math.Floor(width / 4) * 4);
        if (cache.Text is null || cache.Width == width) return;
        cache.Text.MaxTextWidth = width;
        cache.Width = width;
    }

    private FormattedText Make(string text, double em, bool bold) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? _semibold : _regular, em, Ink, _pixelsPerDip)
        { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };

    private void LoadResources()
    {
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (TryFindResource("UIFont") is FontFamily family)
        {
            _regular = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            _semibold = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        }
        if (TryFindResource("Base") is SolidColorBrush baseBrush && baseBrush.Color != _baseColor)
        {
            _baseColor = baseBrush.Color;
            _base = Frozen(baseBrush.Color);
        }
    }

    private static SolidColorBrush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Pen FrozenPen(Color color, double thickness) { var pen = new Pen(Frozen(color), thickness); pen.Freeze(); return pen; }

    // ================================================================= input

    private Node? NodeAt(Point point)
    {
        point = ScreenToScene(point); // NEW (round 5): the scene may be transformed mid-zoom
        for (var i = _drawn.Count - 1; i >= 0; i--)
            if (_drawn[i].Screen.Contains(point)) return _drawn[i].Node;
        return null;
    }

    // What a click acts on: inside the current folder, the child on the path to what was hit
    // (so clicks enter one level at a time); outside it, the branch that was hit.
    private Node? ClickTarget(Node hit)
    {
        var container = _container ?? _root;
        if (container is null || hit == container || IsAncestorOf(hit, container)) return null;
        for (var node = hit; node.Parent is not null; node = node.Parent)
            if (node.Parent == container || IsAncestorOf(node.Parent, container)) return node;
        return null;
    }

    private void Activate(Node target)
    {
        _userMoved = true;
        _message = null;
        if (target.IsContainer)
        {
            FlyTo(Fit(target));
            Report(FolderOf(target));
            return;
        }
        var folder = FolderOf(target.Parent!);
        if (folder == _focusNode)
        {
            ItemSelected?.Invoke(target.Item);
            Highlight(target.Item.Id);
        }
        else
        {
            FlyTo(Fit(folder));
            Report(folder);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        _lastInteraction = Now;
        base.OnMouseWheel(e);
        e.Handled = true;
        ZoomWithWheel(e.GetPosition(this), e.Delta);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _lastInteraction = Now;
        base.OnMouseLeftButtonDown(e);
        Focus();
        _press = e.GetPosition(this);
        _pressCamera = _camera;
        _pressed = true;
        _dragging = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        _mouseInside = true;
        if (_pressed && e.LeftButton == MouseButtonState.Pressed)
        {
            var delta = _mouse - _press;
            if (!_dragging && delta.Length > 4) { _dragging = true; _flight = null; }
            if (_dragging)
            {
                // Drag to pan: the world follows the pointer exactly.
                var scale = _pressCamera.Width / ActualWidth;
                _camera = _target = Clamp(new Rect(_pressCamera.X - delta.X * scale, _pressCamera.Y - delta.Y * scale,
                    _pressCamera.Width, _pressCamera.Height));
                _focusPoint = null;
                _userMoved = true;
                RequestCameraFrame();
            }
        }
        // Hover only touches the overlay layer; the scene is not redrawn.
        var target = !_dragging && NodeAt(_mouse) is { } hit ? ClickTarget(hit) : null;
        var cursor = _dragging ? Cursors.SizeAll : target is not null ? Cursors.Hand : null;
        if (Cursor != cursor) Cursor = cursor;
        _overlayDirty = true;
        if (!_hooked) RenderOverlay();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_pressed) return;
        var wasDragging = _dragging;
        _pressed = _dragging = false;
        ReleaseMouseCapture();
        if (!wasDragging) ClickAt(e.GetPosition(this));
        e.Handled = true;
        RequestFrame();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _pressed = _dragging = false;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseInside = false;
        _overlayDirty = true;
        if (!_hooked) RenderOverlay();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var hit = NodeAt(e.GetPosition(this));
        var target = hit is null ? null : ClickTarget(hit);
        if (target is null || target.Item.Id <= 0) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
        if (target.IsFolder)
        {
            var open = new MenuItem { Header = "Open" };
            open.Click += (_, _) => Activate(target);
            menu.Items.Add(open);
        }
        var inCurrentFolder = target.Parent is not null && FolderOf(target.Parent) == _focusNode;
        var delete = new MenuItem { Header = "Delete permanently…", IsEnabled = inCurrentFolder,
            ToolTip = inCurrentFolder ? null : "Open the containing folder to delete this item" };
        delete.Click += (_, _) => DeleteRequested?.Invoke(target.Item);
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F3) { ToggleStats(); e.Handled = true; return; } // NEW (round 12)
        base.OnKeyDown(e);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        if (e.Key is Key.Add or Key.OemPlus) { e.Handled = ZoomWithWheel(center, 120); }
        else if (e.Key is Key.Subtract or Key.OemMinus) { e.Handled = ZoomWithWheel(center, -120); }
    }
}
