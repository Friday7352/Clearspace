using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clearspace.Services;

namespace Clearspace;

// NEW (pattern maker): draw your own repeating pattern for a theme, one square at a time.
//   - Paint on the grid with the pattern colour, the accent or an extra colour of your own; right-click (or the
//     eraser) clears. Clicking a square that already has the colour clears it, so one click both adds and takes away.
//   - Mirror left-right and/or top-bottom paints the matching squares too, for symmetrical tiles.
//   - Clear, fill, invert, shift (to line the tile up), and sprinkle (random squares) for quick starts.
//   - The large sample shows the tile repeated, as it will look in Clearspace.
//   - The grid can be 8, 12, 16 or 24 squares across; changing it keeps the drawing, resized.
// The result goes to the studio, which writes it into the theme; "Keep in My patterns" also saves it to the
// studio's library (StudioLibrary.Patterns) so other themes can use it.
internal sealed class StudioPatternWindow : Window
{
    private readonly PatternPad _pad = new();
    private readonly Border _sample = new() { Height = 170, BorderThickness = new Thickness(1) };
    private readonly TextBox _name = new() { MaxLength = 40 };
    private readonly WrapPanel _sizes = new(), _pens = new(), _mirrors = new();
    private readonly CheckBox _keep = FileBackupUi.Tick("Keep in My patterns", "Save this pattern so any theme can use it");
    private readonly Color _ink, _accent, _background;
    private string _extra;
    internal StudioCustomPattern? Result { get; private set; }

    internal StudioPatternWindow(Window owner, Color ink, Color accent, Color background, StudioCustomPattern? start)
    {
        if (owner.IsVisible) Owner = owner;
        _ink = ink; _accent = accent; _background = background;
        _extra = start?.Extra ?? "#FF9DCB";
        Title = start is null ? "New pattern" : "Pattern · " + start.Name;
        SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Base"); SetResourceReference(ForegroundProperty, "Ink"); SetResourceReference(FontFamilyProperty, "UIFont");
        FontSize = 13;
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);

        var page = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        page.SetResourceReference(Panel.BackgroundProperty, "Base");
        Content = page;
        page.Children.Add(new TextBlock { Text = "Draw a pattern", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        page.Children.Add(columns);

        // Left: the grid and its tools.
        var left = new StackPanel();
        var padFrame = new Border { BorderThickness = new Thickness(1), Child = _pad, HorizontalAlignment = HorizontalAlignment.Left };
        padFrame.SetResourceReference(Border.BorderBrushProperty, "Line");
        left.Children.Add(padFrame);
        var tools = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), MaxWidth = PatternPad.Side + 2 };
        tools.Children.Add(FileBackupUi.Chip("Clear", false, () => _pad.Apply(c => '0')));
        tools.Children.Add(FileBackupUi.Chip("Fill", false, () => _pad.Apply(c => _pad.Pen == '0' ? '0' : _pad.Pen)));
        tools.Children.Add(FileBackupUi.Chip("Invert", false, () => _pad.Apply(c => c == '0' ? (_pad.Pen == '0' ? '1' : _pad.Pen) : '0')));
        tools.Children.Add(FileBackupUi.Chip("Shift →", false, () => _pad.Shift(1, 0)));
        tools.Children.Add(FileBackupUi.Chip("Shift ↓", false, () => _pad.Shift(0, 1)));
        tools.Children.Add(FileBackupUi.Chip("Sprinkle", false, () => _pad.Sprinkle()));
        left.Children.Add(tools);
        columns.Children.Add(left);

        // Right: colours, grid size, mirrors, the repeated sample, the name.
        var right = new StackPanel(); Grid.SetColumn(right, 2); columns.Children.Add(right);
        right.Children.Add(Heading("Paint with"));
        right.Children.Add(_pens);
        right.Children.Add(Heading("Squares across"));
        right.Children.Add(_sizes);
        right.Children.Add(Heading("Mirror"));
        right.Children.Add(_mirrors);
        right.Children.Add(Heading("How it repeats"));
        _sample.SetResourceReference(Border.BorderBrushProperty, "Line");
        _sample.SetResourceReference(Border.CornerRadiusProperty, "R8");
        right.Children.Add(_sample);
        right.Children.Add(Heading("Name"));
        AutomationProperties.SetName(_name, "Pattern name");
        _name.Text = start?.Name ?? "My pattern";
        right.Children.Add(FileBackupUi.Framed(_name));
        _keep.IsChecked = true; _keep.Margin = new Thickness(4, 10, 0, 0);
        right.Children.Add(_keep);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = FileBackupUi.TextButton("Cancel", false); cancel.IsCancel = true; cancel.Click += (_, _) => Close();
        var use = FileBackupUi.TextButton("Use pattern", true);
        use.SetResourceReference(BackgroundProperty, "Accent"); use.SetResourceReference(ForegroundProperty, "AccentInk"); use.FontWeight = FontWeights.SemiBold;
        use.IsDefault = true;
        use.Click += (_, _) => Finish();
        actions.Children.Add(cancel); actions.Children.Add(use);
        page.Children.Add(actions);

