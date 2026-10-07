// Clearspace | NEW (zip folders): the address of a place inside a ZIP file.
// A ZIP file opens like a folder (double-click it, or press Enter). The place you are then in is not a
// folder on a disk, so it gets an address of Clearspace's own, like This PC and the other pages:
//
//     clearspace://zip/C:\Users\you\Downloads\Photos.zip            the top of the ZIP file
//     clearspace://zip/C:\Users\you\Downloads\Photos.zip/Trip/2024  a folder inside it
//
// The ZIP file's own path is written as Windows writes it (backslashes); what follows the first "/" is the
// path inside the archive, with "/" between the parts as ZIP files themselves use. So the first "/" after
// the prefix is always where the archive ends, without asking the disk.
// Everything that already treats "clearspace://" addresses as "not a folder on a disk" (the folder watcher,
// tags, locks, pinning, backups, the delete and rename code) therefore leaves the inside of a ZIP alone
// without being told to. People never see this form: the address bar, the tab tooltip and Copy path show
// C:\Users\you\Downloads\Photos.zip\Trip\2024 (Display), and typing that takes you there (FromTyped).
// Nothing here reads a ZIP file; Services/ArchiveBrowser.cs does that.
using System.IO;

namespace Clearspace.Services;

internal static class ArchiveLocation
{
    public const string Prefix = "clearspace://zip/";

    /// <summary>True for the top of a ZIP file, a folder inside one, or a file inside one.</summary>
    public static bool Is(string? path) => path is not null && path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The address of the top of a ZIP file, or of <paramref name="inside"/> it ("Trip/2024").</summary>
    public static string For(string archiveFile, string inside = "")
    {
        var archive = archiveFile.Replace('/', '\\');
        inside = inside.Replace('\\', '/').Trim('/');
        return inside.Length == 0 ? Prefix + archive : Prefix + archive + "/" + inside;
    }

    /// <summary>Splits an address into the ZIP file and the path inside it ("" for the top). False when
    /// <paramref name="path"/> is not inside a ZIP file.</summary>
    public static bool TrySplit(string? path, out string archiveFile, out string inside)
    {
        archiveFile = inside = string.Empty;
        if (!Is(path))
            return false;

        var rest = path![Prefix.Length..];
        var end = rest.IndexOf('/');
        archiveFile = end < 0 ? rest : rest[..end];
        inside = end < 0 ? string.Empty : rest[(end + 1)..].Trim('/');
        return archiveFile.Length > 0;
    }

    /// <summary>What an address is on the disk: the ZIP file, for a place inside one; anything else comes
    /// back unchanged. For questions that are about the disk ("is this in a locked folder?").</summary>
    public static string OnDisk(string path) => TrySplit(path, out var archive, out _) ? archive : path;

    /// <summary>The address of something directly inside a place in a ZIP file.</summary>
    public static string Child(string location, string name) => location.TrimEnd('/') + "/" + name;

    /// <summary>The place above: the folder above inside the ZIP, or, from its top, the real folder the ZIP
    /// file is in. Null when there is nothing above (or the address is not inside a ZIP).</summary>
    public static string? Parent(string location)
    {
        if (!TrySplit(location, out var archive, out var inside))
            return null;

        if (inside.Length == 0)
            return Path.GetDirectoryName(archive) is { Length: > 0 } folder ? folder : null;

        var cut = inside.LastIndexOf('/');
        return For(archive, cut < 0 ? string.Empty : inside[..cut]);
    }

    /// <summary>The last part of the address: a folder's or file's name, or the ZIP file's name at its top.</summary>
    public static string NameOf(string location)
    {
        if (!TrySplit(location, out var archive, out var inside))
            return location;

        return inside.Length == 0 ? Path.GetFileName(archive) : inside[(inside.LastIndexOf('/') + 1)..];
    }

    /// <summary>The address the way it is shown and typed: C:\Downloads\Photos.zip\Trip\2024. Anything that
    /// is not inside a ZIP file comes back unchanged.</summary>
    public static string Display(string path)
        => TrySplit(path, out var archive, out var inside)
            ? inside.Length == 0 ? archive : archive + "\\" + inside.Replace('/', '\\')
            : path;

    /// <summary>The address for a path somebody typed or pasted (C:\Downloads\Photos.zip\Trip), or null
    /// when no part of it is a ZIP file. This one asks the disk: it looks for the part that is a .zip file.</summary>
    public static string? FromTyped(string typed)
    {
        var path = typed.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
        var from = 0;
        while (from < path.Length)
        {
            var dot = path.IndexOf(".zip", from, StringComparison.OrdinalIgnoreCase);
            if (dot < 0)
                return null;

            var end = dot + 4;
            from = end;
            if (end != path.Length && path[end] != '\\')
                continue;   // "x.zipped", "x.zip.bak"

            var archive = path[..end];
            try
            {
                if (File.Exists(archive))
                    return For(archive, path[end..]);
            }
            catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        return null;
    }
}
