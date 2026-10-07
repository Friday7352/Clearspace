// Clearspace | NEW (file backup): what one backup of files is, and where everything about it is kept.
// Settings > "Backup and restore…" (FileBackupView.cs) lists backups. Each one is a FileBackupPlan: what to
// copy (a file, a folder or a whole drive), where to, how (FileBackupType), how often (FileBackupSchedule)
// and what to keep. This is separate from "Settings and tags backup" (BackupService.cs), which backs up
// what Clearspace itself keeps.
//
// The four kinds:
//     Clone           the backup is made the same as the original: new and changed files are copied, and
//                     files that are gone from the original are removed from the backup. Windows' own
//                     robocopy does the copying (FileBackupRobocopy.cs).
//     Add and update  the same, but nothing is ever removed from the backup.
//     Versions        every run makes a dated folder that looks like a full copy. A file that has not
//                     changed since the run before is the same file on the disk in both (a hard link), so
//                     only what changed takes up room.
//     Zip archive     every run makes one dated .zip.
//
// Where a backup lives: the person picks a folder (Destination); Clearspace makes a folder of its own in
// it (FolderName) and never writes, removes or thins out anything outside that folder. The folder holds
// "Clearspace backup.json" (FileBackupMarker), which is how Clearspace knows the folder is its own:
//     Clone, Add and update   Files\                      the copy
//                             Earlier versions\<date>\    files a run replaced or removed, when kept
//     Versions                <date>\                     one folder per run
//     Zip archive             <name> <date>.zip           one file per run
//
// What is kept on this PC (FileBackupStore), under %APPDATA%\Clearspace\File backups:
//     backups.json            the plans and "run when Clearspace is closed"
//     state\<id>.json         how the last run of a plan went (FileBackupState)
//     state\<id>.progress.json, .lock, .cancel    a run in progress (FileBackupRunner.cs)
//     logs\<id>\<date>.log    the report of each run
// They are files of their own, not part of settings.json, because a backup is run by a second Clearspace
// process (so it carries on when the window is closed) and settings.json belongs to the window's process.
//
// Nothing in this file opens a window, and the rules (when a backup is due, which folders may be used,
// what a name may be) take what they need as arguments. Tests: Clearspace.Tests/FileBackupTests.cs.
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clearspace.Services;

internal enum FileBackupType { Clone, AddAndUpdate, Versions, Zip }

/// <summary>How often a backup runs by itself.</summary>
internal sealed class FileBackupSchedule
{
    internal const string Off = "Off";
    internal const string Hourly = "Hourly";
    internal const string Daily = "Daily";
    internal const string Weekly = "Weekly";
    internal const string Custom = "Custom";

    internal const int LongestHours = 24 * 365;
    internal static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    public string Kind { get; set; } = Off;

    /// <summary>Daily and Weekly: the time of day, in minutes after midnight (local time).</summary>
    public int AtMinute { get; set; } = 12 * 60;

    /// <summary>Weekly: the days, one bit per <see cref="DayOfWeek"/> (Sunday is 1, Monday 2 ...).</summary>
    public int Days { get; set; } = 1 << (int)DayOfWeek.Monday;

    /// <summary>Custom: every so many hours.</summary>
    public int EveryHours { get; set; } = 6;

    [JsonIgnore]
    public bool IsOn => Kind is Hourly or Daily or Custom || (Kind == Weekly && (Days & 0x7F) != 0);

    internal FileBackupSchedule Copy() => new() { Kind = Kind, AtMinute = AtMinute, Days = Days, EveryHours = EveryHours };

    internal bool Runs(DayOfWeek day) => (Days & (1 << (int)day)) != 0;

    private TimeSpan TimeOfDay => TimeSpan.FromMinutes(Math.Clamp(AtMinute, 0, 24 * 60 - 1));

    private TimeSpan? Interval => Kind switch
    {
        Hourly => TimeSpan.FromHours(1),
        Custom => TimeSpan.FromHours(Math.Clamp(EveryHours, 1, LongestHours)),
        _ => null
    };

    /// <summary>Daily and Weekly: the latest moment at or before <paramref name="nowLocal"/> the backup was meant to start.</summary>
    internal DateTime? LatestOccurrence(DateTime nowLocal)
    {
        if (Kind is not (Daily or Weekly))
            return null;

        for (var back = 0; back <= 7; back++)
        {
            var day = nowLocal.Date.AddDays(-back);
            if (Kind == Weekly && !Runs(day.DayOfWeek))
                continue;

            var at = day + TimeOfDay;
            if (at <= nowLocal)
                return at;
        }

        return null;
    }

    /// <summary>Daily and Weekly: the first moment after <paramref name="nowLocal"/> the backup is meant to start.</summary>
    internal DateTime? NextOccurrence(DateTime nowLocal)
    {
        if (Kind is not (Daily or Weekly))
            return null;

        for (var ahead = 0; ahead <= 7; ahead++)
        {
            var day = nowLocal.Date.AddDays(ahead);
            if (Kind == Weekly && !Runs(day.DayOfWeek))
                continue;

            var at = day + TimeOfDay;
            if (at > nowLocal)
                return at;
        }

        return null;
    }

