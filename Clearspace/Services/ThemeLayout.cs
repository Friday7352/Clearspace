using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;

namespace Clearspace.Services;

// NEW (custom layouts): a theme can carry layout.json, which says how the explorer window is put together:
// a tree of rows and columns holding building blocks. The studio's layout builder writes it; anyone who likes
// can write it by hand. Example (a sidebar, the main file list and a second pane on Downloads, side by side):
//
//   { "format": 1, "root": { "split": "column", "children": [
//       { "block": "tabs" }, { "block": "toolbar" },
//       { "split": "row", "splitters": true, "size": "*", "children": [
//           { "block": "sidebar", "size": "220" },
//           { "block": "files" },
//           { "block": "pane", "folder": "Downloads" } ] },
//       { "block": "status" } ] } }
//
// Blocks: files (the main file list; exactly one), pane (another file list, as many as you like, each opening on
// its own folder), sidebar, toolbar, tabs, status, preview, keys (the shortcut key bar), text, image (a picture in
// the theme's images folder), clock, spacer. Each of the window's own parts (all but pane, text, image, clock and
// spacer) can appear once; leaving one out hides it.
// Every node may also have: size ("*", "2*", "240" pixels, or "auto"), margin, padding, fill and edge (a theme
// key such as "Surface" or a colour "#RRGGBB"), edgeWidth and corner. Rows and columns may have "splitters": true,
// which puts a bar you can drag between their children.
// It is only data: nothing in it can run, so a shared theme's layout is as safe as its colours.
internal sealed class LayoutNode
{
    public string? Block { get; set; }
    public string? Split { get; set; }
    public List<LayoutNode>? Children { get; set; }
    public string? Size { get; set; }
    public bool? Splitters { get; set; }
    public string? Folder { get; set; }
    public string? Title { get; set; }
    public string? Text { get; set; }
    public double? FontSize { get; set; }
    public string? Align { get; set; }
    public string? Image { get; set; }
    public string? Fill { get; set; }
    public string? Edge { get; set; }
    public double? EdgeWidth { get; set; }
    public double? Corner { get; set; }
    public double? Margin { get; set; }
    public double? Padding { get; set; }

    [JsonIgnore] public bool IsContainer => Split is not null;

    public LayoutNode Clone() => JsonSerializer.Deserialize<LayoutNode>(JsonSerializer.Serialize(this, ThemeLayout.Options), ThemeLayout.Options)!;

    public IEnumerable<LayoutNode> All()
    {
        yield return this;
        foreach (var child in Children ?? [])
            foreach (var node in child.All()) yield return node;
    }

    internal static LayoutNode Of(string block, string? size = null) => new() { Block = block, Size = size };
    internal static LayoutNode Row(params LayoutNode[] children) => new() { Split = "row", Children = [.. children], Splitters = true };
    internal static LayoutNode Column(params LayoutNode[] children) => new() { Split = "column", Children = [.. children] };
}

internal sealed class ThemeLayoutFile
{
    public int Format { get; set; } = 1;
    public LayoutNode? Root { get; set; }
}

