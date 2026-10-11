using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Clearspace.Controls;
using Clearspace.Services;

namespace Clearspace;

// NEW (edit in place): making a theme happens on Clearspace itself. "Create a theme" and "Edit theme…" on the
// Themes page put the real window, with your own files, into edit mode; there is no separate studio.
// CHANGED (hand of cards): the theme's cards are back along the bottom, in their own row under the window (not
// over it), all showing at once; click one and it is changed in the panel's Cards tab. See ThemeEditor.Cards.cs.
// REWRITTEN (side panel): the first version laid a brush rack, a bar of small icons on each part, a hand of cards
// and a borrow sheet over the window. It felt busy and slow, its bars moved about and were hard to click, and
// it covered the explorer it was editing. Now:
//   - A slim bar across the top: the theme's name, Undo / Redo, a readability warning, Share, Cancel and Done.
//   - One side panel beside the window (ThemeEditor.Panel.cs), never over it, with three tabs:
//       Part   what you clicked on the window: its color, outline, pattern, corners, see-through, where it sits;
//       Theme  mood, main colors, fonts, layout, effects, pictures and Every setting;
//       Cards  this theme as cards (click one to change it or take it out) and cards to borrow from other themes.
//   - The window itself (ThemeEditor.Canvas.cs) only outlines the part under the pointer, picks a part on a click,
//     moves a part when you drag it, and moves or resizes stickers. A right-click lists every color a spot uses.
// The theme being made is on screen everywhere as it changes (ThemeService.ShowDraft), and Cancel puts the theme
// from before back. Nothing is saved until Done. The editor's own controls always look like Clearspace's Dark
// theme, whatever the theme being made looks like, so they stay readable.
internal sealed partial class ThemeEditor : Grid
{
    private readonly Window _owner;
    private readonly FrameworkElement _root;           // the window whose named parts are being styled
    private readonly ThemeInfo? _editing;
    private ThemeStudioDocument _document;
    private readonly ThemeStudioDocument _initial;
    private readonly Stack<ThemeStudioDocument> _undo = new(), _redo = new();
    private ResourceDictionary? _values;
    private readonly ResourceDictionary _fallback = Chrome();
    // CHANGED (side panel): 60 ms (was 90); the editor no longer rebuilds a hand of cards on every change, so the
    // draft can follow sooner.
    private readonly DispatcherTimer _draftTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private bool _finished, _started, _gesture, _gestureRecorded;

    /// <summary>The bar the window puts above itself while editing.</summary>
    internal Border Banner { get; }
    /// <summary>NEW (side panel): the panel the window puts beside itself while editing.</summary>
    internal Border SidePanel { get; }
    /// <summary>NEW (hand of cards): the row of cards the window puts under itself while editing.</summary>
    internal Border Hand { get; }
    /// <summary>Raised when editing ends: the saved theme's name, or null when it was cancelled.</summary>
    internal event EventHandler<string?>? Finished;
    /// <summary>The theme as it stands. For tests.</summary>
    internal ThemeStudioDocument Draft => _document;

    private readonly TextBox _name = new() { MaxLength = 60, MinWidth = 150 };
    private readonly Border _undoButton, _redoButton;
    private readonly Border _readability = new() { Padding = new Thickness(10, 0, 4, 0), CornerRadius = new CornerRadius(14), Height = 28, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };

    // Clearspace's own Dark look, for the editor's controls and as what a theme leaves out.
    // CHANGED (side panel): read once and shared (it was read four times each time the editor opened).
    private static ResourceDictionary? s_chrome;
    internal static ResourceDictionary Chrome() => s_chrome ??= ReadChrome();

    private static ResourceDictionary ReadChrome()
    {
        var chrome = new ResourceDictionary();
        chrome.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Clearspace;component/Themes/Defaults.xaml", UriKind.Relative) });
        chrome.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Clearspace;component/Themes/Dark/theme.xaml", UriKind.Relative) });
        return chrome;
    }

