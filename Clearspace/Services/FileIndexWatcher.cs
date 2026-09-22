// Clearspace | File-index change watcher.
// REWRITTEN (live index): changes now also patch the index itself through FileIndexUpdater
// (the overlay is still fed for search's "recently added" fallback). File size changes are
// watched too, and each drive is watched once even if it is rescanned.

using System.Diagnostics;
using System.IO;

namespace Clearspace.Services;

internal sealed class FileIndexWatcher : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly IndexOverlay _overlay;
    private readonly FileIndexUpdater? _updater;

    public FileIndexWatcher(IndexOverlay overlay, FileIndexUpdater? updater = null)
    {
        _overlay = overlay;
        _updater = updater;
    }

    // Raised with the drive root whose events were lost (buffer overflow or watcher failure).
    public event EventHandler<string>? Desynchronised;

    public void Watch(string root)
    {
        lock (_watchers)
        {
            if (_watchers.ContainsKey(root)) return; // NEW: rescans no longer add duplicate watchers
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = BufferSize,
                    // CHANGED: Size and LastWrite so growing/shrinking files update live.
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.Size | NotifyFilters.LastWrite
                };

                watcher.Created += (_, e) => { _overlay.OnCreated(e.FullPath); _updater?.OnCreated(e.FullPath); };
                watcher.Deleted += (_, e) => { _overlay.OnDeleted(e.FullPath); _updater?.OnDeleted(e.FullPath); };
                watcher.Renamed += (_, e) => { _overlay.OnRenamed(e.OldFullPath, e.FullPath); _updater?.OnRenamed(e.OldFullPath, e.FullPath); };
                watcher.Changed += (_, e) => _updater?.OnChanged(e.FullPath);
                watcher.Error += (_, e) => OnError(root, e);

                watcher.EnableRaisingEvents = true;
                _watchers[root] = watcher;
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"Clearspace: cannot watch {root}. {exception.Message}");
                _overlay.MarkOverflowed();
                Desynchronised?.Invoke(this, root);
            }
        }
    }

    // An overflow means some changes were missed; the service rescans that drive in the background.
    private void OnError(string root, ErrorEventArgs e)
    {
        Trace.WriteLine($"Clearspace: watcher overflow on {root}. {e.GetException()?.Message}");
        _overlay.MarkOverflowed();
        Desynchronised?.Invoke(this, root);
    }

    public void Dispose()
    {
        lock (_watchers)
        {
            foreach (var watcher in _watchers.Values)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch (Exception)
                {
                }
            }

            _watchers.Clear();
        }
    }
}
