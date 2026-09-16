// Clearspace | Search query parsing and matching.

using System.IO;
using Clearspace.Services;

namespace Clearspace.Models;

public enum SearchKind
{
    Any,
    Folder,
    File,
    Image,
    Audio,
    Video
}

public sealed class SearchQuery
{
    private sealed record TermFilter(
        string Text,
        IReadOnlyList<string> TagIds,
        IReadOnlyList<DirectoryViewProfile> Profiles);

    private readonly TagStore? _tagStore;
    private TagStore Tags => _tagStore ?? TagService.Store;
    private SearchQuery(TagStore? tagStore = null) => _tagStore = tagStore;

    public static SearchQuery Empty { get; } = new();

    private IReadOnlyList<TermFilter> TermFilters { get; init; } = [];

    public IReadOnlyList<string> Terms => TermFilters.Select(term => term.Text).ToArray();

    public IReadOnlyList<string> TagIds { get; private init; } = [];

    public IReadOnlyList<DirectoryViewProfile> Profiles { get; private init; } = [];

    public IReadOnlyList<string> Extensions { get; private init; } = [];

    public SearchKind Kind { get; private init; } = SearchKind.Any;

    public bool IsEmpty => TermFilters.Count == 0 && !HasStructuredFilter;

    public bool HasIndexFilter =>
        TagIds.Count > 0 ||
        Profiles.Count > 0 ||
        TermFilters.Any(term => term.TagIds.Count > 0 || term.Profiles.Count > 0);

    public bool HasStructuredFilter =>
        TagIds.Count > 0 || Profiles.Count > 0 || Extensions.Count > 0 || Kind != SearchKind.Any;

    public static SearchQuery Parse(string? text)
        => Parse(text, TagService.Store);

    internal static SearchQuery Parse(string? text, TagStore tagStore)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Empty;

        var terms = new List<TermFilter>();
        var tags = new List<string>();
        var profiles = new List<DirectoryViewProfile>();
        var extensions = new List<string>();
        var kind = SearchKind.Any;

        foreach (var token in Tokenize(text))
        {
            var separator = token.IndexOf(':');

            if (separator <= 0 || separator == token.Length - 1)
            {
                terms.Add(BuildTerm(token, tagStore));
                continue;
            }

            var prefix = token[..separator];
            var value = token[(separator + 1)..];

            switch (prefix.ToLowerInvariant())
            {
                case "tag" or "t":
                    tags.Add(tagStore.Resolve(value)?.Id ?? $"\u0000missing:{value}");
                    break;

                case "type" or "kindof" or "folder":
                    if (Enum.TryParse<DirectoryViewProfile>(value, ignoreCase: true, out var profile) && Enum.IsDefined(profile))
                        profiles.Add(profile);
                    else
                        terms.Add(BuildTerm(token, tagStore));
                    break;

                case "ext":
                    extensions.Add(value.StartsWith('.') ? value : "." + value);
                    break;

                case "is":
                    var parsedKind = value.ToLowerInvariant() switch
                    {
                        "folder" or "dir" or "directory" => SearchKind.Folder,
                        "file" => SearchKind.File,
                        "image" or "photo" or "picture" => SearchKind.Image,
                        "audio" or "music" or "song" => SearchKind.Audio,
                        "video" or "movie" => SearchKind.Video,
                        _ => SearchKind.Any
                    };
                    if (parsedKind == SearchKind.Any)
                        terms.Add(BuildTerm(token, tagStore));
                    else
                        kind = parsedKind;
                    break;

                default:
                    terms.Add(BuildTerm(token, tagStore));
                    break;
            }
        }

