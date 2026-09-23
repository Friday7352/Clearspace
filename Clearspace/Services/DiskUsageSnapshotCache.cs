// Clearspace | Cached folder-size snapshots for the disk map.
//
// NEW (round 19): building a snapshot aggregates every folder on the volume out of the file index.
// That ran every time the disk view opened - the "Calculating folder sizes" wait, and the reason
// tiles arrived a moment after the window did. A snapshot is now built once and kept, so opening
// the view is immediate.
//
// What does and does not invalidate it matters. A live index edit (one file written anywhere on the
// drive) deliberately does NOT, or a steady trickle of changes would make every open slow again;
// the view's own live refresh replaces the stored snapshot instead, in the background, while it is
// already open. It is thrown away only when the user asks for Refresh or a full index build
// finishes - the two moments where the user has said, or would expect, that the numbers are redone.
//
// Snapshots are large, so at most MaxEntries volumes are kept and the least recently used is
// dropped. Holding one is no more than the view already held while it was open.

namespace Clearspace.Services;

internal static class DiskUsageSnapshotCache
{
    // CHANGED (round 36): two volumes again. Keeping one meant that opening a second drive evicted
    // the first, so returning to it rebuilt its snapshot - and a rebuilt snapshot is a new instance,
    // which the laid-out tree kept for that drive no longer belonged to, so the entire tree was
    // rebuilt with it. Two entries is what makes switching between a pair of drives free. Measured
    // at 46-79 MB each on a real drive, which is cheap for that. "Conserve memory" still keeps one
    // and gives up the round trip along with everything else that setting gives up.
    private static int MaxEntries => SettingsService.GetDiskMapConserveMemory() ? 1 : 2;

    /// <summary>Bytes held by cached snapshots, for the F3 readout.</summary>
    public static long RetainedBytes
    {
        get { lock (Lock) return Entries.Values.Sum(entry => entry.Snapshot.ApproximateBytes); }
    }

    private sealed record Entry(DiskUsageSnapshot Snapshot, string Exclusions, long Used);

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();
    private static long _clock;
    private static bool _started;
    private static CancellationTokenSource? _prewarm;

    /// <summary>Keep snapshots warm as soon as an index exists. Call once, beside FileIndexService.Start.</summary>
    public static void Start()
    {
        lock (Lock)
        {
            if (_started) return;
            _started = true;
        }
        FileIndexService.Changed += (_, _) =>
        {
            // Changed fires throughout indexing. Only a volume whose last full scan actually moved
            // has numbers worth redoing; everything else keeps the snapshot it already has, so a
            // busy index can never turn this into a rebuild loop.
            if (FileIndexService.IsBuilding) return;
            InvalidateStale();
            Prewarm();
        };
        Prewarm();
    }