        if (start is { IsValid: true }) _pad.Load(start.Size, start.Cells);
        _pad.Changed += ShowSample;
        BuildPens(); BuildSizes(); BuildMirrors(); ShowSample();
    }

    private static TextBlock Heading(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        return label;
    }

    private Color Extra => ThemeDesign.TryColor(_extra, out var c) ? c : Colors.HotPink;

    private void BuildPens()
    {
        _pad.Paints = [new SolidColorBrush(_background), new SolidColorBrush(_ink), new SolidColorBrush(_accent), new SolidColorBrush(Extra)];
        _pad.InvalidateVisual();
        _pens.Children.Clear();
        foreach (var (pen, name, color) in new (char, string, Color?)[] { ('1', "Pattern color", _ink), ('2', "Accent", _accent), ('3', "Extra color", Extra), ('0', "Eraser", null) })
        {
            var chosen = _pad.Pen == pen;
            var dot = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(chosen ? 2.5 : 1), Margin = new Thickness(0, 0, 8, 6), Cursor = Cursors.Hand, Focusable = true };
            dot.SetResourceReference(Border.BorderBrushProperty, chosen ? "Accent" : "Line");
            if (color is { } c) dot.Background = new SolidColorBrush(c);
            else
            {
                dot.Background = StudioUi.Checker;
                var glyph = new TextBlock { Text = "", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Black };
                glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
                dot.Child = glyph;
            }
            dot.ToolTip = pen == '3' ? "Extra color · click again to change it" : name;
            AutomationProperties.SetName(dot, name);
            void Pick()
            {
                if (pen == '3' && _pad.Pen == '3')
                {
                    var picker = new StudioColorWindow(this, "#FF" + _extra[1..], false, null, "Extra pattern color");
                    if (picker.ShowDialog() == true) _extra = "#" + picker.SelectedColor[3..];
                }
                _pad.Pen = pen; BuildPens(); ShowSample();
            }
            dot.MouseLeftButtonUp += (_, e) => { e.Handled = true; Pick(); };
            dot.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { e.Handled = true; Pick(); } };
            _pens.Children.Add(dot);
        }
    }

    private void BuildSizes()
    {
        _sizes.Children.Clear();
        foreach (var size in StudioCustomPattern.Sizes)
            _sizes.Children.Add(FileBackupUi.Chip(size.ToString(System.Globalization.CultureInfo.InvariantCulture), _pad.Count == size, () => { _pad.Resize(size); BuildSizes(); }));
    }

    private void BuildMirrors()
    {
        _mirrors.Children.Clear();
        _mirrors.Children.Add(FileBackupUi.Chip("Left ↔ right", _pad.MirrorX, () => { _pad.MirrorX = !_pad.MirrorX; BuildMirrors(); }));
        _mirrors.Children.Add(FileBackupUi.Chip("Top ↕ bottom", _pad.MirrorY, () => { _pad.MirrorY = !_pad.MirrorY; BuildMirrors(); }));
    }

    private StudioCustomPattern Current() =>
        new(ThemeManifest.Tidy(_name.Text, 40) is { Length: > 0 } name ? name : "My pattern", _pad.Count, new string(_pad.Cells), _extra);

    private void ShowSample()
    {
        try
        {
            var markup = StudioPatterns.Custom(Current(), _ink, _accent, _background, 1.5, 1).ToString();
            _sample.Background = (Brush)System.Windows.Markup.XamlReader.Parse(markup);
        }
        catch (Exception error) when (error is ArgumentException or System.Windows.Markup.XamlParseException or InvalidCastException)
        {
            _sample.Background = new SolidColorBrush(_background);
        }
    }

    private void Finish()
    {
        var pattern = Current();
        if (!pattern.IsValid) return;
        if (_keep.IsChecked == true)
        {
            try
            {
                var library = StudioLibrary.Patterns();
                library.RemoveAll(p => string.Equals(p.Name, pattern.Name, StringComparison.OrdinalIgnoreCase));
                library.Insert(0, pattern);
                StudioLibrary.SavePatterns(library);
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { /* the theme still gets it */ }
        }
        Result = pattern;
        DialogResult = true;
    }
}

