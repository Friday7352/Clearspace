// Clearspace | Background file-system search.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Clearspace.Models;
using Clearspace.Native;

namespace Clearspace.Services;

// Iterative concurrent crawl; depth is bounded by the filesystem, not an arbitrary counter.
internal static class FileSearchService
{


    private sealed class VolumeQueue
    {
        public required string Root { get; init; }
        public required int Concurrency { get; init; }
        public ConcurrentQueue<string> Pending { get; } = new();
        public SemaphoreSlim Available { get; } = new(0);

        public int Outstanding;
    }


    public static bool Run(
        IReadOnlyList<string> roots,
        bool showHidden,
        Func<FileSystemItem, bool> matches,
        IProgress<IReadOnlyList<FileSystemItem>> progress,
        int maxResults,
        CancellationToken token)
    {
        if (roots.Count == 0)
            return false;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stopToken = stop.Token;

        var total = 0;
        var capped = false;

        var buffer = new List<FileSystemItem>(256);
        var gate = new object();
        var sinceReport = Stopwatch.StartNew();

        var volumes = BuildVolumes(roots);
        var workers = new List<Task>();

        foreach (var volume in volumes)
        {
            for (var i = 0; i < volume.Concurrency; i++)
            {
                workers.Add(Task.Factory.StartNew(
                    () => Work(volume),
                    stopToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default));
            }
        }

        try
        {
            // Workers observe stopToken themselves. Join before disposing their semaphores,
            // including cancellation, so a canceled search never leaves workers running.
            Task.WaitAll([.. workers]);
        }
        catch (AggregateException exception) when (exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
        }
        finally
        {
            foreach (var volume in volumes)
                volume.Available.Dispose();
        }

        token.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (buffer.Count > 0)
            {
                progress.Report([.. buffer]);
                buffer.Clear();
            }
        }

        return capped;


        void Work(VolumeQueue volume)
        {
            while (!stopToken.IsCancellationRequested)
            {
                if (!volume.Available.Wait(40, stopToken))
                {
                    if (Volatile.Read(ref volume.Outstanding) == 0)
                        return;

                    continue;
                }

                if (!volume.Pending.TryDequeue(out var entry))
                {
                    if (Volatile.Read(ref volume.Outstanding) == 0)
                        return;

                    continue;
                }

                try
                {
                    Process(volume, entry);
                }
                finally
                {
                    if (Interlocked.Decrement(ref volume.Outstanding) == 0)
                    {
                        try
                        {
                            // Wake workers waiting after the last folder finishes.
                            volume.Available.Release(volume.Concurrency);
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                        catch (SemaphoreFullException)
                        {
                        }
                    }
                }
            }
        }


        void Process(VolumeQueue volume, string directory)
        {
            List<FileSystemItem> entries;

            try
            {
                entries = DirectoryEnumerator.Enumerate(directory, showHidden, stopToken).ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return;
            }

            foreach (var item in entries)
            {
                stopToken.ThrowIfCancellationRequested();
                if (FileIndexBuilder.CanDescend(item.Attributes, item.ReparseTag))
                {
                    Interlocked.Increment(ref volume.Outstanding);
                    volume.Pending.Enqueue(item.FullPath);

                    try
                    {
                        volume.Available.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }

                if (!matches(item))
                    continue;

                List<FileSystemItem>? flush = null;

                lock (gate)
                {
                    if (total >= maxResults)
                    {
                        capped = true;
                        stop.Cancel();
                        return;
                    }

                    buffer.Add(item);
                    total++;

                    if (buffer.Count >= 128 || sinceReport.ElapsedMilliseconds >= 200)
                    {
                        flush = [.. buffer];
                        buffer.Clear();
                        sinceReport.Restart();
                    }
                }

                if (flush is not null)
                    progress.Report(flush);
            }
        }
    }


    private static List<VolumeQueue> BuildVolumes(IReadOnlyList<string> roots)
    {
        var byVolume = new Dictionary<string, VolumeQueue>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var key = VolumeProfiler.RootOf(root);

            if (key.Length == 0)
                key = root;

            if (!byVolume.TryGetValue(key, out var volume))
            {
                volume = new VolumeQueue
                {
                    Root = key,
                    Concurrency = VolumeProfiler.ConcurrencyFor(root)
                };

                byVolume[key] = volume;
            }

            Interlocked.Increment(ref volume.Outstanding);
            volume.Pending.Enqueue(root);
            volume.Available.Release();
        }

        return [.. byVolume.Values];
    }
}
