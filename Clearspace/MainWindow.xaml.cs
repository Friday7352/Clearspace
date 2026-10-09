// Clearspace | Main window interaction handlers.

using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
    private readonly MainViewModel _viewModel; // CHANGED (tab to new window): made in the constructor (was "= new()")
    private DiskUsageView? _diskUsageView;
    private IndexingView? _indexingView;

    // MOVED (locked folders): the lock / unlock handlers now live in MainWindow.FileLocks.cs.

    private void OnIndexing(object? sender, RoutedEventArgs e)
    {
        if (_indexingView is not null) return;
        _indexingView = new IndexingView();
        _indexingView.CloseRequested += (_, _) => CloseIndexing();
        _indexingView.WindowsIndexingRequested += (_, _) => _viewModel.OpenIndexingOptions();
        IndexingHost.Content = _indexingView;
        ExplorerShell.Visibility = DiskUsageHost.Visibility = Visibility.Collapsed;
        IndexingHost.Visibility = Visibility.Visible;
        FlashPage(); // NEW (e-reader)
        _indexingView.Focus();
    }

    private void CloseIndexing()
    {
        _indexingView?.Dispose();
        _indexingView = null;
        IndexingHost.Content = null;
        IndexingHost.Visibility = Visibility.Collapsed;
        FlashPage(); // NEW (e-reader)
        if (_diskUsageView is not null) DiskUsageHost.Visibility = Visibility.Visible;
        else ExplorerShell.Visibility = Visibility.Visible;
    }

    private void OnDiskUsage(object sender, RoutedEventArgs e)
    {
        if (_diskUsageView is not null) return;
        // CHANGED: the toolbar button always opens the folder you're in. Only the file-list
        // context menu ("Disk usage…" on a folder) targets the selected folder.
        var selected = _viewModel.Context.SelectedItems;
        var path = sender is MenuItem && selected.Count == 1 && selected[0].IsFolder
            ? selected[0].FullPath
            : _viewModel.CurrentPath;
        // Virtual locations (Home, search results, etc.) have no folder to open.
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase))
            path = null;
        _diskUsageView = new DiskUsageView(path);
        _diskUsageView.IndexingRequested += (_, _) => OnIndexing(this, new RoutedEventArgs());
        _diskUsageView.FileOperationCompleted += (_, _) => _ = _viewModel.RefreshAsync();
        _diskUsageView.CloseRequested += (_, _) =>
        {
            _diskUsageView?.Dispose();
            DiskUsageHost.Content = null;
            DiskUsageHost.Visibility = Visibility.Collapsed;
            ExplorerShell.Visibility = Visibility.Visible;
            _diskUsageView = null;
            FileList.Focus();
            FlashPage(); // NEW (e-reader)
        };
        DiskUsageHost.Content = _diskUsageView;
        ExplorerShell.Visibility = Visibility.Collapsed;
        DiskUsageHost.Visibility = Visibility.Visible;
        FlashPage(); // NEW (e-reader)
    }
    private FileSystemItem? _renameTarget;
    private SidebarEntry? _sidebarDragEntry;
    private Button? _sidebarDragSource;
    private Point _sidebarDragStart;
    private Point _fileDragStart;
    private bool _isFileDragPending;
    // FIXED (drag several): the selected file the button went down on while several were selected. The
    // selection is left alone until the button comes up; see OnFileListPreviewMouseLeftButtonDown.
    private FileSystemItem? _pressedInSelection;
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

    // NEW (new window): the folder this window opens on. Null for the first window, which opens on the
    // folder Clearspace was started with (App.StartupPath) or your user folder.
    private readonly string? _startPath;

    // NEW (tab to new window): the tab this window was made for, when a tab was dragged out of another
    // window (or sent here with "Move to new window"). Its history becomes this window's first tab.
    private readonly ExplorerTab? _startTab;

    // CHANGED (new window): takes the folder to open on (see MainWindow.Windows.cs).
    // CHANGED (tab to new window): or the tab to open with.
    public MainWindow(string? startPath = null, ExplorerTab? startTab = null)
    {
        _startPath = startPath;
        _startTab = startTab;
        _viewModel = new MainViewModel(startTab?.History);
        InitializeComponent();
        DataContext = _viewModel;

        Title = _viewModel.WindowTitle;

        _viewModel.Context.SelectAll = () => FileList.SelectAll();
        _viewModel.Context.ClearSelection = () => FileList.UnselectAll();
        _viewModel.Context.InvertSelection = InvertSelection;
        _viewModel.Context.FocusAddressBar = ShowAddressEditor;
        _viewModel.Context.BeginRename = BeginRename;
        InitializeFileLocks(); // CHANGED (locked folders): wires OpenLockedFile, the folder password gate and re-locking
        InitializeArchives(); // NEW (zip folders): taking copies out of a ZIP file for Open, Copy and dragging

        Loaded += OnLoaded;
        // CHANGED (themes): also stops listening for theme switches when the window closes.
        ThemeService.Changed += OnThemeChanged;
        ThemeService.ReloadFailed += OnThemeReloadFailed; // NEW (theme files): a theme being made was saved with a mistake in it
        // NEW (e-reader): refresh the "page" when the folder changes or the photo viewer opens or closes.
        _viewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.CurrentPath)) TurnPage(); };   // CHANGED (e-ink): a page turn
        _viewModel.Viewer.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PhotoViewerViewModel.IsOpen)) FlashPage(); };
        InitializeUpdates(); // NEW (updates): the quiet update check and the status bar's "Update available" chip
        InitializeBackgroundMenu(); // NEW (empty-area menu): remembers the files-and-folders menu, reads the "New" templates
        InitializeTabs(); // NEW (tabs): the tab strip's mouse and keyboard handling, and each tab's scroll position and selection
        InitializeWindows(); // NEW (new window): "Open in new window" and Ctrl+N
        InitializeBatchRename(); // NEW (batch rename): Rename with several items selected
        InitializePreview(); // NEW (preview pane): Alt+P, the pane beside the file list
        InitializeDuplicates(); // NEW (duplicates): the Find duplicates page, and selecting a file it sends you to
        InitializeNetworkDrives(); // NEW (map network drive): "Map network drive…" and "Disconnect"
        InitializeBackup(); // NEW (backup): Settings > "Back up settings and tags…", and automatic backups
        InitializeFileBackup(); // NEW (file backup): Settings > "Backup and restore…", and the check for scheduled file backups
        InitializeDriveWatch(); // NEW (live drives): a drive plugged in or taken out shows at once, in the sidebar and on This PC
        InitializeDownloads(); // NEW (downloads): the "Just arrived" strip of Downloads folders
        InitializeDragScroll(); // NEW (scroll while dragging): the edges of a list and the mouse wheel, during a drag
        InitializeBandSelect(); // NEW (drag to select): a rectangle dragged over the file list selects what it touches
        SearchTrace.WatchWindow(Dispatcher); // NEW (search trace): records when the window stops responding, and during what
        _viewModel.Context.NewLibraryFolder = CreateLibraryFolder; // NEW (your files): New folder on the Your files page
        // NEW (experimental themes): keeps the Zen theme's fading toolbar in step with the pointer and keyboard.
        ToolbarRow.MouseEnter += (_, _) => UpdateToolbarFade();
        ToolbarRow.MouseLeave += (_, _) => UpdateToolbarFade();
        ToolbarRow.IsKeyboardFocusWithinChanged += (_, _) => UpdateToolbarFade();
        // CHANGED (duplicates): also stops a duplicate search that is still running.
        Closed += (_, _) => { ThemeService.Changed -= OnThemeChanged; ThemeService.ReloadFailed -= OnThemeReloadFailed; _indexingView?.Dispose(); _diskUsageView?.Dispose(); _duplicatesView?.Dispose(); _themesView?.Dispose(); _viewModel.Dispose(); };   // CHANGED (themes page): + the Themes page
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnWindowMouseDown;
        PreviewMouseMove += OnSidebarMouseMove;
        PreviewMouseLeftButtonUp += OnSidebarMouseUp;

        _viewModel.ColumnsChanged += (_, _) => ApplyColumns();
        _viewModel.Viewer.ZoomChanged += OnViewerZoomChanged;
        _viewModel.Viewer.FileChanged += (_, _) => _ = _viewModel.RefreshAsync();
        SizeChanged += (_, _) => UpdateViewerSize();
        // NEW (e-ink): the picture of the previous page no longer fits a resized window, so drop its ghost.
        // CHANGED (new window): this window's own effect (was the one effect every window would have shared).
        SizeChanged += (_, _) => { if (_screen is { } screen) { screen.BeginAnimation(EInkEffect.GhostAmountProperty, null); screen.GhostAmount = 0; } };

        DependencyPropertyDescriptor
            .FromProperty(ListView.ViewProperty, typeof(ListView))
            .AddValueChanged(FileList, (_, _) => ApplyColumns());

        // NEW (folder types, step 2): date groups (Screenshots) follow the list and the view model.
        // CHANGED (search fix): also notes the moment, so the time until the list has been laid out again
        // can be written to the search trace (SearchTrace.ListDrawn). Giving the list new contents makes
        // it build every row on screen again; that time is what typing waits for.
        DependencyPropertyDescriptor
            .FromProperty(ItemsControl.ItemsSourceProperty, typeof(ListView))
            .AddValueChanged(FileList, (_, _) =>
            {
                EndBand(keep: true); // NEW (drag to select): a rectangle being dragged belongs to the old list
                _listReplacedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                ApplyGrouping();
            });
        FileList.LayoutUpdated += (_, _) =>
        {
            if (_listReplacedAt == 0)
                return;

            var took = System.Diagnostics.Stopwatch.GetElapsedTime(_listReplacedAt).TotalMilliseconds;
            _listReplacedAt = 0;
            SearchTrace.ListDrawn(took, FileList.Items.Count,
                FileList.View is null ? "tiles" : FileList.Items.GroupDescriptions.Count > 0 ? "details in groups" : "details");
        };
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ListGrouping))
                ApplyGrouping();
        };

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

        var confirm = MessageDialog.Show( // CHANGED (themes): themed dialog instead of the Windows message box
            this,
            $"Delete the {option.Name} tag?\n\nIt will be removed from everything currently tagged with it. Files themselves are not affected.",
            "Delete tag",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm == MessageBoxResult.OK)
            _viewModel.DeleteTag(option.Tag);
    }

    private void OnCreateTag(object sender, RoutedEventArgs e)
    {
        _isNamingFolderType = false; // NEW
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
        ApplyThemeLayout(); // NEW (custom layouts): before the usual layout, which steps aside for a custom one
        ThemeExplorerLayout.Apply(this);
        var handle = new WindowInteropHelper(this).Handle;
        _viewModel.Context.OwnerHandle = handle;
        _viewModel.Viewer.OwnerHandle = handle;

        ThemeService.ApplyTitleBar(this); // CHANGED (themes): was ApplyDarkTitleBar; the title bar (and text edges) follow the theme
        UpdateToolbarFade(); // NEW (experimental themes)
        ApplyScreenEffect(); // NEW (e-ink)
        HookColumnHeaders();
        ApplyColumns();

        _viewModel.Start(_startPath ?? App.StartupPath); // CHANGED (new window): a new window opens on the folder it was asked for
        if (_startTab is { } moved) ResumeMovedTab(moved); // NEW (tab to new window): its search, scroll position and selection
        FileList.Focus();
    }

    // REMOVED (themes): ApplyDarkTitleBar. ThemeService.ApplyTitleBar now sets the dark or light title bar,
    // the corner style and (Windows 11) the caption colours for whichever theme is active.

    // NEW (themes): the Settings menu ticks the theme in use each time it opens.
    // CHANGED (theme files): it also listed the themes each time, from what was in the Themes folder.
    // CHANGED (themes page): it no longer lists them; it names the theme in use beside "Choose a theme…".
    private void OnSettingsMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu _)
            return;

        ShowThemeInMenu(); // CHANGED (themes page): was ShowThemesInMenu(menu), which listed every theme in the menu
        TickFolderOpener(); // NEW (folder opener): ticked while Windows opens folders with Clearspace
    }

    // REMOVED (theme files): TickCurrentTheme.
    // REMOVED (themes page): OnThemePicked. Themes are picked on the Themes page now (ThemesView.cs), not
    // in the Settings menu.

    // NEW (themes): colours in XAML repaint by themselves. The status colours in the file list (cloud, Git,
    // drive space) are handed out by each file's row data, so the visible rows are rebuilt to ask again.
    // CHANGED (experimental themes): when the switch changes the icon set, every row needs a new icon, so
    // the remembered folder listings and tile pictures are dropped and the current folder is read again.
    private void OnThemeChanged()
    {
        ApplyThemeLayout(); // NEW (custom layouts)
        ThemeExplorerLayout.Apply(this);
        UpdateToolbarFade();
        ApplyScreenEffect(); // NEW (e-ink)

        if (ThemeService.IconsChanged)
        {
            ThumbnailService.Clear();
            FolderSnapshotCache.Clear();
            _ = _viewModel.RefreshAsync();
            return;
        }

        FileList.Items.Refresh();
    }

    // CHANGED (e-ink): the dimming sheet this used to animate is gone, and so is the full refresh that replaced
    // it. Changing the whole view (photo viewer, Disk usage, Indexing) is a page turn like any other; see
    // MainWindow.EInk.cs.
    private void FlashPage() => TurnPage();

    // NEW (experimental themes): in a theme that asks for it (Zen), the toolbar fades back while the pointer
    // is elsewhere and nothing in it has the keyboard, and returns as soon as either comes back.
    private void UpdateToolbarFade()
    {
        if (!ThemeService.FadeToolbar && ToolbarRow.Opacity >= 1)
            return; // nothing to do in the themes that never fade it

        var rest = ThemeService.FadeToolbar && !ToolbarRow.IsMouseOver && !ToolbarRow.IsKeyboardFocusWithin;
        var animation = new System.Windows.Media.Animation.DoubleAnimation(rest ? 0.2 : 1, TimeSpan.FromMilliseconds(rest ? 450 : 120));
        ToolbarRow.BeginAnimation(OpacityProperty, animation);
    }

    // NEW (custom layouts): the extra file panes of the theme's layout, made fresh each time the layout is applied.
    private readonly List<Clearspace.Controls.FilePane> _panes = new();

    // Arranges the window as the theme's layout.json says, or puts it back to the usual layout when it has none.
    private void ApplyThemeLayout()
    {
        foreach (var pane in _panes) pane.Dispose();
        _panes.Clear();
        ThemeLayoutEngine.Apply(this, ThemeService.Layout, node =>
        {
            var pane = new Clearspace.Controls.FilePane(node.Folder, node.Title, () => _viewModel.CurrentPath, path => _viewModel.Navigation.Navigate(path));
            _panes.Add(pane);
            return pane;
        }, ThemeService.LayoutImage);
    }

    // NEW (toolbar groups): the words beside the toolbar's labelled buttons give way when the window gets narrow,
    // so the address bar keeps its room. Tool names (Disk usage, Duplicates) go first; the search scope words
    // (Everywhere, File contents) last, since those say which way a search is looking. Icons and tooltips stay.
    private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var tools = width >= 1180 ? Visibility.Visible : Visibility.Collapsed;
        var scope = width >= 940 ? Visibility.Visible : Visibility.Collapsed;
        DiskUsageLabel.Visibility = DuplicatesLabel.Visibility = tools;
        SearchEverywhereLabel.Visibility = SearchContentsLabel.Visibility = scope;
    }


    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // NEW (drag to select): Esc while a selection rectangle is out puts the selection back as it was.
        // Every other key is swallowed until the button is let go: the arrow keys, Space or Ctrl+A would
        // change the selection under the rectangle while it is keeping its own count of it.
        if (_bandActive)
        {
            if (e.Key == Key.Escape) EndBand(keep: false);
            e.Handled = true;
            return;
        }

        if (_indexingView is not null)
        {
            if (e.Key == Key.Escape) { CloseIndexing(); e.Handled = true; }
            return;
        }
        // CHANGED: while the disk view is open, F3 toggles its performance readout even if
        // keyboard focus is still somewhere outside it.
        if (_diskUsageView is not null)
        {
            if (e.Key == Key.F3) { _diskUsageView.ToggleMapStats(); e.Handled = true; }
            return;
        }
        // NEW (duplicates): Esc goes back to the files; the explorer's own shortcuts are off meanwhile.
        if (_duplicatesView is not null)
        {
            if (e.Key == Key.Escape) { CloseDuplicates(); e.Handled = true; }
            return;
        }
        // NEW (file backup): the same for the Backup and restore page.
        if (_fileBackupView is not null)
        {
            if (e.Key == Key.Escape) { CloseFileBackup(); e.Handled = true; }
            return;
        }
        // NEW (themes page): and for the Themes page.
        if (_themesView is not null)
        {
            if (e.Key == Key.Escape) { CloseThemes(); e.Handled = true; }
            return;
        }
        // NEW (tabs): Ctrl+T, Ctrl+W, Ctrl+Tab ... work wherever the keyboard is, including the search and
        // address boxes (which is why this comes before the text box check below). Not while the photo
        // viewer covers the window. See MainWindow.Tabs.cs.
        if (!_viewModel.Viewer.IsOpen && TryHandleTabKey(e))
            return;

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
        if (_indexingView is not null)
        {
            if (e.ChangedButton == MouseButton.XButton1) { CloseIndexing(); e.Handled = true; }
            return;
        }
        if (_diskUsageView is not null) return;
        // NEW (duplicates): the mouse's back button leaves the page, like Esc.
        if (_duplicatesView is not null)
        {
            if (e.ChangedButton == MouseButton.XButton1) { CloseDuplicates(); e.Handled = true; }
            return;
        }
        // NEW (file backup)
        if (_fileBackupView is not null)
        {
            if (e.ChangedButton == MouseButton.XButton1) { CloseFileBackup(); e.Handled = true; }
            return;
        }
        // NEW (themes page)
        if (_themesView is not null)
        {
            if (e.ChangedButton == MouseButton.XButton1) { CloseThemes(); e.Handled = true; }
            return;
        }
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
        _isNamingFolderType = false; // NEW
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

        if (_isNamingFolderType)
            _viewModel.SaveViewAsFolderType(name); // NEW (folder types)
        else if (_isNamingTag)
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
        _isNamingFolderType = false; // NEW
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
        // CHANGED (scroll while dragging): the wheel is listened for while the drag lasts.
        BeginDragScroll();
        try { DragDrop.DoDragDrop(sourceButton, new DataObject(typeof(SidebarEntry), source), DragDropEffects.Move); }
        finally { EndDragScroll(); }
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
        // CHANGED (themes): the theme's accent instead of a fixed amber.
        button.Background = ThemeService.AccentWash;
        button.BorderBrush = ThemeService.Accent;
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

    // CHANGED (folder types): menus list every type (built-in and custom) from FolderTypes.
    private static string MenuName(FolderType type) => type.Id == FolderTypes.General.Id ? "General files" : type.Name;

    private static void AddTypeItems(ItemsControl menu, string currentId, RoutedEventHandler onClick)
    {
        menu.Items.Clear();
        var custom = FolderTypes.Custom;

        foreach (var type in FolderTypes.BuiltIn)
        {
            // NEW (folder types, step 2): the step-2 types start their own group.
            if (type.Base == DirectoryViewProfile.Screenshots) menu.Items.Add(new Separator());

            menu.Items.Add(new MenuItem
            {
                Header = MenuName(type),
                Tag = type.Id,
                IsCheckable = true,
                IsChecked = type.Id.Equals(currentId, StringComparison.OrdinalIgnoreCase),
                ToolTip = string.IsNullOrEmpty(type.Description) ? null : type.Description // NEW (step 2)
            });
            ((MenuItem)menu.Items[^1]).Click += onClick;
            if (type.Id == FolderTypes.General.Id) menu.Items.Add(new Separator());
        }

        if (custom.Count > 0)
            menu.Items.Add(new Separator());

        foreach (var type in custom)
        {
            var item = new MenuItem
            {
                Header = type.Name,
                Tag = type.Id,
                IsCheckable = true,
                IsChecked = type.Id.Equals(currentId, StringComparison.OrdinalIgnoreCase),
                ToolTip = $"Custom type based on {type.Base}"
            };
            item.Click += onClick;
            menu.Items.Add(item);
        }
    }

    private void OnFolderTypeMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        var path = _viewModel.CurrentPath;
        var assigned = FolderTypes.AssignedTo(path);
        AddTypeItems(menu, assigned.Id, OnFolderProfileSelected);

        menu.Items.Add(new Separator());

        if (_viewModel.FolderTypeInheritedFrom is { } from)
            menu.Items.Add(new MenuItem { Header = $"{_viewModel.FolderType.Name} comes from {from}", IsEnabled = false });

        var subfolders = new MenuItem
        {
            Header = "Apply to subfolders",
            IsCheckable = true,
            IsChecked = _viewModel.FolderTypeAppliesToSubfolders,
            IsEnabled = !assigned.IsAutomatic,
            ToolTip = "Use this folder type for every folder inside this one, unless a folder has its own type"
        };
        subfolders.Click += (_, _) => _viewModel.SetFolderTypeAppliesToSubfolders(subfolders.IsChecked);
        menu.Items.Add(subfolders);

        var save = new MenuItem { Header = "Save this view as a folder type…", ToolTip = "Keeps the current layout and columns under a name you choose, like School" };
        save.Click += (_, _) => BeginNamingFolderType();
        menu.Items.Add(save);

        if (assigned.IsCustom)
        {
            var delete = new MenuItem { Header = $"Delete the \"{assigned.Name}\" folder type" };
            delete.Click += (_, _) =>
            {
                var confirm = MessageDialog.Show( // CHANGED (themes): themed dialog instead of the Windows message box
                    this,
                    $"Delete the {assigned.Name} folder type?\n\nFolders using it go back to Automatic. Files are not affected.",
                    "Delete folder type", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (confirm == MessageBoxResult.OK) _viewModel.DeleteFolderType(assigned);
            };
            menu.Items.Add(delete);
        }
    }

    private void OnSetFolderTypeSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menu && ReferenceEquals(e.OriginalSource, menu))
            AddTypeItems(menu, "", OnSelectedFolderProfile);
    }

    private void OnFolderProfileSelected(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string value } && FolderTypes.Find(value) is { } type)
            _viewModel.SetFolderType(type);
    }

    private void OnSelectedFolderProfile(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string value } && FolderTypes.Find(value) is { } type)
            _viewModel.SetFolderTypeForSelection(type);
    }

    // NEW (folder types): the small naming panel used for tags and categories also names folder types.
    private bool _isNamingFolderType;

    private void BeginNamingFolderType()
    {
        _editingCategoryId = null;
        _isNamingTag = false;
        _isNamingFolderType = true;
        CategoryPanelTitle.Text = "Save this view as a folder type";
        CategoryBox.Text = _viewModel.FolderType.IsCustom ? _viewModel.FolderType.Name : string.Empty;
        CategoryPanel.Visibility = Visibility.Visible;
        CategoryBox.Focus();
        CategoryBox.SelectAll();
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


    // CHANGED (drag to select): a drag moves the file only when it starts on the file itself: its icon or
    // text in the details view, anywhere on its tile in the tile view. From the blank part of a row (the
    // row's own background) or from empty space, a drag draws a selection rectangle instead; see
    // MainWindow.BandSelect.cs. A plain click on the blank part of a row still selects the row.
    private void OnFileListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var row = FindAncestor<ListViewItem>(source);
        var onBlankOfRow = FileList.View is not null && source is Border { TemplatedParent: ListViewItem };

        _isFileDragPending = row is not null && !onBlankOfRow;
        _fileDragStart = e.GetPosition(FileList);
        NoteBandPress(e, row, _isFileDragPending);

        // FIXED (drag several): dragging a selection of several files moved only the one under the pointer.
        // The list reacts to the button going down on a selected file by selecting that file alone, so by
        // the time the drag started the rest of the selection was gone. Now, when the press is on a file
        // that is already selected, several are selected and no Ctrl or Shift is held, the list does not
        // get the press: the selection stays, and a drag takes all of it. If the button comes up again
        // without a drag, that file becomes the only one selected then (OnFileListPreviewMouseLeftButtonUp),
        // which is what a plain click on it has always done, and what File Explorer does.
        // Left alone: a double click's second press, and presses on a button or text box inside the row.
        _pressedInSelection = null;
        if (_isFileDragPending && e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None &&
            row is { IsSelected: true, DataContext: FileSystemItem pressed } && FileList.SelectedItems.Count > 1 &&
            FindAncestor<ButtonBase>(source) is null && FindAncestor<TextBoxBase>(source) is null)
        {
            _pressedInSelection = pressed;
            row.Focus(); // the list would have done this itself
            e.Handled = true;
        }
    }

    // FIXED (drag several): a click (no drag) on one of several selected files selects just that file, now
    // that the press itself no longer does. Shift+click afterwards counts from it, as after any click.
    private void OnFileListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedInSelection is not { } pressed)
            return;

        _pressedInSelection = null;

        // Released somewhere else: nothing was clicked.
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is not { DataContext: FileSystemItem released } ||
            !ReferenceEquals(released, pressed))
            return;

        FileList.SelectedItem = pressed; // in a list that allows several, this selects it alone

        try { SelectionAnchorProperty?.SetValue(FileList, pressed); }
        catch (Exception) { } // Shift+click then counts from the earlier file; nothing worse
    }

    // Where a Shift+click range starts. ListBox keeps it in a protected property and moves it on every
    // click it handles itself; the click above is one it never saw.
    private static readonly System.Reflection.PropertyInfo? SelectionAnchorProperty = typeof(ListBox).GetProperty(
        "AnchorItem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private void OnFileListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        // NEW (drag to select)
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _bandPending = false;
            _pressedInSelection = null; // FIXED (drag several): the button came up somewhere else
            return;
        }

        if (_bandActive)
        {
            // The rectangle's own layer normally hears the mouse; this is for when it could not take it.
            if (BandArea is { } area) { _bandPointer = e.GetPosition(area); _bandDirty = true; }
            return;
        }

        if (TryBeginBand(e))
            return;

        if (!_isFileDragPending)
            return;

        var point = e.GetPosition(FileList);
        if (Math.Abs(point.X - _fileDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _fileDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _isFileDragPending = false;
        _pressedInSelection = null; // FIXED (drag several): this is a drag, so the selection stays as it is

        var paths = _viewModel.Context.SelectedItems
            .Where(item => !item.IsDriveRoot)
            .Select(item => item.FullPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        // NEW (zip folders): things dragged out of a ZIP file are taken out into Clearspace's temporary
        // folder first, and the drag carries those copies, so they can be dropped on a folder here, on the
        // desktop or into another program. Such a drag only copies: nothing leaves the ZIP file.
        var allowed = DragDropEffects.Copy | DragDropEffects.Move;
        if (_viewModel.Context.SelectionInArchive)
        {
            if (ArchiveDragPaths() is not { } copies)
                return;

            paths = copies;
            allowed = DragDropEffects.Copy;
        }

        if (paths.Length == 0)
            return;

        var data = new DataObject(DataFormats.FileDrop, paths);
        // CHANGED (scroll while dragging): the wheel is listened for while the drag lasts.
        BeginDragScroll();
        try { DragDrop.DoDragDrop(FileList, data, allowed); }
        finally { EndDragScroll(); }
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

        // NEW (zip folders): nothing can be dropped into a ZIP file, neither on a folder inside it nor on
        // the empty part of the list (the second is covered below: its address starts with clearspace://).
        if (targetRow?.DataContext is FileSystemItem { IsInArchive: true })
            return DragDropEffects.None;

        targetFolder = targetRow?.DataContext is FileSystemItem { IsFolder: true } folder
            ? folder.FullPath
            : (string.IsNullOrWhiteSpace(_viewModel.CurrentPath) ||
               _viewModel.CurrentPath.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase)
                ? null
                : _viewModel.CurrentPath);

        if (targetFolder is null)
            return DragDropEffects.None;

        return FileDropEffects(sourcePaths, targetFolder, e.KeyStates); // CHANGED (tabs): the rules moved out, below
    }

    // CHANGED (tabs): taken out of ResolveFileDropEffects unchanged, so that dropping files on a tab
    // (MainWindow.Tabs.cs) follows exactly the same rules as dropping them into the list: nothing when the
    // files are already there or a folder would go into itself; Shift = move, Ctrl = copy; otherwise move
    // on the same drive and copy across drives.
    private static DragDropEffects FileDropEffects(string[] sourcePaths, string destination, DragDropKeyStates keys)
    {
        if (sourcePaths.Any(path => IsSameOrAncestorOf(path, destination)))
            return DragDropEffects.None;

        if (sourcePaths.All(path =>
                string.Equals(Path.GetDirectoryName(path), destination, StringComparison.OrdinalIgnoreCase)))
            return DragDropEffects.None;

        if ((keys & DragDropKeyStates.ShiftKey) != 0)
            return DragDropEffects.Move;

        if ((keys & DragDropKeyStates.ControlKey) != 0)
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
        // CHANGED (themes): the theme's accent instead of a fixed amber.
        row.Background = ThemeService.AccentSoft;
        row.BorderBrush = ThemeService.Accent;
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
        {
            ThumbnailService.Request(item, ThumbnailSize);
            if (item.IsVideoFile) MediaPropertyService.Request(item); // NEW (folder types, step 3): length badge
        }
    }

    private void OnTileLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileSystemItem item })
        {
            ThumbnailService.Request(item, ThumbnailSize);
            if (item.IsVideoFile) MediaPropertyService.Request(item); // NEW (folder types, step 3): length badge
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


    // ------------------------------------------------------------------ NEW (folder types, step 2)

    private long _listReplacedAt; // NEW (search fix): when the list last got new contents; 0 once that has been laid out

    private void ApplyGrouping()
    {
        if (FileList.ItemsSource is null)
            return;

        var view = CollectionViewSource.GetDefaultView(FileList.ItemsSource);
        if (view is null || !view.CanGroup)
            return;

        // CHANGED (folder types, step 3): date groups or kind groups (Desktop).
        var want = _viewModel.ListGrouping;
        var have = view.GroupDescriptions.Count > 0 ? (view.GroupDescriptions[0] as PropertyGroupDescription)?.PropertyName : null;
        if (want == have)
            return;

        using (SearchTrace.During("changing how the list is grouped")) // NEW (search fix): named in the search trace
        using (view.DeferRefresh())
        {
            view.GroupDescriptions.Clear();
            if (want is not null)
                view.GroupDescriptions.Add(new PropertyGroupDescription(want));
        }
    }

    private void OnDimensionsCellLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileSystemItem item })
            MediaPropertyService.RequestDimensions(item);
    }

    private void OnDimensionsCellDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is FileSystemItem item && sender is FrameworkElement { IsLoaded: true })
            MediaPropertyService.RequestDimensions(item);
    }

    // NEW (folder types, step 3): Source column (Downloads).
    private void OnSourceCellLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileSystemItem item })
            MediaPropertyService.RequestSource(item);
    }

    private void OnSourceCellDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is FileSystemItem item && sender is FrameworkElement { IsLoaded: true })
            MediaPropertyService.RequestSource(item);
    }

    private void OnPinToProject(object sender, RoutedEventArgs e) => _viewModel.PinSelectionToProject(pin: true);

    private void OnUnpinFromProject(object sender, RoutedEventArgs e) => _viewModel.PinSelectionToProject(pin: false);

    private void OnProjectItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectStripItem item })
            _viewModel.OpenProjectItem(item);
    }

    private void OnProjectItemOpen(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectStripItem item })
            _viewModel.OpenProjectItem(item);
    }

    private void OnProjectItemShowInFolder(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectStripItem item })
            _viewModel.ShowProjectItemInFolder(item);
    }

    private void OnProjectItemUnpin(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectStripItem item })
            _viewModel.UnpinFromProject(item);
    }

    private void OnMusicRowDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // CHANGED (folder types, step 3): whenever a visible column needs file properties (Music, Videos,
        // Documents, Research), not only in Music folders.
        if (_viewModel.NeedsFileProperties && e.NewValue is FileSystemItem item)
            MediaPropertyService.Request(item);
    }

    private void OnMusicRowLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.NeedsFileProperties && sender is FrameworkElement { DataContext: FileSystemItem item })
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

    // CHANGED (empty-area menu): decides which right-click menu the list shows. On a file or folder: the usual
    // one. On empty space inside a real folder: the selection is cleared (as Explorer does) and the empty-area
    // menu from MainWindow.BackgroundMenu.cs is shown. Column headers, scroll bars and places that are not
    // folders (This PC, hubs) keep the usual menu.
    private void OnFileListRightClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        if (FindAncestor<ListViewItem>(source) is { DataContext: FileSystemItem item })
        {
            FileList.ContextMenu = _itemMenu;
            if (!FileList.SelectedItems.Contains(item))
                FileList.SelectedItem = item;
            return;
        }

        var onHeaderOrScrollBar = FindAncestor<GridViewColumnHeader>(source) is not null
                                  || FindAncestor<ScrollBar>(source) is not null;

        // NEW (your files): empty space on the Your files page has a menu of its own (New folder, Add a folder).
        if (!onHeaderOrScrollBar && _viewModel.IsYourFilesHub)
        {
            FileList.UnselectAll();
            FileList.ContextMenu = BuildYourFilesMenu();
            return;
        }

        // NEW (map network drive): so does empty space on the This PC and Network pages (Map network drive, Refresh).
        if (!onHeaderOrScrollBar && _viewModel.IsDrivesPage)
        {
            FileList.UnselectAll();
            FileList.ContextMenu = BuildDrivesPageMenu();
            return;
        }

        // NEW (zip folders): and empty space inside a ZIP file (Extract all, Refresh). See MainWindow.Archives.cs.
        if (!onHeaderOrScrollBar && _viewModel.IsInArchive)
        {
            FileList.UnselectAll();
            FileList.ContextMenu = BuildArchivePageMenu();
            return;
        }

        if (onHeaderOrScrollBar || !Directory.Exists(_viewModel.CurrentPath))
        {
            FileList.ContextMenu = _itemMenu;
            return;
        }

        FileList.UnselectAll();
        FileList.ContextMenu = BuildBackgroundMenu();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // NEW (drag to select): while a selection rectangle is being dragged, rows go in and out of the
        // selection many times a second. The rest of the window is told a few times a second and once at
        // the end (PushBandSelection), not for every row.
        if (_bandQuiet)
        {
            _bandChanged = true;
            return;
        }

        _viewModel.Context.SelectedItems = FileList.SelectedItems
            .Cast<FileSystemItem>()
            .ToList();
    }

    // FIXED (pre-release review): the new selection is set in one step (SetSelection, in MainWindow.BandSelect.cs).
    // It used to be cleared and then built up one row at a time. Each row added made the list look through
    // its selection and told the rest of the window (status line, toolbar, tags, preview) about a selection
    // one row larger, so the work grew with the square of the folder's size: in a folder of twenty thousand
    // files the window stopped responding for a long while.
    private void InvertSelection()
    {
        var selected = FileList.SelectedItems.Cast<FileSystemItem>().ToHashSet();
        SetSelection([.. _viewModel.Items.Where(item => !selected.Contains(item))]);
    }


    private void OnAddressActivate(object sender, MouseButtonEventArgs e) => ShowAddressEditor();

    private void ShowAddressEditor()
    {
        BreadcrumbBar.Visibility = Visibility.Collapsed;
        AddressActivator.Visibility = Visibility.Collapsed;
        AddressBox.Visibility = Visibility.Visible;
        // CHANGED (zip folders): inside a ZIP file the box shows the address written like a path
        // (C:\Downloads\Photos.zip\Trip), which is also what can be typed or pasted to get there.
        AddressBox.Text = ArchiveLocation.Display(_viewModel.CurrentPath);
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
            // NEW (zip folders): the path of a ZIP file, or of a folder inside one, opens it in the list.
            // The path of a file inside one goes to the folder that file is in.
            else if (ArchiveLocation.FromTyped(target) is { } insideZip)
            {
                var found = ArchiveBrowser.Find(insideZip);
                if (found is { IsFolder: false })
                    insideZip = ArchiveLocation.Parent(insideZip) ?? insideZip;

                if (found is null && ArchiveLocation.TrySplit(insideZip, out _, out var rest) && rest.Length > 0)
                {
                    SystemSounds_Beep(); // the ZIP file is there, but has no such folder
                }
                else
                {
                    _viewModel.Navigation.Navigate(insideZip);
                    HideAddressEditor();
                    FileList.Focus();
                }
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
