// Clearspace | Bounded changes and recovery generations, isolated by volume.
using System.IO;
using Clearspace.Models;

namespace Clearspace.Services;

internal sealed class IndexOverlay
{
    private const int MaxTracked = 100_000;
    private const int MaxRemovedTrees = 256;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Changes> _volumes = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Changes
    {
        public readonly HashSet<string> Added = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Removed = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> RemovedTrees = [];
        public long Generation;
        public bool Overflowed;
        public int Count => Added.Count + Removed.Count;
        public void Clear()
        {
            Added.Clear(); Removed.Clear(); RemovedTrees.Clear();
            Added.TrimExcess(); Removed.TrimExcess();
        }
    }

    // Delivered outside the lock; the service queues a rescan of only this root.
    public event Action<string>? LostChanges;
    private static string Root(string path) => (Path.GetPathRoot(path) ?? path).TrimEnd('\\', '/') + "\\";
    private Changes Get(string root)
    {
        if (!_volumes.TryGetValue(root, out var changes)) _volumes[root] = changes = new();
        return changes;
    }

    public int Count { get { lock (_gate) return _volumes.Values.Sum(changes => changes.Count); } }
    public bool IsHealthy(string root)
    {
        lock (_gate) return !_volumes.TryGetValue(Root(root), out var changes) || !changes.Overflowed;
    }

    public long Generation(string root)
    {
        lock (_gate) return Get(Root(root)).Generation;
    }

    // Only a successfully published replacement may recover a volume. New failures during
    // a scan change the generation and cannot be erased by an older scan finishing.
    public bool TryRecover(string root, long generation)
    {
        lock (_gate)
        {
            var changes = Get(Root(root));
            if (changes.Generation != generation) return false;
            changes.Clear();
            changes.Overflowed = false;
            return true;
        }
    }

    public void MarkOverflowed(string root)
    {
        root = Root(root);
        lock (_gate) Lose(Get(root));
        LostChanges?.Invoke(root);
    }

    private static void Lose(Changes changes)
    {
        changes.Generation++;
        changes.Overflowed = true;
        changes.Clear();
    }

    public void OnCreated(string path) => Record(path, false);
    public void OnDeleted(string path) => Record(path, true);
    public void OnRenamed(string oldPath, string newPath)
    {
        OnDeleted(oldPath);
        OnCreated(newPath);
    }

    private void Record(string path, bool deleted)
    {
        if (string.IsNullOrEmpty(path)) return;
        var root = Root(path);
        var lost = false;
        lock (_gate)
        {
            var changes = Get(root);
            if (changes.Overflowed) return;
            if (deleted)
            {
                changes.Added.Remove(path);
                changes.Removed.Add(path);
                // A deleted directory may contain dots; event args do not tell us its kind.
                if (!changes.RemovedTrees.Contains(path, StringComparer.OrdinalIgnoreCase))
                    changes.RemovedTrees.Add(path);
            }
            else
            {
                changes.Removed.Remove(path);
                changes.RemovedTrees.RemoveAll(tree => tree.Equals(path, StringComparison.OrdinalIgnoreCase));
                changes.Added.Add(path);
            }
            if (changes.RemovedTrees.Count > MaxRemovedTrees || _volumes.Values.Sum(value => value.Count) > MaxTracked)
            {
                Lose(changes);
                lost = true;
            }
        }
        if (lost) LostChanges?.Invoke(root);
    }

    public bool IsRemoved(string path)
    {
        lock (_gate)
        {
            if (!_volumes.TryGetValue(Root(path), out var changes)) return false;
            return changes.Removed.Contains(path) || changes.RemovedTrees.Any(tree =>
                path.Length > tree.Length && path.StartsWith(tree, StringComparison.OrdinalIgnoreCase) &&
                path[tree.Length] == Path.DirectorySeparatorChar);
        }
    }

    public void CollectMatches(IReadOnlyList<string> foldedTerms, bool showHidden, int limit, List<FileSystemItem> results)
    {
        string[] candidates;
        lock (_gate) candidates = [.. _volumes.Values.Where(changes => !changes.Overflowed).SelectMany(changes => changes.Added)];
        foreach (var path in candidates)
        {
            if (results.Count >= limit) return;
            var name = Path.GetFileName(path);
            if (name.Length == 0) continue;
            var folded = name.ToLowerInvariant();
            if (foldedTerms.Any(term => !folded.Contains(term, StringComparison.Ordinal))) continue;
            var item = FileSystemItem.FromLocation(path);
            if (item is null || (!showHidden && (item.IsHidden || (item.Attributes & FileAttributes.System) != 0))) continue;
            results.Add(item);
        }
    }
}
