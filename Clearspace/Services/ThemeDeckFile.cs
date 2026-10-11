using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Clearspace.Services;

// NEW (deck files): a whole deck in one file (name.csdeck), pictures and fonts included. A deck code is quicker to
// send but cannot carry pictures or font files; a deck file carries everything, so Dino Park's tyrannosaur and
// its hand-cut font go along with its colors.
// What is in the file (a zip):
//   deck.json     the deck's name and details, and its cards: each card's kind, name, settings (theme.xaml
//                 elements), layout, icon style and switches, and the names of the pictures and fonts it uses;
//   images/…      the pictures the cards use (stickers, a picture behind the files, own icons, layout pictures);
//   fonts/…       the font files the cards use.
// Reading a deck file plays its cards onto an empty theme (ThemeCards.Build), so what it holds is checked by the
// same rules as a theme file before anything is shown. Sizes and names are checked first, so a damaged or made-up
// file cannot fill the disk or reach outside the file.
internal static partial class ThemeDeckFile
{
    internal const string Extension = ".csdeck";
    private const int Version = 1;
    private const long MostFileSize = 160L * 1024 * 1024;
    private const long MostEntrySize = 32L * 1024 * 1024;
    private const long MostTotalSize = 160L * 1024 * 1024;
    private const int MostEntries = 500;
    private const int MostCards = 64;

    private sealed record DeckCard(string K, string N, string D, string F, List<string> E,
        string? L, string? I, bool? C, bool? Fa, List<string> Images, List<string> Fonts);

    private sealed record DeckData(int V, string Name, string? Author, string? Description, string? Edition, bool EInk, List<DeckCard> Cards);

    /// <summary>A deck read from a file: its name, its details (as a manifest) and its cards.</summary>
    internal sealed record Deck(string Name, ThemeManifest Manifest, IReadOnlyList<ThemeCard> Cards)
    {
        /// <summary>The theme the deck's cards make.</summary>
        internal ThemeStudioDocument Theme() => ThemeCards.Build(Manifest, Cards);
    }

    // ---------------------------------------------------------------- writing

