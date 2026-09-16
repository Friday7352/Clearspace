// Clearspace | Tags and tag assignments.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Clearspace.Services;


// CS499: Tags use a single JSON file, linear lookups, and manual orphan cleanup.
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
        catch (Exception)
        {
        }

        return Brushes.Gray;
    }
}

// CS499: TagData maps to tags.json; Enhancement 3 replaces it with indexed tables.
internal sealed class TagData
{
    public List<TagDefinition> Definitions { get; set; } = [];

    public Dictionary<string, List<string>> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TagStore
{
    private readonly Func<string?> _read;
    private readonly Action<string> _write;
    private readonly Func<string, bool> _pathExists;

    public TagStore(string filePath)
        : this(() => File.Exists(filePath) ? File.ReadAllText(filePath) : null,
            json =>
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
                File.WriteAllText(filePath, json);
            }, path => File.Exists(path) || System.IO.Directory.Exists(path))
    {
    }

    internal TagStore(Func<string?> read, Action<string> write, Func<string, bool>? pathExists = null)
    {
        _read = read;
        _write = write;
        _pathExists = pathExists ?? (_ => true);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private TagData? _data;

    public event EventHandler? Changed;

    private TagData Current => _data ??= Load();
    private static List<TagDefinition> CreateDefaults() =>
    [
        new("important", "Important", "#D3A15F"),
        new("work",      "Work",      "#5B8DD9"),
        new("personal",  "Personal",  "#7FB77E"),
        new("project",   "Project",   "#B07FD9"),
        new("todo",      "To do",     "#D9705B"),
        new("reference", "Reference", "#5BB0C4"),
        new("archive",   "Archive",   "#8A8580")
    ];
    // CS499: This reads the entire JSON document; Enhancement 3 would query a database.
    private TagData Load()
    {
        try
        {
            var json = _read();
            if (json is not null)
            {
                var loaded = JsonSerializer.Deserialize<TagData>(json, Options);

                if (loaded is not null)
                {
                    loaded.Definitions ??= [];
                    loaded.Assignments = new Dictionary<string, List<string>>(
                        loaded.Assignments ?? [], StringComparer.OrdinalIgnoreCase);
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
        }

        return new TagData { Definitions = CreateDefaults() };
    }
    // CS499: Every update rewrites the file; a database write could be transactional.
    private void Save()
    {
        try
        {
            _write(JsonSerializer.Serialize(Current, Options));
            LastSaveError = null;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"Clearspace: could not save tags. {exception}");
            LastSaveError = exception.Message;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string? LastSaveError { get; private set; }


    public IReadOnlyList<TagDefinition> All => Current.Definitions;

    public TagDefinition? Find(string id)
        => Current.Definitions.FirstOrDefault(tag => tag.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public TagDefinition? Resolve(string idOrName)
        => Find(idOrName) ?? Current.Definitions.FirstOrDefault(
            tag => tag.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    public TagDefinition Create(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        var existing = Resolve(trimmed);
        if (existing is not null)
            return existing;

        var id = MakeId(trimmed);
        var tag = new TagDefinition(id, trimmed, NextColor());

        Current.Definitions.Add(tag);
        Save();
        return tag;
    }

    public void Rename(string id, string name)
    {
        var index = Current.Definitions.FindIndex(tag => tag.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return;

        Current.Definitions[index] = Current.Definitions[index] with { Name = name.Trim() };
        Save();
    }

    public void Delete(string id)
    {
        if (Current.Definitions.RemoveAll(tag => tag.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) == 0)
            return;
        // CS499: A database foreign key would remove these orphaned IDs automatically.
        foreach (var path in Current.Assignments.Keys.ToList())
        {
            var ids = Current.Assignments[path];
            if (ids.RemoveAll(value => value.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0 && ids.Count == 0)
                Current.Assignments.Remove(path);
        }

        Save();
    }

    private string MakeId(string name)
    {
        var stem = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (string.IsNullOrEmpty(stem))
            stem = "tag";

        var candidate = stem;
        var suffix = 2;
        while (Find(candidate) is not null)
            candidate = $"{stem}{suffix++}";

        return candidate;
    }

    private static readonly string[] Palette =
    [
        "#D3A15F", "#5B8DD9", "#7FB77E", "#B07FD9",
        "#D9705B", "#5BB0C4", "#C4A85B", "#C45B93"
    ];

    private string NextColor() => Palette[Current.Definitions.Count % Palette.Length];


    public IReadOnlyList<string> TagIdsFor(string path)
        => Current.Assignments.TryGetValue(path, out var ids) ? ids : [];

    public IReadOnlyList<TagDefinition> TagsFor(string path)
    {
        var ids = TagIdsFor(path);
        if (ids.Count == 0)
            return [];

        return ids.Select(Find).Where(tag => tag is not null).Cast<TagDefinition>().ToArray();
    }

    public bool HasTag(string path, string tagId)
        => TagIdsFor(path).Any(id => id.Equals(tagId, StringComparison.OrdinalIgnoreCase));

    public void Assign(string path, string tagId)
    {
        if (Find(tagId) is null || HasTag(path, tagId))
            return;

        if (!Current.Assignments.TryGetValue(path, out var ids))
            Current.Assignments[path] = ids = [];

        ids.Add(tagId);
        Save();
    }

    public void MovePath(string oldPath, string newPath)
    {
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (!Current.Assignments.TryGetValue(oldPath, out var ids) || ids.Count == 0)
            return;

        Current.Assignments.Remove(oldPath);
        var destinationIds = Current.Assignments.TryGetValue(newPath, out var existing) ? existing : [];
        Current.Assignments[newPath] = destinationIds.Concat(ids).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Save();
    }

    public void Unassign(string path, string tagId)
    {
        if (!Current.Assignments.TryGetValue(path, out var ids))
            return;

        if (ids.RemoveAll(id => id.Equals(tagId, StringComparison.OrdinalIgnoreCase)) == 0)
            return;

        if (ids.Count == 0)
            Current.Assignments.Remove(path);

        Save();
    }

    public void ToggleForAll(IReadOnlyList<string> paths, string tagId)
    {
        if (paths.Count == 0 || Find(tagId) is null)
            return;

        var everyoneHasIt = paths.All(path => HasTag(path, tagId));

        foreach (var path in paths)
        {
            if (everyoneHasIt)
            {
                if (Current.Assignments.TryGetValue(path, out var ids))
                {
                    ids.RemoveAll(id => id.Equals(tagId, StringComparison.OrdinalIgnoreCase));
                    if (ids.Count == 0)
                        Current.Assignments.Remove(path);
                }
            }
            else if (!HasTag(path, tagId))
            {
                if (!Current.Assignments.TryGetValue(path, out var ids))
                    Current.Assignments[path] = ids = [];

                ids.Add(tagId);
            }
        }

        Save();
    }

    public void ClearTags(IReadOnlyList<string> paths)
    {
        var changed = false;

        foreach (var path in paths)
            changed |= Current.Assignments.Remove(path);

        if (changed)
            Save();
    }

    public IEnumerable<KeyValuePair<string, List<string>>> Assignments => Current.Assignments;
    // CS499: This scans every assignment; Enhancement 3 would use an indexed query.
    public IEnumerable<string> PathsWithTag(string tagId)
        => Current.Assignments
            .Where(pair => pair.Value.Any(id => id.Equals(tagId, StringComparison.OrdinalIgnoreCase)))
            .Select(pair => pair.Key);

    public int PruneMissing()
    {
        var gone = Current.Assignments.Keys
            .Where(path => !_pathExists(path))
            .ToList();

        foreach (var path in gone)
            Current.Assignments.Remove(path);

        if (gone.Count > 0)
            Save();

        return gone.Count;
    }
}
