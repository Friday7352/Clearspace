// Clearspace | NEW (downloads): remembers where you put downloaded files, to offer the same place next time.
// Each time you move files out of a Downloads folder in Clearspace (drag, cut and paste, or the "Move to"
// offer itself), it notes the folder they went to against three things about each file:
//     the site and the kind together   ("github.com" + ".zip")
//     the site                          ("github.com")
//     the kind                          (".pdf")
// When a later download matches something that has gone to one folder often enough, and clearly more often
// than anywhere else, that folder is offered as "Move to <folder>". The most specific match wins.
// It only ever offers. Nothing is moved by itself, nothing is learned from folders that are not Downloads
// folders, and the notes are counts of folder names in settings.json - no file names, no addresses.
// "Stop suggesting this folder" forgets a folder. The counting and the choosing are plain functions over a
// dictionary, so the tests (Clearspace.Tests/DownloadsTests.cs) need no disk and no settings file.
using System.IO;
using Clearspace.Models;

namespace Clearspace.Services;

// Why: "Files from github.com usually go here", for the tooltip.
internal sealed record FilingSuggestion(string Folder, string Why)
{
    public string FolderName => Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder)) is { Length: > 0 } name ? name : Folder;
}

internal static class FilingMemory
{
    internal const int MovesBeforeOffering = 2;        // for a site, or a site and kind together
    internal const int MovesBeforeOfferingByKind = 3;  // the kind alone says less, so it has to be seen more often
    internal const double ClearMajority = 0.6;         // share of the moves that went to the offered folder
    private const int FoldersPerKey = 6;
    private const int MostKeys = 300;
    private const int MostFilesPerMove = 200;

    // The things a file is remembered by, most specific first. `site` may be null (no note on the file).
    internal static IEnumerable<string> Keys(string? site, string extension)
    {
        var kind = extension.ToLowerInvariant();
        var from = string.IsNullOrWhiteSpace(site) ? null : site.ToLowerInvariant();

        if (from is not null && kind.Length > 0) yield return $"both:{from}|{kind}";
        if (from is not null) yield return $"site:{from}";
        if (kind.Length > 0) yield return $"kind:{kind}";
    }

    // Notes one move. However many files went along, each thing they have in common counts once: dragging
    // forty PDFs to a folder in one go is one decision, not forty.
    internal static void Learn(Dictionary<string, Dictionary<string, int>> habits,
        IEnumerable<(string? Site, string Extension)> files, string destination)
    {
        var folder = Normalize(destination);
        if (folder.Length == 0)
            return;

        var touched = files.SelectMany(file => Keys(file.Site, file.Extension)).ToHashSet(StringComparer.Ordinal);

        foreach (var key in touched)
        {
            if (!habits.TryGetValue(key, out var folders))
                habits[key] = folders = [];

            var known = folders.Keys.FirstOrDefault(name => name.Equals(folder, StringComparison.OrdinalIgnoreCase)) ?? folder;
            folders[known] = folders.GetValueOrDefault(known) + 1;

            // Keep it small: of the other folders, the one used least is forgotten.
            while (folders.Count > FoldersPerKey)
                folders.Remove(folders.Where(pair => pair.Key != known).MinBy(pair => pair.Value).Key);
        }

        if (habits.Count <= MostKeys)
            return;

        // Too many notes. One-off moves that never became a habit go first, all of them at once (so a new
        // site has a long while to be seen a second time before the next clear-out); then the least used.
        foreach (var key in habits.Where(pair => !touched.Contains(pair.Key) && pair.Value.Values.Sum() <= 1).Select(pair => pair.Key).ToList())
            habits.Remove(key);

        while (habits.Count > MostKeys && habits.Any(pair => !touched.Contains(pair.Key)))
            habits.Remove(habits.Where(pair => !touched.Contains(pair.Key)).MinBy(pair => pair.Value.Values.Sum()).Key);
    }

    // The folder to offer for a file, or null. `exists`: whether a folder is still there. `currentFolder`:
    // the folder the file is in now, which is never offered.
    internal static FilingSuggestion? Suggest(IReadOnlyDictionary<string, Dictionary<string, int>> habits,
        string? site, string extension, Func<string, bool> exists, string? currentFolder = null)
    {
        var here = currentFolder is null ? string.Empty : Normalize(currentFolder);

        foreach (var key in Keys(site, extension))
        {
            if (!habits.TryGetValue(key, out var folders) || folders.Count == 0)
                continue;

            var total = folders.Values.Sum();
            var (folder, count) = folders.MaxBy(pair => pair.Value);
            var needed = key.StartsWith("kind:", StringComparison.Ordinal) ? MovesBeforeOfferingByKind : MovesBeforeOffering;

            if (count < needed || count < total * ClearMajority)
                continue;
            if (folder.Equals(here, StringComparison.OrdinalIgnoreCase) || !exists(folder))
                continue;

            var why = key.StartsWith("both:", StringComparison.Ordinal) ? $"{extension.ToLowerInvariant()} files from {site} usually go here"
                : key.StartsWith("site:", StringComparison.Ordinal) ? $"Files from {site} usually go here"
                : $"{extension.ToLowerInvariant()} files usually go here";
            return new FilingSuggestion(folder, why);
        }

        return null;
    }

