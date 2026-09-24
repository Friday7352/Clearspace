// Clearspace | In-memory file index and search.

using System.IO;
using System.Runtime.InteropServices;

namespace Clearspace.Services;


[StructLayout(LayoutKind.Sequential)]
// DiskUsageSnapshot derives folder totals without changing persisted entries.
internal struct IndexEntry
{
    public long Size;
    public long ModifiedTicks;
    public long CreatedTicks;

    public int NameOffset;

    public int ParentIndex;

    public FileAttributes Attributes;
    public ushort NameLength;

    public readonly bool IsFolder => (Attributes & FileAttributes.Directory) != 0;

    public readonly bool IsHiddenOrSystem =>
        (Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
}


// CHANGED (live index): the index is now patched in place from file-system events instead of
// being rebuilt. Mutations are serialized by WriteGate; readers stay lock-free:
//   * appends write the entry, then publish the new count (readers never see a partial entry);
//   * deletions set RemovedFlag on the entry and its subtree (a single aligned write each);
//   * size updates overwrite one long.
// Child links (first child / next sibling) are built on the first live change so a path can be
// resolved without scanning the whole volume. Removed entries are dropped when the index is saved.
internal sealed class VolumeIndex
{
    private const int InitialEntries = 4096;
    private const int InitialPool = 65536;

    // NEW: marks an entry deleted since the last full scan. No real file attribute uses this bit.
    internal const FileAttributes RemovedFlag = unchecked((FileAttributes)0x80000000);

    private int[]? _firstChild;
    private int[]? _nextSibling;
    private int _removedCount;
    private long _version;

    private IndexEntry[] _entries;
    private char[] _names;

    // REMOVED (round 41): _folded, a lower-cased copy of every name kept only for search. It was half
    // of the index's name memory - hundreds of megabytes on a large drive - and matching the original
    // names with OrdinalIgnoreCase measured the same speed (vectorised for ASCII, 10M names: 117 ms
    // against 116 ms), so the copy bought nothing.

    private int _count;
    private int _poolLength;

    public VolumeIndex(string root, uint serialNumber)
    {
        Root = root;
        SerialNumber = serialNumber;
        BuiltUtc = DateTime.UtcNow;
        _entries = new IndexEntry[InitialEntries];
        _names = new char[InitialPool];
    }

    // NEW (round 42): an index sized up front, for a copy whose final size is known.
    private VolumeIndex(string root, uint serialNumber, int entries, int pool) : this(root, serialNumber)
    {
        _entries = new IndexEntry[Math.Max(InitialEntries, entries)];
        _names = new char[Math.Max(InitialPool, pool)];
    }

    internal VolumeIndex(
        string root,
        uint serialNumber,
        DateTime builtUtc,
        IndexEntry[] entries,
        int count,
        char[] names,
        int poolLength)
    {
        Root = root;
        SerialNumber = serialNumber;
        BuiltUtc = builtUtc;
        _entries = entries;
        _count = count;
        _names = names;
        _poolLength = poolLength;
        _settled = true;   // NEW (round 42): loaded at its full size
    }

    public string Root { get; }
    internal IndexScanTracker? ScanDetails { get; set; }

    public uint SerialNumber { get; }

    // When the last full scan of this volume finished. Live changes don't move it.
    public DateTime BuiltUtc { get; private set; }

    public int Count => Volatile.Read(ref _count);

    // NEW: live-update bookkeeping.
    internal object WriteGate { get; } = new();
    public long Version => Interlocked.Read(ref _version);
    public int RemovedCount => Volatile.Read(ref _removedCount);
    public bool IsDirty { get; private set; }
    internal void MarkSaved() => IsDirty = false;
    internal void MarkChanged() { Interlocked.Increment(ref _version); IsDirty = true; }
    public bool IsRemoved(int index) => (_entries[index].Attributes & RemovedFlag) != 0;

    internal IndexEntry[] Entries => _entries;

    internal char[] Names => _names;

    internal int PoolLength => _poolLength;

    // CHANGED (round 41): the names once (no folded copy), and the arrays as allocated rather than as
    // filled - growth slack is memory too, and the F3 readout should show it.
    public long EstimatedBytes =>
        ((long)_entries.Length * Marshal.SizeOf<IndexEntry>()) + ((long)_names.Length * sizeof(char))
        + ((long)(_firstChild?.Length ?? 0) + (_nextSibling?.Length ?? 0)) * sizeof(int);


