// Clearspace | NEW (duplicates from the index): the list of files for a duplicate search, without reading
// a single folder. The file index already knows every file's name, size and date on the drives it covers,
// which is all the first step of the search needs (only files that share their size can be copies). This
// goes through the index once, keeps the files under the folder being searched, and hands back the ones
// whose size occurs more than once. On a large folder that takes a moment instead of minutes.
// What the index does not know is what is IN a file, so DuplicateFinder still opens and compares the
// files returned here; and because the index can be a moment behind the disk, it checks each one's size
// again as it opens it.
// Returns null when the folder can't be answered from the index (a drive that is not indexed, an index
// that is still being built or has lost track of changes, a folder the index has not seen): the search
// then reads the folders itself, as before.
// The rules for what is left out are the same as when the folders are read (DuplicateFinder.List): empty
// files, hidden and system files and everything inside hidden folders unless hidden items are shown, and
// cloud files that are not on this device. Tests: Clearspace.Tests/DuplicateTests.cs.
using System.IO;

namespace Clearspace.Services;

internal static class DuplicateIndex
{
    private const byte Unknown = 0, Inside = 1, Outside = 2, InsideHidden = 3;

    // For DuplicateFinder.Find's `fromIndex`.
    internal static DuplicateListing? TryList(string folder, DuplicateOptions options, CancellationToken token)
    {
        if (!FileIndexService.Covers(folder))
            return null; // not indexed, or not being kept up to date right now

        foreach (var volume in FileIndexService.CaptureVolumes())
        {
            if (folder.StartsWith(volume.Root, StringComparison.OrdinalIgnoreCase) && !FileIndexService.IsPartial(volume))
                return ListFromVolume(volume, folder, options, token);
        }

        return null;
    }

    internal static DuplicateListing? ListFromVolume(VolumeIndex volume, string folder, DuplicateOptions options,
        CancellationToken token = default)
    {
        var start = volume.FindConcurrent(folder);
        if (start == -2)
        {
            // The index has not needed to look a folder up by its path yet; the first lookup builds what
            // that takes.
            lock (volume.WriteGate)
                start = volume.FindLocked(folder);
        }

        var count = volume.Count;
        if (start < 0 || start >= count || !volume.Entry(start).IsFolder)
            return null;

        // For every folder: is it under the folder being searched? Worked out on the way up from a file
        // and remembered, so each folder is looked at once however many files it holds.
        var state = new byte[count];
        state[start] = Inside;
        var chain = new List<int>();

        byte StateOf(int folderIndex)
        {
            chain.Clear();
            var current = folderIndex;
            while (current >= 0 && current < count && state[current] == Unknown && chain.Count <= count)
            {
                chain.Add(current);
                current = volume.Entry(current).ParentIndex;
            }

            var above = current >= 0 && current < count ? state[current] : Outside;
            if (above == Unknown)
                above = Outside; // a chain that never ends: not a real folder

            for (var i = chain.Count - 1; i >= 0; i--)
            {
                ref readonly var entry = ref volume.Entry(chain[i]);
                if (above == Inside)
                {
                    if ((entry.Attributes & VolumeIndex.RemovedFlag) != 0)
                        above = Outside;
                    else if (!options.Hidden && entry.IsHiddenOrSystem)
                        above = InsideHidden; // as when the folders are read: a hidden folder is not opened
                }

                state[chain[i]] = above;
            }

            return above;
        }

        var kept = new List<int>();
        var sizes = new Dictionary<long, int>();

        for (var i = 0; i < count; i++)
        {
            if ((i & 0xFFFF) == 0)
                token.ThrowIfCancellationRequested();

            ref readonly var entry = ref volume.Entry(i);
            if (entry.IsFolder || entry.Size <= 0 || (entry.Attributes & VolumeIndex.RemovedFlag) != 0)
                continue;
            if ((entry.Attributes & DuplicateFinder.NotOnThisDevice) != 0 || (!options.Hidden && entry.IsHiddenOrSystem))
                continue;

            var parent = entry.ParentIndex;
            if (parent < 0 || parent >= count)
                continue;
            if (options.Subfolders ? StateOf(parent) != Inside : parent != start)
                continue;

            kept.Add(i);
            sizes[entry.Size] = sizes.GetValueOrDefault(entry.Size) + 1;
        }

        // Paths are built only for the files that share their size with another: the rest can't be copies.
        var files = new List<DuplicateFile>();
        foreach (var i in kept)
        {
            ref readonly var entry = ref volume.Entry(i);
            if (sizes[entry.Size] < 2)
                continue;

            DateTime modified;
            try { modified = DateTime.FromFileTime(entry.ModifiedTicks); }
            catch (ArgumentOutOfRangeException) { continue; } // a date Windows can't express as a time

            files.Add(new DuplicateFile(volume.GetPath(i), entry.Size, modified,
                MayBeLink: (entry.Attributes & FileAttributes.ReparsePoint) != 0));
        }

        return new DuplicateListing(files, kept.Count, FromIndex: true);
    }
}
