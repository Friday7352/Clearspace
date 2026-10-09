// Clearspace | Theme files: checking theme.xaml before it is trusted.
// NEW (theme files): theme files are passed from person to person, and XAML can do far more than hold
// colors: left alone it can start programs or read files as it loads. So a theme's XAML is never handed to
// WPF as written. Prepare() reads it as plain XML first and lets through only what a look is made of:
//   - a ResourceDictionary of brushes, colors, shapes, pens, fonts, corner sizes, margins and transforms
//     (the Parts list below), each top-level one with an x:Key;
//   - plain values. Nothing in { } (bindings, x:Static, x:Type ...), no other namespaces, no code;
//   - no addresses: no file paths, web links or pack URIs anywhere. The only way to name a file is the two
//     fixed forms "fonts/#Family Name" and ImageSource="images/name.png", which point into the theme itself
//     and are swapped here for where Clearspace unpacked those files.
// Whatever fails a check is refused with the line it is on, so the person making the theme can fix it.
// What comes out is rebuilt from the checked XML (comments and processing instructions are dropped), and
// only then goes to XamlReader (ThemeLoader). No WPF in this file.
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Clearspace.Services;

internal static partial class ThemeMarkup
{
    internal const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    internal const string Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private const string Root = "ResourceDictionary";
    private const string FontFamily = "FontFamily";
    private const string BundledFont = "fonts/#";
    private const string BundledImage = "images/";

    private const int MostElements = 250_000;   // Sketchbook's paper grain is about 2,000
    private const int MostDepth = 24;
    private const int MostValueLength = 1_000_000;

    // What a theme may be built from.
    private static readonly HashSet<string> Parts = new(StringComparer.Ordinal)
    {
        // brushes
        "SolidColorBrush", "LinearGradientBrush", "RadialGradientBrush", "GradientStop", "DrawingBrush", "ImageBrush",
        // drawings and shapes (for patterns and textures)
        "DrawingGroup", "GeometryDrawing", "GeometryGroup", "RectangleGeometry", "EllipseGeometry", "LineGeometry",
        "PathGeometry", "Pen", "DashStyle",
        // transforms
        "RotateTransform", "ScaleTransform", "TranslateTransform", "SkewTransform", "MatrixTransform", "TransformGroup",
        // single values, written between the tags
        "Color", "CornerRadius", "Thickness", "Visibility", FontFamily
    };

