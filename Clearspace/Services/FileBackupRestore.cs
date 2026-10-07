// Clearspace | NEW (file backup): getting things back out of a backup.
// Two ways, both read-only as far as the backup goes:
//
//   A whole backup, or one moment of it ("Restore…" on the Backup and restore page)
//     Points lists what a plan can be restored from: the copy as it is now and each dated folder under
//     Earlier versions (Clone, Add and update), each version (Versions), each zip (Zip archive).
//     Restore copies one of them into a folder: where the files came from, or another one. It only
//     ever adds: a file that is already there and the same is passed over; one that is there and
//     different is replaced only when asked to, and otherwise left. Nothing is deleted from the folder.
//
//   One file ("Earlier versions…" in a file's right-click menu, when a backup keeps file history)
//     VersionsOf looks in every backup that covers the file and lists each state of it they hold, newest
//     first: one line per different state (same size and date count as the same), without the state the
//     file is in now. RestoreVersion writes one of them to a path.
//
// A zip's entry names are text from a file: an entry that would land outside the folder being restored
// to ("..\..\Windows\x") is refused.
// Tests: Clearspace.Tests/FileBackupTests.cs.
using System.IO;
using System.IO.Compression;

namespace Clearspace.Services;

internal enum FileBackupPointKind { Latest, Version, Zip, History }

/// <summary>Something a plan can be restored from.</summary>
/// <param name="Path">A folder (Latest, Version, History) or a .zip file.</param>
/// <param name="When">When it was made, local time.</param>
internal sealed record FileBackupPoint(FileBackupPointKind Kind, string Path, DateTime When);

/// <summary>One state of one file, as a backup holds it.</summary>
/// <param name="StoredPath">The file in the backup, or the .zip it is in.</param>
/// <param name="ZipEntry">Its name inside the zip; null when it is a plain file.</param>
/// <param name="SavedLocal">When the backup took it (the run), local time.</param>
internal sealed record FileBackupVersion(string PlanName, FileBackupType Type, FileBackupPointKind Kind, string StoredPath,
    string? ZipEntry, long Size, DateTime ModifiedUtc, DateTime SavedLocal);

internal sealed class FileBackupRestoreResult
{
    public long Restored;      // files written
    public long Same;          // already there and the same
    public long Left;          // there and different, and not replaced
    public long Errors;
    public List<string> Problems { get; } = [];

    internal void Problem(string path, string what)
    {
        Errors++;
        if (Problems.Count < 50)
            Problems.Add($"{path}: {what}");
    }
}

internal static class FileBackupRestore
{
    // Reading a backup back leaves nothing out: whatever is in it was meant to be.
    private static readonly FileBackupFilter Everything = new([], includeCloudOnly: true);

    /// <summary>The folder a plan's files came from (for a plan of one file, the folder that file was in).</summary>
    internal static string OriginalFolder(FileBackupPlan plan)
        => plan.SourceIsFile ? Path.GetDirectoryName(FileBackupPaths.Normalize(plan.Source)) ?? plan.Source : plan.Source;

