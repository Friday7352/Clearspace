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

    // NEW (live index): applies watcher events to the published volumes as they happen.
    private static FileIndexUpdater? _updater;
    private static readonly AutoResetEvent Wake = new(false);
    private static readonly HashSet<string> RescanRequests = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> LastRescan = new(StringComparer.OrdinalIgnoreCase);
    // Heavy disk activity (installs, builds) can overflow the watcher repeatedly; rescan a drive
    // at most this often and keep serving the current index in between.
    private static readonly TimeSpan MinRescanInterval = TimeSpan.FromMinutes(20);

    // NEW: the drive currently being indexed for the first time (readable while it fills).
    private static volatile VolumeIndex? _building;

    // NEW: published volumes plus a first-time index still being built, for the disk usage view.
    internal static VolumeIndex[] CaptureVolumesForDiskUsage()
    {
        var volumes = _volumes;
        var building = _building;
        if (building is null || Array.Exists(volumes, v => v.Root.Equals(building.Root, StringComparison.OrdinalIgnoreCase)))
            return [.. volumes];
        return [.. volumes, building];
    }

    internal static bool IsPartial(VolumeIndex? volume) => volume is not null && ReferenceEquals(volume, _building);

    // NEW: raised (on a background thread) with the paths a batch of live changes touched.
    internal static event Action<IReadOnlyList<string>>? LiveChanged;

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

    // CHANGED: the saved index is kept up to date live, so it is no longer rebuilt every hour.
    // A quiet background catch-up scan (to pick up changes made while Clearspace was closed)
    // runs at most once a day; the current index stays in use until it finishes.
    private static readonly TimeSpan RebuildAfter = TimeSpan.FromHours(24);

    // NEW: live changes are saved periodically, not only on exit.
    private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(5);

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
        _updater?.Dispose(); // NEW
        _updater = null;

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

            // CHANGED: the watcher now patches the index live through the updater.
            _updater = new FileIndexUpdater(() => _volumes);
            _updater.Applied += paths =>
            {
                try { LiveChanged?.Invoke(paths); } catch (Exception) { }
                Raise();
            };
            _watcher = new FileIndexWatcher(Overlay, _updater);
            _watcher.Desynchronised += (_, root) =>
            {
                Report("Some changes were missed · rescanning in the background");
                RequestRescan(root); // NEW: recover in the background instead of waiting for a restart
            };

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
                BuildMissing(token, null);

                if (!token.IsCancellationRequested)
                {
                    FileIndexStore.Save(_volumes);
                    Report(Count > 0 ? $"Index ready · {Count:N0} items" : string.Empty);
                }

                // NEW: stay resident for rescan requests and periodic saves of live changes.
                while (!token.IsCancellationRequested)
                {
                    WaitHandle.WaitAny([token.WaitHandle, Wake], SaveEvery);
                    if (token.IsCancellationRequested) break;

                    string[] rescans;
                    lock (RescanRequests)
                    {
                        var now = DateTime.UtcNow;
                        rescans = [.. RescanRequests.Where(root =>
                            !LastRescan.TryGetValue(root, out var last) || now - last >= MinRescanInterval)];
                        foreach (var root in rescans)
                        {
                            RescanRequests.Remove(root);
                            LastRescan[root] = now;
                        }
                    }

                    if (rescans.Length > 0)
                    {
                        BuildMissing(token, rescans);
                        Overlay.Clear();
                        Report($"Index ready · {Count:N0} items");
                    }

                    if (Array.Exists(_volumes, volume => volume.IsDirty))
                        FileIndexStore.Save(_volumes);
                }
            }
            finally
            {
                FileIndexBuilder.ExitBackgroundMode();
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

    // NEW: ask the background thread to rescan a drive (e.g. after the watcher lost events).
    internal static void RequestRescan(string root)
    {
        lock (RescanRequests) RescanRequests.Add(root);
        Wake.Set();
    }

    // CHANGED: `forced` limits the pass to drives that must be rescanned; otherwise only drives
    // with no index, or whose last full scan is older than RebuildAfter, are scanned. While a
    // drive is scanned its live changes are recorded and replayed onto the new index.
    private static void BuildMissing(CancellationToken token, IReadOnlyCollection<string>? forced)
    {
        var skipped = new List<string>(SkippedRoots);

        foreach (var root in EnumerateIndexableRoots())
        {
            token.ThrowIfCancellationRequested();

            var existing = Array.Find(
                _volumes,
                volume => volume.Root.Equals(root, StringComparison.OrdinalIgnoreCase));

            var force = forced?.Contains(root, StringComparer.OrdinalIgnoreCase) == true;
            if (forced is not null && !force)
                continue;

            if (!force && existing is not null && DateTime.UtcNow - existing.BuiltUtc < RebuildAfter)
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
            // CHANGED: an existing index keeps serving searches while it is refreshed.
            var verb = existing is null ? "Indexing" : "Refreshing the index for";
            Report($"{verb} {root}…");

            StartWatching([root]);
            _updater?.BeginRecording(root); // NEW

            VolumeIndex? built;

            try
            {
                built = FileIndexBuilder.Build(
                    root,
                    serial,
                    budget,
                    count => Report($"{verb} {root}… {count:N0} items"),
                    token,
                    // NEW: only a first-time index is shown while it fills; a refresh keeps the
                    // complete current index on screen until the new one is ready.
                    started: index => { if (existing is null) _building = index; });
            }
            catch (OperationCanceledException)
            {
                _updater?.EndRecording(root);
                _building = null;
                throw;
            }
            catch (Exception exception)
            {
                _updater?.EndRecording(root);
                _building = null;
                Trace.WriteLine($"Clearspace: could not index {root}. {exception.Message}");
                continue;
            }

            // NEW: apply what changed during the scan, so nothing is lost in the swap.
            var during = _updater?.EndRecording(root) ?? [];
            _building = null;
            if (built is not null && during.Count > 0)
                FileIndexUpdater.Replay(built, during, token);

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

    // NEW: every drive Clearspace indexes (fixed and ready), whether or not it is indexed yet.
    internal static string[] IndexableRoots() => [.. EnumerateIndexableRoots()];

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