        return new SearchQuery(tagStore)
        {
            TermFilters = terms,
            TagIds = tags,
            Profiles = profiles,
            Extensions = extensions,
            Kind = kind
        };
    }

    private static TermFilter BuildTerm(string text, TagStore tagStore)
    {
        var tagIds = new List<string>();

        foreach (var tag in tagStore.All)
        {
            if (tag.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                tag.Id.Contains(text, StringComparison.OrdinalIgnoreCase))
                tagIds.Add(tag.Id);
        }

        var profiles = new List<DirectoryViewProfile>();

        foreach (var profile in Enum.GetValues<DirectoryViewProfile>())
        {
            if (profile == DirectoryViewProfile.Automatic)
                continue;

            if (profile.ToString().Contains(text, StringComparison.OrdinalIgnoreCase))
                profiles.Add(profile);
        }

        return new TermFilter(text, tagIds, profiles);
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var character in text)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    public bool Matches(FileSystemItem item)
    {
        for (var i = 0; i < TermFilters.Count; i++)
        {
            if (!MatchesTerm(item, TermFilters[i]))
                return false;
        }

        return MatchesStructural(item);
    }

    public bool MatchesStructural(FileSystemItem item)
    {
        if (Kind != SearchKind.Any && !MatchesKind(item))
            return false;

        if (Extensions.Count > 0 &&
            !Extensions.Any(extension => item.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase)))
            return false;

        for (var i = 0; i < TagIds.Count; i++)
        {
            if (!Tags.HasTag(item.FullPath, TagIds[i]))
                return false;
        }

        if (Profiles.Count > 0 && !HasProfile(item, Profiles))
            return false;

        return true;
    }

    private bool MatchesTerm(FileSystemItem item, TermFilter term)
    {
        if (item.Name.Contains(term.Text, StringComparison.OrdinalIgnoreCase))
            return true;

        for (var i = 0; i < term.TagIds.Count; i++)
        {
            if (Tags.HasTag(item.FullPath, term.TagIds[i]))
                return true;
        }

        return term.Profiles.Count > 0 && HasProfile(item, term.Profiles);
    }

    private bool MatchesKind(FileSystemItem item) => Kind switch
    {
        SearchKind.Folder => item.IsFolder,
        SearchKind.File => !item.IsFolder,
        SearchKind.Image => item.IsImageFile,
        SearchKind.Audio => item.IsAudio,
        SearchKind.Video => !item.IsFolder && MediaTypes.IsVideo(item.Extension),
        _ => true
    };

    private static bool HasProfile(FileSystemItem item, IReadOnlyList<DirectoryViewProfile> wanted)
    {
        if (!item.IsFolder)
            return false;

        var saved = SettingsService.GetFolderViewProfile(item.FullPath);
        if (saved is null || !Enum.TryParse<DirectoryViewProfile>(saved, out var profile))
            return false;

        return wanted.Contains(profile);
    }

    public IEnumerable<string> IndexCandidates()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assignment in Tags.Assignments)
            candidates.Add(assignment.Key);

        foreach (var typed in SettingsService.GetAllFolderViewProfiles())
            candidates.Add(typed.Key);

        foreach (var id in TagIds)
            candidates.IntersectWith(Tags.PathsWithTag(id));

        return candidates;
    }

    public string Describe()
    {
        var parts = new List<string>();

        if (TagIds.Count > 0)
            parts.Add("tagged " + string.Join(" and ", TagIds.Select(id => Tags.Find(id)?.Name ?? "unknown tag")));

        if (Profiles.Count > 0)
            parts.Add("typed " + string.Join(" or ", Profiles.Select(profile => profile.ToString().ToLowerInvariant())));

        if (Extensions.Count > 0)
            parts.Add(string.Join(" or ", Extensions));

        if (Kind != SearchKind.Any)
            parts.Add(Kind.ToString().ToLowerInvariant() + "s");

        foreach (var term in TermFilters)
        {
            var alternatives = new List<string> { $"named {term.Text}" };

            if (term.TagIds.Count > 0)
                alternatives.Add("tagged " + string.Join(" or ", term.TagIds.Select(id => Tags.Find(id)?.Name ?? id)));

            if (term.Profiles.Count > 0)
                alternatives.Add("typed " + string.Join(" or ", term.Profiles.Select(profile => profile.ToString().ToLowerInvariant())));

            parts.Add(string.Join(" or ", alternatives));
        }

        return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
    }
}