    // The parts whose value is the text between their tags: <CornerRadius x:Key="R8">8</CornerRadius>.
    private static readonly HashSet<string> Values = new(StringComparer.Ordinal)
    {
        "Color", "CornerRadius", "Thickness", "Visibility", FontFamily
    };

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9.]{0,63}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex PropertyPattern();

    [GeneratedRegex(@"^RenderOptions\.[A-Za-z]+$")]
    private static partial Regex RenderOptionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._\-]{0,79}$")]
    private static partial Regex FontNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 _.()\-]{0,99}$")]
    private static partial Regex ImageNamePattern();

    /// <summary>Whether a picture's file name is one theme.xaml can refer to (letters, digits, spaces, - _ . ( ) only).</summary>
    internal static bool IsImageName(string fileName) => ImageNamePattern().IsMatch(fileName) && !fileName.Contains("..", StringComparison.Ordinal);

    /// <summary>Checks theme.xaml and returns XAML that is safe to load.</summary>
    /// <param name="markup">theme.xaml as written.</param>
    /// <param name="fontsFolder">Where the theme's fonts were unpacked, as an address ending in "/", or null when it has none.</param>
    /// <param name="image">The address of an unpacked picture by file name, or null when the theme has no such picture.</param>
    public static string Prepare(string markup, string? fontsFolder, Func<string, string?> image) =>
        Check(markup, fontsFolder, image).ToString(SaveOptions.DisableFormatting);

    /// <summary>The same check, returning the checked dictionary as XML (each element still knows its line
    /// in theme.xaml, for messages). ThemeLoader reads plain themes straight from this.</summary>
    public static XElement Check(string markup, string? fontsFolder, Func<string, string?> image)
    {
        XDocument document;
        try
        {
            using var text = new StringReader(markup);
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true
            });
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException error)
        {
            throw new ThemeFormatException($"theme.xaml, line {error.LineNumber}: this is not well-formed XML ({Sentence(error.Message)})");
        }

        var root = document.Root ?? throw new ThemeFormatException("theme.xaml is empty.");
        if (root.Name != XName.Get(Root, Presentation))
            throw Refused(root, $"a theme starts with <{Root} xmlns=\"{Presentation}\" xmlns:x=\"{Xaml}\">.");

        foreach (var attribute in root.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration)
                throw Refused(root, $"<{Root}> takes nothing but its two xmlns lines.");
            CheckNamespace(root, attribute);
        }

        // Walked with a list of its own rather than by recursion, so depth is counted and never trusted.
        var count = 0;
        var waiting = new Stack<(XElement Element, int Depth)>();
        foreach (var node in root.Nodes().Reverse())
        {
            if (node is not XElement resource)
                throw Refused(node, "only resources (brushes, fonts, corner sizes ...) go directly inside the dictionary.");
            waiting.Push((resource, 1));
        }

        while (waiting.Count > 0)
        {
            var (element, depth) = waiting.Pop();
            if (++count > MostElements)
                throw new ThemeFormatException("theme.xaml is far larger than a theme needs to be.");
            if (depth > MostDepth)
                throw Refused(element, "this is nested too deeply.");

            Check(element, topLevel: depth == 1, fontsFolder, image);

            foreach (var child in element.Elements().Reverse())
                waiting.Push((child, depth + 1));
        }

        return root;
    }

    private static void Check(XElement element, bool topLevel, string? fontsFolder, Func<string, string?> image)
    {
        var name = element.Name.LocalName;
        if (element.Name.NamespaceName != Presentation)
            throw Refused(element, $"<{element.Name.LocalName}> comes from a namespace themes may not use.");

        // <DrawingBrush.Drawing>: a property written as an element. Its owner must be a part a theme may use.
        var dot = name.IndexOf('.');
        if (dot >= 0)
        {
            var owner = name[..dot];
            if (topLevel || !Parts.Contains(owner) || !PropertyPattern().IsMatch(name[(dot + 1)..]))
                throw Refused(element, $"<{name}> is not something a theme can contain.");
            if (element.HasAttributes)
                throw Refused(element, $"<{name}> takes no attributes.");
            RequireNoText(element);
            return;
        }

        if (!Parts.Contains(name))
            throw Refused(element, name == Root
                ? "a theme is one dictionary; it cannot pull in others."
                : $"<{name}> is not something a theme can contain.");

        var isValue = Values.Contains(name);
        var hasKey = false;
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                CheckNamespace(element, attribute);
                continue;
            }

            if (attribute.Name.NamespaceName == Xaml)
            {
                if (attribute.Name.LocalName != "Key" || !topLevel)
                    throw Refused(element, $"x:{attribute.Name.LocalName} cannot be used here (only x:Key, on the resources themselves).");
                if (!KeyPattern().IsMatch(attribute.Value) && !ThemeFileIcons.IsKey(attribute.Value))
                    throw Refused(element, $"\"{Shorten(attribute.Value)}\" is not a key Clearspace knows (keys are words such as Base, Ink or Theme.BaseColor).");
                hasKey = true;
                continue;
            }

            var property = attribute.Name.LocalName;
            if (attribute.Name.NamespaceName.Length > 0 || isValue ||
                !(PropertyPattern().IsMatch(property) || RenderOptionPattern().IsMatch(property)))
                throw Refused(element, $"{property}=\"...\" cannot be set on <{name}> in a theme.");

            if (name == "ImageBrush" && property == "ImageSource")
            {
                attribute.Value = ImageAddress(element, attribute.Value, image);
                continue;
            }
            RequirePlain(element, property, attribute.Value);
        }

        if (topLevel && !hasKey)
            throw Refused(element, $"this <{name}> needs an x:Key saying which part of Clearspace it colors.");

        if (!isValue)
        {
            RequireNoText(element);
            return;
        }

        // <Color>, <CornerRadius>, <Thickness>, <Visibility>, <FontFamily>: the value is the text inside.
        if (element.HasElements)
            throw Refused(element, $"<{name}> holds a value, not other elements.");
        var value = element.Value.Trim();
        if (name == FontFamily)
        {
            element.Value = FontList(element, value, fontsFolder);
            return;
        }
        RequirePlain(element, name, value);
        element.Value = value;
    }

    // ---------------------------------------------------------------- the individual rules

    // Only the two namespaces every theme starts with. (A third could name a .NET namespace, and with it code.)
    private static void CheckNamespace(XElement element, XAttribute declaration)
    {
        if (declaration.Value is not (Presentation or Xaml))
            throw Refused(element, $"xmlns \"{Shorten(declaration.Value)}\" is not allowed; a theme uses only the two standard ones.");
    }

    // A plain value: a color, a number, a list of numbers, a word, a shape path. Nothing that could be an
    // address (no / \ :) and nothing in { }, which is how XAML reaches for bindings and code.
    private static void RequirePlain(XElement element, string what, string value)
    {
        if (value.Length > MostValueLength)
            throw Refused(element, $"the value of {what} is too long.");

        foreach (var letter in value)
        {
            if (char.IsAsciiLetterOrDigit(letter) || letter is ' ' or '#' or '.' or ',' or '+' or '-' or '%' or '_' or '\t' or '\r' or '\n')
                continue;

            throw Refused(element, letter is '{' or '}'
                ? $"{what} uses {{ }}. Themes hold plain values only: write the color or number itself."
                : $"{what} has a '{letter}' in it. Themes hold plain values only (colors such as #FF1A1917, numbers, words).");
        }

        // A color written as a color-profile file ("ContextColor file ...") would be an address after all.
        if (value.Contains("ContextColor", StringComparison.OrdinalIgnoreCase))
            throw Refused(element, $"{what}: write colors as #AARRGGBB.");
    }

    private static void RequireNoText(XElement element)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XText text && !string.IsNullOrWhiteSpace(text.Value))
                throw Refused(element, $"there is loose text inside <{element.Name.LocalName}>.");
        }
    }

    // "Segoe UI, fonts/#Pixelify Sans, Consolas": installed fonts by name, the theme's own by "fonts/#Name".
    private static string FontList(XElement element, string value, string? fontsFolder)
    {
        var fonts = new List<string>();
        foreach (var piece in value.Split(','))
        {
            var font = piece.Trim();
            if (font.Length == 0)
                continue;

            var bundled = font.StartsWith("./", StringComparison.Ordinal) ? font[2..] : font;
            if (bundled.StartsWith(BundledFont, StringComparison.OrdinalIgnoreCase))
            {
                var family = bundled[BundledFont.Length..];
                if (!FontNamePattern().IsMatch(family))
                    throw Refused(element, $"\"{Shorten(font)}\" is not a font name.");
                if (fontsFolder is null)
                    throw Refused(element, $"\"{font}\" needs a font file in the theme's fonts folder, and the theme has none.");
                fonts.Add(fontsFolder + "#" + family);
                continue;
            }

            if (!FontNamePattern().IsMatch(font))
                throw Refused(element, $"\"{Shorten(font)}\" is not a font name. Use the name of an installed font, " +
                    "or fonts/#Family Name for a font file in the theme's fonts folder.");
            fonts.Add(font);
        }

        if (fonts.Count == 0)
            throw Refused(element, "this font list is empty.");
        return string.Join(", ", fonts);
    }

    // ImageSource="images/paper.png": a picture from the theme's images folder, and nothing else.
    private static string ImageAddress(XElement element, string value, Func<string, string?> image)
    {
        var written = value.Trim();
        var path = written.StartsWith("./", StringComparison.Ordinal) ? written[2..] : written;
        if (!path.StartsWith(BundledImage, StringComparison.OrdinalIgnoreCase) || !IsImageName(path[BundledImage.Length..]))
            throw Refused(element, "ImageSource must be a picture in the theme's images folder, written like images/paper.png " +
                "(letters, digits, spaces, - _ . ( ) in the name).");

        return image(path[BundledImage.Length..])
            ?? throw Refused(element, $"\"{written}\" is not in the theme (pictures are .png, .jpg, .bmp or .gif files in its images folder).");
    }

    // ---------------------------------------------------------------- wording

    internal static ThemeFormatException Refused(XObject where, string why)
    {
        var line = where is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;
        return new ThemeFormatException(line > 0 ? $"theme.xaml, line {line}: {why}" : $"theme.xaml: {why}");
    }

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..57] + "...";

    // The first sentence of an XML error, without the "Line 3, position 14." the message already ends in.
    private static string Sentence(string message)
    {
        var end = message.IndexOf(" Line ", StringComparison.Ordinal);
        return (end > 0 ? message[..end] : message).Trim().TrimEnd('.');
    }
}
