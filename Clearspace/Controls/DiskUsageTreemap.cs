using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clearspace.Services;

namespace Clearspace.Controls;

// One continuous world of nested, proportional folder rectangles. Layout loads
// in the background; camera flights and wheel input never rearrange the world.
//
// Two immutable, overscanned detail layers are cached at a time. The GPU retains
// their vertices and applies a camera matrix, uploading only when detail,
// viewport, or source data changes. Layers partition space without parent
// overdraw and cross-fade as a whole. A CPU rasterizer consumes the same layers.
// Labels retain frozen glyph drawings and stay at readable screen sizes.
//
// Frames yield to input and reserve the compositor buffer before changing any
// visual layer. A busy buffer retains tiles, labels, and hit targets together.
public sealed class DiskUsageTreemap : FrameworkElement, IDisposable
{
    // ---------------------------------------------------------------- tuning
    private const int DirectLimit = DiskUsagePalette.DirectLimit;
    private const int NamedLimit = DiskUsagePalette.NamedLimit;
    private const int TailGroupSize = 150;      // "smaller items" blocks hold about this many
    private const int MaxTailGroups = 100;
    // CHANGED (round 10): slightly calmer density - the fractal look at a fraction of the cost.
    // CHANGED (round 27): these four thresholds, not the colours or the seams, are what decides how
    // much of the tree you can see at one zoom. A folder needed to be 24 px before it began to open
    // and 56 px to open fully, so a 30 px block - the size of a small project on screen - was barely
    // a fifth open and drew as a flat slab. Opening from 9 px means the same block is fully expanded
    // and shows two or three more levels of nesting, which is the density the map is meant to have.
    private const double MinTile = 1.3;         // px: smaller tiles are represented by their parent
    private const double ExpandStart = 9;       // px: a folder's contents begin to fade in
    private const double ExpandFull = 22;       // px: contents fully shown
    private const double PrefetchSize = 10;     // load a folder shortly before it is big enough to open
    private const double WheelStep = .8;        // camera width multiplier per wheel notch
    // CHANGED (round 24): .045 s meant each wheel notch had all but arrived before the next one came,
    // so a scroll read as a row of discrete jumps rather than one movement. A longer constant lets
    // consecutive notches merge into a single glide.
    private const double WheelSmoothing = .11;  // s: time constant of the wheel's easing
    private const double FocusDebounce = .14;   // s: wheel/drag focus must settle this long before the list follows
    private const double FolderMargin = .02;    // fraction of breathing room around a fitted folder
    private const double DimLevel = .6;         // opacity of the veil over everything outside the current folder
    private const double ColorFade = .55;       // s: cross-fade of colors and labels between levels
    private const int MaxLoads = 4;             // one speculative load while moving, four while settled
    // Raised with the thresholds above: the tree walk costs 0.3 ms and the GPU draws these in well
    // under a millisecond, so the budget that used to protect a 10 ms walk is no longer the limit.
    private const int TileBudget = 48000;       // bounded detail, independent of monitor width
    private const int CpuTileBudget = 18000;    // the software rasterizer pays per rectangle; the GPU does not
    private const int LeanTileBudget = 4000;    // "conserve memory": fewer blocks, much smaller working set
    private const int MaxTexts = 2500;          // labelled nodes allowed to keep their text layouts
    private const int LeanMaxTexts = 700;
    private const double TextIdle = 5;          // s off screen before a node's text is handed back
    // CHANGED (round 28): .74 on top of a folder that had already darkened to .42 gave a seam at 31%
    // of the hue - nearly black, and far heavier than it should be. A folder's surface is one gentle
    // step below its contents; the separation comes from the line being thin, not from it being dark.
    private const double FrameShade = .94;      // a folder's surface, just under its contents
    // NEW (round 21): a laid-out item costs roughly half a kilobyte - the node, its DiskUsageItem,
    // its name - and a folder's grouped tail holds the whole child array alive. Nothing ever
    // collapsed what had been expanded, so exploring a large drive grew without any bound at all.
    private const int NodeBudget = 220_000;
    private const int LeanNodeBudget = 30_000;
    private const double NodeIdle = 20;         // s off screen before an expanded folder is collapsed
    private const double LoadSettle = .45;      // s: batch finished loads into one redraw
    // CHANGED (round 13): full resolution always (the capped and motion buffers looked blurry).
    // The rasterizer now writes straight into the bitmap's memory, which removes a full-frame copy.
    private const double MaxPixels = 8_300_000; // only a safety cap (about a 4K screen)

    // ---------------------------------------------------------------- model
    private enum NodeState { Leaf, Collapsed, Pending, Ready }

    private record struct CachedText(FormattedText? Text, double Em, double NaturalWidth, Brush? Tint = null, double Width = -1, Drawing? Drawing = null);

    private sealed class Node
    {
        public Node(DiskUsageItem item, Rect bounds, Node? parent, int[]? members, long shareBase, int index)
        {
            Item = item;
            Bounds = bounds;
            Parent = parent;
            Members = members;
            Index = index;
            Depth = parent is null ? 0 : parent.Depth + 1;
            var share = shareBase <= 0 ? 0 : item.Bytes * 100d / shareBase;
            ShareText = share > 0 && share < .1 ? "<0.1%" : $"{share:0.#}%";
            State = item.Id < 0 ? (members is { Length: > 1 } ? NodeState.Collapsed : NodeState.Leaf)
                : item.IsFolder && item.Bytes > 0 ? NodeState.Collapsed : NodeState.Leaf;
        }

        public DiskUsageItem Item { get; }
        public Rect Bounds { get; }
        public Node? Parent { get; }
        // CHANGED (round 22): a "smaller items" block used to hold an ArraySegment over its folder's
        // whole child array, so one grouped folder pinned a DiskUsageItem - object, name and all,
        // roughly a hundred bytes - for every file under it, for as long as the folder stayed
        // expanded. It now keeps only their ids, about four bytes each, and resolves them from the
        // snapshot when the block is actually opened.
        public int[]? Members { get; }
        public int Index { get; }                // rank among siblings (largest first)
        public int Depth { get; }
        public string ShareText { get; }
        private string? _sizeText, _detailText;
        public string SizeText => _sizeText ??= Item.SizeText;
        public string DetailText => _detailText ??= $"{SizeText} · {ShareText}";
        public NodeState State { get; set; }
        public Node[] Children { get; set; } = [];
        public int FirstGroup { get; set; }      // NEW (round 15): index of the first "smaller items" child (named ones precede it)
        public double ReadyAt { get; set; } = double.NegativeInfinity;
        public bool IsGroup => Item.Id < 0;
        public bool IsFolder => Item.IsFolder && Item.Id >= 0;
        public bool IsContainer => IsGroup || IsFolder;
        public double Area => Bounds.Width * Bounds.Height;
        public CachedText LabelName, LabelDetail, PillName, PillSize;
        // NEW (round 20): a labelled node holds four WPF text layouts and four frozen glyph
        // drawings - kilobytes each, and nothing ever released them. These two fields let the
        // control hand that memory back for nodes that have left the screen.
        public double LastDrawn = double.NegativeInfinity;
        public bool HasText;
        // Two-slot color cache: the level being faded from and the level being faded to.
        public Node? ColorKeyA, ColorKeyB;
        // CHANGED (round 34): packed 0xRRGGBB rather than System.Windows.Media.Color. That struct
        // carries scRGB floats and a context reference, and every FromArgb converts between the two -
        // which at a hundred thousand blocks and several colour operations each was a large part of
        // the rebuild. Integer maths on packed bytes gives the same pixels.
        public uint ColorA, ColorB;
        // NEW (round 39): where this node sits in the flat whole-drive layout, resolved on first use.
        // FlatStamp says which layout the index belongs to, so a replaced layout is never misread.
        public int Flat = -1;
        public int FlatStamp;
    }

    private readonly record struct Placement(DiskUsageItem Item, Rect Bounds, int[]? Members);
    private readonly record struct Pill(Node Node, Rect Rect, FormattedText Name, FormattedText Size, Color Fill, double Alpha);
    private readonly record struct Caption(Node Node, Rect Visible, Rect Zone, double Alpha, bool IsOpen = false, Color Color = default);
    private sealed record HoverText(Node Node, Node? Focus, FormattedText Title, FormattedText Detail, FormattedText Hint);
    // Detached: a picture kept from a source that no longer exists (a drive change, a resize relayout).
    // It draws frozen at its own camera while the replacement builds, so the map never goes blank.
    private sealed record SceneCache(Rect Camera, Size Viewport, Rect Coverage, TileGeometry Geometry,
        IReadOnlyList<(Node Node, Rect Screen)> Tiles, Caption[] Captions, Dictionary<(Node, bool), Caption> LabelIndex, Node? Level, int Revision)
    {
        public bool Detached { get; init; }
        // NEW (round 40): the same scene in the previous level's colours, built from the same walk inputs
        // so every rectangle is identical. Fading from it to this one changes colour and nothing else,
        // which is what lets a recolour fade in even while the camera is moving. Dropped once the fade
        // has finished, so it only costs memory for the half second it is on screen.
        // CHANGED (round 43): the second colour set lives in the scene itself (TileCommand.ColorB), with
        // its own labels, instead of in a twin scene per colour as in rounds 40-42.
        public Node? LevelB { get; init; }
        public Caption[]? CaptionsB { get; init; }
        public Dictionary<(Node, bool), Caption>? LabelIndexB { get; init; }
        public double FadeSince { get; set; } = double.NaN;   // set when B is a timed fade rather than the zoom
    }

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
    private Func<int, DiskUsageItem>? _item;   // NEW (round 22): resolves a grouped member's id

    // NEW (round 35): a laid-out tree outlives the view that built it. Closing the analyser, or
    // switching to another drive and back, used to throw away every folder that had been read and
    // laid out and start again - which is the wait. The tree is handed to this cache on the way out
    // and taken back on the way in, so a return is immediate and keeps whatever depth was explored.
    //
    // Deliberately not on disk. Nothing here was read from disk: the sizes come from the file index
    // that is already in memory, and the cost is laying out and allocating the nodes. Writing them
    // out and reading them back would have to allocate exactly the same objects, plus the I/O, so it
    // would be slower than rebuilding. What is worth keeping is the built result, in memory.
    private sealed record TreeCacheEntry(object Key, int Aspect, Node Root, int Nodes);
    private static readonly List<TreeCacheEntry> TreeCache = [];
    private const int TreeCacheSize = 2;        // the drive you are on and the one you came from
    private object? _cacheKey;
    private readonly Queue<Node> _frontier = [];
    private bool _background = true;
    // NEW (round 38): background reading made zooming hitch. Every folder it finished bumped the
    // detail revision, and a bumped revision rebuilds the whole geometry once the 0.45 s settle gate
    // passes - even for a folder nowhere near the screen, and even mid-zoom. With "render
    // everything" a rebuild is a ~150 ms walk plus a multi-megabyte array, so a zoom hitched about
    // twice a second for as long as the drive was still being read. Background loads also held the
    // same slots on-screen folders need, so what you zoomed into waited behind them.
    private readonly HashSet<Node> _backgroundNodes = [];   // loads started by the frontier, not the screen
    private int _backgroundLoads;
    private bool _quietDetail;          // a background load changed something drawn; fold it in when idle
    private double _buildStamp = double.NegativeInfinity;   // Now at the start of the last geometry build
    private double _movedAt = double.NegativeInfinity;      // last frame the camera was moving
    private const double BackgroundRest = .6;   // s the camera must be still before reading ahead resumes
    private const double QuietRebuild = 1.5;    // s between rebuilds that only background loads asked for
    // Reading ahead is meant to warm the next zoom, not to fill the tree to its ceiling: a tree near
    // the ceiling is swept (a walk of every node on the UI thread) and every node is more gen2 heap
    // the collector has to trace. "Render everything" loads what is on screen by itself anyway.
    private int BackgroundCeiling => Math.Min(NodeCeiling, NodeBudget) * 7 / 10;
    private const int TreeCacheNodeLimit = 450_000;   // nodes kept across all cached trees combined

    // NEW (round 39): "render everything" draws from a flat whole-drive layout (FlatTreemapLayout)
    // instead of materialising a Node for every file. Nodes still exist for what needs an object -
    // labels, hover, clicks, the open path - at the normal mode's size; every finer block below the
    // deepest loaded Node is drawn straight from the flat arrays, so nothing has to load to appear.
    private FlatTreemapLayout? _flat;          // the layout for the current source, once built
    private FlatTreemapLayout? _flatNow;       // what the geometry being built reads (null when stale)
    private int _flatStamp, _flatStampNow;     // unique per assigned layout, across every instance
    private static int FlatStamps;
    private (object Source, double Aspect)? _flatBuilding;
    private (object Source, double Aspect)? _flatFailed;   // not retried every frame after an error
    private CancellationTokenSource? _flatWork;
    // Kept across close/reopen and drive switches like laid-out trees, so returning is immediate.
    // About 30 MB per million entries, so two is cheap; "conserve memory" keeps one.
    private static readonly List<FlatTreemapLayout> FlatCache = [];
    private static int FlatCacheSize => SettingsService.GetDiskMapConserveMemory() ? 1 : 2;

    /// <summary>True when the flat layout matches the tree on screen and "render everything" is on.</summary>
    private bool FlatActive => _everything && _root is not null && _flat is { } flat
        && ReferenceEquals(flat.Source, _cacheKey) && flat.Aspect == _root.Bounds.Width;

    // Reads the rest of the tree while you are looking at part of it, so switching or zooming later
    // finds it already laid out. Off, folders are read only as they become large enough to show.
    internal bool BackgroundBuilding
    {
        get => _background;
        set
        {
            if (_background == value) return;
            _background = value;
            _frontier.Clear();
            if (value && _root is not null) { SeedFrontier(_root); RequestCameraFrame(); }
        }
    }

    // Aspect is bucketed at roughly the 8% step that forces a re-layout anyway.
    private static int AspectBucket(double aspect) => (int)Math.Round(Math.Log(Math.Max(.05, aspect)) * 12);

    // NEW (round 40): trees a background scene walk is still reading, in any instance. A kept tree can
    // come straight back to a new source or a reopened analyzer while the old walk runs on it; two walks
    // writing the same nodes' colour cache and flat index at once could leave either wrong. Such a
    // tree is simply not reused - it is rebuilt, which is only slower.
    private static readonly HashSet<Node> WalkingRoots = [];

    private static Node? TakeCachedTree(object? key, double aspect, out int nodes)
    {
        nodes = 0;
        if (key is null) return null;
        var bucket = AspectBucket(aspect);
        lock (TreeCache)
            for (var i = 0; i < TreeCache.Count; i++)
                if (ReferenceEquals(TreeCache[i].Key, key) && TreeCache[i].Aspect == bucket && !IsBeingWalked(TreeCache[i].Root))
                {
                    var entry = TreeCache[i];
                    TreeCache.RemoveAt(i);
                    nodes = entry.Nodes;
                    return entry.Root;
                }
        return null;
    }

    private static bool IsBeingWalked(Node root)
    {
        lock (WalkingRoots) return WalkingRoots.Contains(root);
    }

    private void KeepTree(object? key, double aspect, Node? root, int nodes)
    {
        // Keeping a tree is trading memory for an instant return, which is the one thing
        // "conserve memory" is asked to give up.
        if (key is null || root is null || nodes <= 0 || _lean) return;
        lock (TreeCache)
        {
            var bucket = AspectBucket(aspect);
            TreeCache.RemoveAll(entry => ReferenceEquals(entry.Key, key) && entry.Aspect == bucket);
            TreeCache.Add(new TreeCacheEntry(key, bucket, root, nodes));
            while (TreeCache.Count > TreeCacheSize) TreeCache.RemoveAt(0);
            // CHANGED (round 38): kept trees are live heap the collector traces on every gen2 pass
            // while you zoom the current one. Two "render everything" trees are 2.4 million nodes on
            // top of the one on screen, so the total is bounded and the oldest goes first. A single
            // tree over the limit is still kept - it is the one you most likely return to.
            while (TreeCache.Count > 1 && TreeCache.Sum(entry => (long)entry.Nodes) > TreeCacheNodeLimit) TreeCache.RemoveAt(0);
        }
    }

