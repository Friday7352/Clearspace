// Clearspace | Persistent application settings.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clearspace.Services;

public sealed class SettingsData
{
    public Dictionary<string, string> FolderLayouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, double> FolderTileScales { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> FolderViewProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool UseWindowsIndex { get; set; } = true;

    // NEW (round 18): disk map performance options. GPU acceleration draws the blocks through
    // Direct3D; turning it off uses the CPU rasterizer, which is the right choice on machines whose
    // display driver is unhappy with a shared surface. Low detail mode draws only the folder you are
    // in, so the map never walks a deep tree - the escape hatch for weak or integrated graphics.
    public bool DiskMapGpu { get; set; } = true;

    public bool DiskMapLowDetail { get; set; }

    public bool DiskMapConserveMemory { get; set; }

    public bool DiskMapRenderEverything { get; set; }

    public bool DiskMapBackgroundBuilding { get; set; } = true;

    public bool ShowHiddenItems { get; set; }

    public bool SearchEverywhere { get; set; }

    public bool SearchFileContents { get; set; } = true;

    public Dictionary<string, List<string>> FolderColumns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Dictionary<string, double>> FolderColumnWidths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<string>> ProfileColumns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> SidebarOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> PinnedDirectories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<PinnedCategory> PinnedCategories { get; set; } = [];

    public Dictionary<string, string> PinnedDirectoryCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> PinnedDirectoryOrder { get; set; } = [];

    public List<SidebarSectionConfig> SidebarSections { get; set; } = [];

    public List<string> SidebarSectionOrder { get; set; } = [];
}

public sealed class PinnedCategory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New category";
    public bool IsCollapsed { get; set; }
}

public sealed class SidebarSectionConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsCollapsed { get; set; }
}

public sealed record SidebarSectionInfo(string Id, string Name, bool IsCollapsed, bool IsCategory);

public static class SettingsService
{
    private static readonly string Directory_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Clearspace");

    private static readonly string FilePath = Path.Combine(Directory_, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static SettingsData? _data;

    public static SettingsData Current => _data ??= Load();

    public static string SettingsFilePath => FilePath;

    private static SettingsData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<SettingsData>(json, Options);

                if (loaded is not null)
                {
                    loaded.FolderLayouts = new Dictionary<string, string>(loaded.FolderLayouts, StringComparer.OrdinalIgnoreCase);
                    loaded.FolderTileScales = new Dictionary<string, double>(loaded.FolderTileScales ?? [], StringComparer.OrdinalIgnoreCase);
                    loaded.FolderViewProfiles = new Dictionary<string, string>(loaded.FolderViewProfiles ?? [], StringComparer.OrdinalIgnoreCase);
                    loaded.ProfileColumns = new Dictionary<string, List<string>>(loaded.ProfileColumns ?? [], StringComparer.OrdinalIgnoreCase);
                    loaded.FolderColumns = new Dictionary<string, List<string>>(loaded.FolderColumns ?? [], StringComparer.OrdinalIgnoreCase);
                    loaded.FolderColumnWidths = new Dictionary<string, Dictionary<string, double>>(
                        (loaded.FolderColumnWidths ?? [])
                            .Select(pair => new KeyValuePair<string, Dictionary<string, double>>(
                                pair.Key,
                                new Dictionary<string, double>(pair.Value ?? [], StringComparer.OrdinalIgnoreCase))),
                        StringComparer.OrdinalIgnoreCase);
                    loaded.SidebarOverrides = new Dictionary<string, string>(loaded.SidebarOverrides, StringComparer.OrdinalIgnoreCase);
                    loaded.PinnedDirectories = new Dictionary<string, string>(loaded.PinnedDirectories, StringComparer.OrdinalIgnoreCase);
                    loaded.PinnedCategories ??= [];
                    loaded.PinnedDirectoryCategories = new Dictionary<string, string>(loaded.PinnedDirectoryCategories ?? [], StringComparer.OrdinalIgnoreCase);
                    loaded.PinnedDirectoryOrder ??= [];
                    loaded.SidebarSections ??= [];
                    loaded.SidebarSectionOrder ??= [];
                    foreach (var path in loaded.PinnedDirectories.Keys)
                    {
                        if (!loaded.PinnedDirectoryOrder.Contains(path, StringComparer.OrdinalIgnoreCase))
                            loaded.PinnedDirectoryOrder.Add(path);
                    }
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
        }

        return new SettingsData();
    }

