// Clearspace | Media metadata extraction.
//
// CHANGED (folder types, step 2): also reads the pixel size of images and videos for the Dimensions
// column (Design & 3D, Screenshots), on the same background thread and with its own cache.
// CHANGED (folder types, step 3):
//   - Videos and documents are read too, not only songs: videos for their length, documents for their
//     own title, authors and page count (Documents, Research).
//   - PDFs: Windows has no built-in PDF property reader, so when Windows returns nothing Clearspace reads
//     the title, authors and page count itself (PdfInfo, best effort from the start and end of the file).
//   - Source (Downloads): the site a file came from, from the "Zone.Identifier" note Windows attaches
//     to downloaded files.

using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Clearspace.Models;
using Clearspace.Native;

namespace Clearspace.Services;

public static class MediaPropertyService
{
    // CHANGED (step 3): one queue for every kind of read.
    private enum ReadKind { Media, Dimensions, Source }

    private sealed record TagRequest(FileSystemItem Item, int Generation, string CacheKey, ReadKind Kind);

    private const string MusicFormat = "56A3372E-CE9C-11D2-9F0E-006097C686F6";
    private const string MediaFormat = "64440490-4C8B-11D1-8B70-080036B11A03";
    private const string SummaryFormat = "F29F85E0-4FF9-1068-AB91-08002B27B3D9";

    private static NativeMethods.PROPERTYKEY _title = new(SummaryFormat, 2);
    private static NativeMethods.PROPERTYKEY _author = new(SummaryFormat, 4);      // NEW (step 3): System.Author
    private static NativeMethods.PROPERTYKEY _pageCount = new(SummaryFormat, 14);  // NEW (step 3): System.Document.PageCount
    private static NativeMethods.PROPERTYKEY _artist = new(MusicFormat, 2);
    private static NativeMethods.PROPERTYKEY _albumTitle = new(MusicFormat, 4);
    private static NativeMethods.PROPERTYKEY _albumArtist = new(MusicFormat, 13);
    private static NativeMethods.PROPERTYKEY _trackNumber = new(MusicFormat, 7);
    private static NativeMethods.PROPERTYKEY _duration = new(MediaFormat, 3);

    // NEW (step 2): System.Image.HorizontalSize/VerticalSize and System.Video.FrameWidth/FrameHeight.
    private const string ImageFormat = "6444048F-4C8B-11D1-8B70-080036B11A03";
    private const string VideoFormat = "64440491-4C8B-11D1-8B70-080036B11A03";
    private static NativeMethods.PROPERTYKEY _imageWidth = new(ImageFormat, 3);
    private static NativeMethods.PROPERTYKEY _imageHeight = new(ImageFormat, 4);
    private static NativeMethods.PROPERTYKEY _videoWidth = new(VideoFormat, 3);
    private static NativeMethods.PROPERTYKEY _videoHeight = new(VideoFormat, 4);