    // NEW (round 42): kept trees and flat layouts each hold the snapshot they were built from, and that
    // snapshot holds its file index. One whose snapshot the snapshot cache has let go (replaced by a
    // refresh, a delete, a rescan or eviction) can never be used again - nothing will ask for that
    // instance - so it is released here instead of pinning an old snapshot, or a whole old index,
    // until the next time the caches happen to be cleared.
    private static void DropStaleCaches(object? current)
    {
        lock (TreeCache) TreeCache.RemoveAll(entry => !ReferenceEquals(entry.Key, current) && !DiskUsageSnapshotCache.IsHeld(entry.Key));
        lock (FlatCache) FlatCache.RemoveAll(entry => !ReferenceEquals(entry.Source, current) && !DiskUsageSnapshotCache.IsHeld(entry.Source));
    }

    /// <summary>Drops every kept tree. Called when the snapshots behind them are replaced.</summary>
    internal static void ForgetCachedTrees()
    {
        lock (TreeCache) TreeCache.Clear();
        lock (FlatCache) FlatCache.Clear();   // NEW (round 39): same snapshots, same lifetime
    }
    private CancellationTokenSource _generation = new();
    private double _builtAspect = 1;
    private int _loads;
    private bool _building, _disposed;
    private int _navigation;
    private int[] _navigationPath = [];
    private readonly Dictionary<Node, Task> _nodeLoads = [];

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
    private Node? _colorPending;               // a level the camera is passing through
    private double _colorPendingSince;
    private const double ColorSettle = .32;    // s a level must hold before the colours follow it
    private double _sourceFadeAt = double.NegativeInfinity;
    private string? _message;
    private string? _missingName;
    private int? _highlight;
    private Node? _highlightNode;
    private bool _highlightResolved;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _timeOffset;
    private double _lastFrame;
    private bool _hooked;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private DispatcherOperation? _queuedFrame;
    private bool _sceneDirty = true;
    private bool _overlayDirty = true;
    private bool _needsFrame;

    // Two layers: the scene (tiles, labels, veil) and the overlay (hover, selection, card).
    private readonly DrawingVisual _scene = new();
    private readonly DrawingVisual _overlay = new();
    private List<(Node Node, Rect Screen)> _drawn = [];
    // NEW (round 9): software-rendered tile layer.
    private readonly DrawingVisual _labels = new();
    // CHANGED (round 43): ColorB is the block's colour under the scene's second colour level; the GPU mixes the
    // two each frame. Left out (as in tests and diagnostics), it is the same as Color.
    internal readonly record struct TileCommand(float X0, float Y0, float X1, float Y1, uint Color, byte Alpha, uint ColorB = TileCommand.SameColor) // internal: shared with GpuTileRenderer
    {
        public const uint SameColor = 0xFFFFFFFF;   // colours are 0xRRGGBB, so this can never be a real one
        public uint To => ColorB == SameColor ? Color : ColorB;
    }

    // NEW (round 14): GPU rendering through Direct3D 9Ex + D3DImage; the CPU rasterizer is the fallback.
    private GpuTileRenderer? _gpu;
    private bool _gpuTried;
    private bool _gpuShown;
    private bool _gpuEnabled = true, _lowDetail;
    private int _gpuSkipped;   // frames the compositor would not release the surface for

    // NEW (round 18): performance options, owned by the settings panel and persisted between runs.
    // Off, the map uses its CPU rasterizer - the right answer when a display driver will not share a
    // surface, and the only answer on a machine with no usable Direct3D at all.
    internal bool GpuEnabled
    {
        get => _gpuEnabled;
        set
        {
            if (_gpuEnabled == value) return;
            _gpuEnabled = value;
            _gpu?.Dispose();
            _gpu = null;
            _gpuTried = !value;     // off: never try again. on: probe on the next frame.
            _gpuShown = false;      // the scene layer must re-attach to whichever image now draws it
            _shown = null;
            _sceneDirty = true;
            RequestFrame();
        }
    }

    // Draws only the folder you are in: its children stay solid blocks instead of opening into their
    // own contents. Traversal then costs one level however deep the drive is, which is what makes the
    // map usable on integrated graphics and on very large folders.
    internal bool LowDetail
    {
        get => _lowDetail;
        set
        {
            if (_lowDetail == value) return;
            _lowDetail = value;
            _detailRevision++;      // the cached layer's detail decisions are no longer valid
            _sceneDirty = true;
            RequestFrame();
        }
    }

    // What the settings panel reports as the active renderer.
    internal string RendererLabel => !_gpuEnabled ? "CPU rasterizer"
        : _gpu is { IsAvailable: true } gpu ? $"{gpu.AdapterName}{(gpu.IsMultisampled ? " · 4x AA" : "")}"
        : _gpuTried ? "CPU rasterizer - no usable Direct3D adapter"
        : "Starting up";
    private TileCommand[] _commands = new TileCommand[16384];
    private int _commandCount;
    private SceneCache? _cache, _previousCache;
    private readonly TileBatch[] _batches = new TileBatch[3];   // CHANGED (round 42): room for the zoom-blend twin
    private int _batchCount, _detailRevision;
    private readonly List<Node> _texted = [];   // nodes currently holding text layouts, newest last
    private double _sweptAt = double.NegativeInfinity;
    private double _nodeSweptAt = double.NegativeInfinity;
    private int _liveNodes;
    internal int LiveNodeCount => _liveNodes;
    private bool _lean;
    // Each block costs five rectangles. The GPU draws 150,000 of them in well under a millisecond;
    // the CPU rasterizer fills them one span at a time, so it gets a smaller budget and stays smooth.
    private int Budget => _everything ? EverythingBudget : _lean ? LeanTileBudget : _gpuShown ? TileBudget : CpuTileBudget;
    private int TextBudget => _lean ? LeanMaxTexts : MaxTexts;
    // CHANGED (round 39): with the flat layout drawing the detail, "render everything" needs Nodes only
    // for what the normal mode needs them for, so it gets the normal mode's ceiling.
    private int NodeCeiling => _lean ? LeanNodeBudget : _everything && !FlatActive ? EverythingNodeBudget : NodeBudget;

    // NEW (round 32): "render everything". Every block at every depth is drawn, down to a fifth of a
    // pixel, with no expansion threshold and no per-subtree allowance - so zooming reveals nothing
    // that was not already on screen and nothing pops in. The GPU draws a million rectangles without
    // noticing; what this costs is the tree itself, because every folder has to be loaded and kept,
    // and the walk that produces the geometry grows with it. The reuse window widens to match, since
    // a scene this dense tolerates being stretched further between rebuilds.
    private bool _everything;
    // Past about a million the blocks are smaller than a pixel and add nothing but vertices; a
    // 2146x1211 window is 2.6 million pixels in total.
    private const int EverythingBudget = 750_000;
    private const int EverythingNodeBudget = 1_200_000;
    internal bool RenderEverything
    {
        get => _everything;
        set
        {
            if (_everything == value) return;
            _everything = value;
            if (!value)
            {
                // NEW (round 39): the flat layout serves only this mode. The cache keeps a copy for
                // switching back; this instance lets go of its own.
                _flat = null;
                _flatWork?.Cancel();
                _flatWork = null;
                _flatBuilding = null;
            }
            _detailRevision++;
            _sceneDirty = true;
            RequestFrame();
        }
    }

    private double MinimumTile => _everything ? .2 : MinTile;
    private double ExpandLow => _everything ? .5 : ExpandStart;
    private double ExpandHigh => _everything ? 1.5 : ExpandFull;
    // CHANGED (round 39): folders are loaded as Nodes only once they are big enough to be labelled or
    // clicked; below that the flat layout draws them. That holds while the layout is still being built
    // too - loading down to half a pixel for that one second would fill the tree it exists to replace.
    private double PrefetchAt => _everything && _cacheKey is not IFlatSource ? .5 : PrefetchSize;
    internal int LiveTextCount => _texted.Count;

    // Trades detail for a much smaller working set: fewer blocks per frame and far fewer text
    // layouts kept alive. Everything still draws, just with less fine detail far from the camera.
    internal bool ConserveMemory
    {
        get => _lean;
        set
        {
            if (_lean == value) return;
            _lean = value;
            _detailRevision++;
            TrimTextCache(force: true);
            _sceneDirty = true;
            RequestFrame();
        }
    }

    private double _cacheSince;
    private double _fadeLength = CacheFade;
    private double _holdoverSince = double.NegativeInfinity;
    private const double CacheFade = .18;
    private const double SourceFade = .34;      // cross-fade when the whole source is replaced
    private const double HoldoverLimit = 4;     // s: never show a stale picture longer than this
    internal int GeometryBuildCount { get; private set; }
    internal int GeometryReuseCount { get; private set; }
    internal int GpuGeometryUploads => _gpu?.GeometryUploads ?? 0;
    internal TileGeometry? CachedGeometry => _cache?.Geometry;
    private sealed class Surface
    {
        public WriteableBitmap? Bitmap;
        public double ScaleX = 1, ScaleY = 1;
    }
    private readonly Surface _full = new();
    private Surface? _shown;          // the surface the scene layer currently displays

    // NEW (round 12): F3 performance readout (milliseconds, smoothed).
    private bool _showStats;
    private FormattedText? _statsText;
    private double _statsUpdatedAt = double.NegativeInfinity;
    private HoverText? _hoverText;
    private double _statFrame, _statWalk, _statLabels, _statRaster, _statUpload, _statScene;
    private double _statWorstFrame, _statWorstWalk; // NEW (round 15): spikes, not just averages
    private int _statTiles, _statPixelsW, _statPixelsH, _gen2Start;
    private readonly Stopwatch _statClock = new();
    internal void ToggleStats()
    {
        _showStats = !_showStats;
        _gen2Start = GC.CollectionCount(2);
        _statsText = null;
        _overlayDirty = true;
        RequestFrame();
    }
    private static void Smooth(ref double stat, double sample) => stat += (sample - stat) * .15;
    private readonly List<Caption> _deferred = [];
    private bool _overlayAnimating;   // overlay-only frames (kept for future overlay fades)

    private Rect _sceneCamera = new(0, 0, 1, 1);  // camera the scene layer was last drawn with
    private readonly List<(Node Node, double Area)> _wanted = [];
    private readonly HashSet<Node> _openPath = [];
    private Rect _view, _viewLoose;
    private int _tiles;
    private Node? _hover;
    private Point _mouse;
    private bool _mouseInside, _pressed, _dragging;
    private Point _press;
    private Rect _pressCamera;
    private readonly DispatcherTimer _rebuild;

    // NEW (round 40): the analyzer runs the collector in SustainedLowLatency, which never does a
    // blocking full collection on its own. That keeps zooming smooth, but everything the map discards
    // - scene buffers, superseded snapshots and layouts after a live refresh - piles up for as long as
    // the view is open, which is how the process reached gigabytes. Once the map has been left alone
    // for a few seconds and the heap has grown by a few hundred megabytes, one full collection runs.
    // It is non-compacting, so it only marks: tens of milliseconds for the few hundred thousand
    // objects the map keeps, never a copy of the index's large arrays.
    private readonly DispatcherTimer _heapTrim;
    private long _heapAfterTrim;
    private const long HeapTrimGrowth = 384L * 1024 * 1024;

    private void TrimHeapWhenIdle()
    {
        if (_disposed) { _heapTrim.Stop(); return; }
        if (IsAnimating || _dragging || _walk is not null || _building || SecondsSinceInteraction < 3) return;
        var heap = GC.GetTotalMemory(false);
        if (heap - _heapAfterTrim < HeapTrimGrowth) return;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        _heapAfterTrim = GC.GetTotalMemory(false);
        HeapTrims++;
    }

    internal int HeapTrims { get; private set; }

