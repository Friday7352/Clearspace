// Clearspace | Search-result ordering.
//
// CHANGED (search relevance): scoring is split into pieces that both the file-index scan (IndexSearch,
// working on raw index entries) and this class (working on FileSystemItems) call, so an item gets the
// same score whichever source found it. Evidence per word:
//   tag on the item        exact 1100 · prefix 350     (above an exact filename, so "work" puts Work first)
//   filename               exact stem 1000 · starts 600 · word start 400 · anywhere 150
//   tag on a parent folder exact 500  · prefix 200
//   parent folder name     exact 450  · starts/word start 300 · anywhere 120
//   type word ("photos")   420        · folder type ("Photos" folder) 380
// Relevance = average over words + 220 × (share of the name the words cover). Bonuses and penalties
// (folder, current folder, noise paths, derived files, recency) are unchanged from before.

using System.IO;
using Clearspace.Models;
using Clearspace.Native;

namespace Clearspace.Services;

internal static class SearchRanker
{
    internal const int TagExact = 1100;
    internal const int TagPrefix = 350;
    internal const int InheritedTagExact = 500;
    internal const int InheritedTagPrefix = 200;
    internal const int TypeWord = 420;
    internal const int FolderType = 380;

    // Category: 0 none, 1 anywhere, 2 word start, 3 starts with, 4 exact (whole name or stem).
    private static readonly int[] NameByCategory = [0, 150, 400, 600, 1000];
    private static readonly int[] FolderByCategory = [0, 120, 300, 300, 450];

    internal const int NoNoise = 0;
    internal const int SoftNoiseLevel = 1;
    internal const int HardNoiseLevel = 2;

