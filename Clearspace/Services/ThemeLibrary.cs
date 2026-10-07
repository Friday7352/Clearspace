// Clearspace | Theme files: the Themes folder.
// NEW (theme files): every theme is a file in one folder, "Themes", beside Clearspace.exe. The themes that
// come with Clearspace are there as files like any other (Dark.zip, Light.zip ...), and a theme somebody
// made is added by putting its file in the same folder, or with "Import a theme" in Settings, which checks
// the file and copies it there. This class lists the folder, imports, and notices when the folder changes.
//   - A theme's id is its file name without ".zip" (or its folder's name); that is what settings remember.
//   - A folder with the same name as a .zip takes its place: unzip a theme to change it, and the folder is
//     what Clearspace shows while you work.
//   - Listing reads only theme.json. theme.xaml is read and checked when a theme is shown or imported.
// CHANGED (themes page): Dark is part of Clearspace itself, so there is always at least one theme.
//   - Dark is in the list whatever is in the folder. While Dark.zip is there (as it is after installing)
//     it is read like any other theme. When it has been deleted, or can no longer be read, Dark is the
//     look built into the app (App.xaml), and nothing about it can be removed.
//   - Remove takes any other theme out of the folder (the caller decides how: the page moves the file to
//     the Recycle Bin). Dark is refused.
using System.IO;
using System.Text;

namespace Clearspace.Services;

internal static class ThemeLibrary
{
    public const string Extension = ".zip";

    // The themes that come with Clearspace, in the order the Themes page shows them. Anything else in the
    // folder is a theme somebody added. Their sources are the folders in Clearspace\Themes; the build zips
    // each one into the Themes folder beside Clearspace.exe (Clearspace.csproj).
    internal static readonly string[] Shipped =
    [
        "Dark", "OLED", "Light", "Blueprint", "Terminal", "Retro",
        "Pixel", "DosCommander", "Wireframe", "NeonHud", "Glass", "Sketchbook", "SketchbookDark", "TypeTiles", "Zen",
        "Mac1Bit", "Mac1BitDark", "Paper"
    ];

    // NEW (themes page): Dark when its file is not in the folder (or cannot be read): the look built into
    // the app. It has no file, so there is nothing to open, change or delete.
    internal static readonly ThemeInfo BuiltInDark = new(ThemeService.DefaultTheme, ThemeService.DefaultTheme)
    {
        Author = "Clearspace",
        Description = "The default: warm dark greys with an amber accent.",
        Shipped = true,
        BuiltIn = true,
        Stamp = "built in"
    };

