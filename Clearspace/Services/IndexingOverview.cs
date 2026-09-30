// Clearspace | Snapshot of the index for the Indexing page.
//
// CHANGED (indexing page overhaul): each drive now reports how it is kept current (change journal,
// live watcher, or full scans), what happened last (catch-up / full scan, with counts and timings),
// what is pending and why, and how many live changes were applied this session. The page also gets
// the journal/helper status and the recent activity log. Everything is captured on a worker.

using System.IO;
using Clearspace.Journal;

namespace Clearspace.Services;

internal sealed record IndexedDrive(string Root, string State, string Detail, long Items, long MemoryBytes,
    DateTime? ScanStartedUtc, IndexScanDetails? Scan, VolumeIndex? Source)
{
    public DateTime? ScheduledUtc { get; init; }
    public bool CanScan { get; init; }
    // NEW (overhaul)
    public bool CanCatchUp { get; init; }
    public DriveActivity Activity { get; init; } = new();
    public string Method { get; init; } = "Full scans";
    public string MethodDetail { get; init; } = "";
    public bool IsNetwork { get; init; }

    public bool ShowProgress => IsScanning;
    public bool IsScanning => State is "Indexing" or "Updating";
    public bool IsScanComplete => !IsScanning && (State is "Up to date" or "Saved index") && Source is not null;
    public double ProgressPercent => IsScanning ? Scan?.ProgressPercent ?? 0 : IsScanComplete ? 100 : 0;
    public string ProgressText => IsScanning ? $"{ProgressPercent:0}% of discovered folders"
        : IsScanComplete ? State == "Up to date" ? "Up to date · watching for changes" : "Saved entries available" : ScheduledUtc is { } retry && retry > DateTime.UtcNow
        ? $"Full rescan eligible at {retry.ToLocalTime():t}" : State is "Waiting to update" or "Waiting to index" ? "Queued"
        : State == "Not included" ? "Not indexed" : State == "Unavailable" ? "Drive unavailable" : "Needs attention";
    public string ProgressDetail => IsScanning && Scan is { } scan
        ? $"{scan.FoldersProcessed:N0} folders read · {scan.FoldersRemaining:N0} waiting · {scan.FoldersDiscovered:N0} found so far. The percentage can drop as more folders are found."
        : Detail;
    public string CountText => Source is null ? "No saved entries" : $"{Items:N0} entries";
    public string MemoryText => Source is null ? "—" : DiskUsageSnapshot.FormatBytes(MemoryBytes);
    public string ScanText => Scan?.CompletedUtc is { } completed ? $"Scan finished {completed.ToLocalTime():g}"
        : ScanStartedUtc is { } started ? $"Index built {started.ToLocalTime():g}" : "No scan recorded";
    public string CoverageText => Scan is null ? "Folder-level details are kept for scans run this session. They will appear after the next full scan."
        : $"{Scan.FoldersVisited:N0} folders visited · {Scan.SkippedFolders:N0} skipped";
    public string NextText => Activity.PendingReason is { } reason ? reason
        : IsScanning ? "Full scan in progress" : State == "Up to date" ? "Nothing pending" : Detail;
}

internal sealed record IndexingSummary(IReadOnlyList<IndexedDrive> Drives, string IndexPath, long? DiskBytes,
    DateTime? SavedUtc, string? StorageError, long MemoryBytes, string? ActiveRoot, string ActiveFolder,
    int ActiveItems, long VisitedFolders, bool NetworkEnabled)
{
    public string? WorkerActivity { get; init; }
    // NEW (overhaul)
    public JournalAccess Access { get; init; }
    public IReadOnlyList<IndexEvent> Events { get; init; } = [];

    private IndexedDrive? Waiting => Drives.FirstOrDefault(d => d.State is "Waiting to update" or "Waiting to index" or "Needs attention");
    public long IndexedItems => Drives.Sum(drive => drive.Items);
    public long LiveChanges => Drives.Sum(drive => drive.Activity.LiveChanges);
    public bool IsBusy => ActiveRoot is not null || WorkerActivity is not null;
    public bool JournalOn => Access is JournalAccess.Direct or JournalAccess.Helper;
    public string AccessText => IndexJournal.Describe(Access);

    public string ActivityTitle => ActiveRoot is not null ? $"Full scan of {ActiveRoot}"
        : WorkerActivity is not null ? WorkerActivity
        : Waiting is { } waiting ? $"{waiting.Root} needs an update"
        : IndexedItems > 0 ? "Everything is up to date" : "No index yet";

    public string ActivityDetail => ActiveRoot is not null
        ? $"{ActiveItems:N0} entries found · {VisitedFolders:N0} folders visited. Searches keep using the previous index until this finishes."
        : Waiting is { } waiting ? waiting.Activity.PendingReason is { } reason ? $"{reason}. {waiting.Detail}" : waiting.Detail
        : IndexedItems > 0
            ? JournalOn
                ? "Live changes are applied as they happen. After a restart or a missed change, only the drive's change journal is read — no full rescan."
                : "Live changes are applied as they happen. Turn on fast catch-up below so restarts and missed changes do not need full rescans."
            : "Drives are indexed in the background after Clearspace starts.";

    public string LiveChangesText => LiveChanges == 0 ? "None yet" : $"{LiveChanges:N0}";
}