    /// <summary>
    /// Whether the backup should run now. <paramref name="lastRunLocal"/> is when its last run started
    /// (whatever came of it), null when it never ran.
    ///  - A backup that never ran is due at once, so a new one is protected straight away.
    ///  - Every hour / Custom: once that long has passed.
    ///  - Every day / Every week: once the set time has come since the last run. A PC that was off at
    ///    that time catches up when it is next on.
    ///  - A run that failed is tried again after an hour, not every minute.
    ///  - A last run in the future (the clock was set back) counts as due.
    /// </summary>
    internal bool IsDue(DateTime? lastRunLocal, bool lastFailed, DateTime nowLocal)
    {
        if (!IsOn)
            return false;
        if (lastRunLocal is not { } last || last > nowLocal)
            return true;
        if (lastFailed && nowLocal - last >= RetryAfter)
            return true;
        if (Interval is { } every)
            return nowLocal - last >= every;

        return LatestOccurrence(nowLocal) is { } meant && meant > last;
    }

    /// <summary>When the next run is expected, for the page; null when the schedule is off. A time that has passed means "now".</summary>
    internal DateTime? NextRun(DateTime? lastRunLocal, bool lastFailed, DateTime nowLocal)
    {
        if (!IsOn)
            return null;
        if (IsDue(lastRunLocal, lastFailed, nowLocal) || lastRunLocal is not { } last)
            return nowLocal;

        var next = Interval is { } every ? last + every : NextOccurrence(nowLocal);
        if (lastFailed && (next is null || last + RetryAfter < next))
            next = last + RetryAfter;
        return next;
    }

    /// <summary>"Only when you ask", "Every day at 12:00", "Every Monday and Thursday at 18:30", "Every 6 hours".</summary>
    internal string Describe(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var time = (DateTime.MinValue + TimeOfDay).ToString("t", culture);
        switch (Kind)
        {
            case Hourly:
                return "Every hour";
            case Daily:
                return "Every day at " + time;
            case Weekly when IsOn:
                var names = Enumerable.Range(1, 7) // Monday first
                    .Select(index => (DayOfWeek)(index % 7))
                    .Where(Runs)
                    .Select(day => culture.DateTimeFormat.GetDayName(day))
                    .ToList();
                if (names.Count == 7)
                    return "Every day at " + time;
                var days = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
                return $"Every {days} at {time}";
            case Custom:
                var hours = Math.Clamp(EveryHours, 1, LongestHours);
                if (hours % 24 == 0)
                    return hours == 24 ? "Every day" : $"Every {hours / 24:N0} days";
                return hours == 1 ? "Every hour" : $"Every {hours:N0} hours";
            default:
                return "Only when you ask";
        }
    }
}

/// <summary>One backup: what, where to, how, how often, and what to keep.</summary>
internal sealed class FileBackupPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;

    /// <summary>The file, folder or drive that is backed up.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Kept, so the plan still reads right while the source is out of reach (a drive that is not connected).</summary>
    public bool SourceIsFile { get; set; }

    /// <summary>The folder the person chose. The backup's own folder (<see cref="FolderName"/>) is made inside it.</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>The name of the backup's own folder. Set once, so renaming the backup does not lose its copies.</summary>
    public string FolderName { get; set; } = string.Empty;

    public FileBackupType Type { get; set; } = FileBackupType.Clone;
    public FileBackupSchedule Schedule { get; set; } = new();

    /// <summary>Clone, Add and update: a file a run is about to replace is first put under Earlier versions (file history).</summary>
    public bool KeepVersions { get; set; }

    /// <summary>Clone: a file a run is about to remove is first put under Earlier versions (the safety net).</summary>
    public bool KeepRemoved { get; set; } = true;

    /// <summary>How long what is under Earlier versions is kept, in days. 0 keeps it until the person deletes it.</summary>
    public int KeepDays { get; set; } = 30;

    /// <summary>Versions, Zip archive: how many runs are kept. Older ones are deleted.</summary>
    public int KeepCount { get; set; } = 10;

    /// <summary>NEW (retention): Versions, Zip archive: when above 0, runs are kept by age instead of by
    /// number: one older than this many days is deleted. The newest always stays.</summary>
    public int MaxAgeDays { get; set; }

    /// <summary>Names left out, files and folders alike: "node_modules", "*.tmp".</summary>
    public List<string> Exclude { get; set; } = [];

    /// <summary>Cloud files that are not on this PC are left out unless this is on (backing them up downloads them).</summary>
    public bool IncludeCloudOnly { get; set; }

    public DateTime CreatedUtc { get; set; }

    [JsonIgnore]
    public string Root => Path.Combine(Destination, FolderName);

    [JsonIgnore]
    public string FilesFolder => Path.Combine(Root, FileBackupLayout.Files);

    [JsonIgnore]
    public string HistoryFolder => Path.Combine(Root, FileBackupLayout.History);

    [JsonIgnore]
    public bool IsCopy => Type is FileBackupType.Clone or FileBackupType.AddAndUpdate;

    /// <summary>Whether runs put anything under Earlier versions.</summary>
    [JsonIgnore]
    public bool SetsAside => IsCopy && (KeepVersions || (Type == FileBackupType.Clone && KeepRemoved));

    /// <summary>Whether an older state of a file that changed can be had back from this backup ("Earlier versions…").</summary>
    [JsonIgnore]
    public bool HasFileHistory => Type is FileBackupType.Versions or FileBackupType.Zip || (IsCopy && KeepVersions);

    internal FileBackupPlan Copy() => new()
    {
        Id = Id,
        Name = Name,
        Source = Source,
        SourceIsFile = SourceIsFile,
        Destination = Destination,
        FolderName = FolderName,
        Type = Type,
        Schedule = Schedule.Copy(),
        KeepVersions = KeepVersions,
        KeepRemoved = KeepRemoved,
        KeepDays = KeepDays,
        KeepCount = KeepCount,
        MaxAgeDays = MaxAgeDays,
        Exclude = [.. Exclude],
        IncludeCloudOnly = IncludeCloudOnly,
        CreatedUtc = CreatedUtc
    };

    /// <summary>
    /// Where <paramref name="path"/> sits inside what this plan backs up ("Holiday\beach.jpg"), or null
    /// when the plan does not cover it. For a plan of a single file that is the file's name.
    /// </summary>
    internal string? RelativeOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(Source))
            return null;

        if (SourceIsFile)
            return FileBackupPaths.Same(path, Source) ? Path.GetFileName(Source) : null;

        if (!FileBackupPaths.IsInside(path, Source) || FileBackupPaths.Same(path, Source))
            return null;

        var relative = FileBackupPaths.Relative(path, Source);
        var filter = FileBackupFilter.For(this);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(filter.SkipsName) ? null : relative;
    }

    /// <summary>"Clone", "Add and update", "Versions", "Zip archive".</summary>
    internal static string TypeName(FileBackupType type) => type switch
    {
        FileBackupType.Clone => "Clone",
        FileBackupType.AddAndUpdate => "Add and update",
        FileBackupType.Versions => "Versions",
        _ => "Zip archive"
    };

    /// <summary>CHANGED (simpler editor): the few words under the kind that is chosen. TypeHint is its tooltip.</summary>
    internal static string TypeShort(FileBackupType type) => type switch
    {
        FileBackupType.Clone => "An exact copy. Files you delete are removed from it.",
        FileBackupType.AddAndUpdate => "Like Clone, but nothing is ever removed.",
        FileBackupType.Versions => "A dated copy per run. Unchanged files take no extra space.",
        _ => "One .zip file per run."
    };

    /// <summary>One sentence saying what a kind of backup does, for the editor.</summary>
    internal static string TypeHint(FileBackupType type) => type switch
    {
        FileBackupType.Clone => "A 1-to-1 copy. New and changed files are copied; files you delete are removed from the backup too. The fastest for large folders and drives.",
        FileBackupType.AddAndUpdate => "Like Clone, but nothing is ever removed from the backup. A file you delete by mistake is still there.",
        FileBackupType.Versions => "Every run makes a dated copy you can go back to. Files that did not change take up no extra room.",
        _ => "Every run makes one dated .zip file. Smaller, slower; good for small folders and for sending somewhere."
    };
}

