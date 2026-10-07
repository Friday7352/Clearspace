// Clearspace | NEW (backup contents): which backup options suit what is being backed up.
// The New backup window (FileBackupEditWindow.cs) has a "Contents" row: Documents / Photos & media /
// Projects / A mix. Choosing one sets the options recommended for it, so the kinds of backup need not be
// known. Clearspace also looks at what was picked to back up and chooses the row's button by itself.
// CHANGED: this replaces the "Protects against" row (Services/FileBackupGoals.cs, removed), which asked
// what could go wrong instead of what is in the folder.
//
//   Documents        Word, Excel, PDFs, notes: small files that change often.
//                    Keep the last 30 backups, so an older version of a document can be had back.
//                    Skip Office's lock files (~$*) and *.tmp.
//   Photos & media   Pictures, videos, music: large files that rarely change and do not shrink in a zip.
//                    One copy (Clone); what is deleted or changed stays in the backup for 90 days.
//                    Skip Thumbs.db.
//   Projects         Code, Unity and other game projects: very many small files that change all the time,
//                    and folders the tools rebuild by themselves.
//                    Keep the last 10 backups. Skip the folders that get rebuilt, by what was found:
//                    Unity (Library, Temp, Logs, obj, MemoryCaptures, .vs), .NET (.vs, obj),
//                    Node (node_modules), Python (__pycache__), Unreal (Intermediate, DerivedDataCache, .vs),
//                    Godot (.godot). Only names that are safe to lose: "bin" and "Build" are left alone,
//                    because folders with those names often hold things nobody can rebuild.
//   A mix            A bit of everything, or a whole drive. One copy; deleted files are kept 30 days.
//
// None of them is zipped (a zip packs everything again on every run), and all of them run daily unless a
// schedule was chosen already. Where the backup goes matters for the ones that keep several backups: on
// NTFS, dated copies share their unchanged files, so "the last 30" costs little. A drive that stores
// every dated copy in full (exFAT and FAT sticks) gets one copy plus the earlier versions of what changed
// instead, which only takes room for what changed.
//
// Detect looks at the folder: is it, or are most of its folders, a project (FileBackupContents.Project
// reads that from the names in a folder); if not, what kind most of a sample of its files is (Guess, by
// file extension). It looks at most three folders deep, at a limited number of files and for a limited
// time, and never opens a file. A whole drive is "A mix" without looking.
//
// Guess, Project, Advise and Apply work on plain values and read no disk.
// Tests: Clearspace.Tests/FileBackupTests.cs.
using System.Diagnostics;
using System.IO;
using Clearspace.Models;

namespace Clearspace.Services;

internal enum FileBackupContent { Documents, Media, Projects, Mixed }

/// <summary>The tools whose projects were found, which decides the folders a Projects backup leaves out.</summary>
[Flags]
internal enum FileBackupTools
{
    None = 0,
    Unity = 1,
    DotNet = 2,
    Node = 4,
    Python = 8,
    Unreal = 16,
    Godot = 32
}

/// <summary>What Detect made of a file or folder.</summary>
internal readonly record struct FileBackupFinding(FileBackupContent Content, FileBackupTools Tools);

/// <summary>The options recommended for a kind of content.</summary>
/// <param name="Keep">The "Keep" row: only the latest run, or the last so many.</param>
/// <param name="KeepRemoved">One copy: files a run removes are kept.</param>
/// <param name="KeepVersions">One copy: earlier versions of changed files are kept.</param>
/// <param name="KeepDays">For how long those two are kept.</param>
/// <param name="Schedule">How often, used when no schedule was chosen yet.</param>
/// <param name="Skip">Names to leave out.</param>
/// <param name="Why">One or two short sentences for the window: what was set.</param>
internal sealed record FileBackupAdvice(
    FileBackupContent Content,
    FileBackupRetention Keep,
    bool KeepRemoved,
    bool KeepVersions,
    int KeepDays,
    string Schedule,
    IReadOnlyList<string> Skip,
    string Why);

internal static class FileBackupContents
{
    internal const int DocumentRuns = 30;   // dated copies kept of documents
    internal const int ProjectRuns = 10;    // and of projects, which are far bigger
    internal const int LongDays = 90;       // how long earlier versions of documents, photos and media are kept
    internal const int ShortDays = 30;      // and of everything else

    // How far Detect looks.
    private const int DeepestFolder = 3;
    private const int DeepestProject = 2;
    private const int MostFolders = 300;
    private const int MostFiles = 2000;
    private static readonly TimeSpan LongestLook = TimeSpan.FromSeconds(2);

