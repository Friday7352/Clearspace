// Clearspace | NEW (backup drives): which drives to offer as the place to keep a new backup.
// The New backup window (FileBackupEditWindow.cs) shows them under "Save to" as buttons, best first:
//
//   A drive that already has Clearspace backups on it      "My Passport (E:)  ·  3 backups"
//     Any drive but the one being backed up. A click fills in the folder those backups are in, so the
//     new one goes beside them.
//   An external drive with no backups yet                  "USB DISK (F:)  ·  58 GB free"
//     A USB stick, a card, or a disk on a USB cable (never the Windows drive, never the drive being
//     backed up). A click fills in "<drive>\Backups", which is made when the backup is saved.
//
// A backup is found by its marker ("Clearspace backup.json", FileBackupLayout): in a folder at the top of
// the drive or one level down, which is where a backup lands when the drive itself or a folder at its top
// was chosen, and in the folders of the backups this PC knows. Nothing deeper is searched: this runs each
// time the window opens and each time a drive is plugged in.
// Network drives are not looked at (asking one can take many seconds); a backup can still be kept on one
// by choosing it.
//
// Suggest (the choice and the order) takes plain facts, reads no disk, and is tested in Clearspace.Tests/FileBackupTests.cs,
// as is FindBackups on a temporary folder. Look reads the real drives and is checked by hand.
using System.IO;

namespace Clearspace.Services;

/// <summary>What is known about a drive when deciding whether to offer it.</summary>
/// <param name="External">A USB stick, a memory card, or a disk on a USB cable.</param>
/// <param name="System">The drive Windows is on.</param>
/// <param name="Backups">The Clearspace backups on it.</param>
internal sealed record BackupDriveFacts(string Root, string Label, bool External, bool System, long FreeBytes, IReadOnlyList<BackupOnDrive> Backups);

/// <summary>A backup found on a drive: its folder, and what it is called.</summary>
internal sealed record BackupOnDrive(string Folder, string Name);

/// <summary>A drive offered as the place for a new backup.</summary>
/// <param name="Folder">What a click fills in as "Save to".</param>
/// <param name="Backups">How many backups are on it already; 0 for an external drive without any.</param>
/// <param name="Names">The names of those backups, for the tooltip.</param>
internal sealed record BackupDriveSuggestion(string Root, string Label, string Folder, int Backups, long FreeBytes, IReadOnlyList<string> Names)
{
    public bool HasBackups => Backups > 0;

    /// <summary>"My Passport (E:)"</summary>
    public string Title => $"{Label} ({Root.TrimEnd('\\', '/')})";
}

internal static class FileBackupDrives
{
    internal const string NewFolder = "Backups";
    internal const int Most = 4;                 // more buttons than this would be a list, not a hint
    private const int MostFoldersALevel = 300;

    /// <summary>
    /// The folders on a drive that are Clearspace backups: at its top and one level down. Hidden system
    /// folders are passed over, and so is anything that cannot be listed.
    /// </summary>
    internal static List<string> FindBackups(string driveRoot)
    {
        var found = new List<string>();
        foreach (var top in Folders(driveRoot))
        {
            if (IsBackup(top))
            {
                found.Add(top);
                continue; // a backup's own folders are not other backups
            }

            foreach (var below in Folders(top))
            {
                if (IsBackup(below))
                    found.Add(below);
            }
        }

        return found;
    }

    private static bool IsBackup(string folder)
    {
        try { return File.Exists(Path.Combine(folder, FileBackupLayout.Marker)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static List<string> Folders(string folder)
    {
        var folders = new List<string>();
        try
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateDirectories())
            {
                var attributes = entry.Attributes;
                if ((attributes & FileAttributes.System) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                folders.Add(entry.FullName);
                if (folders.Count >= MostFoldersALevel)
                    break;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        return folders;
    }

    /// <summary>
    /// Where a new backup goes on a drive: the folder most of its backups are in (so the new one sits
    /// beside them), or "&lt;drive&gt;\Backups" when it has none.
    /// </summary>
    internal static string FolderFor(string driveRoot, IReadOnlyList<string> backups)
    {
        var parents = backups
            .Select(backup => Path.GetDirectoryName(FileBackupPaths.Normalize(backup)))
            .Where(parent => !string.IsNullOrEmpty(parent))
            .GroupBy(parent => parent!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.Length)
            .FirstOrDefault();
        return parents?.Key ?? Path.Combine(driveRoot, NewFolder);
    }

    /// <summary>
    /// Which drives to offer, best first: those that have backups (most backups first), then external
    /// drives without any (most free room first). Never the drive <paramref name="source"/> is on: a copy
    /// on the same drive is lost with it. Reads nothing from the disk.
    /// </summary>
    internal static List<BackupDriveSuggestion> Suggest(IEnumerable<BackupDriveFacts> drives, string? source)
    {
        var sourceRoot = string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source) ? null : Path.GetPathRoot(Path.GetFullPath(source));

        return drives
            .Where(drive => sourceRoot is null || !string.Equals(FileBackupPaths.Normalize(drive.Root), FileBackupPaths.Normalize(sourceRoot), StringComparison.OrdinalIgnoreCase))
            .Where(drive => drive.Backups.Count > 0 || (drive.External && !drive.System))
            .OrderByDescending(drive => drive.Backups.Count > 0)
            .ThenByDescending(drive => drive.Backups.Count)
            .ThenByDescending(drive => drive.FreeBytes)
            .Take(Most)
            .Select(drive => new BackupDriveSuggestion(drive.Root, drive.Label,
                FolderFor(drive.Root, drive.Backups.Select(backup => backup.Folder).ToList()), drive.Backups.Count, drive.FreeBytes,
                drive.Backups.Select(backup => backup.Name).ToList()))
            .ToList();
    }

    // The folder's own name, whichever way the slashes lean.
    private static string LastPart(string folder)
    {
        var trimmed = folder.TrimEnd('\\', '/');
        return trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..];
    }

    /// <summary>
    /// Looks at the drives that are connected and says which to offer for a backup of <paramref name="source"/>.
    /// Reads each drive (its top two levels of folders) and asks what kind of device it is, so call it off
    /// the UI thread.
    /// </summary>
    internal static List<BackupDriveSuggestion> Look(string? source, IReadOnlyList<FileBackupPlan> plans)
    {
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? string.Empty;
        var facts = new List<BackupDriveFacts>();

        foreach (var drive in DriveWatch.Read())
        {
            if (drive.Type is not (DriveType.Fixed or DriveType.Removable))
                continue;

            try
            {
                var root = drive.Root;
                var backups = FindBackups(root);
                // Backups this PC knows that sit deeper than is searched.
                foreach (var plan in plans)
                {
                    if (FileBackupPaths.IsInside(plan.Root, root) && !backups.Contains(plan.Root, StringComparer.OrdinalIgnoreCase) && IsBackup(plan.Root))
                        backups.Add(plan.Root);
                }

                var external = drive.Type == DriveType.Removable || DriveHardwareProbe.For(root, network: false).Kind == DriveKind.Usb;
                facts.Add(new BackupDriveFacts(root, drive.Label, external,
                    string.Equals(root, system, StringComparison.OrdinalIgnoreCase), new DriveInfo(root).AvailableFreeSpace,
                    backups.Select(backup => new BackupOnDrive(backup, FileBackupLayout.ReadMarker(backup)?.Name ?? LastPart(backup))).ToList()));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // taken out while it was being looked at
            }
        }

        return Suggest(facts, source);
    }
}
