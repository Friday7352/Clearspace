// Clearspace | NEW (shelf and inspector): the column of panels on the far side of the file list.
//   - Inspector (Alt+I, or the toolbar's (i) button): everything about the selected file (Controls/InspectorPane.cs).
//   - Shelf (Alt+S, or the toolbar's shelf button): files collected from anywhere, then moved, copied or zipped
//     into the folder on screen all at once (Controls/ShelfPane.cs). "Add to shelf" on a file's menu puts the
//     selection on it.
// With both open, the Inspector sits above the Shelf. The column goes on the side away from the sidebar
// (Services/ThemeExplorerLayout.cs); the splitter beside it changes its width. Which are open, the width and what
// is on the shelf are remembered (settings.json). They belong to the person, not to a theme, so they stay open
// whichever theme is picked; a theme's custom layout can say where the column goes ("dock").
// Nothing is made until it is first opened, so a window that never uses them pays nothing for them.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Clearspace.Controls;
using Clearspace.Models;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

public partial class MainWindow
{
    private const double DockMinWidth = 220;
    private const double DockMaxWidth = 600;

    private InspectorPane? _inspector;
    private ShelfPane? _shelf;
    private bool _inspectorShown, _shelfShown;

    /// <summary>Whether the Inspector is open.</summary>
    internal bool InspectorShown => _inspectorShown;

    /// <summary>Whether the Shelf is open.</summary>
    internal bool ShelfShown => _shelfShown;

    private bool DockShown => _inspectorShown || _shelfShown;

    /// <summary>For tests: the shelf, once it has been opened.</summary>
    internal ShelfPane? Shelf => _shelf;

    private void InitializeDock()
    {
        _viewModel.Context.ToggleInspector = () => SetInspectorShown(!_inspectorShown);
        _viewModel.Context.ToggleShelf = () => SetShelfShown(!_shelfShown);
        _viewModel.Context.AddToShelf = AddToShelf;
        _viewModel.Context.SelectionChanged += (_, _) =>
        {
            if (_inspectorShown) _inspector?.Show(_viewModel.Context.SelectedItems);
        };
        // The shelf's buttons say which folder "here" is, and what has gone since.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPath) && _shelfShown) _shelf?.Refresh();
        };
        Activated += (_, _) => { if (_shelfShown) _shelf?.Refresh(); };
        DockSplitter.DragCompleted += (_, _) =>
        {
            if (DockShown) SettingsService.SetDockWidth(DockColumn.ActualWidth);
        };

