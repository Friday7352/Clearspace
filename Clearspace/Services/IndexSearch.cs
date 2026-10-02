// Clearspace | Ranked search over one volume's in-memory file index.
//
// NEW (search relevance): replaces "first N filenames containing every word, then rank those" with
// "score every candidate while scanning and keep the best N". The whole query is evaluated in the
// scan: search roots, explicit filters, tags, type words, folder types and the folders above each
// entry. Nothing allocates per entry: names and extensions are spans, tagged paths and typed folders
// are resolved to entry numbers once per search, and what the folders above an entry contribute is
// computed once per folder and cached (FolderInfo). Scores come from the same SearchRanker pieces
// SearchQuery.Relevance uses, so index hits and crawled/Windows hits rank consistently.

using System.IO;
using Clearspace.Models;

namespace Clearspace.Services;

internal static class IndexSearch
{
    private const int ChunkSize = 4096;
    private const int MaxTagTerms = 32;       // 2 bits per word in a ulong
    private const int MaxExplicitTags = 32;   // 1 bit per tag: filter in a uint
    private const int MaxCachedFolders = 65_536;

    // What the folders above an entry contribute. Built top-down, one folder at a time.
    private struct FolderInfo
    {
        public ulong FolderCategories;  // 4 bits per word: best SearchRanker.Category of a folder name above
        public ulong InheritedTags;     // 2 bits per word: best TagStrength on a folder above
        public bool InScope;            // this folder is a search root or inside one
        public bool UnderCurrent;       // this folder is the current folder or inside it
        public byte Noise;              // SearchRanker noise level of this folder or any above it
    }

    // Per-volume resolution of everything that is known by path.
    internal sealed class Plan
    {
        public required HashSet<int> ScopeRoots { get; init; }
        public int Current { get; init; } = -1;
        public Dictionary<int, ulong> TermTags { get; } = new();
        public Dictionary<int, uint> ExplicitTags { get; } = new();
        public uint ExplicitTagMask { get; set; }
        public Dictionary<int, string> Profiles { get; } = new(); // CHANGED (folder types): type IDs
        public List<int>? Candidates { get; set; }
        public HashSet<int> LowRank { get; } = new(); // NEW (folder types, step 2): Archives & Backups folders
    }

    // Returns null when none of the search roots is on this volume.
    internal static Plan? Prepare(VolumeIndex volume, SearchQuery query, IReadOnlyList<string> roots)
    {
        // Settings are read outside the volume lock; they can be large and change on the UI thread.
        KeyValuePair<string, string>[] typedFolders = [];

        if (query.UsesProfiles)
        {
            try { typedFolders = [.. SettingsService.GetAllFolderViewProfiles()]; }
            catch (InvalidOperationException) { } // changed while copying; folder types are skipped this search
        }

        // CHANGED (search fix): resolve paths without holding WriteGate. Saves and subtree scans hold it,
        // sometimes for seconds, and searches used to sit on "0 found" behind them. The lock is only
        // taken the first time a volume needs its child links built.
        int Resolve(string path)
        {
            var found = volume.FindConcurrent(path);

            if (found != -2)
                return found;

            lock (volume.WriteGate)
                return volume.FindLocked(path);
        }

        {
            var scope = new HashSet<int>();

            foreach (var root in roots)
            {
                var index = Resolve(root);

                if (index >= 0)
                    scope.Add(index);
            }

            if (scope.Count == 0)
                return null;

            var plan = new Plan
            {
                ScopeRoots = scope,
                Current = string.IsNullOrEmpty(query.CurrentFolder) ? -1 : Resolve(query.CurrentFolder)
            };

            var terms = query.TermList;

            for (var t = 0; t < Math.Min(terms.Count, MaxTagTerms); t++)
            {
                var shift = 2 * t;

                foreach (var (tagId, strength) in terms[t].Tags)
                {
                    foreach (var path in query.PathsTagged(tagId))
                    {
                        var index = Resolve(path);

                        if (index < 0)
                            continue;

                        plan.TermTags.TryGetValue(index, out var bits);

                        if ((ulong)strength > ((bits >> shift) & 3))
                            plan.TermTags[index] = (bits & ~(3UL << shift)) | ((ulong)strength << shift);
                    }
                }
            }

            var explicitCount = Math.Min(query.TagIds.Count, MaxExplicitTags);

            for (var i = 0; i < explicitCount; i++)
            {
                plan.ExplicitTagMask |= 1u << i;

                foreach (var path in query.PathsTagged(query.TagIds[i]))
                {
                    var index = Resolve(path);

                    if (index < 0)
                        continue;

                    plan.ExplicitTags.TryGetValue(index, out var mask);
                    plan.ExplicitTags[index] = mask | (1u << i);
                }
            }

            foreach (var (path, saved) in typedFolders)
            {
                if (string.IsNullOrWhiteSpace(saved))
                    continue;

                var index = Resolve(path);

                if (index >= 0)
                    plan.Profiles[index] = saved;
            }

            // NEW (folder types, step 2): contents of these folders rank lower (see SearchRanker.Score).
            foreach (var folder in query.LowRankFolders)
            {
                var index = Resolve(folder);

                if (index >= 0)
                    plan.LowRank.Add(index);
            }

            // Explicit tag:/type: filters name their candidates; there is no need to scan the volume.
            if (plan.ExplicitTagMask != 0)
            {
                plan.Candidates = plan.ExplicitTags
                    .Where(pair => (pair.Value & plan.ExplicitTagMask) == plan.ExplicitTagMask)
                    .Select(pair => pair.Key).ToList();
            }
            else if (query.Profiles.Count > 0)
            {
                plan.Candidates = plan.Profiles
                    .Where(pair => query.Profiles.Contains(pair.Value, StringComparer.OrdinalIgnoreCase))
                    .Select(pair => pair.Key).ToList();
            }

            return plan;
        }
    }

