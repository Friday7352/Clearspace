// Clearspace | NEW (archives): "Extract" and "Compress to ZIP file" in the right-click menu.
//  - Extract to "name": unpacks each selected .zip into a new folder beside it. Nothing already on the
//    disk is overwritten; if "name" is taken the folder is called "name (2)".
//  - Extract to…: the same, into a folder you choose.
//  - Compress to ZIP file: packs the selection into one new .zip in the folder you are in.
// Each job shows ProgressWindow, which can stop it. The work itself is in Services/ArchiveService.cs.
// CHANGED (zip folders): a ZIP file now opens in the list like a folder (double-click, or Enter), as in
// File Explorer; it used to go to whichever program Windows uses for .zip files. This file has the window
// side of being inside one (the reading is Services/ArchiveBrowser.cs):
//  - Opening a file, Copy, and dragging out take a copy out of the ZIP first (CopyOutOfArchive).
//  - Extract to… on things selected inside a ZIP unpacks just those into a folder you choose.
//  - Extract all (the toolbar button and the menu of the empty part of the list) unpacks the whole ZIP
//    into a new folder beside it and goes there; Extract all to… asks where.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clearspace.Models;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

public partial class MainWindow
{
    private const string ArchiveGlyph = ""; // Package

    // NEW (zip folders): up to this much is taken out of a ZIP file straight away when you open, copy or
    // drag something; more than this shows the progress window, which can be stopped.
    private const long QuietCopyBytes = 48L * 1024 * 1024;
    private const int QuietCopyFiles = 300;

    // NEW (zip folders): where "Extract to…" last put things, offered first the next time.
    private static string? s_lastExtractFolder;

    // NEW (zip folders): called once from the constructor.
    private void InitializeArchives() => _viewModel.Context.ArchiveCopies = (items, forOpening) => CopyOutOfArchive(items, forOpening);

    // A file or folder that is really on a disk (not a drive, and not an entry of a page such as This PC).
    private static bool IsOnDisk(FileSystemItem item) => !item.IsDriveRoot && Path.IsPathFullyQualified(item.FullPath);

    private static bool IsArchiveItem(FileSystemItem item) => IsOnDisk(item) && !item.IsFolder && ArchiveService.IsArchive(item.FullPath);

