using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Clearspace.Services;

// NEW (theme codes): a card, or a whole deck, as a line of text you can paste anywhere (a chat, a forum, a
// message) and someone else can paste into Clearspace. A code looks like "CS-COLORS-…" for a card and
// "CS-DECK-…" for a deck.
// CHANGED (decks of cards): a deck code is just its cards. Every card of the theme (ThemeCards.Deck) goes in,
// with the deck's name and details; reading it plays every card onto an empty theme, which gives the theme
// back. (The first version wrote the theme's settings as one list.)
//   - What a card carries: its settings (colors, panels, patterns, corners, layout, effects, icon style, fonts by
//     name) and its switches (pixel lettering on the font card, the fading toolbar on the layout card).
//   - What stays out: pictures (stickers, a picture behind the files, your own icon pictures) and font files.
//     They are far too big for a code. A picture card has no code and a deck code leaves those cards out, and
//     says so. A font in a code is used if the other PC has it installed.
//   - How: the settings are written short (a plain color is "Accent=#FFF0B35A"; anything else is its XAML),
//     packed with Deflate and written in URL-safe Base64.
//   - Safety: a code is only data. What it holds is checked by the same rules as a theme file (ThemeLoader)
//     before it is used, so a code can only change how Clearspace looks. Codes are size-limited when read.
internal static partial class ThemeCodes
{
    private const string Prefix = "CS-";
    private const string DeckWord = "DECK";
    private const int Version = 2;
    private const int MostCodeLength = 400_000;       // characters
    private const int MostUnpacked = 2 * 1024 * 1024;  // bytes, after unpacking
    private const int MostElements = 5_000;
    private const int MostCards = 64;
    private static readonly XNamespace Ns = ThemeMarkup.Presentation, X = ThemeMarkup.Xaml;

    // One card: kind, name, detail, where it is from, its settings, and its layout, icon style and switches.
    private sealed record CardData(string K, string N, string D, string F, List<string> E,
        string? L = null, string? I = null, bool? C = null, bool? Fa = null);

    // A code: one card, or a deck (its cards and its details).
    private sealed record CodeData(int V, CardData? Card = null, List<CardData>? Deck = null,
        string? Name = null, string? Author = null, string? Description = null, string? Edition = null, bool EInk = false);

    // ---------------------------------------------------------------- writing

    /// <summary>The card's code, or null with the reason in <paramref name="note"/>. When it works, the note says
    /// what stayed behind (a font file), or is null.</summary>
    internal static string? CardCode(ThemeCard card, out string? note)
    {
        if (Data(card, out var fonts) is not { } data)
        {
            note = card.Kind == CardKind.Sticker || card.Images.Count > 0
                ? "This card is a picture, which is too big for a code. Share the theme file instead."
                : "This card has nothing a code can carry.";
            return null;
        }
        note = fonts ? "The font file is not in the code; it is used if the other PC has the font." : null;
        return Write(Word(card.Kind), new CodeData(Version, Card: data));
    }

    /// <summary>The deck's code: every card of the theme, and its name and details. <paramref name="note"/> says
    /// which cards stayed behind (pictures) and whether font files did, or is null.</summary>
    internal static string DeckCode(ThemeStudioDocument document, string name, out string? note)
    {
        var cards = new List<CardData>();
        int pictures = 0; var fonts = false;
        foreach (var card in ThemeCards.Deck(document, ThemeManifest.Tidy(name, 60)))
        {
            if (Data(card, out var usesFonts) is { } data) { cards.Add(data); fonts |= usesFonts; }
            else if (card.Kind == CardKind.Sticker || card.Images.Count > 0) pictures++;
        }
        var behind = new List<string>();
        if (pictures > 0) behind.Add(pictures == 1 ? "1 picture card" : $"{pictures} picture cards");
        if (fonts) behind.Add("its font files");
        note = behind.Count == 0 ? null : $"The code leaves out {string.Join(" and ", behind)}; share the theme file to include them.";
        var m = document.Manifest;
        return Write(DeckWord, new CodeData(Version, Deck: cards, Name: ThemeManifest.Tidy(name, 60), Author: m.Author, Description: m.Description, Edition: m.Version, EInk: m.EInk));
    }

    private static string Word(CardKind kind) => kind switch
    {
        CardKind.Palette => "COLORS", CardKind.Panels => "PANELS", CardKind.Pattern => "PATTERN", CardKind.Font => "FONT",
        CardKind.Layout => "LAYOUT", CardKind.Effect => "EFFECT", CardKind.Icons => "ICONS", _ => "CARD"
    };