    public int Add(
        int parentIndex,
        ReadOnlySpan<char> name,
        long size,
        long modifiedTicks,
        long createdTicks,
        FileAttributes attributes)
    {
        if (_count == _entries.Length)
            Array.Resize(ref _entries, Math.Max(InitialEntries, Grow(_entries.Length)));

        while (_poolLength + name.Length > _names.Length)
            Array.Resize(ref _names, Math.Max(InitialPool, Grow(_names.Length)));

        name.CopyTo(_names.AsSpan(_poolLength));

        var index = _count;
        _entries[index] = new IndexEntry
        {
            Size = size,
            ModifiedTicks = modifiedTicks,
            CreatedTicks = createdTicks,
            NameOffset = _poolLength,
            ParentIndex = parentIndex,
            Attributes = attributes,
            NameLength = (ushort)name.Length
        };

        _poolLength += name.Length;
        if (_firstChild is not null) Link(index, parentIndex); // NEW
        Volatile.Write(ref _count, index + 1);                 // CHANGED: publish after the entry is complete
        return index;
    }

    // ---------------------------------------------------------------- NEW: live updates

    private void Link(int index, int parent)
    {
        if (_firstChild!.Length <= index)
        {
            var size = Math.Max(index + 1, _entries.Length);
            Array.Resize(ref _firstChild, size);
            Array.Resize(ref _nextSibling, size);
        }
        _firstChild[index] = -1;
        _nextSibling![index] = parent >= 0 ? _firstChild[parent] : -1;
        if (parent >= 0) _firstChild[parent] = index;
    }

    // Caller holds WriteGate.
    private void EnsureLinks()
    {
        if (_firstChild is not null) return;
        var size = Math.Max(_count, _entries.Length);
        var first = new int[size];
        var next = new int[size];
        Array.Fill(first, -1);
        Array.Fill(next, -1);
        for (var i = 1; i < _count; i++)
        {
            var parent = _entries[i].ParentIndex;
            if (parent < 0 || parent >= i) continue;
            next[i] = first[parent];
            first[parent] = i;
        }
        _firstChild = first;
        _nextSibling = next;
    }

