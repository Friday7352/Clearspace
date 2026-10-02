// Clearspace | Folder type definitions.
//
// NEW (folder types, step 1). Every folder type is one definition that the view, the icons, the
// columns and search all read, instead of switch statements spread across MainViewModel, ColumnCatalog
// and FolderIconService. Built-in types keep their old names as IDs ("Photos", "Music", ...), so saved
// settings carry over unchanged. Custom types ("School") are copies of a built-in with their own layout,
// columns and sorting, saved in settings; their ID is "custom:<slug>".
//
// A type is assigned to a folder, and can optionally apply to every folder below it ("apply to
// subfolders"). The nearest assignment wins: a folder's own type beats one inherited from above.
//
// CHANGED (folder types, step 2): definitions also carry behavior - a default sort, tile size, grouping,
// dimming generated folders (Code), and the project strip (Projects) - and six new types:
// Screenshots, Code, Projects, Research, Design & 3D, and Archives & Backups.
//
// CHANGED (folder types, step 3): every type has something it does that General does not:
//   - Grouping is Date or Kind (Desktop: folders, shortcuts and files apart); GroupByDate stays as a shorthand.
//   - PhotoViewer: Screenshots and Design & 3D get the Photos tiles and viewer, not just Photos.
//   - Documents/Research/Downloads/Videos/Photos got columns read from the files (pages, authors, the
//     document's own title, the site a download came from, length and resolution).
//   - Automatic is labelled "Auto (Photos)" and detection also uses Windows' own folders and, after a
//     folder has been opened, what it contains (see AutomaticFolderTypeDetector).

using System.IO;
using Clearspace.Services;

namespace Clearspace.Models;

// NEW (step 3): how the details view groups items.
public enum FolderGrouping
{
    None,
    Date,   // Today / Yesterday / Earlier this week / ... (only while sorted by date)
    Kind    // Folders / Shortcuts / Files
}

public sealed record FolderType
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    // One line for menus: what choosing this type changes.
    public string Description { get; init; } = "";

    // The built-in family this type behaves like: its icon, and features such as the music player
    // column or photo tiles. A custom type copies these from the type it was saved from.
    public DirectoryViewProfile Base { get; init; } = DirectoryViewProfile.General;

    // Null: decided per folder (Automatic).
    public LayoutMode? Layout { get; init; }

    // Null: the column defaults of the Base family.
    public IReadOnlyList<string>? Columns { get; init; }

    // NEW (step 2): how items are ordered when the folder opens (null: your usual sort).
    public SortColumn? Sort { get; init; }
    public bool SortDescending { get; init; }

    // NEW (step 2): starting tile size (null: 100 %). A size you set for a folder still wins.
    public double? TileScale { get; init; }

    // CHANGED (step 3): grouping in details view (was a bool for date grouping only).
    public FolderGrouping Grouping { get; init; }
    public bool GroupByDate => Grouping == FolderGrouping.Date;

    // NEW (step 3): picture tiles get the "View in Clearspace" button and the in-app viewer.
    public bool PhotoViewer { get; init; }

    // NEW (step 2): build output and dependency folders (bin, obj, node_modules, ...) are dimmed.
    public bool DimGenerated { get; init; }

    // NEW (step 2): shows pinned files and recently changed files from anywhere below the folder.
    public bool ShowProjectStrip { get; init; }

    // NEW (step 2): items inside rank lower in search (old copies should not crowd out current files).
    public bool RankLower { get; init; }

    // Whether choosing this type also applies it to subfolders, unless changed for that folder.
    public bool SubfoldersByDefault { get; init; }

    // Extra words that find folders of this type in search (its name always counts).
    public IReadOnlyList<string> SearchWords { get; init; } = [];

    public bool IsCustom { get; init; }
    public bool IsAutomatic => Id == FolderTypes.AutomaticId;
}

public sealed class CustomFolderTypeData
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Base { get; set; } = nameof(DirectoryViewProfile.General);
    public string? Layout { get; set; }
    public List<string>? Columns { get; set; }
    public bool Subfolders { get; set; } = true;
    // NEW (step 2)
    public string? Sort { get; set; }
    public bool SortDescending { get; set; }
    public double? TileScale { get; set; }
}

public static class FolderTypes
{
    public const string AutomaticId = "Automatic";
    public const string CustomPrefix = "custom:";

    public static readonly FolderType Automatic = new()
    {
        Id = AutomaticId, Name = "Automatic", Base = DirectoryViewProfile.Automatic,
        Description = "Picks a type from Windows' own folders, the folder's name, and what it contains"
    };

    public static readonly FolderType General = new()
    {
        Id = nameof(DirectoryViewProfile.General), Name = "General", Base = DirectoryViewProfile.General, Layout = LayoutMode.Details,
        Description = "A plain, fast list with no extras"
    };

