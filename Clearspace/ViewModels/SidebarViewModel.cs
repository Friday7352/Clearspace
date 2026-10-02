// Clearspace | Sidebar state, persistence commands and drive discovery.
using System.Collections.ObjectModel;
using System.IO;
using Clearspace.Services;
using static Clearspace.Services.ExplorerLocations;

namespace Clearspace.ViewModels;

public sealed class SidebarViewModel
{
    private List<SidebarEntry> _driveEntries = [];
    public ObservableCollection<SidebarEntry> Sidebar { get; } = [];
    public SidebarViewModel() => RebuildSidebar();

    public async Task LoadDrivesAsync()
    {
        List<SidebarEntry> drives;

        try
        {
            drives = await Task.Run(() =>
            {
                _ = CloudStorageService.Roots;
                return EnumerateDrives();
            });
        }
        catch (Exception)
        {
            return;
        }

        _driveEntries = drives;
        RebuildSidebar();
    }

    private static SidebarEntry WithCloud(SidebarEntry entry)
        => entry.IsHeader || entry.CloudProvider is not null || !CloudStorageService.IsDiscovered
            ? entry
            : entry with { CloudProvider = CloudStorageService.RootFor(entry.Path)?.Name };

    public void SetSidebarLocation(string name, string path)
    {
        SettingsService.SetSidebarOverride(name, path);
        RebuildSidebar();
    }

    public void ResetSidebarLocation(string name)
    {
        SettingsService.ClearSidebarOverride(name);
        RebuildSidebar();
    }

    public void PinDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(name))
            name = path;

        SettingsService.PinDirectory(path, name);
        RebuildSidebar();
    }

    public void UnpinDirectory(string path)
    {
        SettingsService.UnpinDirectory(path);
        RebuildSidebar();
    }

    public void CreatePinnedCategory(string name)
    {
        SettingsService.CreatePinnedCategory(name);
        RebuildSidebar();
    }

    public void RenamePinnedCategory(string id, string name)
    {
        SettingsService.RenamePinnedCategory(id, name);
        RebuildSidebar();
    }

    public void DeletePinnedCategory(string id)
    {
        SettingsService.DeletePinnedCategory(id);
        RebuildSidebar();
    }

    public void TogglePinnedCategory(string id)
    {
        SettingsService.TogglePinnedCategory(id);
        RebuildSidebar();
    }

    public void ToggleSidebarSection(string id)
    {
        SettingsService.ToggleSidebarSection(id);
        RebuildSidebar();
    }

    public void RenameSidebarSection(string id, string name)
    {
        SettingsService.RenameSidebarSection(id, name);
        RebuildSidebar();
    }

    public void MoveSidebarSection(string sourceId, string targetId, bool placeAfter)
    {
        SettingsService.MoveSidebarSection(sourceId, targetId, placeAfter);
        RebuildSidebar();
    }

    public void MovePinnedDirectory(string path, string? categoryId, string? targetPath, bool placeAfter)
    {
        SettingsService.MovePinnedDirectory(path, categoryId, targetPath, placeAfter);
        RebuildSidebar();
    }

    public void MovePinnedCategory(string sourceId, string beforeId)
    {
        SettingsService.MovePinnedCategory(sourceId, beforeId);
        RebuildSidebar();
    }

    private void RebuildSidebar()
    {
        Sidebar.Clear();

        foreach (var entry in SidebarComposer.Build(_driveEntries, SettingsService.GetSidebarSections(), SettingsService.GetPins,
            LocationCatalog.BuildUserFileEntries(), LocationCatalog.BuildCloudEntries()).Select(WithCloud))
            Sidebar.Add(entry);
    }

    private static List<SidebarEntry> EnumerateDrives()
    {
        var entries = new List<SidebarEntry>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;

                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
                entries.Add(new SidebarEntry(
                    $"{label} ({drive.Name.TrimEnd('\\')})",
                    drive.RootDirectory.FullName,
                    IsNetworkDrive: drive.DriveType == DriveType.Network));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return entries;
    }

}
