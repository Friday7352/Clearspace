// Clearspace | NEW (live folder): tells the window when the folder on screen changes on the disk.
// A folder used to be read when you opened it and not again until you pressed F5, so a file another
// program put there (a download finishing, a save from an editor) did not show up. This watches the one
// folder the window is showing and says "something changed", a moment after the changes stop:
//  - one download is a burst of dozens of events, so the call comes 350 ms after the last one, and
//  - a file that keeps growing never stops, so a call is made every 2 seconds at most while it goes on.
// It only says that something changed, not what: MainViewModel reads the folder again and works out the
// difference (FolderMerge), which also covers events Windows drops when too many arrive at once.
// Only the folder itself is watched, not what is inside its subfolders, and only while it is on screen.
using System.Diagnostics;
using System.IO;

namespace Clearspace.Services;

internal sealed class FolderWatcher : IDisposable
{
    internal static readonly TimeSpan DefaultQuiet = TimeSpan.FromMilliseconds(350);
    internal static readonly TimeSpan DefaultLongest = TimeSpan.FromSeconds(2);

    private readonly Action _changed;
    private readonly SynchronizationContext? _context; // the UI thread's, when made there; `changed` is called on it
    private readonly TimeSpan _quiet;
    private readonly TimeSpan _longest;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private FileSystemWatcher? _watcher;
    private string? _folder;
    private long _order;         // the newest Watch request seen; an older one that arrives late is ignored
    private long _firstPending;  // when the first change not yet reported was seen (0 = nothing waiting)
    private bool _disposed;

    internal FolderWatcher(Action changed, TimeSpan? quiet = null, TimeSpan? longest = null)
    {
        _changed = changed;
        _context = SynchronizationContext.Current;
        _quiet = quiet ?? DefaultQuiet;
        _longest = longest ?? DefaultLongest;
        _timer = new Timer(_ => Report(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // True while a folder is being watched (false for pages that are not folders, and for a folder
    // Windows refuses to watch).
    internal bool IsWatching
    {
        get { lock (_gate) return _watcher is not null; }
    }

    // Starts watching `folder`, or stops when it is null. Safe to call from any thread, and with the
    // folder already being watched (nothing happens). `order` goes up with every request: starting a
    // watch on a slow network folder is done off the UI thread, so two requests can finish out of order,
    // and the older one must not win.
    internal void Watch(string? folder, long order = long.MaxValue)
    {
        lock (_gate)
        {
            if (_disposed || order < _order)
                return;
            if (order != long.MaxValue)
                _order = order;

            if (folder is not null && _watcher is not null && string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
                return;

            StopLocked();
            _folder = folder;
            if (folder is null)
                return;

            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                                   NotifyFilters.Size | NotifyFilters.Attributes,
                    InternalBufferSize = 64 * 1024
                };
                watcher.Created += OnEvent;
                watcher.Deleted += OnEvent;
                watcher.Changed += OnEvent;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException
                                              or InvalidOperationException or PlatformNotSupportedException)
            {
                // The folder is gone, or it is somewhere Windows can't watch (some network shares). The
                // listing still works; it just doesn't update by itself there.
                watcher?.Dispose();
                _watcher = null;
            }
        }
    }

    // Asks for another call a moment from now, as if a change had just been seen. MainViewModel uses it
    // when a change arrives while it is busy loading the folder.
    internal void Nudge()
    {
        lock (_gate)
        {
            if (!_disposed && _watcher is not null)
                ScheduleLocked();
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Seen(sender);

    private void OnRenamed(object sender, RenamedEventArgs e) => Seen(sender);

    private void Seen(object sender)
    {
        lock (_gate)
        {
            // An event from a watcher that has been replaced since (the window moved to another folder).
            if (_disposed || !ReferenceEquals(sender, _watcher))
                return;

            ScheduleLocked();
        }
    }

    // Windows lost track (too many changes at once) or the folder went away. Either way the listing may be
    // out of date, so report a change; the watcher itself is dropped and made again by the next Watch call.
    private void OnError(object sender, ErrorEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _watcher))
                return;

            var broken = _watcher;
            _watcher = null;
            try { broken?.Dispose(); }
            catch (Exception) { }

            _firstPending = Stopwatch.GetTimestamp();
            _timer.Change(_quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private void ScheduleLocked()
    {
        var now = Stopwatch.GetTimestamp();
        if (_firstPending == 0)
            _firstPending = now;

        // Wait for the changes to go quiet, but never past the longest wait since the first of them.
        var waited = Stopwatch.GetElapsedTime(_firstPending, now);
        var due = waited + _quiet > _longest ? _longest - waited : _quiet;
        if (due < TimeSpan.Zero)
            due = TimeSpan.Zero;

        _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void Report()
    {
        lock (_gate)
        {
            if (_disposed || _firstPending == 0)
                return;

            _firstPending = 0;
        }

        if (_context is null)
            _changed();
        else
            _context.Post(_ => _changed(), null);
    }

    private void StopLocked()
    {
        _firstPending = 0;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        var watcher = _watcher;
        _watcher = null;
        if (watcher is null)
            return;

        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            StopLocked();
            _disposed = true;
            _timer.Dispose();
        }
    }
}
