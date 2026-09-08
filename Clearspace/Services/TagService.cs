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

public static class TagService
{
    private static readonly string Directory_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Clearspace");
    // CS499 Enhancement 3: replace tags.json with tags.db.
    private static readonly string FilePath = Path.Combine(Directory_, "tags.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static TagData? _data;

    public static event EventHandler? Changed;

    private static TagData Current => _data ??= Load();

    public static string TagFilePath => FilePath;

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
    private static TagData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<TagData>(File.ReadAllText(FilePath), Options);

                if (loaded is not null)
                {
                    loaded.Definitions ??= [];
                    loaded.Assignments = new Dictionary<string, List<string>>(
                        loaded.Assignments ?? [], StringComparer.OrdinalIgnoreCase);

                    if (loaded.Definitions.Count == 0)
                        loaded.Definitions = CreateDefaults();

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
    private static void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory_);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"Clearspace: could not save tags. {exception}");
            LastSaveError = exception.Message;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static string? LastSaveError { get; private set; }


    public static IReadOnlyList<TagDefinition> All => Current.Definitions;

    public static TagDefinition? Find(string id)
        => Current.Definitions.FirstOrDefault(tag => tag.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static TagDefinition? Resolve(string idOrName)
        => Find(idOrName) ?? Current.Definitions.FirstOrDefault(
            tag => tag.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    public static TagDefinition Create(string name)
    {
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

    public static void Rename(string id, string name)
    {
        var index = Current.Definitions.FindIndex(tag => tag.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return;

        Current.Definitions[index] = Current.Definitions[index] with { Name = name.Trim() };
        Save();
    }

    public static void Delete(string id)
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

    private static string MakeId(string name)
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

    private static string NextColor() => Palette[Current.Definitions.Count % Palette.Length];


    public static IReadOnlyList<string> TagIdsFor(string path)
        => Current.Assignments.TryGetValue(path, out var ids) ? ids : [];

    public static IReadOnlyList<TagDefinition> TagsFor(string path)
    {
        var ids = TagIdsFor(path);
        if (ids.Count == 0)
            return [];

        return ids.Select(Find).Where(tag => tag is not null).Cast<TagDefinition>().ToArray();
    }

    public static bool HasTag(string path, string tagId)
        => TagIdsFor(path).Any(id => id.Equals(tagId, StringComparison.OrdinalIgnoreCase));

    public static void Assign(string path, string tagId)
    {
        if (Find(tagId) is null || HasTag(path, tagId))
            return;

        if (!Current.Assignments.TryGetValue(path, out var ids))
            Current.Assignments[path] = ids = [];

        ids.Add(tagId);
        Save();
    }

    public static void MovePath(string oldPath, string newPath)
    {
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (!Current.Assignments.TryGetValue(oldPath, out var ids) || ids.Count == 0)
            return;

        Current.Assignments.Remove(oldPath);
        Current.Assignments[newPath] = ids;
        Save();
    }

    public static void Unassign(string path, string tagId)
    {
        if (!Current.Assignments.TryGetValue(path, out var ids))
            return;

        if (ids.RemoveAll(id => id.Equals(tagId, StringComparison.OrdinalIgnoreCase)) == 0)
            return;

        if (ids.Count == 0)
            Current.Assignments.Remove(path);

        Save();
    }

    public static void ToggleForAll(IReadOnlyList<string> paths, string tagId)
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

    public static void ClearTags(IReadOnlyList<string> paths)
    {
        var changed = false;

        foreach (var path in paths)
            changed |= Current.Assignments.Remove(path);

        if (changed)
            Save();
    }

    public static IEnumerable<KeyValuePair<string, List<string>>> Assignments => Current.Assignments;
    // CS499: This scans every assignment; Enhancement 3 would use an indexed query.
    public static IEnumerable<string> PathsWithTag(string tagId)
        => Current.Assignments
            .Where(pair => pair.Value.Any(id => id.Equals(tagId, StringComparison.OrdinalIgnoreCase)))
            .Select(pair => pair.Key);

    public static int PruneMissing()
    {
        var gone = Current.Assignments.Keys
            .Where(path => !File.Exists(path) && !System.IO.Directory.Exists(path))
            .ToList();

        foreach (var path in gone)
            Current.Assignments.Remove(path);

        if (gone.Count > 0)
            Save();

        return gone.Count;
    }
}
