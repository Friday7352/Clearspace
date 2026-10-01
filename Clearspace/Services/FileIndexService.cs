// Clearspace | File-index lifecycle and queries.

using System.Diagnostics;
using System.IO;
using Clearspace.Journal;
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
            // CHANGED (journal catch-up): the request is answered from the change journal when possible.
            RequestRescan(root);
            Report($"{root} index needs updating · other drives remain available");
        };
    }

    private static FileIndexWatcher? _watcher;
    private static bool _watching;

    // NEW (journal catch-up): per drive, where its change journal stood. Checkpoint = the saved index
    // reflects every change before it. Pending = a later position that becomes the checkpoint at the
    // next save if the drive stayed fully watched in between (so events still queued at the moment it
    // was read have long been applied by then). See JournalCheckpoints.
    private sealed class JournalCursor
    {
        public uint Serial;
        public ulong JournalId;
        public long Checkpoint;
        public long Pending;
        public DateTime PendingUtc; // NEW (checkpoint fix): when Pending was read
    }

    // NEW (checkpoint fix): a Pending position is only promoted once it is this old - long enough for
    // every change made before it to have been delivered by the watcher - and once the updater has applied
    // everything queued up to now. Closing Clearspace sooner simply keeps the previous checkpoint.
    private static readonly TimeSpan PendingSettle = TimeSpan.FromSeconds(10);

    private static string[] PromotableRoots(VolumeIndex[] volumes)
    {
        var now = DateTime.UtcNow;
        var candidates = new List<string>();

        foreach (var volume in volumes)
        {
            if (!IsRootLive(volume.Root)) continue;
            lock (Cursors)
                if (Cursors.TryGetValue(volume.Root, out var cursor) && cursor.Pending > cursor.Checkpoint && now - cursor.PendingUtc >= PendingSettle)
                    candidates.Add(volume.Root);
        }

        if (candidates.Count == 0 || _updater is not { } updater)
            return [];

        // Everything delivered so far (which includes every change older than PendingSettle) must be applied
        // before the index is written; the save that follows then contains it.
        if (!updater.WaitForApplied(updater.Enqueued, TimeSpan.FromSeconds(2)))
            return [];

        return [.. candidates.Where(IsRootLive)];
    }

    private static readonly Dictionary<string, JournalCursor> Cursors = new(StringComparer.OrdinalIgnoreCase);
    // NEW: drives the person asked to rescan in full ("Scan now"); a journal catch-up does not replace that.
    private static readonly HashSet<string> ForcedScans = new(StringComparer.OrdinalIgnoreCase);

    internal static bool HasJournalPosition(string root)
    {
        lock (Cursors) return Cursors.TryGetValue(root, out var cursor) && cursor.Checkpoint >= 0;
    }

    // NEW: "Catch up now" on the Indexing page. Uses the journal when it can, otherwise a full scan.
    internal static void CatchUpNow(string root)
    {
        IndexActivity.Pending(root, "Catch-up requested from the Indexing page");
        lock (RescanRequests)
        {
            LastRescan.Remove(root);
            RescanRequests.Add(root);
        }
        Wake.Set();
    }

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
            ForcedScans.Add(root); // NEW: a full scan, even when the journal could catch up
        }
        IndexActivity.Pending(root, "Full scan requested from the Indexing page");
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

        // NEW (journal catch-up): which drives were fully watched, and which positions are safe to keep,
        // before the watcher and updater go away (the updater drops anything still queued).
        var volumes = _volumes;
        var watched = volumes.Where(volume => IsRootLive(volume.Root)).Select(volume => volume.Root).ToArray();
        var promotable = PromotableRoots(volumes);

        _watching = false;
        _watcher?.Dispose();
        _watcher = null;
        _updater?.Dispose(); // NEW
        _updater = null;

        if (volumes.Length > 0 && FileIndexStore.Save(volumes))
            SaveJournalPositions(volumes, watched, promotable, queryNext: false);
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
                IndexActivity.LiveApplied(paths); // NEW: counts live changes per drive for the Indexing page
                Raise();
            };
            _watcher = new FileIndexWatcher(Overlay, _updater);

            if (loaded.Count > 0)
            {
                Publish([.. loaded]);
                LoadJournalPositions(loaded); // NEW (journal catch-up)
                // Changes while the app was closed were not observed. Serve other healthy
                // volumes normally while each saved volume catches up in the background.
                foreach (var volume in loaded)
                {
                    IndexActivity.Pending(volume.Root, "Checking for changes made while Clearspace was closed");
                    Overlay.MarkOverflowed(volume.Root);
                }
                // CHANGED (round 50): a network drive is watched only once it answers (OnNetworkDrivesChanged);
                // watching one that is out of reach would wait on the network and mark the index as behind.
                StartWatching(loaded.Select(volume => volume.Root).Where(root => !IsNetwork(root)));
                Report($"Index ready · {Count:N0} items");

                // NEW (journal catch-up): read only what changed while Clearspace was closed, straight away.
                // Drives that cannot be caught up keep their rescan request for after the startup delay.
                CatchUpPending(token);
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
                    SaveIndex(); // CHANGED: also records change-journal positions
                    Report(Count > 0 ? $"Index ready · {Count:N0} items" : string.Empty);
                    VerifyFileTables(token); // NEW
                }

                // NEW: stay resident for rescan requests and periodic saves of live changes.
                while (!token.IsCancellationRequested)
                {
                    WaitHandle.WaitAny([token.WaitHandle, Wake], NextWorkerWait());
                    if (token.IsCancellationRequested) break;

                    // NEW (journal catch-up): answer missed-change requests from the journal first. This is
                    // not subject to the 20-minute rescan spacing; it reads only what changed.
                    CatchUpPending(token);

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
                        SaveIndex(); // CHANGED: also records change-journal positions

                    VerifyFileTables(token); // NEW: one-time file-table checks, when idle
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
        // NEW: a specific reason recorded earlier (startup, manual request) is kept.
        IndexActivity.Pending(root, "Some changes were missed (the live change watcher overflowed or stopped)", keepExisting: true);
        lock (RescanRequests) RescanRequests.Add(root);
        Wake.Set();
    }

    // ------------------------------------------------------------------ NEW: journal catch-up

    private static void LoadJournalPositions(IEnumerable<VolumeIndex> loaded)
    {
        var saved = JournalCheckpoints.Load();

        lock (Cursors)
        {
            foreach (var volume in loaded)
            {
                if (!saved.TryGetValue(volume.Root, out var checkpoint))
                    continue;

                // A different serial number means a different (reformatted or swapped) drive.
                if (checkpoint.Serial != volume.SerialNumber)
                {
                    IndexActivity.JournalNote(volume.Root, "The saved journal position belongs to a different drive; one full scan will replace it.");
                    continue;
                }

                Cursors[volume.Root] = new JournalCursor
                {
                    Serial = checkpoint.Serial,
                    JournalId = checkpoint.JournalId,
                    Checkpoint = checkpoint.Usn,
                    Pending = checkpoint.Usn
                };
            }
        }
    }

    // Tries a journal catch-up for every drive waiting for an update, except explicit full scans.
    private static void CatchUpPending(CancellationToken token)
    {
        string[] pending;
        lock (RescanRequests) pending = [.. RescanRequests.Where(root => !ForcedScans.Contains(root))];
        if (pending.Length == 0) return;

        var access = IndexJournal.Access();

        if (access is not (JournalAccess.Direct or JournalAccess.Helper))
        {
            foreach (var root in pending)
                IndexActivity.JournalNote(root, IndexJournal.Describe(access));
            return;
        }

        foreach (var root in pending)
        {
            token.ThrowIfCancellationRequested();
            TryCatchUp(root, access, token);
        }

        _maintenance = null;
    }

    private static bool TryCatchUp(string root, JournalAccess access, CancellationToken token)
    {
        var volume = Array.Find(_volumes, candidate => candidate.Root.Equals(root, StringComparison.OrdinalIgnoreCase));

        if (volume is null || IsNetwork(root) || string.Equals(_buildingRoot, root, StringComparison.OrdinalIgnoreCase))
            return false;

        JournalCursor? cursor;
        lock (Cursors) Cursors.TryGetValue(root, out cursor);

        if (cursor is null || cursor.Checkpoint < 0 || cursor.Serial != volume.SerialNumber)
        {
            IndexActivity.JournalNote(root, "No saved journal position yet. One full scan (or a few minutes of watching) records it; later updates read only the changes.");
            return false;
        }

        // Watch first, then read: anything that changes during the catch-up is caught by the watcher.
        StartWatching([root]);
        if (_watcher?.IsWatching(root) != true) return false;

        var generation = Overlay.Generation(root);
        var started = Stopwatch.StartNew();
        _maintenance = $"Catching up {root} from the change journal";
        Report($"Catching up {root}…");

        var response = IndexJournal.CatchUp(root, cursor.JournalId, cursor.Checkpoint, token);

        if (response.Status != JournalProtocol.Ok)
        {
            var reason = response.Message ?? $"The change journal could not be used ({response.Status}).";

            // These mean the saved position can never be used again; the full scan records a new one.
            if (response.Status is JournalProtocol.Reset or JournalProtocol.HistoryLost or JournalProtocol.TooMany or JournalProtocol.Unsupported)
            {
                lock (Cursors) Cursors.Remove(root);
                reason += " A full scan will run instead.";
            }

            IndexActivity.CatchUpFailed(root, reason);
            return false;
        }

        try
        {
            var changes = response.Paths.Select(path => (FileIndexUpdater.ChangeKind.Created, path)).ToList();
            FileIndexUpdater.Replay(volume, changes, token); // re-reads each path; missing ones are removed
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            IndexActivity.CatchUpFailed(root, $"Applying journal changes failed: {exception.Message}. A full scan will run instead.");
            return false;
        }

        if (!Overlay.TryRecover(root, generation))
        {
            IndexActivity.CatchUpFailed(root, "More changes were missed while catching up; trying again.");
            return false;
        }

        lock (RescanRequests) RescanRequests.Remove(root);
        lock (Cursors)
        {
            cursor.JournalId = response.JournalId;
            cursor.Checkpoint = response.NextUsn;
            cursor.Pending = response.NextUsn;
        }

        IndexActivity.CatchUpFinished(root, access == JournalAccess.Direct ? "read directly" : "via the helper service",
            response.Records, response.Paths.Count, response.Unresolved, started.Elapsed);
        Report($"Index ready · {Count:N0} items");
        return true;
    }

    // The journal's current position for a drive about to be scanned in full, or null.
    private static (ulong JournalId, long Usn)? JournalStart(string root, CancellationToken token)
    {
        if (IsNetwork(root) || IndexJournal.Access() is not (JournalAccess.Direct or JournalAccess.Helper))
            return null;

        var response = IndexJournal.Query(root, token);
        return response.Status == JournalProtocol.Ok ? (response.JournalId, response.NextUsn) : null;
    }

    private static void SaveIndex()
    {
        _maintenance = "Saving the index to disk";
        var volumes = _volumes;
        var watched = volumes.Where(volume => IsRootLive(volume.Root)).Select(volume => volume.Root).ToArray();
        var promotable = PromotableRoots(volumes); // FIXED: decided before writing, so the file contains those changes
        var started = Stopwatch.StartNew();

        if (FileIndexStore.Save(volumes))
        {
            SaveJournalPositions(volumes, watched, promotable, queryNext: true);
            IndexActivity.Record(null, IndexEventKind.Save, $"Saved the index in {DriveActivity.Seconds(started.Elapsed)}");
        }
        else
            IndexActivity.Record(null, IndexEventKind.Problem, "The index could not be saved; it will be retried.");

        _maintenance = null;
    }

    // Called only after index.db was written. Promotes Pending to Checkpoint for drives that stayed
    // fully watched, then (unless exiting) reads each journal's current position as the next Pending.
    // A fully watched drive without a position (the helper was just installed) is adopted here: it gets
    // a Pending position now and a usable Checkpoint at the following save - no full scan needed.
    private static void SaveJournalPositions(VolumeIndex[] volumes, IReadOnlyCollection<string> watched,
        IReadOnlyCollection<string> promotable, bool queryNext)
    {
        var checkpoints = new List<JournalCheckpoint>();
        var canQuery = queryNext && IndexJournal.Access() is JournalAccess.Direct or JournalAccess.Helper;

        foreach (var volume in volumes)
        {
            var root = volume.Root;
            var isWatched = watched.Contains(root, StringComparer.OrdinalIgnoreCase);
            JournalCursor? cursor;
            lock (Cursors) Cursors.TryGetValue(root, out cursor);

            if (cursor is null && (!isWatched || !canQuery || IsNetwork(root) || !UsnJournal.IsDriveRoot(root)))
                continue;

            // FIXED: only positions whose earlier changes are known to be applied and saved (PromotableRoots).
            if (cursor is not null && promotable.Contains(root, StringComparer.OrdinalIgnoreCase))
                lock (Cursors) cursor.Checkpoint = Math.Max(cursor.Checkpoint, cursor.Pending);

            if (isWatched && canQuery)
            {
                var response = IndexJournal.Query(root, CancellationToken.None);

                lock (Cursors)
                {
                    if (response.Status != JournalProtocol.Ok)
                    {
                        // Keep what we have; a drive without a position simply is not adopted yet.
                    }
                    else if (cursor is null)
                    {
                        cursor = new JournalCursor { Serial = volume.SerialNumber, JournalId = response.JournalId, Checkpoint = -1, Pending = response.NextUsn, PendingUtc = DateTime.UtcNow };
                        Cursors[root] = cursor;
                        IndexActivity.JournalNote(root, "Journal position recorded; from the next save on, this drive is caught up instead of rescanned.");
                    }
                    else if (response.JournalId == cursor.JournalId)
                    {
                        // Keep an older, not yet promoted position rather than moving the goalposts every save.
                        if (cursor.Pending <= cursor.Checkpoint)
                        {
                            cursor.Pending = response.NextUsn;
                            cursor.PendingUtc = DateTime.UtcNow;
                        }
                    }
                    else
                    {
                        Cursors.Remove(root); // the journal was recreated: its old positions mean nothing
                        continue;
                    }
                }
            }

            if (cursor is { Checkpoint: >= 0 })
                checkpoints.Add(new JournalCheckpoint(root, cursor.Serial, cursor.JournalId, cursor.Checkpoint, DateTime.UtcNow));
        }

        JournalCheckpoints.Save(checkpoints);
    }

    // ------------------------------------------------------------------ NEW: file-table scan

    private static readonly Dictionary<string, (uint Serial, long Budget, FileTableIndex.Summary Summary)> Verifications =
        new(StringComparer.OrdinalIgnoreCase);

    private static VolumeIndex? TryFileTableScan(string root, uint serial, long budget, CancellationToken token,
        out FileTableIndex.Summary? summary)
    {
        summary = null;

        if (!UsnJournal.IsDriveRoot(root) || FileTableTrust.IsDisabled(root, serial) ||
            IndexJournal.Access() is not (JournalAccess.Direct or JournalAccess.Helper))
            return null;

        _maintenance = $"Reading the file table of {root}";
        Report($"Reading the file table of {root}…");
        var started = Stopwatch.StartNew();

        try
        {
            var (header, table) = IndexJournal.ScanVolume(root, token);

            if (table is null)
            {
                IndexActivity.Record(root, header.Status == JournalProtocol.Unsupported ? IndexEventKind.Info : IndexEventKind.Problem,
                    $"File-table scan not used ({header.Message ?? header.Status}). Walking folders instead.");
                return null;
            }

            if (FileTableIndex.EstimatedBytes(table.Count, table.NameChars) > budget)
            {
                IndexActivity.Record(root, IndexEventKind.Problem, $"{table.Count:N0} entries exceed the index memory budget.");
                return null;
            }

            var index = FileTableIndex.ToVolumeIndex(table, root, serial);
            summary = FileTableIndex.Summarize(index);
            IndexActivity.Record(root, IndexEventKind.FullScan,
                $"Read the file table · {index.Count:N0} entries from {header.Records:N0} records in {DriveActivity.Seconds(started.Elapsed)}" +
                (header.DeniedFolders > 0 ? $" · {header.DeniedFolders:N0} folders this account cannot open were listed but not entered" : ""));
            return index;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            IndexActivity.Record(root, IndexEventKind.Problem, $"File-table scan failed ({exception.Message}). Walking folders instead.");
            return null;
        }
        finally
        {
            _maintenance = null;
        }
    }

    // One-time check per drive: a normal folder walk must agree with the file-table scan (allowing for
    // files that changed in between). If it does not, that drive goes back to folder walks for good.
    private static void VerifyFileTables(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            string root;
            (uint Serial, long Budget, FileTableIndex.Summary Summary) job;

            lock (Verifications)
            {
                if (Verifications.Count == 0) return;
                (root, job) = Verifications.First();
                Verifications.Remove(root);
            }

            lock (RescanRequests)
                if (RescanRequests.Count > 0) { lock (Verifications) Verifications.TryAdd(root, job); return; } // real work first

            _maintenance = $"One-time check: comparing {root}'s file-table scan with a folder walk";
            IndexActivity.Record(root, IndexEventKind.Info,
                "One-time check started: comparing the file-table scan with a normal folder walk. Searches already use the new index.");
            VolumeIndex? walked;

            try
            {
                walked = FileIndexBuilder.Build(root, job.Serial, job.Budget, null, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                IndexActivity.Record(root, IndexEventKind.Problem, $"The one-time check could not finish ({exception.Message}); it runs again after the next scan.");
                continue;
            }
            finally
            {
                _maintenance = null;
            }

            if (walked is null)
                continue;

            var fromWalk = FileTableIndex.Summarize(walked);
            walked = null;               // NEW (memory): the walk was only needed for its totals
            MemoryRelief.Release();

            if (FileTableIndex.Agree(job.Summary, fromWalk, out var detail))
            {
                FileTableTrust.Set(new FileTableState(root, job.Serial, Verified: true, Disabled: false, detail, DateTime.UtcNow));
                IndexActivity.Record(root, IndexEventKind.Info, $"File-table scan verified · {detail}");
            }
            else
            {
                FileTableTrust.Set(new FileTableState(root, job.Serial, Verified: false, Disabled: true, detail, DateTime.UtcNow));
                IndexActivity.Record(root, IndexEventKind.Problem,
                    $"The file-table scan disagreed with a folder walk, so {root} goes back to folder walks · {detail}");
                ScanNow(root); // rebuild from a walk
            }
        }
    }

    // CHANGED: `forced` limits the pass to drives that must be rescanned; otherwise only drives
    // with no index, or whose last full scan is older than RebuildAfter, are scanned. While a
    // drive is scanned its live changes are recorded and replayed onto the new index.
    private static void BuildMissing(CancellationToken token, IReadOnlyCollection<string>? forced)
    {
        var skipped = new List<string>(SkippedRoots);
        var recovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var published = 0; // NEW (memory)

        foreach (var root in EnumerateIndexableRoots())
        {
            token.ThrowIfCancellationRequested();

            var existing = Array.Find(
                _volumes,
                volume => volume.Root.Equals(root, StringComparison.OrdinalIgnoreCase));

            var force = forced?.Contains(root, StringComparer.OrdinalIgnoreCase) == true;
            if (forced is not null && !force)
                continue;

            // CHANGED (journal catch-up): a drive kept current by its change journal needs no daily rebuild.
            if (!force && existing is not null && (DateTime.UtcNow - existing.BuiltUtc < RebuildAfter || HasJournalPosition(root)))
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

            // NEW (journal catch-up): the journal position before reading the drive. Changes made during
            // the scan are replayed from the recording; later ones are read from here next time.
            var journalStart = JournalStart(root, token);
            IndexActivity.FullScanStarted(root, IndexActivity.For(root).PendingReason
                ?? (existing is null ? "First index of this drive" : force ? "Requested" : "Daily refresh (no change journal)"));

            VolumeIndex? built;
            // NEW (round 50): each drive can be abandoned on its own (a network drive turned off mid-read)
            // without stopping the whole index.
            using var driveBuild = CancellationTokenSource.CreateLinkedTokenSource(token);
            _driveBuild = driveBuild;
            _buildingRoot = root;

            FileTableIndex.Summary? tableSummary = null;

            try
            {
                // NEW (instant first index): read the drive's file table through the index helper; it takes
                // seconds instead of minutes. Anything unexpected falls back to the folder walk below.
                built = network ? null : TryFileTableScan(root, serial, budget, driveBuild.Token, out tableSummary);

                built ??= FileIndexBuilder.Build(
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
                IndexActivity.FullScanEnded(root, 0, completed: false, "network indexing was turned off"); // NEW
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
                IndexActivity.FullScanEnded(root, 0, completed: false, exception.Message); // NEW
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
                IndexActivity.FullScanEnded(root, 0, completed: false, "network indexing was turned off"); // NEW
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
                IndexActivity.FullScanEnded(root, 0, completed: false, "too large for the index memory budget"); // NEW
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
                IndexActivity.FullScanEnded(root, 0, completed: false, exception.Message); // NEW
                continue;
            }
            published++;

            if (_watcher?.IsWatching(root) == true && Overlay.TryRecover(root, recoveryGeneration))
            {
                recovered.Add(root);
                lock (RescanRequests)
                {
                    RescanRequests.Remove(root);
                    ForcedScans.Remove(root); // NEW
                }

                // NEW (journal catch-up): from now on this drive can be caught up instead of rescanned.
                lock (Cursors)
                {
                    if (journalStart is { } position)
                        Cursors[root] = new JournalCursor { Serial = serial, JournalId = position.JournalId, Checkpoint = position.Usn, Pending = position.Usn };
                    else
                        Cursors.Remove(root);
                }
                IndexActivity.FullScanEnded(root, built.Count - built.RemovedCount, completed: true);

                // NEW: the first file-table scan of each drive is compared once with a folder walk, later,
                // when the worker has nothing else to do (other drives are indexed first).
                if (tableSummary is { } fromTable && !FileTableTrust.IsVerified(root, serial))
                    lock (Verifications) Verifications[root] = (serial, budget, fromTable);
            }
            else
            {
                IndexActivity.FullScanEnded(root, built.Count - built.RemovedCount, completed: true);
                RequestRescan(root);
            }
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

        // NEW (memory): the indexes these drives replaced are garbage now; give the space back.
        if (published > 0) MemoryRelief.Release();
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

    // CHANGED (search relevance): each live volume is scanned with the whole query and keeps its best
    // `limit` entries by score (IndexSearch); the best across all volumes are then materialised. This
    // replaces "first limit x 4 filenames containing every word", which ranked only whatever the scan
    // happened to reach first. Filter-only queries (ext:pdf, tag:work, is:folder) are answered here
    // too, so a covered drive no longer falls back to crawling for them.
    public static IReadOnlyList<FileSystemItem> Search(
        SearchQuery query,
        IReadOnlyList<string> roots,
        bool showHidden,
        int limit,
        CancellationToken token)
    {
        var volumes = _volumes;

        if (volumes.Length == 0 || limit <= 0 || query.IsEmpty)
            return [];

        var ranked = new List<(int Score, VolumeIndex Volume, int Index)>();

        foreach (var volume in volumes)
        {
            if (token.IsCancellationRequested)
                return [];

            // CHANGED (search fix): a drive whose index is being refreshed (watcher overflow, rescan) is
            // still searched from its previous snapshot so its results appear immediately. It is not
            // "covered", so the coordinator also crawls it for anything newer, and FinishIndexAsync drops
            // snapshot hits that no longer exist. Disconnected network drives are skipped: their hits
            // cannot be verified.
            if (!IsRootLive(volume.Root) && IsNetwork(volume.Root)) continue;

            var plan = IndexSearch.Prepare(volume, query, roots);

            if (plan is null)
                continue;

            foreach (var (score, index) in IndexSearch.Search(volume, query, plan, showHidden, limit, token))
                ranked.Add((score, volume, index));
        }

        ranked.Sort((left, right) => right.Score.CompareTo(left.Score));

        var results = new List<FileSystemItem>(Math.Min(limit, ranked.Count));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, volume, index) in ranked)
        {
            if (results.Count >= limit || token.IsCancellationRequested)
                break;

            var path = volume.GetPath(index);

            if (path.Length == 0 || Overlay.IsRemoved(path) || !seen.Add(path))
                continue;

            var item = Materialise(volume, index, path);

            if (item is not null)
                results.Add(item);
        }

        // Entries created since the last full scan. The coordinator ranks everything together.
        if (!token.IsCancellationRequested)
        {
            var added = 0;

            foreach (var path in Overlay.AddedPaths())
            {
                if (added >= limit || token.IsCancellationRequested)
                    break;

                if (seen.Contains(path) || !IsUnderAnyRoot(path, roots) || !query.MightMatchPath(path))
                    continue;

                var item = FileSystemItem.FromLocation(path);

                if (item is null || (!showHidden && (item.IsHidden || (item.Attributes & FileAttributes.System) != 0)))
                    continue;

                if (!query.Matches(item) || !seen.Add(path))
                    continue;

                results.Add(item);
                added++;
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
