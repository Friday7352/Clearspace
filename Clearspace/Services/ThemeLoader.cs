// Clearspace | Theme files: turning one into the resources the app paints with.
// NEW (theme files): Load() takes a theme that ThemePackage read, puts its fonts and pictures where WPF can
// open them (ThemeCache), has ThemeMarkup check theme.xaml, and only then builds the brushes, fonts and
// sizes: directly for a theme of plain colors (Plain), through WPF's XAML reader for anything richer. What comes back is checked once more against the list of keys Clearspace uses (ThemeKeys): a
// key Clearspace does not know is dropped, and a known key holding the wrong kind of thing (a brush where a
// corner size belongs) refuses the theme with a message, because that would otherwise stop a window from
// drawing. A theme may leave keys out; App.xaml keeps the Dark theme's values underneath as the fallback.
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;

namespace Clearspace.Services;

// ---------------------------------------------------------------- the keys a theme can set

internal enum ThemeValue
{
    Brush,        // any brush: a flat color, a gradient, a pattern, a picture
    FlatColor,    // a SolidColorBrush: code reads the color out of these for what it draws by hand
    Font,
    Corner,
    Color,
    Visibility,
    Thickness,
    Transform
}

internal static class ThemeKeys
{
    public const string SelectedRowPrefix = "SelectedRow.";

    private static readonly Dictionary<string, ThemeValue> Kinds = Build();

    /// <summary>Every key, for the README's table and the tests.</summary>
    public static IReadOnlyDictionary<string, ThemeValue> All => Kinds;

    private static Dictionary<string, ThemeValue> Build()
    {
        var kinds = new Dictionary<string, ThemeValue>(StringComparer.Ordinal);
        void Add(ThemeValue kind, params string[] keys)
        {
            foreach (var key in keys) kinds[key] = kind;
        }

        Add(ThemeValue.Brush,
            "Base", "Surface", "Overlay", "Hover", "Pressed", "Selected", "Line", "LineFaint", "EdgeRaised", "EdgeSunken",
            "InkDisabled", "AccentInk", "Danger",
            "ThumbRest", "ThumbHover", "Tint", "ChipHover", "Divider", "RowAlt", "Track", "SliderRail", "Well",
            "BadgeFill", "NavFill", "Scrim",
            "MenuHover", "MenuHoverInk", "MenuHoverMuted", "MenuHoverFaint", "MenuHoverAccent",
            "OverlayFill", "BindingFill",
            "ListFill",    // NEW (list backdrop): behind the file list only
            // NEW (complex themes): a shade over the whole window (vignette, glow, sunbeam), under OverlayFill.
            "EdgeShade",
            // NEW (complex themes): each part of the explorer can have its own fill and outline. Sidebar and
            // toolbar panels are see-through unless a theme sets them; the file area and the status bar line
            // follow Surface, EdgeSunken and LineFaint unless a theme sets them (ThemeParts.Complete).
            "Part.Sidebar.Fill", "Part.Sidebar.Edge", "Part.Toolbar.Fill", "Part.Toolbar.Edge",
            "Part.Files.Fill", "Part.Files.Edge", "Part.Status.Fill", "Part.Status.Edge",
            // NEW (stickers): up to four pictures over the whole window and four behind its panels, each an
            // ImageBrush with its place given by Viewport (fractions of the window). See-through by default.
            "Decor.Front1", "Decor.Front2", "Decor.Front3", "Decor.Front4",
            "Decor.Back1", "Decor.Back2", "Decor.Back3", "Decor.Back4");

        // ThemeService hands these to the parts drawn in code (the disk usage map, status colors, hover cards).
        Add(ThemeValue.FlatColor,
            "Ink", "InkMuted", "InkFaint", "Accent", "AccentSoft", "AccentWash", "Good", "Warn", "Bad", "Quiet",
            "CardFill", "CardEdge", "Ground3D");

        Add(ThemeValue.Font, "UIFont", "SerifFont", "DisplayFont", "MonoFont");
        Add(ThemeValue.Corner, "R2", "R3", "R4", "R5", "R6", "R7", "R8", "R9", "R10", "R12", "RPlayerBar");
        Add(ThemeValue.Color, "Theme.CaptionColor", "Theme.CaptionTextColor", "Theme.BaseColor");
        Add(ThemeValue.Visibility, "OverlayVisibility", "FunctionKeysVisibility", "BindingVisibility");
        Add(ThemeValue.Thickness, "PageInset");
        Add(ThemeValue.Thickness, "Explorer.SidebarWidth", "Explorer.RowPadding");
        Add(ThemeValue.Visibility, "Explorer.SidebarOnRight", "Explorer.ToolbarBottom", "Explorer.TabsVisibility", "Explorer.SidebarVisibility");
        // NEW (complex themes): more of the window can move. See Services/ThemeExplorerLayout.cs.
        Add(ThemeValue.Visibility, "Explorer.TabsBelowToolbar", "Explorer.StatusBarVisibility", "Explorer.StatusBarTop", "Explorer.PreviewOnLeft");
        Add(ThemeValue.Thickness, "Part.Sidebar.EdgeWidth", "Part.Toolbar.EdgeWidth", "Part.Files.EdgeWidth");
        Add(ThemeValue.Corner, "Part.Sidebar.Corner", "Part.Toolbar.Corner", "Part.Files.Corner");
        Add(ThemeValue.Transform, "NoteTilt");
        // NEW (complex themes): the size of everything in the explorer, as a ScaleTransform (1 = 100%).
        Add(ThemeValue.Transform, "InterfaceScale");
        return kinds;
    }

