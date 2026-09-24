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

    // CHANGED (round 60): Source - the string the text was made from, so a label whose words change (the
    // live memory view's sizes) is made again rather than kept.
    private record struct CachedText(FormattedText? Text, double Em, double NaturalWidth, Brush? Tint = null, double Width = -1, Drawing? Drawing = null, string? Source = null);

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
        public string DetailText => _detailText ??= Detail ?? $"{SizeText} · {ShareText}";
        // NEW (round 51): a second line of its own (drive covers, the computer).
        // CHANGED (round 60): settable, so a live block (the memory's) keeps its node - and its label - while
        // its numbers change.
        public string? Detail { get => _detail; set { _detail = value; _detailText = null; } }
        public string? Tag { get; set; }       // NEW (round 58): what a name tag says after the name, in place of the size
        private string? _detail;
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
        _memoryTimer.Tick += (_, _) => PollLive();   // NEW (round 59); (round 62) the processor too
        Loaded += (_, _) => { LoadResources(); RequestFrame(); _heapTrim.Start(); _memoryTimer.Start(); };
        Unloaded += (_, _) =>
        {
            StopFrames();
            _heapTrim.Stop();
            _memoryTimer.Stop();   // NEW (round 59)
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
        _memoryTimer.Stop();   // NEW (round 59)
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
    // CHANGED (round 55): keepDrive - the same drive laid out again (the window was resized, or a tree
    // finished for a shape the window no longer has). Its size, free space, hardware and the drive the camera
    // was heading into belong to the drive, not the layout, and nothing would send them again.
    internal void SetSource(DiskUsageItem root, Func<int, CancellationToken, IReadOnlyList<DiskUsageItem>> children,
        IReadOnlyList<int> path, string? missingName = null, Func<int, DiskUsageItem>? item = null,
        object? cacheKey = null, bool keepDrive = false)
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
        _openingDrive = null;
        _driveView = null;
        if (!keepDrive)
        {
            _anchor = null;          // NEW (round 49)
            _thisHardware = default; // NEW (round 51): the new drive's own hardware arrives with its space
            _driveSpace = null;      // the new drive's own space arrives with it
            _restoreView = null;
        }
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
        _anchor = null;          // NEW (round 49)
        _openingDrive = null;
        _thisHardware = default; // NEW (round 51)
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
            var mapping = BatchFor(cache, presented: true);
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
        // NEW (round 44): out at the drive, any click goes back to the folder map as it was.
        if (InDriveView)
        {
            // NEW (round 47): except on another drive, which opens that drive.
            if (NodeAt(point) is { } region && OtherDriveOf(region) is { } picked && CurrentDrive() is { } machine)   // by id, so a node from the last scene still works
            {
                // CHANGED (round 49): fly into it first; it opens where the camera lands, in the same place.
                foreach (var box in machine.Drives)
                    if (box.Index >= 0 && machine.Others[box.Index].Root == picked.Root) OpenDrive(machine, box, OtherDrivePath(region));
                return true;
            }
            // NEW (round 59): in the memory, a click zooms into what was clicked - a program, a file, or the
            // memory itself from its sticks.
            if (NodeAt(point) is { } block && CurrentDrive() is { } view && view.ZoomTiles.TryGetValue(block.Item.Id, out var tile))
            {
                _userMoved = true;
                _message = null;
                FlyTo(FitRaw(tile, .04));
                return true;
            }
            _userMoved = true;
            _message = null;
            _anchor = null;   // NEW (round 49)
            FlyTo(Fit(_root!));
            if (_focusNode != _root) Report(_root!);
            return true;
        }
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
        // NEW (round 49): zooming in over a drive heads into that drive - once the view is still wider than
        // that drive's files, so a notch over the edge of a bigger drive cannot swing the camera out to it.
        _openingDrive = null;   // any wheel movement takes over from a flight into a drive
        var next = Clamp(new Rect(anchorX - u * width, anchorY - v * height, width, height));
        // NEW (round 58): zooming out stops first at the drives alone, framed; only a few more notches out
        // (pushed within a second of each other) go on to the computer below them.
        if (CurrentDrive() is { Row.IsEmpty: false } drives && ZoomOutDetent(drives, basis, next, delta) is { } held)
        {
            _flight = null;
            _target = held;
            RequestCameraFrame();
            return true;
        }
        if (delta > 0) _detentPushes = 0;
        // CHANGED (round 57): zooming in on another drive's files until they fill the view opens it - with
        // the pointer on them, so zooming in elsewhere (the board, a drive's free space) just zooms.
        if (delta > 0 && CurrentDrive() is { } machine && DriveAt(machine, new Point(anchorX, anchorY)) is { Index: >= 0 } into &&
            into.Files.Contains(new Point(anchorX, anchorY)) && next.Width <= FitRaw(into.Files, 0).Width * 1.02)
        {
            OpenDrive(machine, into, null);
            return true;
        }
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
                SetSource(requested.Root, provider, requested.Path, requested.Missing, _item, keepDrive: true);   // CHANGED (round 55)
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
            // NEW (round 55): relaid for a new window shape while zoomed out over the drives: the camera goes
            // back to the same part of the picture, rather than into the files.
            if (_restoreView is { } restore && CurrentDrive() is { } machine)
            {
                var fit = FitRaw(machine.Machine, 0);
                var width = fit.Width * restore.Size;
                var height = width / ViewAspect;
                var centre = new Point(machine.Machine.X + machine.Machine.Width * restore.U, machine.Machine.Y + machine.Machine.Height * restore.V);
                _camera = _target = Clamp(new Rect(centre.X - width / 2, centre.Y - height / 2, width, height));
                _userMoved = true;
            }
            _restoreView = null;
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
        // NEW (round 55): out over the drives, remember where in the picture the camera is (as a share of
        // the whole machine, which is laid out again at the new shape) to come back to it.
        if (InDriveView && CurrentDrive() is { } machine && machine.Machine.Width > 0 && machine.Machine.Height > 0)
        {
            var centre = Center(_camera);
            _restoreView = ((centre.X - machine.Machine.X) / machine.Machine.Width, (centre.Y - machine.Machine.Y) / machine.Machine.Height,
                _camera.Width / Math.Max(1e-12, FitRaw(machine.Machine, 0).Width));
        }
        SetSource(root, _children, path, missing, _item, keepDrive: true);   // CHANGED (round 55): the same drive
        _sourceFadeAt = fade; // a resize is not a new source; don't flash
    }

    private (double U, double V, double Size)? _restoreView;   // NEW (round 55)

    // NEW (round 58): the stop at the drives when zooming out. Returns the view to hold, or null to zoom on.
    private const int DetentPushes = 3;   // wheel notches past the stop
    private int _detentPushes;            // wheel movement (in notch units of 120) past it so far
    private double _detentAt;

    private Rect? ZoomOutDetent(DriveView drives, Rect basis, Rect next, int delta)
    {
        if (delta >= 0) return null;
        // CHANGED (round 59): only when looking at the drives - zooming out of the memory (or anywhere else on
        // the board) must not jump up to them.
        if (!drives.Row.Contains(Center(basis))) return null;
        var stop = Clamp(FitRaw(drives.Row, 0));
        if (stop.Width >= FitRaw(drives.Machine, 0).Width * .98) return null;   // nothing beyond the drives to hold back
        if (basis.Width < stop.Width * .99)
        {
            // Crossing the stop from inside: land exactly on it, the whole row framed.
            if (next.Width <= stop.Width) return null;
            _detentPushes = 0;
            _detentAt = Now;
            return stop;
        }
        if (basis.Width > stop.Width * 1.02) return null;   // already past it, out over the computer
        // At the stop: count pushes out, by how far the wheel turned (a touchpad sends many small ones); a
        // pause resets them.
        if (Now - _detentAt > 1) _detentPushes = 0;
        _detentAt = Now;
        _detentPushes += Math.Min(120, -delta);
        return _detentPushes < DetentPushes * 120 ? stop : null;
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
    // NEW (round 44): the drive around the files. Zooming out past the whole folder map keeps going, and
    // the rest of the drive comes into view around it at the same scale: the space Windows reports as
    // free, and the used space the index does not account for (system files, the page file, anything it
    // could not read). The folder map keeps its exact place - it sits in the top-left corner of a
    // rectangle scaled up by sqrt(drive / files), and the extra space fills the L-shape to its right and
    // below - so nothing is laid out again and zooming back in lands exactly where it was.
    private (long Total, long Free)? _driveSpace;
    private DriveView? _driveView;
    // CHANGED (round 47): Machine is the frame around this drive and the other drives beside it (the same
    // as World when there are none); Others says which drive each of their regions belongs to.
    private sealed record DriveView(Node Root, long Total, long Free, Rect World, DriveRegion[] Regions, Rect Machine, OtherDrive[] Others)
    {
        // NEW (round 48): drawn after the regions, in order - other drives' folders and files, the cables
        // and the computer's details - and which drive (and which item on it) each region stands for.
        public Solid[] Solids { get; init; } = [];
        public Dictionary<int, (int Drive, string? Path)> Owners { get; init; } = [];   // by item id, which is the same across rebuilds
        public DriveBox[] Drives { get; init; } = [];            // NEW (round 49): every drive's box and files, this one as -1
        // NEW (round 51): each drive's body, drawn under its files; and its cover - the lid or label it has
        // as a device - which the overlay draws over it and fades in as the camera pulls out, one per slot
        // of Drives. A cover's node is what hovering or clicking the covered drive finds.
        public Solid[] Underlay { get; init; } = [];
        public CoverShape[] Covers { get; init; } = [];
        public CoverText?[] CoverTexts { get; init; } = [];
        public Rect[] CoverBounds { get; init; } = [];
        public Node?[] CoverNodes { get; init; } = [];
        public Rect Row { get; init; } = Rect.Empty;   // NEW (round 58): the drives alone, the first stop zooming out
        // NEW (round 59): what is in memory - the area its contents fill, the memory sticks drawn over it (by
        // the overlay, fading as the camera closes in, the way a drive's cover does), and where each of its
        // blocks is, for a click to zoom to.
        public Rect MemoryArea { get; init; } = Rect.Empty;
        public LidPart[] Lids { get; init; } = [];            // CHANGED (round 62): the sticks, and the processor's lid
        public Rect CpuArea { get; init; } = Rect.Empty;       // NEW (round 62): what the processor's lid covers
        public Dictionary<int, Rect> ZoomTiles { get; init; } = [];
        public Dictionary<int, Node> Nodes { get; init; } = [];  // NEW (round 49): region labels by id, reused by the next build
    }

    // NEW (round 48): a plain block the drive view draws. Tiles get a gap like the map's blocks and vanish
    // below a pixel; wires keep a visible thickness at any zoom.
    private enum SolidKind : byte { Tile, Wire, Plain }
    // NEW (round 49): one drive in the machine view: which (-1 this one, else an index into Others), its
    // whole box, and the part its files fill - what the camera zooms into to open it.
    private readonly record struct DriveBox(int Index, Rect Box, Rect Files);
    // NEW (round 51): the parts of a drive's cover, in world units; Slot is the drive's place in Drives.
    private enum CoverForm : byte { Box, Rounded, Ellipse, Ring }
    private readonly record struct CoverShape(CoverForm Form, Rect World, uint Color, int Slot, double Corner = 0, double Stroke = 0);
    // NEW (round 59): one memory stick (or empty slot) over the memory's contents, and what hovering it finds.
    // CHANGED (round 62): any lid over a part's insides - the memory's sticks, the processor's heatspreader.
    // Area is what it covers (it fades as that nearly fills the view); Backing fills the area behind it.
    private readonly record struct LidPart(Rect World, Rect Area, uint Color, uint Backing, Node Node, string Label);

    // What a drive's label says, and where on it.
    private sealed record CoverText(Rect Sticker, string Brand, string Model, string Capacity, string Kind, uint Accent);
    private readonly record struct Solid(Rect World, uint Color, SolidKind Kind);
    // CHANGED (round 58): Open - captioned with a name tag in its corner, the way the map tags an open folder,
    // for a region with others on top of it (the motherboard, the memory).
    // CHANGED (round 61): Parent - the region it sits in, whose name tag its own caption keeps clear of.
    private readonly record struct DriveRegion(Node Label, Rect[] Parts, uint Color, bool Fill = true, bool Captioned = true, int Slot = -1, bool Open = false, Node? Parent = null);   // CHANGED (round 48): Fill, Captioned; (round 51) Slot
    private static readonly uint FreeSpaceColor = Pack(Parse("#35413B"));   // quiet: room, not content
    private static readonly uint OtherSpaceColor = GroupColor;               // the grey of "smaller items"
    private const int FreeSpaceId = -900_001, OtherSpaceId = -900_002;
    // CHANGED (round 48): every region out past the files - this drive's free and unindexed space, the
    // computer, and the other drives and the items on them - has an id in this range. Which drive a region
    // belongs to is kept in the drive view's Owners.
    private const int ComputerId = -900_003, DriveRegionLastId = -3_000_000;
    private static bool IsDriveRegion(Node node) => node.Item.Id <= FreeSpaceId && node.Item.Id > DriveRegionLastId;

    /// <summary>NEW (round 47): another drive that can be opened, with its size, for zooming out past this one.
    /// CHANGED (round 48): with a picture of its folders and files when it is indexed.</summary>
    internal readonly record struct OtherDrive(string Root, string Label, long Total, long Free, bool Indexed, DrivePreview? Preview = null,
        DriveHardware Hardware = default);   // CHANGED (round 51): what the drive is, to draw it as that

    // NEW (round 51): what the open drive is.
    private DriveHardware _thisHardware;

    // NEW (round 54): the computer's own parts, drawn around the drives when zoomed all the way out.
    private PcParts _pcParts = PcParts.Unknown;

    internal void SetPcParts(PcParts parts)
    {
        if (ReferenceEquals(_pcParts, parts)) return;
        _pcParts = parts;
        _driveView = null;
        _target = Clamp(_target);
        _camera = Clamp(_camera);
        _detailRevision++;
        RequestFrame();
    }

    // NEW (round 59): what is in memory, read in the background while the memory is on screen (MemoryMap).
    // CHANGED (round 60): live - read every second, but only while you are looking into the memory: its
    // contents on screen (the sticks faded away), the window not minimized, the camera still. Anywhere else
    // nothing is read and nothing redrawn.
    // CHANGED (round 62): the processor the same way (CpuMap), each on its own.
    private MemorySnapshot? _memory;
    private CpuSnapshot? _cpu;   // NEW (round 62)
    private readonly MemoryBook _memoryBook = new();
    private const int MemoryIdFirst = -2_000_000, MemoryIdLast = -2_990_000;
    private readonly DispatcherTimer _memoryTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly LiveReading<MemorySnapshot> _memoryLive = new(MemoryMap.Read);
    private readonly LiveReading<CpuSnapshot> _cpuLive = new(CpuMap.Read);

    // NEW (round 62): one live reading's state: whether it is being read, when it last was, and one read while
    // the camera was moving (or you looked away), shown once it stops.
    private sealed class LiveReading<T>(Func<T> read) where T : class
    {
        public readonly Func<T> Read = read;
        public bool Reading, Failed;
        public double ReadAt = double.NegativeInfinity;
        public T? Waiting;
    }

    // NEW (round 60): what stays the same between readings of the memory, so the live view holds still: each
    // block's id (the same program or file keeps its node and label), each program's colour, and each
    // layout's arrangement (StableTreemap).
    private sealed class MemoryBook
    {
        private readonly Dictionary<string, int> _ids = [];
        private readonly Dictionary<string, int> _hues = [];
        private readonly Dictionary<string, StableTreemap> _layouts = [];
        private int _next = MemoryIdFirst;

        public int Id(string key)
        {
            if (!_ids.TryGetValue(key, out var id)) _ids[key] = id = _next--;
            return id;
        }

        // Programs take the palette's colours in the order they are first seen (largest first), and keep them.
        public uint Hue(string key)
        {
            if (!_hues.TryGetValue(key, out var hue)) _hues[key] = hue = _hues.Count;
            return BranchColors[hue % BranchColors.Length];
        }

        public List<StableTile> Arrange(string plan, IReadOnlyList<(string Key, long Bytes)> items, Rect area)
        {
            if (!_layouts.TryGetValue(plan, out var layout)) _layouts[plan] = layout = new StableTreemap();
            return layout.Arrange(items, area.X, area.Y, area.Width, area.Height);
        }

        // Before each build: forget programs that ended, and start the ids over before they run out.
        public void Prune(MemorySnapshot snapshot)
        {
            var running = snapshot.Programs.Select(program => "f|p|" + program.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var plan in _layouts.Keys.Where(plan => plan.StartsWith("f|", StringComparison.Ordinal) && !running.Contains(plan)).ToArray())
                _layouts.Remove(plan);
            if (_next < MemoryIdLast + 200_000 || _ids.Count > 150_000) { _ids.Clear(); _next = MemoryIdFirst; }
            if (_hues.Count > 4096) _hues.Clear();
        }
    }

    internal void SetMemory(MemorySnapshot snapshot)
    {
        // Nothing to redraw when nothing has moved by as much as a pixel's worth.
        if (_memory is { } shown && Unchanged(shown, snapshot)) return;
        _memoryBook.Prune(snapshot);
        _memory = snapshot;
        _hoverText = null;   // the card under the pointer shows the new numbers
        _driveView = null;
        _target = Clamp(_target);
        _camera = Clamp(_camera);
        _detailRevision++;
        RequestFrame();

        static bool Unchanged(MemorySnapshot a, MemorySnapshot b)
        {
            var step = Math.Max(1L << 20, a.Total / 4000);
            if (Math.Abs(a.Available - b.Available) > step || a.Programs.Length != b.Programs.Length) return false;
            var before = a.Programs.ToDictionary(program => program.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var program in b.Programs)
                if (!before.TryGetValue(program.Key, out var old) || Math.Abs(old.Bytes - program.Bytes) > step || old.Files.Length != program.Files.Length) return false;
            return true;
        }
    }

    // NEW (round 62): what the processor is doing. Every reading changes it, so every one is drawn.
    internal void SetCpu(CpuSnapshot snapshot)
    {
        _cpu = snapshot;
        _hoverText = null;
        _driveView = null;
        _target = Clamp(_target);
        _camera = Clamp(_camera);
        _detailRevision++;
        RequestFrame();
    }

    // NEW (round 60): whether you are looking into a part now - its insides on screen, big enough to read,
    // not hidden under its lid, in a window that is not minimized.
    // CHANGED (round 62): for any part (the memory, the processor).
    private bool LookingInto(Rect area)
    {
        if (_disposed || area.IsEmpty || !IsVisible || Window.GetWindow(this) is { WindowState: WindowState.Minimized }) return false;
        if (!InDriveView) return false;
        var screen = WorldToDisplay(area);
        return screen.IntersectsWith(_view) && screen.Width >= 48 && LidAlpha(area) < .9;
    }

    private void PollLive()
    {
        if (_disposed || CurrentDrive() is not { } drive) return;
        Poll(_memoryLive, drive.MemoryArea, SetMemory);
        Poll(_cpuLive, drive.CpuArea, SetCpu);
    }

    // Reads a part once a second while you are looking into it - and not while the camera is moving, so a
    // new picture never lands mid-zoom; one read meanwhile waits for the camera to stop.
    private void Poll<T>(LiveReading<T> live, Rect area, Action<T> apply) where T : class
    {
        if (live.Reading || !LookingInto(area)) return;
        if (IsAnimating || _dragging || SecondsSinceInteraction < .4) return;
        if (live.Waiting is { } waiting) { live.Waiting = null; apply(waiting); return; }
        if (Now - live.ReadAt < (live.Failed ? 5 : .9)) return;
        live.Reading = true;
        Task.Run(live.Read).ContinueWith(task => Dispatcher.InvokeAsync(() =>
        {
            _ = task.Exception;
            live.Reading = false;
            live.ReadAt = Now;
            live.Failed = task.Status != TaskStatus.RanToCompletion;
            if (_disposed || live.Failed) return;
            if (!LookingInto(area) || IsAnimating || _dragging || SecondsSinceInteraction < .4) live.Waiting = task.Result;
            else apply(task.Result);
        }));
    }

    internal void SetThisHardware(DriveHardware hardware)
    {
        if (_thisHardware == hardware) return;
        _thisHardware = hardware;
        _driveView = null;
        _target = Clamp(_target);
        _camera = Clamp(_camera);
        _detailRevision++;
        RequestFrame();
    }

    /// <summary>NEW (round 48): the width-to-height ratio of the map's world, which drive previews are laid out at.</summary>
    internal double WorldAspect => _root?.Bounds.Width is > 0 and var width ? width : 1.6;
    private OtherDrive[] _otherDrives = [];

    /// <summary>NEW (round 47): raised when a click out at the machine picks another drive.</summary>
    internal event Action<string, string?>? DriveRequested;   // CHANGED (round 48): the drive, and a folder on it to open

    /// <summary>NEW (round 47): the other drives to show beside this one when zoomed all the way out.</summary>
    internal void SetOtherDrives(IReadOnlyList<OtherDrive> drives)
    {
        // CHANGED (round 48): free space on the other drives moves by a few megabytes all the time; that
        // alone is not worth rebuilding the machine view (and every node on it) for.
        if (drives.Count == _otherDrives.Length && drives.Select((drive, i) => (drive, old: _otherDrives[i])).All(pair =>
                pair.drive with { Free = 0 } == pair.old with { Free = 0 } && Math.Abs(pair.drive.Free - pair.old.Free) < 256L << 20))
            return;
        _otherDrives = [.. drives];
        _driveView = null;
        _target = Clamp(_target);
        _camera = Clamp(_camera);
        _detailRevision++;
        RequestFrame();
    }

    /// <summary>NEW (round 44): the size and free space of the drive the current source is the root of.</summary>
    internal void SetDriveSpace((long Total, long Free)? space)
    {
        if (_driveSpace == space) return;
        // NEW (round 49): free space moves by a few megabytes all the time; rebuilding the drive view for
        // that made everything out there flicker.
        if (_driveSpace is { } known && space is { } next && known.Total == next.Total && Math.Abs(known.Free - next.Free) < 256L << 20) return;
        _driveSpace = space is { Total: > 0 } ? space : null;
        _driveView = null;
        _target = Clamp(_target);
        _camera = Clamp(_camera);
        _detailRevision++;
        RequestFrame();
    }

    private DriveView? CurrentDrive()
    {
        if (_root is null || _driveSpace is not { } space) return null;
        if (_driveView is { } view && ReferenceEquals(view.Root, _root) && view.Total == space.Total && view.Free == space.Free) return view;
        return _driveView = BuildDriveView(_root, space.Total, space.Free, _otherDrives, _driveView, _thisHardware, _pcParts, _memory, _cpu, _memoryBook);   // CHANGED (round 59): memory; (round 60) its book; (round 62) the processor
    }

    private static DriveView? BuildDriveView(Node root, long total, long free, OtherDrive[] others, DriveView? previous, DriveHardware hardware, PcParts parts,
        MemorySnapshot? memorySnapshot, CpuSnapshot? cpuSnapshot, MemoryBook memoryBook)   // CHANGED (round 59): what is in memory; (round 60) what stays put in it
    {
        var used = root.Item.Bytes;
        if (used <= 0) return null;
        free = Math.Clamp(free, 0, total);
        var other = Math.Max(0, total - free - used);
        var whole = (double)used + other + free;
        var k = Math.Max(1, Math.Sqrt(whole / used));
        if (k < 1.001 && others.Length == 0) return null;   // the files are the drive, and there is nowhere else to go
        var r = root.Bounds;
        var world = new Rect(r.X, r.Y, r.Width * k, r.Height * k);
        var regions = new List<DriveRegion>();
        var solids = new List<Solid>();
        var owners = new Dictionary<int, (int Drive, string? Path)>();
        var slots = new int[others.Length + 1];
        // CHANGED (round 48): the split around the files is shared with the other drives' previews.
        var (otherParts, freeParts) = Surround(r, k, r.Width * r.Height * other / used);
        if (free > 0) regions.Add(Region(FreeSpaceId, "Free space", free, freeParts, FreeSpaceColor, whole));
        if (other > 0) regions.Add(Region(OtherSpaceId, "Used, not indexed", other, otherParts, OtherSpaceColor, whole));

        // NEW (round 47): the other drives, at the same scale - a drive twice the size has twice the area.
        // CHANGED (round 49): the whole machine is laid out the same way whichever drive is open - every
        // drive in one row in drive-letter order, standing on one line, with every size (gaps, name plates,
        // cables, the computer) taken from the largest drive rather than the one open. Opening another
        // drive then changes nothing on screen but which drive the files are real in; the picture is only
        // re-placed around it. An indexed drive shows its folders and files the way this one does (files in
        // the top-left corner, then what the index does not cover, then free space); one not indexed yet is
        // its used space beside its free space. A drive far smaller than the largest keeps a minimum size so
        // it can still be found and clicked.
        var drives = new List<DriveBox> { new(-1, world, r) };
        foreach (var index in Enumerable.Range(0, regions.Count)) regions[index] = regions[index] with { Slot = 0 };   // this drive's space
        var underlay = new List<Solid>();
        var covers = new List<CoverShape>();
        var coverTexts = new List<CoverText?> { null };
        var coverBounds = new List<Rect> { Rect.Empty };
        var coverNodes = new List<Node?> { null };
        var coverIds = new HashSet<int>();

        // CHANGED (round 56): the drives back in a row, easy to see, each cabled down to the computer - which
        // is now its motherboard, drawn open: its maker and model, the processor under its cooler, the memory
        // in its slots and the graphics card, all read from the machine (PcHardware). Network drives sit on
        // their servers after the local drives, the servers cabled to the board's network port. Sizes come
        // from the largest drive, so the picture is the same whichever drive is open.
        // A drive whose hardware has not been read yet (the first build) is drawn as a plain drive.
        static DriveHardware Known(DriveHardware device, string at) => device.Model is null ? new DriveHardware(DriveKind.Unknown, at) : device;
        hardware = Known(hardware, root.Item.Name);
        var entries = others.Select((drive, index) => (Index: index, drive.Root, Hardware: Known(drive.Hardware, drive.Root), drive.Total))
            .Append((Index: -1, Root: root.Item.Name, Hardware: hardware, Total: total))
            .OrderBy(entry => entry.Hardware.Kind == DriveKind.Network ? 1 : 0)
            .ThenBy(entry => entry.Hardware.Kind == DriveKind.Network ? entry.Hardware.Server ?? entry.Root : "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Root, StringComparer.OrdinalIgnoreCase).ToList();
        var perRootByte = world.Width / Math.Sqrt(Math.Max(1d, whole));   // world width per sqrt(byte)
        var unit = Math.Max(world.Width, others.Length == 0 ? 0 : others.Max(drive => Math.Sqrt(Math.Max(1d, drive.Total))) * perRootByte);
        var gap = unit * .08;
        var plate = unit * .025;
        var wire = unit * .003;
        var plug = new Size(unit * .025, unit * .01);
        var count = entries.Count;
        var boxes = new Rect[count];
        var frames = new Thickness[count];
        // NEW (round 52): where each drive's cable plugs in - filled in by Device().
        var portsOf = new List<(double X, double Y, uint Wire)>[count];
        var here = entries.FindIndex(entry => entry.Index == -1);
        DriveKind KindOf(int i) => entries[i].Hardware.Kind;
        boxes[here] = world;
        frames[here] = Frame(hardware.Kind, world.Height, false);
        for (var i = here + 1; i < count; i++)
        {
            var (width, height) = Measure(entries[i].Total);
            frames[i] = Frame(KindOf(i), height, false);
            var x = boxes[i - 1].Right + frames[i - 1].Right + Spacing(i - 1, i) + frames[i].Left;
            boxes[i] = new Rect(x, world.Bottom - height, width, height);
        }
        for (var i = here - 1; i >= 0; i--)
        {
            var (width, height) = Measure(entries[i].Total);
            frames[i] = Frame(KindOf(i), height, false);
            var x = boxes[i + 1].Left - frames[i + 1].Left - Spacing(i, i + 1) - frames[i].Right - width;
            boxes[i] = new Rect(x, world.Bottom - height, width, height);
        }
        var machine = world;

        // ---- the drives
        var slotOf = new int[count];
        var row = Rect.Empty;
        for (var i = 0; i < count; i++)
        {
            var box = boxes[i];
            var outline = new Rect(box.X - frames[i].Left, box.Y - frames[i].Top, box.Width + frames[i].Left + frames[i].Right, box.Height + frames[i].Top + frames[i].Bottom);
            machine.Union(outline);
            var entry = entries[i];
            string name;
            long driveTotal, driveFree;
            if (entry.Index < 0)
            {
                slotOf[i] = 0;
                name = root.Item.Name.TrimEnd('\\') is { Length: > 0 } letter ? $"{letter}\\ · this drive" : "This drive";
                driveTotal = total;
                driveFree = free;
            }
            else
            {
                var drive = others[entry.Index];
                var first = regions.Count;
                var files = AddDrive(drive, entry.Index, box);
                drives.Add(new DriveBox(entry.Index, box, files));
                slotOf[i] = drives.Count - 1;
                for (var j = first; j < regions.Count; j++) regions[j] = regions[j] with { Slot = slotOf[i] };
                coverTexts.Add(null); coverBounds.Add(Rect.Empty); coverNodes.Add(null);
                name = string.IsNullOrWhiteSpace(drive.Label) ? drive.Root : $"{drive.Root}  {drive.Label}";
                driveTotal = drive.Total;
                driveFree = drive.Free;
            }
            var plateBar = new Rect(outline.X, outline.Y - plate * 1.3, outline.Width, plate);
            machine.Union(plateBar);
            row.Union(outline);
            row.Union(plateBar);
            var plateRegion = Region(entry.Index < 0 ? -1_000_000 : FixedId(entry.Index, 0), name, driveTotal, [plateBar], NamePlate, driveTotal);
            if (entry.Index >= 0) Own(plateRegion, entry.Index, null);
            else regions.Add(plateRegion);
            Device(i, slotOf[i], entry.Index, entry.Hardware, box, frames[i], driveTotal, driveFree);
        }

        // ---- the motherboard under the local drives, a server under each group of network drives
        var groups = new List<(string Key, int First, int Last)>();
        for (var i = 0; i < count; i++)
            if (groups.Count > 0 && groups[^1].Key == Group(i)) groups[^1] = groups[^1] with { Last = i };
            else groups.Add((Group(i), i, i));
        var rowBottom = Enumerable.Range(0, count).Max(i => boxes[i].Bottom + frames[i].Bottom);
        var tallest = boxes.Max(box => box.Height);
        var drop = Math.Max(tallest * .3, unit * .3);
        var hubTop = rowBottom + drop;
        Rect? motherboard = null;
        var servers = new List<Rect>();
        double[] sataPorts = [];
        foreach (var (key, first, last) in groups)
        {
            var left = boxes[first].Left - frames[first].Left;
            var right = boxes[last].Right + frames[last].Right;
            if (key == "local")
            {
                var boardWidth = Math.Clamp((right - left) * .6, unit * 1.8, unit * 3.2);
                var board = new Rect((left + right) / 2 - boardWidth / 2, hubTop, boardWidth, boardWidth * .62);
                motherboard = board;
                // The drives' cables land in SATA ports along the board's top edge, right of the memory (whose
                // eight slots at most reach .7 of the board's width).
                sataPorts = Cables(first, last, board, Math.Min(plug.Width, board.Width * .25 / (last - first + 1) * .7), rowBottom, drop, .72, .97);
                machine.Union(board);
            }
            else
            {
                var hubWidth = Math.Clamp((right - left) * .45, unit * .3, unit * .7);
                var hub = new Rect((left + right) / 2 - hubWidth / 2, hubTop, hubWidth, hubWidth * .5);
                Cables(first, last, hub, Math.Min(plug.Width, hub.Width * .76 / (last - first + 1) * .8), rowBottom, drop, .12, .88);
                Server(hub, entries[first].Hardware.Server ?? key[4..], last - first + 1, Enumerable.Range(first, last - first + 1).Sum(i => entries[i].Total));
                servers.Add(hub);
                machine.Union(hub);
            }
        }
        // The open drive is a share and nothing local is shown: the board still stands left of the servers.
        var host = motherboard ?? new Rect(servers.Min(hub => hub.Left) - unit * 2.2 - gap * 3, hubTop, unit * 2.2, unit * 2.2 * .62);
        machine.Union(host);
        // Network cables from the board's underside to each server's: nested lanes, so they never cross.
        var ordered = servers.OrderBy(hub => Math.Abs(Centre(hub) - Centre(host))).ToArray();
        for (var nth = 0; nth < ordered.Length; nth++)
        {
            var server = ordered[nth];
            var toRight = Centre(server) > Centre(host);
            var from = toRight ? host.Right - host.Width * (.1 + .05 * nth) : host.Left + host.Width * (.1 + .05 * nth);
            var to = toRight ? server.Left + server.Width * .15 : server.Right - server.Width * .15;
            var lane = Math.Max(host.Bottom, server.Bottom) + drop * (.2 + .18 * nth);
            var ethernet = wire * 1.8;
            underlay.Add(new Solid(new Rect(from - ethernet / 2, host.Bottom - ethernet, ethernet, lane - host.Bottom + ethernet * 1.5), Ethernet, SolidKind.Wire));
            underlay.Add(new Solid(new Rect(Math.Min(from, to) - ethernet / 2, lane - ethernet / 2, Math.Abs(to - from) + ethernet, ethernet), Ethernet, SolidKind.Wire));
            underlay.Add(new Solid(new Rect(to - ethernet / 2, server.Bottom, ethernet, lane - server.Bottom + ethernet / 2), Ethernet, SolidKind.Wire));
            underlay.Add(new Solid(new Rect(to - plug.Width / 2, server.Bottom, plug.Width, plug.Height * 1.4), CableEnd, SolidKind.Plain));
            machine.Union(new Rect(Math.Min(from, to), lane, Math.Abs(to - from), wire));
        }

        // ---- the board and its parts.
        // CHANGED (round 58): drawn as the map draws everything else - flat tiles with the map's gaps, named
        // the way its blocks are named - instead of the overlay's shaded picture of a board (round 56). The
        // board is a block with the parts on it, tagged in its corner like an open folder; the processor,
        // memory and graphics card take the map's own colours; the rest of the board is the grey of the
        // map's "smaller items".
        var capacity = total + others.Sum(drive => drive.Total);
        var localDrives = entries.Count(entry => entry.Hardware.Kind != DriveKind.Network);
        var memoryArea = Rect.Empty;                    // NEW (round 59)
        var lids = new List<LidPart>();
        var cpuArea = Rect.Empty;                       // NEW (round 62)
        var zoomTiles = new Dictionary<int, Rect>();
        Board(host, sataPorts, capacity, localDrives);
        machine.Inflate(gap, gap);
        return new DriveView(root, total, free, world, [.. regions], machine, others)
        {
            Solids = [.. solids], Owners = owners, Drives = [.. drives],
            Nodes = regions.Select(region => region.Label).Concat(coverNodes.OfType<Node>()).Concat(lids.Select(lid => lid.Node)).ToDictionary(node => node.Item.Id),
            Underlay = [.. underlay], Covers = [.. covers], CoverTexts = [.. coverTexts], CoverBounds = [.. coverBounds], CoverNodes = [.. coverNodes],
            Row = Rect.Inflate(row, gap, gap),
            MemoryArea = memoryArea, Lids = [.. lids], CpuArea = cpuArea, ZoomTiles = zoomTiles,   // NEW (round 59)
        };

        // NEW (round 58): the board's parts as regions, placed as shares of the board. The board's top strip is
        // left clear for its name tag; the memory's likewise for its own.
        void Board(Rect b, double[] ports, long capacity, int localDrives)
        {
            double W = b.Width, H = b.Height;
            Rect At(double x, double y, double w, double h) => new(b.X + W * x, b.Y + H * y, W * w, H * h);
            var boardName = string.Join(' ', new[] { Maker(parts.BoardMaker), parts.Board }.Where(part => part.Length > 0)) is { Length: > 0 } named ? named : "Motherboard";
            var boardRegion = Region(ComputerId, boardName, capacity, [b], BoardTile, capacity,
                $"Motherboard · {localDrives} drive{(localDrives == 1 ? "" : "s")} · {DiskUsageSnapshot.FormatBytes(capacity)} in all", "Motherboard") with { Open = true };
            regions.Insert(0, boardRegion);
            var onBoard = boardRegion.Label;   // NEW (round 61): what the parts' captions keep clear of

            void Part(string name, Rect where, uint color, string detail, bool captioned = true)
                => regions.Add(Region(NextId(), name, 0, [where], color, 1, detail) with { Captioned = captioned, Parent = onBoard });

            // Where the drives' cables land: a row of ports along the top edge.
            foreach (var x in ports) Part("SATA port", new Rect(x - W * .014, b.Y, W * .028, H * .055), PortTile, "Where a drive's cable connects", false);

            Part("Back panel", At(.02, .12, .08, .52), GroupColor, "USB, network and audio ports");
            Part("Power delivery", At(.12, .19, .035, .32), GroupColor, "Voltage regulators for the processor", false);
            Part("Power delivery", At(.175, .12, .2, .05), GroupColor, "Voltage regulators for the processor", false);
            // CHANGED (round 62): the processor under its lid (the heatspreader), which fades as the camera
            // closes in to show the die and what is running on it.
            var cpu = At(.175, .19, .2, .32);
            var processor = Region(NextId(), parts.Processor, 0, [cpu], CpuTile, 1, $"Processor · {parts.Threads} threads", $"{parts.Threads} threads") with { Open = true, Parent = onBoard };
            regions.Add(processor);
            var die = new Rect(cpu.X + cpu.Width * .04, cpu.Y + cpu.Height * .17, cpu.Width * .92, cpu.Height * .79);
            cpuArea = die;
            zoomTiles[processor.Label.Item.Id] = die;
            var heatspreader = Region(NextId(), parts.Processor, 0, [die], LidTile, 1, $"Processor · {parts.Threads} threads · Zoom in to see the die").Label;
            lids.Add(new LidPart(die, die, LidTile, LidTile, heatspreader, ""));
            zoomTiles[heatspreader.Item.Id] = die;
            if (cpuSnapshot is { } cpuNow) CpuContents(die, cpuNow, processor.Label);
            else
            {
                var waiting = Region(memoryBook.Id("c|reading"), "Reading the processor…", 0, [die], DieTile, 1,
                    "Its cores, how busy each one is, and what is running appear here in a moment") with { Parent = processor.Label };
                regions.Add(waiting);
                zoomTiles[waiting.Label.Item.Id] = die;
            }
            Part("M.2", At(.12, .56, .255, .06), GroupColor, "M.2 slot");
            Part("Chipset", At(.76, .66, .13, .21), GroupColor, "Chipset");
            Part("Power", At(.93, .14, .045, .34), GroupColor, "24-pin power connector", false);

            // Memory: a block holding its slots, a module in each filled one - the second of each pair first,
            // the way boards are filled.
            // CHANGED (round 59): the slots are now the memory's cover. Under them is what the memory holds -
            // the programs running and the files each has loaded - and the sticks fade away as the camera
            // closes in, the way a drive's cover does.
            var slots = Math.Clamp(parts.MemorySlots, 2, 8);
            var modules = parts.Modules;
            var moduleSize = modules.Length > 0 ? modules.Max(module => module.Bytes) : 0;
            var speed = modules.Length > 0 ? modules.Max(module => module.Speed) : 0;
            var memory = At(.42, .12, .28, .46);
            var container = Region(NextId(), "Memory", parts.MemoryBytes, [memory], MemoryTile, Math.Max(1, parts.MemoryBytes),
                string.Join(" · ", new[]
                {
                    parts.MemoryBytes > 0 ? Gigabytes(parts.MemoryBytes) : "",
                    modules.Length > 0 ? $"{modules.Length} × {Gigabytes(moduleSize)}" : "",
                    speed > 0 ? $"{speed} MT/s" : "",
                }.Where(part => part.Length > 0)) is { Length: > 0 } about ? about : "Memory",
                parts.MemoryBytes > 0 ? Gigabytes(parts.MemoryBytes) : "") with { Open = true, Parent = onBoard };
            regions.Add(container);
            var area = new Rect(memory.X + memory.Width * .03, b.Y + H * .21, memory.Width * .94, memory.Bottom - b.Y - H * .235);
            memoryArea = area;
            zoomTiles[container.Label.Item.Id] = area;
            var filled = Enumerable.Range(0, slots).OrderBy(k => (k % 2 == 1 ? 0 : 1, k)).Take(Math.Min(modules.Length, slots)).Order().ToArray();
            var step = area.Width / slots;
            for (var k = 0; k < slots; k++)
            {
                var stick = new Rect(area.X + step * k + step * .12, area.Y, step * .76, area.Height);
                var nth = Array.IndexOf(filled, k);
                var label = nth < 0 ? "" : Gigabytes(modules[nth].Bytes);
                var node = nth < 0
                    ? Region(NextId(), "Empty slot", 0, [stick], PortTile, 1, "No memory in this slot").Label
                    : Region(NextId(), label, modules[nth].Bytes, [stick], StickTile, 1, string.Join(" · ", new[]
                    {
                        modules[nth].Maker, modules[nth].Speed > 0 ? $"{modules[nth].Speed} MT/s" : "", modules[nth].Slot, "Zoom in to see what it holds",
                    }.Where(part => part.Length > 0))).Label;
                lids.Add(new LidPart(stick, area, nth < 0 ? PortTile : StickTile, MemoryTile, node, label));
                zoomTiles[node.Item.Id] = area;
            }
            if (memorySnapshot is { } snapshot) MemoryContents(area, snapshot, parts.MemoryBytes > 0 ? parts.MemoryBytes : snapshot.Total, container.Label);
            else
            {
                var reading = Region(memoryBook.Id("m|reading"), "Reading what is in memory…", 0, [area], Shade(MemoryTile, 1.3), 1,
                    "The programs running, and the files each has loaded, appear here in a moment") with { Parent = container.Label };
                regions.Add(reading);
                zoomTiles[reading.Label.Item.Id] = area;
            }

            if (parts.Graphics.Length > 0)
                Part(parts.Graphics, At(.1, .68, .6, .24), GpuTile, parts.GraphicsMemory > 0 ? $"Graphics · {Gigabytes(parts.GraphicsMemory)}" : "Graphics");
        }

        // NEW (round 62): the processor with its lid off - the die, laid out as the chip is built, above what
        // is running on it. The die holds its core complexes (one per L3 cache: a Ryzen's CCXs; an Intel has
        // one), each its cores over its L3; an Intel's efficiency cores sit in their fours around the L2 they
        // share. Each core holds its threads and its own caches. Cores and threads are lit by how busy they
        // are, from cool to warm. It is drawn as the chip is organised, not to scale.
        void CpuContents(Rect area, CpuSnapshot snapshot, Node within)
        {
            var topology = snapshot.Topology;
            var gap = Math.Min(area.Width, area.Height) * .02;
            var dieRect = new Rect(area.X, area.Y, area.Width, area.Height * .6 - gap / 2);
            var runRect = new Rect(area.X, area.Y + area.Height * .6 + gap / 2, area.Width, area.Height * .4 - gap / 2);
            double Load(IEnumerable<int> threads) => threads.Select(thread => snapshot.Load.GetValueOrDefault(thread)).DefaultIfEmpty(0).Average();
            double Clock(IEnumerable<int> threads) => threads.Select(thread => snapshot.Mhz.GetValueOrDefault(thread)).DefaultIfEmpty(0).Max();
            static string Busy(double load) => $"{load * 100:0}%";
            static string Ghz(double mhz) => mhz > 0 ? $" · {mhz / 1000:0.00} GHz" : "";

            var l3s = topology.Caches.Where(cache => cache.Level == 3 && cache.Threads.Length > 0).OrderBy(cache => cache.Threads.Min()).ToArray();
            // Each core in one complex only, even should two L3 entries claim it.
            var placed = new HashSet<CpuCore>();
            var clusters = new List<(CpuCache? Cache, CpuCore[] Cores)>();
            foreach (var cache in l3s)
            {
                var under = topology.Cores.Where(core => !placed.Contains(core) && core.Threads.All(cache.Threads.Contains)).ToArray();
                if (under.Length == 0) continue;
                placed.UnionWith(under);
                clusters.Add((cache, under));
            }
            var loose = topology.Cores.Where(core => !placed.Contains(core)).ToArray();
            if (loose.Length > 0) clusters.Add((null, loose));

            var dieRegion = Region(memoryBook.Id("c|die"), "Die", 0, [dieRect], DieTile, 1,
                $"{topology.Cores.Length} cores · {topology.Threads.Length} threads" + (topology.BaseMhz > 0 ? $" · {topology.BaseMhz / 1000d:0.0#} GHz base" : "") +
                $" · {Busy(snapshot.Total)} busy", Busy(snapshot.Total), live: true) with { Open = true, Parent = within };
            regions.Add(dieRegion);
            zoomTiles[dieRegion.Label.Item.Id] = dieRect;
            var dieInside = Rect.Inflate(dieRect, -gap, -gap);
            var (columns, rows) = Grid(clusters.Count, dieInside);
            for (var i = 0; i < clusters.Count; i++)
                Cluster(Cell(dieInside, i, columns, rows, gap), clusters[i].Cache, clusters[i].Cores, i, clusters.Count, dieRegion.Label);
            Running(runRect, within);

            void Cluster(Rect cell, CpuCache? l3, CpuCore[] cores, int number, int count, Node parent)
            {
                var body = cell;
                var inside = parent;
                if (count > 1)
                {
                    var threads = cores.SelectMany(core => core.Threads).ToArray();
                    var complex = Region(memoryBook.Id($"c|ccx|{number}"), $"Core complex {number + 1}", 0, [cell], ClusterTile, 1,
                        $"{cores.Length} cores" + (l3 is null ? "" : $" sharing {CacheSize(l3.Bytes)} of L3 cache") + $" · {Busy(Load(threads))} busy",
                        Busy(Load(threads)), live: true) with { Open = true, Parent = parent };
                    regions.Add(complex);
                    zoomTiles[complex.Label.Item.Id] = cell;
                    inside = complex.Label;
                    body = Rect.Inflate(cell, -gap * .6, -gap * .6);
                }
                var coresRect = body;
                if (l3 is not null)
                {
                    coresRect = new Rect(body.X, body.Y, body.Width, body.Height * .76);
                    var l3Rect = new Rect(body.X, body.Y + body.Height * .78, body.Width, body.Height * .22);
                    var cache = Region(memoryBook.Id($"c|l3|{number}"), "L3 cache", l3.Bytes, [l3Rect], CacheTile, 1,
                        $"{CacheSize(l3.Bytes)} · shared by {cores.Length} core{(cores.Length == 1 ? "" : "s")}", live: true) with { Parent = inside };
                    regions.Add(cache);
                    zoomTiles[cache.Label.Item.Id] = l3Rect;
                }
                // Performance cores stand alone; efficiency cores come in the groups that share an L2.
                var units = new List<(CpuCore[] Cores, CpuCache? L2)>();
                var grouped = new HashSet<CpuCore>();
                foreach (var core in cores.OrderBy(core => core.Threads.Min()))
                {
                    if (grouped.Contains(core)) continue;
                    var l2 = topology.Caches.FirstOrDefault(cache => cache.Level == 2 && core.Threads.All(cache.Threads.Contains));
                    var sharing = l2 is null ? [core] : cores.Where(other => other.Threads.All(l2.Threads.Contains)).ToArray();
                    if (sharing.Length > 1) { units.Add((sharing, l2)); grouped.UnionWith(sharing); }
                    else units.Add(([core], null));
                }
                var (unitColumns, unitRows) = Grid(units.Count, coresRect);
                var unitGap = gap * .5;
                for (var u = 0; u < units.Count; u++)
                {
                    var unitCell = Cell(coresRect, u, unitColumns, unitRows, unitGap);
                    if (units[u].Cores.Length == 1) Core(unitCell, units[u].Cores[0], inside);
                    else Module(unitCell, units[u].Cores, units[u].L2!, inside);
                }
            }

            void Module(Rect cell, CpuCore[] cores, CpuCache l2, Node parent)
            {
                var threads = cores.SelectMany(core => core.Threads).ToArray();
                var efficient = topology.Hybrid && cores[0].Efficiency != topology.FastClass;
                var module = Region(memoryBook.Id($"c|module|{cores[0].Number}"), efficient ? "Efficiency cores" : cores.Length == 2 ? "Core pair" : "Core group", 0, [cell], ClusterTile, 1,
                    $"{cores.Length} {(efficient ? "efficiency " : "")}cores sharing {CacheSize(l2.Bytes)} of L2 cache · {Busy(Load(threads))} busy", Busy(Load(threads)), live: true)
                    with { Open = true, Parent = parent };
                regions.Add(module);
                zoomTiles[module.Label.Item.Id] = cell;
                var inner = Rect.Inflate(cell, -gap * .4, -gap * .4);
                var coresRect = new Rect(inner.X, inner.Y, inner.Width, inner.Height * .74);
                var l2Rect = new Rect(inner.X, inner.Y + inner.Height * .77, inner.Width, inner.Height * .23);
                var cache = Region(memoryBook.Id($"c|l2|{cores[0].Number}"), "L2 cache", l2.Bytes, [l2Rect], CacheTile, 1,
                    $"{CacheSize(l2.Bytes)} · shared by these {cores.Length} cores", live: true) with { Parent = module.Label };
                regions.Add(cache);
                zoomTiles[cache.Label.Item.Id] = l2Rect;
                var (columns, rows) = Grid(cores.Length, coresRect);
                for (var c = 0; c < cores.Length; c++) Core(Cell(coresRect, c, columns, rows, gap * .3), cores[c], module.Label);
            }

            void Core(Rect cell, CpuCore core, Node parent)
            {
                var load = Load(core.Threads);
                var clock = Clock(core.Threads);
                var kind = !topology.Hybrid ? "Core" : core.Efficiency == topology.FastClass ? "Performance core" : "Efficiency core";
                var region = Region(memoryBook.Id($"c|core|{core.Number}"), $"Core {core.Number}", 0, [cell], Heat(load), 1,
                    $"{kind} · {Busy(load)} busy{Ghz(clock)} · {core.Threads.Length} thread{(core.Threads.Length == 1 ? "" : "s")}", Busy(load), live: true)
                    with { Open = true, Parent = parent };
                regions.Add(region);
                zoomTiles[region.Label.Item.Id] = cell;
                var inner = Rect.Inflate(cell, -Math.Min(cell.Width, cell.Height) * .05, -Math.Min(cell.Width, cell.Height) * .05);
                if (inner.Width <= 0 || inner.Height <= 0) return;
                // Its threads side by side above its own caches (the L2, and the L1 for data and for instructions).
                var own = topology.Caches.Where(cache => cache.Level <= 2 && cache.Threads.Length > 0 && cache.Threads.All(core.Threads.Contains))
                    .OrderByDescending(cache => cache.Level).ThenByDescending(cache => cache.Kind).ToArray();
                var threadsRect = own.Length == 0 ? inner : new Rect(inner.X, inner.Y, inner.Width, inner.Height * .62);
                var step = threadsRect.Width / core.Threads.Length;
                for (var t = 0; t < core.Threads.Length; t++)
                {
                    var thread = core.Threads[t];
                    var threadLoad = snapshot.Load.GetValueOrDefault(thread);
                    var rect = new Rect(threadsRect.X + step * t, threadsRect.Y, step, threadsRect.Height);
                    var tile = Region(memoryBook.Id($"c|thread|{thread}"), $"Thread {thread}", 0, [rect], Heat(threadLoad), 1,
                        $"Logical processor {thread} · {Busy(threadLoad)} busy{Ghz(snapshot.Mhz.GetValueOrDefault(thread))}", live: true) with { Parent = region.Label };
                    regions.Add(tile);
                    zoomTiles[tile.Label.Item.Id] = rect;
                }
                if (own.Length == 0) return;
                var cachesRect = new Rect(inner.X, inner.Y + inner.Height * .65, inner.Width, inner.Height * .35);
                var weights = own.Select(cache => cache.Level == 2 ? 2d : 1d).ToArray();
                var along = cachesRect.X;
                for (var c = 0; c < own.Length; c++)
                {
                    var cache = own[c];
                    var width = cachesRect.Width * weights[c] / weights.Sum();
                    var rect = new Rect(along, cachesRect.Y, width, cachesRect.Height);
                    along += width;
                    var name = cache.Level == 2 ? "L2" : cache.Kind == 2 ? "L1 data" : cache.Kind == 1 ? "L1 instructions" : $"L{cache.Level}";
                    var tile = Region(memoryBook.Id($"c|cache|{core.Number}|{cache.Level}|{cache.Kind}"), name, cache.Bytes, [rect], CacheTile, 1,
                        $"{name} cache · {CacheSize(cache.Bytes)} · this core's own", live: true) with { Parent = region.Label };
                    regions.Add(tile);
                    zoomTiles[tile.Label.Item.Id] = rect;
                }
            }

            // What is running: every program sized by its share of the time the processor was busy.
            void Running(Rect rect, Node parent)
            {
                var programs = snapshot.Programs;
                var run = Region(memoryBook.Id("c|running"), "What's running", 0, [rect], RunTile, 1,
                    $"{Busy(snapshot.Total)} of the processor in use · {programs.Length} program{(programs.Length == 1 ? "" : "s")}",
                    $"{Busy(snapshot.Total)} busy", live: true) with { Open = true, Parent = parent };
                regions.Add(run);
                zoomTiles[run.Label.Item.Id] = rect;
                var inner = Rect.Inflate(rect, -gap * .5, -gap * .5);
                if (inner.Width <= 0 || inner.Height <= 0) return;
                var shown = programs.Where(program => program.Share >= .0005).ToDictionary(program => "c|p|" + program.Key, StringComparer.Ordinal);
                var rest = programs.Where(program => program.Share < .0005).Sum(program => program.Share);
                var items = shown.Select(pair => (pair.Key, (long)(pair.Value.Share * 1e7))).ToList();
                if (rest > 0) items.Add(("c|others", (long)(rest * 1e7)));
                if (items.Count == 0) items.Add(("c|idle", 1));
                foreach (var tile in memoryBook.Arrange("cpu", items, inner))
                {
                    var at = new Rect(tile.X, tile.Y, tile.Width, tile.Height);
                    DriveRegion block;
                    if (shown.TryGetValue(tile.Key, out var program))
                        block = Region(memoryBook.Id(tile.Key), program.Name, 0, [at], memoryBook.Hue(program.Key), 1,
                            $"{program.Share * 100:0.#}% of the processor · {program.Processes} process{(program.Processes == 1 ? "" : "es")}" +
                            (program.Path is { } path ? $" · {path}" : ""), live: true) with { Parent = run.Label };
                    else if (tile.Key == "c|others")
                        block = Region(memoryBook.Id(tile.Key), "Everything else", 0, [at], GroupColor, 1,
                            $"{rest * 100:0.##}% of the processor · programs using a sliver each", live: true) with { Parent = run.Label };
                    else
                        block = Region(memoryBook.Id(tile.Key), "Idle", 0, [at], FreeSpaceColor, 1, "Nothing is using the processor right now", live: true) with { Parent = run.Label };
                    regions.Add(block);
                    zoomTiles[block.Label.Item.Id] = at;
                }
            }
        }

        // A grid for n equal cells that keeps them near square in a rectangle.
        static (int Columns, int Rows) Grid(int count, Rect rect)
        {
            if (count <= 1) return (1, 1);
            var columns = Math.Clamp((int)Math.Round(Math.Sqrt(count * rect.Width / Math.Max(1e-12, rect.Height))), 1, count);
            return (columns, (count + columns - 1) / columns);
        }

        static Rect Cell(Rect rect, int index, int columns, int rows, double gap)
        {
            var width = (rect.Width - gap * (columns - 1)) / columns;
            var height = (rect.Height - gap * (rows - 1)) / rows;
            return new Rect(rect.X + index % columns * (width + gap), rect.Y + index / columns * (height + gap), Math.Max(0, width), Math.Max(0, height));
        }

        static string CacheSize(long bytes) => bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB" : $"{Math.Max(1, bytes >> 10)} KB";

        // Cool when idle, warm when busy.
        static uint Heat(double load) => Mix(HeatIdle, HeatBusy, Math.Pow(Math.Clamp(load, 0, 1), .75));

        // NEW (round 59): what the memory holds, laid out in its area like a drive's top folder: every
        // program (all its processes together), sized by the memory it takes; then Windows' own share (what
        // is in use but not counted to any program), what is available, and what the hardware keeps. Each
        // program Windows let us look inside holds its files and its working data, the way a folder holds
        // its files.
        void MemoryContents(Rect area, MemorySnapshot snapshot, long installed, Node within)
        {
            var programs = snapshot.Programs;
            var inUse = Math.Max(0, snapshot.Total - snapshot.Available);
            var counted = programs.Sum(program => program.Bytes);
            var blocks = new List<(string Key, string Name, long Bytes, uint Color, string Detail, MemoryProgram? Program)>();
            for (var i = 0; i < programs.Length; i++)
            {
                var program = programs[i];
                var processes = program.Processes == 1 ? "1 process" : $"{program.Processes} processes";
                blocks.Add(("p|" + program.Key, program.Name, program.Bytes, memoryBook.Hue(program.Key),   // CHANGED (round 60): its own colour, kept
                    string.Join(" · ", new[]
                    {
                        MemorySize(program.Bytes), processes, program.Opened ? program.Path ?? "" : "Windows doesn't let other programs look inside",
                    }.Where(part => part.Length > 0)), program));
            }
            if (inUse - counted > 0)
                blocks.Add(("m|windows", "Windows", inUse - counted, GroupColor,
                    $"{MemorySize(inUse - counted)} · Windows itself, its drivers, and memory not counted to any one program", null));
            if (snapshot.Available > 0)
                blocks.Add(("m|available", "Available", snapshot.Available, FreeSpaceColor,
                    $"{MemorySize(snapshot.Available)} · Free, or holding recently used files that Windows hands back the moment it needs the room", null));
            if (installed - snapshot.Total > 16L << 20)
                blocks.Add(("m|reserved", "Reserved by hardware", installed - snapshot.Total, PortTile,
                    $"{MemorySize(installed - snapshot.Total)} · Set aside for the firmware and devices", null));
            var whole = Math.Max(1, installed);
            // CHANGED (round 60): laid out by a StableTreemap, so from one second to the next every block only
            // grows or shrinks where it is.
            var byKey = blocks.ToDictionary(block => block.Key, StringComparer.Ordinal);
            foreach (var tile in memoryBook.Arrange("top", [.. blocks.Select(block => (block.Key, block.Bytes))], area))
            {
                var block = byKey[tile.Key];
                var rect = new Rect(tile.X, tile.Y, tile.Width, tile.Height);
                var open = block.Program is { Opened: true, Files.Length: > 0 };
                var region = Region(memoryBook.Id(block.Key), block.Name, block.Bytes, [rect], block.Color, whole, block.Detail, MemorySize(block.Bytes), live: true) with { Open = open, Parent = within };
                regions.Add(region);
                zoomTiles[region.Label.Item.Id] = rect;
                if (open) MemoryFiles(block.Key, block.Program!, rect, block.Color, region.Label);
            }
        }

        // A program's files and working data, inside its block. The smallest are left out (their share of
        // the room stays, in the program's colour), the way the drive previews leave out what is too small.
        void MemoryFiles(string key, MemoryProgram program, Rect rect, uint hue, Node within)
        {
            const int Shown = 150;
            var inset = Math.Min(rect.Width, rect.Height) * .03;
            var inner = Rect.Inflate(rect, -inset, -inset);
            if (inner.Width <= 0 || inner.Height <= 0) return;
            var shown = program.Files.Take(Shown).ToDictionary(file => $"f|{key}|{file.Path ?? file.Name}", StringComparer.Ordinal);
            var rest = program.Files.Skip(Shown).Sum(file => file.Bytes);
            var items = shown.Select(pair => (pair.Key, pair.Value.Bytes)).ToList();
            items.Add(("\0rest", rest));
            foreach (var tile in memoryBook.Arrange("f|" + key, items, inner))   // CHANGED (round 60): stable, like the programs
            {
                if (!shown.TryGetValue(tile.Key, out var file)) continue;   // the rest: too small to see
                var at = new Rect(tile.X, tile.Y, tile.Width, tile.Height);
                var color = file.Kind is MemoryFileKind.Data or MemoryFileKind.Closed ? Shade(hue, .68) : Shade(hue, .9 * (.84 + .22 * SpreadOf(file.Name)));
                var region = Region(memoryBook.Id(tile.Key), file.Name, file.Bytes, [at], color, Math.Max(1, program.Bytes), FileDetail(file), live: true) with { Parent = within };
                regions.Add(region);
                zoomTiles[region.Label.Item.Id] = at;
            }
        }

        static string FileDetail(MemoryFile file) => file.Kind switch
        {
            MemoryFileKind.Data => $"{MemorySize(file.Bytes)} · The program's own working data: what it has open, made or downloaded",
            MemoryFileKind.Closed => $"{MemorySize(file.Bytes)} · Processes of this program that Windows doesn't let other programs look inside",
            MemoryFileKind.Shared => $"{MemorySize(file.Bytes)} · Memory shared with other programs",
            MemoryFileKind.Program => $"{MemorySize(file.Bytes)} in memory · Program · {file.Path}",
            MemoryFileKind.Library => $"{MemorySize(file.Bytes)} in memory · Library · {file.Path ?? "no file name"}",
            _ => $"{MemorySize(file.Bytes)} in memory · File · {file.Path}",
        };

        // Memory in the units Task Manager uses (1 GB = 1024 MB).
        static string MemorySize(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB"
            : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB" : $"{Math.Max(1, bytes >> 10)} KB";

        static double SpreadOf(string name)
        {
            var hash = 2166136261u;
            foreach (var character in name) hash = (hash ^ character) * 16777619u;
            return ((hash >> 8) & 1023) / 1023d;
        }

        // CHANGED (round 58): memory is sold and labelled in binary sizes - 64 GB of RAM is 64 × 2^30 bytes,
        // which the decimal drive-label rounding showed as "69 GB". Whole gigabytes, as on the box.
        static string Gigabytes(long bytes) => bytes >= 1L << 30 ? $"{Math.Round(bytes / (double)(1L << 30)):0} GB" : DiskUsageSnapshot.FormatBytes(bytes);

        // The maker's name as it is known, without "Technology Co., Ltd." and the like.
        static string Maker(string maker)
        {
            var name = maker;
            foreach (var tail in new[] { " Technology Co., Ltd.", " Technology Co.,Ltd.", " Co., Ltd.", " Co.,Ltd.", " Corporation", " Computer Inc.", " Inc.", " INC.", " International", " Technologies" })
                if (name.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) name = name[..^tail.Length];
            return name.Equals("Gigabyte", StringComparison.OrdinalIgnoreCase) ? "GIGABYTE" : name.Trim();
        }

        (double Width, double Height) Measure(long bytes)
        {
            // CHANGED (round 54): exact - no minimum size - so a drive is the same size whichever drive is open.
            var width = Math.Sqrt(Math.Max(1d, bytes)) * perRootByte;
            return (width, width * world.Height / world.Width);
        }

        string Group(int i) => entries[i].Hardware.Kind == DriveKind.Network ? "net:" + (entries[i].Hardware.Server ?? entries[i].Root) : "local";

        double Spacing(int left, int right) => Group(left) == Group(right) ? gap : gap * 3;

        // How far a device's body reaches past its files on each side: connectors, gold fingers, handles.
        static Thickness Frame(DriveKind kind, double h, bool left)
        {
            var frame = kind switch
            {
                // CHANGED (round 53): connectors are on the drive's short end, as on a real drive; a tray's
                // handle is on the other end.
                DriveKind.Hdd => new Thickness(h * .07, h * .07, h * .16, h * .07),
                DriveKind.Nvme => new Thickness(h * .18, h * .07, h * .09, h * .07),
                DriveKind.Usb => new Thickness(h * .09, h * .09, h * .15, h * .09),
                DriveKind.Network => new Thickness(h * .17, h * .07, h * .12, h * .07),
                _ => new Thickness(h * .06, h * .06, h * .13, h * .06),
            };
            return left ? new Thickness(frame.Right, frame.Top, frame.Left, frame.Bottom) : frame;
        }

        // A cable from each drive in a group down to a port on top of its hub (the board, or a server); lanes as
        // before, so no two cross. Returns where the ports are, for the board to draw its connectors there.
        double[] Cables(int first, int last, Rect hub, double groupPlug, double rowBottom, double drop, double portsFrom, double portsTo)
        {
            var n = last - first + 1;
            var ports = Enumerable.Range(0, n).Select(i => hub.X + hub.Width * (portsFrom + (portsTo - portsFrom) * (i + .5) / n)).ToArray();
            var starts = Enumerable.Range(0, n).Select<int, (double X, double Y, uint Wire)>(i => portsOf[first + i] is { Count: > 0 } found ? found[0]
                : (Centre(boxes[first + i]), boxes[first + i].Bottom + frames[first + i].Bottom, Cable)).ToArray();
            var laneTop = rowBottom + drop * .25;
            var laneBottom = hub.Top - drop * .2;
            var rightward = Enumerable.Range(0, n).Where(i => starts[i].X <= ports[i]).ToArray();
            var leftward = Enumerable.Range(0, n).Where(i => starts[i].X > ports[i]).Reverse().ToArray();
            var lane = new double[n];
            foreach (var side in new[] { rightward, leftward })
                for (var m = 0; m < side.Length; m++)
                    lane[side[m]] = laneBottom - (laneBottom - laneTop) * m / Math.Max(1, side.Length);
            for (var i = 0; i < n; i++)
            {
                var (from, top, color) = starts[i];
                var to = ports[i];
                solids.Add(new Solid(new Rect(from - wire / 2, top - wire / 2, wire, Math.Max(0, lane[i] - top) + wire), color, SolidKind.Wire));
                solids.Add(new Solid(new Rect(Math.Min(from, to) - wire / 2, lane[i] - wire / 2, Math.Abs(to - from) + wire, wire), color, SolidKind.Wire));
                solids.Add(new Solid(new Rect(to - wire / 2, lane[i] - wire / 2, wire, hub.Top - lane[i] + wire / 2), color, SolidKind.Wire));
                solids.Add(new Solid(new Rect(to - groupPlug / 2, hub.Top - plug.Height, groupPlug, plug.Height * 1.6), CableEnd, SolidKind.Plain));
            }
            return ports;
        }

        // A server (or NAS) with a row of drive bays, each with its activity light.
        void Server(Rect hub, string server, int shares, long capacity)
        {
            regions.Add(Region(NextId(), server, capacity, [hub], ComputerCase, capacity,
                $"Server · {shares} share{(shares == 1 ? "" : "s")} · {DiskUsageSnapshot.FormatBytes(capacity)}"));
            const int bays = 4;
            for (var b = 0; b < bays; b++)
            {
                var bay = new Rect(hub.X + hub.Width * .06, hub.Y + hub.Height * (.3 + b * .16), hub.Width * .88, hub.Height * .12);
                solids.Add(new Solid(bay, ComputerPanel, SolidKind.Plain));
                var led = bay.Height * .35;
                solids.Add(new Solid(new Rect(bay.Right - led * 2.2, bay.Y + bay.Height / 2 - led / 2, led, led), b < shares ? PowerLight : CableEnd, SolidKind.Plain));
            }
        }

        // One drive as the device it is: its body under the files (always), and its cover over them.
        void Device(int i, int slot, int index, DriveHardware device, Rect box, Thickness frame, long capacity, long driveFree, bool facesLeft = false)
        {
            var h = box.Height;
            var body = new Rect(box.X - frame.Left, box.Y - frame.Top, box.Width + frame.Left + frame.Right, box.Height + frame.Top + frame.Bottom);
            var kind = device.Kind;
            switch (kind)
            {
                case DriveKind.Nvme:
                    underlay.Add(new Solid(body, Pcb, SolidKind.Plain));
                    // Gold contacts along the connector edge, with the key notch.
                    const int fingers = 22;
                    for (var f = 0; f < fingers; f++)
                    {
                        if (f is 5) continue;   // the M key
                        var y = body.Y + body.Height * (.06 + .88 * f / fingers);
                        underlay.Add(new Solid(new Rect(body.X, y, frame.Left * .55, body.Height * .88 / fingers * .62), Gold, SolidKind.Tile));
                    }
                    // NEW (round 52): the M.2 slot the contacts sit in, which is where its connection comes from.
                    var slot2 = new Rect(body.X - frame.Left * .1, body.Y + body.Height * .03, frame.Left * .55, body.Height * .94);
                    solids.Add(new Solid(slot2, M2Slot, SolidKind.Plain));
                    portsOf[i] = [(slot2.X + slot2.Width / 2, slot2.Bottom, Cable)];   // CHANGED (round 56): cabled from its slot again
                    underlay.Add(new Solid(new Rect(body.Right - frame.Right * .55, body.Y + body.Height * .4, frame.Right * .35, body.Height * .2), Screw, SolidKind.Tile));
                    // A few parts on the board around the label.
                    underlay.Add(new Solid(new Rect(body.Right - frame.Right * .75, body.Y + body.Height * .44, frame.Right * .5, body.Height * .12), Parts, SolidKind.Tile));
                    underlay.Add(new Solid(new Rect(box.X - frame.Left * .35, body.Y + body.Height * .2, frame.Left * .2, body.Height * .08), Parts, SolidKind.Tile));
                    underlay.Add(new Solid(new Rect(box.X - frame.Left * .35, body.Y + body.Height * .7, frame.Left * .2, body.Height * .08), Parts, SolidKind.Tile));
                    break;
                case DriveKind.Usb:
                    underlay.Add(new Solid(body, UsbBody, SolidKind.Plain));
                    // CHANGED (round 53): the port is in the enclosure's end, with the cable's plug in it.
                    var usbPort = new Rect(box.Right + frame.Right * .2, box.Y + box.Height / 2 - h * .08, frame.Right * .5, h * .16);
                    underlay.Add(new Solid(usbPort, CableEnd, SolidKind.Plain));
                    portsOf[i] = [EndPlug(usbPort, body)];
                    underlay.Add(new Solid(new Rect(box.Right + frame.Right * .35, box.Bottom - h * .12, h * .03, h * .03), UsbLight, SolidKind.Tile));
                    break;
                default:
                    // 3.5" and 2.5" cases, and a server's drive tray: a metal body with the SATA data and power
                    // connectors on its end. CHANGED (round 53): on the short end, not along the side.
                    var hdd = kind is DriveKind.Hdd or DriveKind.Network;
                    underlay.Add(new Solid(body, hdd ? HddBody : SsdBody, SolidKind.Plain));
                    underlay.Add(new Solid(Rect.Inflate(box, h * .015, h * .015), hdd ? HddInner : SsdBody, SolidKind.Plain));
                    var end = facesLeft   // where the connectors are
                        ? new Rect(box.X - h * .025 - frame.Left * .5, box.Y, frame.Left * .5, box.Height)
                        : new Rect(box.Right + h * .025, box.Y, frame.Right * .5, box.Height);
                    if (kind == DriveKind.Network)
                    {
                        // A server tray: the handle at the front end, the backplane connector at the back.
                        var handle = new Rect(body.X + frame.Left * .15, box.Y + box.Height * .08, frame.Left * .55, box.Height * .84);
                        underlay.Add(new Solid(handle, TrayHandle, SolidKind.Plain));
                        underlay.Add(new Solid(new Rect(handle.X + handle.Width * .3, handle.Y + handle.Width * .4, handle.Width * .4, handle.Width * .4), PowerLight, SolidKind.Tile));
                        var backplane = new Rect(end.X, box.Y + box.Height * .3, end.Width, box.Height * .4);
                        underlay.Add(new Solid(backplane, CableEnd, SolidKind.Plain));
                        underlay.Add(new Solid(Rect.Inflate(backplane, -end.Width * .25, -backplane.Height * .1), Gold, SolidKind.Tile));
                        portsOf[i] = [EndPlug(backplane, body)];
                    }
                    else
                    {
                        // The 7-pin data connector and the longer 15-pin power connector, side by side on the end.
                        var data = new Rect(end.X, box.Y + box.Height * .16, end.Width, box.Height * .2);
                        var power = new Rect(end.X, box.Y + box.Height * .44, end.Width, box.Height * .36);
                        foreach (var connector in new[] { data, power })
                        {
                            underlay.Add(new Solid(connector, CableEnd, SolidKind.Plain));
                            underlay.Add(new Solid(Rect.Inflate(connector, -end.Width * .25, -connector.Height * .1), Gold, SolidKind.Tile));
                        }
                        portsOf[i] = [EndPlug(data, body, facesLeft)];
                    }
                    var margin = Math.Min(frame.Left, frame.Right);
                    for (var c = 0; c < 4; c++)   // mounting screws in the corners of the body
                    {
                        var sx = c % 2 == 0 ? body.X + margin * .5 : body.Right - margin * .5;
                        var sy = c < 2 ? body.Y + frame.Top * .5 : body.Bottom - frame.Bottom * .5;
                        var screw = Math.Min(frame.Left, frame.Top) * .22;
                        underlay.Add(new Solid(new Rect(sx - screw, sy - screw, screw * 2, screw * 2), Screw, SolidKind.Tile));
                    }
                    break;
            }

            // The cover: what the drive looks like closed. Drawn by the overlay over everything, faded in as
            // the drive gets small on screen.
            var accent = BrandColor(device.Brand);
            var lid = Rect.Inflate(box, h * .04, h * .04);
            Rect sticker;
            switch (kind)
            {
                case DriveKind.Hdd or DriveKind.Network:
                    covers.Add(new CoverShape(CoverForm.Rounded, lid, HddLid, slot, .04));
                    var radius = Math.Min(lid.Height * .42, lid.Width * .27);
                    var hubCentre = new Point(lid.X + lid.Height * .06 + radius, lid.Y + lid.Height / 2);
                    covers.Add(new CoverShape(CoverForm.Ring, Around(hubCentre, radius), HddRing, slot, Stroke: radius * .05));
                    covers.Add(new CoverShape(CoverForm.Ellipse, Around(hubCentre, radius * .2), HddRing, slot));
                    covers.Add(new CoverShape(CoverForm.Ellipse, Around(hubCentre, radius * .07), Screw, slot));
                    var pivot = new Point(lid.Right - lid.Height * .2, lid.Bottom - lid.Height * .2);
                    covers.Add(new CoverShape(CoverForm.Ellipse, Around(pivot, lid.Height * .09), HddRing, slot));
                    foreach (var corner in new[] { lid.TopLeft, lid.TopRight, lid.BottomLeft, lid.BottomRight })
                    {
                        var inset = new Point(corner.X + (corner.X < lid.X + 1e-9 ? 1 : -1) * lid.Height * .06, corner.Y + (corner.Y < lid.Y + 1e-9 ? 1 : -1) * lid.Height * .06);
                        covers.Add(new CoverShape(CoverForm.Ellipse, Around(inset, lid.Height * .022), Screw, slot));
                    }
                    var left = hubCentre.X + radius + lid.Height * .06;
                    sticker = new Rect(left, lid.Y + lid.Height * .1, Math.Max(lid.Height * .2, lid.Right - left - lid.Height * .08), lid.Height * .52);
                    covers.Add(new CoverShape(CoverForm.Rounded, sticker, Label, slot, .04));
                    break;
                case DriveKind.Nvme:
                    sticker = Rect.Inflate(box, h * .01, h * .01);
                    covers.Add(new CoverShape(CoverForm.Box, sticker, NvmeLabel, slot));
                    break;
                case DriveKind.Usb:
                    sticker = lid;
                    covers.Add(new CoverShape(CoverForm.Rounded, lid, UsbFace, slot, .08));
                    break;
                default:
                    sticker = lid;
                    covers.Add(new CoverShape(CoverForm.Rounded, lid, SsdFace, slot, .03));
                    foreach (var corner in new[] { lid.TopLeft, lid.TopRight, lid.BottomLeft, lid.BottomRight })
                    {
                        var inset = new Point(corner.X + (corner.X < lid.X + 1e-9 ? 1 : -1) * lid.Height * .05, corner.Y + (corner.Y < lid.Y + 1e-9 ? 1 : -1) * lid.Height * .05);
                        covers.Add(new CoverShape(CoverForm.Ellipse, Around(inset, lid.Height * .018), Screw, slot));
                    }
                    break;
            }
            // The brand's colour as a band across the top of the label (down the side of an M.2 stick).
            var band = kind == DriveKind.Nvme
                ? new Rect(sticker.X, sticker.Y, sticker.Height * .06, sticker.Height)
                : new Rect(sticker.X, sticker.Y, sticker.Width, sticker.Height * (kind is DriveKind.Hdd or DriveKind.Network ? .08 : .05));
            covers.Add(new CoverShape(CoverForm.Box, band, accent, slot));
            // A barcode in the corner of the label, different for every model.
            var code = new Rect(sticker.Right - sticker.Width * .3 - sticker.Height * .04, sticker.Bottom - sticker.Height * .2, sticker.Width * .3, sticker.Height * .14);
            covers.Add(new CoverShape(CoverForm.Box, code, BarcodeBack, slot));
            var seed = (uint)device.Model.GetHashCode();
            for (var x = code.X + code.Width * .06; x < code.Right - code.Width * .06;)
            {
                seed = seed * 1664525 + 1013904223;
                var bar = code.Width * (.012 + (seed >> 28) * .004);
                if ((seed & 0x100) != 0) covers.Add(new CoverShape(CoverForm.Box, new Rect(x, code.Y + code.Height * .12, bar, code.Height * .76), BarcodeBar, slot));
                x += bar + code.Width * .012;
            }
            coverTexts[slot] = new CoverText(sticker, device.Brand.Length > 0 ? device.Brand : device.KindName,
                device.Model, Marketing(capacity), device.KindName, accent);
            var bounds = kind is DriveKind.Hdd or DriveKind.Network or DriveKind.Usb or DriveKind.SataSsd or DriveKind.Unknown ? lid : sticker;
            coverBounds[slot] = bounds;
            var detail = $"{Marketing(capacity)} {device.KindName} · {DiskUsageSnapshot.FormatBytes(Math.Max(0, driveFree))} free";
            var id = FixedId(index, 1);
            var node = Reuse(id, device.Model, capacity, bounds, detail)
                ?? new Node(new DiskUsageItem(id, device.Model, capacity, 0, false), bounds, null, null, capacity, 0) { Detail = detail };
            coverNodes[slot] = node;
            coverIds.Add(id);
            if (index >= 0) owners[id] = (index, null);
        }

        static Rect Around(Point centre, double radius) => new(centre.X - radius, centre.Y - radius, radius * 2, radius * 2);

        // NEW (round 53): a cable's plug pushed into a connector on the drive's end, standing a little out
        // past the body, and the start of the cable: a short run out of the plug, from where it turns down
        // in the gap beside the drive.
        (double X, double Y, uint Wire) EndPlug(Rect connector, Rect body, bool left = false)
        {
            // CHANGED (round 54): either end - a drive in the cage faces the board, connectors first.
            var inner = left ? connector.Right - connector.Width * .4 : connector.X + connector.Width * .4;
            var outer = left ? body.X - gap * .15 : body.Right + gap * .15;
            var plugBody = new Rect(Math.Min(inner, outer), connector.Y - connector.Height * .08, Math.Abs(outer - inner), connector.Height * 1.16);
            solids.Add(new Solid(plugBody, PlugBody, SolidKind.Plain));
            var y = plugBody.Y + plugBody.Height / 2;
            var x = left ? plugBody.X - gap * .12 : plugBody.Right + gap * .12;
            solids.Add(new Solid(new Rect(Math.Min(x, outer), y - wire / 2, Math.Abs(outer - x), wire), Cable, SolidKind.Wire));
            return (x, y, Cable);
        }

        // Capacity the way a drive's label says it: decimal, rounded.
        static string Marketing(long bytes) => bytes >= 1_000_000_000_000 ? $"{bytes / 1e12:0.#} TB"
            : bytes >= 1_000_000_000 ? $"{Math.Round(bytes / 1e9):0} GB" : DiskUsageSnapshot.FormatBytes(bytes);

        // Returns the rectangle the drive's files fill (the whole box when it has no preview).
        Rect AddDrive(OtherDrive drive, int index, Rect box)
        {
            var driveFree = Math.Clamp(drive.Free, 0, drive.Total);
            var name = string.IsNullOrWhiteSpace(drive.Label) ? drive.Root : $"{drive.Root}  {drive.Label}";
            if (drive.Preview is { Bytes: > 0 } preview)
            {
                // Laid out like this drive: its indexed files in the corner, scaled so areas stay exact.
                var indexed = Math.Min(preview.Bytes, drive.Total);
                var driveK = Math.Max(1, Math.Sqrt(drive.Total / (double)Math.Max(1, indexed)));
                var files = new Rect(box.X, box.Y, box.Width / driveK, box.Height / driveK);
                var unindexed = Math.Max(0, drive.Total - driveFree - indexed);
                var (driveOther, driveFreeParts) = Surround(files, driveK, files.Width * files.Height * unindexed / Math.Max(1d, indexed));
                // The files' background carries the drive's card and click; the items are drawn over it.
                Own(Region(NextId(index), name, indexed, [files], DriveSurface, drive.Total) with { Captioned = false }, index, null);   // named on its plate
                if (unindexed > 0) Own(Region(NextId(index), $"Used, not indexed on {drive.Root}", unindexed, driveOther, OtherSpaceColor, drive.Total), index, null);
                if (driveFree > 0) Own(Region(NextId(index), $"Free on {drive.Root}", driveFree, driveFreeParts, FreeSpaceColor, drive.Total), index, null);
                foreach (var tile in preview.Tiles)
                    solids.Add(new Solid(new Rect(files.X + tile.X * files.Width, files.Y + tile.Y * files.Height, tile.W * files.Width, tile.H * files.Height), tile.Color, SolidKind.Tile));
                // The items directly on the drive: named, with a card, and a click that opens them there.
                foreach (var label in preview.Labels)
                {
                    var rect = new Rect(files.X + label.X * files.Width, files.Y + label.Y * files.Height, label.W * files.Width, label.H * files.Height);
                    var id = NextId(index);
                    var region = new DriveRegion(Reuse(id, label.Name, label.Bytes, rect) ??
                        new Node(new DiskUsageItem(id, label.Name, label.Bytes, 0, label.IsFolder), rect, null, null, drive.Total, 0),
                        [rect], label.Color, Fill: false);   // the tiles are its fill
                    regions.Add(region);
                    owners[id] = (index, label.IsFolder ? System.IO.Path.Combine(drive.Root, label.Name) : drive.Root);
                }
                return files;
            }
            var usedShare = drive.Total <= 0 ? 0 : (drive.Total - driveFree) / (double)drive.Total;
            var hue = Pack(Parse(DiskUsagePalette.BranchColor(index)));
            var muted = (uint)((hue >> 16 & 255) * 7 / 10) << 16 | (uint)((hue >> 8 & 255) * 7 / 10) << 8 | (hue & 255) * 7 / 10;
            var usedBox = new Rect(box.X, box.Y, box.Width * usedShare, box.Height);
            var freeBox = new Rect(usedBox.Right, box.Y, box.Width - usedBox.Width, box.Height);
            Own(Region(NextId(index), $"{drive.Root} used", drive.Total - driveFree, [usedBox], muted, drive.Total), index, null);
            Own(Region(NextId(index), $"Free on {drive.Root}", driveFree, [freeBox], FreeSpaceColor, drive.Total), index, null);
            return box;
        }

        void Own(DriveRegion region, int index, string? path)
        {
            regions.Add(region);
            owners[region.Label.Item.Id] = (index, path);
        }

        // CHANGED (round 49): each drive numbers its own regions in its own block, so a preview arriving for
        // one drive does not renumber the next - ids are what clicks and kept labels go by.
        // CHANGED (round 51): the first two numbers of each block are kept for the drive's name plate and its
        // cover, so those two keep their ids whatever else the drive gains (a preview arriving).
        int NextId(int drive = -1) => -1_000_001 - (drive + 1) * 4096 - 2 - slots[drive + 1]++;
        int FixedId(int drive, int place) => -1_000_001 - (drive + 1) * 4096 - place;

        // CHANGED (round 49): a label that has not changed keeps its node from the last build, so a rebuild
        // (a preview arriving, a little free space coming and going) does not make its name flicker.
        // CHANGED (round 57): "unchanged" allows for the drift a live refresh brings - a few megabytes of free
        // space, a hair of layout - which otherwise gave the label a new node, and its text a new layout that
        // faded in: the flash. A real change (a new name, a size off by more than a fraction of a percent)
        // still gets a new node.
        Node? Reuse(int id, string name, long bytes, Rect bounds, string? detail = null)
            => previous is not null && previous.Nodes.TryGetValue(id, out var old) && old.Item.Name == name &&
               Math.Abs(old.Item.Bytes - bytes) <= Math.Max(64L << 20, Math.Abs(bytes) / 500) &&
               Near(old.Bounds, bounds) && (old.Detail == detail || SizeOnly(old.Detail, detail)) ? old : null;

        static bool Near(Rect a, Rect b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) + Math.Abs(a.Width - b.Width) + Math.Abs(a.Height - b.Height)
            <= Math.Max(Math.Max(a.Width, a.Height), 1e-12) * .01;

        // Two detail lines that differ only in their numbers (a free-space figure moving a little).
        static bool SizeOnly(string? a, string? b)
        {
            if (a is null || b is null) return false;
            static string Words(string text) => string.Concat(text.Where(c => !char.IsDigit(c) && c != '.' && c != ','));
            return Words(a) == Words(b);
        }

        // CHANGED (round 60): live - a block of the live memory view keeps the node it had, whatever its size
        // and place now, with its words brought up to date, so its label never has to be made again.
        DriveRegion Region(int id, string name, long bytes, List<Rect> parts, uint color, double of, string? detail = null, string? tag = null, bool live = false)
        {
            // Largest piece first: that is where its label goes.
            var ordered = parts.Where(part => part.Width > 0 && part.Height > 0).OrderByDescending(part => part.Width * part.Height).ToArray();
            var bounds = ordered.Length > 0 ? ordered[0] : world;
            if (live && previous is not null && previous.Nodes.TryGetValue(id, out var kept) && kept.Item.Name == name)
            {
                if (kept.Detail != detail) kept.Detail = detail;
                kept.Tag = tag;
                return new DriveRegion(kept, ordered, color);
            }
            var label = Reuse(id, name, bytes, bounds, detail) ?? new Node(new DiskUsageItem(id, name, bytes, 0, false), bounds, null, null, (long)of, 0) { Detail = detail, Tag = tag };
            return new DriveRegion(label, ordered, color);
        }

        static double Centre(Rect rect) => rect.X + rect.Width / 2;
    }

    // NEW (round 51): the colour of a maker's labels, muted for the dark theme.
    private static uint BrandColor(string brand) => brand switch
    {
        "Samsung" => Pack(Parse("#2E5AA6")),
        "WD" => Pack(Parse("#2F6DAE")),
        "Seagate" => Pack(Parse("#4F8A3C")),
        "Crucial" => Pack(Parse("#2A7FB8")),
        "Kingston" or "Toshiba" or "SanDisk" => Pack(Parse("#A63E3B")),
        "Intel" or "Micron" => Pack(Parse("#2F74C0")),
        "SK hynix" => Pack(Parse("#C0543C")),
        "HGST" => Pack(Parse("#3E6E9E")),
        _ => Pack(Parse("#6B6660")),
    };

    // The space around a drive's files, which sit in the top-left corner of a rectangle k times their size:
    // used space the index does not cover starts under the files and continues up the right-hand strip
    // from the bottom; free space is the rest, one L-shaped region. Areas are exactly proportional to bytes.
    private static (List<Rect> Other, List<Rect> Free) Surround(Rect r, double k, double otherArea)
    {
        var world = new Rect(r.X, r.Y, r.Width * k, r.Height * k);
        var right = new Rect(r.Right, r.Y, r.Width * (k - 1), r.Height * k);
        var below = new Rect(r.X, r.Bottom, r.Width, r.Height * (k - 1));
        var belowArea = below.Width * below.Height;
        var otherParts = new List<Rect>();
        var freeParts = new List<Rect>();
        if (otherArea <= belowArea)
        {
            var width = belowArea <= 0 ? 0 : below.Width * otherArea / belowArea;
            if (width > 0) otherParts.Add(new Rect(below.X, below.Y, width, below.Height));
            freeParts.Add(right);
            // CHANGED (round 45): the lower arm of the free space runs on under the right-hand strip, so the
            // two overlap in the corner and read as one L-shaped region instead of two separate blocks.
            if (below.Width - width > 0) freeParts.Add(new Rect(below.X + width, below.Y, world.Right - below.X - width, below.Height));
        }
        else
        {
            otherParts.Add(below);
            var height = right.Width <= 0 ? 0 : Math.Min(right.Height, (otherArea - belowArea) / right.Width);
            otherParts.Add(new Rect(right.X, right.Bottom - height, right.Width, height));
            if (right.Height - height > 0) freeParts.Add(new Rect(right.X, right.Y, right.Width, right.Height - height));
        }
        return (otherParts, freeParts);
    }

    // NEW (round 48): colours of the machine drawing.
    private static readonly uint Cable = Pack(Parse("#5A564F"));
    private static readonly uint CableEnd = Pack(Parse("#3A3733"));
    private static readonly uint ComputerCase = Pack(Parse("#34322E"));
    private static readonly uint ComputerPanel = Pack(Parse("#262422"));
    private static readonly uint PowerLight = Pack(Parse("#4F9E73"));
    private static readonly uint DriveSurface = Pack(Parse("#1F1E1C"));
    private static readonly uint NamePlate = Pack(Parse("#2B2926"));
    // NEW (round 51): devices.
    private static readonly uint Ethernet = Pack(Parse("#3E6FA8"));
    // NEW (round 52): plugs and the M.2 slot.
    private static readonly uint PlugBody = Pack(Parse("#1B1C1E"));
    private static readonly uint M2Slot = Pack(Parse("#141415"));
    // NEW (round 54): the computer.
    private static readonly uint PcCase = Pack(Parse("#2A2B2E"));
    private static readonly uint PcInside = Pack(Parse("#1C1D1F"));
    private static readonly uint Motherboard = Pack(Parse("#1A241E"));
    // NEW (round 58): the board as map blocks - the parts that matter in the map's own colours.
    private static readonly uint BoardTile = Pack(Parse("#243029"));
    private static readonly uint PortTile = Pack(Parse("#1C1B19"));
    private static readonly uint CpuTile = Pack(Parse(DiskUsagePalette.Branches[0]));
    private static readonly uint GpuTile = Pack(Parse(DiskUsagePalette.Branches[2]));
    private static readonly uint StickTile = Pack(Parse(DiskUsagePalette.Branches[3]));
    private static readonly uint MemoryTile = Shade(Pack(Parse(DiskUsagePalette.Branches[3])), .42);
    // NEW (round 62): the processor with its lid off.
    private static readonly uint LidTile = Pack(Parse("#6E7680"));       // the heatspreader
    private static readonly uint DieTile = Pack(Parse("#1F252B"));
    private static readonly uint ClusterTile = Pack(Parse("#27303A"));
    private static readonly uint CacheTile = Pack(Parse("#4B5864"));
    private static readonly uint RunTile = Pack(Parse("#22272C"));
    private static readonly uint HeatIdle = Pack(Parse("#2E4256"));
    private static readonly uint HeatBusy = Pack(Parse("#D9823B"));
    private static readonly uint IoShield = Pack(Parse("#3A3C40"));
    private static readonly uint PortHole = Pack(Parse("#141516"));
    private static readonly uint Heatsink = Pack(Parse("#4A4D52"));
    private static readonly uint Socket = Pack(Parse("#2E3033"));
    private static readonly uint Cooler = Pack(Parse("#3B3E43"));
    private static readonly uint FanBlade = Pack(Parse("#26282B"));
    private static readonly uint RamSlot = Pack(Parse("#111213"));
    private static readonly uint RamStick = Pack(Parse("#30343A"));
    private static readonly uint RamLight = Pack(Parse("#6B5FA8"));
    private static readonly uint GraphicsCard = Pack(Parse("#27292D"));
    private static readonly uint PowerSupply = Pack(Parse("#2C2E32"));
    private static readonly uint Cage = Pack(Parse("#232427"));
    private static readonly uint Pcb = Pack(Parse("#1C3326"));
    private static readonly uint Gold = Pack(Parse("#B8973F"));
    private static readonly uint Parts = Pack(Parse("#2B2B2D"));
    private static readonly uint HddBody = Pack(Parse("#55585D"));
    private static readonly uint HddInner = Pack(Parse("#4A4D52"));
    private static readonly uint SsdBody = Pack(Parse("#2C2E32"));
    private static readonly uint UsbBody = Pack(Parse("#2A2D31"));
    private static readonly uint UsbLight = Pack(Parse("#3E7BD6"));
    private static readonly uint TrayHandle = Pack(Parse("#2B2D30"));
    private static readonly uint Screw = Pack(Parse("#3C3E42"));
    private static readonly uint HddLid = Pack(Parse("#8B8E93"));
    private static readonly uint HddRing = Pack(Parse("#999CA1"));
    private static readonly uint Label = Pack(Parse("#1F2429"));
    private static readonly uint NvmeLabel = Pack(Parse("#151618"));
    private static readonly uint SsdFace = Pack(Parse("#24272B"));
    private static readonly uint UsbFace = Pack(Parse("#303338"));
    private static readonly uint BarcodeBack = Pack(Parse("#D6D3CC"));
    private static readonly uint BarcodeBar = Pack(Parse("#1A1A1A"));

    // ---------------------------------------------------------------- NEW (round 51): drive covers
    //
    // A drive's cover is drawn by the overlay, over the scene and every label in it, so it hides what is
    // under it without anything underneath having to know. It fades with the camera, every frame: fully
    // there once the camera is well out past the drive, gone by the time the drive nearly fills the view -
    // so zooming out closes the drives and zooming into one opens it up before it opens for real.
    private double CoverAlpha(DriveView drive, int slot)
    {
        if (slot >= drive.Drives.Length || drive.CoverBounds.Length <= slot || drive.CoverBounds[slot].IsEmpty) return 0;
        var fit = FitRaw(drive.Drives[slot].Box, 0).Width;
        var machineFit = FitRaw(drive.Machine, 0).Width;
        var high = Math.Min(fit * 2, machineFit * .98);
        var low = Math.Min(fit * 1.1, high / 1.1);
        return SmoothStep((_camera.Width - low) / Math.Max(1e-12, high - low));
    }

    // World to where the scene on screen puts it (the scene can trail the camera by a frame).
    private Rect WorldToDisplay(Rect world)
    {
        if (_cache is not { } cache) return ToScreen(world);
        var scaleX = cache.Viewport.Width / cache.Camera.Width;
        var scaleY = cache.Viewport.Height / cache.Camera.Height;
        return BatchFor(cache).Transform(new Rect((world.X - cache.Camera.X) * scaleX, (world.Y - cache.Camera.Y) * scaleY, world.Width * scaleX, world.Height * scaleY));
    }

    private Node? CoverAt(Point point)
    {
        if (_root is null || CurrentDrive() is not { } drive || drive.CoverNodes.Length == 0 || !InDriveView) return null;
        for (var slot = 0; slot < drive.CoverNodes.Length; slot++)
            if (drive.CoverNodes[slot] is { } node && CoverAlpha(drive, slot) > .25 && WorldToDisplay(drive.CoverBounds[slot]).Contains(point))
                return node;
        // NEW (round 59): a memory stick (CHANGED round 62: or any lid) while it covers what is under it.
        foreach (var lid in drive.Lids)
            if (LidAlpha(lid.Area) > .25 && WorldToDisplay(lid.World).Contains(point)) return lid.Node;
        return null;
    }

    // Label text is laid out once at a fixed size and drawn scaled, so zooming (which changes the size
    // every frame) does not lay text out again every frame.
    private const double CoverEm = 24;
    private readonly Dictionary<(string Text, bool Bold, int Width), FormattedText> _coverText = [];

    private void CoverLine(DrawingContext dc, string text, double size, bool bold, double width, Point at, out double height)
    {
        var scale = size / CoverEm;
        // Widths in label units, capped: past that every line fits anyway, and the key stops changing.
        var key = (text, bold, (int)Math.Round(Math.Min(width / scale, 1200) / 16) * 16);
        if (!_coverText.TryGetValue(key, out var line))
        {
            if (_coverText.Count > 256) _coverText.Clear();
            line = Make(text, CoverEm, bold);
            line.SetForegroundBrush(Ink);
            line.MaxTextWidth = Math.Max(1, key.Item3);
            _coverText[key] = line;
        }
        height = line.Height * scale;
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, at.X, at.Y));
        dc.DrawText(line, new Point(0, 0));
        dc.Pop();
    }

    private void DrawCovers(DrawingContext dc)
    {
        if (CurrentDrive() is not { } drive || !InDriveView) return;   // CHANGED (round 59): the memory's too
        DrawLids(dc, drive);
        var view = Rect.Inflate(_view, 4, 4);
        for (var slot = 0; slot < drive.CoverBounds.Length; slot++)
        {
            var alpha = CoverAlpha(drive, slot);
            if (alpha <= .01) continue;
            var bounds = WorldToDisplay(drive.CoverBounds[slot]);
            if (!bounds.IntersectsWith(view)) continue;
            dc.PushOpacity(alpha);
            foreach (var shape in drive.Covers)
            {
                if (shape.Slot != slot) continue;
                var screen = WorldToDisplay(shape.World);
                if (screen.Width < .6 && screen.Height < .6) continue;
                var brush = BrushFor(Unpack(shape.Color), 1);
                switch (shape.Form)
                {
                    case CoverForm.Box: dc.DrawRectangle(brush, null, screen); break;
                    case CoverForm.Rounded:
                        var corner = shape.Corner * Math.Min(screen.Width, screen.Height);
                        dc.DrawRoundedRectangle(brush, null, screen, corner, corner);
                        break;
                    case CoverForm.Ellipse:
                        dc.DrawEllipse(brush, null, new Point(screen.X + screen.Width / 2, screen.Y + screen.Height / 2), screen.Width / 2, screen.Height / 2);
                        break;
                    case CoverForm.Ring:
                        var stroke = shape.Stroke * screen.Width / Math.Max(1e-12, shape.World.Width);
                        if (stroke < .5) break;
                        dc.DrawEllipse(null, new Pen(brush, stroke), new Point(screen.X + screen.Width / 2, screen.Y + screen.Height / 2),
                            screen.Width / 2 - stroke / 2, screen.Height / 2 - stroke / 2);
                        break;
                }
            }
            if (drive.CoverTexts[slot] is { } text) DrawCoverText(dc, text);
            dc.Pop();
        }
    }

    // NEW (round 59): the memory sticks, over what the memory holds. Opaque with the whole board in view,
    // gone once the memory's contents nearly fill it.
    // CHANGED (round 62): any lid - the processor's too - each fading by the area it covers.
    private double LidAlpha(Rect area)
    {
        if (area.IsEmpty) return 0;
        var fit = FitRaw(area, 0).Width;
        return SmoothStep((_camera.Width - fit * 1.3) / (fit * 1.1));
    }

    private void DrawLids(DrawingContext dc, DriveView drive)
    {
        var view = Rect.Inflate(_view, 4, 4);
        var covering = Rect.Empty;
        var skip = true;
        var pushed = false;
        foreach (var lid in drive.Lids)   // in runs, one run per area
        {
            if (lid.Area != covering)
            {
                if (pushed) dc.Pop();
                pushed = false;
                covering = lid.Area;
                var alpha = LidAlpha(covering);
                var area = WorldToDisplay(covering);
                skip = alpha <= .01 || !area.IntersectsWith(view) || area.Width < 2;
                if (skip) continue;
                dc.PushOpacity(alpha);
                pushed = true;
                // The part's own colour behind the lid, so what is under it does not show through its gaps.
                dc.DrawRectangle(BrushFor(Unpack(lid.Backing), 1), null, area);
            }
            if (skip) continue;
            var screen = WorldToDisplay(lid.World);
            var tile = Deflate(screen, Math.Clamp(Math.Min(screen.Width, screen.Height) * .01, .75, 6));   // the map's gap
            if (tile.Width <= 0 || tile.Height <= 0) continue;
            dc.DrawRectangle(BrushFor(Unpack(lid.Color), 1), null, tile);
            var size = Math.Min(tile.Width * .3, 16);
            if (lid.Label.Length > 0 && size >= 8) CoverLine(dc, lid.Label, size, true, tile.Width - size, new Point(tile.X + size * .5, tile.Y + size * .5), out _);
        }
        if (pushed) dc.Pop();
    }

    // The label's writing: the maker (or the kind of drive) large, the model under it, and the capacity big
    // in the lower corner, the way drives are labelled - sized to the label and left out when too small.
    private void DrawCoverText(DrawingContext dc, CoverText text)
    {
        var sticker = WorldToDisplay(text.Sticker);
        var pad = sticker.Height * .1;
        var room = sticker.Width - pad * 2;
        var title = Math.Clamp(sticker.Height * .15, 0, 34);
        if (title < 8 || room < 30) return;
        var y = sticker.Y + pad + sticker.Height * .05;
        CoverLine(dc, text.Brand, title, true, room, new Point(sticker.X + pad, y), out var brandHeight);
        y += brandHeight;
        var small = Math.Max(8, title * .5);
        if (y + small * 1.4 < sticker.Bottom - pad)
        {
            CoverLine(dc, text.Model, small, false, room, new Point(sticker.X + pad, y), out var modelHeight);
            y += modelHeight;
        }
        // Line heights are about 1.33 em; the capacity and kind sit on the bottom of the label.
        var big = Math.Clamp(sticker.Height * .22, 9, 64);
        var bottom = sticker.Bottom - pad;
        var kindTop = bottom - small * 1.33;
        var capacityTop = kindTop - big * 1.33;
        if (capacityTop > y + 2)
        {
            CoverLine(dc, text.Capacity, big, true, room * .62, new Point(sticker.X + pad, capacityTop), out _);
            CoverLine(dc, text.Kind, small, false, room * .62, new Point(sticker.X + pad, kindTop), out _);
        }
    }

    // NEW (round 47): the other drive a region belongs to, or null.
    // CHANGED (round 48): by the drive view's Owners, which also knows the item a label stands for.
    private OtherDrive? OtherDriveOf(Node? node)
        => node is not null && CurrentDrive() is { } drive && drive.Owners.TryGetValue(node.Item.Id, out var owner) && owner.Drive < drive.Others.Length
            ? drive.Others[owner.Drive] : null;

    private string? OtherDrivePath(Node node)
        => CurrentDrive() is { } drive && drive.Owners.TryGetValue(node.Item.Id, out var owner) ? owner.Path : null;

    private uint RegionColor(Node node)
    {
        if (CurrentDrive() is { } drive)
            foreach (var region in drive.Regions)
                if (region.Label.Item.Id == node.Item.Id) return region.Color;
        if (CurrentDrive() is { } machine)   // NEW (round 51): a cover's swatch is its maker's colour
        {
            for (var slot = 0; slot < machine.CoverNodes.Length; slot++)
                if (machine.CoverNodes[slot]?.Item.Id == node.Item.Id && machine.CoverTexts[slot] is { } text) return text.Accent;
            foreach (var stick in machine.Lids)   // NEW (round 59)
                if (stick.Node.Item.Id == node.Item.Id) return stick.Color;
        }
        return OtherSpaceColor;
    }

    // True while the camera is out past the whole folder map, showing the drive around it.
    // CHANGED (round 49): or anywhere over another drive, however far in.
    // CHANGED (round 57): anywhere the camera is not inside the folder map.
    private bool InDriveView => _root is not null && CurrentDrive() is not null && !InMap(_camera, FitRaw(_root.Bounds, 0));

    // NEW (round 49): the drive the camera is heading for. Zooming in with the pointer over a drive makes it
    // the anchor, and the frame the camera is kept in then narrows toward that drive rather than toward the
    // open one - so zooming into another drive goes into it. Once its files fill the view it opens.
    // Kept by root, not by position in the list, so a drive plugged in meanwhile cannot redirect it.
    private string? _anchor;
    private (string Root, string? Path, Rect Target)? _openingDrive;   // flying into a drive; it opens when the camera lands there

    private DriveBox AnchorBox(DriveView drive)
    {
        if (_anchor is not null)
            foreach (var box in drive.Drives)
                if (box.Index >= 0 && string.Equals(drive.Others[box.Index].Root, _anchor, StringComparison.OrdinalIgnoreCase)) return box;
        return new DriveBox(-1, drive.World, _root!.Bounds);
    }

    private static string? RootOf(DriveView drive, DriveBox box) => box.Index >= 0 ? drive.Others[box.Index].Root : null;

    // The drive whose box holds a world point, if any.
    private static DriveBox? DriveAt(DriveView drive, Point point)
    {
        foreach (var box in drive.Drives) if (box.Box.Contains(point)) return box;
        return null;
    }

    // Starts the flight into another drive's files; DriveRequested follows when it lands.
    private void OpenDrive(DriveView drive, DriveBox box, string? path)
    {
        if (box.Index < 0 || box.Index >= drive.Others.Length || _openingDrive is not null) return;
        _anchor = drive.Others[box.Index].Root;
        _userMoved = true;
        _message = null;
        var target = Clamp(FitRaw(box.Files, 0));
        _openingDrive = (_anchor, path, target);
        FlyTo(target, opening: true);
    }

    private static Rect Lerp(Rect from, Rect to, double t) => new(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t,
        from.Width + (to.Width - from.Width) * t, from.Height + (to.Height - from.Height) * t);

    private Rect Clamp(Rect camera)
    {
        if (_root is null) return camera;
        var world = _root.Bounds;
        var full = FitRaw(world, 0);
        var narrowest = full.Width * 1e-9;
        var widest = full.Width;
        // CHANGED (round 57): zoomed out past the files, the camera is free. It can go anywhere over the
        // picture of the machine, as close as it likes (to read the board, or look at a drive), and nothing
        // pulls it toward a drive - round 49's anchor did, which is what felt locked. It is kept to the
        // folder map only once it is inside it: at most as wide as the files and (nearly) all within them.
        if (CurrentDrive() is { } drive && !InMap(camera, full))
        {
            world = drive.Machine;
            widest = Math.Max(full.Width, FitRaw(drive.Machine, 0).Width);
            narrowest = full.Width * 1e-5;   // CHANGED (round 59): deep enough for a small file in the memory
        }
        var width = Math.Clamp(camera.Width, narrowest, widest);
        var height = width / ViewAspect;
        var center = Center(camera);
        var x = width >= world.Width ? world.X + world.Width / 2 : Math.Clamp(center.X, world.Left + width / 2, world.Right - width / 2);
        var y = height >= world.Height ? world.Y + world.Height / 2 : Math.Clamp(center.Y, world.Top + height / 2, world.Bottom - height / 2);
        return new Rect(x - width / 2, y - height / 2, width, height);
    }

    // NEW (round 57): whether a view is inside this drive's folder map (where the map's own rules apply):
    // no wider than the files, and all but a sliver of it over them, so moving into the map never makes the
    // camera jump more than that sliver.
    private bool InMap(Rect camera, Rect full)
    {
        if (_root is null) return true;
        if (camera.Width > full.Width * 1.001) return false;
        var files = Rect.Inflate(_root.Bounds, camera.Width * .12, camera.Height * .12);
        return files.Contains(camera);
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

    private void FlyTo(Rect target, bool opening = false)
    {
        _focusPoint = null;
        // NEW (round 49): any other flight is within the open drive, and calls off opening another.
        if (!opening) { _anchor = null; _openingDrive = null; }
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

    // NEW (round 45): how long a step may wait for its scene before falling back to the timed fade - only
    // reached when the zoom blend never had a scene for that folder (a very fast jump, a failed build).
    private const double StepPatience = .7;

    // The next level from `from` toward `to`: its child on the way down, its parent on the way up, or null
    // when `to` is neither below nor above it.
    private static Node? StepToward(Node from, Node to)
    {
        if (IsAncestorOf(to, from)) return from.Parent;
        if (!IsAncestorOf(from, to)) return null;
        var node = to;
        while (node.Parent is not null && node.Parent != from) node = node.Parent;
        return node.Parent == from ? node : null;
    }

    // Whether the scene on screen already shows the colours the step would lead to.
    private bool ReadyToStep(Node step)
    {
        if (_cache is not { } cache || cache.Level != _colorLevel || IsTimed(cache)) return _cache is null;
        if (step.Parent == _colorLevel) return cache.LevelB == step && _colorBlend >= .96;   // in: fully blended to it
        return cache.LevelB is null || _colorBlend <= .04;                                  // out: blend back at its level
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
        // NEW (round 45): a step waiting on the blend steers it - fully in when the camera is already inside
        // the folder being stepped into, back to nothing when it has left the level for its parent.
        if (_container is { } inside && _colorLevel is { } level && inside != level && StepToward(level, inside) is { } pending)
            want = pending.Parent == level ? (levelB == pending ? 1 : want) : 0;
        // CHANGED (round 45): the further the blend has to go - a scene arriving after the zoom had already
        // moved on - the more gently it catches up, so late colours drift in rather than pop.
        var ease = ApproachEase + .35 * Math.Abs(want - _colorBlend);
        _colorBlend += (want - _colorBlend) * (1 - Math.Exp(-step / ease));
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
            // CHANGED (round 45): a zoom in or out, through any number of levels, moves the colour level one
            // level at a time, and each step waits until the scene on screen is already showing where the
            // step leads. Stepping in: the zoom blend toward that folder has reached its colours. Stepping
            // out: the blend toward the folder being left has run back down. The step itself then changes
            // nothing you can see, and the next level's blend picks up from there - a fast zoom through
            // three folders is three short blends in a row instead of a pause and a jump. Round 42 switched
            // at once whenever the step was one level, even when the scene had not caught up, which is
            // where the snaps came from. Anything else (a sideways jump) still settles, then fades.
            var step = ApproachEnabled && _colorLevel is not null ? StepToward(_colorLevel, _container) : null;
            if (step is not null && _container != _colorPending) { _colorPending = _container; _colorPendingSince = now; }
            if (step is not null && ReadyToStep(step))
            {
                _colorPrevious = _colorLevel;
                _colorLevel = step;
                _colorSince = now;
                _colorPendingSince = now;   // the next step starts its own wait
            }
            else if (step is not null && now - _colorPendingSince < StepPatience) { }   // wait for the scene
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
        // NEW (round 49): the flight into another drive has landed - open it.
        // Only where it was heading: a flight cut short (a resize, a rebuilt tree) calls it off instead.
        if (_openingDrive is { } opening && _flight is null)
        {
            _openingDrive = null;
            if (Near(_camera, opening.Target)) DriveRequested?.Invoke(opening.Root, opening.Path);
        }
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

    private TileBatch BatchFor(SceneCache cache, double opacity = 1, bool presented = false)
        => TileBatch.ForCamera(cache.Geometry, cache.Camera, cache.Viewport,
            cache.Detached ? cache.Camera : presented ? _sceneCamera : _camera,
            new Size(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)), opacity);


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
        public required DriveRegion[]? Drive;   // NEW (round 44): the drive around the files, or null
        public required Solid[]? Solids;        // NEW (round 48): other drives' blocks, cables, the computer
        public required Solid[]? Underlay;      // NEW (round 51): drive bodies and network cables, under everything

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
            if (Underlay is { Length: > 0 } underlay) DrawSolids(underlay);                  // NEW (round 51)
            if (Drive is { } drive) foreach (var region in drive) DrawDriveRegion(region);   // NEW (round 44)
            if (Solids is { Length: > 0 } solids) DrawSolids(solids);                      // NEW (round 48)
            if (Root is { Item.Bytes: > 0 }) DrawNode(Root, ToScreen(Root.Bounds), 1, 1, 1, Rect.Empty, Budget, BasePacked, BasePacked, default, true);
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

        // NEW (round 44): one region of the drive view - free or other used space. Solid, inset by a gap so it
        // reads as separate from the files, labelled in its largest piece. It only ever shows once the
        // camera is out past the folder map; inside it, it is off screen and costs nothing.
        // NEW (round 61): for each tagged region drawn so far, the strip at the top of what is on screen of it
        // that its tag (and its parents' tags above that) take up. A region inside it puts its own tag or label
        // below the strip - so zoomed into a program in the memory, the board's, the memory's and the program's
        // tags stack down the corner like a path, and the labels inside start under them.
        private readonly Dictionary<Node, Rect> _bands = [];
        private const double TagHeight = 23;   // a name tag's height: a 12-point line and its padding

        private void DrawDriveRegion(DriveRegion region)
        {
            for (var i = 0; i < region.Parts.Length; i++)
            {
                var screen = ToScreen(region.Parts[i]);
                if (!screen.IntersectsWith(ViewLoose)) continue;
                var tile = Deflate(screen, Math.Clamp(Math.Min(screen.Width, screen.Height) * .01, .75, 6));
                if (tile.Width <= 0 || tile.Height <= 0) continue;
                if (region.Fill) EmitSolid(tile, region.Color, region.Color);   // CHANGED (round 48): a label over tiles has none
                // NEW (round 45): recorded for hit testing, so hovering a region shows what it is.
                var hit = Rect.Intersect(tile, ViewLoose);
                if (!hit.IsEmpty) Drawn.Add((region.Label, hit));
                if (i != 0 || !region.Captioned) continue;
                var visible = Rect.Intersect(tile, View);
                // CHANGED (round 59): no caption for a block too small to hold one (labels need about 44 by
                // 20 pixels, name tags more) - the memory has thousands of them.
                if (visible.IsEmpty || visible.Width < 30 || visible.Height < 14) continue;
                region.Label.LastDrawn = Stamp;   // keeps its label text from being handed back every frame
                var zone = region.Parent is { } parent && _bands.TryGetValue(parent, out var band) ? band : Rect.Empty;   // NEW (round 61)
                if (region.Open)
                {
                    // Where its tag goes (as LayoutPill will put it), and so where what is inside it must start.
                    var top = !zone.IsEmpty && visible.X + 5 < zone.Right && visible.Y + 5 < zone.Bottom + 3 ? zone.Bottom + 3 : visible.Y + 5;
                    var shows = Math.Min(visible.Width, visible.Height) >= 36 && top + TagHeight <= visible.Bottom - 5;
                    var bottom = shows ? top + TagHeight : zone.IsEmpty ? double.NaN : zone.Bottom;
                    _bands[region.Label] = double.IsNaN(bottom) || bottom <= visible.Y ? Rect.Empty : new Rect(visible.X, visible.Y, visible.Width, bottom - visible.Y);
                }
                var caption = new Caption(region.Label, visible, zone, 1, region.Open, region.Open ? Unpack(region.Color) : default);   // CHANGED (round 58); (round 61) zone
                Deferred.Add(caption);
                if (ColorLevelB is not null) DeferredB.Add(caption);
            }
        }

        // NEW (round 48): the plain blocks of the machine view, in order. Other drives' tiles already carry
        // their gaps and drop out below a pixel; cables keep at least a pixel and a half so they read as
        // wires from far out; everything else is drawn as it is.
        private void DrawSolids(Solid[] solids)
        {
            foreach (var solid in solids)
            {
                var screen = ToScreen(solid.World);
                if (!screen.IntersectsWith(ViewLoose)) continue;
                switch (solid.Kind)
                {
                    case SolidKind.Tile when screen.Width < .8 || screen.Height < .8:
                        continue;
                    case SolidKind.Wire when screen.Width < screen.Height:
                        var across = Math.Max(1.5, screen.Width);
                        screen = new Rect(screen.X + screen.Width / 2 - across / 2, screen.Y, across, screen.Height);
                        break;
                    case SolidKind.Wire:
                        var thick = Math.Max(1.5, screen.Height);
                        screen = new Rect(screen.X, screen.Y + screen.Height / 2 - thick / 2, screen.Width, thick);
                        break;
                }
                EmitSolid(screen, solid.Color, solid.Color);
            }
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
        // CHANGED (round 63): screen is where the node's own layout rectangle lands - exactly, from its world
        // bounds - and inset is how far in from that each side of its block is drawn (the gutter to its
        // neighbours, and on a side it shares with its folder, that folder's frame as well). Blocks used to
        // be placed inside their folder's inset rectangle, whose frame and gutters are sized in pixels; as the
        // zoom changed, those pixels did not scale with the folder, so everything inside it slid a little -
        // more the deeper it sat - and snapped back each time the scene was rebuilt. Now every block sits
        // where the layout puts it at any zoom; only the width of the gaps around it changes.
        private int DrawNode(Node node, Rect screen, double alpha, double labelWeight, double labelWeightB, Rect zone, int budget,
            uint backdrop, uint backdropB, Thickness inset, bool onPath)
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
            // CHANGED (round 64): "render everything" no longer opens every folder whatever its budget. It spent the
            // frame's blocks depth-first, so the first large folders (top-left in the layout) took all 750,000
            // and every folder after them - the bottom-right of the view - was left as a bare surface: the open
            // folder's own shade, or black at the top level. Zooming in made it worse, because more blocks
            // passed the fifth-of-a-pixel threshold. Now a folder opens only if its share of the budget (its
            // share of the screen) can pay for its contents, as in the normal mode - dense corners stay solid
            // blocks until you zoom toward them, and nothing is ever cut off.
            var detail = isRoot || onPath ? 1 : DetailAmount(budget, node.Children.Length);
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
            // CHANGED (round 63): drawn in from its own layout rectangle, by its inset.
            var tile = isRoot ? screen : Inset(screen, inset, min * .12, out inset);
            var drawn = tile.IsEmpty ? Rect.Empty : Rect.Intersect(tile, ViewLoose);
            if (drawn.IsEmpty || drawn.Width <= 0 || drawn.Height <= 0)
            {
                EmitSolid(screen, backdrop, backdropB);   // belt and braces: never leave a region unpainted
                return 0;
            }
            if (isRoot) inset = default;
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
                    if (open > 0 && !onPath) open *= DetailAmount(budget, FlatChildCount(flat, entry));   // CHANGED (round 64): within its budget
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
                // CHANGED (round 63): the frame is how far in its children are drawn from its edges - not a
                // smaller rectangle they are squeezed into.
                var frame = Math.Clamp(min * .005, .5, 2);
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
                    var flatGutter = Math.Clamp(Math.Min(tile.Width, tile.Height) * .0025, .35, .9);
                    var tint = TintBelow(node, ColorLevel!);
                    var spent = DrawFlatChildren(Flat!, flatEntry, screen, inset, frame, childAlpha, fill, fillB, flatGutter,
                        tint, ColorLevelB is null ? tint : TintBelow(node, ColorLevelB), node.Depth + 1, budget - 1);   // CHANGED (round 64): budget
                    if (showPill) Deferred.Add(new Caption(node, visible, zone, alpha * labelWeight * open, true, Unpack(color)));
                    if (showPillB) DeferredB.Add(new Caption(node, visible, zone, alpha * labelWeightB * open, true, Unpack(colorB)));
                    return 1 + spent;
                }
                var children = node.Children;
                // Pooled: with fractal depth many folders are open in every frame.
                var screens = ArrayPool<Rect>.Shared.Rent(children.Length);
                var insets = ArrayPool<Thickness>.Shared.Rent(children.Length);   // NEW (round 63)
                var areas = ArrayPool<double>.Shared.Rent(children.Length);
                var visited = ArrayPool<int>.Shared.Rent(children.Length);
                var visitedCount = 0;
                var areaLeft = 0d;
                // This traversal runs only when constructing a new cached detail layer.
                // CHANGED (round 63): from the folder's own layout rectangle, so a child lands exactly where the
                // layout puts it whatever the zoom.
                var sx = screen.Width / node.Bounds.Width;
                var sy = screen.Height / node.Bounds.Height;
                double ox = node.Bounds.X, oy = node.Bounds.Y;
                // One gutter for every block in this folder, so all its gaps match.
                var childGutter = Math.Clamp(Math.Min(tile.Width, tile.Height) * .0025, .35, .9);
                // A side a child shares with the folder is drawn in by the folder's own inset and its frame.
                var tolerance = Math.Max(node.Bounds.Width, node.Bounds.Height) * 1e-9;
                for (var i = 0; i < children.Length; i++)
                {
                    var child = children[i];
                    // Each edge is mapped from the layout coordinate it shares with its neighbour, rather
                    // than from a position plus a separately scaled width. Two touching blocks then land on
                    // exactly the same pixel instead of a fraction apart, which is the other half of the
                    // misalignment: gaps that looked a pixel wider on one side than the other.
                    var left = screen.X + (child.Bounds.X - ox) * sx;
                    var right = screen.X + (child.Bounds.Right - ox) * sx;
                    var top = screen.Y + (child.Bounds.Y - oy) * sy;
                    var bottom = screen.Y + (child.Bounds.Bottom - oy) * sy;
                    var rect = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
                    if (!rect.IntersectsWith(ViewLoose)) continue;
                    var shown = Rect.Intersect(rect, ViewLoose);
                    insets[visitedCount] = new Thickness(
                        child.Bounds.X - node.Bounds.X <= tolerance ? inset.Left + frame + childGutter : childGutter,
                        child.Bounds.Y - node.Bounds.Y <= tolerance ? inset.Top + frame + childGutter : childGutter,
                        node.Bounds.Right - child.Bounds.Right <= tolerance ? inset.Right + frame + childGutter : childGutter,
                        node.Bounds.Bottom - child.Bounds.Bottom <= tolerance ? inset.Bottom + frame + childGutter : childGutter);
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
                    var spent = DrawNode(child, screens[k], childAlpha, childLabels, childLabelsB, childZone, share, fill, fillB, insets[k],
                        onPath && OpenPath.Contains(child));   // only ever true for one child of an open-path node
                    used += spent;
                }
                ArrayPool<Rect>.Shared.Return(screens);
                ArrayPool<Thickness>.Shared.Return(insets);
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
        // CHANGED (round 63): outer is the parent's own layout rectangle, own its drawn inset, frame its frame -
        // children are placed from the rectangle and drawn in from their edges, as in DrawNode.
        // CHANGED (round 64): budget - the blocks these children may use between them. Each gets one, and the rest
        // is shared by how much of the screen it covers, exactly as DrawNode shares a Node's budget - so the
        // first child can no longer spend what the ones after it needed. The shares are fixed before any child
        // is drawn, so a sibling opening further never takes detail away from its neighbours mid-zoom.
        private int DrawFlatChildren(FlatTreemapLayout flat, int parent, Rect outer, Thickness own, double frame, double alpha, uint backdrop, uint backdropB,
            double gutter, FlatTint tint, FlatTint tintB, int depth, int budget)
        {
            var used = 0;
            var end = flat.End(parent);
            // NEW (round 64): first pass - how many children are on screen, and how much of it they cover.
            var count = 0;
            var areaTotal = 0d;
            for (var child = parent + 1; child < end; child = flat.End(child))
            {
                var area = FlatShownArea(flat, child, outer);
                if (area <= 0) continue;
                count++;
                areaTotal += area;
            }
            var extra = Math.Max(0, budget - count);
            var index = 0;
            for (var child = parent + 1; child < end; child = flat.End(child), index++)
            {
                flat.Edges(child, out var l, out var t, out var r, out var b);
                var left = outer.X + l * outer.Width;
                var right = outer.X + r * outer.Width;
                var top = outer.Y + t * outer.Height;
                var bottom = outer.Y + b * outer.Height;
                var rect = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
                if (!rect.IntersectsWith(ViewLoose)) continue;
                var shown = Rect.Intersect(rect, ViewLoose);
                if (shown.Width * shown.Height <= 0) continue;
                var share = 1 + (areaTotal <= 0 ? 0 : (int)(extra * (shown.Width * shown.Height) / areaTotal));   // NEW (round 64)
                const double Edge = 1e-9;
                var edge = frame + gutter;   // the folder's frame, and the gutter every block has
                var inset = new Thickness(l <= Edge ? own.Left + edge : gutter, t <= Edge ? own.Top + edge : gutter,
                    r >= 1 - Edge ? own.Right + edge : gutter, b >= 1 - Edge ? own.Bottom + edge : gutter);
                used += DrawFlat(flat, child, rect, alpha, backdrop, backdropB, inset,
                    ChildTint(tint, flat, child, index), ChildTint(tintB, flat, child, index), depth, share);   // CHANGED (round 64): share
            }
            return used;
        }

        // NEW (round 64): how much of the screen a flat entry covers inside its parent's rectangle - the same
        // mapping DrawFlatChildren draws it with, so the shares add up to what is drawn.
        private double FlatShownArea(FlatTreemapLayout flat, int entry, Rect outer)
        {
            flat.Edges(entry, out var l, out var t, out var r, out var b);
            var left = outer.X + l * outer.Width;
            var top = outer.Y + t * outer.Height;
            var rect = new Rect(left, top, Math.Max(0, outer.X + r * outer.Width - left), Math.Max(0, outer.Y + b * outer.Height - top));
            if (!rect.IntersectsWith(ViewLoose)) return 0;
            var shown = Rect.Intersect(rect, ViewLoose);
            return shown.Width * shown.Height;
        }

        // NEW (round 64): a flat entry's direct children - at most a few hundred, since the layout groups long tails.
        private static int FlatChildCount(FlatTreemapLayout flat, int entry)
        {
            var count = 0;
            var end = flat.End(entry);
            for (var child = entry + 1; child < end; child = flat.End(child)) count++;
            return count;
        }
        // DrawNode for an entry with no Node: no labels (only a level's direct children are labelled, and
        // those are always Nodes), no hit-testing record, no load request - just the geometry.
        // CHANGED (round 43): both colours, as DrawNode.
        // CHANGED (round 64): budget - the blocks this entry and everything inside it may use.
        private int DrawFlat(FlatTreemapLayout flat, int entry, Rect screen, double alpha, uint backdrop, uint backdropB, Thickness inset,
            FlatTint tint, FlatTint tintB, int depth, int budget)
        {
            var min = Math.Min(screen.Width, screen.Height);
            if (Tiles >= Budget || min < MinimumTile)
            {
                EmitSolid(screen, backdrop, backdropB);
                return 0;
            }
            Tiles++;
            var tile = Inset(screen, inset, min * .12, out inset);   // CHANGED (round 63)
            var drawn = tile.IsEmpty ? Rect.Empty : Rect.Intersect(tile, ViewLoose);
            if (drawn.IsEmpty || drawn.Width <= 0 || drawn.Height <= 0)
            {
                EmitSolid(screen, backdrop, backdropB);
                return 0;
            }
            var spread = flat.Spread(entry);
            var color = FlatColor(tint, spread);
            var colorB = FlatColor(tintB, spread);
            // CHANGED (round 64): opens only as far as its budget pays for its children, as a Node does.
            var open = flat.IsContainer(entry) ? SmoothStep((min - ExpandLow) / (ExpandHigh - ExpandLow)) : 0;
            if (open > 0) open *= DetailAmount(budget, FlatChildCount(flat, entry));   // counted only when it could open
            var shallow = ColorLevel is null || depth - ColorLevel.Depth <= 1;
            var shallowB = ColorLevelB is null ? shallow : depth - ColorLevelB.Depth <= 1;
            var fill = Mix(backdrop, open > 0 ? Mix(color, Shade(color, shallow ? .55 : .72), open) : color, alpha);
            var fillB = Mix(backdropB, open > 0 ? Mix(colorB, Shade(colorB, shallowB ? .55 : .72), open) : colorB, alpha);
            if (open <= 0)
            {
                EmitSolid(tile, fill, fillB);
                return 1;
            }
            var frame = Math.Clamp(min * .005, .5, 2);
            // A folder whose contents fit in less than a pixel is one block: its children could only ever
            // be a spray of sub-pixel rectangles averaging to the same colour, and with a million-file
            // drive in view those would be most of the geometry.
            if ((tile.Width - frame * 2) * (tile.Height - frame * 2) < 1)
            {
                EmitSolid(tile, fill, fillB);
                return 1;
            }
            EmitSolid(tile, Shade(fill, FrameShade), Shade(fillB, FrameShade));
            var childGutter = Math.Clamp(Math.Min(tile.Width, tile.Height) * .0025, .35, .9);
            return 1 + DrawFlatChildren(flat, entry, screen, inset, frame, alpha * open, fill, fillB, childGutter, tint, tintB, depth + 1,
                budget - 1);   // CHANGED (round 64): budget
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
            Drive = CurrentDrive()?.Regions,   // NEW (round 44)
            Solids = CurrentDrive()?.Solids,   // NEW (round 48)
            Underlay = CurrentDrive()?.Underlay,   // NEW (round 51)
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
        // CHANGED (round 61): with the tags above it to keep clear of (the drive view's; the map's have none).
        var zone = caption.Zone.IsEmpty ? Rect.Empty : mapping.Transform(caption.Zone);
        if (caption.IsOpen)
        {
            var pill = LayoutPill(caption.Node, visible, zone, color, alpha);
            if (pill is { } tag) DrawPill(dc, tag);
        }
        else DrawLabel(dc, caption.Node, visible, zone, alpha);
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


    // NEW (round 63): a rectangle drawn in from each side by its own amount, each capped (so a tiny block
    // keeps most of itself); applied says what was actually taken. Empty when nothing is left.
    private static Rect Inset(Rect rect, Thickness by, double cap, out Thickness applied)
    {
        applied = new Thickness(Math.Min(by.Left, cap), Math.Min(by.Top, cap), Math.Min(by.Right, cap), Math.Min(by.Bottom, cap));
        var width = rect.Width - applied.Left - applied.Right;
        var height = rect.Height - applied.Top - applied.Bottom;
        return width > 0 && height > 0 ? new Rect(rect.X + applied.Left, rect.Y + applied.Top, width, height) : Rect.Empty;
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
        var size = Text(node, ref node.PillSize, node.Tag ?? node.SizeText, 11, false);   // CHANGED (round 58): Tag
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
        // CHANGED (round 58): the motherboard is no longer drawn here; it is part of the map itself (BuildDriveView).
        DrawCovers(dc);   // NEW (round 51): drive covers, over the scene and its labels
        if (_highlightNode is { } selected) Outline(dc, selected, AccentEdge);
        _hover = _mouseInside && !_dragging ? NodeAt(_mouse) : null;
        var target = _hover is null ? null : ClickTarget(_hover);
        // REVERTED (round 4): plain outline on hover.
        if (target is not null && _flight is null && !InDriveView) Outline(dc, target, HoverEdge);   // CHANGED (round 44)
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
                $"{(_gpuShown ? (_gpu?.IsMultisampled == true ? "GPU 4×AA" : "GPU") : "CPU")}{(_gpuShown ? (_gpu?.HasColorShader == true ? " · OKLCH fade" : " · RGB fade") : "")} · {_statTiles:N0} tiles · {_statPixelsW}×{_statPixelsH} px · " +
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
        // CHANGED (round 45): out at the drive, only the free and unindexed regions get a card; the files
        // themselves are one click back to the folder map.
        if (InDriveView != IsDriveRegion(hovered)) return;
        // CHANGED: describe the labeled block under the pointer (the item directly inside the
        // current folder), not the tiny tile within it. Zooming in moves the card a level deeper.
        if ((IsDriveRegion(hovered) ? hovered : ClickTarget(hovered)) is not { } node) return;
        const double pad = 12, swatch = 9, maxText = 340;
        if (_hoverText?.Node != node || _hoverText.Focus != _focusNode)
        {
            var folder = node.Parent is null ? null : FolderOf(node.Parent);
            var name = Make(node.Item.Name, 13, true);
            name.SetForegroundBrush(Ink);
            name.MaxTextWidth = maxText;
            var description = Make(node.Detail is { } own ? own   // CHANGED (round 51): covers, the computer, servers
                : IsDriveRegion(node) ? $"{node.SizeText} · {node.ShareText} of " +   // NEW (round 45)
                    (OtherDriveOf(node) is { } otherDrive ? $"{otherDrive.Root} ({DiskUsageSnapshot.FormatBytes(otherDrive.Total)})" : "the drive")   // CHANGED (round 47)
                : $"{DiskUsagePalette.CategoryName(node.Item)} · {node.SizeText}" +
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
        var swatchColor = IsDriveRegion(node) ? RegionColor(node)   // CHANGED (round 47)
            : _colorLevel is { } swatchLevel ? ComputeColor(node, swatchLevel) : _basePacked;
        dc.DrawEllipse(BrushFor(Unpack(swatchColor), 1), null, new Point(x + pad + swatch / 2, line + detail.Height / 2), swatch / 2, swatch / 2);
        dc.DrawText(detail, new Point(x + pad + swatch + 7, line));
        if (hint is not null) dc.DrawText(hint, new Point(x + pad, line + detail.Height + 6));
    }

    private string HintFor(Node target)
    {
        // NEW (round 45): what the two drive regions are.
        if (target.Item.Id == FreeSpaceId) return "Space on the drive not used by anything · click for the files";
        // NEW (round 47): another drive.
        if (OtherDriveOf(target) is { } drive)
            return !drive.Indexed ? $"{drive.Root} is not indexed yet · click to open it when it is"
                : OtherDrivePath(target) is { } path && !string.Equals(path, drive.Root, StringComparison.OrdinalIgnoreCase) ? $"Click to open {path}"
                : $"Click to open {drive.Root}";
        if (target.Item.Id == ComputerId) return "This computer and the drives connected to it · click for the files";   // NEW (round 48)
        if (CurrentDrive()?.ZoomTiles.ContainsKey(target.Item.Id) == true) return "Click to zoom in";   // NEW (round 59)
        if (target.Detail is not null) return "Click for the files";   // NEW (round 51): this drive's cover, a server
        if (target.Item.Id == OtherSpaceId)
            return "Used on the drive but not in the index: file system metadata, restore points, the recycle bin, " +
                   "and folders Clearspace could not read · click for the files";
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
        var current = cache.Text is not null && cache.Em == em;
        if (current && (cache.Source is null || ReferenceEquals(cache.Source, text) || cache.Source == text)) return cache.Text;
        // CHANGED (round 60): out of budget this frame, changed words keep showing the old ones until the
        // new ones are made, rather than the label blinking out.
        if (_textBudget <= 0) { _needsFrame = true; return current ? cache.Text : null; }
        _textBudget--;
        var formatted = Make(text, em, bold);
        cache = new CachedText(formatted, em, formatted.WidthIncludingTrailingWhitespace, Source: text);
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
        if (CoverAt(point) is { } cover) return cover;   // NEW (round 51): a closed drive is one thing to point at
        if (_cache is not { } cache || BatchFor(cache, presented: true) is not { Scale: > 0 } mapping) return null;
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
            if (!_dragging && delta.Length > 4) { _dragging = true; _flight = null; _openingDrive = null; }   // CHANGED (round 49)
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
        var cursor = _dragging ? Cursors.SizeAll : target is not null || InDriveView ? Cursors.Hand : null;   // CHANGED (round 44)
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
