// Clearspace | NEW (downloads): the setup files in a folder, and which of them are no longer needed.
// A Downloads folder collects installers: once the program is on the PC the setup file is only taking up
// space. This picks out the files that are installers and compares each with the programs Windows lists
// as installed (the same list as Settings > Apps > Installed apps):
//   - "Looks installed": a program with that name is on the PC. These are ticked for removal.
//   - "Newer than the installed version": the program is installed, but this file is a later version -
//     probably an update you have not run yet. Not ticked.
//   - "Not found": nothing installed goes by that name (not installed yet, or installed under a name that
//     can't be told from the file). Not ticked.
// The comparison is by name and deliberately strict: once the words that are not part of a name are set
// aside (setup, x64, the version, the publisher), the installer's words have to be exactly the program's
// words. "Git-2.45-64-bit" is Git; it is not Git LFS or GitHub Desktop, which merely contain the word. A
// wrong "looks installed" could lead to removing a file you still want, so when in doubt there is no match.
// It never says a file is SAFE; only that the program is already there. Nothing is removed here:
// InstallersWindow shows the list and sends what stays ticked to the Recycle Bin.
// Tests: Clearspace.Tests/DownloadsTests.cs.
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Clearspace.Services;

// Publisher: who Windows says made it ("Google LLC"); its words are not part of the program's name.
internal sealed record InstalledProgram(string Name, string? Version, string? Publisher = null);

internal enum InstallerState { NotFound, Installed, NewerThanInstalled }

internal sealed record InstallerFile(string FullPath, long Size, DateTime Modified, InstallerState State, InstalledProgram? Program)
{
    public string Name => Path.GetFileName(FullPath);

    public string Note => State switch
    {
        InstallerState.Installed => Program!.Version is { Length: > 0 } version
            ? $"Looks installed: {Program.Name} {version}"
            : $"Looks installed: {Program.Name}",
        InstallerState.NewerThanInstalled => $"Newer than the installed {Program!.Name} {Program.Version}",
        _ => "Not found among the installed programs"
    };
}

// What is known about a file before it is compared. Product, Description, Version and Company come from
// the details a program file carries (none for .msi files); all four may be null.
internal sealed record InstallerCandidate(string FullPath, long Size, DateTime Modified,
    string? Product = null, string? Description = null, string? Version = null, string? Company = null);

internal static class InstallerShelf
{
    // Always an installer.
    private static readonly HashSet<string> PackageEndings = new(StringComparer.OrdinalIgnoreCase)
    {
        ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle"
    };

