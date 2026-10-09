using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clearspace.Controls;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

internal static class ThemeStudioWindowChecks
{
    internal static void Verify(App app)
    {
        var original = ThemeService.Current;
        var brush = app.FindResource("Base");
        var originalIcons = ThemeService.Icons;
        var shutdown = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var editor = new ThemeStudioWindow();
        try
        {
            var preview = new ExplorerThemePreview();
            var doc = ThemeStudioDocument.FromDesign(ThemeDesign.Presets[1].Design).Palette("#123456", "#64D8CF")
                .Layout("Explorer.SidebarOnRight", 1).Layout("Explorer.ToolbarBottom", 1).Layout("Explorer.SidebarWidth", 300);
            preview.Show(doc);
            Assert.AreSame(brush, app.FindResource("Base"), "Preview must never mutate global theme resources.");
            Assert.AreEqual(original, ThemeService.Current);
            Assert.AreEqual(originalIcons, ThemeService.Icons);
            Assert.AreEqual(Color.FromRgb(18, 52, 86), ((SolidColorBrush)preview.Explorer.FindResource("Base")).Color);
            Assert.IsFalse(preview.Explorer.IsHitTestVisible, "Sample files must never receive input.");
            // CHANGED (complex themes): columns are reordered rather than renumbered, so a sidebar on the right
            // is in the last column (after the file list and the preview pane).
            var bodyGrid = (Grid)preview.Explorer.FindName("BrowserBody");
            Assert.AreEqual(bodyGrid.ColumnDefinitions.Count - 1, Grid.GetColumn((UIElement)preview.Explorer.FindName("SidebarScroll")));
            Assert.AreEqual(1, Grid.GetRow((UIElement)preview.Explorer.FindName("NavigationArea")));
            Assert.AreEqual(4, ((GridView)((ListView)preview.Explorer.FindName("FileList")).View).Columns.Count);

            // NEW (complex themes): the other parts move too, and moving them back restores the original order.
            var moved = doc.Layout("Explorer.TabsBelowToolbar", 1).Layout("Explorer.StatusBarTop", 1).Layout("Explorer.PreviewOnLeft", 1)
                .Layout("Explorer.SidebarOnRight", 0).Layout("FunctionKeysVisibility", 1);
            preview.ShowPreviewPane = true;
            preview.Show(moved);
            var status = (Border)preview.Explorer.FindName("StatusBar");
            Assert.AreEqual(0, Grid.GetRow(status), "Status bar above the list");
            Assert.AreEqual(0, Grid.GetRow((UIElement)preview.Explorer.FindName("ToolbarRow")), "Toolbar above the tabs");
            var previewColumn = Grid.GetColumn((UIElement)preview.Explorer.FindName("PreviewCard"));
            var filesColumn = Grid.GetColumn((UIElement)preview.Explorer.FindName("ContentCard"));
            Assert.IsTrue(previewColumn < filesColumn, "Preview pane left of the files");
            Assert.AreEqual(filesColumn, Grid.GetColumn((UIElement)preview.Explorer.FindName("RenamePanel")), "The rename panel stays over the files");
            Render(preview, "theme-studio-explorer-moved.png", 1264, 760);
            preview.Show(doc.Layout("Explorer.StatusBarVisibility", 0));
            Assert.AreEqual(Visibility.Collapsed, status.Visibility);
            Assert.AreEqual(2, Grid.GetRow(status), "Status bar back under the list");
            preview.ShowPreviewPane = false;

            // NEW (complex themes): every pattern, effect and the binding load as theme brushes.
            var styled = doc;
            foreach (var kind in Enum.GetValues<StudioPattern>())
                styled = styled.Paint("Part.Sidebar.Fill", StudioPatterns.Pattern(kind, Colors.White, Colors.Orange, null, 1, .4));
            styled = styled.Paint("Base", StudioPatterns.Pattern(StudioPattern.Blueprint, Colors.White, Colors.Orange, Color.FromRgb(11, 37, 69), 1, .3))
                .Paint("OverlayFill", StudioPatterns.Overlay(StudioOverlay.Scanlines, true, .6)!).Layout("OverlayVisibility", 1)
                .Paint("EdgeShade", StudioPatterns.Shade(StudioShade.Vignette, Colors.Orange, .6)!)
                .Paint("BindingFill", StudioPatterns.Binding(true)).Layout("BindingVisibility", 1)
                .Corner("Part.Files.Corner", 20).Spacing("Part.Files.EdgeWidth", 2, 2, 2, 2).Color("Part.Files.Edge", "#FFFFA500");
            // NEW (pattern maker, every setting): a drawn pattern, and values typed into Every setting, load too.
            var drawn = new StudioCustomPattern("Test", 8, new string('1', 8) + new string('0', 48) + new string('2', 8), "#FF9DCB");
            Assert.IsTrue(drawn.IsValid);
            styled = styled.Paint("Part.Status.Fill", StudioPatterns.Custom(drawn, Colors.White, Colors.Orange, null, 1, .5))
                .Write("R8", "3").Write("Part.Sidebar.EdgeWidth", "0,0,1,0").Write("NoteTilt", "-2").Write("InterfaceScale", "110%")
                .Write("Explorer.StatusBarTop", "Visible").Write("UIFont", "Georgia").Write("Ink", "#EEEEEE");
            var typed = styled.Resources();
            Assert.AreEqual(1.1, ((ScaleTransform)typed["InterfaceScale"]).ScaleX, 1e-9);
            Assert.AreEqual(new CornerRadius(3), typed["R8"]);
            Assert.AreEqual(-2, ((RotateTransform)typed["NoteTilt"]).Angle, 1e-9);
            Assert.ThrowsException<ArgumentException>(() => styled.Write("NoteTilt", "lots"));
            var maker = new StudioPatternWindow(editor, Colors.White, Colors.Orange, Color.FromRgb(23, 29, 48), drawn);
            Render((FrameworkElement)maker.Content, "theme-studio-pattern-maker.png", 640, 560);
            maker.Close();
            // NEW (stickers, picture themes, edge strips)
            var png = TestPicture();
            var stuck = doc.Sticker("Decor.Front1", png, new Rect(.6, .5, .3, .4), .8);
            Assert.AreEqual(1, stuck.Stickers().Count);
            stuck = stuck.PlaceSticker("Decor.Front1", new Rect(.1, .1, .2, .2), .5).MoveSticker("Decor.Front1", "Decor.Back2");
            var only = stuck.Stickers().Single();
            Assert.AreEqual("Decor.Back2", only.Key);
            Assert.AreEqual(.5, only.Opacity, 1e-6);
            Assert.IsFalse(only.Front);
            Assert.IsTrue(Math.Abs(only.Box.X - .1) < 1e-6 && Math.Abs(only.Box.Width - .2) < 1e-6);
            preview.Show(stuck.Paint("BindingFill", StudioPatterns.Tape(Colors.Yellow, Colors.Black)).Layout("BindingVisibility", 1));
            Assert.IsInstanceOfType(preview.Explorer.FindResource("Decor.Back2"), typeof(ImageBrush));
            // NEW (sticker editor): the editor opens on a picture, and an edited picture replaces the old one.
            var imageEditor = new StudioImageWindow(editor, png, fillsWindow: true, 1264d / 760);
            Render((FrameworkElement)imageEditor.Content, "theme-studio-picture-editor.png", 920, 560);
            imageEditor.Close();
            var swapped = stuck.ReplaceStickerImage("Decor.Back2", TestPicture());
            Assert.AreEqual(1, swapped.Stickers().Count);
            Assert.AreEqual(only.Box, swapped.Stickers().Single().Box);
            // NEW (custom layouts): every ready-made layout is valid, writes and reads back the same, and lays out;
            // taking the layout away puts every part back where MainWindow.xaml has it.
            var card = (FrameworkElement)preview.Explorer.FindName("ContentCard");
            var home = card.Parent;
            foreach (var (name, _, make) in ThemeLayout.Templates)
            {
                var json = ThemeLayout.Write(make());
                Assert.AreEqual(json, ThemeLayout.Write(ThemeLayout.Parse(json)!), name);
                preview.Show(doc with { LayoutJson = json });
                Assert.AreNotSame(home, card.Parent, name + " moves the file list into the layout");
                if (name == "Dual pane") Render(preview, "theme-studio-layout-dual-pane.png", 1264, 760);
                if (name == "Four panes") Render(preview, "theme-studio-layout-four-panes.png", 1264, 760);
            }
            preview.Show(doc with { LayoutJson = null });
            Assert.AreSame(home, card.Parent, "Without a layout the file list is back in its place");
            Assert.AreEqual(Visibility.Visible, ((FrameworkElement)preview.Explorer.FindName("BrowserBody")).Visibility);
            foreach (var bad in new[] { "{\"root\":{\"block\":\"pane\"}}", "{\"root\":{\"split\":\"row\",\"children\":[{\"block\":\"files\"},{\"block\":\"files\"}]}}",
                                         "{\"root\":{\"block\":\"files\",\"size\":\"huge\"}}", "{\"root\":{\"block\":\"rocket\"}}", "not json" })
                Assert.IsFalse(ThemeLayout.TryParse(bad, out _, out var why) || why is null, bad);
            var builder = new StudioLayoutBuilderWindow(editor, doc with { LayoutJson = ThemeLayout.Write(ThemeLayout.Templates[1].Make()) });
            Render((FrameworkElement)builder.Content, "theme-studio-layout-builder.png", 1340, 860);
            builder.Close();
            var palette = StudioPicture.Read(StudioPicture.Load(png));
            Assert.IsTrue(ThemeDesign.Contrast(palette.Accent, palette.Background) >= 3, "The accent must stand out on the background");
            foreach (var kind in Enum.GetValues<StudioOverlay>()) Assert.AreEqual(kind == StudioOverlay.None, StudioPatterns.Overlay(kind, false, .5) is null);
            foreach (var kind in Enum.GetValues<StudioShade>()) Assert.AreEqual(kind == StudioShade.None, StudioPatterns.Shade(kind, Colors.Red, .5) is null);
            preview.Show(styled);
            Assert.IsInstanceOfType(preview.Explorer.FindResource("Base"), typeof(DrawingBrush));
            Assert.AreEqual(new CornerRadius(20), ((Border)preview.Explorer.FindName("ContentCard")).CornerRadius);
            Render(preview, "theme-studio-explorer-styled.png", 1264, 760);
            // A theme that sets Surface but not the file area's own fill gets Surface there (ThemeParts).
            var plain = ThemeStudioDocument.FromDesign(ThemeDesign.Presets[1].Design).Resources();
            Assert.AreSame(plain["Surface"], plain["Part.Files.Fill"]);
            editor.StartPreview();
            Render((FrameworkElement)editor.Content, "theme-studio-simple.png", 1192, 760);
            editor.SetMode(true);
            Render((FrameworkElement)editor.Content, "theme-studio-advanced.png", 1192, 760);
            // CHANGED (theme studio, round 2): the page is header / body / footer now, and the settings are cards,
            // not Expanders. The body is the grid in the middle row; its first ScrollViewer holds the settings.
            var page = (Grid)editor.Content;
            var body = page.Children.OfType<Grid>().Single(g => Grid.GetRow(g) == 1);
            var scroll = body.Children.OfType<ScrollViewer>().First();
            scroll.ScrollToVerticalOffset(700);
            Render(page, "theme-studio-advanced-middle.png", 1192, 760);
            scroll.ScrollToVerticalOffset(1400);
            Render(page, "theme-studio-advanced-middle-2.png", 1192, 760);
            scroll.ScrollToVerticalOffset(2100);
            Render(page, "theme-studio-advanced-middle-3.png", 1192, 760);
            scroll.ScrollToEnd();
            Render(page, "theme-studio-advanced-options.png", 1192, 760);
            var color = new StudioColorWindow(editor, "#FF64D8CF", true);
            Assert.AreEqual("#FF64D8CF", color.SelectedColor);
            Render((FrameworkElement)color.Content, "theme-studio-color-wheel.png", 380, 760);
            color.Close();
            // NEW (readability): every generated mood reads at 4.5:1 or better for all three text colours.
            foreach (var (name, design) in ThemeDesign.Presets)
            {
                var r = design.Resources();
                Color C(string key) => ((SolidColorBrush)r[key]).Color;
                foreach (var key in new[] { "Ink", "InkMuted", "InkFaint" })
                    foreach (var under in new[] { "Base", "Surface" })
                        Assert.IsTrue(ThemeDesign.Contrast(C(key), C(under)) >= 4.5, $"{name}: {key} on {under}");
            }
            Render(preview, "theme-studio-explorer.png", 1264, 760);
        }
        finally { editor.Close(); app.ShutdownMode = shutdown; }
        Assert.AreSame(brush, app.FindResource("Base"));
        Assert.AreEqual(original, ThemeService.Current);
    }

    // NEW (stickers): a small two-colour picture, as PNG bytes.
    private static byte[] TestPicture()
    {
        const int size = 16;
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            var left = i % size < size / 2;
            pixels[i * 4] = (byte)(left ? 40 : 200); pixels[i * 4 + 1] = (byte)(left ? 30 : 60); pixels[i * 4 + 2] = (byte)(left ? 20 : 30); pixels[i * 4 + 3] = 255;
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream);
        return stream.ToArray();
    }

    private static void Render(FrameworkElement view, string name, int width, int height)
    {
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        // Keep visual evidence with the other generated UI previews, outside source control.
        var root = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(root, "Clearspace", "Clearspace.csproj")) && Directory.GetParent(root) is { } parent) root = parent.FullName;
        var folder = Path.Combine(root, "output", "previews"); Directory.CreateDirectory(folder);
        using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
    }
}
