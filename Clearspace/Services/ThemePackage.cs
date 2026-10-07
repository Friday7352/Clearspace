// Clearspace | Theme files: what is inside one, and reading it.
// NEW (theme files): a theme is one file - an ordinary .zip - in the Themes folder beside Clearspace.exe:
//
//     Sunset.zip
//       theme.json     its name, who made it, and a few switches (icons, crisp text ...)
//       theme.xaml     the colours, fonts and corner sizes (the keys are listed in the README)
//       fonts\         optional: font files; theme.xaml names one as "fonts/#Family Name"
//       images\        optional: pictures; theme.xaml paints with one as ImageBrush ImageSource="images/paper.png"
//
// The file's name is the theme's id (what settings remember); "name" in theme.json is what the menu shows.
// A plain folder holding the same files works too, which is handy while a theme is still being made:
// every save shows at once, with nothing to zip again.
// Zips made by zipping the theme's folder (so everything sits one folder down) are read as well.
// This file only reads, and knows nothing of WPF. ThemeMarkup checks theme.xaml, ThemeLoader turns it into
// resources, ThemeLibrary lists the Themes folder, and ThemeService shows a theme.
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Clearspace.Services;

/// <summary>A theme that cannot be used. The message is written for the person who made or added it.</summary>
internal sealed class ThemeFormatException(string message) : Exception(message);

/// <summary>theme.json: what a theme is called and the switches it sets.</summary>
/// <param name="Name">What the Themes page calls it.</param>
/// <param name="Experimental">Listed under "Experimental" (only for the themes that come with Clearspace).</param>
/// <param name="Icons">Windows' own icons, or one of the icon sets Clearspace draws (ThemeIcons).</param>
/// <param name="FadeToolbar">The toolbar fades while the pointer is elsewhere.</param>
/// <param name="EInk">The window looks and refreshes like an e-ink panel.</param>
/// <param name="CrispText">Text is drawn with hard pixel edges instead of smoothing.</param>
internal sealed record ThemeManifest(string Name, string Author, string Description, string Version,
    bool Experimental, IconStyle Icons, bool FadeToolbar, bool EInk, bool CrispText)
{
    /// <summary>The newest theme file layout this Clearspace understands ("format" in theme.json).</summary>
    public const int Format = 1;

    /// <summary>Reads theme.json. Anything left out takes its ordinary value; "name" falls back to the file's name.</summary>
    public static ThemeManifest Parse(string json, string fallbackName)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 8
            });
        }
        catch (JsonException error)
        {
            throw new ThemeFormatException($"theme.json is not valid JSON (line {(error.LineNumber ?? 0) + 1}).");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ThemeFormatException("theme.json must be a list of settings between { and }.");

            var format = 1;
            string name = string.Empty, author = string.Empty, description = string.Empty, version = string.Empty;
            var icons = IconStyle.System;
            bool experimental = false, fadeToolbar = false, eInk = false, crispText = false;

            foreach (var setting in root.EnumerateObject())
            {
                var value = setting.Value;
                switch (setting.Name.ToLowerInvariant())
                {
                    case "format":
                        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out format) || format < 1)
                            throw new ThemeFormatException("theme.json: \"format\" must be a whole number, such as 1.");
                        break;
                    case "name": name = Words(value, "name", 60); break;
                    case "author": author = Words(value, "author", 60); break;
                    case "description": description = Words(value, "description", 300); break;
                    case "version": version = Words(value, "version", 30); break;
                    case "icons":
                        var style = Words(value, "icons", 30);
                        if (!Enum.TryParse(style, ignoreCase: true, out icons) || !Enum.IsDefined(icons) || int.TryParse(style, out _))
                            throw new ThemeFormatException(
                                $"theme.json: \"icons\" is \"{style}\", which is not one of {string.Join(", ", Enum.GetNames<IconStyle>())}.");
                        break;
                    case "experimental": experimental = Switch(value, "experimental"); break;
                    case "fadetoolbar": fadeToolbar = Switch(value, "fadeToolbar"); break;
                    case "eink": eInk = Switch(value, "eInk"); break;
                    case "crisptext": crispText = Switch(value, "crispText"); break;
                    // Anything else is left alone, so a theme made for a later Clearspace still loads here.
                }
            }

            if (format > Format)
                throw new ThemeFormatException("This theme was made for a newer Clearspace. Update Clearspace to use it.");

            if (name.Length == 0)
                name = Tidy(fallbackName, 60);
            return new ThemeManifest(name, author, description, version, experimental, icons, fadeToolbar, eInk, crispText);
        }
    }

    private static string Words(JsonElement value, string setting, int most) => value.ValueKind switch
    {
        JsonValueKind.String => Tidy(value.GetString() ?? string.Empty, most),
        JsonValueKind.Number => Tidy(value.GetRawText(), most),   // "version": 1.2 without quotes
        _ => throw new ThemeFormatException($"theme.json: \"{setting}\" must be text in quotes.")
    };

    private static bool Switch(JsonElement value, string setting) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new ThemeFormatException($"theme.json: \"{setting}\" must be true or false.")
    };

    // One line of ordinary text: no line breaks or control characters, and not longer than a menu can show.
    internal static string Tidy(string text, int most)
    {
        var tidy = new StringBuilder(Math.Min(text.Length, most));
        var gap = false;
        foreach (var letter in text.Trim())
        {
            if (char.IsControl(letter) || char.IsWhiteSpace(letter))
            {
                gap = tidy.Length > 0;
                continue;
            }
            if (gap) tidy.Append(' ');
            gap = false;
            tidy.Append(letter);
            if (tidy.Length >= most) break;
        }
        return tidy.ToString();
    }
}