    // Words that say "this is a setup file" or name a kind of company. Never part of a program's name,
    // wherever they stand: "DiscordSetup" is Discord, "MicrosoftEdgeSetup" is Edge.
    private static readonly HashSet<string> SetupWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup", "install", "installer", "installation", "bootstrapper", "user",
        "microsoft", "corporation", "corp", "inc", "ltd", "llc", "gmbh"
    };

    // Build details and small words. Not part of a name when they stand on their own ("vlc-win64",
    // "Ollama version 0.5"), but kept when they are run together with something: the Win of "WinZip" and
    // the Web of "WebStorm" belong to the name.
    private static readonly HashSet<string> BuildWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "web", "online", "offline", "full", "latest", "stable", "release", "final", "portable", "system", "machine",
        "win", "windows", "x", "bit", "amd", "arm", "ia", "exe", "msi", "version", "ver", "v", "for", "the", "and", "of",
        "edition", "update", "updater", "app", "software", "technologies", "en", "us", "multi", "preview", "beta",
        "signed", "bin"
    };

    private const FileAttributes Skipped = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint |
                                           FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000; // + cloud files not on this device

    // Whether a file has an ending installers have (.exe, .msi ...). Cheap: used to notice that the setup
    // files in a folder changed, before anything is read from them.
    internal static bool CouldBeInstaller(string fileName)
    {
        var ending = Path.GetExtension(fileName);
        return ending.Equals(".exe", StringComparison.OrdinalIgnoreCase) || PackageEndings.Contains(ending);
    }

    // Whether a file is an installer, going by its name and the details it carries.
    internal static bool IsInstaller(string fileName, string? description = null)
    {
        var ending = Path.GetExtension(fileName);
        if (PackageEndings.Contains(ending))
            return true;
        if (!ending.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return false;

        var name = Path.GetFileNameWithoutExtension(fileName);
        return SaysSetup(name) || (description is not null && SaysSetup(description)) || HasBuildMarker(name);
    }

    // "DiscordSetup", "dxwebsetup", "Firefox Installer", "serverinstall_100": anywhere in the text, since
    // these words are often run together with the rest of the name.
    private static bool SaysSetup(string text)
        => text.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("install", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("bootstrapper", StringComparison.OrdinalIgnoreCase);

    // "vlc-3.0.20-win64", "python-3.12.3-amd64": a version number or the kind of PC it is for. Programs you
    // run as they are seldom carry these in their name; downloads of a release nearly always do.
    private static bool HasBuildMarker(string name)
    {
        if (NameVersion(name) is not null)
            return true;

        string[] markers = ["x64", "x86", "win64", "win32", "amd64", "arm64", "64bit", "32bit", "64-bit", "32-bit"];
        return markers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    // The installers among `candidates`, each compared with `installed`. Newest first.
    internal static IReadOnlyList<InstallerFile> Find(IEnumerable<InstallerCandidate> candidates, IReadOnlyList<InstalledProgram> installed)
    {
        var found = new List<InstallerFile>();

        foreach (var candidate in candidates)
        {
            var fileName = Path.GetFileName(candidate.FullPath);
            if (!IsInstaller(fileName, candidate.Description))
                continue;

            var name = Path.GetFileNameWithoutExtension(fileName);
            var program = Match(name, installed, candidate.Company) ??
                          (candidate.Product is { Length: > 0 } product ? Match(product, installed, candidate.Company) : null);

            // The version in the file's name is the program's ("Git-2.50.0-64-bit"); the one inside the
            // file is often the setup tool's own, so it is only used when the name has none.
            var version = NameVersion(name) ?? candidate.Version;

            var state = program is null ? InstallerState.NotFound
                : IsNewer(version, program.Version) ? InstallerState.NewerThanInstalled
                : InstallerState.Installed;

            found.Add(new InstallerFile(candidate.FullPath, candidate.Size, candidate.Modified, state, program));
        }

        found.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        return found;
    }

    // The installed program an installer's name stands for, or null. The two match when
    //   - their words are the same set ("GitHubDesktopSetup-x64" and "GitHub Desktop"), or
    //   - they read the same run together ("flstudio_win64" and "FL Studio"), or
    //   - they are the same with the publisher's words left out ("ChromeSetup" and "Google Chrome" by
    //     Google LLC). This last one needs more than the name, because "studio_setup" is not OBS Studio by
    //     OBS Project: the file itself has to say it is from that publisher (`company`).
    // When several installed versions of a program match (Python 3.10 and 3.13), the one with the highest
    // version is returned.
    internal static InstalledProgram? Match(string name, IReadOnlyList<InstalledProgram> installed, string? company = null)
    {
        var words = Words(name).ToHashSet(StringComparer.Ordinal);
        if (!Meaningful(words))
            return null;

        var joined = string.Concat(Words(name));
        InstalledProgram? best = null;

        foreach (var program in installed)
        {
            var theirs = Words(program.Name).ToHashSet(StringComparer.Ordinal);
            if (theirs.Count == 0)
                continue;

            var same = words.SetEquals(theirs) ||
                       (joined.Length >= 4 && joined == string.Concat(Words(program.Name)));

            if (!same && company is { Length: > 0 } && program.Publisher is { Length: > 0 } publisher)
            {
                var publisherWords = Words(publisher, keepAll: true).ToHashSet(StringComparer.Ordinal);
                var samePublisher = Words(company).Any(word => word.Length >= 3 && publisherWords.Contains(word));
                var mine = words.Where(word => !publisherWords.Contains(word)).ToHashSet(StringComparer.Ordinal);
                same = samePublisher && Meaningful(mine) && mine.SetEquals(theirs.Where(word => !publisherWords.Contains(word)));
            }

            if (same && (best is null || IsNewer(program.Version, best.Version)))
                best = program;
        }

        return best;
    }

    // Enough to name a program: "z" or "mb" could be anything.
    private static bool Meaningful(HashSet<string> words) => words.Count > 0 && words.Sum(word => word.Length) >= 3;

    // The words of a name, in small letters. It is cut into pieces at anything that is not a letter (so
    // digits never count), and each piece into words where capitals start one ("GitHubDesktopSetup" is
    // git, hub, desktop, setup; "MBSetup" is mb, setup). Unless keepAll, the words that are not part of a
    // program's name are left out: setup and company words always, build words only when they are a
    // whole piece by themselves.
    internal static IEnumerable<string> Words(string name, bool keepAll = false)
    {
        var i = 0;
        while (i < name.Length)
        {
            if (!char.IsLetter(name[i]))
            {
                i++;
                continue;
            }

            var end = i;
            while (end < name.Length && char.IsLetter(name[end]))
                end++;

            var piece = name[i..end];
            var words = SplitAtCapitals(piece).ToList();
            foreach (var word in words)
            {
                if (keepAll || !(SetupWords.Contains(word) || (words.Count == 1 && BuildWords.Contains(word))))
                    yield return word;
            }

            i = end;
        }
    }

    private static IEnumerable<string> SplitAtCapitals(string piece)
    {
        var start = 0;
        for (var i = 1; i < piece.Length; i++)
        {
            var capital = char.IsUpper(piece[i]);
            var afterSmall = capital && char.IsLower(piece[i - 1]);
            var lastOfCapitals = capital && char.IsUpper(piece[i - 1]) && i + 1 < piece.Length && char.IsLower(piece[i + 1]);
            if (!afterSmall && !lastOfCapitals)
                continue;

            yield return piece[start..i].ToLowerInvariant();
            start = i;
        }

        yield return piece[start..].ToLowerInvariant();
    }

    // Whether the file's version is later than the installed one. Unknown or unreadable versions: no.
    internal static bool IsNewer(string? fileVersion, string? installedVersion)
        => ParseVersion(fileVersion) is { } file && ParseVersion(installedVersion) is { } have && file > have;

    // The version written in a file's name ("python-3.12.3-amd64" gives 3.12.3), or null. It has to have a
    // dot between numbers: a lone number ("x64", "DiscordSetup (4)") is not a version.
    internal static string? NameVersion(string name)
    {
        for (var i = 0; i < name.Length; i++)
        {
            if (!char.IsAsciiDigit(name[i]) || (i > 0 && (char.IsAsciiDigit(name[i - 1]) || name[i - 1] == '.')))
                continue;

            var end = i;
            while (end < name.Length && (char.IsAsciiDigit(name[end]) ||
                   (name[end] == '.' && end + 1 < name.Length && char.IsAsciiDigit(name[end + 1]))))
                end++;

            if (name.AsSpan(i, end - i).Contains('.'))
                return name[i..end];
        }

        return null;
    }

    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // "1.90.2 (user setup)", "2.45.1.windows.1", "24.08": the leading run of numbers and dots, and of
        // those the first four numbers.
        text = text.TrimStart();
        var end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.'))
            end++;

        var parts = new int[4];
        var count = 0;
        foreach (var number in text[..end].Split('.', StringSplitOptions.RemoveEmptyEntries).Take(4))
        {
            if (number.Length > 9 || !int.TryParse(number, out parts[count]))
                return null;
            count++;
        }

        return count == 0 ? null : new Version(parts[0], parts[1], parts[2], parts[3]);
    }

    // ---- reading the disk and the registry (not covered by the tests)

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long Size, long Changed), InstallerCandidate> Details = new();

    // The installers in `folder` (not its subfolders), compared with what is installed. Reads the details
    // of every .exe there, so call it off the UI thread. A folder or file that can't be read is passed over.
    internal static IReadOnlyList<InstallerFile> Scan(string folder, CancellationToken token = default)
    {
        var candidates = new List<InstallerCandidate>();

        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    var ending = file.Extension;
                    var isProgram = ending.Equals(".exe", StringComparison.OrdinalIgnoreCase);
                    if (!isProgram && !PackageEndings.Contains(ending))
                        continue;
                    if ((file.Attributes & Skipped) != 0)
                        continue; // reading a cloud file's details would download it

                    // The details are remembered per file (until it changes), so looking again after one new
                    // download reads one file, not every setup file in the folder.
                    var known = (file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
                    if (!Details.TryGetValue(known, out var candidate))
                    {
                        string? product = null, description = null, version = null, company = null;
                        if (isProgram)
                        {
                            var details = FileVersionInfo.GetVersionInfo(file.FullName);
                            product = details.ProductName;
                            description = details.FileDescription;
                            version = details.ProductVersion ?? details.FileVersion;
                            company = details.CompanyName;
                        }

                        candidate = new InstallerCandidate(file.FullName, file.Length, file.LastWriteTime, product, description, version, company);
                        if (Details.Count > 4000)
                            Details.Clear();
                        Details[known] = candidate;
                    }

                    candidates.Add(candidate);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
                                                  System.Security.SecurityException or NotSupportedException)
                {
                    // In use, no permission, or a date Windows can't express: this one file is left out.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        return Find(candidates, ReadInstalled());
    }

    // The programs Windows lists as installed: for everyone (64-bit and 32-bit) and for this user.
    internal static IReadOnlyList<InstalledProgram> ReadInstalled()
    {
        var programs = new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
            return [];

        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        (RegistryHive Hive, RegistryView View)[] places =
        [
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default)
        ];

        foreach (var (hive, view) in places)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var list = root.OpenSubKey(uninstall);
                if (list is null)
                    continue;

                foreach (var id in list.GetSubKeyNames())
                {
                    using var entry = list.OpenSubKey(id);
                    if (entry?.GetValue("DisplayName") is not string { Length: > 0 } name)
                        continue;
                    if (entry.GetValue("SystemComponent") is 1 || entry.GetValue("ParentKeyName") is string)
                        continue; // parts of Windows, and updates of another entry

                    programs.TryAdd(name, new InstalledProgram(name.Trim(),
                        entry.GetValue("DisplayVersion") as string, entry.GetValue("Publisher") as string));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }

        return [.. programs.Values];
    }
}
