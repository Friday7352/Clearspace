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
