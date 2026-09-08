// Clearspace | Thumbnail extraction and caching.

using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clearspace.Models;
using Clearspace.Native;

namespace Clearspace.Services;

public static class ThumbnailService
{
    private sealed record ThumbnailRequest(FileSystemItem Item, int Size, int Generation, string CacheKey);

    private sealed class CacheEntry
    {
        public required string Key { get; init; }
        public required ImageSource Image { get; init; }
        public required long Bytes { get; init; }

        public required WeakReference<FileSystemItem> Owner { get; set; }
    }

    // Use LIFO so tiles that just entered view are decoded first.
    private static readonly BlockingCollection<ThumbnailRequest> Queue = new(new ConcurrentStack<ThumbnailRequest>());

    private const long MaxCacheBytes = 192L * 1024 * 1024;
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> CacheIndex = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<CacheEntry> CacheOrder = new();
    private static long _cacheBytes;

    private static readonly ConcurrentDictionary<string, ImageSource> ShellIconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Pending = new(StringComparer.OrdinalIgnoreCase);

    internal static long CacheBytes => Interlocked.Read(ref _cacheBytes);

    private static bool TryGetCached(string key, FileSystemItem item, out ImageSource image)
    {
        lock (CacheGate)
        {
            if (CacheIndex.TryGetValue(key, out var node))
            {
                CacheOrder.Remove(node);
                CacheOrder.AddFirst(node);

                node.Value.Owner = new WeakReference<FileSystemItem>(item);

                image = node.Value.Image;
                return true;
            }
        }

        image = null!;
        return false;
    }

    private static void StoreCached(string key, ImageSource image, FileSystemItem owner)
    {
        var bytes = EstimateBytes(image);
        List<CacheEntry>? evicted = null;

        lock (CacheGate)
        {
            if (CacheIndex.Remove(key, out var existing))
            {
                CacheOrder.Remove(existing);
                _cacheBytes -= existing.Value.Bytes;
            }

            var node = CacheOrder.AddFirst(new CacheEntry
            {
                Key = key,
                Image = image,
                Bytes = bytes,
                Owner = new WeakReference<FileSystemItem>(owner)
            });

            CacheIndex[key] = node;
            _cacheBytes += bytes;

            while (_cacheBytes > MaxCacheBytes && CacheOrder.Last is { } oldest)
            {
                CacheOrder.RemoveLast();
                CacheIndex.Remove(oldest.Value.Key);
                _cacheBytes -= oldest.Value.Bytes;

                (evicted ??= []).Add(oldest.Value);
            }
        }

        ReleaseEvicted(evicted);
    }

