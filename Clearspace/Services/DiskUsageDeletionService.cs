using System.IO;
using Clearspace.Models;

namespace Clearspace.Services;

internal enum DiskUsagePathState { Exists, Missing, Unknown }
internal sealed record DiskUsageDeleteTarget(int Id, string Name, string Path, bool IsFolder);
internal sealed record DiskUsageDeleteRequest(IReadOnlyList<DiskUsageDeleteTarget> Targets)
{
    public string ConfirmationMessage =>
        $"Permanently delete {(Targets.Count == 1 ? $"\"{Targets[0].Name}\"" : $"these {Targets.Count:N0} items")}?\n\n" +
        string.Join("\n", Targets.Take(12).Select(target => target.Path)) +
        (Targets.Count > 12 ? $"\n…and {Targets.Count - 12:N0} more selected items." : "") +
        "\n\nThis cannot be undone. These items will not go to the Recycle Bin." +
        (Targets.Any(target => target.IsFolder)
            ? "\nFolders and everything currently inside them will be deleted, including content missing from this saved index." : "");
}
internal sealed record DiskUsageDeletionRefresh(DiskUsageSnapshot Snapshot, IReadOnlyList<string> MissingPaths, bool Incomplete);

internal sealed class DiskUsageDeletionService
{
    // Retain confirmed removals across windows/Refresh until an index built after
    // deletion replaces the old version. Paths, not old volume arrays, are retained.
    public static DiskUsageDeletionService Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _removed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IReadOnlyList<string>, IntPtr, FileOperationResult> _delete;
    private readonly Func<string, DiskUsagePathState> _probe;

    public DiskUsageDeletionService(
        Func<IReadOnlyList<string>, IntPtr, FileOperationResult>? delete = null,
        Func<string, DiskUsagePathState>? probe = null)
    {
        _delete = delete ?? ((paths, owner) => FileOperationService.DeletePermanentlyConfirmed(paths, owner));
        _probe = probe ?? Probe;
    }

    public IReadOnlyList<string> ExclusionsFor(VolumeIndex index)
    {
        lock (_gate)
            return _removed.Where(pair => pair.Value >= index.BuiltUtc).Select(pair => pair.Key).ToArray();
    }

    public static DiskUsageDeleteRequest? CreateRequest(DiskUsageSnapshot snapshot, int parent, IEnumerable<int> ids)
    {
        var targets = new List<DiskUsageDeleteTarget>();
        foreach (var id in ids.Distinct())
        {
            if (id <= 0 || !snapshot.IsAvailable(id) || snapshot.Parent(id) != parent) return null;
            var item = snapshot.Item(id);
            if (item.Name is "." or ".." || item.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            var path = Path.GetFullPath(snapshot.PathFor(id));
            var current = Path.GetFullPath(snapshot.PathFor(parent)).TrimEnd('\\', '/');
            if (!string.Equals(Path.GetDirectoryName(path)?.TrimEnd('\\', '/'), current, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path.TrimEnd('\\', '/'), Path.GetPathRoot(path)?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return null;
            targets.Add(new DiskUsageDeleteTarget(id, item.Name, path, item.IsFolder));
        }
        return targets.Count == 0 ? null : new DiskUsageDeleteRequest(targets.AsReadOnly());
    }

    public FileOperationResult DeleteConfirmed(DiskUsageDeleteRequest request, IntPtr owner)
        => _delete(request.Targets.Select(target => target.Path).ToArray(), owner);

    public DiskUsageDeletionRefresh Reconcile(DiskUsageSnapshot snapshot, DiskUsageDeleteRequest request, CancellationToken token)
    {
        var missing = new List<string>();
        var incomplete = false;
        var pending = new Stack<(int Id, string Path)>(request.Targets.Select(target => (target.Id, target.Path)));
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            var state = _probe(current.Path);
            if (state == DiskUsagePathState.Missing)
            {
                missing.Add(current.Path);
                // Record immediately, even if window closure later cancels reconciliation.
                lock (_gate) _removed[current.Path] = DateTime.UtcNow;
            }
            else if (state == DiskUsagePathState.Unknown) incomplete = true;
            else if (snapshot.Item(current.Id).IsFolder)
                foreach (var child in snapshot.Children(current.Id, token))
                    pending.Push((child.Id, Path.Combine(current.Path, child.Name)));
        }
        return new DiskUsageDeletionRefresh(
            DiskUsageSnapshot.Build(snapshot.Source, token, ExclusionsFor(snapshot.Source)), missing, incomplete);
    }

    private static DiskUsagePathState Probe(string path)
    {
        try { _ = File.GetAttributes(path); return DiskUsagePathState.Exists; }
        catch (FileNotFoundException) { return DiskUsagePathState.Missing; }
        catch (DirectoryNotFoundException) { return DiskUsagePathState.Missing; }
        // File.Exists returns false for access errors as well; that must not hide data.
        catch (IOException) { return DiskUsagePathState.Unknown; }
        catch (UnauthorizedAccessException) { return DiskUsagePathState.Unknown; }
    }
}
