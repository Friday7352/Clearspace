// Clearspace | Index changes collected between rebuilds.

using System.IO;
using Clearspace.Models;

namespace Clearspace.Services;

internal sealed class IndexOverlay
{
    private const int MaxTracked = 100_000;

    private const int MaxRemovedTrees = 256;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _added = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _removedTrees = [];

    public bool Overflowed { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
                return _added.Count + _removed.Count;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _added.Clear();
            _removed.Clear();
            _removedTrees.Clear();
            Overflowed = false;
        }
    }

    public void MarkOverflowed()
    {
        lock (_gate)
            Overflowed = true;
    }

    public void OnCreated(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        lock (_gate)
        {
            _removed.Remove(path);
            _added.Add(path);
            CheckSize();
        }
    }

    public void OnDeleted(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        lock (_gate)
        {
            _added.Remove(path);
            _removed.Add(path);

            if (!Path.HasExtension(path))
            {
                if (_removedTrees.Count >= MaxRemovedTrees)
                    Overflowed = true;
                else
                    _removedTrees.Add(path);
            }

            CheckSize();
        }
    }

    public void OnRenamed(string oldPath, string newPath)
    {
        OnDeleted(oldPath);
        OnCreated(newPath);
    }

    public bool IsRemoved(string path)
    {
        lock (_gate)
        {
            if (_removed.Count == 0)
                return false;

            if (_removed.Contains(path))
                return true;

            if (_removedTrees.Count == 0)
                return false;

            for (var i = 0; i < _removedTrees.Count; i++)
            {
                var tree = _removedTrees[i];

                if (path.Length > tree.Length &&
                    path.StartsWith(tree, StringComparison.OrdinalIgnoreCase) &&
                    path[tree.Length] == Path.DirectorySeparatorChar)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void CollectMatches(
        IReadOnlyList<string> foldedTerms,
        bool showHidden,
        int limit,
        List<FileSystemItem> results)
    {
        string[] candidates;

        lock (_gate)
        {
            if (_added.Count == 0)
                return;

            candidates = [.. _added];
        }

        foreach (var path in candidates)
        {
            if (results.Count >= limit)
                return;

            var name = Path.GetFileName(path);

            if (name.Length == 0)
                continue;

            var folded = name.ToLowerInvariant();
            var matched = true;

            for (var i = 0; i < foldedTerms.Count; i++)
            {
                if (!folded.Contains(foldedTerms[i], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
                continue;

            var item = FileSystemItem.FromLocation(path);

            if (item is null)
                continue;

            if (!showHidden && (item.IsHidden || (item.Attributes & FileAttributes.System) != 0))
                continue;

            results.Add(item);
        }
    }

    private void CheckSize()
    {
        if (_added.Count + _removed.Count > MaxTracked)
            Overflowed = true;
    }
}
