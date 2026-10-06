// Clearspace | NEW (menu by type): the right-click menu's entries for the kind of file, and its icon row.
// Which entries fit is decided in Services/ItemMenu.cs (ItemMenu.For); this file shows and hides them
// when the menu opens and carries them out. The entries themselves are written in MainWindow.xaml, hidden,
// like the archive and download entries before them.
//  - First in the menu: Play in Clearspace (sound), View in Clearspace (pictures), the Extract entries
//    (ZIP), Run as administrator (programs, shortcuts), Open file location (shortcuts).
//  - With Open: Open with… (a file), Open in Terminal (a folder).
//  - Further down: Copy image, Set as desktop background, Print.
//  - Last: a row of icons for Cut, Copy, Paste, Rename and Delete, which used to be five lines of the menu.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace;

public partial class MainWindow
{
    // The files-and-folders menu is opening (called from OnItemMenuOpened, after the Extract entries have
    // been shown or hidden): show the entries that fit what is selected.
    private void UpdateTypeMenu()
    {
        var selected = _viewModel.Context.SelectedItems;

        // CHANGED (only what applies): the lock state is read again for what is selected (a handful of
        // look-ups), so the lock entries go by how things are now, not by when the folder was listed.
        if (selected.Count <= 64)
        {
            foreach (var item in selected)
                item.RefreshLock();
        }

        var pins = SettingsService.GetPinnedDirectories();
        var subjects = selected
            .Select(item => new MenuSubject(item.Extension, item.IsFolder, Path.IsPathFullyQualified(item.FullPath),
                item.LockState, item.CloudState, item.IsDriveRoot, item.IsFolder && pins.ContainsKey(item.FullPath)))
            .ToList();
        var fits = ItemMenu.For(subjects, ShellActions.CanPrint);

        static void Show(MenuItem entry, bool visible) => entry.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        // NEW (only what applies): the general entries, each only when it can do something here.
        var general = ItemMenu.Common(subjects);
        Show(LockMenuItem, general.HasFlag(CommonActions.Lock));
        Show(UnlockMenuItem, general.HasFlag(CommonActions.Unlock));
        Show(RemoveLockMenuItem, general.HasFlag(CommonActions.RemoveLock));
        Show(ChangeLockPasswordMenuItem, general.HasFlag(CommonActions.ChangePassword));
        Show(PinMenuItem, general.HasFlag(CommonActions.Pin));
        Show(DiskUsageMenuItem, general.HasFlag(CommonActions.FolderTools));
        Show(DuplicatesMenuItem, general.HasFlag(CommonActions.FolderTools));
        Show(KeepOnDeviceMenuItem, general.HasFlag(CommonActions.KeepOnDevice));
        Show(FreeUpSpaceMenuItem, general.HasFlag(CommonActions.FreeUpSpace));
        Show(SetFolderTypeMenuItem, general.HasFlag(CommonActions.SetFolderType));

        // The line above the lock entries goes when none of them is shown.
        var locks = new[] { LockMenuItem, UnlockMenuItem, RemoveLockMenuItem, ChangeLockPasswordMenuItem };
        LockMenuSeparator.Visibility = locks.Any(entry => entry.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;

        Show(PlayMenuItem, fits.HasFlag(TypeActions.Play));
        Show(ViewPhotoMenuItem, fits.HasFlag(TypeActions.ViewPhoto));
        Show(RunAsAdminMenuItem, fits.HasFlag(TypeActions.RunAsAdministrator));
        Show(OpenLocationMenuItem, fits.HasFlag(TypeActions.OpenLocation));
        Show(OpenWithMenuItem, fits.HasFlag(TypeActions.OpenWith));
        Show(TerminalHereMenuItem, fits.HasFlag(TypeActions.Terminal));
        Show(CopyImageMenuItem, fits.HasFlag(TypeActions.CopyImage));
        Show(SetWallpaperMenuItem, fits.HasFlag(TypeActions.SetWallpaper));
        Show(PrintMenuItem, fits.HasFlag(TypeActions.Print));

        // The file that is playing offers to pause instead.
        PlayMenuItem.Header = selected.Count == 1 && ReferenceEquals(_viewModel.Player.Current, selected[0]) && _viewModel.Player.IsPlaying
            ? "Pause"
            : "Play in Clearspace";

        // The line under the first group, only when the group has something in it.
        var first = new[] { PlayMenuItem, ViewPhotoMenuItem, ExtractMenuItem, ExtractToMenuItem, RunAsAdminMenuItem, OpenLocationMenuItem };
        TypeMenuSeparator.Visibility = first.Any(entry => entry.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;

        // And the line above the middle group (Compress, Copy image, Print, where a download came from):
        // without it two lines would meet when none of those fit (a drive on the This PC page).
        var middle = new[] { CompressMenuItem, CopyImageMenuItem, SetWallpaperMenuItem, PrintMenuItem, OriginPageMenuItem, OriginLinkMenuItem, FilingMenuItem };
        MiddleMenuSeparator.Visibility = middle.Any(entry => entry.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
    }

    // The one selected item, for the entries that are about a single file or folder.
    private FileSystemItem? OnlySelected() => _viewModel.Context.SelectedItems is [var only] ? only : null;

    // What an action answered: nothing when it went through, otherwise words for the status line.
    private void Report(string? problem)
    {
        if (problem is not null)
            _viewModel.ReportStatus(problem);
    }

    private void OnPlaySelected(object sender, RoutedEventArgs e)
    {
        if (OnlySelected() is not { IsAudio: true } item)
            return;

        if (ReferenceEquals(_viewModel.Player.Current, item))
            _viewModel.Player.TogglePlay();
        else
            _viewModel.PlayTrack(item);
    }

    private void OnViewSelectedPhoto(object sender, RoutedEventArgs e)
    {
        if (OnlySelected() is { } item)
            _viewModel.ViewPhoto(item);
    }

    private void OnRunAsAdministrator(object sender, RoutedEventArgs e)
    {
        if (OnlySelected() is { } item)
            Report(ShellActions.RunAsAdministrator(item.FullPath));
    }

    private void OnOpenWith(object sender, RoutedEventArgs e)
    {
        if (OnlySelected() is { } item)
            Report(ShellActions.OpenWith(item.FullPath));
    }

    private void OnOpenTerminalHere(object sender, RoutedEventArgs e)
    {
        if (OnlySelected() is { IsFolder: true } folder)
            Report(ShellActions.OpenTerminal(folder.FullPath));
    }

    // A shortcut: goes to the folder of the file it points to and selects that file (or opens the folder
    // it points to).
    private void OnOpenShortcutLocation(object sender, RoutedEventArgs e)
    {
        if (OnlySelected() is not { } item)
            return;

        var target = ShellActions.ShortcutTarget(item.FullPath);
        if (target is null || (!File.Exists(target) && !Directory.Exists(target)))
        {
            _viewModel.ReportStatus(target is null
                ? "This shortcut doesn't point to a file or folder."
                : $"{target} is no longer there.");
            return;
        }

        RevealFile(target);
    }

    private void OnCopyImage(object sender, RoutedEventArgs e)
    {
        if (OnlySelected() is not { } item)
            return;

        try
        {
            var picture = new BitmapImage();
            picture.BeginInit();
            picture.UriSource = new Uri(item.FullPath, UriKind.Absolute);
            picture.CacheOption = BitmapCacheOption.OnLoad; // read now, so the file is not kept open
            picture.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            picture.EndInit();
            picture.Freeze();

            Clipboard.SetImage(picture);
            _viewModel.ReportStatus($"Copied the picture in {item.Name}. Paste it into a chat, a document or an image editor.");
        }
        catch (Exception)
        {
            _viewModel.ReportStatus($"Couldn't copy the picture in {item.Name}.");
        }
    }

    private void OnSetWallpaper(object sender, RoutedEventArgs e)
    {
        if (OnlySelected() is not { } item)
            return;

        var problem = ShellActions.SetWallpaper(item.FullPath);
        _viewModel.ReportStatus(problem ?? $"{item.Name} is now the desktop background.");
    }

    private void OnPrintSelected(object sender, RoutedEventArgs e)
    {
        var files = _viewModel.Context.SelectedItems.Where(item => !item.IsFolder).Take(ItemMenu.MostToPrint).ToList();
        if (files.Count == 0)
            return;

        string? problem = null;
        foreach (var file in files)
            problem ??= ShellActions.Print(file.FullPath);

        _viewModel.ReportStatus(problem ?? (files.Count == 1 ? $"Sent {files[0].Name} to its program to print." : $"Sent {files.Count:N0} files to be printed."));
    }

    // ---- the row of icons at the bottom of the menu

    // A button in the row was pressed. Buttons do not close a menu by themselves, so that is done here;
    // the action then runs once the menu is gone and the keyboard is back with the list (Rename puts a
    // text box on screen, which must not lose the keyboard to a menu that is still closing).
    private void OnMenuRowClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: string action })
            return;

        if (_itemMenu is not null)
            _itemMenu.IsOpen = false;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var command = action switch
            {
                "Cut" => _viewModel.CutCommand,
                "Copy" => _viewModel.CopyCommand,
                "Paste" => _viewModel.PasteCommand,
                "Rename" => _viewModel.RenameCommand,
                "Delete" => _viewModel.DeleteCommand,
                _ => null
            };

            if (command is { IsExecutable: true })
                command.Execute(null);
        });
    }
}