    public static void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory_);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
        }
        catch (Exception)
        {
        }
    }

    public static string? GetFolderLayout(string folder)
        => Current.FolderLayouts.TryGetValue(folder, out var layout) ? layout : null;

    public static void SetFolderLayout(string folder, string layout)
    {
        Current.FolderLayouts[folder] = layout;
        Save();
    }

    public static double? GetFolderTileScale(string folder)
        => Current.FolderTileScales.TryGetValue(folder, out var scale) ? scale : null;

    public static void SetFolderTileScale(string folder, double scale)
    {
        Current.FolderTileScales[folder] = scale;
        Save();
    }

    public static string? GetFolderViewProfile(string folder)
        => Current.FolderViewProfiles.TryGetValue(folder, out var profile) ? profile : null;

    public static void SetFolderViewProfile(string folder, string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile) ||
            profile.Equals("Automatic", StringComparison.OrdinalIgnoreCase))
            Current.FolderViewProfiles.Remove(folder);
        else
            Current.FolderViewProfiles[folder] = profile;

        Save();
    }

    public static void SetFolderViewProfiles(IEnumerable<string> folders, string? profile)
    {
        var applyAutomatic = string.IsNullOrWhiteSpace(profile) ||
            profile.Equals("Automatic", StringComparison.OrdinalIgnoreCase);

        foreach (var folder in folders
                     .Where(folder => !string.IsNullOrWhiteSpace(folder))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (applyAutomatic)
                Current.FolderViewProfiles.Remove(folder);
            else
                Current.FolderViewProfiles[folder] = profile!;
        }

        Save();
    }

    public static bool GetDiskMapBackgroundBuilding() => Current.DiskMapBackgroundBuilding;

    public static void SetDiskMapBackgroundBuilding(bool value)
    {
        if (Current.DiskMapBackgroundBuilding == value)
            return;

        Current.DiskMapBackgroundBuilding = value;
        Save();
    }

    public static bool GetDiskMapRenderEverything() => Current.DiskMapRenderEverything;

    public static void SetDiskMapRenderEverything(bool value)
    {
        if (Current.DiskMapRenderEverything == value)
            return;

        Current.DiskMapRenderEverything = value;
        Save();
    }

    public static bool GetDiskMapConserveMemory() => Current.DiskMapConserveMemory;

    public static void SetDiskMapConserveMemory(bool value)
    {
        if (Current.DiskMapConserveMemory == value)
            return;

        Current.DiskMapConserveMemory = value;
        Save();
    }

    public static bool GetDiskMapGpu() => Current.DiskMapGpu;

    public static void SetDiskMapGpu(bool value)
    {
        if (Current.DiskMapGpu == value)
            return;

        Current.DiskMapGpu = value;
        Save();
    }

    public static bool GetDiskMapLowDetail() => Current.DiskMapLowDetail;

    public static void SetDiskMapLowDetail(bool value)
    {
        if (Current.DiskMapLowDetail == value)
            return;

        Current.DiskMapLowDetail = value;
        Save();
    }

    public static bool GetUseWindowsIndex() => Current.UseWindowsIndex;

    public static void SetUseWindowsIndex(bool value)
    {
        if (Current.UseWindowsIndex == value)
            return;

        Current.UseWindowsIndex = value;
        Save();
    }

    public static bool GetShowHiddenItems() => Current.ShowHiddenItems;

    public static void SetShowHiddenItems(bool value)
    {
        if (Current.ShowHiddenItems == value)
            return;

        Current.ShowHiddenItems = value;
        Save();
    }

    public static bool GetSearchFileContents() => Current.SearchFileContents;

    public static void SetSearchFileContents(bool value)
    {
        if (Current.SearchFileContents == value)
            return;

        Current.SearchFileContents = value;
        Save();
    }

    public static bool GetSearchEverywhere() => Current.SearchEverywhere;

    public static void SetSearchEverywhere(bool value)
    {
        if (Current.SearchEverywhere == value)
            return;

        Current.SearchEverywhere = value;
        Save();
    }

    public static IReadOnlyDictionary<string, string> GetAllFolderViewProfiles() => Current.FolderViewProfiles;

    public static IReadOnlyList<string>? GetFolderColumns(string folder)
        => Current.FolderColumns.TryGetValue(folder, out var columns) && columns.Count > 0
            ? columns
            : null;

    public static void SetFolderColumns(string folder, IEnumerable<string> columnIds)
    {
        Current.FolderColumns[folder] = columnIds.ToList();
        Save();
    }

    public static void ClearFolderColumns(string folder)
    {
        var changed = Current.FolderColumns.Remove(folder);
        changed |= Current.FolderColumnWidths.Remove(folder);
        if (changed)
            Save();
    }

    public static double? GetFolderColumnWidth(string folder, string columnId)
        => Current.FolderColumnWidths.TryGetValue(folder, out var widths) &&
           widths.TryGetValue(columnId, out var width) &&
           !double.IsNaN(width) && !double.IsInfinity(width) && width > 0
            ? width
            : null;

    public static void SetFolderColumnWidth(string folder, string columnId, double width)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(columnId) ||
            double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
            return;

        if (!Current.FolderColumnWidths.TryGetValue(folder, out var widths))
        {
            widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Current.FolderColumnWidths[folder] = widths;
        }

        if (widths.TryGetValue(columnId, out var saved) && Math.Abs(saved - width) < .5)
            return;

        widths[columnId] = width;
        Save();
    }

    public static IReadOnlyList<string>? GetProfileColumns(string profile)
        => Current.ProfileColumns.TryGetValue(profile, out var columns) && columns.Count > 0
            ? columns
            : null;

    public static void SetProfileColumns(string profile, IEnumerable<string> columnIds)
    {
        Current.ProfileColumns[profile] = columnIds.ToList();
        Save();
    }

    public static string? GetSidebarOverride(string name)
        => Current.SidebarOverrides.TryGetValue(name, out var path) ? path : null;

    public static void SetSidebarOverride(string name, string path)
    {
        Current.SidebarOverrides[name] = path;
        Save();
    }

    public static void ClearSidebarOverride(string name)
    {
        if (Current.SidebarOverrides.Remove(name))
            Save();
    }

    public static IReadOnlyDictionary<string, string> GetPinnedDirectories() => Current.PinnedDirectories;

    public static void PinDirectory(string path, string name)
    {
        Current.PinnedDirectories[path] = name;
        if (!Current.PinnedDirectoryOrder.Contains(path, StringComparer.OrdinalIgnoreCase))
            Current.PinnedDirectoryOrder.Add(path);
        Save();
    }

    public static void UnpinDirectory(string path)
    {
        if (Current.PinnedDirectories.Remove(path))
        {
            Current.PinnedDirectoryCategories.Remove(path);
            Current.PinnedDirectoryOrder.RemoveAll(item => item.Equals(path, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    public static IReadOnlyList<PinnedCategory> GetPinnedCategories() => Current.PinnedCategories;

    public static IReadOnlyList<KeyValuePair<string, string>> GetPins(string? categoryId)
    {
        var order = Current.PinnedDirectoryOrder
            .Select((path, index) => new { path, index })
            .ToDictionary(item => item.path, item => item.index, StringComparer.OrdinalIgnoreCase);

        return Current.PinnedDirectories
            .Where(pin => Current.PinnedDirectoryCategories.TryGetValue(pin.Key, out var assigned)
                ? string.Equals(assigned, categoryId, StringComparison.OrdinalIgnoreCase)
                : categoryId is null)
            .OrderBy(pin => order.TryGetValue(pin.Key, out var index) ? index : int.MaxValue)
            .ToList();
    }

    public static void CreatePinnedCategory(string name)
    {
        EnsureSidebarSections();
        var category = new PinnedCategory { Name = name };
        Current.PinnedCategories.Add(category);
        var favoritesIndex = Current.SidebarSectionOrder.FindIndex(id => id == "favorites");
        Current.SidebarSectionOrder.Insert(favoritesIndex < 0 ? 0 : favoritesIndex + 1, CategorySectionId(category.Id));
        Save();
    }

    public static void RenamePinnedCategory(string id, string name)
    {
        var category = Current.PinnedCategories.FirstOrDefault(item => item.Id == id);
        if (category is null) return;
        category.Name = name;
        Save();
    }

    public static void DeletePinnedCategory(string id)
    {
        if (Current.PinnedCategories.RemoveAll(item => item.Id == id) == 0)
            return;

        foreach (var path in Current.PinnedDirectoryCategories
                     .Where(pair => pair.Value == id)
                     .Select(pair => pair.Key)
                     .ToList())
            Current.PinnedDirectoryCategories.Remove(path);

        Current.SidebarSectionOrder.RemoveAll(section => section == CategorySectionId(id));

        Save();
    }

    public static void TogglePinnedCategory(string id)
    {
        var category = Current.PinnedCategories.FirstOrDefault(item => item.Id == id);
        if (category is null) return;
        category.IsCollapsed = !category.IsCollapsed;
        Save();
    }

    public static void MovePinnedDirectory(string path, string? categoryId, string? targetPath = null, bool placeAfter = false)
    {
        if (!Current.PinnedDirectories.ContainsKey(path)) return;

        if (string.IsNullOrEmpty(categoryId))
            Current.PinnedDirectoryCategories.Remove(path);
        else
            Current.PinnedDirectoryCategories[path] = categoryId;

        Current.PinnedDirectoryOrder.RemoveAll(item => item.Equals(path, StringComparison.OrdinalIgnoreCase));
        var targetIndex = string.IsNullOrWhiteSpace(targetPath)
            ? -1
            : Current.PinnedDirectoryOrder.FindIndex(item => item.Equals(targetPath, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0)
            Current.PinnedDirectoryOrder.Add(path);
        else
            Current.PinnedDirectoryOrder.Insert(targetIndex + (placeAfter ? 1 : 0), path);

        Save();
    }

    public static void MovePinnedCategory(string sourceId, string beforeId)
    {
        var source = Current.PinnedCategories.FirstOrDefault(item => item.Id == sourceId);
        var targetIndex = Current.PinnedCategories.FindIndex(item => item.Id == beforeId);
        if (source is null || targetIndex < 0 || sourceId == beforeId) return;

        Current.PinnedCategories.Remove(source);
        targetIndex = Current.PinnedCategories.FindIndex(item => item.Id == beforeId);
        Current.PinnedCategories.Insert(targetIndex, source);
        Save();
    }

    public static IReadOnlyList<SidebarSectionInfo> GetSidebarSections()
    {
        EnsureSidebarSections();

        return Current.SidebarSectionOrder
            .Select(ToSectionInfo)
            .Where(section => section is not null)
            .Cast<SidebarSectionInfo>()
            .ToList();
    }

    public static void ToggleSidebarSection(string sectionId)
    {
        EnsureSidebarSections();
        if (TryGetCategoryId(sectionId, out var categoryId))
        {
            TogglePinnedCategory(categoryId);
            return;
        }

        var section = Current.SidebarSections.FirstOrDefault(item => item.Id == sectionId);
        if (section is null) return;
        section.IsCollapsed = !section.IsCollapsed;
        Save();
    }

    public static void RenameSidebarSection(string sectionId, string name)
    {
        EnsureSidebarSections();
        if (TryGetCategoryId(sectionId, out var categoryId))
        {
            RenamePinnedCategory(categoryId, name);
            return;
        }

        var section = Current.SidebarSections.FirstOrDefault(item => item.Id == sectionId);
        if (section is null) return;
        section.Name = name;
        Save();
    }

    public static void MoveSidebarSection(string sourceId, string targetId, bool placeAfter)
    {
        EnsureSidebarSections();
        if (sourceId == targetId || !Current.SidebarSectionOrder.Remove(sourceId)) return;
        var targetIndex = Current.SidebarSectionOrder.IndexOf(targetId);
        if (targetIndex < 0)
            Current.SidebarSectionOrder.Add(sourceId);
        else
            Current.SidebarSectionOrder.Insert(targetIndex + (placeAfter ? 1 : 0), sourceId);
        Save();
    }

    private static readonly (string Id, string Name)[] DefaultSidebarSections =
    [
        ("files", "Your files"),
        ("favorites", "Favorites"),
        ("cloud", "Cloud"),
        ("this-pc", "This PC"),
        ("network", "Network")
    ];

    private static void EnsureSidebarSections()
    {
        foreach (var (id, name) in DefaultSidebarSections)
        {
            if (Current.SidebarSections.All(section => section.Id != id))
                Current.SidebarSections.Add(new SidebarSectionConfig { Id = id, Name = name });
        }

        var known = DefaultSidebarSections.Select(item => item.Id)
            .Concat(Current.PinnedCategories.Select(category => CategorySectionId(category.Id)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Current.SidebarSectionOrder.RemoveAll(id => !known.Contains(id));

        foreach (var category in Current.PinnedCategories)
        {
            var categoryId = CategorySectionId(category.Id);
            if (Current.SidebarSectionOrder.Contains(categoryId)) continue;
            var favoritesIndex = Current.SidebarSectionOrder.FindIndex(id => id == "favorites");
            Current.SidebarSectionOrder.Insert(favoritesIndex < 0 ? Current.SidebarSectionOrder.Count : favoritesIndex + 1, categoryId);
        }

        foreach (var (id, _) in DefaultSidebarSections)
        {
            if (!Current.SidebarSectionOrder.Contains(id))
                Current.SidebarSectionOrder.Add(id);
        }
    }

    private static SidebarSectionInfo? ToSectionInfo(string id)
    {
        if (TryGetCategoryId(id, out var categoryId))
        {
            var category = Current.PinnedCategories.FirstOrDefault(item => item.Id == categoryId);
            return category is null ? null : new SidebarSectionInfo(id, category.Name, category.IsCollapsed, IsCategory: true);
        }

        var section = Current.SidebarSections.FirstOrDefault(item => item.Id == id);
        return section is null ? null : new SidebarSectionInfo(id, section.Name, section.IsCollapsed, IsCategory: false);
    }

    private static string CategorySectionId(string categoryId) => $"category:{categoryId}";

    private static bool TryGetCategoryId(string sectionId, out string categoryId)
    {
        const string prefix = "category:";
        if (sectionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            categoryId = sectionId[prefix.Length..];
            return true;
        }

        categoryId = string.Empty;
        return false;
    }
}
