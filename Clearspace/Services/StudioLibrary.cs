using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace Clearspace.Services;

// Personal studio assets live outside individual themes; selected fonts are bundled on save.
internal static class StudioLibrary
{
    internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clearspace", "ThemeStudio");
    internal static string FontsFolder => Path.Combine(Folder, "Fonts");

    internal static string ImportFont(byte[] bytes, string extension, string? folder = null)
    {
        // Validate using the same font loader that will render the saved theme.
        var checkedFont = ThemeStudioDocument.FromDesign(ThemeDesign.Presets[0].Design).ImportFont(bytes, extension, "UIFont");
        var asset = checkedFont.Package.Fonts.Single();
        folder ??= FontsFolder;
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, asset.FileName), asset.Data);
        return ((FontFamily)checkedFont.Resources()["UIFont"]).Source.Split('#').Last();
    }

    internal static IEnumerable<(string Name, string Path, FontFamily Family)> Fonts(string? folder = null)
    {
        folder ??= FontsFolder;
        if (!Directory.Exists(folder)) yield break;
        foreach (var path in Directory.EnumerateFiles(folder).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".ttf" or ".otf"))
        {
            FontFamily[] families;
            try { families = System.Windows.Media.Fonts.GetFontFamilies(new Uri(path)).ToArray(); }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException) { continue; }
            foreach (var family in families)
                if (family.FamilyNames.Values.FirstOrDefault() is { } name) yield return (name, path, family);
        }
    }

    internal static List<string> Colors(string? folder = null)
    {
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(folder ?? Folder, "colors.json"))) ?? [];
            return values.Where(v => v is not null && v.Length is 7 or 9 && v[0] == '#' && uint.TryParse(v.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _)).Distinct().Take(64).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    internal static void SaveColors(IEnumerable<string> colors, string? folder = null)
    {
        folder ??= Folder; Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "colors.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(colors.Distinct().Take(64)));
        File.Move(temp, path, true);
    }

    // NEW (pattern maker): patterns drawn in the studio, kept for every theme in patterns.json beside colors.json.
    // A pattern used in a theme is written into that theme itself, so a shared theme never needs this file.
    internal static List<StudioCustomPattern> Patterns(string? folder = null)
    {
        try
        {
            var values = JsonSerializer.Deserialize<StudioCustomPattern[]>(File.ReadAllText(Path.Combine(folder ?? Folder, "patterns.json"))) ?? [];
            return values.Where(p => p is not null && p.IsValid).DistinctBy(p => p.Name).Take(100).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return []; }
    }

    internal static void SavePatterns(IEnumerable<StudioCustomPattern> patterns, string? folder = null)
    {
        folder ??= Folder; Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "patterns.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(patterns.Where(p => p.IsValid).DistinctBy(p => p.Name).Take(100).ToArray()));
        File.Move(temp, path, true);
    }
}