    // Order is the menu order.
    public static readonly IReadOnlyList<FolderType> BuiltIn =
    [
        Automatic,
        General,
        // CHANGED (step 3): Documents, Downloads, Desktop, Photos and Videos each do something of their own.
        new()
        {
            Id = nameof(DirectoryViewProfile.Documents), Name = "Documents", Base = DirectoryViewProfile.Documents, Layout = LayoutMode.Details,
            SearchWords = ["docs"], Description = "Page counts and authors, read from the files"
        },
        new()
        {
            Id = nameof(DirectoryViewProfile.Downloads), Name = "Downloads", Base = DirectoryViewProfile.Downloads, Layout = LayoutMode.Details,
            Sort = SortColumn.DateModified, SortDescending = true, Grouping = FolderGrouping.Date,
            Description = "Newest first, grouped by day, with the site each file came from"
        },
        new()
        {
            Id = nameof(DirectoryViewProfile.Desktop), Name = "Desktop", Base = DirectoryViewProfile.Desktop, Layout = LayoutMode.Details,
            Grouping = FolderGrouping.Kind,
            Description = "Folders, shortcuts and files in their own groups"
        },
        new()
        {
            Id = nameof(DirectoryViewProfile.Photos), Name = "Photos", Base = DirectoryViewProfile.Photos, Layout = LayoutMode.Grid,
            PhotoViewer = true, SearchWords = ["pictures", "images"],
            Description = "Picture tiles, the photo viewer, and image sizes in details view"
        },
        new()
        {
            Id = nameof(DirectoryViewProfile.Music), Name = "Music", Base = DirectoryViewProfile.Music, Layout = LayoutMode.Details,
            SearchWords = ["songs"], Description = "Track details and the music player"
        },
        new()
        {
            Id = nameof(DirectoryViewProfile.Videos), Name = "Videos", Base = DirectoryViewProfile.Videos, Layout = LayoutMode.Grid,
            SearchWords = ["movies"], Description = "Video tiles with their length, and resolution in details view"
        },
        // ---- NEW (step 2)
        new()
        {
            // CHANGED (step 3): works like Photos (tiles and viewer), plus newest first and date groups.
            Id = nameof(DirectoryViewProfile.Screenshots), Name = "Screenshots", Base = DirectoryViewProfile.Screenshots,
            Layout = LayoutMode.Grid, Sort = SortColumn.DateModified, SortDescending = true, Grouping = FolderGrouping.Date,
            PhotoViewer = true, SearchWords = ["screenshot", "captures", "snips"],
            Description = "Like Photos, newest first, grouped by date in details view"
        },
        new()
        {
            Id = nameof(DirectoryViewProfile.Code), Name = "Code", Base = DirectoryViewProfile.Code, Layout = LayoutMode.Details,
            DimGenerated = true, SubfoldersByDefault = true, SearchWords = ["repo", "repository", "source"],
            Description = "Git branch and file status; build and dependency folders dimmed"
        },
        new()
        {
            Id = nameof(DirectoryViewProfile.Projects), Name = "Projects", Base = DirectoryViewProfile.Projects, Layout = LayoutMode.Details,
            ShowProjectStrip = true, SubfoldersByDefault = true, SearchWords = ["project"],
            Description = "Pinned files and recent work from anywhere in the project"
        },
        new()
        {
            // CHANGED (step 3): the document's own title and authors, so "2304.12345.pdf" reads as a paper.
            Id = nameof(DirectoryViewProfile.Research), Name = "Research", Base = DirectoryViewProfile.Research, Layout = LayoutMode.Details,
            Sort = SortColumn.DateModified, SortDescending = true, SubfoldersByDefault = true, SearchWords = ["papers", "notes", "reading"],
            Description = "Papers by their real title and authors, most recent first"
        },
        new()
        {
            Id = nameof(DirectoryViewProfile.Design), Name = "Design & 3D", Base = DirectoryViewProfile.Design, Layout = LayoutMode.Grid,
            TileScale = 1.6, PhotoViewer = true, SearchWords = ["design", "art", "artwork", "3d", "models", "renders"],
            Description = "Large previews (3D models too), the photo viewer, and pixel sizes"
        },
        new()
        {
            Id = nameof(DirectoryViewProfile.Archives), Name = "Archives & Backups", Base = DirectoryViewProfile.Archives, Layout = LayoutMode.Details,
            Sort = SortColumn.DateModified, SortDescending = true, Grouping = FolderGrouping.Date, RankLower = true, SubfoldersByDefault = true,
            SearchWords = ["archives", "backup", "backups", "old"],
            Description = "Grouped by date; contents rank below current files in search"
        }
    ];

    public static IReadOnlyList<FolderType> Custom
        => [.. SettingsService.GetCustomFolderTypes().Select(FromData)];

    public static IReadOnlyList<FolderType> All => [.. BuiltIn, .. Custom];

    // By ID, or by name (so "type:school" works); null when unknown. Called for every folder shown
    // (icons), so it allocates only when a custom type matches.
    public static FolderType? Find(string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;

        foreach (var type in BuiltIn)
            if (type.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase)) return type;

        var custom = SettingsService.GetCustomFolderTypes();

