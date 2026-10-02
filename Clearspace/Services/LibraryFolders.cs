// Clearspace | NEW (your files): the folders you added to "Your files".
// Windows' six (Desktop, Documents, Downloads, Pictures, Music, Videos) are always there; this is the list
// of extra folders shown after them, on the Your files page and under Your files in the sidebar. It is a
// list of paths kept in settings.json (SettingsData.LibraryFolders). The folders are ordinary folders:
// taking one off the list never touches the folder itself.
// The functions here only work on the list they are given, so they are covered by ordinary unit tests
// (Clearspace.Tests/YourFilesTests.cs). SettingsService calls them and saves.

using System.IO;

namespace Clearspace.Services;

internal static class LibraryFolders
{
    private static string Tidy(string path) => Path.TrimEndingDirectorySeparator(path.Trim());

    private static int IndexOf(List<string> folders, string path)
    {
        path = Tidy(path);
        return folders.FindIndex(folder => string.Equals(Tidy(folder), path, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool Contains(List<string> folders, string path) => IndexOf(folders, path) >= 0;

    // Adds a folder at the end. False when it is already listed (or the path is empty).
    internal static bool Add(List<string> folders, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Contains(folders, path))
            return false;

        folders.Add(Tidy(path));
        return true;
    }

    internal static bool Remove(List<string> folders, string path)
    {
        var index = IndexOf(folders, path);
        if (index < 0)
            return false;

        folders.RemoveAt(index);
        return true;
    }

    // A listed folder was renamed or moved: it keeps its place in the list under its new path.
    internal static bool Move(List<string> folders, string from, string to)
    {
        var index = IndexOf(folders, from);
        if (index < 0)
            return false;

        if (Contains(folders, to))
            folders.RemoveAt(index);
        else
            folders[index] = Tidy(to);

        return true;
    }

    // Drops folders that were deleted or renamed outside Clearspace. A folder whose parent is missing too
    // is kept: that is a drive that is not connected right now, not a deleted folder.
    internal static bool Prune(List<string> folders, Func<string, bool> exists)
        => folders.RemoveAll(folder =>
               !exists(folder) &&
               Path.GetDirectoryName(folder) is { Length: > 0 } parent &&
               exists(parent)) > 0;

    // The name shown for a folder: its own name ("Projects" for C:\Users\You\Projects).
    internal static string NameOf(string path)
    {
        var name = Path.GetFileName(Tidy(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