    // The files-and-folders menu is opening: show the archive entries that fit what is selected.
    private void OnItemMenuOpened(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.Context.SelectedItems;
        var onDisk = selected.Count > 0 && selected.All(IsOnDisk);
        var archives = onDisk && selected.All(IsArchiveItem);
        var insideZip = selected.Count > 0 && selected.All(item => item.IsInArchive); // NEW (zip folders)

        UpdateDownloadMenu(); // NEW (downloads): "Open the page this came from", "Copy download link", "Move to <folder>"
        UpdateNetworkDriveMenu(); // NEW (map network drive): "Disconnect", when the one selected item is a network drive
        UpdateFileBackupMenu(); // NEW (file backup): "Back up…", and "Earlier versions…" for a file a backup keeps history of

        ExtractMenuItem.Visibility = archives ? Visibility.Visible : Visibility.Collapsed;
        // CHANGED (zip folders): "Extract to…" also shows for things selected inside a ZIP file.
        ExtractToMenuItem.Visibility = archives || insideZip ? Visibility.Visible : Visibility.Collapsed;
        CompressMenuItem.Visibility = onDisk ? Visibility.Visible : Visibility.Collapsed;
        // NEW (zip folders): nothing inside a ZIP file can be tagged.
        TagsMenuItem.Visibility = selected.Any(item => item.IsInArchive) ? Visibility.Collapsed : Visibility.Visible;
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

    // CHANGED (zip folders): the folder is chosen in Clearspace's own chooser (it was Windows' folder
    // picker), and the entry also serves things selected inside a ZIP file.
    private async void OnExtractArchivesTo(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = _viewModel.Context.SelectedItems;
            var insideZip = selected.Count > 0 && selected.All(item => item.IsInArchive);

            var what = insideZip
                ? selected.Count == 1 ? selected[0].Name : $"{selected.Count:N0} items"
                : selected.Count == 1 ? selected[0].Name : $"{selected.Count:N0} ZIP files";
            if (ChooseExtractFolder($"Extract {what}") is not { } folder)
                return;

            if (insideZip)
                ExtractFromArchive([.. selected.Select(item => item.FullPath)], folder);
            else
                await ExtractSelectionAsync(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageDialog.Show(this, error.Message, "Couldn't unpack", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // NEW (zip folders): asks where to unpack, in Clearspace's own chooser. It opens on the folder used
    // last time, else the folder on screen (inside a ZIP: the folder the ZIP file is in). Null when cancelled.
    private string? ChooseExtractFolder(string heading)
    {
        var here = ArchiveLocation.TrySplit(_viewModel.CurrentPath, out var archive, out _)
            ? Path.GetDirectoryName(archive)
            : _viewModel.CurrentPath;
        if (IsTemporary(here))
            here = null; // a ZIP that was opened from inside another ZIP sits in a temporary folder

        var start = s_lastExtractFolder is { } last && Directory.Exists(last) ? last
            : Directory.Exists(here) ? here
            : MainViewModel.LibraryRoot;

        var picker = new FolderPickerWindow(new FolderPickerOptions(
            heading,
            "Open the folder it should go in, then choose Extract here.",
            "Extract here",
            start)
        {
            Places = [.. _viewModel.Sidebar],
            ShowHidden = _viewModel.ShowHiddenItems
        })
        {
            Owner = this
        };

        if (picker.ShowDialog() != true || picker.Chosen is not { } folder)
            return null;

        s_lastExtractFolder = folder;
        return folder;
    }

    private static bool IsTemporary(string? folder)
        => folder is not null && folder.StartsWith(ArchiveBrowser.TempRoot, StringComparison.OrdinalIgnoreCase);

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

    // ------------------------------------------------------------------ NEW (zip folders)

    /// <summary>
    /// Real files and folders for things that are inside a ZIP file: copies in Clearspace's temporary
    /// folder (ArchiveBrowser.Copies). A little is taken out straight away; a lot shows the progress
    /// window, unless <paramref name="quietOnly"/> (a drag, which can't wait behind a window), in which
    /// case the status line says to use Extract instead. Null when it failed, was stopped or was too much.
    /// </summary>
    private IReadOnlyList<string>? CopyOutOfArchive(IReadOnlyList<FileSystemItem> items, bool forOpening, bool quietOnly = false)
    {
        var locations = items.Where(item => item.IsInArchive).Select(item => item.FullPath).ToList();
        if (locations.Count == 0)
            return [];

        var title = forOpening ? "Couldn't open that" : "Couldn't copy that";
        try
        {
            IReadOnlyList<string>? copies = null;
            var (files, bytes) = ArchiveBrowser.Measure(locations);

            if (files <= QuietCopyFiles && bytes <= QuietCopyBytes)
            {
                Mouse.OverrideCursor = Cursors.Wait;
                try { copies = ArchiveBrowser.Copies(locations, forOpening, null, CancellationToken.None); }
                finally { Mouse.OverrideCursor = null; }
            }
            else if (quietOnly)
            {
                _viewModel.ReportStatus("That is a lot to drag out of a ZIP file. Use Extract to… instead.");
                return null;
            }
            else
            {
                var job = ProgressWindow.Run(this,
                    forOpening ? $"Opening {items[0].Name}" : "Copying out of the ZIP file", ArchiveGlyph,
                    async (progress, token) =>
                    {
                        copies = await ArchiveBrowser.CopiesAsync(locations, forOpening, progress, token);
                        return string.Empty;
                    });

                if (job.Stopped)
                    return null;

                if (job.Error is { } problem)
                {
                    MessageDialog.Show(this, problem, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                }
            }

            if (copies is { Count: > 0 })
                return copies;

            _viewModel.ReportStatus("That is no longer in the ZIP file. Press F5 to read it again.");
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
                                          or InvalidDataException or NotSupportedException)
        {
            MessageDialog.Show(this, error.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    // What a drag out of a ZIP file carries: copies of the selected things. Null when there is nothing to drag.
    private string[]? ArchiveDragPaths()
        => CopyOutOfArchive(_viewModel.Context.SelectedItems, forOpening: false, quietOnly: true) is { Count: > 0 } copies
            ? [.. copies]
            : null;

    // Unpacks things selected inside a ZIP file into a folder. Nothing there is overwritten.
    private void ExtractFromArchive(IReadOnlyList<string> locations, string folder)
    {
        IReadOnlyList<string> made = [];
        var job = ProgressWindow.Run(this, $"Unpacking into {Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))}", ArchiveGlyph,
            async (progress, token) =>
            {
                made = await ArchiveBrowser.ExtractAsync(locations, folder, progress, token);
                return folder;
            });

        if (job.Stopped)
            return;

        if (job.Error is { } problem)
        {
            MessageDialog.Show(this, problem, "Couldn't unpack", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _viewModel.ReportStatus(made.Count == 1
            ? $"Unpacked {Path.GetFileName(made[0])} into {folder}."
            : $"Unpacked {made.Count:N0} items into {folder}.");
    }

    // The toolbar's "Extract all", and the same entry in the menu of the empty part of the list.
    private void OnExtractAll(object sender, RoutedEventArgs e) => ExtractAll(choose: false);

    // The whole ZIP file that is open, into a new folder named after it ("Photos.zip" -> "Photos"); the
    // list then goes to that folder. The folder is made beside the ZIP file, or where you choose.
    private void ExtractAll(bool choose)
    {
        if (!ArchiveLocation.TrySplit(_viewModel.CurrentPath, out var archive, out _))
            return;

        try
        {
            var parent = Path.GetDirectoryName(archive);
            if (choose || string.IsNullOrEmpty(parent) || IsTemporary(parent))
                parent = ChooseExtractFolder($"Extract {Path.GetFileName(archive)}");
            if (string.IsNullOrEmpty(parent))
                return;

            var name = Path.GetFileName(archive);
            var job = ProgressWindow.Run(this, $"Unpacking {name}", ArchiveGlyph,
                (progress, token) => ArchiveService.ExtractAsync(archive, parent, progress, token));

            if (job.Stopped)
                return;

            if (job.Error is { } problem)
            {
                MessageDialog.Show(this, problem, $"Couldn't unpack {name}", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (job.Result is not { } folder)
                return;

            _viewModel.Navigation.Navigate(folder);
            _viewModel.ReportStatus($"Unpacked into {Path.GetFileName(folder)}.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageDialog.Show(this, error.Message, "Couldn't unpack", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // The menu of the empty part of the list while it is inside a ZIP file. The folder menu's entries
    // (Paste, New, Open in Terminal, what installed apps add) are all about a folder on a disk.
    private ContextMenu BuildArchivePageMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Entry("Extract all", ArchiveGlyph, () => ExtractAll(choose: false)));
        menu.Items.Add(Entry("Extract all to…", null, () => ExtractAll(choose: true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("Refresh", "", () => _viewModel.RefreshCommand.Execute(null), "F5"));
        menu.Items.Add(Entry("Properties", "", () => _viewModel.PropertiesCommand.Execute(null), "Alt+Enter"));
        return menu;
    }
}