    /// <param name="owner">The window being edited (dialogs open over it).</param>
    /// <param name="root">Where its parts are found by name (the window itself).</param>
    /// <param name="editing">The theme to change, or null to make a new one from the theme on screen.</param>
    internal ThemeEditor(Window owner, FrameworkElement root, ThemeInfo? editing)
    {
        _owner = owner; _root = root; _editing = editing;
        if (editing is not null) _document = ThemeStudioDocument.Open(editing);
        else if (ThemeService.Themes.FirstOrDefault(t => string.Equals(t.Name, ThemeService.Current, StringComparison.OrdinalIgnoreCase)) is { BuiltIn: false, Problem: null } current)
            _document = ThemeStudioDocument.Open(current);
        else _document = ThemeStudioDocument.FromDesign(ThemeDesign.Presets[0].Design);
        _initial = _document;
        _name.Text = editing is null ? "My theme" : editing.Shipped ? editing.Label + " remix" : editing.Label;

        Resources.MergedDictionaries.Add(Chrome());
        Focusable = true; FocusVisualStyle = null;
        AutomationProperties.SetName(this, "Editing the theme. Click a part of the window to change it.");

        (_undoButton, _redoButton) = (BannerButton("Undo", "Undo  ·  Ctrl+Z", () => History(_undo, _redo)), BannerButton("Redo", "Redo  ·  Ctrl+Y", () => History(_redo, _undo)));
        Banner = BuildBanner();
        BuildCanvas();              // ThemeEditor.Canvas.cs
        SidePanel = BuildPanel();   // ThemeEditor.Panel.cs
        Hand = BuildHand();         // ThemeEditor.Cards.cs
        // CHANGED (hand of cards): the hand is redrawn with the draft (at most once per pause), not on every change.
        _draftTimer.Tick += (_, _) => { _draftTimer.Stop(); ShowDraft(); SyncHand(); };
        SizeChanged += (_, _) => Refresh();
    }

    /// <summary>Puts the theme being made on screen. Call once the editor is in the window.</summary>
    internal void Start()
    {
        if (_started) return;
        _started = true;
        ShowDraft();
        Sync();
        SyncHand();   // NEW (hand of cards)
        Focus();
        Report(_editing is null ? "Click any part of the window to change it." : $"Editing {_editing.Label}. Click any part of the window to change it.");
    }

    // ---------------------------------------------------------------- the theme being made

    private void TryChange(Func<ThemeStudioDocument> next)
    {
        try { Change(next()); }
        catch (Exception error) when (error is ThemeFormatException or ArgumentException or InvalidOperationException) { Report(error.Message, true); }
    }

    private void Change(ThemeStudioDocument next)
    {
        if (next == _document) return;
        if (!_gesture || !_gestureRecorded) _undo.Push(_document);
        _gestureRecorded = _gesture;
        _redo.Clear(); _document = next; _values = null;
        Sync();
        _draftTimer.Stop(); _draftTimer.Start();
    }

    private void History(Stack<ThemeStudioDocument> from, Stack<ThemeStudioDocument> to)
    {
        if (!from.TryPop(out var next)) return;
        to.Push(_document); _document = next; _values = null;
        Sync();
        _draftTimer.Stop(); _draftTimer.Start();
    }

    // One undo step for a whole drag of a slider, not one per move. The panel is not rebuilt meanwhile (that
    // would take the slider away from under the pointer); it catches up when the drag ends.
    private void BeginGesture() { _gesture = true; _gestureRecorded = false; }
    private void EndGesture() { _gesture = false; _gestureRecorded = false; SyncPanel(); }

    private void ShowDraft()
    {
        if (_finished) return;
        if (ThemeService.ShowDraft(_document) is { } problem) { Report(problem, true); return; }
        Dispatcher.BeginInvoke(new Action(Refresh), DispatcherPriority.Loaded);
    }

    private bool Dirty => _document != _initial;

    private object? Value(string key)
    {
        var own = _values ??= SafeResources();
        return own.Contains(key) ? own[key] : _fallback.Contains(key) ? _fallback[key] : null;
    }

    private ResourceDictionary SafeResources()
    {
        try { return _document.Resources(); }
        catch (Exception e) when (e is ThemeFormatException or ArgumentException) { return new ResourceDictionary(); }
    }

    private bool On(string key) => Value(key) is Visibility.Visible;

    private static Color Raw(object? value) => value switch
    {
        SolidColorBrush b => b.Color, GradientBrush { GradientStops.Count: > 0 } g => g.GradientStops[0].Color, Color c => c, _ => Colors.Transparent
    };

