// Clearspace | NEW (map network drive): mapping and disconnecting network drives from the window.
// Before this a drive letter for a network folder could only be made in File Explorer. Now:
//  - "Map network drive…" opens MapNetworkDriveWindow. It is on the Network page (a button in its banner),
//    in the menu of the empty part of the This PC and Network pages, and in the sidebar menus of the
//    This PC and Network headings. They all run the one command (MapNetworkDriveAction).
//  - "Disconnect" is in the menu of a network drive, in the sidebar and on the This PC / Network pages.
// The Windows calls are in Services/NetworkMapping.cs. Afterwards every open window's sidebar reads the
// drives again, and a window that is showing This PC or Network reloads that page.
using System.Windows;
using System.Windows.Controls;
using Clearspace.Models;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

public partial class MainWindow
{
    private void InitializeNetworkDrives()
    {
        _viewModel.Context.MapNetworkDrive = ShowMapNetworkDrive;
    }

    // The sidebar menus' "Map network drive…".
    private void OnMapNetworkDrive(object sender, RoutedEventArgs e) => _viewModel.MapNetworkDriveCommand.Execute(null);

    // What the command does (ExplorerContext.MapNetworkDrive): the dialog, then showing the new drive.
    private async void ShowMapNetworkDrive()
    {
        try
        {
            // Browsing a network folder by its \\server\share name: offer that folder to start with.
            var here = _viewModel.CurrentPath;
            var dialog = new MapNetworkDriveWindow(here.StartsWith(@"\\", StringComparison.Ordinal) ? here : null) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.MappedRoot is not { } root)
                return;

            var onNetworkPage = _viewModel.IsNetworkHub;
            DrivesChanged(reloadThisWindow: !onNetworkPage);

            if (!onNetworkPage)
            {
                _viewModel.Navigation.Navigate(root); // as File Explorer does: open the new drive
                return;
            }

            // On the Network page the new drive appears among the others, selected.
            await _viewModel.RefreshAsync();
            var item = _viewModel.Items.FirstOrDefault(candidate => string.Equals(candidate.FullPath, root, StringComparison.OrdinalIgnoreCase));
            if (item is null)
                return;

            FileList.SelectedItem = item;
            FileList.ScrollIntoView(item);
        }
        catch (Exception)
        {
            // the drive is mapped; failing to show it is not worth an error box
        }
    }

    // ---- Disconnect

    // The one selected item, when it is a mapped network drive (a tile on the This PC or Network page).
    private FileSystemItem? SelectedNetworkDrive()
        => _viewModel.Context.SelectedItems is [{ IsDriveRoot: true } only] && NetworkDrives.IsNetworkRoot(only.FullPath)
            ? only
            : null;

    // The files-and-folders menu is opening (called from OnItemMenuOpened): Disconnect shows for a network drive.
    private void UpdateNetworkDriveMenu()
        => DisconnectDriveMenuItem.Visibility = SelectedNetworkDrive() is not null ? Visibility.Visible : Visibility.Collapsed;

    private async void OnDisconnectNetworkDrive(object sender, RoutedEventArgs e)
    {
        if (SelectedNetworkDrive() is { } drive)
            await DisconnectDriveAsync(drive.FullPath);
    }

    private async void OnDisconnectSidebarDrive(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: SidebarEntry { IsNetworkDrive: true } entry })
            await DisconnectDriveAsync(entry.Path);
    }

    private async Task DisconnectDriveAsync(string root)
    {
        try
        {
            if (root.Length < 2)
                return;

            var name = root[..2].ToUpperInvariant(); // "Z:"
            var share = NetworkMapping.RemoteName(root);
            var title = share is null ? name : $"{name} ({share})";

            // Clearspace keeps a drive busy itself while a tab is inside it (the folder on screen is
            // watched for changes), and Windows refuses to disconnect a busy drive. So every tab on the
            // drive, in every window, steps out to the Network page first, and the watcher gets a moment
            // to let go.
            var left = false;
            foreach (var window in Application.Current.Windows.OfType<MainWindow>())
                left |= window.LeaveDrive(name);
            if (left)
                await Task.Delay(300);

            var result = await Task.Run(() => NetworkMapping.Disconnect(root, force: false));

            if (result.InUse)
            {
                var answer = MessageDialog.Show(this,
                    $"Windows says files or folders on {title} are still open, in Clearspace or in another program. " +
                    "Disconnecting now can lose changes that have not been saved.\n\nDisconnect anyway?",
                    $"Disconnect {name}?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                    return;

                result = await Task.Run(() => NetworkMapping.Disconnect(root, force: true));
            }

            if (!result.Succeeded)
            {
                MessageDialog.Show(this, result.Message, $"Couldn't disconnect {name}", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DrivesChanged(reloadThisWindow: true);
        }
        catch (Exception error)
        {
            MessageDialog.Show(this, error.Message, "Couldn't disconnect the drive", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Moves every tab of this window that is on the drive ("Z:") out to the Network page. True when one was.
    internal bool LeaveDrive(string name)
    {
        bool OnDrive(string location) => location.StartsWith(name, StringComparison.OrdinalIgnoreCase);

        var any = Tabs.Items.Any(tab => tab.History.CurrentPath is { } current && OnDrive(current));
        if (any)
            Tabs.MoveOut(OnDrive, ExplorerLocations.NetworkPath);
        return any;
    }

    // A drive was mapped or disconnected: every window's sidebar lists the drives again, network indexing
    // (when it is on) takes the drive up or lets it go, and the This PC and Network pages on screen reload.
    private void DrivesChanged(bool reloadThisWindow)
    {
        SidebarViewModel.ReloadDrivesEverywhere();
        NetworkDrives.ProbeSoon();

        foreach (var window in Application.Current.Windows.OfType<MainWindow>())
        {
            if (!window._viewModel.IsDrivesPage || (ReferenceEquals(window, this) && !reloadThisWindow))
                continue;

            _ = window._viewModel.RefreshAsync();
        }
    }

    // ---- the menu of the empty part of the This PC and Network pages

    private ContextMenu BuildDrivesPageMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Entry("Map network drive…", "\uE8CE", () => _viewModel.MapNetworkDriveCommand.Execute(null)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("Refresh", "\uE72C", () => _viewModel.RefreshCommand.Execute(null), "F5"));
        return menu;
    }
}
