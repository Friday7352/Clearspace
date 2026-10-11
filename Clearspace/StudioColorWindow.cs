using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clearspace.Controls;
using Clearspace.Services;

namespace Clearspace;

// CHANGED (theme studio, round 2): the colour picker, tidied.
//   - Says which part is being colored, and shows the old colour beside the new one.
//   - The suggested colours that sat under the studio's colour button live here now, next to your saved ones.
//   - Saving a colour is a small + at the start of your saved colours instead of a full-width button.
//   - Sliders show their value; the swatches are plain round samples (they used to be Windows buttons
//     with a colour inside, which drew a grey frame round each one).
//   - "preview" (optional) is told every colour as you pick it, so the studio's preview can follow along.
internal sealed class StudioColorWindow : Window
{
    private readonly StudioColorWheel _wheel = new();
    private readonly Slider _brightness = new() { Minimum = 0, Maximum = 1, Value = 1 };
    private readonly Slider _opacity = new() { Minimum = 0, Maximum = 255, Value = 255 };
    private readonly TextBlock _brightnessValue = new() { FontSize = 12 }, _opacityValue = new() { FontSize = 12 };
    private readonly Border _before, _after;
    private readonly TextBox _hex = new() { MaxLength = 9 };
    private readonly WrapPanel _saved = new();
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _use = FileBackupUi.TextButton("Use color", true);
    private readonly List<string> _colors = StudioLibrary.Colors();
    private readonly Action<string>? _preview;
    private bool _syncing, _ready;
    private readonly bool _allowOpacity;
    internal string SelectedColor { get; private set; } = "#FFFFFFFF";

    internal StudioColorWindow(Window owner, string color, bool allowOpacity, Action<string>? preview = null, string? part = null)
    {
        if (owner.IsVisible) Owner = owner;
        _preview = preview;
        Title = part is null ? "Choose a color" : "Color · " + part;
        Width = 380; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _allowOpacity = allowOpacity;
        // CHANGED (theme studio, round 2): follows the app's theme like the studio, rather than the owner's copy of Dark.
        SetResourceReference(BackgroundProperty, "Base"); SetResourceReference(ForegroundProperty, "Ink"); SetResourceReference(FontFamilyProperty, "UIFont");
        FontSize = 13;
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        _use.SetResourceReference(BackgroundProperty, "Accent"); _use.SetResourceReference(ForegroundProperty, "AccentInk");
        _use.FontWeight = FontWeights.SemiBold;

        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        panel.SetResourceReference(Panel.BackgroundProperty, "Base");
        Content = panel;
        panel.Children.Add(new TextBlock { Text = part ?? "Choose a color", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14), TextTrimming = TextTrimming.CharacterEllipsis });

        _wheel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(_wheel);

        void SliderRow(string label, Slider slider, TextBlock value)
        {
            var top = new DockPanel { Margin = new Thickness(0, 12, 0, 2) };
            value.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
            DockPanel.SetDock(value, Dock.Right); top.Children.Add(value);
            top.Children.Add(new TextBlock { Text = label, FontSize = 12.5 });
            panel.Children.Add(top);
            slider.SetResourceReference(StyleProperty, "MediaSlider");
            System.Windows.Automation.AutomationProperties.SetName(slider, label);
            panel.Children.Add(slider);
            slider.ValueChanged += (_, _) => Update();
        }
        SliderRow("Brightness", _brightness, _brightnessValue);
        if (allowOpacity) SliderRow("Opacity", _opacity, _opacityValue);

