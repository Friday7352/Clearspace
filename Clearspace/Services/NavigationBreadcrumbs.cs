// Clearspace | Breadcrumb presentation independent of the main view model.
using System.IO;
using Clearspace.ViewModels;
using static Clearspace.Services.ExplorerLocations;

namespace Clearspace.Services;

internal static class NavigationBreadcrumbs
{
    public static IReadOnlyList<Breadcrumb> BuildBreadcrumbs(string path, Func<string, string?>? categoryName = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return [];

        if (path.Equals(MyPcPath, StringComparison.OrdinalIgnoreCase))
            return [new Breadcrumb("This PC", MyPcPath)];

        if (path.Equals(NetworkPath, StringComparison.OrdinalIgnoreCase))
            return [new Breadcrumb("Network", NetworkPath)];

        if (path.Equals(YourFilesPath, StringComparison.OrdinalIgnoreCase))
            return [new Breadcrumb("Your files", YourFilesPath)];

        if (path.Equals(PinnedPath, StringComparison.OrdinalIgnoreCase))
            return [new Breadcrumb("Pinned directories", PinnedPath)];

        if (path.Equals(CloudPath, StringComparison.OrdinalIgnoreCase))
            return [new Breadcrumb("Cloud", CloudPath)];

        if (path.StartsWith(CategoryPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var categoryId = path[CategoryPathPrefix.Length..];
            var name = categoryName?.Invoke(categoryId) ?? "Category";
            return [new Breadcrumb(name, path)];
        }

        // NEW (zip folders): inside a ZIP file the trail is the folders down to the ZIP file, the ZIP file
        // itself, then the folders inside it: C: > Users > you > Downloads > Photos.zip > Trip > 2024.
        // Each part goes where it says: a real folder, the top of the ZIP, or a folder inside it.
        if (ArchiveLocation.TrySplit(path, out var archive, out var inside))
        {
            var trail = new List<Breadcrumb>();
            if (Path.GetDirectoryName(archive) is { Length: > 0 } folder)
                trail.AddRange(BuildBreadcrumbs(folder));

            trail.Add(new Breadcrumb(Path.GetFileName(archive), ArchiveLocation.For(archive)));

            var soFar = string.Empty;
            foreach (var part in inside.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                soFar = soFar.Length == 0 ? part : soFar + "/" + part;
                trail.Add(new Breadcrumb(part, ArchiveLocation.For(archive, soFar)));
            }

            return trail;
        }

        var crumbs = new List<Breadcrumb>();
        var current = path;

        while (!string.IsNullOrEmpty(current))
        {
            var name = Path.GetFileName(current);
            if (string.IsNullOrEmpty(name))
                name = current.TrimEnd('\\');

            crumbs.Insert(0, new Breadcrumb(name, current));

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                break;

            current = parent;
        }

        return crumbs;
    }

}