    /// <summary>Writes the theme's deck (all its cards, with their pictures and fonts) to <paramref name="path"/>.</summary>
    internal static void Write(string path, ThemeStudioDocument document, string name)
    {
        name = ThemeManifest.Tidy(name, 60) is { Length: > 0 } n ? n : "My deck";
        var cards = ThemeCards.Deck(document, name);
        var images = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var fonts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<DeckCard>();
        foreach (var card in cards)
        {
            foreach (var (imageName, bytes) in card.Images) images[imageName] = bytes;
            foreach (var font in card.Fonts) fonts[font.FileName] = font.Data;
            entries.Add(new DeckCard(card.Kind.ToString(), card.Name, card.Detail, card.From, card.Elements.ToList(), card.LayoutJson, card.Icons?.ToString(),
                card.Crisp, card.Fade, card.Images.Keys.ToList(), card.Fonts.Select(f => f.FileName).ToList()));
        }
        var m = document.Manifest;
        var data = new DeckData(Version, name, m.Author, m.Description, m.Version, m.EInk, entries);

        var temp = path + ".tmp";
        try
        {
            using (var file = File.Create(temp))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (var stream = zip.CreateEntry("deck.json", CompressionLevel.Optimal).Open()) JsonSerializer.Serialize(stream, data);
                foreach (var (file2, bytes) in images) { using var s = zip.CreateEntry("images/" + file2, CompressionLevel.Optimal).Open(); s.Write(bytes); }
                foreach (var (file2, bytes) in fonts) { using var s = zip.CreateEntry("fonts/" + file2, CompressionLevel.Optimal).Open(); s.Write(bytes); }
            }
            File.Move(temp, path, true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    // ---------------------------------------------------------------- reading

    /// <summary>Reads a deck file. Throws ThemeFormatException (with a reason a person can read) when the file
    /// is not a deck Clearspace can use.</summary>
    internal static Deck Read(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MostFileSize) throw new ThemeFormatException("This deck file is too big.");
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count > MostEntries) throw new ThemeFormatException("This deck file holds too many files.");
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.Length > MostEntrySize) throw new ThemeFormatException("A file in this deck is too big.");
                total += entry.Length;
            }
            if (total > MostTotalSize) throw new ThemeFormatException("This deck file is too big.");

            var json = zip.GetEntry("deck.json") ?? throw new ThemeFormatException("This is not a Clearspace deck file (it has no deck.json).");
            DeckData? data;
            using (var stream = json.Open()) data = JsonSerializer.Deserialize<DeckData>(stream);
            if (data is null || data.V != Version || data.Cards is null) throw new ThemeFormatException("This deck file was made by a different Clearspace, or it is damaged.");
            if (data.Cards.Count > MostCards) throw new ThemeFormatException("This deck holds too many cards.");

            byte[] Asset(string folder, string file)
            {
                if (!SafeName().IsMatch(file ?? "")) throw new ThemeFormatException("This deck names a file in a way Clearspace does not accept.");
                var found = zip.GetEntry(folder + "/" + file) ?? throw new ThemeFormatException($"This deck is missing {file}.");
                using var stream = found.Open();
                using var copy = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)   // stops as soon as it is too big, whatever the zip claims
                {
                    copy.Write(buffer, 0, read);
                    if (copy.Length > MostEntrySize) throw new ThemeFormatException("A file in this deck is too big.");
                }
                return copy.ToArray();
            }

            var name = ThemeManifest.Tidy(data.Name ?? "", 60) is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(path);
            var cards = new List<ThemeCard>();
            foreach (var entry in data.Cards)
            {
                if (entry is null) throw new ThemeFormatException("This deck has an empty card.");
                var kind = ThemeCodes.Named<CardKind>(entry.K) ?? throw new ThemeFormatException("This deck holds a kind of card Clearspace does not know.");
                var images = (entry.Images ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(file => file, file => Asset("images", file), StringComparer.OrdinalIgnoreCase);
                var fonts = (entry.Fonts ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Select(file => new ThemeAsset(file, Asset("fonts", file))).ToList();
                cards.Add(new ThemeCard(kind, ThemeManifest.Tidy(entry.N ?? "", 60) is { Length: > 0 } cardName ? cardName : "Card", ThemeManifest.Tidy(entry.D ?? "", 120),
                    ThemeManifest.Tidy(entry.F ?? "", 60) is { Length: > 0 } from ? from : name, (entry.E ?? []).Select(e => e ?? throw new ThemeFormatException("This deck has an empty setting.")).ToList(),
                    images, fonts)
                { LayoutJson = entry.L, Icons = ThemeCodes.Named<IconStyle>(entry.I), Crisp = entry.C, Fade = entry.Fa });
            }
            var manifest = new ThemeManifest(name, ThemeManifest.Tidy(data.Author ?? "", 60), ThemeManifest.Tidy(data.Description ?? "", 200),
                ThemeManifest.Tidy(data.Edition ?? "", 20) is { Length: > 0 } v ? v : "1.0", false, IconStyle.System, false, data.EInk, false);
            var deck = new Deck(name, manifest, cards);
            deck.Theme();   // every card is checked like a theme file before the deck is handed out
            return deck;
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or System.Xml.XmlException or ArgumentException or InvalidOperationException
                                      or NotSupportedException or FormatException or NullReferenceException)
        {
            throw new ThemeFormatException("This deck file could not be read: " + e.Message);
        }
    }

    // A picture or font named inside a deck: a plain file name (any a theme may use), nothing that could reach a
    // folder. Files are only ever looked up by this name inside the deck; nothing is written to disk.
    [GeneratedRegex(@"\A(?!\.)[^/\\:*?""<>|\x00-\x1f]{1,150}\z")]
    private static partial Regex SafeName();
}
