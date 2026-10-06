// Clearspace | NEW (live folder): brings a folder listing up to date without rebuilding it.
// When the folder on screen changes on the disk, the window could simply read it again and show the new
// list. But every row would then be a new object: the selection would be lost, tile pictures would be
// fetched again and the list would flicker, each time a download grows. Merge keeps every row that has
// not changed exactly as it is (the same object), adds the new ones, drops the ones that are gone, and
// replaces only the rows whose file really is different.
// No disk access and no windows in here; the tests are Clearspace.Tests/LiveFolderTests.cs.
using Clearspace.Models;

namespace Clearspace.Services;

// Items is the list to show. When nothing changed it is the very list that was passed in.
internal sealed record FolderChanges(IReadOnlyList<FileSystemItem> Items, int Added, int Removed, int Updated)
{
    public bool Any => Added + Removed + Updated > 0;
}

internal static class FolderMerge
{
    // current: the rows on screen. fresh: what is in the folder now, as just read (in any order).
    // order: how the list is sorted. newcomers gets the rows that are new objects (added or replaced);
    // they still need their icons and tags.
    internal static FolderChanges Merge(IReadOnlyList<FileSystemItem> current, IReadOnlyList<FileSystemItem> fresh,
        IComparer<FileSystemItem> order, out List<FileSystemItem> newcomers)
    {
        newcomers = [];

        var known = new Dictionary<string, FileSystemItem>(current.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var item in current)
            known[item.Name] = item;

        var merged = new List<FileSystemItem>(fresh.Count);
        var added = 0;
        var updated = 0;

        foreach (var now in fresh)
        {
            if (!known.Remove(now.Name, out var was))
            {
                merged.Add(now);
                newcomers.Add(now);
                added++;
            }
            else if (IsSame(was, now))
            {
                merged.Add(was);
            }
            else
            {
                merged.Add(now);
                newcomers.Add(now);
                updated++;
            }
        }

        var removed = known.Count; // what is left was on screen and is no longer in the folder
        if (added + removed + updated == 0)
            return new FolderChanges(current, 0, 0, 0);

        merged.Sort(order);
        return new FolderChanges(merged, added, removed, updated);
    }

    // Whether a row still describes its file. A folder's date changes whenever something inside it does;
    // that is not worth a new row (its picture would be drawn again each time), so for folders only the
    // name and the attributes count. The date shown for a folder catches up at the next F5 or visit.
    internal static bool IsSame(FileSystemItem was, FileSystemItem now)
    {
        if (!string.Equals(was.Name, now.Name, StringComparison.Ordinal) ||
            was.Attributes != now.Attributes ||
            was.ReparseTag != now.ReparseTag)
            return false;

        return was.IsFolder || (was.Size == now.Size && was.DateModified == now.DateModified);
    }
}