        if (SettingsService.GetShowInspector()) SetInspectorShown(true, remember: false);
        if (SettingsService.GetShowShelf()) SetShelfShown(true, remember: false);
    }

    /// <summary>Opens or closes the Inspector.</summary>
    internal void SetInspectorShown(bool shown, bool remember = true)
    {
        _inspectorShown = shown;
        InspectorToggle.IsChecked = shown; // CHANGED (lit when open)
        if (shown) (_inspector ??= new InspectorPane(() => SetInspectorShown(false))).Show(_viewModel.Context.SelectedItems);
        else _inspector?.Show([]);
        LayOutDock();
        if (remember) SettingsService.SetShowInspector(shown);
    }

    /// <summary>Opens or closes the Shelf.</summary>
    internal void SetShelfShown(bool shown, bool remember = true)
    {
        _shelfShown = shown;
        ShelfToggle.IsChecked = shown; // CHANGED (lit when open)
        if (shown) (_shelf ??= new ShelfPane(() => SetShelfShown(false), ShelfFolder, RunShelf, () => AddToShelf(_viewModel.Context.SelectedItems))).Refresh();
        LayOutDock();
        if (remember) SettingsService.SetShowShelf(shown);
    }

    // Puts the open panels in the column (the Inspector above the Shelf) and gives the column its width.
    private void LayOutDock()
    {
        var stack = new Grid { Margin = new Thickness(14) };
        void Put(FrameworkElement pane)
        {
            if (pane.Parent is Panel old) old.Children.Remove(pane);
            if (stack.Children.Count > 0)
            {
                var line = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 12) };
                line.SetResourceReference(Border.BackgroundProperty, "EdgeRaised");
                stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(line, stack.RowDefinitions.Count - 1);
                stack.Children.Add(line);
            }
            stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
            Grid.SetRow(pane, stack.RowDefinitions.Count - 1);
            stack.Children.Add(pane);
        }
        if (_inspectorShown && _inspector is not null) Put(_inspector);
        if (_shelfShown && _shelf is not null) Put(_shelf);

        if (DockShown)
        {
            DockCard.Child = stack;
            ContentColumn.MinWidth = ListMinWidthBesidePreview;
            DockSplitterColumn.Width = GridLength.Auto;
            DockColumn.MinWidth = DockMinWidth;
            DockColumn.MaxWidth = DockMaxWidth;
            DockColumn.Width = new GridLength(SettingsService.GetDockWidth());
            DockSplitter.Visibility = Visibility.Visible;
            DockCard.Visibility = Visibility.Visible;
        }
        else
        {
            if (DockCard.Child is Panel panes) panes.Children.Clear();
            DockCard.Child = null;
            DockSplitter.Visibility = Visibility.Collapsed;
            DockCard.Visibility = Visibility.Collapsed;
            DockColumn.MinWidth = 0;
            DockColumn.Width = new GridLength(0);
            DockSplitterColumn.Width = new GridLength(0);
            if (!_previewShown) ContentColumn.MinWidth = 0;
        }
        ThemeExplorerLayout.Apply(this);
    }

    // ---------------------------------------------------------------- the shelf's work

    // The folder on screen, when it is a real folder (not search results, This PC or the inside of a ZIP file).
    private string? ShelfFolder()
    {
        var path = _viewModel.CurrentPath;
        return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && !ArchiveLocation.Is(path) && Directory.Exists(path) ? path : null;
    }

    private void AddToShelf(IReadOnlyList<FileSystemItem> items)
    {
        var paths = items.Where(item => !item.IsDriveRoot && !item.IsInArchive && Path.IsPathFullyQualified(item.FullPath)).Select(item => item.FullPath).ToList();
        if (paths.Count == 0)
        {
            _viewModel.ReportStatus("Select files or folders to put on the shelf.");
            return;
        }
        if (!_shelfShown) SetShelfShown(true);
        var before = _shelf!.Paths.Count;
        _shelf.Add(paths);
        var added = _shelf.Paths.Count - before;
        _viewModel.ReportStatus(added == 0 ? "That is on the shelf already." : $"Put {added:N0} {(added == 1 ? "item" : "items")} on the shelf.");
    }

    private static bool InFolder(string path, string folder) => string.Equals(
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase);

    private async void RunShelf(ShelfJob job, IReadOnlyList<string> paths)
    {
        if (ShelfFolder() is not { } here || paths.Count == 0 || _shelf is null) return;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(here));
        if (string.IsNullOrEmpty(name)) name = here;
        try
        {
            switch (job)
            {
                case ShelfJob.Move:
                {
                    var moving = paths.Where(path => !InFolder(path, here)).ToList();
                    if (moving.Count == 0) { _viewModel.ReportStatus("Everything on the shelf is in this folder already."); return; }
                    var result = FileOperationService.Move(moving, here, _viewModel.Context.OwnerHandle);
                    _viewModel.Context.ReportFileOperation(result);
                    // What really moved now has a new path; the shelf follows it there.
                    var moves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var path in moving)
                    {
                        var to = Path.Combine(here, Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
                        if (!File.Exists(path) && !Directory.Exists(path) && (File.Exists(to) || Directory.Exists(to))) moves[path] = to;
                    }
                    _shelf.Moved(moves);
                    if (moves.Count > 0) _viewModel.ReportStatus($"Moved {moves.Count:N0} {(moves.Count == 1 ? "item" : "items")} into {name}.");
                    break;
                }
                case ShelfJob.Copy:
                {
                    var result = FileOperationService.Copy(paths, here, _viewModel.Context.OwnerHandle, keepBoth: paths.Any(path => InFolder(path, here)));
                    _viewModel.Context.ReportFileOperation(result);
                    if (result.Succeeded) _viewModel.ReportStatus($"Copied {paths.Count:N0} {(paths.Count == 1 ? "item" : "items")} into {name}.");
                    break;
                }
                case ShelfJob.Zip:
                {
                    var zip = paths.Count == 1 ? ArchiveService.ZipFor(paths, here) : ArchiveService.UniquePath(Path.Combine(here, "Shelf.zip"));
                    var work = ProgressWindow.Run(this, $"Zipping the shelf to {Path.GetFileName(zip)}", ArchiveGlyph,
                        (progress, token) => ArchiveService.CompressAsync(paths, zip, progress, token));
                    if (work.Error is { } problem)
                    {
                        MessageDialog.Show(this, problem, "Couldn't make the ZIP file", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (work.Result is null) return;   // stopped
                    await _viewModel.RefreshAsync();
                    SelectInList(work.Result);
                    _viewModel.ReportStatus($"Made {Path.GetFileName(work.Result)}.");
                    _shelf.Refresh();
                    return;
                }
            }
            await _viewModel.RefreshAsync();
            _shelf.Refresh();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MessageDialog.Show(this, error.Message, "The shelf couldn't do that", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