    /// <summary>Builds any missing snapshots in the background. Safe to call at any time.</summary>
    public static void Prewarm()
    {
        CancellationTokenSource source;
        lock (Lock)
        {
            _prewarm?.Cancel();
            _prewarm = source = new CancellationTokenSource();
        }
        // Warming a drive the user has not opened is exactly the memory this setting gives up.
        if (SettingsService.GetDiskMapConserveMemory()) return;
        var token = source.Token;
        Task.Run(() =>
        {
            try
            {
                // CHANGED (round 42): only as many drives as are kept, and never one that already has a
                // snapshot of any kind. Warming every drive on every index change meant that with more
                // drives than slots, each change rebuilt and evicted whole-drive snapshots in turn; and a
                // view holding a snapshot with this session's deletions excluded was replaced by a plain
                // one, which the view then rebuilt again.
                var room = MaxEntries;
                lock (Lock) room -= Entries.Count;
                foreach (var volume in FileIndexService.CaptureVolumesForDiskUsage())
                {
                    if (token.IsCancellationRequested) return;
                    lock (Lock) if (Entries.ContainsKey(volume.Root)) continue;
                    if (room-- <= 0) return;
                    // Exclusions come from deletions made in this session; there are none to apply
                    // ahead of time, and a view that has some simply builds its own.
                    Get(volume, token, []);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine($"Clearspace: snapshot prewarm failed. {exception.Message}"); }
        }, token);
    }

    /// <summary>The snapshot for this volume, building and storing it only if one is not held.</summary>
    public static DiskUsageSnapshot Get(VolumeIndex volume, CancellationToken token, IReadOnlyList<string> exclusions)
    {
        var signature = Signature(exclusions);
        if (Lookup(volume.Root, signature) is { } held) return held;
        // One build per volume: a second caller waits here and then finds the finished snapshot,
        // rather than aggregating the same drive twice.
        lock (GateFor(volume.Root))
        {
            if (Lookup(volume.Root, signature) is { } raced) return raced;
            return Store(DiskUsageSnapshot.Build(volume, token, exclusions), signature);
        }
    }

    /// <summary>Builds a fresh snapshot and stores it, replacing whatever was held.</summary>
    public static DiskUsageSnapshot Rebuild(VolumeIndex volume, CancellationToken token, IReadOnlyList<string> exclusions)
    {
        lock (GateFor(volume.Root))
            return Store(DiskUsageSnapshot.Build(volume, token, exclusions), Signature(exclusions));
    }

    /// <summary>Stores a snapshot produced elsewhere, such as the one reconciled after a deletion.</summary>
    public static DiskUsageSnapshot Store(DiskUsageSnapshot snapshot, IReadOnlyList<string> exclusions)
        => Store(snapshot, Signature(exclusions));

    /// <summary>NEW (round 42): whether this exact snapshot is still one the cache holds.</summary>
    public static bool IsHeld(object snapshot)
    {
        lock (Lock)
            foreach (var entry in Entries.Values)
                if (ReferenceEquals(entry.Snapshot, snapshot)) return true;
        return false;
    }

    public static void Invalidate(string root)
    {
        lock (Lock) Entries.Remove(root);
        Controls.DiskUsageTreemap.ForgetCachedTrees();
    }

    public static void InvalidateAll()
    {
        lock (Lock) Entries.Clear();
        Controls.DiskUsageTreemap.ForgetCachedTrees();
    }

    /// <summary>Drops snapshots whose volume has been fully scanned again since they were built.</summary>
    private static void InvalidateStale()
    {
        foreach (var volume in FileIndexService.CaptureVolumesForDiskUsage())
            lock (Lock)
                if (Entries.TryGetValue(volume.Root, out var entry) && entry.Snapshot.BuiltUtc != volume.BuiltUtc)
                {
                    Entries.Remove(volume.Root);
                    Controls.DiskUsageTreemap.ForgetCachedTrees();
                }
    }

    private static DiskUsageSnapshot Store(DiskUsageSnapshot snapshot, string signature)
    {
        lock (Lock)
        {
            Entries[snapshot.Root] = new Entry(snapshot, signature, ++_clock);
            while (Entries.Count > MaxEntries)
            {
                var oldest = Entries.OrderBy(pair => pair.Value.Used).First().Key;
                Entries.Remove(oldest);
            }
        }
        return snapshot;
    }

    private static DiskUsageSnapshot? Lookup(string root, string signature)
    {
        lock (Lock)
        {
            if (!Entries.TryGetValue(root, out var entry) || entry.Exclusions != signature) return null;
            Entries[root] = entry with { Used = ++_clock };
            return entry.Snapshot;
        }
    }

    private static object GateFor(string root)
    {
        lock (Lock)
        {
            if (!Gates.TryGetValue(root, out var gate)) Gates[root] = gate = new object();
            return gate;
        }
    }

    private static string Signature(IReadOnlyList<string> exclusions)
        => exclusions.Count == 0 ? string.Empty
            : string.Join("|", exclusions.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
}
