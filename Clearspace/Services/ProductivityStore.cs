// Clearspace | NEW (productivity): what the Productivity page keeps - saved tab groups, the tabs you had
// open last time, and how often you go into each folder.
//
//  - Tab groups: a name, a colour and a list of places (one per tab). Opening one adds its tabs to the
//    window. CHANGED (groups stay as saved): a group only changes when you edit it or save tabs over it; it
//    no longer follows the window it is open in ("keep up to date" is gone).
//  - Last session: the tabs of the window you used last, written a moment after every change and when the
//    window closes, so they come back even after a crash. What was saved when Clearspace started is kept
//    aside (PreviousSession) - that is what "Reopen last session" brings back, not the tabs you have now.
//  - Folder visits: how many times you went into each folder, per day, for "Most visited". Days older than
//    a month are dropped, and so are folders not visited for two months; at most 500 folders are kept.
//    Pages that are not folders (This PC, hubs) and the insides of ZIP files are not counted.
// Everything is in %AppData%\Clearspace\productivity.json, on this PC only. It is written a couple of
// seconds after a change (one write for a burst of changes), and at once for a change to a tab group.
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clearspace.Services;

public sealed class TabGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Tab group";
    public string Color { get; set; } = ProductivityStore.Colors[0].Hex;
    public List<string> Tabs { get; set; } = [];
    public DateTime Saved { get; set; } = DateTime.Now;
    public DateTime? LastOpened { get; set; }
    // REMOVED (groups stay as saved): KeepUpToDate. A file written with it still reads; the value is ignored.
}

