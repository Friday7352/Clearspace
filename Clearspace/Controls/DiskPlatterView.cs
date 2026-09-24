using System.IO;
using BitOperations = System.Numerics.BitOperations;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clearspace.Services;
using Microsoft.Win32.SafeHandles;

namespace Clearspace.Controls;

// NEW (round 46): experimental. The drive as it is laid out on disk: where the clusters of the largest
// files actually are, read from the file system (the same call a defragmenter uses), drawn either as a
// spinning platter - cluster 0 on the outer edge, the end of the drive at the hub - or as the familiar
// defragmenter grid. Colours follow the folder you are in: each item inside it keeps its colour from
// the list and the map, files elsewhere on the drive are grey.
//
// It is honest about what it knows. Only the largest files are read (about twenty thousand, plus the
// largest in the folder you are in), so the rest of the used space is shown from the volume's
// allocation bitmap as "used, not read" when Windows allows reading that, and as unknown otherwise.
// On an SSD the positions are logical: the drive's controller decides where data physically lives.
//
// Everything that touches the disk or the snapshot runs on one background worker; the UI thread only
// copies finished pixels into a bitmap, spins it and draws the head arm.
internal enum PlatterStyle { Platter, Grid }

public sealed class DiskPlatterView : Grid
{
    // ------------------------------------------------------------------ tuning
    private const int Buckets = 1 << 18;          // the drive's clusters, grouped; what the pictures are drawn from
    private const int DriveFiles = 20_000;        // largest files on the drive whose clusters are read
    private const int FolderFiles = 4_000;        // plus the largest inside the folder you are in
    private const int MaxFiles = 64_000;
    private const long DriveMinBytes = 64 * 1024; // smaller files mostly live inside the MFT anyway
    private const int Batch = 1_500;              // files read between progressive redraws
    private const int PlatterPixels = 1024;
    private const double OuterRadius = .485, InnerRadius = .17, TrackPitch = 3.2;   // radii as fractions of the side; pitch in pixels
    private const double ParkedArm = .51, SpinSpeed = 9;                           // arm rest radius; degrees per second
    private const int CellPitch = 7, CellSize = 6;
    private const int Named = 10;                 // items in the folder with their own colour in the legend

    public event Action<int>? FolderOpened;
    public event Action<int>? ItemSelected;

