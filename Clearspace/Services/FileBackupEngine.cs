// Clearspace | NEW (file backup): doing one run of a backup (see FileBackupPlan.cs for what a backup is).
// FileBackupEngine.Run takes a plan and does what its kind asks for, inside the plan's own folder only:
//
//   Clone, Add and update
//     1. When the plan keeps earlier versions or removed files, a first pass (SetAside) compares the
//        backup with the original and moves every file the copy is about to replace or remove into
//        "Earlier versions\<date of this run>\", at the same place in the folder tree. A move inside one
//        drive is a rename, so this costs no copying.
//     2. A Clone that does not keep removed files deletes from the backup what is gone from the
//        original (Purge). Removing is always done here, never by robocopy, so the rules below hold
//        whatever robocopy would have made of a case.
//     3. The copy itself. Windows' robocopy does it (FileBackupRobocopy.cs): several files at a time,
//        only what is new or changed. Where robocopy is not there, Clearspace's own copy (ManagedCopy)
//        does the same, more slowly.
//     4. Folders under Earlier versions that are older than the plan keeps them are deleted.
//   Versions
//     A fingerprint of the original (every name, size and date) is compared with the one of the newest
//     version; when nothing changed, nothing is made. Otherwise a new dated folder is filled: a file
//     that is the same as in the version before is hard-linked to it, anything else is copied. The
//     folder has ".partial" after its name until it is whole. Then versions beyond the newest N go.
//   Zip archive
//     The same fingerprint check, then one dated .zip (".cspart" after its name until it is whole),
//     then zips beyond the newest N go.
//     CHANGED (resumable zip): the zip is made safe as it grows, and a run carries on with the zip a run
//     before it did not finish (FileBackupZip.cs).
//
// What is the same: a file is taken to be unchanged when its size is the same and its modified date is
// within two seconds (drives formatted FAT or exFAT keep dates to two seconds).
// What is never copied: links to other folders (they could lead in a circle), cloud files that are not on
// this PC (reading them would download them; a plan can ask for them), Windows' own "System Volume
// Information", Recycle Bin and page files at the top of a drive, and the names the plan leaves out.
// A file that cannot be read (in use, no permission) is noted in the report and the run goes on.
//
// Safety rules:
//   - nothing is written, moved or deleted outside the plan's own folder, and that folder is only used
//     when Clearspace made it (FileBackupLayout.WhyNotRoot);
//   - a Clone whose original has become completely empty refuses to run instead of emptying the backup;
//   - a folder of the original that cannot be listed is never taken to mean "everything in it is gone".
//
// Everything here runs off the UI thread and opens no window. Tests: Clearspace.Tests/FileBackupTests.cs
// (with ManagedCopy; robocopy itself is checked by hand).
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Clearspace.Models;

namespace Clearspace.Services;

/// <summary>Which names a run leaves out.</summary>
internal sealed class FileBackupFilter
{
    /// <summary>Left out of every backup: Windows' own bookkeeping at the top of a drive, which cannot be read anyway.</summary>
    internal static readonly string[] Always =
        ["System Volume Information", "$RECYCLE.BIN", "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log.tmp"];

    /// <summary>A cloud file whose contents are not on this PC (the attributes OneDrive and the others set).</summary>
    internal const FileAttributes NotOnThisDevice = FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;

    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Regex> _wildcards = [];

    internal FileBackupFilter(IEnumerable<string> exclude, bool includeCloudOnly)
    {
        IncludeCloudOnly = includeCloudOnly;
        Patterns = Always.Concat(exclude.Select(pattern => pattern.Trim()).Where(pattern => pattern.Length > 0))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var pattern in Patterns)
        {
            if (pattern.IndexOfAny(['*', '?']) < 0)
            {
                _names.Add(pattern);
                continue;
            }

            var expression = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            _wildcards.Add(new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline));
        }
    }

    internal static FileBackupFilter For(FileBackupPlan plan) => new(plan.Exclude, plan.IncludeCloudOnly);

    internal bool IncludeCloudOnly { get; }

    /// <summary>Every name left out, the built-in ones first, as they are handed to robocopy.</summary>
    internal IReadOnlyList<string> Patterns { get; }

    /// <summary>Whether a file or folder of this name is left out.</summary>
    internal bool SkipsName(string name)
    {
        if (_names.Contains(name))
            return true;

        foreach (var wildcard in _wildcards)
        {
            if (wildcard.IsMatch(name))
                return true;
        }

        return false;
    }

    /// <summary>Whether a file with these attributes is left out because its contents are in the cloud only.</summary>
    internal bool SkipsCloudOnly(FileAttributes attributes) => !IncludeCloudOnly && (attributes & NotOnThisDevice) != 0;
}

/// <summary>One file or folder of what is being backed up.</summary>
/// <param name="Relative">Its place below the top ("Holiday\beach.jpg").</param>
internal readonly record struct FileBackupEntry(string Relative, string FullPath, bool IsFolder, long Size, DateTime ModifiedUtc);

/// <summary>What a walk through a folder passed over.</summary>
internal sealed class FileBackupWalkNotes
{
    public long Links;
    public long CloudOnly;
    public long LeftOut;
    public long Unreadable;
}

internal static class FileBackupWalk
{
    internal static readonly TimeSpan DateSlack = TimeSpan.FromSeconds(2);

    /// <summary>Whether two files with these sizes and modified dates are taken to be different.</summary>
    internal static bool Differs(long size, DateTime modifiedUtc, long otherSize, DateTime otherModifiedUtc)
        => size != otherSize || (modifiedUtc - otherModifiedUtc).Duration() > DateSlack;

    /// <summary>A date that is always there: Windows can hold file dates .NET refuses to express.</summary>
    internal static DateTime Modified(FileSystemInfo info)
    {
        try { return info.LastWriteTimeUtc; }
        catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
    }

