// Clearspace | NEW (duplicates): finds files in a folder that are exact copies of each other.
// Two files are copies when their contents are the same byte for byte; the name, the date and where they
// are make no difference. Reading every file in full would be slow, so the search narrows down in steps:
//   1. size  - only files that share their size with another file can be copies; the rest are dropped
//              without being opened at all
//   2. start - of those, the first 64 KB is read and compared (by its SHA-256)
//   3. whole - files that still match are read in full and compared (by their SHA-256)
// CHANGED (duplicates from the index): step 1 needs only each file's name and size, which the file index
// already holds, so on an indexed drive the list comes from there (Services/DuplicateIndex.cs) and no
// folder is read at all. Steps 2 and 3 are about contents, which no index has: those files are read. The
// index can be a moment behind the disk, so every file is checked as it is opened: one whose size is no
// longer what the list said is left out, and its date is taken from the disk, not from the list.
// Left out, because removing them would not do what the view promises:
//   - empty files (they are all "the same" and free nothing)
//   - links (a link is a second name, not a second copy), and two names for one file (hard links)
//   - cloud files that are not on this device (reading them would download them)
//   - hidden and system files, unless hidden items are shown
// Folders that OneDrive and other cloud services keep in step are looked through like any other; only
// links to somewhere else (shortcuts-as-folders, junctions) are not followed.
// Nothing is changed on the disk here. DuplicatesView shows the groups and removes what you tick; Removable
// looks at the ticked files once more just before that (their size and date, not their contents again). Tests: Clearspace.Tests/DuplicateTests.cs.
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Clearspace.Services;

// MayBeLink: Windows marks the file as special (a link, or a cloud file); which of the two is found out
// when it is opened, and a link is then left out.
internal sealed record DuplicateFile(string FullPath, long Size, DateTime Modified, bool MayBeLink = false)
{
    public string Name => Path.GetFileName(FullPath);
    public string Folder => Path.GetDirectoryName(FullPath) ?? string.Empty;
}

// Files with the same contents: always two or more, oldest first.
internal sealed record DuplicateGroup(long Size, IReadOnlyList<DuplicateFile> Files)
{
    // What removing every copy but one would free.
    public long Wasted => Size * (Files.Count - 1);
}

// Groups: the most wasteful first. Skipped: files that could not be compared (in use, no permission, a
// link, or changed since they were listed). FromIndex: the list of files came from the file index.
internal sealed record DuplicateScan(IReadOnlyList<DuplicateGroup> Groups, int FilesChecked, int Skipped, bool FromIndex = false)
{
    public long Wasted => Groups.Sum(group => group.Wasted);
    public int Copies => Groups.Sum(group => group.Files.Count - 1);
}

internal sealed record DuplicateOptions(bool Subfolders = true, bool Hidden = false);

// The files that could be copies. Checked: how many files were looked at to arrive at them (a listing
// from the index only carries the ones that share their size with another).
internal sealed record DuplicateListing(List<DuplicateFile> Files, int Checked, bool FromIndex);

// Stage: "Listing files" (Total is 0 until it ends), then "Comparing" (bytes read of bytes to read).
internal readonly record struct DuplicateProgress(string Stage, long Done, long Total, string Current);

internal static class DuplicateFinder
{
    internal const int HeadBytes = 64 * 1024;
    private const int BufferBytes = 256 * 1024;
    internal const FileAttributes NotOnThisDevice = FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000; // + recall on open / on data access

    // `fromIndex`: asked first for the list of files; null from it (the folder is not indexed) means the
    // folders are read instead. Left out, the folders are always read.
    internal static Task<DuplicateScan> FindAsync(string folder, DuplicateOptions options,
        IProgress<DuplicateProgress>? progress, CancellationToken token,
        Func<string, DuplicateOptions, CancellationToken, DuplicateListing?>? fromIndex = null)
        => Task.Run(() => Find(folder, options, progress, token, fromIndex), token);

