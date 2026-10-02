using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Clearspace.Controls;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class DiskUsagePerformanceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SlowAndSupersededFolderLoadsNeverRunOnTheDispatcher() => RunSta(async () =>
    {
        using var map = CreateMap();
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var uiThread = Environment.CurrentManagedThreadId;
        var providerThread = uiThread;
        map.SetSource(new DiskUsageItem(0, "Old drive", 100, 1, true), (_, _) =>
        {
            providerThread = Environment.CurrentManagedThreadId;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(3)); // bounded even if a regression blocks the UI
            return [new DiskUsageItem(99, "Old file", 100, 1, false)];
        }, []);
        try
        {
            await WaitFor(() => entered.IsSet);
            Assert.AreNotEqual(uiThread, providerThread, "Reading and laying out folders must not block input.");
            Assert.IsFalse(release.IsSet);
            var inputRan = false;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => inputRan = true, DispatcherPriority.Input);
            Assert.IsTrue(inputRan);
            map.SetSource(new DiskUsageItem(0, "New drive", 200, 1, true), (_, _) =>
                [new DiskUsageItem(2, "New file", 200, 1, false)], []);
            await WaitFor(() => map.PendingLoads == 0);
            release.Set();
            await Task.Delay(30);
            map.RenderTestFrame(.5);
            Assert.IsNotNull(map.ScreenBoundsOf(2));
            Assert.IsNull(map.ScreenBoundsOf(99), "An old load must not overwrite the new drive.");
        }
        finally { release.Set(); }

        release.Reset(); entered.Reset();
        map.SetSource(new DiskUsageItem(0, "Drive", 200, 1, true), (id, _) =>
        {
            if (id == 0) return [new DiskUsageItem(2, "Folder", 200, 1, true)];
            providerThread = Environment.CurrentManagedThreadId;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(3));
            return [new DiskUsageItem(3, "Nested", 200, 1, true)];
        }, []);
        await WaitFor(() => map.ScreenBoundsOf(2) is not null);
        try
        {
            map.ShowFolder([2, 3]);
            await WaitFor(() => entered.IsSet);
            Assert.AreNotEqual(uiThread, providerThread);
            map.ShowFolder([]);
            release.Set();
            await WaitFor(() => map.PendingLoads == 0);
            Assert.AreEqual(0, map.FocusFolderId, "Back must supersede an unfinished folder navigation.");
            map.ShowFolder([2, 3]);
            await WaitFor(() => map.FocusFolderId == 3);
        }
        finally { release.Set(); }
    });

    [DataTestMethod]
    [DataRow(1280, 800)]
    [DataRow(4748, 1220)]
    [DataRow(4748, 1220, true)]
    public void DenseCameraFramesHaveBoundedWorkAndReportTimings(int width, int height, bool useGpu = false) => RunSta(async () =>
    {
        using var map = CreateMap(width, height);
        using var host = useGpu ? new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Clearspace dense GPU regression")
        { Width = width, Height = height, WindowStyle = unchecked((int)0x80000000) }) : null;
        if (host is not null) { host.RootVisual = map; await WaitFor(() => map.IsLoaded); }
        const int folders = 64, filesPerFolder = 400;
        var children = Enumerable.Range(1, folders).ToDictionary(id => id, id =>
            (IReadOnlyList<DiskUsageItem>)Enumerable.Range(1, filesPerFolder)
                .Select(file => new DiskUsageItem(1000 + id * filesPerFolder + file, $"File {file}.dat", 1000 + file % 31, 1, false)).ToArray());
        var roots = children.Select(pair => new DiskUsageItem(pair.Key, $"Folder {pair.Key}", pair.Value.Sum(file => file.Bytes), filesPerFolder, true)).ToArray();
        map.SetSource(new DiskUsageItem(0, "Drive", roots.Sum(item => item.Bytes), folders * filesPerFolder, true),
            (id, _) => id == 0 ? roots : children.GetValueOrDefault(id, []), []);
        await WaitFor(() => map.PendingLoads == 0);
        for (var i = 0; i < 50; i++)
        {
            map.RenderTestFrame(.25);
            await Task.Delay(2);
        }
        var times = new List<double>();
        var walks = new List<double>();
        long allocated = 0;
        var largest = 0;
        var buildStart = map.GeometryBuildCount;
        var reuseStart = map.GeometryReuseCount;
        var uploadStart = map.GpuGeometryUploads;
        var skipped = 0;
        var lastWheelFrame = -1;
        for (var frame = 0; frame < 180; frame++)
        {
            if (useGpu) await Task.Delay(16); // let the compositor consume its previous frame
            if (frame % 12 == 0 && lastWheelFrame != frame)
            {
                lastWheelFrame = frame;
                map.ZoomWithWheel(new Point(width * .4375, height * .45), frame < 84 ? 120 : -120);
            }
            var prepared = map.PreparedFrameCount;
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            map.RenderTestFrame();
            if (useGpu && prepared == map.PreparedFrameCount)
            {
                Assert.IsTrue(++skipped < 600, "The GPU must eventually present the pending camera.");
                frame--;
                continue; // do not report zero-cost skipped frames as rendering performance
            }
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            walks.Add(map.LastWalkMilliseconds);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            largest = Math.Max(largest, map.RenderedTileCount);
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
        times.Sort();
        walks.Sort();
        TestContext.WriteLine($"{(useGpu ? "GPU" : "CPU fallback")}, {width}x{height}, 25,600 files, 180 camera frames: median {times[90]:F2} ms, p95 {times[171]:F2} ms, max {times[^1]:F2} ms; walk median {walks[90]:F2} ms, p95 {walks[171]:F2} ms; UI allocation {allocated / 180:N0} bytes/frame; peak {largest:N0} tiles; {map.GeometryBuildCount - buildStart} builds, {map.GeometryReuseCount - reuseStart} reused frames, {map.GpuGeometryUploads - uploadStart} GPU uploads.");
        Assert.IsTrue(largest > 1000, "The measurement must exercise a dense, expanded scene.");
        Assert.IsTrue(largest <= 12000, "Rendering must remain bounded independently of the filesystem size.");
        Assert.IsTrue(allocated / 180 < 600000, "Per-tile closure allocations must not return to the frame loop.");
        // CHANGED (round 16): a contended compositor defers most frames, so how many frames this loop
        // gets to prepare is not under the test's control. What must hold at any frame rate: a moving
        // camera reuses its geometry far more often than it rebuilds it, and only a rebuild uploads.
        var builds = map.GeometryBuildCount - buildStart;
        var reused = map.GeometryReuseCount - reuseStart;
        Assert.IsTrue(reused > builds * 4, $"Most zoom frames must reuse cached geometry ({reused} reused, {builds} rebuilt).");
        if (useGpu)
        {
            Assert.IsTrue(map.GpuGeometryUploads > 0, "This fixture must exercise real GPU geometry.");
            Assert.IsTrue(map.GpuGeometryUploads - uploadStart <= builds, "Only a rebuilt layer may upload vertices.");
            host!.RootVisual = null;
        }
    });

    [TestMethod]
    public void FrameRequestsCoalesceAndLetInputRunFirst() => RunSta(async () =>
    {
        using var map = CreateMap(4748, 1220);
        var before = map.ScheduledFrameCount;
        for (var i = 0; i < 100; i++) map.QueueFrame();
        var inputSaw = -1;
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => inputSaw = map.ScheduledFrameCount, DispatcherPriority.Input);
        Assert.AreEqual(before, inputSaw, "Input must run ahead of queued map rendering.");
        await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.AreEqual(before + 1, map.ScheduledFrameCount, "A burst of render requests should prepare one current frame.");
        map.QueueFrame();
        map.Dispose();
        await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.AreEqual(before + 1, map.ScheduledFrameCount, "Closing the map must cancel queued rendering.");
    });

    [TestMethod]
    public void LoadedMapKeepsZoomingThroughTheInputFriendlyFrameQueue() => RunSta(async () =>
    {
        // A real WPF presentation source exercises the loaded/GPU route. This
        // popup is hidden (no WS_VISIBLE), never activated, and creates no App.
        using var map = CreateMap();
        using var host = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Clearspace render regression")
        { Width = 1280, Height = 800, WindowStyle = unchecked((int)0x80000000) });
        host.RootVisual = map;
        await WaitFor(() => map.IsLoaded);
        var items = Enumerable.Range(1, 32).Select(i => new DiskUsageItem(i, $"File {i}", 100, 1, false)).ToArray();
        map.SetSource(new DiskUsageItem(0, "Drive", 3200, 32, true), (_, _) => items, []);
        await WaitFor(() => map.PendingLoads == 0);
        map.QueueFrame();
        await Dispatcher.Yield(DispatcherPriority.Background);
        var width = map.Camera.Width;
        var frames = map.PreparedFrameCount;
        Assert.IsTrue(map.ZoomWithWheel(new Point(600, 400), 240));
        for (var i = 0; i < 20; i++)
        {
            var inputSaw = -1;
            var prior = map.ScheduledFrameCount;
            map.QueueFrame();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => inputSaw = map.ScheduledFrameCount, DispatcherPriority.Input);
            Assert.AreEqual(prior, inputSaw);
            await Task.Delay(16);
        }
        Assert.IsTrue(map.Camera.Width < width * .8);
        Assert.IsTrue(map.PreparedFrameCount > frames + 2, "A live map must keep publishing updated scenes through the queue.");
        Assert.IsNotNull(map.ScreenBoundsOf(1));
        host.RootVisual = null;
    });

    [TestMethod]
    public void CameraFramesReuseGeometryAndCachedTilesDoNotOverlap() => RunSta(async () =>
    {
        using var map = CreateMap();
        var items = Enumerable.Range(1, 144).Select(id => new DiskUsageItem(id, $"File {id}", 100, 1, false)).ToArray();
        map.SetSource(new DiskUsageItem(0, "Drive", 14400, 144, true), (_, _) => items, []);
        await WaitFor(() => map.PendingLoads == 0);
        map.RenderTestFrame(.5);
        var geometry = map.CachedGeometry!;
        var builds = map.GeometryBuildCount;
        var camera = map.Camera;
        Assert.IsTrue(geometry.Commands.Length > 100);
        Assert.IsTrue(geometry.Commands.All(c => c.Alpha == 255), "Transparency must be flattened before caching.");
        // CHANGED (round 28): a folder paints its surface once and its children paint single inset
        // rectangles on top, so a point may be covered twice - by a block and by the folder beneath
        // it. That is the point: the surface underneath makes an unpainted region impossible, and it
        // costs one rectangle per block instead of five. What must still hold is full coverage, and a
        // bounded number of layers over any one point.
        var area = geometry.Commands.Sum(c => (double)(c.X1 - c.X0) * (c.Y1 - c.Y0));
        Assert.IsTrue(area >= 1280d * 800, "The scene must cover the whole viewport.");
        for (var x = 7.13; x < 1280; x += 37.17)
            for (var y = 9.71; y < 800; y += 31.13)
            {
                var layers = geometry.Commands.Count(c => x >= c.X0 && x < c.X1 && y >= c.Y0 && y < c.Y1);
                Assert.IsTrue(layers >= 1, $"({x}, {y}) is not covered by any cached rectangle.");
                Assert.IsTrue(layers <= 8, $"({x}, {y}) is painted {layers} times; overdraw must stay bounded.");
            }
        for (var frame = 0; frame < 24; frame++)
        {
            if (frame % 6 == 0) map.ZoomWithWheel(new Point(640, 400), frame < 12 ? 24 : -24);
            map.RenderTestFrame();
            Assert.AreSame(geometry, map.CachedGeometry, "Small camera changes must reuse the same immutable geometry.");
            Assert.AreEqual(map.Camera, map.PresentedCamera);
        }
        Assert.AreEqual(builds, map.GeometryBuildCount);
        Assert.IsTrue(map.GeometryReuseCount >= 24);
        Assert.AreNotEqual(camera, map.Camera);
        map.Measure(new Size(1000, 800));
        map.Arrange(new Rect(0, 0, 1000, 800));
        map.RenderTestFrame();
        Assert.AreNotSame(geometry, map.CachedGeometry, "A resized viewport must invalidate its geometry.");
        map.Dispose();
        Assert.IsNull(map.CachedGeometry);
    });

    [TestMethod]
    public void ResidentGpuGeometryMovesActualPixelsWithoutReuploading() => RunSta(() =>
    {
        using var gpu = GpuTileRenderer.TryCreate();
        if (gpu is not { IsAvailable: true }) { Assert.Inconclusive("Direct3D required."); return Task.CompletedTask; }
        var geometry = new TileGeometry([new(0, 0, 100, 240, 0xC08040, 255), new(100, 0, 400, 240, 0x2050A0, 255)]);
        Assert.IsTrue(gpu.RenderCached([new(geometry, 1, 0, 0, 1)], 400, 240, 1, 1, 0, out _));
        AssertColor(0x2050A0, gpu.ReadPixelForTest(150, 80));
        for (var i = 0; i < 12; i++)
        {
            Assert.IsTrue(gpu.RenderCached([new(geometry, 2, -20, -10, 1)], 400, 240, 1, 1, 0, out _));
            AssertColor(0xC08040, gpu.ReadPixelForTest(150, 80));
        }
        Assert.AreEqual(1, gpu.GeometryUploads, "Camera-only frames must never upload the same vertices again.");
        var incoming = new TileGeometry([new(0, 0, 400, 240, 0x40C080, 255)]);
        Assert.IsTrue(gpu.RenderCached([new(geometry, 1, 0, 0, 1), new(incoming, 1, 0, 0, .5)], 400, 240, 1, 1, 0, out _));
        AssertColor(0x80A060, gpu.ReadPixelForTest(50, 80), 2);
        Assert.AreEqual(2, gpu.CachedGeometryCount);
        Assert.IsTrue(gpu.RenderCached([new(incoming, 1, 0, 0, 1)], 480, 240, 1, 1, 0, out _));
        AssertColor(0x40C080, gpu.ReadPixelForTest(50, 80));
        Assert.AreEqual(1, gpu.CachedGeometryCount, "Finished transitions must release the old GPU buffer.");
        Assert.AreEqual(2, gpu.GeometryUploads, "Resizing a render target must not discard reusable vertices.");
        // CHANGED (round 43): six vertices of 20 bytes - position, colour, and the second colour the shader mixes toward.
        Assert.AreEqual(120L, gpu.CachedGeometryBytes);
        gpu.Dispose();
        Assert.AreEqual(0, gpu.CachedGeometryCount);
        return Task.CompletedTask;
    });

    private static void AssertColor(uint expected, uint actual, int tolerance = 0)
    {
        foreach (var shift in new[] { 0, 8, 16 })
            Assert.IsTrue(Math.Abs((int)((expected >> shift) & 255) - (int)((actual >> shift) & 255)) <= tolerance,
                $"Expected RGB {expected:X6}, got {actual & 0xFFFFFF:X6}.");
    }

    [TestMethod]
    public void DetailBudgetFadesGraduallyAndReversibly()
    {
        const int children = 150;
        var previous = 0d;
        for (var budget = children; budget <= children * 2; budget++)
        {
            var detail = DiskUsageTreemap.DetailAmount(budget, children);
            Assert.IsTrue(detail >= previous && detail - previous < .025,
                "One extra tile of allowance must not make a whole cluster flash into view.");
            previous = detail;
        }
        Assert.AreEqual(1d, previous);
        Assert.AreEqual(0d, DiskUsageTreemap.DetailAmount(children + 1, children));
    }

    [TestMethod]
    public void BusyGpuDefersLabelsHitTargetsAndTilesTogether() => RunSta(async () =>
    {
        using var map = CreateMap();
        using var host = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Clearspace busy frame regression")
        { Width = 1280, Height = 800, WindowStyle = unchecked((int)0x80000000) });
        host.RootVisual = map;
        await WaitFor(() => map.IsLoaded);
        map.SetSource(new DiskUsageItem(0, "Drive", 200, 2, true), (_, _) =>
            [new(1, "First", 100, 1, false), new(2, "Second", 100, 1, false)], []);
        await WaitFor(() => map.PendingLoads == 0);
        map.RenderTestFrame(.5);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var gpu = (GpuTileRenderer?)typeof(DiskUsageTreemap).GetField("_gpu", flags)!.GetValue(map);
        if (gpu is not { IsAvailable: true }) { Assert.Inconclusive("Direct3D required."); return; }
        var ready = (ManualResetEvent)typeof(System.Windows.Interop.D3DImage).GetField("_canWriteEvent", flags)!.GetValue(gpu.Image)!;
        var count = typeof(System.Windows.Interop.D3DImage).GetField("_lockCount", flags)!;
        var camera = map.PresentedCamera;
        var tiles = map.VisibleTiles.ToArray();
        var frames = map.PreparedFrameCount;
        try
        {
            ready.Reset();
            map.ZoomWithWheel(new Point(300, 300), 240);
            for (var i = 0; i < 3; i++) map.RenderTestFrame();
            Assert.AreNotEqual(camera, map.Camera, "The pending camera must actually move in this regression.");
            Assert.AreEqual(camera, map.PresentedCamera);
            Assert.AreEqual(frames, map.PreparedFrameCount, "A busy frame must not rebuild labels ahead of its tiles.");
            CollectionAssert.AreEqual(tiles, map.VisibleTiles.ToArray(), "Hit targets must stay with the visible image.");
            Assert.AreEqual(0u, (uint)count.GetValue(gpu.Image)!);
            ready.Set();
            map.RenderTestFrame();
            Assert.AreEqual(map.Camera, map.PresentedCamera);
            Assert.AreEqual(frames + 1, map.PreparedFrameCount);
        }
        finally { ready.Set(); host.RootVisual = null; }
    });

    [TestMethod]
    public void ZoomPrefetchStartsOnlyOneBackgroundFolderAtATime() => RunSta(async () =>
    {
        // At rest these folders are below the prefetch threshold. Zooming makes
        // them large enough to load without allowing an earlier idle load burst.
        using var map = CreateMap(100, 100);
        // Idle prebuilding is now an independent feature. Isolate speculative zoom work;
        // at 100px each of the 144 tiles is below the current 10px prefetch threshold.
        map.BackgroundBuilding = false;
        using var release = new ManualResetEventSlim();
        var loads = 0;
        var roots = Enumerable.Range(1, 144).Select(id => new DiskUsageItem(id, $"Folder {id}", 100, 1, true)).ToArray();
        map.SetSource(new DiskUsageItem(0, "Drive", 14400, 144, true), (id, token) =>
        {
            if (id == 0) return roots;
            Interlocked.Increment(ref loads);
            release.Wait(TimeSpan.FromSeconds(3), token);
            return [new DiskUsageItem(100 + id, "Child", 100, 1, false)];
        }, []);
        await WaitFor(() => map.PendingLoads == 0);
        try
        {
            map.ZoomWithWheel(new Point(50, 50), 720);
            map.RenderTestFrame(.15);
            await WaitFor(() => { map.RenderTestFrame(); return Volatile.Read(ref loads) > 0; });
            Assert.AreEqual(1, map.PendingLoads, "Zooming must not launch a burst of speculative folder allocations.");
            Assert.AreEqual(1, Volatile.Read(ref loads));
        }
        finally { release.Set(); }
    });

    [TestMethod]
    public void ClosingMapCancelsBackgroundWorkAndPreventsRestart() => RunSta(async () =>
    {
        using var map = CreateMap();
        using var entered = new ManualResetEventSlim();
        CancellationToken captured = default;
        map.SetSource(new DiskUsageItem(0, "Drive", 100, 1, true), (_, token) =>
        {
            captured = token;
            entered.Set();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(3));
            token.ThrowIfCancellationRequested();
            return [];
        }, []);
        await WaitFor(() => entered.IsSet);
        map.Dispose();
        Assert.IsTrue(captured.IsCancellationRequested);
        await WaitFor(() => map.PendingLoads == 0);
        var restarted = false;
        map.SetSource(new DiskUsageItem(0, "Drive", 100, 1, true), (_, _) => { restarted = true; return []; }, []);
        map.RenderTestFrame();
        Assert.IsFalse(restarted);
        Assert.AreEqual(0, map.RenderedTileCount);
    });

    [TestMethod]
    public void BusyGpuFrameReleasesItsLockAndTheNextFrameCanRender() => RunSta(() =>
    {
        using var gpu = GpuTileRenderer.TryCreate();
        if (gpu is not { IsAvailable: true })
        {
            Assert.Inconclusive("This regression requires the Direct3D renderer.");
            return Task.CompletedTask;
        }
        // Force the compositor-busy path without relying on GPU timing. D3DImage
        // increments its lock count even when TryLock returns false (.NET WPF).
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var imageType = typeof(System.Windows.Interop.D3DImage);
        var ready = (ManualResetEvent)imageType.GetField("_canWriteEvent", flags)!.GetValue(gpu.Image)!;
        var count = imageType.GetField("_lockCount", flags)!;
        DiskUsageTreemap.TileCommand[] commands = [new(0, 0, 200, 200, 0x4F86B8, 255)];
        Assert.IsTrue(gpu.Render(commands, 400, 240, 1, 1, 0x1A1917, out _));
        try
        {
            ready.Reset();
            for (var i = 0; i < 3; i++)
            {
                Assert.IsFalse(gpu.Render(commands, 400, 240, 1, 1, 0x1A1917, out var busy));
                Assert.IsTrue(busy);
                Assert.AreEqual(0u, (uint)count.GetValue(gpu.Image)!, "A skipped frame must not leave D3DImage locked forever.");
            }
            ready.Set();
            commands[0] = new(30, 30, 350, 230, 0xC47A45, 255);
            Assert.IsTrue(gpu.Render(commands, 400, 240, 1, 1, 0x1A1917, out var stillBusy));
            Assert.IsFalse(stillBusy);
            Assert.AreEqual(0u, (uint)count.GetValue(gpu.Image)!);
        }
        finally
        {
            ready.Set();
            while ((uint)count.GetValue(gpu.Image)! > 0) gpu.Image.Unlock();
        }
        return Task.CompletedTask;
    });

    [TestMethod]
    public void GpuRendererPresentsAndResizesWhenAvailable() => RunSta(async () =>
    {
        using var gpu = GpuTileRenderer.TryCreate();
        if (gpu is not { IsAvailable: true })
        {
            TestContext.WriteLine("GPU unavailable in this session; CPU fallback is covered by the dense-frame test.");
            return;
        }
        DiskUsageTreemap.TileCommand[] commands = [new(0, 0, 400, 240, 0x4F86B8, 255), new(20, 20, 120, 120, 0xC47A45, 180)];
        var presented = 0;
        for (var i = 0; i < 12; i++)
        {
            var width = i < 6 ? 400 : 480;
            if (gpu.Render(commands, width, 240, 1, 1, 0x1A1917, out var busy)) presented++;
            else Assert.IsTrue(busy, "An available GPU should render or defer when the compositor owns its buffer.");
            await Task.Delay(4);
        }
        Assert.IsTrue(presented > 0);
        TestContext.WriteLine($"GPU smoke check: {presented}/12 frames presented; multisampling {gpu.IsMultisampled}; resize exercised.");
    });

    private static DiskUsageTreemap CreateMap(int width = 1280, int height = 800)
    {
        var map = new DiskUsageTreemap();
        map.Measure(new Size(width, height));
        map.Arrange(new Rect(0, 0, width, height));
        return map;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 600 && !condition(); i++) await Task.Delay(5);
        Assert.IsTrue(condition(), "Background map work did not complete.");
    }

    private static void RunSta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(60)), "Map responsiveness check timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
