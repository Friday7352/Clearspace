namespace Clearspace.Services;

internal sealed record IndexScanIssue(string Path, string Reason);
internal sealed record IndexScanDetails(string CurrentFolder, long FoldersVisited, long SkippedFolders,
    DateTime? CompletedUtc, IReadOnlyList<IndexScanIssue> Examples)
{
    public long FoldersDiscovered { get; init; }
    public long FoldersProcessed { get; init; }
    public long FoldersRemaining => Math.Max(0, FoldersDiscovered - FoldersProcessed);
    public double ProgressPercent => CompletedUtc is not null ? 100
        : FoldersDiscovered == 0 ? 0 : Math.Min(99, 100d * FoldersProcessed / FoldersDiscovered);
}

// A bounded diagnostic log. Does not retain one object per indexed file or raise UI events.
internal sealed class IndexScanTracker
{
    private readonly object _gate = new();
    private readonly List<IndexScanIssue> _examples = [];
    private string _folder = "";
    private long _visited, _skipped;
    private long _discovered = 1, _processed;
    private DateTime? _completed;
    public void Visit(string folder) { lock (_gate) { _folder = folder; _visited++; } }
    public void DiscoverFolder() { lock (_gate) _discovered++; }
    public void FinishFolder() { lock (_gate) _processed++; }
    public void Skip(string path, string reason)
    {
        lock (_gate)
        {
            _skipped++;
            if (_examples.Count < 200) _examples.Add(new(path, reason));
        }
    }
    public void Complete() { lock (_gate) { _completed = DateTime.UtcNow; _folder = ""; } }
    public IndexScanDetails Capture()
    {
        lock (_gate) return new(_folder, _visited, _skipped, _completed, _examples.ToArray())
        { FoldersDiscovered = _discovered, FoldersProcessed = _processed };
    }
}
