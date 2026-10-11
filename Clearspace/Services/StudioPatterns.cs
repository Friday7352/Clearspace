using System.Globalization;
using System.Windows.Media;
using System.Xml.Linq;

namespace Clearspace.Services;

// NEW (complex themes): the patterns, screen effects and shades the theme studio makes, written as plain
// theme.xaml brushes (DrawingBrush, gradients). They are the same kind of markup the shipped themes use
// (Blueprint's grid, Neon HUD's scanlines, Sketchbook's binding), so a saved theme needs no pictures and
// passes ThemeMarkup's checks like any other.

internal enum StudioPattern { Dots, Grid, Blueprint, Stripes, Ruled, Checks, Grain, Stars, Waves, Confetti }

/// <summary>NEW (pattern maker): a pattern someone drew in the studio's pattern maker, cell by cell.
/// Cells holds Size x Size characters, row by row: '0' empty, '1' the pattern colour, '2' the accent,
/// '3' the pattern's own extra colour (Extra, #RRGGBB).</summary>
internal sealed record StudioCustomPattern(string Name, int Size, string Cells, string Extra)
{
    internal static readonly int[] Sizes = [8, 12, 16, 24];

    internal bool IsValid => Sizes.Contains(Size) && Cells.Length == Size * Size && Cells.All(c => c is >= '0' and <= '3')
        && ThemeDesign.TryColor(Extra, out _) && Name.Length is > 0 and <= 40;
}
internal enum StudioOverlay { None, Scanlines, Grain, Grid, ScreenDoor }
internal enum StudioShade { None, Vignette, Glow, Sunbeam, Dusk }

internal static class StudioPatterns
{
    private static readonly XNamespace Ns = ThemeMarkup.Presentation;

    internal static string Label(StudioPattern pattern) => pattern switch
    {
        StudioPattern.Dots => "Dots", StudioPattern.Grid => "Grid", StudioPattern.Blueprint => "Blueprint",
        StudioPattern.Stripes => "Stripes", StudioPattern.Ruled => "Ruled paper", StudioPattern.Checks => "Checks",
        StudioPattern.Grain => "Grain", StudioPattern.Stars => "Stars", StudioPattern.Waves => "Waves", _ => "Confetti"
    };

    internal static string Label(StudioOverlay overlay) => overlay switch
    {
        StudioOverlay.None => "None", StudioOverlay.Scanlines => "Scanlines", StudioOverlay.Grain => "Film grain",
        StudioOverlay.Grid => "Fine grid", _ => "Screen door"
    };

    internal static string Label(StudioShade shade) => shade switch
    {
        StudioShade.None => "None", StudioShade.Vignette => "Vignette", StudioShade.Glow => "Glow from above",
        StudioShade.Sunbeam => "Sunbeam", _ => "Dusk"
    };

