// Clearspace | Details-view column definitions.

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
        new("length",       "Length",        "Col.Length"),
        new("datemodified", "Date modified", "Col.DateModified"),
        new("datecreated",  "Date created",  "Col.DateCreated"),
        new("tags",         "Tags",          "Col.Tags"),
        new("status",       "Status",        "Col.Status"),
        new("type",         "Type",          "Col.Type"),
        new("size",         "Size",          "Col.Size")
    ];

    private static readonly string[] GeneralDefault = ["name", "datemodified", "type"];
    private static readonly string[] PhotosDefault = ["name", "datemodified", "type"];
    private static readonly string[] MusicDefault = ["play", "name", "artist", "album", "length", "datemodified"];

    private static readonly string[] CloudDefault = ["name", "status", "datemodified", "type"];

    public static IReadOnlyList<string> DefaultsFor(DirectoryViewProfile profile) => profile switch
    {
        DirectoryViewProfile.Music => MusicDefault,
        DirectoryViewProfile.Photos => PhotosDefault,
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
