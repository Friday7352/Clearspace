using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Clearspace.Services;

namespace Clearspace;

// NEW (side panel): the one panel beside the window while a theme is being edited. It sits in its own column
// (MainWindow.ThemeEdit.cs), so it never covers the explorer, and its rows stay where they are: nothing in it
// floats or follows the pointer.
// CHANGED (cards first): two tabs, and the cards are where things are changed:
//   Cards  (first) what you are changing, in a box at the top: the card you clicked in your hand, or the part
//          you clicked on the window (with the cards its look is on lifted in the hand), or a sticker. Then Add
//          a card (a pattern, a gradient, a font, a layout, an effect, a sticker, your own icon…) and Borrow
//          cards (from other themes, My cards, a code or a deck file). ThemeEditor.Cards.cs.
//   Theme  where to start from (moods, a picture, your wallpaper, another theme, a deck code), sharing (deck
//          code, deck file, theme file) and Every setting.
// (The Part tab is gone: a part's settings are the box at the top of the Cards tab.)
// Messages ("Text lifted just enough to read clearly.") show along the bottom of the panel.
internal sealed partial class ThemeEditor
{
    private const double PanelWidth = 344;
    private string _tab = "Cards";   // CHANGED (cards first): the Cards tab first
    private readonly StackPanel _body = new() { Margin = new Thickness(18, 4, 18, 28) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
    private readonly Border _statusBar = new() { Padding = new Thickness(18, 10, 18, 12), BorderThickness = new Thickness(0, 1, 0, 0), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _statusTimer = new();
    private readonly List<(string Id, Border Chip, TextBlock Text)> _tabChips = new();
    private readonly UniformGrid _tabsRow = new() { Rows = 1, Margin = new Thickness(14, 14, 14, 8) };   // hidden in Arrange mode
    private bool _keepPanel;   // a slider or tick in the panel made the change: it already shows it, so keep it (and its focus)

    // Runs a change made by one of the panel's own sliders or ticks without rebuilding the panel under it.
    private void FromPanel(Action change)
    {
        _keepPanel = true;
        try { change(); }
        finally { _keepPanel = false; }
    }

    private Border BuildPanel()
    {
        var dock = new DockPanel();
        var tabs = _tabsRow;
        foreach (var (id, label, tip) in new[]
                 {
                     ("Cards", "Cards", "What you are changing, adding a card, and borrowing cards"),
                     ("Theme", "Theme", "Where to start from, sharing, and every setting")
                 })
        {
            var text = new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
            var chip = new Border { Height = 34, CornerRadius = new CornerRadius(8), Margin = new Thickness(2, 0, 2, 0), Child = text, Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null, ToolTip = tip };
            chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowTab(id); };
            chip.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { e.Handled = true; ShowTab(id); } };
            AutomationProperties.SetName(chip, label);
            _tabChips.Add((id, chip, text));
            tabs.Children.Add(chip);
        }
        DockPanel.SetDock(tabs, Dock.Top);
        dock.Children.Add(tabs);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        _statusBar.Child = _status;
        _statusBar.SetResourceReference(Border.BorderBrushProperty, "Line");
        _statusBar.SetResourceReference(Border.BackgroundProperty, "Base");
        DockPanel.SetDock(_statusBar, Dock.Bottom);
        dock.Children.Add(_statusBar);
        _scroll.Content = _body;
        dock.Children.Add(_scroll);
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); _statusBar.Visibility = Visibility.Collapsed; };

        var panel = new Border { Width = PanelWidth, Child = dock, BorderThickness = new Thickness(1, 0, 0, 0) };
        panel.Resources.MergedDictionaries.Add(Chrome());
        panel.SetResourceReference(Border.BackgroundProperty, "Surface");
        panel.SetResourceReference(Border.BorderBrushProperty, "Line");
        panel.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "UIFont");
        panel.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Ink");
        System.Windows.Documents.TextElement.SetFontSize(panel, 13);
        PaintTabs();
        return panel;
    }

    /// <summary>Shows one of the panel's tabs: "Cards" or "Theme" ("Part", the old tab, is the Cards tab).</summary>
    internal void ShowTab(string id)
    {
        if (id != "Theme") id = "Cards";
        var changed = id != _tab;
        _tab = id;
        PaintTabs();
        SyncPanel();
        if (changed) _scroll.ScrollToTop();
    }

    private void PaintTabs()
    {
        foreach (var (id, chip, text) in _tabChips)
        {
            if (id == _tab) { chip.SetResourceReference(Border.BackgroundProperty, "Accent"); text.SetResourceReference(TextBlock.ForegroundProperty, "AccentInk"); }
            else { chip.SetResourceReference(Border.BackgroundProperty, "Tint"); text.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); }
        }
    }

    // Rebuilds the open tab (only that one) from the theme as it stands, keeping the scroll position.
    private void SyncPanel()
    {
        if (_gesture || _finished || _keepPanel) return;
        var offset = _scroll.VerticalOffset;
        _body.Children.Clear();
        if (_arranging) BuildArrangePanel();   // NEW (Arrange mode): ThemeEditor.Arrange.cs
        else if (_tab == "Theme") BuildThemeTab();
        else BuildCardsTab();   // ThemeEditor.Cards.cs
        _scroll.ScrollToVerticalOffset(offset);
    }

    private void Report(string text, bool error = false)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "Ink");
        _statusBar.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Interval = TimeSpan.FromSeconds(error ? 12 : 7);
        _statusTimer.Start();
    }

    // ---------------------------------------------------------------- building blocks

    private void Title(string text, string? note = null)
    {
        _body.Children.Add(new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis });
        if (note is not null) _body.Children.Add(Note(note));
    }

    private void Heading(string text)
    {
        var heading = new TextBlock { Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 6) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        _body.Children.Add(heading);
    }

    private static TextBlock Note(string text)
    {
        var note = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        return note;
    }

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 10, 0, 6) };

    // A whole row that is one button: words on the left, what it is now on the right. The row is passed to the
    // click so a menu can open under it.
    private static Border Row(string label, UIElement? value, Action<FrameworkElement> click, string? tip = null)
    {
        var dock = new DockPanel { LastChildFill = false };
        if (value is not null) { DockPanel.SetDock(value, Dock.Right); dock.Children.Add(value); }
        dock.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        Border? row = null;
        row = StudioUi.ClickRow(dock, label, () => click(row!));
        row.Margin = new Thickness(-8, 0, -8, 0);
        row.MinHeight = 38;
        row.ToolTip = tip;
        return row;
    }

    private static TextBlock ValueText(string text, FontFamily? font = null)
    {
        var value = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis };
        value.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        if (font is not null) value.FontFamily = font;
        return value;
    }

    private FrameworkElement Swatch(string key)
    {
        var (outer, inner) = StudioUi.Swatch(24);
        inner.Background = Value(key) switch { Brush b => b, Color c => new SolidColorBrush(c), _ => Brushes.Transparent };
        var code = new TextBlock { Text = Value(key) is SolidColorBrush or Color ? Hex(Raw(Value(key))) : PatternName(key), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        code.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        code.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        var both = new StackPanel { Orientation = Orientation.Horizontal };
        both.Children.Add(code);
        both.Children.Add(outer);
        return both;
    }

    private void ColorRow(string label, string key, string pickerTitle) =>
        _body.Children.Add(Row(label, Swatch(key), _ => PickColor(key, pickerTitle), "Change the " + label.ToLowerInvariant()));

    private static Border Pill(string text, Action click, bool on = false, string? tip = null, bool danger = false)
    {
        var words = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var pill = new Border { MinHeight = 32, Padding = new Thickness(12, 5, 12, 6), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 6, 6), Child = words, Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null, ToolTip = tip, BorderThickness = new Thickness(1) };
        void Rest()
        {
            if (on) { pill.SetResourceReference(Border.BackgroundProperty, "Accent"); pill.SetResourceReference(Border.BorderBrushProperty, "Accent"); words.SetResourceReference(TextBlock.ForegroundProperty, "AccentInk"); }
            else { pill.SetResourceReference(Border.BackgroundProperty, "Tint"); pill.SetResourceReference(Border.BorderBrushProperty, "LineFaint"); words.SetResourceReference(TextBlock.ForegroundProperty, danger ? "Danger" : "Ink"); }
        }
        Rest();
        pill.MouseEnter += (_, _) => { if (!on) pill.SetResourceReference(Border.BackgroundProperty, "Hover"); };
        pill.MouseLeave += (_, _) => Rest();
        pill.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        pill.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { e.Handled = true; click(); } };
        AutomationProperties.SetName(pill, text);
        return pill;
    }

    private void Pills(params Border[] pills)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var pill in pills) wrap.Children.Add(pill);
        _body.Children.Add(wrap);
    }

    // A row of choices, one of them lit.
    private void Choices(IEnumerable<(string Label, bool On, Action Pick)> choices)
    {
        var wrap = new WrapPanel();
        foreach (var (label, on, pick) in choices) wrap.Children.Add(Pill(label, pick, on));
        _body.Children.Add(wrap);
    }

    private void Check(string label, bool on, Action<bool> set, string? tip = null)
    {
        var check = FileBackupUi.Tick(label, tip ?? label);
        check.IsChecked = on;
        check.Margin = new Thickness(0, 6, 0, 6);
        check.Checked += (_, _) => FromPanel(() => set(true));
        check.Unchecked += (_, _) => FromPanel(() => set(false));
        _body.Children.Add(check);
    }

    // A slider whose whole drag is one undo step.
    // NEW (cards first): what build() adds to the panel, put in a framed box (the box at the top of the Cards tab).
    private void Framed(Action build, Brush? edge = null)
    {
        var start = _body.Children.Count;
        build();
        var items = _body.Children.Cast<UIElement>().Skip(start).ToList();
        foreach (var item in items) _body.Children.Remove(item);
        var stack = new StackPanel();
        foreach (var item in items) stack.Children.Add(item);
        var frame = new Border { Child = stack, Padding = new Thickness(14, 6, 14, 10), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 10, 0, 4) };
        frame.SetResourceReference(Border.BackgroundProperty, "Tint");
        if (edge is not null) frame.BorderBrush = edge; else frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        _body.Children.Add(frame);
    }

    private void PercentSlider(string label, double value, double min, double max, string unit, Action<double> change)
    {
        var top = new DockPanel { Margin = new Thickness(0, 10, 0, 2) };
        var shown = new TextBlock { Text = $"{value:0}{unit}" };
        shown.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        DockPanel.SetDock(shown, Dock.Right);
        top.Children.Add(shown);
        top.Children.Add(new TextBlock { Text = label });
        _body.Children.Add(top);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, IsMoveToPointEnabled = true, SmallChange = 1, LargeChange = 10 };
        slider.SetResourceReference(StyleProperty, "MediaSlider");
        AutomationProperties.SetName(slider, label);
        slider.PreviewMouseLeftButtonDown += (_, _) => BeginGesture();
        slider.PreviewMouseLeftButtonUp += (_, _) => EndGesture();
        slider.LostMouseCapture += (_, _) => { if (_gesture) EndGesture(); };
        slider.ValueChanged += (_, e) => { shown.Text = $"{e.NewValue:0}{unit}"; FromPanel(() => change(e.NewValue)); };
        _body.Children.Add(slider);
    }

    // ---------------------------------------------------------------- a part, a sticker, the parts (CHANGED, cards first)

    private static readonly string[] PartOrder = ["Window", "Toolbar", "Tabs", "Sidebar", "Files", "Preview", "Status", "Keys"];

    // Every theme key a part's look is made of: what it is filled, outlined and rounded with, its pattern, whether
    // it shows, and where it sits. The cards holding any of these are the part's cards.
    private IReadOnlySet<string> PartKeys(CanvasPart part)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in new[] { part.Fill, part.Edge, part.EdgeWidth, part.Corner, part.Hide, part.PatternTarget }) if (key is not null) keys.Add(key);
        foreach (var place in part.Places) if (PlaceSetting(part.Id, place) is { } setting) keys.Add(setting.Key);
        if (part.Id == "Window") { keys.Add("Theme.BaseColor"); keys.Add("Accent"); keys.Add("Ink"); }
        return keys;
    }

    // The part clicked on the window: the cards its look is on (click one to see the whole card), then its rows.
    private void PartBox(CanvasPart part, IReadOnlyList<ThemeCard> deck)
    {
        Title(part.Label, part.Id == "Window" ? "The background behind every panel." : "Drag it on the window to move it.");
        var keys = PartKeys(part);
        var holding = deck.Where(c => c.Keys.Any(keys.Contains)).ToList();
        if (holding.Count > 0)
        {
            _body.Children.Add(Note(holding.Count == 1 ? "Its look is on this card (lifted in your hand):" : "Its look is on these cards (lifted in your hand):"));
            Pills(holding.Select(card => Pill(Kinds[card.Kind].Word + " · " + card.Name, () => PickCard(card, toggle: false), tip: "Show the whole card")).ToArray());
        }
        else _body.Children.Add(Note("It uses Clearspace's own look. Change anything below and it goes on a card."));
        var custom = _document.LayoutJson is not null;

        Heading("Color");
        if (part.Fill is { } fill) ColorRow(part.Id == "Window" ? "Background" : "Fill", fill, part.Label + (part.Id == "Window" ? " background" : " fill"));
        if (part.Id == "Window")
        {
            ColorRow("Accent", "Accent", "Accent");
            ColorRow("Text", "Ink", "Text");
        }
        if (part.Edge is { } edge) ColorRow("Outline", edge, part.Label + " outline");
        if (part.EdgeWidth is { } width)
        {
            var now = Value(width) is Thickness t ? Math.Max(Math.Max(t.Left, t.Right), Math.Max(t.Top, t.Bottom)) : 0;
            _body.Children.Add(Label("Outline width"));
            Choices(new (string, double)[] { ("None", 0), ("Thin", 1), ("Medium", 2), ("Thick", 4) }
                .Select(c => (c.Item1, Math.Abs(now - c.Item2) < .5, (Action)(() => TryChange(() => _document.Spacing(width, c.Item2, c.Item2, c.Item2, c.Item2))))));
        }
        if (part.Fill is { } partFill && partFill.StartsWith("Part.", StringComparison.Ordinal) && Value(partFill) is SolidColorBrush)
        {
            var color = ((SolidColorBrush)Value(partFill)!).Color;
            var clear = Math.Round((1 - color.A / 255d) * 100);
            PercentSlider("See-through", clear, 0, 90, "%", percent =>
            {
                var now = Value(partFill) is SolidColorBrush current ? current.Color : color;
                TryChange(() => _document.Color(partFill, StudioUi.Hex(Color.FromArgb((byte)Math.Round(255 * (1 - percent / 100d)), now.R, now.G, now.B))));
            });
        }

        if (part.PatternTarget is { } target)
        {
            Heading("Pattern or gradient");
            _body.Children.Add(Row("Pattern", ValueText(PatternName(target)), row => PatternMenu(row, target), "Dots, grid, stripes, waves… or draw your own"));
            GradientRows(target);   // ThemeEditor.Make.cs
        }
        if (part.Corner is { } corner)
        {
            var now = Value(corner) is CornerRadius cr ? cr.TopLeft : 0;
            Heading("Corners");
            Choices(new (string, double)[] { ("Square", 0), ("Slight", 4), ("Round", 10), ("Very round", 18) }
                .Select(c => (c.Item1, Math.Abs(now - c.Item2) < .5, (Action)(() => TryChange(() => _document.Corner(corner, c.Item2))))));
        }

        // CHANGED (Arrange mode): where a part sits, and hiding it, are in Arrange mode now.
        if ((part.Places.Length > 0 || part.Hide is not null) && part.Id != "Window" && !custom)
        {
            _body.Children.Add(Note("To move or hide it, switch to Arrange on the bar at the top."));
            Pills(Pill("Arrange", () => SetMode(true)));
        }

        Heading("More");
        var more = new List<Border> { Pill("Every color in it…", () => PartColors(part), tip: "Every theme color this part is drawn with") };
        if (part.Id != "Window") more.Add(Pill("Keep as a card", () => MakeCard(part), tip: "Keep this part's look in My cards, to use in any theme"));
        more.Add(Pill("Reset", () => ResetPart(part), tip: "Back to Clearspace's own look", danger: true));
        more.Add(Pill("Done", () => Select(null)));
        Pills(more.ToArray());
    }

    // The parts to pick from (hidden ones too, to show again), and the stickers.
    private void PartList()
    {
        Heading("Parts of the window");
        foreach (var id in PartOrder)
        {
            var part = PartById(id);
            var hidden = part.Hide is { } hide && !On(hide);
            UIElement value = hidden ? ValueText("Hidden · show") : Dot(Value(part.Fill ?? "Base") switch { Brush b => b, _ => Brushes.Transparent }, 18);
            _body.Children.Add(Row(part.Label, value, _ => { if (hidden) ShowPart(id); else Select(id); }, hidden ? "Show it again" : null));
        }
        var number = 0;
        foreach (var sticker in _document.Stickers().Where(s => !s.Cover))
        {
            number++;
            var key = sticker.Key;
            _body.Children.Add(Row("Sticker " + number, ValueText(sticker.Front ? "In front" : "Behind"), _ => Select(null, key)));
        }
    }

    private void StickerBox(StudioSticker sticker)
    {
        var number = Array.IndexOf(ThemeStudioDocument.FrontStickers.Concat(ThemeStudioDocument.BackStickers).ToArray(), sticker.Key) + 1;
        Title("Sticker " + number, "Drag it on the window to move it; drag its corner to resize it.");
        _body.Children.Add(Label("Where"));
        Choices(new (string, bool, Action)[]
        {
            ("In front", sticker.Front, () => { if (!sticker.Front) Relayer(sticker); }),
            ("Behind the panels", !sticker.Front, () => { if (sticker.Front) Relayer(sticker); })
        });
        var key = sticker.Key;
        PercentSlider("How solid", Math.Round(sticker.Opacity * 100), 10, 100, "%", percent =>
        {
            if (CurrentSticker() is { } now) TryChange(() => _document.PlaceSticker(key, now.Box, percent / 100d));
        });
        Pills(Pill("Edit the picture…", () => EditSticker(sticker), tip: "Remove its background or crop it"),
              Pill("Remove", () => RemoveSticker(sticker), danger: true),
              Pill("Done", () => Select(null)));
    }

    // ---------------------------------------------------------------- the Theme tab (CHANGED, cards first)

    private void BuildThemeTab()
    {
        Title("Theme", "Where to start from, and sharing. Colors, fonts, layout and the rest are on your cards.");

        Heading("Start from");
        var moods = new WrapPanel();
        foreach (var (name, design) in ThemeDesign.Presets)
        {
            ThemeDesign.TryColor(design.Background, out var background);
            ThemeDesign.TryColor(design.Accent, out var accent);
            var picture = new Grid { Height = 40 };
            picture.Children.Add(new Border { Background = new SolidColorBrush(background), CornerRadius = new CornerRadius(6) });
            picture.Children.Add(new Border { Background = new SolidColorBrush(accent), Width = 26, Height = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 8) });
            var stack = new StackPanel();
            stack.Children.Add(picture);
            stack.Children.Add(new TextBlock { Text = name, FontSize = 11.5, Margin = new Thickness(2, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            var mood = name; var made = design;
            var tile = StudioUi.ClickRow(stack, name + " mood", () => ApplyMood(mood, made));
            tile.Width = 96; tile.Padding = new Thickness(5); tile.Margin = new Thickness(0, 0, 4, 4);
            moods.Children.Add(tile);
        }
        _body.Children.Add(moods);
        Border? start = null;
        start = Pill("Another theme…", () => StartFromMenu(start!));
        Pills(Pill("Surprise me", SurpriseMe), Pill("A picture…", FromPictureFile), Pill("My wallpaper", FromWallpaper), start,
              Pill("A deck code", PasteCode, tip: "Start from a deck someone sent you as a code"),
              Pill("A deck file…", () => OpenDeckFile(useWhole: true), tip: "Start from a deck file (.csdeck) someone sent you"));   // NEW (deck files)

        Heading("Share");
        _body.Children.Add(Row("Copy deck code", null, _ => CopyThemeCode(), "All your cards as one line of text to send (picture cards stay behind)"));
        _body.Children.Add(Row("Save deck file…", null, _ => SaveDeckFile(), "All your cards in one file, pictures and fonts included"));   // NEW (deck files)
        _body.Children.Add(Row("Save theme file…", null, _ => ExportTheme(), "This theme as a theme file anyone can import"));

        Heading("Everything else");
        _body.Children.Add(Row("Every setting…", null, _ => OpenSettings(), "Everything a theme can hold, with a search box, and this theme's details"));
    }
}
