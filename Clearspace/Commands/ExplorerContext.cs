// Clearspace | State shared by command actions.

using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace.Commands;

public sealed class ExplorerContext : ObservableObject
{
    public required NavigationService Navigation { get; init; }

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

    public event EventHandler? SelectionChanged;

    public event EventHandler? RefreshRequested;

    public void RequestRefresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);

    public Action<FileSystemItem?>? BeginRename { get; set; }

    public Action? SelectAll { get; set; }

    public Action? ClearSelection { get; set; }

    public Action? InvertSelection { get; set; }

    public Action? FocusAddressBar { get; set; }

    public Action? ToggleLayout { get; set; }

    public string[] SelectedPaths => SelectedItems.Select(item => item.FullPath).ToArray();
}
