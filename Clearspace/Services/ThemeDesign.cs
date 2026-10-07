using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace Clearspace.Services;

// The studio owns a small palette; related colours are derived together so every screen stays coherent.
internal sealed record ThemeDesign(string Background, string Accent, string Font = "Segoe UI", double Roundness = 8)
{
    internal static readonly (string Name, ThemeDesign Design)[] Presets =
    [
        ("Midnight", new("#171D30", "#AFA1FF")),
        ("Matcha", new("#EFF3E8", "#426B41")),
        ("Peach", new("#FFF1E7", "#A54432")),
        ("Lagoon", new("#10292C", "#64D8CF")),
        ("Candy", new("#2B1930", "#FF9DCB")),
        ("Ink & paper", new("#F4F1EB", "#3E546D", "Georgia", 2))
    ];

    internal static bool TryColor(string text, out Color color)
    {
        color = default;
        if (text.Length != 7 || text[0] != '#' || !uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return false;
        color = Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static double Luminance(Color c)
    {
        static double Linear(byte value) { var n = value / 255d; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
        return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
    }

    internal static double Contrast(Color a, Color b) => (Math.Max(Luminance(a), Luminance(b)) + .05) / (Math.Min(Luminance(a), Luminance(b)) + .05);
    private static Color TextOn(Color background) => Contrast(background, Colors.White) > Contrast(background, Colors.Black) ? Colors.White : Colors.Black;

    internal ResourceDictionary Resources()
    {
        if (!TryColor(Background, out var background) || !TryColor(Accent, out var accent))
            throw new ArgumentException("Use six-digit colours, such as #AFA1FF.");
        if (!double.IsFinite(Roundness) || Roundness < 0 || Roundness > 16)
            throw new ArgumentException("Roundness must be between 0 and 16.");
        if (!new[] { "Segoe UI", "Georgia", "Consolas", "Trebuchet MS" }.Contains(Font))
            throw new ArgumentException("Choose one of the studio fonts.");
        var ink = TextOn(background);
        var dark = ink == Colors.White;
        var surface = Mix(background, ink, .045);
        var overlay = Mix(background, ink, .08);
        var selected = Mix(surface, accent, .22);
        var muted = Mix(surface, ink, .68);
        var faint = Mix(surface, ink, .53);
        var line = Mix(surface, ink, .17);
        var r = new ResourceDictionary();
        void Brush(string key, Color c) => r[key] = new SolidColorBrush(c);
        void Group(Color c, params string[] keys) { foreach (var key in keys) Brush(key, c); }
        Group(background, "Base", "Scrim", "NavFill", "BadgeFill");
        Group(surface, "Surface", "CardFill", "Well");
        Group(overlay, "Overlay", "Ground3D", "Tint", "Track", "SliderRail");
        Group(line, "Line", "EdgeRaised", "EdgeSunken", "CardEdge", "Divider");
        Group(Mix(surface, ink, .08), "LineFaint", "Hover", "MenuHover");
        Group(Mix(surface, ink, .14), "Pressed");
        Group(selected, "Selected", "ChipHover");
        Group(ink, "Ink", "MenuHoverInk");
        Group(muted, "InkMuted", "Quiet", "MenuHoverMuted");
        Group(faint, "InkFaint", "MenuHoverFaint");
        Group(Mix(surface, ink, .34), "InkDisabled", "ThumbRest");
        Group(faint, "ThumbHover");
        Group(accent, "Accent", "MenuHoverAccent");
        Group(Mix(surface, accent, .3), "AccentSoft");
        Group(Mix(surface, accent, .15), "AccentWash");
        Group(TextOn(accent), "AccentInk");
        Group(Mix(surface, ink, .025), "RowAlt");
        Group(dark ? Color.FromRgb(255, 156, 152) : Color.FromRgb(156, 36, 43), "Danger", "Bad");
        Group(dark ? Color.FromRgb(127, 214, 156) : Color.FromRgb(30, 108, 61), "Good");
        Group(dark ? Color.FromRgb(243, 202, 121) : Color.FromRgb(130, 83, 12), "Warn");
        // Selection may be lighter than the page, especially with very bright accent colours.
        var selectionInk = TextOn(selected);
        Group(selectionInk, "SelectedRow.Ink");
        Group(Mix(selected, selectionInk, .75), "SelectedRow.InkMuted", "SelectedRow.InkFaint");
        foreach (var key in new[] { "UIFont", "SerifFont", "DisplayFont", "MonoFont" })
            r[key] = new FontFamily(key == "MonoFont" ? "Consolas" : Font);
        foreach (var size in new[] { 2, 3, 4, 5, 6, 7, 8, 9, 10, 12 })
            r["R" + size] = new CornerRadius(size * Roundness / 8);
        r["RPlayerBar"] = new CornerRadius(0, 0, 11 * Roundness / 8, 11 * Roundness / 8);
        return r;
    }

    internal string Markup()
    {
        XNamespace ns = ThemeMarkup.Presentation, x = ThemeMarkup.Xaml;
        var root = new XElement(ns + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", x));
        var resources = Resources();
        foreach (string key in resources.Keys.Cast<string>().OrderBy(k => k, StringComparer.Ordinal))
        {
            var value = resources[key];
            var element = value switch
            {
                SolidColorBrush brush => new XElement(ns + "SolidColorBrush", new XAttribute("Color", brush.Color.ToString(CultureInfo.InvariantCulture))),
                FontFamily font => new XElement(ns + "FontFamily", font.Source),
                CornerRadius corner => new XElement(ns + "CornerRadius", string.Join(",", new[] { corner.TopLeft, corner.TopRight, corner.BottomRight, corner.BottomLeft }.Select(v => v.ToString(CultureInfo.InvariantCulture)))),
                _ => throw new InvalidOperationException("Unsupported studio value.")
            };
            element.Add(new XAttribute(x + "Key", key));
            root.Add(element);
        }
        return root.ToString();
    }

    // A complete, validated archive is published in one move. User labels never become paths.
    internal string Save(string folder, string name)
    {
        name = ThemeManifest.Tidy(name, 60);
        if (name.Length == 0) throw new ArgumentException("Give your theme a name first.");
        var markup = Markup();
        Directory.CreateDirectory(folder);
        var id = "Custom-" + Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(folder, id + ".tmp");
        var target = Path.Combine(folder, id + ".zip");
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                void Write(string entry, string text) { using var writer = new StreamWriter(zip.CreateEntry(entry).Open()); writer.Write(text); }
                Write("theme.json", JsonSerializer.Serialize(new { format = 1, name, description = "Made in Clearspace Theme studio", version = "1.0" }));
                Write("theme.xaml", markup);
            }
            ThemeLoader.Load(ThemePackage.Open(temporary));
            File.Move(temporary, target);
            return id;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
