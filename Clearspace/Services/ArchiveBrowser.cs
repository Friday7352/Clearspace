// Clearspace | NEW (zip folders): reading a ZIP file as if it were a folder, and taking things out of it.
// A ZIP file opens in the list like a folder (Services/ArchiveLocation.cs has the address of a place inside
// one). This file is what reads it:
//  - List gives the files and folders at one place inside the ZIP. Only the ZIP's table of contents is
//    read, never the files themselves, and the table is remembered for the last few ZIP files, so going
//    from folder to folder inside one does not read it again. A ZIP that has changed on the disk (another
//    size or date) is read afresh.
//  - A ZIP often has no entry for its folders, only names like "Trip/2024/a.jpg"; the folders are worked
//    out from those names.
//  - Nothing here ever writes to a ZIP file. The inside of a ZIP is read-only in Clearspace.
//  - Extract writes chosen files and folders into a real folder. As in ArchiveService: nothing already on
//    the disk is overwritten ("name (2)"), a name that would land outside the folder is refused, a file
//    that unpacks larger than the ZIP says is stopped, and what was half made is removed on failure.
//  - Copies writes them into Clearspace's own temporary folder, for opening a file (double-click), for the
//    preview pane, and for Copy and dragging out. A copy that is already there and matches is used again.
//    Copies made for opening are marked read-only, so a program says so instead of quietly saving changes
//    into a file nobody will find. Copies older than a day are removed when Clearspace starts (TidyLater).
//  - A copy made for opening keeps Windows' "came from the internet" mark when the ZIP file has it (it
//    was downloaded), so Windows still asks before running a program straight out of a downloaded ZIP.
//    What is extracted or copied out is written without the mark, as Extract always has: Clearspace can't
//    lock a file that carries one (Services/FileLockSafety.cs allows a single data stream).
//  - Each folder of copies notes which ZIP file it came from, so that the copies from ZIP files in a
//    locked folder can be removed when that folder is locked again (RemoveCopiesFrom).
// Only .zip, like the rest of the archive support. The window side is MainWindow.Archives.cs.
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Clearspace.Models;

namespace Clearspace.Services;

/// <summary>One file or folder inside a ZIP file.</summary>
/// <param name="Inside">Its path inside the ZIP, "/" between the parts ("Trip/2024/a.jpg").</param>
/// <param name="Index">Where its entry is in the ZIP's table of contents; -1 for a folder the ZIP only implies.</param>
/// <param name="Source">The entry's name exactly as the ZIP has it, to check the ZIP is still the same one.</param>
internal sealed record ArchiveEntry(string Name, string Inside, bool IsFolder, long Size, DateTime Modified,
    int Index, string? Source, bool Encrypted);

internal static class ArchiveBrowser
{
    private const int BufferSize = 1 << 16;
    private const int MostRemembered = 4;          // tables of contents kept in memory
    private const string PasswordMessage = "This file is protected with a password. Clearspace can't open those yet.";
    private const string ChangedMessage = "The ZIP file has changed. Refresh and try again.";

    /// <summary>Entries larger than this are not copied out just to show them in the preview pane.</summary>
    internal const long MostToPreview = 32L * 1024 * 1024;

    // ---- the table of contents

    private sealed class Contents(string file, long length, DateTime modifiedUtc)
    {
        public string File => file;
        public long Length => length;
        public DateTime ModifiedUtc => modifiedUtc;

        // Folder (its path inside, "" for the top) -> what is directly in it, by name.
        public Dictionary<string, Dictionary<string, ArchiveEntry>> Folders { get; } = new(StringComparer.Ordinal)
        {
            [string.Empty] = new(StringComparer.Ordinal)
        };

        public Dictionary<string, ArchiveEntry> All { get; } = new(StringComparer.Ordinal);

        // What is in a folder. The exact spelling first; a typed address may differ in capitals.
        public bool TryGetFolder(string inside, out string spelled, out Dictionary<string, ArchiveEntry> items)
        {
            spelled = inside;
            if (Folders.TryGetValue(inside, out items!))
                return true;

            foreach (var (name, found) in Folders)
            {
                if (!name.Equals(inside, StringComparison.OrdinalIgnoreCase))
                    continue;
                spelled = name;
                items = found;
                return true;
            }

            return false;
        }