    internal static DuplicateScan Find(string folder, DuplicateOptions options,
        IProgress<DuplicateProgress>? progress = null, CancellationToken token = default,
        Func<string, DuplicateOptions, CancellationToken, DuplicateListing?>? fromIndex = null)
    {
        // ---- 1. size
        var listing = fromIndex?.Invoke(folder, options, token);
        if (listing is null)
        {
            var onDisk = List(folder, options, progress, token);
            listing = new DuplicateListing(onDisk, onDisk.Count, FromIndex: false);
        }

        var checkedCount = listing.Checked;
        var listedFromIndex = listing.FromIndex;
        var skipped = 0;

        var sameSize = listing.Files.GroupBy(file => file.Size).Where(group => group.Count() > 1).Select(group => group.ToList()).ToList();
        listing = null; // can be millions of rows on a whole drive; only the candidates are needed from here

        // What is about to be read, so the bar can show how far along it is: the start of every candidate
        // now, and (an estimate, as most candidates of the same size do turn out to be copies) the rest of them.
        long total = sameSize.Sum(group => group.Sum(file => file.Size));
        long done = 0;
        var lastReport = 0L;

        void Report(string current, bool force = false)
        {
            if (progress is null)
                return;

            var now = Environment.TickCount64;
            if (!force && now - lastReport < 80)
                return;

            lastReport = now;
            progress.Report(new DuplicateProgress("Comparing", Math.Min(done, total), total, current));
        }

        var groups = new List<DuplicateGroup>();
        var identities = new Dictionary<string, FileIdentity?>(StringComparer.Ordinal);

        // FIXED (pre-release review): one buffer for the whole search. Hash used to make a new one for every
        // file it opened (and the file stream a second one of the same size): on a folder with tens of
        // thousands of candidates that is gigabytes of short-lived large blocks for the garbage collector.
        var buffer = new byte[BufferBytes];

        foreach (var candidates in sameSize)
        {
            token.ThrowIfCancellationRequested();
            var size = candidates[0].Size;

            // ---- 2. start
            var byHead = new Dictionary<string, List<DuplicateFile>>(StringComparer.Ordinal);
            foreach (var listed in candidates)
            {
                token.ThrowIfCancellationRequested();
                Report(listed.Name);

                var head = Hash(listed, HeadBytes, identities, buffer, onRead: null, token, out var modified);
                if (head is null)
                {
                    skipped++;
                    done += size; // none of it will be read
                    continue;
                }

                // From here on the file carries the date the disk gave when it was opened.
                var file = listed.Modified == modified ? listed : listed with { Modified = modified };
                if (!byHead.TryGetValue(head, out var list))
                    byHead[head] = list = [];
                list.Add(file);
            }

            foreach (var alike in byHead.Values)
            {
                if (alike.Count < 2)
                {
                    done += size;
                    continue;
                }

                // A file no longer than the start has been read in full already.
                if (size <= HeadBytes)
                {
                    done += size * alike.Count;
                    Add(groups, size, alike, identities);
                    continue;
                }

                // ---- 3. whole
                var byContents = new Dictionary<string, List<DuplicateFile>>(StringComparer.Ordinal);
                foreach (var file in alike)
                {
                    token.ThrowIfCancellationRequested();
                    var before = done;
                    var whole = Hash(file, long.MaxValue, identities, buffer, read =>
                    {
                        done += read;
                        Report(file.Name);
                    }, token, out _);
                    done = before + size;

                    if (whole is null)
                    {
                        skipped++;
                        continue;
                    }

                    if (!byContents.TryGetValue(whole, out var list))
                        byContents[whole] = list = [];
                    list.Add(file);
                }

                foreach (var same in byContents.Values.Where(list => list.Count > 1))
                    Add(groups, size, same, identities);
            }
        }

        done = total;
        Report(string.Empty, force: true);

        groups.Sort((a, b) =>
        {
            var byWaste = b.Wasted.CompareTo(a.Wasted);
            return byWaste != 0 ? byWaste : string.Compare(a.Files[0].FullPath, b.Files[0].FullPath, StringComparison.OrdinalIgnoreCase);
        });
        return new DuplicateScan(groups, checkedCount, skipped, listedFromIndex);
    }

    // Adds a group, counting two names for one file (hard links) as a single copy: removing one of them
    // frees nothing, so it is not a duplicate in the sense the view means.
    private static void Add(List<DuplicateGroup> groups, long size, List<DuplicateFile> files,
        Dictionary<string, FileIdentity?> identities)
    {
        var seen = new List<FileIdentity>();
        var copies = new List<DuplicateFile>(files.Count);

        foreach (var file in files.OrderBy(file => file.Modified).ThenBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            if (identities.TryGetValue(file.FullPath, out var identity) && identity is { } known)
            {
                if (seen.Contains(known))
                    continue;
                seen.Add(known);
            }

            copies.Add(file);
        }

        if (copies.Count > 1)
            groups.Add(new DuplicateGroup(size, copies));
    }