    private static void ReleaseEvicted(List<CacheEntry>? evicted)
    {
        if (evicted is null || evicted.Count == 0)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            () =>
            {
                foreach (var entry in evicted)
                {
                    if (entry.Owner.TryGetTarget(out var item) &&
                        ReferenceEquals(item.Thumbnail, entry.Image))
                        item.Thumbnail = null;
                }
            });
    }

    private static long EstimateBytes(ImageSource image) => image switch
    {
        BitmapSource bitmap => (long)bitmap.PixelWidth * bitmap.PixelHeight * 4,
        _ => 4096
    };

    private static int _generation;
    private static Thread? _worker;
    private static readonly Lock StartLock = new();

    public static void CancelPending() => Interlocked.Increment(ref _generation);

    public static void Invalidate(string path)
    {
        var prefix = path + "|";

        lock (CacheGate)
        {
            var doomed = CacheIndex
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Value)
                .ToList();

            foreach (var node in doomed)
            {
                CacheOrder.Remove(node);
                CacheIndex.Remove(node.Value.Key);
                _cacheBytes -= node.Value.Bytes;
            }
        }

        foreach (var key in Pending.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                Pending.TryRemove(key, out _);
        }

        var iconPrefix = $"path:{path}|";

        foreach (var key in ShellIconCache.Keys)
        {
            if (key.StartsWith(iconPrefix, StringComparison.OrdinalIgnoreCase))
                ShellIconCache.TryRemove(key, out _);
        }
    }

    public static void Request(FileSystemItem item, int size)
    {
        if (item.Thumbnail is not null)
            return;

        var key = CacheKey(item, size);

        if (TryGetCached(key, item, out var cached))
        {
            item.Thumbnail = cached;
            return;
        }

        if (!Pending.TryAdd(key, 0))
            return;

        EnsureWorker();
        Queue.Add(new ThumbnailRequest(item, size, Volatile.Read(ref _generation), key));
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
                Name = "Clearspace thumbnails",
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

                var source = Extract(request.Item, request.Size);

                if (source is null)
                    continue;

                StoreCached(request.CacheKey, source, request.Item);

                if (request.Generation != Volatile.Read(ref _generation))
                    continue;

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null)
                    continue;

                dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () => request.Item.Thumbnail = source);
            }
            finally
            {
                Pending.TryRemove(request.CacheKey, out _);
            }
        }
    }

    private static ImageSource? Extract(FileSystemItem item, int size)
    {
        var path = item.FullPath;

        // Cached previews only; requesting a thumbnail may download the file.
        if (item.IsOnlineOnly && !item.IsFolder)
        {
            var cachedPreview = ExtractShellImage(
                path,
                size,
                NativeMethods.SIIGBF_THUMBNAILONLY |
                NativeMethods.SIIGBF_INCACHEONLY |
                NativeMethods.SIIGBF_BIGGERSIZEOK);

            if (cachedPreview is not null)
                return cachedPreview;

            return GetShellIcon(item, size)
                ?? (MediaTypes.IsVideo(item.Extension)
                    ? ScalableIconService.Video
                    : ScalableIconService.File(item.Extension));
        }

        if (item.IsFolder && !item.IsDriveRoot && Directory.Exists(path))
        {
            if (FolderIconService.HasType(item))
            {
                var shellFolder = GetShellIcon(item, size) ?? IconService.GetLargeIcon(item);
                var typedFolder = FolderIconService.AddTypeBadge(item, shellFolder);
                if (typedFolder is not null)
                    return typedFolder;
            }

            var preview = CreateFolderPreview(path, size);
            if (preview is not null)
                return preview;
        }

        if (MediaTypes.IsImage(Path.GetExtension(path)))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.DecodePixelWidth = Math.Max(48, size);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile |
                                      BitmapCreateOptions.IgnoreImageCache;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception)
            {
            }
        }

        if (MediaTypes.IsVideo(Path.GetExtension(path)))
        {
            var videoFrame = VideoThumbnailService.Extract(path, size);
            if (videoFrame is not null)
                return videoFrame;
        }

        if (!item.IsFolder)
        {
            var shellThumbnail = ExtractShellImage(
                path,
                size,
                NativeMethods.SIIGBF_THUMBNAILONLY | NativeMethods.SIIGBF_BIGGERSIZEOK);

            if (shellThumbnail is not null)
                return shellThumbnail;
        }

        var shellIcon = GetShellIcon(item, size);
        if (shellIcon is not null)
            return shellIcon;

        if (!item.IsFolder)
            return MediaTypes.IsVideo(item.Extension)
                ? ScalableIconService.Video
                : ScalableIconService.File(item.Extension);

        var largeIcon = IconService.GetLargeIcon(item);
        if (largeIcon is not null)
            return largeIcon;

        return null;
    }

    private static ImageSource? GetShellIcon(FileSystemItem item, int size)
    {
        var extension = item.Extension;
        var pathSpecific = item.IsFolder ||
                           extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                           extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                           extension.Equals(".ico", StringComparison.OrdinalIgnoreCase);
        var key = pathSpecific
            ? $"path:{item.FullPath}|{size}"
            : $"type:{(string.IsNullOrEmpty(extension) ? "file" : extension)}|{size}";

        if (ShellIconCache.TryGetValue(key, out var cached))
            return cached;

        var icon = ExtractShellImage(
            item.FullPath,
            size,
            NativeMethods.SIIGBF_ICONONLY | NativeMethods.SIIGBF_BIGGERSIZEOK);

        if (icon is not null)
        {
            if (ShellIconCache.Count > 2048)
                ShellIconCache.Clear();

            ShellIconCache.TryAdd(key, icon);
        }

        return icon;
    }

    private static ImageSource? ExtractShellImage(string path, int size, int flags)
    {
        var bitmap = IntPtr.Zero;

        try
        {
            var iid = NativeMethods.IID_IShellItemImageFactory;
            var result = NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);

            if (result < 0 || factory is null)
                return null;

            try
            {
                var hr = factory.GetImage(new NativeMethods.SIZE(size, size), flags, out bitmap);
                if (hr < 0 || bitmap == IntPtr.Zero)
                    return null;

                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    bitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                source.Freeze();
                return source;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(bitmap);
        }
    }

    private static string CacheKey(FileSystemItem item, int size)
        => $"{item.FullPath}|{size}|{item.DateModified.Ticks}";

    private static ImageSource? CreateFolderPreview(string path, int size)
    {
        var previewFiles = new List<string>(3);

        try
        {
            foreach (var candidate in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
            {
                if (!MediaTypes.IsImage(Path.GetExtension(candidate)))
                    continue;

                previewFiles.Add(candidate);
                if (previewFiles.Count == 3)
                    break;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var previews = previewFiles
            .Select(file => LoadImage(file, size))
            .Where(image => image is not null)
            .Cast<BitmapSource>()
            .ToList();

        if (previews.Count == 0)
            return null;

        var pixels = Math.Max(128, size);
        var visual = new DrawingVisual();

        using (var drawing = visual.RenderOpen())
        {
            var width = (double)pixels;
            var height = (double)pixels;
            var back = new StreamGeometry();
            using (var geometry = back.Open())
            {
                geometry.BeginFigure(new Point(width * .10, height * .28), true, true);
                geometry.LineTo(new Point(width * .36, height * .28), true, false);
                geometry.LineTo(new Point(width * .45, height * .15), true, false);
                geometry.LineTo(new Point(width * .67, height * .15), true, false);
                geometry.LineTo(new Point(width * .77, height * .28), true, false);
                geometry.LineTo(new Point(width * .90, height * .28), true, false);
                geometry.LineTo(new Point(width * .90, height * .75), true, false);
                geometry.LineTo(new Point(width * .10, height * .75), true, false);
            }
            back.Freeze();
            drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(246, 184, 43)), null, back);

            var face = new Rect(width * .10, height * .34, width * .80, height * .43);
            drawing.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(255, 215, 111)),
                new Pen(new SolidColorBrush(Color.FromRgb(221, 158, 23)), Math.Max(1, width * .012)),
                face,
                width * .045,
                width * .045);

            var previewBounds = new Rect(width * .17, height * .44, width * .66, height * .22);
            drawing.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(68, 65, 58)),
                null,
                previewBounds,
                width * .018,
                width * .018);

            if (previews.Count == 1)
            {
                DrawCroppedImage(drawing, previews[0], previewBounds, width * .025);
            }
            else if (previews.Count > 1)
            {
                var gap = width * .025;
                var tileWidth = (previewBounds.Width - gap) / 2;
                DrawCroppedImage(drawing, previews[0], new Rect(previewBounds.X, previewBounds.Y, tileWidth, previewBounds.Height), width * .02);
                DrawCroppedImage(drawing, previews[1], new Rect(previewBounds.X + tileWidth + gap, previewBounds.Y, tileWidth, previewBounds.Height), width * .02);
            }

            drawing.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                new Pen(new SolidColorBrush(Color.FromArgb(76, 255, 255, 255)), Math.Max(1, width * .006)),
                previewBounds,
                width * .018,
                width * .018);
        }

        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource? LoadImage(string path, int size)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.DecodePixelWidth = Math.Max(64, size / 3);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile |
                                  BitmapCreateOptions.IgnoreImageCache;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void DrawCroppedImage(DrawingContext drawing, BitmapSource image, Rect bounds, double radius)
    {
        var scale = Math.Max(bounds.Width / image.PixelWidth, bounds.Height / image.PixelHeight);
        var size = new Size(image.PixelWidth * scale, image.PixelHeight * scale);
        var destination = new Rect(
            bounds.X + (bounds.Width - size.Width) / 2,
            bounds.Y + (bounds.Height - size.Height) / 2,
            size.Width,
            size.Height);

        drawing.PushClip(new RectangleGeometry(bounds, radius, radius));
        drawing.DrawImage(image, destination);
        drawing.Pop();
    }
}
