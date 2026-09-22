// Clearspace | File-index lifecycle and queries.

using System.Diagnostics;
using System.IO;
using Clearspace.Models;

namespace Clearspace.Services;

public static class FileIndexService
{
    private static volatile VolumeIndex[] _volumes = [];

    private static Thread? _worker;
    private static CancellationTokenSource? _cancellation;
    private static readonly Lock StartGate = new();

    private static readonly IndexOverlay Overlay = new();

    private static FileIndexWatcher? _watcher;
    private static bool _watching;

    private static readonly long MaxIndexBytes = ResolveMemoryBudget();

    private static long ResolveMemoryBudget()
    {
        const long Minimum = 512L * 1024 * 1024;
        const long Maximum = 2048L * 1024 * 1024;

        try
        {
            var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

            return available <= 0
                ? Minimum * 2
                : Math.Clamp(available / 8, Minimum, Maximum);
        }
        catch (Exception)
        {
            return Minimum * 2;
        }
    }

    private static readonly TimeSpan RebuildAfter = TimeSpan.FromHours(1);

    private static readonly TimeSpan StartupBuildDelay = TimeSpan.FromSeconds(10);

    public static event EventHandler? Changed;

    public static bool IsBuilding { get; private set; }

    public static bool IsLive => _volumes.Length > 0 && _watching && !Overlay.Overflowed;

    public static int PendingChanges => Overlay.Count;

    public static string Status { get; private set; } = string.Empty;

    public static int Count
    {
        get
        {
            var total = 0;
            foreach (var volume in _volumes)
                total += volume.Count;
            return total;
        }
    }

    public static long EstimatedBytes
    {
        get
        {
            var total = 0L;
            foreach (var volume in _volumes)
                total += volume.EstimatedBytes;
            return total;
        }
    }

    public static IReadOnlyList<string> SkippedRoots { get; private set; } = [];

    // A shallow copy pins immutable, published volumes; the disk usage view does not
    // mix newer overlay events into older parent/size data or change the search index.
    internal static VolumeIndex[] CaptureVolumes() => [.. _volumes];

    public static bool Covers(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsLive)
            return false;

