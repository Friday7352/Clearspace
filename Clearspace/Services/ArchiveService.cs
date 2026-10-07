// Clearspace | NEW (archives): unpacking and making ZIP files.
//  - Extract always unpacks into a new folder named after the archive ("Photos.zip" -> "Photos", or
//    "Photos (2)" when that is taken), so nothing that is already on the disk is ever overwritten.
//  - A name inside an archive is only a piece of text, and a hostile archive can use one like
//    "..\..\Windows\x" to write outside the folder it is unpacked into. DestinationFor refuses any name
//    that does not land inside that folder.
//  - Compress writes the chosen files and folders (everything inside them) into one new .zip. Links and
//    junctions are not followed, so a folder that points back at itself cannot go round for ever.
//  - Stopping, or a failure, removes what was half made: the new folder, or the unfinished .zip.
// Only .zip (the format built into .NET). The windows that use this are in MainWindow.Archives.cs.
// NEW (zip folders): looking inside a ZIP file without unpacking it is Services/ArchiveBrowser.cs.
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace Clearspace.Services;

internal sealed record ArchiveProgress(long BytesDone, long BytesTotal, string CurrentName);

internal sealed record ArchiveEntryInfo(string Name, long Size, bool IsFolder);

// The first entries of an archive, for the preview pane. TotalSize is what the files take up unpacked.
internal sealed record ArchiveListing(IReadOnlyList<ArchiveEntryInfo> Entries, int TotalEntries, long TotalSize);

internal static class ArchiveService
{
    private const int BufferSize = 1 << 16;