/// <summary>Everything Clearspace remembers about file backups on this PC (backups.json).</summary>
internal sealed class FileBackupSettings
{
    /// <summary>Scheduled backups also run while Clearspace is closed, through an entry in Windows' Task Scheduler.</summary>
    public bool RunWhenClosed { get; set; } = true;

    public List<FileBackupPlan> Plans { get; set; } = [];

    [JsonIgnore]
    public bool AnyScheduled => Plans.Any(plan => plan.Schedule.IsOn);
}

/// <summary>How the last run of a plan went (state\&lt;id&gt;.json).</summary>
internal sealed class FileBackupState
{
    internal const string Ok = "Ok";               // finished, something was copied or removed
    internal const string Nothing = "Nothing";     // finished, nothing had changed
    internal const string Problems = "Problems";   // finished, but some files could not be copied
    internal const string Failed = "Failed";       // could not be done at all
    internal const string Stopped = "Stopped";     // stopped by the person
    internal const string Waiting = "Waiting";     // not started: the backup's drive or the original is not connected

    public string Outcome { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    /// <summary>When the last run started. A run that only waited (<see cref="Waiting"/>) does not count.</summary>
    public DateTime? LastRunUtc { get; set; }
    public DateTime? LastEndUtc { get; set; }

    /// <summary>When a run last finished without failing or being stopped.</summary>
    public DateTime? LastGoodUtc { get; set; }

    public long Copied { get; set; }
    public long CopiedBytes { get; set; }
    public long Removed { get; set; }
    public long SetAside { get; set; }
    public long Errors { get; set; }
    public string? LogPath { get; set; }

    [JsonIgnore]
    public bool HasRun => LastRunUtc is not null;

    [JsonIgnore]
    public bool LastFailed => Outcome == Failed;
}

/// <summary>How far a run in progress is (state\&lt;id&gt;.progress.json), written by the process that runs it.</summary>
internal sealed class FileBackupProgress
{
    public string Phase { get; set; } = string.Empty;
    public long Files { get; set; }
    public long Bytes { get; set; }
    public string Current { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
}

/// <summary>"Clearspace backup.json" in a backup's own folder: says the folder is Clearspace's, and whose.</summary>
internal sealed class FileBackupMarker
{
    public int Format { get; set; } = FileBackupLayout.Format;
    public string PlanId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastRunUtc { get; set; }

    /// <summary>Versions, Zip archive: a fingerprint of what the newest run copied, so a run with nothing new makes nothing.</summary>
    public string? Fingerprint { get; set; }

    /// <summary>How many files the run behind <see cref="Fingerprint"/> could not copy.</summary>
    public long Errors { get; set; }
}

/// <summary>The names inside a backup's own folder, and its dated names.</summary>
internal static class FileBackupLayout
{
    internal const int Format = 1;
    internal const string Marker = "Clearspace backup.json";
    internal const string Files = "Files";
    internal const string History = "Earlier versions";
    internal const string Partial = ".partial";     // a Versions folder that is still being made
    internal const string Part = ".cspart";         // a file that is still being written

    private const string StampFormat = "yyyy-MM-dd HH.mm.ss";

    /// <summary>"2026-10-06 16.30.05": the name of a dated folder, sortable and allowed in a file name.</summary>
    internal static string Stamp(DateTime local) => local.ToString(StampFormat, CultureInfo.InvariantCulture);