        // Before | after, and the code
        var (beforeOuter, beforeInner) = StudioUi.Swatch(40);
        var (afterOuter, afterInner) = StudioUi.Swatch(40);
        _before = beforeInner; _after = afterInner;
        beforeOuter.Width = afterOuter.Width = double.NaN; beforeOuter.Height = afterOuter.Height = 36;
        beforeOuter.CornerRadius = new CornerRadius(8, 0, 0, 8); beforeInner.CornerRadius = new CornerRadius(7, 0, 0, 7);
        afterOuter.CornerRadius = new CornerRadius(0, 8, 8, 0); afterInner.CornerRadius = new CornerRadius(0, 7, 7, 0);
        afterOuter.BorderThickness = new Thickness(0, 1, 1, 1);
        beforeOuter.ToolTip = "Before"; afterOuter.ToolTip = "New";
        var compare = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        compare.ColumnDefinitions.Add(new ColumnDefinition()); compare.ColumnDefinitions.Add(new ColumnDefinition());
        compare.Children.Add(beforeOuter); Grid.SetColumn(afterOuter, 1); compare.Children.Add(afterOuter);
        panel.Children.Add(compare);
        var compareLabels = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        compareLabels.ColumnDefinitions.Add(new ColumnDefinition()); compareLabels.ColumnDefinitions.Add(new ColumnDefinition());
        var beforeLabel = FileBackupUi.Muted("Before"); beforeLabel.FontSize = 11.5;
        var afterLabel = FileBackupUi.Muted("New"); afterLabel.FontSize = 11.5; Grid.SetColumn(afterLabel, 1);
        compareLabels.Children.Add(beforeLabel); compareLabels.Children.Add(afterLabel);
        panel.Children.Add(compareLabels);

