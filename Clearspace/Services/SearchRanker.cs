// Clearspace | Search-result ordering.

using System.IO;
using Clearspace.Models;
using Clearspace.Native;

namespace Clearspace.Services;

internal static class SearchRanker
{
    private const int ExactStem = 1000;
    private const int StartsWith = 600;
    private const int WordStart = 400;
    private const int Anywhere = 150;

    private static readonly string[] HardNoise =
    [
        @"\appdata\", @"\node_modules\", @"\.git\", @"\temp\", @"\tmp\",
        @"\windows\", @"\programdata\", @"\$recycle.bin\", @"\cache\",
        @"\caches\", @"\.vs\", @"\package cache\", @"\system volume information\"
    ];

    private static readonly string[] SoftNoise =
    [
        @"\obj\", @"\bin\", @"\.nuget\", @"\.gradle\", @"\node\", @"\dist\"
    ];

    private static readonly HashSet<string> DerivedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".log", ".bak", ".cache", ".pdb", ".obj", ".idb", ".ilk",
        ".dmp", ".etl", ".old", ".part", ".crdownload"
    };

    public static void Rank(
        List<FileSystemItem> items,
        IReadOnlyList<string> terms,
        string? currentFolder)
    {
        if (items.Count < 2 || terms.Count == 0)
            return;

        var scored = new (int Score, FileSystemItem Item)[items.Count];

        for (var i = 0; i < items.Count; i++)
            scored[i] = (Score(items[i], terms, currentFolder), items[i]);

        Array.Sort(scored, (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);

            return byScore != 0 ? byScore : CompareTies(left.Item, right.Item);
        });

        items.Clear();

        foreach (var entry in scored)
            items.Add(entry.Item);
    }

    public static int Score(FileSystemItem item, IReadOnlyList<string> terms, string? currentFolder)
    {
        var name = item.Name;

        if (name.Length == 0)
            return 0;

        var quality = 0;
        var matchedLength = 0;
        var stem = Path.GetFileNameWithoutExtension(name);

        foreach (var term in terms)
        {
            if (term.Length == 0)
                continue;

            var at = name.IndexOf(term, StringComparison.OrdinalIgnoreCase);

            if (at < 0)
                continue;

            matchedLength += term.Length;

            quality += stem.Equals(term, StringComparison.OrdinalIgnoreCase)
                ? ExactStem
                : at == 0
                    ? StartsWith
                    : IsWordStart(name, at)
                        ? WordStart
                        : Anywhere;
        }

        if (quality == 0)
            return 0;

        var score = quality / terms.Count;

        score += (int)(220.0 * matchedLength / name.Length);

        if (item.IsFolder)
            score += 90;

        var path = item.FullPath;

        if (!string.IsNullOrEmpty(currentFolder) &&
            path.StartsWith(currentFolder, StringComparison.OrdinalIgnoreCase))
        {
            score += 260;
        }

        foreach (var segment in HardNoise)
        {
            if (path.Contains(segment, StringComparison.OrdinalIgnoreCase))
            {
                score -= 500;
                break;
            }
        }

        foreach (var segment in SoftNoise)
        {
            if (path.Contains(segment, StringComparison.OrdinalIgnoreCase))
            {
                score -= 150;
                break;
            }
        }

        if (!item.IsFolder)
        {
            if (DerivedTypes.Contains(item.Extension))
                score -= 220;

            if (item.IsShortcut)
                score -= 70;
        }

        var age = DateTime.Now - item.DateModified;

        if (item.DateModified != DateTime.MinValue)
        {
            score += age.TotalDays switch
            {
                < 7 => 70,
                < 30 => 45,
                < 365 => 20,
                _ => 0
            };
        }

        return score;
    }

    private static bool IsWordStart(string name, int at)
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