    // What a colour looks like on screen: see-through text and panels are laid over what is behind them.
    private Color Solid(string key)
    {
        var page = Value("Base") is SolidColorBrush or GradientBrush ? Raw(Value("Base")) : Value("Theme.BaseColor") is Color named ? named : Raw(Value("Base"));
        page = Color.FromRgb(page.R, page.G, page.B);
        if (key == "Base") return page;
        var surface = StudioUi.Over(Raw(Value("Surface")), page);
        if (key == "Surface") return surface;
        return StudioUi.Over(Raw(Value(key)), surface);
    }

    // Everything that shows the theme's state.
    private void Sync()
    {
        _undoButton.Opacity = _undo.Count > 0 ? 1 : .45;
        _redoButton.Opacity = _redo.Count > 0 ? 1 : .45;
        UpdateReadability();
        SyncCanvas();   // ThemeEditor.Canvas.cs
        SyncPanel();    // ThemeEditor.Panel.cs
    }

    // Puts the outlines where the parts are now.
    private void Refresh() => RefreshCanvas();

    // ---------------------------------------------------------------- readability

    private bool LowContrast()
    {
        var page = Solid("Base"); var surface = Solid("Surface");
        return new[] { "Ink", "InkMuted", "InkFaint" }.Any(key => Math.Min(ThemeDesign.Contrast(Solid(key), page), ThemeDesign.Contrast(Solid(key), surface)) < 4.5);
    }

    private void UpdateReadability() => _readability.Visibility = LowContrast() ? Visibility.Visible : Visibility.Collapsed;

    private void FixReadability()
    {
        var page = Solid("Base"); var surface = Solid("Surface");
        var pole = StudioUi.TextOn(surface);
        var ink = ThemeDesign.Legible(Solid("Ink"), pole, 7, page, surface);
        var muted = ThemeDesign.Legible(Solid("InkMuted"), ink, 5.6, page, surface);
        var faint = ThemeDesign.Legible(Solid("InkFaint"), ink, 4.5, page, surface);
        TryChange(() => _document.Color("Ink", StudioUi.Hex(ink)).Color("InkMuted", StudioUi.Hex(muted))
            .Color("Quiet", StudioUi.Hex(muted)).Color("InkFaint", StudioUi.Hex(faint)));
        Report("Text lifted just enough to read clearly.");
    }

    // ---------------------------------------------------------------- the bar across the top