    /// <summary>
    /// Whether a folder Windows marks as special is a link to somewhere else (a symbolic link, a junction,
    /// a mounted drive) rather than a cloud folder. When that cannot be found out it is taken to be one.
    /// </summary>
    internal static bool IsLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
            return false;

        try { return entry.LinkTarget is not null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>
    /// Everything under <paramref name="top"/> that a backup copies, always in the same order: a folder,
    /// then its files by name, then its folders by name. A folder comes before anything inside it.
    /// </summary>
    internal static IEnumerable<FileBackupEntry> Entries(string top, FileBackupFilter filter, FileBackupWalkNotes? notes,
        CancellationToken token, Action<string, Exception>? unreadable = null)
    {
        var waiting = new Stack<(string Full, string Relative)>();
        waiting.Push((top, string.Empty));

        while (waiting.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (folder, relative) = waiting.Pop();

            List<FileSystemInfo> inside;
            try
            {
                inside = [.. new DirectoryInfo(folder).EnumerateFileSystemInfos()];
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (relative.Length == 0)
                    throw; // the top itself: nothing can be backed up
                if (notes is not null) notes.Unreadable++;
                unreadable?.Invoke(folder, error);
                continue;
            }

            if (relative.Length > 0)
                yield return new FileBackupEntry(relative, folder, IsFolder: true, 0, DateTime.MinValue);

            inside.Sort((one, other) => string.CompareOrdinal(one.Name, other.Name));
            var folders = new List<FileSystemInfo>();

            foreach (var entry in inside)
            {
                if (filter.SkipsName(entry.Name))
                {
                    if (notes is not null) notes.LeftOut++;
                    continue;
                }

                var attributes = entry.Attributes;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (IsLink(entry))
                    {
                        if (notes is not null) notes.Links++;
                    }
                    else
                    {
                        folders.Add(entry);
                    }

                    continue;
                }

                if (entry is not FileInfo file)
                    continue;

                if (filter.SkipsCloudOnly(attributes))
                {
                    if (notes is not null) notes.CloudOnly++;
                    continue;
                }

                long size;
                try { size = file.Length; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (notes is not null) notes.Unreadable++;
                    unreadable?.Invoke(file.FullName, error);
                    continue;
                }

                yield return new FileBackupEntry(Join(relative, file.Name), file.FullName, IsFolder: false, size, Modified(file));
            }

            for (var index = folders.Count - 1; index >= 0; index--)
                waiting.Push((folders[index].FullName, Join(relative, folders[index].Name)));
        }
    }

    /// <summary>What a plan copies: the walk through its folder, or its one file.</summary>
    internal static IEnumerable<FileBackupEntry> Entries(FileBackupPlan plan, FileBackupFilter filter, FileBackupWalkNotes? notes,
        CancellationToken token, Action<string, Exception>? unreadable = null)
    {
        if (!plan.SourceIsFile)
            return Entries(plan.Source, filter, notes, token, unreadable);

        var file = new FileInfo(plan.Source);
        return [new FileBackupEntry(file.Name, file.FullName, IsFolder: false, file.Length, Modified(file))];
    }

    /// <summary>The same names, sizes and dates always give the same text; anything added, removed or changed gives another.</summary>
    internal static string Fingerprint(IEnumerable<FileBackupEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries)
        {
            var line = entry.IsFolder
                ? "D|" + entry.Relative + "\n"
                : "F|" + entry.Relative + "|" + entry.Size.ToString(CultureInfo.InvariantCulture) + "|" + entry.ModifiedUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "\n";
            hash.AppendData(Encoding.UTF8.GetBytes(line));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Join(string relative, string name) => relative.Length == 0 ? name : relative + Path.DirectorySeparatorChar + name;
}

/// <summary>What a run is given.</summary>
internal sealed class FileBackupRun
{
    public required FileBackupPlan Plan { get; init; }
    public DateTime NowLocal { get; init; } = DateTime.Now;

    /// <summary>Where the run's report is written; null for none.</summary>
    public string? LogPath { get; init; }

    public Action<FileBackupProgress>? Progress { get; init; }
    public CancellationToken Token { get; init; }

    /// <summary>Off in tests, where the copy is Clearspace's own.</summary>
    public bool UseRobocopy { get; init; } = true;
}

/// <summary>What a run did.</summary>
internal sealed class FileBackupResult
{
    public string Outcome { get; set; } = FileBackupState.Ok;
    public string Message { get; set; } = string.Empty;
    public long Copied;        // files copied into the backup
    public bool CopiedSome;    // robocopy said it copied files, but its log gave no count
    public long CopiedBytes;
    public long Linked;        // Versions: files that were the same as in the version before
    public long Removed;       // Clone: files removed from the backup because they are gone from the original
    public long SetAside;      // files moved under Earlier versions before being replaced or removed
    public long Errors;        // files and folders that could not be copied
    public long Pruned;        // old versions, zips or Earlier versions folders deleted
    public bool Made;          // Versions, Zip archive: a new one was made
    public string? MadePath;
}

/// <summary>A run's report: plain text, one line per thing worth knowing. Safe to write from several threads.</summary>
internal sealed class FileBackupLog : IDisposable
{
    private const int MostProblems = 5000; // beyond this only the count goes up
    private readonly StreamWriter? _writer;
    private readonly Lock _gate = new();
    private long _problems;

    private FileBackupLog(StreamWriter? writer) => _writer = writer;

    internal static FileBackupLog Open(string? path)
    {
        if (path is null)
            return new FileBackupLog(null);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new FileBackupLog(new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new FileBackupLog(null); // a run is not stopped because its report cannot be written
        }
    }

    internal long Problems => Interlocked.Read(ref _problems);

