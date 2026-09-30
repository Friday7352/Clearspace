// Clearspace | Search query parsing and matching.
//
// CHANGED (search relevance): plain words are evidence about what the person means, not filename
// filters. Each word is satisfied by the item's own name, a tag on the item (exact name, or a prefix
// while typing), a type word ("photos", "pdfs"), a folder type, or a folder above the item — the
// folder name or a tag on it. At least one word must be satisfied by the item itself, so "school"
// alone does not return everything inside School folders. Explicit syntax (tag:, ext:, is:, type:)
// is still a strict restriction. Relevance numbers live in SearchRanker so the file index scan and
// this class score an item identically.

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

// NEW: how well a typed word matched a tag. Exact beats a filename match; Prefix covers "wor" -> Work.
internal enum TagStrength : byte
{
    None = 0,
    Prefix = 1,
    Exact = 2
}

// NEW: one plain word (or quoted phrase, or recognized multiword tag) and every way it can be satisfied.
internal sealed class SearchTerm
{
    public required string Text { get; init; }
    public bool Quoted { get; init; }
    public IReadOnlyList<(string TagId, TagStrength Strength)> Tags { get; init; } = [];
    public HashSet<string>? TypeExtensions { get; init; }
    public string? TypeName { get; init; }
    public IReadOnlyList<DirectoryViewProfile> Profiles { get; init; } = [];

    public bool TypeMatches(ReadOnlySpan<char> extension)
        => TypeExtensions is not null && !extension.IsEmpty &&
           TypeExtensions.GetAlternateLookup<ReadOnlySpan<char>>().Contains(extension);
}

public sealed class SearchQuery
{
    // NEW: the longest tag name, in words, that the parser tries to recognize inside plain text.
    private const int MaxTagWords = 4;
    // NEW: words above this count can still match by name/tag/type but not through parent folders.
    internal const int MaxContextTerms = 16;

    private readonly TagStore? _tagStore;
    // Capture only the requested tags once per search; per-file matching stays in memory.
    private Dictionary<string, HashSet<string>> _tagPaths = new(StringComparer.OrdinalIgnoreCase);
    private TagStore Tags => _tagStore ?? TagService.Store;
    private SearchQuery(TagStore? tagStore = null) => _tagStore = tagStore;

    public static SearchQuery Empty { get; } = new();

    // CHANGED: terms carry their tag/type/folder-type interpretations.
    internal IReadOnlyList<SearchTerm> TermList { get; private init; } = [];

    public IReadOnlyList<string> Terms => TermList.Select(term => term.Text).ToArray();

    public IReadOnlyList<string> TagIds { get; private init; } = [];

    public IReadOnlyList<DirectoryViewProfile> Profiles { get; private init; } = [];

    public IReadOnlyList<string> Extensions { get; private init; } = [];

    // NEW: Extensions as a set so the index can test a name's extension span without allocating.
    internal HashSet<string>? ExtensionSet { get; private init; }

    public SearchKind Kind { get; private init; } = SearchKind.Any;

    // NEW: the folder the person is searching from. Ranking only; see At().
    internal string? CurrentFolder { get; private init; }

    public bool IsEmpty => TermList.Count == 0 && !HasStructuredFilter;

    // CHANGED (search fix): only exact tag names (and folder types) pull tagged items from everywhere.
    // A prefix while typing ("wo" -> Work) no longer does: it read every tagged item from disk per keystroke.
    public bool HasIndexFilter =>
        TagIds.Count > 0 ||
        Profiles.Count > 0 ||
        TermList.Any(term => term.Tags.Any(tag => tag.Strength == TagStrength.Exact) || term.Profiles.Count > 0);

    public bool HasStructuredFilter =>
        TagIds.Count > 0 || Profiles.Count > 0 || Extensions.Count > 0 || Kind != SearchKind.Any;

    // NEW: true when any plain word or explicit filter needs folder-type settings.
    internal bool UsesProfiles => Profiles.Count > 0 || TermList.Any(term => term.Profiles.Count > 0);