    // CHANGED: folder names rather than "\name\" path fragments, so the index can check one folder
    // at a time. A path contains "\appdata\" exactly when one of its folders is named AppData.
    private static readonly HashSet<string> HardNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "appdata", "node_modules", ".git", "temp", "tmp", "windows", "programdata", "$recycle.bin",
        "cache", "caches", ".vs", "package cache", "system volume information"
    };

    private static readonly HashSet<string> SoftNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "obj", "bin", ".nuget", ".gradle", "node", "dist"
    };

    private static readonly HashSet<string> DerivedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".log", ".bak", ".cache", ".pdb", ".obj", ".idb", ".ilk",
        ".dmp", ".etl", ".old", ".part", ".crdownload"
    };

    internal static int NamePoints(int category) => NameByCategory[category];
    internal static int FolderPoints(int category) => FolderByCategory[category];

    internal static int TagPoints(TagStrength strength) => strength switch
    {
        TagStrength.Exact => TagExact,
        TagStrength.Prefix => TagPrefix,
        _ => 0
    };

    internal static int InheritedTagPoints(TagStrength strength) => strength switch
    {
        TagStrength.Exact => InheritedTagExact,
        TagStrength.Prefix => InheritedTagPrefix,
        _ => 0
    };

    // NEW: how a word appears in one name (file or folder).
    internal static int Category(ReadOnlySpan<char> name, ReadOnlySpan<char> term)
    {
        if (term.IsEmpty || name.IsEmpty)
            return 0;

        var at = name.IndexOf(term, StringComparison.OrdinalIgnoreCase);

        if (at < 0)
            return 0;

        if (name.Equals(term, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(name).Equals(term, StringComparison.OrdinalIgnoreCase))
            return 4;

        if (at == 0)
            return 3;

        return IsWordStart(name, at) ? 2 : 1;
    }

    // NEW: average word evidence plus name coverage (unchanged formula from the filename-only ranker).
    internal static int Combine(int strengthSum, int termCount, int matchedLength, int nameLength)
    {
        if (termCount == 0)
            return 0;

        var score = strengthSum / termCount;

        if (nameLength > 0 && matchedLength > 0)
            score += (int)(220.0 * Math.Min(matchedLength, nameLength) / nameLength);

        return score;
    }

    // NEW: noise level of one folder name.
    internal static int NoiseOf(ReadOnlySpan<char> folderName)
    {
        if (HardNoise.GetAlternateLookup<ReadOnlySpan<char>>().Contains(folderName))
            return HardNoiseLevel;

        return SoftNoise.GetAlternateLookup<ReadOnlySpan<char>>().Contains(folderName) ? SoftNoiseLevel : NoNoise;
    }

    // NEW: noise level of the folders above an item.
    internal static int NoiseOfPath(string path)
    {
        var end = path.LastIndexOf(Path.DirectorySeparatorChar);
        var noise = NoNoise;
        var segmentStart = 0;

        for (var at = 0; at <= end && noise < HardNoiseLevel; at++)
        {
            if (at < end && path[at] != Path.DirectorySeparatorChar)
                continue;

            if (at > segmentStart)
                noise = Math.Max(noise, NoiseOf(path.AsSpan(segmentStart, at - segmentStart)));

            segmentStart = at + 1;
        }

        return noise;
    }

    // NEW: everything that is not about the words. ageDays is NaN when the modified time is unknown.
    internal static int Bonus(bool isFolder, ReadOnlySpan<char> extension, double ageDays, int noise, bool underCurrent)
    {
        var score = 0;

        if (isFolder)
            score += 90;

        if (underCurrent)
            score += 260;

        score -= noise switch
        {
            HardNoiseLevel => 500,
            SoftNoiseLevel => 150,
            _ => 0
        };

        if (!isFolder)
        {
            if (DerivedTypes.GetAlternateLookup<ReadOnlySpan<char>>().Contains(extension))
                score -= 220;

            if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                score -= 70;
        }

        if (!double.IsNaN(ageDays))
        {
            score += ageDays switch
            {
                < 7 => 70,
                < 30 => 45,
                < 365 => 20,
                _ => 0
            };
        }

        return score;
    }

    // NEW: strictly inside the current folder (the folder itself is not "under" it).
    internal static bool IsUnder(string path, string? folder)
    {
        if (string.IsNullOrEmpty(folder) || path.Length <= folder.Length ||
            !path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            return false;

        return folder.EndsWith(Path.DirectorySeparatorChar) || path[folder.Length] == Path.DirectorySeparatorChar;
    }

    // CHANGED: ranks by the whole query (tags, folders, types), not just filename terms, and also
    // orders filter-only searches (ext:pdf) by the bonuses.
    public static void Rank(List<FileSystemItem> items, SearchQuery query, string? currentFolder)
    {
        if (items.Count < 2)
            return;

        var scored = new (int Score, FileSystemItem Item)[items.Count];

        for (var i = 0; i < items.Count; i++)
            scored[i] = (Score(items[i], query, currentFolder), items[i]);

        Array.Sort(scored, (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);

            return byScore != 0 ? byScore : CompareTies(left.Item, right.Item);
        });

        items.Clear();

        foreach (var entry in scored)
            items.Add(entry.Item);
    }

    public static int Score(FileSystemItem item, SearchQuery query, string? currentFolder)
    {
        if (item.Name.Length == 0)
            return 0;

        // Items can arrive from a source with a narrower check (Windows Search matches filenames and
        // contents). Those still rank, just without word evidence.
        var relevance = Math.Max(0, query.Relevance(item));
        var extension = item.IsFolder ? ReadOnlySpan<char>.Empty : Path.GetExtension(item.Name.AsSpan());
        var ageDays = item.DateModified == DateTime.MinValue
            ? double.NaN
            : (DateTime.Now - item.DateModified).TotalDays;

        return relevance + Bonus(item.IsFolder, extension, ageDays, NoiseOfPath(item.FullPath),
            IsUnder(item.FullPath, currentFolder));
    }

    private static bool IsWordStart(ReadOnlySpan<char> name, int at)
    {
        if (at <= 0)
            return true;

        var previous = name[at - 1];

        if (!char.IsLetterOrDigit(previous))
            return true;

        return char.IsUpper(name[at]) && !char.IsUpper(previous);
    }

    public static int CompareTies(FileSystemItem x, FileSystemItem y)
    {
        if (x.IsFolder != y.IsFolder)
            return x.IsFolder ? -1 : 1;

        return NativeMethods.StrCmpLogicalW(x.Name, y.Name);
    }
}