    /// <summary>What kind of value a key holds; false for a key Clearspace does not use.
    /// "SelectedRow.Ink" replaces Ink inside a selected row, so it holds what Ink holds.</summary>
    public static bool TryGetKind(string key, out ThemeValue kind)
    {
        if (ThemeFileIcons.IsKey(key)) { kind = ThemeValue.Brush; return true; }
        if (Kinds.TryGetValue(key, out kind))
            return true;

        return key.StartsWith(SelectedRowPrefix, StringComparison.Ordinal)
            && Kinds.TryGetValue(key[SelectedRowPrefix.Length..], out kind)
            && kind is ThemeValue.Brush or ThemeValue.FlatColor;
    }

    public static bool Fits(ThemeValue kind, object? value) => kind switch
    {
        ThemeValue.Brush => value is Brush,
        ThemeValue.FlatColor => value is SolidColorBrush,
        ThemeValue.Font => value is FontFamily,
        ThemeValue.Corner => value is CornerRadius,
        ThemeValue.Color => value is Color,
        ThemeValue.Visibility => value is Visibility,
        ThemeValue.Thickness => value is Thickness,
        ThemeValue.Transform => value is Transform,
        _ => false
    };

    public static string Describe(ThemeValue kind) => kind switch
    {
        ThemeValue.Brush => "a brush (SolidColorBrush, a gradient, a DrawingBrush or an ImageBrush)",
        ThemeValue.FlatColor => "one flat color (a SolidColorBrush)",
        ThemeValue.Font => "a FontFamily",
        ThemeValue.Corner => "a CornerRadius",
        ThemeValue.Color => "a Color",
        ThemeValue.Visibility => "a Visibility (Visible or Collapsed)",
        ThemeValue.Thickness => "a Thickness",
        _ => "a transform (such as RotateTransform)"
    };
}

// ---------------------------------------------------------------- loading

// NEW (complex themes): the file area's own fill, outline and corners, and the status bar's line, start out as
// the theme's Surface, EdgeSunken, R12 and LineFaint. A theme that sets those but not the part keys gets them
// copied across here, so older themes (and every theme that never styles parts) look exactly as before.
// A theme that sets neither falls through to Dark's values for both, which agree with each other.
internal static class ThemeParts
{
    private static readonly (string Part, string From)[] Followers =
    [
        ("Part.Files.Fill", "Surface"), ("Part.Files.Edge", "EdgeSunken"), ("Part.Files.Corner", "R12"), ("Part.Status.Edge", "LineFaint")
    ];

    public static void Complete(ResourceDictionary resources)
    {
        foreach (var (part, from) in Followers)
            if (!resources.Contains(part) && resources.Contains(from))
                resources[part] = resources[from];
    }
}

