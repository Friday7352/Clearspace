using System.IO;

namespace Clearspace.Services;

internal sealed record DiskUsageItem(int Id, string Name, long Bytes, long FileCount, bool IsFolder)
{
    public string SizeText => DiskUsageSnapshot.FormatBytes(Bytes);
    public string Kind => Id < 0 ? "Group" : IsFolder ? "Folder" : "File";
    public double Share { get; init; }
    public string ShareText => Share > 0 && Share < 0.1 ? "<0.1%" : $"{Share:0.#}%";
    public string ColorHex { get; init; } = "#615C54";
    public string Glyph => Id < 0 ? "\uE8A9" : IsFolder ? "\uE8B7" : "\uE8A5";
}

// Published indexes are immutable. Keep one version alive while the user explores it.
// Parallel arrays avoid allocating an object and a child list for every indexed file.
// CHANGED (round 39): also an IFlatSource, so the whole-drive flat layout can read it directly.
internal sealed class DiskUsageSnapshot : IFlatSource
{
    private readonly VolumeIndex _index;
    private readonly long[] _bytes;
    private readonly long[] _files;
    private readonly int[] _firstChild;
    private readonly int[] _nextSibling;
    private readonly bool[] _excluded;
    private readonly int _count; // NEW: the live index keeps growing; a snapshot reads a fixed prefix

    private DiskUsageSnapshot(VolumeIndex index)
    {
        _index = index;
        _count = index.Count;
        _bytes = new long[_count];
        _files = new long[_count];
        _firstChild = new int[_count];
        _nextSibling = new int[_count];
        _excluded = new bool[_count];
        Array.Fill(_firstChild, -1);
        Array.Fill(_nextSibling, -1);
    }

    public string Root => _index.Root;
    public DateTime BuiltUtc => _index.BuiltUtc;
    public int Count => _count;

    // NEW (round 23): five parallel arrays of 8+8+4+4+1 bytes per indexed entry. Reported in the
    // F3 readout so the map's own memory can be told apart from the index it is built on.
    public long ApproximateBytes => (long)_count * 25;
    public long SourceVersion { get; private init; } // NEW: index version this snapshot was built from
    internal VolumeIndex Source => _index;
    public bool IsAvailable(int id) => id >= 0 && id < Count && !_excluded[id];

    public static DiskUsageSnapshot Build(VolumeIndex index, CancellationToken token, IEnumerable<string>? excludedPaths = null)
    {
        token.ThrowIfCancellationRequested();
        var version = index.Version;
        var result = new DiskUsageSnapshot(index) { SourceVersion = version };
        if (result._count == 0)
            throw new InvalidDataException("The index contains no root folder.");
        for (var i = 0; i < result._count; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = index.Entry(i);
            if (i == 0 ? entry.ParentIndex != -1 || !entry.IsFolder
                       : entry.ParentIndex < 0 || entry.ParentIndex >= i || !index.Entry(entry.ParentIndex).IsFolder)
                throw new InvalidDataException("The index has an invalid folder hierarchy.");
            if (entry.NameOffset < 0 || entry.NameOffset > index.PoolLength - entry.NameLength)
                throw new InvalidDataException("The index contains an invalid file name.");
            result._bytes[i] = entry.IsFolder ? 0 : Math.Max(0, entry.Size);
            result._files[i] = entry.IsFolder ? 0 : 1;
            // NEW: entries deleted since the last full scan are excluded like confirmed deletions.
            if ((entry.Attributes & VolumeIndex.RemovedFlag) != 0) result._excluded[i] = true;
            if (i == 0) continue;
            result._nextSibling[i] = result._firstChild[entry.ParentIndex];
            result._firstChild[entry.ParentIndex] = i;
        }

        if (excludedPaths is not null)
            foreach (var path in excludedPaths)
            {
                token.ThrowIfCancellationRequested();
                var id = result.FindEntry(path, token);
                if (id > 0) result._excluded[id] = true;
            }
        for (var i = 1; i < result._count; i++)
        {
            token.ThrowIfCancellationRequested();
            result._excluded[i] |= result._excluded[index.Entry(i).ParentIndex];
            if (result._excluded[i]) { result._bytes[i] = 0; result._files[i] = 0; }
        }

        // Parents precede children in the index. Reverse order is a bottom-up traversal:
        // each subtree contributes exactly once, without recursion or repeated disk reads.
        for (var i = result._count - 1; i > 0; i--)
        {
            token.ThrowIfCancellationRequested();
            var parent = index.Entry(i).ParentIndex;
            result._bytes[parent] = checked(result._bytes[parent] + result._bytes[i]);
            result._files[parent] += result._files[i];
        }
        return result;
    }