    private Border BannerButton(string text, string tip, Action click, bool solid = false)
    {
        var words = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontWeight = solid ? FontWeights.SemiBold : FontWeights.Normal };
        var button = new Border
        {
            Height = 32, Padding = new Thickness(14, 0, 14, 0), CornerRadius = new CornerRadius(8), Margin = new Thickness(6, 0, 0, 0), Child = words,
            BorderThickness = new Thickness(solid ? 0 : 1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x5A, 0x1A, 0x19, 0x17)), Cursor = Cursors.Hand,
            Focusable = true, FocusVisualStyle = null, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center
        };
        if (solid)
        {
            button.SetResourceReference(Border.BackgroundProperty, "Base");
            words.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        }
        else
        {
            button.Background = Brushes.Transparent;
            words.SetResourceReference(TextBlock.ForegroundProperty, "AccentInk");
            button.MouseEnter += (_, _) => button.Background = new SolidColorBrush(Color.FromArgb(0x1C, 0, 0, 0));
            button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        }
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        button.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { e.Handled = true; click(); } };
        AutomationProperties.SetName(button, text);
        return button;
    }

    // CHANGED (side panel): only the name, Undo / Redo, the readability warning, Share, Cancel and Done. Mood,
    // Fonts, Layout and More moved into the panel's Theme tab as plain rows.
    private Border BuildBanner()
    {
        var row = new DockPanel { LastChildFill = false };
        var editing = new TextBlock { Text = "Editing theme", FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        editing.SetResourceReference(TextBlock.ForegroundProperty, "AccentInk");
        row.Children.Add(editing);
        _name.SetResourceReference(StyleProperty, "PlainTextBox");
        _name.FontSize = 14; _name.FontWeight = FontWeights.SemiBold;
        _name.SetResourceReference(Control.ForegroundProperty, "AccentInk");
        _name.SetResourceReference(TextBoxBase.CaretBrushProperty, "AccentInk");
        AutomationProperties.SetName(_name, "Theme name");
        _name.ToolTip = "The theme's name";
        var nameFrame = new Border { Child = _name, BorderThickness = new Thickness(0, 0, 0, 1.5), BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 0x1A, 0x19, 0x17)), Padding = new Thickness(2, 3, 2, 2), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(nameFrame);
        row.Children.Add(ModeSwitch());   // NEW (Arrange mode): Style | Arrange, ThemeEditor.Arrange.cs
        _undoButton.Margin = new Thickness(18, 0, 0, 0);
        row.Children.Add(_undoButton);
        row.Children.Add(_redoButton);
        var low = new StackPanel { Orientation = Orientation.Horizontal };
        var warn = new TextBlock { Text = "Some text is hard to read", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        warn.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        low.Children.Add(warn);
        var fix = BannerButton("Fix", "Lifts any text that reads below 4.5:1 just enough to pass", FixReadability);
        fix.Height = 22; fix.Padding = new Thickness(8, 0, 8, 0); fix.BorderThickness = new Thickness(0);
        ((TextBlock)fix.Child).SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        low.Children.Add(fix);
        _readability.Child = low;
        _readability.SetResourceReference(Border.BackgroundProperty, "Base");
        _readability.Margin = new Thickness(14, 0, 0, 0);
        row.Children.Add(_readability);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(right, Dock.Right);
        right.Children.Add(BannerButton("Share", "Save this theme as one file you can send to anyone", ExportTheme));
        right.Children.Add(BannerButton("Cancel", "Put the theme from before back, without saving", () => Cancel(ask: true)));
        right.Children.Add(BannerButton(_editing is { Shipped: false, BuiltIn: false } ? "Save" : "Done", "Save this theme and use it  ·  Ctrl+S", Save, solid: true));
        row.Children.Add(right);

        var banner = new Border { Height = 48, Padding = new Thickness(18, 0, 14, 0), Child = row };
        banner.Resources.MergedDictionaries.Add(Chrome());
        banner.SetResourceReference(Border.BackgroundProperty, "Accent");
        banner.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "UIFont");
        System.Windows.Documents.TextElement.SetFontSize(banner, 13);
        return banner;
    }

    // ---------------------------------------------------------------- menus

    private static ContextMenu Menu(FrameworkElement target) => new() { PlacementTarget = target, Placement = PlacementMode.Bottom };

    private static MenuItem Item(ItemsControl menu, string header, Action act, bool check = false, string? tip = null)
    {
        var item = new MenuItem { Header = header, IsCheckable = check, IsChecked = check, ToolTip = tip };
        item.Click += (_, _) => act();
        menu.Items.Add(item);
        return item;
    }

    // ---------------------------------------------------------------- moods and starting points

    private void ApplyMood(string name, ThemeDesign design)
    {
        Change(ThemeStudioDocument.FromDesign(design) with { Manifest = _document.Manifest });
        Report($"{name} mood. Undo brings your colors back.");
    }

    private void SurpriseMe()
    {
        var p = ThemeDesign.Presets[Random.Shared.Next(ThemeDesign.Presets.Length)].Design;
        TryChange(() => Surprise(p with { Accent = StudioUi.SuggestedColors[Random.Shared.Next(7)], Roundness = Random.Shared.Next(17) }));
    }

    // A mood, and now and then a pattern on the window, a ready layout or a shade, all in one undo step.
    private ThemeStudioDocument Surprise(ThemeDesign design)
    {
        ThemeDesign.TryColor(design.Background, out var background);
        ThemeDesign.TryColor(design.Accent, out var accent);
        var ink = StudioUi.TextOn(background);
        var next = ThemeStudioDocument.FromDesign(design) with { Manifest = _document.Manifest };
        var dice = Random.Shared;
        if (dice.NextDouble() < .5)
        {
            var patterns = Enum.GetValues<StudioPattern>();
            next = next.Paint("Base", StudioPatterns.Pattern(patterns[dice.Next(patterns.Length)], ink, accent, background, .8 + dice.NextDouble() * .8, .08 + dice.NextDouble() * .12))
                .Color("Theme.BaseColor", StudioUi.Hex(background, opaque: true));
        }
        if (dice.NextDouble() < .35) next = WithLayout(next, LayoutPresets[dice.Next(LayoutPresets.Length)].Layout);
        if (dice.NextDouble() < .35 && StudioPatterns.Shade((StudioShade)(1 + dice.Next(4)), accent, .5) is { } shade) next = next.Paint("EdgeShade", shade);
        return next;
    }

    private void StartFromMenu(FrameworkElement target)
    {
        var menu = Menu(target);
        foreach (var theme in ThemeService.Themes.Where(t => t.Problem is null && !t.BuiltIn).OrderBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase))
            Item(menu, theme.Label, () =>
            {
                try { Change(ThemeStudioDocument.Open(theme) with { Manifest = _document.Manifest with { Icons = theme.Icons } }); Report($"Started from {theme.Label}. Saving makes a theme of your own; {theme.Label} stays as it is."); }
                catch (Exception error) when (error is ThemeFormatException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                { Report($"{theme.Label} could not be opened: {error.Message}", true); }
            });
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No other themes yet", IsEnabled = false });
        menu.IsOpen = true;
    }

    private void FromPictureFile()
    {
        if (ChoosePicture("Choose a picture to build a theme from") is { } path) FromPicture(path, "your picture");
    }

    private void FromWallpaper()
    {
        if (StudioPicture.WallpaperPath() is not { } path) { Report("Windows did not say which picture is your wallpaper (it may be a solid color).", true); return; }
        FromPicture(path, "your wallpaper");
    }

    private string? ChoosePicture(string title)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp", CheckFileExists = true };
        return picker.ShowDialog(_owner) == true ? picker.FileName : null;
    }

    // The picture's colours become the background and the accent; the picture itself goes behind the panels,
    // which become slightly see-through so it shows round the edges and faintly behind the files.
    private void FromPicture(string path, string what)
    {
        try
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) { Report("Choose a picture smaller than 16 MB.", true); return; }
            var bytes = File.ReadAllBytes(path);
            var palette = StudioPicture.Read(StudioPicture.Load(bytes));
            var next = ThemeStudioDocument.FromDesign(new ThemeDesign(StudioPicture.Hex(palette.Background), StudioPicture.Hex(palette.Accent))) with { Manifest = _document.Manifest };
            var surface = ((SolidColorBrush)next.Resources()["Surface"]).Color;
            next = next.Sticker("Decor.Back1", bytes, new Rect(0, 0, 1, 1), palette.Dark ? .5 : .4, cover: true)
                .Color("Part.Files.Fill", StudioUi.Hex(Color.FromArgb(0xEB, surface.R, surface.G, surface.B)))
                .Color("Part.Sidebar.Fill", StudioUi.Hex(Color.FromArgb(0xA6, palette.Background.R, palette.Background.G, palette.Background.B)))
                .Color("Part.Toolbar.Fill", StudioUi.Hex(Color.FromArgb(0xA6, palette.Background.R, palette.Background.G, palette.Background.B)));
            Change(next);
            Report($"Built from {what}. The picture is behind the panels.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException or ThemeFormatException or InvalidOperationException)
        { Report("That picture could not be used: " + error.Message, true); }
    }

    // ---------------------------------------------------------------- fonts

    // A menu of fonts for one kind of text ("All" for every kind).
    private void FontsMenu(FrameworkElement target, string key, Action? done = null, bool atPointer = false)
    {
        var menu = Menu(target);
        if (atPointer) menu.Placement = PlacementMode.MousePoint;
        if (done is not null) menu.Closed += (_, _) => done();
        FillFonts(menu, key);
        menu.Items.Add(new Separator());
        Item(menu, "Add a font file…", ImportFont, tip: "Adds a TTF or OTF font to your library, so every font list offers it");
        menu.IsOpen = true;
    }

    private void FillFonts(ItemsControl into, string key)
    {
        var now = key == "All" ? "" : FontLabel(key);
        foreach (var font in StudioLibrary.Fonts().OrderBy(f => f.Name))
            Item(into, font.Name + " · Your library", () =>
            {
                try { TryChange(() => Choose(_document.ImportFont(File.ReadAllBytes(font.Path), Path.GetExtension(font.Path), key == "All" ? "UIFont" : key), key, "fonts/#" + font.Name)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Report("Could not use that font: " + e.Message, true); }
            }).FontFamily = font.Family;
        foreach (var family in Fonts.SystemFontFamilies.OrderBy(f => f.Source))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(family.Source, "^[A-Za-z0-9][A-Za-z0-9 ._\\-]{0,79}$")) continue;
            Item(into, family.Source, () => TryChange(() => Choose(_document, key, family.Source)), check: family.Source == now).FontFamily = family;
        }
    }

    private static ThemeStudioDocument Choose(ThemeStudioDocument document, string key, string family) => key == "All" ? document.AllFonts(family) : document.Font(key, family);

    private string FontLabel(string key)
    {
        var source = (Value(key) as FontFamily)?.Source ?? "";
        if (source.Contains('#')) return source[(source.LastIndexOf('#') + 1)..];
        return source.Split(',')[0].Trim();
    }

    private void ImportFont()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Add a font to your Clearspace library", Filter = "Font files|*.ttf;*.otf" };
        if (picker.ShowDialog(_owner) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 16 * 1024 * 1024) throw new ArgumentException("Choose a font smaller than 16 MB.");
            var name = StudioLibrary.ImportFont(File.ReadAllBytes(picker.FileName), Path.GetExtension(picker.FileName));
            Report(name + " added. It is in every font list now.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException or ThemeFormatException)
        { Report("Could not add that font: " + error.Message, true); }
    }

    // ---------------------------------------------------------------- layout

    private static readonly (string Name, string Tip, StudioLayout Layout)[] LayoutPresets =
    [
        ("Classic", "Sidebar left, toolbar on top", new(true, false, false, true, false, true, false, false, false)),
        ("Mirrored", "Sidebar right, preview pane left", new(true, true, false, true, false, true, false, true, false)),
        ("Focus", "Just the files: no sidebar, tabs or status bar", new(false, false, false, false, false, false, false, false, false)),
        ("Commander", "Status on top and a bar of shortcut keys along the bottom", new(true, false, false, true, false, true, true, false, true)),
        ("Bottom bar", "Toolbar and tabs along the bottom, like a dock", new(true, false, true, true, true, true, true, false, false))
    ];

    private static ThemeStudioDocument WithLayout(ThemeStudioDocument d, StudioLayout l)
    {
        static double B(bool on) => on ? 1 : 0;
        return d.Layout("Explorer.SidebarVisibility", B(l.Sidebar)).Layout("Explorer.SidebarOnRight", B(l.SidebarRight))
            .Layout("Explorer.ToolbarBottom", B(l.ToolbarBottom)).Layout("Explorer.TabsVisibility", B(l.Tabs))
            .Layout("Explorer.TabsBelowToolbar", B(l.TabsBelow)).Layout("Explorer.StatusBarVisibility", B(l.Status))
            .Layout("Explorer.StatusBarTop", B(l.StatusTop)).Layout("Explorer.PreviewOnLeft", B(l.PreviewLeft))
            .Layout("FunctionKeysVisibility", B(l.Keys));
    }

    private void OpenLayoutBuilder()
    {
        var builder = new StudioLayoutBuilderWindow(_owner, _document);
        var window = new Window
        {
            Title = "Layout builder", Content = builder, Owner = _owner, Width = Math.Min(1360, SystemParameters.WorkArea.Width - 40),
            Height = Math.Min(880, SystemParameters.WorkArea.Height - 40), WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        window.SetResourceReference(Control.BackgroundProperty, "Base");
        builder.Completed += (_, accepted) =>
        {
            if (accepted)
            {
                var result = builder.Result;
                TryChange(() => (builder.Document with { LayoutJson = result }).DropUnusedImages());
                Report(result is null ? "Back to the usual layout." : "Custom layout in use.");
            }
            window.Close();
        };
        window.Closed += (_, _) => builder.Dispose();
        window.ShowDialog();
    }

    // ---------------------------------------------------------------- effects

    private void OverlayMenu(FrameworkElement target) { var menu = Menu(target); FillOverlay(menu); menu.IsOpen = true; }

    // CHANGED (cards first): the effect menus fill any menu, so Add a card › Effect can offer them as submenus.
    private void FillOverlay(ItemsControl menu)
    {
        var dark = StudioUi.TextOn(Solid("Base")) == Colors.White;
        foreach (var kind in Enum.GetValues<StudioOverlay>())
            Item(menu, StudioPatterns.Label(kind), () => TryChange(() => StudioPatterns.Overlay(kind, dark, .5) is { } fill
                ? _document.Paint("OverlayFill", fill).Layout("OverlayVisibility", 1) : _document.Without("OverlayFill").Layout("OverlayVisibility", 0)));
    }

    private void ShadeMenu(FrameworkElement target) { var menu = Menu(target); FillShade(menu); menu.IsOpen = true; }

    private void FillShade(ItemsControl menu)
    {
        var accent = Solid("Accent");
        foreach (var kind in Enum.GetValues<StudioShade>())
            Item(menu, StudioPatterns.Label(kind), () => TryChange(() => StudioPatterns.Shade(kind, accent, .5) is { } shade ? _document.Paint("EdgeShade", shade) : _document.Without("EdgeShade")));
    }

    private void EdgeMenu(FrameworkElement target) { var menu = Menu(target); FillEdge(menu); menu.IsOpen = true; }

    private void FillEdge(ItemsControl menu)
    {
        var dark = StudioUi.TextOn(Solid("Base")) == Colors.White;
        var accent = Solid("Accent");
        Item(menu, "Nothing", () => TryChange(() => _document.Without("BindingFill", "PageInset").Layout("BindingVisibility", 0)));
        Item(menu, "Notebook spiral", () => TryChange(() => _document.Paint("BindingFill", StudioPatterns.Binding(dark)).Layout("BindingVisibility", 1).Spacing("PageInset", 30, 0, 0, 0)));
        Item(menu, "Warning tape", () => TryChange(() => _document.Paint("BindingFill", StudioPatterns.Tape(accent, Color.FromRgb(12, 12, 12))).Layout("BindingVisibility", 1).Spacing("PageInset", 10, 0, 0, 0)));
    }

    private void IconsMenu(FrameworkElement target) { var menu = Menu(target); FillIcons(menu); menu.IsOpen = true; }

    private void FillIcons(ItemsControl menu)
    {
        foreach (var style in Enum.GetValues<IconStyle>())
            Item(menu, ThemeCards.IconName(style), () => Change(_document with { Manifest = _document.Manifest with { Icons = style } }), check: _document.Manifest.Icons == style);
    }

    private void OpenSettings()
    {
        var window = new ThemeSettingsWindow(_owner, this);
        window.ShowDialog();
        SyncPanel();
    }

    // ---------------------------------------------------------------- pictures

    private void ChooseBackdrop()
    {
        if (ChoosePicture("Choose a picture") is { } path) UseBackdrop(path);
    }

    private void UseBackdrop(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) { Report("Choose a picture smaller than 16 MB.", true); return; }
            var editor = new StudioImageWindow(_owner, File.ReadAllBytes(path), fillsWindow: true, WindowAspect());
            if (editor.ShowDialog() != true || editor.Result is not { } edited) return;
            Change(_document.Backdrop(edited, .2));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException)
        { Report("That picture could not be used: " + error.Message, true); }
    }

    // NEW (side panel): replaces the Stamp brush. The picture goes in the lower right; drag it on the window to
    // move it, or its corner to resize it.
    private void AddSticker()
    {
        var stickers = _document.Stickers();
        var free = ThemeStudioDocument.FrontStickers.Concat(ThemeStudioDocument.BackStickers).FirstOrDefault(k => stickers.All(s => s.Key != k));
        if (free is null) { Report("A theme holds up to eight stickers. Remove one to add another.", true); return; }
        if (ChoosePicture("Choose a picture for a sticker") is not { } path) return;
        try
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) { Report("Choose a picture smaller than 16 MB.", true); return; }
            var editor = new StudioImageWindow(_owner, File.ReadAllBytes(path), fillsWindow: false, WindowAspect());
            if (editor.ShowDialog() != true || editor.Result is not { } edited) return;
            TryChange(() => _document.Sticker(free, edited, new Rect(.62, .52, .3, .4), 1));
            Select(null, free);
            Report("Sticker added. Drag it on the window to move it; drag its corner to resize it.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException or InvalidOperationException)
        { Report("That picture could not be used: " + error.Message, true); }
    }

    private double WindowAspect() => ActualWidth > 100 && ActualHeight > 100 ? ActualWidth / ActualHeight : 1264d / 760;

    // ---------------------------------------------------------------- keys

    /// <summary>The window hands every key to the editor while it is open.</summary>
    internal void OnKey(KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox && e.Key != Key.Escape && !(e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)) return;
        var control = Keyboard.Modifiers == ModifierKeys.Control || Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (control && e.Key == Key.S) { e.Handled = true; Save(); return; }
        if (control && e.Key == Key.Z && !shift) { e.Handled = true; History(_undo, _redo); return; }
        if (control && (e.Key == Key.Y || (e.Key == Key.Z && shift))) { e.Handled = true; History(_redo, _undo); return; }
        if (e.Key == Key.Escape) { e.Handled = true; Escape(); return; }
        if (e.Key == Key.Delete) { e.Handled = true; DeleteSelected(); return; }
        if (e.Key == Key.System) return; // Alt+F4 and the like still work
        // Tab, arrows, Enter and Space still work in the panel; the explorer's own shortcuts are off.
        if ((SidePanel.IsKeyboardFocusWithin || Hand.IsKeyboardFocusWithin) && e.Key is Key.Tab or Key.Enter or Key.Space or Key.Up or Key.Down or Key.Left or Key.Right) return;
        e.Handled = true;
    }

    // Esc lets go of things one at a time: a drag, the picked card, the picked part.
    private void Escape()
    {
        if (CancelPress()) return;                                                   // ThemeEditor.Canvas.cs
        if (_iconForm) { _iconForm = false; SyncPanel(); return; }                     // NEW (cards first): ThemeEditor.Make.cs
        if (_pickedCard is not null) { _pickedCard = null; PaintHand(); SyncPanel(); return; }     // ThemeEditor.Cards.cs
        if (_selected is not null || _sticker is not null) { Select(null); return; }
        Report("Done saves your theme; Cancel puts the one from before back.");
    }

    // ---------------------------------------------------------------- saving, sharing, cancelling

    private void Save()
    {
        var name = ThemeManifest.Tidy(_name.Text, 60);
        if (name.Length == 0) { Report("Give your theme a name first (at the top).", true); _name.Focus(); return; }
        _draftTimer.Stop();
        try
        {
            var replace = _editing is { Shipped: false, BuiltIn: false } ? _editing : null;
            var id = _document.Save(ThemeService.Folder, name, replace);
            Finish();
            ThemeService.EndDraft(restore: false);
            ThemeService.Refresh();
            if (!ThemeService.ApplyEdited(id))
            {
                Finished?.Invoke(this, null);
                MessageDialog.Show(_owner, "Saved, but it could not be put on screen: " + ThemeService.LastProblem, "Theme saved", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Finished?.Invoke(this, name);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ThemeFormatException or ArgumentException)
        { Report("Could not save: " + error.Message, true); }
    }

    /// <summary>Cancel: asks first when something changed. Returns whether editing ended.</summary>
    internal bool Cancel(bool ask = true)
    {
        if (_finished) return true;
        if (ask && Dirty && MessageDialog.Show(_owner, "Your changes to this theme have not been saved.", "Stop editing without saving?",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return false;
        Finish();
        ThemeService.EndDraft(restore: true);
        Finished?.Invoke(this, null);
        return true;
    }

    private void Finish()
    {
        _finished = true;
        _draftTimer.Stop(); _colorTimer.Stop();
    }

    // Writes the theme as a theme file (a zip) wherever you choose, without adding it to your themes.
    private void ExportTheme()
    {
        var name = ThemeManifest.Tidy(_name.Text, 60);
        if (name.Length == 0) { Report("Give your theme a name first (at the top).", true); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Share theme file", FileName = name + ".zip", Filter = "Clearspace theme|*.zip", AddExtension = true };
        if (dialog.ShowDialog(_owner) != true) return;
        var temp = Path.Combine(Path.GetTempPath(), "Clearspace-theme-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = _document.Save(temp, name);
            File.Copy(Path.Combine(temp, id + ".zip"), dialog.FileName, overwrite: true);
            Report($"Saved {Path.GetFileName(dialog.FileName)}. Anyone can add it from their Themes page.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ThemeFormatException or ArgumentException)
        { Report("Could not save the theme file: " + error.Message, true); }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