internal static class ThemeLoader
{
    /// <summary>Builds the theme's resources. Throws ThemeFormatException, with the reason, when the theme
    /// cannot be used. Call it on the thread that will show the theme.</summary>
    public static ResourceDictionary Load(ThemePackage package)
    {
        var assets = ThemeCache.Unpack(package);
        var markup = ThemeMarkup.Check(package.Markup, assets.FontsFolder, assets.Image);

        // Most themes are nothing but flat colors, fonts and sizes. Those are built right here, which is
        // many times quicker than WPF's XAML reader (this runs before the first window opens) and can say
        // which line a bad value is on. Gradients, patterns and pictures go to the XAML reader.
        var resources = Plain(markup);
        if (resources is null)
        {
            try
            {
                resources = XamlReader.Parse(markup.ToString(SaveOptions.DisableFormatting)) as ResourceDictionary
                    ?? throw new ThemeFormatException("theme.xaml must be a ResourceDictionary.");
            }
            catch (ThemeFormatException)
            {
                throw;
            }
            catch (Exception error)
            {
                // Usually a value WPF cannot read: a misspelt color, a number where a word belongs.
                throw new ThemeFormatException("theme.xaml has a value Clearspace cannot read: " + Reason(error));
            }
        }

        // (The keys are copied first: reading a value can make the dictionary rewrite its entry.)
        foreach (var key in resources.Keys.Cast<object>().ToArray())
        {
            if (key is not string name || !ThemeKeys.TryGetKind(name, out var kind))
            {
                resources.Remove(key);   // not something Clearspace uses (or a key from a later version)
                continue;
            }

            var value = resources[key];
            if (!ThemeKeys.Fits(kind, value))
                throw new ThemeFormatException($"theme.xaml: {name} must be {ThemeKeys.Describe(kind)}.");

            // Frozen resources cost less to paint with and can be read from any thread.
            if (value is Freezable { CanFreeze: true, IsFrozen: false } freezable)
                freezable.Freeze();
        }

        ThemeParts.Complete(resources); // NEW (complex themes)
        return resources;
    }

    // The dictionary, when every resource in it is one of the plain kinds written in the plain way:
    //     <SolidColorBrush x:Key="Ink" Color="#FFECE9E3" />      (and Opacity)
    //     <Color>, <CornerRadius>, <Thickness>, <Visibility>, <FontFamily> with their value between the tags
    //     <RotateTransform x:Key="NoteTilt" Angle="-1.5" />
    // Null as soon as anything else turns up; the whole theme then goes through the XAML reader instead.
    // Values are read with WPF's own converters, so both ways give the same result (ThemeTests compares them).
    internal static ResourceDictionary? Plain(XElement dictionary)
    {
        var resources = new ResourceDictionary();
        foreach (var element in dictionary.Elements())
        {
            string? key = null;
            string? color = null, opacity = null, angle = null;
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                    continue;
                if (attribute.Name.NamespaceName == ThemeMarkup.Xaml)
                    key = attribute.Value;
                else if (element.Name.LocalName == "SolidColorBrush" && attribute.Name.LocalName == "Color")
                    color = attribute.Value;
                else if (element.Name.LocalName == "SolidColorBrush" && attribute.Name.LocalName == "Opacity")
                    opacity = attribute.Value;
                else if (element.Name.LocalName == "RotateTransform" && attribute.Name.LocalName == "Angle")
                    angle = attribute.Value;
                else
                    return null;
            }
            if (key is null || element.HasElements)
                return null;

            object value;
            try
            {
                switch (element.Name.LocalName)
                {
                    case "SolidColorBrush":
                        var brush = color is null ? new SolidColorBrush() : new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                        if (opacity is not null)
                            brush.Opacity = double.Parse(opacity, System.Globalization.CultureInfo.InvariantCulture);
                        value = brush;
                        break;
                    case "Color":
                        value = (Color)ColorConverter.ConvertFromString(element.Value);
                        break;
                    case "CornerRadius":
                        value = (CornerRadius)new CornerRadiusConverter().ConvertFromInvariantString(element.Value)!;
                        break;
                    case "Thickness":
                        value = (Thickness)new ThicknessConverter().ConvertFromInvariantString(element.Value)!;
                        break;
                    case "Visibility":
                        value = Enum.Parse<Visibility>(element.Value, ignoreCase: true);
                        break;
                    case "FontFamily":
                        value = new FontFamily(element.Value);
                        break;
                    case "RotateTransform":
                        value = new RotateTransform(angle is null ? 0 : double.Parse(angle, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    default:
                        return null;
                }
            }
            catch (Exception error) when (error is FormatException or ArgumentException or NotSupportedException or InvalidCastException or OverflowException)
            {
                var written = element.Name.LocalName == "SolidColorBrush" ? color ?? opacity : angle ?? element.Value;
                throw ThemeMarkup.Refused(element, $"{key}: Clearspace cannot read \"{written}\" as a {element.Name.LocalName}.");
            }

            if (resources.Contains(key))
                throw ThemeMarkup.Refused(element, $"{key} is set twice.");
            resources.Add(key, value);
        }

        return resources;
    }

    // The innermost message says what was wrong ("'#GGG' is not a valid value for Color"); the outer ones
    // only say where in the checked copy of the file, which is not how the person wrote it.
    private static string Reason(Exception error)
    {
        while (error.InnerException is { } inner)
            error = inner;

        var message = error.Message.Trim();
        var line = message.IndexOfAny(['\r', '\n']);
        return line > 0 ? message[..line] : message;
    }
}

// ---------------------------------------------------------------- fonts and pictures on disk

/// <summary>Where a theme's fonts and pictures were unpacked.</summary>
/// <param name="FontsFolder">The fonts folder as an address ending in "/", or null when the theme has no fonts.</param>
/// <param name="Image">The address of a picture by file name, or null when the theme has no such picture.</param>
internal sealed record ThemeAssets(string? FontsFolder, Func<string, string?> Image)
{
    public static readonly ThemeAssets None = new(null, _ => null);
}

// WPF opens fonts and pictures by address, so the ones inside a theme file are written out here first:
// %LocalAppData%\Clearspace\ThemeCache\<fingerprint of the files>\fonts and \images. The folder is named
// after what is in it, so a theme whose colors change keeps using the same copies (WPF holds font files
// open, and could not have them replaced under it), and a theme whose fonts change gets a new folder.
internal static class ThemeCache
{
    private static readonly TimeSpan Unused = TimeSpan.FromDays(14);
    private const string Doomed = ".old";

