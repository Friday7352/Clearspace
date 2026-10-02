// Clearspace | Sidebar state, persistence commands and drive discovery.
using System.Collections.ObjectModel;
using System.IO;
using Clearspace.Services;
using static Clearspace.Services.ExplorerLocations;

namespace Clearspace.ViewModels;

// CHANGED (new window): every window has its own sidebar, built from the same settings. A change made in
// one window (a pin, a category, a folder added to Your files) now rebuilds the sidebar of every open
// window (RebuildEverySidebar), not only its own.
public sealed class SidebarViewModel : IDisposable
{
    private static event Action? SettingsChanged; // NEW (new window)

    private List<SidebarEntry> _driveEntries = [];
    public ObservableCollection<SidebarEntry> Sidebar { get; } = [];

    public SidebarViewModel()
    {
        RebuildSidebar();
        SettingsChanged += RebuildSidebar;
    }

    // NEW (new window): a closed window's sidebar stops listening (called from MainViewModel.Dispose).
    public void Dispose() => SettingsChanged -= RebuildSidebar;

    private static void RebuildEverySidebar() => SettingsChanged?.Invoke();

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
        RebuildEverySidebar();
    }

    public void ResetSidebarLocation(string name)
    {
        SettingsService.ClearSidebarOverride(name);
        RebuildEverySidebar();
    }

    // ---- NEW (your files): folders you add to Your files, after Windows' six.

    public void AddLibraryFolder(string path)
    {
        if (SettingsService.AddLibraryFolder(path))
            RebuildEverySidebar();
    }

    public void RemoveLibraryFolder(string path)
    {
        if (SettingsService.RemoveLibraryFolder(path))
            RebuildEverySidebar();
    }

    // A folder was renamed in Clearspace: if it is one of yours, it stays in Your files under its new name.
    public void RenameLibraryFolder(string from, string to)
    {
        if (SettingsService.MoveLibraryFolder(from, to))
            RebuildEverySidebar();
    }

    // Forgets folders of yours that no longer exist. True when any were dropped.
    public bool PruneLibraryFolders()
    {
        if (!SettingsService.PruneLibraryFolders())
            return false;

        RebuildEverySidebar();
        return true;
    }

    public void PinDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(name))
            name = path;

        SettingsService.PinDirectory(path, name);
        RebuildEverySidebar();
    }

    public void UnpinDirectory(string path)
    {
        SettingsService.UnpinDirectory(path);
        RebuildEverySidebar();
    }

    public void CreatePinnedCategory(string name)
    {
        SettingsService.CreatePinnedCategory(name);
        RebuildEverySidebar();
    }

    public void RenamePinnedCategory(string id, string name)
    {
        SettingsService.RenamePinnedCategory(id, name);
        RebuildEverySidebar();
    }

    public void DeletePinnedCategory(string id)
    {
        SettingsService.DeletePinnedCategory(id);
        RebuildEverySidebar();
    }

    public void TogglePinnedCategory(string id)
    {
        SettingsService.TogglePinnedCategory(id);
        RebuildEverySidebar();
    }

    public void ToggleSidebarSection(string id)
    {
        SettingsService.ToggleSidebarSection(id);
        RebuildEverySidebar();
    }

    public void RenameSidebarSection(string id, string name)
    {
        SettingsService.RenameSidebarSection(id, name);
        RebuildEverySidebar();
    }

    public void MoveSidebarSection(string sourceId, string targetId, bool placeAfter)
    {
        SettingsService.MoveSidebarSection(sourceId, targetId, placeAfter);
        RebuildEverySidebar();
    }

    public void MovePinnedDirectory(string path, string? categoryId, string? targetPath, bool placeAfter)
    {
        SettingsService.MovePinnedDirectory(path, categoryId, targetPath, placeAfter);
        RebuildEverySidebar();
    }

    public void MovePinnedCategory(string sourceId, string beforeId)
    {
        SettingsService.MovePinnedCategory(sourceId, beforeId);
        RebuildEverySidebar();
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
