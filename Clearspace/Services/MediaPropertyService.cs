// Clearspace | Media metadata extraction.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using Clearspace.Models;
using Clearspace.Native;

namespace Clearspace.Services;

public static class MediaPropertyService
{
    private sealed record TagRequest(FileSystemItem Item, int Generation, string CacheKey);

    private const string MusicFormat = "56A3372E-CE9C-11D2-9F0E-006097C686F6";
    private const string MediaFormat = "64440490-4C8B-11D1-8B70-080036B11A03";
    private const string SummaryFormat = "F29F85E0-4FF9-1068-AB91-08002B27B3D9";

    private static NativeMethods.PROPERTYKEY _title = new(SummaryFormat, 2);
    private static NativeMethods.PROPERTYKEY _artist = new(MusicFormat, 2);
    private static NativeMethods.PROPERTYKEY _albumTitle = new(MusicFormat, 4);
    private static NativeMethods.PROPERTYKEY _albumArtist = new(MusicFormat, 13);
    private static NativeMethods.PROPERTYKEY _trackNumber = new(MusicFormat, 7);
    private static NativeMethods.PROPERTYKEY _duration = new(MediaFormat, 3);

    private static readonly BlockingCollection<TagRequest> Queue = new(new ConcurrentQueue<TagRequest>());
    private static readonly ConcurrentDictionary<string, MediaInfo> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock StartLock = new();

    private static int _generation;
    private static Thread? _worker;

    public readonly record struct MediaInfo(
        string? Title,
        string? Artist,
        string? Album,
        uint TrackNumber,
        TimeSpan Duration);

    public static void CancelPending() => Interlocked.Increment(ref _generation);

    public static void Request(FileSystemItem item)
    {
        if (item.IsFolder || item.HasMediaInfo || !item.IsAudio)
            return;

        var key = CacheKey(item);
        if (Cache.TryGetValue(key, out var cached))
        {
            item.ApplyMediaInfo(cached);
            return;
        }

        if (!Pending.TryAdd(key, 0))
            return;

        EnsureWorker();
        Queue.Add(new TagRequest(item, Volatile.Read(ref _generation), key));
    }

    private static void EnsureWorker()
    {
        if (_worker is not null)
            return;

        lock (StartLock)
        {
            if (_worker is not null)
                return;

            _worker = new Thread(Work)
            {
                IsBackground = true,
                Name = "Clearspace media tags",
                Priority = ThreadPriority.BelowNormal
            };

            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }
    }

    private static void Work()
    {
        foreach (var request in Queue.GetConsumingEnumerable())
        {
            try
            {
                if (request.Generation != Volatile.Read(ref _generation))
                    continue;

                var info = Read(request.Item.FullPath);

                if (Cache.Count > 20_000)
                    Cache.Clear();

                Cache[request.CacheKey] = info;

                if (request.Generation != Volatile.Read(ref _generation))
                    continue;

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null)
                    continue;

                dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () => request.Item.ApplyMediaInfo(info));
            }
            finally
            {
                Pending.TryRemove(request.CacheKey, out _);
            }
        }
    }

    private static MediaInfo Read(string path)
    {
        NativeMethods.IPropertyStore? store = null;

        try
        {
            var iid = NativeMethods.IID_IPropertyStore;
            var result = NativeMethods.SHGetPropertyStoreFromParsingName(
                path, IntPtr.Zero, NativeMethods.GPS_DEFAULT, ref iid, out store);

            if (result < 0 || store is null)
                return default;

            var artist = ReadString(store, ref _artist) ?? ReadString(store, ref _albumArtist);
            var hundredNanoseconds = ReadUInt64(store, ref _duration);

            return new MediaInfo(
                ReadString(store, ref _title),
                artist,
                ReadString(store, ref _albumTitle),
                ReadUInt32(store, ref _trackNumber),
                hundredNanoseconds > 0 ? TimeSpan.FromTicks((long)hundredNanoseconds) : TimeSpan.Zero);
        }
        catch (Exception)
        {
            return default;
        }
        finally
        {
            if (store is not null)
                Marshal.ReleaseComObject(store);
        }
    }

    private static string? ReadString(NativeMethods.IPropertyStore store, ref NativeMethods.PROPERTYKEY key)
    {
        var variant = default(NativeMethods.PROPVARIANT);

        try
        {
            if (store.GetValue(ref key, out variant) < 0)
                return null;

            if (NativeMethods.PropVariantToStringAlloc(ref variant, out var text) < 0 || text == IntPtr.Zero)
                return null;

            try
            {
                var value = Marshal.PtrToStringUni(text);
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            finally
            {
                NativeMethods.CoTaskMemFree(text);
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            NativeMethods.PropVariantClear(ref variant);
        }
    }

    private static uint ReadUInt32(NativeMethods.IPropertyStore store, ref NativeMethods.PROPERTYKEY key)
    {
        var variant = default(NativeMethods.PROPVARIANT);

        try
        {
            if (store.GetValue(ref key, out variant) < 0)
                return 0;

            return NativeMethods.PropVariantToUInt32(ref variant, out var value) < 0 ? 0 : value;
        }
        catch (Exception)
        {
            return 0;
        }
        finally
        {
            NativeMethods.PropVariantClear(ref variant);
        }
    }

    private static ulong ReadUInt64(NativeMethods.IPropertyStore store, ref NativeMethods.PROPERTYKEY key)
    {
        var variant = default(NativeMethods.PROPVARIANT);

        try
        {
            if (store.GetValue(ref key, out variant) < 0)
                return 0;

            return NativeMethods.PropVariantToUInt64(ref variant, out var value) < 0 ? 0 : value;
        }
        catch (Exception)
        {
            return 0;
        }
        finally
        {
            NativeMethods.PropVariantClear(ref variant);
        }
    }

    private static string CacheKey(FileSystemItem item)
        => $"{item.FullPath}|{item.DateModified.Ticks}";
}
