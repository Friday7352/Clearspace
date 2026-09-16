// Clearspace | Main window interaction handlers.

using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Clearspace.Commands;
using Clearspace.Models;
using Clearspace.Native;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private FileSystemItem? _renameTarget;
    private SidebarEntry? _sidebarDragEntry;
    private Button? _sidebarDragSource;
    private Point _sidebarDragStart;
    private Point _fileDragStart;
    private bool _isFileDragPending;
    private ListViewItem? _fileDropTarget;
    private string? _editingCategoryId;
    private bool _isPanningViewer;
    private Point _viewerPanOrigin;
    private double _viewerPanHorizontalOffset;
    private double _viewerPanVerticalOffset;
    private readonly Dictionary<GridViewColumn, string> _columnIds = [];
    private GridViewColumnHeader? _columnDragHeader;
    private Point _columnDragStart;
    private bool _isColumnDragging;
    private bool _suppressColumnSort;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Title = _viewModel.WindowTitle;

        _viewModel.Context.SelectAll = () => FileList.SelectAll();
        _viewModel.Context.ClearSelection = () => FileList.UnselectAll();
        _viewModel.Context.InvertSelection = InvertSelection;
        _viewModel.Context.FocusAddressBar = ShowAddressEditor;
        _viewModel.Context.BeginRename = BeginRename;

        Loaded += OnLoaded;
        Closed += (_, _) => _viewModel.Dispose();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnWindowMouseDown;
        PreviewMouseMove += OnSidebarMouseMove;
        PreviewMouseLeftButtonUp += OnSidebarMouseUp;

        _viewModel.ColumnsChanged += (_, _) => ApplyColumns();
        _viewModel.Viewer.ZoomChanged += OnViewerZoomChanged;
        _viewModel.Viewer.FileChanged += (_, _) => _ = _viewModel.RefreshAsync();
        SizeChanged += (_, _) => UpdateViewerSize();

        DependencyPropertyDescriptor
            .FromProperty(ListView.ViewProperty, typeof(ListView))
            .AddValueChanged(FileList, (_, _) => ApplyColumns());

        FileList.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnColumnResizeCompleted), true);
        FileList.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnColumnHeaderMouseDown), true);
        FileList.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnColumnHeaderMouseMove), true);
        FileList.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnColumnHeaderMouseUp), true);
    }


    private void ApplyColumns()
    {
        if (FileList.View is not GridView view)
            return;

        _columnIds.Clear();
        view.Columns.Clear();

        foreach (var id in _viewModel.VisibleColumns)
        {
            var info = ColumnCatalog.Find(id);
            if (info is null)
                continue;

            if (TryFindResource(info.ResourceKey) is GridViewColumn column)
            {
                var savedWidth = _viewModel.GetColumnWidth(id);
                if (savedWidth is not null)
                    column.Width = savedWidth.Value;

                _columnIds[column] = id;
                view.Columns.Add(column);
            }
        }
    }

    private void OnColumnResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        if (e.OriginalSource is not Thumb thumb ||
            FindAncestor<GridViewColumnHeader>(thumb) is not { Column: { } column } ||
            !_columnIds.TryGetValue(column, out var id))
            return;

        _viewModel.SaveColumnWidth(id, column.Width);
    }

    private void OnColumnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Thumb>(e.OriginalSource as DependencyObject) is not null ||
            FindAncestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject) is not { Column: { } column } header ||
            !_columnIds.ContainsKey(column))
            return;

        _columnDragHeader = header;
        _columnDragStart = e.GetPosition(FileList);
        _isColumnDragging = false;
    }

    private void OnColumnHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (_columnDragHeader is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var point = e.GetPosition(FileList);
        if (!_isColumnDragging &&
            Math.Abs(point.X - _columnDragStart.X) < SystemParameters.MinimumHorizontalDragDistance)
            return;

        _isColumnDragging = true;
        _columnDragHeader.Opacity = .55;
        Mouse.OverrideCursor = Cursors.SizeWE;
        e.Handled = true;
    }

    private void OnColumnHeaderMouseUp(object sender, MouseButtonEventArgs e)
    {
        var source = _columnDragHeader;
        var wasDragging = _isColumnDragging;
        ResetColumnHeaderDrag();

        if (!wasDragging || source?.Column is null || FileList.View is not GridView view)
            return;

        var target = FindAncestor<GridViewColumnHeader>(FileList.InputHitTest(e.GetPosition(FileList)) as DependencyObject);
        if (target?.Column is null || target == source)
            return;

        var sourceIndex = view.Columns.IndexOf(source.Column);
        var targetIndex = view.Columns.IndexOf(target.Column);
        if (sourceIndex < 0 || targetIndex < 0)
            return;

        var dropAfter = e.GetPosition(target).X > target.ActualWidth / 2;
        var insertIndex = targetIndex + (dropAfter ? 1 : 0);
        view.Columns.RemoveAt(sourceIndex);
        if (sourceIndex < insertIndex)
            insertIndex--;
        view.Columns.Insert(Math.Clamp(insertIndex, 0, view.Columns.Count), source.Column);

        _viewModel.SaveColumnOrder(view.Columns
            .Where(column => _columnIds.ContainsKey(column))
            .Select(column => _columnIds[column]));
        _suppressColumnSort = true;
        e.Handled = true;
    }

    private void ResetColumnHeaderDrag()
    {
        if (_columnDragHeader is not null)
            _columnDragHeader.Opacity = 1;

        _columnDragHeader = null;
        _isColumnDragging = false;
        Mouse.OverrideCursor = null;
    }

    private void OnColumnsButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
            return;

        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void OnResetColumns(object sender, RoutedEventArgs e) => _viewModel.ResetColumns();


    private void OnOpenElevated(object sender, RoutedEventArgs e) => _viewModel.OpenCurrentElevated();

    private async void OnKeepOnDevice(object sender, RoutedEventArgs e)
        => await _viewModel.SetCloudPinStateAsync(pinned: true);

    private async void OnFreeUpSpace(object sender, RoutedEventArgs e)
        => await _viewModel.SetCloudPinStateAsync(pinned: false);

    private void OnOpenIndexingOptions(object sender, RoutedEventArgs e) => _viewModel.OpenIndexingOptions();


    private void OnTagsSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu)
            return;

        if (!ReferenceEquals(e.OriginalSource, menu))
            return;

        menu.Items.Clear();

        foreach (var option in _viewModel.TagOptions)
        {
            var entry = new MenuItem
            {
                Header = option.Name,
                IsCheckable = true,
                IsChecked = option.IsApplied,
                StaysOpenOnClick = true,
                DataContext = option
            };

            entry.Click += OnTagChecked;
            menu.Items.Add(entry);
        }

        if (_viewModel.TagOptions.Count > 0)
            menu.Items.Add(new Separator());

        var create = new MenuItem { Header = "New tag…" };
        create.Click += OnCreateTag;
        menu.Items.Add(create);

        var clear = new MenuItem { Header = "Clear tags", IsEnabled = _viewModel.Context.HasSelection };
        clear.Click += OnClearTags;
        menu.Items.Add(clear);

        var delete = new MenuItem { Header = "Delete tag", IsEnabled = _viewModel.TagOptions.Count > 0 };

        foreach (var option in _viewModel.TagOptions)
        {
            var entry = new MenuItem { Header = option.Name, DataContext = option };
            entry.Click += OnDeleteTag;
            delete.Items.Add(entry);
        }

        menu.Items.Add(delete);
    }

    private void OnTagChecked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: TagOption option } item)
            option.IsApplied = item.IsChecked;
    }

    private void OnDeleteTag(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: TagOption option })
            return;

        var confirm = MessageBox.Show(
            $"Delete the {option.Name} tag?\n\nIt will be removed from everything currently tagged with it. Files themselves are not affected.",
            "Delete tag",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm == MessageBoxResult.OK)
            _viewModel.DeleteTag(option.Tag);
    }

    private void OnCreateTag(object sender, RoutedEventArgs e)
    {
        _editingCategoryId = null;
        CategoryPanelTitle.Text = "New tag";
        CategoryBox.Text = string.Empty;
        CategoryPanel.Visibility = Visibility.Visible;
        CategoryBox.Focus();
        _isNamingTag = true;
    }

    private void OnClearTags(object sender, RoutedEventArgs e) => _viewModel.ClearTagsOnSelection();

    private bool _isNamingTag;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _viewModel.Context.OwnerHandle = handle;
        _viewModel.Viewer.OwnerHandle = handle;

        ApplyDarkTitleBar(handle);
        HookColumnHeaders();
        ApplyColumns();

        _viewModel.Start(App.StartupPath);
        FileList.Focus();
    }

    private static void ApplyDarkTitleBar(IntPtr handle)
    {
        var enabled = 1;
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref enabled, sizeof(int));

        var round = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }


    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is TextBox)
            return;

        if (_viewModel.Viewer.IsOpen)
        {
            var viewer = _viewModel.Viewer;
            var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            switch (e.Key)
            {
                case Key.Escape:
                    if (viewer.IsCropping)
                    {
                        HideCropRectangle();
                        viewer.CancelCrop();
                    }
                    else
                    {
                        EndViewerPan();
                        viewer.Close();
                        FileList.Focus();
                    }
                    break;
                case Key.Left:
                    viewer.Previous();
                    break;
                case Key.Right:
                case Key.Space:
                    viewer.Next();
                    break;
                case Key.OemPlus or Key.Add:
                    viewer.ZoomBy(1.25);
                    break;
                case Key.OemMinus or Key.Subtract:
                    viewer.ZoomBy(1 / 1.25);
                    break;
                case Key.D0 or Key.NumPad0:
                    viewer.FitToWindow();
                    break;
                case Key.D1 or Key.NumPad1:
                    viewer.ActualSize();
                    break;
                case Key.Delete:
                    viewer.DeleteCurrent();
                    break;
                case Key.C when control:
                    viewer.CopyImage();
                    break;
                default:
                    return;
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space && _viewModel.Player.IsActive)
        {
            _viewModel.Player.TogglePlay();
            e.Handled = true;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var command = _viewModel.Commands.TryGetByHotKey(new HotKey(key, Keyboard.Modifiers));

        if (command is null || !command.CanExecute(null))
            return;

        command.Execute(null);
        e.Handled = true;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        _viewModel.SearchText = string.Empty;
        FileList.Focus();
        e.Handled = true;
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        _viewModel.SearchText = string.Empty;
        SearchBox.Focus();
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2))
            return;

        if (_viewModel.Viewer.IsOpen)
        {
            if (!_viewModel.Viewer.IsCropping)
            {
                if (e.ChangedButton == MouseButton.XButton1)
                    _viewModel.Viewer.Previous();
                else
                    _viewModel.Viewer.Next();
            }

            e.Handled = true;
            return;
        }

        var command = e.ChangedButton == MouseButton.XButton1
            ? _viewModel.BackCommand
            : _viewModel.ForwardCommand;

        if (command.CanExecute(null))
            command.Execute(null);

        e.Handled = true;
    }


    private void OnSidebarClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SidebarEntry entry })
            return;

        if (string.IsNullOrEmpty(entry.Path))
            return;

        if (Directory.Exists(entry.Path) || entry.Path.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase))
        {
            _viewModel.Navigation.Navigate(entry.Path);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = entry.Path, UseShellExecute = true });
        }
        catch (Exception) { }
    }

    private void OnToggleSidebarSection(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SidebarEntry entry } &&
            entry.IsSection && !string.IsNullOrWhiteSpace(entry.SectionId))
            _viewModel.ToggleSidebarSection(entry.SectionId);
    }

    private void OnChangeSidebarLocation(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: SidebarEntry entry })
            return;

        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Choose a folder for {entry.Name}",
            InitialDirectory = System.IO.Directory.Exists(entry.Path) ? entry.Path : string.Empty,
            Multiselect = false
        };

        if (picker.ShowDialog(this) != true)
            return;

        _viewModel.SetSidebarLocation(entry.Name, picker.FolderName);
    }

    private void OnResetSidebarLocation(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: SidebarEntry entry })
            _viewModel.ResetSidebarLocation(entry.Name);
    }

    private void OnUnpinSidebar(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: SidebarEntry entry } && entry.IsPinned)
            _viewModel.UnpinDirectory(entry.Path);
    }

    private void OnPinDirectory(object sender, RoutedEventArgs e)
    {
        var selected = FileList.SelectedItems.Cast<FileSystemItem>().FirstOrDefault();
        if (selected is not null)
            _viewModel.PinDirectory(selected.FullPath);
    }


    private void OnCreateCategory(object sender, RoutedEventArgs e) => BeginCategoryEdit(null, string.Empty);

    private void OnRenameCategory(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: SidebarEntry entry } && entry.IsSection)
            BeginCategoryEdit(entry.SectionId, entry.Name);
    }

    private void OnOpenSectionHub(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: SidebarEntry entry } && entry.HasHub)
            _viewModel.Navigation.Navigate(entry.Path);
    }

    private void OnDeleteCategory(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: SidebarEntry entry } &&
            entry.IsCategory && !string.IsNullOrWhiteSpace(entry.CategoryId))
            _viewModel.DeletePinnedCategory(entry.CategoryId);
    }

    private void BeginCategoryEdit(string? categoryId, string name)
    {
        _isNamingTag = false;
        _editingCategoryId = categoryId;
        CategoryPanelTitle.Text = categoryId is null ? "New category" : "Rename category";
        CategoryBox.Text = name;
        CategoryPanel.Visibility = Visibility.Visible;
        CategoryBox.Focus();
        CategoryBox.SelectAll();
    }

    private void OnCategoryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseCategoryPanel();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        var name = CategoryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SystemSounds_Beep();
            return;
        }

        if (_isNamingTag)
            _viewModel.CreateTagForSelection(name);
        else if (string.IsNullOrWhiteSpace(_editingCategoryId))
            _viewModel.CreatePinnedCategory(name);
        else
            _viewModel.RenameSidebarSection(_editingCategoryId, name);

        CloseCategoryPanel();
    }

    private void CloseCategoryPanel()
    {
        CategoryPanel.Visibility = Visibility.Collapsed;
        _editingCategoryId = null;
        _isNamingTag = false;
        FileList.Focus();
    }

    private void OnSidebarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { DataContext: SidebarEntry entry } && (entry.IsPinned || entry.IsSection))
        {
            _sidebarDragEntry = entry;
            _sidebarDragSource = (Button)sender;
            _sidebarDragStart = e.GetPosition(this);
        }
        else
        {
            _sidebarDragEntry = null;
            _sidebarDragSource = null;
        }
    }

    private void OnSidebarMouseMove(object sender, MouseEventArgs e)
    {
        if (_sidebarDragEntry is null || _sidebarDragSource is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _sidebarDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _sidebarDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var source = _sidebarDragEntry;
        _sidebarDragEntry = null;
        var sourceButton = _sidebarDragSource;
        _sidebarDragSource = null;
        DragDrop.DoDragDrop(sourceButton, new DataObject(typeof(SidebarEntry), source), DragDropEffects.Move);
    }

    private void OnSidebarMouseUp(object sender, MouseButtonEventArgs e)
    {
        _sidebarDragEntry = null;
        _sidebarDragSource = null;
    }

    private void OnSidebarDragEnter(object sender, DragEventArgs e)
    {
        if (sender is not Button button)
            return;

        if (IsFileDropOnSidebar(e, button, out _))
            ShowSidebarDropTarget(button, e);
        else if (TryGetSidebarDrop(e, button, out _))
            ShowSidebarDropTarget(button, e);
    }

    private void OnSidebarDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button button)
            ClearSidebarDropTarget(button);
    }

    private void OnSidebarDragOver(object sender, DragEventArgs e)
    {
        if (sender is Button button && (IsFileDropOnSidebar(e, button, out _) || TryGetSidebarDrop(e, button, out _)))
        {
            ShowSidebarDropTarget(button, e);
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnSidebarDrop(object sender, DragEventArgs e)
    {
        if (sender is not Button button)
            return;

        if (IsFileDropOnSidebar(e, button, out var targetFolder))
        {
            ClearSidebarDropTarget(button);

            var sourcePaths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            var owner = _viewModel.Context.OwnerHandle;
            var moveWithinSameDrive = sourcePaths.All(path =>
                string.Equals(Path.GetPathRoot(path), Path.GetPathRoot(targetFolder), StringComparison.OrdinalIgnoreCase));

            var copying = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 || !moveWithinSameDrive;
            var result = copying
                ? FileOperationService.Copy(sourcePaths, targetFolder!, owner)
                : FileOperationService.Move(sourcePaths, targetFolder!, owner);

            _viewModel.Context.ReportFileOperation(result);
            // A canceled/failed batch may still have moved some source items.
            _ = _viewModel.RefreshAsync();

            e.Effects = result.Succeeded
                ? (copying ? DragDropEffects.Copy : DragDropEffects.Move)
                : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (!TryGetSidebarDrop(e, button, out var source) || button.DataContext is not SidebarEntry target)
            return;

        ClearSidebarDropTarget(button);
        var placeAfter = e.GetPosition(button).Y >= button.ActualHeight / 2;

        if (source.IsPinned)
        {
            if (target.IsCategory && !string.IsNullOrWhiteSpace(target.CategoryId))
                _viewModel.MovePinnedDirectory(source.Path, target.CategoryId, null, placeAfter);
            else if (target.IsPinned)
                _viewModel.MovePinnedDirectory(source.Path, target.CategoryId, target.Path, placeAfter);
            else if (target.IsPinnedRoot)
                _viewModel.MovePinnedDirectory(source.Path, null, null, placeAfter);
            else
                return;

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (source.IsSection && target.IsSection &&
            !string.IsNullOrWhiteSpace(source.SectionId) && !string.IsNullOrWhiteSpace(target.SectionId))
        {
            _viewModel.MoveSidebarSection(source.SectionId, target.SectionId, placeAfter);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private static bool IsFileDropOnSidebar(DragEventArgs e, Button targetButton, out string? targetFolder)
    {
        targetFolder = null;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            targetButton.DataContext is not SidebarEntry { IsHeader: false, IsCategory: false, IsPinnedRoot: false, IsSection: false } entry ||
            string.IsNullOrWhiteSpace(entry.Path) ||
            entry.Path.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(entry.Path))
            return false;

        targetFolder = entry.Path;
        return true;
    }

    private static bool TryGetSidebarDrop(DragEventArgs e, Button targetButton, out SidebarEntry source)
    {
        source = null!;
        if (targetButton.DataContext is not SidebarEntry target ||
            !e.Data.GetDataPresent(typeof(SidebarEntry)) ||
            e.Data.GetData(typeof(SidebarEntry)) is not SidebarEntry dragged)
            return false;

        if (dragged.IsPinned)
        {
            source = dragged;
            return target.IsPinned || target.IsPinnedRoot || target.IsCategory;
        }

        if (dragged.IsSection)
        {
            source = dragged;
            return target.IsSection && !string.Equals(dragged.SectionId, target.SectionId, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static void ShowSidebarDropTarget(Button button, DragEventArgs e)
    {
        var placeAfter = e.GetPosition(button).Y >= button.ActualHeight / 2;
        button.Background = new SolidColorBrush(Color.FromArgb(30, 211, 161, 95));
        button.BorderBrush = new SolidColorBrush(Color.FromRgb(211, 161, 95));
        button.BorderThickness = placeAfter ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
    }

    private static void ClearSidebarDropTarget(Button button)
    {
        button.Background = Brushes.Transparent;
        button.BorderBrush = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
    }

    private void OnToggleLayout(object sender, RoutedEventArgs e) => _viewModel.ToggleLayout();

    private void OnFolderProfileButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
            return;

        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void OnFolderProfileSelected(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string value } &&
            Enum.TryParse<DirectoryViewProfile>(value, out var profile))
            _viewModel.SetFolderProfile(profile);
    }

    private void OnSelectedFolderProfile(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string value } &&
            Enum.TryParse<DirectoryViewProfile>(value, out var profile))
            _viewModel.SetFolderProfilesForSelection(profile);
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => _viewModel.AdjustTileScale(0.15);

    private void OnZoomOut(object sender, RoutedEventArgs e) => _viewModel.AdjustTileScale(-0.15);

    private void OnFileListMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_viewModel.IsGrid || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        _viewModel.AdjustTileScale(e.Delta > 0 ? 0.10 : -0.10);
        e.Handled = true;
    }


    private void OnFileListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isFileDragPending = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is not null;
        _fileDragStart = e.GetPosition(FileList);
    }

    private void OnFileListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isFileDragPending || e.LeftButton != MouseButtonState.Pressed)
            return;

        var point = e.GetPosition(FileList);
        if (Math.Abs(point.X - _fileDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _fileDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _isFileDragPending = false;

        var paths = _viewModel.Context.SelectedItems
            .Where(item => !item.IsDriveRoot)
            .Select(item => item.FullPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        if (paths.Length == 0)
            return;

        var data = new DataObject(DataFormats.FileDrop, paths);
        DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void OnFileListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ResolveFileDropEffects(e, out _, out var targetRow);
        HighlightFileDropTarget(targetRow, e.Effects != DragDropEffects.None);
        e.Handled = true;
    }

    private void OnFileListDragLeave(object sender, DragEventArgs e) => ClearFileDropHighlight();

    private void OnFileListDrop(object sender, DragEventArgs e)
    {
        ClearFileDropHighlight();

        var effects = ResolveFileDropEffects(e, out var targetFolder, out _);
        e.Handled = true;

        if (effects == DragDropEffects.None || targetFolder is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var sourcePaths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var owner = _viewModel.Context.OwnerHandle;

        var result = effects == DragDropEffects.Copy
            ? FileOperationService.Copy(sourcePaths, targetFolder, owner)
            : FileOperationService.Move(sourcePaths, targetFolder, owner);

        _viewModel.Context.ReportFileOperation(result);
        e.Effects = result.Succeeded ? effects : DragDropEffects.None;
        _ = _viewModel.RefreshAsync();
    }

    private DragDropEffects ResolveFileDropEffects(DragEventArgs e, out string? targetFolder, out ListViewItem? targetRow)
    {
        targetFolder = null;
        targetRow = null;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return DragDropEffects.None;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } sourcePaths)
            return DragDropEffects.None;

        targetRow = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        targetFolder = targetRow?.DataContext is FileSystemItem { IsFolder: true } folder
            ? folder.FullPath
            : (string.IsNullOrWhiteSpace(_viewModel.CurrentPath) ||
               _viewModel.CurrentPath.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase)
                ? null
                : _viewModel.CurrentPath);

        if (targetFolder is null)
            return DragDropEffects.None;

        var destination = targetFolder;

        if (sourcePaths.Any(path => IsSameOrAncestorOf(path, destination)))
            return DragDropEffects.None;

        if (sourcePaths.All(path =>
                string.Equals(Path.GetDirectoryName(path), destination, StringComparison.OrdinalIgnoreCase)))
            return DragDropEffects.None;

        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0)
            return DragDropEffects.Move;

        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0)
            return DragDropEffects.Copy;

        var sameDrive = sourcePaths.All(path =>
            string.Equals(Path.GetPathRoot(path), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase));

        return sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    private static bool IsSameOrAncestorOf(string candidateAncestor, string path)
    {
        var normalizedAncestor = Path.TrimEndingDirectorySeparator(candidateAncestor);
        var normalizedPath = Path.TrimEndingDirectorySeparator(path);

        if (string.Equals(normalizedAncestor, normalizedPath, StringComparison.OrdinalIgnoreCase))
            return true;

        return normalizedPath.StartsWith(
            normalizedAncestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void HighlightFileDropTarget(ListViewItem? row, bool isAcceptable)
    {
        if (row is null || row.DataContext is not FileSystemItem { IsFolder: true } || !isAcceptable)
        {
            ClearFileDropHighlight();
            return;
        }

        if (ReferenceEquals(_fileDropTarget, row))
            return;

        ClearFileDropHighlight();
        _fileDropTarget = row;
        row.Background = new SolidColorBrush(Color.FromArgb(45, 211, 161, 95));
        row.BorderBrush = new SolidColorBrush(Color.FromRgb(211, 161, 95));
        row.BorderThickness = new Thickness(1);
    }

    private void ClearFileDropHighlight()
    {
        if (_fileDropTarget is null)
            return;

        _fileDropTarget.ClearValue(BackgroundProperty);
        _fileDropTarget.ClearValue(BorderBrushProperty);
        _fileDropTarget.ClearValue(BorderThicknessProperty);
        _fileDropTarget = null;
    }

    private void OnTileDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is FileSystemItem item)
            ThumbnailService.Request(item, ThumbnailSize);
    }

    private void OnTileLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileSystemItem item })
        {
            ThumbnailService.Request(item, ThumbnailSize);
        }
    }

    // Decode enough source pixels for the largest tile size.
    private const int ThumbnailSize = 512;

    private void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && !string.IsNullOrEmpty(path))
            _viewModel.Navigation.Navigate(path);
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is null)
            return;

        _viewModel.OpenCommand.Execute(null);
    }

    private void OnPlayTrackClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileSystemItem item })
            return;

        if (ReferenceEquals(_viewModel.Player.Current, item))
            _viewModel.Player.TogglePlay();
        else
            _viewModel.PlayTrack(item);

        e.Handled = true;
    }

    private void OnViewPhotoClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileSystemItem item })
            _viewModel.ViewPhoto(item);

        e.Handled = true;
    }


    private void OnMusicRowDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel.IsMusicProfile && e.NewValue is FileSystemItem item)
            MediaPropertyService.Request(item);
    }

    private void OnMusicRowLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsMusicProfile && sender is FrameworkElement { DataContext: FileSystemItem item })
            MediaPropertyService.Request(item);
    }

    private void OnPlayerTogglePlay(object sender, RoutedEventArgs e) => _viewModel.Player.TogglePlay();

    private void OnPlayerNext(object sender, RoutedEventArgs e) => _viewModel.Player.Next();

    private void OnPlayerPrevious(object sender, RoutedEventArgs e) => _viewModel.Player.Previous();

    private void OnPlayerStop(object sender, RoutedEventArgs e) => _viewModel.Player.Stop();

    private void OnSeekStart(object sender, MouseButtonEventArgs e) => _viewModel.Player.BeginScrub();

    private void OnSeekEnd(object sender, MouseButtonEventArgs e) => _viewModel.Player.EndScrub();


    private Point _cropOrigin;
    private bool _isDraggingCrop;

    private void UpdateViewerSize()
    {
        var viewer = _viewModel.Viewer;
        var image = viewer.Image;

        if (image is null || !viewer.IsOpen)
            return;

        double scale;

        if (viewer.IsFitToWindow)
        {
            var availableWidth = Math.Max(1, ViewerScroll.ViewportWidth - 40);
            var availableHeight = Math.Max(1, ViewerScroll.ViewportHeight - 40);

            scale = Math.Min(availableWidth / image.PixelWidth, availableHeight / image.PixelHeight);

            scale = Math.Min(scale, 1);
            viewer.SeedZoom(scale);
        }
        else
        {
            scale = viewer.Zoom;
        }

        ViewerImage.Width = Math.Max(1, image.PixelWidth * scale);
        ViewerImage.Height = Math.Max(1, image.PixelHeight * scale);
    }

    private void OnViewerZoomChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(UpdateViewerSize), System.Windows.Threading.DispatcherPriority.Loaded);

    private void OnViewerMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var viewer = _viewModel.Viewer;

        if (!viewer.IsOpen || viewer.IsCropping)
            return;

        e.Handled = true;

        if (viewer.Image is null)
            return;

        var onImage = e.GetPosition(ViewerImage);
        var fractionX = ViewerImage.ActualWidth > 0
            ? Math.Clamp(onImage.X / ViewerImage.ActualWidth, 0, 1)
            : 0.5;
        var fractionY = ViewerImage.ActualHeight > 0
            ? Math.Clamp(onImage.Y / ViewerImage.ActualHeight, 0, 1)
            : 0.5;

        var inViewport = e.GetPosition(ViewerScroll);

        viewer.ZoomBy(e.Delta > 0 ? 1.15 : 1 / 1.15);

        UpdateViewerSize();
        ViewerScroll.UpdateLayout();

        var originX = ViewerStage.Margin.Left;
        var originY = ViewerStage.Margin.Top;

        ViewerScroll.ScrollToHorizontalOffset(fractionX * ViewerImage.ActualWidth + originX - inViewport.X);
        ViewerScroll.ScrollToVerticalOffset(fractionY * ViewerImage.ActualHeight + originY - inViewport.Y);
    }

    private void OnViewerSurfaceDown(object sender, MouseButtonEventArgs e)
    {
        ViewerScroll.Focus();

        if (_viewModel.Viewer.IsCropping)
            return;

        var point = e.GetPosition(ViewerImage);

        var onImage = point.X >= 0 &&
                      point.Y >= 0 &&
                      point.X <= ViewerImage.ActualWidth &&
                      point.Y <= ViewerImage.ActualHeight;

        if (onImage)
            return;

        EndViewerPan();
        _viewModel.Viewer.Close();
        FileList.Focus();
        e.Handled = true;
    }

    private void OnViewerSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle ||
            !_viewModel.Viewer.IsOpen ||
            _viewModel.Viewer.IsCropping)
            return;

        _isPanningViewer = true;
        _viewerPanOrigin = e.GetPosition(ViewerScroll);
        _viewerPanHorizontalOffset = ViewerScroll.HorizontalOffset;
        _viewerPanVerticalOffset = ViewerScroll.VerticalOffset;
        ViewerScroll.CaptureMouse();
        ViewerScroll.Cursor = Cursors.ScrollAll;
        e.Handled = true;
    }

    private void OnViewerSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanningViewer || e.MiddleButton != MouseButtonState.Pressed)
            return;

        var point = e.GetPosition(ViewerScroll);
        ViewerScroll.ScrollToHorizontalOffset(_viewerPanHorizontalOffset - (point.X - _viewerPanOrigin.X));
        ViewerScroll.ScrollToVerticalOffset(_viewerPanVerticalOffset - (point.Y - _viewerPanOrigin.Y));
        e.Handled = true;
    }

    private void OnViewerSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !_isPanningViewer)
            return;

        EndViewerPan();
        e.Handled = true;
    }

    private void OnViewerSurfaceLostMouseCapture(object sender, MouseEventArgs e) => EndViewerPan();

    private void EndViewerPan()
    {
        if (!_isPanningViewer)
            return;

        _isPanningViewer = false;
        if (Mouse.Captured == ViewerScroll)
            ViewerScroll.ReleaseMouseCapture();
        ViewerScroll.Cursor = null;
    }

    private void OnViewerZoomIn(object sender, RoutedEventArgs e) => _viewModel.Viewer.ZoomBy(1.25);

    private void OnViewerZoomOut(object sender, RoutedEventArgs e) => _viewModel.Viewer.ZoomBy(1 / 1.25);

    private void OnViewerFit(object sender, RoutedEventArgs e) => _viewModel.Viewer.FitToWindow();

    private void OnViewerActualSize(object sender, RoutedEventArgs e) => _viewModel.Viewer.ActualSize();

    private void OnViewerRotateLeft(object sender, RoutedEventArgs e) => _viewModel.Viewer.Rotate(270);

    private void OnViewerRotateRight(object sender, RoutedEventArgs e) => _viewModel.Viewer.Rotate(90);

    private void OnViewerCopyImage(object sender, RoutedEventArgs e) => _viewModel.Viewer.CopyImage();

    private void OnViewerCopyPath(object sender, RoutedEventArgs e) => _viewModel.Viewer.CopyPath();

    private void OnViewerOpenWith(object sender, RoutedEventArgs e) => _viewModel.Viewer.OpenWith();

    private void OnViewerShowInFolder(object sender, RoutedEventArgs e) => _viewModel.Viewer.ShowInFolder();

    private void OnViewerDelete(object sender, RoutedEventArgs e) => _viewModel.Viewer.DeleteCurrent();


    private void OnViewerCrop(object sender, RoutedEventArgs e)
    {
        HideCropRectangle();
        _viewModel.Viewer.BeginCrop();
    }

    private void OnCropDown(object sender, MouseButtonEventArgs e)
    {
        _cropOrigin = e.GetPosition(CropLayer);
        _isDraggingCrop = true;
        CropLayer.CaptureMouse();

        Canvas.SetLeft(CropRectangle, _cropOrigin.X);
        Canvas.SetTop(CropRectangle, _cropOrigin.Y);
        CropRectangle.Width = 0;
        CropRectangle.Height = 0;
        CropRectangle.Visibility = Visibility.Visible;
    }

    private void OnCropMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingCrop)
            return;

        var point = e.GetPosition(CropLayer);
        var left = Math.Max(0, Math.Min(_cropOrigin.X, point.X));
        var top = Math.Max(0, Math.Min(_cropOrigin.Y, point.Y));
        var right = Math.Min(CropLayer.ActualWidth, Math.Max(_cropOrigin.X, point.X));
        var bottom = Math.Min(CropLayer.ActualHeight, Math.Max(_cropOrigin.Y, point.Y));

        Canvas.SetLeft(CropRectangle, left);
        Canvas.SetTop(CropRectangle, top);
        CropRectangle.Width = Math.Max(0, right - left);
        CropRectangle.Height = Math.Max(0, bottom - top);
    }

    private void OnCropUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingCrop)
            return;

        _isDraggingCrop = false;
        CropLayer.ReleaseMouseCapture();
        CommitCropSelection();
    }

    private void CommitCropSelection()
    {
        var image = _viewModel.Viewer.Image;

        if (image is null || CropLayer.ActualWidth < 1 || CropLayer.ActualHeight < 1)
            return;

        var scaleX = image.PixelWidth / CropLayer.ActualWidth;
        var scaleY = image.PixelHeight / CropLayer.ActualHeight;

        var left = Canvas.GetLeft(CropRectangle);
        var top = Canvas.GetTop(CropRectangle);

        _viewModel.Viewer.CropRegion = new Int32Rect(
            (int)Math.Round(left * scaleX),
            (int)Math.Round(top * scaleY),
            (int)Math.Round(CropRectangle.Width * scaleX),
            (int)Math.Round(CropRectangle.Height * scaleY));
    }

    private void HideCropRectangle()
    {
        CropRectangle.Visibility = Visibility.Collapsed;
        CropRectangle.Width = 0;
        CropRectangle.Height = 0;
    }

    private void OnCropCancel(object sender, RoutedEventArgs e)
    {
        HideCropRectangle();
        _viewModel.Viewer.CancelCrop();
    }

    private void OnCropSaveCopy(object sender, RoutedEventArgs e)
    {
        _viewModel.Viewer.CommitCrop(asCopy: true);
        HideCropRectangle();
    }

    private void OnCropOverwrite(object sender, RoutedEventArgs e)
    {
        _viewModel.Viewer.CommitCrop(asCopy: false);
        HideCropRectangle();
    }

    private void OnViewerClose(object sender, RoutedEventArgs e)
    {
        EndViewerPan();
        HideCropRectangle();
        _viewModel.Viewer.Close();
        FileList.Focus();
    }

    private void OnViewerNext(object sender, RoutedEventArgs e) => _viewModel.Viewer.Next();

    private void OnViewerPrevious(object sender, RoutedEventArgs e) => _viewModel.Viewer.Previous();

    private void OnFileListRightClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is not { DataContext: FileSystemItem item })
            return;

        if (!FileList.SelectedItems.Contains(item))
            FileList.SelectedItem = item;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewModel.Context.SelectedItems = FileList.SelectedItems
            .Cast<FileSystemItem>()
            .ToList();
    }

    private void InvertSelection()
    {
        var selected = FileList.SelectedItems.Cast<FileSystemItem>().ToHashSet();

        FileList.SelectedItems.Clear();
        foreach (var item in _viewModel.Items)
        {
            if (!selected.Contains(item))
                FileList.SelectedItems.Add(item);
        }
    }


    private void OnAddressActivate(object sender, MouseButtonEventArgs e) => ShowAddressEditor();

    private void ShowAddressEditor()
    {
        BreadcrumbBar.Visibility = Visibility.Collapsed;
        AddressActivator.Visibility = Visibility.Collapsed;
        AddressBox.Visibility = Visibility.Visible;
        AddressBox.Text = _viewModel.CurrentPath;
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void HideAddressEditor()
    {
        AddressBox.Visibility = Visibility.Collapsed;
        BreadcrumbBar.Visibility = Visibility.Visible;
        AddressActivator.Visibility = Visibility.Visible;
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var target = AddressBox.Text.Trim().Trim('"');

            if (Directory.Exists(target))
            {
                _viewModel.Navigation.Navigate(target);
                HideAddressEditor();
                FileList.Focus();
            }
            else if (File.Exists(target))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = target,
                        UseShellExecute = true
                    });
                }
                catch (Exception)
                {
                }
            }
            else
            {
                SystemSounds_Beep();
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideAddressEditor();
            FileList.Focus();
            e.Handled = true;
        }
    }

    private void OnAddressLostFocus(object sender, RoutedEventArgs e) => HideAddressEditor();

    private static void SystemSounds_Beep() => System.Media.SystemSounds.Beep.Play();


    private void BeginRename(FileSystemItem? item)
    {
        if (item is null)
            return;

        _renameTarget = item;
        RenameBox.Text = item.Name;
        RenamePanel.Visibility = Visibility.Visible;
        RenameBox.Focus();

        var stemLength = item.IsFolder
            ? item.Name.Length
            : item.Name.Length - Path.GetExtension(item.Name).Length;

        RenameBox.Select(0, Math.Max(0, stemLength));
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelRename();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
            return;

        e.Handled = true;

        var item = _renameTarget;
        var newName = RenameBox.Text.Trim();

        if (item is null || string.IsNullOrEmpty(newName) || newName == item.Name)
        {
            CancelRename();
            return;
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            SystemSounds_Beep();
            return;
        }

        var directory = Path.GetDirectoryName(item.FullPath);
        if (directory is null)
        {
            CancelRename();
            return;
        }

        var destination = Path.Combine(directory, newName);
        var result = FileOperationService.Rename(item.FullPath, destination, _viewModel.Context.OwnerHandle);
        CancelRename();
        _viewModel.Context.ReportFileOperation(result);

        if (result.Succeeded)
            _viewModel.ApplyRename(item, destination);
        else
            _ = _viewModel.RefreshAsync();
    }

    private void CancelRename()
    {
        RenamePanel.Visibility = Visibility.Collapsed;
        _renameTarget = null;
        FileList.Focus();
    }


    private void HookColumnHeaders()
        => FileList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnColumnHeaderClick));

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (_suppressColumnSort)
        {
            _suppressColumnSort = false;
            e.Handled = true;
            return;
        }

        if (e.OriginalSource is not GridViewColumnHeader { Content: string header })
            return;

        var column = header switch
        {
            "Date modified" => SortColumn.DateModified,
            "Date created" => SortColumn.DateCreated,
            "Type" => SortColumn.Type,
            "Size" => SortColumn.Size,
            "Title" => SortColumn.Title,
            "Artist" => SortColumn.Artist,
            "Album" => SortColumn.Album,
            "Length" => SortColumn.Duration,
            "Track" => SortColumn.Track,
            _ => SortColumn.Name
        };

        _viewModel.Sort(column);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null and not T)
            source = VisualTreeHelper.GetParent(source);

        return source as T;
    }
}
