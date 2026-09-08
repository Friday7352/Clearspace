// Clearspace | Cached folder listings.

using Clearspace.Models;

namespace Clearspace.Services;

internal static class FolderSnapshotCache
{
    private sealed record Entry(FileSystemItem[] Items, LinkedListNode<string> Node);

    private const int MaxFolders = 32;
    private const int MaxItems = 100_000;

    private const int PixelRetentionFolders = 3;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Recency = new();
    private static int _itemCount;

    internal static bool TryGet(string path, out IReadOnlyList<FileSystemItem> items)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(path, out var entry))
            {
                items = [];
                return false;
            }

            Recency.Remove(entry.Node);
            Recency.AddFirst(entry.Node);
            items = entry.Items;
            return true;
        }
    }

    internal static void Set(string path, IReadOnlyList<FileSystemItem> items)
    {
        if (items.Count > MaxItems)
            return;

        var snapshot = items.ToArray();

        lock (Gate)
        {
            if (Entries.Remove(path, out var existing))
            {
                Recency.Remove(existing.Node);
                _itemCount -= existing.Items.Length;
            }

            var node = Recency.AddFirst(path);
            Entries[path] = new Entry(snapshot, node);
            _itemCount += snapshot.Length;

            while (Entries.Count > MaxFolders || _itemCount > MaxItems)
            {
                var oldest = Recency.Last;
                if (oldest is null)
                    break;

                Recency.RemoveLast();
                if (Entries.Remove(oldest.Value, out var removed))
                    _itemCount -= removed.Items.Length;
            }

            ReleasePixelsBeyondRetention();
        }
    }

    private static void ReleasePixelsBeyondRetention()
    {
        var index = 0;

        for (var node = Recency.First; node is not null; node = node.Next)
        {
            if (index++ < PixelRetentionFolders)
                continue;

            if (!Entries.TryGetValue(node.Value, out var entry))
                continue;

            foreach (var item in entry.Items)
            {
                item.Thumbnail = null;
                item.GridPlaceholder = null;
            }
        }
    }
}