    internal static bool TryParseStamp(string name, out DateTime local)
    {
        var read = DateTime.TryParseExact(name, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out local);
        local = DateTime.SpecifyKind(local, DateTimeKind.Local); // so it is never taken for a UTC time when shown
        return read;
    }

    /// <summary>"Photos 2026-10-06 16.30.05.zip"</summary>
    internal static string ZipName(string folderName, DateTime local) => $"{folderName} {Stamp(local)}.zip";

    /// <summary>The date in a zip's name, when it is one of this backup's.</summary>
    internal static bool TryParseZip(string fileName, string folderName, out DateTime local)
    {
        local = default;
        var prefix = folderName + " ";
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return TryParseStamp(fileName[prefix.Length..^4], out local);
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static FileBackupMarker? ReadMarker(string root)
    {
        try
        {
            var path = Path.Combine(root, Marker);
            return File.Exists(path) ? JsonSerializer.Deserialize<FileBackupMarker>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static void WriteMarker(string root, FileBackupMarker marker)
        => FileBackupStore.WriteWhole(Path.Combine(root, Marker), JsonSerializer.Serialize(marker, Json));

    /// <summary>
    /// Why a plan's own folder cannot be used, in words; null when it can. It can when it is not there
    /// yet, is empty, or carries this plan's marker (or the marker of a lost plan for the same source
    /// and kind, which is then taken over). A folder with anything else in it is never written to:
    /// Clone removes what is not in the original, and that must only ever happen in a folder Clearspace made.
    /// </summary>
    internal static string? WhyNotRoot(FileBackupPlan plan)
    {
        var root = plan.Root;
        try
        {
            if (File.Exists(root))
                return $"{root} is a file. Choose another place for this backup.";
            if (!Directory.Exists(root))
                return null;

            if (ReadMarker(root) is { } marker)
            {
                if (marker.Format > Format)
                    return $"The backup in {root} was made by a newer Clearspace. Update Clearspace to use it.";
                if (marker.PlanId == plan.Id)
                    return null;
                if (FileBackupPaths.Same(marker.Source, plan.Source) && marker.Type == plan.Type.ToString())
                    return null; // the same backup, set up again (after a reinstall): carried on
                return $"{root} already holds another backup (\"{marker.Name}\" of {marker.Source}). Choose another place, or give this backup another name.";
            }

            return Directory.EnumerateFileSystemEntries(root).Any()
                ? $"{root} already has other things in it. A backup needs a folder of its own; choose another place, or give this backup another name."
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{root} could not be looked at: {error.Message}";
        }
    }
}

/// <summary>Comparing and naming paths. Windows' rules (capitals do not matter) are used everywhere.</summary>
internal static class FileBackupPaths
{
    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>A path as it is compared and kept: full, with no separator at the end unless it is a drive ("D:\").</summary>
    internal static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            var full = Path.GetFullPath(path.Trim());
            var root = Path.GetPathRoot(full) ?? string.Empty;
            return full.Length > root.Length ? full.TrimEnd(Separators) : full;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return path.Trim();
        }
    }

    internal static bool Same(string one, string other)
        => string.Equals(Normalize(one), Normalize(other), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or somewhere under it.</summary>
    internal static bool IsInside(string path, string folder)
    {
        var child = Normalize(path);
        var parent = Normalize(folder);
        if (child.Length == 0 || parent.Length == 0)
            return false;
        if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) || parent.EndsWith(Path.AltDirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The part of <paramref name="path"/> below <paramref name="folder"/> (which it must be inside).</summary>
    internal static string Relative(string path, string folder)
    {
        var child = Normalize(path);
        var parent = Normalize(folder);
        return child.Length <= parent.Length ? string.Empty : child[parent.Length..].TrimStart(Separators);
    }

    internal static bool IsDriveRoot(string path)
    {
        var full = Normalize(path);
        return full.Length > 0 && string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// NEW (backup contents): whether a backup kept in <paramref name="destination"/> would be on the same
    /// drive as what it backs up, and so be lost with it. False while either is not a full path yet.
    /// </summary>
    internal static bool OnSameDrive(string? source, string? destination)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination) ||
                !Path.IsPathFullyQualified(source.Trim()) || !Path.IsPathFullyQualified(destination.Trim()))
                return false;

            var from = Path.GetPathRoot(Path.GetFullPath(source.Trim()));
            var to = Path.GetPathRoot(Path.GetFullPath(destination.Trim()));
            return !string.IsNullOrEmpty(from) && string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }
    }

    // What Windows does not allow in a file or folder name, wherever this code runs.
    private const string NotInNames = "\\/:*?\"<>|";
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>A backup's name as a folder name: nothing Windows refuses, at most 60 characters, never empty.</summary>
    internal static string CleanFolderName(string name)
    {
        var text = new StringBuilder();
        foreach (var letter in name ?? string.Empty)
            text.Append(letter < ' ' || NotInNames.Contains(letter) ? ' ' : letter);

        var clean = string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.', ' ');
        if (clean.Length > 60)
            clean = clean[..60].TrimEnd('.', ' ');
        if (clean.Length == 0)
            return "Backup";
        return Reserved.Contains(clean) ? clean + " backup" : clean;
    }

    /// <summary>Whether a name can be a folder's on Windows as it stands.</summary>
    internal static bool IsFolderName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= 120 && name == name.Trim() && !name.EndsWith('.') &&
           !name.Any(letter => letter < ' ' || NotInNames.Contains(letter)) && !Reserved.Contains(name);

    /// <summary>What a backup of <paramref name="source"/> is called until the person says otherwise: "Photos", "Drive D", "report.docx".</summary>
    internal static string DefaultName(string source)
    {
        var full = Normalize(source);
        if (full.Length == 0)
            return string.Empty;

        if (IsDriveRoot(full))
        {
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                return CleanFolderName(full.TrimStart('\\').Replace('\\', ' '));
            var letter = full.TrimEnd(Separators).TrimEnd(':');
            return letter.Length == 1 ? "Drive " + char.ToUpperInvariant(letter[0]) : CleanFolderName(letter.Length == 0 ? "Drive" : letter);
        }

        return CleanFolderName(Path.GetFileName(full));
    }

    /// <summary>
    /// A folder name for a new backup in <paramref name="destination"/>: its name, or its name with
    /// "backup" or a number after it when a folder of that name is already there or is used by another plan.
    /// </summary>
    internal static string FreeFolderName(string destination, string name, IEnumerable<FileBackupPlan> others, Func<string, bool>? exists = null)
    {
        exists ??= path => Directory.Exists(path) || File.Exists(path);
        var taken = others
            .Where(plan => Same(plan.Destination, destination))
            .Select(plan => plan.FolderName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var clean = CleanFolderName(name);
        var candidates = new[] { clean, clean.EndsWith("backup", StringComparison.OrdinalIgnoreCase) ? clean + " 2" : clean + " backup" }
            .Concat(Enumerable.Range(2, 98).Select(number => $"{clean} backup {number}"));
        foreach (var candidate in candidates)
        {
            if (!taken.Contains(candidate) && !exists(Path.Combine(destination, candidate)))
                return candidate;
        }

        return clean + " " + Guid.NewGuid().ToString("N")[..8];
    }
}

/// <summary>The rules a plan has to keep before it is saved or run. None of them looks at the disk.</summary>
internal static class FileBackupRules
{
    // A name to leave out is a name, not a path, and is handed to robocopy as it is.
    private static readonly char[] NotInPatterns = ['\\', '/', ':', '"', '<', '>', '|'];

    /// <summary>What was typed in "Leave out", as a list: split at commas, semicolons and line ends.</summary>
    internal static List<string> ParseExclude(string? typed)
        => (typed ?? string.Empty)
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// NEW (retention): what "Keep" and "Zip to save space" in the editor make of a new plan. The two
    /// together decide its kind, so nobody has to know the kinds to get what they mean:
    ///     Keep: latest only    not zipped   a Clone (or Add and update, when chosen under More options)
    ///                          zipped       a Zip archive that keeps one zip
    ///     Keep: last N, N days not zipped   Versions: a dated copy per run, thinned out by number or age
    ///                          zipped       a Zip archive, thinned out the same way
    /// </summary>
    internal static void Shape(FileBackupPlan plan, bool zipped, FileBackupRetention keep, FileBackupType latestKind = FileBackupType.Clone)
    {
        if (latestKind is not (FileBackupType.Clone or FileBackupType.AddAndUpdate))
            latestKind = FileBackupType.Clone;

        plan.Type = zipped ? FileBackupType.Zip : keep.LatestOnly ? latestKind : FileBackupType.Versions;
        SetRetention(plan, keep);
    }

    /// <summary>NEW (retention): how many runs a Versions or Zip archive plan keeps, by number or by age. Its kind stays.</summary>
    internal static void SetRetention(FileBackupPlan plan, FileBackupRetention keep)
    {
        plan.MaxAgeDays = keep.Days > 0 ? keep.Days : 0;
        plan.KeepCount = keep.Days > 0 ? 1 : Math.Max(1, keep.Count);
    }

    /// <summary>
    /// A time of day as typed ("14:30", "2:30 PM", "9", "0930") in minutes after midnight. The PC's own
    /// way of writing times is tried first.
    /// </summary>
    internal static bool TryParseTime(string? typed, out int minutes, CultureInfo? culture = null)
    {
        minutes = 0;
        var text = (typed ?? string.Empty).Trim();
        if (text.Length == 0)
            return false;

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            // "9" is nine o'clock, "0930" or "930" half past nine.
            var (hours, rest) = text.Length <= 2 ? (number, 0) : (number / 100, number % 100);
            if (hours > 23 || rest > 59)
                return false;
            minutes = hours * 60 + rest;
            return true;
        }

        var formats = new[] { "H:mm", "HH:mm", "H.mm", "HH.mm", "h:mm tt", "h tt", "htt", "h:mmtt" };
        if (!DateTime.TryParse(text, culture ?? CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out var parsed) &&
            !DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out parsed))
            return false;
        if (parsed.Year != 1)
            return false; // a date was typed, not a time

        minutes = parsed.Hour * 60 + parsed.Minute;
        return true;
    }

