// Clearspace | NEW (duplicates): opening and closing the "Find duplicates" page.
// The page (DuplicatesView.cs) takes the place of the file list, the same way Disk usage does, and works on
// any folder whatever its folder type. It is reached from:
//   - the toolbar button beside Disk usage (the folder you are in)
//   - "Find duplicates…" in the right-click menu (the folder you right-clicked, else the folder you are in)
//   - "Find duplicates…" in the menu of the empty area of the list
// Esc, the mouse's back button or the page's own Back button return to the files. "Show in its folder" on
// a row returns to the files with that file selected.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Clearspace.ViewModels;

namespace Clearspace;

public partial class MainWindow
{
    private DuplicatesView? _duplicatesView;
    private string? _revealPath; // a file to select once the folder it is in has been listed

    private void InitializeDuplicates()
    {
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.IsLoading) || _viewModel.IsLoading || _revealPath is not { } path)
                return;

            // The folder is listed (or the window went somewhere else instead): either way this was the one chance.
            _revealPath = null;
            if (IsCurrentFolder(Path.GetDirectoryName(path)))
            {
                SelectInList(path);
                FileList.Focus();
            }
        };
    }

    private void OnFindDuplicates(object sender, RoutedEventArgs e)
    {
        if (_duplicatesView is not null || _diskUsageView is not null || _indexingView is not null)
            return;

        // From the right-click menu with one folder selected: that folder. Otherwise the folder on screen.
        var selected = _viewModel.Context.SelectedItems;
        var path = sender is MenuItem && selected.Count == 1 && selected[0].IsFolder
            ? selected[0].FullPath
            : _viewModel.CurrentPath;

        // Pages that are not folders (This PC, Your files, search across drives) have nothing to look through.
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        {
            _viewModel.ReportStatus("Open a folder first, then look for duplicates in it.");
            return;
        }

        _duplicatesView = new DuplicatesView(path);
        _duplicatesView.CloseRequested += (_, _) => CloseDuplicates();
        _duplicatesView.FileOperationCompleted += (_, _) => _ = _viewModel.RefreshAsync();
        _duplicatesView.ShowFileRequested += (_, file) =>
        {
            CloseDuplicates();
            RevealFile(file);
        };

        DuplicatesHost.Content = _duplicatesView;
        ExplorerShell.Visibility = Visibility.Collapsed;
        DuplicatesHost.Visibility = Visibility.Visible;
        FlashPage(); // the e-reader theme refreshes its "page"
    }

    private void CloseDuplicates()
    {
        if (_duplicatesView is null)
            return;

        _duplicatesView.Dispose(); // stops a search that is still running
        _duplicatesView = null;
        DuplicatesHost.Content = null;
        DuplicatesHost.Visibility = Visibility.Collapsed;
        ExplorerShell.Visibility = Visibility.Visible;
        FileList.Focus();
        FlashPage();
    }

    // Goes to the folder a file is in and selects the file there.
    private void RevealFile(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder))
            return;

        if (IsCurrentFolder(folder) && !_viewModel.IsLoading && !_viewModel.HasSearch)
        {
            SelectInList(path);
            FileList.Focus();
            return;
        }

        _revealPath = path;
        if (_viewModel.HasSearch)
            _viewModel.SearchText = string.Empty; // a search would hide the file
        if (IsCurrentFolder(folder))
            _ = _viewModel.RefreshAsync();
        else
            _viewModel.Navigation.Navigate(folder);
    }

    private bool IsCurrentFolder(string? folder)
        => !string.IsNullOrEmpty(folder) &&
           string.Equals(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(_viewModel.CurrentPath), StringComparison.OrdinalIgnoreCase);
}
