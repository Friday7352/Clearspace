// Clearspace | Automatic folder-type detection.

using System.IO;
using Clearspace.Models;

namespace Clearspace.Services;

internal static class AutomaticFolderTypeDetector
{
    private static readonly string[] PhotoKeywords =
    [
        "photo", "photos", "picture", "pictures", "pics", "image", "images",
        "camera roll", "camera", "screenshot", "screenshots", "wallpaper",
        "wallpapers", "gallery", "snapshots"
    ];

    private static readonly string[] MusicKeywords =
    [
        "music", "musik", "song", "songs", "audio", "album", "albums",
        "soundtrack", "soundtracks", "mp3", "mp3s", "playlist", "playlists",
        "tunes"
    ];

    public static DirectoryViewProfile? DetectFromName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (ContainsKeyword(name, PhotoKeywords))
            return DirectoryViewProfile.Photos;

        if (ContainsKeyword(name, MusicKeywords))
            return DirectoryViewProfile.Music;

        return null;
    }

    private static bool ContainsKeyword(string name, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