    /// <summary>The cache folder. Tests point it at a temporary folder.</summary>
    internal static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clearspace", "ThemeCache");

    public static ThemeAssets Unpack(ThemePackage package)
    {
        if (package.Fonts.Count == 0 && package.Images.Count == 0)
            return ThemeAssets.None;

        try
        {
            var home = Path.Combine(Folder, Fingerprint(package));
            string? fonts = null;
            if (package.Fonts.Count > 0)
            {
                var folder = Path.Combine(home, ThemePackage.FontsFolder);
                Directory.CreateDirectory(folder);
                for (var index = 0; index < package.Fonts.Count; index++)
                {
                    // Fonts are found by the family name inside them, so the file's own name does not matter.
                    var font = package.Fonts[index];
                    Put(Path.Combine(folder, $"font{index + 1:00}{Path.GetExtension(font.FileName).ToLowerInvariant()}"), font.Data);
                }
                fonts = Address(folder + Path.DirectorySeparatorChar);
            }

            var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (package.Images.Count > 0)
            {
                var folder = Path.Combine(home, ThemePackage.ImagesFolder);
                Directory.CreateDirectory(folder);
                foreach (var (name, data) in package.Images)
                {
                    var file = Path.Combine(folder, name);
                    Put(file, data);
                    images[name] = Address(file);
                }
            }

            try { Directory.SetLastWriteTimeUtc(home, DateTime.UtcNow); }   // "still in use", for Tidy
            catch (Exception) { }

            return new ThemeAssets(fonts, name => images.GetValueOrDefault(name));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ThemeFormatException("The theme's fonts and pictures could not be unpacked: " + error.Message);
        }
    }

    /// <summary>Removes unpacked fonts and pictures no theme has used for two weeks. Best effort, in the background.</summary>
    public static void TidyLater() => Task.Run(() =>
    {
        try
        {
            if (!Directory.Exists(Folder))
                return;
            foreach (var folder in Directory.EnumerateDirectories(Folder))
            {
                try
                {
                    var leftover = folder.EndsWith(Doomed, StringComparison.OrdinalIgnoreCase);
                    if (!leftover && DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) <= Unused)
                        continue;

                    // Renamed first: Windows refuses to rename a folder while a running Clearspace still has
                    // a font in it open, so a folder in use is left whole rather than half deleted.
                    var doomed = leftover ? folder : folder + Doomed;
                    if (!leftover)
                        Directory.Move(folder, doomed);
                    Directory.Delete(doomed, recursive: true);
                }
                catch (Exception) { }   // in use, or gone already: next time
            }
        }
        catch (Exception) { }
    });

    private static void Put(string file, byte[] data)
    {
        var existing = new FileInfo(file);
        if (existing.Exists && existing.Length == data.Length)
            return;   // unpacked on an earlier run
        File.WriteAllBytes(file, data);
    }

    // file:///C:/Users/.../fonts/  A comma would split a font list in two, so it is written as %2C.
    private static string Address(string path) => new Uri(path).AbsoluteUri.Replace(",", "%2C", StringComparison.Ordinal);

    private static string Fingerprint(ThemePackage package)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string kind, string name, byte[] data)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"{kind}|{name.ToLowerInvariant()}|{data.Length}|"));
            hash.AppendData(data);
        }

        foreach (var font in package.Fonts)
            Add("font", font.FileName, font.Data);
        foreach (var (name, data) in package.Images.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            Add("image", name, data);

        return Convert.ToHexString(hash.GetHashAndReset(), 0, 8).ToLowerInvariant();
    }
}
