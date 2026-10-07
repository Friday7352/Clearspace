using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Clearspace.Services;

namespace Clearspace;

internal sealed class ThemeStudioWindow : Window
{
    private ThemeStudioDocument _document;
    private readonly ThemeStudioDocument _initial;
    private readonly ThemeInfo? _editing;
    private readonly Stack<ThemeStudioDocument> _undo = new(), _redo = new();
    private readonly TextBox _name = new() { MaxLength = 60 };
    private readonly TextBox _hex = new() { MaxLength = 9 };
    private readonly TextBox _gradientEnd = new() { Text = "#364B77", MaxLength = 9 };
    private readonly Slider[] _channels = new Slider[4];
    private readonly Button _colorTarget = FileBackupUi.TextButton("Window background", false);
    private readonly Border _swatch = new() { Height = 38, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _contrast = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _undoButton = FileBackupUi.TextButton("Undo", false), _redoButton = FileBackupUi.TextButton("Redo", false);
    private readonly Button _save = FileBackupUi.TextButton("Save & use", true);
    private readonly Button _copy = FileBackupUi.TextButton("Save a copy", false);
    private readonly Slider _roundness = new() { Minimum = 0, Maximum = 16, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly Slider _backdropOpacity = new() { Minimum = 0, Maximum = 1, Value = .2, TickFrequency = .05, IsSnapToTickEnabled = true };
    private readonly Dictionary<string, Button> _fontButtons = new();
    private readonly Button _icons = FileBackupUi.TextButton("Windows icons", false);
    private readonly CheckBox _crisp = new() { Content = "Crisp pixel lettering", Margin = new Thickness(0, 8, 0, 8) };
    private readonly CheckBox _fade = new() { Content = "Fade toolbar when idle", Margin = new Thickness(0, 8, 0, 8) };
    private readonly ScrollViewer _previewScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Rectangle _mirror = new() { IsHitTestVisible = false };
    private readonly Viewbox _fit = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top };
    private readonly DispatcherTimer _paintTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private ThemeService.PreviewSession? _session;
    private FrameworkElement? _explorer;
    private ResourceDictionary? _fallback;
    private ResourceDictionary? _values;
    private string _key = "Base";
    private bool _syncing, _native, _invalid, _closing;
    private bool _gesture, _gestureRecorded;
    internal string? SavedName { get; private set; }

    internal ThemeStudioWindow(ThemeInfo? editing = null)
    {
        _editing = editing;
        _document = editing is null ? ThemeStudioDocument.FromDesign(ThemeDesign.Presets[0].Design) : ThemeStudioDocument.Open(editing);
        _initial = _document;
        _name.Text = editing is null ? "My midnight" : editing.Shipped ? editing.Label + " remix" : editing.Label;
        Title = editing is null ? "Theme studio" : "Theme studio · " + editing.Label;
        Width = 1240; Height = 850; MinWidth = 920; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Keep the editor legible even while experimenting with low-contrast colours in the Explorer.
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Clearspace;component/Themes/Defaults.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Clearspace;component/Themes/Dark/theme.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "Base"); SetResourceReference(ForegroundProperty, "Ink"); SetResourceReference(FontFamilyProperty, "UIFont");
        FontSize = 13;
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);

