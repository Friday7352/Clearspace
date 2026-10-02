// Clearspace | The main window's side of locking.
// CHANGED (Explorer re-lock): everything that has to work without a main window (re-locking, Explorer
// commands, the password prompts) moved to LockAgent.cs. This file only connects the window to it:
//  - the Lock / Unlock / Remove lock menu items and Settings > Remove all locks;
//  - the password gate: moving into a locked folder (or anywhere inside it) asks for the password first;
//  - telling LockAgent where the window is, so things are locked again once you've left their folder.
using System.IO;
using System.Windows;
using Clearspace.Services;

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
        Closed += (_, _) => Agent.Detach(this);
    }

    // ---- Used by LockAgent ----

    internal string? CurrentLocation => _viewModel.Navigation.CurrentPath;

    internal void NavigateTo(string path) => _viewModel.Navigation.Navigate(path);

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
}
