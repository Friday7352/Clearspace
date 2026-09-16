// Clearspace | Materialization of drive and hub locations.
using System.IO;
using Clearspace.Models;
using Clearspace.Native;
using Clearspace.ViewModels;

namespace Clearspace.Services;

internal static class LocationCatalog
{
    internal static SidebarEntry Entry(string name, string defaultPath)
    {
        var path = SettingsService.GetSidebarOverride(name) ?? defaultPath;

        return new SidebarEntry(
            name,
            path,
            IsKnownFolder: true,
            CloudProvider: CloudStorageService.IsDiscovered
                ? CloudStorageService.RootFor(path)?.Name
                : null);
    }

    internal static List<FileSystemItem> EnumerateDriveItems(bool networkOnly)
    {
        var entries = new List<FileSystemItem>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady ||
                    (networkOnly && drive.DriveType != DriveType.Network) ||
                    (!networkOnly && drive.DriveType == DriveType.Network))
                    continue;

                entries.Add(FileSystemItem.FromDrive(drive));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return entries;
    }

    internal static List<FileSystemItem> BuildHubItems(bool pinnedOnly, bool cloudOnly, string? categoryId)
    {
        IEnumerable<SidebarEntry> locations = categoryId is not null
            ? SettingsService.GetPins(categoryId).Select(pin => new SidebarEntry(pin.Value, pin.Key, IsPinned: true, CategoryId: categoryId))
            : cloudOnly
            ? BuildCloudEntries()
            : pinnedOnly
            ? SettingsService.GetPinnedDirectories()
                .OrderBy(pin => pin.Value, StringComparer.OrdinalIgnoreCase)
                .Select(pin => new SidebarEntry(pin.Value, pin.Key, IsPinned: true))
            : BuildUserFileEntries();

        return locations
            .Select(location => FileSystemItem.FromLocation(location.Path, location.Name))
            .Where(item => item is not null)
            .Cast<FileSystemItem>()
            .ToList();
    }

    internal static IEnumerable<SidebarEntry> BuildCloudEntries()
        => CloudStorageService.Roots.Select(root => new SidebarEntry(root.Name, root.Path, IsKnownFolder: true));

    internal static IEnumerable<SidebarEntry> BuildUserFileEntries()
    {
        yield return Entry("Desktop", KnownFolders.Desktop);
        yield return Entry("Documents", KnownFolders.Documents);
        yield return Entry("Downloads", KnownFolders.Downloads);
        yield return Entry("Pictures", KnownFolders.Pictures);
        yield return Entry("Music", KnownFolders.Music);
        yield return Entry("Videos", KnownFolders.Videos);
    }
}
