// Clearspace | NEW (lock icons, locked folders): an in-memory snapshot of what is locked, so every listed
// row can show its lock badge without touching the disk. Loaded from the lock tables on first use and
// replaced whenever a lock operation finishes (Reload). Thread-safe: readers see one immutable snapshot.
using System.IO;

namespace Clearspace.Services;

internal enum LockState { None, Locked, Open }

internal static class FileLockRegistry
{
    // CHANGED (Explorer integration): + the owner of each open folder.
    private sealed record Snapshot(HashSet<string> Files, Dictionary<string, LockState> Folders, Dictionary<string, string?> Owners,
        Dictionary<string, (string Directory, string? Owner)> OpenFiles); // CHANGED (unlock vs remove lock): + files open for a visit

    private static Snapshot? _snapshot;
    private static readonly object Gate = new();

    // Defaults to the app database (read lazily); tests can point it elsewhere.
    private static string? _databasePath;
    private static string? _legacyPath;
    private static bool _legacySet;
    internal static string DatabasePath { get => _databasePath ?? TagService.DatabasePath; set => _databasePath = value; }
    internal static string? LegacyPath { get => _legacySet ? _legacyPath : TagService.TagFilePath; set { _legacyPath = value; _legacySet = true; } }

    internal static void Reload()
    {
        Snapshot next;
        try
        {
            using var store = new FileLockStore(DatabasePath, LegacyPath);
            var folders = store.Folders();
            next = new Snapshot(
                new HashSet<string>(store.LockedFilePaths(), StringComparer.OrdinalIgnoreCase),
                folders.ToDictionary(
                    record => record.Path,
                    record => record.State == "Open" ? LockState.Open : LockState.Locked,
                    StringComparer.OrdinalIgnoreCase),
                folders.ToDictionary(record => record.Path, record => record.Owner, StringComparer.OrdinalIgnoreCase),
                store.OpenFiles().ToDictionary(record => record.Path, record => (record.Directory, record.Owner), StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // Lock badges are a convenience; never let a database problem break folder listing.
            next = new Snapshot(new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, LockState>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, (string Directory, string? Owner)>(StringComparer.OrdinalIgnoreCase));
        }
        lock (Gate) _snapshot = next;
    }

    private static Snapshot Current
    {
        get
        {
            var snapshot = Volatile.Read(ref _snapshot);
            if (snapshot is not null) return snapshot;
            Reload();
            return Volatile.Read(ref _snapshot)!;
        }
    }

    // The badge for one row: a locked folder record, or a file recorded as locked.
    internal static LockState StateOf(string path, bool isFolder)
    {
        var snapshot = Current;
        if (isFolder)
            return snapshot.Folders.TryGetValue(Path.TrimEndingDirectorySeparator(path), out var state) ? state : LockState.None;
        if (snapshot.OpenFiles.ContainsKey(path)) return LockState.Open; // NEW (unlock vs remove lock)
        // CHANGED (Explorer integration): a ".cslock" name also counts (e.g. files locked on another PC).
        return snapshot.Files.Contains(path) || LockNames.HasLockedName(path) ? LockState.Locked : LockState.None;
    }

    // The outermost locked folder that is, or contains, `path` (or null).
    internal static (string Folder, LockState State)? Governing(string path)
    {
        (string Folder, LockState State)? best = null;
        foreach (var (folder, state) in Current.Folders)
        {
            if (!FileLockService.SamePath(folder, path) && !FileLockService.IsInside(path, folder)) continue;
            if (best is null || folder.Length < best.Value.Folder.Length) best = (folder, state);
        }
        return best;
    }

    // NEW (Explorer integration): the Clearspace process that has a folder open, if any.
    internal static string? OwnerOf(string folder) => Current.Owners.GetValueOrDefault(folder);

    // NEW (unlock vs remove lock): files unlocked for a visit, with the folder they belong to.
    internal static IReadOnlyList<(string Path, string Directory, string? Owner)> OpenFiles =>
        Current.OpenFiles.Select(pair => (pair.Key, pair.Value.Directory, pair.Value.Owner)).ToList();

    internal static IReadOnlyList<string> OpenFolders =>
        Current.Folders.Where(pair => pair.Value == LockState.Open).Select(pair => pair.Key).ToList();
}
