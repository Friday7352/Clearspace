using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Clearspace.Controls;
using Clearspace.Services;

namespace Clearspace;

// CHANGED (theme studio, round 2): the studio is laid out again from the top. What changed, in short:
//   - It follows the theme Clearspace is using, like every other window, instead of always being Dark with
//     hand-set colours on its tick boxes. Draft colours still only ever reach the preview.
//   - Header: the name of the theme, undo / redo (also Ctrl+Z / Ctrl+Y) and Simple / Advanced, in one row.
//   - Moods are small colour tiles you can see, not a block of plain buttons.
//   - Colours are a list: every part shows its colour and code, and a click opens the colour picker for it.
//     (The old way was a button that opened a long menu, and then a second button.) Advanced mode has a
//     search box over the list. While the picker is open the preview follows it live.
//   - A Readability card shows how well main text, dates and sizes, and labels read on this theme, and one
//     button lifts any that are too faint.
//   - Fonts, icon style and layout use the same rows; sliders show their value; the default WPF Expanders
//     (which drew in Windows' own style) are gone, every section is a card.
//   - The preview sits in a frame with Fit / 100% beside its heading; the footer only holds Reset, Cancel and
//     the save buttons, and says when there are unsaved changes.
//   - Fixed: the preview used to overwrite the message line 180 ms after every change, so errors such as
//     "Could not import that icon" vanished before they could be read. Closing with unsaved changes now asks.
// NEW (complex themes): themes that go well past colours, made by clicking and dragging:
//   - Layout: a mini map of the window (Controls/StudioLayoutMap.cs). Drag the sidebar, toolbar, tabs, status
//     bar, preview pane and key bar to where they should go, or onto the shelf to hide them; or pick a ready
//     layout (Classic, Mirrored, Focus, Commander, Bottom bar).
//   - Patterns: dots, grids, blueprint, stripes, ruled paper, checks, grain, stars, waves and confetti, for the
//     window, the file list, the file area, the sidebar or the toolbar, with colour, size and strength.
//   - Effects: scanlines, film grain, a fine grid or a screen door over everything; a vignette, a glow, a
//     sunbeam or dusk; a notebook spiral down the left edge; tilted sticky-note tooltips.
//   - Parts (Advanced): the sidebar, toolbar, file area and status bar each get their own fill, outline,
//     outline width and corners.
//   - Surprise me now also rolls a pattern, a layout and a shade now and then.
//   - The preview can show the preview pane, so where it sits can be seen.
// NEW (sticker editor): a new sticker opens in StudioImageWindow first (remove its background, crop it), and
// Edit picture… opens any sticker there again; for a picture that fills the window, cropping picks the part shown.
// NEW (stickers, picture themes): what makes a theme like Dino Park special is its own artwork and details:
//   - Stickers: your pictures anywhere on the window, in front of everything or behind the panels. Drag one in
//     the preview to move it, drag its corner to resize it; quick places, how solid it is, front or back.
//   - From a picture / My wallpaper: a whole theme built from a photo's colours (StudioPicture.cs), with the
//     photo behind see-through panels.
//   - Your themes: start from any theme you have (Dino Park included) and make it into a new one.
//   - Edge strips: a notebook spiral, warning tape, or your pattern down the left edge.
// All of it is written as ordinary theme keys (see ThemeKeys in ThemeLoader.cs), so a theme made here is one
// theme file like any other and can be shared.
internal sealed class ThemeStudioWindow : Window
{
    private ThemeStudioDocument _document;
    private readonly ThemeStudioDocument _initial;
    private readonly ThemeInfo? _editing;
    private readonly Stack<ThemeStudioDocument> _undo = new(), _redo = new();
    private readonly ResourceDictionary _fallback = new();
    private ResourceDictionary? _values;
    private bool _advanced, _syncing, _native, _invalid, _closing, _saved;
    private bool _gesture, _gestureRecorded;
    private bool _iconExtension = true;
    private string _gradientEnd = "#FF364B77";
    internal string? SavedName { get; private set; }

    // Header
    private readonly TextBox _name = new() { MaxLength = 60 };
    private readonly Button _undoButton = FileBackupUi.IconButton("", "Undo  ·  Ctrl+Z");
    private readonly Button _redoButton = FileBackupUi.IconButton("", "Redo  ·  Ctrl+Y");
    private readonly StackPanel _modeChips = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

    // Settings column
    private readonly TextBox _colorFilter = new();
    private readonly Border _colorFilterFrame;
    private readonly StackPanel _colorRows = new();
    private readonly Dictionary<string, (Border Swatch, TextBlock Code)> _colorCells = new();
    private readonly StackPanel _readability = new();
    private readonly Button _fixReadability = FileBackupUi.TextButton("Make text easier to read", false);
    private readonly Dictionary<string, TextBlock> _fontValues = new();
    private readonly StackPanel _simpleFontRow = new(), _advancedFontRows = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _layoutDetails = new() { Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, (Slider Slider, TextBlock Value)> _layoutSliders = new();
    private readonly StackPanel _advancedCards = new() { Visibility = Visibility.Collapsed };
    private readonly Slider _roundness = new() { Minimum = 0, Maximum = 16, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _roundnessValue = NumberText();
    private readonly Slider _backdropOpacity = new() { Minimum = 0, Maximum = 1, Value = .2, TickFrequency = .05, IsSnapToTickEnabled = true };
    private readonly TextBlock _backdropValue = NumberText();
    private readonly Border _gradientSwatch;
    private readonly TextBlock _iconStyleValue = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly CheckBox _crisp = FileBackupUi.Tick("Crisp pixel lettering", "Draws text with hard pixel edges, for pixel-art themes");
    private readonly CheckBox _fade = FileBackupUi.Tick("Fade toolbar when idle", "The toolbar fades back while the pointer is elsewhere");
    private readonly WrapPanel _matchChips = new();
    private readonly TextBox _iconTarget = new() { Text = ".pdf", MaxLength = 200 };
    private readonly StackPanel _iconRules = new();

    // NEW (complex themes): layout map, patterns, effects and parts.
    private readonly StudioLayoutMap _layoutMap = new();
    private readonly WrapPanel _layoutPresets = new();
    private readonly WrapPanel _patternTargets = new(), _patternTiles = new();
    private readonly Border _patternInkSwatch;
    private readonly Slider _patternSize = new() { Minimum = .5, Maximum = 3, Value = 1, TickFrequency = .1, IsSnapToTickEnabled = true };
    private readonly TextBlock _patternSizeValue = NumberText();
    private readonly Slider _patternStrength = new() { Minimum = .05, Maximum = 1, Value = .3, TickFrequency = .05, IsSnapToTickEnabled = true };
    private readonly TextBlock _patternStrengthValue = NumberText();
    private string _patternTarget = "Base";
    private StudioPattern? _pattern;   // the pattern put on _patternTarget in this session; re-made as the sliders move
    private StudioCustomPattern? _custom; // NEW (pattern maker): or one drawn in the pattern maker
    private Color? _patternInk;        // null: the theme's text colour
    private readonly WrapPanel _overlayChips = new(), _shadeChips = new();
    private StudioOverlay? _overlay;   // null: keep whatever the theme already has
    private StudioShade? _shade;
    private readonly Slider _effectStrength = new() { Minimum = .1, Maximum = 1, Value = .5, TickFrequency = .05, IsSnapToTickEnabled = true };
    private readonly TextBlock _effectStrengthValue = NumberText();
    private readonly WrapPanel _edgeChips = new(); // CHANGED (edge strips): was a "Notebook spiral" tick box
    private string? _edge;                          // null: keep whatever strip the theme already has
    // NEW (stickers)
    private readonly StackPanel _stickerList = new(), _stickerControls = new() { Visibility = Visibility.Collapsed };
    private readonly WrapPanel _stickerLayerChips = new(), _stickerPlaceChips = new();
    private readonly Slider _stickerOpacity = new() { Minimum = .05, Maximum = 1, Value = 1, TickFrequency = .05, IsSnapToTickEnabled = true };
    private readonly TextBlock _stickerOpacityValue = NumberText();
    private string? _sticker;                       // the selected sticker's key
    private readonly Canvas _stickerLayer = new() { ClipToBounds = true };
    private readonly Grid _stickerHandle = new() { Visibility = Visibility.Collapsed, Cursor = Cursors.SizeAll };
    private readonly Border _stickerGrip = new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Cursor = Cursors.SizeNWSE, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -7, -7) };
    private Rect _dragBox, _dragStartBox;
    private Point _dragStart;
    private bool _dragResize, _dragging;
    private readonly CheckBox _tilt = FileBackupUi.Tick("Tilted tooltips", "Tooltips lean a little, like sticky notes");
    private readonly WrapPanel _partChips = new();
    private string _part = "Files";
    private readonly StackPanel _partColorRows = new();
    private readonly Dictionary<string, (Border Swatch, TextBlock Code)> _partCells = new();
    private readonly Slider _partOutline = new() { Minimum = 0, Maximum = 4, TickFrequency = .5, IsSnapToTickEnabled = true };
    private readonly TextBlock _partOutlineValue = NumberText();
    private readonly Slider _partCorner = new() { Minimum = 0, Maximum = 24, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _partCornerValue = NumberText();
    private readonly StackPanel _partShape = new();
    private bool _paneShown;
    private readonly StackPanel _layoutClassic = new();          // NEW (custom layouts)
    private readonly TextBlock _customLayoutNote = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };

    // NEW (everything): interface size, click-to-change in the preview, every setting, and the theme's details.
    private readonly Slider _scale = new() { Minimum = .75, Maximum = 1.5, Value = 1, TickFrequency = .05, IsSnapToTickEnabled = true };
    private readonly TextBlock _scaleValue = NumberText();
    private Border? _previewFrame;
    private readonly TextBox _everyFilter = new();
    private readonly StackPanel _everyRows = new();
    private readonly Dictionary<string, (Border Swatch, TextBlock Code)> _everyCells = new();
    private readonly List<Action> _everyRefresh = new();
    private readonly TextBox _author = new() { MaxLength = 60 }, _description = new() { MaxLength = 200, TextWrapping = TextWrapping.Wrap }, _version = new() { MaxLength = 20 };
    private readonly CheckBox _eink = FileBackupUi.Tick("E-reader screen", "Flashes a page turn when the view changes, for e-ink displays");
    private readonly CheckBox _experimental = FileBackupUi.Tick("Experimental", "Listed with the experimental themes");

