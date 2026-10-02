// Clearspace | Transactional tag operations backed by indexed SQLite tables.
using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Microsoft.Data.Sqlite;

namespace Clearspace.Services;

public sealed record TagDefinition(string Id, string Name, string Color)
{
    private Brush? _brush;
    [JsonIgnore]
    public Brush Brush => _brush ??= CreateBrush(Color);

    private static Brush CreateBrush(string color)
    {
        try
        {
            if (ColorConverter.ConvertFromString(color) is Color parsed)
            {
                var brush = new SolidColorBrush(parsed);
                brush.Freeze();
                return brush;
            }
        }
        catch (Exception) { }
        return Brushes.Gray;
    }
}

public sealed class TagStore : IDisposable
{
    private readonly TagDatabase _database;
    private readonly Func<string, bool> _pathExists;

    public TagStore(string databasePath, string? legacyJsonPath = null)
        : this(databasePath, legacyJsonPath, path => File.Exists(path) || Directory.Exists(path)) { }

    internal TagStore(string databasePath, string? legacyJsonPath, Func<string, bool> pathExists)
    {
        _database = new TagDatabase(databasePath, legacyJsonPath);
        _pathExists = pathExists;
    }

    internal static readonly TagDefinition[] Defaults =
    [
        new("important", "Important", "#D3A15F"), new("work", "Work", "#5B8DD9"),
        new("personal", "Personal", "#7FB77E"), new("project", "Project", "#B07FD9"),
        new("todo", "To do", "#D9705B"), new("reference", "Reference", "#5BB0C4"),
        new("archive", "Archive", "#8A8580")
    ];
    private static readonly string[] Palette =
        ["#D3A15F", "#5B8DD9", "#7FB77E", "#B07FD9", "#D9705B", "#5BB0C4", "#C4A85B", "#C45B93"];

    public event EventHandler? Changed;
    public string? LastSaveError { get; private set; }

    // NEW (search fix): reads are served from an in-memory snapshot of all tags and assignments.
    // Before, every read was its own SQLite transaction under the store's lock: showing search results
    // ran one query per result (up to 10,000), and each keystroke's query parsing had to wait for its
    // turn on the same lock, which made typing stutter. The snapshot is rebuilt after this store's own
    // writes, and when PRAGMA data_version shows that another connection changed the database - so
    // separate stores and processes still see each other's changes, as before.
    private sealed record Snapshot(
        long Version,
        TagDefinition[] Tags,
        Dictionary<string, TagDefinition> ById,
        Dictionary<string, List<string>> ByPath,
        Dictionary<string, HashSet<string>> ByTag);

    private volatile Snapshot? _snapshot;
    private static readonly HashSet<string> NoPaths = new(StringComparer.OrdinalIgnoreCase);