    // Every file under `folder` that could be a copy of another. Folders that can't be read are passed over.
    internal static List<DuplicateFile> List(string folder, DuplicateOptions options,
        IProgress<DuplicateProgress>? progress = null, CancellationToken token = default)
    {
        var found = new List<DuplicateFile>();
        var waiting = new Stack<string>();
        waiting.Push(folder);
        var lastReport = 0L;

        while (waiting.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = waiting.Pop();

            try
            {
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    var attributes = entry.Attributes;
                    var special = (attributes & FileAttributes.ReparsePoint) != 0;
                    if (!options.Hidden && (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        continue;

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        // CHANGED: a folder a cloud service keeps in step is marked as special too, and
                        // is looked through; a link to another folder is not (it could lead in a circle,
                        // and its files are the same files, not copies).
                        if (options.Subfolders && !(special && IsLink(entry)))
                            waiting.Push(entry.FullName);
                        continue;
                    }

                    if ((attributes & NotOnThisDevice) != 0 || entry is not FileInfo file || file.Length == 0)
                        continue;

                    try
                    {
                        found.Add(new DuplicateFile(file.FullName, file.Length, file.LastWriteTime, MayBeLink: special));
                    }
                    catch (ArgumentException)
                    {
                        // A date Windows can't express as a time. The file is left out; it could not be
                        // checked again before removing anyway.
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A folder Windows won't list. What could be read is still compared.
            }

            if (progress is not null && Environment.TickCount64 - lastReport >= 80)
            {
                lastReport = Environment.TickCount64;
                progress.Report(new DuplicateProgress("Listing files", found.Count, 0, current));
            }
        }

        return found;
    }

    // Whether a file or folder Windows marks as special is a link to somewhere else (a symbolic link, a
    // junction, a mounted drive) rather than a cloud file. When that can't be found out, it is taken to be
    // one: leaving something out is the safe side.
    private static bool IsLink(FileSystemInfo entry)
    {
        try
        {
            return entry.LinkTarget is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            return true;
        }
    }

    // The SHA-256 of the first `limit` bytes of a file (all of them for long.MaxValue), or null when it
    // can't be compared: it can't be read, it is a link, or it is not the size the list said (it changed
    // since it was listed, or shrank while being read). Other programs may keep the file open; it is read
    // as it is. modified: the file's date as the disk gives it now. buffer: what the file is read through;
    // the caller keeps one for the whole search.
    private static string? Hash(DuplicateFile file, long limit, Dictionary<string, FileIdentity?> identities,
        byte[] buffer, Action<int>? onRead, CancellationToken token, out DateTime modified)
    {
        modified = file.Modified;
        var path = file.FullPath;

        try
        {
            if (file.MayBeLink && IsLink(new FileInfo(path)))
                return null;

            // CHANGED (pre-release review): bufferSize 1 = the stream keeps no buffer of its own. The reads
            // below are already large blocks into `buffer`; a second 256 KB buffer per file only cost memory,
            // and for the 64 KB "start" read it made Windows read four times what was asked for.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);

            if (stream.Length != file.Size)
                return null;
            modified = File.GetLastWriteTime(stream.SafeFileHandle);

            if (!identities.ContainsKey(path))
                identities[path] = Identity(stream.SafeFileHandle);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var left = Math.Min(limit, file.Size);

            while (left > 0)
            {
                token.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read == 0)
                    return null; // shorter than it was a moment ago

                hash.AppendData(buffer, 0, read);
                left -= read;
                onRead?.Invoke(read);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or
                                          NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    // ---- choosing what to remove

    // Every copy but the newest (or the oldest) of a group.
    internal static IEnumerable<DuplicateFile> AllButNewest(DuplicateGroup group)
    {
        var keep = group.Files.MaxBy(file => file.Modified)!;
        return group.Files.Where(file => !ReferenceEquals(file, keep));
    }

    internal static IEnumerable<DuplicateFile> AllButOldest(DuplicateGroup group)
    {
        var keep = group.Files.MinBy(file => file.Modified)!;
        return group.Files.Where(file => !ReferenceEquals(file, keep));
    }

    // Whether `file` may be ticked: a group must keep at least one copy that is not ticked.
    internal static bool CanTick(DuplicateGroup group, DuplicateFile file, ISet<string> ticked)
        => group.Files.Any(other => !ReferenceEquals(other, file) && !ticked.Contains(other.FullPath));

    // The ticked files that are safe to remove right now. Time has passed since the search, so each group
    // is looked at again: a ticked file is removed only if it is still the file that was compared (same
    // size and date) and a copy that is NOT ticked is too. `probe` gives a file's size and date now, or
    // null when it is gone; File.Exists and friends in the app, a table in the tests.
    internal static IReadOnlyList<DuplicateFile> Removable(IEnumerable<DuplicateGroup> groups, ISet<string> ticked,
        Func<string, (long Size, DateTime Modified)?> probe, out int heldBack)
    {
        var remove = new List<DuplicateFile>();
        heldBack = 0;

        foreach (var group in groups)
        {
            var wanted = group.Files.Where(file => ticked.Contains(file.FullPath)).ToList();
            if (wanted.Count == 0)
                continue;

            bool Unchanged(DuplicateFile file) => probe(file.FullPath) is { } now && now.Size == file.Size && now.Modified == file.Modified;

            if (!group.Files.Any(file => !ticked.Contains(file.FullPath) && Unchanged(file)))
            {
                heldBack += wanted.Count; // no copy would be left that is known to be the same
                continue;
            }

            foreach (var file in wanted)
            {
                if (Unchanged(file))
                    remove.Add(file);
                else
                    heldBack++;
            }
        }

        return remove;
    }

    internal static (long Size, DateTime Modified)? ProbeDisk(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTime) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            return null;
        }
    }

    // ---- which file on the disk a name stands for (two names with the same answer are one file)

    private readonly record struct FileIdentity(uint Volume, uint IndexHigh, uint IndexLow);

    private static FileIdentity? Identity(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            return GetFileInformationByHandle(handle, out var info) && info.Links > 1
                ? new FileIdentity(info.Volume, info.IndexHigh, info.IndexLow)
                : null; // one name only: nothing to tell apart
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInformation info);
}
