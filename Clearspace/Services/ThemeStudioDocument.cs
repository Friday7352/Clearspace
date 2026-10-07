using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace Clearspace.Services;

// Immutable edit state: only touched resources change; artwork, bundled fonts and manifest switches survive.
internal sealed record ThemeStudioDocument(string Markup, ThemeManifest Manifest, ThemePackage? Original = null)
{
    private static readonly XNamespace Ns = ThemeMarkup.Presentation, X = ThemeMarkup.Xaml;
    internal static ThemeStudioDocument FromDesign(ThemeDesign design) => new(design.Markup(),
        new ThemeManifest("My theme", "", "Made in Clearspace Theme studio", "1.0", false, IconStyle.System, false, false, false));
    internal static ThemeStudioDocument Open(ThemeInfo theme)
    {
        var p = ThemePackage.Open(theme.Path);
        ThemeLoader.Load(p);
        return new(p.Markup, p.Manifest, p);
    }
    internal ThemePackage Package => new("studio", Manifest, Markup, Original?.Fonts ?? [], Original?.Images ?? new Dictionary<string, byte[]>());
    internal ResourceDictionary Resources() => ThemeLoader.Load(Package);

    private ThemeStudioDocument Set(string key, XElement value)
    {
        var root = XElement.Parse(Markup);
        foreach (var old in root.Elements().Where(e => (string?)e.Attribute(X + "Key") == key).ToArray()) old.Remove();
        value.SetAttributeValue(X + "Key", key); root.Add(value);
        return this with { Markup = root.ToString() };
    }
    internal ThemeStudioDocument Color(string key, string hex)
    {
        if (!ThemeKeys.TryGetKind(key, out var kind) || kind is not (ThemeValue.Brush or ThemeValue.FlatColor or ThemeValue.Color))
            throw new ArgumentException("Choose a colour resource.");
        if (hex.Length is not (7 or 9) || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _))
            throw new ArgumentException("Use #RRGGBB or #AARRGGBB.");
        return Set(key, kind == ThemeValue.Color ? new XElement(Ns + "Color", hex) : new XElement(Ns + "SolidColorBrush", new XAttribute("Color", hex)));
    }
    internal ThemeStudioDocument Font(string key, string family)
    {
        if (!ThemeKeys.TryGetKind(key, out var kind) || kind != ThemeValue.Font) throw new ArgumentException("Choose a font resource.");
        var next = Set(key, new XElement(Ns + "FontFamily", family));
        next.Resources(); // applies the same font/path validation as imported themes
        return next;
    }
    internal ThemeStudioDocument Corners(double radius)
    {
        var next = this;
        foreach (var size in new[] { 2, 3, 4, 5, 6, 7, 8, 9, 10, 12 })
            next = next.Set("R" + size, new XElement(Ns + "CornerRadius", (size * radius / 8).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var bottom = (11 * radius / 8).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return next.Set("RPlayerBar", new XElement(Ns + "CornerRadius", $"0,0,{bottom},{bottom}"));
    }
    internal ThemeStudioDocument Gradient(string first, string second, bool diagonal)
    {
        var checkedColors = Color("Base", first).Color("Base", second);
        _ = checkedColors;
        return Set("Base", new XElement(Ns + "LinearGradientBrush", new XAttribute("StartPoint", "0,0"),
            new XAttribute("EndPoint", diagonal ? "1,1" : "0,1"),
            new XElement(Ns + "GradientStop", new XAttribute("Color", first), new XAttribute("Offset", "0")),
            new XElement(Ns + "GradientStop", new XAttribute("Color", second), new XAttribute("Offset", "1"))))
            .Color("Theme.BaseColor", first);
    }

    internal ThemeStudioDocument Backdrop(byte[] image, double opacity)
    {
        if (image.Length > 16 * 1024 * 1024) throw new ArgumentException("Choose a picture smaller than 16 MB.");
        // Read it before accepting it, so a broken picture never replaces the working preview.
        using var stream = new MemoryStream(image);
        _ = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var images = Original?.Images.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        images["studio-backdrop.png"] = image;
        var assets = new ThemePackage(Original?.Source ?? "studio", Manifest, Markup, Original?.Fonts ?? [], images);
        return (this with { Original = assets }).Set("ListFill", new XElement(Ns + "ImageBrush",
            new XAttribute("ImageSource", "images/studio-backdrop.png"), new XAttribute("Stretch", "UniformToFill"),
            new XAttribute("Opacity", Math.Clamp(opacity, 0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    internal ThemeStudioDocument BackdropOpacity(double opacity)
    {
        var root = XElement.Parse(Markup);
        var image = root.Elements().FirstOrDefault(e => (string?)e.Attribute(X + "Key") == "ListFill" && e.Name.LocalName == "ImageBrush");
        if (image is null) return this;
        image.SetAttributeValue("Opacity", Math.Clamp(opacity, 0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return this with { Markup = root.ToString() };
    }

    internal string Save(string folder, string name, ThemeInfo? replace = null)
    {
        name = ThemeManifest.Tidy(name, 60);
        if (name.Length == 0) throw new ArgumentException("Give your theme a name.");
        ThemeLoader.Load(Package);
        Directory.CreateDirectory(folder);
        var id = replace?.Name ?? "Custom-" + Guid.NewGuid().ToString("N");
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        var target = replace?.Path ?? Path.Combine(folder, id + ".zip");
        if (replace is not null && (replace.Shipped || replace.BuiltIn ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(target)) + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Built-in themes can only be saved as a copy.");
        var m = Manifest;
        var json = JsonSerializer.Serialize(new { format = 1, name, author = m.Author, description = m.Description, version = m.Version,
            experimental = m.Experimental, icons = m.Icons.ToString(), fadeToolbar = m.FadeToolbar, eInk = m.EInk, crispText = m.CrispText });
        if (Directory.Exists(target))
        {
            // Preserve all assets in folder themes. Roll back the first replacement if the second fails.
            var markupPath = Path.Combine(target, "theme.xaml");
            var manifestPath = Path.Combine(target, "theme.json");
            var oldMarkup = File.ReadAllBytes(markupPath);
            AtomicWrite(markupPath, System.Text.Encoding.UTF8.GetBytes(Markup));
            try { AtomicWrite(manifestPath, System.Text.Encoding.UTF8.GetBytes(json)); }
            catch { AtomicWrite(markupPath, oldMarkup); throw; }
            return id;
        }
        var temp = Path.Combine(folder, ".studio-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void Write(string entry, byte[] bytes) { written.Add(entry); using var s = zip.CreateEntry(entry).Open(); s.Write(bytes); }
                Write("theme.json", System.Text.Encoding.UTF8.GetBytes(json));
                Write("theme.xaml", System.Text.Encoding.UTF8.GetBytes(Markup));
                if (Original is { } original)
                {
                    foreach (var asset in original.Fonts) Write("fonts/" + asset.FileName, asset.Data);
                    foreach (var (file, bytes) in original.Images) Write("images/" + file, bytes);
                    // Keep ancillary files (for example font licences) when editing/copying an archive.
                    if (File.Exists(original.Source))
                    {
                        using var source = ZipFile.OpenRead(original.Source);
                        var manifestEntry = source.Entries.First(e => e.FullName.Replace('\\', '/').EndsWith("theme.json", StringComparison.OrdinalIgnoreCase));
                        var prefix = manifestEntry.FullName[..^"theme.json".Length];
                        foreach (var entry in source.Entries)
                        {
                            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
                            var relative = entry.FullName[prefix.Length..].Replace('\\', '/');
                            if (relative.Length == 0 || relative.EndsWith('/') || relative.Contains("..") || relative.StartsWith('/') ||
                                !written.Add(relative)) continue;
                            using var from = entry.Open(); using var to = zip.CreateEntry(relative).Open(); from.CopyTo(to);
                        }
                    }
                }
            }
            ThemeLoader.Load(ThemePackage.Open(temp));
            File.Move(temp, target, overwrite: replace is not null);
            return id;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
