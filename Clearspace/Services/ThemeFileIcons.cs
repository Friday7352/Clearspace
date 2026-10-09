using System.IO;
using System.Windows.Media;
using Clearspace.Models;

namespace Clearspace.Services;

internal static class ThemeFileIcons
{
    internal const string Prefix = "FileIcon.";
    internal static bool IsKey(string key)
    {
        if (key == Prefix + "Folder") return true;
        var start = key.StartsWith(Prefix + "Extension.", StringComparison.Ordinal) ? (Prefix + "Extension.").Length :
            key.StartsWith(Prefix + "Name.", StringComparison.Ordinal) ? (Prefix + "Name.").Length : -1;
        return start >= 0 && key.Length > start && key.Length - start <= 200 &&
            key[start..].IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }
    internal static string Key(string target, bool extension)
    {
        target = target.Trim().ToLowerInvariant();
        if (target == "folder") return Prefix + "Folder";
        if (extension) target = target.TrimStart('.');
        if (target.Length == 0 || target.Length > 200 || target.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || target.Contains('/') || target.Contains('\\'))
            throw new ArgumentException("Enter a file extension such as .pdf, or a file name such as notes.txt.");
        return Prefix + (extension ? "Extension." : "Name.") + target;
    }
    internal static ImageSource? Find(FileSystemItem item, Func<string, object?> read)
    {
        var exact = read(Prefix + "Name." + item.Name.ToLowerInvariant()) as ImageBrush;
        var general = read(item.IsFolder ? Prefix + "Folder" : Prefix + "Extension." + item.Extension.TrimStart('.').ToLowerInvariant()) as ImageBrush;
        return (exact ?? general)?.ImageSource;
    }
}
