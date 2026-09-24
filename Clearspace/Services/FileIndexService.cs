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

    static FileIndexService()
    {
        Overlay.LostChanges += root =>
        {
            RequestRescan(root);
            Report($"{root} index needs updating · other drives remain available");
        };
    }

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
    private static volatile VolumeIndex? _scanningVolume;

    // Read-only diagnostics. The page polls off the UI thread; opening it never starts a scan.
    internal static VolumeIndex? ScanningVolume => _scanningVolume;
    internal static bool HasLiveCoverage(string root) => IsRootLive(root);
    internal static string[] PendingRescans { get { lock (RescanRequests) return [.. RescanRequests]; } }
    internal static long MemoryBudget => MaxIndexBytes;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ScanProblems = new(StringComparer.OrdinalIgnoreCase);
    internal static string? ScanProblem(string root) => ScanProblems.GetValueOrDefault(root);
    internal static string? WorkerProblem { get; private set; }
    private static volatile string? _maintenance;
    internal static string? Maintenance => _maintenance;
    internal static DateTime? RetryAt(string root)
    {
        lock (RescanRequests)
            return RescanRequests.Contains(root) && LastRescan.TryGetValue(root, out var last)
                ? last + MinRescanInterval : null;
    }

    // Explicit requests bypass the automatic cooldown, but still use the single background worker.
    internal static void ScanNow(string root)
    {
        lock (RescanRequests)
        {
            if (string.Equals(_buildingRoot, root, StringComparison.OrdinalIgnoreCase)) return;
            LastRescan.Remove(root);
            RescanRequests.Add(root);
        }
        Wake.Set();
    }

    internal static TimeSpan RescanWait(DateTime now, IEnumerable<DateTime> eligibleTimes)
    {
        var delay = SaveEvery;
        foreach (var time in eligibleTimes)
            if (time - now < delay) delay = time - now;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    private static TimeSpan NextWorkerWait()
    {
        lock (RescanRequests)
            return RescanWait(DateTime.UtcNow, RescanRequests.Select(root =>
                LastRescan.TryGetValue(root, out var last) ? last + MinRescanInterval : DateTime.MinValue));
    }

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

    public static bool IsLive => _watching && Array.Exists(_volumes, volume => IsRootLive(volume.Root));

    private static bool IsRootLive(string root)
    {
        if (!_watching || _watcher?.IsWatching(root) != true || !Overlay.IsHealthy(root)) return false;
        lock (NetworkRoots)
            return !NetworkRoots.Contains(root) || NetworkDrives.IsReady(root);
    }

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
        if (string.IsNullOrWhiteSpace(path))
            return false;

        foreach (var volume in _volumes)
        {
            if (IsUnderAnyRoot(path, [volume.Root]) && IsRootLive(volume.Root))
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

        // NEW (round 50): network drives, when the user has turned indexing them on.
        NetworkDrives.Changed += OnNetworkDrivesChanged;
        if (NetworkDrives.Enabled) NetworkDrives.Start();
    }

    // NEW (round 50): a network drive came within reach (or went): index what is new, and let views
    // update their drive lists.
    private static void OnNetworkDrivesChanged()
    {
        // A network drive indexed earlier (loaded while out of reach) gets its live updates once it answers.
        // This runs on the probe's thread, never the UI's: starting to watch a share talks to it.
        foreach (var volume in _volumes)
            if (NetworkDrives.IsReady(volume.Root) && _watcher is { } watcher && !watcher.IsWatching(volume.Root))
                watcher.Watch(volume.Root);
        IndexNewDrives();
        Raise();
    }

    private static int _lookForNewDrives;

    /// <summary>NEW (round 50): index any drive that has become indexable and has no index yet.</summary>
    internal static void IndexNewDrives()
    {
        Interlocked.Exchange(ref _lookForNewDrives, 1);
        Wake.Set();
    }

    /// <summary>NEW (round 50): network indexing was turned off - drop those drives' indexes. The index
    /// thread does it (it is the only writer of the drive list, so nothing it adds meanwhile is lost);
    /// a network drive it is reading right now is abandoned first.</summary>
    internal static void DropNetworkDrives()
    {
        Interlocked.Exchange(ref _dropNetwork, 1);
        if (_buildingRoot is { } root && IsNetwork(root))
            try { _driveBuild?.Cancel(); } catch (ObjectDisposedException) { }   // that drive just finished
        Wake.Set();
    }

    private static int _dropNetwork;
    private static volatile string? _buildingRoot;
    private static CancellationTokenSource? _driveBuild;
    // Drives known to be network drives when they were indexed or loaded - still recognised after their
    // mapping has been removed, when Windows no longer says what they were.
    private static readonly HashSet<string> NetworkRoots = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsNetwork(string root)
    {
        lock (NetworkRoots) if (NetworkRoots.Contains(root)) return true;
        if (!NetworkDrives.IsNetworkRoot(root)) return false;
        lock (NetworkRoots) NetworkRoots.Add(root);
        return true;
    }

    // On the index thread.
    private static void DropNetworkVolumes()
    {
        if (Interlocked.Exchange(ref _dropNetwork, 0) == 0 || NetworkDrives.Enabled) return;
        var kept = _volumes.Where(volume => !IsNetwork(volume.Root)).ToArray();
        foreach (var volume in _volumes.Except(kept)) _watcher?.Unwatch(volume.Root);
        if (kept.Length == _volumes.Length) return;
        Publish(kept);
        FileIndexStore.Save(kept);
        Report($"Index ready · {Count:N0} items");
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
            _maintenance = "Loading the saved index from disk";

            var loaded = FileIndexStore.Load();

            // CHANGED: the watcher now patches the index live through the updater.
            _updater = new FileIndexUpdater(() => _volumes);
            _updater.Failed += Overlay.MarkOverflowed;
            _updater.Applied += paths =>
            {
                try { LiveChanged?.Invoke(paths); } catch (Exception) { }
                Raise();
            };
            _watcher = new FileIndexWatcher(Overlay, _updater);

            if (loaded.Count > 0)
            {
                Publish([.. loaded]);
                // Changes while the app was closed were not observed. Serve other healthy
                // volumes normally while each saved volume catches up in the background.
                foreach (var volume in loaded) Overlay.MarkOverflowed(volume.Root);
                // CHANGED (round 50): a network drive is watched only once it answers (OnNetworkDrivesChanged);
                // watching one that is out of reach would wait on the network and mark the index as behind.
                StartWatching(loaded.Select(volume => volume.Root).Where(root => !IsNetwork(root)));
                Report($"Index ready · {Count:N0} items");
            }

            _maintenance = "Starting background scans after a brief startup delay";
            if (WaitHandle.WaitAny([token.WaitHandle, Wake], StartupBuildDelay) == 0)
                return;
            _maintenance = null;

            FileIndexBuilder.EnterBackgroundMode();

            try
            {
                BuildMissing(token, null);

                if (!token.IsCancellationRequested)
                {
                    _maintenance = "Saving the index to disk";
                    FileIndexStore.Save(_volumes);
                    _maintenance = null;
                    Report(Count > 0 ? $"Index ready · {Count:N0} items" : string.Empty);
                }

                // NEW: stay resident for rescan requests and periodic saves of live changes.
                while (!token.IsCancellationRequested)
                {
                    WaitHandle.WaitAny([token.WaitHandle, Wake], NextWorkerWait());
                    if (token.IsCancellationRequested) break;

                    string[] rescans;
                    lock (RescanRequests)
                    {
                        var now = DateTime.UtcNow;
                        rescans = [.. RescanRequests.Where(root =>
                            !LastRescan.TryGetValue(root, out var last) || now - last >= MinRescanInterval)];
                        // Also back off unavailable drives that never reach the builder.
                        foreach (var root in rescans) LastRescan[root] = now;
                    }

                    // NEW (round 50): network drives turned off are dropped; drives that became indexable
                    // since the last pass (a network drive turned on or reconnected) are indexed if they have
                    // no index yet.
                    DropNetworkVolumes();
                    if (Interlocked.Exchange(ref _lookForNewDrives, 0) == 1)
                        BuildMissing(token, null);

                    if (rescans.Length > 0)
                    {
                        BuildMissing(token, rescans);
                        Report($"Index ready · {Count:N0} items");
                    }

                    if (Array.Exists(_volumes, volume => volume.IsDirty))
                    {
                        _maintenance = "Saving the index to disk";
                        FileIndexStore.Save(_volumes);
                        _maintenance = null;
                    }
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
            WorkerProblem = $"The indexing worker stopped: {exception.Message}. Restart Clearspace to retry.";
            Report(string.Empty);
        }
        finally
        {
            _maintenance = null;
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
        var recovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            lock (RescanRequests) LastRescan[root] = DateTime.UtcNow;

            var budget = MaxIndexBytes - EstimatedBytes + (existing?.EstimatedBytes ?? 0);

            if (budget <= 0)
            {
                ScanProblems[root] = "Index memory budget reached. This drive is searched directly.";
                if (!skipped.Contains(root, StringComparer.OrdinalIgnoreCase))
                    skipped.Add(root);
                SkippedRoots = [.. skipped];
                continue;
            }

            var serial = FileIndexBuilder.GetSerialNumber(root);
            var network = IsNetwork(root);   // NEW (round 50)

            IsBuilding = true;
            // CHANGED: an existing index keeps serving searches while it is refreshed.
            var verb = existing is null ? "Indexing" : "Refreshing the index for";
            Report($"{verb} {root}…");

            var recoveryGeneration = Overlay.Generation(root);
            StartWatching([root]);
            _updater?.BeginRecording(root); // NEW

            VolumeIndex? built;
            // NEW (round 50): each drive can be abandoned on its own (a network drive turned off mid-read)
            // without stopping the whole index.
            using var driveBuild = CancellationTokenSource.CreateLinkedTokenSource(token);
            _driveBuild = driveBuild;
            _buildingRoot = root;

            try
            {
                built = FileIndexBuilder.Build(
                    root,
                    serial,
                    budget,
                    count => Report($"{verb} {root}… {count:N0} items"),
                    driveBuild.Token,
                    // NEW: only a first-time index is shown while it fills; a refresh keeps the
                    // complete current index on screen until the new one is ready.
                    started: index => { _scanningVolume = index; if (existing is null) _building = index; });
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // NEW (round 50): only this drive was called off (network indexing turned off).
                _updater?.EndRecording(root);
                _building = null;
                _buildingRoot = null;
                _watcher?.Unwatch(root);
                DropNetworkVolumes();
                continue;
            }
            catch (OperationCanceledException)
            {
                _updater?.EndRecording(root);
                _building = null;
                _buildingRoot = null;
                throw;
            }
            catch (Exception exception)
            {
                _updater?.EndRecording(root);
                _building = null;
                _buildingRoot = null;
                Overlay.MarkOverflowed(root);
                Trace.WriteLine($"Clearspace: could not index {root}. {exception.Message}");
                ScanProblems[root] = exception.Message;
                continue;
            }
            finally { _scanningVolume = null; }

            // NEW: apply what changed during the scan, so nothing is lost in the swap.
            _building = null;
            _buildingRoot = null;
            // NEW (round 50): network indexing turned off while this drive was being read: do not keep it.
            if (network && !NetworkDrives.Enabled)
            {
                _updater?.EndRecording(root);
                _watcher?.Unwatch(root);
                DropNetworkVolumes();
                continue;
            }
            if (built is null)
            {
                ScanProblems[root] = "Too large for the index memory budget. This drive is searched directly.";
                _updater?.EndRecording(root);
                Overlay.MarkOverflowed(root);
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

            try
            {
                if (_updater is { } updater) updater.PublishReplacement(built, () => Publish(updated), token);
                else Publish(updated);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                Overlay.MarkOverflowed(root);
                Trace.WriteLine($"Clearspace: could not replay changes for {root}. {exception.Message}");
                ScanProblems[root] = exception.Message;
                continue;
            }
            if (_watcher?.IsWatching(root) == true && Overlay.TryRecover(root, recoveryGeneration))
            {
                recovered.Add(root);
                lock (RescanRequests) RescanRequests.Remove(root);
            }
            else
                RequestRescan(root);
            skipped.RemoveAll(path => path.Equals(root, StringComparison.OrdinalIgnoreCase));
            ScanProblems.TryRemove(root, out _);
            SkippedRoots = [.. skipped];
            Report($"Index ready · {Count:N0} items");
        }

        // Offline, failed and budget-limited drives keep their pending recovery request.
        if (forced is not null)
            lock (RescanRequests)
                foreach (var root in forced)
                    if (!recovered.Contains(root)) RescanRequests.Add(root);
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
    // CHANGED (round 50): plus mapped network drives that answered their last check, when network indexing
    // is on. Their readiness comes from NetworkDrives, never from asking the server here.
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
                if (drive.DriveType == DriveType.Network)
                {
                    if (!NetworkDrives.IsReady(drive.RootDirectory.FullName)) continue;   // CHANGED (round 50)
                }
                else if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
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

            if (!IsRootLive(volume.Root)) continue;

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
            // NEW (round 50): on a network drive "not found" is also what a slow or dropped connection
            // says, and marking every hit deleted would hide them until the next rescan. The watcher and
            // rescans keep a network drive's index honest instead.
            if (NetworkDrives.Enabled && item.FullPath.Length >= 3 && IsNetwork(item.FullPath[..3]))
                continue;
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
