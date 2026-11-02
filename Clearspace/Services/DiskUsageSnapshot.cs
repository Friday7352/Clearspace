using System.IO;
using System.Numerics;

namespace Clearspace.Services;

internal sealed record DiskUsageItem(int Id, string Name, long Bytes, long FileCount, bool IsFolder)
{
    public string SizeText => DiskUsageSnapshot.FormatBytes(Bytes);
    public string Kind => Id < 0 ? "Group" : IsFolder ? "Folder" : "File";
    public double Share { get; init; }
    public string ShareText => Share > 0 && Share < 0.1 ? "<0.1%" : $"{Share:0.#}%";
    public string ColorHex { get; init; } = "#615C54";
    public string Glyph => Id < 0 ? "" : IsFolder ? "" : "";
}

// Published indexes are immutable. Keep one version alive while the user explores it.
// Parallel arrays avoid allocating an object and a child list for every indexed file.
// CHANGED (round 39): also an IFlatSource, so the whole-drive flat layout can read it directly.
// CHANGED (round 65, memory): totals are kept for folders only, and nothing else per entry but two bits.
// It used to keep five arrays over every entry (bytes, files, first child, next sibling, excluded: 25 bytes
// each, 280 MB for a drive of 11 million files, two drives kept warm). Now:
//   * a file's size is read from the index itself;
//   * children come from the index (VolumeIndex.Children), which needs no link arrays;
//   * "is a folder" and "excluded" are one bit each, and a folder's place in the totals arrays is its rank
//     among folders (a running count every 64 entries plus a bit count).
// A file whose size changes after the snapshot shows its new size; folder totals stay as built until the
// snapshot is refreshed, as before.
internal sealed class DiskUsageSnapshot : IFlatSource
{
    private readonly VolumeIndex _index;
    private readonly int _count; // NEW: the live index keeps growing; a snapshot reads a fixed prefix
    private readonly ulong[] _folderBits;    // NEW (round 65): 1 bit per entry - a folder
    private readonly int[] _folderRank;      // NEW (round 65): folders before each 64-entry word
    private readonly ulong[] _excludedBits;  // NEW (round 65): 1 bit per entry - left out of the totals
    private long[] _folderBytes = [];        // NEW (round 65): by folder rank
    private long[] _folderFiles = [];

    private DiskUsageSnapshot(VolumeIndex index)
    {
        _index = index;
        _count = index.Count;
        var words = (_count + 63) >> 6;
        _folderBits = new ulong[words];
        _folderRank = new int[words];
        _excludedBits = new ulong[words];
    }

    public string Root => _index.Root;
    public DateTime BuiltUtc => _index.BuiltUtc;
    public int Count => _count;

    // NEW (round 23): reported in the F3 readout so the map's own memory can be told apart from the index
    // it is built on. CHANGED (round 65): two bit arrays and a rank word per 64 entries, plus 16 bytes a folder.
    public long ApproximateBytes => (long)_folderBits.Length * 20 + (long)_folderBytes.Length * 16;
    public long SourceVersion { get; private init; } // NEW: index version this snapshot was built from
    internal VolumeIndex Source => _index;
    public bool IsAvailable(int id) => id >= 0 && id < Count && !IsExcluded(id);

    private bool IsFolderId(int id) => (_folderBits[id >> 6] & (1UL << (id & 63))) != 0;
    private bool IsExcluded(int id) => (_excludedBits[id >> 6] & (1UL << (id & 63))) != 0;
    private void Exclude(int id) => _excludedBits[id >> 6] |= 1UL << (id & 63);
    private int Rank(int id) => _folderRank[id >> 6] + BitOperations.PopCount(_folderBits[id >> 6] & ((1UL << (id & 63)) - 1));

