// Clearspace | NEW (backup): backing up and putting back the things Clearspace itself keeps.
// Settings > "Back up settings and tags…" (BackupWindow.cs). A backup is one file, "Clearspace backup <date>.csbackup",
// which is an ordinary ZIP holding three small texts:
//     manifest.json   when it was made, by which Clearspace, what is in it
//     settings.json   the settings: pins, sidebar, Your files, folder types, columns, themes, filing habits
//     tags.json       every tag, and which files and folders carry it
// What is NOT in a backup: your files, the search index (it rebuilds itself), and anything about password
// locks. A locked file carries its own lock inside it, and the record of what is locked right now must
// never be rolled back to an older state, so restoring leaves those records exactly as they are.
//
// Restoring:
//  - Tags are put back on the files that still exist (or whose drive is not connected, so nobody can
//    tell). By default nothing you have now is taken away; "exactly as in the backup" also removes tags
//    and tagging made since.
//  - Settings replace today's settings as a whole, and Clearspace restarts to use them. How backups are
//    made (schedule, folder, how many to keep) stays as it is now.
//  - Before either, a backup of how things are now is made ("before restore"), so a restore can be undone.
//
// Automatic backups: daily, weekly, or every so many hours or days ("Custom"), checked a little after
// Clearspace starts and every hour while it is open. When nothing has changed since the newest backup, no new file is written. Only automatic
// backups are thinned out (the newest N are kept); ones you made yourself stay until you delete them.
//
// The rules here (when a backup is due, which to thin out, which items to restore, reading and writing
// the file) take what they need as arguments and are tested in Clearspace.Tests/BackupTests.cs.
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Clearspace.Services;

internal enum BackupKind { Manual, Automatic, BeforeRestore }

/// <summary>What a backup says about itself (manifest.json).</summary>
internal sealed class BackupManifest
{
    public int Format { get; set; } = BackupService.Format;
    public string AppVersion { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string Kind { get; set; } = nameof(BackupKind.Manual);
    public int Tags { get; set; }
    public int TaggedItems { get; set; }
    public bool HasSettings { get; set; }
    /// <summary>A fingerprint of the settings and tags, to tell whether anything changed since.</summary>
    public string ContentHash { get; set; } = string.Empty;
    /// <summary>NEW (backup label): what the person called this backup when making it ("Before reinstalling
    /// Windows"). Shown in the list instead of "Made by you". Null when they left it as it was.</summary>
    public string? Note { get; set; }

    internal BackupKind KindValue => Enum.TryParse<BackupKind>(Kind, out var kind) ? kind : BackupKind.Manual;
}

/// <summary>What goes into a backup, taken from the running Clearspace.</summary>
internal sealed record BackupSnapshot(string SettingsJson, TagDefinition[] Tags, Dictionary<string, List<string>> Assignments);

/// <summary>A backup file in the list: where it is and what it says about itself.</summary>
internal sealed record BackupFile(string Path, BackupManifest Manifest);

/// <summary>A backup read in full, ready to be restored from.</summary>
internal sealed record BackupContents(string Path, BackupManifest Manifest, string? SettingsJson,
    TagDefinition[] Tags, Dictionary<string, List<string>> Assignments);

internal static class BackupService
{
    internal const int Format = 1;
    internal const string Extension = ".csbackup";
    private const long MostBytesPerPart = 256L * 1024 * 1024; // nothing Clearspace writes comes near this

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // ------------------------------------------------------------------ where and when

    /// <summary>Documents\Clearspace Backups: found by other backup tools and by OneDrive, and easy to see.</summary>
    internal static string DefaultFolder => System.IO.Path.Combine(KnownFolders.Documents, "Clearspace Backups");

    /// <summary>Where backups go: the folder chosen in the Backup window, or the usual one.</summary>
    internal static string Folder => SettingsService.GetBackupFolder() ?? DefaultFolder;

