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
internal sealed class DiskUsageSnapshot
{
    private readonly VolumeIndex _index;
    private readonly long[] _bytes;
    private readonly long[] _files;
    private readonly int[] _firstChild;
    private readonly int[] _nextSibling;
    private readonly bool[] _excluded;

    private DiskUsageSnapshot(VolumeIndex index)
    {
        _index = index;
        _bytes = new long[index.Count];
        _files = new long[index.Count];
        _firstChild = new int[index.Count];
        _nextSibling = new int[index.Count];
        _excluded = new bool[index.Count];
        Array.Fill(_firstChild, -1);
        Array.Fill(_nextSibling, -1);
    }

    public string Root => _index.Root;
    public DateTime BuiltUtc => _index.BuiltUtc;
    public int Count => _index.Count;
    internal VolumeIndex Source => _index;
    public bool IsAvailable(int id) => id >= 0 && id < Count && !_excluded[id];

    public static DiskUsageSnapshot Build(VolumeIndex index, CancellationToken token, IEnumerable<string>? excludedPaths = null)
    {
        token.ThrowIfCancellationRequested();
        if (index.Count == 0)
            throw new InvalidDataException("The index contains no root folder.");
        var result = new DiskUsageSnapshot(index);
        for (var i = 0; i < index.Count; i++)
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
        for (var i = 1; i < index.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            result._excluded[i] |= result._excluded[index.Entry(i).ParentIndex];
            if (result._excluded[i]) { result._bytes[i] = 0; result._files[i] = 0; }
        }

        // Parents precede children in the index. Reverse order is a bottom-up traversal:
        // each subtree contributes exactly once, without recursion or repeated disk reads.
        for (var i = index.Count - 1; i > 0; i--)
        {
            token.ThrowIfCancellationRequested();
            var parent = index.Entry(i).ParentIndex;
            result._bytes[parent] = checked(result._bytes[parent] + result._bytes[i]);
            result._files[parent] += result._files[i];
        }
        return result;
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