    // NEW: the paths assigned a tag at the moment the query was parsed (empty for unknown tags).
    internal HashSet<string> PathsTagged(string tagId)
        => _tagPaths.TryGetValue(tagId, out var paths) ? paths : EmptyPaths;

    private static readonly HashSet<string> EmptyPaths = new(StringComparer.OrdinalIgnoreCase);

    // NEW: the same query, ranked relative to the folder the search started from.
    internal SearchQuery At(string? currentFolder) => new(_tagStore)
    {
        _tagPaths = _tagPaths,
        TermList = TermList,
        TagIds = TagIds,
        Profiles = Profiles,
        Extensions = Extensions,
        ExtensionSet = ExtensionSet,
        Kind = Kind,
        CurrentFolder = currentFolder
    };

    public static SearchQuery Parse(string? text)
        => Parse(text, TagService.Store);

    internal static SearchQuery Parse(string? text, TagStore tagStore)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Empty;

        // CHANGED: one read of the tag list per parse instead of one per word.
        var definitions = tagStore.All;
        var terms = new List<SearchTerm>();
        var run = new List<(string Text, bool Quoted)>();
        var tags = new List<string>();
        var profiles = new List<DirectoryViewProfile>();
        var extensions = new List<string>();
        var kind = SearchKind.Any;

        void Flush()
        {
            AddRun(run, definitions, terms);
            run.Clear();
        }

        foreach (var (token, quoted) in Tokenize(text))
        {
            var separator = token.IndexOf(':');

            // CHANGED: a quoted "a:b" is a literal phrase, never a filter.
            if (quoted || separator <= 0 || separator == token.Length - 1)
            {
                run.Add((token, quoted));
                continue;
            }

            var prefix = token[..separator];
            var value = token[(separator + 1)..];

            switch (prefix.ToLowerInvariant())
            {
                case "tag" or "t":
                    Flush();
                    tags.Add(ResolveTag(definitions, value)?.Id ?? $"\u0000missing:{value}");
                    break;

                case "type" or "kindof" or "folder":
                    if (Enum.TryParse<DirectoryViewProfile>(value, ignoreCase: true, out var profile) && Enum.IsDefined(profile))
                    {
                        Flush();
                        profiles.Add(profile);
                    }
                    else
                        run.Add((token, false));
                    break;

                case "ext":
                    Flush();
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
                        run.Add((token, false));
                    else
                    {
                        Flush();
                        kind = parsedKind;
                    }
                    break;

                default:
                    run.Add((token, false));
                    break;
            }
        }

        Flush();