    /// <summary>A repeating pattern. <paramref name="background"/> fills each tile first (for the window or
    /// the file area); null leaves it see-through (over the file list, the sidebar or the toolbar).
    /// <paramref name="scale"/> 0.5–3 sizes it; <paramref name="strength"/> 0–1 is how strongly it shows.</summary>
    internal static XElement Pattern(StudioPattern kind, Color ink, Color accent, Color? background, double scale, double strength)
    {
        scale = Math.Clamp(scale, .5, 3);
        var c = Alpha(ink, strength);
        var soft = Alpha(ink, strength * .45);
        var group = new XElement(Ns + "DrawingGroup");
        double w, h;
        switch (kind)
        {
            case StudioPattern.Dots:
                w = h = 16 * scale;
                group.Add(Fill(c, Ellipse(w / 2, h / 2, Math.Max(.8, w * .09))));
                break;
            case StudioPattern.Grid:
                w = h = 24 * scale;
                group.Add(Fill(c, Many(Rect(0, 0, w, 1), Rect(0, 0, 1, h))));
                break;
            case StudioPattern.Blueprint:
                w = h = 80 * scale;
                var minor = new List<XElement>();
                for (var i = 1; i < 5; i++) { minor.Add(Rect(0, h * i / 5, w, .6)); minor.Add(Rect(w * i / 5, 0, .6, h)); }
                group.Add(Fill(soft, Many(minor.ToArray())));
                group.Add(Fill(c, Many(Rect(0, 0, w, 1.2), Rect(0, 0, 1.2, h))));
                break;
            case StudioPattern.Stripes:
                w = h = 14 * scale;
                group.Add(Stroke(c, w * .2, Many(Line(-1, h + 1, w + 1, -1), Line(-w / 2 - 1, h / 2 + 1, w / 2 + 1, -h / 2 - 1), Line(w / 2 - 1, h * 1.5 + 1, w * 1.5 + 1, h / 2 - 1))));
                break;
            case StudioPattern.Ruled:
                w = 40 * scale; h = 22 * scale;
                group.Add(Fill(c, Rect(0, h - 1, w, 1)));
                break;
            case StudioPattern.Checks:
                w = h = 20 * scale;
                group.Add(Fill(Alpha(ink, strength * .6), Many(Rect(0, 0, w / 2, h / 2), Rect(w / 2, h / 2, w / 2, h / 2))));
                break;
            case StudioPattern.Grain:
            {
                w = h = 48 * scale;
                var random = new Random(7);
                var dark = new List<XElement>(); var light = new List<XElement>();
                for (var i = 0; i < 140; i++)
                    (i % 2 == 0 ? dark : light).Add(Rect(random.NextDouble() * w, random.NextDouble() * h, scale, scale));
                group.Add(Fill(c, Many(dark.ToArray())));
                group.Add(Fill(soft, Many(light.ToArray())));
                break;
            }
            case StudioPattern.Stars:
            {
                w = h = 64 * scale;
                var random = new Random(11);
                var dots = new List<XElement>();
                for (var i = 0; i < 9; i++) dots.Add(Ellipse(random.NextDouble() * w, random.NextDouble() * h, (.5 + random.NextDouble()) * scale));
                group.Add(Fill(soft, Many(dots.ToArray())));
                group.Add(Fill(c, Many(Star(w * .25, h * .3, 4.5 * scale), Star(w * .72, h * .68, 3 * scale))));
                break;
            }
            case StudioPattern.Waves:
                w = 40 * scale; h = 20 * scale;
                group.Add(Stroke(c, 1.4 * scale, Path(
                    $"M{F(0)},{F(h / 2)} C{F(w * .25)},{F(h * .1)} {F(w * .25)},{F(h * .1)} {F(w / 2)},{F(h / 2)} " +
                    $"S{F(w * .75)},{F(h * .9)} {F(w)},{F(h / 2)}")));
                break;
            default: // Confetti
            {
                w = h = 60 * scale;
                var random = new Random(3);
                var colors = new[] { Alpha(accent, Math.Min(1, strength * 1.4)), c, Alpha(accent, strength * .6) };
                for (var i = 0; i < 9; i++)
                {
                    var x = random.NextDouble() * w; var y = random.NextDouble() * h;
                    group.Add(Fill(colors[i % 3], Piece(x, y, 5 * scale, 2.2 * scale, random.NextDouble() * Math.PI)));
                }
                break;
            }
        }
        if (background is { } back) group.AddFirst(Fill(back, Rect(0, 0, w, h)));
        return Tile(group, w, h);
    }

    /// <summary>NEW (pattern maker): a pattern drawn in the pattern maker, as a repeating brush. Each cell is
    /// 4 px at size 100%; runs of same-coloured cells in a row become one rectangle, so even a busy 24 x 24
    /// drawing stays a small theme file.</summary>
    internal static XElement Custom(StudioCustomPattern pattern, Color ink, Color accent, Color? background, double scale, double strength)
    {
        if (!pattern.IsValid) throw new ArgumentException("This pattern could not be read.");
        scale = Math.Clamp(scale, .5, 3);
        var cell = 4 * scale;
        var size = pattern.Size * cell;
        ThemeDesign.TryColor(pattern.Extra, out var extra);
        var colors = new[] { Alpha(ink, strength), Alpha(accent, strength), Alpha(extra, strength) };
        var group = new XElement(Ns + "DrawingGroup");
        if (background is { } back) group.Add(Fill(back, Rect(0, 0, size, size)));
        for (var index = 1; index <= 3; index++)
        {
            var mark = (char)('0' + index);
            var runs = new List<XElement>();
            for (var y = 0; y < pattern.Size; y++)
            {
                var x = 0;
                while (x < pattern.Size)
                {
                    if (pattern.Cells[y * pattern.Size + x] != mark) { x++; continue; }
                    var start = x;
                    while (x < pattern.Size && pattern.Cells[y * pattern.Size + x] == mark) x++;
                    runs.Add(Rect(start * cell, y * cell, (x - start) * cell, cell));
                }
            }
            if (runs.Count > 0) group.Add(Fill(colors[index - 1], Many(runs.ToArray())));
        }
        return Tile(group, size, size);
    }