// NEW (pattern maker): the grid you paint on.
internal sealed class PatternPad : FrameworkElement
{
    internal const double Side = 288;
    internal int Count { get; private set; } = 12;
    internal char[] Cells { get; private set; } = new string('0', 144).ToCharArray();
    internal Brush[] Paints { get; set; } = [Brushes.Black, Brushes.White, Brushes.Orange, Brushes.HotPink];
    internal char Pen { get; set; } = '1';
    internal bool MirrorX { get; set; }
    internal bool MirrorY { get; set; }
    internal event Action? Changed;
    private char _stroke;

    internal PatternPad()
    {
        Width = Height = Side; Focusable = true; Cursor = Cursors.Pen;
        AutomationProperties.SetName(this, "Pattern grid. Click squares to paint them.");
    }

    internal void Load(int count, string cells)
    {
        Count = count; Cells = cells.ToCharArray(); InvalidateVisual();
    }

    internal void Resize(int count)
    {
        if (count == Count) return;
        var next = new char[count * count];
        for (var y = 0; y < count; y++)
            for (var x = 0; x < count; x++)
                next[y * count + x] = Cells[(y * Count / count) * Count + x * Count / count];
        Count = count; Cells = next; InvalidateVisual(); Changed?.Invoke();
    }

    internal void Apply(Func<char, char> change)
    {
        for (var i = 0; i < Cells.Length; i++) Cells[i] = change(Cells[i]);
        InvalidateVisual(); Changed?.Invoke();
    }

    internal void Shift(int dx, int dy)
    {
        var next = new char[Cells.Length];
        for (var y = 0; y < Count; y++)
            for (var x = 0; x < Count; x++)
                next[((y + dy) % Count) * Count + (x + dx) % Count] = Cells[y * Count + x];
        Cells = next; InvalidateVisual(); Changed?.Invoke();
    }

    internal void Sprinkle()
    {
        var pen = Pen == '0' ? '1' : Pen;
        for (var i = 0; i < Cells.Length; i++)
            if (Random.Shared.NextDouble() < .12) Cells[i] = pen;
        InvalidateVisual(); Changed?.Invoke();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var cell = Side / Count;
        dc.DrawRectangle(Paints[0], null, new Rect(0, 0, Side, Side));
        for (var y = 0; y < Count; y++)
            for (var x = 0; x < Count; x++)
            {
                var c = Cells[y * Count + x];
                if (c != '0') dc.DrawRectangle(Paints[c - '0'], null, new Rect(x * cell, y * cell, cell, cell));
            }
        var line = new Pen(new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), 1);
        line.Freeze();
        for (var i = 1; i < Count; i++)
        {
            var at = Math.Round(i * cell) + .5;
            dc.DrawLine(line, new Point(at, 0), new Point(at, Side));
            dc.DrawLine(line, new Point(0, at), new Point(Side, at));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) => Begin(e, erase: false);
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) => Begin(e, erase: true);

    private void Begin(MouseButtonEventArgs e, bool erase)
    {
        Focus(); CaptureMouse();
        var index = IndexAt(e.GetPosition(this));
        // A first click on a square that already has this colour starts an erasing stroke.
        _stroke = erase || Pen == '0' ? '0' : index >= 0 && Cells[index] == Pen ? '0' : Pen;
        PaintAt(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured && (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed)) PaintAt(e.GetPosition(this));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
    }

    private int IndexAt(Point p)
    {
        var cell = Side / Count;
        var x = (int)(p.X / cell); var y = (int)(p.Y / cell);
        return x < 0 || y < 0 || x >= Count || y >= Count ? -1 : y * Count + x;
    }

    private void PaintAt(Point p)
    {
        var index = IndexAt(p);
        if (index < 0) return;
        var x = index % Count; var y = index / Count;
        var changed = false;
        void Set(int cx, int cy) { var i = cy * Count + cx; if (Cells[i] != _stroke) { Cells[i] = _stroke; changed = true; } }
        Set(x, y);
        if (MirrorX) Set(Count - 1 - x, y);
        if (MirrorY) Set(x, Count - 1 - y);
        if (MirrorX && MirrorY) Set(Count - 1 - x, Count - 1 - y);
        if (!changed) return;
        InvalidateVisual(); Changed?.Invoke();
    }
}