    // NEW (pre-release review): kinds of file that are compressed already. Squeezing them again makes the
    // .zip no smaller (a photo or a video shrinks by a fraction of a percent) and is by far the slowest part
    // of making one, so Compress stores these as they are and spends the effort on everything else.
    private static readonly HashSet<string> PackedAlready = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".avif", ".jxl",
        ".mp4", ".m4v", ".mkv", ".mov", ".webm", ".wmv", ".mpg", ".mpeg",
        ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".wma",
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".epub", ".jar", ".apk", ".msix", ".appx", ".cslock"
    };

    internal static CompressionLevel LevelFor(string path)
        => PackedAlready.Contains(Path.GetExtension(path)) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;

    internal static bool IsArchive(string path) => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    // "Photos" -> "Photos (2)" -> "Photos (3)" ... until the name is free. A file keeps its ending.
    internal static string UniquePath(string desired)
    {
        if (!File.Exists(desired) && !Directory.Exists(desired))
            return desired;

        var folder = Path.GetDirectoryName(desired) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(desired);
        var extension = Path.GetExtension(desired);

        for (var number = 2; ; number++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({number}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    // The folder an archive is unpacked into, inside `parent`.
    internal static string FolderFor(string archivePath, string parent)
    {
        var name = Path.GetFileNameWithoutExtension(archivePath).TrimEnd(' ', '.');
        if (name.Length == 0)
            name = "Archive";

        // A folder has no ending to keep: "v1.2" must become "v1.2 (2)", not "v1 (2).2".
        var desired = Path.Combine(parent, name);
        if (!File.Exists(desired) && !Directory.Exists(desired))
            return desired;

        for (var number = 2; ; number++)
        {
            var candidate = Path.Combine(parent, $"{name} ({number})");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    // The .zip a selection is packed into, inside `folder`: one item gives its own name, several the
    // name of the folder they are in.
    internal static string ZipFor(IReadOnlyList<string> paths, string folder)
    {
        var name = paths.Count == 1
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0]))
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));

        if (paths.Count == 1 && File.Exists(paths[0]))
            name = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrWhiteSpace(name))
            name = "Archive";

        return UniquePath(Path.Combine(folder, name + ".zip"));
    }

    // Where an entry goes inside `root`. Throws InvalidDataException for a name that would leave it.
    internal static string DestinationFor(string root, string entryName)
    {
        var rootFull = Path.GetFullPath(root);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
            rootFull += Path.DirectorySeparatorChar;

        var name = entryName.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (name.Length == 0 || Path.IsPathRooted(name))
            throw new InvalidDataException($"The archive contains a name that can't be used here: {entryName}");

        var full = Path.GetFullPath(Path.Combine(rootFull, name));
        // "." and "sub\.." come out as the folder itself, without the separator at the end; that is still inside.
        var probe = full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
        if (!probe.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The archive tries to put a file outside the folder it is unpacked into: {entryName}");

        return full;
    }

    private static bool IsFolderEntry(ZipArchiveEntry entry)
        => entry.Name.Length == 0 || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    internal static ArchiveListing List(string archivePath, int limit = 300)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = new List<ArchiveEntryInfo>(Math.Min(limit, archive.Entries.Count));
        long size = 0;

        foreach (var entry in archive.Entries)
        {
            var isFolder = IsFolderEntry(entry);
            if (!isFolder)
                size += entry.Length;
            if (entries.Count < limit)
                entries.Add(new ArchiveEntryInfo(entry.FullName, isFolder ? 0 : entry.Length, isFolder));
        }

        return new ArchiveListing(entries, archive.Entries.Count, size);
    }

    // ---- Extract

    // Returns the folder that was made.
    internal static Task<string> ExtractAsync(string archivePath, string parentFolder,
        IProgress<ArchiveProgress>? progress, CancellationToken token)
        => Task.Run(() => Extract(archivePath, parentFolder, progress, token), token);

    internal static string Extract(string archivePath, string parentFolder,
        IProgress<ArchiveProgress>? progress, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (IsFolderEntry(entry))
                continue;
            if (entry.IsEncrypted)
                throw new InvalidDataException("This archive is protected with a password. Clearspace can't open those yet.");
            total += entry.Length;
        }

        EnsureRoom(parentFolder, total);

        var target = FolderFor(archivePath, parentFolder);
        Directory.CreateDirectory(target);
        try
        {
            var buffer = new byte[BufferSize];
            var clock = Stopwatch.StartNew();
            long done = 0;

            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                var destination = DestinationFor(target, entry.FullName);

                if (IsFolderEntry(entry))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using (var input = entry.Open())
                using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
                {
                    long written = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        written += read;
                        // An archive can claim a small size and unpack to a huge one; stop at what it claimed.
                        if (written > entry.Length)
                            throw new InvalidDataException($"{entry.FullName} is larger than the archive says it is.");

                        output.Write(buffer, 0, read);
                        done += read;
                        if (clock.ElapsedMilliseconds >= 100)
                        {
                            progress?.Report(new ArchiveProgress(done, total, entry.FullName));
                            clock.Restart();
                        }
                    }
                }

                try { File.SetLastWriteTime(destination, entry.LastWriteTime.LocalDateTime); }
                catch (Exception error) when (error is IOException or ArgumentOutOfRangeException or UnauthorizedAccessException)
                {
                    // the file is unpacked; its date is not worth failing for
                }
            }

            progress?.Report(new ArchiveProgress(total, total, string.Empty));
            return target;
        }
        catch
        {
            TryDeleteFolder(target);
            throw;
        }
    }

    private static void EnsureRoom(string folder, long bytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root))
                return;

            if (new DriveInfo(root).AvailableFreeSpace < bytes)
                throw new IOException($"There isn't enough free space on {root} to unpack this archive.");
        }
        catch (ArgumentException)
        {
            // a network path: Windows gives no drive to ask, and writing fails by itself if it fills up
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---- Compress

    // Returns the .zip that was made.
    internal static Task<string> CompressAsync(IReadOnlyList<string> paths, string zipPath,
        IProgress<ArchiveProgress>? progress, CancellationToken token)
        => Task.Run(() => Compress(paths, zipPath, progress, token), token);

    internal static string Compress(IReadOnlyList<string> paths, string zipPath,
        IProgress<ArchiveProgress>? progress, CancellationToken token)
    {
        var files = new List<(string Path, string EntryName)>();
        var emptyFolders = new List<string>();
        var topNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            var item = Path.TrimEndingDirectorySeparator(path);
            if (string.Equals(item, zipPath, StringComparison.OrdinalIgnoreCase))
                continue;

            // Two chosen items with the same name (from different folders) get "name (2)" inside the archive.
            var name = Path.GetFileName(item);
            if (name.Length == 0)
                continue;
            for (var number = 2; !topNames.Add(name); number++)
                name = $"{Path.GetFileNameWithoutExtension(item)} ({number}){Path.GetExtension(item)}";

            if (Directory.Exists(item))
                Walk(item, name);
            else if (File.Exists(item))
                AddFile(item, name);
        }

        void AddFile(string path, string entryName)
        {
            files.Add((path, entryName));
            total += new FileInfo(path).Length;
        }

        void Walk(string folder, string entryName)
        {
            token.ThrowIfCancellationRequested();
            var any = false;

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                any = true;
                if (!string.Equals(file, zipPath, StringComparison.OrdinalIgnoreCase))
                    AddFile(file, entryName + "/" + Path.GetFileName(file));
            }

            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                any = true;
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    continue; // a link or junction: not followed
                Walk(child, entryName + "/" + Path.GetFileName(child));
            }

            if (!any)
                emptyFolders.Add(entryName + "/");
        }

        // CreateNew: a .zip that is already there is never replaced (and so never deleted below either).
        var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
        try
        {
            using var closing = stream;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

            foreach (var folder in emptyFolders)
                archive.CreateEntry(folder);

            var buffer = new byte[BufferSize];
            var clock = Stopwatch.StartNew();
            long done = 0;

            foreach (var (path, entryName) in files)
            {
                token.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(entryName, LevelFor(path)); // CHANGED (pre-release review): was always Optimal
                try { entry.LastWriteTime = File.GetLastWriteTime(path); }
                catch (ArgumentOutOfRangeException)
                {
                    // ZIP dates only cover 1980 to 2107; the file is added with today's date instead
                }

                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
                using var output = entry.Open();
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    done += read;
                    if (clock.ElapsedMilliseconds >= 100)
                    {
                        progress?.Report(new ArchiveProgress(done, total, entryName));
                        clock.Restart();
                    }
                }
            }

            progress?.Report(new ArchiveProgress(total, total, string.Empty));
        }
        catch
        {
            try { File.Delete(zipPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
            throw;
        }

        return zipPath;
    }
}
