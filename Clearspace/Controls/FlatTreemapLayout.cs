using Clearspace.Services;

namespace Clearspace.Controls;

// NEW (round 39): the whole drive laid out once, as flat arrays instead of one object per file.
//
// "Render everything" used to get its detail by materialising a Node for every file on the drive:
// a class of roughly 400 bytes, plus a DiskUsageItem and its name string, allocated one folder at a
// time as the camera reached it. A million files was the better part of a gigabyte of small
// objects that the collector had to trace on every gen2 pass, and each folder appeared only once
// it had been read - the loading you could watch while zooming.
//
// This is the same layout computed up front, for everything, into five arrays: four float edges,
// a subtree end, an id, a colour-spread value and a flag byte per entry - 31 bytes. A million
// entries is about 30 MB in a handful of large arrays that contain no references, so the collector
// never walks them. The control keeps its Node tree for what needs objects (labels, hover, clicks,
// navigation) at the normal mode's modest size, and draws every finer block straight from here.
//
// Entries are in depth-first pre-order, so a folder's whole subtree is the contiguous range
// [i, End(i)): its first child is i + 1, the next sibling of any entry is End(entry), and skipping a
// subtree that is off screen or too small is a single jump.
//
// The layout reproduces DiskUsageTreemap.LayoutLevel exactly - the same ordering, the same
// "smaller items" grouping, the same squarified call on the same world rectangles - so a folder
// drawn from here and the same folder drawn from its Nodes land on the same pixels, and the switch
// from one to the other as Nodes load is invisible.
internal sealed class FlatTreemapLayout
{
    private const byte ContainerFlag = 1;
    private const byte GroupFlag = 2;
    // A folder holding more than this many files lays its children's subtrees out in parallel.
    private const long ParallelFiles = 25_000;

    private readonly float[] _edges;   // left, top, right, bottom per entry, as fractions of the parent
    private readonly int[] _ends;      // exclusive end of each entry's subtree
    private readonly int[] _ids;       // snapshot id; groups use LayoutLevel's -1 - position
    private readonly ushort[] _spread; // 0..1023, the name hash DiskUsageTreemap.Spread computes
    private readonly byte[] _flags;

    private FlatTreemapLayout(object source, double aspect, Chunk chunk, double milliseconds)
    {
        Source = source;
        Aspect = aspect;
        Count = chunk.Count;
        // Kept as built rather than copied to exact length: the root chunk is sized to an upper bound
        // up front, so the slack is only the zero-byte and excluded entries, and a copy would double
        // the peak for the length of it.
        _edges = chunk.Edges;
        _ends = chunk.Ends;
        _ids = chunk.Ids;
        _spread = chunk.Spreads;
        _flags = chunk.Flags;
        BuildMilliseconds = milliseconds;
    }

    /// <summary>The snapshot this was laid out from; a layout is only valid for that exact instance.</summary>
    public object Source { get; }
    /// <summary>Width of the root's world rectangle (its height is 1), exactly as the Node tree's root.</summary>
    public double Aspect { get; }
    public int Count { get; }
    public double BuildMilliseconds { get; }
    public long ApproximateBytes => (long)_ids.Length * (16 + 4 + 4 + 2 + 1);

    public int End(int entry) => _ends[entry];
    public int Id(int entry) => _ids[entry];
    public bool IsContainer(int entry) => (_flags[entry] & ContainerFlag) != 0;
    public bool IsGroup(int entry) => (_flags[entry] & GroupFlag) != 0;
    public double Spread(int entry) => _spread[entry] / 1023d;

    /// <summary>The entry's edges as fractions of its parent's rectangle.</summary>
    public void Edges(int entry, out float left, out float top, out float right, out float bottom)
    {
        var at = entry * 4;
        left = _edges[at];
        top = _edges[at + 1];
        right = _edges[at + 2];
        bottom = _edges[at + 3];
    }

    // ------------------------------------------------------------------------------------ build

