// Clearspace | NEW (archives): "Extract" and "Compress to ZIP file" in the right-click menu.
//  - Extract to "name": unpacks each selected .zip into a new folder beside it. Nothing already on the
//    disk is overwritten; if "name" is taken the folder is called "name (2)".
//  - Extract to…: the same, into a folder you choose.
//  - Compress to ZIP file: packs the selection into one new .zip in the folder you are in.
// Each job shows ProgressWindow, which can stop it. The work itself is in Services/ArchiveService.cs.
// Opening a .zip (double-click) still goes to whichever program Windows uses for .zip files.
using System.IO;
using System.Windows;
using Clearspace.Models;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

public partial class MainWindow
{
    private const string ArchiveGlyph = ""; // Package

    // A file or folder that is really on a disk (not a drive, and not an entry of a page such as This PC).
    private static bool IsOnDisk(FileSystemItem item) => !item.IsDriveRoot && Path.IsPathFullyQualified(item.FullPath);

    private static bool IsArchiveItem(FileSystemItem item) => IsOnDisk(item) && !item.IsFolder && ArchiveService.IsArchive(item.FullPath);

    // The files-and-folders menu is opening: show the archive entries that fit what is selected.
    private void OnItemMenuOpened(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.Context.SelectedItems;
        var onDisk = selected.Count > 0 && selected.All(IsOnDisk);
        var archives = onDisk && selected.All(IsArchiveItem);

        UpdateDownloadMenu(); // NEW (downloads): "Open the page this came from", "Copy download link", "Move to <folder>"
        UpdateNetworkDriveMenu(); // NEW (map network drive): "Disconnect", when the one selected item is a network drive

        ExtractMenuItem.Visibility = archives ? Visibility.Visible : Visibility.Collapsed;
        ExtractToMenuItem.Visibility = archives ? Visibility.Visible : Visibility.Collapsed;
        CompressMenuItem.Visibility = onDisk ? Visibility.Visible : Visibility.Collapsed;
        UpdateTypeMenu(); // NEW (menu by type): Play, View, Run as administrator, Open with… and the rest

        if (!archives)
            return;

        // A menu reads "_" as "underline the next letter", so underscores in a file name are doubled.
        var folderName = Path.GetFileNameWithoutExtension(selected[0].FullPath).Replace("_", "__");
        ExtractMenuItem.Header = selected.Count == 1
            ? $"Extract to \"{folderName}\""
            : "Extract each to its own folder";
    }

    private async void OnExtractArchives(object sender, RoutedEventArgs e)
    {
        try { await ExtractSelectionAsync(destination: null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageDialog.Show(this, error.Message, "Couldn't unpack", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnExtractArchivesTo(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose where to unpack",
                InitialDirectory = Directory.Exists(_viewModel.CurrentPath) ? _viewModel.CurrentPath : MainViewModel.LibraryRoot,
                Multiselect = false
            };

            if (picker.ShowDialog(this) != true)
                return;

            await ExtractSelectionAsync(picker.FolderName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageDialog.Show(this, error.Message, "Couldn't unpack", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // destination: the folder to unpack into, or null for the folder each archive is in.
    private async Task ExtractSelectionAsync(string? destination)
    {
        var archives = _viewModel.Context.SelectedItems.Where(IsArchiveItem).Select(item => item.FullPath).ToList();
        if (archives.Count == 0)
            return;

        string? lastFolder = null;
        var made = 0;
        foreach (var archive in archives)
        {
            var parent = destination ?? Path.GetDirectoryName(archive);
            if (string.IsNullOrEmpty(parent))
                continue;

            string into = parent;
            var name = Path.GetFileName(archive);
            var job = ProgressWindow.Run(this, $"Unpacking {name}", ArchiveGlyph,
                (progress, token) => ArchiveService.ExtractAsync(archive, into, progress, token));

            if (job.Stopped)
                break; // Cancel stops the rest of the selection too

            if (job.Error is { } problem)
            {
                MessageDialog.Show(this, problem, $"Couldn't unpack {name}", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            lastFolder = job.Result;
            made++;
        }

        if (made == 0)
            return;

        await _viewModel.RefreshAsync();
        SelectInList(lastFolder);
        _viewModel.ReportStatus(made == 1
            ? $"Unpacked into {Path.GetFileName(lastFolder)}."
            : $"Unpacked {made:N0} archives.");
    }

    private async void OnCompressSelection(object sender, RoutedEventArgs e)
    {
        try
        {
            var paths = _viewModel.Context.SelectedItems.Where(IsOnDisk).Select(item => item.FullPath).ToList();
            if (paths.Count == 0)
                return;

            // The new .zip goes in the folder on screen; on a page that is not a folder (search results
            // across drives, Your files), next to the first item.
            var folder = Directory.Exists(_viewModel.CurrentPath)
                ? _viewModel.CurrentPath
                : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(paths[0]));
            if (string.IsNullOrEmpty(folder))
                return;

            var zip = ArchiveService.ZipFor(paths, folder);
            var job = ProgressWindow.Run(this, $"Compressing to {Path.GetFileName(zip)}", ArchiveGlyph,
                (progress, token) => ArchiveService.CompressAsync(paths, zip, progress, token));

            if (job.Error is { } problem)
            {
                MessageDialog.Show(this, problem, "Couldn't make the ZIP file", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (job.Result is null)
                return; // stopped

            await _viewModel.RefreshAsync();
            SelectInList(job.Result);
            _viewModel.ReportStatus($"Made {Path.GetFileName(job.Result)}.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageDialog.Show(this, error.Message, "Couldn't make the ZIP file", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Selects a file or folder in the list if it is on screen (it may be hidden by a search or a filter).
    private void SelectInList(string? path)
    {
        if (path is null)
            return;

        var item = _viewModel.Items.FirstOrDefault(candidate => string.Equals(candidate.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (item is null)
            return;

        FileList.SelectedItem = item;
        FileList.ScrollIntoView(item);
    }
}