    // Preview column
    private readonly StackPanel _zoomChips = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _previewScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Viewbox _fit = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top };
    private ExplorerThemePreview? _isolatedPreview;

    // Footer
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly TextBlock _changes = new() { Text = "Unsaved changes", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 0, 14, 0), Visibility = Visibility.Collapsed };
    private readonly Button _save = FileBackupUi.TextButton("Save & use", true);
    private readonly Button _copy = FileBackupUi.TextButton("Save a copy", false);

    private readonly DispatcherTimer _paintTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _pickerTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private (string Key, string Hex)? _pickerDraft;

    private static readonly string[] Swatches = ["#AFA1FF", "#64D8CF", "#FF9DCB", "#F6C56F", "#426B41", "#A54432", "#326CC0", "#FFFFFF", "#171D30", "#000000"];
    internal static IReadOnlyList<string> SuggestedColors => Swatches;

    internal ThemeStudioWindow(ThemeInfo? editing = null)
    {
        _editing = editing;
        _document = editing is null ? ThemeStudioDocument.FromDesign(ThemeDesign.Presets[0].Design) : ThemeStudioDocument.Open(editing);
        _initial = _document;
        _name.Text = editing is null ? "My theme" : editing.Shipped ? editing.Label + " remix" : editing.Label;
        Title = editing is null ? "Theme studio" : "Theme studio · " + editing.Label;
        Width = 1240; Height = 820; MinWidth = 980; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // CHANGED (theme studio, round 2): no Dark dictionary merged onto the window any more; it uses the app's
        // theme like the other windows. The draft is only ever merged into the isolated preview.
        // Missing keys in a draft are read from the same base the preview and the loader use.
        _fallback.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Clearspace;component/Themes/Defaults.xaml", UriKind.Relative) });
        _fallback.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Clearspace;component/Themes/Dark/theme.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "Base"); SetResourceReference(ForegroundProperty, "Ink"); SetResourceReference(FontFamilyProperty, "UIFont");
        FontSize = 13;
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);

        var page = new Grid();
        page.SetResourceReference(Panel.BackgroundProperty, "Base");
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition());
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ---- Header: title, name, undo / redo, Simple / Advanced
        var header = new Grid { Margin = new Thickness(24, 18, 24, 16) };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var title = new TextBlock { Text = "Theme studio", FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(title);
        var naming = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var nameLabel = FileBackupUi.Muted("Name"); nameLabel.VerticalAlignment = VerticalAlignment.Center; nameLabel.Margin = new Thickness(0, 0, 10, 0);
        naming.Children.Add(nameLabel);
        AutomationProperties.SetName(_name, "Theme name");
        var nameFrame = FileBackupUi.Framed(_name); nameFrame.Width = 260; naming.Children.Add(nameFrame);
        _name.TextChanged += (_, _) => UpdateButtons();
        Grid.SetColumn(naming, 1); header.Children.Add(naming);
        var history = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        _undoButton.Click += (_, _) => History(_undo, _redo); _redoButton.Click += (_, _) => History(_redo, _undo);
        history.Children.Add(_undoButton); history.Children.Add(_redoButton);
        Grid.SetColumn(history, 3); header.Children.Add(history);
        Grid.SetColumn(_modeChips, 4); header.Children.Add(_modeChips);
        page.Children.Add(header);

        // ---- Body: settings on the left, preview on the right
        var body = new Grid { Margin = new Thickness(24, 0, 24, 16) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(344) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(body, 1); page.Children.Add(body);
        var controls = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        body.Children.Add(new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        // Start from (moods)
        var moods = new WrapPanel();
        foreach (var (name, design) in ThemeDesign.Presets)
            moods.Children.Add(MoodTile(name, design));
        moods.Children.Add(SurpriseTile());
        // NEW (picture themes, start from any theme)
        moods.Children.Add(GlyphTile("\uEB9F", "From a picture", "A whole theme from a photo's colors, with the photo behind the panels", FromPictureFile));
        moods.Children.Add(GlyphTile("\uE7F4", "My wallpaper", "A whole theme from your desktop wallpaper", FromWallpaper));
        moods.Children.Add(GlyphTile("\uE790", "Your themes…", "Start from any theme you have, and save it as a new one", ShowYourThemes));
        var moodNote = Small("Replaces your colors. Undo brings them back.");
        controls.Children.Add(StudioUi.Card("Start from", moods, moodNote));

        // Colors
        _colorFilter.TextChanged += (_, _) => BuildColorRows();
        AutomationProperties.SetName(_colorFilter, "Find a part to color");
        _colorFilterFrame = FileBackupUi.Framed(_colorFilter);
        _colorFilterFrame.Margin = new Thickness(0, 0, 0, 8);
        _colorFilterFrame.Visibility = Visibility.Collapsed;
        _colorFilter.ToolTip = "Find a part, such as text, border or menu";
        controls.Children.Add(StudioUi.Card("Colors", _colorFilterFrame, _colorRows));

        // Readability
        _fixReadability.HorizontalAlignment = HorizontalAlignment.Left;
        _fixReadability.Margin = new Thickness(0, 10, 0, 0);
        _fixReadability.ToolTip = "Lifts any text that reads below 4.5:1 just enough to pass";
        _fixReadability.Click += (_, _) => FixReadability();
        controls.Children.Add(StudioUi.Card("Readability", _readability, _fixReadability));

        // CHANGED (complex themes): Layout is a mini map you drag parts around on, with ready layouts above it.
        // The sidebar width and row spacing sliders stay in Advanced; the four layout tick boxes are the map now.
        AddLayoutSlider(_layoutDetails, "Sidebar width", "Explorer.SidebarWidth", 170, 400);
        AddLayoutSlider(_layoutDetails, "Row spacing", "Explorer.RowPadding", 0, 14);
        _layoutDetails.Margin = new Thickness(0, 6, 0, 0);
        _layoutMap.Margin = new Thickness(0, 4, 0, 4);
        _layoutMap.Changed += layout => TryChange(() => WithLayout(_document, layout));
        // NEW (everything): how big everything in the explorer is drawn.
        _scale.SetResourceReference(StyleProperty, "MediaSlider"); GroupDrag(_scale);
        AutomationProperties.SetName(_scale, "Interface size");
        _scale.ValueChanged += (_, _) => { _scaleValue.Text = $"{_scale.Value * 100:0}%"; if (!_syncing) TryChange(() => _document.Scale(_scale.Value)); };
        // CHANGED (custom layouts): the map and ready layouts move whole parts; "Build a custom layout…" opens the
        // layout builder (StudioLayoutBuilderWindow), which can put anything anywhere, extra file panes included.
        // While a theme has a custom layout, the map steps aside (the custom layout decides everything).
        _layoutClassic.Children.Add(_layoutPresets);
        _layoutClassic.Children.Add(Small("Drag a part to move it. Drop it on the shelf to hide it."));
        _layoutClassic.Children.Add(_layoutMap);
        var builder = FileBackupUi.TextButton("Build a custom layout…", true);
        builder.HorizontalAlignment = HorizontalAlignment.Left; builder.Margin = new Thickness(0, 8, 0, 4);
        builder.ToolTip = "Put any part anywhere: extra file panes side by side, a browser look, a dashboard, text, pictures, a clock";
        builder.Click += (_, _) => OpenLayoutBuilder();
        _customLayoutNote.Margin = new Thickness(0, 2, 0, 4);
        controls.Children.Add(StudioUi.Card("Layout", _layoutClassic, _customLayoutNote, builder,
            SliderRow("Interface size", _scale, _scaleValue), _layoutDetails));

        // NEW (complex themes): patterns.
        var (inkOuter, inkInner) = StudioUi.Swatch(22);
        _patternInkSwatch = inkInner;
        var inkRow = new DockPanel();
        DockPanel.SetDock(inkOuter, Dock.Left); inkRow.Children.Add(inkOuter);
        inkRow.Children.Add(new TextBlock { Text = "Pattern color", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        var inkPick = StudioUi.ClickRow(inkRow, "Pattern color", PickPatternInk);
        inkPick.Padding = new Thickness(6, 5, 8, 5);
        foreach (var slider in new[] { _patternSize, _patternStrength })
        {
            slider.SetResourceReference(StyleProperty, "MediaSlider"); GroupDrag(slider);
            slider.ValueChanged += (_, _) => { ShowPatternValues(); if (!_syncing) ReapplyPattern(); };
        }
        AutomationProperties.SetName(_patternSize, "Pattern size"); AutomationProperties.SetName(_patternStrength, "Pattern strength");
        _patternTiles.Margin = new Thickness(0, 6, 0, 2);
        controls.Children.Add(StudioUi.Card("Patterns", Small("Where"), _patternTargets, _patternTiles, inkPick,
            SliderRow("Size", _patternSize, _patternSizeValue), SliderRow("Strength", _patternStrength, _patternStrengthValue)));

        // NEW (complex themes): screen effects.
        _effectStrength.SetResourceReference(StyleProperty, "MediaSlider"); GroupDrag(_effectStrength);
        AutomationProperties.SetName(_effectStrength, "Effect strength");
        _effectStrength.ValueChanged += (_, _) => { _effectStrengthValue.Text = $"{_effectStrength.Value * 100:0}%"; if (!_syncing && (_overlay is not null || _shade is not null)) ApplyEffects(); };
        _tilt.Margin = new Thickness(8, 8, 0, 4);
        _tilt.Checked += (_, _) => SetTilt(true); _tilt.Unchecked += (_, _) => SetTilt(false);
        controls.Children.Add(StudioUi.Card("Effects", Small("Over the whole window"), _overlayChips, Small("Light and shade"), _shadeChips,
            SliderRow("Strength", _effectStrength, _effectStrengthValue), StudioUi.Divider(), Small("Down the left edge"), _edgeChips, _tilt));

        // NEW (stickers)
        var addSticker = FileBackupUi.TextButton("Add a picture…", false);
        addSticker.HorizontalAlignment = HorizontalAlignment.Left; addSticker.Margin = new Thickness(0, 6, 0, 0);
        addSticker.ToolTip = "A PNG with a see-through background works best";
        addSticker.Click += (_, _) => AddSticker();
        _stickerOpacity.SetResourceReference(StyleProperty, "MediaSlider"); GroupDrag(_stickerOpacity);
        AutomationProperties.SetName(_stickerOpacity, "Sticker strength");
        _stickerOpacity.ValueChanged += (_, _) =>
        {
            _stickerOpacityValue.Text = $"{_stickerOpacity.Value * 100:0}%";
            if (!_syncing && _sticker is { } key && CurrentSticker() is { } s) TryChange(() => _document.PlaceSticker(key, s.Box, _stickerOpacity.Value));
        };
        var removeSticker = FileBackupUi.TextButton("Remove", false);
        removeSticker.HorizontalAlignment = HorizontalAlignment.Left; removeSticker.Margin = new Thickness(0, 8, 0, 0);
        removeSticker.Click += (_, _) => { if (_sticker is { } key) { _sticker = null; TryChange(() => _document.Without(key).DropUnusedImages()); } };
        // NEW (sticker editor)
        var editSticker = FileBackupUi.TextButton("Edit picture…", false);
        editSticker.HorizontalAlignment = HorizontalAlignment.Left; editSticker.Margin = new Thickness(0, 8, 0, 0);
        editSticker.ToolTip = "Remove the background, or crop it (for a picture that fills the window, choose which part shows)";
        editSticker.Click += (_, _) => EditSticker();
        _stickerControls.Children.Add(editSticker);
        _stickerControls.Children.Add(Small("Drag it in the preview to move it; drag its corner to resize it."));
        _stickerControls.Children.Add(_stickerLayerChips);
        _stickerControls.Children.Add(Small("Quick places"));
        _stickerControls.Children.Add(_stickerPlaceChips);
        _stickerControls.Children.Add(SliderRow("Strength", _stickerOpacity, _stickerOpacityValue));
        _stickerControls.Children.Add(removeSticker);
        controls.Children.Add(StudioUi.Card("Stickers", Small("Your own pictures on the window, in front of the files or behind the panels."),
            _stickerList, addSticker, _stickerControls));

        // Font
        _simpleFontRow.Children.Add(MenuRow("All text", "All", b => ShowFonts(b, "All")));
        foreach (var (key, label) in new[] { ("UIFont", "Interface"), ("SerifFont", "File names"), ("DisplayFont", "Headings"), ("MonoFont", "Paths & code") })
            _advancedFontRows.Children.Add(MenuRow(label, key, b => ShowFonts(b, key)));
        var addFont = FileBackupUi.TextButton("Add a font file…", false);
        addFont.HorizontalAlignment = HorizontalAlignment.Left; addFont.Margin = new Thickness(0, 8, 0, 0);
        addFont.ToolTip = "Adds a TTF or OTF font to your library, so every font list offers it";
        addFont.Click += (_, _) => ImportFont();
        controls.Children.Add(StudioUi.Card("Font", _simpleFontRow, _advancedFontRows, addFont));

        // NEW (complex themes): parts (Advanced). Pick a part, then its fill, outline, outline width and corners.
        foreach (var slider in new[] { _partOutline, _partCorner }) { slider.SetResourceReference(StyleProperty, "MediaSlider"); GroupDrag(slider); }
        AutomationProperties.SetName(_partOutline, "Outline width"); AutomationProperties.SetName(_partCorner, "Corners");
        _partOutline.ValueChanged += (_, _) => { _partOutlineValue.Text = $"{_partOutline.Value:0.#} px"; if (!_syncing) TryChange(() => _document.Spacing($"Part.{_part}.EdgeWidth", _partOutline.Value, _partOutline.Value, _partOutline.Value, _partOutline.Value)); };
        _partCorner.ValueChanged += (_, _) => { _partCornerValue.Text = $"{_partCorner.Value:0} px"; if (!_syncing) TryChange(() => _document.Corner($"Part.{_part}.Corner", _partCorner.Value)); };
        _partShape.Children.Add(SliderRow("Outline width", _partOutline, _partOutlineValue));
        _partShape.Children.Add(SliderRow("Corners", _partCorner, _partCornerValue));
        var resetPart = FileBackupUi.TextButton("Reset this part", false);
        resetPart.HorizontalAlignment = HorizontalAlignment.Left; resetPart.Margin = new Thickness(0, 8, 0, 0);
        resetPart.Click += (_, _) => TryChange(() => _document.Without($"Part.{_part}.Fill", $"Part.{_part}.Edge", $"Part.{_part}.EdgeWidth", $"Part.{_part}.Corner"));
        _advancedCards.Children.Add(StudioUi.Card("Parts", _partChips, _partColorRows, _partShape, resetPart));

        // NEW (everything): every value a theme can hold, in one searchable list.
        AutomationProperties.SetName(_everyFilter, "Find a setting");
        _everyFilter.ToolTip = "Find a setting, such as menu, corner or sidebar";
        _everyFilter.TextChanged += (_, _) => BuildEverySetting();
        var everyFrame = FileBackupUi.Framed(_everyFilter); everyFrame.Margin = new Thickness(0, 0, 0, 8);
        _everyRows.Margin = new Thickness(0, 0, 0, 0);
        _advancedCards.Children.Add(StudioUi.Card("Every setting", Small("Everything a theme can change. Click a color or font to change it; type a size and press Enter."),
            everyFrame, _everyRows));

        // NEW (everything): the theme's own details, shown on the Themes page.
        foreach (var (box, name) in new[] { (_author, "Made by"), (_version, "Version") })
            AutomationProperties.SetName(box, name);
        AutomationProperties.SetName(_description, "Description");
        _author.LostFocus += (_, _) => SetDetails(); _version.LostFocus += (_, _) => SetDetails(); _description.LostFocus += (_, _) => SetDetails();
        _eink.Checked += (_, _) => SetDetails(); _eink.Unchecked += (_, _) => SetDetails();
        _experimental.Checked += (_, _) => SetDetails(); _experimental.Unchecked += (_, _) => SetDetails();
        _eink.Margin = new Thickness(8, 8, 0, 4); _experimental.Margin = new Thickness(8, 4, 0, 4);
        _advancedCards.Children.Add(StudioUi.Card("About this theme", Small("Made by"), FileBackupUi.Framed(_author), Small("Description"),
            FileBackupUi.Framed(_description), Small("Version"), FileBackupUi.Framed(_version), _eink, _experimental));

        // Advanced only: shape, background, icons
        _roundness.SetResourceReference(StyleProperty, "MediaSlider"); AutomationProperties.SetName(_roundness, "Corner roundness");
        GroupDrag(_roundness);
        _roundness.ValueChanged += (_, _) => { _roundnessValue.Text = $"{_roundness.Value:0} px"; if (!_syncing) Change(_document.Corners(_roundness.Value)); };
        _advancedCards.Children.Add(StudioUi.Card("Shape", SliderRow("Corner roundness", _roundness, _roundnessValue)));

        var (gradientOuter, gradientInner) = StudioUi.Swatch(26);
        _gradientSwatch = gradientInner;
        var gradientRow = new DockPanel();
        var gradientLabel = new TextBlock { Text = "Blend the background into", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(gradientOuter, Dock.Left); gradientRow.Children.Add(gradientOuter); gradientRow.Children.Add(gradientLabel);
        var gradientPick = StudioUi.ClickRow(gradientRow, "Gradient end color", PickGradientEnd);
        var gradientChips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        gradientChips.Children.Add(FileBackupUi.Chip("Diagonal", false, () => AddGradient(true)));
        gradientChips.Children.Add(FileBackupUi.Chip("Top to bottom", false, () => AddGradient(false)));
        gradientChips.Children.Add(FileBackupUi.Chip("Solid", false, () => TryChange(() => _document.Color("Base", StudioUi.Hex(Solid("Base"))))));
        var pictureChips = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        pictureChips.Children.Add(FileBackupUi.Chip("Choose picture…", false, ChooseBackdrop));
        pictureChips.Children.Add(FileBackupUi.Chip("Remove", false, () => TryChange(() => _document.Color("ListFill", "#00000000"))));
        _backdropOpacity.SetResourceReference(StyleProperty, "MediaSlider"); AutomationProperties.SetName(_backdropOpacity, "Picture strength");
        GroupDrag(_backdropOpacity);
        _backdropOpacity.ValueChanged += (_, _) => { _backdropValue.Text = $"{_backdropOpacity.Value * 100:0}%"; if (!_syncing) Change(_document.BackdropOpacity(_backdropOpacity.Value)); };
        _advancedCards.Children.Add(StudioUi.Card("Background", gradientPick, gradientChips, StudioUi.Divider(),
            Small("A picture behind your files"), pictureChips, SliderRow("Picture strength", _backdropOpacity, _backdropValue)));

        var iconStyle = MenuRow("Icon style", null, ShowIconStyles, _iconStyleValue);
        _crisp.Checked += (_, _) => SetSwitches(); _crisp.Unchecked += (_, _) => SetSwitches();
        _fade.Checked += (_, _) => SetSwitches(); _fade.Unchecked += (_, _) => SetSwitches();
        _crisp.Margin = new Thickness(8, 6, 0, 4); _fade.Margin = new Thickness(8, 4, 0, 4);
        AutomationProperties.SetName(_iconTarget, "Custom icon target");
        var targetFrame = FileBackupUi.Framed(_iconTarget); targetFrame.Margin = new Thickness(0, 2, 0, 6);
        var chooseIcon = FileBackupUi.TextButton("Choose icon image…", false);
        chooseIcon.HorizontalAlignment = HorizontalAlignment.Left; chooseIcon.Margin = new Thickness(0);
        chooseIcon.Click += (_, _) => ImportIcon();
        _iconRules.Margin = new Thickness(0, 6, 0, 0);
        _advancedCards.Children.Add(StudioUi.Card("File icons", iconStyle, _crisp, _fade, StudioUi.Divider(),
            Small("Your own icon for a file type, a file name, or all folders"), _matchChips, targetFrame, chooseIcon, _iconRules));

        controls.Children.Add(_advancedCards);

        // ---- Preview
        var right = new Grid(); Grid.SetColumn(right, 2); body.Children.Add(right);
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition());
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var previewHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_zoomChips, Dock.Right); previewHeader.Children.Add(_zoomChips);
        var previewTitle = StudioUi.Heading("Preview"); previewTitle.VerticalAlignment = VerticalAlignment.Center; previewTitle.Margin = new Thickness(2, 0, 0, 0);
        previewHeader.Children.Add(previewTitle);
        right.Children.Add(previewHeader);
        // CHANGED (stickers): the preview has a layer on top for the selected sticker's handle.
        var stage = new Grid();
        stage.Children.Add(_previewScroll); stage.Children.Add(_stickerLayer);
        BuildStickerHandle();
        var frame = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(12), ClipToBounds = true, Child = stage };
        frame.SizeChanged += (_, _) => PlaceStickerHandle();
        _previewScroll.ScrollChanged += (_, _) => PlaceStickerHandle();
        frame.SetResourceReference(Border.BackgroundProperty, "Tint");
        frame.SetResourceReference(Border.BorderBrushProperty, "LineFaint");
        frame.SetResourceReference(Border.CornerRadiusProperty, "R12");
        Grid.SetRow(frame, 1); right.Children.Add(frame);
        // NEW (everything): click anything in the preview to change the colours and fonts it is drawn with.
        _previewFrame = frame;
        frame.Cursor = Cursors.Hand;
        frame.MouseLeftButtonUp += (_, e) => PickFromPreview(e);
        var caption = Small("Click anything in the preview to change it. Nothing in Clearspace changes until you save.");
        caption.Margin = new Thickness(2, 8, 0, 0);
        Grid.SetRow(caption, 2); right.Children.Add(caption);

        // ---- Footer
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(24, 12, 24, 14) };
        footer.SetResourceReference(Border.BorderBrushProperty, "LineFaint");
        Grid.SetRow(footer, 2); page.Children.Add(footer);
        var footerRow = new DockPanel(); footer.Child = footerRow;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(actions, Dock.Right); footerRow.Children.Add(actions);
        var reset = FileBackupUi.TextButton("Reset", false);
        reset.ToolTip = "Go back to how this theme was when the studio opened";
        reset.Click += (_, _) => Change(_initial);
        var cancel = FileBackupUi.TextButton("Cancel", false); cancel.IsCancel = true; cancel.Click += (_, _) => Close();
        _copy.Visibility = editing is { Shipped: false } ? Visibility.Visible : Visibility.Collapsed;
        _copy.Click += (_, _) => Save(copy: true);
        _save.Content = editing is { Shipped: false } ? "Save changes" : "Save & use";
        _save.SetResourceReference(BackgroundProperty, "Accent");
        _save.SetResourceReference(ForegroundProperty, "AccentInk");
        _save.FontWeight = FontWeights.SemiBold;
        _save.Click += (_, _) => Save(copy: false);
        actions.Children.Add(reset); actions.Children.Add(cancel); actions.Children.Add(_copy); actions.Children.Add(_save);
        var notes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _changes.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        notes.Children.Add(_changes); notes.Children.Add(_status);
        footerRow.Children.Add(notes);

        Content = page;
        _paintTimer.Tick += (_, _) => { _paintTimer.Stop(); Paint(); };
        _pickerTimer.Tick += (_, _) => { _pickerTimer.Stop(); PaintDraftColor(); };
        PreviewKeyDown += OnKeys;
        Closing += OnClosing;
        Loaded += (_, _) => StartPreview();
        Closed += (_, _) => { _closing = true; _paintTimer.Stop(); _pickerTimer.Stop(); _previewScroll.Content = null; _fit.Child = null; _isolatedPreview = null; };
        BuildMatchChips();
        SetMode(false);
    }

    // ---------------------------------------------------------------- building blocks

    private static TextBlock NumberText() => new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock Small(string text)
    {
        var block = FileBackupUi.Muted(text); block.FontSize = 12; block.Margin = new Thickness(0, 4, 0, 4); return block;
    }

    // A mood: a little picture of its background, text and accent, with its name under it.
    private FrameworkElement MoodTile(string name, ThemeDesign design)
    {
        ThemeDesign.TryColor(design.Background, out var background);
        ThemeDesign.TryColor(design.Accent, out var accent);
        var ink = StudioUi.TextOn(background);
        var picture = new Grid { Width = 92, Height = 52 };
        picture.Children.Add(new TextBlock
        {
            Text = "Aa", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(ink),
            FontFamily = new FontFamily(design.Font), Margin = new Thickness(10, 7, 0, 0)
        });
        picture.Children.Add(new Border
        {
            Width = 30, Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(accent),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 10)
        });
        var swatch = new Border { Background = new SolidColorBrush(background), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = picture };
        swatch.SetResourceReference(Border.BorderBrushProperty, "Line");
        return Tile(swatch, name, () => Change(ThemeStudioDocument.FromDesign(design)));
    }

    private FrameworkElement SurpriseTile()
    {
        var glyph = new TextBlock { Text = "", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        var swatch = new Border { Width = 92, Height = 52, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = glyph };
        swatch.SetResourceReference(Border.BackgroundProperty, "Tint");
        swatch.SetResourceReference(Border.BorderBrushProperty, "Line");
        return Tile(swatch, "Surprise me", () =>
        {
            var p = ThemeDesign.Presets[Random.Shared.Next(ThemeDesign.Presets.Length)].Design;
            TryChange(() => Surprise(p with { Accent = Swatches[Random.Shared.Next(7)], Roundness = Random.Shared.Next(17) }));
        });
    }

    private static FrameworkElement Tile(FrameworkElement picture, string name, Action pick)
    {
        var stack = new StackPanel();
        stack.Children.Add(picture);
        stack.Children.Add(new TextBlock { Text = name, FontSize = 12, Margin = new Thickness(2, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        var tile = StudioUi.ClickRow(stack, name, pick);
        tile.Padding = new Thickness(4);
        tile.Margin = new Thickness(0, 0, 4, 4);
        tile.ToolTip = name == "Surprise me" ? "A random mood, sometimes with a pattern, a layout or a glow" : "Start from " + name;
        return tile;
    }

    // A row with a label on the left, a value, and a ⌄ that opens a menu at the row.
    private Border MenuRow(string label, string? fontKey, Action<FrameworkElement> open, TextBlock? value = null)
    {
        value ??= new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        if (fontKey is not null) _fontValues[fontKey] = value;
        var row = new DockPanel();
        var name = FileBackupUi.Muted(label); name.Width = 96; name.VerticalAlignment = VerticalAlignment.Center; name.TextWrapping = TextWrapping.NoWrap;
        DockPanel.SetDock(name, Dock.Left); row.Children.Add(name);
        var chevron = new TextBlock { Text = "", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        chevron.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        DockPanel.SetDock(chevron, Dock.Right); row.Children.Add(chevron);
        row.Children.Add(value);
        Border? clickable = null;
        clickable = StudioUi.ClickRow(row, label, () => open(clickable!));
        return clickable;
    }

    private static StackPanel SliderRow(string label, Slider slider, TextBlock value)
    {
        var block = new StackPanel { Margin = new Thickness(0, 6, 0, 2) };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        value.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        DockPanel.SetDock(value, Dock.Right); top.Children.Add(value);
        top.Children.Add(new TextBlock { Text = label, FontSize = 12.5 });
        block.Children.Add(top); block.Children.Add(slider);
        return block;
    }

    private void AddLayoutSlider(Panel panel, string label, string key, double min, double max)
    {
        var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = 1, IsSnapToTickEnabled = true };
        var value = NumberText();
        slider.SetResourceReference(StyleProperty, "MediaSlider"); AutomationProperties.SetName(slider, label); GroupDrag(slider);
        slider.ValueChanged += (_, _) => { value.Text = $"{slider.Value:0} px"; if (!_syncing) Change(_document.Layout(key, slider.Value)); };
        panel.Children.Add(SliderRow(label, slider, value)); _layoutSliders.Add(key, (slider, value));
    }

    private void GroupDrag(Slider slider)
    {
        // One undo step per drag, not one per pixel.
        slider.PreviewMouseLeftButtonDown += (_, _) => { _gesture = true; _gestureRecorded = false; };
        slider.PreviewMouseLeftButtonUp += (_, _) => { _gesture = false; _gestureRecorded = false; };
        slider.LostMouseCapture += (_, _) => { _gesture = false; _gestureRecorded = false; };
    }

    // ---------------------------------------------------------------- modes and colors

    internal void SetMode(bool advanced)
    {
        _advanced = advanced;
        _modeChips.Children.Clear();
        var simple = FileBackupUi.Chip("Simple", !advanced, () => SetMode(false));
        simple.ToolTip = "Moods, two colors, one font and quick layouts";
        var detailed = FileBackupUi.Chip("Advanced", advanced, () => SetMode(true));
        detailed.ToolTip = "Every color, each font, pictures, icons and layout details";
        simple.Margin = detailed.Margin = new Thickness(0, 0, 6, 0);
        _modeChips.Children.Add(simple); _modeChips.Children.Add(detailed);
        _advancedCards.Visibility = _layoutDetails.Visibility = _advancedFontRows.Visibility = _colorFilterFrame.Visibility =
            advanced ? Visibility.Visible : Visibility.Collapsed;
        _simpleFontRow.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
        if (!advanced) _colorFilter.Text = "";
        BuildColorRows();
        BuildPatternChoices(); BuildEffectChips(); BuildPartChoices(); // NEW (complex themes)
        BuildEdgeChips(); // NEW (edge strips)
        if (advanced) BuildEverySetting(); else { _everyRows.Children.Clear(); _everyRefresh.Clear(); _everyCells.Clear(); } // NEW (everything)
        Sync();
    }

    private void BuildColorRows()
    {
        _colorRows.Children.Clear(); _colorCells.Clear();
        var filter = _colorFilter.Text.Trim();
        var options = (_advanced ? StudioColorOptions.All : StudioColorOptions.All.Where(o => o.Key is "Base" or "Accent"))
            .Where(o => filter.Length == 0 || o.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) || o.Group.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var group in options.GroupBy(o => o.Group))
        {
            if (_advanced)
            {
                var heading = new TextBlock { Text = group.Key, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, _colorRows.Children.Count == 0 ? 0 : 10, 0, 2) };
                heading.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
                _colorRows.Children.Add(heading);
            }
            foreach (var option in group) _colorRows.Children.Add(ColorRow(option.Key, option.Label, _colorCells));
        }
        if (options.Length == 0) _colorRows.Children.Add(Small("No part matches “" + filter + "”."));
        if (!_advanced) _colorRows.Children.Add(Small("Text, borders and menus follow these two."));
        SyncColors();
    }

    private Border ColorRow(string key, string label, Dictionary<string, (Border Swatch, TextBlock Code)> cells)
    {
        var (outer, inner) = StudioUi.Swatch(24);
        var code = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        code.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        code.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        var row = new DockPanel();
        DockPanel.SetDock(outer, Dock.Left); row.Children.Add(outer);
        DockPanel.SetDock(code, Dock.Right); row.Children.Add(code);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        cells[key] = (inner, code);
        var clickable = StudioUi.ClickRow(row, "Change " + label, () => PickColor(key, label));
        clickable.Padding = new Thickness(6, 5, 8, 5);
        return clickable;
    }

    private void PickColor(string key, string label)
    {
        var picker = new StudioColorWindow(this, ReadColor(key), _advanced || key is not ("Base" or "Accent"), hex => { _pickerDraft = (key, hex); _pickerTimer.Stop(); _pickerTimer.Start(); }, label);
        var chosen = picker.ShowDialog() == true;
        _pickerTimer.Stop(); _pickerDraft = null;
        if (chosen) TryChange(() => EditColor(key, picker.SelectedColor));
        else Paint(); // put the preview back as it was
    }

    // NEW (theme studio, round 2): the preview follows the colour picker while it is open, without
    // touching undo or the saved draft.
    private void PaintDraftColor()
    {
        if (_closing || _isolatedPreview is null || _pickerDraft is not { } draft) return;
        try { _isolatedPreview.Show(EditColor(draft.Key, draft.Hex)); }
        catch (Exception error) when (error is ThemeFormatException or ArgumentException) { }
    }

    private ThemeStudioDocument EditColor(string key, string hex)
    {
        // CHANGED (complex themes): only the background and the accent re-derive the palette in Simple mode. Any
        // other part (picked from the preview, a part's fill, every setting) is set on its own, in either mode.
        if (_advanced || key is not ("Base" or "Accent")) return _document.Color(key, hex);
        _document.Color(key, hex); // validate before conversion
        // Simple mode keeps text, borders and menus balanced by re-deriving them from background and accent.
        return _document.Palette(StudioUi.Hex(key == "Base" ? Parse(hex) : Solid("Base"), opaque: true).Remove(1, 2),
                                 StudioUi.Hex(key == "Accent" ? Parse(hex) : Solid("Accent"), opaque: true).Remove(1, 2));
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private void PickGradientEnd()
    {
        var picker = new StudioColorWindow(this, _gradientEnd, false, null, "Gradient end");
        if (picker.ShowDialog() != true) return;
        _gradientEnd = picker.SelectedColor;
        _gradientSwatch.Background = new SolidColorBrush(Parse(_gradientEnd));
        AddGradient(true);
    }

    private void AddGradient(bool diagonal)
    {
        try { Change(_document.Gradient(StudioUi.Hex(Solid("Base")), _gradientEnd, diagonal)); }
        catch (ArgumentException) { Report("Choose a valid end color for the gradient.", true); }
    }

    // ---------------------------------------------------------------- layout (NEW, complex themes)

    private bool On(string key) => Value(key) is Visibility.Visible;

    private StudioLayout CurrentLayout() => new(On("Explorer.SidebarVisibility"), On("Explorer.SidebarOnRight"), On("Explorer.ToolbarBottom"),
        On("Explorer.TabsVisibility"), On("Explorer.TabsBelowToolbar"), On("Explorer.StatusBarVisibility"), On("Explorer.StatusBarTop"),
        On("Explorer.PreviewOnLeft"), On("FunctionKeysVisibility"));

    private static ThemeStudioDocument WithLayout(ThemeStudioDocument d, StudioLayout l)
    {
        static double B(bool on) => on ? 1 : 0;
        return d.Layout("Explorer.SidebarVisibility", B(l.Sidebar)).Layout("Explorer.SidebarOnRight", B(l.SidebarRight))
            .Layout("Explorer.ToolbarBottom", B(l.ToolbarBottom)).Layout("Explorer.TabsVisibility", B(l.Tabs))
            .Layout("Explorer.TabsBelowToolbar", B(l.TabsBelow)).Layout("Explorer.StatusBarVisibility", B(l.Status))
            .Layout("Explorer.StatusBarTop", B(l.StatusTop)).Layout("Explorer.PreviewOnLeft", B(l.PreviewLeft))
            .Layout("FunctionKeysVisibility", B(l.Keys));
    }

    private static readonly (string Name, string Tip, StudioLayout Layout)[] LayoutPresets =
    [
        ("Classic", "Sidebar left, toolbar on top", new(true, false, false, true, false, true, false, false, false)),
        ("Mirrored", "Sidebar right, preview pane left", new(true, true, false, true, false, true, false, true, false)),
        ("Focus", "Just the files: no sidebar, tabs or status bar", new(false, false, false, false, false, false, false, false, false)),
        ("Commander", "Status on top and a bar of shortcut keys along the bottom", new(true, false, false, true, false, true, true, false, true)),
        ("Bottom bar", "Toolbar and tabs along the bottom, like a dock", new(true, false, true, true, true, true, true, false, false))
    ];

    private void BuildLayoutPresets()
    {
        var current = CurrentLayout();
        _layoutPresets.Children.Clear();
        foreach (var (name, tip, layout) in LayoutPresets)
        {
            var chip = FileBackupUi.Chip(name, layout == current, () => TryChange(() => WithLayout(_document, layout)));
            chip.ToolTip = tip;
            _layoutPresets.Children.Add(chip);
        }
    }

    // ---------------------------------------------------------------- patterns (NEW, complex themes)

    // CHANGED (pattern maker): "Behind files" is gone. It was the file list's backdrop (ListFill), which sits inside
    // the file area and so looked the same as "File area"; the picture backdrop in Advanced still uses it.
    // The status bar is a place a pattern can go now.
    private static readonly (string Key, string Label)[] PatternTargets =
    [
        ("Base", "Window"), ("Part.Files.Fill", "File area"), ("Part.Sidebar.Fill", "Sidebar"), ("Part.Toolbar.Fill", "Toolbar"), ("Part.Status.Fill", "Status bar")
    ];

    // What a pattern sits on: the window and the file area are filled with their own colour first;
    // over the sidebar, the toolbar and the status bar the pattern is see-through.
    private Color? PatternBackground(string target) => target switch
    {
        "Base" => Solid("Base"), "Part.Files.Fill" => Solid("Surface"), _ => null
    };

    private void BuildPatternChoices()
    {
        _patternTargets.Children.Clear();
        foreach (var (key, label) in PatternTargets)
            _patternTargets.Children.Add(FileBackupUi.Chip(label, key == _patternTarget, () =>
            {
                _patternTarget = key; _pattern = null; _custom = null; BuildPatternChoices();
            }));

        _patternTiles.Children.Clear();
        var ink = _patternInk ?? Solid("Ink");
        var accent = Solid("Accent");
        var under = PatternBackground(_patternTarget) ?? Solid(_patternTarget == "Part.Status.Fill" ? "Surface" : "Base");
        var strength = Math.Max(.45, _patternStrength.Value);
        var none = new TextBlock { Text = "None", FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        none.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        _patternTiles.Children.Add(PatternTile(new Border { Child = none, Background = new SolidColorBrush(under) }, "None", false, RemovePattern));
        foreach (var kind in Enum.GetValues<StudioPattern>())
            _patternTiles.Children.Add(PatternTile(Sample(under, () => StudioPatterns.Pattern(kind, ink, accent, null, .8, strength)),
                StudioPatterns.Label(kind), _pattern == kind, () => ApplyPattern(kind)));

        // NEW (pattern maker): your own patterns, then a tile to draw a new one.
        foreach (var mine in StudioLibrary.Patterns())
        {
            var tile = PatternTile(Sample(under, () => StudioPatterns.Custom(mine, ink, accent, null, .8, strength)),
                mine.Name, _custom?.Name == mine.Name, () => ApplyCustom(mine));
            var menu = new ContextMenu();
            var edit = new MenuItem { Header = "Edit…" }; edit.Click += (_, _) => DrawPattern(mine);
            var remove = new MenuItem { Header = "Remove from My patterns" };
            remove.Click += (_, _) =>
            {
                try { var list = StudioLibrary.Patterns(); list.RemoveAll(p => p.Name == mine.Name); StudioLibrary.SavePatterns(list); BuildPatternChoices(); }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { Report("Could not remove it: " + e.Message, true); }
            };
            menu.Items.Add(edit); menu.Items.Add(remove);
            tile.ContextMenu = menu;
            tile.ToolTip = mine.Name + " · right-click to edit or remove";
            _patternTiles.Children.Add(tile);
        }
        var plus = new TextBlock { Text = "\uE70F", FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        plus.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        plus.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var draw = new Border { Child = plus };
        draw.SetResourceReference(Border.BackgroundProperty, "AccentWash");
        _patternTiles.Children.Add(PatternTile(draw, "Draw…", false, () => DrawPattern(null)));
    }

    // A small sample of a pattern for its tile. A pattern that will not draw shows its plain background.
    private static Border Sample(Color under, Func<System.Xml.Linq.XElement> pattern)
    {
        var sample = new Border { Background = new SolidColorBrush(under) };
        try { sample.Child = new Border { Background = (Brush)System.Windows.Markup.XamlReader.Parse(pattern().ToString()) }; }
        catch (Exception error) when (error is System.Windows.Markup.XamlParseException or InvalidCastException or System.Xml.XmlException or ArgumentException) { }
        return sample;
    }

    private static FrameworkElement PatternTile(Border sample, string name, bool chosen, Action pick)
    {
        sample.Width = 56; sample.Height = 38; sample.CornerRadius = new CornerRadius(6); sample.BorderThickness = new Thickness(chosen ? 2 : 1);
        sample.ClipToBounds = true;
        sample.SetResourceReference(Border.BorderBrushProperty, chosen ? "Accent" : "Line");
        if (sample.Child is Border inner) inner.CornerRadius = new CornerRadius(5);
        var stack = new StackPanel();
        stack.Children.Add(sample);
        stack.Children.Add(new TextBlock { Text = name, FontSize = 11, Margin = new Thickness(1, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 58 });
        var tile = StudioUi.ClickRow(stack, name == "None" ? "No pattern" : name + " pattern", pick);
        tile.Padding = new Thickness(3); tile.Margin = new Thickness(0, 0, 2, 2);
        return tile;
    }

    private void ApplyPattern(StudioPattern kind)
    {
        var fresh = _pattern != kind || _custom is not null;
        PutPattern(background => StudioPatterns.Pattern(kind, _patternInk ?? Solid("Ink"), Solid("Accent"), background, _patternSize.Value, _patternStrength.Value));
        _pattern = kind; _custom = null;
        if (fresh) BuildPatternChoices();
    }

    // NEW (pattern maker)
    private void ApplyCustom(StudioCustomPattern pattern)
    {
        var fresh = _custom != pattern;
        PutPattern(background => StudioPatterns.Custom(pattern, _patternInk ?? Solid("Ink"), Solid("Accent"), background, _patternSize.Value, _patternStrength.Value));
        _custom = pattern; _pattern = null;
        if (fresh) BuildPatternChoices();
    }

    private void ReapplyPattern()
    {
        if (_pattern is { } kind) ApplyPattern(kind);
        else if (_custom is { } mine) ApplyCustom(mine);
    }

    private void PutPattern(Func<Color?, System.Xml.Linq.XElement> make)
    {
        var target = _patternTarget;
        var background = PatternBackground(target);
        TryChange(() =>
        {
            var next = _document.Paint(target, make(background));
            // The window background is a pattern now, so code that needs one flat colour reads Theme.BaseColor.
            return target == "Base" && background is { } flat ? next.Color("Theme.BaseColor", StudioUi.Hex(flat, opaque: true)) : next;
        });
    }

    // NEW (pattern maker): draw a pattern (or change one of yours) and put it on the chosen place.
    private void DrawPattern(StudioCustomPattern? start)
    {
        var under = PatternBackground(_patternTarget) ?? Solid(_patternTarget == "Part.Status.Fill" ? "Surface" : "Base");
        var maker = new StudioPatternWindow(this, _patternInk ?? Solid("Ink"), Solid("Accent"), under, start);
        if (maker.ShowDialog() != true || maker.Result is not { } drawn) { BuildPatternChoices(); return; }
        _custom = null;
        ApplyCustom(drawn);
        BuildPatternChoices();
    }

    private void RemovePattern()
    {
        var target = _patternTarget;
        TryChange(() => target switch
        {
            "Base" => _document.Color("Base", StudioUi.Hex(Solid("Base"), opaque: true)),
            _ => _document.Without(target)
        });
        _pattern = null; _custom = null; BuildPatternChoices();
    }

    private void PickPatternInk()
    {
        var picker = new StudioColorWindow(this, StudioUi.Hex(_patternInk ?? Solid("Ink")), false, null, "Pattern color");
        if (picker.ShowDialog() != true) return;
        _patternInk = Parse(picker.SelectedColor);
        _patternInkSwatch.Background = new SolidColorBrush(_patternInk.Value);
        ReapplyPattern();
        BuildPatternChoices();
    }

    private void ShowPatternValues()
    {
        _patternSizeValue.Text = $"{_patternSize.Value * 100:0}%";
        _patternStrengthValue.Text = $"{_patternStrength.Value * 100:0}%";
    }

    // ---------------------------------------------------------------- effects (NEW, complex themes)

    private void BuildEffectChips()
    {
        _overlayChips.Children.Clear(); _shadeChips.Children.Clear();
        var hasOverlay = On("OverlayVisibility");
        var hasShade = Value("EdgeShade") is Brush shade && !(shade is SolidColorBrush { Color.A: 0 });
        if (_overlay is null && hasOverlay) _overlayChips.Children.Add(FileBackupUi.Chip("Theme's own", true, () => { }));
        foreach (var kind in Enum.GetValues<StudioOverlay>())
        {
            var chosen = _overlay is { } picked ? picked == kind : kind == StudioOverlay.None && !hasOverlay;
            _overlayChips.Children.Add(FileBackupUi.Chip(StudioPatterns.Label(kind), chosen, () => { _overlay = kind; ApplyEffects(); BuildEffectChips(); }));
        }
        if (_shade is null && hasShade) _shadeChips.Children.Add(FileBackupUi.Chip("Theme's own", true, () => { }));
        foreach (var kind in Enum.GetValues<StudioShade>())
        {
            var chosen = _shade is { } picked ? picked == kind : kind == StudioShade.None && !hasShade;
            _shadeChips.Children.Add(FileBackupUi.Chip(StudioPatterns.Label(kind), chosen, () => { _shade = kind; ApplyEffects(); BuildEffectChips(); }));
        }
    }

    // Only the effect that was picked here is rewritten; a theme's own scanlines or shade stay until replaced.
    private void ApplyEffects()
    {
        var dark = StudioUi.TextOn(Solid("Base")) == Colors.White;
        var strength = _effectStrength.Value;
        var accent = Solid("Accent");
        TryChange(() =>
        {
            var next = _document;
            if (_overlay is { } overlay)
                next = StudioPatterns.Overlay(overlay, dark, strength) is { } fill
                    ? next.Paint("OverlayFill", fill).Layout("OverlayVisibility", 1)
                    : next.Without("OverlayFill").Layout("OverlayVisibility", 0);
            if (_shade is { } kind)
                next = StudioPatterns.Shade(kind, accent, strength) is { } shade ? next.Paint("EdgeShade", shade) : next.Without("EdgeShade");
            return next;
        });
    }

    private void SetTilt(bool on)
    {
        if (_syncing) return;
        TryChange(() => on ? _document.Tilt(-1.4) : _document.Without("NoteTilt"));
    }

    // ---------------------------------------------------------------- parts (NEW, complex themes)

    private static readonly (string Id, string Label)[] Parts = [("Sidebar", "Sidebar"), ("Toolbar", "Toolbar"), ("Files", "File area"), ("Status", "Status bar")];

    private void BuildPartChoices()
    {
        _partChips.Children.Clear();
        foreach (var (id, label) in Parts)
            _partChips.Children.Add(FileBackupUi.Chip(label, id == _part, () => { _part = id; BuildPartChoices(); Sync(); }));
        _partColorRows.Children.Clear(); _partCells.Clear();
        _partColorRows.Margin = new Thickness(0, 4, 0, 0);
        var name = Parts.First(p => p.Id == _part).Label;
        _partColorRows.Children.Add(ColorRow($"Part.{_part}.Fill", "Fill", _partCells));
        _partColorRows.Children.Add(ColorRow($"Part.{_part}.Edge", _part == "Status" ? "Line" : "Outline", _partCells));
        _partShape.Visibility = _part == "Status" ? Visibility.Collapsed : Visibility.Visible;
        _partChips.ToolTip = "Style the " + name.ToLowerInvariant() + " on its own";
        SyncColors();
    }

    private void SyncPart()
    {
        if (_part == "Status") return;
        _partOutline.Value = Value($"Part.{_part}.EdgeWidth") is Thickness edge ? Math.Clamp(edge.Left, 0, 4) : 0;
        _partCorner.Value = Value($"Part.{_part}.Corner") is CornerRadius corner ? Math.Clamp(corner.TopLeft, 0, 24) : 0;
        _partOutlineValue.Text = $"{_partOutline.Value:0.#} px";
        _partCornerValue.Text = $"{_partCorner.Value:0} px";
    }

    // ---------------------------------------------------------------- surprise (NEW, complex themes)

    // A mood, and now and then a pattern on the window, a ready layout or a shade, all in one undo step.
    private ThemeStudioDocument Surprise(ThemeDesign design)
    {
        ThemeDesign.TryColor(design.Background, out var background);
        ThemeDesign.TryColor(design.Accent, out var accent);
        var ink = StudioUi.TextOn(background);
        var next = ThemeStudioDocument.FromDesign(design);
        var dice = Random.Shared;
        if (dice.NextDouble() < .5)
        {
            var patterns = Enum.GetValues<StudioPattern>();
            var kind = patterns[dice.Next(patterns.Length)];
            next = next.Paint("Base", StudioPatterns.Pattern(kind, ink, accent, background, .8 + dice.NextDouble() * .8, .08 + dice.NextDouble() * .12))
                .Color("Theme.BaseColor", StudioUi.Hex(background, opaque: true));
        }
        if (dice.NextDouble() < .35) next = WithLayout(next, LayoutPresets[dice.Next(LayoutPresets.Length)].Layout);
        if (dice.NextDouble() < .35 && StudioPatterns.Shade((StudioShade)(1 + dice.Next(4)), accent, .5) is { } shade) next = next.Paint("EdgeShade", shade);
        _overlay = null; _shade = null; _pattern = null; _custom = null; _edge = null; _sticker = null;
        return next;
    }

    // ---------------------------------------------------------------- click to change (NEW, everything)

    // Finds what was clicked in the preview, walks up from it, and offers every theme colour and font the
    // parts on the way are drawn with (by matching the very brush and font objects the theme handed out).
    private void PickFromPreview(MouseButtonEventArgs e)
    {
        if (_isolatedPreview is null || _closing) return;
        e.Handled = true;
        var explorer = _isolatedPreview.Explorer;
        var point = e.GetPosition(explorer);
        HitTestResult? hit;
        explorer.IsHitTestVisible = true;
        try { hit = VisualTreeHelper.HitTest(explorer, point); }
        finally { explorer.IsHitTestVisible = false; }
        // The preview loads its own copy of the draft, so its brushes are the ones to match against.
        var own = _isolatedPreview.Theme ?? (_values ??= _document.Resources());
        var below = _isolatedPreview.Defaults;
        var found = new List<string>();
        DependencyObject? node = hit?.VisualHit;
        for (var depth = 0; node is not null && depth < 16 && found.Count < 9; depth++)
        {
            foreach (var value in ValuesOn(node))
                foreach (var key in KeysFor(value, own, below))
                    if (!found.Contains(key)) found.Add(key);
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Items.Add(new MenuItem { Header = found.Count == 0 ? "Nothing to change here" : "Change", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        foreach (var key in found)
        {
            ThemeKeys.TryGetKind(key, out var kind);
            var label = SettingLabel(key);
            var item = new MenuItem { Header = label, ToolTip = key };
            if (kind == ThemeValue.Font)
            {
                item.Icon = new TextBlock { Text = "Aa", FontFamily = Value(key) as FontFamily ?? FontFamily };
                item.Click += (_, _) => ShowFonts(_previewFrame!, key);
            }
            else
            {
                var (outer, inner) = StudioUi.Swatch(16);
                inner.Background = Value(key) switch { Brush b => b, Color c => new SolidColorBrush(c), _ => Brushes.Transparent };
                item.Icon = outer;
                item.Click += (_, _) => PickColor(key, label);
            }
            menu.Items.Add(item);
        }
        menu.PlacementTarget = _previewFrame;
        menu.IsOpen = true;
    }

    private static IEnumerable<object?> ValuesOn(DependencyObject node)
    {
        switch (node)
        {
            case Border b: yield return b.Background; yield return b.BorderBrush; break;
            case TextBlock t: yield return t.Foreground; yield return t.Background; yield return t.FontFamily; break;
            case System.Windows.Shapes.Shape shape: yield return shape.Fill; yield return shape.Stroke; break;
            case Control c: yield return c.Background; yield return c.Foreground; yield return c.BorderBrush; yield return c.FontFamily; break;
            case Panel p: yield return p.Background; break;
        }
    }

    private static IEnumerable<string> KeysFor(object? value, ResourceDictionary own, ResourceDictionary below)
    {
        if (value is null) yield break;
        foreach (var key in own.Keys.OfType<string>())
            if (ReferenceEquals(own[key], value) && ThemeKeys.TryGetKind(key, out var kind) && kind is not ThemeValue.Corner) yield return key;
        foreach (var dictionary in below.MergedDictionaries)
            foreach (var key in dictionary.Keys.OfType<string>())
                if (!own.Contains(key) && ReferenceEquals(dictionary[key], value) && ThemeKeys.TryGetKind(key, out _)) yield return key;
    }

    // ---------------------------------------------------------------- every setting (NEW, everything)

    private static readonly Dictionary<string, string> SettingNames = new(StringComparer.Ordinal)
    {
        ["UIFont"] = "Interface font", ["SerifFont"] = "File name font", ["DisplayFont"] = "Heading font", ["MonoFont"] = "Path & code font",
        ["RPlayerBar"] = "Music player corners", ["PageInset"] = "Room down the left edge", ["NoteTilt"] = "Tooltip lean (degrees)",
        ["InterfaceScale"] = "Interface size (1 = 100%)", ["OverlayVisibility"] = "Show the screen overlay",
        ["FunctionKeysVisibility"] = "Show the shortcut key bar", ["BindingVisibility"] = "Show the notebook binding",
        ["Explorer.SidebarWidth"] = "Sidebar width", ["Explorer.RowPadding"] = "Row spacing",
        ["Explorer.SidebarOnRight"] = "Sidebar on the right", ["Explorer.ToolbarBottom"] = "Toolbar at the bottom",
        ["Explorer.TabsVisibility"] = "Show tabs", ["Explorer.SidebarVisibility"] = "Show the sidebar",
        ["Explorer.TabsBelowToolbar"] = "Tabs below the toolbar", ["Explorer.StatusBarVisibility"] = "Show the status bar",
        ["Explorer.StatusBarTop"] = "Status bar above the files", ["Explorer.PreviewOnLeft"] = "Preview pane on the left",
        ["Part.Sidebar.EdgeWidth"] = "Sidebar outline width", ["Part.Toolbar.EdgeWidth"] = "Toolbar outline width", ["Part.Files.EdgeWidth"] = "File area outline width",
        ["Part.Sidebar.Corner"] = "Sidebar corners", ["Part.Toolbar.Corner"] = "Toolbar corners", ["Part.Files.Corner"] = "File area corners"
    };

    private static string SettingLabel(string key)
    {
        if (StudioColorOptions.All.FirstOrDefault(o => o.Key == key) is { Label: { } label }) return label;
        if (SettingNames.TryGetValue(key, out var name)) return name;
        return key.Length is > 1 and <= 4 && key[0] == 'R' && key[1..].All(char.IsDigit) ? $"Corner size {key[1..]}" : key;
    }

    private static string SettingGroup(string key, ThemeValue kind)
    {
        if (StudioColorOptions.All.FirstOrDefault(o => o.Key == key) is { Group: { } group }) return group;
        return kind switch
        {
            ThemeValue.Font => "Fonts",
            ThemeValue.Corner => key.StartsWith("Part.", StringComparison.Ordinal) ? "Parts" : "Corners",
            ThemeValue.Thickness when key.StartsWith("Part.", StringComparison.Ordinal) => "Parts",
            ThemeValue.Visibility or ThemeValue.Thickness when key.StartsWith("Explorer.", StringComparison.Ordinal) || key == "FunctionKeysVisibility" => "Layout",
            _ => "Effects & size"
        };
    }

    private void BuildEverySetting()
    {
        _everyRows.Children.Clear(); _everyRefresh.Clear(); _everyCells.Clear();
        var filter = _everyFilter.Text.Trim();
        var keys = ThemeKeys.All.Keys.Concat(StudioColorOptions.All.Select(o => o.Key)).Distinct()
            .Where(key => !key.StartsWith("Decor.", StringComparison.Ordinal)) // stickers have their own card
            .Select(key => (Key: key, Kind: ThemeKeys.TryGetKind(key, out var kind) ? kind : ThemeValue.Brush))
            .Select(k => (k.Key, k.Kind, Label: SettingLabel(k.Key), Group: SettingGroup(k.Key, k.Kind)))
            .Where(k => filter.Length == 0 || k.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) || k.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || k.Group.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var group in keys.GroupBy(k => k.Group))
        {
            var heading = new TextBlock { Text = group.Key, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, _everyRows.Children.Count == 0 ? 0 : 10, 0, 2) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
            _everyRows.Children.Add(heading);
            foreach (var (key, kind, label, _) in group) _everyRows.Children.Add(SettingRow(key, kind, label));
        }
        if (keys.Length == 0) _everyRows.Children.Add(Small("No setting matches “" + filter + "”."));
        _syncing = true;
        foreach (var refresh in _everyRefresh) refresh();
        _syncing = false;
        SyncColors();
    }

    private FrameworkElement SettingRow(string key, ThemeValue kind, string label)
    {
        switch (kind)
        {
            case ThemeValue.Brush or ThemeValue.FlatColor or ThemeValue.Color:
                return ColorRow(key, label, _everyCells);
            case ThemeValue.Font:
            {
                var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                _everyRefresh.Add(() => value.Text = FontLabel(key));
                return MenuRow(label, null, target => ShowFonts(target, key), value);
            }
            case ThemeValue.Visibility:
            {
                var check = FileBackupUi.Tick(label, key);
                check.Margin = new Thickness(8, 5, 0, 5);
                check.Checked += (_, _) => { if (!_syncing) TryChange(() => _document.Write(key, "Visible")); };
                check.Unchecked += (_, _) => { if (!_syncing) TryChange(() => _document.Write(key, "Collapsed")); };
                _everyRefresh.Add(() => check.IsChecked = Value(key) is Visibility.Visible);
                return check;
            }
            default:
            {
                var box = new TextBox { Width = 96 };
                AutomationProperties.SetName(box, label);
                box.ToolTip = kind switch
                {
                    ThemeValue.Corner => "One number for every corner, or four: top-left, top-right, bottom-right, bottom-left",
                    ThemeValue.Thickness => "One number, or four: left, top, right, bottom",
                    _ => key == "InterfaceScale" ? "1 is 100%; 1.25 is 125%" : "Degrees; 0 is straight"
                };
                void Commit()
                {
                    if (box.Text.Trim() == Describe(Value(key))) return;
                    TryChange(() => _document.Write(key, box.Text));
                }
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Commit(); } };
                box.LostFocus += (_, _) => Commit();
                _everyRefresh.Add(() => { if (!box.IsKeyboardFocusWithin) box.Text = Describe(Value(key)); });
                var row = new DockPanel { Margin = new Thickness(8, 3, 0, 3) };
                var frame = FileBackupUi.Framed(box); frame.Padding = new Thickness(8, 3, 8, 3);
                DockPanel.SetDock(frame, Dock.Right); row.Children.Add(frame);
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                return row;
            }
        }
    }

    private static string Describe(object? value)
    {
        static string N(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return value switch
        {
            CornerRadius c when c.TopLeft == c.TopRight && c.TopLeft == c.BottomRight && c.TopLeft == c.BottomLeft => N(c.TopLeft),
            CornerRadius c => $"{N(c.TopLeft)},{N(c.TopRight)},{N(c.BottomRight)},{N(c.BottomLeft)}",
            Thickness t when t.Left == t.Top && t.Left == t.Right && t.Left == t.Bottom => N(t.Left),
            Thickness t => $"{N(t.Left)},{N(t.Top)},{N(t.Right)},{N(t.Bottom)}",
            RotateTransform r => N(r.Angle),
            ScaleTransform s => N(s.ScaleX),
            _ => ""
        };
    }

    // ---------------------------------------------------------------- about this theme (NEW, everything)

    private void SetDetails()
    {
        if (_syncing) return;
        var m = _document.Manifest;
        var next = m with
        {
            Author = ThemeManifest.Tidy(_author.Text, 60), Description = ThemeManifest.Tidy(_description.Text, 200),
            Version = ThemeManifest.Tidy(_version.Text, 20) is { Length: > 0 } v ? v : "1.0",
            EInk = _eink.IsChecked == true, Experimental = _experimental.IsChecked == true
        };
        if (next != m) Change(_document with { Manifest = next });
    }

    // ---------------------------------------------------------------- start from a picture or a theme (NEW)

    private FrameworkElement GlyphTile(string glyph, string name, string tip, Action pick)
    {
        var mark = new TextBlock { Text = glyph, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        mark.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        mark.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var swatch = new Border { Width = 92, Height = 52, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = mark };
        swatch.SetResourceReference(Border.BackgroundProperty, "AccentWash");
        swatch.SetResourceReference(Border.BorderBrushProperty, "Line");
        var tile = Tile(swatch, name, pick);
        tile.ToolTip = tip;
        return tile;
    }

    private void FromPictureFile()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose a picture to build a theme from", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        FromPicture(picker.FileName, "your picture");
    }

    private void FromWallpaper()
    {
        if (StudioPicture.WallpaperPath() is not { } path) { Report("Windows did not say which picture is your wallpaper (it may be a solid color).", true); return; }
        FromPicture(path, "your wallpaper");
    }

    // The picture's colours become the background and the accent; the picture itself goes behind the panels,
    // which become slightly see-through so it shows round the edges and faintly behind the files.
    private void FromPicture(string path, string what)
    {
        try
        {
            if (new System.IO.FileInfo(path).Length > 16 * 1024 * 1024) { Report("Choose a picture smaller than 16 MB.", true); return; }
            var bytes = System.IO.File.ReadAllBytes(path);
            var palette = StudioPicture.Read(StudioPicture.Load(bytes));
            var next = ThemeStudioDocument.FromDesign(new ThemeDesign(StudioPicture.Hex(palette.Background), StudioPicture.Hex(palette.Accent)));
            var surface = ((SolidColorBrush)next.Resources()["Surface"]).Color;
            next = next.Sticker("Decor.Back1", bytes, new Rect(0, 0, 1, 1), palette.Dark ? .5 : .4, cover: true)
                .Color("Part.Files.Fill", StudioUi.Hex(Color.FromArgb(0xEB, surface.R, surface.G, surface.B)))
                .Color("Part.Sidebar.Fill", StudioUi.Hex(Color.FromArgb(0xA6, palette.Background.R, palette.Background.G, palette.Background.B)))
                .Color("Part.Toolbar.Fill", StudioUi.Hex(Color.FromArgb(0xA6, palette.Background.R, palette.Background.G, palette.Background.B)));
            _overlay = null; _shade = null; _pattern = null; _custom = null; _edge = null; _sticker = null;
            Change(next);
            _sticker = "Decor.Back1"; Sync(); // NEW (sticker editor): selected, so it can be cropped or moved right away
            Report($"Built from {what}. The picture is behind the panels: Edit picture… under Stickers crops it; remove it there to keep just the colors.");
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException or ThemeFormatException or InvalidOperationException)
        { Report("That picture could not be used: " + error.Message, true); }
    }

    private void ShowYourThemes()
    {
        var menu = new ContextMenu { MaxHeight = 460, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        foreach (var theme in ThemeService.Themes.OrderBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new MenuItem { Header = theme.Label };
            item.Click += (_, _) =>
            {
                try
                {
                    var opened = ThemeStudioDocument.Open(theme);
                    _overlay = null; _shade = null; _pattern = null; _custom = null; _edge = null; _sticker = null;
                    Change(opened);
                    Report($"Started from {theme.Label}. Saving makes a new theme; {theme.Label} stays as it is.");
                }
                catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException or ThemeFormatException or NotSupportedException or FormatException or InvalidOperationException)
                { Report($"{theme.Label} could not be opened: {error.Message}", true); }
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---------------------------------------------------------------- edge strips (NEW)

    private void BuildEdgeChips()
    {
        _edgeChips.Children.Clear();
        var has = On("BindingVisibility");
        if (_edge is null && has) _edgeChips.Children.Add(FileBackupUi.Chip("Theme's own", true, () => { }));
        foreach (var (id, label) in new[] { ("None", "None"), ("Spiral", "Notebook spiral"), ("Tape", "Warning tape"), ("Pattern", "Your pattern") })
        {
            var chosen = _edge is { } picked ? picked == id : id == "None" && !has;
            var chip = FileBackupUi.Chip(label, chosen, () => { _edge = id; SetEdge(id); BuildEdgeChips(); });
            if (id == "Pattern") chip.ToolTip = "The pattern picked under Patterns (or stripes), as a strip";
            _edgeChips.Children.Add(chip);
        }
    }

    private void SetEdge(string kind)
    {
        var dark = StudioUi.TextOn(Solid("Base")) == Colors.White;
        var accent = Solid("Accent");
        TryChange(() => kind switch
        {
            "Spiral" => _document.Paint("BindingFill", StudioPatterns.Binding(dark)).Layout("BindingVisibility", 1).Spacing("PageInset", 30, 0, 0, 0),
            "Tape" => _document.Paint("BindingFill", StudioPatterns.Tape(accent, Color.FromRgb(12, 12, 12))).Layout("BindingVisibility", 1).Spacing("PageInset", 10, 0, 0, 0),
            "Pattern" => _document.Paint("BindingFill", _custom is { } mine
                    ? StudioPatterns.Custom(mine, _patternInk ?? Solid("Ink"), accent, Solid("Base"), .8, .8)
                    : StudioPatterns.Pattern(_pattern ?? StudioPattern.Stripes, _patternInk ?? Solid("Ink"), accent, Solid("Base"), .8, .6))
                .Layout("BindingVisibility", 1).Spacing("PageInset", 30, 0, 0, 0),
            _ => _document.Without("BindingFill", "PageInset").Layout("BindingVisibility", 0)
        });
    }

    // ---------------------------------------------------------------- stickers (NEW)

    private StudioSticker? CurrentSticker() => _sticker is { } key ? _document.Stickers().FirstOrDefault(s => s.Key == key) : null;

    private void AddSticker()
    {
        var stickers = _document.Stickers();
        var free = ThemeStudioDocument.FrontStickers.Concat(ThemeStudioDocument.BackStickers).FirstOrDefault(k => stickers.All(s => s.Key != k));
        if (free is null) { Report("A theme holds up to eight stickers. Remove one to add another.", true); return; }
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose a picture for a sticker", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new System.IO.FileInfo(picker.FileName).Length > 16 * 1024 * 1024) { Report("Choose a picture smaller than 16 MB.", true); return; }
            var bytes = System.IO.File.ReadAllBytes(picker.FileName);
            // CHANGED (sticker editor): the picture opens in the editor first, to cut out its background or crop it.
            var editor = new StudioImageWindow(this, bytes, fillsWindow: false, WindowAspect());
            if (editor.ShowDialog() != true || editor.Result is not { } edited) return;
            Change(_document.Sticker(free, edited, new Rect(.62, .52, .36, .46), 1));
            _sticker = free;
            Sync();
            Report("Sticker added. Drag it in the preview to move it.");
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException or ThemeFormatException or InvalidOperationException)
        { Report("That picture could not be used: " + error.Message, true); }
    }

    // NEW (sticker editor)
    private void EditSticker()
    {
        if (CurrentSticker() is not { } sticker || _document.StickerImage(sticker.Image) is not { } bytes) return;
        try
        {
            var fills = sticker.Cover || Same(sticker.Box, new Rect(0, 0, 1, 1));
            var editor = new StudioImageWindow(this, bytes, fills, WindowAspect());
            if (editor.ShowDialog() != true || editor.Result is not { } edited) return;
            TryChange(() => _document.ReplaceStickerImage(sticker.Key, edited));
            Report("Picture updated.");
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or FormatException or InvalidOperationException or System.IO.IOException)
        { Report("That picture could not be edited: " + error.Message, true); }
    }

    // The shape of the Clearspace window, for cropping a picture that fills it.
    private static double WindowAspect() =>
        Application.Current?.MainWindow is { ActualWidth: > 100, ActualHeight: > 100 } main ? main.ActualWidth / main.ActualHeight : 1264d / 760;

    private void BuildStickers()
    {
        var stickers = _document.Stickers();
        if (_sticker is { } chosen && stickers.All(s => s.Key != chosen)) _sticker = null;
        _stickerList.Children.Clear();
        foreach (var sticker in stickers)
        {
            var thumb = new Border { Width = 44, Height = 32, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Background = StudioUi.Checker, ClipToBounds = true };
            thumb.SetResourceReference(Border.BorderBrushProperty, "Line");
            if (_document.StickerImage(sticker.Image) is { } bytes)
            {
                try { thumb.Child = new Border { Background = new ImageBrush(StudioPicture.Load(bytes)) { Stretch = Stretch.Uniform } }; }
                catch (Exception e) when (e is NotSupportedException or ArgumentException or System.IO.IOException or InvalidOperationException) { }
            }
            var row = new DockPanel();
            DockPanel.SetDock(thumb, Dock.Left); row.Children.Add(thumb);
            var words = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var number = Array.IndexOf(ThemeStudioDocument.FrontStickers.Concat(ThemeStudioDocument.BackStickers).ToArray(), sticker.Key) + 1;
            words.Children.Add(new TextBlock { Text = "Sticker " + number, FontWeight = sticker.Key == _sticker ? FontWeights.SemiBold : FontWeights.Normal });
            words.Children.Add(Small(sticker.Front ? "In front of everything" : "Behind the panels"));
            row.Children.Add(words);
            var key = sticker.Key;
            var clickable = StudioUi.ClickRow(row, "Sticker " + number, () => { _sticker = _sticker == key ? null : key; Sync(); });
            clickable.Padding = new Thickness(6, 4, 6, 4);
            if (key == _sticker) clickable.SetResourceReference(Border.BackgroundProperty, "AccentWash");
            _stickerList.Children.Add(clickable);
        }
        var selected = CurrentSticker();
        _stickerControls.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        if (selected is not null)
        {
            _stickerLayerChips.Children.Clear();
            _stickerLayerChips.Children.Add(FileBackupUi.Chip("In front", selected.Front, () => Relayer(front: true)));
            _stickerLayerChips.Children.Add(FileBackupUi.Chip("Behind the panels", !selected.Front, () => Relayer(front: false)));
            _stickerPlaceChips.Children.Clear();
            foreach (var (name, box) in new[]
            {
                ("Bottom right", new Rect(.62, .52, .36, .46)), ("Bottom left", new Rect(.02, .52, .36, .46)),
                ("Top right", new Rect(.62, .02, .36, .46)), ("Top left", new Rect(.02, .02, .36, .46)),
                ("Middle", new Rect(.3, .25, .4, .5)), ("Whole window", new Rect(0, 0, 1, 1))
            })
                _stickerPlaceChips.Children.Add(FileBackupUi.Chip(name, Same(selected.Box, box), () => TryChange(() => _document.PlaceSticker(selected.Key, box, _stickerOpacity.Value))));
            _stickerOpacity.Value = Math.Clamp(selected.Opacity, .05, 1);
            _stickerOpacityValue.Text = $"{_stickerOpacity.Value * 100:0}%";
        }
        PlaceStickerHandle();
    }

    private static bool Same(Rect a, Rect b) => Math.Abs(a.X - b.X) < .005 && Math.Abs(a.Y - b.Y) < .005 && Math.Abs(a.Width - b.Width) < .005 && Math.Abs(a.Height - b.Height) < .005;

    private void Relayer(bool front)
    {
        if (CurrentSticker() is not { } sticker || sticker.Front == front) return;
        var stickers = _document.Stickers();
        var target = (front ? ThemeStudioDocument.FrontStickers : ThemeStudioDocument.BackStickers).FirstOrDefault(k => stickers.All(s => s.Key != k));
        if (target is null) { Report(front ? "There is no room in front: four stickers are there already." : "There is no room behind: four stickers are there already.", true); return; }
        TryChange(() => _document.MoveSticker(sticker.Key, target));
        _sticker = target; Sync();
    }

    private void BuildStickerHandle()
    {
        var outline = new System.Windows.Shapes.Rectangle { StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 }, RadiusX = 4, RadiusY = 4 };
        outline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent");
        outline.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentWash");
        _stickerGrip.SetResourceReference(Border.BackgroundProperty, "Accent");
        _stickerGrip.ToolTip = "Drag to resize";
        _stickerHandle.Children.Add(outline);
        _stickerHandle.Children.Add(_stickerGrip);
        _stickerHandle.ToolTip = "Drag to move this sticker";
        _stickerLayer.Children.Add(_stickerHandle);
        _stickerHandle.MouseLeftButtonDown += (_, e) => BeginStickerDrag(e, resize: ReferenceEquals(e.OriginalSource, _stickerGrip) || IsInside(_stickerGrip, e));
        _stickerHandle.MouseMove += (_, e) => DragSticker(e);
        _stickerHandle.MouseLeftButtonUp += (_, e) => EndStickerDrag(e);
        _stickerHandle.LostMouseCapture += (_, _) => { if (_dragging) { _dragging = false; PlaceStickerHandle(); } };
    }

    private static bool IsInside(FrameworkElement element, MouseEventArgs e)
    {
        var p = e.GetPosition(element);
        return p.X >= 0 && p.Y >= 0 && p.X <= element.ActualWidth && p.Y <= element.ActualHeight;
    }

    // Where the sample window is drawn inside the preview, in the handle layer's coordinates.
    private Rect? WindowArea()
    {
        if (_isolatedPreview is not { } preview || !preview.Explorer.IsVisible || !_stickerLayer.IsVisible) return null;
        try
        {
            var explorer = preview.Explorer;
            return explorer.TransformToVisual(_stickerLayer).TransformBounds(new Rect(0, 0, explorer.ActualWidth, explorer.ActualHeight));
        }
        catch (InvalidOperationException) { return null; }
    }

    private void PlaceStickerHandle()
    {
        if (_dragging) return;
        if (CurrentSticker() is not { } sticker || WindowArea() is not { } area) { _stickerHandle.Visibility = Visibility.Collapsed; return; }
        ShowHandle(sticker.Box, area);
    }

    private void ShowHandle(Rect box, Rect area)
    {
        _stickerHandle.Visibility = Visibility.Visible;
        Canvas.SetLeft(_stickerHandle, area.X + box.X * area.Width);
        Canvas.SetTop(_stickerHandle, area.Y + box.Y * area.Height);
        _stickerHandle.Width = Math.Max(16, box.Width * area.Width);
        _stickerHandle.Height = Math.Max(16, box.Height * area.Height);
    }

    private void BeginStickerDrag(MouseButtonEventArgs e, bool resize)
    {
        if (CurrentSticker() is not { } sticker) return;
        _dragging = true; _dragResize = resize;
        _dragStart = e.GetPosition(_stickerLayer);
        _dragStartBox = _dragBox = sticker.Box;
        _stickerHandle.CaptureMouse();
        e.Handled = true;
    }

    private void DragSticker(MouseEventArgs e)
    {
        if (!_dragging || WindowArea() is not { } area || area.Width <= 0 || area.Height <= 0) return;
        var p = e.GetPosition(_stickerLayer);
        var dx = (p.X - _dragStart.X) / area.Width; var dy = (p.Y - _dragStart.Y) / area.Height;
        var b = _dragStartBox;
        _dragBox = _dragResize
            ? new Rect(b.X, b.Y, Math.Max(.03, b.Width + dx), Math.Max(.03, b.Height + dy))
            : new Rect(b.X + dx, b.Y + dy, b.Width, b.Height);
        ShowHandle(_dragBox, area);
    }

    private void EndStickerDrag(MouseButtonEventArgs e)
    {
        e.Handled = true; // so the click does not also open the "Change" menu of the preview
        if (!_dragging) return;
        _dragging = false;
        _stickerHandle.ReleaseMouseCapture();
        if (_sticker is { } key && !Same(_dragBox, _dragStartBox)) TryChange(() => _document.PlaceSticker(key, _dragBox, _stickerOpacity.Value));
        PlaceStickerHandle();
    }

    // ---------------------------------------------------------------- custom layouts (NEW)

    private void OpenLayoutBuilder()
    {
        var builder = new StudioLayoutBuilderWindow(this, _document);
        if (builder.ShowDialog() != true) return;
        var result = builder.Result;
        TryChange(() => (builder.Document with { LayoutJson = result }).DropUnusedImages());
        Report(result is null ? "Back to the usual layout." : "Custom layout in use. Save to keep it.");
    }

    // ---------------------------------------------------------------- readability

    // NEW (theme studio, round 2): how well the three text colours read on the page and in the file area.
    // 4.5:1 is the usual line for small text; below 3:1 is hard for almost anyone.
    private void UpdateReadability()
    {
        _readability.Children.Clear();
        var page = Solid("Base"); var surface = Solid("Surface");
        var low = false;
        foreach (var (key, label) in new[] { ("Ink", "File names & main text"), ("InkMuted", "Dates, sizes & status"), ("InkFaint", "Headings & hints") })
        {
            var text = Solid(key);
            var ratio = Math.Min(ThemeDesign.Contrast(text, page), ThemeDesign.Contrast(text, surface));
            var (word, tone) = ratio >= 4.5 ? ("Good", "Good") : ratio >= 3 ? ("Low", "Warn") : ("Too faint", "Danger");
            low |= ratio < 4.5;
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var verdict = new TextBlock { Text = $"{ratio:0.0}:1  ·  {word}", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            verdict.SetResourceReference(TextBlock.ForegroundProperty, tone);
            DockPanel.SetDock(verdict, Dock.Right); row.Children.Add(verdict);
            var sample = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(text) };
            var chip = new Border { Child = sample, Padding = new Thickness(8, 3, 8, 3), CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(surface), HorizontalAlignment = HorizontalAlignment.Left };
            row.Children.Add(chip);
            row.ToolTip = $"{label}: {ratio:0.0}:1 against the window and the file area";
            _readability.Children.Add(row);
        }
        _fixReadability.Visibility = low ? Visibility.Visible : Visibility.Collapsed;
    }

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

    // ---------------------------------------------------------------- fonts, icons, files

    private void ImportFont()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Add a font to your Clearspace library", Filter = "Font files|*.ttf;*.otf" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new System.IO.FileInfo(picker.FileName).Length > 16 * 1024 * 1024) throw new ArgumentException("Choose a font smaller than 16 MB.");
            var name = StudioLibrary.ImportFont(System.IO.File.ReadAllBytes(picker.FileName), System.IO.Path.GetExtension(picker.FileName));
            Report(name + " added. Pick it from any font list.");
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException or ThemeFormatException)
        { Report("Could not add that font: " + error.Message, true); }
    }

    private void ImportIcon()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose a custom icon", Filter = "Icon images|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.gif" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new System.IO.FileInfo(picker.FileName).Length > 16 * 1024 * 1024) throw new ArgumentException("Choose an icon smaller than 16 MB.");
            Change(_document.FileIcon(_iconTarget.Text, _iconExtension, System.IO.File.ReadAllBytes(picker.FileName)));
            Report("Icon added for " + _iconTarget.Text.Trim() + ".");
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException or ThemeFormatException)
        { Report("Could not use that icon: " + error.Message, true); }
    }

    private void BuildMatchChips()
    {
        _matchChips.Children.Clear();
        _matchChips.Margin = new Thickness(0, 4, 0, 2);
        _matchChips.Children.Add(FileBackupUi.Chip("File type", _iconExtension, () => SetMatch(true)));
        _matchChips.Children.Add(FileBackupUi.Chip("File name", !_iconExtension, () => SetMatch(false)));
        _iconTarget.ToolTip = _iconExtension ? "A file type such as .pdf, or folder for all folders" : "A file name such as Notes.txt, in any folder";
    }

    private void SetMatch(bool extension)
    {
        if (_iconExtension == extension) return;
        _iconExtension = extension;
        _iconTarget.Text = extension ? ".pdf" : "Notes.txt";
        BuildMatchChips();
    }

    private void ShowIconStyles(FrameworkElement target)
    {
        var menu = new ContextMenu();
        foreach (var icon in Enum.GetValues<IconStyle>())
        {
            var item = new MenuItem { Header = IconLabel(icon), IsCheckable = true, IsChecked = _document.Manifest.Icons == icon };
            item.Click += (_, _) => Change(_document with { Manifest = _document.Manifest with { Icons = icon } });
            menu.Items.Add(item);
        }
        menu.PlacementTarget = target; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }

    private void ShowFonts(FrameworkElement target, string key)
    {
        var menu = new ContextMenu { MaxHeight = 420 };
        foreach (var font in StudioLibrary.Fonts().OrderBy(f => f.Name))
        {
            var item = new MenuItem { Header = font.Name + " · Your library", FontFamily = font.Family };
            item.Click += (_, _) =>
            {
                try
                {
                    var next = _document.ImportFont(System.IO.File.ReadAllBytes(font.Path), System.IO.Path.GetExtension(font.Path), key == "All" ? "UIFont" : key);
                    ChooseFont(next, key, "fonts/#" + font.Name);
                }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or ArgumentException or ThemeFormatException)
                { Report("Could not use that font: " + e.Message, true); }
            };
            menu.Items.Add(item);
        }
        if (_document.Original is { Fonts.Count: > 0 } package && ThemeCache.Unpack(package).FontsFolder is { } folder)
        {
            foreach (var family in Fonts.GetFontFamilies(new Uri(folder)))
            {
                var name = family.FamilyNames.Values.FirstOrDefault();
                if (name is null) continue;
                var item = new MenuItem { Header = name + " · In this theme", FontFamily = family };
                item.Click += (_, _) => ChooseFont(_document, key, "fonts/#" + name); menu.Items.Add(item);
            }
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        foreach (var family in Fonts.SystemFontFamilies.OrderBy(f => f.Source))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(family.Source, "^[A-Za-z0-9][A-Za-z0-9 ._\\-]{0,79}$")) continue;
            var item = new MenuItem { Header = family.Source, FontFamily = family };
            item.Click += (_, _) => ChooseFont(_document, key, family.Source); menu.Items.Add(item);
        }
        menu.PlacementTarget = target; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }

    private void ChooseFont(ThemeStudioDocument document, string key, string family)
        => TryChange(() => key == "All" ? document.AllFonts(family) : document.Font(key, family));

    private string FontLabel(string key)
    {
        var source = (Value(key) as FontFamily)?.Source ?? "Choose a font";
        if (source.Contains('#')) return source[(source.LastIndexOf('#') + 1)..];
        return source.Split(',')[0].Trim();
    }

    private static string IconLabel(IconStyle style) => style switch
    {
        IconStyle.System => "Windows file icons", IconStyle.Pixel => "Pixel art", IconStyle.OneBit => "Black & white pixels",
        IconStyle.OneBitDark => "White pixels on dark", IconStyle.Ink => "Ink outlines", IconStyle.Line => "Simple outlines",
        IconStyle.Neon => "Neon glow", IconStyle.Sketch => "Pencil sketches", IconStyle.Tile => "File type tiles",
        IconStyle.Dot => "Colored dots", IconStyle.Mark => "Retro symbols", _ => "Windows file icons"
    };

    private void SetSwitches()
    {
        if (!_syncing) Change(_document with { Manifest = _document.Manifest with { CrispText = _crisp.IsChecked == true, FadeToolbar = _fade.IsChecked == true } });
    }

    private void ChooseBackdrop()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose a picture", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new System.IO.FileInfo(picker.FileName).Length > 16 * 1024 * 1024) { Report("Choose a picture smaller than 16 MB.", true); return; }
            // CHANGED (sticker editor): crop it first, to choose which part sits behind the files.
            var editor = new StudioImageWindow(this, System.IO.File.ReadAllBytes(picker.FileName), fillsWindow: true, WindowAspect());
            if (editor.ShowDialog() != true || editor.Result is not { } edited) return;
            Change(_document.Backdrop(edited, .2));
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException)
        { Report("That picture could not be used: " + error.Message, true); }
    }

    // ---------------------------------------------------------------- state

    private void TryChange(Func<ThemeStudioDocument> next)
    {
        try { Change(next()); }
        catch (Exception error) when (error is ThemeFormatException or ArgumentException) { Report(error.Message, true); }
    }

    private void Change(ThemeStudioDocument next, bool sync = true)
    {
        if (next != _document)
        {
            if (!_gesture || !_gestureRecorded) _undo.Push(_document);
            _gestureRecorded = _gesture;
            _redo.Clear(); _document = next; _values = null;
        }
        _invalid = false;
        if (sync) Sync(); else UpdateButtons();
        _paintTimer.Stop(); _paintTimer.Start();
    }

    private void History(Stack<ThemeStudioDocument> from, Stack<ThemeStudioDocument> to)
    {
        if (!from.TryPop(out var next)) return;
        to.Push(_document); _document = next; _values = null; _invalid = false; Sync(); _paintTimer.Stop(); _paintTimer.Start();
    }

    private void UpdateButtons()
    {
        _undoButton.IsEnabled = _undo.Count > 0; _redoButton.IsEnabled = _redo.Count > 0;
        var named = ThemeManifest.Tidy(_name.Text, 60).Length > 0;
        _save.IsEnabled = _copy.IsEnabled = !_invalid && named;
        _save.ToolTip = named ? null : "Give your theme a name first";
        _changes.Visibility = Dirty ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool Dirty => _document != _initial;

    private object? Value(string key)
    {
        var own = _values ??= _document.Resources();
        return own.Contains(key) ? own[key] : _fallback.Contains(key) ? _fallback[key] : null;
    }

    private string ReadColor(string key) => Value(key) switch
    {
        SolidColorBrush b => b.Color.ToString(), GradientBrush g when g.GradientStops.Count > 0 => g.GradientStops[0].Color.ToString(),
        Color c => c.ToString(), _ => "#FF232220"
    };

    // What a colour actually looks like on screen: see-through text and panels are laid over what is behind them.
    private Color Solid(string key)
    {
        static Color Raw(object? value) => value switch
        {
            SolidColorBrush b => b.Color, GradientBrush { GradientStops.Count: > 0 } g => g.GradientStops[0].Color, Color c => c, _ => Colors.Transparent
        };
        // A patterned or picture background (Sketchbook) has no single colour; its themes name one in Theme.BaseColor.
        var page = Value("Base") is SolidColorBrush or GradientBrush ? Raw(Value("Base")) : Value("Theme.BaseColor") is Color named ? named : Raw(Value("Base"));
        page = Color.FromRgb(page.R, page.G, page.B);
        if (key == "Base") return page;
        var surface = StudioUi.Over(Raw(Value("Surface")), page);
        if (key == "Surface") return surface;
        return StudioUi.Over(Raw(Value(key)), surface);
    }

    private void Sync()
    {
        _syncing = true;
        SyncColors();
        _fontValues["All"].Text = new[] { "SerifFont", "DisplayFont" }.Any(k => FontLabel(k) != FontLabel("UIFont")) ? "Mixed fonts" : FontLabel("UIFont");
        foreach (var key in new[] { "UIFont", "SerifFont", "DisplayFont", "MonoFont" }) _fontValues[key].Text = FontLabel(key);
        foreach (var (key, (slider, value)) in _layoutSliders)
        {
            slider.Value = Value(key) is Thickness t ? key == "Explorer.SidebarWidth" ? t.Left : t.Top : slider.Minimum;
            value.Text = $"{slider.Value:0} px";
        }
        _layoutMap.Value = CurrentLayout(); // NEW (complex themes)
        // NEW (custom layouts)
        _layoutClassic.Visibility = _document.LayoutJson is null ? Visibility.Visible : Visibility.Collapsed;
        _customLayoutNote.Text = _document.LayoutJson is null ? "" : "This theme has a custom layout. Open the builder to change it, or to go back to the usual one.";
        _customLayoutNote.Visibility = _document.LayoutJson is null ? Visibility.Collapsed : Visibility.Visible;
        _customLayoutNote.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        BuildLayoutPresets();
        BuildStickers(); // NEW (stickers)
        _tilt.IsChecked = Value("NoteTilt") is RotateTransform { Angle: not 0d };
        _patternInkSwatch.Background = new SolidColorBrush(_patternInk ?? Solid("Ink"));
        _effectStrengthValue.Text = $"{_effectStrength.Value * 100:0}%";
        ShowPatternValues();
        SyncPart();
        _scale.Value = Value("InterfaceScale") is ScaleTransform zoom ? Math.Clamp(zoom.ScaleX, .75, 1.5) : 1; // NEW (everything)
        _scaleValue.Text = $"{_scale.Value * 100:0}%";
        if (!_author.IsKeyboardFocusWithin) _author.Text = _document.Manifest.Author;
        if (!_version.IsKeyboardFocusWithin) _version.Text = _document.Manifest.Version;
        if (!_description.IsKeyboardFocusWithin) _description.Text = _document.Manifest.Description;
        _eink.IsChecked = _document.Manifest.EInk; _experimental.IsChecked = _document.Manifest.Experimental;
        foreach (var refresh in _everyRefresh) refresh();
        _iconRules.Children.Clear();
        foreach (var key in (_values ??= _document.Resources()).Keys.Cast<object>().OfType<string>().Where(ThemeFileIcons.IsKey).OrderBy(k => k))
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var remove = FileBackupUi.IconButton("", "Remove this icon");
            remove.Click += (_, _) => Change(_document.Remove(key));
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            row.Children.Add(new TextBlock { Text = key[ThemeFileIcons.Prefix.Length..], VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            _iconRules.Children.Add(row);
        }
        _roundness.Value = Value("R8") is CornerRadius radius ? Math.Clamp(radius.TopLeft, 0, 16) : 8;
        _roundnessValue.Text = $"{_roundness.Value:0} px";
        _backdropOpacity.Value = Value("ListFill") is ImageBrush backdrop ? backdrop.Opacity : .2;
        _backdropValue.Text = $"{_backdropOpacity.Value * 100:0}%";
        _gradientSwatch.Background = new SolidColorBrush(Parse(_gradientEnd));
        _iconStyleValue.Text = IconLabel(_document.Manifest.Icons);
        _crisp.IsChecked = _document.Manifest.CrispText; _fade.IsChecked = _document.Manifest.FadeToolbar;
        _syncing = false;
        BuildZoomChips();
        UpdateReadability();
        UpdateButtons();
    }

    private void SyncColors()
    {
        foreach (var (key, (swatch, code)) in _colorCells.Concat(_partCells).Concat(_everyCells))
        {
            var value = Value(key);
            swatch.Background = value switch { Brush brush => brush, Color c => new SolidColorBrush(c), _ => Brushes.Transparent };
            code.Text = value switch
            {
                SolidColorBrush { Color.A: 255 } b => StudioUi.Hex(b.Color).Remove(1, 2),
                SolidColorBrush { Color.A: 0 } => "None",
                SolidColorBrush b => StudioUi.Hex(b.Color),
                Color { A: 255 } c => StudioUi.Hex(c).Remove(1, 2),
                Color c => StudioUi.Hex(c),
                GradientBrush => "Gradient",
                ImageBrush => "Picture",
                _ => "Pattern"
            };
        }
    }

    private void BuildZoomChips()
    {
        _zoomChips.Children.Clear();
        var fit = FileBackupUi.Chip("Fit", !_native, () => { _native = false; LayoutPreview(); BuildZoomChips(); });
        var actual = FileBackupUi.Chip("100%", _native, () => { _native = true; LayoutPreview(); BuildZoomChips(); });
        fit.ToolTip = "Shrink the preview to fit"; actual.ToolTip = "Actual size, to check small text";
        fit.Margin = actual.Margin = new Thickness(6, 0, 0, 0);
        // NEW (complex themes): show the preview pane in the sample, to see where it sits.
        var pane = FileBackupUi.Chip("Preview pane", _paneShown, () => { _paneShown = !_paneShown; BuildZoomChips(); Paint(); });
        pane.ToolTip = "Show the preview pane beside the sample files"; pane.Margin = new Thickness(0, 0, 10, 0);
        _zoomChips.Children.Add(pane);
        _zoomChips.Children.Add(fit); _zoomChips.Children.Add(actual);
    }

    private void Report(string text, bool error = false)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "InkMuted");
    }

    // ---------------------------------------------------------------- keys and closing

    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control && Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift)) return;
        if (e.Key == Key.S) { e.Handled = true; if (_save.IsEnabled) Save(copy: false); return; }
        if (Keyboard.FocusedElement is TextBox) return; // text boxes keep their own undo
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.Key == Key.Z && !shift) { e.Handled = true; History(_undo, _redo); }
        else if (e.Key == Key.Y || (e.Key == Key.Z && shift)) { e.Handled = true; History(_redo, _undo); }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_saved || !IsVisible || !Dirty) return;
        var answer = MessageDialog.Show(this, "Your changes to this theme have not been saved.", "Close without saving?",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) e.Cancel = true;
    }

    // ---------------------------------------------------------------- preview and saving

    internal void StartPreview()
    {
        if (_isolatedPreview is not null) return;
        _isolatedPreview = new ExplorerThemePreview();
        LayoutPreview(); Paint();
    }

    private void LayoutPreview()
    {
        if (_isolatedPreview is null) return;
        _previewScroll.Content = null; _fit.Child = null;
        if (_native) _previewScroll.Content = _isolatedPreview;
        else { _fit.Child = _isolatedPreview; _previewScroll.Content = _fit; }
        _previewScroll.HorizontalScrollBarVisibility = _native ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _previewScroll.VerticalScrollBarVisibility = _native ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        Dispatcher.BeginInvoke(new Action(PlaceStickerHandle), DispatcherPriority.Loaded); // NEW (stickers)
    }

    private void Paint()
    {
        if (_closing || _isolatedPreview is null) return;
        try
        {
            _isolatedPreview.ShowPreviewPane = _paneShown;
            _isolatedPreview.Show(_document);
            Dispatcher.BeginInvoke(new Action(PlaceStickerHandle), DispatcherPriority.Loaded); // NEW (stickers)
            // CHANGED (theme studio, round 2): no "Preview only" message here any more; it replaced every
            // other message 120 ms after it appeared. The caption under the preview says it once.
        }
        catch (Exception error) when (error is ThemeFormatException or ArgumentException) { _invalid = true; UpdateButtons(); Report(error.Message, true); }
    }

    private void Save(bool copy)
    {
        if (_invalid || ThemeManifest.Tidy(_name.Text, 60).Length == 0) return;
        _paintTimer.Stop();
        try
        {
            var replace = !copy && _editing is { Shipped: false } ? _editing : null;
            var id = _document.Save(ThemeService.Folder, _name.Text, replace);
            ThemeService.Refresh();
            if (!ThemeService.ApplyEdited(id)) { Report("Saved, but could not apply: " + ThemeService.LastProblem, true); return; }
            SavedName = ThemeManifest.Tidy(_name.Text, 60); _saved = true; DialogResult = true;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ThemeFormatException or ArgumentException) { Report("Could not save: " + error.Message, true); }
    }
}