    private readonly Image _image = new() { Stretch = Stretch.Uniform, Margin = new Thickness(12, 66, 12, 12), RenderTransformOrigin = new Point(.5, .5) };
    private readonly RotateTransform _spin = new();
    private readonly PlatterChrome _chrome;
    private readonly TextBlock _status = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly WrapPanel _legend = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _card = new();
    private readonly TextBlock _cardTitle = new() { FontWeight = FontWeights.SemiBold, FontSize = 13, MaxWidth = 420, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _cardDetail = new() { FontSize = 11.5, MaxWidth = 420, TextWrapping = TextWrapping.Wrap };
    private WriteableBitmap? _bitmap;

    // What the worker should draw; replaced on the UI thread, read by the worker.
    private sealed record Request(DiskUsageSnapshot Snapshot, int Folder, string FolderPath, PlatterStyle Style, int GridWidth, int GridHeight);
    private Request? _request;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private CancellationTokenSource? _work;
    private Disk? _disk;
    private PlatterStyle _shownStyle;      // style of the bitmap on screen
    private int _shownWidth, _shownHeight;

    // Spin and head arm.
    private bool _spinning;
    private double _angle, _speed = SpinSpeed, _arm = ParkedArm, _armTarget = ParkedArm, _lastFrame = -1;
    private Point? _mouse;
    private (int Kind, int File) _hover = (-1, -1);
    private System.Windows.Threading.DispatcherTimer? _resize;

    private static readonly Brush Ink = Frozen(Color.FromRgb(0xEC, 0xE9, 0xE3));
    private static readonly Brush InkMuted = Frozen(Color.FromRgb(0x9C, 0x96, 0x8D));
    private static readonly Color Surface = Color.FromRgb(0x1A, 0x19, 0x17);

    // Bucket colours that are not a file read from the drive.
    private const uint ElsewhereColor = 0x77716A, UsedColor = 0x45413C, FreeColor = 0x262422, UnknownColor = 0x34312D;
    private const uint HubColor = 0x3A3834, SpindleColor = 0x24221F;

    public DiskPlatterView()
    {
        Background = Frozen(Surface);
        ClipToBounds = true;
        Focusable = true;
        _image.RenderTransform = _spin;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.Linear);
        Children.Add(_image);
        _chrome = new PlatterChrome(this) { IsHitTestVisible = false };
        Children.Add(_chrome);
        _status.Foreground = InkMuted;
        var header = new StackPanel { Margin = new Thickness(12, 10, 12, 0), VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        header.Children.Add(_status);
        header.Children.Add(_legend);
        Children.Add(header);
        _cardTitle.Foreground = Ink;
        _cardDetail.Foreground = InkMuted;
        _card.Child = new StackPanel { Children = { _cardTitle, _cardDetail } };
        _card.Background = Frozen(Color.FromArgb(0xF4, 0x23, 0x22, 0x20));
        _card.BorderBrush = Frozen(Color.FromRgb(0x3A, 0x37, 0x32));
        _card.BorderThickness = new Thickness(1);
        _card.CornerRadius = new CornerRadius(8);
        _card.Padding = new Thickness(12, 9, 12, 10);
        _card.Visibility = Visibility.Collapsed;
        _overlay.Children.Add(_card);
        Children.Add(_overlay);
        IsVisibleChanged += (_, _) => OnVisibility();
        SizeChanged += (_, _) => OnResized();
    }

    /// <summary>Shows the drive of a snapshot, coloured for one of its folders.</summary>
    internal void Show(DiskUsageSnapshot snapshot, int folder, PlatterStyle style)
    {
        // A new snapshot of the same drive keeps what was read: paths do not change with it, and the worker
        // starts over by itself when the drive does. Refresh and deletions are handled by Forget and Recheck.
        var (width, height) = GridSize();
        lock (_gate) _request = new Request(snapshot, folder, snapshot.PathFor(folder), style, width, height);
        if (style != _shownStyle) { _card.Visibility = Visibility.Collapsed; _hover = (-1, -1); }
        EnsureWorker();
        _signal.Release();
        UpdateSpin();
    }

    /// <summary>Empties the view, with a message - a drive that is waiting for its index.</summary>
    internal void Clear(string? message)
    {
        lock (_gate) _request = null;
        _work?.Cancel();
        _work = null;
        _bitmap = null;
        _image.Source = null;
        _legend.Children.Clear();
        _status.Text = message ?? "";
        _card.Visibility = Visibility.Collapsed;
        _hover = (-1, -1);
        _chrome.InvalidateVisual();
        UpdateSpin();
    }

    /// <summary>Reads the drive again from the start (Refresh). What is on screen stays until the new reading has a picture.</summary>
    internal void Forget()
    {
        // Hover and clicks go by the disk the picture was drawn from, so it can be dropped at once.
        Volatile.Write(ref _disk, null);
        _signal.Release();
    }

    /// <summary>Files were deleted or moved: drops the ones that are gone.</summary>
    internal void Recheck()
    {
        Interlocked.Exchange(ref _recheck, 1);
        _signal.Release();
    }

    private int _recheck;

    private (int Width, int Height) GridSize()
        => ((int)Math.Max(CellPitch * 8, ActualWidth - 24), (int)Math.Max(CellPitch * 8, ActualHeight - 78));

    private void OnResized()
    {
        _chrome.InvalidateVisual();
        if (_request is not { Style: PlatterStyle.Grid }) return;
        // The grid is drawn at the control's size; wait for the resize to settle before redrawing it.
        _resize ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(250), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                _resize!.Stop();
                if (_request is not { Style: PlatterStyle.Grid } request) return;
                var (width, height) = GridSize();
                if (width == request.GridWidth && height == request.GridHeight) return;
                lock (_gate) _request = request with { GridWidth = width, GridHeight = height };
                _signal.Release();
            }, Dispatcher);
        _resize.Stop();
        _resize.Start();
    }

    private void OnVisibility()
    {
        if (IsVisible) { EnsureWorker(); _signal.Release(); }
        else { _work?.Cancel(); _work = null; }
        UpdateSpin();
    }

    // One worker at a time: a new one (the view shown again) waits for the cancelled one to finish, since
    // both would otherwise write the same Disk.
    private Task _worker = Task.CompletedTask;

    private void EnsureWorker()
    {
        if (_work is not null || !IsVisible || _request is null) return;
        var work = _work = new CancellationTokenSource();
        var previous = _worker;
        _worker = Task.Run(async () =>
        {
            try { await previous; } catch (Exception) { }
            await RunAsync(work.Token);
        });
    }

    // ------------------------------------------------------------------ worker

    private sealed class Disk
    {
        public required string Root;
        public required long ClusterBytes, TotalClusters, UsedBytes;
        public required int BucketCount;                      // Buckets, or fewer on a drive with fewer clusters
        public required double ClustersPerBucket;             // at least 1
        public readonly int[] Owner = Filled(Buckets, -1);   // bucket -> index into Files, or -1
        public byte[]? Used;                                  // bucket occupancy from the allocation bitmap, 0-255
        public bool? SolidState;
        public readonly DiskFile?[] Files = new DiskFile?[MaxFiles];
        public volatile int FileCount;                         // written by the worker only; files before it are complete
        public readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> FoldersListed = new(StringComparer.OrdinalIgnoreCase);
        public readonly Queue<string> Pending = new(), Urgent = new();   // still to read: the drive's, the folder's
        public bool DriveListed;
        public long ClustersRead;
        public int Pieces, Failed;
        public int Remaining => Urgent.Count + Pending.Count;
    }

    private sealed record DiskFile(string Path, long Bytes, long Clusters, int Pieces, long FirstLcn);

    private Request? CurrentRequest()
    {
        lock (_gate) return _request;
    }

    private bool DriveChanged(Disk disk)
        => CurrentRequest() is not { } request || !string.Equals(disk.Root, VolumeRoot(request.Snapshot.Root), StringComparison.OrdinalIgnoreCase);

    private async Task RunAsync(CancellationToken token)
    {
        Request? drawn = null;
        var drawnFiles = -1;
        var buffer = new byte[16 + 16 * 512];
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (CurrentRequest() is not { } request) return;
                var disk = Volatile.Read(ref _disk);
                if (disk is null || DriveChanged(disk))
                {
                    Post(null, "Reading how the drive is laid out…", null, token);
                    disk = OpenDisk(request.Snapshot.Root, token);
                    if (disk is null)
                    {
                        Post(null, "This drive does not report where its files are stored.", [], token);
                        await _signal.WaitAsync(token);
                        continue;
                    }
                    Volatile.Write(ref _disk, disk);
                    drawn = null;
                }
                if (Interlocked.Exchange(ref _recheck, 0) == 1)
                {
                    try { Prune(disk, request.FolderPath, token); }
                    catch (OperationCanceledException) { Interlocked.Exchange(ref _recheck, 1); throw; }   // finish it next time
                    drawn = null;
                }

                // What to read: the largest files on the drive once, and the largest in each folder visited.
                if (!disk.DriveListed)
                {
                    foreach (var path in LargestFiles(request.Snapshot, 0, DriveFiles, DriveMinBytes, disk, token)) disk.Pending.Enqueue(path);
                    disk.DriveListed = true;
                }
                if (request.Folder != 0 && !disk.FoldersListed.Contains(request.FolderPath))
                {
                    foreach (var path in LargestFiles(request.Snapshot, request.Folder, FolderFiles, 1, disk, token)) disk.Urgent.Enqueue(path);
                    disk.FoldersListed.Add(request.FolderPath);
                }

                if (disk.Remaining > 0 && disk.FileCount < MaxFiles)
                {
                    for (var read = 0; read < Batch && disk.FileCount < MaxFiles && (disk.Urgent.TryDequeue(out var path) || disk.Pending.TryDequeue(out path)); read++)
                    {
                        if (read % 64 == 0) { token.ThrowIfCancellationRequested(); if (DriveChanged(disk)) break; }
                        if (disk.Known.Add(path)) ReadFile(disk, path, buffer);
                    }
                    if (DriveChanged(disk)) continue;   // another drive was picked: start on it, draw nothing of this one
                    // A drive that reports nothing for the first batch (network, FAT, no permission) says so.
                    if (disk.FileCount == 0 && disk.Failed >= Math.Min(Batch, 200)) { disk.Pending.Clear(); disk.Urgent.Clear(); }
                }
                else if (drawn == request && drawnFiles == disk.FileCount)
                {
                    await _signal.WaitAsync(token);
                    while (_signal.CurrentCount > 0) _signal.Wait(0);
                    continue;
                }

                if (CurrentRequest() is not { } latest) return;   // draw the latest wishes, not the ones the batch started with
                Draw(disk, latest, disk.FileCount < MaxFiles ? disk.Remaining : 0, token);
                drawn = latest;
                drawnFiles = disk.FileCount;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Post(null, $"The disk view stopped: {exception.Message}", null, token);
            // Let the next Show start a new worker instead of waiting on this one.
            _ = Dispatcher.InvokeAsync(() => { if (_work is { } work && work.Token == token) _work = null; });
        }
    }

    // Drops files that no longer exist, and their clusters; they show as the bitmap last saw them until
    // the drive is read again. Only files under the folder being looked at are checked - that is where
    // the analyzer deletes from - so a single delete does not stat every file read.
    private static void Prune(Disk disk, string folder, CancellationToken token)
    {
        var prefix = folder.EndsWith('\\') ? folder : folder + "\\";
        var gone = new HashSet<int>();
        for (var i = 0; i < disk.FileCount; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            if (disk.Files[i] is not { } file || !file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || File.Exists(file.Path)) continue;
            gone.Add(i);
            disk.Files[i] = null;
            disk.Known.Remove(file.Path);
            disk.ClustersRead -= file.Clusters;
            if (file.Pieces > 1) disk.Pieces--;
        }
        if (gone.Count == 0) return;
        for (var b = 0; b < disk.BucketCount; b++)
            if (disk.Owner[b] >= 0 && gone.Contains(disk.Owner[b])) disk.Owner[b] = -1;
    }

    // The largest files under a folder (the whole drive for the root) that have not been read yet.
    private static List<string> LargestFiles(DiskUsageSnapshot snapshot, int folder, int limit, long minimum, Disk disk, CancellationToken token)
    {
        var heap = new PriorityQueue<int, long>(limit + 1);
        var stack = new Stack<int>();
        var children = new List<int>();
        stack.Push(folder);
        var visited = 0;
        while (stack.TryPop(out var id))
        {
            children.Clear();
            snapshot.ChildIds(id, children);
            foreach (var child in children)
            {
                if ((++visited & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
                if (snapshot.IsFolderEntry(child)) { stack.Push(child); continue; }
                var bytes = snapshot.BytesOf(child);
                if (bytes < minimum) continue;
                if (heap.Count < limit) heap.Enqueue(child, bytes);
                else if (heap.TryPeek(out _, out var smallest) && bytes > smallest) heap.EnqueueDequeue(child, bytes);
            }
        }
        var found = new List<(string Path, long Bytes)>(heap.Count);
        while (heap.TryDequeue(out var id, out var bytes))
        {
            var path = snapshot.PathFor(id);
            if (!disk.Known.Contains(path)) found.Add((path, bytes));
        }
        found.Reverse();   // largest first
        return found.ConvertAll(file => file.Path);
    }

    // Reads where one file's clusters are and marks them on the drive.
    private static void ReadFile(Disk disk, string path, byte[] buffer)
    {
        using var handle = CreateFileW(@"\\?\" + path, FileReadAttributes, 7, IntPtr.Zero, OpenExisting,
            BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) { disk.Failed++; return; }
        // Cloud placeholders and links have no clusters of their own worth showing - and asking a
        // placeholder about its data could make it download.
        if (GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out var info, 8) && (info.Attributes & SkipAttributes) != 0) return;

        var index = disk.FileCount;
        long vcn = 0, clusters = 0, first = -1, end = -1;
        var pieces = 0;
        while (true)
        {
            var ok = DeviceIoControl(handle, FsctlGetRetrievalPointers, ref vcn, 8, buffer, buffer.Length, out var returned, IntPtr.Zero);
            var error = ok ? 0 : Marshal.GetLastPInvokeError();
            if (!ok && error != ErrorMoreData) { if (error != ErrorHandleEof && clusters == 0) disk.Failed++; break; }
            if (returned < 16) break;
            var count = BitConverter.ToInt32(buffer, 0);
            var previous = BitConverter.ToInt64(buffer, 8);
            for (var i = 0; i < count && 32 + i * 16 <= returned; i++)
            {
                var next = BitConverter.ToInt64(buffer, 16 + i * 16);
                var lcn = BitConverter.ToInt64(buffer, 24 + i * 16);
                var length = next - previous;
                if (lcn >= 0 && length > 0)
                {
                    if (first < 0) first = lcn;
                    if (lcn != end) pieces++;   // runs that continue where the last one ended are one piece
                    end = lcn + length;
                    clusters += length;
                    Paint(disk, lcn, length, index);
                }
                previous = next;
            }
            if (ok || count == 0) break;
            vcn = previous;
        }
        if (clusters == 0) return;
        disk.Files[index] = new DiskFile(path, clusters * disk.ClusterBytes, clusters, pieces, first);
        disk.FileCount = index + 1;
        disk.ClustersRead += clusters;
        if (pieces > 1) disk.Pieces++;
    }

    // Marks a run of clusters as a file's. Buckets it covers completely always take it; the partly
    // covered ones at its ends only when nothing else has claimed them.
    private static void Paint(Disk disk, long lcn, long length, int file)
    {
        var first = (long)(lcn / disk.ClustersPerBucket);
        var last = (long)((lcn + length - 1) / disk.ClustersPerBucket);
        if (first >= disk.BucketCount) return;
        last = Math.Min(last, disk.BucketCount - 1);
        for (var bucket = first; bucket <= last; bucket++)
            if ((bucket > first && bucket < last) || disk.Owner[bucket] < 0)
                disk.Owner[bucket] = file;
    }

    private static string VolumeRoot(string path) => Path.GetPathRoot(path) is { Length: > 0 } root ? root : path;

    private static Disk? OpenDisk(string root, CancellationToken token)
    {
        var volume = VolumeRoot(root);
        if (!GetDiskFreeSpaceW(volume, out var sectorsPerCluster, out var bytesPerSector, out _, out var totalClusters)) return null;
        var clusterBytes = (long)sectorsPerCluster * bytesPerSector;
        if (clusterBytes <= 0) return null;
        long total = totalClusters, freeBytes = 0;
        if (GetDiskFreeSpaceExW(volume, out _, out var totalBytes, out var free))
        {
            total = Math.Max(total, (long)(totalBytes / (ulong)clusterBytes));
            freeBytes = (long)free;
        }
        if (total <= 0) return null;
        var buckets = (int)Math.Min(Buckets, total);
        var disk = new Disk
        {
            Root = volume, ClusterBytes = clusterBytes, TotalClusters = total, BucketCount = buckets,
            UsedBytes = Math.Max(0, total * clusterBytes - freeBytes), ClustersPerBucket = total / (double)buckets,
        };
        // Drive letters only: the allocation bitmap and the drive type come from the volume itself.
        if (volume.Length == 3 && volume[1] == ':' && char.IsAsciiLetter(volume[0]))
        {
            using var handle = OpenVolume(@"\\.\" + volume[..2]);
            if (handle is not null)
            {
                disk.SolidState = SolidState(handle);
                ReadBitmap(disk, handle, token);
            }
        }
        return disk;
    }

    private static SafeFileHandle? OpenVolume(string device)
    {
        // Reading the allocation bitmap needs a volume opened for reading, which needs administrator rights;
        // without them a query-only handle still says whether the drive is solid state.
        foreach (var access in new[] { GenericRead, FileReadAttributes, 0u })
        {
            var handle = CreateFileW(device, access, 3, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            handle.Dispose();
        }
        return null;
    }

    private static bool? SolidState(SafeFileHandle volume)
    {
        var query = new byte[12];
        BitConverter.TryWriteBytes(query.AsSpan(0), StorageDeviceSeekPenaltyProperty);   // QueryType 0: standard query
        var answer = new byte[12];
        return DeviceIoControlBytes(volume, IoctlStorageQueryProperty, query, query.Length, answer, answer.Length, out var returned, IntPtr.Zero)
            && returned >= 9 ? answer[8] == 0 : null;
    }

    // Counts the used clusters in every bucket from the volume's allocation bitmap, a megabyte at a time.
    private static void ReadBitmap(Disk disk, SafeFileHandle volume, CancellationToken token)
    {
        var counts = new long[Buckets];
        var buffer = new byte[16 + (1 << 20)];
        long lcn = 0, used = 0;
        while (lcn < disk.TotalClusters)
        {
            token.ThrowIfCancellationRequested();
            var ok = DeviceIoControl(volume, FsctlGetVolumeBitmap, ref lcn, 8, buffer, buffer.Length, out var returned, IntPtr.Zero);
            if (!ok && Marshal.GetLastPInvokeError() != ErrorMoreData) return;   // not allowed: leave Used null
            if (returned <= 16) return;
            var start = BitConverter.ToInt64(buffer, 0);
            var size = BitConverter.ToInt64(buffer, 8);
            var bits = Math.Min(size, (long)(returned - 16) * 8);
            var words = MemoryMarshal.Cast<byte, ulong>(buffer.AsSpan(16, (int)((bits + 63) / 64 * 8)));
            for (var w = 0; w < words.Length; w++)
            {
                var word = words[w];
                if (word == 0) continue;
                var cluster = start + w * 64L;
                var valid = Math.Min(64, bits - w * 64L);
                if (valid < 64) word &= (1UL << (int)valid) - 1;
                var firstBucket = (long)(cluster / disk.ClustersPerBucket);
                var lastBucket = (long)((cluster + valid - 1) / disk.ClustersPerBucket);
                if (firstBucket == lastBucket && firstBucket < disk.BucketCount) { var n = BitOperations.PopCount(word); counts[firstBucket] += n; used += n; continue; }
                for (; word != 0; word &= word - 1)
                {
                    var bucket = (long)((cluster + BitOperations.TrailingZeroCount(word)) / disk.ClustersPerBucket);
                    if (bucket < disk.BucketCount) counts[bucket]++;
                    used++;
                }
            }
            if (ok || bits <= 0) break;
            lcn = start + bits;
        }
        var occupancy = new byte[Buckets];
        for (var b = 0; b < disk.BucketCount; b++)
        {
            // Clusters that fall in this bucket: its share of the drive, at least one.
            var span = Math.Max(1, Math.Floor((b + 1) * disk.ClustersPerBucket) - Math.Floor(b * disk.ClustersPerBucket));
            occupancy[b] = (byte)Math.Clamp(counts[b] * 255 / span, 0, 255);
        }
        disk.Used = occupancy;
        disk.UsedBytes = used * disk.ClusterBytes;
    }

    // ------------------------------------------------------------------ drawing (worker)

    private sealed record Legend((string Name, uint Color)[] Entries, string Status);

    private void Draw(Disk disk, Request request, int remaining, CancellationToken token)
    {
        var colors = FileColors(disk, request, out var named);
        int width, height;
        int[] pixels;
        if (request.Style == PlatterStyle.Platter)
        {
            width = height = PlatterPixels;
            pixels = RasterPlatter(disk, colors, token);
        }
        else
        {
            width = request.GridWidth; height = request.GridHeight;
            pixels = RasterGrid(disk, colors, width, height, token);
        }

        var entries = new List<(string, uint)>(named);
        var atRoot = request.Folder == 0;
        if (!atRoot) entries.Add(("Elsewhere on the drive", ElsewhereColor));
        if (disk.Used is not null) { entries.Add(("Used, not read", UsedColor)); entries.Add(("Free", FreeColor)); }
        else entries.Add(("Not read", UnknownColor));

        if (disk.FileCount == 0 && disk.Failed > 0 && remaining == 0)
        {
            Post(null, "Windows did not report where files on this drive are stored (it may be a network or non-NTFS drive).", [], token);
            return;
        }
        var read = disk.ClustersRead * disk.ClusterBytes;
        var share = disk.UsedBytes > 0 ? Math.Min(1, read / (double)disk.UsedBytes) : 0;
        var status = $"Disk (experimental) · {disk.Root.TrimEnd('\\')} · {DiskUsageSnapshot.FormatBytes(disk.ClusterBytes)} clusters · " +
            $"{disk.FileCount:N0} largest files read, {share:P0} of used space · {disk.Pieces:N0} in more than one piece" +
            (remaining > 0 ? $" · reading {remaining:N0} more…" : "") +
            (disk.SolidState == true ? " · SSD: positions are logical, the drive decides where data physically lives" : "") +
            (disk.Used is null ? " · run as administrator to see free space" : "");
        Post(new Frame(pixels, width, height, request.Style, colors, disk), status, entries.ToArray(), token);
    }

    // Each file read gets the colour of the item inside the current folder that holds it, in the same
    // order the list and the map use; files outside the folder are grey.
    private static uint[] FileColors(Disk disk, Request request, out List<(string Name, uint Color)> named)
    {
        var count = disk.FileCount;
        var colors = new uint[count];
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lookup = ranks.GetAlternateLookup<ReadOnlySpan<char>>();
        var items = request.Snapshot.Children(request.Folder);
        named = [];
        for (var rank = 0; rank < items.Count && items[rank].Bytes > 0; rank++)
        {
            ranks.TryAdd(items[rank].Name, rank);
            if (rank < Named) named.Add((items[rank].Name, ParseColor(DiskUsagePalette.BranchColor(rank))));
        }
        if (items.Count > Named) named.Add(("Everything else here", ParseColor(DiskUsagePalette.GroupColor)));
        var prefix = request.FolderPath.EndsWith('\\') ? request.FolderPath : request.FolderPath + "\\";
        var group = ParseColor(DiskUsagePalette.GroupColor);
        for (var i = 0; i < count; i++)
        {
            if (disk.Files[i] is not { } file) continue;   // pruned: its clusters were released too
            var path = file.Path.AsSpan();
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { colors[i] = ElsewhereColor; continue; }
            var rest = path[prefix.Length..];
            var slash = rest.IndexOf('\\');
            var name = slash < 0 ? rest : rest[..slash];
            colors[i] = lookup.TryGetValue(name, out var rank) && rank < Named ? ParseColor(DiskUsagePalette.BranchColor(rank)) : group;
        }
        return colors;
    }

    private static uint BucketColor(Disk disk, uint[] colors, int bucket)
    {
        var owner = disk.Owner[bucket];
        if (owner >= 0 && owner < colors.Length) return colors[owner];
        if (disk.Used is not { } used) return UnknownColor;
        var fill = used[bucket];
        return fill == 0 ? FreeColor : Mix(FreeColor, UsedColor, Math.Min(1, .35 + fill / 255d));
    }

    // Tracks from the outer edge inwards, each holding a share of the drive in proportion to its length -
    // the way a hard drive packs more sectors into its outer tracks. Cluster 0 is at the top of the edge.
    private static int TrackCount => (int)((OuterRadius - InnerRadius) * PlatterPixels / TrackPitch);

    private static double[] TrackStarts(int buckets)
    {
        var tracks = TrackCount;
        var starts = new double[tracks + 1];
        double sum = 0;
        for (var k = 0; k < tracks; k++) sum += TrackRadius(k);
        double at = 0;
        for (var k = 0; k < tracks; k++) { starts[k] = at / sum * buckets; at += TrackRadius(k); }
        starts[tracks] = buckets;
        return starts;
    }

    private static double TrackRadius(int k) => OuterRadius * PlatterPixels - (k + .5) * TrackPitch;

    // Bitmap pixel -> bucket on the platter, or -1 off the tracks. Also the track's position (0 outer, 1 inner).
    private static int PlatterBucket(double x, double y, double[] starts, out double radius, out double groove, out int track)
    {
        var centre = PlatterPixels / 2d;
        double dx = x - centre, dy = y - centre;
        radius = Math.Sqrt(dx * dx + dy * dy);
        var t = (OuterRadius * PlatterPixels - radius) / TrackPitch;
        track = (int)Math.Floor(t);
        groove = t - track;
        if (t < 0 || track >= starts.Length - 1) return -1;
        var turn = Math.Atan2(dx, -dy) / (2 * Math.PI);
        if (turn < 0) turn += 1;
        var bucket = (int)(starts[track] + turn * (starts[track + 1] - starts[track]));
        return Math.Clamp(bucket, 0, (int)starts[^1] - 1);
    }

    private static int[] RasterPlatter(Disk disk, uint[] colors, CancellationToken token)
    {
        const int size = PlatterPixels;
        var pixels = new int[size * size];
        var starts = TrackStarts(disk.BucketCount);
        var hub = InnerRadius * size - 4;
        var spindle = hub * .36;
        var screws = hub * .64;
        for (var y = 0; y < size; y++)
        {
            if ((y & 63) == 0) token.ThrowIfCancellationRequested();
            for (var x = 0; x < size; x++)
            {
                var bucket = PlatterBucket(x + .5, y + .5, starts, out var radius, out var groove, out var track);
                uint color;
                if (bucket >= 0)
                {
                    color = BucketColor(disk, colors, bucket);
                    // Grooves between tracks, and a faint banding from track to track like a record's.
                    var shade = groove < .2 ? .52 : .9 + .1 * ((track * 7919 % 13) / 12d);
                    color = Scale(color, shade);
                }
                else if (radius <= hub)
                {
                    // The hub spins with the platter: a spindle and six screws so the motion reads.
                    var angle = Math.Atan2(y + .5 - size / 2d, x + .5 - size / 2d);
                    var nearest = Math.Round(angle / (Math.PI / 3)) * (Math.PI / 3);
                    double sx = size / 2d + Math.Cos(nearest) * screws - (x + .5), sy = size / 2d + Math.Sin(nearest) * screws - (y + .5);
                    color = radius < spindle ? SpindleColor
                        : sx * sx + sy * sy < hub * hub * .006 ? SpindleColor
                        : Scale(HubColor, .9 + .1 * Math.Cos(radius * .35));
                }
                else continue;   // outside the platter, or the gap around the hub: transparent
                pixels[y * size + x] = unchecked((int)(0xFF000000u | color));
            }
        }
        return pixels;
    }

    // The defragmenter's grid: cells in reading order, each the most common colour among its buckets.
    private static int[] RasterGrid(Disk disk, uint[] colors, int width, int height, CancellationToken token)
    {
        var pixels = new int[width * height];
        int columns = width / CellPitch, rows = height / CellPitch;
        var cells = Math.Max(1, columns * rows);
        var tally = new (uint Color, int Count, int Bucket)[64];
        for (var cell = 0; cell < cells; cell++)
        {
            if ((cell & 1023) == 0) token.ThrowIfCancellationRequested();
            var (first, last) = CellRange(disk, cell, cells);
            var fill = unchecked((int)(0xFF000000u | BucketColor(disk, colors, CellBucket(disk, colors, first, last, tally))));
            int left = cell % columns * CellPitch, top = cell / columns * CellPitch;
            for (var y = top; y < top + CellSize; y++)
                pixels.AsSpan(y * width + left, CellSize).Fill(fill);
        }
        return pixels;
    }

    private static (int First, int Last) CellRange(Disk disk, int cell, int cells)
    {
        var first = (int)((long)cell * disk.BucketCount / cells);
        return (first, Math.Max(first + 1, (int)((long)(cell + 1) * disk.BucketCount / cells)));
    }

    // The bucket that stands for a grid cell: one with the colour most of the cell has. The picture and the
    // hover card both use it, so what the card describes is what the cell shows.
    private static int CellBucket(Disk disk, uint[] colors, int first, int last, (uint Color, int Count, int Bucket)[] tally)
    {
        var used = 0;
        var step = Math.Max(1, (last - first) / 64);
        for (var bucket = first; bucket < Math.Min(last, disk.BucketCount); bucket += step)
        {
            var color = BucketColor(disk, colors, bucket);
            var found = false;
            for (var i = 0; i < used; i++) if (tally[i].Color == color) { tally[i].Count++; found = true; break; }
            if (!found && used < tally.Length) tally[used++] = (color, 1, bucket);
        }
        if (used == 0) return Math.Min(first, disk.BucketCount - 1);
        var best = 0;
        for (var i = 1; i < used; i++) if (tally[i].Count > tally[best].Count) best = i;
        return tally[best].Bucket;
    }

    // ------------------------------------------------------------------ UI thread

    // A picture and what it was drawn from, so the hover card describes exactly what is on screen.
    private sealed record Frame(int[] Pixels, int Width, int Height, PlatterStyle Style, uint[] Colors, Disk Disk);
    private uint[] _shownColors = [];
    private Disk? _shownDisk;

    // From the worker. A cancelled worker's last posts are dropped, so a view that was cleared or hidden
    // does not get an old picture back.
    private void Post(Frame? frame, string status, (string Name, uint Color)[]? legend, CancellationToken token)
        => Dispatcher.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested) return;
            _status.Text = status;
            if (legend is not null)
            {
                _legend.Children.Clear();
                foreach (var (name, color) in legend)
                {
                    var entry = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 4) };
                    entry.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = Frozen(Unpack(color)),
                        Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                    entry.Children.Add(new TextBlock { Text = name, Foreground = InkMuted, FontSize = 11, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis });
                    _legend.Children.Add(entry);
                }
            }
            if (frame is null) return;
            if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
            {
                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                _image.Source = _bitmap;
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
            _shownStyle = frame.Style;
            _shownColors = frame.Colors;
            _shownDisk = frame.Disk;
            _shownWidth = frame.Width;
            _shownHeight = frame.Height;
            var grid = frame.Style == PlatterStyle.Grid;
            _image.Stretch = grid ? Stretch.None : Stretch.Uniform;
            _image.HorizontalAlignment = grid ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            _image.VerticalAlignment = grid ? VerticalAlignment.Top : VerticalAlignment.Stretch;
            RenderOptions.SetBitmapScalingMode(_image, grid ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);
            if (grid) _spin.Angle = 0;
            _chrome.InvalidateVisual();
            UpdateSpin();
        });

    private void UpdateSpin()
    {
        var spin = IsVisible && _bitmap is not null && _shownStyle == PlatterStyle.Platter;
        if (spin == _spinning) return;
        _spinning = spin;
        _lastFrame = -1;
        if (spin) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs args ? args.RenderingTime.TotalSeconds : Environment.TickCount64 / 1000d;
        if (now == _lastFrame) return;
        var dt = _lastFrame < 0 ? 0 : Math.Clamp(now - _lastFrame, 0, .1);
        _lastFrame = now;
        // The platter slows to a stop under the pointer, so what it points at holds still.
        var wanted = _mouse is not null && _hover.Kind >= 0 ? 0 : SpinSpeed;
        _speed += (wanted - _speed) * (1 - Math.Exp(-dt / .35));
        _angle = (_angle + _speed * dt) % 360;
        _spin.Angle = _angle;
        var before = _arm;
        _arm += (_armTarget - _arm) * (1 - Math.Exp(-dt / .12));
        if (Math.Abs(_arm - before) > 1e-5) _chrome.InvalidateVisual();
        if (_mouse is { } point && _speed > .05) Hover(point);
    }

    // Where the platter is drawn, in this control's coordinates.
    internal bool PlatterRect(out Point centre, out double side)
    {
        centre = default;
        side = Math.Min(_image.ActualWidth, _image.ActualHeight);
        if (_bitmap is null || _shownStyle != PlatterStyle.Platter || side < 10) return false;
        var origin = _image.TranslatePoint(new Point(_image.ActualWidth / 2, _image.ActualHeight / 2), this);
        centre = origin;
        return true;
    }

    internal double ArmRadius => _arm;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        Hover(_mouse.Value);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        _hover = (-1, -1);
        _armTarget = ParkedArm;
        _card.Visibility = Visibility.Collapsed;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_hover.File >= 0 && _shownDisk is { } disk && disk.Files[_hover.File] is { } file && _request is { } request)
        {
            Open(request, file.Path);
            e.Handled = true;
        }
    }

    // Clicking a file opens the item inside the current folder that holds it, one level down as on the map;
    // a file directly inside is selected; a file elsewhere opens the folder it is in.
    private void Open(Request request, string path)
    {
        var snapshot = request.Snapshot;
        var prefix = request.FolderPath.EndsWith('\\') ? request.FolderPath : request.FolderPath + "\\";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = path[prefix.Length..];
            var slash = rest.IndexOf('\\');
            if (slash > 0)
            {
                if (snapshot.FindFolder(prefix + rest[..slash]) is var child and >= 0) FolderOpened?.Invoke(child);
                return;
            }
            if (FindFile(snapshot, request.Folder, rest) is var file and >= 0) ItemSelected?.Invoke(file);
            return;
        }
        if (Path.GetDirectoryName(path) is { } parent && snapshot.FindFolder(parent) is var folder and >= 0) FolderOpened?.Invoke(folder);
    }

    private static int FindFile(DiskUsageSnapshot snapshot, int folder, string name)
    {
        var children = new List<int>();
        snapshot.ChildIds(folder, children);
        foreach (var child in children)
            if (snapshot.NameOf(child).Equals(name, StringComparison.OrdinalIgnoreCase)) return child;
        return -1;
    }

    // What is under the pointer: kind 0 a file read, 1 used but not read, 2 free, 3 unknown; -1 nothing.
    private void Hover(Point point)
    {
        var bucket = BucketAt(point, out var armRadius);
        _armTarget = bucket >= 0 && armRadius > 0 ? armRadius : ParkedArm;
        if (bucket < 0 || _shownDisk is not { } disk)
        {
            _hover = (-1, -1);
            _card.Visibility = Visibility.Collapsed;
            return;
        }
        var owner = disk.Owner[bucket];
        var file = owner >= 0 && owner < _shownColors.Length && disk.Files[owner] is not null ? owner : -1;   // as drawn
        var kind = file >= 0 ? 0 : disk.Used is not { } used ? 3 : used[bucket] > 0 ? 1 : 2;
        if ((kind, file) != _hover)
        {
            _hover = (kind, file);
            var position = $"{(bucket + .5) / disk.BucketCount:P1} into the drive";
            if (file >= 0 && disk.Files[file] is { } found)
            {
                _cardTitle.Text = Path.GetFileName(found.Path);
                _cardDetail.Text = $"{DiskUsageSnapshot.FormatBytes(found.Bytes)} on disk · " +
                    (found.Pieces <= 1 ? "in one piece" : $"in {found.Pieces:N0} pieces") + $" · starts {found.FirstLcn * 100d / disk.TotalClusters:0.#}% into the drive\n" +
                    $"{Path.GetDirectoryName(found.Path)}\nClick to open";
            }
            else
            {
                _cardTitle.Text = kind switch { 1 => "Used, not read", 2 => "Free space", _ => "Not read" };
                _cardDetail.Text = kind switch
                {
                    1 => $"{position}\nFiles smaller than the ones read, and the file system's own records",
                    2 => position,
                    _ => $"{position}\nOnly the largest files are read; without administrator rights\nthe rest of the drive's allocation cannot be seen",
                };
            }
        }
        _card.Visibility = Visibility.Visible;
        _card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var x = point.X + 16;
        var y = point.Y + 18;
        if (x + _card.DesiredSize.Width > ActualWidth - 6) x = point.X - 12 - _card.DesiredSize.Width;
        if (y + _card.DesiredSize.Height > ActualHeight - 6) y = point.Y - 12 - _card.DesiredSize.Height;
        Canvas.SetLeft(_card, Math.Max(6, x));
        Canvas.SetTop(_card, Math.Max(6, y));
    }

    private double[]? _starts;
    private readonly (uint Color, int Count, int Bucket)[] _tally = new (uint, int, int)[64];

    // Control point -> bucket, undoing the platter's spin; armRadius is the track's radius as a fraction of the side.
    private int BucketAt(Point point, out double armRadius)
    {
        armRadius = 0;
        if (_bitmap is null || _shownDisk is not { } disk) return -1;
        if (_shownStyle == PlatterStyle.Platter)
        {
            if (!PlatterRect(out var centre, out var side)) return -1;
            var scale = PlatterPixels / side;
            var angle = -_angle * Math.PI / 180;
            double dx = point.X - centre.X, dy = point.Y - centre.Y;
            var x = (dx * Math.Cos(angle) - dy * Math.Sin(angle)) * scale + PlatterPixels / 2d;
            var y = (dx * Math.Sin(angle) + dy * Math.Cos(angle)) * scale + PlatterPixels / 2d;
            if (_starts is null || (int)_starts[^1] != disk.BucketCount) _starts = TrackStarts(disk.BucketCount);
            var bucket = PlatterBucket(x, y, _starts, out var radius, out _, out _);
            armRadius = bucket >= 0 ? radius / PlatterPixels : 0;
            return bucket;
        }
        var local = TranslatePoint(point, _image);
        if (local.X < 0 || local.Y < 0 || local.X >= _shownWidth || local.Y >= _shownHeight) return -1;
        int columns = _shownWidth / CellPitch, rows = _shownHeight / CellPitch;
        int column = (int)local.X / CellPitch, row = (int)local.Y / CellPitch;
        if (column >= columns || row >= rows) return -1;
        var (first, last) = CellRange(disk, row * columns + column, Math.Max(1, columns * rows));
        return CellBucket(disk, _shownColors, first, last, _tally);
    }

    // ------------------------------------------------------------------ chrome: rim, sheen and head arm

    // Drawn over the spinning bitmap and never rotated, so reflections and the arm stay where they are
    // while the platter turns under them.
    private sealed class PlatterChrome(DiskPlatterView owner) : FrameworkElement
    {
        private static readonly Pen Rim = FrozenPen(Color.FromRgb(0x44, 0x41, 0x3C), 2.5);
        private static readonly Brush Sheen = MakeSheen();
        private static readonly Brush ArmBrush = MakeArm();
        private static readonly Pen ArmEdge = FrozenPen(Color.FromArgb(0x90, 0x14, 0x13, 0x12), 1);
        private static readonly Brush Pivot = MakePivot();

        protected override void OnRender(DrawingContext dc)
        {
            if (!owner.PlatterRect(out var c, out var side)) return;
            var outer = side * OuterRadius;
            dc.DrawEllipse(null, Rim, c, outer + 1.5, outer + 1.5);
            // A bow-tie of light across the platter, as brushed aluminium reflects a lamp.
            var sheen = new StreamGeometry();
            using (var g = sheen.Open())
                foreach (var direction in new[] { -40d, 140d })
                {
                    var a0 = (direction - 13) * Math.PI / 180;
                    var a1 = (direction + 13) * Math.PI / 180;
                    g.BeginFigure(c, true, true);
                    g.LineTo(new Point(c.X + Math.Cos(a0) * outer, c.Y + Math.Sin(a0) * outer), false, false);
                    g.ArcTo(new Point(c.X + Math.Cos(a1) * outer, c.Y + Math.Sin(a1) * outer), new Size(outer, outer), 0, false, SweepDirection.Clockwise, false, false);
                }
            sheen.Freeze();
            dc.DrawGeometry(Sheen, null, sheen);

            // The head arm: pivots beside the platter and swings its head to the track under the pointer.
            var pivot = new Point(c.X + side * .47, c.Y + side * .40);
            var length = side * .5;
            var r = owner.ArmRadius * side;
            var toCentre = c - pivot;
            var d = toCentre.Length;
            var along = (length * length - r * r + d * d) / (2 * d);
            var across = Math.Sqrt(Math.Max(0, length * length - along * along));
            toCentre.Normalize();
            var normal = new Vector(-toCentre.Y, toCentre.X);
            var tip = pivot + toCentre * along + normal * across;   // the arm stays on the platter's right
            var direction2 = tip - pivot; direction2.Normalize();
            var side2 = new Vector(-direction2.Y, direction2.X);
            var body = new StreamGeometry();
            using (var g = body.Open())
            {
                g.BeginFigure(pivot + side2 * side * .026, true, true);
                g.LineTo(tip + side2 * side * .007, true, true);
                g.LineTo(tip - side2 * side * .007, true, true);
                g.LineTo(pivot - side2 * side * .026, true, true);
            }
            body.Freeze();
            dc.DrawGeometry(ArmBrush, ArmEdge, body);
            // The slider at the tip, and the pivot's bearing.
            var head = new StreamGeometry();
            using (var g = head.Open())
            {
                var h = side * .012;
                g.BeginFigure(tip + direction2 * h + side2 * h * .8, true, true);
                g.LineTo(tip + direction2 * h - side2 * h * .8, true, true);
                g.LineTo(tip - direction2 * h - side2 * h * .8, true, true);
                g.LineTo(tip - direction2 * h + side2 * h * .8, true, true);
            }
            head.Freeze();
            dc.DrawGeometry(ArmBrush, ArmEdge, head);
            dc.DrawEllipse(Pivot, ArmEdge, pivot, side * .045, side * .045);
            dc.DrawEllipse(Frozen(Color.FromRgb(0x2A, 0x28, 0x25)), null, pivot, side * .014, side * .014);
        }

        private static Brush MakeSheen()
        {
            var brush = new RadialGradientBrush(Color.FromArgb(0x16, 255, 255, 255), Color.FromArgb(0x05, 255, 255, 255));
            brush.Freeze();
            return brush;
        }

        private static Brush MakeArm()
        {
            var brush = new LinearGradientBrush(Color.FromRgb(0x9A, 0x96, 0x8F), Color.FromRgb(0x5E, 0x5A, 0x54), 90);
            brush.Freeze();
            return brush;
        }

        private static Brush MakePivot()
        {
            var brush = new RadialGradientBrush(Color.FromRgb(0x8A, 0x86, 0x7F), Color.FromRgb(0x46, 0x43, 0x3E)) { GradientOrigin = new Point(.35, .35) };
            brush.Freeze();
            return brush;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static int[] Filled(int length, int value)
    {
        var array = new int[length];
        Array.Fill(array, value);
        return array;
    }

    private static uint ParseColor(string hex) => Convert.ToUInt32(hex.TrimStart('#'), 16) & 0xFFFFFF;
    private static Color Unpack(uint color) => Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);

    private static uint Scale(uint color, double factor)
    {
        var r = (uint)Math.Clamp((color >> 16 & 255) * factor, 0, 255);
        var g = (uint)Math.Clamp((color >> 8 & 255) * factor, 0, 255);
        var b = (uint)Math.Clamp((color & 255) * factor, 0, 255);
        return r << 16 | g << 8 | b;
    }

    private static uint Mix(uint from, uint to, double t)
    {
        uint Channel(int shift) => (uint)Math.Round((from >> shift & 255) + ((double)(to >> shift & 255) - (from >> shift & 255)) * t);
        return Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }

    // ------------------------------------------------------------------ Windows

    private const uint GenericRead = 0x80000000, FileReadAttributes = 0x80, OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    private const uint FsctlGetRetrievalPointers = 0x00090073, FsctlGetVolumeBitmap = 0x0009006F, IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7, FileAttributeTagInfo = 9;
    private const int ErrorMoreData = 234, ErrorHandleEof = 38;
    // Offline, recall on open, recall on data access (cloud placeholders), and reparse points (links).
    private const uint SkipAttributes = 0x1000 | 0x40000 | 0x400000 | 0x400;

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { public uint Attributes; public uint ReparseTag; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref long input, int inputSize,
        [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlBytes(SafeFileHandle device, uint code, byte[] input, int inputSize,
        [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out AttributeTagInfo info, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string root, out ulong available, out ulong total, out ulong free);
}