        var codeRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var codeLabel = new TextBlock { Text = "Code", VerticalAlignment = VerticalAlignment.Center, Width = 44, FontSize = 12.5 };
        DockPanel.SetDock(codeLabel, Dock.Left); codeRow.Children.Add(codeLabel);
        System.Windows.Automation.AutomationProperties.SetName(_hex, "Color code");
        _hex.ToolTip = allowOpacity ? "#RRGGBB, or #AARRGGBB with opacity first" : "#RRGGBB";
        var hexFrame = FileBackupUi.Framed(_hex);
        _hex.SetResourceReference(FontFamilyProperty, "MonoFont");
        codeRow.Children.Add(hexFrame);
        panel.Children.Add(codeRow);
        _hex.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            try
            {
                if (_hex.Text.Length is not (7 or 9) || !_hex.Text.StartsWith('#')) throw new FormatException();
                var typed = _hex.Text; var caret = _hex.CaretIndex;
                Set((Color)ColorConverter.ConvertFromString(typed)!);
                _syncing = true; _hex.Text = typed; _hex.CaretIndex = caret; _syncing = false;
                _use.IsEnabled = true; _message.Text = "";
            }
            catch (Exception e) when (e is FormatException or NotSupportedException) { _use.IsEnabled = false; _message.Text = "Enter a color such as #64D8CF."; }
        };

        // Suggestions and your saved colours
        panel.Children.Add(SectionLabel("Suggestions"));
        var suggestions = new WrapPanel();
        foreach (var hex in StudioUi.SuggestedColors) // CHANGED (edit in place): was ThemeStudioWindow.SuggestedColors
            suggestions.Children.Add(Dot(hex, "Use " + hex, () => Set((Color)ColorConverter.ConvertFromString(hex)!)));
        panel.Children.Add(suggestions);
        panel.Children.Add(SectionLabel("Your colors"));
        panel.Children.Add(new ScrollViewer { Content = _saved, MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _message.Margin = new Thickness(0, 6, 0, 0);
        _message.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        panel.Children.Add(_message);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = FileBackupUi.TextButton("Cancel", false); cancel.IsCancel = true; cancel.Click += (_, _) => Close();
        _use.IsDefault = true;
        _use.Click += (_, _) => DialogResult = true; actions.Children.Add(cancel); actions.Children.Add(_use); panel.Children.Add(actions);

        var start = (Color)ColorConverter.ConvertFromString(color)!;
        if (!allowOpacity) start.A = 255;
        _before.Background = new SolidColorBrush(start);
        _wheel.Changed += Update; Set(start); RenderSaved();
        _ready = true;
    }

    private static TextBlock SectionLabel(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        return label;
    }

    // A round colour sample that picks its colour when clicked.
    private static Border Dot(string hex, string name, Action pick)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex)!;
        var dot = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 6, 6),
            Background = new SolidColorBrush(color), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Focusable = true, ToolTip = hex
        };
        dot.SetResourceReference(Border.BorderBrushProperty, "Line");
        dot.MouseEnter += (_, _) => dot.SetResourceReference(Border.BorderBrushProperty, "Ink");
        dot.MouseLeave += (_, _) => dot.SetResourceReference(Border.BorderBrushProperty, "Line");
        dot.MouseLeftButtonUp += (_, e) => { e.Handled = true; pick(); };
        dot.KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { e.Handled = true; pick(); } };
        System.Windows.Automation.AutomationProperties.SetName(dot, name);
        return dot;
    }

    private void Set(Color c)
    {
        _syncing = true;
        var max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255d; var min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255d; var d = max - min;
        var h = d == 0 ? 0 : max == c.R / 255d ? 60 * ((c.G - c.B) / 255d / d % 6) : max == c.G / 255d ? 60 * ((c.B - c.R) / 255d / d + 2) : 60 * ((c.R - c.G) / 255d / d + 4);
        _wheel.Set((h + 360) % 360, max == 0 ? 0 : d / max); _brightness.Value = max; _opacity.Value = _allowOpacity ? c.A : 255;
        _syncing = false; Update();
    }

    private void Update()
    {
        if (_syncing) return;
        var c = StudioColorWheel.FromHsv(_wheel.Hue, _wheel.Saturation, _brightness.Value); c.A = (byte)_opacity.Value;
        SelectedColor = c.ToString(); _after.Background = new SolidColorBrush(c);
        _brightnessValue.Text = $"{_brightness.Value * 100:0}%";
        _opacityValue.Text = $"{_opacity.Value / 255 * 100:0}%";
        _syncing = true; _hex.Text = _allowOpacity ? SelectedColor : "#" + SelectedColor[3..]; _syncing = false; _use.IsEnabled = true;
        if (_ready) _preview?.Invoke(SelectedColor);
    }

    private void RenderSaved()
    {
        _saved.Children.Clear();
        // The first place is the + that saves the colour you are on.
        var add = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Focusable = true, ToolTip = "Save this color for all your themes" };
        add.SetResourceReference(Border.BorderBrushProperty, "Line");
        var plus = new TextBlock { Text = "", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        plus.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        plus.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        add.Child = plus;
        void SaveCurrent()
        {
            if (!_use.IsEnabled) return;
            if (!_colors.Contains(SelectedColor)) _colors.Insert(0, SelectedColor);
            try { StudioLibrary.SaveColors(_colors); RenderSaved(); _message.Text = "Saved. Right-click a saved color to remove it."; }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { _message.Text = "Could not save this color: " + e.Message; }
        }
        add.MouseLeftButtonUp += (_, e) => { e.Handled = true; SaveCurrent(); };
        add.KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { e.Handled = true; SaveCurrent(); } };
        System.Windows.Automation.AutomationProperties.SetName(add, "Save this color");
        _saved.Children.Add(add);

        foreach (var color in _colors.Take(64))
        {
            var dot = Dot(color, "Use saved color " + color, () => Set((Color)ColorConverter.ConvertFromString(color)!));
            var menu = new ContextMenu(); var remove = new MenuItem { Header = "Remove saved color" };
            remove.Click += (_, _) =>
            {
                try { _colors.Remove(color); StudioLibrary.SaveColors(_colors); RenderSaved(); }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { _message.Text = "Could not remove color: " + e.Message; }
            };
            menu.Items.Add(remove); dot.ContextMenu = menu; _saved.Children.Add(dot);
        }
    }
}