    internal void Line(string text)
    {
        if (_writer is null)
            return;

        lock (_gate)
        {
            try { _writer.WriteLine(text); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
    }

    /// <summary>A file or folder that could not be dealt with. Counted, and written while the report is not yet huge.</summary>
    internal void Problem(string path, string what)
    {
        if (Interlocked.Increment(ref _problems) <= MostProblems)
            Line($"PROBLEM  {path}  -  {what}");
    }

    public void Dispose()
    {
        lock (_gate)
            _writer?.Dispose();
    }
}

internal static class FileBackupEngine
{
    private const int CopyThreads = 4;

    /// <summary>Does one run. Never throws for something that went wrong with the backup: the result says so.</summary>
    internal static FileBackupResult Run(FileBackupRun run)
    {
        var plan = run.Plan;
        var result = new FileBackupResult();
        using var log = FileBackupLog.Open(run.LogPath);
        log.Line($"Clearspace backup \"{plan.Name}\"  ({FileBackupPlan.TypeName(plan.Type)})");
        log.Line($"From   {plan.Source}");
        log.Line($"To     {plan.Root}");
        log.Line($"Start  {run.NowLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        log.Line(string.Empty);

        try
        {
            if (FileBackupRules.WhyWaiting(plan) is { } waiting)
                return End(result, log, FileBackupState.Waiting, waiting);
            if (FileBackupLayout.WhyNotRoot(plan) is { } unusable)
                return End(result, log, FileBackupState.Failed, unusable);

            var marker = OpenRoot(plan);
            switch (plan.Type)
            {
                case FileBackupType.Versions:
                    MakeVersion(run, marker, result, log);
                    break;
                case FileBackupType.Zip:
                    MakeZip(run, marker, result, log);
                    break;
                default:
                    Copy(run, result, log);
                    break;
            }

            marker.LastRunUtc = run.NowLocal.ToUniversalTime();
            FileBackupLayout.WriteMarker(plan.Root, marker);

            result.Errors += log.Problems;
            var outcome = result.Errors > 0
                ? FileBackupState.Problems
                : result.Copied + result.Removed + result.SetAside == 0 && !result.Made && !result.CopiedSome ? FileBackupState.Nothing : FileBackupState.Ok;
            return End(result, log, outcome, Summary(plan, result));
        }
        catch (OperationCanceledException)
        {
            return End(result, log, FileBackupState.Stopped, "Stopped. What was copied so far is kept, and the next run carries on from there.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException
                                          or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        {
            return End(result, log, FileBackupState.Failed, error.Message);
        }
    }

    private static FileBackupResult End(FileBackupResult result, FileBackupLog log, string outcome, string message)
    {
        result.Outcome = outcome;
        result.Message = message;
        log.Line(string.Empty);
        log.Line($"End    {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}  {outcome}");
        log.Line(message);
        return result;
    }

    /// <summary>What a finished run did, in a sentence or two for the page.</summary>
    internal static string Summary(FileBackupPlan plan, FileBackupResult result)
    {
        static string Files(long count) => count == 1 ? "1 file" : $"{count:N0} files";

        var parts = new List<string>();
        if (plan.Type == FileBackupType.Versions)
        {
            parts.Add(result.Made
                ? $"A new version was made: {Files(result.Copied)} copied ({FileSystemItem.FormatSize(result.CopiedBytes)}), {Files(result.Linked)} unchanged."
                : "Nothing has changed since the newest version, so no new one was made.");
        }
        else if (plan.Type == FileBackupType.Zip)
        {
            parts.Add(result.Made
                ? $"A new zip was made with {Files(result.Copied)} ({FileSystemItem.FormatSize(result.CopiedBytes)} before packing)."
                : "Nothing has changed since the newest zip, so no new one was made.");
        }
        else
        {
            if (result.Copied > 0)
                parts.Add($"{Files(result.Copied)} copied{(result.CopiedBytes > 0 ? $" ({FileSystemItem.FormatSize(result.CopiedBytes)})" : string.Empty)}.");
            else if (result.CopiedSome)
                parts.Add("New and changed files were copied.");
            if (result.Removed > 0)
                parts.Add($"{Files(result.Removed)} removed from the backup.");
            if (result.SetAside > 0)
                parts.Add($"{Files(result.SetAside)} kept under Earlier versions.");
            if (parts.Count == 0)
                parts.Add("Nothing had changed.");
        }

        if (result.Errors > 0)
            parts.Add($"{Files(result.Errors)} could not be copied; see the report.");
        return string.Join(" ", parts);
    }

    // Makes the plan's own folder if need be and (re)writes its marker.
    private static FileBackupMarker OpenRoot(FileBackupPlan plan)
    {
        Directory.CreateDirectory(plan.Root);
        var marker = FileBackupLayout.ReadMarker(plan.Root) ?? new FileBackupMarker { CreatedUtc = DateTime.UtcNow };
        marker.Format = FileBackupLayout.Format;
        marker.PlanId = plan.Id;
        marker.Name = plan.Name;
        marker.Source = plan.Source;
        marker.Type = plan.Type.ToString();
        FileBackupLayout.WriteMarker(plan.Root, marker);
        return marker;
    }

    private static void Report(FileBackupRun run, string phase, long files = 0, long bytes = 0, string current = "")
        => run.Progress?.Invoke(new FileBackupProgress { Phase = phase, Files = files, Bytes = bytes, Current = current });

    // ------------------------------------------------------------------ Clone, Add and update

    private static void Copy(FileBackupRun run, FileBackupResult result, FileBackupLog log)
    {
        var plan = run.Plan;
        var filter = FileBackupFilter.For(plan);
        var files = plan.FilesFolder;
        var purge = plan.Type == FileBackupType.Clone;
        Directory.CreateDirectory(files);

        // A drive that is connected but shows nothing, a folder emptied by accident: cloning that would
        // empty the backup. Better to stop and say so. (Windows' own folders at the top of a drive do not
        // count: a freshly formatted drive has them too.)
        if (purge && !plan.SourceIsFile &&
            !new DirectoryInfo(plan.Source).EnumerateFileSystemInfos().Any(entry => !filter.SkipsName(entry.Name)) &&
            Directory.EnumerateFileSystemEntries(files).Any())
        {
            throw new InvalidDataException(
                $"{plan.Source} is empty now, and a Clone would empty the backup as well, so nothing was done. " +
                "If the files are really gone and the backup should go too, delete this backup.");
        }

        var history = Path.Combine(plan.HistoryFolder, FreeStamp(plan.HistoryFolder, run.NowLocal));
        var replaced = new List<string>();
        if (plan.SetsAside)
        {
            Report(run, "Keeping earlier versions");
            SetAside(plan.Source, plan.SourceIsFile, files, history, plan.KeepVersions, purge && plan.KeepRemoved, filter, result, log, run.Token, replaced);
        }

        // What is gone from the original and was not just moved aside goes now, before the copy, so the
        // room is free and a file that became a folder (or the other way round) is out of the way.
        if (purge && !plan.SourceIsFile && !plan.KeepRemoved)
        {
            Report(run, "Removing what is gone");
            Purge(plan.Source, files, filter, result, log, run.Token);
        }

        Report(run, "Copying");
        try
        {
            if (run.UseRobocopy && FileBackupRobocopy.Find() is { } robocopy)
            {
                FileBackupRobocopy.Run(robocopy, plan, files, filter, RobocopyLogPath(run.LogPath), result, log, run.Token,
                    (count, bytes, current) => Report(run, "Copying", count, bytes, current));
            }
            else
            {
                ManagedCopy(plan, files, filter, result, log, run.Token,
                    (count, bytes, current) => Report(run, "Copying", count, bytes, current));
            }
        }
        finally
        {
            // A file whose earlier version was set aside but whose new version then was not copied (it is
            // open in a program, or the run was stopped before its turn) would be missing from the copy.
            // Its earlier version goes back until the new one can be copied.
            PutBackUncopied(files, history, replaced, result);
        }

        // Copying a whole drive hands the drive's own "hidden, system" marks to the folder it lands in.
        if (!plan.SourceIsFile && FileBackupPaths.IsDriveRoot(plan.Source))
            Unhide(files);

        result.Pruned += PruneHistory(plan, run.NowLocal, log);
    }

    private static string? RobocopyLogPath(string? logPath)
        => logPath is null ? null : Path.ChangeExtension(logPath, ".robocopy.log");

    private static void Unhide(string folder)
    {
        try
        {
            var attributes = File.GetAttributes(folder);
            if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                File.SetAttributes(folder, attributes & ~(FileAttributes.Hidden | FileAttributes.System));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Before the copy: moves out of <paramref name="files"/> every file the copy is about to replace
    /// (when <paramref name="replaced"/>) or remove because it is gone from the original (when
    /// <paramref name="removed"/>), to the same place under <paramref name="history"/>.
    /// </summary>
    /// <param name="moved">Gets the place ("Holiday\beach.jpg") of every file moved because it was about to be replaced.</param>
    internal static void SetAside(string source, bool sourceIsFile, string files, string history, bool replaced, bool removed,
        FileBackupFilter filter, FileBackupResult result, FileBackupLog log, CancellationToken token, List<string>? moved = null)
    {
        if (sourceIsFile)
        {
            var kept = new FileInfo(Path.Combine(files, Path.GetFileName(source)));
            var original = new FileInfo(source);
            if (replaced && kept.Exists && original.Exists &&
                FileBackupWalk.Differs(original.Length, FileBackupWalk.Modified(original), kept.Length, FileBackupWalk.Modified(kept)))
            {
                if (MoveAside(kept.FullName, Path.Combine(history, kept.Name), result, log))
                    moved?.Add(kept.Name);
            }

            return;
        }

        var waiting = new Stack<(string Original, string Kept, string Relative)>();
        waiting.Push((source, files, string.Empty));

        while (waiting.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (originalFolder, keptFolder, relative) = waiting.Pop();

            // What the original has here. If it cannot be listed, nothing in the backup is touched: not
            // being able to look is not the same as "gone".
            Dictionary<string, FileSystemInfo> originals;
            List<FileSystemInfo> keptEntries;
            try
            {
                originals = new Dictionary<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in new DirectoryInfo(originalFolder).EnumerateFileSystemInfos())
                    originals.TryAdd(entry.Name, entry);
                keptEntries = [.. new DirectoryInfo(keptFolder).EnumerateFileSystemInfos()];
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var kept in keptEntries)
            {
                if (filter.SkipsName(kept.Name) || kept.Name.EndsWith(FileBackupLayout.Part, StringComparison.OrdinalIgnoreCase))
                    continue;

                originals.TryGetValue(kept.Name, out var original);
                var place = relative.Length == 0 ? kept.Name : Path.Combine(relative, kept.Name);

                // "Gone" covers a name that is no longer there; "replaced" a file whose contents differ, and
                // also a file that has become a folder or a folder that has become a file.
                var gone = original is null;

                if (kept is DirectoryInfo)
                {
                    if (original is DirectoryInfo originalSub)
                    {
                        if (!FileBackupWalk.IsLink(originalSub))
                            waiting.Push((originalSub.FullName, kept.FullName, place));
                    }
                    else if (gone ? removed : replaced || removed)
                    {
                        MoveFolderAside(kept.FullName, Path.Combine(history, place), result, log);
                    }

                    continue;
                }

                if (kept is not FileInfo keptFile)
                    continue;

                if (original is FileInfo originalFile)
                {
                    if (replaced && !filter.SkipsCloudOnly(originalFile.Attributes) &&
                        FileBackupWalk.Differs(originalFile.Length, FileBackupWalk.Modified(originalFile), keptFile.Length, FileBackupWalk.Modified(keptFile)) &&
                        MoveAside(keptFile.FullName, Path.Combine(history, place), result, log))
                    {
                        moved?.Add(place);
                    }
                }
                else if (gone ? removed : replaced || removed)
                {
                    MoveAside(keptFile.FullName, Path.Combine(history, place), result, log);
                }
            }
        }
    }

    private static bool MoveAside(string from, string to, FileBackupResult result, FileBackupLog log)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to, overwrite: true);
            result.SetAside++;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.Problem(from, "could not be kept as an earlier version: " + error.Message);
            return false;
        }
    }

    /// <summary>
    /// After the copy: every file that was set aside to be replaced but is not in the copy now (its new
    /// version could not be read) gets its earlier version back, so the backup still has the file.
    /// </summary>
    internal static void PutBackUncopied(string files, string history, List<string> replaced, FileBackupResult result)
    {
        foreach (var place in replaced)
        {
            var copy = Path.Combine(files, place);
            var earlier = Path.Combine(history, place);
            try
            {
                if (File.Exists(copy) || Directory.Exists(copy) || !File.Exists(earlier))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Move(earlier, copy);
                result.SetAside--;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // it stays under Earlier versions, where it can still be found
            }
        }

        if (replaced.Count > 0)
            RemoveEmptyFolders(history);
    }

    // Takes away a dated folder (and the folders in it) that ended up with no file in it.
    private static void RemoveEmptyFolders(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return;
            foreach (var sub in Directory.GetDirectories(folder))
                RemoveEmptyFolders(sub);
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void MoveFolderAside(string from, string to, FileBackupResult result, FileBackupLog log)
    {
        try
        {
            var count = Directory.EnumerateFiles(from, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).LongCount();
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            Directory.Move(from, to);
            result.SetAside += count;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.Problem(from, "could not be kept as an earlier version: " + error.Message);
        }
    }

    /// <summary>
    /// Clearspace's own copy, for when robocopy is not there: copies into <paramref name="files"/> every
    /// file of the plan's original that is new or different there. Nothing is removed.
    /// </summary>
    internal static void ManagedCopy(FileBackupPlan plan, string files, FileBackupFilter filter, FileBackupResult result,
        FileBackupLog log, CancellationToken token, Action<long, long, string>? report)
    {
        Directory.CreateDirectory(files);
        var notes = new FileBackupWalkNotes();
        var entries = FileBackupWalk.Entries(plan, filter, notes, token, (path, error) => log.Problem(path, error.Message));

        CopyEach(FoldersMade(entries, files, replaceFiles: true, log), token, entry =>
        {
            var target = Path.Combine(files, entry.Relative);
            try
            {
                var kept = new FileInfo(target);
                if (kept.Exists && !FileBackupWalk.Differs(entry.Size, entry.ModifiedUtc, kept.Length, FileBackupWalk.Modified(kept)))
                    return;

                if (Directory.Exists(target))
                {
                    log.Problem(target, "is a folder in the backup and a file in the original; left as it is");
                    return;
                }

                CopyFile(entry, target);
                Interlocked.Increment(ref result.Copied);
                Interlocked.Add(ref result.CopiedBytes, entry.Size);
                report?.Invoke(Interlocked.Read(ref result.Copied), Interlocked.Read(ref result.CopiedBytes), entry.FullPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                log.Problem(entry.FullPath, error.Message);
            }
        });

        NoteWalk(notes, log);
    }

    // Hands on the files of a walk, having made each folder on the way. With replaceFiles, a file that sits
    // where a folder has to be is deleted first (the original turned a file into a folder).
    private static IEnumerable<FileBackupEntry> FoldersMade(IEnumerable<FileBackupEntry> entries, string target, bool replaceFiles, FileBackupLog log)
    {
        foreach (var entry in entries)
        {
            if (!entry.IsFolder)
            {
                yield return entry;
                continue;
            }

            var folder = Path.Combine(target, entry.Relative);
            try
            {
                if (replaceFiles && File.Exists(folder))
                    DeleteFile(folder);
                Directory.CreateDirectory(folder);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log.Problem(folder, error.Message);
            }
        }
    }

    // Runs `copy` for every file, a few at a time. The walk itself (and the folders it makes) stays on one thread.
    private static void CopyEach(IEnumerable<FileBackupEntry> files, CancellationToken token, Action<FileBackupEntry> copy)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = CopyThreads, CancellationToken = token };
        try
        {
            Parallel.ForEach(Partitioner.Create(files, EnumerablePartitionerOptions.NoBuffering), options, copy);
        }
        catch (AggregateException several)
        {
            // What the walk itself threw (the top folder could not be listed, or the run was stopped) arrives wrapped.
            token.ThrowIfCancellationRequested();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(several.Flatten().InnerExceptions[0]).Throw();
        }

        token.ThrowIfCancellationRequested();
    }

    private static void NoteWalk(FileBackupWalkNotes notes, FileBackupLog log)
    {
        if (notes.CloudOnly > 0)
            log.Line($"Left out: {notes.CloudOnly:N0} cloud files that are not on this PC (the backup can be set to include them).");
        if (notes.Links > 0)
            log.Line($"Left out: {notes.Links:N0} links to other folders.");
        if (notes.LeftOut > 0)
            log.Line($"Left out: {notes.LeftOut:N0} files and folders with names this backup leaves out.");
    }

    /// <summary>Copies one file under another name and renames it when it is whole, so the backup never holds half a file under the real name.</summary>
    internal static void CopyFile(FileBackupEntry entry, string target)
    {
        var partial = target + FileBackupLayout.Part;
        try
        {
            if (File.Exists(partial))
                DeleteFile(partial);
            File.Copy(entry.FullPath, partial, overwrite: true);
            KeepDate(partial, entry.ModifiedUtc);
            if (File.Exists(target))
                File.SetAttributes(target, FileAttributes.Normal); // a read-only file can't be replaced
            File.Move(partial, target, overwrite: true);
        }
        catch
        {
            try { DeleteFile(partial); }
            catch (Exception) { }
            throw;
        }
    }

    // Windows' copy keeps the modified date; this makes sure of it everywhere, because "unchanged" is decided by it.
    private static void KeepDate(string path, DateTime modifiedUtc)
    {
        if (modifiedUtc == DateTime.MinValue)
            return;

        try
        {
            if ((File.GetLastWriteTimeUtc(path) - modifiedUtc).Duration() > TimeSpan.FromMilliseconds(1))
                File.SetLastWriteTimeUtc(path, modifiedUtc);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
        }
    }

    /// <summary>
    /// Removes from the backup what the original no longer has (and what changed from a file into a
    /// folder or back). A folder of the original that cannot be listed is skipped altogether, and so are
    /// the names the plan leaves out.
    /// </summary>
    internal static void Purge(string source, string files, FileBackupFilter filter, FileBackupResult result, FileBackupLog log, CancellationToken token)
    {
        var waiting = new Stack<(string Original, string Kept)>();
        waiting.Push((source, files));

        while (waiting.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (originalFolder, keptFolder) = waiting.Pop();

            Dictionary<string, FileSystemInfo> originals;
            List<FileSystemInfo> keptEntries;
            try
            {
                originals = new Dictionary<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in new DirectoryInfo(originalFolder).EnumerateFileSystemInfos())
                    originals.TryAdd(entry.Name, entry);
                keptEntries = [.. new DirectoryInfo(keptFolder).EnumerateFileSystemInfos()];
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var kept in keptEntries)
            {
                if (filter.SkipsName(kept.Name))
                    continue;

                originals.TryGetValue(kept.Name, out var original);
                try
                {
                    if (kept is DirectoryInfo)
                    {
                        if (original is DirectoryInfo originalSub)
                        {
                            if (!FileBackupWalk.IsLink(originalSub))
                                waiting.Push((originalSub.FullName, kept.FullName));
                            continue;
                        }

                        var count = Directory.EnumerateFiles(kept.FullName, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).LongCount();
                        DeleteTree(kept.FullName);
                        result.Removed += count;
                    }
                    else if (original is not FileInfo)
                    {
                        DeleteFile(kept.FullName);
                        if (!kept.Name.EndsWith(FileBackupLayout.Part, StringComparison.OrdinalIgnoreCase))
                            result.Removed++;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    log.Problem(kept.FullName, "could not be removed from the backup: " + error.Message);
                }
            }
        }
    }

    // ------------------------------------------------------------------ Versions

    /// <summary>A plan's versions (dated folders), newest first.</summary>
    internal static List<(string Path, DateTime When)> Versions(string root)
    {
        var found = new List<(string Path, DateTime When)>();
        try
        {
            if (!Directory.Exists(root))
                return found;

            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                if (FileBackupLayout.TryParseStamp(Path.GetFileName(folder), out var when))
                    found.Add((folder, when));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        found.Sort((one, other) => other.When.CompareTo(one.When));
        return found;
    }

    private static void MakeVersion(FileBackupRun run, FileBackupMarker marker, FileBackupResult result, FileBackupLog log)
    {
        var plan = run.Plan;
        var root = plan.Root;
        var filter = FileBackupFilter.For(plan);

        // A version an earlier run did not finish.
        foreach (var leftover in Directory.EnumerateDirectories(root, "*" + FileBackupLayout.Partial))
        {
            try { DeleteTree(leftover); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log.Problem(leftover, error.Message); }
        }

        Report(run, "Looking for changes");
        var print = FileBackupWalk.Fingerprint(FileBackupWalk.Entries(plan, filter, null, run.Token));
        var versions = Versions(root);
        var previous = versions.Count > 0 ? versions[0].Path : null;
        if (previous is not null && marker.Fingerprint == print)
        {
            result.Errors += marker.Errors; // still true: those files are not in the newest version
            ThinOut(versions, plan, run.NowLocal, result, log, "version", DeleteTree); // NEW (retention): age goes on although nothing changed
            return;
        }

        var name = FreeStamp(root, run.NowLocal);
        var final = Path.Combine(root, name);
        var partial = final + FileBackupLayout.Partial;
        Directory.CreateDirectory(partial);

        var notes = new FileBackupWalkNotes();
        var entries = FileBackupWalk.Entries(plan, filter, notes, run.Token, (path, error) => log.Problem(path, error.Message));
        CopyEach(FoldersMade(entries, partial, replaceFiles: false, log), run.Token, entry =>
        {
            var target = Path.Combine(partial, entry.Relative);
            try
            {
                if (previous is not null)
                {
                    var before = new FileInfo(Path.Combine(previous, entry.Relative));
                    if (before.Exists && !FileBackupWalk.Differs(entry.Size, entry.ModifiedUtc, before.Length, FileBackupWalk.Modified(before)) &&
                        FileBackupNative.TryHardLink(target, before.FullName))
                    {
                        Interlocked.Increment(ref result.Linked);
                        return;
                    }
                }

                CopyFile(entry, target);
                Interlocked.Increment(ref result.Copied);
                Interlocked.Add(ref result.CopiedBytes, entry.Size);
                Report(run, "Copying", Interlocked.Read(ref result.Copied), Interlocked.Read(ref result.CopiedBytes), entry.FullPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                log.Problem(entry.FullPath, error.Message);
            }
        });

        NoteWalk(notes, log);
        Rename(partial, final);
        result.Made = true;
        result.MadePath = final;
        marker.Fingerprint = print;
        marker.Errors = log.Problems;

        ThinOut(Versions(root), plan, run.NowLocal, result, log, "version", DeleteTree);
    }

    // ------------------------------------------------------------------ Zip archive

    /// <summary>A plan's zips, newest first.</summary>
    internal static List<(string Path, DateTime When)> Zips(FileBackupPlan plan)
    {
        var found = new List<(string Path, DateTime When)>();
        try
        {
            if (!Directory.Exists(plan.Root))
                return found;

            foreach (var file in Directory.EnumerateFiles(plan.Root, "*.zip"))
            {
                if (FileBackupLayout.TryParseZip(Path.GetFileName(file), plan.FolderName, out var when))
                    found.Add((file, when));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        found.Sort((one, other) => other.When.CompareTo(one.When));
        return found;
    }

    // CHANGED (resumable zip): the zip is written by FileBackupZipWriter (FileBackupZip.cs), which makes
    // what has been packed safe every 30 seconds. A zip that a run before this one did not finish (the PC
    // lost power or was shut down, the run was stopped, the drive was unplugged) is carried on with
    // instead of being deleted and started again: files that are in it and have not changed since are
    // kept, the rest is packed. Before, an unfinished zip was always thrown away.
    // Also new: a file that cannot be read to its end while it is packed is noted in the report and left
    // out, as a file that cannot be opened already was, instead of failing the whole run.
    private static void MakeZip(FileBackupRun run, FileBackupMarker marker, FileBackupResult result, FileBackupLog log)
    {
        var plan = run.Plan;
        var root = plan.Root;
        var filter = FileBackupFilter.For(plan);

        Report(run, "Looking for changes");
        var print = FileBackupWalk.Fingerprint(FileBackupWalk.Entries(plan, filter, null, run.Token));
        var zips = Zips(plan);
        if (zips.Count > 0 && marker.Fingerprint == print)
        {
            ClearUnfinishedZips(root, keep: null); // the newest zip is the folder as it is: nothing to carry on with
            result.Errors += marker.Errors;
            ThinOut(zips, plan, run.NowLocal, result, log, "zip", DeleteFile); // NEW (retention)
            return;
        }

        var when = run.NowLocal;
        while (File.Exists(Path.Combine(root, FileBackupLayout.ZipName(plan.FolderName, when))))
            when = when.AddSeconds(1);
        var final = Path.Combine(root, FileBackupLayout.ZipName(plan.FolderName, when));

        // The newest unfinished zip that can be carried on with; any other is cleared away.
        FileBackupZipWriter? writer = null;
        foreach (var leftover in UnfinishedZips(root))
        {
            if ((writer = FileBackupZipWriter.Resume(leftover)) is not null)
                break;
        }

        var notes = new FileBackupWalkNotes();
        try
        {
            ClearUnfinishedZips(root, keep: writer?.Path);
            writer ??= FileBackupZipWriter.Start(final + FileBackupLayout.Part);
            if (writer.Carried > 0)
                log.Line($"Carrying on with the zip a run before this one did not finish: {writer.Carried:N0} files and folders are in it. Those that have not changed since are kept.");

            foreach (var entry in FileBackupWalk.Entries(plan, filter, notes, run.Token, (path, error) => log.Problem(path, error.Message)))
            {
                run.Token.ThrowIfCancellationRequested();
                var name = entry.Relative.Replace('\\', '/');
                if (entry.IsFolder)
                {
                    if (!writer.KeepFolder(name + "/"))
                        writer.AddFolder(name + "/"); // so an empty folder is in the zip too
                    continue;
                }

                if (!writer.Keep(name, entry.Size, entry.ModifiedUtc))
                {
                    FileStream from;
                    try
                    {
                        from = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        log.Problem(entry.FullPath, error.Message);
                        continue;
                    }

                    using (from)
                    {
                        try
                        {
                            writer.Add(name, from, entry.Size, entry.ModifiedUtc, ZipDate(entry.ModifiedUtc), run.Token);
                        }
                        catch (FileBackupUnreadableException error)
                        {
                            log.Problem(entry.FullPath, error.Message);
                            continue;
                        }
                    }
                }

                // Counted whether it was packed now or by the run that was cut short: it is in this zip.
                result.Copied++;
                result.CopiedBytes += entry.Size;
                Report(run, "Packing", result.Copied, result.CopiedBytes, entry.FullPath);
            }

            Report(run, "Finishing the zip", result.Copied, result.CopiedBytes);
            writer.Finish(final);
        }
        catch (InvalidDataException)
        {
            // The unfinished zip is not what its notes say: it cannot be carried on with. The next run starts a new one.
            var broken = writer?.Path;
            writer?.Dispose();
            if (broken is not null)
                FileBackupZipWriter.Discard(broken);
            throw;
        }
        finally
        {
            writer?.Dispose(); // stopped or failed: what was packed is saved and stays for the next run
        }

        NoteWalk(notes, log);
        result.Made = true;
        result.MadePath = final;
        marker.Fingerprint = print;
        marker.Errors = log.Problems;

        ThinOut(Zips(plan), plan, run.NowLocal, result, log, "zip", DeleteFile);
    }

    // NEW (resumable zip): the zips in a plan's folder that were never finished, newest first.
    private static List<string> UnfinishedZips(string root)
    {
        var found = new List<(string Path, DateTime Written)>();
        foreach (var file in Directory.EnumerateFiles(root, "*.zip" + FileBackupLayout.Part))
        {
            try { found.Add((file, File.GetLastWriteTimeUtc(file))); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        found.Sort((one, other) => other.Written.CompareTo(one.Written));
        return found.Select(one => one.Path).ToList();
    }

    // NEW (resumable zip): deletes every unfinished zip and what is kept beside one, except the one being carried on with.
    private static void ClearUnfinishedZips(string root, string? keep)
    {
        var spare = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (keep is not null)
        {
            spare.Add(keep);
            spare.Add(FileBackupZipWriter.DirPath(keep));
            spare.Add(FileBackupZipWriter.LogPath(keep));
        }

        foreach (var leftover in Directory.EnumerateFiles(root, "*" + FileBackupLayout.Part))
        {
            if (!spare.Contains(leftover))
                FileBackupStore.TryDelete(leftover);
        }
    }

    // A zip holds dates from 1980 to 2107, in local time.
    internal static DateTimeOffset ZipDate(DateTime modifiedUtc)
    {
        var local = modifiedUtc == DateTime.MinValue ? new DateTime(1980, 1, 1) : DateTime.SpecifyKind(modifiedUtc, DateTimeKind.Utc).ToLocalTime();
        if (local.Year < 1980)
            local = new DateTime(1980, 1, 1);
        else if (local.Year > 2107)
            local = new DateTime(2107, 12, 31, 23, 59, 58);
        return new DateTimeOffset(local);
    }

    // ------------------------------------------------------------------ keeping and deleting

    /// <summary>
    /// NEW (retention): which of a plan's versions or zips (newest first) are to be deleted. By age when the
    /// plan says so (older than MaxAgeDays), otherwise everything beyond the newest KeepCount. The newest
    /// one is never deleted, however old it is: a backup that has not run for a while still has a copy.
    /// </summary>
    internal static List<(string Path, DateTime When)> Expired(IReadOnlyList<(string Path, DateTime When)> newestFirst, FileBackupPlan plan, DateTime nowLocal)
    {
        if (plan.MaxAgeDays > 0)
            return newestFirst.Skip(1).Where(made => nowLocal - made.When > TimeSpan.FromDays(plan.MaxAgeDays)).ToList();
        return newestFirst.Skip(Math.Max(1, plan.KeepCount)).ToList();
    }

    // NEW (retention): deletes the versions or zips the plan no longer keeps (Expired) and notes each in the report.
    private static void ThinOut(IReadOnlyList<(string Path, DateTime When)> newestFirst, FileBackupPlan plan, DateTime nowLocal,
        FileBackupResult result, FileBackupLog log, string what, Action<string> delete)
    {
        foreach (var old in Expired(newestFirst, plan, nowLocal))
        {
            try
            {
                delete(old.Path);
                result.Pruned++;
                log.Line($"Deleted the old {what} {Path.GetFileName(old.Path)}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log.Line($"The old {what} {Path.GetFileName(old.Path)} could not be deleted: {error.Message}");
            }
        }
    }

    /// <summary>The dated folders under a plan's Earlier versions, newest first.</summary>
    internal static List<(string Path, DateTime When)> History(FileBackupPlan plan) => Versions(plan.HistoryFolder);

    /// <summary>Deletes the Earlier versions folders that are older than the plan keeps them. Returns how many went.</summary>
    internal static int PruneHistory(FileBackupPlan plan, DateTime nowLocal, FileBackupLog? log = null)
    {
        if (plan.KeepDays <= 0)
            return 0;

        var gone = 0;
        foreach (var old in History(plan).Where(set => nowLocal - set.When > TimeSpan.FromDays(plan.KeepDays)))
        {
            try
            {
                DeleteTree(old.Path);
                gone++;
                log?.Line("Deleted the earlier versions of " + Path.GetFileName(old.Path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log?.Line($"The earlier versions of {Path.GetFileName(old.Path)} could not be deleted: {error.Message}");
            }
        }

        return gone;
    }

    // Gives a finished version its real name. Another program may be looking into the folder at that
    // moment (a virus scanner, the search indexer), which makes Windows refuse; a moment later it works.
    private static void Rename(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(600 * attempt);
            }
        }
    }

    // A dated name that is not taken in `folder` yet (two runs in the same second get different names).
    private static string FreeStamp(string folder, DateTime nowLocal)
    {
        var when = nowLocal;
        while (Directory.Exists(Path.Combine(folder, FileBackupLayout.Stamp(when))) ||
               Directory.Exists(Path.Combine(folder, FileBackupLayout.Stamp(when) + FileBackupLayout.Partial)))
            when = when.AddSeconds(1);
        return FileBackupLayout.Stamp(when);
    }

    internal static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
            File.SetAttributes(path, FileAttributes.Normal); // read-only
            File.Delete(path);
        }
    }

    /// <summary>Deletes a folder and everything in it, read-only files included.</summary>
    internal static void DeleteTree(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
            return;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!Directory.Exists(folder))
                return;
        }

        var all = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", all))
        {
            try
            {
                if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                    entry.Attributes &= ~FileAttributes.ReadOnly;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }

        Directory.Delete(folder, recursive: true);
    }
}

/// <summary>The one thing .NET has no call for: making a second name for a file that is already on the drive.</summary>
internal static class FileBackupNative
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkUnix([MarshalAs(UnmanagedType.LPUTF8Str)] string existing, [MarshalAs(UnmanagedType.LPUTF8Str)] string newName);

    /// <summary>For tests: answers in place of the drive (a drive that has no hard links).</summary>
    internal static Func<string, string, bool>? Override { get; set; }

    /// <summary>
    /// Makes <paramref name="newName"/> a second name for the file <paramref name="existing"/>. False when
    /// the drive cannot do that (FAT, exFAT, some network folders) or the file already has as many names
    /// as Windows allows; the caller then copies instead.
    /// </summary>
    internal static bool TryHardLink(string newName, string existing)
    {
        try
        {
            if (Override is { } stand)
                return stand(newName, existing);

            return OperatingSystem.IsWindows()
                ? CreateHardLinkW(LongPath(newName), LongPath(existing), IntPtr.Zero)
                : LinkUnix(existing, newName) == 0; // only where the tests run
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or IOException)
        {
            return false;
        }
    }

    // CreateHardLinkW stops at 260 characters unless the path is written the long way.
    private static string LongPath(string path)
    {
        if (path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }
}
