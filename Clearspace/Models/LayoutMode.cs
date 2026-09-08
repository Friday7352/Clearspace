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
    Videos
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
        ".pdf", ".ai", ".eps", ".blend", ".3mf"
    };

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
