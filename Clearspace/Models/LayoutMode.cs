// Clearspace | Folder layout and view profiles.

using System.IO;

namespace Clearspace.Models;

public enum LayoutMode
{
    Details,

    Grid
}

public enum DirectoryViewProfile
{
    Automatic,
    General,
    Desktop,
    Documents,
    Downloads,
    Photos,
    Music,
    Videos,
    // NEW (folder types, step 2): families with their own behavior. Names are saved IDs; keep them stable.
    Screenshots,
    Code,
    Projects,
    Research,
    Design,
    Archives
}

public static class MediaTypes
{
    private static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff",
        ".heic", ".heif", ".avif", ".jxl", ".ico", ".svg",
        ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2"
    };

    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".flv"
    };

    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".flac", ".wav", ".wma", ".ogg", ".opus",
        ".aiff", ".aif", ".alac", ".ape", ".mid", ".midi"
    };

    private static readonly HashSet<string> PreviewDocument = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdn", ".psd", ".psb", ".xcf", ".kra", ".ora", ".clip",
        ".pdf", ".ai", ".eps", ".blend", ".3mf",
        // NEW (folder types, step 3): 3D models (Design & 3D). Windows draws these when 3D Viewer or a
        // slicer is installed; otherwise the tile keeps the file's icon.
        ".stl", ".obj", ".fbx", ".glb", ".gltf", ".ply", ".3ds", ".skp", ".afdesign", ".afphoto"
    };

    // NEW (search relevance): the search index tests extensions straight from name spans, so it needs
    // the sets themselves (span lookups) rather than the string-only helpers below. Read-only by convention.
    internal static HashSet<string> ImageExtensions => Image;
    internal static HashSet<string> VideoExtensions => Video;
    internal static HashSet<string> AudioExtensions => Audio;

    public static bool IsImage(string extension) => Image.Contains(extension);

    public static bool IsVideo(string extension) => Video.Contains(extension);

    public static bool IsAudio(string extension) => Audio.Contains(extension);

    public static bool IsThumbnailCandidate(string extension)
        => Image.Contains(extension) || Video.Contains(extension) || PreviewDocument.Contains(extension);

    public static bool IsVisual(string extension) => Image.Contains(extension) || Video.Contains(extension);

    public static bool LooksVisual(IReadOnlyList<FileSystemItem> items)
    {
        var files = 0;
        var visual = 0;

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].IsFolder)
                continue;

            files++;

            if (IsVisual(Path.GetExtension(items[i].Name)))
                visual++;
        }

        return files >= 4 && visual * 2 > files;
    }
}
