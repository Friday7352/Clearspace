using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clearspace.Services;

// NEW (theme from a picture): reads the main colours of a picture and turns them into a background and an
// accent that work together, so the studio can build a whole theme from a photo or the desktop wallpaper.
//   - The picture is shrunk to 64 pixels across and its colours grouped into six (k-means).
//   - Mostly dark pictures make a dark theme and mostly light ones a light theme. The background keeps the
//     hue of the commonest colour but is pushed very dark (or very light) and calm.
//   - The accent is the most colourful group that is common enough to matter, made bright (or deep) enough
//     to read at 3:1 on the background.
internal static class StudioPicture
{
    internal sealed record Palette(Color Background, Color Accent, bool Dark, Color[] Colors);

    internal static BitmapSource Load(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    internal static Palette Read(BitmapSource picture)
    {
        var scale = 64d / Math.Max(picture.PixelWidth, picture.PixelHeight);
        BitmapSource small = scale < 1 ? new TransformedBitmap(picture, new ScaleTransform(scale, scale)) : picture;
        var converted = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var points = new List<(double R, double G, double B)>();
        for (var i = 0; i + 3 < pixels.Length; i += 4)
            if (pixels[i + 3] > 128) points.Add((pixels[i + 2], pixels[i + 1], pixels[i]));
        if (points.Count == 0) points.Add((40, 40, 48));

        // k-means, started from points spread evenly through the picture so the result is the same every time.
        const int k = 6;
        var centres = Enumerable.Range(0, k).Select(i => points[i * (points.Count - 1) / Math.Max(1, k - 1)]).ToArray();
        var owner = new int[points.Count];
        for (var round = 0; round < 10; round++)
        {
            for (var p = 0; p < points.Count; p++)
            {
                var best = 0; var bestDistance = double.MaxValue;
                for (var c = 0; c < k; c++)
                {
                    var d = Square(points[p].R - centres[c].R) + Square(points[p].G - centres[c].G) + Square(points[p].B - centres[c].B);
                    if (d < bestDistance) { bestDistance = d; best = c; }
                }
                owner[p] = best;
            }
            for (var c = 0; c < k; c++)
            {
                var mine = points.Where((_, p) => owner[p] == c).ToArray();
                if (mine.Length > 0) centres[c] = (mine.Average(m => m.R), mine.Average(m => m.G), mine.Average(m => m.B));
            }
        }
        var groups = Enumerable.Range(0, k)
            .Select(c => (Color: Color.FromRgb((byte)centres[c].R, (byte)centres[c].G, (byte)centres[c].B), Share: owner.Count(o => o == c) / (double)points.Count))
            .Where(g => g.Share > 0).OrderByDescending(g => g.Share).ToArray();

        var light = groups.Sum(g => Lightness(g.Color) * g.Share);
        var dark = light < .55;
        var (h, s, _) = Hsl(groups[0].Color);
        var background = FromHsl(h, Math.Min(s, dark ? .35 : .25), dark ? .085 : .955);

        // The accent: colourful and common enough to matter; failing that, the group that stands out most.
        var accentGroup = groups.Where(g => Hsl(g.Color).S >= .25 && g.Share >= .03)
            .OrderByDescending(g => Hsl(g.Color).S * Math.Sqrt(g.Share))
            .Select(g => (Color?)g.Color).FirstOrDefault()
            ?? groups.OrderByDescending(g => ThemeDesign.Contrast(g.Color, background)).First().Color;
        var (ah, asat, al) = Hsl(accentGroup);
        asat = Math.Max(asat, .45);
        var accent = FromHsl(ah, asat, al);
        for (var step = 0; step < 40 && ThemeDesign.Contrast(accent, background) < 3; step++)
        {
            al = dark ? Math.Min(.92, al + .02) : Math.Max(.12, al - .02);
            accent = FromHsl(ah, asat, al);
        }
        return new Palette(background, accent, dark, groups.Select(g => g.Color).ToArray());
    }

    /// <summary>The desktop wallpaper's file, or null when Windows does not say (a solid colour, a slideshow
    /// it has not copied yet). Falls back to the copy Windows keeps of the current wallpaper.</summary>
    internal static string? WallpaperPath()
    {
        try
        {
            using var desktop = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (desktop?.GetValue("WallPaper") is string path && path.Length > 0 && File.Exists(path)) return path;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        var copy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
        return File.Exists(copy) ? copy : null;
    }

    internal static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static double Square(double v) => v * v;
    private static double Lightness(Color c) => (Math.Max(c.R, Math.Max(c.G, c.B)) + Math.Min(c.R, Math.Min(c.G, c.B))) / 510d;

    private static (double H, double S, double L) Hsl(Color c)
    {
        double r = c.R / 255d, g = c.G / 255d, b = c.B / 255d;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2; var d = max - min;
        if (d == 0) return (0, 0, l);
        var s = l > .5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360;
        double Channel(double t)
        {
            var q = l < .5 ? l * (1 + s) : l + s - l * s; var p = 2 * l - q;
            if (t < 0) t += 1; if (t > 1) t -= 1;
            return t < 1d / 6 ? p + (q - p) * 6 * t : t < .5 ? q : t < 2d / 3 ? p + (q - p) * (2d / 3 - t) * 6 : p;
        }
        if (s == 0) { var v = (byte)Math.Round(l * 255); return Color.FromRgb(v, v, v); }
        return Color.FromRgb((byte)Math.Round(Channel(h + 1d / 3) * 255), (byte)Math.Round(Channel(h) * 255), (byte)Math.Round(Channel(h - 1d / 3) * 255));
    }
}
