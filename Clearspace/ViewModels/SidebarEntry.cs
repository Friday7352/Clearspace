namespace Clearspace.ViewModels;

public sealed record SidebarEntry(
    string Name,
    string Path,
    bool IsHeader = false,
    bool IsPinned = false,
    bool IsKnownFolder = false,
    bool IsNetworkDrive = false,
    bool IsPinnedRoot = false,
    bool IsCategory = false,
    string? CategoryId = null,
    bool IsCollapsed = false,
    bool IsSection = false,
    string? SectionId = null,
    bool IsChild = false,
    string? CloudProvider = null,
    bool IsLibrary = false, // NEW (your files): a folder you added to Your files (it can be taken off again)
    bool IsPlace = false,   // NEW (productivity): a heading drawn as a place to go (icon, full-size name), not a small label
    string? Glyph = null,   // NEW (productivity): its icon, from the icon font
    string? Dot = null,     // NEW (productivity): a tab group's colour, shown as a dot before its name
    string? TabGroupId = null) // NEW (productivity): a saved tab group (its Path is empty: a click opens the group, it is never navigated to)
{
    public string DisplayName => Name;
    public string CollapseGlyph => IsCollapsed ? "\uE76C" : "\uE70D";
    public bool HasHub => IsSection && !string.IsNullOrWhiteSpace(Path);

    // NEW (recent): headings that can be folded away show the collapse arrow.
    // CHANGED (recent sidebar): all of them again, Recent included, now that it lists your last folders.
    public bool CanCollapse => IsSection;

    // NEW (productivity): a section heading that is drawn as a small label (every heading but a place).
    public bool IsPlainHeader => IsHeader && !IsPlace;

    public bool HasDot => !string.IsNullOrEmpty(Dot);

    // NEW (productivity)
    public bool IsTabGroup => TabGroupId is not null;
    public bool IsNotTabGroup => TabGroupId is null;
    public string Hint => IsTabGroup ? $"Open the tab group \"{Name}\"" : Path;
    public bool IsNestedPin => IsPinned;

    // NEW (map network drive): the This PC and Network headings have "Map network drive…" in their menu.
    public bool OffersMapDrive => IsSection && SectionId is "this-pc" or "network";

    public bool IsCloudBacked => !string.IsNullOrWhiteSpace(CloudProvider);

    public string CloudHint => IsCloudBacked
        ? $"Backed up by {CloudProvider}"
        : string.Empty;
}