        public ArchiveEntry? Find(string inside)
        {
            if (All.TryGetValue(inside, out var exact))
                return exact;

            foreach (var (name, found) in All)
            {
                if (name.Equals(inside, StringComparison.OrdinalIgnoreCase))
                    return found;
            }

            return null;
        }
    }

    private static readonly List<Contents> Remembered = [];

    private static Contents Read(string archiveFile)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(archiveFile);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new FileNotFoundException("That ZIP file is no longer there.", archiveFile);
        }

        if (!info.Exists)
            throw new FileNotFoundException("That ZIP file is no longer there.", archiveFile);

        var length = info.Length;
        var modified = info.LastWriteTimeUtc;

        lock (Remembered)
        {
            for (var index = 0; index < Remembered.Count; index++)
            {
                var known = Remembered[index];
                if (!known.File.Equals(info.FullName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (known.Length == length && known.ModifiedUtc == modified)
                {
                    Remembered.RemoveAt(index);
                    Remembered.Insert(0, known); // most recently used first
                    return known;
                }

                Remembered.RemoveAt(index); // the ZIP changed on the disk
                break;
            }
        }

        var contents = Build(info.FullName, length, modified);

        lock (Remembered)
        {
            Remembered.Insert(0, contents);
            if (Remembered.Count > MostRemembered)
                Remembered.RemoveRange(MostRemembered, Remembered.Count - MostRemembered);
        }

        return contents;
    }

    private static ZipArchive Open(string archiveFile)
    {
        try
        {
            return ZipFile.OpenRead(archiveFile);
        }
        catch (InvalidDataException)
        {
            throw new IOException("Clearspace can't read this ZIP file. It may be damaged, or not finished downloading.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new IOException("You don't have permission to read this ZIP file.");
        }
    }

    private static Contents Build(string archiveFile, long length, DateTime modifiedUtc)
    {
        var contents = new Contents(archiveFile, length, modifiedUtc);
        using var archive = Open(archiveFile);

        IReadOnlyList<ZipArchiveEntry> entries;
        try
        {
            entries = archive.Entries;
        }
        catch (InvalidDataException)
        {
            throw new IOException("Clearspace can't read this ZIP file. It may be damaged, or not finished downloading.");
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var name = entry.FullName.Replace('\\', '/');
            var isFolder = name.EndsWith('/');
            var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);

            // "." and ".." are how a hostile ZIP reaches outside the folder it is unpacked into. Such an
            // entry is not shown, so it can't be opened or extracted either.
            if (parts.Length == 0 || Array.Exists(parts, part => part is "." or ".."))
                continue;

            // Every folder on the way down exists, whether or not the ZIP has an entry for it.
            var parent = string.Empty;
            var blocked = false;
            var folders = isFolder ? parts.Length : parts.Length - 1;
            for (var depth = 0; depth < folders; depth++)
            {
                var path = parent.Length == 0 ? parts[depth] : parent + "/" + parts[depth];
                if (contents.All.TryGetValue(path, out var existing))
                {
                    if (!existing.IsFolder)
                    {
                        blocked = true; // a file and a folder with one name: the file was first, it stays
                        break;
                    }
                }
                else
                {
                    var made = new ArchiveEntry(parts[depth], path, IsFolder: true, 0, DateTime.MinValue, -1, null, false);
                    contents.All[path] = made;
                    contents.Folders[parent][parts[depth]] = made;
                    contents.Folders[path] = new Dictionary<string, ArchiveEntry>(StringComparer.Ordinal);
                }

                parent = path;
            }

            if (blocked)
                continue;

            var when = ModifiedOf(entry);

            if (isFolder)
            {
                // The ZIP's own entry for a folder: it knows the folder's date.
                var folder = contents.All[parent];
                if (folder.Index < 0)
                {
                    var dated = folder with { Modified = when, Index = index, Source = entry.FullName };
                    contents.All[parent] = dated;
                    var above = parent.LastIndexOf('/');
                    contents.Folders[above < 0 ? string.Empty : parent[..above]][dated.Name] = dated;
                }

                continue;
            }

            var fileName = parts[^1];
            var inside = parent.Length == 0 ? fileName : parent + "/" + fileName;
            if (contents.All.TryGetValue(inside, out var same) && same.IsFolder)
                continue; // a folder and a file with one name: the folder was first, it stays

            // The same name twice: the later entry is the newer one (a ZIP that was added to), so it wins.
            var file = new ArchiveEntry(fileName, inside, IsFolder: false, entry.Length, when, index, entry.FullName, entry.IsEncrypted);
            contents.All[inside] = file;
            contents.Folders[parent][fileName] = file;
        }

        return contents;
    }

    private static DateTime ModifiedOf(ZipArchiveEntry entry)
    {
        try
        {
            return entry.LastWriteTime.LocalDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Forgets every table of contents that is remembered (for tests).</summary>
    internal static void Forget()
    {
        lock (Remembered)
            Remembered.Clear();
    }

    // ---- looking

    /// <summary>
    /// The files and folders at a place inside a ZIP file, unsorted. Throws FileNotFoundException when
    /// the ZIP is gone, DirectoryNotFoundException when it has no such folder, and IOException (with words
    /// for the status line) when it can't be read.
    /// </summary>
    internal static List<FileSystemItem> List(string location, CancellationToken token = default)
    {
        if (!ArchiveLocation.TrySplit(location, out var archiveFile, out var inside))
            throw new DirectoryNotFoundException();

        var contents = Read(archiveFile);
        token.ThrowIfCancellationRequested();

        if (!contents.TryGetFolder(inside, out _, out var entries))
            throw new DirectoryNotFoundException();

        var items = new List<FileSystemItem>(entries.Count);
        foreach (var entry in entries.Values)
        {
            items.Add(FileSystemItem.FromArchiveEntry(
                ArchiveLocation.For(contents.File, entry.Inside), entry.Name, entry.IsFolder, entry.Size, entry.Modified));
        }

        return items;
    }

    /// <summary>The file or folder at an address inside a ZIP (the top of the ZIP counts as a folder), or
    /// null when there is none. Does not throw for a ZIP that is missing or can't be read.</summary>
    internal static ArchiveEntry? Find(string location)
    {
        if (!ArchiveLocation.TrySplit(location, out var archiveFile, out var inside))
            return null;

        try
        {
            var contents = Read(archiveFile);
            return inside.Length == 0
                ? new ArchiveEntry(Path.GetFileName(archiveFile), string.Empty, IsFolder: true, 0, DateTime.MinValue, -1, null, false)
                : contents.Find(inside);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>How many files the chosen things hold (everything inside chosen folders counts) and how
    /// large they are unpacked. Things that are not there count as nothing.</summary>
    internal static (int Files, long Bytes) Measure(IReadOnlyList<string> locations)
    {
        var files = 0;
        long bytes = 0;

        foreach (var (contents, tops) in Group(locations))
        {
            foreach (var top in tops)
            {
                foreach (var file in FilesUnder(contents, top))
                {
                    files++;
                    bytes += file.Size;
                }
            }
        }

        return (files, bytes);
    }

    // The chosen things, by the ZIP they are in (normally all in one).
    private static List<(Contents Contents, List<ArchiveEntry> Tops)> Group(IReadOnlyList<string> locations)
    {
        var groups = new List<(Contents Contents, List<ArchiveEntry> Tops)>();

        foreach (var location in locations)
        {
            if (!ArchiveLocation.TrySplit(location, out var archiveFile, out var inside) || inside.Length == 0)
                continue;

            var at = groups.FindIndex(group => group.Contents.File.Equals(archiveFile, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                groups.Add((Read(archiveFile), []));
                at = groups.Count - 1;
            }

            if (groups[at].Contents.Find(inside) is { } entry && !groups[at].Tops.Contains(entry))
                groups[at].Tops.Add(entry);
        }

        return groups;
    }

    // Every file in or under an entry. No recursion: a ZIP can nest folders deeper than a call stack goes.
    private static IEnumerable<ArchiveEntry> FilesUnder(Contents contents, ArchiveEntry top)
    {
        if (!top.IsFolder)
        {
            yield return top;
            yield break;
        }

        var waiting = new Stack<ArchiveEntry>();
        waiting.Push(top);
        while (waiting.Count > 0)
        {
            foreach (var child in contents.Folders[waiting.Pop().Inside].Values)
            {
                if (child.IsFolder)
                    waiting.Push(child);
                else
                    yield return child;
            }
        }
    }

    // ---- taking things out

    /// <summary>
    /// Writes the chosen files and folders (with everything in them) into <paramref name="folder"/> and
    /// returns what was made there. Nothing on the disk is replaced: a name that is taken becomes "name (2)".
    /// </summary>
    internal static IReadOnlyList<string> Extract(IReadOnlyList<string> locations, string folder,
        IProgress<ArchiveProgress>? progress, CancellationToken token)
    {
        var groups = Group(locations);
        var total = Check(groups);
        EnsureRoom(folder, total);
        Directory.CreateDirectory(folder);

        var made = new List<string>();
        var meter = new Meter(total, progress);
        try
        {
            foreach (var (contents, tops) in groups)
            {
                using var archive = Open(contents.File);

                foreach (var top in tops)
                {
                    token.ThrowIfCancellationRequested();
                    var destination = ArchiveService.UniquePath(Path.Combine(folder, SafeName(top.Name)));

                    if (!top.IsFolder)
                    {
                        made.Add(WriteNew(archive, top, destination, meter, token));
                        continue;
                    }

                    Directory.CreateDirectory(destination);
                    made.Add(destination);
                    WriteFolder(archive, contents, top, destination, meter, token);
                }
            }

            meter.Finish();
            return made;
        }
        catch
        {
            foreach (var path in made)
                TryDelete(path);
            throw;
        }
    }

    public static Task<IReadOnlyList<string>> ExtractAsync(IReadOnlyList<string> locations, string folder,
        IProgress<ArchiveProgress>? progress, CancellationToken token)
        => Task.Run(() => Extract(locations, folder, progress, token), token);

    // Everything in a folder of the ZIP, into a folder on the disk that was just made.
    private static void WriteFolder(ZipArchive archive, Contents contents, ArchiveEntry top, string destination,
        Meter meter, CancellationToken token)
    {
        var waiting = new Stack<(ArchiveEntry Folder, string Path)>();
        waiting.Push((top, destination));

        while (waiting.Count > 0)
        {
            var (folder, path) = waiting.Pop();
            foreach (var child in contents.Folders[folder.Inside].Values)
            {
                token.ThrowIfCancellationRequested();
                var target = ArchiveService.DestinationFor(path, SafeName(child.Name));

                if (child.IsFolder)
                {
                    Directory.CreateDirectory(target);
                    waiting.Push((child, target));
                }
                else
                {
                    WriteNew(archive, child, target, meter, token);
                }
            }
        }
    }

    // Refuses what can't be taken out, before anything is written. Returns the size of all of it.
    private static long Check(List<(Contents Contents, List<ArchiveEntry> Tops)> groups)
    {
        long total = 0;
        foreach (var (contents, tops) in groups)
        {
            foreach (var top in tops)
            {
                foreach (var file in FilesUnder(contents, top))
                {
                    if (file.Encrypted)
                        throw new IOException(PasswordMessage);
                    total += file.Size;
                }
            }
        }

        return total;
    }

    // The entry of the ZIP that is open for one remembered file, checked to be the same one.
    private static ZipArchiveEntry SourceOf(ZipArchive archive, ArchiveEntry file)
    {
        if (file.Index < 0 || file.Index >= archive.Entries.Count)
            throw new IOException(ChangedMessage);

        var source = archive.Entries[file.Index];
        if (!string.Equals(source.FullName, file.Source, StringComparison.Ordinal) || source.Length != file.Size)
            throw new IOException(ChangedMessage);

        return source;
    }

    // Writes one file under a name that is free; returns the name it got.
    private static string WriteNew(ZipArchive archive, ArchiveEntry file, string destination,
        Meter meter, CancellationToken token)
    {
        var source = SourceOf(archive, file);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        FileStream output;
        while (true)
        {
            try
            {
                // CreateNew: a file that is already there is never replaced.
                output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
                break;
            }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
            {
                // Taken after all: two names in the ZIP that Windows reads as one ("a.txt" and "A.TXT").
                destination = ArchiveService.UniquePath(destination);
            }
        }

        Fill(source, file, output, destination, mark: null, meter, token);
        return destination;
    }

    // Copies an entry into a file that was just opened for it, then gives the file its date. Closes the
    // file; removes it again when the copy fails or is stopped.
    private static void Fill(ZipArchiveEntry source, ArchiveEntry file, FileStream output, string destination,
        byte[]? mark, Meter meter, CancellationToken token)
    {
        try
        {
            using (output)
            using (var input = OpenEntry(source))
            {
                var buffer = meter.Buffer;
                long written = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    written += read;
                    // A ZIP can claim a small size and unpack to a huge one; stop at what it claimed.
                    if (written > file.Size)
                        throw new IOException($"{file.Name} is larger than the ZIP file says it is.");

                    output.Write(buffer, 0, read);
                    meter.Add(read, file.Name);
                }
            }

            ZoneMark.Write(destination, mark); // before the date: writing the mark counts as a change

            if (file.Modified != DateTime.MinValue)
            {
                try { File.SetLastWriteTime(destination, file.Modified); }
                catch (Exception error) when (error is IOException or ArgumentOutOfRangeException or UnauthorizedAccessException)
                {
                    // the file is unpacked; its date is not worth failing for
                }
            }
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    private static Stream OpenEntry(ZipArchiveEntry source)
    {
        try
        {
            return source.Open();
        }
        catch (InvalidDataException)
        {
            throw new IOException($"{source.Name} can't be read. It is damaged, or packed in a way Clearspace does not know.");
        }
    }

    // A name from a ZIP as a name Windows accepts for one file or folder. ZIP files made on other systems
    // use characters Windows does not allow (a colon, a question mark); those become "_".
    internal static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new StringBuilder(name.Length);
        foreach (var letter in name)
            text.Append(Array.IndexOf(invalid, letter) >= 0 ? '_' : letter);

        var safe = text.ToString().TrimEnd(' ', '.');
        if (safe.Length == 0)
            return "_";

        // CON, NUL, COM1 and the like are devices to Windows, whatever ending follows.
        var stem = safe.Split('.')[0];
        string[] devices = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        return Array.Exists(devices, device => device.Equals(stem, StringComparison.OrdinalIgnoreCase)) ? "_" + safe : safe;
    }

    private static void EnsureRoom(string folder, long bytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root))
                return;

            if (new DriveInfo(root).AvailableFreeSpace < bytes)
                throw new IOException($"There isn't enough free space on {root} for this.");
        }
        catch (ArgumentException)
        {
            // a network path: Windows gives no drive to ask, and writing fails by itself if it fills up
        }
    }

    // A file, or a folder with everything in it. Copies made for opening are read-only, which a delete minds.
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }

                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Counts what has been written and reports it a few times a second.
    private sealed class Meter(long total, IProgress<ArchiveProgress>? progress)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _done;

        public byte[] Buffer { get; } = new byte[BufferSize];

        public void Add(long bytes, string name)
        {
            _done += bytes;
            if (progress is null || _clock.ElapsedMilliseconds < 100)
                return;

            progress.Report(new ArchiveProgress(_done, total, name));
            _clock.Restart();
        }

        public void Finish() => progress?.Report(new ArchiveProgress(total, total, string.Empty));
    }

    // ---- copies in the temporary folder

    /// <summary>Where Clearspace keeps what it takes out of ZIP files to open, preview or copy.</summary>
    internal static string TempRoot { get; set; } = Path.Combine(Path.GetTempPath(), "Clearspace", "Zip");

    /// <summary>
    /// Real files and folders for the chosen things, in Clearspace's temporary folder; one path for each
    /// location that is there, in the same order. A copy that is already there and still matches the ZIP is
    /// used again. <paramref name="forOpening"/> marks the files read-only.
    /// </summary>
    internal static IReadOnlyList<string> Copies(IReadOnlyList<string> locations, bool forOpening,
        IProgress<ArchiveProgress>? progress, CancellationToken token)
    {
        var groups = Group(locations);
        var total = Check(groups);
        EnsureRoom(TempRoot, total);

        var copies = new List<string>();
        var meter = new Meter(total, progress);

        foreach (var (contents, tops) in groups)
        {
            // One folder for each ZIP file as it is now; a ZIP that changes gets a new one.
            var root = Path.Combine(TempRoot, forOpening ? "Open" : "Copy",
                Fingerprint($"{contents.File.ToUpperInvariant()}|{contents.Length}|{contents.ModifiedUtc.Ticks}"));
            Directory.CreateDirectory(root);
            Touch(root);
            NoteSource(root, contents.File);

            ZipArchive? archive = null;
            try
            {
                ZipArchive Archive() => archive ??= Open(contents.File);
                var mark = forOpening ? ZoneMark.Read(contents.File) : null;

                foreach (var top in tops)
                {
                    token.ThrowIfCancellationRequested();

                    // Things from different folders of the ZIP may share a name, so each folder of the ZIP has
                    // a folder of its own here. A short one: the ZIP's own paths can be very long.
                    var above = top.Inside.LastIndexOf('/');
                    var home = Path.Combine(root, Fingerprint(above < 0 ? string.Empty : top.Inside[..above], 8));
                    var destination = ArchiveService.DestinationFor(home, SafeName(top.Name));

                    if (!top.IsFolder)
                    {
                        Refresh(Archive, top, destination, forOpening, mark, meter, token);
                        copies.Add(destination);
                        continue;
                    }

                    Directory.CreateDirectory(destination);
                    var waiting = new Stack<(ArchiveEntry Folder, string Path)>();
                    waiting.Push((top, destination));
                    while (waiting.Count > 0)
                    {
                        var (folder, path) = waiting.Pop();
                        foreach (var child in contents.Folders[folder.Inside].Values)
                        {
                            token.ThrowIfCancellationRequested();
                            var target = ArchiveService.DestinationFor(path, SafeName(child.Name));
                            if (child.IsFolder)
                            {
                                Directory.CreateDirectory(target);
                                waiting.Push((child, target));
                            }
                            else
                            {
                                Refresh(Archive, child, target, forOpening, mark, meter, token);
                            }
                        }
                    }

                    copies.Add(destination);
                }
            }
            finally
            {
                archive?.Dispose();
            }
        }

        meter.Finish();
        return copies;
    }

    public static Task<IReadOnlyList<string>> CopiesAsync(IReadOnlyList<string> locations, bool forOpening,
        IProgress<ArchiveProgress>? progress, CancellationToken token)
        => Task.Run(() => Copies(locations, forOpening, progress, token), token);

    /// <summary>A real, read-only copy of one file inside a ZIP, to open or preview.</summary>
    internal static string CopyForOpening(string location, CancellationToken token = default)
    {
        if (Find(location) is not { IsFolder: false })
            throw new FileNotFoundException("That file is no longer in the ZIP file.");

        return Copies([location], forOpening: true, null, token)[0];
    }

    // Makes sure the copy of one file is there and matches the ZIP.
    private static void Refresh(Func<ZipArchive> archive, ArchiveEntry file, string destination, bool readOnly,
        byte[]? mark, Meter meter, CancellationToken token)
    {
        var existing = new FileInfo(destination);
        if (existing.Exists && existing.Length == file.Size && file.Modified != DateTime.MinValue &&
            Math.Abs((existing.LastWriteTime - file.Modified).TotalSeconds) < 2.5) // a ZIP keeps dates to two seconds
        {
            meter.Add(file.Size, file.Name);
            return;
        }

        var source = SourceOf(archive(), file);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (existing.Exists)
            File.SetAttributes(destination, FileAttributes.Normal); // it was made read-only last time

        var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
        Fill(source, file, output, destination, mark, meter, token);

        if (readOnly)
        {
            try { File.SetAttributes(destination, FileAttributes.ReadOnly); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // Which ZIP file a folder of copies came from: one line in a small file at its top.
    private const string SourceNote = "source.txt";

    private static void NoteSource(string root, string archiveFile)
    {
        try
        {
            var note = Path.Combine(root, SourceNote);
            if (!File.Exists(note))
                File.WriteAllText(note, archiveFile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Removes the copies that were taken out of ZIP files in <paramref name="folder"/> (or anywhere under
    /// it), and of ZIP files that were inside those. Called when a locked folder is locked again: what was
    /// opened out of a ZIP in it must not stay readable in the temporary folder. A copy that a program still
    /// has open can't be removed; the daily tidy takes it later. Never throws.
    /// </summary>
    internal static void RemoveCopiesFrom(string folder)
    {
        try
        {
            var root = TempRoot;
            if (!Directory.Exists(root))
                return;

            var under = new List<string> { Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar };
            for (var found = true; found;)
            {
                found = false;
                foreach (var kind in Directory.EnumerateDirectories(root))
                {
                    foreach (var copies in Directory.EnumerateDirectories(kind))
                    {
                        string source;
                        try { source = File.ReadAllText(Path.Combine(copies, SourceNote)).Trim(); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }

                        if (!under.Exists(place => source.StartsWith(place, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        // A ZIP that was opened out of this one has copies of its own; they go as well.
                        under.Add(copies + Path.DirectorySeparatorChar);
                        TryDelete(copies);
                        found = true;
                    }
                }
            }

            lock (Remembered)
                Remembered.RemoveAll(known => under.Exists(place => known.File.StartsWith(place, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    private static void Touch(string folder)
    {
        try { Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static string Fingerprint(string text, int letters = 12)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..letters];

    /// <summary>Removes copies that have not been used for a day. In the background; never throws.</summary>
    internal static void TidyLater()
    {
        var root = TempRoot;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false); // well after start-up
            Tidy(root, TimeSpan.FromDays(1));
        });
    }

    internal static void Tidy(string root, TimeSpan unusedFor)
    {
        try
        {
            if (!Directory.Exists(root))
                return;

            var cutoff = DateTime.UtcNow - unusedFor;
            foreach (var kind in Directory.EnumerateDirectories(root))
            {
                foreach (var folder in Directory.EnumerateDirectories(kind))
                {
                    if (Directory.GetLastWriteTimeUtc(folder) < cutoff)
                        TryDelete(folder);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Windows' "this came from the internet" mark. A browser puts it on what it downloads (in a second stream
/// of the file called Zone.Identifier), and Windows asks before running a marked program. A file opened
/// straight out of a marked ZIP gets the mark too; otherwise a ZIP would be a way around the question.
/// </summary>
internal static class ZoneMark
{
    private const string StreamName = ":Zone.Identifier";

    /// <summary>The mark to put on files from this ZIP, or null when the ZIP has none (or the disk keeps no marks).</summary>
    internal static byte[]? Read(string file)
    {
        try
        {
            // Read the way DownloadOrigin reads it: the mark is a few lines, so only the start is looked at.
            using var stream = new FileStream(file + StreamName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[4096];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

            foreach (var line in Encoding.UTF8.GetString(buffer, 0, read).Split('\n'))
            {
                var text = line.Trim();
                if (!text.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase))
                    continue;

                // 3 is the internet, 4 sites marked as dangerous. Anything lower needs no question.
                return int.TryParse(text.AsSpan(7), out var zone) && zone >= 3
                    ? Encoding.ASCII.GetBytes($"[ZoneTransfer]\r\nZoneId={zone}\r\n")
                    : null;
            }

            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    internal static void Write(string file, byte[]? mark)
    {
        if (mark is null)
            return;

        try
        {
            File.WriteAllBytes(file + StreamName, mark);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // a disk that keeps no marks (FAT, some network shares): the file is still unpacked
        }
    }
}