    // NEW (round 39): direct reads for FlatTreemapLayout. Children() allocates a record and a name
    // string per child, which for a whole drive is the same million small objects the flat layout
    // exists to avoid.
    public long BytesOf(int id) => _bytes[id];
    public long FilesOf(int id) => _files[id];
    public bool IsFolderEntry(int id) => _index.Entry(id).IsFolder;
    public ReadOnlySpan<char> NameOf(int id) => _index.NameSpan(id);
    public void ChildIds(int folder, List<int> into)
    {
        for (var child = _firstChild[folder]; child >= 0; child = _nextSibling[child])
            if (!_excluded[child]) into.Add(child);
    }

    public DiskUsageItem Item(int id) => new(id, _index.GetName(id), _bytes[id], _files[id], _index.Entry(id).IsFolder);
    public int Parent(int id) => _index.Entry(id).ParentIndex;

    public IReadOnlyList<DiskUsageItem> Children(int id, CancellationToken token = default)
    {
        var children = new List<DiskUsageItem>();
        for (var child = _firstChild[id]; child >= 0; child = _nextSibling[child])
        {
            token.ThrowIfCancellationRequested();
            if (_excluded[child]) continue;
            children.Add(Item(child));
        }
        children.Sort((a, b) =>
        {
            var size = b.Bytes.CompareTo(a.Bytes);
            if (size != 0) return size;
            var name = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return name != 0 ? name : a.Id.CompareTo(b.Id);
        });
        token.ThrowIfCancellationRequested();
        return children;
    }

    public string PathFor(int id)
    {
        var parts = new Stack<string>();
        for (var current = id; current > 0; current = Parent(current))
            parts.Push(_index.GetName(current));
        return parts.Count == 0 ? Root : Path.Combine(Root, Path.Combine(parts.ToArray()));
    }

    public int FindFolder(string path, CancellationToken token = default)
    {
        var id = FindEntry(path, token);
        return id >= 0 && _index.Entry(id).IsFolder ? id : -1;
    }

    private int FindEntry(string path, CancellationToken token)
    {
        var root = Root.TrimEnd('\\', '/');
        var target = path.TrimEnd('\\', '/');
        if (target.Equals(root, StringComparison.OrdinalIgnoreCase)) return 0;
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return -1;
        var current = 0;
        foreach (var part in target[(root.Length + 1)..].Split(Path.DirectorySeparatorChar))
        {
            var match = -1;
            for (var child = _firstChild[current]; child >= 0; child = _nextSibling[child])
            {
                token.ThrowIfCancellationRequested();
                if (!_excluded[child] && _index.NameSpan(child).Equals(part, StringComparison.OrdinalIgnoreCase))
                { match = child; break; }
            }
            if (match < 0) return -1;
            current = match;
        }
        return current;
    }

    // Bound visual complexity without dropping bytes. Every remaining item is in the list.
    public static IReadOnlyList<DiskUsageItem> MapItems(IReadOnlyList<DiskUsageItem> sorted, int limit = 200)
    {
        if (limit < 2) throw new ArgumentOutOfRangeException(nameof(limit));
        var positive = sorted.Where(item => item.Bytes > 0).ToArray();
        if (positive.Length <= limit) return positive;
        var visible = positive.Take(limit - 1).ToList();
        var rest = positive.Skip(limit - 1).ToArray();
        visible.Add(new DiskUsageItem(-1, $"Other {rest.Length:N0} items", rest.Sum(item => item.Bytes),
            rest.Sum(item => item.FileCount), false));
        return visible;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