    /// <summary>A time of day as it is shown in the box it was typed in.</summary>
    internal static string TimeText(int minutes, CultureInfo? culture = null)
        => (DateTime.MinValue + TimeSpan.FromMinutes(Math.Clamp(minutes, 0, 24 * 60 - 1))).ToString("t", culture ?? CultureInfo.CurrentCulture);

    /// <summary>What is wrong with a plan, in words for the person; null when it can be saved.</summary>
    internal static string? Problem(FileBackupPlan plan, IEnumerable<FileBackupPlan> others)
    {
        if (string.IsNullOrWhiteSpace(plan.Source))
            return "Choose what to back up.";
        if (!Path.IsPathFullyQualified(plan.Source))
            return "What to back up has to be a full path, such as D:\\Photos.";
        if (string.IsNullOrWhiteSpace(plan.Destination))
            return "Choose where the backup is kept.";
        if (!Path.IsPathFullyQualified(plan.Destination))
            return "Where the backup is kept has to be a full path, such as E:\\Backups.";
        if (string.IsNullOrWhiteSpace(plan.Name))
            return "Give the backup a name.";
        if (!FileBackupPaths.IsFolderName(plan.FolderName))
            return "The backup's folder name can't be used. Give the backup another name.";

        var root = plan.Root;
        if (!plan.SourceIsFile && FileBackupPaths.IsInside(root, plan.Source))
            return "A backup can't be kept inside what it backs up. Choose somewhere else, such as another drive.";
        if (FileBackupPaths.IsInside(plan.Source, root))
            return "What you are backing up is inside the folder this backup would use. Choose somewhere else to keep it.";

        foreach (var other in others.Where(other => other.Id != plan.Id))
        {
            if (FileBackupPaths.IsInside(root, other.Root) || FileBackupPaths.IsInside(other.Root, root))
                return $"The backup \"{other.Name}\" already uses that folder. Choose another place, or give this backup another name.";
        }

        if (plan.Schedule.Kind == FileBackupSchedule.Weekly && !plan.Schedule.IsOn)
            return "Choose at least one day for the weekly backup.";
        if (plan.Schedule.Kind == FileBackupSchedule.Custom && plan.Schedule.EveryHours is < 1 or > FileBackupSchedule.LongestHours)
            return "How often has to be between 1 hour and a year.";
        if (plan.KeepCount < 1)
            return "At least one copy has to be kept.";
        if (plan.KeepDays < 0)
            return "How long earlier versions are kept can't be less than nothing.";

        foreach (var pattern in plan.Exclude)
        {
            if (pattern.IndexOfAny(NotInPatterns) >= 0)
                return $"\"{pattern}\" can't be left out: write a name such as node_modules or *.tmp, not a path.";
        }

        return null;
    }

