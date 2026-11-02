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
// NEW (rar and 7z): Extract also unpacks .7z, .rar (including multi-part .part1.rar sets) and .tar files
// (plain or .tar.gz / .tar.bz2 / .tar.xz / .tar.zst), the kinds File Explorer can unpack too. Those are read
// with the SharpCompress library (ExtractOther). The same rules hold: a new folder named after the archive,
// names that would leave it refused, and what was half made removed when it stops or fails. Only ZIP files
// open like a folder in the list; the preview pane lists what is in a .7z or .rar as well.
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

    // A ZIP file: one that opens like a folder in the list (ArchiveBrowser), as well as being unpacked.
    internal static bool IsArchive(string path) => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    // NEW (rar and 7z): the other kinds Extract can unpack. Two-part endings come before the one-part
    // ones, so "Photos.tar.gz" is known as ".tar.gz" and its folder is called "Photos", not "Photos.tar".
    private static readonly string[] OtherEndings =
    [
        ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst",
        ".7z", ".rar", ".tar", ".tgz", ".tbz", ".tbz2", ".txz", ".tzst"
    ];

    private static string? OtherEnding(string path)
        => OtherEndings.FirstOrDefault(ending => path.EndsWith(ending, StringComparison.OrdinalIgnoreCase));

    // NEW (rar and 7z): anything "Extract" in the right-click menu can unpack.
    internal static bool CanExtract(string path) => IsArchive(path) || OtherEnding(path) is not null;

    // NEW (rar and 7z): the kinds whose contents the preview pane lists. A compressed .tar would have to
    // be read from start to end just to be listed, so it is not.
    internal static bool CanList(string path)
        => IsArchive(path) || OtherEnding(path) is ".7z" or ".rar";

    // NEW (rar and 7z): the name an archive's folder gets: its name without the ending ("Photos.tar.gz" -> "Photos").
    internal static string StemOf(string archivePath)
    {
        var name = Path.GetFileName(archivePath);
        var ending = OtherEnding(archivePath) ?? Path.GetExtension(name);
        return name[..^ending.Length];
    }

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
        var name = StemOf(archivePath).TrimEnd(' ', '.'); // CHANGED (rar and 7z): was GetFileNameWithoutExtension, which left "Photos.tar"
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
        if (!IsArchive(archivePath))
            return ListOther(archivePath, limit); // NEW (rar and 7z)

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
    // CHANGED (rar and 7z): a .7z, .rar or .tar goes to ExtractOther; a ZIP file to Extract, as before.
    internal static Task<string> ExtractAsync(string archivePath, string parentFolder,
        IProgress<ArchiveProgress>? progress, CancellationToken token)
        => Task.Run(() => IsArchive(archivePath)
            ? Extract(archivePath, parentFolder, progress, token)
            : ExtractOther(archivePath, parentFolder, progress, token), token);

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

    // ---- NEW (rar and 7z): the other kinds, read with SharpCompress

    private const string PasswordMessage = "This archive is protected with a password. Clearspace can't open those yet.";

    // What SharpCompress throws is turned into the same InvalidDataException a damaged ZIP file gives, with
    // words a person can act on. A missing part of a multi-part RAR set is the usual reason.
    private static InvalidDataException Unreadable(string archivePath, Exception error)
    {
        if (error is SharpCompress.Common.CryptographicException)
            return new InvalidDataException(PasswordMessage, error);

        var name = Path.GetFileName(archivePath);
        if (error is SharpCompress.Common.MultiVolumeExtractionException or SharpCompress.Common.IncompleteArchiveException)
            return new InvalidDataException($"{name} is one part of a set, and a part of that set is missing. Keep all the parts in the same folder and extract the first one.", error);

        return new InvalidDataException($"Clearspace couldn't read {name}. It may be damaged or not finished downloading. ({error.Message})", error);
    }

    // SharpCompress's own errors, and the plain ones it lets through for a file it can't make sense of.
    private static bool IsLibraryError(Exception error)
        => error is SharpCompress.Common.SharpCompressException or InvalidOperationException or NotSupportedException
            or EndOfStreamException or ArgumentException or IndexOutOfRangeException or OverflowException;

    // .7z and .rar have a table of contents to read; .tar (and a compressed one) is read from start to end.
    private static bool HasContents(string archivePath) => OtherEnding(archivePath) is ".7z" or ".rar";

    private static ArchiveListing ListOther(string archivePath, int limit)
    {
        if (!HasContents(archivePath))
            throw new NotSupportedException("Only ZIP, 7z and RAR files are listed.");

        try
        {
            // From the path, not a stream: a RAR set finds its other parts beside the first.
            using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(archivePath);
            var entries = new List<ArchiveEntryInfo>();
            long size = 0;
            var count = 0;

            foreach (var entry in archive.Entries)
            {
                count++;
                if (!entry.IsDirectory)
                    size += entry.Size;
                if (entries.Count < limit)
                    entries.Add(new ArchiveEntryInfo(entry.Key ?? string.Empty, entry.IsDirectory ? 0 : entry.Size, entry.IsDirectory));
            }

            return new ArchiveListing(entries, count, size);
        }
        catch (Exception error) when (IsLibraryError(error))
        {
            throw Unreadable(archivePath, error);
        }
    }

    internal static string ExtractOther(string archivePath, string parentFolder,
        IProgress<ArchiveProgress>? progress, CancellationToken token)
    {
        string? target = null;
        try
        {
            if (HasContents(archivePath))
            {
                using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(archivePath);

                long total = 0;
                foreach (var entry in archive.Entries)
                {
                    if (entry.IsDirectory)
                        continue;
                    if (entry.IsEncrypted)
                        throw new InvalidDataException(PasswordMessage);
                    total += entry.Size;
                }

                EnsureRoom(parentFolder, total);
                target = FolderFor(archivePath, parentFolder);
                Directory.CreateDirectory(target);
                var writer = new EntryWriter(target, total, progress, token);

                // A "solid" archive (most .7z files, some .rar) is one long stream: its files have to be read
                // in order, which ExtractAllEntries does in one pass. Others can be read file by file.
                if (archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
                {
                    using var reader = archive.ExtractAllEntries();
                    while (reader.MoveToNextEntry())
                        writer.Write(reader.Entry, reader.OpenEntryStream);
                }
                else
                {
                    foreach (var entry in archive.Entries)
                        writer.Write(entry, entry.OpenEntryStream);
                }

                writer.Done();
            }
            else
            {
                // A .tar (plain or compressed) says how much it holds only as it is read, so the progress bar
                // follows how far through the file it is, and the free-space check uses the file's own size.
                using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
                EnsureRoom(parentFolder, file.Length);
                target = FolderFor(archivePath, parentFolder);
                Directory.CreateDirectory(target);
                var writer = new EntryWriter(target, 0, progress, token) { Through = file };

                using var reader = SharpCompress.Readers.ReaderFactory.OpenReader(file, SharpCompress.Readers.ReaderOptions.ForExternalStream);
                while (reader.MoveToNextEntry())
                    writer.Write(reader.Entry, reader.OpenEntryStream);

                writer.Done();
            }

            return target;
        }
        catch (Exception error)
        {
            if (target is not null)
                TryDeleteFolder(target);

            if (error is not OperationCanceledException && IsLibraryError(error))
                throw Unreadable(archivePath, error);
            throw;
        }
    }

    // Writes the entries of a .7z, .rar or .tar into the new folder, one at a time, with the same checks
    // Extract makes for a ZIP file.
    private sealed class EntryWriter(string target, long total, IProgress<ArchiveProgress>? progress, CancellationToken token)
    {
        private readonly byte[] _buffer = new byte[BufferSize];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _done;

        // Set for a .tar: progress is how far through this file reading has got.
        internal FileStream? Through { get; init; }

        internal void Write(SharpCompress.Common.IEntry entry, Func<Stream> open)
        {
            token.ThrowIfCancellationRequested();

            var name = entry.Key ?? string.Empty;
            if (entry.IsDirectory)
            {
                // A .tar often starts with "./", which is the folder itself.
                if (name.Trim('/', '\\', '.').Length > 0)
                    Directory.CreateDirectory(DestinationFor(target, name));
                return;
            }

            if (entry.IsEncrypted)
                throw new InvalidDataException(PasswordMessage);

            // A link inside a .tar or .rar is not made: it could point anywhere on the disk.
            if (!string.IsNullOrEmpty(entry.LinkTarget))
                return;

            var destination = DestinationFor(target, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using (var input = open())
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            {
                long written = 0;
                int read;
                while ((read = input.Read(_buffer, 0, _buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    written += read;
                    // As for a ZIP file: an entry that unpacks to more than it claimed is stopped. (A size of
                    // 0 means the archive didn't say; the free-space check and the disk itself are the limit.)
                    if (entry.Size > 0 && written > entry.Size)
                        throw new InvalidDataException($"{name} is larger than the archive says it is.");

                    output.Write(_buffer, 0, read);
                    _done += read;
                    if (_clock.ElapsedMilliseconds >= 100)
                    {
                        Report(name);
                        _clock.Restart();
                    }
                }
            }

            if (entry.LastModifiedTime is { } stamp)
            {
                try { File.SetLastWriteTime(destination, stamp); }
                catch (Exception error) when (error is IOException or ArgumentOutOfRangeException or UnauthorizedAccessException)
                {
                    // the file is unpacked; its date is not worth failing for
                }
            }
        }

        private void Report(string name)
        {
            if (Through is { } file)
            {
                long position;
                try { position = file.Position; }
                catch (ObjectDisposedException) { return; }
                progress?.Report(new ArchiveProgress(position, file.Length, name));
            }
            else
            {
                progress?.Report(new ArchiveProgress(_done, total, name));
            }
        }

        internal void Done()
        {
            var end = Through?.Length ?? total;
            progress?.Report(new ArchiveProgress(end, end, string.Empty));
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