/// <summary>A font or picture that travels inside a theme.</summary>
internal sealed record ThemeAsset(string FileName, byte[] Data);

/// <summary>Everything read out of one theme file (or theme folder).</summary>
internal sealed class ThemePackage
{
    public const string ManifestName = "theme.json";
    public const string MarkupName = "theme.xaml";
    public const string FontsFolder = "fonts";
    public const string ImagesFolder = "images";

    // A theme is small: a page of settings, a page or two of colours, perhaps a few fonts and pictures.
    // The limits are far above anything real and keep a damaged or hostile file from filling memory.
    private const int MostManifestBytes = 64 * 1024;
    private const int MostMarkupBytes = 8 * 1024 * 1024;
    private const int MostAssetBytes = 16 * 1024 * 1024;
    private const long MostAssetBytesInAll = 64L * 1024 * 1024;
    private const int MostAssets = 64;
    private const int MostEntries = 2000;

    private static readonly string[] FontEndings = [".ttf", ".otf", ".ttc"];
    private static readonly string[] ImageEndings = [".png", ".jpg", ".jpeg", ".bmp", ".gif"];

    private ThemePackage(string source, ThemeManifest manifest, string markup,
        IReadOnlyList<ThemeAsset> fonts, IReadOnlyDictionary<string, byte[]> images)
    {
        Source = source;
        Manifest = manifest;
        Markup = markup;
        Fonts = fonts;
        Images = images;
    }

    /// <summary>The theme file or folder this was read from.</summary>
    public string Source { get; }

    public ThemeManifest Manifest { get; }

    /// <summary>The text of theme.xaml, exactly as written (ThemeMarkup checks it before it is used).</summary>
    public string Markup { get; }

    /// <summary>The files in fonts\.</summary>
    public IReadOnlyList<ThemeAsset> Fonts { get; }

    /// <summary>The files in images\, by file name ("paper.png").</summary>
    public IReadOnlyDictionary<string, byte[]> Images { get; }

    /// <summary>The theme's id: the name of its file without ".zip", or the name of its folder.</summary>
    public static string NameOf(string path) =>
        Directory.Exists(path) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : Path.GetFileNameWithoutExtension(path);

    /// <summary>Reads only theme.json, which is all that is needed to list a theme.</summary>
    public static ThemeManifest ReadManifest(string path) => Read(path, manifestOnly: true).Manifest;

    /// <summary>Reads the whole theme. Throws ThemeFormatException, with the reason, when it cannot be used.</summary>
    public static ThemePackage Open(string path) => Read(path, manifestOnly: false);

    private static ThemePackage Read(string path, bool manifestOnly)
    {
        try
        {
            return Directory.Exists(path) ? ReadFolder(path, manifestOnly) : ReadZip(path, manifestOnly);
        }
        catch (InvalidDataException)
        {
            throw new ThemeFormatException("This is not a zip file Clearspace can read (it may be damaged or password-protected).");
        }
        catch (IOException error)
        {
            throw new ThemeFormatException("The theme could not be read: " + error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new ThemeFormatException("The theme could not be read: " + error.Message);
        }
        catch (NotSupportedException error)   // a kind of zip compression .NET cannot open
        {
            throw new ThemeFormatException("The theme could not be read: " + error.Message);
        }
    }

    // ---------------------------------------------------------------- a .zip

    private static ThemePackage ReadZip(string path, bool manifestOnly)
    {
        // Shared for reading and writing: the file may still be on its way into the folder.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > MostEntries)
            throw new ThemeFormatException("This zip holds far more files than a theme has.");

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (name.Length > 0 && !name.EndsWith('/'))
                entries.TryAdd(name, entry);
        }

        // theme.json sits at the top, or one folder down when the theme's folder itself was zipped.
        var root = string.Empty;
        if (!entries.ContainsKey(ManifestName))
        {
            var below = entries.Keys
                .Where(name => name.EndsWith("/" + ManifestName, StringComparison.OrdinalIgnoreCase) && name.Count(letter => letter == '/') == 1)
                .ToArray();
            if (below.Length != 1)
                throw new ThemeFormatException("There is no theme.json in it, so it is not a Clearspace theme.");
            root = below[0][..^ManifestName.Length];
        }

        var manifest = ThemeManifest.Parse(Text(Bytes(entries[root + ManifestName], MostManifestBytes, ManifestName)), NameOf(path));
        if (manifestOnly)
            return new ThemePackage(path, manifest, string.Empty, [], new Dictionary<string, byte[]>());

        if (!entries.TryGetValue(root + MarkupName, out var markupEntry))
            throw new ThemeFormatException("There is no theme.xaml in it. That file holds the theme's colours.");
        var markup = Text(Bytes(markupEntry, MostMarkupBytes, MarkupName));

        var fonts = new List<ThemeAsset>();
        var images = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var (name, entry) in entries.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!name.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            if (AssetKind(name[root.Length..]) is not (var isFont, var fileName))
                continue;

            var data = Bytes(entry, MostAssetBytes, fileName);
            Keep(isFont, fileName, data, fonts, images, ref total);
        }

