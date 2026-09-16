// Clearspace | Application-wide tag access; storage behavior lives in an independently testable store.
using System.IO;

namespace Clearspace.Services;

public static class TagService
{
    public static string TagFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clearspace", "tags.json");

    internal static TagStore Store { get; } = new(TagFilePath);

    public static event EventHandler? Changed
    {
        add => Store.Changed += value;
        remove => Store.Changed -= value;
    }

    public static string? LastSaveError => Store.LastSaveError;
    public static IReadOnlyList<TagDefinition> All => Store.All;
    public static IEnumerable<KeyValuePair<string, List<string>>> Assignments => Store.Assignments;
    public static TagDefinition? Find(string id) => Store.Find(id);
    public static TagDefinition? Resolve(string idOrName) => Store.Resolve(idOrName);
    public static TagDefinition Create(string name) => Store.Create(name);
    public static void Rename(string id, string name) => Store.Rename(id, name);
    public static void Delete(string id) => Store.Delete(id);
    public static IReadOnlyList<string> TagIdsFor(string path) => Store.TagIdsFor(path);
    public static IReadOnlyList<TagDefinition> TagsFor(string path) => Store.TagsFor(path);
    public static bool HasTag(string path, string tagId) => Store.HasTag(path, tagId);
    public static void Assign(string path, string tagId) => Store.Assign(path, tagId);
    public static void MovePath(string oldPath, string newPath) => Store.MovePath(oldPath, newPath);
    public static void Unassign(string path, string tagId) => Store.Unassign(path, tagId);
    public static void ToggleForAll(IReadOnlyList<string> paths, string tagId) => Store.ToggleForAll(paths, tagId);
    public static void ClearTags(IReadOnlyList<string> paths) => Store.ClearTags(paths);
    public static IEnumerable<string> PathsWithTag(string tagId) => Store.PathsWithTag(tagId);
    public static int PruneMissing() => Store.PruneMissing();
}
