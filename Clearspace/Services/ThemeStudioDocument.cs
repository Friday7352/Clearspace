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
        return new(p.Markup, p.Manifest, p) { LayoutJson = p.Layout }; // CHANGED (custom layouts): keeps the theme's layout.json
    }
    internal ThemePackage Package => new("studio", Manifest, Markup, Original?.Fonts ?? [], Original?.Images ?? new Dictionary<string, byte[]>(), LayoutJson);

    /// <summary>NEW (custom layouts): the text of the theme's layout.json, or null for the usual layout.</summary>
    // (Named LayoutJson because Layout(key, value) above already sets the layout switches.)
    internal string? LayoutJson { get; init; }
    internal ResourceDictionary Resources() => ThemeLoader.Load(Package);

    internal ThemeStudioDocument Palette(string background, string accent)
    {
        var generated = XElement.Parse(new ThemeDesign(background, accent).Markup());
        var next = this;
        foreach (var element in generated.Elements().Where(e => e.Name.LocalName == "SolidColorBrush"))
            next = next.Set((string)element.Attribute(X + "Key")!, new XElement(element));
        return next;
    }

    internal ThemeStudioDocument Remove(string key)
    {
        var root = XElement.Parse(Markup);
        foreach (var element in root.Elements().Where(e => (string?)e.Attribute(X + "Key") == key).ToArray()) element.Remove();
        return this with { Markup = root.ToString() };
    }

    internal ThemeStudioDocument Layout(string key, double number)
    {
        if (!double.IsFinite(number)) throw new ArgumentException("Choose a finite layout value.");
        if (key == "Explorer.SidebarWidth") return Set(key, new XElement(Ns + "Thickness", $"{Math.Clamp(number, 170, 400).ToString(System.Globalization.CultureInfo.InvariantCulture)},0,0,0"));
        if (key == "Explorer.RowPadding") return Set(key, new XElement(Ns + "Thickness", $"0,{Math.Clamp(number, 0, 14).ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        // CHANGED (complex themes): every on/off a theme can set, not only the first four layout switches.
        if (!Switches.Contains(key)) throw new ArgumentException("Unknown layout option.");
        return Set(key, new XElement(Ns + "Visibility", number == 0 ? "Collapsed" : "Visible"));
    }

    // NEW (complex themes): the on/off keys the studio may set.
    private static readonly HashSet<string> Switches = new(StringComparer.Ordinal)
    {
        "Explorer.SidebarOnRight", "Explorer.ToolbarBottom", "Explorer.TabsVisibility", "Explorer.SidebarVisibility",
        "Explorer.TabsBelowToolbar", "Explorer.StatusBarVisibility", "Explorer.StatusBarTop", "Explorer.PreviewOnLeft",
        "FunctionKeysVisibility", "OverlayVisibility", "BindingVisibility"
    };

    // NEW (complex themes): any brush (a pattern, a gradient, a picture already in the theme) for a brush key.
    // The result is loaded once, so a bad brush is refused here and never reaches the preview or a saved file.
    internal ThemeStudioDocument Paint(string key, XElement brush)
    {
        if (!ThemeKeys.TryGetKind(key, out var kind) || kind != ThemeValue.Brush) throw new ArgumentException("Choose a part that can hold a pattern.");
        var next = Set(key, new XElement(brush));
        next.Resources();
        return next;
    }

    // NEW (complex themes): a corner size for one part (Part.Files.Corner ...).
    internal ThemeStudioDocument Corner(string key, double radius)
    {
        if (!ThemeKeys.TryGetKind(key, out var kind) || kind != ThemeValue.Corner || !double.IsFinite(radius)) throw new ArgumentException("Choose a corner size.");
        return Set(key, new XElement(Ns + "CornerRadius", Math.Clamp(radius, 0, 40).ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    // NEW (complex themes): an outline width for one part (Part.Files.EdgeWidth ...), or the room left down the
    // window's left edge (PageInset, for a notebook binding).
    internal ThemeStudioDocument Spacing(string key, double left, double top, double right, double bottom)
    {
        if (!ThemeKeys.TryGetKind(key, out var kind) || kind != ThemeValue.Thickness) throw new ArgumentException("Choose a size.");
        var values = new[] { left, top, right, bottom };
        if (values.Any(v => !double.IsFinite(v))) throw new ArgumentException("Choose a finite size.");
        return Set(key, new XElement(Ns + "Thickness", string.Join(",", values.Select(v => Math.Clamp(v, 0, 60).ToString(System.Globalization.CultureInfo.InvariantCulture)))));
    }

    // NEW (complex themes): how far tooltips lean (Sketchbook's sticky notes). 0 is straight.
    internal ThemeStudioDocument Tilt(double angle)
    {
        if (!double.IsFinite(angle)) throw new ArgumentException("Choose a finite angle.");
        return Set("NoteTilt", new XElement(Ns + "RotateTransform", new XAttribute("Angle", Math.Clamp(angle, -6, 6).ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    // NEW (complex themes): how big the explorer is drawn, 0.75 to 1.5 (1 = 100%).
    internal ThemeStudioDocument Scale(double scale)
    {
        if (!double.IsFinite(scale)) throw new ArgumentException("Choose a finite size.");
        var value = Math.Clamp(scale, .75, 1.5).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Set("InterfaceScale", new XElement(Ns + "ScaleTransform", new XAttribute("ScaleX", value), new XAttribute("ScaleY", value)));
    }

    // NEW (complex themes, every setting): any key from text, the way theme.xaml would hold it: a colour
    // (#RRGGBB or #AARRGGBB), a font name, a corner ("8" or "8,8,0,0"), a size ("4" or "0,2,0,2"),
    // Visible / Collapsed, or a number for the two transforms (NoteTilt's angle, InterfaceScale's size).
    // The theme is loaded once with it, so a value WPF cannot read is refused here with the reason.
    internal ThemeStudioDocument Write(string key, string text)
    {
        if (!ThemeKeys.TryGetKind(key, out var kind)) throw new ArgumentException("Clearspace does not use " + key + ".");
        text = text.Trim();
        if (text.Length == 0) return Remove(key);
        var next = kind switch
        {
            ThemeValue.Brush or ThemeValue.FlatColor or ThemeValue.Color => Color(key, text.Length == 7 ? "#FF" + text[1..] : text),
            ThemeValue.Font => Font(key, text),
            ThemeValue.Corner => Set(key, new XElement(Ns + "CornerRadius", text)),
            ThemeValue.Thickness => Set(key, new XElement(Ns + "Thickness", text)),
            ThemeValue.Visibility => Set(key, new XElement(Ns + "Visibility", text.StartsWith('v') || text.StartsWith('V') || text is "1" or "on" or "yes" ? "Visible" : "Collapsed")),
            _ when key == "InterfaceScale" => Scale(Percent(text)),
            _ => Tilt(Number(text))
        };
        next.Resources(); // refuses a value WPF cannot read, with the reason
        return next;
    }

    // "125%" or "125" mean 1.25; "1.25" is already a scale.
    private static double Percent(string text)
    {
        var n = Number(text.TrimEnd('%'));
        return n > 3 ? n / 100 : n;
    }

    private static double Number(string text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value : throw new ArgumentException("Write a number, such as 1.2.");

    // ---------------------------------------------------------------- NEW (stickers)

    internal static readonly string[] FrontStickers = ["Decor.Front1", "Decor.Front2", "Decor.Front3", "Decor.Front4"];
    internal static readonly string[] BackStickers = ["Decor.Back1", "Decor.Back2", "Decor.Back3", "Decor.Back4"];

    /// <summary>The pictures placed on the window, read back from the markup.</summary>
    internal IReadOnlyList<StudioSticker> Stickers()
    {
        var list = new List<StudioSticker>();
        foreach (var element in XElement.Parse(Markup).Elements().Where(e => e.Name.LocalName == "ImageBrush"))
        {
            var key = (string?)element.Attribute(X + "Key");
            if (key is null || !key.StartsWith("Decor.", StringComparison.Ordinal)) continue;
            var source = (string?)element.Attribute("ImageSource") ?? "";
            var box = (string?)element.Attribute("Viewport") is { } viewport ? ReadBox(viewport) : new Rect(0, 0, 1, 1);
            var opacity = double.TryParse((string?)element.Attribute("Opacity"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var o) ? o : 1;
            list.Add(new StudioSticker(key, source.StartsWith("images/", StringComparison.Ordinal) ? source[7..] : source, box, opacity,
                (string?)element.Attribute("Stretch") == "UniformToFill"));
        }
        return list.OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
    }

    private static Rect ReadBox(string text)
    {
        try { return Rect.Parse(text); }
        catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException) { return new Rect(0, 0, 1, 1); }
    }

    /// <summary>A new picture on the window. It is stored in the theme (images/sticker-....png), at most 1600
    /// pixels across. <paramref name="box"/> is where it goes, as fractions of the window; <paramref name="cover"/>
    /// fills the box (cropping) instead of fitting inside it.</summary>
    internal ThemeStudioDocument Sticker(string key, byte[] bytes, Rect box, double opacity, bool cover = false)
    {
        if (!key.StartsWith("Decor.", StringComparison.Ordinal) || !ThemeKeys.TryGetKind(key, out _)) throw new ArgumentException("Choose a sticker place.");
        if (bytes.Length > 16 * 1024 * 1024) throw new ArgumentException("Choose a picture smaller than 16 MB.");
        using var stream = new MemoryStream(bytes);
        var image = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        var scale = Math.Min(1d, 1600d / Math.Max(image.PixelWidth, image.PixelHeight));
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(scale < 1
            ? new System.Windows.Media.Imaging.TransformedBitmap(image, new ScaleTransform(scale, scale)) : (System.Windows.Media.Imaging.BitmapSource)image));
        using var output = new MemoryStream(); encoder.Save(output);
        var png = output.ToArray();
        var name = "sticker-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png))[..16] + ".png";
        var images = Original?.Images.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        images[name] = png;
        var package = new ThemePackage(Original?.Source ?? "studio", Manifest, Markup, Original?.Fonts ?? [], images);
        return (this with { Original = package }).PlaceImage(key, name, box, opacity, cover);
    }

    /// <summary>Moves, resizes or fades a picture already on the window.</summary>
    internal ThemeStudioDocument PlaceSticker(string key, Rect box, double opacity)
    {
        var sticker = Stickers().FirstOrDefault(s => s.Key == key) ?? throw new ArgumentException("That sticker is gone.");
        return PlaceImage(key, sticker.Image, box, opacity, sticker.Cover);
    }

    /// <summary>Moves a picture to another place in the stack (for example from behind the panels to the front).</summary>
    internal ThemeStudioDocument MoveSticker(string from, string to)
    {
        var sticker = Stickers().FirstOrDefault(s => s.Key == from) ?? throw new ArgumentException("That sticker is gone.");
        return Remove(from).PlaceImage(to, sticker.Image, sticker.Box, sticker.Opacity, sticker.Cover);
    }

    /// <summary>NEW (sticker editor): a sticker's picture swapped for an edited one (cut out, cropped); its place,
    /// strength and fit stay. The picture it no longer uses is dropped from the theme.</summary>
    internal ThemeStudioDocument ReplaceStickerImage(string key, byte[] bytes)
    {
        var sticker = Stickers().FirstOrDefault(s => s.Key == key) ?? throw new ArgumentException("That sticker is gone.");
        return Sticker(key, bytes, sticker.Box, sticker.Opacity, sticker.Cover).DropUnusedImages();
    }

    /// <summary>NEW (sticker editor): forgets pictures added in the studio that nothing in theme.xaml points at
    /// any more, so a theme does not carry every version of an edited sticker.</summary>
    internal ThemeStudioDocument DropUnusedImages()
    {
        if (Original is not { } original) return this;
        // CHANGED (custom layouts): a picture named in layout.json is in use too.
        var used = original.Images.Where(p => Markup.Contains("images/" + p.Key, StringComparison.OrdinalIgnoreCase)
                || LayoutJson?.Contains("\"" + p.Key + "\"", StringComparison.OrdinalIgnoreCase) == true)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (used.Count == original.Images.Count) return this;
        return this with { Original = new ThemePackage(original.Source, Manifest, Markup, original.Fonts, used) };
    }

    /// <summary>NEW (custom layouts): a picture added to the theme's images folder (for a layout's picture block),
    /// at most 1600 pixels across. Returns the theme with it and the picture's file name.</summary>
    internal (ThemeStudioDocument Document, string Name) AddImage(byte[] bytes)
    {
        if (bytes.Length > 16 * 1024 * 1024) throw new ArgumentException("Choose a picture smaller than 16 MB.");
        using var stream = new MemoryStream(bytes);
        var image = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        var scale = Math.Min(1d, 1600d / Math.Max(image.PixelWidth, image.PixelHeight));
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(scale < 1
            ? new System.Windows.Media.Imaging.TransformedBitmap(image, new ScaleTransform(scale, scale)) : (System.Windows.Media.Imaging.BitmapSource)image));
        using var output = new MemoryStream(); encoder.Save(output);
        var png = output.ToArray();
        var name = "picture-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png))[..16] + ".png";
        var images = Original?.Images.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        images[name] = png;
        var package = new ThemePackage(Original?.Source ?? "studio", Manifest, Markup, Original?.Fonts ?? [], images);
        return (this with { Original = package }, name);
    }

    /// <summary>NEW (custom layouts): the names of the pictures in the theme.</summary>
    internal IReadOnlyList<string> ImageNames => Original?.Images.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() ?? [];

    /// <summary>The picture file a sticker shows, for thumbnails.</summary>
    internal byte[]? StickerImage(string image) => Original is { } original && original.Images.TryGetValue(image, out var bytes) ? bytes : null;

    private ThemeStudioDocument PlaceImage(string key, string image, Rect box, double opacity, bool cover)
    {
        static string F(double v) => Math.Round(v, 4).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var x = Math.Clamp(box.X, -1, 2); var y = Math.Clamp(box.Y, -1, 2);
        var w = Math.Clamp(box.Width, .02, 3); var h = Math.Clamp(box.Height, .02, 3);
        var element = new XElement(Ns + "ImageBrush",
            new XAttribute("ImageSource", "images/" + image), new XAttribute("Stretch", cover ? "UniformToFill" : "Uniform"),
            new XAttribute("TileMode", "None"), new XAttribute("ViewportUnits", "RelativeToBoundingBox"),
            new XAttribute("Viewport", $"{F(x)},{F(y)},{F(w)},{F(h)}"), new XAttribute("Opacity", F(Math.Clamp(opacity, .05, 1))));
        return Set(key, element);
    }

    // NEW (complex themes): several keys back to what Clearspace uses when a theme leaves them out.
    internal ThemeStudioDocument Without(params string[] keys)
    {
        var next = this;
        foreach (var key in keys) next = next.Remove(key);
        return next;
    }

    internal ThemeStudioDocument ImportFont(byte[] bytes, string extension, string role)
    {
        extension = extension.ToLowerInvariant();
        if (extension is not (".ttf" or ".otf") || bytes.Length > 16 * 1024 * 1024) throw new ArgumentException("Choose a TTF or OTF font smaller than 16 MB.");
        var name = "font-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16] + extension;
        var asset = new ThemeAsset(name, bytes);
        var candidate = new ThemePackage("studio-font", Manifest, Markup, [asset], new Dictionary<string, byte[]>());
        var location = ThemeCache.Unpack(candidate).FontsFolder!;
        var families = System.Windows.Media.Fonts.GetFontFamilies(new Uri(location));
        var family = families.FirstOrDefault()?.FamilyNames.Values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(family)) throw new ArgumentException("This file does not contain a readable font.");
        var fonts = (Original?.Fonts ?? []).Where(f => f.FileName != name).Append(asset).ToArray();
        var package = new ThemePackage(Original?.Source ?? "studio", Manifest, Markup, fonts, Original?.Images ?? new Dictionary<string, byte[]>());
        return (this with { Original = package }).Font(role, "fonts/#" + family);
    }

    internal ThemeStudioDocument FileIcon(string target, bool extension, byte[] bytes)
    {
        var key = ThemeFileIcons.Key(target, extension);
        if (bytes.Length > 16 * 1024 * 1024) throw new ArgumentException("Choose an icon smaller than 16 MB.");
        using var stream = new MemoryStream(bytes);
        var image = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        var scale = Math.Min(1d, 256d / Math.Max(image.PixelWidth, image.PixelHeight));
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(new System.Windows.Media.Imaging.TransformedBitmap(image, new ScaleTransform(scale, scale))));
        using var output = new MemoryStream(); encoder.Save(output);
        var png = output.ToArray();
        var name = "icon-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png))[..16] + ".png";
        var images = Original?.Images.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        images[name] = png;
        var package = new ThemePackage(Original?.Source ?? "studio", Manifest, Markup, Original?.Fonts ?? [], images);
        return (this with { Original = package }).Set(key, new XElement(Ns + "ImageBrush", new XAttribute("ImageSource", "images/" + name), new XAttribute("Stretch", "Uniform")));
    }

    // ---------------------------------------------------------------- NEW (theme cards)

    /// <summary>NEW (theme cards): an element taken from another theme (a card), set by its own x:Key. It is
    /// loaded once with the theme, so one Clearspace cannot read is refused here.</summary>
    internal ThemeStudioDocument Put(XElement element)
    {
        var key = (string?)element.Attribute(X + "Key");
        if (key is null || !ThemeKeys.TryGetKind(key, out _) && !ThemeFileIcons.IsKey(key)) throw new ArgumentException("That card holds something Clearspace does not use.");
        var copy = new XElement(element);
        foreach (var declaration in copy.DescendantsAndSelf().SelectMany(e => e.Attributes()).Where(a => a.IsNamespaceDeclaration).ToArray()) declaration.Remove();
        return Set(key, copy);
    }

    /// <summary>NEW (theme cards): pictures and font files a card brings, added to the theme's own.</summary>
    internal ThemeStudioDocument WithAssets(IReadOnlyDictionary<string, byte[]> images, IReadOnlyList<ThemeAsset> fonts)
    {
        if (images.Count == 0 && fonts.Count == 0) return this;
        var allImages = Original?.Images.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, bytes) in images) allImages[name] = bytes;
        var allFonts = (Original?.Fonts ?? []).Where(f => fonts.All(n => !string.Equals(n.FileName, f.FileName, StringComparison.OrdinalIgnoreCase))).Concat(fonts).ToArray();
        return this with { Original = new ThemePackage(Original?.Source ?? "studio", Manifest, Markup, allFonts, allImages) };
    }

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
            throw new ArgumentException("Choose a color resource.");
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
    internal ThemeStudioDocument AllFonts(string family)
    {
        var next = this;
        foreach (var role in new[] { "UIFont", "SerifFont", "DisplayFont", "MonoFont" }) next = next.Font(role, family);
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
        ThemeLayout.Parse(LayoutJson); // NEW (custom layouts): a layout that cannot be used is refused before anything is written
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
            // New imported assets must travel with folder themes as well as ZIP themes.
            if (Original is { } assets)
            {
                Directory.CreateDirectory(Path.Combine(target, "fonts"));
                Directory.CreateDirectory(Path.Combine(target, "images"));
                foreach (var font in assets.Fonts) AtomicWrite(Path.Combine(target, "fonts", font.FileName), font.Data);
                foreach (var (file, bytes) in assets.Images) AtomicWrite(Path.Combine(target, "images", file), bytes);
            }
            // Preserve all assets in folder themes. Roll back the first replacement if the second fails.
            var markupPath = Path.Combine(target, "theme.xaml");
            var manifestPath = Path.Combine(target, "theme.json");
            var oldMarkup = File.ReadAllBytes(markupPath);
            AtomicWrite(markupPath, System.Text.Encoding.UTF8.GetBytes(Markup));
            try { AtomicWrite(manifestPath, System.Text.Encoding.UTF8.GetBytes(json)); }
            catch { AtomicWrite(markupPath, oldMarkup); throw; }
            // NEW (custom layouts): layout.json written, or taken away when the theme went back to the usual layout.
            var layoutPath = Path.Combine(target, ThemePackage.LayoutName);
            if (LayoutJson is not null) AtomicWrite(layoutPath, System.Text.Encoding.UTF8.GetBytes(LayoutJson));
            else if (File.Exists(layoutPath)) File.Delete(layoutPath);
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
                // NEW (custom layouts): layout.json, when there is one; marked written either way, so an old one
                // in the theme being edited is not copied across below once the layout has been taken away.
                if (LayoutJson is not null) Write(ThemePackage.LayoutName, System.Text.Encoding.UTF8.GetBytes(LayoutJson));
                else written.Add(ThemePackage.LayoutName);
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

/// <summary>NEW (stickers): a picture on the window: its key (Decor.Front1 ... Decor.Back4), the picture's file
/// name in the theme, its place as fractions of the window, how solid it is, and whether it fills its box.</summary>
internal sealed record StudioSticker(string Key, string Image, Rect Box, double Opacity, bool Cover)
{
    internal bool Front => Key.StartsWith("Decor.Front", StringComparison.Ordinal);
}