    // Resolves a full path to a live (not removed) entry, or -1. Caller holds WriteGate.
    internal int FindLocked(string path)
    {
        EnsureLinks();
        var root = Root.TrimEnd('\\', '/');
        var target = path.TrimEnd('\\', '/');
        if (target.Equals(root, StringComparison.OrdinalIgnoreCase)) return _count > 0 ? 0 : -1;
        if (!target.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return -1;
        var current = 0;
        foreach (var part in target[(root.Length + 1)..].Split('\\'))
        {
            var match = -1;
            for (var child = _firstChild![current]; child >= 0; child = _nextSibling![child])
            {
                if (!IsRemoved(child) && NameSpan(child).Equals(part, StringComparison.OrdinalIgnoreCase))
                {
                    match = child;
                    break;
                }
            }
            if (match < 0) return -1;
            current = match;
        }
        return current;
    }

    // Adds (or refreshes) one entry at `path`. Returns its index, or -1 if its folder isn't indexed.
    internal int Upsert(string path, long size, long modifiedTicks, long createdTicks, FileAttributes attributes)
    {
        lock (WriteGate)
        {
            var existing = FindLocked(path);
            if (existing >= 0)
            {
                ref var entry = ref _entries[existing];
                if (entry.Size != size || entry.ModifiedTicks != modifiedTicks)
                {
                    entry.Size = size;
                    entry.ModifiedTicks = modifiedTicks;
                    MarkChanged();
                }
                return existing;
            }
            var parent = FindLocked(Path.GetDirectoryName(path) ?? string.Empty);
            if (parent < 0 || !_entries[parent].IsFolder) return -1;
            var name = Path.GetFileName(path.TrimEnd('\\', '/'));
            if (name.Length == 0 || name.Length > ushort.MaxValue) return -1;
            var index = Add(parent, name, size, modifiedTicks, createdTicks, attributes & ~RemovedFlag);
            MarkChanged();
            return index;
        }
    }

    // Marks the entry at `path` and everything under it as removed.
    internal bool Remove(string path)
    {
        lock (WriteGate)
        {
            var index = FindLocked(path);
            if (index <= 0) return false;
            var pending = new Stack<int>();
            pending.Push(index);
            while (pending.TryPop(out var current))
            {
                if (IsRemoved(current)) continue;
                _entries[current].Attributes |= RemovedFlag;
                _removedCount++;
                for (var child = _firstChild![current]; child >= 0; child = _nextSibling![child])
                    pending.Push(child);
            }
            MarkChanged();
            return true;
        }
    }

    // Copy without removed entries (parents keep preceding children). Caller holds WriteGate.
    internal VolumeIndex CompactedCopyLocked()
    {
        // CHANGED (round 42): sized for everything that is not removed, so the copy never grows; it
        // used to start at 4,096 entries and resize its way up to the full index on every save.
        var copy = new VolumeIndex(Root, SerialNumber, _count - _removedCount, _poolLength) { BuiltUtc = BuiltUtc };
        var remap = new int[_count];
        for (var i = 0; i < _count; i++)
        {
            remap[i] = -1;
            ref readonly var entry = ref _entries[i];
            if ((entry.Attributes & RemovedFlag) != 0) continue;
            var parent = entry.ParentIndex < 0 ? -1 : remap[entry.ParentIndex];
            if (entry.ParentIndex >= 0 && parent < 0) continue;
            remap[i] = copy.Add(parent, NameSpan(i), entry.Size, entry.ModifiedTicks, entry.CreatedTicks, entry.Attributes);
        }
        copy.Compact();
        return copy;
    }

    // NEW (round 41): doubling is right while an index is small, but a compacted index for a large drive
    // is hundreds of megabytes, and the first file added by a live update used to double it again -
    // most of it empty for the rest of the session. Past a million entries it grows by an eighth.
    // CHANGED (round 42): only once the index has settled (loaded, or compacted after a build). While a
    // scan or a copy is still filling it, growing by an eighth meant twenty-odd full copies of a
    // multi-gigabyte array on the way up - several gigabytes of garbage per save - so it doubles then.
    private bool _settled;
    private int Grow(int length) => !_settled || length < (1 << 20) ? length * 2 : (int)Math.Min(Array.MaxLength, length + (long)length / 8);

    public void Compact()
    {
        _settled = true;
        if (_entries.Length != _count)
            Array.Resize(ref _entries, _count);

        if (_names.Length != _poolLength)
            Array.Resize(ref _names, _poolLength);
    }

    public ReadOnlySpan<char> NameSpan(int index)
    {
        ref var entry = ref _entries[index];
        return _names.AsSpan(entry.NameOffset, entry.NameLength);
    }

    public string GetName(int index) => new(NameSpan(index));

    public ref readonly IndexEntry Entry(int index) => ref _entries[index];


    private const int MaxChain = 256;

    [ThreadStatic]
    private static char[]? _pathBuffer;

    public string GetPath(int index)
    {
        if (index < 0 || index >= _count)
            return string.Empty;

        Span<int> chain = stackalloc int[MaxChain];
        var depth = 0;
        var current = index;

        while (current >= 0 && depth < MaxChain)
        {
            chain[depth++] = current;
            current = _entries[current].ParentIndex;
        }

        var capacity = 0;
        for (var i = 0; i < depth; i++)
            capacity += _entries[chain[i]].NameLength + 1;

        var buffer = _pathBuffer;

        if (buffer is null || buffer.Length < capacity)
            _pathBuffer = buffer = new char[Math.Max(512, capacity)];

        var written = 0;

        for (var i = depth - 1; i >= 0; i--)
        {
            var entry = _entries[chain[i]];

            if (written > 0 && buffer[written - 1] != Path.DirectorySeparatorChar)
                buffer[written++] = Path.DirectorySeparatorChar;

            _names.AsSpan(entry.NameOffset, entry.NameLength).CopyTo(buffer.AsSpan(written));
            written += entry.NameLength;
        }

        return new string(buffer, 0, written);
    }


    public static string Fold(string term) => term.ToLowerInvariant();

    public List<int> Search(
        IReadOnlyList<string> foldedTerms,
        bool showHidden,
        bool foldersOnly,
        bool filesOnly,
        int limit,
        CancellationToken token)
    {
        var results = new List<int>();

        if (_count == 0 || foldedTerms.Count == 0 || limit <= 0)
            return results;

        const int ChunkSize = 4096;
        var count = Count; // CHANGED: one consistent count while live appends continue
        var chunks = (count + ChunkSize - 1) / ChunkSize;
        var gate = new object();
        var total = 0;

        try
        {
            Parallel.For(0, chunks, new ParallelOptions { CancellationToken = token }, chunk =>
            {
                if (Volatile.Read(ref total) >= limit)
                    return;

                var start = chunk * ChunkSize;
                var end = Math.Min(count, start + ChunkSize);
                List<int>? local = null;

                for (var i = start; i < end; i++)
                {
                    if ((_entries[i].Attributes & RemovedFlag) != 0) // NEW: deleted since the last scan
                        continue;

                    if (!showHidden && _entries[i].IsHiddenOrSystem)
                        continue;

                    var isFolder = _entries[i].IsFolder;

                    if (foldersOnly && !isFolder)
                        continue;

                    if (filesOnly && isFolder)
                        continue;

                    // CHANGED (round 41): the original name, matched ignoring case (terms arrive lower-cased).
                    var name = _names.AsSpan(_entries[i].NameOffset, _entries[i].NameLength);
                    var matched = true;

                    for (var t = 0; t < foldedTerms.Count; t++)
                    {
                        if (name.IndexOf(foldedTerms[t].AsSpan(), StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            matched = false;
                            break;
                        }
                    }

                    if (matched)
                        (local ??= []).Add(i);
                }

                if (local is null)
                    return;

                lock (gate)
                {
                    results.AddRange(local);
                    total = results.Count;
                }
            });
        }
        catch (OperationCanceledException)
        {
            return [];
        }

        if (results.Count > limit)
            results.RemoveRange(limit, results.Count - limit);

        return results;
    }
}