    // Forgets a folder everywhere it is noted. True when something was forgotten.
    internal static bool Forget(Dictionary<string, Dictionary<string, int>> habits, string destination)
    {
        var folder = Normalize(destination);
        var changed = false;

        foreach (var key in habits.Keys.ToList())
        {
            var folders = habits[key];
            foreach (var name in folders.Keys.Where(name => name.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList())
                changed |= folders.Remove(name);

            if (folders.Count == 0)
                habits.Remove(key);
        }

        return changed;
    }

    private static string Normalize(string folder)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return string.Empty;
        }
    }

    // ---- the app's side: the saved notes, and the files being moved

    // The notes are changed on the UI thread (a move, "Stop suggesting") and read from a background one
    // (the Just arrived strip works out its offers there), so both go through this.
    private static readonly object Gate = new();

    // trustNetworkFolders: take a remembered folder on a network drive to be there without asking the
    // network (which can take seconds when it is not connected). For callers on the UI thread; the move
    // itself checks again and says so if the folder has gone.
    internal static FilingSuggestion? SuggestFor(string path, string? site, bool trustNetworkFolders = false)
    {
        var extension = Path.GetExtension(path);

        // A copy of the few notes that matter, so the disk check below (is the folder still there? - slow
        // for a network folder that is not connected) is not made while holding the lock.
        var notes = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        lock (Gate)
        {
            var habits = SettingsService.GetFilingHabits();
            foreach (var key in Keys(site, extension))
            {
                if (habits.TryGetValue(key, out var folders))
                    notes[key] = new Dictionary<string, int>(folders);
            }
        }

        return notes.Count == 0 ? null : Suggest(notes, site, extension,
            folder => (trustNetworkFolders && IsOnNetwork(folder)) || Directory.Exists(folder), Path.GetDirectoryName(path));
    }

    internal static bool IsOnNetwork(string folder) // CHANGED (recommended folders): FilingAdvisor asks too
    {
        try
        {
            var root = Path.GetPathRoot(folder);
            return !string.IsNullOrEmpty(root) &&
                   (root.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType == DriveType.Network);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // CHANGED (recommended folders): also takes the folder off the recently used list.
    internal static void ForgetFolder(string folder)
    {
        lock (Gate)
        {
            var target = Normalize(folder);
            var forgotten = Forget(SettingsService.GetFilingHabits(), folder);
            forgotten |= SettingsService.GetRecentMoveFolders().RemoveAll(known => known.Equals(target, StringComparison.OrdinalIgnoreCase)) > 0;
            if (forgotten)
                SettingsService.Save();
        }
    }

    // ---- NEW (recommended folders): the folders files were moved to lately, and a copy of all the notes

    internal const int MostRecentFolders = 8;

    // Puts a folder at the front of the recently used list (once, however it is spelled) and keeps the
    // list short. False when the folder was at the front already, so there is nothing to save.
    internal static bool NoteRecent(List<string> recent, string destination)
    {
        var folder = Normalize(destination);
        if (folder.Length == 0)
            return false;

        if (recent.Count > 0 && recent[0].Equals(folder, StringComparison.OrdinalIgnoreCase))
            return false;

        recent.RemoveAll(known => known.Equals(folder, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, folder);
        if (recent.Count > MostRecentFolders)
            recent.RemoveRange(MostRecentFolders, recent.Count - MostRecentFolders);
        return true;
    }

    // Called after files were moved to a folder in Clearspace, from any folder (not only Downloads: what
    // is kept is the folder's name alone, and it is only used to recommend it in the folder chooser).
    internal static void NoteDestination(string destination)
    {
        try
        {
            lock (Gate)
            {
                if (NoteRecent(SettingsService.GetRecentMoveFolders(), destination))
                    SettingsService.Save();
            }
        }
        catch (Exception)
        {
            // a nicety; it never gets in the way of the move itself
        }
    }

    // A copy of the filing notes and the recently used folders, for FilingAdvisor to weigh on another thread.
    internal static (Dictionary<string, Dictionary<string, int>> Habits, List<string> Recent) Snapshot()
    {
        lock (Gate)
        {
            var habits = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (var (key, folders) in SettingsService.GetFilingHabits())
                habits[key] = new Dictionary<string, int>(folders);

            return (habits, new List<string>(SettingsService.GetRecentMoveFolders()));
        }
    }

    // Called just before files are moved; Moved() is called on the result if the move went through. The
    // site is read now because that is where the file still is. Returns null when there is nothing to
    // learn: no files leaving a Downloads folder for somewhere else.
    internal static Move? Watch(IReadOnlyList<string> paths, string destination, Func<string, bool>? isDownloadsFolder = null)
    {
        try
        {
            isDownloadsFolder ??= folder => FolderTypes.Resolved(folder).Base == DirectoryViewProfile.Downloads;
            var target = Normalize(destination);
            var folders = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var files = new List<(string Path, string? Site, string Extension)>();

            foreach (var path in paths.Take(MostFilesPerMove))
            {
                var parent = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(parent) || Normalize(parent).Equals(target, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                    continue;

                if (!folders.TryGetValue(parent, out var counts))
                    folders[parent] = counts = isDownloadsFolder(parent);
                if (counts)
                    files.Add((path, DownloadOrigin.Read(path)?.Site, Path.GetExtension(path)));
            }

            return files.Count == 0 ? null : new Move(files, destination);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null; // learning is a nicety; it never gets in the way of the move itself
        }
    }

    internal sealed class Move(List<(string Path, string? Site, string Extension)> files, string destination)
    {
        // Notes the files that really left (Windows may have skipped some, or the move was cancelled).
        internal void Moved()
        {
            var gone = files.Where(file => !File.Exists(file.Path)).Select(file => (file.Site, file.Extension)).ToList();
            if (gone.Count == 0)
                return;

            lock (Gate)
            {
                Learn(SettingsService.GetFilingHabits(), gone, destination);
                SettingsService.Save();
            }
        }
    }
}