        return new SearchQuery(tagStore)
        {
            _tagPaths = tagStore.SnapshotPathsForTags(tags.Concat(terms.SelectMany(term => term.Tags.Select(tag => tag.TagId)))),
            TermList = terms,
            TagIds = tags,
            Profiles = profiles,
            Extensions = extensions,
            ExtensionSet = extensions.Count == 0 ? null : new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase),
            Kind = kind
        };
    }

    private static TagDefinition? ResolveTag(IReadOnlyList<TagDefinition> definitions, string value)
        => definitions.FirstOrDefault(tag => tag.Id.Equals(value, StringComparison.OrdinalIgnoreCase)) ??
           definitions.FirstOrDefault(tag => tag.Name.Equals(value, StringComparison.OrdinalIgnoreCase));

    // NEW: groups adjacent plain words into a multiword tag when they spell one ("home repairs quote").
    private static void AddRun(List<(string Text, bool Quoted)> run, IReadOnlyList<TagDefinition> definitions, List<SearchTerm> terms)
    {
        var i = 0;

        while (i < run.Count)
        {
            var grouped = false;

            if (!run[i].Quoted)
            {
                for (var end = Math.Min(run.Count, i + MaxTagWords) - 1; end > i && !grouped; end--)
                {
                    if (run.Skip(i).Take(end - i + 1).Any(word => word.Quoted))
                        continue;

                    var phrase = string.Join(' ', run.Skip(i).Take(end - i + 1).Select(word => word.Text));
                    var tag = definitions.FirstOrDefault(definition =>
                        definition.Name.Equals(phrase, StringComparison.OrdinalIgnoreCase));

                    if (tag is null)
                        continue;

                    terms.Add(new SearchTerm { Text = phrase, Tags = [(tag.Id, TagStrength.Exact)] });
                    i = end + 1;
                    grouped = true;
                }
            }

            if (grouped)
                continue;

            terms.Add(BuildTerm(run[i].Text, run[i].Quoted, definitions));
            i++;
        }
    }

    private static SearchTerm BuildTerm(string text, bool quoted, IReadOnlyList<TagDefinition> definitions)
    {
        // CHANGED: exact tag name/ID, or a word of the tag name starting with what was typed.
        // The old substring rule made "or" match Important and "port" match Important.
        var tags = new List<(string, TagStrength)>();

        foreach (var tag in definitions)
        {
            if (tag.Name.Equals(text, StringComparison.OrdinalIgnoreCase) ||
                tag.Id.Equals(text, StringComparison.OrdinalIgnoreCase))
                tags.Add((tag.Id, TagStrength.Exact));
            else if (!quoted && text.Length >= 2 && WordStartsWith(tag.Name, text))
                tags.Add((tag.Id, TagStrength.Prefix));
        }

        var profiles = new List<DirectoryViewProfile>();
        HashSet<string>? typeExtensions = null;
        string? typeName = null;

        if (!quoted)
        {
            foreach (var profile in Enum.GetValues<DirectoryViewProfile>())
            {
                if (profile is DirectoryViewProfile.Automatic or DirectoryViewProfile.General)
                    continue;

                var name = profile.ToString();

                if (name.Equals(text, StringComparison.OrdinalIgnoreCase) ||
                    (text.Length >= 3 && name.StartsWith(text, StringComparison.OrdinalIgnoreCase)))
                    profiles.Add(profile);
            }

            if (SearchVocabulary.TypeWords.TryGetValue(text, out var type))
            {
                typeExtensions = type.Extensions;
                typeName = type.Name;
            }
        }

        return new SearchTerm
        {
            Text = text,
            Quoted = quoted,
            Tags = tags,
            Profiles = profiles,
            TypeExtensions = typeExtensions,
            TypeName = typeName
        };
    }

    private static bool WordStartsWith(string name, string prefix)
    {
        for (var at = 0; at < name.Length; at++)
        {
            if ((at == 0 || !char.IsLetterOrDigit(name[at - 1])) &&
                name.AsSpan(at).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // CHANGED: reports whether each token was quoted, so phrases stay literal.
    private static List<(string Text, bool Quoted)> Tokenize(string text)
    {
        var tokens = new List<(string, bool)>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var wasQuoted = false;

        void Emit()
        {
            if (current.Length > 0)
                tokens.Add((current.ToString(), wasQuoted));

            current.Clear();
            wasQuoted = false;
        }

        foreach (var character in text)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                wasQuoted = true;
                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                Emit();
                continue;
            }

            current.Append(character);
        }

        Emit();
        return tokens;
    }

    public bool Matches(FileSystemItem item) => Relevance(item) >= 0;

    // NEW: -1 when the item does not satisfy the query; otherwise the term relevance used for ranking
    // (average evidence strength plus how much of the name the words cover). The index scan in
    // IndexSearch computes the same number from index entries.
    internal int Relevance(FileSystemItem item)
    {
        if (!MatchesStructural(item))
            return -1;

        var count = TermList.Count;

        if (count == 0)
            return 0;

        var name = item.Name.AsSpan();
        var isFolder = item.IsFolder;
        var extension = isFolder ? ReadOnlySpan<char>.Empty : Path.GetExtension(name);
        DirectoryViewProfile? profile = null;
        var profileLoaded = false;
        Span<int> strengths = count <= 64 ? stackalloc int[count] : new int[count];
        var own = 0;
        var matchedLength = 0;

        for (var t = 0; t < count; t++)
        {
            var term = TermList[t];
            var category = SearchRanker.Category(name, term.Text);
            var strength = SearchRanker.NamePoints(category);

            if (category > 0)
                matchedLength += term.Text.Length;

            foreach (var (tagId, tagStrength) in term.Tags)
            {
                if (PathsTagged(tagId).Contains(item.FullPath))
                    strength = Math.Max(strength, SearchRanker.TagPoints(tagStrength));
            }

            if (!isFolder && term.TypeMatches(extension))
                strength = Math.Max(strength, SearchRanker.TypeWord);

            if (isFolder && term.Profiles.Count > 0)
            {
                if (!profileLoaded)
                {
                    profile = FolderProfile(item.FullPath);
                    profileLoaded = true;
                }

                if (profile is { } value && term.Profiles.Contains(value))
                    strength = Math.Max(strength, SearchRanker.FolderType);
            }

            strengths[t] = strength;

            if (strength > 0)
                own++;
        }

        if (own == 0)
            return -1;

        var sum = 0;

        for (var t = 0; t < count; t++)
        {
            if (strengths[t] == 0)
            {
                strengths[t] = t < MaxContextTerms ? ContextStrength(item.FullPath, TermList[t]) : 0;

                if (strengths[t] == 0)
                    return -1;
            }

            sum += strengths[t];
        }

        return SearchRanker.Combine(sum, count, matchedLength, name.Length);
    }

    // NEW: evidence from the folders above an item (drive root excluded): a folder name containing the
    // word, or a tag on one of those folders. Mirrors IndexSearch's per-folder cache.
    private int ContextStrength(string path, SearchTerm term)
    {
        var start = Path.GetPathRoot(path.AsSpan()).Length;
        var end = path.LastIndexOf(Path.DirectorySeparatorChar);

        if (end <= start)
            return 0;

        var best = 0;
        var segmentStart = start;

        for (var at = start; at <= end; at++)
        {
            if (at < end && path[at] != Path.DirectorySeparatorChar)
                continue;

            if (at > segmentStart)
            {
                var folder = path.AsSpan(segmentStart, at - segmentStart);
                best = Math.Max(best, SearchRanker.FolderPoints(SearchRanker.Category(folder, term.Text)));

                foreach (var (tagId, tagStrength) in term.Tags)
                {
                    if (PathsTagged(tagId).GetAlternateLookup<ReadOnlySpan<char>>().Contains(path.AsSpan(0, at)))
                        best = Math.Max(best, SearchRanker.InheritedTagPoints(tagStrength));
                }
            }

            segmentStart = at + 1;
        }

        return best;
    }

    // NEW: a cheap necessary condition, checked before touching the disk for paths the index has not
    // scanned yet (IndexOverlay additions). False means the path cannot match; true means "check it".
    internal bool MightMatchPath(string path)
    {
        var extension = Path.GetExtension(path.AsSpan());

        if (ExtensionSet is not null &&
            (extension.IsEmpty || !ExtensionSet.GetAlternateLookup<ReadOnlySpan<char>>().Contains(extension)))
            return false;

        foreach (var tagId in TagIds)
        {
            if (!PathsTagged(tagId).Contains(path))
                return false;
        }

        foreach (var term in TermList)
        {
            // Tags and folder types are not visible in a path, so those words cannot rule it out.
            if (term.Tags.Count > 0 || term.Profiles.Count > 0 || term.TypeMatches(extension) ||
                path.Contains(term.Text, StringComparison.OrdinalIgnoreCase))
                continue;

            return false;
        }

        return true;
    }

    private static DirectoryViewProfile? FolderProfile(string path)
    {
        var saved = SettingsService.GetFolderViewProfile(path);
        return saved is not null && Enum.TryParse<DirectoryViewProfile>(saved, out var profile) ? profile : null;
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
            if (!PathsTagged(TagIds[i]).Contains(item.FullPath))
                return false;
        }

        if (Profiles.Count > 0 && !(item.IsFolder && FolderProfile(item.FullPath) is { } profile && Profiles.Contains(profile)))
            return false;

        return true;
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

    public IEnumerable<string> IndexCandidates()
    {
        if (TagIds.Count > 0)
        {
            var tagged = new HashSet<string>(PathsTagged(TagIds[0]), StringComparer.OrdinalIgnoreCase);
            foreach (var id in TagIds.Skip(1)) tagged.IntersectWith(PathsTagged(id));
            return tagged;
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // CHANGED: only the tags this query mentions, from the parse-time snapshot.
        foreach (var term in TermList)
            foreach (var (tagId, _) in term.Tags)
                candidates.UnionWith(PathsTagged(tagId));

        if (UsesProfiles)
            foreach (var typed in SettingsService.GetAllFolderViewProfiles())
                candidates.Add(typed.Key);

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

        foreach (var term in TermList)
        {
            var alternatives = new List<string> { $"named {term.Text}" };

            var exactTags = term.Tags.Where(tag => tag.Strength == TagStrength.Exact).ToArray();
            if (exactTags.Length > 0)
                alternatives.Add("tagged " + string.Join(" or ", exactTags.Select(tag => Tags.Find(tag.TagId)?.Name ?? tag.TagId)));

            if (term.TypeName is not null)
                alternatives.Add(term.TypeName);

            if (term.Profiles.Count > 0)
                alternatives.Add("typed " + string.Join(" or ", term.Profiles.Select(profile => profile.ToString().ToLowerInvariant())));

            alternatives.Add($"in a folder named {term.Text}");
            parts.Add(string.Join(" or ", alternatives));
        }

        return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
    }
}

// NEW: everyday words for kinds of files. A word here is extra evidence, never a restriction:
// "photos beach" still finds a folder named "Beach Photos" by name.
internal static class SearchVocabulary
{
    internal sealed record TypeWord(string Name, HashSet<string> Extensions);

    private static HashSet<string> Set(params string[] extensions) => new(extensions, StringComparer.OrdinalIgnoreCase);

    private static readonly TypeWord Images = new("images", MediaTypes.ImageExtensions);
    private static readonly TypeWord Videos = new("videos", MediaTypes.VideoExtensions);
    private static readonly TypeWord Audio = new("audio files", MediaTypes.AudioExtensions);
    private static readonly TypeWord Documents = new("documents",
        Set(".doc", ".docx", ".odt", ".rtf", ".pdf", ".txt", ".md", ".pages"));
    private static readonly TypeWord Spreadsheets = new("spreadsheets",
        Set(".xls", ".xlsx", ".xlsm", ".csv", ".ods", ".numbers"));
    private static readonly TypeWord Presentations = new("presentations",
        Set(".ppt", ".pptx", ".odp", ".key"));
    private static readonly TypeWord Pdfs = new("PDFs", Set(".pdf"));

    internal static readonly Dictionary<string, TypeWord> TypeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["photo"] = Images, ["photos"] = Images, ["picture"] = Images, ["pictures"] = Images,
        ["pic"] = Images, ["pics"] = Images, ["image"] = Images, ["images"] = Images,
        ["video"] = Videos, ["videos"] = Videos, ["movie"] = Videos, ["movies"] = Videos,
        ["music"] = Audio, ["song"] = Audio, ["songs"] = Audio, ["audio"] = Audio,
        ["doc"] = Documents, ["docs"] = Documents, ["document"] = Documents, ["documents"] = Documents,
        ["spreadsheet"] = Spreadsheets, ["spreadsheets"] = Spreadsheets,
        ["presentation"] = Presentations, ["presentations"] = Presentations, ["slides"] = Presentations,
        ["pdf"] = Pdfs, ["pdfs"] = Pdfs
    };
}