    /// <summary>
    /// The last step before a plan from the editor is saved: what stands in the way, in words; null when
    /// nothing does. For a new plan this also finds out whether it backs up a file or a folder and gives
    /// the backup its own folder name. Looks at the disk, so call it off the UI thread.
    /// </summary>
    /// <param name="createDestination">NEW (backup drives): the folder to keep the backup in was offered by
    /// Clearspace ("E:\Backups" on a drive without one) and may be made when it is not there. A folder the
    /// person typed is never made up: a missing one there is more likely a slip or an unplugged drive.</param>
    internal static string? Prepare(FileBackupPlan plan, IReadOnlyList<FileBackupPlan> others, bool isNew, bool createDestination = false)
    {
        if (isNew)
        {
            if (plan.Source.Length == 0)
                return "Choose what to back up.";
            if (plan.Destination.Length == 0)
                return "Choose where the backup is kept.";
            if (!Path.IsPathFullyQualified(plan.Source) || !Path.IsPathFullyQualified(plan.Destination))
                return Problem(plan, others);

            plan.SourceIsFile = File.Exists(plan.Source);
            if (!plan.SourceIsFile && !Directory.Exists(plan.Source))
                return $"{plan.Source} is not there. Choose the file, folder or drive to back up.";
            if (createDestination && !Directory.Exists(plan.Destination) && Path.GetPathRoot(plan.Destination) is { Length: > 0 } drive && Directory.Exists(drive))
                Directory.CreateDirectory(plan.Destination);
            if (!Directory.Exists(plan.Destination))
                return $"{plan.Destination} is not there. Choose a folder that exists (if it is on another drive, connect that first).";
            if (plan.Name.Length == 0)
                return "Give the backup a name.";

            plan.CreatedUtc = DateTime.UtcNow;
            // A folder of that name is in the way unless it is this very backup's from before (set up again
            // after its settings were lost), which is then carried on.
            plan.FolderName = FileBackupPaths.FreeFolderName(plan.Destination, plan.Name, others, path =>
            {
                if (!Directory.Exists(path))
                    return File.Exists(path);
                var marker = FileBackupLayout.ReadMarker(path);
                return !(marker is not null && FileBackupPaths.Same(marker.Source, plan.Source) && marker.Type == plan.Type.ToString());
            });
        }

        return Problem(plan, others) ?? (isNew ? FileBackupLayout.WhyNotRoot(plan) : null);
    }

    /// <summary>
    /// Why a plan cannot run right now although nothing is wrong with it: the original or the place its
    /// backup goes is out of reach (a drive that is not plugged in). Null when both are there.
    /// Looks at the disk.
    /// </summary>
    internal static string? WhyWaiting(FileBackupPlan plan)
    {
        try
        {
            if (!(plan.SourceIsFile ? File.Exists(plan.Source) : Directory.Exists(plan.Source)))
                return $"Waiting for {plan.Source}, which is not there right now.";
            if (!Directory.Exists(plan.Destination))
                return $"Waiting for {plan.Destination}, which is not connected.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return error.Message;
        }

        return null;
    }
}

/// <summary>Reading and writing what is kept on this PC about file backups. One folder; see the top of this file.</summary>
internal sealed class FileBackupStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Lock _gate = new();
    private FileBackupSettings? _cached;
    private (DateTime Written, long Length) _cachedFrom;

    internal static FileBackupStore Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clearspace", "File backups"));

    internal FileBackupStore(string folder) => Folder = folder;

    internal string Folder { get; }
    internal string PlansPath => Path.Combine(Folder, "backups.json");
    internal string StateFolder => Path.Combine(Folder, "state");
    internal string LogFolder => Path.Combine(Folder, "logs");