    // The best `limit` matches on this volume, highest score first.
    internal static List<(int Score, int Index)> Search(
        VolumeIndex volume,
        SearchQuery query,
        Plan plan,
        bool showHidden,
        int limit,
        CancellationToken token)
    {
        // Count first, then the arrays: entries below a published count are complete in any
        // array read afterwards (VolumeIndex.Add writes the entry before publishing the count).
        var count = volume.Count;
        var scan = new Scanner(query, plan, volume.Entries, volume.Names, count, showHidden);
        var best = new PriorityQueue<int, int>();
        var gate = new object();
        var threshold = int.MinValue;

        if (count == 0 || limit <= 0)
            return [];

        void Keep(List<(int Score, int Index)> local)
        {
            lock (gate)
            {
                foreach (var (score, index) in local)
                {
                    if (best.Count < limit)
                        best.Enqueue(index, score);
                    else if (best.TryPeek(out _, out var lowest) && score > lowest)
                        best.EnqueueDequeue(index, score);
                }

                if (best.Count >= limit && best.TryPeek(out _, out var floor))
                    Volatile.Write(ref threshold, floor);
            }
        }

        try
        {
            if (plan.Candidates is { } candidates)
            {
                var cache = new Dictionary<int, FolderInfo>();
                var local = new List<(int Score, int Index)>();

                foreach (var index in candidates)
                {
                    token.ThrowIfCancellationRequested();

                    if (index < count && scan.TryScore(index, cache, out var score))
                        local.Add((score, index));
                }

                Keep(local);
            }
            else
            {
                var chunks = (count + ChunkSize - 1) / ChunkSize;

                Parallel.For(0, chunks, new ParallelOptions { CancellationToken = token },
                    () => new Dictionary<int, FolderInfo>(),
                    (chunk, _, cache) =>
                    {
                        if (cache.Count > MaxCachedFolders)
                            cache.Clear();

                        var floor = Volatile.Read(ref threshold);
                        var start = chunk * ChunkSize;
                        var end = Math.Min(count, start + ChunkSize);
                        List<(int Score, int Index)>? local = null;

                        for (var i = start; i < end; i++)
                        {
                            if (scan.TryScore(i, cache, out var score) && score > floor)
                                (local ??= []).Add((score, i));
                        }

                        if (local is not null)
                            Keep(local);

                        return cache;
                    },
                    _ => { });
            }
        }
        catch (OperationCanceledException)
        {
            return [];
        }

        var results = new List<(int Score, int Index)>(best.Count);

        while (best.TryDequeue(out var index, out var score))
            results.Add((score, index));

        results.Reverse();
        return results;
    }

