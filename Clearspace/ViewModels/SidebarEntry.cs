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
    string? CloudProvider = null)
{
    public string DisplayName => Name;
    public string CollapseGlyph => IsCollapsed ? "\uE76C" : "\uE70D";
    public bool HasHub => IsSection && !string.IsNullOrWhiteSpace(Path);
    public bool IsNestedPin => IsPinned;

    public bool IsCloudBacked => !string.IsNullOrWhiteSpace(CloudProvider);

    public string CloudHint => IsCloudBacked
        ? $"Backed up by {CloudProvider}"
        : string.Empty;
}