        var page = new Grid { Margin = new Thickness(24) };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); page.RowDefinitions.Add(new RowDefinition()); page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        heading.Children.Add(new TextBlock { Text = "Your space. Your rules.", FontSize = 28, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(FileBackupUi.Muted("Mix a mood, tune every detail, and see it in the real Explorer.")); page.Children.Add(heading);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); body.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(body, 1); page.Children.Add(body);
        var controls = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        body.Children.Add(new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Label(controls, "START WITH A MOOD");
        var moods = new WrapPanel();
        foreach (var (name, design) in ThemeDesign.Presets)
        {
            var button = Button(name, () => Change(ThemeStudioDocument.FromDesign(design)));
            moods.Children.Add(button);
        }
        moods.Children.Add(Button("Surprise me", () =>
        {
            var p = ThemeDesign.Presets[Random.Shared.Next(ThemeDesign.Presets.Length)].Design;
            Change(ThemeStudioDocument.FromDesign(p with { Accent = Swatches[Random.Shared.Next(Swatches.Length)], Roundness = Random.Shared.Next(17) }));
        }));
        controls.Children.Add(moods);
        var warning = FileBackupUi.Muted("Moods replace the whole design. Undo brings yours back."); warning.TextWrapping = TextWrapping.Wrap; controls.Children.Add(warning);
        Label(controls, "PAINT YOUR SPACE");
        _colorTarget.HorizontalAlignment = HorizontalAlignment.Stretch;
        _colorTarget.Click += (_, _) => ShowColorMenu(); controls.Children.Add(_colorTarget); controls.Children.Add(_swatch);
        AutomationProperties.SetName(_hex, "Selected colour hex value"); controls.Children.Add(FileBackupUi.Framed(_hex));
        _hex.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            try { Change(_document.Color(_key, _hex.Text), sync: false); _invalid = false; SyncChannels(); }
            catch (ArgumentException) { _invalid = true; Report("Use #RRGGBB, or #AARRGGBB with transparency.", true); }
            UpdateButtons();
        };
        var swatches = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        foreach (var hex in Swatches)
        {
            var b = Button("", () => Change(_document.Color(_key, hex)));
            b.Content = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = (Brush)new BrushConverter().ConvertFromString(hex)! };
            b.ToolTip = hex; AutomationProperties.SetName(b, "Use " + hex); swatches.Children.Add(b);
        }
        controls.Children.Add(swatches);
        for (var i = 0; i < 4; i++)
        {
            var channel = i;
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock { Text = new[] { "Red", "Green", "Blue", "Opacity" }[i], Width = 62, VerticalAlignment = VerticalAlignment.Center });
            var slider = new Slider { Minimum = 0, Maximum = 255, TickFrequency = 1, IsSnapToTickEnabled = true };
            slider.SetResourceReference(StyleProperty, "MediaSlider"); AutomationProperties.SetName(slider, new[] { "Red", "Green", "Blue", "Opacity" }[i]);
            _channels[i] = slider; row.Children.Add(slider); controls.Children.Add(row);
            GroupDrag(slider);
            slider.ValueChanged += (_, _) =>
            {
                if (_syncing) return;
                var color = Color.FromArgb((byte)_channels[3].Value, (byte)_channels[0].Value, (byte)_channels[1].Value, (byte)_channels[2].Value);
                Change(_document.Color(_key, color.ToString()));
            };
        }
        controls.Children.Add(_contrast);
        var gradients = new StackPanel();
        gradients.Children.Add(FileBackupUi.Muted("From the window colour to…"));
        AutomationProperties.SetName(_gradientEnd, "Gradient end colour"); gradients.Children.Add(FileBackupUi.Framed(_gradientEnd));
        gradients.Children.Add(Button("Diagonal gradient", () => AddGradient(true)));
        gradients.Children.Add(Button("Vertical gradient", () => AddGradient(false)));
        gradients.Children.Add(Button("Make background solid", () => Change(_document.Color("Base", ReadColor("Base")))));
        AddSection(controls, "Background gradients", gradients);
        var pictures = new StackPanel();
        pictures.Children.Add(Button("Choose a picture…", ChooseBackdrop));
        pictures.Children.Add(FileBackupUi.Muted("A photo or texture behind your files."));
        var intensity = _backdropOpacity;
        intensity.SetResourceReference(StyleProperty, "MediaSlider"); AutomationProperties.SetName(intensity, "Backdrop intensity");
        intensity.ValueChanged += (_, _) => { if (!_syncing) Change(_document.BackdropOpacity(intensity.Value)); };
        GroupDrag(intensity);
        Label(pictures, "Picture intensity"); pictures.Children.Add(intensity);
        pictures.Children.Add(Button("Remove picture", () => Change(_document.Color("ListFill", "#00000000"))));
        AddSection(controls, "Photo & texture backdrop", pictures);
        var typography = new StackPanel();
        foreach (var (key, label) in new[] { ("UIFont", "Interface"), ("SerifFont", "File names"), ("DisplayFont", "Headings"), ("MonoFont", "Paths & code") })
        {
            Label(typography, label);
            var button = FileBackupUi.TextButton("Choose font", false); button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Click += (_, _) => ShowFonts(button, key); typography.Children.Add(button); _fontButtons.Add(key, button);
        }
        Label(typography, "Corner roundness");
        _roundness.SetResourceReference(StyleProperty, "MediaSlider"); AutomationProperties.SetName(_roundness, "Corner roundness");
        GroupDrag(_roundness);
        _roundness.ValueChanged += (_, _) => { if (!_syncing) Change(_document.Corners(_roundness.Value)); }; typography.Children.Add(_roundness);
        AddSection(controls, "Fonts & shape", typography);
        var personality = new StackPanel(); personality.Children.Add(_icons);
        _icons.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var icon in Enum.GetValues<IconStyle>())
            {
                var item = new MenuItem { Header = icon.ToString(), IsCheckable = true, IsChecked = _document.Manifest.Icons == icon };
                item.Click += (_, _) => Change(_document with { Manifest = _document.Manifest with { Icons = icon } }); menu.Items.Add(item);
            }
            menu.PlacementTarget = _icons; menu.IsOpen = true;
        };
        _crisp.Checked += (_, _) => SetSwitches(); _crisp.Unchecked += (_, _) => SetSwitches();
        _fade.Checked += (_, _) => SetSwitches(); _fade.Unchecked += (_, _) => SetSwitches();
        personality.Children.Add(_crisp); personality.Children.Add(_fade); AddSection(controls, "Icons & personality", personality);

        var right = new Grid(); Grid.SetColumn(right, 2); body.Children.Add(right);
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); right.RowDefinitions.Add(new RowDefinition()); right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var previewHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var zoom = Button("Fit / 100%", () => { _native = !_native; LayoutPreview(); }); DockPanel.SetDock(zoom, Dock.Right); previewHeader.Children.Add(zoom);
        previewHeader.Children.Add(new TextBlock { Text = "LIVE EXPLORER", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold }); right.Children.Add(previewHeader);
        Grid.SetRow(_previewScroll, 1); right.Children.Add(_previewScroll);
        var naming = new StackPanel { Margin = new Thickness(0, 14, 0, 0) }; Grid.SetRow(naming, 2); right.Children.Add(naming);
        var explanation = FileBackupUi.Muted("This is your actual Explorer window—not a mockup. Fit scales it down; 100% shows its real size. Changes are temporary until saved."); explanation.TextWrapping = TextWrapping.Wrap; naming.Children.Add(explanation);
        Label(naming, "THEME NAME"); AutomationProperties.SetName(_name, "Theme name"); naming.Children.Add(FileBackupUi.Framed(_name));
        _name.TextChanged += (_, _) => UpdateButtons();
        var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0) }; Grid.SetRow(footer, 2); page.Children.Add(footer);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions);
        _undoButton.Click += (_, _) => History(_undo, _redo); actions.Children.Add(_undoButton);
        _redoButton.Click += (_, _) => History(_redo, _undo); actions.Children.Add(_redoButton);
        actions.Children.Add(Button("Reset", () => Change(_initial)));
        var cancel = Button("Cancel", Close); cancel.IsCancel = true; actions.Children.Add(cancel);
        _copy.Visibility = editing is { Shipped: false } ? Visibility.Visible : Visibility.Collapsed;
        _copy.Click += (_, _) => Save(copy: true); actions.Children.Add(_copy);
        _save.Content = editing is { Shipped: false } ? "Save changes" : "Save & use";
        _save.Click += (_, _) => Save(copy: false); actions.Children.Add(_save);
        _status.Margin = new Thickness(0, 0, 10, 0); _status.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(_status);
        Content = page;
        _paintTimer.Tick += (_, _) => { _paintTimer.Stop(); Paint(); };
        Loaded += (_, _) => StartPreview();
        Closed += (_, _) => { _closing = true; _paintTimer.Stop(); _session?.Dispose(); _session = null; if (_explorer is not null) _explorer.SizeChanged -= ExplorerSizeChanged; if (Owner is MainWindow main) main.EndStudioPreview(); _mirror.Fill = null; };
        Sync();
    }

    private static readonly string[] Swatches = ["#AFA1FF", "#64D8CF", "#FF9DCB", "#F6C56F", "#426B41", "#A54432", "#326CC0", "#FFFFFF", "#171D30", "#000000"];
    private static Button Button(string text, Action action)
    {
        var b = FileBackupUi.TextButton(text, false); b.Margin = new Thickness(0, 0, 6, 6); b.Click += (_, _) => action(); return b;
    }
    private static void Label(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 8) });
    private static void AddSection(Panel panel, string title, UIElement content) => panel.Children.Add(new Expander { Header = title, Content = content, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(0, 8, 0, 0) });
    private void Report(string text, bool error = false) { _status.Text = text; _status.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "InkMuted"); }
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
    private void GroupDrag(Slider slider)
    {
        slider.PreviewMouseLeftButtonDown += (_, _) => { _gesture = true; _gestureRecorded = false; };
        slider.PreviewMouseLeftButtonUp += (_, _) => { _gesture = false; _gestureRecorded = false; };
        slider.LostMouseCapture += (_, _) => { _gesture = false; _gestureRecorded = false; };
    }
    private void History(Stack<ThemeStudioDocument> from, Stack<ThemeStudioDocument> to)
    {
        if (!from.TryPop(out var next)) return;
        to.Push(_document); _document = next; _values = null; _invalid = false; Sync(); _paintTimer.Stop(); _paintTimer.Start();
    }
    private void UpdateButtons()
    {
        _undoButton.IsEnabled = _undo.Count > 0; _redoButton.IsEnabled = _redo.Count > 0;
        _save.IsEnabled = _copy.IsEnabled = !_invalid && ThemeManifest.Tidy(_name.Text, 60).Length > 0;
    }
    private object? Value(string key)
    {
        var own = _values ??= _document.Resources();
        return own.Contains(key) ? own[key] : _fallback?[key] ?? TryFindResource(key);
    }
    private string ReadColor(string key) => Value(key) switch
    {
        SolidColorBrush b => b.Color.ToString(), GradientBrush g when g.GradientStops.Count > 0 => g.GradientStops[0].Color.ToString(),
        Color c => c.ToString(), _ => "#FF232220"
    };
    private void Sync()
    {
        _syncing = true;
        _hex.Text = ReadColor(_key);
        foreach (var (key, button) in _fontButtons) button.Content = (Value(key) as FontFamily)?.Source ?? "Choose font";
        _roundness.Value = Value("R8") is CornerRadius radius ? Math.Clamp(radius.TopLeft, 0, 16) : 8;
        _backdropOpacity.Value = Value("ListFill") is ImageBrush backdrop ? backdrop.Opacity : .2;
        _icons.Content = "Icons · " + _document.Manifest.Icons;
        _crisp.IsChecked = _document.Manifest.CrispText; _fade.IsChecked = _document.Manifest.FadeToolbar;
        _syncing = false; SyncChannels(); UpdateButtons();
    }
    private void SyncChannels()
    {
        if (ColorConverter.ConvertFromString(_hex.Text) is not Color c) return;
        _syncing = true;
        _channels[0].Value = c.R; _channels[1].Value = c.G; _channels[2].Value = c.B; _channels[3].Value = c.A;
        _swatch.Background = new SolidColorBrush(c); _syncing = false;
    }
    private void ShowColorMenu()
    {
        var menu = new ContextMenu { MaxHeight = 520 };
        var friendly = new Dictionary<string, string> { ["Base"] = "Window background", ["Surface"] = "File area & cards", ["Overlay"] = "Menus & popups", ["Ink"] = "Main text", ["InkMuted"] = "Secondary text", ["InkFaint"] = "Subtle text", ["Accent"] = "Accent colour", ["AccentInk"] = "Text on accent", ["Selected"] = "Selected files", ["Line"] = "Borders", ["Hover"] = "Hover highlight", ["ListFill"] = "File list backdrop" };
        foreach (var group in new[] { ("Everyday colours", friendly.Keys.AsEnumerable()), ("All colour details", ThemeKeys.All.Where(p => p.Value is ThemeValue.Brush or ThemeValue.FlatColor or ThemeValue.Color).Select(p => p.Key).Except(friendly.Keys).OrderBy(k => k)), ("Selected file text", new[] { "SelectedRow.Ink", "SelectedRow.InkMuted", "SelectedRow.InkFaint" }.AsEnumerable()) })
        {
            var section = new MenuItem { Header = group.Item1 };
            foreach (var key in group.Item2)
            {
                var label = friendly.GetValueOrDefault(key, key);
                var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = key == _key };
                item.Click += (_, _) => { _key = key; _colorTarget.Content = label; _invalid = false; Sync(); }; section.Items.Add(item);
            }
            menu.Items.Add(section);
        }
        menu.PlacementTarget = _colorTarget; menu.IsOpen = true;
    }
    private void ShowFonts(Button button, string key)
    {
        var menu = new ContextMenu { MaxHeight = 420 };
        foreach (var family in Fonts.SystemFontFamilies.OrderBy(f => f.Source))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(family.Source, "^[A-Za-z0-9][A-Za-z0-9 ._\\-]{0,79}$")) continue;
            var item = new MenuItem { Header = family.Source, FontFamily = family };
            item.Click += (_, _) => Change(_document.Font(key, family.Source)); menu.Items.Add(item);
        }
        menu.PlacementTarget = button; menu.IsOpen = true;
    }
    private void SetSwitches()
    {
        if (!_syncing) Change(_document with { Manifest = _document.Manifest with { CrispText = _crisp.IsChecked == true, FadeToolbar = _fade.IsChecked == true } });
    }
    private void AddGradient(bool diagonal)
    {
        try { Change(_document.Gradient(ReadColor("Base"), _gradientEnd.Text, diagonal)); }
        catch (ArgumentException) { Report("Choose a valid end colour for the gradient.", true); }
    }
    private void ChooseBackdrop()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose a backdrop", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new System.IO.FileInfo(picker.FileName).Length > 16 * 1024 * 1024) { Report("Choose a picture smaller than 16 MB.", true); return; }
            Change(_document.Backdrop(System.IO.File.ReadAllBytes(picker.FileName), .2));
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Report("That picture could not be used: " + error.Message, true); }
    }
    private void StartPreview()
    {
        if (Owner is not MainWindow main) { Report("Open Theme studio from the Themes page to see the live Explorer.", true); return; }
        _fallback = new ResourceDictionary();
        _fallback.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Clearspace;component/Themes/Defaults.xaml", UriKind.Relative) });
        _fallback.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Clearspace;component/Themes/Dark/theme.xaml", UriKind.Relative) });
        _session = new ThemeService.PreviewSession();
        _explorer = main.BeginStudioPreview();
        _explorer.SizeChanged += ExplorerSizeChanged;
        _mirror.Fill = new VisualBrush(_explorer) { Stretch = Stretch.Fill };
        LayoutPreview(); Paint();
    }
    private void ExplorerSizeChanged(object sender, SizeChangedEventArgs e) => LayoutPreview();
    private void LayoutPreview()
    {
        if (_explorer is null) return;
        _previewScroll.Content = null; _fit.Child = null;
        _mirror.Width = Math.Max(1, _explorer.ActualWidth); _mirror.Height = Math.Max(1, _explorer.ActualHeight);
        if (_native) _previewScroll.Content = _mirror;
        else { _fit.Child = _mirror; _previewScroll.Content = _fit; }
        _previewScroll.HorizontalScrollBarVisibility = _native ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }
    private void Paint()
    {
        if (_closing || _session is null) return;
        try
        {
            _session.Show(_document.Package);
            _explorer?.UpdateLayout();
            Report("Live preview · Cancel restores your previous theme.");
            var ink = Value("Ink") as SolidColorBrush; var surface = Value("Surface") as SolidColorBrush;
            if (ink is not null && surface is not null)
            {
                var ratio = ThemeDesign.Contrast(ink.Color, surface.Color);
                _contrast.Text = ratio >= 4.5 ? $"Text contrast {ratio:0.0}:1 · Easy to read" : $"Text contrast {ratio:0.0}:1 · Try lighter text or a darker surface";
                _contrast.SetResourceReference(TextBlock.ForegroundProperty, ratio >= 4.5 ? "InkMuted" : "Warn");
            }
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
            _session?.Dispose(); _session = null;
            ThemeService.Refresh();
            if (!ThemeService.ApplyEdited(id)) { Report("Saved, but could not apply: " + ThemeService.LastProblem, true); _session = new ThemeService.PreviewSession(); return; }
            SavedName = ThemeManifest.Tidy(_name.Text, 60); DialogResult = true;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ThemeFormatException or ArgumentException) { Report("Could not save: " + error.Message, true); }
    }
}
