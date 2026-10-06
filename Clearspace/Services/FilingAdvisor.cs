// Clearspace | NEW (recommended folders): where a file most likely belongs, for the folder chooser.
// "Move to a folder…" opens Clearspace's own chooser (FolderPickerWindow.cs). At its top it now lists up to
// three recommended folders, each with the reason, and it opens in the first of them. Nothing is moved
// until you press Move here; a recommendation only saves the walking.
//
// What is weighed, strongest first:
//   1. Habit   - where files like this one have gone before: the same site and kind, the same site, the
//                same kind. These are FilingMemory's counts; here every folder noted is a candidate, not
//                only one with a clear majority, because this is a list to pick from and not an offer.
//   2. Title   - a folder whose name is in the file's name ("PlayLedger-main.zip" and a folder called
//                PlayLedger). The folders looked at are your pins, your own folders in Your files, every
//                folder a file has been moved to before, and the folders the index finds by the words of
//                the file's name.
//   3. Kind    - Pictures for images, Music for audio, Videos, Documents; and folders you gave a matching
//                folder type (Photos, Music, Videos, Documents, Archives & Backups, Design & 3D).
//   4. Recent  - the folders files were moved to most recently, whatever they were.
// A folder that earns points on several counts adds them up, and shows the reason that earned the most.
//
// Rank, Words and NameMatch are plain functions (tests: Clearspace.Tests/FilingAdvisorTests.cs). Notes
// copies what settings.json holds, on the UI thread; Recommend asks the index and the disk, off it.
using System.IO;
using System.Text;
using Clearspace.Models;

namespace Clearspace.Services;

/// <summary>A folder worth offering for a file, and why, in words for the user.</summary>
internal sealed record FolderRecommendation(string Folder, string Why, int Score)
{
    public string FolderName => Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder)) is { Length: > 0 } name ? name : Folder;
}

/// <summary>Everything known that bears on where one file belongs.</summary>
/// <param name="FileName">The file's name with its ending ("PlayLedger-main.zip").</param>
internal sealed record FilingClues(string FileName)
{
    /// <summary>The site it was downloaded from, when the file still says.</summary>
    public string? Site { get; init; }

    /// <summary>The folder it is in now; never recommended.</summary>
    public string? CurrentFolder { get; init; }

    /// <summary>FilingMemory's notes: per site and kind, how often files went to each folder.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, int>> Habits { get; init; } = new Dictionary<string, Dictionary<string, int>>();

    /// <summary>Folders files were moved to lately, the latest first.</summary>
    public IReadOnlyList<string> RecentFolders { get; init; } = [];

    /// <summary>Folders whose names are compared with the file's name.</summary>
    public IReadOnlyList<string> NamedFolders { get; init; } = [];

    /// <summary>Folders for this kind of file, each with its reason.</summary>
    public IReadOnlyList<(string Folder, string Why)> KindFolders { get; init; } = [];
}

internal static class FilingAdvisor
{
    internal const int StrongMatch = 60;  // every word of the folder's name is in the file's name
    internal const int WeakMatch = 26;    // they share a word
    internal const int KindPoints = 22;
    internal const int Least = 10;        // below this a folder is not worth a line
    private const int WordsAsked = 3;     // how many of the file's words the index is asked about
    private const int HitsPerWord = 12;

