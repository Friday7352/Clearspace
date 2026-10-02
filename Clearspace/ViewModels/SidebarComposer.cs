// Clearspace | Pure sidebar composition, suitable for isolated tests.
using Clearspace.Services;
using static Clearspace.Services.ExplorerLocations;

namespace Clearspace.ViewModels;

internal static class SidebarComposer
{
    internal static IEnumerable<SidebarEntry> Build(IEnumerable<SidebarEntry> drives,
        IReadOnlyList<SidebarSectionInfo> sections,
        Func<string?, IReadOnlyList<KeyValuePair<string, string>>> getPins,
        IEnumerable<SidebarEntry> userFiles, IEnumerable<SidebarEntry> cloudFiles)
    {
        foreach (var section in sections)
        {
            switch (section.Id)
            {
                case "files":
                    yield return Section(section, YourFilesPath);
                    if (!section.IsCollapsed)
                        foreach (var location in userFiles) yield return Child(location);
                    break;

                case "favorites":
                    yield return Section(section, PinnedPath, isFavorites: true);
                    if (!section.IsCollapsed)
                        foreach (var pin in getPins(null))
                            yield return new SidebarEntry(pin.Value, pin.Key, IsPinned: true, IsChild: true);
                    break;

                case "this-pc":
                    yield return Section(section, MyPcPath);
                    if (!section.IsCollapsed)
                        foreach (var drive in drives.Where(drive => !drive.IsNetworkDrive)) yield return Child(drive);
                    break;

                case "network":
                    yield return Section(section, NetworkPath);
                    if (!section.IsCollapsed)
                        foreach (var drive in drives.Where(drive => drive.IsNetworkDrive)) yield return Child(drive);
                    break;

                case "cloud":
                    if (!cloudFiles.Any())
                        break;

                    yield return Section(section, CloudPath);
                    if (!section.IsCollapsed)
                        foreach (var root in cloudFiles) yield return Child(root);
                    break;

                case var _ when section.IsCategory:
                    var categoryId = section.Id["category:".Length..];
                    yield return Section(section, CategoryPathPrefix + categoryId, isCategory: true, categoryId: categoryId);
                    if (!section.IsCollapsed)
                        foreach (var pin in getPins(categoryId))
                            yield return new SidebarEntry(pin.Value, pin.Key, IsPinned: true, CategoryId: categoryId, IsChild: true);
                    break;
            }
        }
    }

    private static SidebarEntry Section(SidebarSectionInfo section, string path, bool isFavorites = false, bool isCategory = false, string? categoryId = null)
        => new(section.Name, path, IsHeader: true, IsPinnedRoot: isFavorites, IsCategory: isCategory,
            CategoryId: categoryId, IsCollapsed: section.IsCollapsed, IsSection: true, SectionId: section.Id);

    private static SidebarEntry Child(SidebarEntry entry) => entry with { IsChild = true };

}
