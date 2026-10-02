// Clearspace | The main window's side of locking.
// CHANGED (Explorer re-lock): everything that has to work without a main window (re-locking, Explorer
// commands, the password prompts) moved to LockAgent.cs. This file only connects the window to it:
//  - the Lock / Unlock / Remove lock menu items and Settings > Remove all locks;
//  - the password gate: moving into a locked folder (or anywhere inside it) asks for the password first;
//  - telling LockAgent where the window is, so things are locked again once you've left their folder.
using System.IO;
using System.Windows;
using Clearspace.Services;
using Clearspace.ViewModels; // NEW (tabs): ExplorerTabs.ParentOf

namespace Clearspace;

public partial class MainWindow
{
    private static LockAgent Agent => LockAgent.Current;

    private void InitializeFileLocks()
    {
        _viewModel.Context.OpenLockedFile = Agent.OpenLockedFile;
        _viewModel.Navigation.CanEnter = CanEnterLocation;
        _viewModel.Navigation.Navigated += (_, _) => Agent.Check();
        Loaded += (_, _) => Agent.Attach(this);
        Activated += (_, _) => Agent.Activated(this); // NEW (new window): password prompts belong to the window in use
        Closed += (_, _) => Agent.Detach(this);
    }

    // ---- Used by LockAgent ----

    internal string? CurrentLocation => _viewModel.Navigation.CurrentPath;

    // NEW (tabs): every folder this window counts as showing: the tab on screen, and background tabs that
    // have been shown where they are. Something unlocked for a visit stays unlocked while a tab is in it,
    // the same way it does while an Explorer tab is.
    internal IEnumerable<string> ShownLocations => Tabs.ShownLocations;

    internal void NavigateTo(string path) => _viewModel.Navigation.Navigate(path);

    // NEW (tabs): a folder is about to be locked again on request: every tab that is in it (or anywhere
    // inside it) moves out to the folder above. Background tabs are moved without being shown.
    internal void LeaveFolder(string folder) => Tabs.MoveOut(
        location => FileLockService.SamePath(location, folder) || FileLockService.IsInside(location, folder),
        ExplorerTabs.ParentOf(folder));

    internal void ReportLockStatus(string message) => _viewModel.ReportFileLock(message);

    internal void RefreshLockBadges() => _viewModel.RefreshLockBadges();

    internal void RefreshAfterLockChange(string? message) => _ = RefreshAfterFileLock(message);

    private async Task RefreshAfterFileLock(string? message)
    {
        await _viewModel.RefreshAsync();
        if (message is not null) _viewModel.ReportFileLock(message);
    }

    internal void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true; // Windows only lets a background app take focus this way
        Topmost = false;
        Focus();
    }

    // ---- Menu: Lock with password… / Unlock… / Remove lock… / Settings ----

    private void OnLockFile(object sender, RoutedEventArgs e)
    {
        if (SingleSelection("lock") is { } item) Agent.LockItem(item.FullPath, item.IsFolder, attach: true);
    }

    private void OnUnlockFile(object sender, RoutedEventArgs e)
    {
        if (SingleSelection("unlock") is not { } item) return;
        if (item.IsFolder) _viewModel.Navigation.Navigate(item.FullPath); // asks for the password if it's locked
        else Agent.UnlockFile(item.FullPath, attach: true);
    }

    // Works on every selected file and folder.
    private void OnRemoveLock(object sender, RoutedEventArgs e)
    {
        var paths = _viewModel.Context.SelectedItems.Select(item => item.FullPath).ToList();
        if (paths.Count == 0) _viewModel.ReportFileLock("Select the files and folders to remove the lock from.");
        else Agent.RemoveLocks(paths, attach: true);
    }

    // NEW (change password): works on every selected file and folder.
    private void OnChangeLockPassword(object sender, RoutedEventArgs e)
    {
        var paths = _viewModel.Context.SelectedItems.Select(item => item.FullPath).ToList();
        if (paths.Count > 0) Agent.ChangePassword(paths, attach: true);
    }

    // Settings > Remove all locks and reset password.
    private void OnRemoveAllLocks(object sender, RoutedEventArgs e) => Agent.RemoveAll();

    private Models.FileSystemItem? SingleSelection(string verb)
    {
        var items = _viewModel.Context.SelectedItems;
        if (items.Count == 1) return items[0];
        _viewModel.ReportFileLock($"Select one file or folder to {verb}.");
        return null;
    }

    // ---- The password gate ----

    private bool CanEnterLocation(string path)
    {
        if (path.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase)) return true;
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception) { return true; } // not a file system path; let normal navigation report it

        if (FileLockRegistry.Governing(full) is not { } governing) return true;
        var entered = Agent.OpenFolderPrompt(governing.Folder, attach: true);
        _viewModel.RefreshLockBadges();
        return entered;
    }

    // NEW (tabs): whether going to this location would put the password prompt on screen (it is in a
    // locked folder that is not open right now). Asked before anything that must not be interrupted by a
    // prompt: switching tabs while files are being dragged, taking a drop on a tab.
    private static bool WouldAskForPassword(string path)
    {
        if (path.Length == 0 || path.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase)) return false;
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception) { return false; }

        return FileLockRegistry.Governing(full) is { } governing && !Agent.IsOpenHere(governing.Folder);
    }
}