internal sealed record IndexedEntry(int Id, string Name, string Path, bool IsFolder, long Bytes)
{
    public string Glyph => IsFolder ? "" : "";
    public string SizeText => IsFolder ? "Folder" : DiskUsageSnapshot.FormatBytes(Bytes);
}
internal sealed record IndexedFolderPage(string Path, int Parent, IReadOnlyList<IndexedEntry> Entries, int MatchingCount);

internal static class IndexingOverview
{
    // Called only on a worker. Drive metadata and FileInfo never run in the page's render loop.
    public static IndexingSummary Capture()
    {
        var published = FileIndexService.CaptureVolumes();
        var scanning = FileIndexService.ScanningVolume;
        var access = IndexJournal.CachedAccess();
        var journalOn = access is JournalAccess.Direct or JournalAccess.Helper;
        var roots = new Dictionary<string, DriveType>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
                roots[drive.Name] = drive.DriveType; // Does not query free space on remote servers.
        }
        catch (IOException) { }
        foreach (var volume in published) roots.TryAdd(volume.Root, DriveType.Unknown);
        if (scanning is not null) roots.TryAdd(scanning.Root, DriveType.Unknown);
        var pending = FileIndexService.PendingRescans;
        var rows = new List<IndexedDrive>();
        foreach (var (root, kind) in roots.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var saved = Array.Find(published, volume => volume.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
            var active = scanning is not null && scanning.Root.Equals(root, StringComparison.OrdinalIgnoreCase);
            var source = saved ?? (active ? scanning : null);
            var details = (active ? scanning : saved)?.ScanDetails?.Capture();
            var problem = FileIndexService.ScanProblem(root);
            var network = kind == DriveType.Network;
            var state = active ? saved is null ? "Indexing" : "Updating"
                : problem is not null ? "Needs attention"
                : network && !NetworkDrives.Enabled ? "Not included"
                : network && !NetworkDrives.IsReady(root) ? "Unavailable"
                : pending.Contains(root, StringComparer.OrdinalIgnoreCase) ? "Waiting to update"
                : saved is not null && FileIndexService.HasLiveCoverage(root) ? "Up to date"
                : saved is not null ? "Saved index"
                : kind is DriveType.Fixed or DriveType.Network ? "Waiting to index" : "Not included";
            var retry = FileIndexService.RetryAt(root);
            var activity = IndexActivity.For(root);
            var hasPosition = FileIndexService.HasJournalPosition(root);
            var (method, methodDetail) = network || !UsnJournal.IsDriveRoot(root)
                ? ("Watcher + full scans", "Network drives have no change journal Clearspace can read. Live changes are applied; missed changes need a full rescan.")
                : journalOn && hasPosition
                    ? ("Change journal", "Live changes are applied as they happen. After a restart or a missed change, only the change journal is read.")
                    : journalOn
                        ? ("Journal pending", activity.JournalNote ?? "The journal position is recorded after the next save or full scan; after that, restarts read only the changes.")
                        : ("Watcher + full scans", "Live changes are applied as they happen. Without fast catch-up, a restart or missed change means a full rescan of this drive.");
            var detail = active ? saved is null ? "Building its first index." : "Rescanning in full; the previous entries stay searchable until it finishes."
                : FileIndexService.WorkerProblem ?? problem ?? (state switch
                {
                    "Up to date" => "Watching for changes. Unreadable folders and folder links are skipped.",
                    "Waiting to update" => ExplainWait(retry, scanning?.Root, FileIndexService.Maintenance, journalOn && hasPosition),
                    "Waiting to index" => ExplainWait(null, scanning?.Root, FileIndexService.Maintenance, false),
                    "Unavailable" => "The network drive is not responding. Any saved entries remain available.",
                    "Not included" => network ? "Network indexing is off." : "Only fixed drives and opted-in network drives are indexed.",
                    "Saved index" => "Saved entries are searchable; live watching is not confirmed right now.",
                    _ => "Included in indexing when the drive is available."
                });
            var canScan = !active && FileIndexService.WorkerProblem is null &&
                (kind == DriveType.Fixed || network && NetworkDrives.Enabled && NetworkDrives.IsReady(root));
            rows.Add(new(root, state, detail, source is null ? 0 : Math.Max(0, source.Count - source.RemovedCount),
                source?.EstimatedBytes ?? 0, source?.BuiltUtc, details, source)
            {
                ScheduledUtc = scanning is null ? retry : null,
                CanScan = canScan,
                CanCatchUp = canScan && saved is not null,
                Activity = activity,
                Method = method,
                MethodDetail = methodDetail,
                IsNetwork = network
            });
        }
        long? disk = null;
        DateTime? savedAt = null;
        string? storageError = null;
        try
        {
            var file = new FileInfo(FileIndexStore.FilePath);
            if (file.Exists) { disk = file.Length; savedAt = file.LastWriteTimeUtc; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { storageError = ex.Message; }
        var memory = published.Sum(volume => volume.EstimatedBytes);
        if (scanning is not null && !published.Contains(scanning)) memory += scanning.EstimatedBytes;
        var scan = scanning?.ScanDetails?.Capture();
        return new(rows, FileIndexStore.FilePath, disk, savedAt, storageError, memory, scanning?.Root,
            scan?.CurrentFolder ?? "", scanning?.Count ?? 0, scan?.FoldersVisited ?? 0, NetworkDrives.Enabled)
        {
            WorkerActivity = FileIndexService.WorkerProblem ?? FileIndexService.Maintenance,
            Access = access,
            Events = IndexActivity.Events()
        };
    }

    internal static string ExplainWait(DateTime? retry, string? activeRoot, string? maintenance, bool journal = false)
    {
        if (journal) return "It will be caught up from the change journal in a moment; only the changes are read.";
        if (activeRoot is not null) return $"Waiting for {activeRoot} to finish. Clearspace scans one drive at a time to limit disk activity. Saved entries remain searchable.";
        if (retry is { } time && time > DateTime.UtcNow)
            return $"Automatic full rescans are spaced 20 minutes apart to limit disk activity. Eligible at {time.ToLocalTime():t}; choose Full rescan to skip the wait. Saved entries remain searchable.";
        if (maintenance is not null) return $"{maintenance}. This drive is next; saved entries remain searchable.";
        return "A full rescan is queued to pick up changes Clearspace could not observe. Saved entries remain searchable.";
    }

    // Read the actual indexed parent IDs, never the filesystem. Results and UI objects are bounded.
    // A direct scan avoids allocating a second whole-drive hierarchy just to open this page.
    internal static IndexedFolderPage ReadFolder(VolumeIndex source, int parent, string filter, CancellationToken token, int limit = 1000, int offset = 0)
    {
        token.ThrowIfCancellationRequested();
        if (parent < 0 || parent >= source.Count) throw new ArgumentOutOfRangeException(nameof(parent));
        if (limit <= 0 || offset < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var entries = new List<IndexedEntry>();
        var count = source.Count;
        var matches = 0;
        for (var id = 1; id < count; id++)
        {
            token.ThrowIfCancellationRequested();
            var entry = source.Entry(id);
            if (entry.ParentIndex != parent || (entry.Attributes & VolumeIndex.RemovedFlag) != 0) continue;
            if (filter.Length > 0 && !source.NameSpan(id).Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            matches++;
            if (matches > offset && entries.Count < limit) entries.Add(new(id, source.GetName(id), source.GetPath(id), entry.IsFolder, entry.Size));
        }
        entries.Sort((a, b) => a.IsFolder != b.IsFolder ? a.IsFolder ? -1 : 1 : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return new(source.GetPath(parent), source.Entry(parent).ParentIndex, entries, matches);
    }
}
