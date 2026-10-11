// Clearspace | NEW (undo): takes an item back out of the Recycle Bin, for undoing a delete.
// When Windows recycles C:\Work\report.docx it moves it to C:\$Recycle.Bin\<your account's id>\$Rxxxxxx.docx
// and writes a small $Ixxxxxx.docx next to it saying where it came from and when it was deleted. Restoring is
// moving the $R item back to that place and removing its $I note, which is what Explorer's "Restore" does.
// Reading the notes directly keeps this independent of the language Windows is in (the Recycle Bin's
// own "Restore" command has a different name in every language).
// $I layout: version (8 bytes: 1 before Windows 10, 2 since), size (8), deleted at (8, a FILETIME in UTC),
// then version 2: the path's length in characters (4) and the path; version 1: the path in 520 bytes.
// Tests: Clearspace.Tests/UndoTests.cs.
using System.IO;
using System.Security.Principal;
using System.Text;
using Clearspace.Native;

namespace Clearspace.Services;

internal static class RecycleBin
{
    // Puts back what was deleted from `originalPath` at or after `deletedSinceUtc` (the newest such, when
    // there are several). Null when it is back; otherwise words saying why not.
    internal static string? Restore(string originalPath, DateTime deletedSinceUtc)
        => RestoreAll([originalPath], deletedSinceUtc).FirstOrDefault();

    // The same for several items, reading each drive's Recycle Bin once (it can hold thousands of notes).
    // Returns words for each item that could not be put back.
    internal static List<string> RestoreAll(IReadOnlyList<string> originalPaths, DateTime deletedSinceUtc)
    {
        var problems = new List<string>();
        var account = WindowsIdentity.GetCurrent().User?.Value;
        var earliest = deletedSinceUtc.AddSeconds(-5); // the clock Windows stamps with, and ours, can differ a little
        var notesByBin = new Dictionary<string, Dictionary<string, (string Note, DateTime At)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var original in originalPaths)
        {
            var path = Path.TrimEndingDirectorySeparator(original);
            var name = Path.GetFileName(path);

            try
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    problems.Add($"Something named {name} is in its place now, so it was left in the Recycle Bin.");
                    continue;
                }

                var root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root) || account is null)
                {
                    problems.Add($"{name} could not be found in the Recycle Bin.");
                    continue;
                }

                var bin = Path.Combine(root, "$Recycle.Bin", account);
                if (!notesByBin.TryGetValue(bin, out var notes))
                    notesByBin[bin] = notes = ReadNotes(bin, earliest);

                if (!notes.Remove(path, out var found))
                {
                    problems.Add($"{name} is no longer in the Recycle Bin.");
                    continue;
                }

                var item = Path.Combine(bin, "$R" + Path.GetFileName(found.Note)[2..]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (Directory.Exists(item))
                    Directory.Move(item, path);
                else if (File.Exists(item))
                    File.Move(item, path);
                else
                {
                    problems.Add($"{name} is no longer in the Recycle Bin.");
                    continue;
                }

                File.Delete(found.Note);
                NativeMethods.SHChangeNotify(NativeMethods.SHCNE_UPDATEDIR, NativeMethods.SHCNF_PATHW, Path.GetDirectoryName(path), IntPtr.Zero);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                problems.Add($"{name} could not be taken out of the Recycle Bin: {error.Message}");
            }
        }

        // The Recycle Bin's own window (if open) shows what left it.
        foreach (var bin in notesByBin.Keys)
            NativeMethods.SHChangeNotify(NativeMethods.SHCNE_UPDATEDIR, NativeMethods.SHCNF_PATHW, bin, IntPtr.Zero);

        return problems;
    }

    // Every note in one Recycle Bin for an item deleted at or after `earliest`: the newest for each path.
    private static Dictionary<string, (string Note, DateTime At)> ReadNotes(string bin, DateTime earliest)
    {
        var notes = new Dictionary<string, (string Note, DateTime At)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(bin))
            return notes;

        foreach (var note in Directory.EnumerateFiles(bin, "$I*"))
        {
            try
            {
                if (!TryReadNote(File.ReadAllBytes(note), out var from, out var at) || at < earliest)
                    continue;
                if (!notes.TryGetValue(from, out var known) || at > known.At)
                    notes[from] = (note, at);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // one unreadable note; the others still count
            }
        }

        return notes;
    }

    // Reads a $I note: where the item was deleted from, and when (UTC).
    internal static bool TryReadNote(byte[] note, out string deletedFrom, out DateTime deletedAtUtc)
    {
        deletedFrom = string.Empty;
        deletedAtUtc = DateTime.MinValue;
        if (note.Length < 24)
            return false;

        var version = BitConverter.ToInt64(note, 0);
        var stamp = BitConverter.ToInt64(note, 16);
        if (stamp <= 0)
            return false;

        try
        {
            deletedAtUtc = DateTime.FromFileTimeUtc(stamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if (version >= 2)
        {
            if (note.Length < 28)
                return false;
            var characters = BitConverter.ToInt32(note, 24);
            if (characters <= 0 || 28 + characters * 2 > note.Length)
                return false;
            deletedFrom = Encoding.Unicode.GetString(note, 28, characters * 2);
        }
        else
        {
            deletedFrom = Encoding.Unicode.GetString(note, 24, Math.Min(520, note.Length - 24));
        }

        var end = deletedFrom.IndexOf('\0');
        if (end >= 0)
            deletedFrom = deletedFrom[..end];
        deletedFrom = Path.TrimEndingDirectorySeparator(deletedFrom);
        return deletedFrom.Length > 0;
    }
}
