// Clearspace | What the index has been doing, for the Indexing page.
//
// NEW (indexing page overhaul). A bounded event log plus per-drive facts: how the drive was last
// brought up to date (journal catch-up or full scan), how long it took, how many changes, why the next
// update is needed, and how many live changes were applied this session. Memory stays small: at most
// MaxEvents events and one small record per drive. Thread-safe; written from the index worker and the
// updater thread, read by the page on a worker.

using System.IO;

namespace Clearspace.Services;

internal enum IndexEventKind
{
    Info,
    CatchUp,
    FullScan,
    Missed,
    Save,
    Problem
}

internal sealed record IndexEvent(DateTime Utc, string? Root, IndexEventKind Kind, string Message)
{
    public string TimeText => Utc.ToLocalTime().ToString(Utc.Date == DateTime.UtcNow.Date ? "T" : "g");
    public string RootText => Root ?? "Index";
    public string Glyph => Kind switch
    {
        IndexEventKind.CatchUp => "",   // sync
        IndexEventKind.FullScan => "",  // search
        IndexEventKind.Missed => "",    // warning
        IndexEventKind.Save => "",      // save
        IndexEventKind.Problem => "",   // error
        _ => ""                         // info
    };
}

internal sealed record DriveActivity
{
    public DateTime? LastCatchUpUtc { get; init; }
    public int LastCatchUpRecords { get; init; }
    public int LastCatchUpChanges { get; init; }
    public TimeSpan LastCatchUpDuration { get; init; }
    public string? LastCatchUpNote { get; init; }
    public DateTime? LastFullScanUtc { get; init; }
    public TimeSpan? LastFullScanDuration { get; init; }
    public string? LastFullScanReason { get; init; }
    public long LastFullScanItems { get; init; }
    public DateTime? FullScanStartedUtc { get; init; }
    public long LiveChanges { get; init; }
    public DateTime? LastLiveChangeUtc { get; init; }
    public string? PendingReason { get; init; }
    public string? JournalNote { get; init; }

    public string CatchUpText => LastCatchUpUtc is { } when
        ? $"{Ago(when)} · {LastCatchUpChanges:N0} change{(LastCatchUpChanges == 1 ? "" : "s")} from {LastCatchUpRecords:N0} journal records in {Seconds(LastCatchUpDuration)}"
          + (LastCatchUpNote is null ? "" : $" · {LastCatchUpNote}")
        : "Not caught up from the change journal this session";

    public string FullScanText => FullScanStartedUtc is { } started
        ? $"Running since {started.ToLocalTime():t}" + (LastFullScanReason is null ? "" : $" · {LastFullScanReason}")
        : LastFullScanUtc is { } when
            ? $"{Ago(when)} · {LastFullScanItems:N0} entries in {Seconds(LastFullScanDuration ?? TimeSpan.Zero)}"
              + (LastFullScanReason is null ? "" : $" · {LastFullScanReason}")
            : "No full scan this session";

    public string LiveText => LiveChanges == 0 ? "None yet this session"
        : $"{LiveChanges:N0} applied this session · last {Ago(LastLiveChangeUtc ?? DateTime.UtcNow)}";

    internal static string Ago(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age.TotalSeconds < 60 ? "just now"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalHours < 24 ? $"{(int)age.TotalHours} h ago"
            : utc.ToLocalTime().ToString("g");
    }

    internal static string Seconds(TimeSpan span) => span.TotalSeconds < 10 ? $"{span.TotalSeconds:0.0} s"
        : span.TotalMinutes < 2 ? $"{span.TotalSeconds:0} s" : $"{(int)span.TotalMinutes} min {span.Seconds} s";
}

internal static class IndexActivity
{
    private const int MaxEvents = 300;
    private static readonly Lock Gate = new();
    private static readonly LinkedList<IndexEvent> Log = new();
    private static readonly Dictionary<string, DriveActivity> Drives = new(StringComparer.OrdinalIgnoreCase);

    public static event Action? Changed;

    public static IReadOnlyList<IndexEvent> Events()
    {
        lock (Gate) return [.. Log];
    }

