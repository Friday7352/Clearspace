// Clearspace | State shared by command actions.

using Clearspace.Models;
using Clearspace.Services;
using Clearspace.ViewModels; // NEW (tabs): ExplorerTabs

namespace Clearspace.Commands;

public sealed class ExplorerContext : ObservableObject
{
    public required NavigationService Navigation { get; init; }

    // NEW (tabs): the window's tabs, for the tab actions (Commands/Actions/TabActions.cs). Null where
    // there is no tab strip (unit tests that build a context on its own).
    public ExplorerTabs? Tabs { get; set; }

    public IntPtr OwnerHandle { get; set; }

    private FileOperationResult? _lastFileOperation;
    public FileOperationResult? LastFileOperation
    {
        get => _lastFileOperation;
        private set
        {
            if (SetProperty(ref _lastFileOperation, value))
                OnPropertyChanged(nameof(HasFileOperationResult));
        }
    }

    public bool HasFileOperationResult => LastFileOperation is not null;

    public void ReportFileOperation(FileOperationResult result) => LastFileOperation = result;

    // NEW (status line): takes the "Delete: Completed." line away again. MainViewModel calls it a few
    // seconds after a result was reported; nothing else clears it (a refresh deliberately does not).
    public void ClearFileOperation() => LastFileOperation = null;

    private string _currentPath = string.Empty;
    public string CurrentPath
    {
        get => _currentPath;
        set => SetProperty(ref _currentPath, value);
    }

    private IReadOnlyList<FileSystemItem> _selectedItems = [];
    public IReadOnlyList<FileSystemItem> SelectedItems
    {
        get => _selectedItems;
        set
        {
            if (SetProperty(ref _selectedItems, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasSingleSelection));
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool HasSelection => SelectedItems.Count > 0;

    public bool HasSingleSelection => SelectedItems.Count == 1;

    // NEW (zip folders): something that is selected is inside a ZIP file. The inside of a ZIP is read-only
    // in Clearspace, so Cut, Rename and Delete are off for it.
    public bool SelectionInArchive => SelectedItems.Any(item => item.IsInArchive);

    // NEW (zip folders): gives real files and folders for things selected inside a ZIP file (copies in
    // Clearspace's temporary folder), or null when that failed or was stopped; `forOpening` asks for copies
    // marked read-only. Set by the main window, which shows progress for a large job. Null where there
    // is no window (unit tests that build a context on its own); the actions then do nothing for such items.
    public Func<IReadOnlyList<FileSystemItem>, bool, IReadOnlyList<string>?>? ArchiveCopies { get; set; }

    // NEW (zip folders): puts a sentence on the status line. Set by the main view model.
    public Action<string>? Notify { get; set; }

    public event EventHandler? SelectionChanged;

    public event EventHandler? RefreshRequested;

    public void RequestRefresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);

    public Action<FileSystemItem?>? BeginRename { get; set; }

    // NEW (batch rename): Rename with several items selected opens the batch rename dialog instead of the
    // single-name box. Set by the main window (the dialog is the window's).
    public Action<IReadOnlyList<FileSystemItem>>? BeginBatchRename { get; set; }

    // NEW (preview pane): shows or hides the preview pane beside the file list. Set by the main window.
    public Action? TogglePreviewPane { get; set; }

    public Func<string, Task<bool>>? OpenLockedFile { get; set; }

    public Action? SelectAll { get; set; }

    public Action? ClearSelection { get; set; }

    public Action? InvertSelection { get; set; }

    public Action? FocusAddressBar { get; set; }

    public Action? ToggleLayout { get; set; }

    // NEW (new window): opens another Clearspace window on a location. Set by the main window.
    public Action<string>? OpenWindow { get; set; }

    // NEW (your files): makes a new folder of your own on the Your files page and starts renaming it.
    // Set by the main window (the renaming box is the window's).
    public Action? NewLibraryFolder { get; set; }

    // NEW (map network drive): opens the Map network drive dialog. Set by the main window (the dialog is
    // the window's).
    public Action? MapNetworkDrive { get; set; }

    public string[] SelectedPaths => SelectedItems.Select(item => item.FullPath).ToArray();
}
