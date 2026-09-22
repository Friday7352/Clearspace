using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clearspace.Controls;
using Clearspace.Models;
using Clearspace.Services;
using Clearspace.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

// REWRITTEN for the continuous map: one fixed world layout and one camera.
// Animations are driven with DiskUsageTreemap.AdvanceTime so results don't depend on frame timing.
[TestClass]
public sealed class DiskUsageWindowTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void MapZoomsContinuouslyBetweenFoldersWithRealApplicationStyles()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                DiskUsageView? window = null;
                try
                {
                    // Load resources without starting the real app or its filesystem services.
                    var app = new App();
                    app.InitializeComponent();
                    var index = Sample();
                    var second = new VolumeIndex(@"D:\", 2);
                    second.Add(-1, @"D:\", 0, 0, 0, FileAttributes.Directory);
                    var removedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var deletionCalls = 0;
                    var deletion = new DiskUsageDeletionService((paths, _) =>
                    {
                        deletionCalls++;
                        foreach (var path in paths) removedPaths.Add(path);
                        return FileOperationResult.FromShellResult(FileOperationKind.Delete, 0, false);
                    }, path => removedPaths.Contains(path) ? DiskUsagePathState.Missing : DiskUsagePathState.Exists);
                    var allowDelete = false;
                    using var vm = new DiskUsageViewModel(() => [index, second], deletion);
                    var windowCount = app.Windows.Count;
                    window = new DiskUsageView(vm, confirmDelete: _ => allowDelete);
                    var host = new ContentControl { Content = window };
                    Assert.AreSame(window, host.Content);
                    Assert.AreEqual(windowCount, app.Windows.Count, "Analyzer must be embedded without creating a window.");
                    host.Content = null;
                    await vm.LoadAsync();
                    var map = (DiskUsageTreemap)window.FindName("Treemap");

                    Render(window, "disk-usage-large.png", 1900, 1000);
                    Assert.IsTrue(map.ActualWidth > map.ActualHeight, "The map should fill the space beside the sidebar, not a centered square.");
                    Render(window, "disk-usage-wide.png", 1072, 900);
                    await Task.Delay(320); // the map re-lays out once after a large aspect change
                    await Settle(window, map, "disk-usage-wide.png");

                    var darkList = (ListView)window.FindName("ItemList");
                    darkList.IsEnabled = false;
                    var disabledPreview = Render(window, "disk-usage-loading.png", 1072, 900);
                    var listPoint = darkList.TranslatePoint(new Point(12, darkList.ActualHeight - 8), window);
                    var pixel = new byte[4];
                    disabledPreview.CopyPixels(new Int32Rect((int)listPoint.X, (int)listPoint.Y, 1, 1), pixel, 4, 0);
                    Assert.IsTrue(pixel[0] < 70 && pixel[1] < 70 && pixel[2] < 70,
                        "The list's disabled/loading background must stay dark instead of flashing the system light color.");
                    darkList.ClearValue(UIElement.IsEnabledProperty);

                    // Areas are exact byte ratios and the whole drive fills the view.
                    var rootItems = vm.Items.Where(item => item.Bytes > 0).ToArray();
                    var areas = rootItems.ToDictionary(item => item.Id, item => Area(map.ScreenBoundsOf(item.Id)!.Value));
                    var totalArea = areas.Values.Sum();
                    Assert.AreEqual(map.ActualWidth * map.ActualHeight, totalArea, totalArea * 1e-6);
                    foreach (var item in rootItems)
                        Assert.AreEqual(item.Share / 100, areas[item.Id] / totalArea, 1e-8, $"Rendered area differs from bytes for {item.Name}.");

                    // Clicking a folder flies into the very rectangle that was on screen.
                    var videos = vm.Items.Single(item => item.Name == "Videos");
                    var photos = vm.Items.Single(item => item.Name == "Photos");
                    var videosBefore = map.ScreenBoundsOf(videos.Id)!.Value;
                    var worldBefore = ToWorld(map, videosBefore);
                    Assert.IsTrue(map.ClickAt(Center(videosBefore)));
                    Assert.IsTrue(map.IsAnimating, "Opening a folder should zoom from its box into its contents.");
                    map.AdvanceTime(.12);
                    Render(window, "disk-usage-folder-transition.png", 1072, 900);
                    var midFlight = map.ScreenBoundsOf(videos.Id)!.Value;
                    Assert.IsTrue(midFlight.Width > videosBefore.Width && midFlight.Width < map.ActualWidth * 1.1,
                        "Mid-flight, the folder should be growing continuously.");
                    Assert.AreEqual(@"C:\", vm.CurrentPath, "The sidebar must wait while the folder is still growing.");
                    map.AdvanceTime(1);
                    Assert.IsFalse(map.IsAnimating);
                    await WaitFor(() => vm.CurrentPath == @"C:\Videos", "The settled folder should open in the list.");
                    var videosAfter = map.ScreenBoundsOf(videos.Id)!.Value;
                    Assert.IsTrue(videosAfter.Width >= map.ActualWidth * .9 || videosAfter.Height >= map.ActualHeight * .9,
                        "The opened folder should fill the view.");
                    var worldAfter = ToWorld(map, videosAfter);
                    Assert.AreEqual(worldBefore.X, worldAfter.X, 1e-9, "Folder geometry must not be rebuilt when entering it.");
                    Assert.AreEqual(worldBefore.Width, worldAfter.Width, 1e-9);
                    Assert.IsNotNull(map.ScreenBoundsOf(photos.Id), "Neighbors stay in place (dimmed) around the open folder.");
                    await WaitFor(async () =>
                    {
                        await Settle(window, map, "disk-usage-folder.png");
                        return map.VisibleTiles.Any(tile => tile.Item.Name == "Walkthrough.mp4");
                    }, "Nested folders should load and fade in as they grow.");

                    // Back reverses the same camera path; Forward repeats it.
                    var back = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.XButton1)
                        { RoutedEvent = Mouse.PreviewMouseDownEvent };
                    window.RaiseEvent(back);
                    Assert.IsTrue(back.Handled);
                    await WaitFor(() => !vm.IsBusy && vm.CurrentPath == @"C:\", "Back should return to the drive.");
                    Assert.IsTrue(map.IsAnimating, "Back should zoom out of the folder.");
                    map.AdvanceTime(1);
                    Assert.AreEqual(0, map.FocusFolderId);
                    Assert.IsTrue(((Button)window.FindName("ForwardButton")).IsEnabled);
                    var forward = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.XButton2)
                        { RoutedEvent = Mouse.PreviewMouseDownEvent };
                    window.RaiseEvent(forward);
                    Assert.IsTrue(forward.Handled);
                    await WaitFor(() => !vm.IsBusy && vm.CurrentPath == @"C:\Videos", "Forward should reopen the folder.");
                    map.AdvanceTime(1);
                    Assert.AreEqual(videos.Id, map.FocusFolderId);
                    await vm.UpAsync();
                    map.AdvanceTime(1);
                    Assert.AreEqual(@"C:\", vm.CurrentPath);

                    // The wheel zooms about the pointer and keeps the point under it fixed.
                    await Settle(window, map, "disk-usage-wheel-start.png");
                    var music = map.ScreenBoundsOf(vm.Items.Single(item => item.Name == "Music.flac").Id)!.Value;
                    var pointer = Center(music);
                    var anchor = WorldAt(map, pointer);
                    var startWidth = map.Camera.Width;
                    Assert.IsTrue(map.ZoomWithWheel(pointer, 120));
                    Assert.IsTrue(map.ZoomWithWheel(pointer, 120), "Successive wheel ticks must work during animation.");
                    map.AdvanceTime(1);
                    Assert.IsFalse(map.IsAnimating);
                    Assert.AreEqual(startWidth * .8 * .8, map.Camera.Width, startWidth * 1e-9);
                    var anchored = WorldAt(map, pointer);
                    Assert.AreEqual(anchor.X, anchored.X, startWidth * 1e-9);
                    Assert.AreEqual(anchor.Y, anchored.Y, startWidth * 1e-9);
                    Assert.AreEqual(@"C:\", vm.CurrentPath, "Wheel zoom over a file must stay in its containing folder.");
                    Assert.IsTrue(map.IsZoomed);
                    ((Button)window.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    map.AdvanceTime(1);
                    Assert.IsFalse(map.IsZoomed, "Back first returns to the whole folder.");
                    Assert.AreEqual(startWidth, map.Camera.Width, startWidth * 1e-9);

                    // Wheel into a folder: the list follows once the zoom settles inside it.
                    await Settle(window, map, "disk-usage-wheel-before-enter.png");
                    pointer = Center(map.ScreenBoundsOf(videos.Id)!.Value);
                    for (var i = 0; i < 5; i++) Assert.IsTrue(map.ZoomWithWheel(pointer, 120));
                    map.AdvanceTime(1);
                    await WaitFor(() => vm.CurrentPath.StartsWith(@"C:\Videos", StringComparison.OrdinalIgnoreCase),
                        "Wheel zoom should enter the folder under the pointer.");
                    await Settle(window, map, "disk-usage-wheel-folder.png");
                    Assert.IsTrue(map.ZoomWithWheel(new Point(map.ActualWidth / 2, map.ActualHeight / 2), -1200));
                    map.AdvanceTime(1);
                    await WaitFor(() => vm.CurrentPath == @"C:\", "Scrolling out of a folder should return to its parent.");
                    await Settle(window, map, "disk-usage-wheel-out.png");

                    // Empty folders have no area; the map says so over the parent.
                    await vm.NavigateAsync(4);
                    Assert.IsTrue(vm.IsEmpty);
                    map.AdvanceTime(1);
                    Assert.AreEqual(4, map.FocusFolderId);
                    Render(window, "disk-usage-empty.png", 802, 580);

                    var picker = (ComboBox)window.FindName("Drives");
                    picker.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, @"D:\");
                    while (vm.IsBusy) await Task.Delay(5);
                    Assert.AreEqual(@"D:\", vm.CurrentPath);
                    ((Button)window.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    while (vm.IsBusy) await Task.Delay(5);
                    Assert.AreEqual(@"C:\Empty folder", vm.CurrentPath);
                    Assert.AreEqual(@"C:\", picker.SelectedItem);
                    await vm.NavigateAsync(0);
                    var list = (ListView)window.FindName("ItemList");
                    list.SelectedItem = vm.Items.Single(item => item.Name == "Notes.txt");
                    allowDelete = true;
                    ((Button)window.FindName("DeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    while (vm.IsBusy) await Task.Delay(5);
                    Assert.AreEqual(1, deletionCalls);
                    Assert.IsFalse(vm.Items.Any(item => item.Name == "Notes.txt"));
                    await vm.LoadAsync(@"C:\");
                    Assert.IsFalse(vm.Items.Any(item => item.Name == "Notes.txt"));
                    await Settle(window, map, "disk-usage-restored.png");

                    // A folder with 100,000 files stays cheap: grouped tails, no per-file controls.
                    var dense = new VolumeIndex(@"E:\", 3);
                    dense.Add(-1, @"E:\", 0, 0, 0, FileAttributes.Directory);
                    dense.Add(0, "Many", 0, 0, 0, FileAttributes.Directory);
                    for (var i = 1; i <= 100000; i++) dense.Add(1, $"File {i}.dat", 1 + i % 127, 0, 0, FileAttributes.Normal);
                    var denseSnapshot = DiskUsageSnapshot.Build(dense, CancellationToken.None);
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    map.SetSource(denseSnapshot.Item(0), (id, token) => denseSnapshot.Children(id, token), [1]);
                    await WaitFor(() => map.PendingLoads == 0, "Initial folder layout should finish in the background.");
                    map.AdvanceTime(1);
                    Render(window, "disk-usage-dense.png", 1072, 900);
                    TestContext.WriteLine($"100,000 files: first frame {timer.ElapsedMilliseconds} ms; {map.VisibleTiles.Count} tiles drawn.");
                    Assert.IsTrue(map.VisibleTiles.Count <= 60000, "Dense folders must stay within the per-frame tile budget.");
                    var densePoint = new Point(map.ActualWidth * .4, map.ActualHeight * .4);
                    for (var i = 0; i < 14; i++) Assert.IsTrue(map.ZoomWithWheel(densePoint, 120));
                    map.AdvanceTime(1);
                    await WaitFor(async () =>
                    {
                        await Settle(window, map, "disk-usage-dense-detail.png");
                        return map.VisibleTiles.Any(tile => tile.Item.Id > 1 && tile.Item.Name.StartsWith("File "));
                    }, "Closer zoom must expose individual files.");
                }
                catch (Exception exception) { failure = exception; }
                finally { window?.Dispose(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(60)), "Window verification timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // Render, let background folder loads land, finish fades, and render again.
    private async Task Settle(UserControl window, DiskUsageTreemap map, string name)
    {
        Render(window, name, 1072, 900);
        for (var i = 0; i < 200 && map.PendingLoads > 0; i++) await Task.Delay(5);
        await Dispatcher.Yield(DispatcherPriority.Background);
        map.AdvanceTime(.5);
        Render(window, name, 1072, 900);
    }

    private static async Task WaitFor(Func<bool> condition, string message)
    {
        for (var i = 0; i < 600 && !condition(); i++) await Task.Delay(5);
        Assert.IsTrue(condition(), message);
    }

    private static async Task WaitFor(Func<Task<bool>> condition, string message)
    {
        for (var i = 0; i < 40; i++)
            if (await condition()) return;
        Assert.Fail(message);
    }

    private static double Area(Rect rect) => rect.Width * rect.Height;
    private static Point Center(Rect rect) => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    private static Point WorldAt(DiskUsageTreemap map, Point screen)
        => new(map.Camera.X + screen.X / map.ActualWidth * map.Camera.Width, map.Camera.Y + screen.Y / map.ActualHeight * map.Camera.Height);

    private static Rect ToWorld(DiskUsageTreemap map, Rect screen)
    {
        var topLeft = WorldAt(map, screen.TopLeft);
        return new Rect(topLeft.X, topLeft.Y, screen.Width / map.ActualWidth * map.Camera.Width, screen.Height / map.ActualHeight * map.Camera.Height);
    }

    private RenderTargetBitmap Render(UserControl window, string name, int width, int height)
    {
        FrameworkElement content = window;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        image.Render(background);
        image.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var path = Path.Combine(TestContext.TestResultsDirectory!, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = File.Create(path)) encoder.Save(stream);
        TestContext.AddResultFile(path);
        return image;
    }

    private static VolumeIndex Sample()
    {
        var index = new VolumeIndex(@"C:\", 1);
        index.Add(-1, @"C:\", 0, 0, 0, FileAttributes.Directory);
        index.Add(0, "Videos", 0, 0, 0, FileAttributes.Directory);
        index.Add(0, "Photos", 0, 0, 0, FileAttributes.Directory);
        index.Add(0, "Projects", 0, 0, 0, FileAttributes.Directory);
        index.Add(0, "Empty folder", 0, 0, 0, FileAttributes.Directory);
        index.Add(1, "Summer trip.mp4", 24L << 30, 0, 0, FileAttributes.Normal);
        index.Add(1, "Screen recordings", 0, 0, 0, FileAttributes.Directory);
        index.Add(6, "Walkthrough.mp4", 12L << 30, 0, 0, FileAttributes.Normal);
        index.Add(1, "Family archive.mkv", 8L << 30, 0, 0, FileAttributes.Normal);
        index.Add(2, "Photo library.zip", 30L << 30, 0, 0, FileAttributes.Normal);
        index.Add(3, "Project backup.zip", 18L << 30, 0, 0, FileAttributes.Normal);
        index.Add(0, "Music.flac", 10L << 30, 0, 0, FileAttributes.Normal);
        index.Add(0, "Documents.zip", 6L << 30, 0, 0, FileAttributes.Normal);
        index.Add(0, "Notes.txt", 512, 0, 0, FileAttributes.Normal);
        foreach (var (name, megabytes) in new[] { ("Cache.db", 128), ("Archive.zip", 240), ("Podcast.mp3", 160),
                     ("Design assets.zip", 280), ("Photoshoot.raw", 196), ("Demo.mp4", 320), ("Installer.msi", 224), ("Backup.zip", 360) })
            index.Add(0, name, (long)megabytes << 20, 0, 0, FileAttributes.Normal);
        return index;
    }
}
