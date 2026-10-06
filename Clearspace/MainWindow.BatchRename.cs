// Clearspace | NEW (batch rename): Rename (F2, the right-click menu) with several items selected.
// One selected item still gets the small name box (BeginRename in MainWindow.xaml.cs). Several open
// BatchRenameWindow, which shows every new name before anything is renamed. Numbers ({n}) follow the order
// the items have in the list, not the order they were clicked in.
using System.IO;
using System.Windows;
using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace;

public partial class MainWindow
{
    private void InitializeBatchRename() => _viewModel.Context.BeginBatchRename = BeginBatchRename;

    private async void BeginBatchRename(IReadOnlyList<FileSystemItem> items)
    {
        try
        {
            // Places that are not files or folders on a disk (drives, This PC entries) have no name to change.
            var positions = new Dictionary<FileSystemItem, int>(_viewModel.Items.Count);
            for (var i = 0; i < _viewModel.Items.Count; i++)
                positions[_viewModel.Items[i]] = i;

            var ordered = items
                .Where(item => !item.IsDriveRoot && Path.IsPathFullyQualified(item.FullPath))
                .OrderBy(item => positions.TryGetValue(item, out var position) ? position : int.MaxValue)
                .ToList();

            if (ordered.Count == 0)
                return;

            if (ordered.Count == 1)
            {
                BeginRename(ordered[0]);
                return;
            }

            var sources = ordered
                .Select(item => new RenameSource(item.FullPath, item.IsFolder, item.DateModified))
                .ToList();

            var dialog = new BatchRenameWindow(sources) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Outcome is not { } outcome)
            {
                FileList.Focus();
                return;
            }

            await _viewModel.ApplyBatchRenameAsync(outcome.Renamed);
            FileList.Focus();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageDialog.Show(this, error.Message, "Couldn't rename", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
