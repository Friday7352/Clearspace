using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace Clearspace.Services;

// NEW (theme cards): a theme is a hand of cards. Each card is one piece of a theme's look that makes sense on
// its own: its colors, its panels (fills, outlines, corners), a pattern or picture on one part, its fonts, where
// the parts sit, one sticker, one effect, its file icons. Any theme can be read as a deck (Deck), so a card can
// be taken from any theme you have and played onto the one you are making (Play), or taken back out (TakeOut).
// A card is only theme.xaml elements (with the pictures and fonts they name), the same things a theme file holds,
// so playing one can never do more than a theme file can. Cards you keep live in cards.json beside your colors.
// CHANGED (decks of cards): the cards hold everything a theme looks like. Every setting in a theme belongs to
// exactly one card, and so do the theme's switches (pixel lettering rides on the font card; a fading toolbar on
// the layout card; the icon style on the icons card). Playing every card of a deck onto an empty theme gives
// the same theme back, which is what a deck code does (ThemeCodes): a deck is just its cards.
internal enum CardKind { Palette, Panels, Pattern, Font, Layout, Sticker, Effect, Icons }

internal sealed record ThemeCard(CardKind Kind, string Name, string Detail, string From,
    IReadOnlyList<string> Elements, IReadOnlyDictionary<string, byte[]> Images, IReadOnlyList<ThemeAsset> Fonts)
{
    /// <summary>The custom layout this layout card carries (null: the usual layout).</summary>
    public string? LayoutJson { get; init; }
    /// <summary>The icon style an icons card sets.</summary>
    public IconStyle? Icons { get; init; }
    /// <summary>NEW (decks of cards): pixel lettering, which a font card turns on or off.</summary>
    public bool? Crisp { get; init; }
    /// <summary>NEW (decks of cards): the toolbar that fades when idle, which a layout card turns on or off.</summary>
    public bool? Fade { get; init; }

    /// <summary>The theme keys the card sets.</summary>
    internal IEnumerable<string> Keys => Elements.Select(e => (string?)XElement.Parse(e).Attribute(ThemeCards.X + "Key")).OfType<string>();

    /// <summary>What makes two cards the same card, whatever they are called.</summary>
    /// <summary>NEW (every card by default): a card that only shows Clearspace's own (the theme sets nothing on
    /// it yet), so there is nothing to take out.</summary>
    internal bool IsDefault => Elements.Count == 0 && LayoutJson is null && Icons is null or IconStyle.System && Crisp != true && Fade != true;

    internal string Signature => Kind + "|" + string.Join("|", Elements) + "|" + LayoutJson + "|" + Icons + "|" + Crisp + "|" + Fade;
}

internal static class ThemeCards
{
    internal static readonly XNamespace Ns = ThemeMarkup.Presentation, X = ThemeMarkup.Xaml;
    private static readonly string[] FontKeys = ["UIFont", "SerifFont", "DisplayFont", "MonoFont"];

    // ---------------------------------------------------------------- reading a theme as a deck