        return new ThemePackage(path, manifest, markup, fonts, images);
    }

    private static byte[] Bytes(ZipArchiveEntry entry, int most, string what)
    {
        if (entry.Length > most)
            throw new ThemeFormatException($"{what} is too big ({entry.Length / 1024:N0} KB; the most is {most / 1024:N0} KB).");

        // The size written in the zip is only a claim, so the limit is checked again while unpacking.
        using var packed = entry.Open();
        using var unpacked = new MemoryStream((int)Math.Min(entry.Length, most));
        var buffer = new byte[81920];
        int read;
        while ((read = packed.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (unpacked.Length + read > most)
                throw new ThemeFormatException($"{what} is too big (the most is {most / 1024:N0} KB).");
            unpacked.Write(buffer, 0, read);
        }
        return unpacked.ToArray();
    }

    // ---------------------------------------------------------------- a folder

    private static ThemePackage ReadFolder(string path, bool manifestOnly)
    {
        var manifestPath = Path.Combine(path, ManifestName);
        if (!File.Exists(manifestPath))
            throw new ThemeFormatException("There is no theme.json in it, so it is not a Clearspace theme.");

        var manifest = ThemeManifest.Parse(Text(Bytes(manifestPath, MostManifestBytes, ManifestName)), NameOf(path));
        if (manifestOnly)
            return new ThemePackage(path, manifest, string.Empty, [], new Dictionary<string, byte[]>());

        var markupPath = Path.Combine(path, MarkupName);
        if (!File.Exists(markupPath))
            throw new ThemeFormatException("There is no theme.xaml in it. That file holds the theme's colours.");
        var markup = Text(Bytes(markupPath, MostMarkupBytes, MarkupName));

        var fonts = new List<ThemeAsset>();
        var images = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var folder in new[] { FontsFolder, ImagesFolder })
        {
            var inside = Path.Combine(path, folder);
            if (!Directory.Exists(inside))
                continue;

            foreach (var file in Directory.EnumerateFiles(inside).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                if (AssetKind(folder + "/" + Path.GetFileName(file)) is not (var isFont, var fileName))
                    continue;
                Keep(isFont, fileName, Bytes(file, MostAssetBytes, fileName), fonts, images, ref total);
            }
        }

        return new ThemePackage(path, manifest, markup, fonts, images);
    }

    private static byte[] Bytes(string file, int most, string what)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > most)
            throw new ThemeFormatException($"{what} is too big ({stream.Length / 1024:N0} KB; the most is {most / 1024:N0} KB).");

        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        return data;
    }

    // ---------------------------------------------------------------- shared

    // "fonts/Chicago.ttf" or "images/paper.png": which of the two, and the file's own name. Null for
    // everything else in a theme (licence texts, notes, files in deeper folders), which is simply not used.
    private static (bool IsFont, string FileName)? AssetKind(string inside)
    {
        var slash = inside.IndexOf('/');
        if (slash <= 0 || inside.IndexOf('/', slash + 1) >= 0)
            return null;

        var folder = inside[..slash];
        var fileName = inside[(slash + 1)..];
        var ending = Path.GetExtension(fileName);
        if (folder.Equals(FontsFolder, StringComparison.OrdinalIgnoreCase) && FontEndings.Contains(ending, StringComparer.OrdinalIgnoreCase))
            return (true, fileName);
        if (folder.Equals(ImagesFolder, StringComparison.OrdinalIgnoreCase) && ImageEndings.Contains(ending, StringComparer.OrdinalIgnoreCase)
            && ThemeMarkup.IsImageName(fileName))
            return (false, fileName);
        return null;
    }

    private static void Keep(bool isFont, string fileName, byte[] data, List<ThemeAsset> fonts, Dictionary<string, byte[]> images, ref long total)
    {
        total += data.Length;
        if (fonts.Count + images.Count >= MostAssets || total > MostAssetBytesInAll)
            throw new ThemeFormatException("The theme's fonts and pictures are more than Clearspace will load (64 files, 64 MB).");

        if (isFont) fonts.Add(new ThemeAsset(fileName, data));
        else images.TryAdd(fileName, data);
    }

    // UTF-8, with or without a byte order mark (UTF-16 with a mark is read too).
    private static string Text(byte[] data)
    {
        using var reader = new StreamReader(new MemoryStream(data), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
