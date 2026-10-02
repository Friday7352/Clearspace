// Clearspace | Automatic folder-type detection.
//
// CHANGED (folder types, step 2): screenshot folders are recognized separately from photos, and code
// repositories are recognized by their contents (a .git folder, a solution or package manifest) in the
// folder or any folder above it, so a whole repository is shown as Code.
// DetectFromName never touches the disk (it runs for every folder shown, for icons). DetectFromContents
// does, and is used only for the folder being opened; its answers are cached briefly.
//
// CHANGED (folder types, step 3): Automatic now really picks a type, in this order:
//   1. Windows' own folders (Desktop, Documents, Downloads, Pictures, Music, Videos, Pictures\Screenshots)
//   2. the folder's name (more types recognized: Videos, Downloads, Archives, Research, Design)
//   3. a code repository at or above the folder
//   4. what the folder contained when it was last opened (mostly pictures -> Photos, mostly files named
//      "Screenshot..." -> Screenshots, mostly songs -> Music, and so on). MainViewModel reports each
//      folder's items through LearnContents after it loads.

using System.Collections.Concurrent;
using System.IO;
using Clearspace.Models;

namespace Clearspace.Services;

internal static class AutomaticFolderTypeDetector
{
    private static readonly string[] ScreenshotKeywords =
    [
        "screenshot", "screenshots", "screen shot", "screen capture", "screen captures", "screencaps", "snips", "snipping"
    ];

    private static readonly string[] PhotoKeywords =
    [
        "photo", "photos", "picture", "pictures", "pics", "image", "images",
        "camera roll", "camera", "wallpaper", "wallpapers", "gallery", "snapshots"
    ];

    private static readonly string[] MusicKeywords =
    [
        "music", "musik", "song", "songs", "audio", "album", "albums",
        "soundtrack", "soundtracks", "mp3", "mp3s", "playlist", "playlists",
        "tunes"
    ];

    // NEW (step 3)
    private static readonly string[] VideoKeywords = ["video", "videos", "movies", "clips", "recordings", "footage"];
    private static readonly string[] DownloadKeywords = ["downloads"];
    private static readonly string[] ArchiveKeywords = ["backup", "archive"];
    private static readonly string[] ResearchKeywords = ["research", "papers", "literature", "readings", "thesis"];
    private static readonly string[] DesignKeywords = ["design", "renders", "artwork", "3d print", "3d models", "blender"];