    /// <summary>
    /// Whether an automatic backup should be made now. Daily means once the last one is 20 hours old (so
    /// opening Clearspace each morning gets one a day whatever the exact minute); weekly, 6 and a half days.
    /// </summary>
    /// CHANGED (custom schedule): "Custom" means every <paramref name="customHours"/> hours, to the hour.
    internal static bool IsDue(string schedule, DateTime? lastUtc, DateTime nowUtc, int customHours = 0)
    {
        var every = schedule switch
        {
            "Daily" => TimeSpan.FromHours(20),
            "Weekly" => TimeSpan.FromDays(6.5),
            "Custom" when customHours >= 1 => TimeSpan.FromHours(customHours),
            _ => (TimeSpan?)null
        };

        if (every is not { } interval)
            return false;

        return lastUtc is not { } last || nowUtc - last >= interval || last > nowUtc; // a clock set back counts as due
    }

    /// <summary>The automatic backups beyond the newest <paramref name="keep"/>. Other kinds are never thinned out.</summary>
    internal static IReadOnlyList<BackupFile> ToPrune(IEnumerable<BackupFile> backups, int keep)
        => backups
            .Where(backup => backup.Manifest.KindValue == BackupKind.Automatic)
            .OrderByDescending(backup => backup.Manifest.CreatedUtc)
            .Skip(Math.Max(1, keep))
            .ToList();

    /// <summary>"Clearspace backup 2026-10-06 16.30.05 (automatic).csbackup"</summary>
    internal static string FileName(DateTime createdUtc, BackupKind kind)
    {
        var stamp = createdUtc.ToLocalTime().ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture);
        var mark = kind switch
        {
            BackupKind.Automatic => " (automatic)",
            BackupKind.BeforeRestore => " (before restore)",
            _ => string.Empty
        };
        return $"Clearspace backup {stamp}{mark}{Extension}";
    }

    // ------------------------------------------------------------------ making one

    /// <summary>What is in Clearspace right now. Call on the UI thread, where settings and tags change.</summary>
    internal static BackupSnapshot Capture()
    {
        var (tags, assignments) = TagService.Store.Export();
        return new BackupSnapshot(SettingsService.CurrentJson(), tags, assignments);
    }

    /// <summary>The tags of a backup as text (the layout of the old tags.json: Definitions and Assignments).</summary>
    internal static string TagsJson(TagDefinition[] tags, Dictionary<string, List<string>> assignments)
        => JsonSerializer.Serialize(new TagsDocument
        {
            Definitions = tags.Select(tag => new TagEntry { Id = tag.Id, Name = tag.Name, Color = tag.Color }).ToList(),
            Assignments = new SortedDictionary<string, List<string>>(assignments, StringComparer.OrdinalIgnoreCase)
        }, Json);

