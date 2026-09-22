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

[TestClass]
public sealed class DiskUsageWindowTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void WindowRendersAndFolderTilesNavigateWithRealApplicationStyles()
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
                    string? confirmation = null;
                    using var vm = new DiskUsageViewModel(() => [index, second], deletion);
                    var windowCount = app.Windows.Count;
                    window = new DiskUsageView(vm, confirmDelete: request => { confirmation = request.ConfirmationMessage; return allowDelete; });
                    var host = new ContentControl { Content = window };
                    Assert.AreSame(window, host.Content);
                    Assert.AreEqual(windowCount, app.Windows.Count, "Analyzer must be embedded without creating a window.");
                    host.Content = null;
                    await vm.LoadAsync();
                    var map = (DiskUsageTreemap)window.FindName("Treemap");
                    var folderTransitionStarted = false;
                    vm.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(DiskUsageViewModel.MapItems)) folderTransitionStarted = map.IsAnimating;
                    };
                    Render(window, "disk-usage-large.png", 1900, 1000);
                    Assert.IsTrue(map.ActualHeight >= 850, "On a large screen the square should occupy most of the available height.");
                    Render(window, "disk-usage-wide.png", 1072, 900);
                    var darkList = (ListView)window.FindName("ItemList");
                    darkList.IsEnabled = false;
                    var disabledPreview = Render(window, "disk-usage-loading.png", 1072, 900);
                    var listPoint = darkList.TranslatePoint(new Point(12, darkList.ActualHeight - 8), window);
                    var pixel = new byte[4];
                    disabledPreview.CopyPixels(new Int32Rect((int)listPoint.X, (int)listPoint.Y, 1, 1), pixel, 4, 0);
                    Assert.IsTrue(pixel[0] < 70 && pixel[1] < 70 && pixel[2] < 70,
                        "The list's disabled/loading background must stay dark instead of flashing the system light color.");
                    darkList.ClearValue(UIElement.IsEnabledProperty);
                    Assert.IsTrue(map.Children.Count > 0);
                    // The filled map retains byte ratios without blank packing regions.
                    var occupiedArea = map.TileButtons.Sum(tile => tile.Clip?.GetArea() ?? tile.ActualWidth * tile.ActualHeight);
                    Assert.AreEqual(map.ActualWidth, map.ActualHeight, 1e-8);
                    foreach (var tile in map.TileButtons)
                    {
                        var item = (DiskUsageItem)tile.Tag;
                        Assert.AreEqual(item.Share / 100, (tile.Clip?.GetArea() ?? tile.ActualWidth * tile.ActualHeight) / occupiedArea, 1e-8,
                            $"Rendered area differs from bytes for {item.Name}.");
                    }
                    var parentMap = map.CurrentMap;
                    var parentBounds = map.TileButtons.ToDictionary(button => ((DiskUsageItem)button.Tag).Id,
                        button => new Rect(Canvas.GetLeft(button) / map.ActualWidth, Canvas.GetTop(button) / map.ActualHeight,
                            button.Width / map.ActualWidth, button.Height / map.ActualHeight));
                    var region = map.ClusterBounds;
                    Assert.IsFalse(region.IsEmpty);
                    Assert.AreEqual(region.Width, region.Height, 1e-8);
                    var tiny = map.TileButtons.First(button => button.Content is null &&
                        region.Contains(new Point(Canvas.GetLeft(button) + button.Width / 2, Canvas.GetTop(button) + button.Height / 2)));
                    Assert.IsTrue(map.TryZoomCluster(new Point(Canvas.GetLeft(tiny) + tiny.Width / 2, Canvas.GetTop(tiny) + tiny.Height / 2)));
                    Assert.AreSame(parentMap, map.CurrentMap, "Zoom must retain the exact geometry and colors without rebuilding it.");
                    Assert.IsTrue(map.IsZoomed);
                    Assert.IsTrue(map.VisibleItems.Count > 1, "Zoom must show the surrounding cluster, not a single-file panel.");
                    await Task.Delay(360);
                    Render(window, "disk-usage-zoom-transition.png", 1072, 900);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.IsFalse(map.IsAnimating, "Cluster zoom should finish before accepting file actions.");
                    Render(window, "disk-usage-zoom.png", 1072, 900);
                    Assert.IsTrue(map.TileButtons.Count(button => button.Content is not null) >= 2);
                    foreach (var tile in map.TileButtons)
                    {
                        var original = parentBounds[((DiskUsageItem)tile.Tag).Id];
                        var magnified = new Rect((original.X - map.Camera.X) / map.Camera.Width,
                            (original.Y - map.Camera.Y) / map.Camera.Height, original.Width / map.Camera.Width, original.Height / map.Camera.Height);
                        var clipped = Rect.Intersect(magnified, new Rect(0, 0, 1, 1));
                        Assert.AreEqual(clipped.X, Canvas.GetLeft(tile) / map.ActualWidth, 1e-8);
                        Assert.AreEqual(clipped.Y, Canvas.GetTop(tile) / map.ActualHeight, 1e-8);
                        Assert.AreEqual(clipped.Width, tile.Width / map.ActualWidth, 1e-8);
                        Assert.AreEqual(clipped.Height, tile.Height / map.ActualHeight, 1e-8);
                        if (tile.Content is Grid label)
                        {
                            var caption = ((StackPanel)label.Children[1]).Children.OfType<TextBlock>().Last();
                            var bottom = caption.TransformToAncestor(tile).Transform(new Point(0, caption.ActualHeight)).Y;
                            Assert.IsTrue(bottom <= tile.ActualHeight, $"Size label for {((DiskUsageItem)tile.Tag).Name} should fit inside its tile: {bottom} > {tile.ActualHeight}.");
                        }
                    }
                    Assert.IsFalse(((Button)window.FindName("DeleteButton")).IsEnabled, "Zooming a group must not select it for deletion.");
                    var zoomFile = map.TileButtons.Single(button => ((DiskUsageItem)button.Tag).Name == "Cache.db");
                    ((MenuItem)zoomFile.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    while (vm.IsBusy) await Task.Delay(5);
                    Assert.AreEqual(0, deletionCalls);
                    StringAssert.Contains(confirmation!, @"C:\Cache.db");
                    Assert.IsTrue(vm.Items.Any(item => item.Name == "Notes.txt"));
                    ((Button)window.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(360);
                    Assert.IsFalse(map.IsZoomed);
                    Assert.AreSame(parentMap, map.CurrentMap, "Zooming out must restore the original map without repacking.");
                    Assert.AreEqual(@"C:\", vm.CurrentPath);
                    Render(window, "disk-usage-restored.png", 1072, 900);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.AreEqual(map.ActualWidth, map.ActualHeight, 1e-8);
                    var videos = map.TileButtons.Single(button => button.ToolTip.ToString()!.StartsWith("Videos\n"));
                    videos.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    while (vm.IsBusy) await Task.Delay(5);
                    Assert.AreEqual(@"C:\Videos", vm.CurrentPath);
                    Assert.IsTrue(vm.CanGoUp);
                    Assert.IsTrue(folderTransitionStarted, "Opening a folder should zoom from its box into its contents.");
                    await Task.Delay(120);
                    Render(window, "disk-usage-folder-transition.png", 1072, 900);
                    await Task.Delay(240);
                    Render(window, "disk-usage-folder-end.png", 1072, 900);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.IsFalse(map.IsAnimating);
                    Render(window, "disk-usage-folder.png", 802, 580);
                    Assert.AreEqual(map.ActualWidth, map.ActualHeight, 1e-8);
                    var back = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.XButton1)
                        { RoutedEvent = Mouse.PreviewMouseDownEvent };
                    window.RaiseEvent(back);
                    while (vm.IsBusy) await Task.Delay(5);
                    Assert.IsTrue(back.Handled);
                    Assert.AreEqual(@"C:\", vm.CurrentPath);
                    Assert.IsTrue(folderTransitionStarted, "Back should reverse the folder zoom.");
                    Assert.IsTrue(((Button)window.FindName("ForwardButton")).IsEnabled);
                    var forward = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.XButton2)
                        { RoutedEvent = Mouse.PreviewMouseDownEvent };
                    window.RaiseEvent(forward);
                    while (vm.IsBusy) await Task.Delay(5);
                    Assert.IsTrue(forward.Handled);
                    Assert.AreEqual(@"C:\Videos", vm.CurrentPath);
                    Assert.IsTrue(folderTransitionStarted, "Forward should zoom into the folder even if the previous transition is still finishing.");
                    await vm.UpAsync();
                    Assert.AreEqual(@"C:\", vm.CurrentPath);
                    await vm.NavigateAsync(4); // Empty folder still supports navigation.
                    Assert.IsTrue(vm.IsEmpty);
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
                    Render(window, "disk-usage-wheel-start.png", 1072, 900);
                    var music = map.TileButtons.Single(button => ((DiskUsageItem)button.Tag).Name == "Music.flac");
                    var pointer = new Point(Canvas.GetLeft(music) + music.Width / 2, Canvas.GetTop(music) + music.Height / 2);
                    var anchor = new Point(pointer.X / map.ActualWidth, pointer.Y / map.ActualHeight);
                    Assert.IsTrue(map.ZoomWithWheel(pointer, 120));
                    Assert.AreEqual(.78, map.Camera.Width, 1e-10);
                    Assert.AreEqual(anchor.X, map.Camera.X + anchor.X * map.Camera.Width, 1e-10);
                    Assert.AreEqual(anchor.Y, map.Camera.Y + anchor.Y * map.Camera.Height, 1e-10);
                    Assert.IsTrue(map.ZoomWithWheel(pointer, 120), "Successive wheel ticks must work during animation.");
                    Assert.AreEqual(.78 * .78, map.Camera.Width, 1e-10);
                    await Task.Delay(240);
                    Render(window, "disk-usage-wheel-in.png", 1072, 900);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.IsFalse(map.IsAnimating);
                    Assert.AreEqual(@"C:\", vm.CurrentPath, "Wheel zoom over a file must stay in its containing folder.");
                    Assert.IsTrue(map.ZoomWithWheel(pointer, -240));
                    await Task.Delay(240);
                    Render(window, "disk-usage-wheel-out.png", 1072, 900);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.AreEqual(new Rect(0, 0, 1, 1), map.Camera);
                    Assert.IsFalse(map.IsZoomed);
                    videos = map.TileButtons.Single(button => ((DiskUsageItem)button.Tag).Name == "Videos");
                    pointer = new Point(Canvas.GetLeft(videos) + videos.Width / 2, Canvas.GetTop(videos) + videos.Height / 2);
                    Assert.IsTrue(map.ZoomWithWheel(pointer, 600));
                    await Task.Delay(240);
                    Render(window, "disk-usage-wheel-enter.png", 1072, 900);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    while (vm.IsBusy) await Task.Delay(5);
                    Assert.AreEqual(@"C:\Videos", vm.CurrentPath, "Wheel zoom should enter the folder under the pointer.");
                    await Task.Delay(360);
                    Render(window, "disk-usage-wheel-folder.png", 1072, 900);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.IsTrue(map.ZoomWithWheel(new Point(map.ActualWidth / 2, map.ActualHeight / 2), -120));
                    while (vm.IsBusy) await Task.Delay(5);
                    Assert.AreEqual(@"C:\", vm.CurrentPath, "Scrolling out of a folder should return to its parent.");
                    var dominantItems = NestedDiskMap.Colorize(new DiskUsageItem[] {
                        new DiskUsageItem(1, "AppData", 9400, 100, true) { Share = 94 },
                        new DiskUsageItem(2, "Small folder", 300, 2, true) { Share = 3 },
                        new DiskUsageItem(3, "Small file", 200, 1, false) { Share = 2 },
                        new DiskUsageItem(4, "Tiny file", 100, 1, false) { Share = 1 }
                    });
                    var dominantScene = NestedDiskMap.Create(dominantItems, id => id == 1 ? NestedDiskMap.Colorize([
                        new DiskUsageItem(10, "Local", 6500, 60, true), new DiskUsageItem(11, "Roaming", 2900, 40, true)]) :
                        id == 10 ? NestedDiskMap.Colorize([new DiskUsageItem(20, "Browser cache", 4000, 40, false), new DiskUsageItem(21, "Game data", 2500, 20, false)]) :
                        id == 11 ? NestedDiskMap.Colorize([new DiskUsageItem(30, "Creative apps", 2000, 20, false), new DiskUsageItem(31, "Settings", 900, 20, false)]) : []);
                    map.SetItems(dominantItems, prepared: dominantScene);
                    Assert.IsNull(dominantScene.Tiles.Single(tile => tile.Item.Id == 1).Inside);
                    Render(window, "disk-usage-dominant-folder.png", 1072, 900);
                    var dominant = map.TileButtons.Single(button => ((DiskUsageItem)button.Tag).Name == "AppData");
                    Assert.IsNotNull(dominant.Content);
                    Assert.IsNotNull(dominant.Clip);
                    Assert.AreEqual(.94, dominant.Clip.GetArea() / (map.ActualWidth * map.ActualHeight), 1e-7);
                    var folderPoint = new Point(Canvas.GetLeft(dominant) + dominant.Width / 2, Canvas.GetTop(dominant) + dominant.Height / 2);
                    Assert.IsFalse(map.IsClusterHit(folderPoint), "The small-items square must not intercept a labeled folder's click.");
                    Assert.IsFalse(map.TryZoomCluster(folderPoint));
                    var cornerFile = map.TileButtons.Single(button => ((DiskUsageItem)button.Tag).Name == "Tiny file");
                    var cornerPoint = new Point(Canvas.GetLeft(cornerFile) + cornerFile.Width / 2, Canvas.GetTop(cornerFile) + cornerFile.Height / 2);
                    Assert.IsTrue(map.TryZoomCluster(cornerPoint), "The clipped dominant folder must not intercept clicks in the smaller-item corner.");
                    var manyFiles = NestedDiskMap.Colorize(Enumerable.Range(1, 100000)
                        .Select(i => new DiskUsageItem(i, $"File {i}.dat", 1 + i % 127, 1, false)).ToArray());
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var denseScene = NestedDiskMap.Create(manyFiles);
                    var modelMs = timer.ElapsedMilliseconds;
                    map.SetItems(manyFiles, prepared: denseScene);
                    window.UpdateLayout();
                    var layoutMs = timer.ElapsedMilliseconds;
                    Render(window, "disk-usage-dense.png", 1072, 900);
                    TestContext.WriteLine($"100,000 files: model {modelMs} ms; model + layout {layoutMs} ms; including PNG export {timer.ElapsedMilliseconds} ms; {map.TileButtons.Count} interactive controls.");
                    Assert.IsTrue(map.TileButtons.Count < 650, "Dense folders should use lightweight mosaics, not one control per file.");
                    Assert.AreEqual(100000, map.CurrentMap.Items.Count, "Detail must remain available for deeper zoom.");
                    var denseMap = map.CurrentMap;
                    var densePoint = new Point(map.ActualWidth * .4, map.ActualHeight * .4);
                    for (var i = 0; i < 8; i++) Assert.IsTrue(map.ZoomWithWheel(densePoint, 120));
                    await Task.Delay(240);
                    Render(window, "disk-usage-dense-zoom.png", 1072, 900);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Render(window, "disk-usage-dense-detail.png", 1072, 900);
                    Assert.AreSame(denseMap, map.CurrentMap);
                    Assert.IsTrue(map.TileButtons.Count < 650);
                    Assert.IsTrue(map.TileButtons.Any(button => ((DiskUsageItem)button.Tag).Id > 0), "Closer zoom must expose individual files.");
                }
                catch (Exception exception) { failure = exception; }
                finally { window?.Dispose(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "Window verification timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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