    /// <summary>A see-through texture over the whole window (OverlayFill), or null for none.</summary>
    internal static XElement? Overlay(StudioOverlay kind, bool darkTheme, double strength)
    {
        var black = Alpha(Colors.Black, strength * (darkTheme ? .45 : .22));
        var white = Alpha(Colors.White, strength * (darkTheme ? .12 : .35));
        var group = new XElement(Ns + "DrawingGroup");
        double size;
        switch (kind)
        {
            case StudioOverlay.Scanlines:
                size = 3; group.Add(Fill(black, Rect(0, 2, 3, 1)));
                break;
            case StudioOverlay.Grain:
            {
                size = 64;
                var random = new Random(19);
                var dark = new List<XElement>(); var light = new List<XElement>();
                for (var i = 0; i < 220; i++) (i % 2 == 0 ? dark : light).Add(Rect(random.Next(64), random.Next(64), 1, 1));
                group.Add(Fill(black, Many(dark.ToArray()))); group.Add(Fill(white, Many(light.ToArray())));
                break;
            }
            case StudioOverlay.Grid:
                size = 8; group.Add(Fill(Alpha(darkTheme ? Colors.White : Colors.Black, strength * .08), Many(Rect(0, 0, 8, 1), Rect(0, 0, 1, 8))));
                break;
            case StudioOverlay.ScreenDoor:
                size = 3; group.Add(Fill(black, Many(Rect(2, 0, 1, 3), Rect(0, 2, 3, 1))));
                break;
            default:
                return null;
        }
        return Tile(group, size, size);
    }

    /// <summary>A soft shade over the whole window (EdgeShade), or null for none.</summary>
    internal static XElement? Shade(StudioShade kind, Color accent, double strength)
    {
        XElement Stop(Color color, double offset) => new(Ns + "GradientStop", new XAttribute("Color", Hex(color)), new XAttribute("Offset", F(offset)));
        var clear = Colors.Transparent;
        return kind switch
        {
            StudioShade.Vignette => new XElement(Ns + "RadialGradientBrush", new XAttribute("Center", "0.5,0.5"), new XAttribute("GradientOrigin", "0.5,0.5"),
                new XAttribute("RadiusX", "0.75"), new XAttribute("RadiusY", "0.75"), Stop(clear, .55), Stop(Alpha(Colors.Black, strength * .7), 1)),
            StudioShade.Glow => new XElement(Ns + "RadialGradientBrush", new XAttribute("Center", "0.5,0"), new XAttribute("GradientOrigin", "0.5,0"),
                new XAttribute("RadiusX", "0.9"), new XAttribute("RadiusY", "0.9"), Stop(Alpha(accent, strength * .45), 0), Stop(clear, 1)),
            StudioShade.Sunbeam => new XElement(Ns + "LinearGradientBrush", new XAttribute("StartPoint", "0,0"), new XAttribute("EndPoint", "1,1"),
                Stop(Alpha(Colors.White, strength * .35), 0), Stop(clear, .6)),
            StudioShade.Dusk => new XElement(Ns + "LinearGradientBrush", new XAttribute("StartPoint", "0,0"), new XAttribute("EndPoint", "0,1"),
                Stop(clear, .35), Stop(Alpha(accent, strength * .4), 1)),
            _ => null
        };
    }

    /// <summary>NEW (edge strips): warning tape for down the window's left edge, like Dino Park's: 9 wide inside a
    /// 30-wide tile (the binding strip is 30 wide; the rest is see-through), so PageInset is 10.</summary>
    internal static XElement Tape(Color stripe, Color dark) => Tile(new XElement(Ns + "DrawingGroup",
        Fill(dark, Rect(0, 0, 9, 16)),
        Fill(stripe, Path("M0,8 L9,0 L9,8 L0,16 Z")),
        Fill(Alpha(Colors.Black, .45), Rect(9, 0, 1, 16))), 30, 16);