    /// <summary>What a plan can be restored from, newest first (the copy as it is now leads). Looks at the disk.</summary>
    internal static List<FileBackupPoint> Points(FileBackupPlan plan)
    {
        var points = new List<FileBackupPoint>();
        try
        {
            if (plan.Type == FileBackupType.Versions)
            {
                points.AddRange(FileBackupEngine.Versions(plan.Root).Select(version => new FileBackupPoint(FileBackupPointKind.Version, version.Path, version.When)));
            }
            else if (plan.Type == FileBackupType.Zip)
            {
                points.AddRange(FileBackupEngine.Zips(plan).Select(zip => new FileBackupPoint(FileBackupPointKind.Zip, zip.Path, zip.When)));
            }
            else
            {
                if (Directory.Exists(plan.FilesFolder))
                {
                    var when = FileBackupLayout.ReadMarker(plan.Root)?.LastRunUtc is { } last
                        ? DateTime.SpecifyKind(last, DateTimeKind.Utc).ToLocalTime()
                        : Directory.GetLastWriteTime(plan.FilesFolder);
                    points.Add(new FileBackupPoint(FileBackupPointKind.Latest, plan.FilesFolder, when));
                }

                points.AddRange(FileBackupEngine.History(plan).Select(set => new FileBackupPoint(FileBackupPointKind.History, set.Path, set.When)));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        return points;
    }

    /// <summary>
    /// Copies what is in <paramref name="point"/> into <paramref name="target"/>. Files that are already
    /// there and the same are passed over; files that are there and different are replaced only with
    /// <paramref name="replace"/>. Nothing is ever deleted from the target.
    /// </summary>
    internal static FileBackupRestoreResult Restore(FileBackupPlan plan, FileBackupPoint point, string target, bool replace,
        Action<long, string>? report, CancellationToken token)
    {
        if (FileBackupPaths.IsInside(target, plan.Root))
            throw new InvalidDataException("A backup can't be restored into its own folder. Choose another folder.");

        var result = new FileBackupRestoreResult();
        Directory.CreateDirectory(target);
        if (point.Kind == FileBackupPointKind.Zip)
            RestoreZip(point.Path, target, replace, result, report, token);
        else
            RestoreFolder(point.Path, target, replace, result, report, token);
        return result;
    }

    private static void RestoreFolder(string from, string target, bool replace, FileBackupRestoreResult result,
        Action<long, string>? report, CancellationToken token)
    {
        foreach (var entry in FileBackupWalk.Entries(from, Everything, null, token, (path, error) => result.Problem(path, error.Message)))
        {
            var to = Path.Combine(target, entry.Relative);
            try
            {
                if (entry.IsFolder)
                {
                    if (!File.Exists(to))
                        Directory.CreateDirectory(to);
                    continue;
                }

                if (entry.Relative.EndsWith(FileBackupLayout.Part, StringComparison.OrdinalIgnoreCase))
                    continue; // a file a stopped run left half written

                if (Directory.Exists(to))
                {
                    result.Problem(to, "is a folder here and a file in the backup; left as it is");
                    continue;
                }

                var there = new FileInfo(to);
                if (there.Exists)
                {
                    if (!FileBackupWalk.Differs(entry.Size, entry.ModifiedUtc, there.Length, FileBackupWalk.Modified(there)))
                    {
                        result.Same++;
                        continue;
                    }

                    if (!replace)
                    {
                        result.Left++;
                        continue;
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                FileBackupEngine.CopyFile(entry, to);
                result.Restored++;
                report?.Invoke(result.Restored, to);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                result.Problem(to, error.Message);
            }
        }
    }

    private static void RestoreZip(string zipPath, string target, bool replace, FileBackupRestoreResult result,
        Action<long, string>? report, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (Inside(target, entry.FullName) is not { } to)
            {
                result.Problem(entry.FullName, "would land outside the folder; left out");
                continue;
            }

            try
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    if (!File.Exists(to))
                        Directory.CreateDirectory(to);
                    continue;
                }

                if (Directory.Exists(to))
                {
                    result.Problem(to, "is a folder here and a file in the backup; left as it is");
                    continue;
                }

                var modified = entry.LastWriteTime.UtcDateTime;
                var there = new FileInfo(to);
                if (there.Exists)
                {
                    if (!FileBackupWalk.Differs(entry.Length, modified, there.Length, FileBackupWalk.Modified(there)))
                    {
                        result.Same++;
                        continue;
                    }

                    if (!replace)
                    {
                        result.Left++;
                        continue;
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Extract(entry, to);
                result.Restored++;
                report?.Invoke(result.Restored, to);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException)
            {
                result.Problem(to, error.Message);
            }
        }
    }

    /// <summary>Where a zip entry lands under <paramref name="folder"/>, or null when its name would take it outside.</summary>
    internal static string? Inside(string folder, string entryName)
    {
        var name = entryName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        if (name.Length == 0 || Path.IsPathRooted(name) || name.Contains(':'))
            return null;

        try
        {
            var full = Path.GetFullPath(Path.Combine(folder, name));
            return FileBackupPaths.IsInside(full, folder) && !FileBackupPaths.Same(full, folder) ? full : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // Unpacks one entry under another name and renames it when it is whole.
    private static void Extract(ZipArchiveEntry entry, string to)
    {
        var partial = to + FileBackupLayout.Part;
        try
        {
            using (var from = entry.Open())
            using (var into = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                from.CopyTo(into, 1 << 16);

            try { File.SetLastWriteTimeUtc(partial, entry.LastWriteTime.UtcDateTime); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { }

            if (File.Exists(to))
                File.SetAttributes(to, FileAttributes.Normal);
            File.Move(partial, to, overwrite: true);
        }
        catch
        {
            FileBackupStore.TryDelete(partial);
            throw;
        }
    }

    // ------------------------------------------------------------------ one file

    /// <summary>Whether "Earlier versions…" is offered for a file: a backup that keeps file history covers it. Reads no disk.</summary>
    internal static bool Offers(string path, IEnumerable<FileBackupPlan> plans)
        => plans.Any(plan => plan.HasFileHistory && plan.RelativeOf(path) is not null);

    /// <summary>
    /// Every state of the file at <paramref name="path"/> that the backups hold and that differs from the
    /// file as it is now, newest first. Looks at the disk (and opens zips), so call it off the UI thread.
    /// </summary>
    internal static List<FileBackupVersion> VersionsOf(string path, IEnumerable<FileBackupPlan> plans, CancellationToken token = default)
    {
        var found = new List<FileBackupVersion>();
        foreach (var plan in plans)
        {
            token.ThrowIfCancellationRequested();
            if (plan.RelativeOf(path) is not { } relative)
                continue;

            try
            {
                foreach (var point in Points(plan))
                {
                    token.ThrowIfCancellationRequested();
                    if (point.Kind == FileBackupPointKind.Zip)
                    {
                        using var zip = ZipFile.OpenRead(point.Path);
                        if (zip.GetEntry(relative.Replace('\\', '/')) is { } entry)
                        {
                            found.Add(new FileBackupVersion(plan.Name, plan.Type, point.Kind, point.Path, entry.FullName,
                                entry.Length, entry.LastWriteTime.UtcDateTime, point.When));
                        }

                        continue;
                    }

                    var stored = new FileInfo(Path.Combine(point.Path, relative));
                    if (stored.Exists)
                    {
                        found.Add(new FileBackupVersion(plan.Name, plan.Type, point.Kind, stored.FullName, null,
                            stored.Length, FileBackupWalk.Modified(stored), point.When));
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // a backup that can't be read right now (its drive is not connected) has nothing to offer
            }
        }

        // The file as it is now is not an earlier version of itself.
        var now = new FileInfo(path);
        var states = new List<FileBackupVersion>();
        if (now.Exists)
            states.Add(new FileBackupVersion(string.Empty, FileBackupType.Clone, FileBackupPointKind.Latest, path, null, now.Length, FileBackupWalk.Modified(now), DateTime.Now));
        var skip = states.Count;

        // One line per state. Of several copies of the same state the first found stays (plans in their
        // order, each newest first).
        foreach (var version in found)
        {
            if (!states.Any(state => !FileBackupWalk.Differs(state.Size, state.ModifiedUtc, version.Size, version.ModifiedUtc)))
                states.Add(version);
        }

        return states.Skip(skip).OrderByDescending(version => version.ModifiedUtc).ToList();
    }

    /// <summary>Writes one state of a file to <paramref name="target"/> (replacing what is there).</summary>
    internal static void RestoreVersion(FileBackupVersion version, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (version.ZipEntry is null)
        {
            FileBackupEngine.CopyFile(new FileBackupEntry(Path.GetFileName(target), version.StoredPath, IsFolder: false, version.Size, version.ModifiedUtc), target);
            return;
        }

        using var zip = ZipFile.OpenRead(version.StoredPath);
        var entry = zip.GetEntry(version.ZipEntry) ?? throw new FileNotFoundException("That file is no longer in the zip.", version.StoredPath);
        Extract(entry, target);
    }

    /// <summary>"report (from 3 Oct 2026 14.30).docx": a name for a copy of an earlier version beside the file, not taken yet.</summary>
    internal static string CopyName(string path, DateTime modifiedLocal, Func<string, bool>? exists = null)
    {
        exists ??= candidate => File.Exists(candidate) || Directory.Exists(candidate);
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ending = Path.GetExtension(path);
        var date = modifiedLocal.ToString("d MMM yyyy HH.mm", System.Globalization.CultureInfo.InvariantCulture);

        var candidate = Path.Combine(folder, $"{name} (from {date}){ending}");
        for (var number = 2; exists(candidate) && number < 1000; number++)
            candidate = Path.Combine(folder, $"{name} (from {date}) ({number}){ending}");
        return candidate;
    }
}