    public static DiskUsageSnapshot Build(VolumeIndex index, CancellationToken token, IEnumerable<string>? excludedPaths = null)
    {
        token.ThrowIfCancellationRequested();
        var version = index.Version;
        var result = new DiskUsageSnapshot(index) { SourceVersion = version };
        if (result._count == 0)
            throw new InvalidDataException("The index contains no root folder.");
        for (var i = 0; i < result._count; i++)
        {
            if ((i & 0xFFF) == 0) token.ThrowIfCancellationRequested();
            ref readonly var entry = ref index.Entry(i);
            if (i == 0 ? entry.ParentIndex != -1 || !entry.IsFolder
                       : entry.ParentIndex < 0 || entry.ParentIndex >= i || !index.Entry(entry.ParentIndex).IsFolder)
                throw new InvalidDataException("The index has an invalid folder hierarchy.");
            if (!index.HasValidName(i))
                throw new InvalidDataException("The index contains an invalid file name.");
            if (entry.IsFolder) result._folderBits[i >> 6] |= 1UL << (i & 63);
            // NEW: entries deleted since the last full scan are excluded like confirmed deletions.
            if ((entry.Attributes & VolumeIndex.RemovedFlag) != 0) result.Exclude(i);
        }

        var folders = 0;
        for (var word = 0; word < result._folderBits.Length; word++)
        {
            result._folderRank[word] = folders;
            folders += BitOperations.PopCount(result._folderBits[word]);
        }
        result._folderBytes = new long[folders];
        result._folderFiles = new long[folders];

        if (excludedPaths is not null)
            foreach (var path in excludedPaths)
            {
                token.ThrowIfCancellationRequested();
                var id = result.FindEntry(path, token);
                if (id > 0) result.Exclude(id);
            }
        for (var i = 1; i < result._count; i++)
        {
            if ((i & 0xFFF) == 0) token.ThrowIfCancellationRequested();
            if (result.IsExcluded(index.Entry(i).ParentIndex)) result.Exclude(i);
        }

        // Parents precede children in the index. Reverse order is a bottom-up traversal:
        // each subtree contributes exactly once, without recursion or repeated disk reads.
        for (var i = result._count - 1; i > 0; i--)
        {
            if ((i & 0xFFF) == 0) token.ThrowIfCancellationRequested();
            if (result.IsExcluded(i)) continue;
            ref readonly var entry = ref index.Entry(i);
            var parent = result.Rank(entry.ParentIndex);
            if (result.IsFolderId(i))
            {
                var own = result.Rank(i);
                result._folderBytes[parent] = checked(result._folderBytes[parent] + result._folderBytes[own]);
                result._folderFiles[parent] += result._folderFiles[own];
            }
            else
            {
                result._folderBytes[parent] = checked(result._folderBytes[parent] + Math.Max(0, entry.Size));
                result._folderFiles[parent]++;
            }
        }
        return result;
    }

    // NEW (round 39): direct reads for FlatTreemapLayout. Children() allocates a record and a name
    // string per child, which for a whole drive is the same million small objects the flat layout
    // exists to avoid.
    public long BytesOf(int id)
        => IsExcluded(id) ? 0 : IsFolderId(id) ? _folderBytes[Rank(id)] : Math.Max(0, _index.Entry(id).Size);

    public long FilesOf(int id)
        => IsExcluded(id) ? 0 : IsFolderId(id) ? _folderFiles[Rank(id)] : 1;

    public bool IsFolderEntry(int id) => IsFolderId(id);

    // CHANGED (round 65): valid until the fourth NameOf/NameSpan call after it on the same thread
    // (VolumeIndex.NameSpan), which covers comparing two names.
    public ReadOnlySpan<char> NameOf(int id) => _index.NameSpan(id);

    public void ChildIds(int folder, List<int> into)
    {
        foreach (var child in _index.Children(folder, _count))
            if (!IsExcluded(child)) into.Add(child);
    }

    public DiskUsageItem Item(int id) => new(id, _index.GetName(id), BytesOf(id), FilesOf(id), IsFolderId(id));
    public int Parent(int id) => _index.Entry(id).ParentIndex;

    public IReadOnlyList<DiskUsageItem> Children(int id, CancellationToken token = default)
    {
        var children = new List<DiskUsageItem>();
        foreach (var child in _index.Children(id, _count))
        {
            token.ThrowIfCancellationRequested();
            if (IsExcluded(child)) continue;
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
        return id >= 0 && IsFolderId(id) ? id : -1;
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
            foreach (var child in _index.Children(current, _count))
            {
                token.ThrowIfCancellationRequested();
                if (!IsExcluded(child) && _index.NameEquals(child, part))
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