    private sealed class Scanner(
        SearchQuery query,
        Plan plan,
        IndexEntry[] entries,
        char[] names,
        int count,
        bool showHidden)
    {
        private readonly IReadOnlyList<SearchTerm> _terms = query.TermList;
        private readonly int _contextTerms = Math.Min(query.TermList.Count, SearchQuery.MaxContextTerms);
        private readonly SearchKind _kind = query.Kind;
        private readonly HashSet<string>? _extensions = query.ExtensionSet;
        private readonly IReadOnlyList<string> _profiles = query.Profiles;
        private readonly Plan _plan = plan;
        private readonly bool _hasTermTags = plan.TermTags.Count > 0;
        private readonly long _now = DateTime.UtcNow.ToFileTimeUtc();

        public bool TryScore(int i, Dictionary<int, FolderInfo> cache, out int score)
        {
            score = 0;
            ref readonly var entry = ref entries[i];

            if ((entry.Attributes & VolumeIndex.RemovedFlag) != 0)
                return false;

            if (!showHidden && entry.IsHiddenOrSystem)
                return false;

            var isFolder = entry.IsFolder;
            var name = names.AsSpan(entry.NameOffset, entry.NameLength);
            var extension = isFolder ? ReadOnlySpan<char>.Empty : Path.GetExtension(name);

            // Strict filters first: they are the cheapest way to reject.
            switch (_kind)
            {
                case SearchKind.Folder when !isFolder:
                case SearchKind.File when isFolder:
                    return false;
                case SearchKind.Image when isFolder || !Contains(MediaTypes.ImageExtensions, extension):
                case SearchKind.Audio when isFolder || !Contains(MediaTypes.AudioExtensions, extension):
                case SearchKind.Video when isFolder || !Contains(MediaTypes.VideoExtensions, extension):
                    return false;
            }

            if (_extensions is not null && (isFolder || !Contains(_extensions, extension)))
                return false;

            if (_plan.ExplicitTagMask != 0 &&
                (!_plan.ExplicitTags.TryGetValue(i, out var have) || (have & _plan.ExplicitTagMask) != _plan.ExplicitTagMask))
                return false;

            string? profile = null;

            if (isFolder && _plan.Profiles.TryGetValue(i, out var typed))
                profile = typed;

            if (_profiles.Count > 0 && !(profile is { } wanted && _profiles.Contains(wanted, StringComparer.OrdinalIgnoreCase)))
                return false;

            // Words: evidence on the entry itself.
            var termCount = _terms.Count;
            ulong tagBits = 0;

            if (_hasTermTags)
                _plan.TermTags.TryGetValue(i, out tagBits);

            var own = 0;
            var sum = 0;
            var matched = 0;
            ulong pending = 0;

            for (var t = 0; t < termCount; t++)
            {
                var term = _terms[t];
                var category = SearchRanker.Category(name, term.Text);
                var strength = SearchRanker.NamePoints(category);

                if (category > 0)
                    matched += term.Text.Length;

                if (t < MaxTagTerms)
                    strength = Math.Max(strength, SearchRanker.TagPoints((TagStrength)((tagBits >> (2 * t)) & 3)));

                if (!isFolder && term.TypeMatches(extension))
                    strength = Math.Max(strength, SearchRanker.TypeWord);

                if (profile is { } value && term.Profiles.Contains(value, StringComparer.OrdinalIgnoreCase))
                    strength = Math.Max(strength, SearchRanker.FolderType);

                if (strength > 0)
                {
                    own++;
                    sum += strength;
                }
                else if (t < _contextTerms)
                    pending |= 1UL << t;
                else
                    return false;
            }

            if (termCount > 0 && own == 0)
                return false;

            // Scope and the remaining words: evidence from the folders above.
            var parent = Info(entry.ParentIndex, cache);

            if (!parent.InScope && !_plan.ScopeRoots.Contains(i))
                return false;

            for (var t = 0; pending != 0; t++, pending >>= 1)
            {
                if ((pending & 1) == 0)
                    continue;

                var folder = SearchRanker.FolderPoints((int)((parent.FolderCategories >> (4 * t)) & 0xF));
                var inherited = SearchRanker.InheritedTagPoints((TagStrength)((parent.InheritedTags >> (2 * t)) & 3));
                var strength = Math.Max(folder, inherited);

                if (strength == 0)
                    return false;

                sum += strength;
            }

            var ageDays = entry.ModifiedTicks > 0
                ? (_now - entry.ModifiedTicks) / (double)TimeSpan.TicksPerDay
                : double.NaN;

            score = SearchRanker.Combine(sum, termCount, matched, name.Length) +
                    SearchRanker.Bonus(isFolder, extension, ageDays, parent.Noise, parent.UnderCurrent);
            return true;
        }

        // One reusable chain per scanning thread (the scanner is shared by Parallel.For workers).
        [ThreadStatic]
        private static int[]? _chain;

        private static bool Contains(HashSet<string> set, ReadOnlySpan<char> extension)
            => !extension.IsEmpty && set.GetAlternateLookup<ReadOnlySpan<char>>().Contains(extension);

        private FolderInfo Info(int folder, Dictionary<int, FolderInfo> cache)
        {
            if (folder < 0 || folder >= count)
                return default;

            if (cache.TryGetValue(folder, out var known))
                return known;

            // FIXED (review): no depth limit. The old 256-level stack buffer treated anything deeper as outside
            // the search roots. The chain grows as needed; `count` bounds it against a damaged parent loop.
            var chain = _chain ??= new int[64];
            var depth = 0;
            var current = folder;
            FolderInfo info = default;

            while (current >= 0 && current < count && depth <= count)
            {
                if (cache.TryGetValue(current, out info))
                    break;

                if (depth == chain.Length)
                    Array.Resize(ref _chain, chain.Length * 2);
                chain = _chain!;

                chain[depth++] = current;
                current = entries[current].ParentIndex;
            }

            if (current < 0 || current >= count)
                info = default;

            for (var i = depth - 1; i >= 0; i--)
            {
                info = Derive(info, chain[i]);
                cache[chain[i]] = info;
            }

            return info;
        }

        private FolderInfo Derive(FolderInfo parent, int folder)
        {
            ref readonly var entry = ref entries[folder];
            var name = names.AsSpan(entry.NameOffset, entry.NameLength);
            var info = parent;

            info.InScope = parent.InScope || _plan.ScopeRoots.Contains(folder);
            info.UnderCurrent = parent.UnderCurrent || folder == _plan.Current;
            info.Noise = (byte)Math.Max(parent.Noise, SearchRanker.NoiseOf(name));

            // NEW (folder types, step 2): below an Archives & Backups folder counts as soft noise.
            if (_plan.LowRank.Count > 0 && _plan.LowRank.Contains(folder))
                info.Noise = (byte)Math.Max((int)info.Noise, SearchRanker.SoftNoiseLevel); // FIXED: int overload (CS0121)

            // The drive root is not a meaningful folder name ("C:\" would match the word "c").
            if (entry.ParentIndex < 0)
                return info;

            for (var t = 0; t < _contextTerms; t++)
            {
                var shift = 4 * t;
                var category = (ulong)SearchRanker.Category(name, _terms[t].Text);

                if (category > ((info.FolderCategories >> shift) & 0xF))
                    info.FolderCategories = (info.FolderCategories & ~(0xFUL << shift)) | (category << shift);
            }

            if (_hasTermTags && _plan.TermTags.TryGetValue(folder, out var bits))
            {
                for (var t = 0; t < _contextTerms; t++)
                {
                    var shift = 2 * t;
                    var strength = (bits >> shift) & 3;

                    if (strength > ((info.InheritedTags >> shift) & 3))
                        info.InheritedTags = (info.InheritedTags & ~(3UL << shift)) | (strength << shift);
                }
            }

            return info;
        }
    }
}