        foreach (var data in custom)
            if (data.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase)) return FromData(data);

        foreach (var type in BuiltIn)
            if (type.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase)) return type;

        foreach (var data in custom)
            if (data.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase)) return FromData(data);

        return null;
    }

    public static FolderType ForProfile(DirectoryViewProfile profile)
        => BuiltIn.FirstOrDefault(type => type.Base == profile && !type.IsCustom) ?? General;

    // The type chosen for exactly this folder, or Automatic.
    public static FolderType AssignedTo(string path)
        => Find(SettingsService.GetFolderViewProfile(path)) ?? Automatic;

    // The type in effect: this folder's own, else the nearest parent's that applies to subfolders,
    // else Automatic. `from` is the folder it was inherited from (null when not inherited).
    public static FolderType Effective(string path, out string? from)
    {
        from = null;
        if (string.IsNullOrWhiteSpace(path)) return Automatic;

        var own = AssignedTo(path);
        if (!own.IsAutomatic) return own;

        for (var parent = Parent(path); parent is not null; parent = Parent(parent))
        {
            if (!SettingsService.GetFolderTypeAppliesToSubfolders(parent)) continue;
            var inherited = AssignedTo(parent);
            if (inherited.IsAutomatic) continue;
            from = parent;
            return inherited;
        }

        return Automatic;
    }

    public static FolderType Effective(string path) => Effective(path, out _);

    // What Automatic means for a folder, else General. `lookAtContents` touches the disk (Windows'
    // folders, repository markers, and what the folder was last seen to contain); icons pass false and
    // use the name only.
    public static FolderType Detected(string? path, bool lookAtContents = false)
    {
        var profile = lookAtContents
            ? AutomaticFolderTypeDetector.DetectFromContents(path)
            : AutomaticFolderTypeDetector.DetectFromName(path);

        return profile is { } found ? ForProfile(found) : General;
    }

    // The type that decides the view of the folder being opened: effective type, or detected.
    public static FolderType Resolved(string path)
    {
        var effective = Effective(path);
        return effective.IsAutomatic ? Detected(path, lookAtContents: true) : effective;
    }

    public static IReadOnlyList<string> DefaultColumns(FolderType type, bool isCloudFolder)
        => type.Columns is { Count: > 0 } columns ? columns : ColumnCatalog.DefaultsFor(type.Base, isCloudFolder);

    // CHANGED (step 3): "Auto (Photos)" - short enough for the toolbar button, and says what was picked.
    public static string Label(FolderType type, string? path)
    {
        if (!type.IsAutomatic) return type.Name;
        var detected = Detected(path, lookAtContents: true);
        return detected.Id == General.Id ? "Auto" : $"Auto ({detected.Name})";
    }

    // NEW (step 2): folders assigned a type whose contents rank lower in search (Archives & Backups, or a
    // custom type based on it). Read by SearchQuery.Parse, possibly off the UI thread.
    public static IReadOnlyList<string> RankLowerFolders()
    {
        try
        {
            List<string>? found = null;

            foreach (var (folder, id) in SettingsService.GetAllFolderViewProfiles())
            {
                if (Find(id) is { RankLower: true })
                    (found ??= []).Add(folder);
            }

            return found is null ? [] : found;
        }
        catch (InvalidOperationException)
        {
            return []; // settings changed while reading; this search ranks without it
        }
    }

    // The words search treats as asking for this type.
    public static IEnumerable<string> Words(FolderType type) => type.SearchWords.Prepend(type.Name);

    public static string NewCustomId(string name)
    {
        var stem = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (stem.Length == 0) stem = "type";
        var id = CustomPrefix + stem;
        for (var suffix = 2; Find(id) is not null; suffix++) id = $"{CustomPrefix}{stem}{suffix}";
        return id;
    }

    internal static FolderType FromData(CustomFolderTypeData data)
    {
        var basis = Enum.TryParse<DirectoryViewProfile>(data.Base, out var profile) && profile != DirectoryViewProfile.Automatic
            ? ForProfile(profile) : General;

        // A custom type keeps its base family's behaviors (grouping, viewer, Git, project strip, ranking)
        // and replaces its layout, columns, sort and tile size with what was saved.
        return basis with
        {
            Id = data.Id,
            Name = data.Name,
            Description = $"Custom type based on {basis.Name}",
            Layout = Enum.TryParse<LayoutMode>(data.Layout, out var layout) ? layout : null,
            Columns = data.Columns is { Count: > 0 } ? ColumnCatalog.Sanitise(data.Columns) : null,
            Sort = Enum.TryParse<SortColumn>(data.Sort, out var sort) ? sort : basis.Sort,
            SortDescending = data.Sort is null ? basis.SortDescending : data.SortDescending,
            TileScale = data.TileScale ?? basis.TileScale,
            SubfoldersByDefault = data.Subfolders,
            SearchWords = [],
            IsCustom = true
        };
    }

    private static string? Parent(string path)
    {
        try
        {
            var trimmed = path.TrimEnd('\\', '/');
            var parent = Path.GetDirectoryName(trimmed);
            return string.IsNullOrEmpty(parent) || parent.Equals(trimmed, StringComparison.OrdinalIgnoreCase) ? null : parent;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