internal static partial class ThemeLayout
{
    internal static readonly string[] Blocks = ["files", "pane", "sidebar", "toolbar", "tabs", "status", "preview", "keys", "text", "image", "clock", "spacer"];
    internal static readonly string[] OnlyOnce = ["files", "sidebar", "toolbar", "tabs", "status", "preview", "keys"];
    private const int MostNodes = 80, MostDepth = 10, MostChildren = 12;

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        MaxDepth = 64
    };

    [GeneratedRegex(@"^(auto|\d{1,4}(\.\d+)?|(\d{1,3}(\.\d+)?)?\*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();

    /// <summary>The layout in layout.json, checked; null when there is none. Throws ThemeFormatException with the reason.</summary>
    internal static LayoutNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        ThemeLayoutFile? file;
        try { file = JsonSerializer.Deserialize<ThemeLayoutFile>(json, Options); }
        catch (JsonException error) { throw new ThemeFormatException("layout.json is not valid JSON: " + error.Message); }
        if (file?.Root is not { } root) throw new ThemeFormatException("layout.json has no \"root\".");
        if (file.Format > 1) throw new ThemeFormatException("layout.json is from a newer Clearspace.");
        Check(root);
        return root;
    }

    internal static bool TryParse(string? json, out LayoutNode? root, out string? problem)
    {
        try { root = Parse(json); problem = null; return true; }
        catch (ThemeFormatException error) { root = null; problem = error.Message; return false; }
    }

    internal static string Write(LayoutNode root) => JsonSerializer.Serialize(new ThemeLayoutFile { Root = root }, Options);

    /// <summary>Throws ThemeFormatException when the layout cannot be used, saying why.</summary>
    internal static void Check(LayoutNode root)
    {
        var count = 0;
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        void Walk(LayoutNode node, int depth)
        {
            if (++count > MostNodes) throw new ThemeFormatException($"layout.json has more than {MostNodes} parts.");
            if (depth > MostDepth) throw new ThemeFormatException("layout.json is nested too deeply.");
            if ((node.Block is null) == (node.Split is null)) throw new ThemeFormatException("Each part of layout.json is either a \"block\" or a \"split\" (row or column).");
            if (node.Size is { } size && !SizePattern().IsMatch(size.Trim())) throw new ThemeFormatException($"\"{size}\" is not a size (use *, 2*, 240 or auto).");
            Range(node.FontSize, 6, 96, "fontSize"); Range(node.EdgeWidth, 0, 12, "edgeWidth"); Range(node.Corner, 0, 40, "corner");
            Range(node.Margin, 0, 60, "margin"); Range(node.Padding, 0, 60, "padding");
            foreach (var brush in new[] { node.Fill, node.Edge })
                if (brush is not null && !IsBrush(brush)) throw new ThemeFormatException($"\"{brush}\" is not a theme color key or a color such as #FF8800.");
            if (node.Split is { } split)
            {
                if (split is not ("row" or "column")) throw new ThemeFormatException($"\"split\" is row or column, not \"{split}\".");
                if (node.Children is not { Count: > 0 } children) throw new ThemeFormatException("A row or column needs at least one part inside.");
                if (children.Count > MostChildren) throw new ThemeFormatException($"A row or column holds at most {MostChildren} parts.");
                foreach (var child in children) Walk(child, depth + 1);
                return;
            }
            var block = node.Block!;
            if (!Blocks.Contains(block)) throw new ThemeFormatException($"\"{block}\" is not a block Clearspace knows.");
            if (node.Children is { Count: > 0 }) throw new ThemeFormatException($"A {block} block cannot hold other parts.");
            seen[block] = seen.GetValueOrDefault(block) + 1;
            if (OnlyOnce.Contains(block) && seen[block] > 1) throw new ThemeFormatException($"The {Label(block).ToLowerInvariant()} can only appear once.");
            if (node.Text is { Length: > 200 }) throw new ThemeFormatException("Text blocks hold at most 200 letters.");
            if (node.Title is { Length: > 60 }) throw new ThemeFormatException("Pane titles hold at most 60 letters.");
            if (node.Folder is { } folder && (folder.Length > 260 || folder.Any(char.IsControl))) throw new ThemeFormatException("A pane's folder is not a folder name.");
            if (node.Align is { } align && align is not ("left" or "center" or "right")) throw new ThemeFormatException("\"align\" is left, center or right.");
            if (node.Image is { } image && !ThemeMarkup.IsImageName(image)) throw new ThemeFormatException($"\"{image}\" is not a picture name.");
        }
        Walk(root, 1);
        if (seen.GetValueOrDefault("files") != 1) throw new ThemeFormatException("A layout needs the main file list (the \"files\" block).");
    }

    private static void Range(double? value, double low, double high, string name)
    {
        if (value is { } v && (!double.IsFinite(v) || v < low || v > high)) throw new ThemeFormatException($"\"{name}\" must be between {low} and {high}.");
    }

    internal static bool IsBrush(string text) =>
        text.Length is 7 or 9 && text[0] == '#' && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
        || ThemeKeys.TryGetKind(text, out var kind) && kind is ThemeValue.Brush or ThemeValue.FlatColor;

    /// <summary>A node's size as a grid length; parts with no size get one that suits them.</summary>
    internal static GridLength Length(LayoutNode node)
    {
        var size = node.Size?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(size))
            return node.Block switch
            {
                "tabs" or "toolbar" or "status" or "keys" or "text" or "clock" or "image" or "preview" => GridLength.Auto,
                "sidebar" => new GridLength(228),
                _ => new GridLength(1, GridUnitType.Star)
            };
        if (size == "auto") return GridLength.Auto;
        if (size.EndsWith('*'))
            return new GridLength(size.Length == 1 ? 1 : double.Parse(size[..^1], CultureInfo.InvariantCulture), GridUnitType.Star);
        return new GridLength(double.Parse(size, CultureInfo.InvariantCulture));
    }

    internal static string Label(string block) => block switch
    {
        "files" => "Main file list", "pane" => "File pane", "sidebar" => "Sidebar", "toolbar" => "Toolbar", "tabs" => "Tabs",
        "status" => "Status bar", "preview" => "Preview pane", "keys" => "Shortcut keys", "text" => "Text", "image" => "Picture",
        "clock" => "Clock", "spacer" => "Empty space", _ => block
    };

    internal static string Glyph(string block) => block switch
    {
        "files" => "", "pane" => "", "sidebar" => "", "toolbar" => "", "tabs" => "", "status" => "",
        "preview" => "", "keys" => "", "text" => "", "image" => "", "clock" => "", _ => ""
    };

    // Ready-made layouts to start from.
    internal static readonly (string Name, string Tip, Func<LayoutNode> Make)[] Templates =
    [
        ("Classic", "The usual Clearspace window", () => LayoutNode.Column(LayoutNode.Of("tabs"), LayoutNode.Of("toolbar"),
            Sized(LayoutNode.Row(LayoutNode.Of("sidebar"), LayoutNode.Column(LayoutNode.Of("files"), LayoutNode.Of("status")), LayoutNode.Of("preview")), "*"))),
        ("Dual pane", "Two file lists side by side, like a commander, with the shortcut keys along the bottom", () => LayoutNode.Column(LayoutNode.Of("tabs"), LayoutNode.Of("toolbar"),
            Sized(LayoutNode.Row(LayoutNode.Of("sidebar", "200"), LayoutNode.Of("files"), new LayoutNode { Block = "pane", Folder = "Downloads" }), "*"),
            LayoutNode.Of("status"), LayoutNode.Of("keys"))),
        ("Browser", "Tabs and an address bar across the top, the files below, nothing else", () => LayoutNode.Column(LayoutNode.Of("tabs"), LayoutNode.Of("toolbar"),
            LayoutNode.Of("files"), LayoutNode.Of("status"))),
        ("Dashboard", "Your files, with Downloads and Desktop open beside them", () => LayoutNode.Column(LayoutNode.Of("toolbar"),
            Sized(LayoutNode.Row(LayoutNode.Of("sidebar", "210"), Sized(LayoutNode.Of("files"), "2*"),
                Sized(Splits(LayoutNode.Column(new LayoutNode { Block = "pane", Folder = "Downloads" }, new LayoutNode { Block = "pane", Folder = "Desktop" })), "*")), "*"),
            LayoutNode.Of("status"))),
        ("Four panes", "Four folders at once", () => LayoutNode.Column(LayoutNode.Of("toolbar"),
            Sized(LayoutNode.Row(
                Splits(LayoutNode.Column(LayoutNode.Of("files"), new LayoutNode { Block = "pane", Folder = "Documents" })),
                Splits(LayoutNode.Column(new LayoutNode { Block = "pane", Folder = "Downloads" }, new LayoutNode { Block = "pane", Folder = "Desktop" }))), "*"),
            LayoutNode.Of("status"))),
        ("Focus", "Just the toolbar and the files", () => LayoutNode.Column(LayoutNode.Of("toolbar"), LayoutNode.Of("files")))
    ];

    private static LayoutNode Sized(LayoutNode node, string size) { node.Size = size; return node; }
    private static LayoutNode Splits(LayoutNode node) { node.Splitters = true; node.Size ??= "*"; return node; }
}
