// Clearspace | In-memory file index and search.
//
// CHANGED (round 65, memory): the index is laid out to take about half the memory it used to, with the
// same answers and the same search speed.
//   * IndexEntry is 28 bytes instead of 40: sizes are 48-bit (up to 256 TiB per file), and dates are kept
//     to the second (seconds since 1970) instead of to the 100 nanoseconds. Nothing shows or compares
//     dates more finely than that.
//   * Names are stored as one byte per character when they are plain ASCII (nearly all of them), and as
//     UTF-16 otherwise, so no name is ever changed by the encoding.
//   * Entries and names live in fixed-size chunks rather than one huge array each. Adding to the index
//     never copies all of it again (each copy briefly held two whole indexes, and .NET kept the space).
//   * There are no child-link arrays any more (8 bytes per entry, built on the first live change). Entries
//     are kept in breadth-first order - a folder's children are next to each other and ParentIndex never
//     goes down - so a folder's children are found with a binary search. Entries added live after that
//     (the "tail") are linked per folder in a small table of their own.

using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Clearspace.Services;


// CHANGED (round 65, memory): 28 bytes (was 40). The fields that shrank are private and reached through
// properties with the old names, so code reading Size, ModifiedTicks, CreatedTicks and Attributes is
// unchanged. NameLength is now the name's length in characters; NameBytes is its length in the pool.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct IndexEntry
{
    private uint _sizeLow;
    private ushort _sizeHigh;
    private ushort _name;          // bit 15: stored as UTF-16; bits 0-14: bytes in the name pool
    private uint _modified;        // seconds since 1970-01-01 UTC; 0 = unknown
    private uint _created;
    public int NameOffset;         // position in the volume's name pool
    public int ParentIndex;
    private uint _attributes;

    internal const long MaxSize = (1L << 48) - 1;
    internal const int WideFlag = 0x8000;
    internal const int MaxNameBytes = 0x7FFF;
    private const long UnixEpochFileTime = 116_444_736_000_000_000L;

    public long Size
    {
        readonly get => ((long)_sizeHigh << 32) | _sizeLow;
        set
        {
            var clamped = Math.Clamp(value, 0, MaxSize);
            _sizeLow = (uint)clamped;
            _sizeHigh = (ushort)(clamped >> 32);
        }
    }

    // FILETIME ticks, as before, rounded down to the second.
    public long ModifiedTicks
    {
        readonly get => Unpack(_modified);
        set => _modified = Pack(value);
    }

    public long CreatedTicks
    {
        readonly get => Unpack(_created);
        set => _created = Pack(value);
    }

    public FileAttributes Attributes
    {
        readonly get => (FileAttributes)_attributes;
        set => _attributes = (uint)value;
    }

    public readonly int NameBytes => _name & MaxNameBytes;

    public readonly bool IsWideName => (_name & WideFlag) != 0;

    // In characters.
    public readonly int NameLength => IsWideName ? NameBytes / 2 : NameBytes;

    public readonly bool IsFolder => (Attributes & FileAttributes.Directory) != 0;

    public readonly bool IsHiddenOrSystem =>
        (Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    internal void SetName(int offset, int bytes, bool wide)
    {
        NameOffset = offset;
        _name = (ushort)(bytes | (wide ? WideFlag : 0));
    }

    internal static uint Pack(long fileTime)
    {
        if (fileTime <= 0) return 0;
        if (fileTime <= UnixEpochFileTime) return 1;   // before 1970: kept as "1970", not as "unknown"
        return (uint)Math.Clamp((fileTime - UnixEpochFileTime) / TimeSpan.TicksPerSecond, 1, uint.MaxValue);
    }

    internal static long Unpack(uint seconds)
        => seconds == 0 ? 0 : UnixEpochFileTime + seconds * TimeSpan.TicksPerSecond;

    // What a date reads back as once stored, for comparing a date from disk with a stored one.
    internal static long Round(long fileTime) => Unpack(Pack(fileTime));
}


// CHANGED (live index): the index is patched in place from file-system events instead of being rebuilt.
// Mutations are serialized by WriteGate; readers stay lock-free:
//   * appends write the entry, then publish the new count (readers never see a partial entry);
//   * deletions set RemovedFlag on the entry and its subtree (a single aligned write each);
//   * size updates overwrite the size fields.
// Removed entries are dropped when the index is saved.
internal sealed class VolumeIndex
{
    // NEW (round 65, memory): chunk sizes. 64K entries (1.8 MB) and 1 MB of names per chunk. Only the first
    // chunk starts small (a USB stick with a hundred files should not cost 3 MB) and grows to full size.
    internal const int EntryShift = 16;
    internal const int EntryChunk = 1 << EntryShift;
    internal const int EntryMask = EntryChunk - 1;
    internal const int NameShift = 20;
    internal const int NameChunk = 1 << NameShift;
    internal const int NameMask = NameChunk - 1;
    private const int FirstEntryChunk = 256;
    private const int FirstNameChunk = 4096;
    private const int MaxNameChunks = int.MaxValue >> NameShift;   // positions stay below 2 GB
    internal static readonly int EntrySize = Unsafe.SizeOf<IndexEntry>();

    // Longest name accepted, in characters (Windows' own limit is 255).
    internal const int MaxNameChars = IndexEntry.MaxNameBytes / 2;

    // NEW: marks an entry deleted since the last full scan. No real file attribute uses this bit.
    internal const FileAttributes RemovedFlag = unchecked((FileAttributes)0x80000000);

    private IndexEntry[][] _entries = [];
    private byte[][] _names = [];
    private int _count;
    private int _poolLength;
    private long _entryBytes;   // allocated, for EstimatedBytes (read on every folder of a build)
    private long _nameBytes;

    // NEW (round 65, memory): entries [0, _ordered) are breadth-first, so ParentIndex never goes down in
    // that range and a folder's children there are contiguous. Everything after is the tail: entries added
    // live, linked per folder (_tailHeads: folder -> newest child; _tailNext: child -> next older child).
    private int _ordered;
    private int _lastOrderedParent = -1;
    private int _tailBase = -1;
    private ConcurrentDictionary<int, int>? _tailHeads;
    private int[] _tailNext = [];
    private int _tailHeadCount;

    private int _removedCount;
    private long _version;

    // REMOVED (round 41): _folded, a lower-cased copy of every name kept only for search.
    // REMOVED (round 65, memory): _firstChild and _nextSibling (8 bytes per entry), replaced by the
    // breadth-first order above.

    public VolumeIndex(string root, uint serialNumber)
        : this(root, serialNumber, DateTime.UtcNow)
    {
    }

    // NEW (round 65): for an index rebuilt from saved data (FileIndexStore's format upgrade).
    internal VolumeIndex(string root, uint serialNumber, DateTime builtUtc)
    {
        Root = root;
        SerialNumber = serialNumber;
        BuiltUtc = builtUtc;
    }

    // CHANGED (round 65): takes chunks (FileIndexStore reads them straight from the save file). The caller
    // has validated them (Validate). Works out how much is breadth-first and links whatever is not.
    internal VolumeIndex(
        string root,
        uint serialNumber,
        DateTime builtUtc,
        IndexEntry[][] entries,
        int count,
        byte[][] names,
        int poolLength)
    {
        Root = root;
        SerialNumber = serialNumber;
        BuiltUtc = builtUtc;
        _entries = entries;
        _names = names;
        _count = count;
        _poolLength = poolLength;

        foreach (var chunk in entries) _entryBytes += (long)(chunk?.Length ?? 0) * EntrySize;
        foreach (var chunk in names) _nameBytes += chunk?.Length ?? 0;

        for (var i = 0; i < count; i++)
        {
            var parent = At(i).ParentIndex;
            if (i == _ordered && (i == 0 ? parent < 0 : parent >= 0 && parent >= _lastOrderedParent && parent < i))
            {
                _lastOrderedParent = parent;
                _ordered = i + 1;
            }
            else
                LinkTail(i, parent);
        }
    }

    // NEW (round 65): a second index over the same data, published as newer (tests use it to stand in for
    // a rescan). Its live changes are its own only as far as the tail goes; it is not meant to be edited.
    internal VolumeIndex SharedCopy(DateTime builtUtc)
        => new(Root, SerialNumber, builtUtc, _entries, _count, _names, _poolLength);

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
    public bool IsRemoved(int index) => (At(index).Attributes & RemovedFlag) != 0;

    // CHANGED (round 65): the storage is chunked. Entry i is EntryChunks[i >> EntryShift][i & EntryMask];
    // a name at position p is NameChunks[p >> NameShift] from p & NameMask. Read Count first.
    internal IndexEntry[][] EntryChunks => Volatile.Read(ref _entries);

    internal byte[][] NameChunks => Volatile.Read(ref _names);

    internal int PoolLength => _poolLength;

    // NEW (round 65): how many entries are breadth-first (the rest are the live tail).
    internal int OrderedCount => Volatile.Read(ref _ordered);

    // CHANGED (round 65): what is allocated, kept as a running total.
    public long EstimatedBytes =>
        Volatile.Read(ref _entryBytes) + Volatile.Read(ref _nameBytes) +
        (long)_tailNext.Length * sizeof(int) + (long)_tailHeadCount * 48;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref IndexEntry At(int index) => ref _entries[index >> EntryShift][index & EntryMask];

    public ref readonly IndexEntry Entry(int index) => ref At(index);

    // NEW (round 65): writable access, for tests that damage an index on purpose.
    internal ref IndexEntry EntryAt(int index) => ref At(index);


    public int Add(
        int parentIndex,
        ReadOnlySpan<char> name,
        long size,
        long modifiedTicks,
        long createdTicks,
        FileAttributes attributes)
    {
        if (name.Length > MaxNameChars)
            throw new ArgumentOutOfRangeException(nameof(name), "The name is too long for the index.");

        // NEW (round 65): ASCII names take one byte per character; anything else keeps its UTF-16 exactly.
        var wide = !Ascii.IsValid(name);
        var bytes = wide ? name.Length * 2 : name.Length;
        var offset = ReserveName(bytes, wide);
        var target = _names[offset >> NameShift].AsSpan(offset & NameMask, bytes);

        if (wide)
            MemoryMarshal.AsBytes(name).CopyTo(target);
        else
            Ascii.FromUtf16(name, target, out _);

        var index = _count;
        EnsureEntry(index);
        ref var entry = ref At(index);
        entry = default;
        entry.Size = size;
        entry.ModifiedTicks = modifiedTicks;
        entry.CreatedTicks = createdTicks;
        entry.ParentIndex = parentIndex;
        entry.Attributes = attributes;
        entry.SetName(offset, bytes, wide);
        _poolLength = offset + bytes;

        // NEW (round 65): still breadth-first, or part of the tail.
        if (index == _ordered && (index == 0 ? parentIndex < 0 : parentIndex >= 0 && parentIndex >= _lastOrderedParent && parentIndex < index))
        {
            _lastOrderedParent = parentIndex;
            Volatile.Write(ref _ordered, index + 1);
        }
        else
            LinkTail(index, parentIndex);

        Volatile.Write(ref _count, index + 1);                 // CHANGED: publish after the entry is complete
        return index;
    }

    // ---------------------------------------------------------------- NEW (round 65): chunks

    private void EnsureEntry(int index)
    {
        var chunkIndex = index >> EntryShift;
        var local = index & EntryMask;
        var directory = _entries;

        if (chunkIndex >= directory.Length)
        {
            var larger = new IndexEntry[Math.Max(4, Math.Max(chunkIndex + 1, directory.Length * 2))][];
            Array.Copy(directory, larger, directory.Length);
            Volatile.Write(ref _entries, directory = larger);
        }

        var chunk = directory[chunkIndex];

        if (chunk is null)
        {
            directory[chunkIndex] = new IndexEntry[chunkIndex == 0 ? FirstEntryChunk : EntryChunk];
            Interlocked.Add(ref _entryBytes, (long)directory[chunkIndex].Length * EntrySize);
        }
        else if (local >= chunk.Length)
        {
            var length = Math.Min(EntryChunk, Math.Max(local + 1, chunk.Length * 2));
            Array.Resize(ref chunk, length);
            Interlocked.Add(ref _entryBytes, (long)(length - directory[chunkIndex].Length) * EntrySize);
            directory[chunkIndex] = chunk;
        }
    }

    // Where the next name of `bytes` bytes goes. A name never straddles two chunks, and UTF-16 names start
    // on an even position.
    internal static int PlaceName(int position, int bytes, bool wide)
    {
        if (wide && (position & 1) != 0) position++;
        if ((position & NameMask) + bytes > NameChunk) position = ((position >> NameShift) + 1) << NameShift;
        return position;
    }

    private int ReserveName(int bytes, bool wide)
    {
        var position = PlaceName(_poolLength, bytes, wide);
        var chunkIndex = position >> NameShift;
        var needed = (position & NameMask) + bytes;

        if (chunkIndex >= MaxNameChunks)
            throw new InvalidOperationException("The index's name pool is full.");

        var directory = _names;

        if (chunkIndex >= directory.Length)
        {
            var larger = new byte[Math.Max(4, Math.Max(chunkIndex + 1, directory.Length * 2))][];
            Array.Copy(directory, larger, directory.Length);
            Volatile.Write(ref _names, directory = larger);
        }

        var chunk = directory[chunkIndex];

        if (chunk is null)
        {
            directory[chunkIndex] = new byte[chunkIndex == 0 ? Math.Max(FirstNameChunk, needed) : NameChunk];
            Interlocked.Add(ref _nameBytes, directory[chunkIndex].Length);
        }
        else if (needed > chunk.Length)
        {
            var length = Math.Min(NameChunk, Math.Max(needed, chunk.Length * 2));
            Array.Resize(ref chunk, length);
            Interlocked.Add(ref _nameBytes, length - directory[chunkIndex].Length);
            directory[chunkIndex] = chunk;
        }

        return position;
    }

    // CHANGED (round 65): trims the last chunks to what is used. Nothing is copied but those two chunks.
    public void Compact()
    {
        if (_count == 0)
        {
            Volatile.Write(ref _entries, []);
            _entryBytes = 0;
        }
        else
        {
            var last = (_count - 1) >> EntryShift;
            var used = ((_count - 1) & EntryMask) + 1;
            var directory = _entries;

            if (directory[last].Length != used)
            {
                var chunk = directory[last];
                Array.Resize(ref chunk, used);
                directory[last] = chunk;
            }

            if (directory.Length != last + 1)
                Volatile.Write(ref _entries, directory[..(last + 1)]);

            _entryBytes = 0;
            foreach (var chunk in _entries) _entryBytes += (long)chunk.Length * EntrySize;
        }

        if (_poolLength == 0)
        {
            // Entries with empty names still point at position 0, so one empty chunk stays.
            Volatile.Write(ref _names, _count == 0 ? [] : new[] { Array.Empty<byte>() });
            _nameBytes = 0;
        }
        else
        {
            var last = (_poolLength - 1) >> NameShift;
            var used = ((_poolLength - 1) & NameMask) + 1;
            var directory = _names;

            if (directory[last].Length > used)
            {
                var chunk = directory[last];
                Array.Resize(ref chunk, used);
                directory[last] = chunk;
            }

            if (directory.Length != last + 1)
                Volatile.Write(ref _names, directory[..(last + 1)]);

            _nameBytes = 0;
            foreach (var chunk in _names) _nameBytes += chunk?.Length ?? 0;
        }
    }

    // ---------------------------------------------------------------- NEW (round 65): children

    private void LinkTail(int index, int parent)
    {
        if (parent < 0 || parent >= index)
            return;

        if (_tailBase < 0)
            _tailBase = index;

        var heads = _tailHeads ??= new ConcurrentDictionary<int, int>();
        var slot = index - _tailBase;
        var next = _tailNext;

        if (slot >= next.Length)
        {
            var larger = new int[Math.Max(256, Math.Max(slot + 1, next.Length * 2))];
            Array.Copy(next, larger, next.Length);
            Volatile.Write(ref _tailNext, next = larger);
        }

        if (heads.TryGetValue(parent, out var head))
            next[slot] = head;
        else
        {
            next[slot] = -1;
            _tailHeadCount++;
        }

        heads[parent] = index;
    }

    private int TailHead(int parent)
        => _tailHeads is { } heads && heads.TryGetValue(parent, out var head) ? head : -1;

    private int TailNext(int child)
    {
        var next = Volatile.Read(ref _tailNext);
        var slot = child - _tailBase;
        return (uint)slot < (uint)next.Length ? next[slot] : -1;
    }

    // First position in [low, high) whose ParentIndex is at least `parent` (the range is breadth-first).
    private int LowerBound(int parent, int low, int high)
    {
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (At(middle).ParentIndex < parent) low = middle + 1;
            else high = middle;
        }

        return low;
    }

    /// <summary>NEW (round 65): the children of <paramref name="parent"/> below <paramref name="limit"/>,
    /// removed ones included. Safe alongside live appends (an entry added during the walk may be missed).</summary>
    internal ChildEnumerator Children(int parent, int limit = int.MaxValue) => new(this, parent, limit);

    internal struct ChildEnumerator
    {
        private readonly VolumeIndex _index;
        private readonly int _parent;
        private readonly int _limit;
        private readonly int _orderedEnd;
        private int _position;
        private int _tail;

        internal ChildEnumerator(VolumeIndex index, int parent, int limit)
        {
            _index = index;
            _parent = parent;
            var count = index.Count;
            _limit = Math.Min(limit, count);
            var ordered = Math.Min(index.OrderedCount, count);

            if (parent >= 0 && parent < ordered)
            {
                _orderedEnd = ordered;
                _position = index.LowerBound(parent, parent + 1, ordered);
            }
            else
            {
                _orderedEnd = 0;
                _position = 0;
            }

            _tail = parent >= 0 ? index.TailHead(parent) : -1;
            Current = -1;
        }

        public int Current { get; private set; }

        public readonly ChildEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_position < _orderedEnd && _position < _limit)
            {
                if (_index.At(_position).ParentIndex == _parent)
                {
                    Current = _position++;
                    return true;
                }

                _position = _orderedEnd;
            }

            while (_tail >= 0)
            {
                var child = _tail;
                _tail = _index.TailNext(child);

                if (child < _limit)
                {
                    Current = child;
                    return true;
                }
            }

            return false;
        }
    }

    // ---------------------------------------------------------------- names

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<byte> NameBytesOf(in IndexEntry entry)
        => _names[entry.NameOffset >> NameShift].AsSpan(entry.NameOffset & NameMask, entry.NameBytes);

    // NEW (round 65): whether an entry's name lies inside the pool (DiskUsageSnapshot checks every entry).
    internal bool HasValidName(int index)
    {
        ref readonly var entry = ref At(index);
        var chunk = entry.NameOffset >> NameShift;
        var names = _names;
        return entry.NameOffset >= 0 && chunk < names.Length && names[chunk] is { } bytes &&
               (entry.NameOffset & NameMask) + entry.NameBytes <= bytes.Length &&
               (!entry.IsWideName || (entry.NameBytes & 1) == 0);
    }

    // The name as characters. CHANGED (round 65): an ASCII name is widened into one of four per-thread
    // buffers, used in turn, so a span stays valid until the fourth NameSpan call after it on the same
    // thread (enough to compare two names). Copy it (new string(...)) to keep it longer.
    public ReadOnlySpan<char> NameSpan(int index)
    {
        ref readonly var entry = ref At(index);
        var bytes = NameBytesOf(entry);

        if (entry.IsWideName)
            return MemoryMarshal.Cast<byte, char>(bytes);

        var ring = _nameRing ??= new char[4][];
        var slot = _nameRingNext;
        _nameRingNext = (slot + 1) & 3;
        var buffer = ring[slot];

        if (buffer is null || buffer.Length < bytes.Length)
            ring[slot] = buffer = new char[Math.Max(260, bytes.Length)];

        Ascii.ToUtf16(bytes, buffer, out var written);
        return buffer.AsSpan(0, written);
    }

    [ThreadStatic]
    private static char[][]? _nameRing;

    [ThreadStatic]
    private static int _nameRingNext;

    public string GetName(int index)
    {
        ref readonly var entry = ref At(index);
        var bytes = NameBytesOf(entry);
        return entry.IsWideName ? new string(MemoryMarshal.Cast<byte, char>(bytes)) : Encoding.ASCII.GetString(bytes);
    }

    // NEW (round 65): the name compared with `other` ignoring case, as string.Equals(OrdinalIgnoreCase).
    internal bool NameEquals(int index, ReadOnlySpan<char> other) => NameEquals(At(index), other);

    internal bool NameEquals(in IndexEntry entry, ReadOnlySpan<char> other)
    {
        var bytes = NameBytesOf(entry);

        if (entry.IsWideName)
            return MemoryMarshal.Cast<byte, char>(bytes).Equals(other, StringComparison.OrdinalIgnoreCase);

        if (bytes.Length != other.Length)
            return false;

        if (Ascii.IsValid(other))
            return Ascii.EqualsIgnoreCase(bytes, other);

        Span<char> wide = bytes.Length <= 512 ? stackalloc char[bytes.Length] : new char[bytes.Length];
        Ascii.ToUtf16(bytes, wide, out _);
        return ((ReadOnlySpan<char>)wide).Equals(other, StringComparison.OrdinalIgnoreCase);
    }

    // Writes the name into `destination` (at least NameLength characters); returns the characters written.
    private int CopyName(in IndexEntry entry, Span<char> destination)
    {
        var bytes = NameBytesOf(entry);

        if (entry.IsWideName)
        {
            var chars = MemoryMarshal.Cast<byte, char>(bytes);
            chars.CopyTo(destination);
            return chars.Length;
        }

        Ascii.ToUtf16(bytes, destination, out var written);
        return written;
    }

    // NEW (round 65): case-insensitive "contains" of an ASCII name and a lower-case ASCII term, without
    // turning the name into characters. Exactly what IndexOf(OrdinalIgnoreCase) answers for ASCII text.
    internal static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> name, ReadOnlySpan<byte> lowerTerm)
    {
        var length = lowerTerm.Length;

        if (length == 0)
            return true;

        if (length > name.Length)
            return false;

        var first = lowerTerm[0];
        var upper = first is >= (byte)'a' and <= (byte)'z' ? (byte)(first - 32) : first;
        var rest = lowerTerm[1..];
        var last = name.Length - length;
        var at = 0;

        while (at <= last)
        {
            var window = name.Slice(at, last - at + 1);
            var found = first == upper ? window.IndexOf(first) : window.IndexOfAny(first, upper);

            if (found < 0)
                return false;

            at += found;

            if (rest.IsEmpty || Ascii.EqualsIgnoreCase(name.Slice(at + 1, length - 1), rest))
                return true;

            at++;
        }

        return false;
    }

    // ---------------------------------------------------------------- NEW: live updates

    // Resolves a full path to a live (not removed) entry, or -1.
    // CHANGED (round 65): needs no lock and no link arrays any more; FindLocked is kept for its callers.
    internal int FindLocked(string path) => FindConcurrent(path);

    // NEW (search fix): lookups without WriteGate, for searches. Safe alongside Add: the worst case is
    // missing an entry that was added during the lookup. CHANGED (round 65): never returns -2 any more
    // (there are no links left to build).
    internal int FindConcurrent(string path)
    {
        var count = Count;
        var root = Root.TrimEnd('\\', '/');
        var target = path.TrimEnd('\\', '/');

        if (target.Equals(root, StringComparison.OrdinalIgnoreCase)) return count > 0 ? 0 : -1;
        if (!target.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return -1;

        var current = 0;

        foreach (var part in target[(root.Length + 1)..].Split('\\'))
        {
            var match = -1;

            foreach (var child in Children(current, count))
            {
                ref readonly var entry = ref At(child);

                if ((entry.Attributes & RemovedFlag) == 0 && NameEquals(entry, part))
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
                ref var entry = ref At(existing);
                // CHANGED (round 65): compared as stored (sizes clamp, dates are kept to the second).
                var storedSize = Math.Clamp(size, 0, IndexEntry.MaxSize);
                var storedModified = IndexEntry.Round(modifiedTicks);
                if (entry.Size != storedSize || entry.ModifiedTicks != storedModified)
                {
                    entry.Size = size;
                    entry.ModifiedTicks = modifiedTicks;
                    MarkChanged();
                }
                return existing;
            }
            var parent = FindLocked(Path.GetDirectoryName(path) ?? string.Empty);
            if (parent < 0 || !At(parent).IsFolder) return -1;
            var name = Path.GetFileName(path.TrimEnd('\\', '/'));
            if (name.Length == 0 || name.Length > MaxNameChars) return -1;
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
                ref var entry = ref At(current);
                if ((entry.Attributes & RemovedFlag) != 0) continue;
                entry.Attributes |= RemovedFlag;
                _removedCount++;
                foreach (var child in Children(current))
                    pending.Push(child);
            }
            MarkChanged();
            return true;
        }
    }

    // ---------------------------------------------------------------- saving

    // CHANGED (round 65): writes the save file's section for this volume (FileIndexStore's format 4):
    //   root, serial, built ticks, entry count, pool length; the entries as stored; the name pool as stored.
    // An index with no removed entries and no live tail is written exactly as it is in memory. Otherwise the
    // live entries are written breadth-first with their names repacked, so the next load is fully ordered
    // again; that takes one small list of folders, not a second index. Caller holds WriteGate.
    internal void WriteLocked(BinaryWriter writer)
    {
        var stream = writer.BaseStream;

        if (_removedCount == 0 && _ordered == _count)
        {
            writer.Write(Root);
            writer.Write(SerialNumber);
            writer.Write(BuiltUtc.Ticks);
            writer.Write(_count);
            writer.Write(_poolLength);
            writer.Flush();

            for (var start = 0; start < _count; start += EntryChunk)
                stream.Write(MemoryMarshal.AsBytes(_entries[start >> EntryShift].AsSpan(0, Math.Min(EntryChunk, _count - start))));

            for (var start = 0; start < _poolLength; start += NameChunk)
            {
                var used = Math.Min(NameChunk, _poolLength - start);
                var chunk = _names[start >> NameShift];
                var have = Math.Min(used, chunk.Length);
                stream.Write(chunk.AsSpan(0, have));
                WriteZeros(stream, used - have);
            }

            return;
        }

        // Pass 1: how many entries survive, and how long their repacked names are.
        var kept = 0;
        var pool = 0;

        foreach (var (old, _) in LiveBreadthFirst())
        {
            var entry = At(old);
            pool = PlaceName(pool, entry.NameBytes, entry.IsWideName) + entry.NameBytes;
            kept++;
        }

        writer.Write(Root);
        writer.Write(SerialNumber);
        writer.Write(BuiltUtc.Ticks);
        writer.Write(kept);
        writer.Write(pool);
        writer.Flush();

        // Pass 2: the entries, with parents and name positions moved to their new places.
        var buffer = new IndexEntry[4096];
        var filled = 0;
        var position = 0;

        foreach (var (old, parent) in LiveBreadthFirst())
        {
            var entry = At(old);
            position = PlaceName(position, entry.NameBytes, entry.IsWideName);
            entry.ParentIndex = parent;
            entry.SetName(position, entry.NameBytes, entry.IsWideName);
            position += entry.NameBytes;
            buffer[filled++] = entry;
            if (filled == buffer.Length) { stream.Write(MemoryMarshal.AsBytes(buffer.AsSpan())); filled = 0; }
        }

        stream.Write(MemoryMarshal.AsBytes(buffer.AsSpan(0, filled)));

        // Pass 3: the names, in the same order and at the same positions.
        position = 0;

        foreach (var (old, _) in LiveBreadthFirst())
        {
            var entry = At(old);
            var placed = PlaceName(position, entry.NameBytes, entry.IsWideName);
            WriteZeros(stream, placed - position);
            stream.Write(NameBytesOf(entry));
            position = placed + entry.NameBytes;
        }
    }

    private static readonly byte[] Zeros = new byte[4096];

    private static void WriteZeros(Stream stream, int count)
    {
        while (count > 0)
        {
            var part = Math.Min(count, Zeros.Length);
            stream.Write(Zeros, 0, part);
            count -= part;
        }
    }

    // Live entries in breadth-first order: (index now, index of its parent in the new order).
    private IEnumerable<(int Old, int NewParent)> LiveBreadthFirst()
    {
        if (_count == 0)
            yield break;

        yield return (0, -1);

        var folders = new List<(int Old, int New)>();
        if (At(0).IsFolder) folders.Add((0, 0));
        var next = 1;

        for (var q = 0; q < folders.Count; q++)
        {
            var (folder, folderNew) = folders[q];

            foreach (var child in Children(folder))
            {
                var entry = At(child);

                if ((entry.Attributes & RemovedFlag) != 0)
                    continue;

                yield return (child, folderNew);

                if (entry.IsFolder)
                    folders.Add((child, next));

                next++;
            }
        }
    }

    // NEW (round 65): checks chunks read from a save file before they are trusted: every parent comes
    // before its child, the first entry is the root, and every name lies inside the pool.
    internal static bool Validate(IndexEntry[][] entries, int count, byte[][] names, int poolLength)
    {
        if (count < 1 || poolLength < 0)
            return false;

        for (var i = 0; i < count; i++)
        {
            ref readonly var entry = ref entries[i >> EntryShift][i & EntryMask];

            if (i == 0 ? entry.ParentIndex != -1 : entry.ParentIndex < 0 || entry.ParentIndex >= i)
                return false;

            var chunk = entry.NameOffset >> NameShift;

            if (entry.NameOffset < 0 || chunk >= names.Length ||
                (entry.NameOffset & NameMask) + entry.NameBytes > names[chunk].Length ||
                entry.NameOffset + entry.NameBytes > poolLength ||
                (entry.IsWideName && (entry.NameBytes & 1) != 0))
                return false;
        }

        return true;
    }


    // FIXED (review): folder chains of any depth. Up to 256 levels still use the stack; deeper ones used
    // to be cut off, producing a truncated path.
    private const int StackChain = 256;

    [ThreadStatic]
    private static char[]? _pathBuffer;

    public string GetPath(int index)
    {
        var count = Count;

        if (index < 0 || index >= count)
            return string.Empty;

        Span<int> chain = stackalloc int[StackChain];
        var depth = 0;
        var current = index;

        while (current >= 0 && depth <= count)
        {
            if (depth == chain.Length)
            {
                // FIXED (review): deeper than the stack buffer - continue on the heap.
                var larger = new int[chain.Length * 2];
                chain.CopyTo(larger);
                chain = larger;
            }

            chain[depth++] = current;
            current = At(current).ParentIndex;
        }

        var capacity = 0;
        for (var i = 0; i < depth; i++)
            capacity += At(chain[i]).NameLength + 1;

        var buffer = _pathBuffer;

        if (buffer is null || buffer.Length < capacity)
            _pathBuffer = buffer = new char[Math.Max(512, capacity)];

        var written = 0;

        for (var i = depth - 1; i >= 0; i--)
        {
            ref readonly var entry = ref At(chain[i]);

            if (written > 0 && buffer[written - 1] != Path.DirectorySeparatorChar)
                buffer[written++] = Path.DirectorySeparatorChar;

            written += CopyName(entry, buffer.AsSpan(written));   // CHANGED (round 65)
        }

        return new string(buffer, 0, written);
    }


    public static string Fold(string term) => term.ToLowerInvariant();

    // The plain name search (tests and simple callers; the app's own search is IndexSearch).
    // CHANGED (round 65): ASCII terms are matched against ASCII names without converting them.
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
        var terms = foldedTerms.ToArray();
        var asciiTerms = terms.Select(term => Ascii.IsValid(term) ? Encoding.ASCII.GetBytes(term.ToLowerInvariant()) : null).ToArray();

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
                    ref readonly var entry = ref At(i);

                    if ((entry.Attributes & RemovedFlag) != 0) // NEW: deleted since the last scan
                        continue;

                    if (!showHidden && entry.IsHiddenOrSystem)
                        continue;

                    var isFolder = entry.IsFolder;

                    if (foldersOnly && !isFolder)
                        continue;

                    if (filesOnly && isFolder)
                        continue;

                    var bytes = NameBytesOf(entry);
                    var matched = true;

                    for (var t = 0; t < terms.Length && matched; t++)
                    {
                        if (entry.IsWideName)
                            matched = MemoryMarshal.Cast<byte, char>(bytes).IndexOf(terms[t].AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0;
                        else if (asciiTerms[t] is { } ascii)
                            matched = ContainsAsciiIgnoreCase(bytes, ascii);
                        else
                            matched = NameSpan(i).IndexOf(terms[t].AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0;
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

        // NEW (round 65): chunks finish in any order; keep index order as before.
        results.Sort();

        if (results.Count > limit)
            results.RemoveRange(limit, results.Count - limit);

        return results;
    }
}