    internal static bool IsDefault(string? name) =>
        string.Equals(name, ThemeService.DefaultTheme, StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, ThemeInfo> Remembered = new(StringComparer.OrdinalIgnoreCase);   // by path
    private static FileSystemWatcher? _watcher;
    private static Timer? _settle;
    private static Action? _changed;

    /// <summary>The folder theme files live in. Tests point it at a temporary folder.</summary>
    public static string Folder { get; internal set; } = Path.Combine(AppContext.BaseDirectory, "Themes");

    /// <summary>What was in the folder the last time it was read: Clearspace's own themes first, in menu
    /// order, then added themes by name. Includes themes that cannot be used (Problem says why).
    /// CHANGED (themes page): Dark is always in it, and always usable.</summary>
    public static IReadOnlyList<ThemeInfo> Themes { get; private set; } = [BuiltInDark];

    // ---------------------------------------------------------------- listing

    /// <summary>Reads the folder again. Cheap: a theme whose file has not changed is not opened again.</summary>
    public static IReadOnlyList<ThemeInfo> Refresh()
    {
        var found = new Dictionary<string, ThemeInfo>(StringComparer.OrdinalIgnoreCase);   // by theme name
        try
        {
            if (Directory.Exists(Folder))
            {
                // Folders first: one with the same name as a .zip takes its place.
                foreach (var folder in Directory.EnumerateDirectories(Folder))
                {
                    if (File.Exists(Path.Combine(folder, ThemePackage.ManifestName)))
                        found.TryAdd(ThemePackage.NameOf(folder), Describe(folder));
                }

                foreach (var file in Directory.EnumerateFiles(Folder, "*" + Extension))
                    found.TryAdd(ThemePackage.NameOf(file), Describe(file));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The folder cannot be read just now; keep what could be listed.
        }

        foreach (var path in Remembered.Keys.Where(path => !found.Values.Any(theme => SamePath(theme.Path, path))).ToArray())
            Remembered.Remove(path);

        // NEW (themes page): Dark is listed even when its file is gone, and never as a theme that cannot be used.
        if (!found.TryGetValue(ThemeService.DefaultTheme, out var dark) || dark.Problem is not null)
            found[ThemeService.DefaultTheme] = BuiltInDark;

        Themes =
        [
            .. found.Values
                .OrderBy(theme => theme.Shipped ? Array.FindIndex(Shipped, name => name.Equals(theme.Name, StringComparison.OrdinalIgnoreCase)) : int.MaxValue)
                .ThenBy(theme => theme.Label, StringComparer.CurrentCultureIgnoreCase)
        ];
        return Themes;
    }

    /// <summary>The theme with this name, or null. A name that is not in the list is looked for in the folder
    /// directly (its folder, then its .zip), without reading every other theme: at start-up only the saved
    /// theme is needed before the window opens.</summary>
    public static ThemeInfo? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // CHANGED (themes page): the built-in Dark is in the list from the start (before the folder has
        // been read), so it is passed over here; the folder is asked whether Dark's file is there.
        var listed = Themes.FirstOrDefault(theme => string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase));
        if (listed is not null && !listed.BuiltIn)
            return listed;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        ThemeInfo? found = null;
        try
        {
            var folder = Path.Combine(Folder, name);
            var file = Path.Combine(Folder, name + Extension);
            if (File.Exists(Path.Combine(folder, ThemePackage.ManifestName)))
                found = Describe(folder);
            else if (File.Exists(file))
                found = Describe(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            found = null;
        }

        // NEW (themes page): there is always a Dark. Without a file that can be read, it is the built-in one.
        if (IsDefault(name) && (found is null || found.Problem is not null))
            return BuiltInDark;
        return found;
    }

    private static ThemeInfo Describe(string path)
    {
        var stamp = Stamp(path);
        if (Remembered.TryGetValue(path, out var known) && known.Stamp == stamp)
            return known;

        var name = ThemePackage.NameOf(path);
        var shipped = Shipped.Contains(name, StringComparer.OrdinalIgnoreCase);
        ThemeInfo theme;
        try
        {
            var manifest = ThemePackage.ReadManifest(path);
            theme = new ThemeInfo(name, manifest.Name, manifest.Experimental, manifest.Icons, manifest.FadeToolbar, manifest.EInk, manifest.CrispText)
            {
                Path = path,
                Author = manifest.Author,
                Description = manifest.Description,
                Version = manifest.Version,
                Shipped = shipped,
                Stamp = stamp
            };
        }
        catch (ThemeFormatException error)
        {
            theme = new ThemeInfo(name, name) { Path = path, Shipped = shipped, Stamp = stamp, Problem = error.Message };
        }

        Remembered[path] = theme;
        return theme;
    }

    // Changes whenever the theme's files do: sizes and last-changed times, nothing read from inside them.
    private static string Stamp(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                var file = new FileInfo(path);
                return $"{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            }

            var stamp = new StringBuilder();
            void Add(string file)
            {
                var info = new FileInfo(file);
                if (info.Exists)
                    stamp.Append(info.Name).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append(';');
            }

            Add(Path.Combine(path, ThemePackage.ManifestName));
            Add(Path.Combine(path, ThemePackage.MarkupName));
            foreach (var folder in new[] { ThemePackage.FontsFolder, ThemePackage.ImagesFolder })
            {
                var inside = Path.Combine(path, folder);
                if (!Directory.Exists(inside))
                    continue;
                foreach (var file in Directory.EnumerateFiles(inside).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                    Add(file);
            }
            return stamp.ToString();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    // ---------------------------------------------------------------- importing

    /// <summary>Checks a theme file and copies it into the Themes folder.</summary>
    /// <param name="file">A theme .zip from anywhere on the PC.</param>
    /// <param name="replace">Asked when an added theme of the same name is already there; false keeps
    /// both (the new one gets a number). Clearspace's own themes are never replaced.</param>
    /// <returns>The theme as it is now listed.</returns>
    /// <exception cref="ThemeFormatException">The file is not a usable theme, or could not be copied. Nothing was added.</exception>
    public static ThemeInfo Import(string file, Func<ThemeInfo, bool>? replace = null)
    {
        if (!File.Exists(file))
            throw new ThemeFormatException("That file is no longer there.");
        if (!Extension.Equals(Path.GetExtension(file), StringComparison.OrdinalIgnoreCase))
            throw new ThemeFormatException("A theme file is a .zip file.");

        // Read all of it and build its resources once, so a theme that would not show is never added.
        ThemeLoader.Load(ThemePackage.Open(file));

        var name = ThemePackage.NameOf(file).Trim();
        if (name.Length == 0)
            throw new ThemeFormatException("The theme file needs a name.");

        try
        {
            Directory.CreateDirectory(Folder);
            var target = Path.Combine(Folder, name + Extension);
            if (SamePath(file, target))
                return Refreshed(name);   // it is in the folder already

            Refresh();   // the list may be older than the folder (a theme deleted by hand since it was read)
            var existing = Find(name);
            if (existing is not null && (existing.Shipped || Directory.Exists(existing.Path) || replace?.Invoke(existing) != true))
            {
                // Keep both: "Sunset (2).zip".
                for (var number = 2; ; number++)
                {
                    var candidate = $"{name} ({number})";
                    if (Find(candidate) is null && !File.Exists(Path.Combine(Folder, candidate + Extension)))
                    {
                        name = candidate;
                        break;
                    }
                }
                target = Path.Combine(Folder, name + Extension);
            }

            File.Copy(file, target, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ThemeFormatException("The theme could not be copied into the Themes folder: " + error.Message);
        }

        return Refreshed(name);

        static ThemeInfo Refreshed(string wanted)
        {
            Refresh();
            return Find(wanted) ?? throw new ThemeFormatException("The theme was copied, but the Themes folder could not be read.");
        }
    }

    // ---------------------------------------------------------------- removing

    /// <summary>NEW (themes page): takes a theme out of the Themes folder.</summary>
    /// <param name="delete">Removes the theme's file (or folder) and says whether it is gone. The Themes
    /// page moves it to the Recycle Bin, so a theme removed by mistake can be put back.</param>
    /// <returns>False when nothing was removed (the question Windows asks was answered No).</returns>
    /// <exception cref="ThemeFormatException">The theme is Dark, which is part of Clearspace.</exception>
    public static bool Remove(ThemeInfo theme, Func<string, bool> delete)
    {
        if (theme.BuiltIn || IsDefault(theme.Name))
            throw new ThemeFormatException("Dark is part of Clearspace and can't be removed.");

        var there = File.Exists(theme.Path) || Directory.Exists(theme.Path);
        if (there && !delete(theme.Path))
            return false;

        Refresh();
        return true;
    }

    private static bool SamePath(string first, string second)
    {
        try
        {
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- noticing changes

    /// <summary>Calls <paramref name="changed"/> a moment after files in the Themes folder change (a theme
    /// added, removed, or saved again). It is called on a background thread. Makes the folder when it is
    /// missing, so "Open themes folder" always has somewhere to go.</summary>
    public static void Watch(Action changed)
    {
        StopWatching();
        _changed = changed;
        try
        {
            Directory.CreateDirectory(Folder);
            _settle = new Timer(_ => _changed?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
            var watcher = new FileSystemWatcher(Folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Created += (_, _) => Settle();
            watcher.Changed += (_, _) => Settle();
            watcher.Deleted += (_, _) => Settle();
            watcher.Renamed += (_, _) => Settle();
            watcher.Error += (_, _) => Settle();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception)
        {
            // No live updates (the folder cannot be made or watched). The Themes page still reads the
            // folder each time it opens.
        }
    }

    public static void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        _settle?.Dispose();
        _settle = null;
        _changed = null;
    }

    // Saving or copying a file raises several events; wait until they stop.
    private static void Settle()
    {
        try { _settle?.Change(400, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }
}