    /// <summary>A spiral notebook binding for down the window's left edge (BindingFill), 30 wide like Sketchbook's.</summary>
    internal static XElement Binding(bool darkTheme)
    {
        var group = new XElement(Ns + "DrawingGroup",
            Fill(Alpha(Colors.Black, darkTheme ? .35 : .12), Rect(0, 0, 3, 30)),
            Fill(darkTheme ? Color.FromRgb(16, 16, 20) : Color.FromRgb(69, 69, 77), Ellipse(17, 15, 4.5)),
            Round(darkTheme ? Color.FromRgb(140, 144, 152) : Color.FromRgb(108, 112, 121), 4.2, Line(-4, 10.5, 16, 14)),
            Round(Color.FromRgb(220, 223, 229), 1.2, Line(-4, 9.4, 15, 12.8)));
        return Tile(group, 30, 30);
    }

    // ---------------------------------------------------------------- markup helpers

    private static string F(double v) => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);
    private static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    private static Color Alpha(Color c, double a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);

    private static XElement Tile(XElement drawing, double w, double h) => new(Ns + "DrawingBrush",
        new XAttribute("TileMode", "Tile"), new XAttribute("Stretch", "None"),
        new XAttribute("Viewport", $"0,0,{F(w)},{F(h)}"), new XAttribute("ViewportUnits", "Absolute"),
        new XAttribute("Viewbox", $"0,0,{F(w)},{F(h)}"), new XAttribute("ViewboxUnits", "Absolute"),
        new XElement(Ns + "DrawingBrush.Drawing", drawing));

    private static XElement Fill(Color color, XElement geometry) => new(Ns + "GeometryDrawing",
        new XAttribute("Brush", Hex(color)), new XElement(Ns + "GeometryDrawing.Geometry", geometry));

    private static XElement Stroke(Color color, double thickness, XElement geometry) => new(Ns + "GeometryDrawing",
        new XElement(Ns + "GeometryDrawing.Pen", new XElement(Ns + "Pen", new XAttribute("Brush", Hex(color)), new XAttribute("Thickness", F(thickness)))),
        new XElement(Ns + "GeometryDrawing.Geometry", geometry));

    private static XElement Round(Color color, double thickness, XElement geometry) => new(Ns + "GeometryDrawing",
        new XElement(Ns + "GeometryDrawing.Pen", new XElement(Ns + "Pen", new XAttribute("Brush", Hex(color)), new XAttribute("Thickness", F(thickness)),
            new XAttribute("StartLineCap", "Round"), new XAttribute("EndLineCap", "Round"))),
        new XElement(Ns + "GeometryDrawing.Geometry", geometry));

    private static XElement Many(params XElement[] geometries) => new(Ns + "GeometryGroup", geometries);
    private static XElement Rect(double x, double y, double w, double h) => new(Ns + "RectangleGeometry", new XAttribute("Rect", $"{F(x)},{F(y)},{F(w)},{F(h)}"));
    private static XElement Ellipse(double x, double y, double r) => new(Ns + "EllipseGeometry",
        new XAttribute("Center", $"{F(x)},{F(y)}"), new XAttribute("RadiusX", F(r)), new XAttribute("RadiusY", F(r)));
    private static XElement Line(double x1, double y1, double x2, double y2) => new(Ns + "LineGeometry",
        new XAttribute("StartPoint", $"{F(x1)},{F(y1)}"), new XAttribute("EndPoint", $"{F(x2)},{F(y2)}"));
    private static XElement Path(string figures) => new(Ns + "PathGeometry", new XAttribute("Figures", figures));

    // A four-pointed sparkle.
    private static XElement Star(double x, double y, double r)
    {
        var i = r * .28;
        return Path($"M{F(x)},{F(y - r)} L{F(x + i)},{F(y - i)} L{F(x + r)},{F(y)} L{F(x + i)},{F(y + i)} " +
                    $"L{F(x)},{F(y + r)} L{F(x - i)},{F(y + i)} L{F(x - r)},{F(y)} L{F(x - i)},{F(y - i)} Z");
    }

    // A small turned rectangle (a piece of confetti).
    private static XElement Piece(double x, double y, double length, double width, double angle)
    {
        var (dx, dy) = (Math.Cos(angle) * length / 2, Math.Sin(angle) * length / 2);
        var (nx, ny) = (-Math.Sin(angle) * width / 2, Math.Cos(angle) * width / 2);
        return Path($"M{F(x - dx - nx)},{F(y - dy - ny)} L{F(x + dx - nx)},{F(y + dy - ny)} L{F(x + dx + nx)},{F(y + dy + ny)} L{F(x - dx + nx)},{F(y - dy + ny)} Z");
    }
}