    /// <summary>Held by the process that runs whatever is due, so two never do.</summary>
    internal string RunnerLockPath => Path.Combine(StateFolder, "runner.lock");

    /// <summary>While this file is there, every run stops (Clearspace is being updated or removed).</summary>
    internal string StopAllPath => Path.Combine(StateFolder, "stop-all");

    internal string StatePath(string id) => Path.Combine(StateFolder, id + ".json");
    internal string ProgressPath(string id) => Path.Combine(StateFolder, id + ".progress.json");
    internal string LockPath(string id) => Path.Combine(StateFolder, id + ".lock");
    internal string CancelPath(string id) => Path.Combine(StateFolder, id + ".cancel");

    /// <summary>
    /// The plans as they are on the disk. Read again only when the file has changed, because the
    /// right-click menu asks every time it opens. What is returned is shared: copy a plan before changing it.
    /// </summary>
    internal FileBackupSettings Load()
    {
        lock (_gate)
        {
            try
            {
                var file = new FileInfo(PlansPath);
                if (!file.Exists)
                    return _cached = new FileBackupSettings();

                var from = (file.LastWriteTimeUtc, file.Length);
                if (_cached is not null && from == _cachedFrom)
                    return _cached;

                var read = JsonSerializer.Deserialize<FileBackupSettings>(File.ReadAllText(PlansPath), Json) ?? new FileBackupSettings();
                read.Plans = (read.Plans ?? []).Where(plan => plan is not null && !string.IsNullOrWhiteSpace(plan.Id)).ToList();
                foreach (var plan in read.Plans)
                {
                    plan.Schedule ??= new FileBackupSchedule();
                    plan.Exclude ??= [];
                }

                _cachedFrom = from;
                return _cached = read;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                // Unreadable just now: what was read before is better than pretending there are no backups.
                return _cached ?? new FileBackupSettings();
            }
        }
    }