    private Snapshot Current()
    {
        var version = Run(false, sql => sql.Number("PRAGMA data_version"));
        var snapshot = _snapshot;
        if (snapshot is not null && snapshot.Version == version) return snapshot;

        snapshot = Run(false, sql =>
        {
            var tags = sql.Query("SELECT Id, Name, Color FROM Tags ORDER BY rowid", ReadTag).ToArray();
            var byId = new Dictionary<string, TagDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in tags) byId.TryAdd(tag.Id, tag);
            var byPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var byTag = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, id) in sql.Query("SELECT Path, TagId FROM PathTags ORDER BY rowid", r => (r.GetString(0), r.GetString(1))))
            {
                if (!byPath.TryGetValue(path, out var ids)) byPath[path] = ids = [];
                ids.Add(id);
                if (!byTag.TryGetValue(id, out var paths)) byTag[id] = paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                paths.Add(path);
            }
            return new Snapshot(sql.Number("PRAGMA data_version"), tags, byId, byPath, byTag);
        });

        _snapshot = snapshot;
        return snapshot;
    }

    private T Run<T>(bool write, Func<TagSql, T> action)
    {
        (T Value, bool Changed) result;
        try
        {
            result = _database.Run(write, action);
            if (write)
            {
                LastSaveError = null;
                _snapshot = null; // NEW: our own commits do not change data_version for this connection
            }
        }
        catch (Exception exception) when (exception is SqliteException or IOException or InvalidDataException or
            UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            LastSaveError = exception.Message;
            throw new InvalidOperationException(
                "Clearspace could not read or save tags. No changes from this operation were saved. " +
                "The original tags.json, if present, has been kept. " + exception.Message, exception);
        }
        // Notify only after a successful commit, outside the database connection lock.
        if (write && result.Changed) Changed?.Invoke(this, EventArgs.Empty);
        return result.Value;
    }

    private void Write(Action<TagSql> action) => Run(true, sql => { action(sql); return 0; });
    private static TagDefinition ReadTag(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2));
    private static TagDefinition? Find(TagSql sql, string id) =>
        sql.Query("SELECT Id, Name, Color FROM Tags WHERE Id = $p0", ReadTag, id).FirstOrDefault();
    private static TagDefinition? Resolve(TagSql sql, string value) => Find(sql, value) ??
        sql.Query("SELECT Id, Name, Color FROM Tags WHERE Name = $p0 ORDER BY rowid LIMIT 1", ReadTag, value).FirstOrDefault();
    private static bool HasTag(TagSql sql, string path, string id) =>
        sql.Number("SELECT COUNT(*) FROM PathTags WHERE Path = $p0 AND TagId = $p1", path, id) > 0;
    private static void RemoveUnusedPaths(TagSql sql) => sql.Execute(
        "DELETE FROM Paths WHERE NOT EXISTS (SELECT 1 FROM PathTags WHERE PathTags.Path = Paths.Path)");
    private static void RemoveUnusedPath(TagSql sql, string path) => sql.Execute(
        "DELETE FROM Paths WHERE Path = $p0 AND NOT EXISTS (SELECT 1 FROM PathTags WHERE PathTags.Path = Paths.Path)", path);

    // CHANGED (search fix): reads come from the snapshot.
    public IReadOnlyList<TagDefinition> All => Current().Tags;
    public TagDefinition? Find(string id) => Current().ById.GetValueOrDefault(id);
    public TagDefinition? Resolve(string idOrName)
    {
        var snapshot = Current();
        return snapshot.ById.GetValueOrDefault(idOrName)
            ?? Array.Find(snapshot.Tags, tag => tag.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
    }

    private static TagDefinition Create(TagSql sql, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (Resolve(sql, name) is { } existing) return existing;
        var stem = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (stem.Length == 0) stem = "tag";
        var id = stem;
        for (var suffix = 2; Find(sql, id) is not null; suffix++) id = $"{stem}{suffix}";
        var tag = new TagDefinition(id, name, Palette[sql.Number("SELECT COUNT(*) FROM Tags") % Palette.Length]);
        sql.Execute("INSERT INTO Tags(Id, Name, Color) VALUES($p0, $p1, $p2)", tag.Id, tag.Name, tag.Color);
        return tag;
    }

    public TagDefinition Create(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Run(true, sql => Create(sql, name));
    }

    public TagDefinition CreateForPaths(string name, IReadOnlyList<string> paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidatePaths(paths);
        return Run(true, sql =>
        {
            var tag = Create(sql, name);
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase)) Assign(sql, path, tag.Id);
            return tag;
        });
    }

    public void Rename(string id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Write(sql => sql.Execute("UPDATE Tags SET Name = $p0 WHERE Id = $p1 AND Name COLLATE BINARY <> $p0", name.Trim(), id));
    }

    public void Delete(string id) => Write(sql =>
    {
        sql.Execute("DELETE FROM Tags WHERE Id = $p0", id);
        RemoveUnusedPaths(sql);
    });

    public IReadOnlyList<string> TagIdsFor(string path)
        => Current().ByPath.TryGetValue(path, out var ids) ? ids.ToArray() : [];

    public IReadOnlyList<TagDefinition> TagsFor(string path)
    {
        var snapshot = Current();
        if (!snapshot.ByPath.TryGetValue(path, out var ids)) return [];
        var tags = new List<TagDefinition>(ids.Count);
        foreach (var id in ids)
            if (snapshot.ById.TryGetValue(id, out var tag)) tags.Add(tag);
        return tags;
    }

    public bool HasTag(string path, string tagId)
        => Current().ByTag.TryGetValue(tagId, out var paths) && paths.Contains(path);

    private static void Assign(TagSql sql, string path, string id)
    {
        if (Find(sql, id) is null) return;
        sql.Execute("INSERT INTO Paths(Path) VALUES($p0) ON CONFLICT DO NOTHING", path);
        sql.Execute("INSERT INTO PathTags(Path, TagId) VALUES($p0, $p1) ON CONFLICT DO NOTHING", path, id);
    }

    public void Assign(string path, string tagId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Write(sql => Assign(sql, path, tagId));
    }

    public void MovePath(string oldPath, string newPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return;
        Write(sql =>
        {
            if (sql.Number("SELECT COUNT(*) FROM Paths WHERE Path = $p0", oldPath) == 0) return;
            sql.Execute("INSERT INTO Paths(Path) VALUES($p0) ON CONFLICT DO NOTHING", newPath);
            sql.Execute("""
                INSERT INTO PathTags(Path, TagId) SELECT $p0, TagId FROM PathTags WHERE Path = $p1
                ON CONFLICT DO NOTHING
                """, newPath, oldPath);
            sql.Execute("DELETE FROM Paths WHERE Path = $p0", oldPath);
        });
    }

    public void Unassign(string path, string tagId) => Write(sql =>
    {
        sql.Execute("DELETE FROM PathTags WHERE Path = $p0 AND TagId = $p1", path, tagId);
        RemoveUnusedPath(sql, path);
    });

    public void ToggleForAll(IReadOnlyList<string> paths, string tagId)
    {
        ValidatePaths(paths);
        if (paths.Count == 0) return;
        Write(sql =>
        {
            if (Find(sql, tagId) is null) return;
            var remove = paths.All(path => HasTag(sql, path, tagId));
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (remove)
                {
                    sql.Execute("DELETE FROM PathTags WHERE Path = $p0 AND TagId = $p1", path, tagId);
                    RemoveUnusedPath(sql, path);
                }
                else Assign(sql, path, tagId);
            }
        });
    }

    public void ClearTags(IReadOnlyList<string> paths)
    {
        ValidatePaths(paths);
        Write(sql =>
        {
            foreach (var path in paths) sql.Execute("DELETE FROM Paths WHERE Path = $p0", path);
        });
    }

    private static void ValidatePaths(IReadOnlyList<string> paths)
    {
        foreach (var path in paths) ArgumentException.ThrowIfNullOrWhiteSpace(path);
    }

    public IEnumerable<KeyValuePair<string, List<string>>> Assignments
        => Current().ByPath.Select(pair => new KeyValuePair<string, List<string>>(pair.Key, [.. pair.Value])).ToArray();

    public IEnumerable<string> PathsWithTag(string tagId)
        => Current().ByTag.TryGetValue(tagId, out var paths) ? paths.ToArray() : [];

    // CHANGED (search fix): the snapshot's sets are shared, not copied; callers only read them.
    internal Dictionary<string, HashSet<string>> SnapshotPathsForTags(IEnumerable<string> tagIds)
    {
        var snapshot = Current();
        return tagIds.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(id => id,
            id => snapshot.ByTag.TryGetValue(id, out var paths) ? paths : NoPaths, StringComparer.OrdinalIgnoreCase);
    }

    public int PruneMissing()
    {
        // Filesystem checks do not hold the SQLite write lock. Offline drives can look like missing files.
        var paths = Run(false, sql => sql.Query("SELECT Path FROM Paths", r => r.GetString(0)));
        var missing = paths.Where(path => !_pathExists(path)).ToArray();
        return Run(true, sql =>
        {
            var count = 0;
            foreach (var path in missing) count += sql.Execute("DELETE FROM Paths WHERE Path = $p0", path);
            return count;
        });
    }

    public void Dispose() => _database.Dispose();
}