    /// <summary>The cards a theme is made of, in the order a hand shows them.</summary>
    internal static IReadOnlyList<ThemeCard> Deck(ThemeStudioDocument document, string from)
    {
        var root = XElement.Parse(document.Markup);
        var elements = root.Elements().Where(e => e.Attribute(X + "Key") is not null).ToList();
        string Key(XElement e) => (string)e.Attribute(X + "Key")!;
        var images = document.Original?.Images ?? new Dictionary<string, byte[]>();
        var fonts = document.Original?.Fonts ?? [];
        var cards = new List<ThemeCard>();
        var used = new HashSet<XElement>();

        ThemeCard Make(CardKind kind, string name, string detail, IEnumerable<XElement> parts)
        {
            var list = parts.ToList();
            foreach (var part in list) used.Add(part);
            var text = list.Select(p => p.ToString(SaveOptions.DisableFormatting)).ToList();
            var named = images.Where(i => text.Any(t => t.Contains("images/" + i.Key, StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(i => i.Key, i => i.Value, StringComparer.OrdinalIgnoreCase);
            var needsFonts = text.Any(t => t.Contains("fonts/", StringComparison.OrdinalIgnoreCase));
            return new ThemeCard(kind, name, detail, from, text, named, needsFonts ? fonts : []);
        }

        bool IsPicture(XElement e) => e.Name.LocalName is "DrawingBrush" or "VisualBrush" or "ImageBrush" or "LinearGradientBrush" or "RadialGradientBrush";

        // Stickers, one card each.
        var number = 0;
        foreach (var e in elements.Where(e => Key(e).StartsWith("Decor.", StringComparison.Ordinal)).OrderBy(Key, StringComparer.Ordinal))
            cards.Add(Make(CardKind.Sticker, "Sticker " + ++number, Key(e).StartsWith("Decor.Front", StringComparison.Ordinal) ? "In front of everything" : "Behind the panels", [e]));

        // Patterns and pictures on the window and the parts, one card each.
        foreach (var e in elements.Where(e => IsPicture(e) && (Key(e) is "Base" or "ListFill" || Key(e).StartsWith("Part.", StringComparison.Ordinal) && Key(e).EndsWith(".Fill", StringComparison.Ordinal))))
        {
            var key = Key(e);
            var parts = new List<XElement> { e };
            if (key == "Base" && elements.FirstOrDefault(x => Key(x) == "Theme.BaseColor") is { } flat) parts.Add(flat);
            var what = e.Name.LocalName switch { "ImageBrush" => "Picture", "LinearGradientBrush" or "RadialGradientBrush" => "Gradient", _ => "Pattern" };
            cards.Add(Make(CardKind.Pattern, what, PlaceName(key), parts));
        }

        // Effects
        void Effect(string name, string detail, params string[] keys)
        {
            var parts = elements.Where(e => keys.Contains(Key(e)) && !used.Contains(e)).ToList();
            if (parts.Any(e => Key(e) == keys[0])) cards.Add(Make(CardKind.Effect, name, detail, parts));
        }
        Effect("Screen effect", "Over the whole window", "OverlayFill", "OverlayVisibility");
        Effect("Light & shade", "A glow or shade over the window", "EdgeShade");
        Effect("Edge strip", "Down the left edge", "BindingFill", "BindingVisibility", "PageInset");
        Effect("Leaning tooltips", "Like sticky notes", "NoteTilt");

        // Fonts
        // CHANGED (decks of cards): pixel lettering is part of the font card, which there is even with no fonts set.
        // CHANGED (every card by default): the colors, panels, font, layout and icons cards are always in a deck,
        // even when the theme leaves those things to Clearspace: then the card shows Clearspace's own, and
        // changing it puts the change on that card. (They used to appear only once something was changed.)
        var fontParts = elements.Where(e => FontKeys.Contains(Key(e))).ToList();
        {
            var families = fontParts.Select(e => FamilyName(e.Value)).Distinct().ToList();
            var fontName = families.Count > 0 ? families[0] : document.Manifest.CrispText ? "Pixel lettering" : "Usual fonts";
            var fontDetail = families.Count > 1 ? "With " + string.Join(", ", families.Skip(1)) : families.Count == 1 ? "All text" : document.Manifest.CrispText ? "Crisp, pixel-sharp text" : "Clearspace's own fonts";
            if (document.Manifest.CrispText && families.Count > 0) fontDetail += " · pixel lettering";
            cards.Add(Make(CardKind.Font, fontName, fontDetail, fontParts) with { Crisp = document.Manifest.CrispText });
        }

        // Layout
        var layoutParts = elements.Where(e => Key(e).StartsWith("Explorer.", StringComparison.Ordinal) || Key(e) is "FunctionKeysVisibility" or "InterfaceScale").ToList();
        // CHANGED (decks of cards): the fading toolbar is part of the layout card.
        {   // CHANGED (every card by default): always
            var card = Make(CardKind.Layout, document.LayoutJson is not null ? "Custom layout" : LayoutName(layoutParts),
                document.Manifest.FadeToolbar ? "Where the parts sit · fading toolbar" : "Where the parts sit", layoutParts) with { Fade = document.Manifest.FadeToolbar };
            if (document.LayoutJson is { } json)
                card = card with
                {
                    Images = images.Where(i => json.Contains("\"" + i.Key + "\"", StringComparison.OrdinalIgnoreCase)).ToDictionary(i => i.Key, i => i.Value, StringComparer.OrdinalIgnoreCase),
                    LayoutJson = json
                };
            cards.Add(card);
        }

        // File icons
        var iconParts = elements.Where(e => ThemeFileIcons.IsKey(Key(e))).ToList();
        // CHANGED (every card by default): always
        cards.Add(Make(CardKind.Icons, iconParts.Count > 0 ? "Own icons" : IconName(document.Manifest.Icons), iconParts.Count > 0 ? iconParts.Count + " file icons" : "File icons", iconParts) with { Icons = document.Manifest.Icons });

        // Colors: every plain colour that is not a part's own.
        bool IsColor(XElement e) => e.Name.LocalName is "SolidColorBrush" or "Color";
        var colorParts = elements.Where(e => !used.Contains(e) && IsColor(e) && !Key(e).StartsWith("Part.", StringComparison.Ordinal) && !Key(e).StartsWith("Decor.", StringComparison.Ordinal)).ToList();
        foreach (var part in colorParts) used.Add(part);

        // Panels: each part's fill, outline and corners, corner sizes everywhere, and anything else left.
        var panelParts = elements.Where(e => !used.Contains(e) && !Key(e).StartsWith("Decor.", StringComparison.Ordinal)).ToList();
        // CHANGED (every card by default): always, first in the hand.
        cards.Insert(0, Make(CardKind.Panels, from + " panels", panelParts.Count > 0 ? "Fills, outlines and corners" : "Clearspace's own panels", panelParts));
        cards.Insert(0, Make(CardKind.Palette, from, colorParts.Count > 0 ? "Window, text and accent colors" : "Clearspace's own colors", colorParts));
        return cards;
    }

    internal static string PlaceName(string key) => key switch
    {
        "Base" => "On the window", "ListFill" => "Behind the files", "Part.Files.Fill" => "On the file area", "Part.Sidebar.Fill" => "On the sidebar",
        "Part.Toolbar.Fill" => "On the toolbar", "Part.Status.Fill" => "On the status bar", _ => "On " + key
    };

    private static string FamilyName(string family)
    {
        family = family.Trim();
        if (family.Contains('#')) family = family[(family.LastIndexOf('#') + 1)..];
        return family.Split(',')[0].Trim();
    }

    private static string LayoutName(IEnumerable<XElement> parts)
    {
        bool On(string key) => parts.Any(e => (string?)e.Attribute(X + "Key") == key && e.Value.Trim() == "Visible");
        bool Off(string key) => parts.Any(e => (string?)e.Attribute(X + "Key") == key && e.Value.Trim() == "Collapsed");
        var words = new List<string>();
        if (Off("Explorer.SidebarVisibility")) words.Add("No sidebar"); else if (On("Explorer.SidebarOnRight")) words.Add("Sidebar right");
        if (On("Explorer.ToolbarBottom")) words.Add("toolbar at the bottom");
        if (On("FunctionKeysVisibility")) words.Add("key bar");
        if (Off("Explorer.StatusBarVisibility")) words.Add("no status bar");
        if (words.Count == 0) return "Classic";
        var name = string.Join(", ", words);
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    internal static string IconName(IconStyle style) => style switch
    {
        IconStyle.System => "Windows icons", IconStyle.Pixel => "Pixel art", IconStyle.OneBit => "Black & white pixels",
        IconStyle.OneBitDark => "White pixels on dark", IconStyle.Ink => "Ink outlines", IconStyle.Line => "Simple outlines",
        IconStyle.Neon => "Neon glow", IconStyle.Sketch => "Pencil sketches", IconStyle.Tile => "File type tiles",
        IconStyle.Dot => "Colored dots", IconStyle.Mark => "Retro symbols", _ => "Windows icons"
    };

    // ---------------------------------------------------------------- playing and taking back

    /// <summary>The theme with the card played onto it. A pattern card can be aimed at another part
    /// (<paramref name="target"/>, a key such as "Part.Sidebar.Fill"); a sticker card takes a free place.</summary>
    internal static ThemeStudioDocument Play(ThemeStudioDocument document, ThemeCard card, string? target = null)
    {
        if (card.Kind == CardKind.Sticker) return PlaySticker(document, card);
        var next = document.WithAssets(card.Images, card.Fonts);
        foreach (var text in card.Elements)
        {
            var element = XElement.Parse(text);
            var key = (string?)element.Attribute(X + "Key");
            if (card.Kind == CardKind.Pattern && target is not null && key is not "Theme.BaseColor")
            {
                // A picture of the window's own background only goes behind the files or on the window.
                if (element.Name.LocalName == "ImageBrush" && target is not ("Base" or "ListFill" or "Part.Files.Fill")) target = "Part.Files.Fill";
                element.SetAttributeValue(X + "Key", target);
            }
            else if (card.Kind == CardKind.Pattern && target is not null and not "Base" && key == "Theme.BaseColor") continue;
            next = next.Put(element);
        }
        if (card.Kind == CardKind.Layout) next = next with { LayoutJson = card.LayoutJson };
        if (card.Icons is { } icons) next = next with { Manifest = next.Manifest with { Icons = icons } };
        if (card.Crisp is { } crisp) next = next with { Manifest = next.Manifest with { CrispText = crisp } };   // NEW (decks of cards)
        if (card.Fade is { } fade) next = next with { Manifest = next.Manifest with { FadeToolbar = fade } };
        next.Resources(); // a card Clearspace cannot read is refused here, before it reaches the screen
        return next;
    }

    private static ThemeStudioDocument PlaySticker(ThemeStudioDocument document, ThemeCard card)
    {
        var element = XElement.Parse(card.Elements[0]);
        var source = ((string?)element.Attribute("ImageSource") ?? "").Replace("images/", "", StringComparison.Ordinal);
        if (!card.Images.TryGetValue(source, out var bytes)) throw new ArgumentException("This sticker's picture is missing.");
        var front = ((string?)element.Attribute(X + "Key"))?.StartsWith("Decor.Front", StringComparison.Ordinal) == true;
        var taken = document.Stickers().Select(s => s.Key).ToHashSet();
        var places = front ? ThemeStudioDocument.FrontStickers.Concat(ThemeStudioDocument.BackStickers) : ThemeStudioDocument.BackStickers.Concat(ThemeStudioDocument.FrontStickers);
        var free = places.FirstOrDefault(k => !taken.Contains(k)) ?? throw new ArgumentException("A theme holds up to eight stickers. Take one out to add another.");
        var box = (string?)element.Attribute("Viewport") is { } viewport ? SafeRect(viewport) : new Rect(.62, .52, .36, .46);
        var opacity = double.TryParse((string?)element.Attribute("Opacity"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var o) ? o : 1;
        return document.Sticker(free, bytes, box, opacity, (string?)element.Attribute("Stretch") == "UniformToFill");
    }

    private static Rect SafeRect(string text)
    {
        try { return Rect.Parse(text); }
        catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException) { return new Rect(.62, .52, .36, .46); }
    }

    /// <summary>NEW (decks of cards): a theme with nothing in it; everything is Clearspace's own until cards are
    /// played onto it.</summary>
    internal static ThemeStudioDocument EmptyTheme(ThemeManifest manifest) =>
        new(new XElement(Ns + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName)).ToString(SaveOptions.DisableFormatting),
            manifest with { Icons = IconStyle.System, CrispText = false, FadeToolbar = false });

    /// <summary>NEW (decks of cards): the theme a deck makes: its cards played one by one onto an empty theme.
    /// Anything Clearspace would refuse is refused here.</summary>
    internal static ThemeStudioDocument Build(ThemeManifest manifest, IEnumerable<ThemeCard> cards)
    {
        var document = EmptyTheme(manifest);
        foreach (var card in cards) document = Play(document, card);
        ThemeLayout.Parse(document.LayoutJson);
        return document;
    }

    /// <summary>The theme without the card: what it set goes back to what Clearspace uses when a theme leaves
    /// it out.</summary>
    internal static ThemeStudioDocument TakeOut(ThemeStudioDocument document, ThemeCard card)
    {
        var next = document.Without(card.Keys.ToArray());
        if (card.Kind == CardKind.Layout) next = next with { LayoutJson = null };
        if (card.Kind == CardKind.Icons) next = next with { Manifest = next.Manifest with { Icons = IconStyle.System } };
        if (card.Crisp is not null) next = next with { Manifest = next.Manifest with { CrispText = false } };   // NEW (decks of cards)
        if (card.Fade is not null) next = next with { Manifest = next.Manifest with { FadeToolbar = false } };
        return next.DropUnusedImages();
    }

    /// <summary>A card made from one part of a theme: these keys, as they are now.</summary>
    internal static ThemeCard? FromKeys(ThemeStudioDocument document, CardKind kind, string name, string detail, string from, IEnumerable<string> keys)
    {
        var wanted = keys.ToHashSet(StringComparer.Ordinal);
        var parts = XElement.Parse(document.Markup).Elements().Where(e => (string?)e.Attribute(X + "Key") is { } k && wanted.Contains(k)).ToList();
        if (parts.Count == 0) return null;
        var text = parts.Select(p => p.ToString(SaveOptions.DisableFormatting)).ToList();
        var images = (document.Original?.Images ?? new Dictionary<string, byte[]>()).Where(i => text.Any(t => t.Contains("images/" + i.Key, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(i => i.Key, i => i.Value, StringComparer.OrdinalIgnoreCase);
        return new ThemeCard(kind, name, detail, from, text, images, text.Any(t => t.Contains("fonts/", StringComparison.OrdinalIgnoreCase)) ? document.Original?.Fonts ?? [] : []);
    }

    /// <summary>What a card looks like: the card's own elements loaded like a small theme (missing keys come
    /// from Clearspace's fallback), so a hand can draw each card from its real colors, patterns and pictures.</summary>
    internal static ResourceDictionary? Look(ThemeCard card)
    {
        try
        {
            var root = new XElement(Ns + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName));
            foreach (var text in card.Elements)
            {
                var element = XElement.Parse(text);
                foreach (var declaration in element.DescendantsAndSelf().SelectMany(e => e.Attributes()).Where(a => a.IsNamespaceDeclaration).ToArray()) declaration.Remove();
                root.Add(element);
            }
            var manifest = new ThemeManifest(card.Name, "", "", "1.0", false, card.Icons ?? IconStyle.System, false, false, false);
            return ThemeLoader.Load(new ThemePackage("card", manifest, root.ToString(), card.Fonts, card.Images));
        }
        catch (Exception e) when (e is ThemeFormatException or ArgumentException or System.Xml.XmlException or IOException or InvalidOperationException) { return null; }
    }

    // ---------------------------------------------------------------- cards you keep

    private sealed record SavedCard(string Kind, string Name, string Detail, string From, List<string> Elements,
        Dictionary<string, string> Images, List<SavedFont> Fonts, string? LayoutJson, string? Icons, bool? Crisp = null, bool? Fade = null);
    private sealed record SavedFont(string FileName, string Data);

    private static string LibraryFile(string? folder) => Path.Combine(folder ?? StudioLibrary.Folder, "cards.json");

    /// <summary>The cards you kept, newest first.</summary>
    internal static List<ThemeCard> Saved(string? folder = null)
    {
        try
        {
            var path = LibraryFile(folder);
            if (!File.Exists(path) || new FileInfo(path).Length > 64L * 1024 * 1024) return [];
            var saved = JsonSerializer.Deserialize<List<SavedCard>>(File.ReadAllText(path)) ?? [];
            return saved.Where(s => Enum.TryParse<CardKind>(s.Kind, out var known) && Enum.IsDefined(known) && !int.TryParse(s.Kind, out _)).Take(60).Select(s => new ThemeCard(Enum.Parse<CardKind>(s.Kind), s.Name, s.Detail, s.From, s.Elements,
                    s.Images.ToDictionary(i => i.Key, i => Convert.FromBase64String(i.Value), StringComparer.OrdinalIgnoreCase),
                    s.Fonts.Select(f => new ThemeAsset(f.FileName, Convert.FromBase64String(f.Data))).ToList())
                { LayoutJson = s.LayoutJson, Icons = Enum.TryParse<IconStyle>(s.Icons, out var icons) && Enum.IsDefined(icons) ? icons : null, Crisp = s.Crisp, Fade = s.Fade }).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or NotSupportedException) { return []; }
    }

    /// <summary>Keeps a card (the same card is kept once).</summary>
    internal static void Keep(ThemeCard card, string? folder = null)
    {
        var all = Saved(folder).Where(c => c.Signature != card.Signature).Prepend(card).Take(60).Select(c => new SavedCard(c.Kind.ToString(), c.Name, c.Detail, c.From, c.Elements.ToList(),
            c.Images.ToDictionary(i => i.Key, i => Convert.ToBase64String(i.Value)), c.Fonts.Select(f => new SavedFont(f.FileName, Convert.ToBase64String(f.Data))).ToList(),
            c.LayoutJson, c.Icons?.ToString(), c.Crisp, c.Fade)).ToList();
        WriteLibrary(all, folder);
    }

    /// <summary>Forgets a kept card.</summary>
    internal static void Forget(ThemeCard card, string? folder = null)
    {
        var all = Saved(folder).Where(c => c.Signature != card.Signature).Select(c => new SavedCard(c.Kind.ToString(), c.Name, c.Detail, c.From, c.Elements.ToList(),
            c.Images.ToDictionary(i => i.Key, i => Convert.ToBase64String(i.Value)), c.Fonts.Select(f => new SavedFont(f.FileName, Convert.ToBase64String(f.Data))).ToList(),
            c.LayoutJson, c.Icons?.ToString(), c.Crisp, c.Fade)).ToList();
        WriteLibrary(all, folder);
    }

    private static void WriteLibrary(List<SavedCard> all, string? folder)
    {
        var path = LibraryFile(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all));
        File.Move(temp, path, true);
    }
}
