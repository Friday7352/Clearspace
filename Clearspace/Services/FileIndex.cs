// Clearspace | In-memory file index and search.

using System.IO;
using System.Runtime.InteropServices;

namespace Clearspace.Services;


[StructLayout(LayoutKind.Sequential)]
// CS499: Folder-size aggregation remains a planned algorithms enhancement.
internal struct IndexEntry
{
    // CS499: Enhancement 2 can derive folder sizes from Size and ParentIndex.
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


internal sealed class VolumeIndex
{
    private const int InitialEntries = 4096;
    private const int InitialPool = 65536;

    private IndexEntry[] _entries;
    private char[] _names;

    private char[] _folded;

    private int _count;
    private int _poolLength;

    public VolumeIndex(string root, uint serialNumber)
    {
        Root = root;
        SerialNumber = serialNumber;
        BuiltUtc = DateTime.UtcNow;
        _entries = new IndexEntry[InitialEntries];
        _names = new char[InitialPool];
        _folded = new char[InitialPool];
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

        _folded = new char[names.Length];

        const int FoldChunk = 1 << 16;
        var source = names;
        var target = _folded;
        var chunks = (poolLength + FoldChunk - 1) / FoldChunk;

        if (chunks > 0)
        {
            Parallel.For(0, chunks, chunk =>
            {
                var start = chunk * FoldChunk;
                var end = Math.Min(poolLength, start + FoldChunk);

                for (var i = start; i < end; i++)
                    target[i] = char.ToLowerInvariant(source[i]);
            });
        }
    }

    public string Root { get; }

    public uint SerialNumber { get; }

    public DateTime BuiltUtc { get; }

    public int Count => _count;

    internal IndexEntry[] Entries => _entries;

    internal char[] Names => _names;

    internal int PoolLength => _poolLength;

    public long EstimatedBytes =>
        ((long)_count * Marshal.SizeOf<IndexEntry>()) + ((long)_poolLength * sizeof(char) * 2);


    public int Add(
        int parentIndex,
        ReadOnlySpan<char> name,
        long size,
        long modifiedTicks,
        long createdTicks,
        FileAttributes attributes)
    {
        if (_count == _entries.Length)
            Array.Resize(ref _entries, Math.Max(InitialEntries, _entries.Length * 2));

        while (_poolLength + name.Length > _names.Length)
        {
            var grown = Math.Max(InitialPool, _names.Length * 2);
            Array.Resize(ref _names, grown);
            Array.Resize(ref _folded, grown);
        }

        name.CopyTo(_names.AsSpan(_poolLength));

        for (var i = 0; i < name.Length; i++)
            _folded[_poolLength + i] = char.ToLowerInvariant(name[i]);

        _entries[_count] = new IndexEntry
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
        return _count++;
    }

    public void Compact()
    {
        if (_entries.Length != _count)
            Array.Resize(ref _entries, _count);

        if (_names.Length != _poolLength)
        {
            Array.Resize(ref _names, _poolLength);
            Array.Resize(ref _folded, _poolLength);
        }
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
        var chunks = (_count + ChunkSize - 1) / ChunkSize;
        var gate = new object();
        var total = 0;

        try
        {
            Parallel.For(0, chunks, new ParallelOptions { CancellationToken = token }, chunk =>
            {
                if (Volatile.Read(ref total) >= limit)
                    return;

                var start = chunk * ChunkSize;
                var end = Math.Min(_count, start + ChunkSize);
                List<int>? local = null;

                for (var i = start; i < end; i++)
                {
                    if (!showHidden && _entries[i].IsHiddenOrSystem)
                        continue;

                    var isFolder = _entries[i].IsFolder;

                    if (foldersOnly && !isFolder)
                        continue;

                    if (filesOnly && isFolder)
                        continue;

                    var name = _folded.AsSpan(_entries[i].NameOffset, _entries[i].NameLength);
                    var matched = true;

                    for (var t = 0; t < foldedTerms.Count; t++)
                    {
                        if (name.IndexOf(foldedTerms[t].AsSpan()) < 0)
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
