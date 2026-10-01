// Clearspace | Details-view column definitions.
//
// CHANGED (folder types, step 2): Git and Dimensions columns, and default columns for the new types.
// CHANGED (folder types, step 3): Authors, Pages and Source columns, read from the files themselves;
// Photos and Videos show resolution, Videos show length, Documents and Research show pages and authors.

using System.Collections.ObjectModel;

namespace Clearspace.Models;

public sealed record ColumnInfo(string Id, string Header, string ResourceKey, bool IsRequired = false);

public static class ColumnCatalog
{
    public static readonly IReadOnlyList<ColumnInfo> All =
    [
        new("name",         "Name",          "Col.Name",         IsRequired: true),
        new("play",         "Play",          "Col.Play"),
        new("track",        "Track number",  "Col.Track"),
        new("title",        "Title",         "Col.Title"),
        new("artist",       "Artist",        "Col.Artist"),
        new("album",        "Album",         "Col.Album"),
        new("authors",      "Authors",       "Col.Authors"),      // NEW (step 3): Documents, Research
        new("pages",        "Pages",         "Col.Pages"),        // NEW (step 3): Documents, Research
        new("length",       "Length",        "Col.Length"),
        new("datemodified", "Date modified", "Col.DateModified"),
        new("datecreated",  "Date created",  "Col.DateCreated"),
        new("tags",         "Tags",          "Col.Tags"),
        new("git",          "Git",           "Col.Git"),          // NEW (step 2): Code
        new("dimensions",   "Dimensions",    "Col.Dimensions"),   // NEW (step 2): Photos, Videos, Screenshots, Design & 3D
        new("source",       "Source",        "Col.Source"),       // NEW (step 3): Downloads - the site a file came from
        new("status",       "Status",        "Col.Status"),
        new("type",         "Type",          "Col.Type"),
        new("size",         "Size",          "Col.Size")
    ];

    // NEW (step 3): columns whose values are read from the file's properties (title, artist, length, pages...).
    private static readonly HashSet<string> PropertyColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "artist", "album", "authors", "pages", "length", "track", "play"
    };

    public static bool NeedsFileProperties(IEnumerable<string> visible) => visible.Any(PropertyColumns.Contains);

    private static readonly string[] GeneralDefault = ["name", "datemodified", "type"];
    private static readonly string[] MusicDefault = ["play", "name", "artist", "album", "length", "datemodified"];

    private static readonly string[] CloudDefault = ["name", "status", "datemodified", "type"];

    // CHANGED (step 3)
    private static readonly string[] PhotosDefault = ["name", "datemodified", "dimensions", "type", "size"];
    private static readonly string[] VideosDefault = ["name", "length", "dimensions", "datemodified", "size"];
    private static readonly string[] DocumentsDefault = ["name", "pages", "authors", "datemodified", "type", "size"];
    private static readonly string[] DownloadsDefault = ["name", "source", "datemodified", "type", "size"];
    private static readonly string[] DesktopDefault = ["name", "type", "datemodified", "size"];

    // NEW (step 2)
    private static readonly string[] ScreenshotsDefault = ["name", "datemodified", "dimensions", "size"];
    private static readonly string[] CodeDefault = ["name", "git", "datemodified", "type", "size"];
    private static readonly string[] ProjectsDefault = ["name", "datemodified", "type", "tags"];
    private static readonly string[] ResearchDefault = ["name", "title", "authors", "pages", "datemodified"]; // CHANGED (step 3)
    private static readonly string[] DesignDefault = ["name", "dimensions", "type", "size", "datemodified"];
    private static readonly string[] ArchivesDefault = ["name", "datemodified", "datecreated", "size", "type"];

    public static IReadOnlyList<string> DefaultsFor(DirectoryViewProfile profile) => profile switch
    {
        DirectoryViewProfile.Music => MusicDefault,
        DirectoryViewProfile.Photos => PhotosDefault,
        DirectoryViewProfile.Videos => VideosDefault,        // NEW (step 3)
        DirectoryViewProfile.Documents => DocumentsDefault,  // NEW (step 3)
        DirectoryViewProfile.Downloads => DownloadsDefault,  // NEW (step 3)
        DirectoryViewProfile.Desktop => DesktopDefault,      // NEW (step 3)
        DirectoryViewProfile.Screenshots => ScreenshotsDefault,
        DirectoryViewProfile.Code => CodeDefault,
        DirectoryViewProfile.Projects => ProjectsDefault,
        DirectoryViewProfile.Research => ResearchDefault,
        DirectoryViewProfile.Design => DesignDefault,
        DirectoryViewProfile.Archives => ArchivesDefault,
        _ => GeneralDefault
    };

    public static IReadOnlyList<string> DefaultsFor(DirectoryViewProfile profile, bool isCloudFolder)
        => isCloudFolder && profile is DirectoryViewProfile.General or DirectoryViewProfile.Automatic
            ? CloudDefault
            : DefaultsFor(profile);

    public static ColumnInfo? Find(string id)
        => All.FirstOrDefault(column => column.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static List<string> Sanitise(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var id in ids)
        {
            if (Find(id) is null || !seen.Add(id))
                continue;

            result.Add(id);
        }

        foreach (var required in All.Where(column => column.IsRequired))
        {
            if (!seen.Contains(required.Id))
                result.Insert(0, required.Id);
        }

        return result;
    }
}

public sealed class ColumnOption : ObservableObject
{
    private readonly Action<ColumnOption> _onToggled;
    private bool _isVisible;

    public ColumnOption(ColumnInfo info, bool isVisible, Action<ColumnOption> onToggled)
    {
        Info = info;
        _isVisible = isVisible;
        _onToggled = onToggled;
    }

    public ColumnInfo Info { get; }

    public string Id => Info.Id;

    public string Header => Info.Header;

    public bool CanToggle => !Info.IsRequired;

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (Info.IsRequired)
                return;

            if (SetProperty(ref _isVisible, value))
                _onToggled(this);
        }
    }
}
