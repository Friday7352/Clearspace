using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clearspace.Services;

namespace Clearspace;

// NEW (sticker editor): cut a sticker out of its background and crop it, before or after it is on the window.
//   - Remove background: clears the area joined to the picture's edges that is close to the edges' most common
//     colour (a flood fill from the border). Works best on plain or simple backgrounds (product shots, clipart,
//     screenshots, drawings); a busy photo may need the click tools after it.
//   - Erase by click / Bring back by click: clears (or restores) the patch of similar colour you click on.
//   - Tolerance: how different a colour may be and still count as the same; Edge softness: feathers the cut edge.
//   - Crop: drag a frame on the picture (drag inside it to move it). "Window shape" keeps the frame the shape of
//     the window, for a picture that fills the window: cropping is how you choose which part shows.
//   - Undo and Start over; the result is a PNG with a see-through background.
// It all runs here on the computer; nothing is sent anywhere.
internal sealed class StudioImageWindow : Window
{
    private const double StageWidth = 600, StageHeight = 420;
    private readonly int _w, _h;
    private readonly byte[] _pixels, _original;      // Bgra32, straight alpha
    private readonly Stack<byte[]> _undo = new();
    private readonly WriteableBitmap _bitmap;
    private readonly double _scale;
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent, ClipToBounds = true };
    private readonly Border _cropFrame = new() { BorderThickness = new Thickness(2), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Slider _tolerance = new() { Minimum = 4, Maximum = 120, Value = 36, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly Slider _softness = new() { Minimum = 0, Maximum = 6, Value = 1, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _toleranceValue = new() { FontSize = 12 }, _softnessValue = new() { FontSize = 12 };
    private readonly WrapPanel _tools = new(), _shapes = new();
    private readonly TextBlock _message = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _undoButton = FileBackupUi.TextButton("Undo", false);
    private readonly double _windowAspect;
    private string _tool = "Erase";                  // Erase, Restore or Crop
    private double _aspect;                          // 0: free
    private Int32Rect _crop;                         // in picture pixels; empty: the whole picture
    private Point _dragStart; private Int32Rect _dragStartCrop; private bool _dragging, _dragMove;
    internal byte[]? Result { get; private set; }

    internal StudioImageWindow(Window owner, byte[] image, bool fillsWindow, double windowAspect)
    {
        if (owner.IsVisible) Owner = owner;
        _windowAspect = windowAspect > 0 ? windowAspect : 1264d / 760;
        Title = "Edit picture";
        SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Base"); SetResourceReference(ForegroundProperty, "Ink"); SetResourceReference(FontFamilyProperty, "UIFont");
        FontSize = 13;
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);

        // The picture, at most 1600 pixels across (the size stickers are kept at), as straight-alpha BGRA.
        var source = StudioPicture.Load(image);
        var fit = Math.Min(1d, 1600d / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource sized = fit < 1 ? new TransformedBitmap(source, new ScaleTransform(fit, fit)) : source;
        var converted = new FormatConvertedBitmap(sized, PixelFormats.Bgra32, null, 0);
        _w = converted.PixelWidth; _h = converted.PixelHeight;
        _pixels = new byte[_w * _h * 4];
        converted.CopyPixels(_pixels, _w * 4, 0);
        _original = (byte[])_pixels.Clone();
        _bitmap = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Bgra32, null);
        _scale = Math.Min(StageWidth / _w, StageHeight / _h);

        var page = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        page.SetResourceReference(Panel.BackgroundProperty, "Base");
        Content = page;
        page.Children.Add(new TextBlock { Text = "Edit picture", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        page.Children.Add(columns);

        // The picture on a checkerboard, with the crop frame over it.
        var shown = new Size(_w * _scale, _h * _scale);
        var stage = new Grid { Width = shown.Width, Height = shown.Height, Background = StudioUi.Checker };
        stage.Children.Add(new Image { Source = _bitmap, Stretch = Stretch.Fill });
        _cropFrame.SetResourceReference(Border.BorderBrushProperty, "Accent");
        _overlay.Children.Add(_cropFrame);
        stage.Children.Add(_overlay);
        _overlay.MouseLeftButtonDown += OnDown;
        _overlay.MouseMove += OnMove;
        _overlay.MouseLeftButtonUp += OnUp;
        var stageFrame = new Border { Width = StageWidth + 2, Height = StageHeight + 2, BorderThickness = new Thickness(1), Child = stage, VerticalAlignment = VerticalAlignment.Top };
        stageFrame.SetResourceReference(Border.BorderBrushProperty, "Line");
        stageFrame.SetResourceReference(Border.BackgroundProperty, "Surface");
        columns.Children.Add(stageFrame);

        // The tools.
        var right = new StackPanel(); Grid.SetColumn(right, 2); columns.Children.Add(right);
        var auto = FileBackupUi.TextButton("Remove background", true);
        auto.SetResourceReference(BackgroundProperty, "Accent"); auto.SetResourceReference(ForegroundProperty, "AccentInk"); auto.FontWeight = FontWeights.SemiBold;
        auto.HorizontalAlignment = HorizontalAlignment.Stretch; auto.Margin = new Thickness(0);
        auto.ToolTip = "Clears the background joined to the picture's edges";
        auto.Click += (_, _) => RemoveBackground();
        right.Children.Add(auto);
        right.Children.Add(Heading("Click on the picture to"));
        right.Children.Add(_tools);
        foreach (var (slider, value, name) in new[] { (_tolerance, _toleranceValue, "Tolerance"), (_softness, _softnessValue, "Edge softness") })
        {
            var top = new DockPanel { Margin = new Thickness(0, 10, 0, 2) };
            value.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
            DockPanel.SetDock(value, Dock.Right); top.Children.Add(value);
            top.Children.Add(new TextBlock { Text = name, FontSize = 12.5 });
            right.Children.Add(top);
            slider.SetResourceReference(StyleProperty, "MediaSlider");
            AutomationProperties.SetName(slider, name);
            right.Children.Add(slider);
        }
        _tolerance.ToolTip = "Higher clears more colors that are close to the one picked";
        _softness.ToolTip = "Feathers the cut edge so it blends in";
        _tolerance.ValueChanged += (_, _) => _toleranceValue.Text = $"{_tolerance.Value:0}";
        _softness.ValueChanged += (_, _) => { _softnessValue.Text = $"{_softness.Value:0} px"; Redraw(); };
        right.Children.Add(Heading("Crop shape"));
        right.Children.Add(_shapes);
        var history = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        _undoButton.Margin = new Thickness(0, 0, 6, 6); _undoButton.Click += (_, _) => Undo();
        var over = FileBackupUi.TextButton("Start over", false); over.Margin = new Thickness(0, 0, 6, 6);
        over.Click += (_, _) => { Remember(); Array.Copy(_original, _pixels, _pixels.Length); _crop = Int32Rect.Empty; Redraw(); };
        history.Children.Add(_undoButton); history.Children.Add(over);
        right.Children.Add(history);
        _message.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        right.Children.Add(_message);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = FileBackupUi.TextButton("Cancel", false); cancel.IsCancel = true; cancel.Click += (_, _) => Close();
        var use = FileBackupUi.TextButton("Use picture", true);
        use.SetResourceReference(BackgroundProperty, "Accent"); use.SetResourceReference(ForegroundProperty, "AccentInk"); use.FontWeight = FontWeights.SemiBold;
        use.IsDefault = true; use.Click += (_, _) => Finish();
        actions.Children.Add(cancel); actions.Children.Add(use);
        page.Children.Add(actions);

        _aspect = fillsWindow ? _windowAspect : 0;
        if (fillsWindow) _tool = "Crop";
        BuildTools(); BuildShapes();
        _toleranceValue.Text = $"{_tolerance.Value:0}"; _softnessValue.Text = $"{_softness.Value:0} px";
        if (fillsWindow) StartCropInShape();
        Redraw();
        Say(fillsWindow ? "Drag the frame to choose which part of the picture fills the window." : "Try Remove background, then click any bits it missed.");
    }

    private static TextBlock Heading(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        return label;
    }

    private void Say(string text) => _message.Text = text;

    private void BuildTools()
    {
        _tools.Children.Clear();
        foreach (var (id, label, tip) in new[] { ("Erase", "Erase", "Clears the patch of similar color you click"), ("Restore", "Bring back", "Brings back the patch you click"), ("Crop", "Crop", "Drag a frame; drag inside it to move it") })
        {
            var chip = FileBackupUi.Chip(label, _tool == id, () => { _tool = id; BuildTools(); _overlay.Cursor = id == "Crop" ? Cursors.Cross : Cursors.Hand; });
            chip.ToolTip = tip;
            _tools.Children.Add(chip);
        }
        _overlay.Cursor = _tool == "Crop" ? Cursors.Cross : Cursors.Hand;
    }

    private void BuildShapes()
    {
        _shapes.Children.Clear();
        foreach (var (label, aspect) in new[] { ("Free", 0d), ("Window shape", _windowAspect), ("Square", 1d) })
            _shapes.Children.Add(FileBackupUi.Chip(label, Math.Abs(_aspect - aspect) < .001, () =>
            {
                _aspect = aspect; BuildShapes();
                if (aspect > 0) StartCropInShape();
                Redraw();
            }));
        var none = FileBackupUi.Chip("No crop", false, () => { _crop = Int32Rect.Empty; Redraw(); });
        none.ToolTip = "Use the whole picture";
        _shapes.Children.Add(none);
    }

    // The largest frame of the chosen shape, centred (or kept where the current one is).
    private void StartCropInShape()
    {
        if (_aspect <= 0) return;
        double width = _w, height = _w / _aspect;
        if (height > _h) { height = _h; width = _h * _aspect; }
        var cx = _crop.IsEmpty ? _w / 2d : _crop.X + _crop.Width / 2d;
        var cy = _crop.IsEmpty ? _h / 2d : _crop.Y + _crop.Height / 2d;
        _crop = Clamp(new Int32Rect((int)Math.Round(cx - width / 2), (int)Math.Round(cy - height / 2), (int)Math.Round(width), (int)Math.Round(height)));
        _tool = "Crop"; BuildTools();
    }

    private Int32Rect Clamp(Int32Rect r)
    {
        var width = Math.Clamp(r.Width, 8, _w); var height = Math.Clamp(r.Height, 8, _h);
        return new Int32Rect(Math.Clamp(r.X, 0, _w - width), Math.Clamp(r.Y, 0, _h - height), width, height);
    }

    // ---------------------------------------------------------------- showing it

    private void Redraw()
    {
        var shown = Feathered();
        _bitmap.WritePixels(new Int32Rect(0, 0, _w, _h), shown, _w * 4, 0);
        _undoButton.IsEnabled = _undo.Count > 0;
        if (_crop.IsEmpty) { _cropFrame.Visibility = Visibility.Collapsed; return; }
        _cropFrame.Visibility = Visibility.Visible;
        Canvas.SetLeft(_cropFrame, _crop.X * _scale); Canvas.SetTop(_cropFrame, _crop.Y * _scale);
        _cropFrame.Width = _crop.Width * _scale; _cropFrame.Height = _crop.Height * _scale;
    }

    // The pixels with the cut edge softened: alpha is blurred and never made more solid than it was.
    private byte[] Feathered()
    {
        var copy = (byte[])_pixels.Clone();
        var radius = (int)_softness.Value;
        if (radius <= 0) return copy;
        var alpha = new float[_w * _h];
        for (var i = 0; i < alpha.Length; i++) alpha[i] = _pixels[i * 4 + 3];
        var blurred = BoxBlur(BoxBlur(alpha, radius, horizontal: true), radius, horizontal: false);
        for (var i = 0; i < alpha.Length; i++) copy[i * 4 + 3] = (byte)Math.Min(alpha[i], blurred[i]);
        return copy;
    }

    private float[] BoxBlur(float[] input, int radius, bool horizontal)
    {
        var output = new float[input.Length];
        int lines = horizontal ? _h : _w, length = horizontal ? _w : _h;
        for (var line = 0; line < lines; line++)
        {
            int At(int i) => horizontal ? line * _w + i : i * _w + line;
            double sum = 0; var count = 0;
            for (var i = 0; i <= Math.Min(radius, length - 1); i++) { sum += input[At(i)]; count++; }
            for (var i = 0; i < length; i++)
            {
                output[At(i)] = (float)(sum / count);
                var add = i + radius + 1; var drop = i - radius;
                if (add < length) { sum += input[At(add)]; count++; }
                if (drop >= 0) { sum -= input[At(drop)]; count--; }
            }
        }
        return output;
    }

    // ---------------------------------------------------------------- cutting out

    private void Remember()
    {
        _undo.Push((byte[])_pixels.Clone());
        if (_undo.Count > 20) { var keep = _undo.Take(20).Reverse().ToArray(); _undo.Clear(); foreach (var item in keep) _undo.Push(item); }
    }

    private void Undo()
    {
        if (!_undo.TryPop(out var before)) return;
        Array.Copy(before, _pixels, _pixels.Length); Redraw();
    }

    private double Distance(byte[] p, int a, int b)
    {
        double db = p[a * 4] - p[b * 4], dg = p[a * 4 + 1] - p[b * 4 + 1], dr = p[a * 4 + 2] - p[b * 4 + 2];
        return Math.Sqrt(dr * dr * .3 + dg * dg * .59 + db * db * .11);
    }

    // Grows from the seeds through neighbours of similar colour: each step may differ a little from the last
    // and the whole patch a little more from where it started, so soft gradients are followed but edges stop it.
    private bool[] Flood(IEnumerable<int> seeds, byte[] pixels, double tolerance, bool skipClear)
    {
        var inside = new bool[_w * _h];
        var origin = new int[_w * _h];
        var queue = new Queue<int>();
        foreach (var seed in seeds)
        {
            if (inside[seed] || (skipClear && pixels[seed * 4 + 3] == 0)) continue;
            inside[seed] = true; origin[seed] = seed; queue.Enqueue(seed);
        }
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            int x = i % _w, y = i / _w;
            foreach (var n in new[] { x > 0 ? i - 1 : -1, x < _w - 1 ? i + 1 : -1, y > 0 ? i - _w : -1, y < _h - 1 ? i + _w : -1 })
            {
                if (n < 0 || inside[n] || (skipClear && pixels[n * 4 + 3] == 0)) continue;
                if (Distance(pixels, n, i) > tolerance * .6 || Distance(pixels, n, origin[i]) > tolerance * 1.4) continue;
                inside[n] = true; origin[n] = origin[i]; queue.Enqueue(n);
            }
        }
        return inside;
    }

    private void RemoveBackground()
    {
        // The edges' most common colour (in steps of 16), and the edge pixels close to it, are where it starts.
        var edge = new List<int>();
        for (var x = 0; x < _w; x++) { edge.Add(x); edge.Add((_h - 1) * _w + x); }
        for (var y = 0; y < _h; y++) { edge.Add(y * _w); edge.Add(y * _w + _w - 1); }
        var opaque = edge.Where(i => _pixels[i * 4 + 3] > 0).ToList();
        if (opaque.Count == 0) { Say("The edges are already see-through."); return; }
        var common = opaque.GroupBy(i => (_pixels[i * 4] >> 4, _pixels[i * 4 + 1] >> 4, _pixels[i * 4 + 2] >> 4)).OrderByDescending(g => g.Count()).First().First();
        var tolerance = _tolerance.Value;
        var seeds = opaque.Where(i => Distance(_pixels, i, common) <= tolerance).ToList();
        Remember();
        var clear = Flood(seeds, _pixels, tolerance, skipClear: true);
        var count = 0;
        for (var i = 0; i < clear.Length; i++) if (clear[i]) { _pixels[i * 4 + 3] = 0; count++; }
        Redraw();
        Say(count == 0 ? "No plain background was found at the edges. Try Erase by clicking it, or raise Tolerance."
            : count > clear.Length * .97 ? "That cleared almost everything. Undo and lower Tolerance." : "Done. Click any bits it missed, or Bring back anything it took.");
    }

    private void ClickAt(Point p)
    {
        var x = (int)(p.X / _scale); var y = (int)(p.Y / _scale);
        if (x < 0 || y < 0 || x >= _w || y >= _h) return;
        var i = y * _w + x;
        Remember();
        if (_tool == "Erase")
        {
            if (_pixels[i * 4 + 3] == 0) { _undo.Pop(); return; }
            var patch = Flood([i], _pixels, _tolerance.Value, skipClear: true);
            for (var j = 0; j < patch.Length; j++) if (patch[j]) _pixels[j * 4 + 3] = 0;
        }
        else
        {
            var patch = Flood([i], _original, _tolerance.Value, skipClear: false);
            for (var j = 0; j < patch.Length; j++) if (patch[j]) _pixels[j * 4 + 3] = _original[j * 4 + 3];
        }
        Redraw();
    }

    // ---------------------------------------------------------------- the mouse

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_overlay);
        if (_tool != "Crop") { ClickAt(p); e.Handled = true; return; }
        _dragStart = p; _dragging = true; _overlay.CaptureMouse();
        var x = p.X / _scale; var y = p.Y / _scale;
        _dragMove = !_crop.IsEmpty && x >= _crop.X && y >= _crop.Y && x <= _crop.X + _crop.Width && y <= _crop.Y + _crop.Height;
        _dragStartCrop = _crop;
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(_overlay);
        if (_dragMove)
        {
            var dx = (int)Math.Round((p.X - _dragStart.X) / _scale); var dy = (int)Math.Round((p.Y - _dragStart.Y) / _scale);
            _crop = Clamp(new Int32Rect(_dragStartCrop.X + dx, _dragStartCrop.Y + dy, _dragStartCrop.Width, _dragStartCrop.Height));
        }
        else
        {
            double x0 = Math.Clamp(_dragStart.X / _scale, 0, _w), y0 = Math.Clamp(_dragStart.Y / _scale, 0, _h);
            double x1 = Math.Clamp(p.X / _scale, 0, _w), y1 = Math.Clamp(p.Y / _scale, 0, _h);
            double width = Math.Abs(x1 - x0), height = Math.Abs(y1 - y0);
            if (_aspect > 0)
            {
                if (width / Math.Max(1, height) > _aspect) width = height * _aspect; else height = width / _aspect;
            }
            var left = x1 < x0 ? x0 - width : x0; var top = y1 < y0 ? y0 - height : y0;
            if (width >= 8 && height >= 8) _crop = Clamp(new Int32Rect((int)left, (int)top, (int)width, (int)height));
        }
        Redraw();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false; _overlay.ReleaseMouseCapture();
        e.Handled = true;
    }

    // ---------------------------------------------------------------- the result

    private void Finish()
    {
        try
        {
            var pixels = Feathered();
            BitmapSource result = BitmapSource.Create(_w, _h, 96, 96, PixelFormats.Bgra32, null, pixels, _w * 4);
            if (!_crop.IsEmpty) result = new CroppedBitmap(result, _crop);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(result));
            using var stream = new MemoryStream(); encoder.Save(stream);
            Result = stream.ToArray();
            DialogResult = true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        { Say("The picture could not be made: " + error.Message); }
    }
}
