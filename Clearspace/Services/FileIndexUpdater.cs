// Clearspace | Applies file-system changes to the live index.
// NEW: replaces "rebuild the index every hour" with patching it as changes happen.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Clearspace.Native;

namespace Clearspace.Services;

internal sealed class FileIndexUpdater : IDisposable
{
    internal enum ChangeKind { Created, Deleted, Changed }

    // Events for the same path within this window collapse into one (a file being written
    // raises dozens of Changed events).
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(350);

    private readonly Func<VolumeIndex[]> _volumes;
    private readonly ConcurrentQueue<(ChangeKind Kind, string Path)> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private readonly Lock _recordingGate = new();
    private readonly Dictionary<string, List<(ChangeKind Kind, string Path)>> _recording = new(StringComparer.OrdinalIgnoreCase);

    public FileIndexUpdater(Func<VolumeIndex[]> volumes)
    {
        _volumes = volumes;
        _thread = new Thread(Run) { IsBackground = true, Name = "Clearspace index updates", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    // Raised on the updater thread after a batch is applied, with the paths that changed.
    public event Action<IReadOnlyList<string>>? Applied;
    public event Action<string>? Failed;

    public void OnCreated(string path) => Enqueue(ChangeKind.Created, path);
    public void OnDeleted(string path) => Enqueue(ChangeKind.Deleted, path);
    public void OnChanged(string path) => Enqueue(ChangeKind.Changed, path);
    public void OnRenamed(string oldPath, string newPath)
    {
        Enqueue(ChangeKind.Deleted, oldPath);
        Enqueue(ChangeKind.Created, newPath);
    }

    // While a volume is being rescanned in the background, keep its changes so they can be
    // replayed onto the fresh index before it replaces the live one.
    public void BeginRecording(string root)
    {
        lock (_recordingGate) _recording[root] = [];
    }

    public IReadOnlyList<(ChangeKind Kind, string Path)> EndRecording(string root)
    {
        lock (_recordingGate)
        {
            if (!_recording.Remove(root, out var changes)) return [];
            return changes;
        }
    }

    public static void Replay(VolumeIndex index, IReadOnlyList<(ChangeKind Kind, string Path)> changes, CancellationToken token)
    {
        foreach (var (kind, path) in Coalesce(changes))
            Apply(index, kind, path, token);
    }

    // Keep replay and publication together: an event arriving during the swap must target
    // either the recorded replacement or the newly published volume, never only the old one.
    public void PublishReplacement(VolumeIndex index, Action publish, CancellationToken token)
    {
        lock (_recordingGate)
        {
            var changes = EndRecording(index.Root);
            Replay(index, changes, token);
            publish();
        }
    }

    private void Enqueue(ChangeKind kind, string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        lock (_recordingGate)
        {
            foreach (var (root, changes) in _recording)
                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) changes.Add((kind, path));
            _queue.Enqueue((kind, path));
        }
        _signal.Set();
    }

    private void Run()
    {
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                _signal.WaitOne();
                if (token.IsCancellationRequested) return;
                // Let a burst of events land, then take everything at once.
                if (token.WaitHandle.WaitOne(Settle)) return;
                var batch = new List<(ChangeKind, string)>();
                while (_queue.TryDequeue(out var change)) batch.Add(change);
                if (batch.Count == 0) continue;
                ApplyBatch(batch, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private void ApplyBatch(List<(ChangeKind Kind, string Path)> batch, CancellationToken token)
    {
        var applied = new List<string>();
        foreach (var (kind, path) in Coalesce(batch))
        {
            VolumeIndex? volume = null;
            try
            {
                lock (_recordingGate)
                {
                    volume = Array.Find(_volumes(), v => path.StartsWith(v.Root, StringComparison.OrdinalIgnoreCase));
                    if (volume is not null && Apply(volume, kind, path, token)) applied.Add(path);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                Trace.WriteLine($"Clearspace: could not apply a change to the index for {path}. {exception.Message}");
                if (volume is not null) Failed?.Invoke(volume.Root);
            }
        }
        if (applied.Count > 0) Applied?.Invoke(applied);
    }

    // Last event per path wins; deletions are applied before creations so a rename or
    // delete-then-recreate lands on the new entry.
    private static IEnumerable<(ChangeKind Kind, string Path)> Coalesce(IEnumerable<(ChangeKind Kind, string Path)> changes)
    {
        var last = new Dictionary<string, ChangeKind>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var (kind, path) in changes)
        {
            if (!last.ContainsKey(path)) order.Add(path);
            // A Changed event never downgrades a pending Created/Deleted.
            if (kind == ChangeKind.Changed && last.TryGetValue(path, out var previous) && previous != ChangeKind.Changed) continue;
            last[path] = kind;
        }
        foreach (var path in order) if (last[path] == ChangeKind.Deleted) yield return (ChangeKind.Deleted, path);
        foreach (var path in order) if (last[path] != ChangeKind.Deleted) yield return (last[path], path);
    }

    private static bool Apply(VolumeIndex index, ChangeKind kind, string path, CancellationToken token)
    {
        if (kind == ChangeKind.Deleted) return index.Remove(path);

        // Created or Changed: read the item's current metadata (it may already be gone again).
        using var handle = NativeMethods.FindFirstFileExW(
            path.TrimEnd('\\'),
            NativeMethods.FINDEX_INFO_LEVELS.FindExInfoBasic,
            out var data,
            NativeMethods.FINDEX_SEARCH_OPS.FindExSearchNameMatch,
            IntPtr.Zero,
            0);
        if (handle.IsInvalid) return kind == ChangeKind.Created && index.Remove(path);

        var attributes = data.dwFileAttributes;
        var isFolder = (attributes & FileAttributes.Directory) != 0;
        if (kind == ChangeKind.Changed && isFolder) return false; // folder timestamps: nothing to update
        var size = isFolder ? 0 : ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;

        var before = index.Version;
        var entry = index.Upsert(path, size, data.ftLastWriteTime.ToLong(), data.ftCreationTime.ToLong(), attributes);
        if (entry < 0) return false;

        // A folder that appeared (new, or moved in) brings its whole subtree with it.
        if (kind == ChangeKind.Created && FileIndexBuilder.CanDescend(attributes, data.dwReserved0) && index.Version != before)
            FileIndexBuilder.ScanSubtree(index, path, entry, token);
        return index.Version != before;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _signal.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
        _signal.Dispose();
    }
}