    // A file named like one of these is a screenshot (Windows, macOS, Snipping Tool, Steam, ShareX...).
    private static readonly string[] ScreenshotFilePrefixes = ["screenshot", "screen shot", "screen recording", "capture", "snip", "scr_", "sharex"];

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".bak", ".iso", ".img", ".vhd", ".vhdx", ".wim", ".cab"
    };

    private static readonly HashSet<string> DesignExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".psd", ".psb", ".ai", ".eps", ".svg", ".blend", ".stl", ".obj", ".fbx", ".3mf", ".glb", ".gltf", ".3ds", ".max",
        ".skp", ".dwg", ".dxf", ".step", ".stp", ".f3d", ".kra", ".xcf", ".afdesign", ".afphoto", ".fig", ".sketch", ".xd", ".c4d", ".ma", ".mb"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".odt", ".rtf", ".txt", ".md", ".xls", ".xlsx", ".ods", ".csv", ".ppt", ".pptx", ".odp", ".epub", ".pages", ".numbers", ".key"
    };

    // A folder containing one of these is the root of a code project.
    private static readonly string[] RepositoryFolders = [".git"];
    private static readonly string[] RepositoryFiles =
    [
        ".git", "package.json", "Cargo.toml", "go.mod", "pyproject.toml", "pom.xml", "build.gradle", "build.gradle.kts",
        "CMakeLists.txt", "Makefile", "composer.json", "Gemfile", "mix.exs", "pubspec.yaml"
    ];
    private static readonly string[] RepositoryPatterns = ["*.sln", "*.slnx", "*.csproj", "*.xcodeproj"];

    private const int MaxLevelsUp = 12;
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, (string? Root, DateTime Utc)> RepositoryCache = new(StringComparer.OrdinalIgnoreCase);

    // NEW (step 3): what each opened folder's contents looked like (null: nothing in particular).
    private static readonly ConcurrentDictionary<string, DirectoryViewProfile?> ContentCache = new(StringComparer.OrdinalIgnoreCase);

    public static DirectoryViewProfile? DetectFromName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (ContainsKeyword(name, ScreenshotKeywords)) return DirectoryViewProfile.Screenshots;
        if (ContainsKeyword(name, PhotoKeywords)) return DirectoryViewProfile.Photos;
        if (ContainsKeyword(name, MusicKeywords)) return DirectoryViewProfile.Music;
        // NEW (step 3)
        if (ContainsKeyword(name, VideoKeywords)) return DirectoryViewProfile.Videos;
        if (ContainsKeyword(name, DownloadKeywords)) return DirectoryViewProfile.Downloads;
        if (ContainsKeyword(name, ArchiveKeywords)) return DirectoryViewProfile.Archives;
        if (ContainsKeyword(name, ResearchKeywords)) return DirectoryViewProfile.Research;
        if (ContainsKeyword(name, DesignKeywords)) return DirectoryViewProfile.Design;

        return null;
    }

    // CHANGED (step 3): Windows' folders, then the name, then a repository, then the last-seen contents.
    public static DirectoryViewProfile? DetectFromContents(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase))
            return null;

        if (FromKnownFolder(path) is { } known)
            return known;

        if (DetectFromName(path) is { } byName)
            return byName;

        if (RepositoryRoot(path) is not null)
            return DirectoryViewProfile.Code;

        return ContentCache.TryGetValue(Normalize(path), out var seen) ? seen : null;
    }

    // NEW (step 3): called with a folder's items after it loads. True when the answer changed, so the
    // caller can re-apply the folder's view.
    public static bool LearnContents(string path, IReadOnlyList<FileSystemItem> items)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase))
            return false;

        var key = Normalize(path);
        var profile = FromContents(items);
        var had = ContentCache.TryGetValue(key, out var previous);

        if (ContentCache.Count > 5_000) ContentCache.Clear();
        ContentCache[key] = profile;
        return !had ? profile is not null : previous != profile;
    }

    // NEW (step 3): the type a set of items looks like, or null. At least three files, and most of them
    // (60 %) of one kind.
    internal static DirectoryViewProfile? FromContents(IReadOnlyList<FileSystemItem> items)
    {
        int files = 0, images = 0, screenshots = 0, videos = 0, audio = 0, archives = 0, design = 0, documents = 0;

        foreach (var item in items)
        {
            if (item.IsFolder)
                continue;

            files++;
            var extension = Path.GetExtension(item.Name);

            if (MediaTypes.IsImage(extension))
            {
                images++;
                if (StartsWithAny(item.Name, ScreenshotFilePrefixes)) screenshots++;
            }
            else if (MediaTypes.IsVideo(extension)) videos++;
            else if (MediaTypes.IsAudio(extension)) audio++;
            else if (ArchiveExtensions.Contains(extension)) archives++;
            else if (DesignExtensions.Contains(extension)) design++;
            else if (DocumentExtensions.Contains(extension)) documents++;
        }

        if (files < 3)
            return null;

        bool Most(int count) => count * 10 >= files * 6;

        // Design files are usually kept with their renders and exports, so pictures count towards Design
        // once a quarter of the files are design files.
        if (design * 4 >= files && Most(design + images)) return DirectoryViewProfile.Design;
        if (Most(images)) return screenshots * 10 >= images * 6 ? DirectoryViewProfile.Screenshots : DirectoryViewProfile.Photos;
        if (Most(videos)) return DirectoryViewProfile.Videos;
        if (Most(audio)) return DirectoryViewProfile.Music;
        if (Most(archives)) return DirectoryViewProfile.Archives;
        if (Most(documents)) return DirectoryViewProfile.Documents;
        return null;
    }

    // NEW (step 3): Windows' own folders keep their meaning wherever they have been moved to.
    private static DirectoryViewProfile? FromKnownFolder(string path)
    {
        try
        {
            var folder = Normalize(path);

            if (Same(folder, KnownFolders.Desktop)) return DirectoryViewProfile.Desktop;
            if (Same(folder, KnownFolders.Downloads)) return DirectoryViewProfile.Downloads;
            if (Same(folder, KnownFolders.Documents)) return DirectoryViewProfile.Documents;
            if (Same(folder, KnownFolders.Music)) return DirectoryViewProfile.Music;
            if (Same(folder, KnownFolders.Videos)) return DirectoryViewProfile.Videos;
            if (Same(folder, KnownFolders.Pictures)) return DirectoryViewProfile.Photos;
            if (Same(folder, Path.Combine(KnownFolders.Pictures, "Screenshots"))) return DirectoryViewProfile.Screenshots;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException)
        {
        }

        return null;

        static bool Same(string folder, string? known)
            => !string.IsNullOrEmpty(known) && folder.Equals(Normalize(known), StringComparison.OrdinalIgnoreCase);
    }

    // The nearest folder at or above `path` that looks like a code repository root, or null.
    public static string? RepositoryRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase))
            return null;

        var start = path.TrimEnd('\\', '/');
        if (RepositoryCache.TryGetValue(start, out var cached) && DateTime.UtcNow - cached.Utc < CacheFor)
            return cached.Root;

        string? found = null;
        var current = start;

        for (var level = 0; level <= MaxLevelsUp && !string.IsNullOrEmpty(current); level++)
        {
            if (IsRepositoryRoot(current))
            {
                found = current;
                break;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent.Equals(current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }

        if (RepositoryCache.Count > 2_000) RepositoryCache.Clear();
        RepositoryCache[start] = (found, DateTime.UtcNow);
        return found;
    }

    internal static bool IsRepositoryRoot(string folder)
    {
        try
        {
            // A drive root or the user profile is never "the project", even with a stray package.json.
            if (Path.GetPathRoot(folder)?.TrimEnd('\\').Equals(folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true ||
                folder.TrimEnd('\\').Equals(KnownFolders.Profile.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return false;

            foreach (var name in RepositoryFolders)
                if (Directory.Exists(Path.Combine(folder, name))) return true;

            foreach (var name in RepositoryFiles)
                if (File.Exists(Path.Combine(folder, name))) return true;

            foreach (var pattern in RepositoryPatterns)
                if (Directory.EnumerateFileSystemEntries(folder, pattern).Any()) return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return false;
    }

    private static string Normalize(string path) => path.TrimEnd('\\', '/');

    private static bool ContainsKeyword(string name, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool StartsWithAny(string name, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
