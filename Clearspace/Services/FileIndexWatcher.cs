// Clearspace | File-index change watcher.

using System.Diagnostics;
using System.IO;

namespace Clearspace.Services;

internal sealed class FileIndexWatcher : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly IndexOverlay _overlay;

    public FileIndexWatcher(IndexOverlay overlay) => _overlay = overlay;

    public event EventHandler? Desynchronised;

    public void Watch(string root)
    {
        try
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = BufferSize,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
            };

            watcher.Created += (_, e) => _overlay.OnCreated(e.FullPath);
            watcher.Deleted += (_, e) => _overlay.OnDeleted(e.FullPath);
            watcher.Renamed += (_, e) => _overlay.OnRenamed(e.OldFullPath, e.FullPath);
            watcher.Error += OnError;

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Clearspace: cannot watch {root}. {exception.Message}");
            _overlay.MarkOverflowed();
            Desynchronised?.Invoke(this, EventArgs.Empty);
        }
    }

    // CS499: An overflow invalidates the whole volume instead of recovering at folder scope.
    private void OnError(object sender, ErrorEventArgs e)
    {
        Trace.WriteLine($"Clearspace: watcher overflow. {e.GetException()?.Message}");
        _overlay.MarkOverflowed();
        Desynchronised?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
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