    // NEW (step 3): documents whose title, authors and pages are worth reading.
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".docm", ".odt", ".rtf", ".xls", ".xlsx", ".ppt", ".pptx", ".odp", ".epub", ".xps", ".oxps"
    };

    private static readonly BlockingCollection<TagRequest> Queue = new(new ConcurrentQueue<TagRequest>());
    private static readonly ConcurrentDictionary<string, MediaInfo> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (uint Width, uint Height)> DimensionCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string?> SourceCache = new(StringComparer.OrdinalIgnoreCase); // NEW (step 3)
    private static readonly ConcurrentDictionary<string, byte> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock StartLock = new();

    private static int _generation;
    private static Thread? _worker;

    // CHANGED (step 3): Authors and PageCount for documents.
    public readonly record struct MediaInfo(
        string? Title,
        string? Artist,
        string? Album,
        uint TrackNumber,
        TimeSpan Duration,
        string? Authors = null,
        uint PageCount = 0);

    public static void CancelPending() => Interlocked.Increment(ref _generation);

    // NEW (step 3): the files Request reads (songs, videos, documents).
    public static bool HasReadableProperties(FileSystemItem item)
        => !item.IsFolder && (item.IsAudio || item.IsVideoFile || DocumentExtensions.Contains(item.Extension));

    // CHANGED (step 3): songs, videos and documents (was songs only).
    public static void Request(FileSystemItem item)
    {
        if (item.HasMediaInfo || !HasReadableProperties(item))
            return;

        var key = CacheKey(item);
        if (Cache.TryGetValue(key, out var cached))
        {
            item.ApplyMediaInfo(cached);
            return;
        }

        Enqueue(item, key, ReadKind.Media);
    }

    // NEW (step 2): pixel size for the Dimensions column. Only images and videos have one.
    public static void RequestDimensions(FileSystemItem item)
    {
        if (item.IsFolder || item.HasDimensions ||
            !(MediaTypes.IsImage(item.Extension) || MediaTypes.IsVideo(item.Extension)))
            return;

        var key = "px|" + CacheKey(item);
        if (DimensionCache.TryGetValue(key, out var cached))
        {
            item.ApplyDimensions(cached.Width, cached.Height);
            return;
        }

        Enqueue(item, key, ReadKind.Dimensions);
    }

    // NEW (step 3): the site a downloaded file came from, for the Source column.
    public static void RequestSource(FileSystemItem item)
    {
        if (item.IsFolder || item.HasSource)
            return;

        var key = "src|" + CacheKey(item);
        if (SourceCache.TryGetValue(key, out var cached))
        {
            item.ApplySource(cached);
            return;
        }

        Enqueue(item, key, ReadKind.Source);
    }

    private static void Enqueue(FileSystemItem item, string key, ReadKind kind)
    {
        if (!Pending.TryAdd(key, 0))
            return;

        EnsureWorker();
        Queue.Add(new TagRequest(item, Volatile.Read(ref _generation), key, kind));
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

                Action apply;
                var item = request.Item;

                switch (request.Kind)
                {
                    case ReadKind.Dimensions:
                    {
                        var size = ReadDimensions(item.FullPath, MediaTypes.IsVideo(item.Extension));
                        if (DimensionCache.Count > 20_000) DimensionCache.Clear();
                        DimensionCache[request.CacheKey] = size;
                        apply = () => item.ApplyDimensions(size.Width, size.Height);
                        break;
                    }

                    case ReadKind.Source:
                    {
                        var source = ReadSource(item.FullPath);
                        if (SourceCache.Count > 20_000) SourceCache.Clear();
                        SourceCache[request.CacheKey] = source;
                        apply = () => item.ApplySource(source);
                        break;
                    }

                    default:
                    {
                        var info = Read(item.FullPath);

                        // NEW (step 3): Windows reads no PDF properties by itself.
                        if (item.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
                            (info.Title is null || info.Authors is null || info.PageCount == 0) &&
                            PdfInfo.TryRead(item.FullPath) is { } pdf)
                        {
                            info = info with
                            {
                                Title = info.Title ?? pdf.Title,
                                Authors = info.Authors ?? pdf.Authors,
                                PageCount = info.PageCount > 0 ? info.PageCount : pdf.PageCount
                            };
                        }

                        if (Cache.Count > 20_000) Cache.Clear();
                        Cache[request.CacheKey] = info;
                        apply = () => item.ApplyMediaInfo(info);
                        break;
                    }
                }

                if (request.Generation != Volatile.Read(ref _generation))
                    continue;

                Application.Current?.Dispatcher?.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, apply);
            }
            catch (Exception)
            {
                // One unreadable file must not stop the worker.
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
                hundredNanoseconds > 0 ? TimeSpan.FromTicks((long)hundredNanoseconds) : TimeSpan.Zero,
                ReadString(store, ref _author),       // NEW (step 3)
                ReadUInt32(store, ref _pageCount));   // NEW (step 3)
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

    // NEW (step 2)
    private static (uint Width, uint Height) ReadDimensions(string path, bool isVideo)
    {
        NativeMethods.IPropertyStore? store = null;

        try
        {
            var iid = NativeMethods.IID_IPropertyStore;
            var result = NativeMethods.SHGetPropertyStoreFromParsingName(
                path, IntPtr.Zero, NativeMethods.GPS_DEFAULT, ref iid, out store);

            if (result < 0 || store is null)
                return default;

            return isVideo
                ? (ReadUInt32(store, ref _videoWidth), ReadUInt32(store, ref _videoHeight))
                : (ReadUInt32(store, ref _imageWidth), ReadUInt32(store, ref _imageHeight));
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

    // NEW (step 3): "github.com" from the file's Zone.Identifier stream; null when it has none.
    private static string? ReadSource(string path)
    {
        try
        {
            var note = File.ReadAllText(path + ":Zone.Identifier");
            return ParseZoneIdentifier(note);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    internal static string? ParseZoneIdentifier(string note)
    {
        string? host = null, referrer = null;
        var zone = -1;

        foreach (var raw in note.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("HostUrl=", StringComparison.OrdinalIgnoreCase)) host = line[8..];
            else if (line.StartsWith("ReferrerUrl=", StringComparison.OrdinalIgnoreCase)) referrer = line[12..];
            else if (line.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase) && int.TryParse(line[7..], out var id)) zone = id;
        }

        // The page you downloaded from says more than the download server ("github.com", not a CDN).
        foreach (var url in new[] { referrer, host })
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0)
                return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        }

        return zone switch
        {
            3 => "Internet",
            4 => "Blocked site",
            1 => "Local network",
            _ => null
        };
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
