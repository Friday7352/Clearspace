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
        var baseColor = ((SolidColorBrush)brush).Color;   // NEW (edit in place)
        // REMOVED (edit in place): the ThemeStudioWindow made here; there is no studio any more.
        var dialogOwner = new Window();
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
            // NEW (shelf and inspector): the column holding them goes on the side away from the sidebar.
            var dockCard = (FrameworkElement)preview.Explorer.FindName("DockCard");
            dockCard.Visibility = Visibility.Visible;
            ThemeExplorerLayout.Apply(preview.Explorer);
            Assert.AreEqual(0, Grid.GetColumn(dockCard), "Sidebar on the right: the Inspector and the Shelf at the left edge");
            dockCard.Visibility = Visibility.Collapsed;
            ThemeExplorerLayout.Apply(preview.Explorer);

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
            var maker = new StudioPatternWindow(dialogOwner, Colors.White, Colors.Orange, Color.FromRgb(23, 29, 48), drawn);
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
            var imageEditor = new StudioImageWindow(dialogOwner, png, fillsWindow: true, 1264d / 760);
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
            var builder = new StudioLayoutBuilderWindow(dialogOwner, doc with { LayoutJson = ThemeLayout.Write(ThemeLayout.Templates[1].Make()) });
            Render((FrameworkElement)builder.Content, "theme-studio-layout-builder.png", 1340, 860);
            builder.Dispose();
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
            // REMOVED (edit in place): the studio's own checks (StartPreview, ShowTool, SetMode, UseOnAllPanels,
            // DropColor, DropPattern, OpenLayoutBuilder inside the studio, and the Themes page hosting it). The studio
            // is gone; the checks below are for what replaced it.

            // NEW (edit in place): the editor lies over a window with the explorer's named parts. Parts are picked,
            // painted with brushes, moved and hidden; nothing reaches the screen until the editor is started.
            // CHANGED (side panel): the editor's panel sits in its own column beside the window, as in MainWindow.
            var host = new Grid();
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var stage = new Grid();
            stage.Children.Add(new ExplorerThemePreview());
            host.Children.Add(stage);
            var target = ((ExplorerThemePreview)stage.Children[0]).Explorer;
            var editor = new ThemeEditor(dialogOwner, target, null);
            stage.Children.Add(editor);
            Grid.SetColumn(editor.SidePanel, 1);
            host.Children.Add(editor.SidePanel);
            // NEW (hand of cards): the hand sits under the window, beside the panel.
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRowSpan(editor.SidePanel, 2);
            Grid.SetRow(editor.Hand, 1);
            host.Children.Add(editor.Hand);
            editor.ShowTab("Part");
            Render(host, "theme-edit-start.png", 1440, 900);
            editor.Select("Sidebar");
            Assert.AreEqual("Sidebar", editor.SelectedPartId);
            Render(host, "theme-edit-part.png", 1440, 900);
            editor.ShowTab("Theme");
            Render(host, "theme-edit-theme-tab.png", 1440, 900);
            editor.ShowTab("Part");
            editor.Paint("Sidebar", "Fill", Color.FromRgb(0x12, 0x34, 0x56));
            Assert.AreEqual(Color.FromRgb(0x12, 0x34, 0x56), ((SolidColorBrush)editor.Draft.Resources()["Part.Sidebar.Fill"]).Color);
            editor.Paint("Sidebar", "Outline", Color.FromRgb(0x65, 0x43, 0x21));
            Assert.AreEqual(Color.FromRgb(0x65, 0x43, 0x21), ((SolidColorBrush)editor.Draft.Resources()["Part.Sidebar.Edge"]).Color);
            editor.Paint("Toolbar", "Waves", Colors.Orange);
            Assert.IsNotInstanceOfType(editor.Draft.Resources()["Part.Toolbar.Fill"], typeof(SolidColorBrush), "Waves painted on the toolbar fill it with a pattern");
            editor.Paint("Toolbar", "Eraser", Colors.Orange);
            editor.Paint("Toolbar", "Rocket", Colors.Orange); // an unknown brush does nothing
            editor.PlacePart("Sidebar", "Right");
            Assert.AreEqual(Visibility.Visible, editor.Draft.Resources()["Explorer.SidebarOnRight"]);
            editor.PlacePart("Tabs", "Below");
            Assert.AreEqual(Visibility.Visible, editor.Draft.Resources()["Explorer.TabsBelowToolbar"]);
            editor.Select("Status");
            editor.PlacePart("Status", "Hidden");
            Assert.AreEqual(Visibility.Collapsed, editor.Draft.Resources()["Explorer.StatusBarVisibility"]);
            Assert.IsNull(editor.SelectedPartId, "Hiding the selected part lets go of it");
            editor.ShowPart("Status");
            Assert.AreEqual(Visibility.Visible, editor.Draft.Resources()["Explorer.StatusBarVisibility"]);
            Assert.AreEqual("Status", editor.SelectedPartId);
            editor.Select("Rocket");
            Assert.AreEqual("Status", editor.SelectedPartId, "An unknown part is ignored");
            editor.Select(null);
            Assert.IsNull(editor.SelectedPartId);

            // NEW (theme cards): a theme reads as a deck of cards; playing one onto another theme gives it that
            // part of the look, and taking it out puts Clearspace's own value back.
            var source = styled.Sticker("Decor.Front1", png, new Rect(.6, .5, .3, .4), .8).Layout("Explorer.SidebarOnRight", 1);
            var deck = ThemeCards.Deck(source, "Test");
            Assert.IsTrue(deck.Count >= 4, "A styled theme is several cards");
            Assert.AreEqual(CardKind.Palette, deck[0].Kind, "The palette comes first");
            foreach (var kind in new[] { CardKind.Palette, CardKind.Pattern, CardKind.Effect, CardKind.Layout, CardKind.Sticker })
                Assert.IsTrue(deck.Any(c => c.Kind == kind), kind + " card in the deck");
            Assert.AreEqual(deck.Count, deck.Select(c => c.Signature).Distinct().Count(), "No card twice");
            var blank = ThemeStudioDocument.FromDesign(ThemeDesign.Presets[0].Design);
            var pattern = deck.First(c => c.Kind == CardKind.Pattern && c.Keys.Contains("Part.Sidebar.Fill"));
            var played = ThemeCards.Play(blank, pattern, "Part.Files.Fill");
            Assert.IsNotInstanceOfType(played.Resources()["Part.Files.Fill"], typeof(SolidColorBrush), "A pattern card aimed at the file area fills it");
            var sticker = deck.First(c => c.Kind == CardKind.Sticker);
            var withSticker = ThemeCards.Play(blank, sticker);
            Assert.AreEqual(1, withSticker.Stickers().Count);
            Assert.AreEqual(2, ThemeCards.Play(withSticker, sticker).Stickers().Count, "A second copy takes another place");
            var layout = deck.First(c => c.Kind == CardKind.Layout);
            var laid = ThemeCards.Play(blank, layout);
            Assert.AreEqual(Visibility.Visible, laid.Resources()["Explorer.SidebarOnRight"]);
            var takenOut = ThemeCards.TakeOut(laid, layout).Resources();
            Assert.IsFalse(takenOut.Contains("Explorer.SidebarOnRight") && takenOut["Explorer.SidebarOnRight"] is Visibility.Visible, "Taking the card out moves the sidebar back");
            Assert.IsNotNull(ThemeCards.Look(deck[0]), "A card can be drawn from its own colors");
            // NEW (theme codes): a card and a whole theme go to a line of text and back; pictures stay behind.
            var paletteCard = deck.First(c => c.Kind == CardKind.Palette);
            var paletteCode = ThemeCodes.CardCode(paletteCard, out _);
            Assert.IsNotNull(paletteCode);
            StringAssert.StartsWith(paletteCode, "CS-COLORS-");
            Assert.IsTrue(ThemeCodes.TryRead("  " + paletteCode![..40] + "\n" + paletteCode[40..] + " ", out var fromCode, out var notTheme, out _, out _), "Line breaks and spaces from a chat are ignored");
            Assert.IsNull(notTheme);
            CollectionAssert.AreEquivalent(paletteCard.Keys.ToList(), fromCode!.Keys.ToList());
            Assert.IsNull(ThemeCodes.CardCode(sticker, out var whyNot), "A sticker is too big for a code");
            Assert.IsNotNull(whyNot);
            var themeCode = ThemeCodes.DeckCode(source, "Shared", out var leftOut);
            StringAssert.StartsWith(themeCode, "CS-DECK-");
            Assert.IsNotNull(leftOut, "The code says the sticker stayed behind");
            Assert.IsTrue(ThemeCodes.TryRead(themeCode, out _, out var sharedTheme, out var sharedName, out _));
            Assert.AreEqual("Shared", sharedName);
            Assert.AreEqual(((SolidColorBrush)source.Resources()["Accent"]).Color, ((SolidColorBrush)sharedTheme!.Resources()["Accent"]).Color);
            Assert.AreEqual(Visibility.Visible, sharedTheme.Resources()["Explorer.SidebarOnRight"]);
            // CHANGED (decks of cards): a deck code is its cards. Every card but the sticker comes back, and the
            // theme they make holds the same settings (and switches) as the theme the deck came from.
            var crispDeck = source with { Manifest = source.Manifest with { CrispText = true, FadeToolbar = true } };
            Assert.IsTrue(ThemeCodes.TryRead(ThemeCodes.DeckCode(crispDeck, "Shared", out _), out _, out var rebuilt, out _, out _));
            Assert.IsTrue(rebuilt!.Manifest.CrispText && rebuilt.Manifest.FadeToolbar, "The switches ride on the font and layout cards");
            var before = ThemeCards.Deck(crispDeck, "Shared").Where(c => c.Kind != CardKind.Sticker).Select(c => c.Kind + ":" + string.Join(",", c.Keys.OrderBy(k => k))).ToList();
            var after = ThemeCards.Deck(rebuilt, "Shared").Select(c => c.Kind + ":" + string.Join(",", c.Keys.OrderBy(k => k))).ToList();
            CollectionAssert.AreEquivalent(before, after, "The same cards, holding the same settings");
            // NEW (deck files): a deck file carries every card, pictures and fonts included, and makes the same theme.
            var deckPath = Path.Combine(Path.GetTempPath(), "clearspace-deck-" + Guid.NewGuid().ToString("N") + ThemeDeckFile.Extension);
            var notADeck = Path.Combine(Path.GetTempPath(), "clearspace-not-a-deck-" + Guid.NewGuid().ToString("N") + ThemeDeckFile.Extension);
            try
            {
                ThemeDeckFile.Write(deckPath, crispDeck, "Shared");
                var opened = ThemeDeckFile.Read(deckPath);
                Assert.AreEqual("Shared", opened.Name);
                Assert.AreEqual(ThemeCards.Deck(crispDeck, "Shared").Count, opened.Cards.Count, "Every card, the sticker too");
                var fromFile = opened.Theme();
                Assert.AreEqual(1, fromFile.Stickers().Count, "The sticker's picture came along");
                Assert.IsTrue(fromFile.Manifest.CrispText && fromFile.Manifest.FadeToolbar);
                File.WriteAllText(notADeck, "hello");
                Assert.ThrowsException<ThemeFormatException>(() => ThemeDeckFile.Read(notADeck), "A file that is not a deck is refused with a reason");
            }
            finally
            {
                if (File.Exists(deckPath)) File.Delete(deckPath);
                if (File.Exists(notADeck)) File.Delete(notADeck);
            }
            Assert.IsFalse(ThemeCodes.TryRead("CS-COLORS-notarealcode", out _, out _, out _, out _));
            Assert.IsFalse(ThemeCodes.TryRead("hello", out _, out _, out _, out _));
            var kept = Path.Combine(Path.GetTempPath(), "clearspace-cards-" + Guid.NewGuid().ToString("N"));
            try
            {
                ThemeCards.Keep(sticker, kept);
                var back = ThemeCards.Saved(kept).Single();
                Assert.AreEqual(sticker.Signature, back.Signature, "A kept card reads back the same");
                Assert.IsTrue(back.Images.Count > 0, "A kept sticker keeps its picture");
                ThemeCards.Forget(back, kept);
                Assert.AreEqual(0, ThemeCards.Saved(kept).Count);
            }
            finally { if (Directory.Exists(kept)) Directory.Delete(kept, true); }
            editor.Play(pattern, "Files");
            Assert.IsNotInstanceOfType(editor.Draft.Resources()["Part.Files.Fill"], typeof(SolidColorBrush), "A card dropped on the file area fills it");
            // NEW (arrange mode): Arrange puts the cards away and lets go of the selection; Style brings them back.
            editor.Select("Sidebar");
            editor.SetMode(true);
            Assert.IsTrue(editor.Arranging);
            Assert.IsNull(editor.SelectedPartId, "Arranging lets go of the selected part");
            Assert.AreEqual(Visibility.Collapsed, editor.Hand.Visibility, "No cards while arranging");
            Render(host, "theme-edit-arrange.png", 1440, 900);
            editor.SetMode(false);
            Assert.IsFalse(editor.Arranging);
            Assert.AreEqual(Visibility.Visible, editor.Hand.Visibility);
            editor.ShowTab("Cards");
            Render(host, "theme-edit-cards.png", 1440, 900);
            Assert.IsTrue(editor.Cancel(ask: false));
            Assert.IsFalse(ThemeService.Drafting, "A cancelled editor leaves no draft on screen");

            // NEW (edit in place): a draft goes on screen everywhere and Cancel puts the theme from before back.
            var draft = ThemeStudioDocument.FromDesign(ThemeDesign.Presets[1].Design).Palette("#123456", "#64D8CF");
            Assert.IsNull(ThemeService.ShowDraft(draft));
            Assert.IsTrue(ThemeService.Drafting);
            Assert.AreEqual(Color.FromRgb(18, 52, 86), ((SolidColorBrush)app.FindResource("Base")).Color, "The draft is on screen");
            ThemeService.EndDraft(restore: true);
            Assert.IsFalse(ThemeService.Drafting);
            Assert.AreEqual(original, ThemeService.Current, "Cancel puts the theme from before back");
            Assert.AreEqual(baseColor, ((SolidColorBrush)app.FindResource("Base")).Color);

            // NEW (edit in place): Every setting opens as its own window over the editor.
            var settingsEditor = new ThemeEditor(dialogOwner, target, null);
            var settings = new ThemeSettingsWindow(dialogOwner, settingsEditor);
            Render((FrameworkElement)settings.Content, "theme-edit-every-setting.png", 520, 760);
            settings.Close();
            Assert.AreEqual("Sidebar corners", ThemeSettingsWindow.SettingLabel("Part.Sidebar.Corner"));
            Assert.AreEqual("1,2,3,4", ThemeSettingsWindow.Describe(new CornerRadius(1, 2, 3, 4)));
            settingsEditor.Cancel(ask: false);

            var color = new StudioColorWindow(dialogOwner, "#FF64D8CF", true);
            Assert.AreEqual("#FF64D8CF", color.SelectedColor);
            Render((FrameworkElement)color.Content, "theme-studio-color-wheel.png", 380, 760);
            color.Close();
            var windows = app.Windows.Count;

            // CHANGED (edit in place): Create a theme on the Themes page asks the window to go into edit mode;
            // the page itself stays as it is and can always close.
            using var gallery = new ThemesView();
            var galleryContent = gallery.Content;
            ThemeInfo? asked = null; var raised = false;
            gallery.EditRequested += (_, theme) => { raised = true; asked = theme; };
            gallery.OpenStudio(null);
            Assert.IsTrue(raised, "Create a theme asks the window");
            Assert.IsNull(asked, "A new theme");
            Assert.AreSame(galleryContent, gallery.Content);
            Assert.AreEqual(windows, app.Windows.Count, "Editing must stay inside Clearspace.");
            Assert.IsTrue(gallery.TryClose());
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
        finally { if (ThemeService.Drafting) ThemeService.EndDraft(restore: true); dialogOwner.Close(); app.ShutdownMode = shutdown; }
        // CHANGED (edit in place): the draft check puts the theme back as a new copy, so the colour is compared.
        Assert.AreEqual(baseColor, ((SolidColorBrush)app.FindResource("Base")).Color);
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
