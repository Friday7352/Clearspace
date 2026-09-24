using System.IO;

namespace Clearspace.Services;

internal sealed record IndexedDrive(string Root, string State, string Detail, long Items, long MemoryBytes,
    DateTime? ScanStartedUtc, IndexScanDetails? Scan, VolumeIndex? Source)
{
    public DateTime? ScheduledUtc { get; init; }
    public bool CanScan { get; init; }
    public bool ShowProgress => IsScanning;
    public bool IsScanning => State is "Indexing" or "Updating";
    public bool IsScanComplete => !IsScanning && (State is "Up to date" or "Saved index") && Source is not null;
    public double ProgressPercent => IsScanning ? Scan?.ProgressPercent ?? 0 : IsScanComplete ? 100 : 0;
    public string ProgressText => IsScanning ? $"{ProgressPercent:0}% of discovered folders"
        : IsScanComplete ? State == "Up to date" ? "Ready · watching for changes" : "Saved entries available" : ScheduledUtc is { } retry && retry > DateTime.UtcNow
        ? $"Retry at {retry.ToLocalTime():t}" : State is "Waiting to update" or "Waiting to index" ? "Queued · scan has not started"
        : State == "Not included" ? "Not indexed" : State == "Unavailable" ? "Drive unavailable" : "Scan needs attention";
    public string ProgressDetail => IsScanning && Scan is { } scan
        ? $"{scan.FoldersProcessed:N0} processed · {scan.FoldersRemaining:N0} waiting · {scan.FoldersDiscovered:N0} discovered. More folders may be found, so the percentage can decrease. Skipped locations are listed below."
        : State == "Up to date" ? "No full scan is needed right now. Changes are tracked automatically. You can request a fresh scan below."
        : Detail;
    public string CountText => Source is null ? "No saved entries" : $"{Items:N0} entries";
    public string MemoryText => Source is null ? "—" : DiskUsageSnapshot.FormatBytes(MemoryBytes);
    public string ScanText => Scan?.CompletedUtc is { } completed ? $"Scan finished {completed.ToLocalTime():g}"
        : ScanStartedUtc is { } started ? $"Scan started {started.ToLocalTime():g}" : "No scan recorded";
    public string CoverageText => Scan is null ? "Folder-level scan details were not saved. They will appear after the next scan."
        : $"{Scan.FoldersVisited:N0} folders visited · {Scan.SkippedFolders:N0} skipped";
}

internal sealed record IndexingSummary(IReadOnlyList<IndexedDrive> Drives, string IndexPath, long? DiskBytes,
    DateTime? SavedUtc, string? StorageError, long MemoryBytes, string? ActiveRoot, string ActiveFolder,
    int ActiveItems, long VisitedFolders, bool NetworkEnabled)
{
    public string? WorkerActivity { get; init; }
    private IndexedDrive? Waiting => Drives.FirstOrDefault(d => d.State is "Waiting to update" or "Waiting to index" or "Needs attention");
    public long IndexedItems => Drives.Sum(drive => drive.Items);
    public string ActivityTitle => ActiveRoot is not null ? $"Scanning {ActiveRoot}" : WorkerActivity is not null ? "Indexing status"
        : Waiting is { } waiting ? $"{waiting.Root} needs an update" : "Index ready";
    public string ActivityDetail => ActiveRoot is not null ? $"{ActiveItems:N0} entries found · {VisitedFolders:N0} folders visited"
        : WorkerActivity ?? (Waiting is { } waiting ? waiting.Detail
        : IndexedItems > 0 ? "Your saved index is available. File changes are tracked automatically; a full scan does not need to run continuously."
        : "No indexed entries are available yet. Select a drive below to see its status.");
}

internal sealed record IndexedEntry(int Id, string Name, string Path, bool IsFolder, long Bytes)
{
    public string Glyph => IsFolder ? "\uE8B7" : "\uE8A5";
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
            var state = active ? saved is null ? "Indexing" : "Updating"
                : problem is not null ? "Needs attention"
                : kind == DriveType.Network && !NetworkDrives.Enabled ? "Not included"
                : kind == DriveType.Network && !NetworkDrives.IsReady(root) ? "Unavailable"
                : pending.Contains(root, StringComparer.OrdinalIgnoreCase) ? "Waiting to update"
                : saved is not null && FileIndexService.HasLiveCoverage(root) ? "Up to date"
                : saved is not null ? "Saved index"
                : kind is DriveType.Fixed or DriveType.Network ? "Waiting to index" : "Not included";
            var retry = FileIndexService.RetryAt(root);
            var detail = active ? saved is null ? "Building its first index." : "Refreshing this drive; its previous entries are still listed."
                : FileIndexService.WorkerProblem ?? problem ?? (state switch
                {
                    "Up to date" => "Watching for changes. Unreadable folders and folder links may be skipped.",
                    "Waiting to update" => ExplainWait(retry, scanning?.Root, FileIndexService.Maintenance),
                    "Waiting to index" => ExplainWait(null, scanning?.Root, FileIndexService.Maintenance),
                    "Unavailable" => "The network drive is not responding. Any saved entries remain available.",
                    "Not included" => kind == DriveType.Network ? "Network indexing is off." : "Only fixed drives and opted-in network drives are indexed.",
                    "Saved index" => "Saved entries are available; live coverage is not currently confirmed.",
                    _ => "Included in indexing when the drive is available."
                });
            rows.Add(new(root, state, detail, source is null ? 0 : Math.Max(0, source.Count - source.RemovedCount),
                source?.EstimatedBytes ?? 0, source?.BuiltUtc, details, source)
            { ScheduledUtc = scanning is null ? retry : null, CanScan = !active && FileIndexService.WorkerProblem is null &&
                (kind == DriveType.Fixed || kind == DriveType.Network && NetworkDrives.Enabled && NetworkDrives.IsReady(root)) });
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
        { WorkerActivity = FileIndexService.WorkerProblem ?? FileIndexService.Maintenance };
    }

    internal static string ExplainWait(DateTime? retry, string? activeRoot, string? maintenance)
    {
        if (activeRoot is not null) return $"Waiting for {activeRoot} to finish. Clearspace scans one drive at a time to limit disk activity. Your saved entries remain available.";
        if (retry is { } time && time > DateTime.UtcNow)
            return $"Changes need to be checked. Automatic rescans are spaced 20 minutes apart to limit disk activity. Eligible again at {time.ToLocalTime():t}; choose Scan now to skip this wait. Your saved entries remain available.";
        if (maintenance is not null) return $"{maintenance}. This drive will be checked afterward; saved entries remain available.";
        return "A catch-up scan is queued to verify changes missed while Clearspace was closed or busy. The background worker will pick it up next; saved entries remain available.";
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