    // A card as it goes in a code, or null when it is a picture (or holds nothing a code can carry).
    private static CardData? Data(ThemeCard card, out bool fonts)
    {
        fonts = false;
        if (card.Kind == CardKind.Sticker) return null;
        var lines = new List<string>();
        foreach (var text in card.Elements)
        {
            var element = XElement.Parse(text);
            if (Mentions(element, "images/")) continue;
            fonts |= Mentions(element, "fonts/");
            lines.Add(Short(element));
        }
        // A card that is only a picture (a picture behind the files, own icon pictures) has nothing left.
        if (lines.Count == 0 && card.LayoutJson is null && card.Icons is null && card.Crisp is null && card.Fade is null) return null;
        return new CardData(card.Kind.ToString(), card.Name, card.Detail, card.From, lines, card.LayoutJson, card.Icons?.ToString(), card.Crisp, card.Fade);
    }

    private static bool Mentions(XElement element, string what) =>
        element.DescendantsAndSelf().Any(e => e.Attributes().Any(a => a.Value.Contains(what, StringComparison.OrdinalIgnoreCase))
                                              || (!e.HasElements && e.Value.Contains(what, StringComparison.OrdinalIgnoreCase)));

    // One setting, written short: "Key=#AARRGGBB" for a plain brush, "Key:#AARRGGBB" for a colour, otherwise its
    // XAML without the namespace declarations. A bundled font becomes its family name.
    private static string Short(XElement element)
    {
        var key = (string)element.Attribute(X + "Key")!;
        var others = element.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name != X + "Key").ToList();
        if (element.Name == Ns + "SolidColorBrush" && !element.HasElements && others.Count == 1 && others[0].Name == "Color" && IsHex(others[0].Value))
            return key + "=" + others[0].Value;
        if (element.Name == Ns + "Color" && !element.HasElements && others.Count == 0 && IsHex(element.Value.Trim()))
            return key + ":" + element.Value.Trim();
        var copy = new XElement(element);
        foreach (var e in copy.DescendantsAndSelf())
        {
            foreach (var a in e.Attributes().ToArray())
                if (a.Value.Contains("fonts/#", StringComparison.OrdinalIgnoreCase)) a.Value = BundledFont().Replace(a.Value, "");
            if (!e.HasElements && e.Value.Contains("fonts/#", StringComparison.OrdinalIgnoreCase)) e.Value = BundledFont().Replace(e.Value, "");
        }
        // Declared once on the setting itself so it is written with the usual "x:" (not a made-up prefix), then
        // the declarations are taken off the text: reading puts them back.
        foreach (var declaration in copy.DescendantsAndSelf().SelectMany(e => e.Attributes()).Where(a => a.IsNamespaceDeclaration).ToArray()) declaration.Remove();
        copy.SetAttributeValue(XNamespace.Xmlns + "x", ThemeMarkup.Xaml);
        var text = copy.ToString(SaveOptions.DisableFormatting);
        return text.Replace(" xmlns=\"" + ThemeMarkup.Presentation + "\"", "", StringComparison.Ordinal)
                   .Replace(" xmlns:x=\"" + ThemeMarkup.Xaml + "\"", "", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"[^,""<>]*fonts/#", RegexOptions.IgnoreCase)]
    private static partial Regex BundledFont();

    [GeneratedRegex(@"\A#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?\z")]
    private static partial Regex Hex();

    private static bool IsHex(string text) => Hex().IsMatch(text);

    private static string Write(string word, CodeData payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.SmallestSize, leaveOpen: true)) deflate.Write(json);
        var base64 = Convert.ToBase64String(packed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Prefix + word + "-" + base64;
    }

    // ---------------------------------------------------------------- reading

    /// <summary>Whether the text looks like a Clearspace code at all (it starts with "CS-").</summary>
    internal static bool LooksLikeCode(string? text) => text is not null && Clean(text).StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a code: a card (<paramref name="card"/>) or a deck, which comes back as the theme its cards
    /// make (<paramref name="theme"/>, with its name in <paramref name="name"/>). Returns false with the reason in
    /// <paramref name="problem"/>. What it holds has been checked like a theme file.</summary>
    internal static bool TryRead(string text, out ThemeCard? card, out ThemeStudioDocument? theme, out string name, out string? problem)
    {
        card = null; theme = null; name = ""; problem = null;
        try
        {
            var code = Clean(text);
            if (!code.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) { problem = "That is not a Clearspace code (they start with CS-)."; return false; }
            if (code.Length > MostCodeLength) { problem = "That code is too long to be a Clearspace code."; return false; }
            var dash = code.IndexOf('-', Prefix.Length);
            if (dash < 0) { problem = "That code is cut short. Copy all of it."; return false; }
            var base64 = code[(dash + 1)..].Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var data = JsonSerializer.Deserialize<CodeData>(Unpack(Convert.FromBase64String(base64)));
            if (data is null || data.V != Version) { problem = "That code was made by a different Clearspace, or it is damaged."; return false; }

            if (data.Deck is { } deck)
            {
                if (deck.Count > MostCards || deck.Sum(c => c?.E?.Count ?? 0) > MostElements) { problem = "That code holds too much."; return false; }
                name = ThemeManifest.Tidy(data.Name ?? "", 60) is { Length: > 0 } n ? n : "Shared theme";
                var manifest = new ThemeManifest(name, ThemeManifest.Tidy(data.Author ?? "", 60), ThemeManifest.Tidy(data.Description ?? "", 200),
                    ThemeManifest.Tidy(data.Edition ?? "", 20) is { Length: > 0 } v ? v : "1.0",
                    false,   // a pasted theme never lists itself as experimental
                    IconStyle.System, false, data.EInk, false);   // the icon style and switches come with the cards
                // The deck's cards, played one by one onto an empty theme.
                var document = ThemeCards.Build(manifest, deck.Select(entry => Card(entry ?? throw new FormatException("An empty card."))).ToList());
                theme = document;
                return true;
            }
            if (data.Card is not { } one) { problem = "That code holds no card."; return false; }
            if ((one.E?.Count ?? 0) > MostElements) { problem = "That code holds too much."; return false; }
            var read = Card(one);
            ThemeCards.Play(ThemeCards.EmptyTheme(new ThemeManifest("Check", "", "", "1.0", false, IconStyle.System, false, false, false)), read);   // refused here if Clearspace would refuse it
            card = read;
            name = read.Name;
            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or JsonException or System.Xml.XmlException or ArgumentException
                                      or ThemeFormatException or InvalidOperationException or NotSupportedException or IOException or NullReferenceException)
        {
            problem = "That code could not be read. Copy all of it and try again.";
            return false;
        }
    }

    // A card back from a code. Its layout is checked like a theme's.
    private static ThemeCard Card(CardData data)
    {
        if (Named<CardKind>(data.K) is not { } kind || kind == CardKind.Sticker) throw new FormatException("A kind of card this Clearspace does not know.");
        var texts = (data.E ?? []).Select(line => Long(line ?? "").ToString(SaveOptions.DisableFormatting)).ToList();
        var card = new ThemeCard(kind, ThemeManifest.Tidy(data.N ?? "", 60) is { Length: > 0 } cardName ? cardName : "Shared card", ThemeManifest.Tidy(data.D ?? "", 120),
            ThemeManifest.Tidy(data.F ?? "", 60) is { Length: > 0 } from ? from : "A code", texts, new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase), [])
        { LayoutJson = data.L, Icons = Named<IconStyle>(data.I), Crisp = data.C, Fade = data.Fa };
        ThemeLayout.Parse(card.LayoutJson);
        return card;
    }

    // A value by its name only: not a number, not several names joined, and one this Clearspace knows.
    internal static T? Named<T>(string? text) where T : struct, Enum =>
        text is { Length: > 0 } && !int.TryParse(text, out _) && !text.Contains(',') && Enum.TryParse<T>(text, out var value) && Enum.IsDefined(value) ? value : null;

    // A code may come back with line breaks, spaces or quotes round it from a chat or an e-mail.
    private static string Clean(string text) => new(text.Trim().Trim('"', '\'', '`', '<', '>').Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static byte[] Unpack(byte[] packed)
    {
        using var input = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > MostUnpacked) throw new InvalidDataException("Too big.");
        }
        return output.ToArray();
    }

    // One setting back from its short form, with its namespaces.
    private static XElement Long(string line)
    {
        if (!line.StartsWith('<'))
        {
            var at = line.IndexOfAny(['=', ':']);
            if (at <= 0) throw new FormatException("Unknown setting.");
            var key = line[..at]; var value = line[(at + 1)..];
            if (!IsHex(value)) throw new FormatException("Unknown color.");
            return line[at] == '='
                ? new XElement(Ns + "SolidColorBrush", new XAttribute(XNamespace.Xmlns + "x", ThemeMarkup.Xaml), new XAttribute(X + "Key", key), new XAttribute("Color", value))
                : new XElement(Ns + "Color", new XAttribute(XNamespace.Xmlns + "x", ThemeMarkup.Xaml), new XAttribute(X + "Key", key), value);
        }
        var wrapped = XElement.Parse($"<r xmlns=\"{ThemeMarkup.Presentation}\" xmlns:x=\"{ThemeMarkup.Xaml}\">{line}</r>");
        var element = wrapped.Elements().Single();
        element.Remove();
        if (element.Attribute(X + "Key") is null) throw new FormatException("A setting without a name.");
        element.SetAttributeValue(XNamespace.Xmlns + "x", ThemeMarkup.Xaml);   // written with "x:" like a deck's own cards
        return element;
    }
}