    internal static readonly FileBackupContent[] All =
        [FileBackupContent.Documents, FileBackupContent.Media, FileBackupContent.Projects, FileBackupContent.Mixed];

    internal static string Name(FileBackupContent content) => content switch
    {
        FileBackupContent.Documents => "Documents",
        FileBackupContent.Media => "Photos & media",
        FileBackupContent.Projects => "Projects",
        _ => "A mix"
    };

    /// <summary>A button's tooltip: what counts as this, and why it is backed up the way it is.</summary>
    internal static string Hint(FileBackupContent content) => content switch
    {
        FileBackupContent.Documents =>
            "Word, Excel, PDFs, notes.\nSmall files that change often, so several backups are kept to go back to.",
        FileBackupContent.Media =>
            "Pictures, videos and music.\nLarge files that rarely change and do not shrink in a zip, so one copy is kept, and what you delete or change stays for a while.",
        FileBackupContent.Projects =>
            "Code, Unity and other game projects, anything with very many small files.\nSeveral backups are kept to go back to, and folders that get rebuilt are skipped.",
        _ => "A bit of everything, or a whole drive.\nOne copy; what you delete stays for a while."
    };

    // ------------------------------------------------------------------ by file extension

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".docm", ".dot", ".dotx", ".rtf", ".odt", ".txt", ".md", ".pdf", ".xps", ".epub", ".tex",
        ".xls", ".xlsx", ".xlsm", ".xlsb", ".csv", ".ods", ".ppt", ".pptx", ".pptm", ".odp", ".one", ".pub",
        ".pages", ".numbers", ".key", ".wpd", ".wps", ".msg", ".eml"
    };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".vb", ".fs", ".c", ".cc", ".cpp", ".h", ".hpp", ".java", ".kt", ".py", ".js", ".jsx", ".ts", ".tsx",
        ".go", ".rs", ".rb", ".php", ".swift", ".lua", ".gd", ".shader", ".css", ".scss", ".sh", ".ps1",
        ".unity", ".prefab", ".uasset"
    };

    // Beside photos without being one: what a camera or an editor writes next to them.
    private static readonly HashSet<string> MediaSidecars = new(StringComparer.OrdinalIgnoreCase) { ".xmp", ".thm", ".lrv", ".srt", ".cue", ".m3u" };

    // Files that say nothing about a folder.
    private static bool SaysNothing(string name)
        => name.StartsWith("~$", StringComparison.Ordinal) ||
           name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);

    private static FileBackupContent? KindOf(string name)
    {
        if (SaysNothing(name))
            return null;

        var extension = Path.GetExtension(name);
        if (extension.Length == 0)
            return FileBackupContent.Mixed;
        if (MediaTypes.IsImage(extension) || MediaTypes.IsVideo(extension) || MediaTypes.IsAudio(extension) || MediaSidecars.Contains(extension))
            return FileBackupContent.Media;
        if (DocumentExtensions.Contains(extension))
            return FileBackupContent.Documents;
        return CodeExtensions.Contains(extension) ? FileBackupContent.Projects : FileBackupContent.Mixed;
    }

    /// <summary>
    /// What a set of files mostly is, by their names: documents, photos and media, or code when more than
    /// half of them are; otherwise a mix.
    /// </summary>
    internal static FileBackupContent Guess(IEnumerable<string> fileNames)
    {
        var counts = new int[All.Length];
        var total = 0;
        foreach (var name in fileNames)
        {
            if (KindOf(name) is not { } kind)
                continue;
            counts[(int)kind]++;
            total++;
        }

        foreach (var content in new[] { FileBackupContent.Media, FileBackupContent.Documents, FileBackupContent.Projects })
        {
            if (counts[(int)content] * 2 > total)
                return content;
        }

        return FileBackupContent.Mixed;
    }

    // ------------------------------------------------------------------ projects

    private static readonly string[] ProjectFolders = [".git", ".svn", ".hg"];
    private static readonly string[] ProjectFiles = ["CMakeLists.txt", "Cargo.toml", "go.mod", "pom.xml", "build.gradle", "build.gradle.kts", "Makefile"];
    private static readonly string[] DotNetFiles = [".sln", ".slnx", ".csproj", ".vbproj", ".fsproj", ".vcxproj"];
    private static readonly string[] PythonFiles = ["pyproject.toml", "requirements.txt", "setup.py"];

    /// <summary>
    /// Whether a folder is the top of a project, going by the names of the folders and files straight in
    /// it, and which tools it belongs to.
    /// </summary>
    internal static bool Project(IEnumerable<string> folderNames, IEnumerable<string> fileNames, out FileBackupTools tools)
    {
        var folders = new HashSet<string>(folderNames, StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(fileNames, StringComparer.OrdinalIgnoreCase);
        tools = FileBackupTools.None;

        if (folders.Contains("Assets") && folders.Contains("ProjectSettings"))
            tools |= FileBackupTools.Unity;
        if (folders.Contains(".vs") || files.Any(name => DotNetFiles.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)))
            tools |= FileBackupTools.DotNet;
        if (files.Contains("package.json") || folders.Contains("node_modules"))
            tools |= FileBackupTools.Node;
        if (PythonFiles.Any(files.Contains) || folders.Contains("__pycache__"))
            tools |= FileBackupTools.Python;
        if (files.Any(name => name.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase)))
            tools |= FileBackupTools.Unreal;
        if (files.Contains("project.godot"))
            tools |= FileBackupTools.Godot;

        return tools != FileBackupTools.None || ProjectFolders.Any(folders.Contains) || ProjectFiles.Any(files.Contains);
    }

    /// <summary>
    /// The folders a tool makes again by itself, which a backup of its projects can leave out. A name is
    /// left out wherever it is found, so only names that are safe to lose are here.
    /// </summary>
    internal static List<string> Rebuilt(FileBackupTools tools)
    {
        var names = new List<string>();
        void Add(params string[] more)
        {
            foreach (var name in more)
            {
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
            }
        }

        if (tools.HasFlag(FileBackupTools.Unity))
            Add("Library", "Temp", "Logs", "obj", "MemoryCaptures", ".vs");
        if (tools.HasFlag(FileBackupTools.Unreal))
            Add("Intermediate", "DerivedDataCache", ".vs");
        if (tools.HasFlag(FileBackupTools.Godot))
            Add(".godot");
        if (tools.HasFlag(FileBackupTools.DotNet))
            Add(".vs", "obj");
        if (tools.HasFlag(FileBackupTools.Node))
            Add("node_modules");
        if (tools.HasFlag(FileBackupTools.Python))
            Add("__pycache__");
        return names;
    }

    // ------------------------------------------------------------------ the recommendation

    /// <summary>
    /// The options recommended for a kind of content.
    /// </summary>
    /// <param name="tools">Projects: the tools found, for the folders to leave out.</param>
    /// <param name="sharesSpace">Unchanged files take no extra room in dated copies on the drive the
    /// backup goes to (NTFS; assumed while the drive is not known).</param>
    internal static FileBackupAdvice Advise(FileBackupContent content, FileBackupTools tools, bool sharesSpace)
    {
        const string daily = FileBackupSchedule.Daily;
        switch (content)
        {
            case FileBackupContent.Documents:
            {
                string[] skip = ["~$*", "*.tmp"];
                return sharesSpace
                    ? new FileBackupAdvice(content, new FileBackupRetention(DocumentRuns), true, false, ShortDays, daily, skip,
                        $"Keeps the last {DocumentRuns} backups, so you can go back to an older version of a document.")
                    : new FileBackupAdvice(content, FileBackupRetention.Latest, true, true, LongDays, daily, skip,
                        $"Keeps one copy, and earlier versions of changed or deleted documents for {LongDays} days.");
            }

            case FileBackupContent.Media:
                return new FileBackupAdvice(content, FileBackupRetention.Latest, true, true, LongDays, daily, ["Thumbs.db"],
                    $"Keeps one copy, and what you delete or change for {LongDays} days.");

            case FileBackupContent.Projects:
            {
                var skip = Rebuilt(tools);
                var skips = skip.Count == 0 ? string.Empty
                    : " Skips folders that get rebuilt: " + string.Join(", ", skip.Take(3)) + (skip.Count > 3 ? "…" : ".");
                return sharesSpace
                    ? new FileBackupAdvice(content, new FileBackupRetention(ProjectRuns), true, false, ShortDays, daily, skip,
                        $"Keeps the last {ProjectRuns} backups to go back to." + skips)
                    : new FileBackupAdvice(content, FileBackupRetention.Latest, true, true, ShortDays, daily, skip,
                        $"Keeps one copy, and earlier versions of changed or deleted files for {ShortDays} days." + skips);
            }

            default:
                return new FileBackupAdvice(content, FileBackupRetention.Latest, true, false, ShortDays, daily, [],
                    $"Keeps one copy, and what you delete for {ShortDays} days.");
        }
    }

    /// <summary>
    /// Sets a new backup up as recommended: a normal copy (not zipped), what is kept, how often, and the
    /// names to skip. A schedule that is on is never changed, and with <paramref name="schedule"/> false
    /// (how often was chosen by hand, "Manual" included) none is. Names put there by the recommendation
    /// before this one (<paramref name="previous"/>) are taken out again; names typed by hand stay.
    /// </summary>
    internal static void Apply(FileBackupPlan plan, FileBackupAdvice advice, FileBackupAdvice? previous = null, bool schedule = true)
    {
        FileBackupRules.Shape(plan, zipped: false, advice.Keep, FileBackupType.Clone);
        plan.KeepRemoved = advice.KeepRemoved;
        plan.KeepVersions = advice.KeepVersions;
        plan.KeepDays = advice.KeepDays;

        if (schedule && plan.Schedule.Kind is not (FileBackupSchedule.Hourly or FileBackupSchedule.Daily or FileBackupSchedule.Weekly or FileBackupSchedule.Custom))
            plan.Schedule.Kind = advice.Schedule;

        var names = plan.Exclude
            .Where(name => previous is null || !previous.Skip.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        foreach (var name in advice.Skip)
        {
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }
        plan.Exclude = names;
    }

    // ------------------------------------------------------------------ looking at what is backed up

    /// <summary>
    /// What a file, folder or drive mostly holds. Null when it is not there (a path half typed). Reads
    /// names only, a limited number of them, so call it off the UI thread and expect it back soon.
    /// </summary>
    internal static FileBackupFinding? Detect(string source, CancellationToken token = default)
    {
        try
        {
            var path = FileBackupPaths.Normalize(source);
            if (path.Length == 0 || !Path.IsPathFullyQualified(path))
                return null;
            if (File.Exists(path))
                return new FileBackupFinding(Guess([Path.GetFileName(path)]), FileBackupTools.None);
            if (!Directory.Exists(path))
                return null;
            if (FileBackupPaths.IsDriveRoot(path))
                return new FileBackupFinding(FileBackupContent.Mixed, FileBackupTools.None);

            return Look(path, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static FileBackupFinding Look(string top, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var tools = FileBackupTools.None;
        var sample = new List<string>();
        var nextTop = 0;                            // numbers the folders straight in the top one
        var topsLooked = 0;                         // how many of those were looked at
        var projectTops = new HashSet<int>();       // and which of them are, or hold, a project

        // (folder, how deep, which top-level folder it is in; -1 for the top itself)
        var waiting = new Queue<(string Folder, int Depth, int Top)>();
        waiting.Enqueue((top, 0, -1));
        var looked = 0;

        while (waiting.Count > 0 && looked < MostFolders && clock.Elapsed < LongestLook)
        {
            token.ThrowIfCancellationRequested();
            var (folder, depth, inTop) = waiting.Dequeue();
            looked++;
            if (depth == 1)
                topsLooked++;

            var folders = new List<(string Name, bool Enter)>();
            var files = new List<string>();
            try
            {
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        // Named for what it says about the folder; entered only when it is an ordinary one.
                        var ordinary = (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0 &&
                                       !entry.Name.StartsWith('.') && !entry.Name.StartsWith('$');
                        folders.Add((entry.Name, ordinary));
                    }
                    else if (files.Count < MostFiles)
                    {
                        files.Add(entry.Name);
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue; // a folder that cannot be read says nothing
            }

            if (depth <= DeepestProject && Project(folders.Select(one => one.Name), files, out var found))
            {
                tools |= found;
                if (depth == 0)
                    return new FileBackupFinding(FileBackupContent.Projects, tools);

                projectTops.Add(inTop);
                continue; // a project is not looked into: it can be enormous, and what it is has been found
            }

            if (sample.Count < MostFiles)
                sample.AddRange(files.Take(MostFiles - sample.Count));

            if (depth >= DeepestFolder)
                continue;
            foreach (var (name, enter) in folders)
            {
                if (!enter)
                    continue;
                var index = depth == 0 ? nextTop++ : inTop;
                waiting.Enqueue((Path.Combine(folder, name), depth + 1, index));
            }
        }

        // Projects when at least half of the folders in it are projects (or hold them): a folder of projects.
        if (projectTops.Count > 0 && projectTops.Count * 2 >= topsLooked)
            return new FileBackupFinding(FileBackupContent.Projects, tools);

        return new FileBackupFinding(Guess(sample), tools);
    }
}
