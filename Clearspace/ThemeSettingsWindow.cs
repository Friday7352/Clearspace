using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clearspace.Services;

namespace Clearspace;

// NEW (edit in place): Every setting, as its own window. Opened from More › Every setting… while editing a theme.
// It lists everything a theme can hold, with a search box, plus the theme's own details (made by, description,
// version, e-ink, experimental). Each change goes through the editor, so it is one undo step and shows on the
// window straight away. Ported from the old studio's "More" drawer (ThemeStudioWindow is gone).
internal sealed class ThemeSettingsWindow : Window
{
    private readonly ThemeEditor _editor;
    private readonly TextBox _filter = new();
    private readonly StackPanel _rows = new();
    private readonly List<Action> _refresh = [];
    private readonly TextBox _author = new() { MaxLength = 60 }, _version = new() { MaxLength = 20 };
    private readonly TextBox _description = new() { MaxLength = 200, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MinHeight = 52 };
    private readonly CheckBox _eink = FileBackupUi.Tick("Made for e-ink screens", "Shown on the Themes page so people with e-ink screens can find it");
    private readonly CheckBox _experimental = FileBackupUi.Tick("Experimental", "Shown on the Themes page as a theme that is still being tried out");
    private bool _syncing;

    internal ThemeSettingsWindow(Window owner, ThemeEditor editor)
    {
        _editor = editor;
        if (owner.IsVisible) Owner = owner;
        Title = "Every setting";
        Width = 520; Height = Math.Min(760, SystemParameters.WorkArea.Height - 60);
        MinWidth = 420; MinHeight = 360;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // The editor's own Dark look, so the list stays readable whatever the theme being made looks like.
        Resources.MergedDictionaries.Add(ThemeEditor.Chrome());
        SetResourceReference(BackgroundProperty, "Base"); SetResourceReference(ForegroundProperty, "Ink"); SetResourceReference(FontFamilyProperty, "UIFont");
        FontSize = 13;
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);

        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 22) };

        // About this theme
        panel.Children.Add(Heading("About this theme"));
        foreach (var (box, name) in new[] { (_author, "Made by"), (_version, "Version"), (_description, "Description") })
        {
            AutomationProperties.SetName(box, name);
            box.LostFocus += (_, _) => SetDetails();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; SetDetails(); } };
        }
        panel.Children.Add(Small("Made by")); panel.Children.Add(FileBackupUi.Framed(_author));
        panel.Children.Add(Small("Description")); panel.Children.Add(FileBackupUi.Framed(_description));
        panel.Children.Add(Small("Version")); panel.Children.Add(FileBackupUi.Framed(_version));
        foreach (var check in new[] { _eink, _experimental })
        {
            check.Margin = new Thickness(2, 8, 0, 0);
            check.Checked += (_, _) => SetDetails(); check.Unchecked += (_, _) => SetDetails();
            panel.Children.Add(check);
        }

        // Every setting
        var every = Heading("Every setting"); every.Margin = new Thickness(0, 22, 0, 2);
        panel.Children.Add(every);
        panel.Children.Add(Small("Click a color or font to change it; type a size and press Enter."));
        AutomationProperties.SetName(_filter, "Find a setting");
        _filter.ToolTip = "Find a setting, such as menu, corner or sidebar";
        _filter.TextChanged += (_, _) => Build();
        var filterFrame = FileBackupUi.Framed(_filter); filterFrame.Margin = new Thickness(0, 4, 0, 8);
        panel.Children.Add(filterFrame);
        panel.Children.Add(_rows);

        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Loaded += (_, _) => { Build(); _filter.Focus(); };
    }

    private object? Value(string key) => _editor.ValueOf(key);

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };

    private static TextBlock Small(string text)
    {
        var block = FileBackupUi.Muted(text); block.FontSize = 12; block.Margin = new Thickness(0, 6, 0, 3); block.TextWrapping = TextWrapping.Wrap; return block;
    }

    // Puts every row (and the details) back in step with the theme after a change.
    private void Sync()
    {
        _syncing = true;
        var m = _editor.Draft.Manifest;
        if (!_author.IsKeyboardFocusWithin) _author.Text = m.Author;
        if (!_version.IsKeyboardFocusWithin) _version.Text = m.Version;
        if (!_description.IsKeyboardFocusWithin) _description.Text = m.Description;
        _eink.IsChecked = m.EInk; _experimental.IsChecked = m.Experimental;
        foreach (var refresh in _refresh) refresh();
        _syncing = false;
    }

    private void Apply(Func<ThemeStudioDocument> next) { _editor.Apply(next); Sync(); }

    private void SetDetails()
    {
        if (_syncing) return;
        var m = _editor.Draft.Manifest;
        var next = m with
        {
            Author = ThemeManifest.Tidy(_author.Text, 60), Description = ThemeManifest.Tidy(_description.Text, 200),
            Version = ThemeManifest.Tidy(_version.Text, 20) is { Length: > 0 } v ? v : "1.0",
            EInk = _eink.IsChecked == true, Experimental = _experimental.IsChecked == true
        };
        if (next != m) Apply(() => _editor.Draft with { Manifest = next });
    }

    // ---------------------------------------------------------------- the list

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
        ["Part.Status.EdgeWidth"] = "Status bar outline width",
        ["Part.Sidebar.Corner"] = "Sidebar corners", ["Part.Toolbar.Corner"] = "Toolbar corners", ["Part.Files.Corner"] = "File area corners",
        ["Part.Status.Corner"] = "Status bar corners"
    };

    internal static string SettingLabel(string key)
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

    private void Build()
    {
        _rows.Children.Clear(); _refresh.Clear();
        var filter = _filter.Text.Trim();
        var keys = ThemeKeys.All.Keys.Concat(StudioColorOptions.All.Select(o => o.Key)).Distinct()
            .Where(key => !key.StartsWith("Decor.", StringComparison.Ordinal)) // stickers are moved on the window itself
            .Select(key => (Key: key, Kind: ThemeKeys.TryGetKind(key, out var kind) ? kind : ThemeValue.Brush))
            .Select(k => (k.Key, k.Kind, Label: SettingLabel(k.Key), Group: SettingGroup(k.Key, k.Kind)))
            .Where(k => filter.Length == 0 || k.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) || k.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || k.Group.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var group in keys.GroupBy(k => k.Group))
        {
            var heading = new TextBlock { Text = group.Key, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, _rows.Children.Count == 0 ? 4 : 14, 0, 2) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
            _rows.Children.Add(heading);
            foreach (var (key, kind, label, _) in group) _rows.Children.Add(Row(key, kind, label));
        }
        if (keys.Length == 0) _rows.Children.Add(Small("No setting matches “" + filter + "”."));
        Sync();
    }

    private FrameworkElement Row(string key, ThemeValue kind, string label)
    {
        switch (kind)
        {
            case ThemeValue.Brush or ThemeValue.FlatColor or ThemeValue.Color:
            {
                var (outer, inner) = StudioUi.Swatch(22);
                var code = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                code.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                code.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
                var row = new DockPanel();
                DockPanel.SetDock(outer, Dock.Left); row.Children.Add(outer);
                DockPanel.SetDock(code, Dock.Right); row.Children.Add(code);
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
                _refresh.Add(() =>
                {
                    var value = Value(key);
                    inner.Background = value switch { Brush b => b, Color c => new SolidColorBrush(c), _ => Brushes.Transparent };
                    code.Text = value switch
                    {
                        SolidColorBrush b => StudioUi.Hex(b.Color), Color c => StudioUi.Hex(c), ImageBrush => "picture",
                        GradientBrush => "blend", DrawingBrush or VisualBrush => "pattern", _ => "—"
                    };
                });
                var clickable = StudioUi.ClickRow(row, "Change " + label, () => { _editor.ChooseColor(key, label, this); Sync(); });
                clickable.Padding = new Thickness(6, 5, 8, 5);
                return clickable;
            }
            case ThemeValue.Font:
            {
                var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 };
                value.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
                _refresh.Add(() => { value.Text = _editor.FontName(key); value.FontFamily = Value(key) as FontFamily ?? FontFamily; });
                var row = new DockPanel();
                DockPanel.SetDock(value, Dock.Right); row.Children.Add(value);
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                Border? clickable = null;
                clickable = StudioUi.ClickRow(row, "Change " + label, () => _editor.ChooseFont(clickable!, key, Sync));
                clickable.Padding = new Thickness(6, 6, 8, 6);
                return clickable;
            }
            case ThemeValue.Visibility:
            {
                var check = FileBackupUi.Tick(label, key);
                check.Margin = new Thickness(6, 5, 0, 5);
                check.Checked += (_, _) => { if (!_syncing) Apply(() => _editor.Draft.Write(key, "Visible")); };
                check.Unchecked += (_, _) => { if (!_syncing) Apply(() => _editor.Draft.Write(key, "Collapsed")); };
                _refresh.Add(() => check.IsChecked = Value(key) is Visibility.Visible);
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
                    if (_syncing || box.Text.Trim() == Describe(Value(key))) return;
                    Apply(() => _editor.Draft.Write(key, box.Text));
                }
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Commit(); } };
                box.LostFocus += (_, _) => Commit();
                _refresh.Add(() => { if (!box.IsKeyboardFocusWithin) box.Text = Describe(Value(key)); });
                var row = new DockPanel { Margin = new Thickness(6, 3, 0, 3) };
                var frame = FileBackupUi.Framed(box); frame.Padding = new Thickness(8, 3, 8, 3);
                DockPanel.SetDock(frame, Dock.Right); row.Children.Add(frame);
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                return row;
            }
        }
    }

    internal static string Describe(object? value)
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
            double d => N(d),
            _ => ""
        };
    }
}

// NEW (edit in place): what Every setting needs from the editor.
internal sealed partial class ThemeEditor
{
    internal object? ValueOf(string key) => Value(key);

    internal void Apply(Func<ThemeStudioDocument> next) => TryChange(next);

    internal void ChooseColor(string key, string label, Window over) => PickColor(key, label, over);

    internal string FontName(string key) => FontLabel(key);

    /// <summary>A menu of fonts for one kind of text. <paramref name="done"/> runs after a font is chosen.</summary>
    internal void ChooseFont(FrameworkElement target, string key, Action done)
    {
        var menu = Menu(target);
        menu.Closed += (_, _) => done();
        FillFonts(menu, key);
        menu.IsOpen = true;
    }
}