// NEW (theme studio, round 2): the small pieces the studio and its colour picker share.
internal static class StudioUi
{
    internal static readonly Brush Checker = MakeChecker();

    private static Brush MakeChecker()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        var squares = new GeometryGroup();
        squares.Children.Add(new RectangleGeometry(new Rect(0, 0, 4, 4)));
        squares.Children.Add(new RectangleGeometry(new Rect(4, 4, 4, 4)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(204, 204, 204)), null, squares));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }

    /// <summary>A small framed colour sample; the checkerboard shows through see-through colours.</summary>
    internal static (Border Outer, Border Inner) Swatch(double size)
    {
        var inner = new Border { CornerRadius = new CornerRadius(5) };
        var outer = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Background = Checker, Child = inner, SnapsToDevicePixels = true };
        outer.SetResourceReference(Border.BorderBrushProperty, "Line");
        return (outer, inner);
    }

    /// <summary>A whole row that acts as one button: hover fill, click, Enter / Space.</summary>
    internal static Border ClickRow(UIElement content, string name, Action click)
    {
        var row = new Border { Padding = new Thickness(8, 7, 8, 7), Child = content, Cursor = Cursors.Hand, Background = Brushes.Transparent, Focusable = true };
        row.SetResourceReference(Border.CornerRadiusProperty, "R6");
        void Lit(bool on) { if (on) row.SetResourceReference(Border.BackgroundProperty, "Hover"); else row.Background = Brushes.Transparent; }
        row.MouseEnter += (_, _) => Lit(true);
        row.MouseLeave += (_, _) => Lit(row.IsKeyboardFocused);
        row.GotKeyboardFocus += (_, _) => Lit(true);
        row.LostKeyboardFocus += (_, _) => Lit(row.IsMouseOver);
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        row.KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { e.Handled = true; click(); } };
        AutomationProperties.SetName(row, name);
        return row;
    }

    internal static TextBlock Heading(string text)
    {
        var heading = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 6) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        return heading;
    }

    /// <summary>A heading over a card holding the given pieces, top to bottom.</summary>
    internal static StackPanel Card(string title, params UIElement[] children)
    {
        var inside = new StackPanel();
        foreach (var child in children) inside.Children.Add(child);
        var card = new Border { Padding = new Thickness(10), BorderThickness = new Thickness(1), Child = inside };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "LineFaint");
        card.SetResourceReference(Border.CornerRadiusProperty, "R12");
        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        block.Children.Add(Heading(title));
        block.Children.Add(card);
        return block;
    }

    internal static Border Divider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 8) };
        line.SetResourceReference(Border.BackgroundProperty, "LineFaint");
        return line;
    }

    internal static string Hex(Color c, bool opaque = false) => $"#{(opaque ? (byte)255 : c.A):X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    internal static Color TextOn(Color background) =>
        ThemeDesign.Contrast(background, Colors.White) > ThemeDesign.Contrast(background, Colors.Black) ? Colors.White : Colors.Black;

    /// <summary>A colour laid over another, as the screen shows it.</summary>
    internal static Color Over(Color top, Color under)
    {
        var a = top.A / 255d;
        byte Mix(byte t, byte u) => (byte)Math.Round(t * a + u * (1 - a));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }
}