public sealed class FolderVisitLog
{
    public int Count { get; set; }
    public DateTime Last { get; set; }
    // "yyyyMMdd" -> visits that day
    public Dictionary<string, int> Days { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ProductivityData
{
    public List<TabGroup> TabGroups { get; set; } = [];
    public List<string> SessionTabs { get; set; } = [];
    public int SessionActive { get; set; }
    public DateTime? SessionSaved { get; set; }
    public Dictionary<string, FolderVisitLog> Visits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A saved set of tabs as it was when Clearspace started.</summary>
public sealed record SavedSession(IReadOnlyList<string> Tabs, int Active, DateTime? Saved);

/// <summary>A folder and how often it was visited lately.</summary>
public sealed record FolderVisitSummary(string Path, int RecentVisits, DateTime Last);

public static class ProductivityStore
{
    // The colours a group can have, in the order new groups get them.
    public static readonly IReadOnlyList<(string Name, string Hex)> Colors =
    [
        ("Amber", "#D3A15F"), ("Blue", "#7FB2D9"), ("Purple", "#C792C9"),
        ("Green", "#8FC79A"), ("Coral", "#E39482"), ("Grey", "#A8A299")
    ];

    // "Most visited" counts this many days back.
    public const int RecentDays = 14;
    private const int KeepDays = 31;
    private const int MostFolders = 500;
    private static readonly TimeSpan ForgetFolderAfter = TimeSpan.FromDays(60);
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    private static readonly object Gate = new();
    private static ProductivityData? s_data;
    private static SavedSession? s_previous;
    private static Timer? s_saveTimer;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clearspace", "productivity.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>A tab group was added, changed or removed. Raised on the thread that made the change (the UI thread).</summary>
    public static event Action? GroupsChanged;

    // For tests: read from and write to another file.
    internal static string? TestFilePath { get; set; }
    private static string FileToUse => TestFilePath ?? FilePath;

    internal static void ResetForTests()
    {
        lock (Gate) { s_data = null; s_previous = null; }
    }

    private static ProductivityData Data
    {
        get
        {
            lock (Gate)
            {
                if (s_data is not null)
                    return s_data;

                s_data = Load();
                s_previous = new SavedSession([.. s_data.SessionTabs], s_data.SessionActive, s_data.SessionSaved);
                return s_data;
            }
        }
    }

    private static ProductivityData Load()
    {
        try
        {
            if (File.Exists(FileToUse) &&
                JsonSerializer.Deserialize<ProductivityData>(File.ReadAllText(FileToUse), JsonOptions) is { } loaded)
            {
                // JSON doesn't keep a dictionary's comparer: put back "C:\Foo" == "c:\foo".
                loaded.Visits = new Dictionary<string, FolderVisitLog>(loaded.Visits ?? new Dictionary<string, FolderVisitLog>(), StringComparer.OrdinalIgnoreCase);
                loaded.TabGroups ??= [];
                loaded.SessionTabs ??= [];
                foreach (var group in loaded.TabGroups)
                    group.Tabs ??= [];
                return loaded;
            }
        }
        catch (Exception)
        {
            // a damaged file starts empty; the old one is overwritten at the next save
        }

        return new ProductivityData();
    }

    // ---- saving

    private static void SaveSoon()
    {
        lock (Gate)
        {
            s_saveTimer ??= new Timer(_ => SaveNow());
            s_saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes the file now (also called when a window closes).</summary>
    public static void SaveNow()
    {
        try
        {
            string json;
            lock (Gate)
            {
                if (s_data is null)
                    return;
                json = JsonSerializer.Serialize(s_data, JsonOptions);
            }

            var folder = Path.GetDirectoryName(FileToUse)!;
            Directory.CreateDirectory(folder);
            var temp = FileToUse + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FileToUse, overwrite: true);
        }
        catch (Exception)
        {
            // best effort: the next change tries again
        }
    }

    // ---- tab groups

    /// <summary>The groups, newest saved first. Copies: change them through the methods below.</summary>
    public static IReadOnlyList<TabGroup> Groups
    {
        get
        {
            lock (Gate)
                return [.. Data.TabGroups.Select(Copy)];
        }
    }

    public static TabGroup? Find(string id)
    {
        lock (Gate)
            return Data.TabGroups.FirstOrDefault(group => group.Id == id) is { } found ? Copy(found) : null;
    }

    private static TabGroup Copy(TabGroup group) => new()
    {
        Id = group.Id, Name = group.Name, Color = group.Color, Tabs = [.. group.Tabs],
        Saved = group.Saved, LastOpened = group.LastOpened
    };

    /// <summary>Saves a new group of these places and returns it. The name is made unique ("Work (2)").</summary>
    public static TabGroup Add(string name, IEnumerable<string> tabs)
    {
        TabGroup group;
        lock (Gate)
        {
            var data = Data;
            group = new TabGroup
            {
                Name = UniqueName(data, string.IsNullOrWhiteSpace(name) ? "Tab group" : name.Trim(), null),
                Color = Colors[data.TabGroups.Count % Colors.Count].Hex,
                Tabs = [.. Clean(tabs)],
                Saved = DateTime.Now
            };
            data.TabGroups.Insert(0, group);
            group = Copy(group);
        }

        Changed();
        return group;
    }

    /// <summary>Changes a group in place. False when it no longer exists.</summary>
    public static bool Update(string id, Action<TabGroup> change)
    {
        lock (Gate)
        {
            var data = Data;
            if (data.TabGroups.FirstOrDefault(group => group.Id == id) is not { } group)
                return false;

            change(group);
            group.Name = UniqueName(data, string.IsNullOrWhiteSpace(group.Name) ? "Tab group" : group.Name.Trim(), group.Id);
            group.Tabs = [.. Clean(group.Tabs)];
        }

        Changed();
        return true;
    }

    // REMOVED (groups stay as saved): Follow, which made a group take the tabs its window had.

    public static void MarkOpened(string id) => Update(id, group => group.LastOpened = DateTime.Now);

    public static bool Remove(string id)
    {
        bool removed;
        lock (Gate)
            removed = Data.TabGroups.RemoveAll(group => group.Id == id) > 0;

        if (removed)
            Changed();
        return removed;
    }

    private static void Changed()
    {
        SaveNow();
        GroupsChanged?.Invoke();
    }

    // Only places a tab can show; the Productivity page itself is not one of a group's tabs.
    internal static IEnumerable<string> Clean(IEnumerable<string> tabs)
        => tabs.Where(tab => !string.IsNullOrWhiteSpace(tab) &&
                             !tab.Equals(ExplorerLocations.ProductivityPath, StringComparison.OrdinalIgnoreCase))
               .Select(tab => tab.Trim());

    internal static string UniqueName(ProductivityData data, string name, string? exceptId)
    {
        bool Taken(string candidate) => data.TabGroups.Any(group => group.Id != exceptId &&
            group.Name.Equals(candidate, StringComparison.CurrentCultureIgnoreCase));

        if (!Taken(name))
            return name;
        for (var number = 2; ; number++)
            if (!Taken($"{name} ({number})"))
                return $"{name} ({number})";
    }

    // ---- last session

    /// <summary>The tabs saved when Clearspace started (empty when there were none).</summary>
    public static SavedSession PreviousSession
    {
        get
        {
            _ = Data;
            lock (Gate)
                return s_previous ?? new SavedSession([], 0, null);
        }
    }

    /// <summary>The window's tabs now, written a moment later (immediately with now = true).</summary>
    public static void SaveSession(IReadOnlyList<string> tabs, int active, bool now = false)
    {
        var clean = Clean(tabs).ToList();
        if (clean.Count == 0)
            return;

        lock (Gate)
        {
            var data = Data;
            data.SessionTabs = clean;
            data.SessionActive = Math.Clamp(active, 0, clean.Count - 1);
            data.SessionSaved = DateTime.Now;
        }

        if (now)
            SaveNow();
        else
            SaveSoon();
    }

    // ---- folder visits

    /// <summary>Clearspace went into a folder.</summary>
    public static void RecordVisit(string path) => RecordVisit(path, DateTime.Now);

    internal static void RecordVisit(string path, DateTime when)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase))
            return;

        path = Path.TrimEndingDirectorySeparator(path);
        if (path.Length == 2 && path[1] == ':')
            path += "\\";

        lock (Gate)
        {
            var visits = Data.Visits;
            if (!visits.TryGetValue(path, out var log))
                visits[path] = log = new FolderVisitLog();

            log.Count++;
            log.Last = when;
            var day = when.ToString("yyyyMMdd");
            log.Days[day] = log.Days.GetValueOrDefault(day) + 1;

            Trim(visits, when);
        }

        SaveSoon();
    }

    private static void Trim(Dictionary<string, FolderVisitLog> visits, DateTime now)
    {
        var oldest = now.AddDays(-KeepDays).ToString("yyyyMMdd");
        foreach (var log in visits.Values)
            foreach (var day in log.Days.Keys.Where(day => string.CompareOrdinal(day, oldest) < 0).ToList())
                log.Days.Remove(day);

        foreach (var stale in visits.Where(pair => now - pair.Value.Last > ForgetFolderAfter).Select(pair => pair.Key).ToList())
            visits.Remove(stale);

        if (visits.Count > MostFolders)
            foreach (var extra in visits.OrderBy(pair => pair.Value.Last).Take(visits.Count - MostFolders).Select(pair => pair.Key).ToList())
                visits.Remove(extra);
    }

    /// <summary>Folders visited most in the last RecentDays days, most first.</summary>
    public static IReadOnlyList<FolderVisitSummary> MostVisited(int count) => MostVisited(count, DateTime.Now);

    internal static IReadOnlyList<FolderVisitSummary> MostVisited(int count, DateTime now)
    {
        var since = now.AddDays(-(RecentDays - 1)).ToString("yyyyMMdd");
        lock (Gate)
        {
            return [.. Data.Visits
                .Select(pair => new FolderVisitSummary(pair.Key,
                    pair.Value.Days.Where(day => string.CompareOrdinal(day.Key, since) >= 0).Sum(day => day.Value),
                    pair.Value.Last))
                .Where(folder => folder.RecentVisits > 0)
                .OrderByDescending(folder => folder.RecentVisits)
                .ThenByDescending(folder => folder.Last)
                .Take(count)];
        }
    }

    /// <summary>Folders visited last, newest first.</summary>
    public static IReadOnlyList<FolderVisitSummary> LastVisited(int count)
    {
        lock (Gate)
        {
            return [.. Data.Visits
                .OrderByDescending(pair => pair.Value.Last)
                .Take(count)
                .Select(pair => new FolderVisitSummary(pair.Key, pair.Value.Count, pair.Value.Last))];
        }
    }

    /// <summary>Forgets every folder visit ("Clear" on the page).</summary>
    public static void ClearVisits()
    {
        lock (Gate)
            Data.Visits.Clear();
        SaveNow();
    }
}