    /// <summary>
    /// Lays out the whole tree under entry 0 in a world rectangle of (0, 0, aspect, 1). Thread-safe
    /// and pure: reads the source, allocates only its own buffers. Runs on the thread pool.
    /// </summary>
    public static FlatTreemapLayout Build(IFlatSource source, double aspect, CancellationToken token)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var root = new Item(0, source.BytesOf(0), source.FilesOf(0), source.IsFolderEntry(0), null, 0, 0, null);
        // Every entry appears at most once, plus "smaller items" groups, which hold about 150 members
        // each: a 1/64 margin covers them, so this never regrows.
        var chunk = new Chunk(Math.Max(1024, source.Count + source.Count / 64 + 1024));
        var builder = new Builder(source, token);
        var index = chunk.Add(0, root.Bytes > 0 ? ContainerFlag : (byte)0, SpreadOf(source.NameOf(0)), 0, 0, 1, 1);
        if (root.Bytes > 0) builder.WriteChildren(chunk, root, new Box(0, 0, aspect, 1));
        chunk.Ends[index] = chunk.Count;
        return new FlatTreemapLayout(source, aspect, chunk, clock.Elapsed.TotalMilliseconds);
    }

    // FNV-1a over the name's UTF-16 units, exactly as DiskUsageTreemap.Spread, quantised the same way.
    private static ushort SpreadOf(ReadOnlySpan<char> name)
    {
        var hash = 2166136261u;
        foreach (var character in name) hash = (hash ^ character) * 16777619u;
        return (ushort)((hash >> 8) & 1023);
    }

    // A world rectangle in doubles, carried exactly as LayoutLevel carries a Rect.
    private readonly record struct Box(double X, double Y, double Width, double Height)
    {
        public double Right => X + Width;
        public double Bottom => Y + Height;
    }

    // One laid-out child. Members/MemberStart/MemberCount describe a "smaller items" group: a slice
    // of its folder's sorted children, which LayoutLevel then lays out again inside the group.
    private readonly record struct Item(int Id, long Bytes, long Files, bool IsFolder, int[]? Members, int MemberStart,
        int MemberCount, string? GroupName)
    {
        public bool IsGroup => Members is not null;
        public bool IsContainer => IsGroup ? MemberCount > 1 : IsFolder && Bytes > 0;
    }

    private sealed class Builder(IFlatSource source, CancellationToken token)
    {
        public void WriteChildren(Chunk into, Item parent, Box bounds)
        {
            token.ThrowIfCancellationRequested();
            var children = Layout(parent, bounds);
            if (children.Count == 0) return;
            if (parent.Files > ParallelFiles && children.Count > 1)
            {
                // Each child's subtree goes to its own chunk, then they are appended in order: the
                // result is byte-for-byte what the sequential walk would have written.
                var parts = new Chunk[children.Count];
                Parallel.For(0, children.Count, new ParallelOptions { CancellationToken = token }, i =>
                {
                    var part = new Chunk((int)Math.Clamp(children[i].Item.Files + children[i].Item.Files / 16 + 16, 16, 1 << 22));
                    Write(part, children[i].Item, children[i].Bounds, bounds);
                    parts[i] = part;
                });
                foreach (var part in parts) into.Append(part);
                return;
            }
            foreach (var (item, box) in children) Write(into, item, box, bounds);
        }

        private void Write(Chunk into, Item item, Box box, Box parent)
        {
            // Edges relative to the parent. A shared edge between two siblings is the same double in
            // both, so it rounds to the same float, and the two blocks meet on the same pixel.
            var left = (float)((box.X - parent.X) / parent.Width);
            var top = (float)((box.Y - parent.Y) / parent.Height);
            var right = (float)((box.Right - parent.X) / parent.Width);
            var bottom = (float)((box.Bottom - parent.Y) / parent.Height);
            var flags = (byte)((item.IsContainer ? ContainerFlag : 0) | (item.IsGroup ? GroupFlag : 0));
            var spread = item.GroupName is { } name ? SpreadOf(name) : SpreadOf(source.NameOf(item.Id));
            var index = into.Add(item.Id, flags, spread, left, top, right, bottom);
            if (item.IsContainer) WriteChildren(into, item, box);
            into.Ends[index] = into.Count;
        }

        // DiskUsageTreemap.LayoutLevel, over ids instead of DiskUsageItem records. Any change there
        // has to be made here too, or the flat detail and the Node tree drift apart.
        private List<(Item Item, Box Bounds)> Layout(Item parent, Box bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return [];
            int[] sorted;
            int start, count;
            if (parent.IsGroup)
            {
                // Members were taken from an already sorted, all-positive list: LayoutLevel's
                // IsDescending check passes and it keeps their order, and so do we.
                sorted = parent.Members!;
                start = parent.MemberStart;
                count = parent.MemberCount;
            }
            else
            {
                sorted = SortedPositiveChildren(parent.Id);
                start = 0;
                count = sorted.Length;
            }
            if (count == 0) return [];

            var entries = new List<Item>(Math.Min(count, DiskUsagePalette.DirectLimit + 1));
            if (count <= DiskUsagePalette.DirectLimit)
                for (var i = 0; i < count; i++) entries.Add(Direct(sorted[start + i]));
            else
            {
                for (var i = 0; i < DiskUsagePalette.NamedLimit; i++) entries.Add(Direct(sorted[start + i]));
                var tail = count - DiskUsagePalette.NamedLimit;
                var groups = Math.Clamp((tail + TailGroupSize - 1) / TailGroupSize, 1, MaxTailGroups);
                var chunk = (tail + groups - 1) / groups;
                for (var at = DiskUsagePalette.NamedLimit; at < count; at += chunk)
                {
                    token.ThrowIfCancellationRequested();
                    var members = Math.Min(chunk, count - at);
                    if (members == 1) { entries.Add(Direct(sorted[start + at])); continue; }
                    long bytes = 0, files = 0;
                    for (var k = 0; k < members; k++)
                    {
                        var id = sorted[start + at + k];
                        bytes += source.BytesOf(id);
                        files += source.FilesOf(id);
                    }
                    entries.Add(new Item(-1 - entries.Count, bytes, files, false, sorted, start + at, members,
                        $"{members:N0} smaller items"));
                }
            }

            var weights = new long[entries.Count];
            for (var i = 0; i < weights.Length; i++) weights[i] = entries[i].Bytes;
            var placed = new Box?[entries.Count];
            foreach (var tile in SquarifiedTreemap.Layout(weights, bounds.Width, bounds.Height))
                placed[tile.ItemIndex] = new Box(bounds.X + tile.X, bounds.Y + tile.Y, Math.Max(0, tile.Width), Math.Max(0, tile.Height));
            var result = new List<(Item, Box)>(entries.Count);
            for (var i = 0; i < entries.Count; i++)
                if (placed[i] is { } box) result.Add((entries[i], box));
            return result;
        }

        private Item Direct(int id) => new(id, source.BytesOf(id), source.FilesOf(id), source.IsFolderEntry(id), null, 0, 0, null);

        [ThreadStatic] private static List<int>? _scratch;

        // A pool thread keeps its scratch list, but not at the size of the largest folder it ever read.
        private static void Release(List<int> scratch)
        {
            if (scratch.Capacity > 65_536) _scratch = null;
        }

        // The order DiskUsageSnapshot.Children produces - size descending, then name ignoring case,
        // then id - with zero-byte entries dropped as LayoutLevel drops them.
        private int[] SortedPositiveChildren(int folder)
        {
            var scratch = _scratch ??= new List<int>(256);
            scratch.Clear();
            source.ChildIds(folder, scratch);
            var count = 0;
            for (var i = 0; i < scratch.Count; i++)
                if (source.BytesOf(scratch[i]) > 0) scratch[count++] = scratch[i];
            if (count == 0) { Release(scratch); return []; }
            var ids = new int[count];
            var keys = new long[count];
            for (var i = 0; i < count; i++) { ids[i] = scratch[i]; keys[i] = source.BytesOf(ids[i]); }
            Release(scratch);
            Array.Sort(keys, ids, Comparer<long>.Create((a, b) => b.CompareTo(a)));
            // Equal sizes are common (duplicate files, empty-ish logs); order each run of them by name.
            for (var runStart = 0; runStart < count;)
            {
                var runEnd = runStart + 1;
                while (runEnd < count && keys[runEnd] == keys[runStart]) runEnd++;
                if (runEnd - runStart > 1)
                    Array.Sort(ids, runStart, runEnd - runStart, Comparer<int>.Create((a, b) =>
                    {
                        var name = source.NameOf(a).CompareTo(source.NameOf(b), StringComparison.OrdinalIgnoreCase);
                        return name != 0 ? name : a.CompareTo(b);
                    }));
                runStart = runEnd;
            }
            return ids;
        }
    }

    // Must match DiskUsageTreemap's own constants of the same names.
    internal const int TailGroupSize = 150;
    internal const int MaxTailGroups = 100;

    // A growable set of parallel arrays in pre-order.
    private sealed class Chunk(int capacity)
    {
        public float[] Edges = new float[capacity * 4];
        public int[] Ends = new int[capacity];
        public int[] Ids = new int[capacity];
        public ushort[] Spreads = new ushort[capacity];
        public byte[] Flags = new byte[capacity];
        public int Count;

        public int Add(int id, byte flags, ushort spread, float left, float top, float right, float bottom)
        {
            Ensure(Count + 1);
            var at = Count * 4;
            Edges[at] = left;
            Edges[at + 1] = top;
            Edges[at + 2] = right;
            Edges[at + 3] = bottom;
            Ids[Count] = id;
            Flags[Count] = flags;
            Spreads[Count] = spread;
            Ends[Count] = Count + 1;
            return Count++;
        }

        public void Append(Chunk other)
        {
            Ensure(Count + other.Count);
            Array.Copy(other.Edges, 0, Edges, Count * 4, other.Count * 4);
            Array.Copy(other.Ids, 0, Ids, Count, other.Count);
            Array.Copy(other.Flags, 0, Flags, Count, other.Count);
            Array.Copy(other.Spreads, 0, Spreads, Count, other.Count);
            for (var i = 0; i < other.Count; i++) Ends[Count + i] = other.Ends[i] + Count;
            Count += other.Count;
        }

        private void Ensure(int needed)
        {
            if (needed <= Ids.Length) return;
            var size = Math.Max(needed, Ids.Length * 2);
            Array.Resize(ref Edges, size * 4);
            Array.Resize(ref Ends, size);
            Array.Resize(ref Ids, size);
            Array.Resize(ref Spreads, size);
            Array.Resize(ref Flags, size);
        }
    }
}