    public static DriveActivity For(string root)
    {
        lock (Gate) return Drives.TryGetValue(root, out var drive) ? drive : new DriveActivity();
    }

    public static void Record(string? root, IndexEventKind kind, string message)
    {
        lock (Gate)
        {
            Log.AddFirst(new IndexEvent(DateTime.UtcNow, root, kind, message));
            while (Log.Count > MaxEvents) Log.RemoveLast();
        }

        Notify();
    }

    // Why this drive needs an update. keepExisting: an automatic reason does not replace a specific one.
    public static void Pending(string root, string reason, bool keepExisting = false)
    {
        bool changed;

        lock (Gate)
        {
            var drive = Get(root);
            changed = drive.PendingReason is null || !keepExisting;
            if (changed) Drives[root] = drive with { PendingReason = reason };
        }

        if (changed) Record(root, IndexEventKind.Missed, reason);
    }

    public static void JournalNote(string root, string? note)
    {
        lock (Gate) Drives[root] = Get(root) with { JournalNote = note };
        Notify();
    }

    public static void CatchUpFinished(string root, string method, int records, int changes, int unresolved, TimeSpan duration)
    {
        var note = unresolved > 0 ? $"{unresolved:N0} records were for items this account cannot open" : null;

        lock (Gate)
        {
            Drives[root] = Get(root) with
            {
                LastCatchUpUtc = DateTime.UtcNow,
                LastCatchUpRecords = records,
                LastCatchUpChanges = changes,
                LastCatchUpDuration = duration,
                LastCatchUpNote = note,
                PendingReason = null,
                JournalNote = $"Kept current by the change journal ({method})"
            };
        }

        Record(root, IndexEventKind.CatchUp,
            $"Caught up from the change journal · {changes:N0} changed item{(changes == 1 ? "" : "s")} from {records:N0} records in {DriveActivity.Seconds(duration)}");
    }

    public static void CatchUpFailed(string root, string reason)
    {
        lock (Gate) Drives[root] = Get(root) with { JournalNote = reason };
        Record(root, IndexEventKind.Problem, reason);
    }

    public static void FullScanStarted(string root, string reason)
    {
        lock (Gate) Drives[root] = Get(root) with { FullScanStartedUtc = DateTime.UtcNow, LastFullScanReason = reason };
        Record(root, IndexEventKind.FullScan, $"Full scan started · {reason}");
    }

    public static void FullScanEnded(string root, long items, bool completed, string? problem = null)
    {
        DriveActivity drive;

        lock (Gate)
        {
            drive = Get(root);
            var duration = drive.FullScanStartedUtc is { } started ? DateTime.UtcNow - started : TimeSpan.Zero;
            drive = completed
                ? drive with
                {
                    FullScanStartedUtc = null,
                    LastFullScanUtc = DateTime.UtcNow,
                    LastFullScanDuration = duration,
                    LastFullScanItems = items,
                    PendingReason = null
                }
                : drive with { FullScanStartedUtc = null };
            Drives[root] = drive;
        }

        if (completed)
            Record(root, IndexEventKind.FullScan,
                $"Full scan finished · {items:N0} entries in {DriveActivity.Seconds(drive.LastFullScanDuration ?? TimeSpan.Zero)}");
        else
            Record(root, IndexEventKind.Problem, $"Full scan stopped{(problem is null ? "" : $" · {problem}")}");
    }

    public static void LiveApplied(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        lock (Gate)
        {
            foreach (var group in paths.GroupBy(path => Path.GetPathRoot(path) ?? "", StringComparer.OrdinalIgnoreCase))
            {
                if (group.Key.Length == 0) continue;
                var drive = Get(group.Key);
                Drives[group.Key] = drive with { LiveChanges = drive.LiveChanges + group.Count(), LastLiveChangeUtc = DateTime.UtcNow };
            }
        }

        Notify();
    }

    private static DriveActivity Get(string root) => Drives.TryGetValue(root, out var drive) ? drive : new DriveActivity();

    private static void Notify()
    {
        try { Changed?.Invoke(); } catch (Exception) { }
    }
}