    // ---------------------------------------------------------------- resources
    private static readonly uint[] BranchColors = DiskUsagePalette.Branches.Select(hex => Pack(Parse(hex))).ToArray();
    private static readonly uint GroupColor = Pack(Parse(DiskUsagePalette.GroupColor));
    private static readonly Color White = Color.FromRgb(0xF6, 0xF3, 0xEE);
    private readonly Dictionary<uint, SolidColorBrush> _brushes = [];
    private Typeface _regular = new("Segoe UI");
    private Typeface _semibold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private double _pixelsPerDip = 1;
    private Color _baseColor = Color.FromRgb(0x1A, 0x19, 0x17);
    private uint _basePacked = 0x1A1917;
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
        // NEW (round 40): see TrimHeapWhenIdle.
        _heapTrim = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(2) };
        _heapTrim.Tick += (_, _) => TrimHeapWhenIdle();   // started on Loaded, stopped on Unloaded
        _rebuild = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(220) };
        _rebuild.Tick += (_, _) => { _rebuild.Stop(); Rebuild(); };
        Loaded += (_, _) => { LoadResources(); RequestFrame(); _heapTrim.Start(); };
        Unloaded += (_, _) =>
        {
            StopFrames();
            _heapTrim.Stop();
            _rebuild.Stop();
            _gpu?.Dispose(); // NEW (round 14)
            _gpu = null;
            _gpuTried = _gpuShown = false;
            _shown = null;
        };
    }

    protected override int VisualChildrenCount => 3;
    protected override Visual GetVisualChild(int index) => index switch { 0 => _scene, 1 => _labels, _ => _overlay };

    private void ReleaseTreeReferences()
    {
        _sceneEpoch++;   // NEW (round 40): a background walk started before this is never installed
        // CHANGED (round 19): hold the picture that is on screen rather than dropping it. SetSource
        // runs on a drive change, on the post-resize relayout and on first load, and each of those
        // used to show the bare background for as long as the background build took - the flash.
        var held = _cache ?? _previousCache;
        // CHANGED (round 40): the held picture does not keep a colour twin alive for its holdover.
        _previousCache = held is null ? null : held with { Detached = true };
        _holdoverSince = held is null ? double.NegativeInfinity : Now;
        _cache = null;
        Array.Clear(_batches);
        _batchCount = 0;
        _nodeLoads.Clear();
        _frontier.Clear();
        _backgroundNodes.Clear();   // NEW (round 38)
        _backgroundLoads = 0;
        _quietDetail = false;
        _pathSets.Clear();
        _openPath.Clear();
        _wanted.Clear();
        _drawn.Clear();
        _deferred.Clear();
        foreach (var node in _texted) ReleaseText(node);
        _texted.Clear();
        _liveNodes = 0;
        _container = _focusNode = _colorLevel = _colorPrevious = _candidate = _hover = null;
        _highlightNode = null;
        _hoverText = null;
        _highlightResolved = false;
    }

    private void CancelLoads()
    {
        _generation.Cancel();
        foreach (var node in _nodeLoads.Keys)
            if (node.State == NodeState.Pending) node.State = NodeState.Collapsed;
        _nodeLoads.Clear();
        _generation = new CancellationTokenSource();
        _loads = 0;
        _building = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _navigation++;
        _generation.Cancel();
        _heapTrim.Stop();      // NEW (round 40)
        _commandPool.Clear();  // NEW (round 41)
        _pooledGeometry.Clear();
        _flatWork?.Cancel();   // NEW (round 39)
        _flat = _flatNow = null;
        StopFrames();
        _rebuild.Stop();
        KeepTree(_cacheKey, _builtAspect, _root, _liveNodes);   // so reopening the analyser is immediate
        ReleaseTreeReferences();
        _previousCache = null;   // NEW (round 19): do not keep a held picture alive past close
        _root = null;
        _pending = null;
        _children = null;
        _gpu?.Dispose();
        _gpu = null;
        _full.Bitmap = null;
        using (_scene.RenderOpen()) { }
        using (_labels.RenderOpen()) { }
        using (_overlay.RenderOpen()) { }
    }

    // ================================================================= public surface

    /// <summary>Show a new index snapshot. <paramref name="path"/> lists folder ids below the root.</summary>
    internal void SetSource(DiskUsageItem root, Func<int, CancellationToken, IReadOnlyList<DiskUsageItem>> children,
        IReadOnlyList<int> path, string? missingName = null, Func<int, DiskUsageItem>? item = null,
        object? cacheKey = null)
    {
        if (_disposed) return;
        _item = item ?? _item;
        KeepTree(_cacheKey, _builtAspect, _root, _liveNodes);   // the tree being replaced is worth keeping
        _cacheKey = cacheKey ?? _cacheKey;
        DropStaleCaches(_cacheKey);   // NEW (round 42)
        CancelLoads();
        _navigation++;
        ReleaseTreeReferences();
        _children = children;
        _navigationPath = path.ToArray();
        _pending = (root, _navigationPath, missingName);
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
        if (_disposed) return;
        _navigationPath = path.ToArray();
        if (_root is null)
        {
            if (_pending is { } pending) _pending = (pending.Root, _navigationPath, missingName);
            return;
        }
        _ = ShowFolderAsync(_navigationPath, missingName, ++_navigation);
    }

    private async Task ShowFolderAsync(IReadOnlyList<int> path, string? missingName, int navigation)
    {
        var generation = _generation;
        var target = _root!;
        var complete = true;
        foreach (var part in path)
        {
            var next = await FindChildAsync(target, part);
            if (_disposed || generation != _generation || navigation != _navigation) return;
            if (next is null) { complete = false; break; }
            target = next;
        }
        var id = path.Count == 0 ? _root!.Item.Id : path[^1];
        _missingName = complete ? null : missingName;
        _message = complete ? null : $"“{missingName ?? "This folder"}” has no file sizes to show";
        _candidate = null;
        // A focus change the map reported itself comes back here; the camera is already there.
        if (complete && id == _reportedFocus && _focusNode == target) { RequestFrame(); return; }
        _reportedFocus = id;
        _focusNode = target;
        _highlightResolved = false;
        _userMoved = false;
        FlyTo(Fit(target));
    }

    // NEW: show nothing but a message (a drive that isn't indexed yet).
    internal void ClearSource(string? message)
    {
        if (_disposed) return;
        CancelLoads();
        _navigation++;
        ReleaseTreeReferences();
        _previousCache = null;   // NEW (round 19): nothing to show, so hold nothing over
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
        _highlightResolved = false;
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
    internal int RenderedTileCount => _cache?.Tiles.Count ?? 0;
    internal double LastWalkMilliseconds { get; private set; }
    internal int PreparedFrameCount { get; private set; }
    internal int ScheduledFrameCount { get; private set; }
    internal Rect PresentedCamera => _sceneCamera;
    internal void RenderTestFrame(double seconds = 1 / 60d)
    {
        UseManualClock();
        _timeOffset += seconds;
        StepFrame(seconds);
        if (RenderScene()) RenderOverlay();
    }
    internal string? CurrentContainerName => _container?.Item.Name;
    internal IReadOnlyList<(DiskUsageItem Item, Rect Screen)> VisibleTiles
    {
        get
        {
            if (_cache is not { } cache) return [];
            var mapping = BatchFor(cache);
            return cache.Tiles.Select(tile => (tile.Node.Item, mapping.Transform(tile.Screen))).ToArray();
        }
    }

    internal Rect? ScreenBoundsOf(int id)
    {
        var node = _root is null ? null : Find(_root, id);
        return node is null ? null : ToScreen(node.Bounds);
    }

    /// <summary>Advance the animation clock deterministically (tests, previews).</summary>
    internal void AdvanceTime(double seconds)
    {
        UseManualClock();
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
        _lastInteraction = Now;
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
        if (_disposed || _building || _root is not null || _pending is not { } pending || ActualWidth < 1 || ActualHeight < 1) return;
        LoadResources();
        _builtAspect = ActualWidth / ActualHeight;
        _building = true;
        _loads++;
        _ = BuildSourceAsync(pending, _generation, _builtAspect, _children!);
    }

    private async Task BuildSourceAsync((DiskUsageItem Root, IReadOnlyList<int> Path, string? Missing) pending,
        CancellationTokenSource generation, double aspect, Func<int, CancellationToken, IReadOnlyList<DiskUsageItem>> provider)
    {
        try
        {
            var kept = TakeCachedTree(_cacheKey, aspect, out var keptNodes);
            // NEW (round 39): a tree kept from "render everything" before it had the flat layout can hold
            // a Node for every file. Rebuilding is quicker than sweeping that down, and far lighter.
            if (kept is not null && _everything && _cacheKey is IFlatSource && keptNodes > NodeBudget * 2) kept = null;
            // NEW (round 39): the flat layout is started now, beside the tree, not after it - it is laid
            // out for the root the tree will have, which for a kept tree is the shape it was built for.
            EnsureFlat(kept?.Bounds.Width ?? aspect);
            var tree = kept ?? await Task.Run(() => BuildTree(pending.Root, provider, _item, aspect,
                new HashSet<int>(pending.Path), [], generation.Token), generation.Token);
            if (_disposed || generation != _generation) return;
            var requested = _pending ?? pending;
            if (Math.Abs(ViewAspect / aspect - 1) > .08)
            {
                // Built for a shape the window no longer has. Keep it under that shape's bucket so
                // returning to it costs nothing, and lay the tree out again for the current one.
                KeepTree(_cacheKey, aspect, tree, kept is not null ? keptNodes : Sweep(tree, double.NegativeInfinity).Count);
                SetSource(requested.Root, provider, requested.Path, requested.Missing, _item);
                return;
            }
            _root = tree;
            // A kept tree already knows its size; a fresh one is counted once.
            _liveNodes = kept is not null ? keptNodes : Sweep(tree, double.NegativeInfinity).Count;
            SeedFrontier(tree);
            _pending = null;
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
            _sourceFadeAt = Now;
            if (!requested.Path.SequenceEqual(pending.Path)) ShowFolder(requested.Path, requested.Missing);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (generation == _generation) { _pending = null; _message = $"Could not prepare map: {exception.Message}"; }
        }
        finally
        {
            if (generation == _generation)
            {
                _building = false;
                _loads--;
                if (!_disposed) RequestFrame();
            }
        }
    }

    private void Rebuild()
    {
        if (_root is null || _children is null) return;
        var path = PathOf(_focusNode ?? _root);
        var missing = _missingName;
        var root = _root.Item;
        var fade = _sourceFadeAt;
        SetSource(root, _children, path, missing, _item);
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

        var entries = new List<(DiskUsageItem Item, int[]? Members)>(Math.Min(positive.Length, DirectLimit + 1));
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
                var members = new int[count];
                long bytes = 0, files = 0;
                for (var k = 0; k < count; k++)
                {
                    var member = positive[start + k];
                    members[k] = member.Id;
                    bytes += member.Bytes;
                    files += member.FileCount;
                }
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
    // CHANGED (round 39): takes the new snapshot as its cache key, which it never did - so a refreshed
    // tree was kept under the snapshot it replaced - and lays out the flat detail for it in parallel
    // with the tree, so the two are swapped in together and never disagree for a frame.
    // FIXED (round 39): also takes the new snapshot's item resolver. Groups were resolved with the old
    // snapshot's, so their members kept stale sizes, and an id past its end threw and lost the refresh.
    internal void RefreshSource(DiskUsageItem root, Func<int, CancellationToken, IReadOnlyList<DiskUsageItem>> children,
        IReadOnlyList<int> path, string? missingName = null, object? cacheKey = null, Func<int, DiskUsageItem>? itemResolver = null)
    {
        if (_disposed) return;
        if (_root is null || _root.Item.Id != root.Id || ActualWidth < 1 || ActualHeight < 1)
        {
            SetSource(root, children, path, missingName, itemResolver ?? _item, cacheKey);
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
        var item = itemResolver ?? _item;
        var pathIds = path.ToArray();
        CancelLoads();
        var generation = _generation;
        var token = generation.Token;
        // Only for a new snapshot the caller named: a layout keyed to the old one would be paired with
        // a tree of new data.
        var flatSource = _everything ? cacheKey as IFlatSource : null;
        Task.Run(() =>
        {
            // The flat layout is a bonus to the refresh, never a reason to lose it: a failure leaves it
            // null, and EnsureFlat tries again from the next frame.
            var flatStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var flatTask = flatSource is null ? null : Task.Run(() =>
            {
                try { return FlatTreemapLayout.Build(flatSource, aspect, flatStop.Token); }
                catch (Exception) when (!token.IsCancellationRequested) { return null; }
            }, flatStop.Token);
            Node tree;
            try { tree = BuildTree(root, children, item, aspect, folders, groups, token); }
            catch
            {
                flatStop.Cancel();   // nothing to pair it with
                flatTask?.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                throw;
            }
            FlatTreemapLayout? flat = null;
            try { flat = flatTask?.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            return (Tree: tree, Flat: flat);
        }, token).ContinueWith(task =>
            Dispatcher.InvokeAsync(() =>
            {
                _ = task.Exception; // Observe errors even when an obsolete refresh is discarded.
                if (_disposed || !ReferenceEquals(generation, _generation) || task.Status != TaskStatus.RanToCompletion) return;
                var latestPath = _navigationPath;
                var navigated = !latestPath.SequenceEqual(pathIds);
                if (navigated) pathIds = latestPath.ToArray();
                CancelLoads();
                ReleaseTreeReferences();
                _children = children;
                _item = item;   // FIXED (round 39)
                // NEW (round 39): the superseded snapshot's layout is useless now and would pin that
                // snapshot in memory; a build still running for it is cancelled.
                if (cacheKey is not null && !ReferenceEquals(cacheKey, _cacheKey))
                {
                    var replaced = _cacheKey;
                    lock (FlatCache) FlatCache.RemoveAll(entry => ReferenceEquals(entry.Source, replaced));
                    _flatWork?.Cancel();
                    _flatWork = null;
                    _flatBuilding = null;
                }
                _cacheKey = cacheKey ?? _cacheKey;
                DropStaleCaches(_cacheKey);   // NEW (round 42)
                _root = task.Result.Tree;
                if (task.Result.Flat is { } flat)
                {
                    StoreFlat(flat);
                    if (_everything) UseFlat(flat);
                }
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
                if (navigated && !complete) ShowFolder(pathIds);
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
        Func<int, DiskUsageItem>? item, double aspect, HashSet<int> folders, HashSet<(int Folder, string Key)> groups, CancellationToken token)
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
                    !node.Members!.Any(folders.Contains)) continue;
                items = Resolve(node.Members!, item, token);
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

    // CHANGED (round 8): folders *and* grouped small items load here, off the UI thread, and the
    // child nodes are built there too; the UI thread only attaches the finished array.
    private int ForegroundLoads => _loads - _backgroundLoads;

    private Task StartLoad(Node node, bool background = false)
    {
        if (_nodeLoads.TryGetValue(node, out var existing)) return existing;
        if (_disposed || node.State != NodeState.Collapsed || (_children is null && !node.IsGroup)) return Task.CompletedTask;
        node.State = NodeState.Pending;
        _loads++;
        if (background && _backgroundNodes.Add(node)) _backgroundLoads++;   // NEW (round 38)
        var generation = _generation;
        var task = LoadNodeAsync(node, generation, _children);
        if (!task.IsCompleted) _nodeLoads[node] = task;
        return task;
    }

    private async Task LoadNodeAsync(Node node, CancellationTokenSource generation,
        Func<int, CancellationToken, IReadOnlyList<DiskUsageItem>>? provider)
    {
        var token = generation.Token;
        var resolver = _item;
        try
        {
            var children = await Task.Run(() =>
            {
                var items = node.IsGroup ? Resolve(node.Members!, resolver, token) : provider!(node.Item.Id, token);
                return BuildChildren(node, LayoutLevel(items, node.Bounds, token));
            }, token);
            if (_disposed || generation != _generation) return;
            node.Children = children;
            _liveNodes += children.Length;
            Extend(node);
            // CHANGED (round 38): a folder that was not in the scene on screen cannot change it - it
            // appears with its contents the next time the geometry is built for any other reason.
            // One that was drawn but read in the background waits for a still camera and is folded
            // in with its neighbours, instead of rebuilding the scene under a zoom.
            // NEW (round 39): a folder the flat layout already drew looks the same with its Nodes as
            // without them, unless its children are the labelled level. Rebuilding for it would be a
            // full walk for nothing, and during a zoom that is the hitch.
            // CHANGED (round 40): while a background walk runs, its reads and these writes can pass each
            // other, so the rebuild is always asked for then - an extra build, never a missed one.
            var sameAsFlat = _walk is null && node.FlatStamp == _flatStampNow && node.Flat >= 0 && _flatNow is not null
                && !OnPath(node, _colorLevel) && (_cache?.LevelB is not { } labelledB || !OnPath(node, labelledB));   // CHANGED (round 43)
            if ((node.LastDrawn >= _buildStamp || _walk is not null) && !sameAsFlat)
            {
                if (_backgroundNodes.Contains(node)) _quietDetail = true;
                else _detailRevision++;
            }
            node.State = children.Length > 0 ? NodeState.Ready : NodeState.Leaf;
            node.ReadyAt = Now;
            _highlightResolved = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (generation == _generation) node.State = NodeState.Leaf; }
        finally
        {
            if (generation == _generation)
            {
                _loads--;
                if (_backgroundNodes.Remove(node)) _backgroundLoads--;   // NEW (round 38)
                _nodeLoads.Remove(node);
                // CHANGED (round 8): arriving detail doesn't force a full redraw mid-zoom; it is
                // picked up by the next scheduled redraw (immediately when the camera is still).
                _needsFrame = true;
                if (!_disposed)
                {
                    if (_hooked) _overlayDirty = true;
                    else RequestFrame();
                }
            }
        }
    }

    private void PumpLoads()
    {
        // Rendering everything means loading everything, so the speculative limit comes off.
        var limit = _everything ? 12 : IsAnimating || _dragging ? 1 : MaxLoads;
        // CHANGED (round 38): counted without background loads, so reading ahead never takes a slot
        // from something on screen.
        if (_wanted.Count > 0 && ForegroundLoads < limit)
        {
            _wanted.Sort((a, b) => b.Area.CompareTo(a.Area));
            foreach (var (node, _) in _wanted)
            {
                if (ForegroundLoads >= limit) break;
                if (node.State == NodeState.Collapsed) _ = StartLoad(node); // largest on screen first
            }
        }
        _wanted.Clear();
        PumpBackground();
    }

    // NEW (round 36): background building. What is on screen is read first, as always; whatever
    // capacity is left goes to a breadth-first frontier that keeps reading the rest of the tree while
    // the window is open. Breadth-first because the next thing you zoom into is more likely to be
    // large and shallow than small and deep. It stops well short of the node ceiling, so it can fill
    // a drive in without ever provoking the eviction sweep it would otherwise fight.
    private void PumpBackground()
    {
        if (!_background || _disposed || _root is null) return;
        // NEW (round 39): with the flat layout the whole drive is already drawn; reading ahead would
        // only build Nodes nobody needs.
        if (FlatActive) return;
        // CHANGED (round 38): a lower, fixed ceiling (was 80% of whichever ceiling applied, which with
        // "render everything" was 960,000 nodes), and nothing at all while the camera moves or has
        // only just stopped - a zoom gets the whole machine, and reading ahead resumes once it rests.
        if (_liveNodes >= BackgroundCeiling) return;
        if (IsAnimating || _dragging || Now - _movedAt < BackgroundRest)
        {
            if (_frontier.Count > 0) _needsFrame = true;   // come back once the rest has elapsed
            return;
        }
        var limit = _everything ? 4 : 2;
        while (_backgroundLoads < limit && _frontier.Count > 0)
        {
            var node = _frontier.Dequeue();
            if (node.State == NodeState.Collapsed) _ = StartLoad(node, background: true);
        }
        // Each finished load already asks for a frame, and this pump runs on every frame, so the two
        // carry each other along. This only covers the case where nothing is in flight to wake us.
        if (_loads == 0 && _frontier.Count > 0) RequestCameraFrame();
    }

    // A tree taken back from the cache is already part-read; the frontier has to be recovered from
    // whatever is still collapsed in it, rather than from the root alone.
    private void SeedFrontier(Node root)
    {
        _frontier.Clear();
        if (!_background) return;
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (_frontier.Count > 200_000) return;
            foreach (var child in node.Children)
            {
                if (!child.IsContainer) continue;
                if (child.State == NodeState.Collapsed) _frontier.Enqueue(child);
                else pending.Push(child);
            }
        }
    }

    /// <summary>Queues a node's container children for background reading.</summary>
    private void Extend(Node node)
    {
        if (!_background || _frontier.Count > 200_000) return;
        foreach (var child in node.Children)
            if (child.IsContainer && child.State == NodeState.Collapsed) _frontier.Enqueue(child);
    }

    private Node ExpandPath(IReadOnlyList<int> path, out bool complete)
    {
        var node = _root!;
        complete = true;
        foreach (var id in path)
        {
            var next = FindChild(node, id);
            if (next is null) { complete = false; break; }
            node = next;
        }
        return node;
    }

    // Resolves a grouped block's members back into items, only when the block is opened.
    private static IReadOnlyList<DiskUsageItem> Resolve(int[] ids, Func<int, DiskUsageItem>? resolve, CancellationToken token)
    {
        if (resolve is null) return [];
        var items = new DiskUsageItem[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            items[i] = resolve(ids[i]);
        }
        return items;
    }

    private Node? FindChild(Node parent, int id)
    {
        if (parent.State != NodeState.Ready) return null;
        foreach (var child in parent.Children)
            if (child.Item.Id == id) return child;
        foreach (var child in parent.Children)
            if (child.IsGroup && child.State == NodeState.Ready && Array.IndexOf(child.Members!, id) >= 0)
                return FindChild(child, id);
        return null;
    }

    private async Task<Node?> FindChildAsync(Node parent, int id)
    {
        var generation = _generation;
        await StartLoad(parent);
        if (_disposed || generation != _generation || parent.State != NodeState.Ready) return null;
        foreach (var child in parent.Children)
            if (child.Item.Id == id) return child;
        if (parent.FirstGroup < parent.Children.Length)
        {
            // Membership can span millions of tiny files. Only the resulting
            // group reference comes back to the dispatcher.
            var token = generation.Token;
            Node? group;
            try
            {
                group = await Task.Run(() => parent.Children.FirstOrDefault(child =>
                {
                    token.ThrowIfCancellationRequested();
                    return child.IsGroup && Array.IndexOf(child.Members!, id) >= 0;
                }), token);
            }
            catch (OperationCanceledException) { return null; }
            if (_disposed || generation != _generation) return null;
            if (group is not null) return await FindChildAsync(group, id);
        }
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

    // CHANGED (round 16): once a test drives frames itself, the clock stops following wall time.
    // Cache ageing, fades and flights then advance by exactly the step the caller asked for, so a
    // slow machine can no longer age the geometry cache faster than the camera actually moves.
    private double Now => _manualClock ? _timeOffset : _clock.Elapsed.TotalSeconds + _timeOffset;
    private bool _manualClock;
    private void UseManualClock()
    {
        if (_manualClock) return;
        _timeOffset += _clock.Elapsed.TotalSeconds; // keep Now continuous across the switch
        _manualClock = true;
    }
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

    // NEW (round 42): colours that follow the zoom. As a folder grows from about a third of the view
    // (ApproachStart) to filling it (the 1.12 at which the camera enters it), its own colours blend in
    // over its parent's, in proportion to the zoom. Entering it then only hands over a scene that is
    // already showing those colours, and zooming back out runs the same blend in reverse. A step of
    // more than one level at once (a jump in the list, the back button) still uses the timed fade.
    private Node? _approachTarget;
    private double _approachP, _approachAt = double.NegativeInfinity;
    // CHANGED (round 43): the blend shown between the scene's two colour sets, 0 = its level, 1 = LevelB.
    private double _colorBlend;
    private const double ApproachStart = 3.2;   // camera width / the folder's fitted width where its colours begin
    private const double ApproachEase = .09;    // s: the shown blend chases the zoom this closely
    private bool ApproachEnabled => CanWalkInBackground;   // it needs twin scenes; the inline path stays simple

    private (Node? Target, double P) ComputeApproach()
    {
        var level = _colorLevel;
        if (level is null || level.State != NodeState.Ready || !ApproachEnabled) return (null, 0);
        var point = _focusPoint ?? Center(_camera);
        Node? child = null;
        foreach (var candidate in level.Children)
            if (candidate.IsContainer && candidate.Bounds.Contains(point)) { child = candidate; break; }
        if (child is null) return (null, 0);
        // Measured on a log scale, so the blend moves at the same rate for every wheel notch.
        var ratio = Math.Max(1e-12, _camera.Width / Fit(child).Width);
        var p = SmoothStep((Math.Log(ApproachStart) - Math.Log(ratio)) / (Math.Log(ApproachStart) - Math.Log(1.12)));
        return p <= .001 ? (null, 0) : (child, p);
    }

    // NEW (round 43): which two colour levels the next scene should carry.
    //  - Normally: the colour level, and the folder the zoom is heading into (or none).
    //  - Right after a step the zoom did not blend (a jump of more than one level, the back button): the
    //    colours that were on screen, fading to the new level over ColorFade.
    //  - While such a fade runs, it is kept until it has finished.
    // The colours on screen are always the ones the scene shows at its current blend, so the next scene
    // can start from exactly there.
    private (Node? Level, Node? LevelB, bool Timed) WantedColors()
    {
        var level = _colorLevel;
        var cache = _cache;
        if (cache is null || !ApproachEnabled) return (level, null, false);
        if (IsTimed(cache) && cache.LevelB == level && TimedBlend(cache) < 1) return (cache.Level, level, true);
        var shown = cache.LevelB is not null && _colorBlend >= .5 ? cache.LevelB : cache.Level;
        var near = shown == level || shown?.Parent == level || level?.Parent == shown || cache.LevelB == level;
        if (near) return (level, _approachTarget, false);
        return (shown, level, true);
    }

    private static bool IsTimed(SceneCache? cache) => cache is not null && !double.IsNaN(cache.FadeSince);
    private double TimedBlend(SceneCache cache) => SmoothStep((Now - cache.FadeSince) / ColorFade);

    // NEW (round 43): moves the shown blend toward where it should be this frame. A timed fade follows the
    // clock; otherwise it follows the zoom toward the folder the scene's second colours belong to, holds at
    // full once that folder has become the level (until the next scene takes over), and eases back to the
    // scene's own colours when neither applies.
    private void StepColorBlend()
    {
        var step = Math.Clamp(Now - _approachAt, 0, .1);
        _approachAt = Now;
        if (_cache is not { LevelB: { } levelB } cache) { _colorBlend = 0; return; }
        if (IsTimed(cache))
        {
            _colorBlend = TimedBlend(cache);
            if (_colorBlend < 1) _needsFrame = true;
            return;
        }
        var want = levelB == _colorLevel ? 1 : levelB == _approachTarget && cache.Level == _colorLevel ? _approachP : 0;
        _colorBlend += (want - _colorBlend) * (1 - Math.Exp(-step / ApproachEase));
        if (Math.Abs(want - _colorBlend) < .002) _colorBlend = want;
        else _needsFrame = true;
    }

    private bool StepFrame(double dt)
    {
        if (_root is null) return false;
        var now = Now;
        var moving = StepCamera(now, dt);
        _container = ComputeContainer();
        // CHANGED (round 34): the container follows the camera continuously, so a single zoom used to
        // walk through several levels and recolour the whole map at each one - which is what makes it
        // hard to keep track of where you are heading. A new level now has to hold still for a moment
        // before the colours follow it, so passing through a level on the way somewhere costs nothing
        // and only arriving somewhere changes the colours.
        if (_container != _colorLevel)
        {
            // NEW (round 42): one level in or out is already blended by the zoom, so it follows at once.
            var adjacent = ApproachEnabled && _colorLevel is not null
                && (_container.Parent == _colorLevel || _colorLevel.Parent == _container);
            if (adjacent)
            {
                _colorPrevious = _colorLevel;
                _colorLevel = _container;
                _colorSince = now;
                _colorPending = null;
            }
            else
            {
                if (_container != _colorPending) { _colorPending = _container; _colorPendingSince = now; }
                if (now - _colorPendingSince >= ColorSettle)
                {
                    _colorPrevious = _colorLevel;
                    _colorLevel = _container;
                    _colorSince = now;
                }
            }
        }
        else _colorPending = null;
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
        _navigationPath = PathOf(folder).ToArray();
        _highlightResolved = false;
        FolderFocused?.Invoke(folder.Item.Id);
    }

    // ================================================================= frame loop

    private void RequestFrame()
    {
        if (_disposed) return;
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
        if (_disposed || _hooked || !IsLoaded) return;
        _hooked = true;
        _lastRenderingTime = TimeSpan.MinValue;
        _lastFrame = Now;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopFrames()
    {
        _queuedFrame?.Abort();
        _queuedFrame = null;
        if (!_hooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _hooked = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _lastRenderingTime) return;
            _lastRenderingTime = rendering.RenderingTime;
        }
        // NEW (round 31): with the GPU path the whole scene costs about a millisecond, and this
        // callback is the moment in the frame where WPF has not yet taken the composition surface.
        // Handing the work to a background-priority dispatcher item put us on the far side of the
        // compositor, so D3DImage was usually already locked when we asked - which is exactly what
        // the deferred-frame counter was counting, and why a 1 ms scene took 22 ms to appear. The
        // queue stays for the CPU rasterizer, whose frame is heavy enough to starve input.
        if (_gpuShown && _queuedFrame is null && !_disposed) { RenderScheduledFrame(); return; }
        QueueFrame();
    }

    // Render notifications run ahead of Input in WPF. Coalesce expensive scene
    // work below input instead, so zooming cannot starve window dragging/resizing.
    internal void QueueFrame()
    {
        if (_disposed || _queuedFrame is not null) return;
        _queuedFrame = Dispatcher.InvokeAsync(() =>
        {
            _queuedFrame = null;
            if (!_disposed) RenderScheduledFrame();
        }, DispatcherPriority.Background);
    }

    private void RenderScheduledFrame()
    {
        ScheduledFrameCount++;
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
        // Prepare all layers together, after pending input, at full pixel resolution.
        if ((busy || moving || _sceneDirty || _needsFrame || !Near(_sceneCamera, _camera)) && !RenderScene()) return;
        if (busy || moving || _overlayDirty || _overlayAnimating) RenderOverlay();
        if (!busy && !moving && !_needsFrame && !_sceneDirty && !_overlayDirty && !_overlayAnimating)
            StopFrames();
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

    // Live layout and animation notifications share the coalesced frame queue.
    // Off-screen captures remain synchronous for deterministic image verification.
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        // CHANGED (round 5): the background lives here, untransformed, under the scene layer.
        drawingContext.DrawRectangle(_base, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (IsLoaded)
        {
            QueueFrame();
            return;
        }
        if (RenderScene()) RenderOverlay();
    }

    // NEW (round 5): rounded corners are clipped on the element, not inside the moving scene.
    private void UpdateClip()
    {
        var clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 10, 10);
        clip.Freeze();
        Clip = clip;
    }

    // ================================================================= scene layer

    private bool RenderScene()
    {
        if (_disposed) return false;
        if (!_gpuTried && _gpuEnabled && IsLoaded && PresentationSource.FromVisual(this) is not null)
        {
            _gpuTried = true;
            // The adapter is chosen from the window's monitor, so pass the handle.
            _gpu = GpuTileRenderer.TryCreate(PresentationSource.FromVisual(this) is HwndSource host ? host.Handle : IntPtr.Zero);
        }
        var gpu = _gpuEnabled && _gpu is { IsAvailable: true } available ? available : null;
        if (gpu is not null && !gpu.TryBeginFrame())
        {
            _gpuSkipped++;
            _needsFrame = true;
            // NEW (round 16): nothing below runs on a deferred frame, so when the frame loop is not
            // already running this is the only chance to come back for it. Without this, a map repainted
            // while the compositor held the surface would stay stale until the next input.
            if (!_hooked) Dispatcher.InvokeAsync(RequestFrame, DispatcherPriority.Background);
            return false;
        }
        try { RenderSceneCore(); return true; }
        finally { gpu?.EndFrame(); }
    }

    private void RenderSceneCore()
    {
        if (_disposed) return;
        PreparedFrameCount++;
        if (!_statClock.IsRunning) _statClock.Start();
        var sceneStart = _statClock.Elapsed.TotalMilliseconds;
        _sceneDirty = false;
        _needsFrame = false;
        _view = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));
        _viewLoose = Rect.Inflate(_view, 2, 2);
        _sceneCamera = _camera;
        _textBudget = IsAnimating || _dragging ? 8 : TextPerFrame;
        if (ActualWidth < 1 || ActualHeight < 1) return;
        // NEW (round 38): background detail is folded in only while the camera is still, at most once
        // every QuietRebuild seconds, so reading ahead never costs a rebuild during a gesture.
        if (IsAnimating || _dragging) _movedAt = Now;
        else if (_quietDetail && Now - _movedAt >= BackgroundRest && Now - _cacheSince >= QuietRebuild)
        {
            _quietDetail = false;
            _detailRevision++;
        }
        EnsureBuilt();
        if (_root is not null) EnsureFlat(_root.Bounds.Width);   // NEW (round 39): cheap when already current
        _container ??= _root is null ? null : ComputeContainer();
        _colorLevel ??= _container;
        var viewport = new Size(ActualWidth, ActualHeight);
        (_approachTarget, _approachP) = ComputeApproach();
        var coverage = _cache is null ? Rect.Empty : BatchFor(_cache).Transform(_cache.Coverage);
        // CHANGED (round 19): scale comes from the batch, so it accounts for a resized viewport as
        // well as a moved camera, and a resize no longer hard-invalidates every frame. Only a change
        // of aspect ratio can't be expressed by the single-scale mapping.
        var scale = _cache is null ? 1 : BatchFor(_cache).Scale;
        var aspectChange = _cache is not null &&
            Math.Abs(_cache.Viewport.Width * viewport.Height / (_cache.Viewport.Height * viewport.Width) - 1) > .01;
        var hardChange = _cache is null || aspectChange || !coverage.Contains(_view);
        // CHANGED (round 24): the window was wide because the tree walk cost 10 ms; it costs 0.3 ms
        // now, so geometry can be rebuilt far sooner. Tiles and their gutters are never stretched
        // more than about a quarter, which stops the lattice breathing as the camera moves.
        // CHANGED (round 42): "render everything" used the same window as the rest again (.45-2.4 before).
        // Gaps between blocks are sized in pixels when a scene is built, so a scene stretched 2.4 times
        // had gaps twice as wide as the next one, and every rebuild made the whole lattice jump - the
        // shaking while zooming. Rebuilds are off the UI thread now, so rebuilding sooner costs nothing
        // you can see, and a quarter is too little for a gap to visibly change.
        var zoomChange = scale < .78 || scale > 1.3;
        // CHANGED (round 43): the colour levels the scene should carry, against the ones it does.
        var (wantLevel, wantLevelB, timedFade) = WantedColors();
        var levelChange = _cache is not null && (_cache.Level != wantLevel || _cache.LevelB != wantLevelB || timedFade != IsTimed(_cache));
        var approachChange = levelChange && !timedFade;   // a zoom-driven change: rebuilt at once
        var detailChange = _cache is not null && (_cache.Revision != _detailRevision || levelChange || zoomChange);
        // CHANGED (round 21): every folder that finished loading bumped the revision and bought its
        // own cross-fade, so a burst of loads read as continuous flickering. A rebuild driven only by
        // finished loads now waits for them to settle and arrives as one redraw.
        var gate = approachChange ? 0 : detailChange && !levelChange && !zoomChange ? LoadSettle : _fadeLength;
        // With no tree there is nothing to build; the held picture stays up until one arrives.
        if (_root is not null && (hardChange || (detailChange && Now - _cacheSince >= gate)))
        {
            // CHANGED (round 40): built on the thread pool whenever there is already a picture to keep
            // showing. Only one build runs at a time; if another is wanted when it lands, this same
            // check asks for it on the next frame.
            if (_cache is not null && CanWalkInBackground)
            {
                if (_walk is null) StartWalk(viewport, wantLevel, wantLevelB, timedFade);
            }
            else if (_walk is null) InstallCache(BuildGeometryCache(viewport));   // never beside a running walk
        }
        else if (_cache is not null) GeometryReuseCount++;
        // A source that never arrives must not leave a stale picture up for good.
        if (_previousCache is { Detached: true } && Now - _holdoverSince > HoldoverLimit) _previousCache = null;
        // NEW (round 24): a cross-fade shows two copies of the same scene at different scales at once,
        // so every edge is doubled and slightly offset - which is precisely the shimmer you see while
        // zooming. A moving camera swaps outright: mid-gesture the eye never catches the change. The
        // fade is kept for a still camera, where a detail change would otherwise pop, and for a
        // replaced source, where the two pictures are unrelated and there is nothing to double.
        if ((IsAnimating || _dragging) && _previousCache is { Detached: false }) _previousCache = null;
        var blend = _cache is null ? 0 : _previousCache is null ? 1 : SmoothStep((Now - _cacheSince) / _fadeLength);
        if (blend >= 1) _previousCache = null;
        // NEW (round 43): the mix between the scene's two colour sets for this frame.
        StepColorBlend();
        _batchCount = 0;
        if (_previousCache is { } old) _batches[_batchCount++] = BatchFor(old);
        if (_cache is { } current) _batches[_batchCount++] = BatchFor(current, blend) with { ColorBlend = _colorBlend };
        if (_batchCount < 2) _batches[1] = default;
        _needsFrame |= blend < 1 || detailChange || _quietDetail;   // CHANGED (round 38): so held detail lands
        _view = new Rect(0, 0, ActualWidth, ActualHeight);
        _viewLoose = Rect.Inflate(_view, 2, 2);
        // REMOVED (round 33): this walked every cached tile on every frame to maintain a flat list for
        // hit testing and prefetch. At a few thousand blocks that cost a millisecond; with "render
        // everything" it is hundreds of thousands of rectangles per frame, and it was the whole reason
        // that mode did not feel smooth. Nothing here needed to be per-frame: the cache already holds
        // the tiles, hit testing maps the pointer into cache space on demand, and prefetch is decided
        // while the geometry is built. Per-frame cost is now independent of how many blocks are drawn.
        var labelsStart = _statClock.Elapsed.TotalMilliseconds;
        LastWalkMilliseconds = labelsStart - sceneStart;
        using (var dc = _labels.RenderOpen())
        {
            if (_previousCache is { } prior) DrawCachedLabels(dc, prior, 1 - blend);
            if (_cache is { } latest) DrawCachedLabels(dc, latest, blend, _colorBlend);
            if (_root is not null) DrawVeil(dc);
        }
        if (_showStats) Smooth(ref _statLabels, _statClock.Elapsed.TotalMilliseconds - labelsStart);
        if (!PresentGpu())
        {
            PrepareFallbackCommands();
            Present(EnsureSurface(_full, MaxPixels));
        }
        PumpLoads();
        TrimTextCache(force: false);
        TrimNodeTree();
        ReleaseUnusedGeometry();   // NEW (round 41)
        if (_showStats)
        {
            Smooth(ref _statWalk, LastWalkMilliseconds);
            _statWorstWalk = Math.Max(LastWalkMilliseconds, _statWorstWalk * .985);
            Smooth(ref _statScene, _statClock.Elapsed.TotalMilliseconds - sceneStart);
            _statTiles = RenderedTileCount;
        }
        if (_needsFrame && !_hooked) Dispatcher.InvokeAsync(RequestFrame, DispatcherPriority.Background);
    }

    // CHANGED (round 40): what used to happen inline after a build, now shared by the inline and the
    // background path. The change of level, of aspect and the coverage are judged at the moment the
    // scene is installed, against whatever is on screen then.
    // CHANGED (round 40): what used to happen inline after a build, now shared by the inline and the
    // background path.
    // CHANGED (round 43): colour changes no longer cross-fade two scenes; the new scene starts its blend
    // where the colours on screen are, so installing it changes nothing you can see. Only a held picture
    // from a replaced source is still cross-faded.
    private void InstallCache(SceneCache built)
    {
        _pooledGeometry.Add(built.Geometry);   // NEW (round 41): returned to the pool once off screen
        var before = _cache;
        _colorBlend = ContinuedBlend(before, _colorBlend, built);
        var previous = _cache ?? _previousCache;
        _cache = built;
        _previousCache = previous is { Detached: true } ? previous : null;
        _fadeLength = _previousCache is { Detached: true } ? SourceFade : CacheFade;
        _cacheSince = Now;
    }

    // Where the new scene's blend has to start for the colours on screen not to move. The old scene
    // showed its Level at 1 - t and its LevelB at t; the new one shows its Level at 1 - t' and its
    // LevelB at t'. Whichever level they share decides t'.
    private static double ContinuedBlend(SceneCache? before, double t, SceneCache built)
    {
        if (before is null || built.LevelB is null) return 0;
        if (built.Level == before.Level && built.LevelB == before.LevelB) return t;   // timed: StepColorBlend follows its clock
        if (built.Level == before.LevelB) return 0;                              // entered: B has become the level
        if (built.LevelB == before.Level) return before.LevelB is null ? 1 : 1 - t; // zoomed out: the old level is now B
        if (built.Level == before.Level) return 0;                               // a different folder ahead
        return 0;
    }

    // NEW (round 40): background builds only in a live window on the real clock. Tests and off-screen
    // captures step time by hand and expect the scene to exist as soon as a frame has rendered.
    private SceneWalk? _walk;
    private int _sceneEpoch;
    private bool _walkFailed;
    private double _lastBuildMilliseconds;
    private bool CanWalkInBackground => !_manualClock && !_walkFailed && IsLoaded && PresentationSource.FromVisual(this) is not null;

    // CHANGED (round 43): one walk carrying both colour sets; no twins.
    private void StartWalk(Size viewport, Node? level, Node? levelB, bool timed)
    {
        var walk = _walk = PrepareWalk(viewport, level, levelB);
        var root = _root!;
        var epoch = _sceneEpoch;
        var gpu = _gpuShown ? _gpu : null;
        lock (WalkingRoots) WalkingRoots.Add(root);
        Task.Run(() =>
        {
            try
            {
                var built = walk.Run();
                // The vertex buffer is filled here too, so the UI thread only has to draw it.
                gpu?.Prepare(built.Geometry);
                return built;
            }
            finally
            {
                lock (WalkingRoots) WalkingRoots.Remove(root);
            }
        }).ContinueWith(task => Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(_walk, walk)) _walk = null;
            if (task.Status != TaskStatus.RanToCompletion)
            {
                // Something the walk read changed under it in a way it could not survive. Build inline
                // from now on rather than failing the same way every frame.
                _ = task.Exception;
                _walkFailed = true;
                RequestFrame();
                return;
            }
            var built = task.Result;
            // A tree replaced while it ran (new source, refresh, resize) makes the result stale: it is
            // dropped, and the next frame asks for one of the current tree. The epoch catches the same
            // tree coming back from the tree cache after being released in between.
            if (_disposed || !ReferenceEquals(root, _root) || epoch != _sceneEpoch)
            {
                if (!_disposed) DropGeometry(built.Geometry);
                gpu?.Forget(built.Geometry);
                if (!_disposed) RequestFrame();
                return;
            }
            // A timed fade keeps its clock across rebuilds of the same fade (a zoom during it), so it is
            // not restarted by them.
            if (timed && built.LevelB is not null)
                built.FadeSince = IsTimed(_cache) && _cache!.Level == built.Level && _cache.LevelB == built.LevelB ? _cache.FadeSince : Now;
            InstallCache(built);
            AdoptWalk(walk);
            // Back to back walks during a long gesture would otherwise never leave a gap for the sweep.
            TrimNodeTree();
            _needsFrame = true;
            RequestFrame();
        }, DispatcherPriority.Render), TaskScheduler.Default);
    }

    private TileBatch BatchFor(SceneCache cache, double opacity = 1)
        => TileBatch.ForCamera(cache.Geometry, cache.Camera, cache.Viewport,
            cache.Detached ? cache.Camera : _camera, new Size(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)), opacity);


    // NEW (round 40): one geometry build, self-contained so it can run off the UI thread.
    //
    // Rebuilding the scene - the walk over every block on screen and the vertex buffer it becomes -
    // was the freeze: with "render everything" it is several hundred thousand blocks, 100 ms or more
    // of walk plus tens of megabytes of vertices, all on the UI thread, every time a zoom or pan left
    // the range the last build covered. The walk now takes a snapshot of everything it reads when it
    // is prepared, writes only into its own buffers, and runs on the thread pool; the GPU buffer is
    // filled there too (the device is created multithreaded). Meanwhile the previous scene keeps
    // being drawn, stretched to the moving camera exactly as it is between rebuilds anyway, and the
    // new one is swapped in the moment it is ready.
    //
    // The methods below are the control's own walk, moved here unchanged except that the fields
    // they used now belong to the walk. Nodes are still shared with the UI thread: the walk only
    // reads the tree, writes LastDrawn, the colour cache and the flat index on nodes (fields nothing
    // else writes while a walk is running), and the node sweep waits for it to finish.
    private sealed class SceneWalk
    {
        // Inputs, captured on the UI thread.
        public required Node Root;
        public required Node? ColorLevel;
        public required Node? ColorLevelB;   // CHANGED (round 43): the second colour set; null for one
        public required Rect Camera;
        public required Size Viewport;
        public required int Budget;
        public required double MinimumTile, ExpandLow, ExpandHigh, Prefetch;
        public required bool CanRequestLoads, Everything, LowDetail;
        public required uint BasePacked;
        public required FlatTreemapLayout? Flat;
        public required int FlatStamp;
        public required double Stamp;          // the control's clock when prepared; stamped on drawn nodes
        public required int Revision;
        public required HashSet<Node> OpenPath;

        // Outputs.
        public required TileCommand[] Commands;
        public int CommandCount;
        public int Tiles;
        public List<(Node Node, Rect Screen)> Drawn = [];
        public readonly List<Caption> Deferred = [];
        public readonly List<Caption> DeferredB = [];   // NEW (round 43): labels under ColorLevelB
        public readonly List<(Node Node, double Area)> Wanted = [];
        public double Milliseconds;
        private readonly Dictionary<Node, HashSet<Node>> PathSets = [];
        private Rect View, ViewLoose;

        public SceneCache Run()
        {
            var clock = Stopwatch.StartNew();
            // Overscan lets ordinary pans/zooms reuse geometry that was just outside
            // the viewport. Two 12,000-tile scenes bound CPU and GPU cache memory.
            // CHANGED (round 18): 35% overscan keeps the cached layer covering the viewport down to a
            // scale of about .59, just past the .62 where a rebuild is wanted anyway. Zooming out no
            // longer runs off the edge of its geometry and forces an ungated rebuild every frame.
            var coverage = Rect.Inflate(new Rect(Viewport), Viewport.Width * .35, Viewport.Height * .35);
            View = coverage;
            ViewLoose = Rect.Inflate(coverage, 2, 2);
            if (Root is { Item.Bytes: > 0 }) DrawNode(Root, ToScreen(Root.Bounds), 1, 1, 1, Rect.Empty, Budget, BasePacked, BasePacked, 0, true);
            // CHANGED (round 41): the geometry takes the working buffer itself rather than a copy of it.
            var cache = new SceneCache(Camera, Viewport, coverage, new TileGeometry(Commands, CommandCount),
                Drawn, Deferred.ToArray(), Deferred.ToDictionary(c => (c.Node, c.IsOpen)), ColorLevel, Revision)
            {
                LevelB = ColorLevelB,
                CaptionsB = ColorLevelB is null ? null : DeferredB.ToArray(),
                LabelIndexB = ColorLevelB is null ? null : DeferredB.ToDictionary(c => (c.Node, c.IsOpen)),
            };
            Milliseconds = clock.Elapsed.TotalMilliseconds;
            return cache;
        }

        private Rect ToScreen(Rect world)
        {
            var scaleX = Viewport.Width / Camera.Width;
            var scaleY = Viewport.Height / Camera.Height;
            return new Rect((world.X - Camera.X) * scaleX, (world.Y - Camera.Y) * scaleY, world.Width * scaleX, world.Height * scaleY);
        }

        // alpha: accumulated fade of this tile. labelWeight: 1 when this tile's parent is the level
        // being labeled (0..1 while cross-fading between levels). zone: an ancestor's name tag.
        // Children share the subtree budget by visible area, independently of their
        // siblings' expansion state. A block with no detail budget remains solid.
        // Returns the number of tiles used.
        // CHANGED (round 43): every block carries two colours - under the colour level (A) and under a
        // second level (B): the folder the zoom is heading into, or the level a jump came from. The GPU
        // mixes them every frame, around the colour wheel, so one scene serves a whole colour change.
        private int DrawNode(Node node, Rect screen, double alpha, double labelWeight, double labelWeightB, Rect zone, int budget,
            uint backdrop, uint backdropB, double gutter, bool onPath)
        {
            if (!screen.IntersectsWith(ViewLoose)) return 0;
            var min = Math.Min(screen.Width, screen.Height);
            var isRoot = node == Root;
            if (!isRoot && (Tiles >= Budget || min < MinimumTile))
            {
                EmitSolid(screen, backdrop, backdropB);
                return 0;
            }
            // Detail fades through its allowance instead of switching on/off at an
            // integer threshold. Sibling budgets are independent of expansion state.
            // CHANGED (round 34): whether a node is on the open path is carried down the recursion. It was
            // two hash-set lookups per node, and only a handful of nodes can ever be on the path.
            var detail = isRoot || Everything || onPath ? 1 : DetailAmount(budget, node.Children.Length);
            Tiles++;
            // CHANGED (round 25): the gutter is handed down by the folder, so every block inside it insets
            // by the same amount and every gap between them is the same width. Deriving it from each
            // block's own size meant a big block and a small one met with two different half-gaps, which is
            // what made the grid look hand-drawn.
            // CHANGED (round 26): the gutter can never take more than a third of a block. Between MinTile
            // and twice the gutter it used to consume the block entirely, and the degenerate result was
            // returned unpainted - so the base colour showed through as a black notch. That was the black.
            // CHANGED (round 29): a third of a block was far too much to give up. A 4 px block lost 30% of
            // its width to gaps, which is why small tiles read as hard and chopped-up. At an eighth the gap
            // falls below a pixel as blocks get small, and a sub-pixel line antialiases into a hairline -
            // which is the softness, rather than a hard edge scaled down.
            var tile = isRoot ? screen : Deflate(screen, Math.Min(gutter, min * .12));
            var drawn = Rect.Intersect(tile, ViewLoose);
            if (drawn.IsEmpty || drawn.Width <= 0 || drawn.Height <= 0)
            {
                EmitSolid(screen, backdrop, backdropB);   // belt and braces: never leave a region unpainted
                return 0;
            }
            var visible = Rect.Intersect(tile, View);
            var hasVisible = !visible.IsEmpty && visible.Width > 1 && visible.Height > 1;
            Drawn.Add((node, drawn));
            node.LastDrawn = Stamp;   // stamped while the geometry is built, which is the only time it changes
            // CHANGED (round 39): moving never prefetches smaller than at rest. With "render everything"
            // ExpandHigh is 1.5 px, so every zoom loaded Nodes down to that size and rebuilt mid-gesture.
            var prefetch = Prefetch;   // CHANGED (round 40): decided once, when the walk is prepared
            // CHANGED (round 38): background loads no longer count here - with them filling every slot,
            // on-screen folders were never even asked for, so zooming in revealed nothing until they drained.
            if (CanRequestLoads && budget > 2 && node.State == NodeState.Collapsed && min >= prefetch)
                Wanted.Add((node, drawn.Width * drawn.Height));

            var color = isRoot ? BasePacked : ColorUnder(node, ColorLevel!);
            var colorB = isRoot || ColorLevelB is not { } levelB ? color : ColorUnder(node, levelB);
            var open = detail * OpenAmount(node, onPath, min);
            // NEW (round 39): a folder with no Nodes loaded is opened from the flat layout instead, at
            // exactly the size it would open at if it had them.
            var flatEntry = -1;
            if (open <= 0 && !isRoot && Flat is { } flat && node.IsContainer && node.State != NodeState.Ready && !LowDetail)
            {
                var entry = FlatIndexOf(node, flat);
                if (entry >= 0 && flat.IsContainer(entry))
                {
                    open = onPath ? 1 : SmoothStep((min - ExpandLow) / (ExpandHigh - ExpandLow));
                    if (open > 0) flatEntry = entry;
                }
            }
            // An open folder becomes a frame of its own hue behind its contents: dark for the labeled
            // blocks (structure you read), barely darker deeper down (texture you zoom into), so
            // children too small to draw blend into it instead of showing as dark gaps.
            // CHANGED (round 27): a folder more than one level down barely darkened at all (.8), so its
            // frame vanished and everything below the second level read as one undifferentiated field.
            // Every level now recesses, just less sharply the deeper it sits.
            var shallow = ColorLevel is null || node.Depth - ColorLevel.Depth <= 1;
            // CHANGED (round 29): .42 made an open folder's surface 42% of its hue, and the seam on top of
            // that came out at 36% - almost black, so every gap read as a hard cut rather than as shading.
            // A surface a little over half the brightness of its contents separates them without the weight.
            var fill = open > 0 && !isRoot ? Mix(color, Shade(color, shallow ? .55 : .72), open) : color;
            // Flatten alpha against the parent's color once. Cached rectangles form
            // a partition rather than repeatedly painting over their ancestors.
            // This cuts fill work and permits a true old/new scene cross-fade.
            fill = Mix(backdrop, fill, isRoot ? 1 : alpha);
            var shallowB = ColorLevelB is null ? shallow : node.Depth - ColorLevelB.Depth <= 1;
            var fillB = open > 0 && !isRoot ? Mix(colorB, Shade(colorB, shallowB ? .55 : .72), open) : colorB;
            fillB = Mix(backdropB, fillB, isRoot ? 1 : alpha);
            // Collapsed label (fades out as the folder opens). Deferred, so drawing order is unaffected.
            if (!isRoot && open < 1 && labelWeight > 0 && hasVisible)
            {
                var labelAlpha = alpha * labelWeight * (1 - open);
                Deferred.Add(new Caption(node, visible, zone, labelAlpha));
            }
            if (ColorLevelB is not null && !isRoot && open < 1 && labelWeightB > 0 && hasVisible)
                DeferredB.Add(new Caption(node, visible, zone, alpha * labelWeightB * (1 - open)));

            if (open > 0)
            {
                // CHANGED (round 28): a folder paints its whole surface once, and its children each paint a
                // single inset rectangle on top. What shows between them is that surface, so the seam needs
                // no rectangles of its own. This replaced four border strips plus a fill per block with one
                // rectangle per block - five times fewer - which is what pays for the extra detail below,
                // and makes an unpainted region impossible: the surface is always underneath.
                var inner = Deflate(tile, Math.Clamp(min * .005, .5, 2));
                if (inner.Width <= 0 || inner.Height <= 0) inner = tile;
                EmitSolid(tile, Shade(fill, FrameShade), Shade(fillB, FrameShade));
                // Folder tags stay readable even when their geometry spans the view.
                var showPill = !isRoot && labelWeight > 0 && hasVisible && !onPath;
                var childZone = Rect.Empty;
                var showPillB = ColorLevelB is not null && !isRoot && labelWeightB > 0 && hasVisible && !onPath;
                var childLabels = LabelWeight(node, ColorLevel);
                var childLabelsB = ColorLevelB is null ? childLabels : LabelWeight(node, ColorLevelB);
                var childAlpha = alpha * open;
                if (flatEntry >= 0)
                {
                    // NEW (round 39): the same gutter and the same recursion rules, read from the arrays.
                    var flatGutter = Math.Clamp(Math.Min(inner.Width, inner.Height) * .0025, .35, .9);
                    var tint = TintBelow(node, ColorLevel!);
                    var spent = DrawFlatChildren(Flat!, flatEntry, inner, childAlpha, fill, fillB, flatGutter,
                        tint, ColorLevelB is null ? tint : TintBelow(node, ColorLevelB), node.Depth + 1);
                    if (showPill) Deferred.Add(new Caption(node, visible, zone, alpha * labelWeight * open, true, Unpack(color)));
                    if (showPillB) DeferredB.Add(new Caption(node, visible, zone, alpha * labelWeightB * open, true, Unpack(colorB)));
                    return 1 + spent;
                }
                var children = node.Children;
                // Pooled: with fractal depth many folders are open in every frame.
                var screens = ArrayPool<Rect>.Shared.Rent(children.Length);
                var areas = ArrayPool<double>.Shared.Rent(children.Length);
                var visited = ArrayPool<int>.Shared.Rent(children.Length);
                var visitedCount = 0;
                var areaLeft = 0d;
                // This traversal runs only when constructing a new cached detail layer.
                var sx = inner.Width / node.Bounds.Width;
                var sy = inner.Height / node.Bounds.Height;
                double ox = node.Bounds.X, oy = node.Bounds.Y;
                // One gutter for every block in this folder, so all its gaps match.
                var childGutter = Math.Clamp(Math.Min(inner.Width, inner.Height) * .0025, .35, .9);
                for (var i = 0; i < children.Length; i++)
                {
                    var child = children[i];
                    // Each edge is mapped from the layout coordinate it shares with its neighbour, rather
                    // than from a position plus a separately scaled width. Two touching blocks then land on
                    // exactly the same pixel instead of a fraction apart, which is the other half of the
                    // misalignment: gaps that looked a pixel wider on one side than the other.
                    var left = inner.X + (child.Bounds.X - ox) * sx;
                    var right = inner.X + (child.Bounds.Right - ox) * sx;
                    var top = inner.Y + (child.Bounds.Y - oy) * sy;
                    var bottom = inner.Y + (child.Bounds.Bottom - oy) * sy;
                    var rect = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
                    if (!rect.IntersectsWith(ViewLoose)) continue;
                    var shown = Rect.Intersect(rect, ViewLoose);
                    screens[visitedCount] = rect;
                    areas[visitedCount] = shown.Width * shown.Height;
                    visited[visitedCount++] = i;
                    areaLeft += shown.Width * shown.Height;
                }
                var extra = Math.Max(0, budget - 1 - visitedCount);
                var used = 1;
                for (var k = 0; k < visitedCount; k++)
                {
                    // NEW (round 26): a child with no measurable on-screen area used to be skipped, which
                    // left its region unpainted - and what shows through an unpainted region is the base
                    // colour, which is why a stray block came out black. Its area is now filled by the
                    // folder, so every part of the folder is painted exactly once whatever happens.
                    if (areas[k] <= 0) continue;   // the folder's surface already covers it
                    var share = 1 + (areaLeft <= 0 ? 0 : (int)(extra * areas[k] / areaLeft));
                    var child = children[visited[k]];
                    var spent = DrawNode(child, screens[k], childAlpha, childLabels, childLabelsB, childZone, share, fill, fillB, childGutter,
                        onPath && OpenPath.Contains(child));   // only ever true for one child of an open-path node
                    used += spent;
                }
                ArrayPool<Rect>.Shared.Return(screens);
                ArrayPool<double>.Shared.Return(areas);
                ArrayPool<int>.Shared.Return(visited);
                if (showPill) Deferred.Add(new Caption(node, visible, zone, alpha * labelWeight * open, true, Unpack(color)));
                if (showPillB) DeferredB.Add(new Caption(node, visible, zone, alpha * labelWeightB * open, true, Unpack(colorB)));
                return used;
            }
            // One rectangle. Its gap to its neighbours is the folder's surface showing through from
            // underneath, so nothing needs to be drawn for the seam itself.
            EmitSolid(isRoot ? screen : tile, fill, fillB);
            return 1;
        }
        private FlatTint TintBelow(Node node, Node level)
        {
            if (OnPath(node, level)) return new FlatTint(0, 0, true, PaletteSlot(node));
            var anchor = node;
            var depth = 0;
            while (anchor.Parent is not null && !OnPath(anchor.Parent, level)) { anchor = anchor.Parent; depth++; }
            var hue = anchor.IsGroup ? GroupColor : BranchColors[PaletteSlot(anchor)];
            return new FlatTint(hue, depth, false, 0);
        }
        // Children of one flat entry inside its parent's content rectangle. Mirrors the child loop in
        // DrawNode: edges mapped individually so neighbours meet on one pixel, anything with no area on
        // screen skipped because the parent's surface already covers it.
        private int DrawFlatChildren(FlatTreemapLayout flat, int parent, Rect inner, double alpha, uint backdrop, uint backdropB,
            double gutter, FlatTint tint, FlatTint tintB, int depth)
        {
            var used = 0;
            var end = flat.End(parent);
            var index = 0;
            for (var child = parent + 1; child < end; child = flat.End(child), index++)
            {
                flat.Edges(child, out var l, out var t, out var r, out var b);
                var left = inner.X + l * inner.Width;
                var right = inner.X + r * inner.Width;
                var top = inner.Y + t * inner.Height;
                var bottom = inner.Y + b * inner.Height;
                var rect = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
                if (!rect.IntersectsWith(ViewLoose)) continue;
                var shown = Rect.Intersect(rect, ViewLoose);
                if (shown.Width * shown.Height <= 0) continue;
                used += DrawFlat(flat, child, rect, alpha, backdrop, backdropB, gutter,
                    ChildTint(tint, flat, child, index), ChildTint(tintB, flat, child, index), depth);
            }
            return used;
        }
        // DrawNode for an entry with no Node: no labels (only a level's direct children are labelled, and
        // those are always Nodes), no hit-testing record, no load request - just the geometry.
        // CHANGED (round 43): both colours, as DrawNode.
        private int DrawFlat(FlatTreemapLayout flat, int entry, Rect screen, double alpha, uint backdrop, uint backdropB, double gutter,
            FlatTint tint, FlatTint tintB, int depth)
        {
            var min = Math.Min(screen.Width, screen.Height);
            if (Tiles >= Budget || min < MinimumTile)
            {
                EmitSolid(screen, backdrop, backdropB);
                return 0;
            }
            Tiles++;
            var tile = Deflate(screen, Math.Min(gutter, min * .12));
            var drawn = Rect.Intersect(tile, ViewLoose);
            if (drawn.IsEmpty || drawn.Width <= 0 || drawn.Height <= 0)
            {
                EmitSolid(screen, backdrop, backdropB);
                return 0;
            }
            var spread = flat.Spread(entry);
            var color = FlatColor(tint, spread);
            var colorB = FlatColor(tintB, spread);
            var open = flat.IsContainer(entry) ? SmoothStep((min - ExpandLow) / (ExpandHigh - ExpandLow)) : 0;
            var shallow = ColorLevel is null || depth - ColorLevel.Depth <= 1;
            var shallowB = ColorLevelB is null ? shallow : depth - ColorLevelB.Depth <= 1;
            var fill = Mix(backdrop, open > 0 ? Mix(color, Shade(color, shallow ? .55 : .72), open) : color, alpha);
            var fillB = Mix(backdropB, open > 0 ? Mix(colorB, Shade(colorB, shallowB ? .55 : .72), open) : colorB, alpha);
            if (open <= 0)
            {
                EmitSolid(tile, fill, fillB);
                return 1;
            }
            var inner = Deflate(tile, Math.Clamp(min * .005, .5, 2));
            if (inner.Width <= 0 || inner.Height <= 0) inner = tile;
            // A folder whose contents fit in less than a pixel is one block: its children could only ever
            // be a spray of sub-pixel rectangles averaging to the same colour, and with a million-file
            // drive in view those would be most of the geometry.
            if (inner.Width * inner.Height < 1)
            {
                EmitSolid(tile, fill, fillB);
                return 1;
            }
            EmitSolid(tile, Shade(fill, FrameShade), Shade(fillB, FrameShade));
            var childGutter = Math.Clamp(Math.Min(inner.Width, inner.Height) * .0025, .35, .9);
            return 1 + DrawFlatChildren(flat, entry, inner, alpha * open, fill, fillB, childGutter, tint, tintB, depth + 1);
        }
        // Finds a Node's entry by walking down from the root; each folder resolves all of its children at
        // once and caches them, so this is paid once per folder per layout. Ids are checked on the way,
        // so a tree and a layout that somehow disagree fall back to solid blocks instead of wrong ones.
        private int FlatIndexOf(Node node, FlatTreemapLayout flat)
        {
            if (node.FlatStamp == FlatStamp) return node.Flat;
            if (node.Parent is not { } parent)
            {
                node.Flat = flat.Count > 0 && flat.Id(0) == node.Item.Id ? 0 : -2;
                node.FlatStamp = FlatStamp;
                return node.Flat;
            }
            var parentEntry = FlatIndexOf(parent, flat);
            var siblings = parent.Children;
            var end = parentEntry >= 0 ? flat.End(parentEntry) : 0;
            var entry = parentEntry >= 0 && flat.IsContainer(parentEntry) ? parentEntry + 1 : end;
            var matched = true;
            for (var i = 0; i < siblings.Length; i++)
            {
                if (matched && entry < end && flat.Id(entry) == siblings[i].Item.Id)
                {
                    siblings[i].Flat = entry;
                    entry = flat.End(entry);
                }
                else
                {
                    matched = false;
                    siblings[i].Flat = -2;
                }
                siblings[i].FlatStamp = FlatStamp;
            }
            if (!matched || entry != end)
                foreach (var sibling in siblings) sibling.Flat = -2;
            if (node.FlatStamp != FlatStamp) { node.Flat = -2; node.FlatStamp = FlatStamp; }   // no longer a child
            return node.Flat;
        }
        private void EmitSolid(Rect rect, uint color, uint colorB)
        {
            rect.Intersect(ViewLoose);
            if (!rect.IsEmpty) Emit(rect, color, colorB, 1);
        }
        private double OpenAmount(Node node, bool onPath, double min)
        {
            if (node.State != NodeState.Ready) return 0;
            // NEW (round 18): low detail mode opens only the folders you are inside. Everything within
            // them stays a solid block, so a frame walks one level instead of the whole subtree.
            if (LowDetail && !onPath) return 0;
            var zoom = onPath ? 1 : SmoothStep((min - ExpandLow) / (ExpandHigh - ExpandLow));
            if (zoom <= 0) return 0;
            return zoom; // newly loaded detail fades in as a complete cached layer
        }
        // Fades 0..1 as a parent's children gain or lose their labels when the level changes.
        // CHANGED (round 43): per level; each of the two label sets has its own.
        private double LabelWeight(Node parent, Node? level) => OnPath(parent, level) ? 1d : 0d;
        private uint ColorUnder(Node node, Node level)
        {
            if (node.ColorKeyA == level) return node.ColorA;
            if (node.ColorKeyB == level) return node.ColorB;
            var color = ComputeColor(node, level);
            var keepA = node.ColorKeyA is not null && (node.ColorKeyA == ColorLevel || node.ColorKeyA == ColorLevelB);
            if (keepA) { node.ColorKeyB = level; node.ColorB = color; }
            else { node.ColorKeyA = level; node.ColorA = color; }
            return color;
        }
        private bool OnPath(Node node, Node? level)
        {
            if (level is null) return false;
            if (!PathSets.TryGetValue(level, out var set))
            {
                if (PathSets.Count > 8) PathSets.Clear();
                set = [];
                for (var current = level; current is not null; current = current.Parent) set.Add(current);
                PathSets[level] = set;
            }
            return set.Contains(node);
        }
        // Each item directly inside the level (the "anchor") has its own hue; everything inside it
        // shares that hue, a little darker per level and varied between siblings.
        private uint ComputeColor(Node node, Node level)
        {
            var anchor = node;
            var depth = 0;
            while (anchor.Parent is not null && !OnPath(anchor.Parent, level)) { anchor = anchor.Parent; depth++; }
            if (anchor.Parent is null) return BasePacked;
            var hue = anchor.IsGroup ? GroupColor : BranchColors[PaletteSlot(anchor)];   // CHANGED (round 43)
            if (depth == 0) return hue;
            // CHANGED (round 18): the old .05-per-index step varied siblings by at most 10% and ramped in
            // size order, so a folder's contents fused into one flat field. Value now comes from a hash of
            // the name across a wide range: neighbours speckle instead of ramping, and the block still
            // reads as one hue. Depth keeps nested folders recessed.
            // CHANGED (round 27): the spread was +/-20%, which reads as noise rather than texture. A
            // narrower band keeps blocks individually visible while the folder still reads as one colour.
            return Shade(hue, (1 - .05 * Math.Min(depth, 3)) * (.82 + .24 * Spread(node)));
        }
        private void Emit(Rect rect, uint color, uint colorB, double alpha)
        {
            var a = (int)Math.Round(Clamp01(alpha) * 255);
            if (a <= 0 || rect.Width <= 0 || rect.Height <= 0) return;
            if (CommandCount == Commands.Length) Array.Resize(ref Commands, Commands.Length * 2);
            Commands[CommandCount++] = new TileCommand((float)rect.Left, (float)rect.Top, (float)rect.Right, (float)rect.Bottom,
                color, (byte)a, colorB);
        }
    }

    // NEW (round 40): what one walk needs, read from the control on the UI thread.
    private SceneWalk PrepareWalk(Size viewport, Node? colorLevel = null, Node? colorLevelB = null)
    {
        GeometryBuildCount++;
        var openPath = new HashSet<Node>();
        for (var node = _container; node is not null; node = node.Parent) openPath.Add(node);
        return new SceneWalk
        {
            Root = _root!,
            ColorLevel = colorLevel ?? _colorLevel,
            ColorLevelB = colorLevelB is not null && colorLevelB != (colorLevel ?? _colorLevel) ? colorLevelB : null,
            Camera = _camera,
            Viewport = viewport,
            Budget = Budget,
            MinimumTile = MinimumTile,
            ExpandLow = ExpandLow,
            ExpandHigh = ExpandHigh,
            // CHANGED (round 39): moving never prefetches smaller than at rest.
            Prefetch = IsAnimating || _dragging ? Math.Max(ExpandHigh, PrefetchAt) : PrefetchAt,
            CanRequestLoads = ForegroundLoads < (_everything ? 12 : MaxLoads),
            Everything = _everything,
            LowDetail = _lowDetail,
            BasePacked = _basePacked,
            Flat = FlatActive ? _flat : null,
            FlatStamp = _flatStamp,
            Stamp = Now,
            Revision = _detailRevision,
            OpenPath = openPath,
            // CHANGED (round 41): a buffer from the pool, sized from the last scene so it rarely grows.
            // It becomes the scene's geometry and returns to the pool when that scene leaves the screen.
            Commands = TakeCommandBuffer(Math.Max(16384, _lastCommandCount + _lastCommandCount / 4)),
        };
    }

    // CHANGED (round 40): a walk run inline, for the first scene of a source (nothing to show while
    // waiting), off-screen renders and tests, which expect the scene the moment a frame is rendered.
    private SceneCache BuildGeometryCache(Size viewport)
    {
        var walk = PrepareWalk(viewport);
        var cache = walk.Run();
        AdoptWalk(walk);
        return cache;
    }

    // Brings a finished walk's side results back to the control: what it stamped, what it wants
    // loaded, and which flat layout its nodes' indices refer to.
    // NEW (round 41): command buffers, reused from scene to scene. UI thread only.
    private readonly List<TileCommand[]> _commandPool = [];
    private readonly List<TileGeometry> _pooledGeometry = [];   // geometries whose buffer came from the pool
    private int _lastCommandCount;
    private const int CommandPoolSize = 3;

    private TileCommand[] TakeCommandBuffer(int capacity)
    {
        var best = -1;
        for (var i = 0; i < _commandPool.Count; i++)
            if (_commandPool[i].Length >= capacity && (best < 0 || _commandPool[i].Length < _commandPool[best].Length)) best = i;
        if (best < 0) return new TileCommand[capacity];
        var buffer = _commandPool[best];
        _commandPool.RemoveAt(best);
        return buffer;
    }

    private void ReturnCommandBuffer(TileCommand[] buffer)
    {
        if (buffer.Length < 1024) return;
        _commandPool.Add(buffer);
        // Keep the largest few: a small one would only be replaced by growing it anyway.
        if (_commandPool.Count > CommandPoolSize)
        {
            var smallest = 0;
            for (var i = 1; i < _commandPool.Count; i++)
                if (_commandPool[i].Length < _commandPool[smallest].Length) smallest = i;
            _commandPool.RemoveAt(smallest);
        }
    }

    // A geometry from a walk that will never be shown: its buffer goes straight back.
    private void DropGeometry(TileGeometry geometry) => ReturnCommandBuffer(geometry.Release());

    // Any pooled geometry that no scene slot refers to any more is finished with: nothing will draw it
    // again, and the GPU keeps its own copy of what it has already drawn. Run once per frame.
    private void ReleaseUnusedGeometry()
    {
        for (var i = _pooledGeometry.Count - 1; i >= 0; i--)
        {
            var geometry = _pooledGeometry[i];
            if (Uses(_cache, geometry) || Uses(_previousCache, geometry)) continue;
            _pooledGeometry.RemoveAt(i);
            DropGeometry(geometry);
        }

        static bool Uses(SceneCache? cache, TileGeometry geometry)
            => cache is not null && ReferenceEquals(cache.Geometry, geometry);
    }

    private void AdoptWalk(SceneWalk walk)
    {
        _lastCommandCount = walk.CommandCount;
        _buildStamp = walk.Stamp;
        _flatNow = walk.Flat;
        _flatStampNow = walk.FlatStamp;
        _tiles = walk.Tiles;
        _wanted.Clear();
        _wanted.AddRange(walk.Wanted);
        _lastBuildMilliseconds = walk.Milliseconds;
    }

    // Hands back the text layouts of nodes that have left the screen, and enforces a hard ceiling
    // on how many may be held at once. A reclaimed node simply re-creates its text if it comes
    // back, through the same per-frame allowance that limits new labels.
    private void TrimTextCache(bool force)
    {
        if (!force && _texted.Count <= TextBudget && Now - _sweptAt < 1) return;
        _sweptAt = Now;
        // CHANGED (round 33): nodes are stamped when geometry is built, not every frame, so idleness is
        // measured from the last build. A view nobody touches stops building and stops evicting, which
        // is the right answer: nothing has left the screen.
        var cutoff = _cacheSince - TextIdle;
        var kept = 0;
        for (var i = 0; i < _texted.Count; i++)
        {
            var node = _texted[i];
            if (!force && node.LastDrawn >= cutoff) { _texted[kept++] = node; continue; }
            ReleaseText(node);
        }
        _texted.RemoveRange(kept, _texted.Count - kept);
        if (_texted.Count <= TextBudget) return;
        // Still over the ceiling: give up the nodes that have been off screen longest.
        _texted.Sort(static (a, b) => a.LastDrawn.CompareTo(b.LastDrawn));
        var excess = _texted.Count - TextBudget;
        for (var i = 0; i < excess; i++) ReleaseText(_texted[i]);
        _texted.RemoveRange(0, excess);
    }

    private static void ReleaseText(Node node)
    {
        node.LabelName = node.LabelDetail = node.PillName = node.PillSize = default;
        node.HasText = false;
    }

    // Collapses folders whose whole subtree has been off screen for a while, which is the only thing
    // that bounds memory: a collapsed folder drops its child nodes, their grouped tails, and the
    // DiskUsageItem array those tails were holding open. They reload on demand exactly as they did
    // the first time. Folders on the open path and anything recently drawn are never touched.
    private void TrimNodeTree()
    {
        if (_root is null || _liveNodes <= NodeCeiling) return;
        if (_walk is not null) return;   // NEW (round 40): never collapse nodes a background walk is reading
        // A sweep walks every expanded node, so wait for a pause unless the tree is far over budget.
        if ((IsAnimating || _dragging) && _liveNodes < NodeCeiling * 2) return;
        if (Now - _nodeSweptAt < 2) return;
        _nodeSweptAt = Now;
        var before = _liveNodes;
        // Everything the rest of the control still points at has to survive the sweep, or a click
        // could land on a node that is no longer part of the tree.
        _openPath.Clear();
        Protect(_container);
        Protect(_focusNode);
        Protect(_colorLevel);
        Protect(_colorPrevious);
        Protect(_candidate);
        Protect(_hover);
        Protect(_highlightNode);
        _liveNodes = Sweep(_root, _cacheSince - NodeIdle).Count;
        // Everything alive is on screen or recent: another sweep would walk the tree for nothing.
        if (_liveNodes > NodeCeiling && _liveNodes >= before) _nodeSweptAt = Now + 8;

        void Protect(Node? node)
        {
            for (; node is not null; node = node.Parent) _openPath.Add(node);
        }
    }

    private (int Count, double Newest) Sweep(Node node, double cutoff)
    {
        if (node.Children.Length == 0) return (1, node.LastDrawn);
        var count = 1;
        var newest = node.LastDrawn;
        foreach (var child in node.Children)
        {
            var (childCount, childNewest) = Sweep(child, cutoff);
            count += childCount;
            if (childNewest > newest) newest = childNewest;
        }
        if (newest >= cutoff || node == _root || _openPath.Contains(node)) return (count, newest);
        foreach (var child in node.Children) Forget(child);
        node.Children = [];
        node.State = NodeState.Collapsed;
        node.ReadyAt = double.NegativeInfinity;
        return (1, newest);
    }

    // Text held by a node about to become unreachable is handed back now; the text sweep removes it
    // from its list on the next pass, because a forgotten node can never be drawn again.
    private static void Forget(Node node)
    {
        ReleaseText(node);
        node.LastDrawn = double.NegativeInfinity;
        foreach (var child in node.Children) Forget(child);
        node.Children = [];
    }

    // CHANGED (round 43): a scene carries a label set per colour level. A label in both (the folders
    // outside the one being entered) is drawn once, its colour mixed; the others fade with the blend.
    private void DrawCachedLabels(DrawingContext dc, SceneCache cache, double opacity, double colorBlend = 0)
    {
        if (opacity <= .01) return;
        var mapping = BatchFor(cache);
        var second = colorBlend > 0 ? cache.LabelIndexB : null;
        foreach (var caption in cache.Captions)
        {
            var key = (caption.Node, caption.IsOpen);
            // Shared labels are drawn once. Drawing old/new copies would make
            // WPF reshape and recolor the same cached text twice every frame.
            if (ReferenceEquals(cache, _previousCache) && opacity < .99 && _cache?.LabelIndex.ContainsKey(key) == true) continue;
            var alpha = caption.Alpha;
            var color = caption.Color;
            if (second is not null)
            {
                if (second.TryGetValue(key, out var other))
                {
                    alpha += (other.Alpha - alpha) * colorBlend;
                    color = Mix(color, other.Color, colorBlend);
                }
                else alpha *= 1 - colorBlend;
            }
            alpha *= opacity;
            if (ReferenceEquals(cache, _cache) && _previousCache?.LabelIndex.TryGetValue(key, out var prior) == true)
            {
                alpha += prior.Alpha * (1 - opacity);
                color = Mix(prior.Color, color, opacity);
            }
            DrawCaption(dc, caption, mapping, alpha, color);
        }
        if (second is null || cache.CaptionsB is null) return;
        foreach (var caption in cache.CaptionsB)
            if (!cache.LabelIndex.ContainsKey((caption.Node, caption.IsOpen)))
                DrawCaption(dc, caption, mapping, caption.Alpha * colorBlend * opacity, caption.Color);
    }

    private void DrawCaption(DrawingContext dc, Caption caption, TileBatch mapping, double alpha, Color color)
    {
        if (alpha <= .01) return;
        var visible = Rect.Intersect(mapping.Transform(caption.Visible), _view);
        if (visible.IsEmpty) return;
        if (caption.IsOpen)
        {
            var pill = LayoutPill(caption.Node, visible, Rect.Empty, color, alpha);
            if (pill is { } tag) DrawPill(dc, tag);
        }
        else DrawLabel(dc, caption.Node, visible, Rect.Empty, alpha);
    }

    private void PrepareFallbackCommands()
    {
        _commandCount = 0;
        for (var i = 0; i < _batchCount; i++)
        {
            var batch = _batches[i];
            if (batch.Opacity <= 0) continue;
            foreach (ref readonly var command in batch.Geometry.Span)
            {
                var rect = batch.Transform(new Rect(command.X0, command.Y0, command.X1 - command.X0, command.Y1 - command.Y0));
                rect.Intersect(_viewLoose);
                if (rect.IsEmpty) continue;
                // CHANGED (round 43): the software path mixes the two colour sets directly.
                Emit(rect, batch.ColorBlend > 0 ? Mix(command.Color, command.To, batch.ColorBlend) : command.Color, command.Alpha / 255d * batch.Opacity);
            }
        }
    }


    // ---------------------------------------------------------------- NEW (round 39): flat detail

    // How colour is inherited below a Node, per level: either each child is its own anchor (the Node
    // is on the level's path, so its children are the ones being coloured apart), or every
    // descendant shares one anchor's hue and darkens with its distance from it. ComputeColor's rule.
    private readonly record struct FlatTint(uint Hue, int Depth, bool Anchors, int Slot);

    // NEW (round 43): a folder's children take the palette from the folder's own place in it. Each
    // node's slot is its parent's slot plus its rank, so the first (largest) child of a folder gets the
    // folder's own colour and the rest continue round the palette from there. Every level still shows
    // the whole palette, but zooming into a folder keeps its biggest block the colour it already was,
    // and nothing outside the folder changes colour at all.
    private static int PaletteSlot(Node node)
    {
        var slot = 0;
        for (var current = node; current.Parent is not null; current = current.Parent) slot += current.Index;
        return slot % BranchColors.Length;
    }


    private static FlatTint ChildTint(FlatTint parent, FlatTreemapLayout flat, int child, int index)
        => parent.Anchors
            ? new FlatTint(flat.IsGroup(child) ? GroupColor : BranchColors[(parent.Slot + index) % BranchColors.Length], 0, false, 0)
            : parent with { Depth = parent.Depth + 1 };

    private static uint FlatColor(FlatTint tint, double spread)
        => tint.Depth == 0 ? tint.Hue : Shade(tint.Hue, (1 - .05 * Math.Min(tint.Depth, 3)) * (.82 + .24 * spread));




    // Makes sure a flat layout exists, or is being built, for the current source at this root shape.
    private void EnsureFlat(double aspect)
    {
        if (!_everything || _disposed || _cacheKey is not IFlatSource source) return;
        if (_flat is { } current && ReferenceEquals(current.Source, source) && current.Aspect == aspect) return;
        if (FindFlat(source, aspect) is { } cached)
        {
            UseFlat(cached);
            return;
        }
        if (_flatBuilding is { } building && ReferenceEquals(building.Source, source) && building.Aspect == aspect) return;
        if (_flatFailed is { } failed && ReferenceEquals(failed.Source, source) && failed.Aspect == aspect) return;
        _flatWork?.Cancel();
        var work = _flatWork = new CancellationTokenSource();
        _flatBuilding = (source, aspect);
        var token = work.Token;
        Task.Run(() => FlatTreemapLayout.Build(source, aspect, token), token).ContinueWith(task =>
            Dispatcher.InvokeAsync(() =>
            {
                _ = task.Exception;
                if (ReferenceEquals(_flatWork, work)) { _flatWork = null; _flatBuilding = null; }
                work.Dispose();
                if (task.IsFaulted) _flatFailed = (source, aspect);   // a deterministic error is not retried each frame
                if (task.Status != TaskStatus.RanToCompletion) return;
                StoreFlat(task.Result);   // even after close: reopening is exactly when it is wanted
                if (_disposed) return;
                // FlatActive decides whether it matches the tree on screen; a layout for a shape the
                // window has since left simply waits in the cache.
                if (_everything && ReferenceEquals(task.Result.Source, _cacheKey)) UseFlat(task.Result);
            }), TaskScheduler.Default);
    }

    private void UseFlat(FlatTreemapLayout flat)
    {
        if (ReferenceEquals(_flat, flat)) return;
        _flat = flat;
        _flatStamp = Interlocked.Increment(ref FlatStamps);
        _detailRevision++;   // the next build reads it; a load-settle gate still applies mid-gesture
        _sceneDirty = true;
        RequestFrame();
    }

    private static FlatTreemapLayout? FindFlat(object source, double aspect)
    {
        lock (FlatCache) return FlatCache.Find(flat => ReferenceEquals(flat.Source, source) && flat.Aspect == aspect);
    }

    private static void StoreFlat(FlatTreemapLayout flat)
    {
        lock (FlatCache)
        {
            FlatCache.RemoveAll(entry => ReferenceEquals(entry.Source, flat.Source) && entry.Aspect == flat.Aspect);
            FlatCache.Add(flat);
            while (FlatCache.Count > FlatCacheSize) FlatCache.RemoveAt(0);
        }
    }


    // Maps a child's world rectangle into its parent's on-screen content rectangle.
    private static Rect Within(Rect inner, Rect parentWorld, Rect childWorld)
    {
        var scaleX = inner.Width / parentWorld.Width;
        var scaleY = inner.Height / parentWorld.Height;
        return new Rect(inner.X + (childWorld.X - parentWorld.X) * scaleX, inner.Y + (childWorld.Y - parentWorld.Y) * scaleY,
            childWorld.Width * scaleX, childWorld.Height * scaleY);
    }

    internal static double DetailAmount(int budget, int children)
        => SmoothStep((budget - children - 1d) / Math.Max(8, children * .5));


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

    private void Emit(Rect rect, uint color, double alpha)
    {
        var a = (int)Math.Round(Clamp01(alpha) * 255);
        if (a <= 0 || rect.Width <= 0 || rect.Height <= 0) return;
        if (_commandCount == _commands.Length) Array.Resize(ref _commands, _commands.Length * 2);
        _commands[_commandCount++] = new TileCommand((float)rect.Left, (float)rect.Top, (float)rect.Right, (float)rect.Bottom,
            color, (byte)a);
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
        if (!_gpuEnabled || !IsLoaded || PresentationSource.FromVisual(this) is null) return false;
        if (!_gpuTried)
        {
            _gpuTried = true;
            // The adapter is chosen from the window's monitor, so pass the handle.
            _gpu = GpuTileRenderer.TryCreate(PresentationSource.FromVisual(this) is HwndSource host ? host.Handle : IntPtr.Zero);
        }
        if (_gpu is not { IsAvailable: true } gpu) return false;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        var background = (uint)_baseColor.R << 16 | (uint)_baseColor.G << 8 | _baseColor.B;
        var start = _statClock.Elapsed.TotalMilliseconds;
        if (!gpu.RenderCached(_batches.AsSpan(0, _batchCount), width, height, width / ActualWidth, height / ActualHeight, background, out var busy))
        {
            if (busy) { _gpuSkipped++; _needsFrame = true; }
            return busy; // Retain the GPU frame on contention; CPU fallback is for failure only.
        }
        if (!_gpuShown)
        {
            using var dc = _scene.RenderOpen();
            dc.DrawImage(gpu.Image, new Rect(0, 0, ActualWidth, ActualHeight));
            _gpuShown = true;
            _shown = null;
        }
        if (_showStats)
        {
            Smooth(ref _statRaster, Math.Max(0, _statClock.Elapsed.TotalMilliseconds - start - gpu.LastUploadMilliseconds));
            Smooth(ref _statUpload, gpu.LastUploadMilliseconds);
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
        if (!bitmap.TryLock(new Duration(TimeSpan.Zero))) { _needsFrame = true; return; }
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




    // Each item directly inside the level (the "anchor") has its own hue; everything inside it
    // shares that hue, a little darker per level and varied between siblings.
    private uint ComputeColor(Node node, Node level)
    {
        var anchor = node;
        var depth = 0;
        while (anchor.Parent is not null && !OnPath(anchor.Parent, level)) { anchor = anchor.Parent; depth++; }
        if (anchor.Parent is null) return _basePacked;
        var hue = anchor.IsGroup ? GroupColor : BranchColors[PaletteSlot(anchor)];   // CHANGED (round 43)
        if (depth == 0) return hue;
        // CHANGED (round 18): the old .05-per-index step varied siblings by at most 10% and ramped in
        // size order, so a folder's contents fused into one flat field. Value now comes from a hash of
        // the name across a wide range: neighbours speckle instead of ramping, and the block still
        // reads as one hue. Depth keeps nested folders recessed.
        // CHANGED (round 27): the spread was +/-20%, which reads as noise rather than texture. A
        // narrower band keeps blocks individually visible while the folder still reads as one colour.
        return Shade(hue, (1 - .05 * Math.Min(depth, 3)) * (.82 + .24 * Spread(node)));
    }

    // 0..1, stable per item, so the texture never shimmers while zooming.
    private static double Spread(Node node)
    {
        var hash = 2166136261u;
        foreach (var character in node.Item.Name) hash = (hash ^ (uint)character) * 16777619u;
        return ((hash >> 8) & 1023) / 1023d;
    }

    private static uint Pack(Color color) => (uint)color.R << 16 | (uint)color.G << 8 | color.B;
    private static Color Unpack(uint color) => Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);

    private static uint Shade(uint color, double factor)
    {
        var scale = (uint)Math.Clamp((int)(factor * 256), 0, 2048);
        var r = Math.Min(255u, ((color >> 16 & 255) * scale) >> 8);
        var g = Math.Min(255u, ((color >> 8 & 255) * scale) >> 8);
        var b = Math.Min(255u, ((color & 255) * scale) >> 8);
        return r << 16 | g << 8 | b;
    }

    private static uint Mix(uint from, uint to, double amount)
    {
        var w = (uint)Math.Clamp((int)(amount * 256), 0, 256);
        var inverse = 256 - w;
        var r = ((from >> 16 & 255) * inverse + (to >> 16 & 255) * w) >> 8;
        var g = ((from >> 8 & 255) * inverse + (to >> 8 & 255) * w) >> 8;
        var b = ((from & 255) * inverse + (to & 255) * w) >> 8;
        return r << 16 | g << 8 | b;
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
        var name = Text(node, ref node.PillName, node.Item.Name, 12, true);
        var size = Text(node, ref node.PillSize, node.SizeText, 11, false);
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
        DrawCachedText(dc, ref pill.Node.PillName, new Point(pill.Rect.X + 7, pill.Rect.Y + (pill.Rect.Height - pill.Name.Height) / 2));
        DrawCachedText(dc, ref pill.Node.PillSize, new Point(pill.Rect.Right - 7 - pill.Size.WidthIncludingTrailingWhitespace,
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
        var name = Text(node, ref node.LabelName, node.Item.Name, big ? 13.5 : 12, true);
        if (name is null) return;
        Trim(ref node.LabelName, room);
        var detail = twoLines ? Text(node, ref node.LabelDetail, node.DetailText, big ? 12 : 11, false) : null;
        if (twoLines && detail is null) return;
        if (detail is not null) Trim(ref node.LabelDetail, room);
        var x = visible.X + pad;
        var y = visible.Y + pad - 2;
        if (!zone.IsEmpty && x < zone.Right && y < zone.Bottom + 2) y = zone.Bottom + 3;
        var height = name.Height + (detail?.Height ?? 0);
        if (y + height > visible.Bottom - 2) return;
        Tint(ref node.LabelName, BrushFor(White, alpha) ?? Brushes.Transparent);
        DrawCachedText(dc, ref node.LabelName, new Point(x, y));
        if (detail is null) return;
        Tint(ref node.LabelDetail, BrushFor(White, alpha * .72) ?? Brushes.Transparent);
        DrawCachedText(dc, ref node.LabelDetail, new Point(x, y + name.Height));
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
        if (!_highlightResolved)
        {
            _highlightNode = _highlight is int id && _focusNode is not null ? FindChild(_focusNode, id) : null;
            _highlightResolved = true;
        }
        if (_highlightNode is { } selected) Outline(dc, selected, AccentEdge);
        _hover = _mouseInside && !_dragging ? NodeAt(_mouse) : null;
        var target = _hover is null ? null : ClickTarget(_hover);
        // REVERTED (round 4): plain outline on hover.
        if (target is not null && _flight is null) Outline(dc, target, HoverEdge);
        DrawHoverCard(dc);
        if (_showStats) DrawStats(dc);
    }

    // NEW (round 12): where each frame's time goes. Toggle with F3.
    private static GCMemoryInfo GcInfo => GC.GetGCMemoryInfo();

    private void DrawStats(DrawingContext dc)
    {
        // Diagnostics must not create and shape three new text lines on every
        // animation frame. Refresh the readout four times per second.
        if (_statsText is null || Now - _statsUpdatedAt >= .25)
        {
            var fps = _statFrame > 0 ? 1000 / _statFrame : 0;
            var text = Make(
                $"frame {_statFrame:0.0} ms ({fps:0} fps) · worst {_statWorstFrame:0} ms   scene {_statScene:0.0} ms · worst walk {_statWorstWalk:0} ms\n" +
                $"walk {_statWalk:0.0} · labels {_statLabels:0.0} · raster {_statRaster:0.0} · upload {_statUpload:0.0} ms · " +
                // NEW (round 40): the last scene build, which runs off the UI thread in a live window.
                $"last build {_lastBuildMilliseconds:0} ms{(CanWalkInBackground ? " (background)" : "")}\n" +
                $"{(_gpuShown ? (_gpu?.IsMultisampled == true ? "GPU 4×AA" : "GPU") : "CPU")} · {_statTiles:N0} tiles · {_statPixelsW}×{_statPixelsH} px · " +
                $"{_loads} loading · gen2 GCs {GC.CollectionCount(2) - _gen2Start}\n" +
                $"cache · {GeometryBuildCount} builds · {GeometryReuseCount} reused frames · {GpuGeometryUploads} GPU uploads\n" +
                // NEW (round 20): where the memory actually is. The rectangles are the cheap part;
                // the labels are WPF text layouts and glyph drawings, kilobytes each.
                $"memory · heap {GC.GetTotalMemory(false) / 1048576.0:0} MB · {LiveNodeCount:N0}/{NodeCeiling:N0} nodes · {LiveTextCount:N0}/{TextBudget:N0} labels · " +
                $"{(_cache?.Geometry.Count ?? 0) + (_previousCache?.Geometry.Count ?? 0):N0} rects cached · " +
                $"GPU {(_gpu?.CachedGeometryBytes ?? 0) / 1048576.0:0.0} MB{(_lean ? " · conserving" : "")}\n" +
                // The map's own share, next to the snapshot it reads. Anything left over is the file
                // index itself, which the rest of Clearspace needs whether this view is open or not.
                // NEW (round 39): the flat layout's own line item when "render everything" uses it.
                // NEW (round 40): the whole process, so the map's share can be told apart from the rest.
                $"process {Environment.WorkingSet / 1048576.0:0} MB · GC committed {GcInfo.TotalCommittedBytes / 1048576.0:0} MB, " +
                $"fragmented {GcInfo.FragmentedBytes / 1048576.0:0} MB · index {FileIndexService.EstimatedBytes / 1048576.0:0} MB · " +
                $"{HeapTrims} idle trims\n" +
                $"map ≈ {(LiveNodeCount * 400L + LiveTextCount * 4096L + (_flat?.ApproximateBytes ?? 0)) / 1048576.0:0} MB" +
                (_flat is { } flatStat ? $" (flat {flatStat.Count:N0} in {flatStat.ApproximateBytes / 1048576.0:0} MB, {flatStat.BuildMilliseconds:0} ms{(FlatActive ? "" : ", idle")})" : "") + " · " +
                $"snapshots {DiskUsageSnapshotCache.RetainedBytes / 1048576.0:0} MB · " +
                $"{_gpuSkipped:N0} GPU frames deferred", 11, false);
            text.MaxLineCount = 7;
            text.SetForegroundBrush(Ink);
            _statsText = text;
            _statsUpdatedAt = Now;
        }
        var box = new Rect(10, 10, _statsText.Width + 20, _statsText.Height + 14);
        dc.DrawRoundedRectangle(CardFill, CardEdge, box, 6, 6);
        dc.DrawText(_statsText, new Point(20, 17));
    }

    private void Outline(DrawingContext dc, Node node, Pen pen)
    {
        if (_cache is not { } cache) return;
        var mapping = BatchFor(cache);
        foreach (var (drawnNode, screen) in cache.Tiles)
        {
            if (drawnNode != node) continue;
            var box = Rect.Intersect(mapping.Transform(screen), Rect.Inflate(_view, 2, 2));
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
        const double pad = 12, swatch = 9, maxText = 340;
        if (_hoverText?.Node != node || _hoverText.Focus != _focusNode)
        {
            var folder = node.Parent is null ? null : FolderOf(node.Parent);
            var name = Make(node.Item.Name, 13, true);
            name.SetForegroundBrush(Ink);
            name.MaxTextWidth = maxText;
            var description = Make($"{DiskUsagePalette.CategoryName(node.Item)} · {node.SizeText}" +
                (folder is null ? "" : $" · {node.ShareText} of {folder.Item.Name.TrimEnd('\\')}"), 11.5, false);
            description.SetForegroundBrush(InkMuted);
            description.MaxTextWidth = maxText - swatch - 7;
            var instruction = Make(HintFor(node), 11, false);
            instruction.SetForegroundBrush(InkFaint);
            instruction.MaxTextWidth = maxText;
            _hoverText = new HoverText(node, _focusNode, name, description, instruction);
        }
        var title = _hoverText.Title;
        var detail = _hoverText.Detail;
        var hint = _hoverText.Hint;
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
        // CHANGED (round 40): computed, not read through the node's colour cache - that cache is the
        // geometry walk's, and the walk may be writing it on another thread right now.
        var swatchColor = _colorLevel is { } swatchLevel ? ComputeColor(node, swatchLevel) : _basePacked;
        dc.DrawEllipse(BrushFor(Unpack(swatchColor), 1), null, new Point(x + pad + swatch / 2, line + detail.Height / 2), swatch / 2, swatch / 2);
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
    private FormattedText? Text(Node node, ref CachedText cache, string text, double em, bool bold)
    {
        if (cache.Text is not null && cache.Em == em) return cache.Text;
        if (_textBudget <= 0) { _needsFrame = true; return null; }
        _textBudget--;
        var formatted = Make(text, em, bold);
        cache = new CachedText(formatted, em, formatted.WidthIncludingTrailingWhitespace);
        // Registered once per node, so the sweep can find everything that is holding text.
        if (!node.HasText) { node.HasText = true; _texted.Add(node); }
        return cache.Text;
    }

    // NEW: re-tinting or re-trimming makes WPF lay the text out again, so only do it on change.
    private static void Tint(ref CachedText cache, Brush brush)
    {
        if (cache.Text is null || ReferenceEquals(cache.Tint, brush)) return;
        cache.Text.SetForegroundBrush(brush);
        cache.Tint = brush;
        cache.Drawing = null;
    }

    private void Trim(ref CachedText cache, double width)
    {
        // CHANGED (round 24): while the camera moves, a name that already has a layout keeps it. Every
        // MaxTextWidth change makes WPF reshape the whole line, and the re-flow is visible as the text
        // twitching inside its block. The ellipsis catches up as soon as the camera settles.
        if ((IsAnimating || _dragging) && cache.Width > 0) return;
        const double step = 4;
        width = Math.Max(1, Math.Floor(width / step) * step);
        // Once the whole name fits, growing its tile must not reflow the same
        // text on every zoom frame (WPF allocates a new glyph layout each time).
        width = Math.Min(width, Math.Ceiling(cache.NaturalWidth + 1));
        if (cache.Text is null || cache.Width == width) return;
        cache.Text.MaxTextWidth = width;
        cache.Width = width;
        cache.Drawing = null;
    }

    private static void DrawCachedText(DrawingContext dc, ref CachedText cache, Point origin)
    {
        if (cache.Text is null) return;
        if (cache.Drawing is null)
        {
            var drawing = new DrawingGroup();
            using (var text = drawing.Open()) text.DrawText(cache.Text, new Point());
            drawing.Freeze();
            cache.Drawing = drawing;
        }
        var placement = new TranslateTransform(origin.X, origin.Y);
        placement.Freeze();
        dc.PushTransform(placement);
        dc.DrawDrawing(cache.Drawing);
        dc.Pop();
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
            _basePacked = Pack(baseBrush.Color);
            _base = Frozen(baseBrush.Color);
        }
    }

    private static SolidColorBrush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Pen FrozenPen(Color color, double thickness) { var pen = new Pen(Frozen(color), thickness); pen.Freeze(); return pen; }

    // ================================================================= input

    // CHANGED (round 33): the pointer is mapped back into the cached scene's own coordinates and the
    // tiles are searched from the end, where the deepest blocks are - parents are recorded before
    // their children. This hit-tests the pixels that are actually on screen, including while a GPU
    // frame is deferred, without keeping a transformed copy of every tile up to date each frame.
    private Node? NodeAt(Point point)
    {
        if (_cache is not { } cache || BatchFor(cache) is not { Scale: > 0 } mapping) return null;
        var local = new Point((point.X - mapping.X) / mapping.Scale, (point.Y - mapping.Y) / mapping.Scale);
        var tiles = cache.Tiles;
        for (var i = tiles.Count - 1; i >= 0; i--)
            if (tiles[i].Screen.Contains(local)) return tiles[i].Node;
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
                _lastInteraction = Now;
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