    /// <summary>The same settings and tags always give the same fingerprint, whenever they are backed up.</summary>
    internal static string Fingerprint(string settingsJson, string tagsJson)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settingsJson + "\n--\n" + tagsJson)));

    /// <summary>
    /// Writes a backup file into <paramref name="folder"/> and returns it. Written under a temporary name
    /// first, so a backup that could not be finished never looks like a good one.
    /// </summary>
    /// CHANGED (backup label): <paramref name="note"/> is the person's own label for it, if they gave one.
    internal static BackupFile Write(BackupSnapshot snapshot, string folder, BackupKind kind, DateTime nowUtc, string appVersion, string? note = null)
    {
        Directory.CreateDirectory(folder);

        var tagsJson = TagsJson(snapshot.Tags, snapshot.Assignments);
        var manifest = new BackupManifest
        {
            AppVersion = appVersion,
            CreatedUtc = nowUtc,
            Kind = kind.ToString(),
            Tags = snapshot.Tags.Length,
            TaggedItems = snapshot.Assignments.Count,
            HasSettings = true,
            ContentHash = Fingerprint(snapshot.SettingsJson, tagsJson),
            Note = CleanNote(note)
        };

        var path = System.IO.Path.Combine(folder, FileName(nowUtc, kind));
        var partial = path + ".part";
        try
        {
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                Add(zip, "manifest.json", JsonSerializer.Serialize(manifest, Json));
                Add(zip, "settings.json", snapshot.SettingsJson);
                Add(zip, "tags.json", tagsJson);
            }

            File.Move(partial, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(partial); }
            catch (Exception) { }
            throw;
        }

        return new BackupFile(path, manifest);
    }

    internal const int LongestNote = 80;

    /// <summary>A label as it is kept: one line, no spaces at the ends, not too long; null when nothing is left.</summary>
    internal static string? CleanNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
            return null;

        var line = string.Join(' ', note.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
        if (line.Length > LongestNote)
            line = line[..LongestNote].TrimEnd();
        return line.Length == 0 ? null : line;
    }

    /// <summary>What a backup is called in the list: the person's own label, or what kind it is.</summary>
    internal static string Label(BackupManifest manifest)
        => CleanNote(manifest.Note) ?? (manifest.KindValue switch
        {
            BackupKind.Automatic => "Automatic",
            BackupKind.BeforeRestore => "Made before a restore",
            _ => MadeByYou
        });

    internal const string MadeByYou = "Made by you";

    private static void Add(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }

    // ------------------------------------------------------------------ reading them

    /// <summary>The backups in a folder, newest first. Files that are not Clearspace backups are passed over.</summary>
    internal static IReadOnlyList<BackupFile> List(string folder)
    {
        var found = new List<BackupFile>();
        if (!Directory.Exists(folder))
            return found;

        foreach (var path in Directory.EnumerateFiles(folder, "*" + Extension))
        {
            try
            {
                using var zip = ZipFile.OpenRead(path);
                if (ReadManifest(zip) is { } manifest)
                    found.Add(new BackupFile(path, manifest));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                // damaged, or in use by something else right now: not offered
            }
        }

        found.Sort((one, other) => other.Manifest.CreatedUtc.CompareTo(one.Manifest.CreatedUtc));
        return found;
    }

    /// <summary>
    /// Reads a whole backup. Throws InvalidDataException with words for the user when the file is not a
    /// Clearspace backup, is damaged, or was made by a newer Clearspace than this one understands.
    /// </summary>
    internal static BackupContents Read(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var manifest = ReadManifest(zip) ?? throw new InvalidDataException("This file is not a Clearspace backup.");
            if (manifest.Format > Format)
                throw new InvalidDataException($"This backup was made by a newer Clearspace ({manifest.AppVersion}). Update Clearspace to restore from it.");

            var settings = Text(zip, "settings.json");
            var tags = Array.Empty<TagDefinition>();
            var assignments = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            if (Text(zip, "tags.json") is { } tagsJson && JsonSerializer.Deserialize<TagsDocument>(tagsJson, Json) is { } document)
            {
                tags = (document.Definitions ?? [])
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Id) && !string.IsNullOrWhiteSpace(entry.Name) && !string.IsNullOrWhiteSpace(entry.Color))
                    .Select(entry => new TagDefinition(entry.Id!, entry.Name!, entry.Color!))
                    .ToArray();

                if (document.Assignments is { } saved)
                {
                    foreach (var (item, ids) in saved)
                    {
                        if (!string.IsNullOrWhiteSpace(item) && ids is { Count: > 0 })
                            assignments[item] = ids;
                    }
                }
            }

            return new BackupContents(path, manifest, settings, tags, assignments);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("This backup could not be read: " + error.Message, error);
        }
    }

    private static BackupManifest? ReadManifest(ZipArchive zip)
        => Text(zip, "manifest.json") is { } json && JsonSerializer.Deserialize<BackupManifest>(json, Json) is { CreatedUtc.Year: > 2000 } manifest
            ? manifest
            : null;

    private static string? Text(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null)
            return null;
        if (entry.Length > MostBytesPerPart)
            throw new InvalidDataException("This backup is far larger than a Clearspace backup can be.");

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Deletes automatic backups beyond the newest <paramref name="keep"/>. Returns how many went.</summary>
    internal static int Prune(string folder, int keep)
    {
        var gone = 0;
        foreach (var old in ToPrune(List(folder), keep))
        {
            try
            {
                File.Delete(old.Path);
                gone++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // left for the next time
            }
        }

        return gone;
    }

    // ------------------------------------------------------------------ automatic backups

    private static int _automaticRunning;

    /// <summary>What went wrong with the last automatic backup, for the Backup window; null when it worked.</summary>
    internal static string? LastAutomaticError { get; private set; }

    /// <summary>Raised (on the UI thread) after a backup was written or thinned out, so an open Backup window can list it.</summary>
    internal static event Action? Changed;

    internal static void NotifyChanged() => Changed?.Invoke();

    /// <summary>
    /// Makes an automatic backup if one is due. Call on the UI thread (every window's timer does; only one
    /// runs at a time). The settings and tags are copied here; the file is written on another thread.
    /// When nothing changed since the newest backup, no file is written and the clock just starts again.
    /// </summary>
    internal static async Task RunAutomaticIfDueAsync()
    {
        if (!IsDue(SettingsService.GetBackupSchedule(), SettingsService.GetLastBackupUtc(), DateTime.UtcNow, SettingsService.GetBackupEveryHours()))
            return;
        if (Interlocked.Exchange(ref _automaticRunning, 1) == 1)
            return;

        try
        {
            var snapshot = Capture();
            var folder = Folder;
            var keep = SettingsService.GetBackupsToKeep();
            var now = DateTime.UtcNow;

            var wrote = await Task.Run(() =>
            {
                var fingerprint = Fingerprint(snapshot.SettingsJson, TagsJson(snapshot.Tags, snapshot.Assignments));
                var newest = List(folder).FirstOrDefault();
                if (newest is not null && newest.Manifest.ContentHash == fingerprint)
                    return false; // the newest backup already holds exactly this

                Write(snapshot, folder, BackupKind.Automatic, now, UpdateService.CurrentText);
                Prune(folder, keep);
                return true;
            });

            SettingsService.SetLastBackupUtc(now);
            LastAutomaticError = null;
            if (wrote)
                NotifyChanged();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            // The folder is not there (a drive that is not connected), or it could not be written to. Said in
            // the Backup window; tried again at the next check.
            LastAutomaticError = error.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _automaticRunning, 0);
        }
    }

    // ------------------------------------------------------------------ restoring

    /// <summary>
    /// Whether a tagged item of a backup gets its tags back: it is still there, or it is on a drive (or
    /// network share) that is not connected, in which case nobody can tell and it is better kept than dropped.
    /// </summary>
    internal static bool ShouldRestore(string item, Func<string, bool> exists, Func<string, bool> rootReachable)
    {
        if (string.IsNullOrWhiteSpace(item))
            return false;

        string? root;
        try { root = System.IO.Path.GetPathRoot(item); }
        catch (ArgumentException) { return false; }

        if (string.IsNullOrEmpty(root))
            return false;

        return !rootReachable(root) || exists(item);
    }

    /// <summary>
    /// The tagged items of a backup that should be restored, and how many were left out because they no
    /// longer exist. Looks at the disk, so call it off the UI thread.
    /// </summary>
    internal static (Dictionary<string, List<string>> Keep, int Skipped) ItemsToRestore(IReadOnlyDictionary<string, List<string>> assignments)
    {
        var roots = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool Reachable(string root)
        {
            if (roots.TryGetValue(root, out var known))
                return known;

            bool there;
            // A network share is taken to be out of reach rather than asked (that can take many seconds).
            try { there = !root.StartsWith(@"\\", StringComparison.Ordinal) && !NetworkDrives.IsNetworkRoot(root) && Directory.Exists(root); }
            catch (Exception) { there = false; }
            return roots[root] = there;
        }

        var keep = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        foreach (var (item, ids) in assignments)
        {
            if (ShouldRestore(item, path => File.Exists(path) || Directory.Exists(path), Reachable))
                keep[item] = ids;
            else
                skipped++;
        }

        return (keep, skipped);
    }

    // The layout of tags.json. Names match the tags.json older versions of Clearspace kept.
    private sealed class TagsDocument
    {
        public List<TagEntry>? Definitions { get; set; }
        // Written sorted (so the same tags always give the same text), read back as an ordinary dictionary.
        public IDictionary<string, List<string>>? Assignments { get; set; }
    }

    private sealed class TagEntry
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Color { get; set; }
    }
}