    // Words that say nothing about where a file belongs.
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "main", "master", "final", "copy", "new", "old", "the", "and", "for", "with", "from", "image", "images", "img",
        "file", "files", "folder", "download", "downloads", "setup", "install", "installer", "release", "latest",
        "version", "win", "windows", "x64", "x86", "amd64", "arm64", "portable", "untitled",
        "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"
    };

    // ------------------------------------------------------------------ names

    /// <summary>
    /// The telling words of a name, in small letters, each once: split at anything that is not a letter or
    /// a digit, without numbers, version marks (v2), words under three letters and filler ("main", "copy").
    /// </summary>
    internal static IReadOnlyList<string> Words(string name)
    {
        var words = new List<string>();
        var word = new StringBuilder();

        void End()
        {
            if (word.Length == 0)
                return;

            var text = word.ToString();
            word.Clear();

            var isNumber = text.All(char.IsDigit);
            var isVersion = text.Length <= 4 && text[0] == 'v' && text.Skip(1).All(char.IsDigit);
            if (text.Length < 3 || isNumber || isVersion || Filler.Contains(text) || words.Contains(text))
                return;

            words.Add(text);
        }

        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character))
                word.Append(char.ToLowerInvariant(character));
            else
                End();
        }

        End();
        return words;
    }

    /// <summary>
    /// How well a folder's name fits a file's words: StrongMatch and up when every telling word of the
    /// folder's name is among them (or the names run together are the same: "Play Ledger" and
    /// "playledger"), WeakMatch when they share a word of four letters or more, 0 otherwise.
    /// </summary>
    internal static int NameMatch(IReadOnlyList<string> fileWords, string folderName)
    {
        var folderWords = Words(folderName);
        if (fileWords.Count == 0 || folderWords.Count == 0)
            return 0;

        var shared = folderWords.Where(word => fileWords.Contains(word)).ToList();

        if (shared.Count == folderWords.Count)
            return StrongMatch + Math.Min(20, shared.Sum(word => word.Length));

        if (shared.Count == 0)
        {
            var folderRun = string.Concat(folderWords);
            var fileRun = string.Concat(fileWords);
            return folderRun.Length >= 5 && fileRun.StartsWith(folderRun, StringComparison.Ordinal) ? StrongMatch : 0;
        }

        return shared.Any(word => word.Length >= 4) ? WeakMatch : 0;
    }

    // ------------------------------------------------------------------ ranking

    /// <summary>
    /// The best folders for a file, best first, at most <paramref name="most"/>. <paramref name="exists"/>
    /// says whether a folder can be used (it is asked only about folders that would make the list).
    /// </summary>
    internal static IReadOnlyList<FolderRecommendation> Rank(FilingClues clues, Func<string, bool> exists, int most = 3)
    {
        var extension = Path.GetExtension(clues.FileName).ToLowerInvariant();
        var here = Key(clues.CurrentFolder);
        var found = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);

        void Add(string? folder, int points, string why)
        {
            var key = Key(folder);
            if (points <= 0 || key.Length == 0 || key.Equals(here, StringComparison.OrdinalIgnoreCase))
                return;

            if (!found.TryGetValue(key, out var known))
                found[key] = new Candidate(key, points, points, why, found.Count);
            else
                found[key] = known with { Total = known.Total + points, Best = Math.Max(known.Best, points), Why = points > known.Best ? why : known.Why };
        }

        // 1. Habit
        foreach (var key in FilingMemory.Keys(clues.Site, extension))
        {
            if (!clues.Habits.TryGetValue(key, out var folders) || folders.Count == 0)
                continue;

            var both = key.StartsWith("both:", StringComparison.Ordinal);
            var site = key.StartsWith("site:", StringComparison.Ordinal);
            var weight = both ? 50 : site ? 40 : 30;
            var total = folders.Values.Sum();

            foreach (var (folder, count) in folders)
            {
                if (count <= 0)
                    continue;

                var times = count == 1 ? "once" : $"{count} times";
                Add(folder, weight * count / total + 4 * Math.Min(count, 5),
                    both ? $"{extension} files from {clues.Site} have gone here {times}"
                    : site ? $"Files from {clues.Site} have gone here {times}"
                    : $"{extension} files have gone here {times}");
            }
        }

        // 2. Title
        var words = Words(Path.GetFileNameWithoutExtension(clues.FileName));
        if (words.Count > 0)
        {
            foreach (var folder in clues.NamedFolders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var match = NameMatch(words, NameOf(folder));
                if (match > 0)
                    Add(folder, match, match >= StrongMatch ? "Its name is in the file's name" : "Its name shares a word with the file's");
            }
        }

        // 3. Kind
        foreach (var (folder, why) in clues.KindFolders)
            Add(folder, KindPoints, why);

        // 4. Recent
        for (var i = 0; i < clues.RecentFolders.Count; i++)
            Add(clues.RecentFolders[i], Math.Max(4, 16 - 3 * i), "You moved a file here recently");

        return found.Values
            .Where(candidate => candidate.Total >= Least)
            .OrderByDescending(candidate => candidate.Total)
            .ThenBy(candidate => candidate.Order)
            .Where(candidate => exists(candidate.Folder))
            .Take(most)
            .Select(candidate => new FolderRecommendation(candidate.Folder, candidate.Why, candidate.Total))
            .ToList();
    }

    private sealed record Candidate(string Folder, int Total, int Best, string Why, int Order);

    private static string Key(string? folder)
        => string.IsNullOrWhiteSpace(folder) ? string.Empty : Path.TrimEndingDirectorySeparator(folder.Trim());

    private static string NameOf(string folder)
        => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder;

    // ------------------------------------------------------------------ the app's side

    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".rtf", ".odt", ".ods", ".odp", ".csv", ".epub"
    };

    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".iso"
    };

    private static readonly HashSet<string> Designs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".stl", ".3mf", ".obj", ".step", ".stp", ".fbx", ".blend", ".gcode", ".psd", ".psb", ".ai", ".xcf", ".kra", ".pdn"
    };

    /// <summary>
    /// What settings.json knows that bears on this file: the filing notes, the recent folders, your pins
    /// and own folders, and the folders for its kind. Copies, so the result can be used on another thread.
    /// Call it on the UI thread, where the settings are changed.
    /// </summary>
    internal static FilingClues Notes(string path, string? site)
    {
        var (habits, recent) = FilingMemory.Snapshot();
        var extension = Path.GetExtension(path);

        var named = new List<string>();
        named.AddRange(SettingsService.GetPinnedDirectories().Keys);
        named.AddRange(SettingsService.GetLibraryFolders());
        named.AddRange(habits.Values.SelectMany(folders => folders.Keys));
        named.AddRange(recent);

        // The kind of file: Windows' folder for it, then folders you gave the matching folder type.
        var kinds = new List<(string Folder, string Why)>();
        DirectoryViewProfile? wanted = null;

        if (MediaTypes.IsImage(extension))
        {
            wanted = DirectoryViewProfile.Photos;
            kinds.Add((KnownFolders.Pictures, "Pictures, for an image"));
        }
        else if (MediaTypes.IsVideo(extension))
        {
            wanted = DirectoryViewProfile.Videos;
            kinds.Add((KnownFolders.Videos, "Videos, for a video"));
        }
        else if (MediaTypes.IsAudio(extension))
        {
            wanted = DirectoryViewProfile.Music;
            kinds.Add((KnownFolders.Music, "Music, for a sound file"));
        }
        else if (Documents.Contains(extension))
        {
            wanted = DirectoryViewProfile.Documents;
            kinds.Add((KnownFolders.Documents, "Documents, for a document"));
        }
        else if (Archives.Contains(extension))
            wanted = DirectoryViewProfile.Archives;
        else if (Designs.Contains(extension))
            wanted = DirectoryViewProfile.Design;

        if (wanted is { } profile)
        {
            foreach (var (folder, id) in SettingsService.GetAllFolderViewProfiles().ToList())
            {
                if (FolderTypes.Find(id) is { } type && type.Base == profile)
                    kinds.Add((folder, $"One of your {type.Name} folders"));
            }
        }

        return new FilingClues(Path.GetFileName(path))
        {
            Site = site,
            CurrentFolder = Path.GetDirectoryName(path),
            Habits = habits,
            RecentFolders = recent,
            NamedFolders = named,
            KindFolders = kinds
        };
    }

    /// <summary>
    /// The recommended folders for the file the notes are about. Asks the index for folders named like the
    /// file and the disk whether the best candidates are still there, so call it off the UI thread.
    /// </summary>
    internal static IReadOnlyList<FolderRecommendation> Recommend(FilingClues notes, int most = 3)
    {
        var named = new List<string>(notes.NamedFolders);
        try
        {
            named.AddRange(IndexedFoldersNamedLike(notes.FileName));
        }
        catch (Exception)
        {
            // the index is a help, not a need: without it the other clues still count
        }

        var checkedFolders = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        return Rank(notes with { NamedFolders = named },
            folder => checkedFolders.TryGetValue(folder, out var usable) ? usable : (checkedFolders[folder] = Usable(folder)), most);
    }

    // Folders anywhere on the indexed drives whose name has one of the file's telling words in it, the
    // longest words first (they say the most). Answered from the index in memory; the disk is not read.
    private static List<string> IndexedFoldersNamedLike(string fileName)
    {
        var found = new List<string>();
        var words = Words(Path.GetFileNameWithoutExtension(fileName)).OrderByDescending(word => word.Length).Take(WordsAsked).ToList();
        if (words.Count == 0)
            return found;

        var roots = FileIndexService.IndexableRoots();
        if (roots.Length == 0)
            return found;

        foreach (var word in words)
        {
            var hits = FileIndexService.Search(SearchQuery.Parse(word + " is:folder"), roots, showHidden: false, HitsPerWord, CancellationToken.None);
            foreach (var hit in hits)
            {
                if (hit.IsFolder && !hit.IsDriveRoot && !IsNoise(hit.FullPath))
                    found.Add(hit.FullPath);
            }
        }

        return found;
    }

    /// <summary>Folders nobody files things into: Windows' own, programs', caches, and hidden-by-convention ones.</summary>
    internal static bool IsNoise(string path)
    {
        foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            if (part.StartsWith('.') || part.StartsWith('$') ||
                part.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("Program Files", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("ProgramData", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("AppData", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Temp", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // Still there, and not locked with a password right now. A folder on a network drive is taken to be
    // there without asking the network (that can take seconds); the chooser says so if it has gone.
    private static bool Usable(string folder)
    {
        try
        {
            if (FileLockRegistry.Governing(folder) is { State: LockState.Locked })
                return false;

            return FilingMemory.IsOnNetwork(folder) || Directory.Exists(folder);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