        foreach (var volume in _volumes)
        {
            if (path.StartsWith(volume.Root, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static void Start()
    {
        lock (StartGate)
        {
            if (_worker is not null)
                return;

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;

            _worker = new Thread(() => Run(token))
            {
                IsBackground = true,
                Name = "Clearspace index",
                Priority = ThreadPriority.Lowest
            };

            _worker.Start();
        }
    }

    public static void Stop()
    {
        try
        {
            _cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _watching = false;
        _watcher?.Dispose();
        _watcher = null;

        var volumes = _volumes;

        if (volumes.Length > 0)
            FileIndexStore.Save(volumes);
    }

    private static void Run(CancellationToken token)
    {
        try
        {
            Report("Loading index…");

            var loaded = FileIndexStore.Load();

            _watcher = new FileIndexWatcher(Overlay);
            _watcher.Desynchronised += (_, _) =>
                Report("Index out of date · searching by crawling until it rebuilds");

            if (loaded.Count > 0)
            {
                Publish([.. loaded]);
                StartWatching(loaded.Select(volume => volume.Root));
                Report($"Index ready · {Count:N0} items");
            }

            if (token.WaitHandle.WaitOne(StartupBuildDelay))
                return;

            FileIndexBuilder.EnterBackgroundMode();

            try
            {
                BuildMissing(token);
            }
            finally
            {
                FileIndexBuilder.ExitBackgroundMode();
            }

            if (!token.IsCancellationRequested)
            {
                FileIndexStore.Save(_volumes);
                Report(Count > 0 ? $"Index ready · {Count:N0} items" : string.Empty);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Clearspace: file index failed. {exception}");
            Report(string.Empty);
        }
        finally
        {
            IsBuilding = false;
            Raise();
        }
    }

    private static void BuildMissing(CancellationToken token)
    {
        var skipped = new List<string>(SkippedRoots);

        foreach (var root in EnumerateIndexableRoots())
        {
            token.ThrowIfCancellationRequested();

            var existing = Array.Find(
                _volumes,
                volume => volume.Root.Equals(root, StringComparison.OrdinalIgnoreCase));

            if (existing is not null && DateTime.UtcNow - existing.BuiltUtc < RebuildAfter)
                continue;

            var budget = MaxIndexBytes - EstimatedBytes + (existing?.EstimatedBytes ?? 0);

            if (budget <= 0)
            {
                if (!skipped.Contains(root, StringComparer.OrdinalIgnoreCase))
                    skipped.Add(root);

                continue;
            }

            var serial = FileIndexBuilder.GetSerialNumber(root);

            IsBuilding = true;
            Report($"Indexing {root}…");

            StartWatching([root]);

            VolumeIndex? built;

            try
            {
                built = FileIndexBuilder.Build(
                    root,
                    serial,
                    budget,
                    count => Report($"Indexing {root}… {count:N0} items"),
                    token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"Clearspace: could not index {root}. {exception.Message}");
                continue;
            }

            if (built is null)
            {
                if (!skipped.Contains(root, StringComparer.OrdinalIgnoreCase))
                    skipped.Add(root);

                SkippedRoots = [.. skipped];
                Report($"{root} is too large to index · still searched by crawling");
                continue;
            }

            var updated = _volumes
                .Where(volume => !volume.Root.Equals(root, StringComparison.OrdinalIgnoreCase))
                .Append(built)
                .ToArray();

            Publish(updated);
            SkippedRoots = [.. skipped];
            Report($"Index ready · {Count:N0} items");
        }

        IsBuilding = false;
    }

    private static void StartWatching(IEnumerable<string> roots)
    {
        if (_watcher is null)
            return;

        foreach (var root in roots)
            _watcher.Watch(root);

        _watching = true;
    }

    private static IEnumerable<string> EnumerateIndexableRoots()
    {
        DriveInfo[] drives;

        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            string root;

            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                    continue;

                root = drive.RootDirectory.FullName;
            }
            catch (Exception)
            {
                continue;
            }

            yield return root;
        }
    }

    public static IReadOnlyList<FileSystemItem> Search(
        SearchQuery query,
        IReadOnlyList<string> roots,
        bool showHidden,
        int limit,
        CancellationToken token)
    {
        var volumes = _volumes;

        if (volumes.Length == 0 || limit <= 0)
            return [];

        var terms = query.Terms;

        if (terms.Count == 0)
            return [];

        var folded = new string[terms.Count];

        for (var i = 0; i < terms.Count; i++)
            folded[i] = VolumeIndex.Fold(terms[i]);

        var scanLimit = Math.Min(limit * 4, 200_000);
        var results = new List<FileSystemItem>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var volume in volumes)
        {
            if (token.IsCancellationRequested || results.Count >= limit)
                break;

            var hits = volume.Search(folded, showHidden, false, false, scanLimit, token);

            foreach (var hit in hits)
            {
                if (results.Count >= limit)
                    break;

                var path = volume.GetPath(hit);

                if (path.Length == 0)
                    continue;

                if (!IsUnderAnyRoot(path, roots))
                    continue;

                if (Overlay.IsRemoved(path))
                    continue;

                if (!seen.Add(path))
                    continue;

                var item = Materialise(volume, hit, path);

                if (item is null || !query.Matches(item))
                    continue;

                results.Add(item);
            }
        }

        if (results.Count < limit && !token.IsCancellationRequested)
        {
            var recent = new List<FileSystemItem>();
            Overlay.CollectMatches(folded, showHidden, limit - results.Count, recent);

            foreach (var item in recent)
            {
                if (!IsUnderAnyRoot(item.FullPath, roots))
                    continue;

                if (!seen.Add(item.FullPath) || !query.Matches(item))
                    continue;

                results.Add(item);
            }
        }

        return results;
    }

    public static IReadOnlyList<FileSystemItem> PruneMissing(IReadOnlyList<FileSystemItem> items)
    {
        var missing = new List<FileSystemItem>();

        foreach (var item in items)
        {
            try
            {
                if (item.IsFolder ? Directory.Exists(item.FullPath) : File.Exists(item.FullPath))
                    continue;
            }
            catch (Exception)
            {
                continue;
            }

            missing.Add(item);
            Overlay.OnDeleted(item.FullPath);
        }

        return missing;
    }

    private static bool IsUnderAnyRoot(string path, IReadOnlyList<string> roots)
    {
        for (var i = 0; i < roots.Count; i++)
        {
            var root = roots[i];

            if (root.Length == 0 || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;

            if (path.Length == root.Length ||
                root.EndsWith(Path.DirectorySeparatorChar) ||
                path[root.Length] == Path.DirectorySeparatorChar)
            {
                return true;
            }
        }

        return false;
    }

    private static FileSystemItem? Materialise(VolumeIndex volume, int index, string path)
    {
        try
        {
            var entry = volume.Entry(index);

            return new FileSystemItem
            {
                Name = volume.GetName(index),
                FullPath = path,
                Attributes = entry.Attributes,
                Size = entry.Size,
                DateModified = ToDateTime(entry.ModifiedTicks),
                DateCreated = ToDateTime(entry.CreatedTicks),
                IsInCloudRoot = CloudStorageService.IsCloudPath(path)
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTime ToDateTime(long fileTime)
    {
        if (fileTime <= 0)
            return DateTime.MinValue;

        try
        {
            return DateTime.FromFileTime(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    private static void Publish(VolumeIndex[] volumes)
    {
        _volumes = volumes;
        Raise();
    }

    private static void Report(string status)
    {
        Status = status;
        Raise();
    }

    private static void Raise()
    {
        try
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
        catch (Exception)
        {
        }
    }
}
