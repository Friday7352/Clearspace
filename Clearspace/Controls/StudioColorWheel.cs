using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clearspace.Controls;

internal sealed class StudioColorWheel : FrameworkElement
{
    private readonly BitmapSource _wheel;
    internal double Hue { get; private set; }
    internal double Saturation { get; private set; }
    internal event Action? Changed;
    internal StudioColorWheel()
    {
        Width = Height = 240; Focusable = true; Cursor = Cursors.Cross;
        System.Windows.Automation.AutomationProperties.SetName(this, "Color wheel. Arrow keys change hue and saturation.");
        const int size = 240;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
        {
            var dx = (x - 119.5) / 119.5; var dy = (y - 119.5) / 119.5; var s = Math.Sqrt(dx * dx + dy * dy);
            if (s > 1) continue;
            var c = FromHsv(Math.Atan2(dy, dx) * 180 / Math.PI, s, 1);
            var i = (y * size + x) * 4; pixels[i] = c.B; pixels[i + 1] = c.G; pixels[i + 2] = c.R; pixels[i + 3] = 255;
        }
        _wheel = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4); _wheel.Freeze();
    }
    internal void Set(double hue, double saturation) { Hue = hue; Saturation = saturation; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawImage(_wheel, new Rect(0, 0, Width, Height));
        var radians = Hue * Math.PI / 180;
        var p = new Point(120 + Math.Cos(radians) * Saturation * 118, 120 + Math.Sin(radians) * Saturation * 118);
        dc.DrawEllipse(null, new Pen(Brushes.Black, 4), p, 6, 6); dc.DrawEllipse(null, new Pen(Brushes.White, 2), p, 6, 6);
    }
    private void Pick(Point p)
    {
        var dx = p.X - 120; var dy = p.Y - 120;
        Set((Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360, Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / 118, 0, 1)); Changed?.Invoke();
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { Focus(); CaptureMouse(); Pick(e.GetPosition(this)); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Pick(e.GetPosition(this)); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) { base.OnKeyDown(e); return; }
        Set((Hue + (e.Key == Key.Left ? -3 : e.Key == Key.Right ? 3 : 0) + 360) % 360,
            Math.Clamp(Saturation + (e.Key == Key.Up ? .03 : e.Key == Key.Down ? -.03 : 0), 0, 1)); Changed?.Invoke(); e.Handled = true;
    }
    internal static Color FromHsv(double hue, double saturation, double value)
    {
        var h = ((hue % 360 + 360) % 360) / 60; var c = value * saturation; var x = c * (1 - Math.Abs(h % 2 - 1)); var m = value - c;
        var (r, g, b) = h switch { < 1 => (c, x, 0d), < 2 => (x, c, 0d), < 3 => (0d, c, x), < 4 => (0d, x, c), < 5 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