    internal void Save(FileBackupSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Folder);
            WriteWhole(PlansPath, JsonSerializer.Serialize(settings, Json));
            _cached = null;
        }
    }

    internal FileBackupState State(string id) => Read<FileBackupState>(StatePath(id)) ?? new FileBackupState();

    internal void SaveState(string id, FileBackupState state)
    {
        Directory.CreateDirectory(StateFolder);
        WriteWhole(StatePath(id), JsonSerializer.Serialize(state, Json));
    }

    internal FileBackupProgress? Progress(string id) => Read<FileBackupProgress>(ProgressPath(id));

    internal void SaveProgress(string id, FileBackupProgress progress)
    {
        try
        {
            Directory.CreateDirectory(StateFolder);
            // Written in place, several times a second: a reader that catches it half written reads it again.
            File.WriteAllText(ProgressPath(id), JsonSerializer.Serialize(progress, Json));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // progress is only for show
        }
    }

    internal void ClearProgress(string id) => TryDelete(ProgressPath(id));

    /// <summary>"logs\&lt;id&gt;\2026-10-06 16.30.05.log"; the folder is made.</summary>
    internal string NewLogPath(string id, DateTime nowLocal)
    {
        var folder = Path.Combine(LogFolder, id);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, FileBackupLayout.Stamp(nowLocal) + ".log");
    }

    /// <summary>Deletes a plan's reports beyond the newest <paramref name="keep"/> runs.</summary>
    internal void PruneLogs(string id, int keep = 20)
    {
        try
        {
            var folder = Path.Combine(LogFolder, id);
            if (!Directory.Exists(folder))
                return;

            // A run's files share the date their names start with ("2026-10-06 16.30.05.log", "….robocopy.log").
            static string Run(string path)
            {
                var name = Path.GetFileName(path);
                return name.Length >= 19 ? name[..19] : name;
            }

            var runs = Directory.EnumerateFiles(folder)
                .GroupBy(Run, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(run => run.Key, StringComparer.OrdinalIgnoreCase)
                .Skip(Math.Max(1, keep));
            foreach (var path in runs.SelectMany(run => run))
                TryDelete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // left for the next run
        }
    }

    /// <summary>Removes what this PC keeps about a plan that was deleted. The backup itself is not touched.</summary>
    internal void Forget(string id)
    {
        foreach (var path in new[] { StatePath(id), ProgressPath(id), CancelPath(id), LockPath(id) })
            TryDelete(path);

        try
        {
            var logs = Path.Combine(LogFolder, id);
            if (Directory.Exists(logs))
                Directory.Delete(logs, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes a small text file under another name first, then swaps it in, so a half-written file is never read.</summary>
    internal static void WriteWhole(string path, string text)
    {
        var partial = path + ".new";
        File.WriteAllText(partial, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(partial, path, overwrite: true);
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

internal enum FileBackupTone { Quiet, Good, Warn, Bad }

/// <summary>
/// NEW (retention): how many of a backup's runs are kept. Either the newest <paramref name="Count"/>
/// (1: only the latest), or, when <paramref name="Days"/> is above 0, every run of the last so many days.
/// </summary>
internal readonly record struct FileBackupRetention(int Count, int Days = 0)
{
    internal static FileBackupRetention Latest => new(1);

    internal bool LatestOnly => Days <= 0 && Count <= 1;

    /// <summary>What a plan keeps: a Clone or Add and update has one copy; the other two say.</summary>
    internal static FileBackupRetention Of(FileBackupPlan plan)
        => plan.Type is FileBackupType.Versions or FileBackupType.Zip
            ? plan.MaxAgeDays > 0 ? new FileBackupRetention(1, plan.MaxAgeDays) : new FileBackupRetention(Math.Max(1, plan.KeepCount))
            : Latest;

    /// <summary>"Latest only", "Last 10", "30 days", "1 year": the words on the editor's buttons.</summary>
    internal string Label => Days switch
    {
        365 => "1 year",
        1 => "1 day",
        > 0 => $"{Days:N0} days",
        _ => LatestOnly ? "Latest only" : $"Last {Count:N0}"
    };
}

/// <summary>What the Backup and restore page says about a plan. Words only, so they can be tested without a window.</summary>
internal static class FileBackupWords
{
    /// <summary>"for 30 days", "for a year", "for good".</summary>
    internal static string Period(int days) => days switch
    {
        <= 0 => "for good",
        1 => "for a day",
        365 => "for a year",
        _ => $"for {days:N0} days"
    };

    /// <summary>What a plan keeps besides the copy itself: "keeps removed files for 30 days", "keeps the last 10 versions".</summary>
    internal static string Keeps(FileBackupPlan plan)
    {
        switch (plan.Type)
        {
            // CHANGED (retention): by age as well as by number.
            case FileBackupType.Versions:
                return plan.MaxAgeDays > 0 ? $"keeps versions {Period(plan.MaxAgeDays)}"
                    : plan.KeepCount == 1 ? "keeps the newest version only" : $"keeps the last {plan.KeepCount:N0} versions";
            case FileBackupType.Zip:
                return plan.MaxAgeDays > 0 ? $"keeps zips {Period(plan.MaxAgeDays)}"
                    : plan.KeepCount == 1 ? "keeps the newest zip only" : $"keeps the last {plan.KeepCount:N0} zips";
        }

        var removed = plan.Type == FileBackupType.Clone && plan.KeepRemoved;
        var period = Period(plan.KeepDays);
        if (removed && plan.KeepVersions)
            return $"keeps removed files and earlier versions {period}";
        if (removed)
            return $"keeps removed files {period}";
        if (plan.KeepVersions)
            return $"keeps earlier versions {period}";
        return plan.Type == FileBackupType.Clone ? "removed files are gone from the backup too" : "nothing is ever removed";
    }

    /// <summary>
    /// One line on a plan's card: "Clone  ·  Every day at 12:00".
    /// CHANGED (simpler page): what the plan keeps is no longer in the line; it is the line's tooltip (Keeps).
    /// </summary>
    /// CHANGED (retention): a plan that keeps more than its latest run says so: "…  ·  keeps last 10".
    internal static string Summary(FileBackupPlan plan, CultureInfo? culture = null)
    {
        var keep = FileBackupRetention.Of(plan);
        var line = $"{FileBackupPlan.TypeName(plan.Type)}  ·  {plan.Schedule.Describe(culture)}";
        return keep.LatestOnly ? line : $"{line}  ·  keeps {char.ToLowerInvariant(keep.Label[0])}{keep.Label[1..]}";
    }

    /// <summary>
    /// How things stand with a plan that is not running right now, in as few words as will do.
    /// <paramref name="day"/> words a moment ("today, 16:30"); <paramref name="cutShort"/>: its last run
    /// ended without saying how.
    /// </summary>
    internal static (string Text, FileBackupTone Tone) Status(FileBackupPlan plan, FileBackupState state, bool cutShort, Func<DateTime, string> day)
    {
        if (cutShort)
            return ("The last run was interrupted. The next one carries on.", FileBackupTone.Warn);

        var when = state.LastEndUtc ?? state.LastRunUtc;
        var at = when is { } moment ? day(moment) : string.Empty;
        var said = string.IsNullOrWhiteSpace(state.Message) ? string.Empty : " " + state.Message;
        switch (state.Outcome)
        {
            case FileBackupState.Waiting:
                return (state.Message, FileBackupTone.Warn);
            case FileBackupState.Ok:
                return ($"Backed up {at}.{said}", FileBackupTone.Good);
            case FileBackupState.Nothing:
                return ($"Up to date. Checked {at}.", FileBackupTone.Good);
            case FileBackupState.Problems:
                return ($"Backed up {at}, with problems.{said}", FileBackupTone.Warn);
            case FileBackupState.Failed:
                return ($"Failed {at}.{said}", FileBackupTone.Bad);
            case FileBackupState.Stopped:
                return ($"Stopped {at}.", FileBackupTone.Warn);
            default:
                return ("Not backed up yet.", FileBackupTone.Warn);
        }
    }

    /// <summary>"Next: tomorrow, 12:00"; null when the plan has no schedule or is waiting for a drive.</summary>
    internal static string? Next(FileBackupPlan plan, FileBackupState state, DateTime nowLocal, Func<DateTime, string> day)
    {
        if (!plan.Schedule.IsOn || state.Outcome == FileBackupState.Waiting)
            return null;

        var last = state.LastRunUtc is { } utc ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime() : (DateTime?)null;
        if (plan.Schedule.NextRun(last, state.LastFailed, nowLocal) is not { } next)
            return null;
        return next <= nowLocal.AddMinutes(1) ? "Next: any minute" : "Next: " + day(next);
    }

    /// <summary>What a run in progress is doing: "Copying…  1,234 files (2.1 GB)".</summary>
    internal static string Progress(FileBackupProgress progress, Func<long, string> size)
    {
        var phase = string.IsNullOrWhiteSpace(progress.Phase) ? "Backing up" : progress.Phase;
        if (progress.Files <= 0)
            return phase + "…";
        var files = progress.Files == 1 ? "1 file" : $"{progress.Files:N0} files";
        return progress.Bytes > 0 ? $"{phase}…  {files} ({size(progress.Bytes)})" : $"{phase}…  {files}";
    }
}
