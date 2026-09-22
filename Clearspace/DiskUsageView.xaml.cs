using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

public partial class DiskUsageView : UserControl, IDisposable
{
    private readonly DiskUsageViewModel _viewModel;
    private readonly string? _preferredPath;
    private readonly Func<DiskUsageDeleteRequest, bool> _confirmDelete;
    public event EventHandler? FileOperationCompleted;
    public event EventHandler? CloseRequested;
    private bool _started;
    private IntPtr OwnerHandle => Window.GetWindow(this) is { } owner ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;

    public DiskUsageView(string? preferredPath = null) : this(new DiskUsageViewModel(), preferredPath) { }

    internal DiskUsageView(DiskUsageViewModel viewModel, string? preferredPath = null,
        Func<DiskUsageDeleteRequest, bool>? confirmDelete = null)
    {
        _viewModel = viewModel;
        _preferredPath = preferredPath;
        _confirmDelete = confirmDelete ?? (request => MessageBox.Show(Window.GetWindow(this), request.ConfirmationMessage,
            "Delete permanently", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewChanged;
        Treemap.ItemInvoked += OnTileInvoked;
        Treemap.DeleteRequested += OnTileDelete;
        Treemap.ZoomChanged += OnZoomChanged;
        Treemap.ParentRequested += OnWheelParent;
        _viewModel.FileOperationCompleted += OnFileOperationCompleted;
        PreviewMouseDown += OnNavigationMouseDown;
        PreviewKeyDown += OnNavigationKeyDown;
        Loaded += async (_, _) =>
        {
            if (_started) return;
            _started = true;
            await _viewModel.LoadAsync(preferredPath: _preferredPath);
        };
    }

    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnViewChanged;
        _viewModel.FileOperationCompleted -= OnFileOperationCompleted;
        _viewModel.Dispose();
    }

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private async void OnWheelParent() => await _viewModel.UpAsync();

    private void OnFileOperationCompleted(object? sender, EventArgs e) => FileOperationCompleted?.Invoke(this, e);
    private async void OnTileDelete(DiskUsageItem item)
    {
        if (_viewModel.IsBusy) return;
        ItemList.SelectedItems.Clear();
        ItemList.SelectedItem = item;
        await _viewModel.DeletePermanentlyAsync(OwnerHandle, _confirmDelete);
    }
    private async void OnDeletePermanently(object sender, RoutedEventArgs e)
        => await _viewModel.DeletePermanentlyAsync(OwnerHandle, _confirmDelete);
    private void OnListRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ItemList, e.OriginalSource as DependencyObject) is ListViewItem row)
        {
            if (!row.IsSelected) { ItemList.SelectedItems.Clear(); row.IsSelected = true; }
            row.Focus();
        }
        else { ItemList.SelectedItems.Clear(); }
    }

    private void OnViewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiskUsageViewModel.MapItems)) Treemap.SetItems(_viewModel.Items, _viewModel.CurrentPath, _viewModel.MapScene);
        if (e.PropertyName is nameof(DiskUsageViewModel.CanGoBack) or nameof(DiskUsageViewModel.IsBusy)) UpdateZoomNavigation();
    }

    private void UpdateZoomNavigation() => BackButton.SetCurrentValue(IsEnabledProperty,
        !_viewModel.IsBusy && (Treemap.IsZoomed || _viewModel.CanGoBack));

    private void OnZoomChanged()
    {
        UpdateZoomNavigation();
        ItemList.SelectedItems.Clear();
        ZoomOutButton.Visibility = Treemap.IsZoomed ? Visibility.Visible : Visibility.Collapsed;
        Treemap.ToolTip = "Scroll to zoom at the pointer. Click a folder to open it. Back zooms out.";
    }

    private async void OnTileInvoked(DiskUsageItem item)
    {
        if (_viewModel.IsBusy) return;
        _viewModel.Select(item);
        if (item.IsFolder) await _viewModel.NavigateAsync(item.Id);
        else if (item.Id >= 0) { ItemList.SelectedItem = item; ItemList.ScrollIntoView(item); }
        else
        {
            ItemList.SelectedItem = null;
            _viewModel.Select(item);
            var firstGrouped = _viewModel.Items.Skip(23).FirstOrDefault();
            if (firstGrouped is not null) ItemList.ScrollIntoView(firstGrouped);
        }
    }

    private async void OnNavigationMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2)) return;
        e.Handled = true;
        if (e.ChangedButton == MouseButton.XButton1 && Treemap.IsZoomed) Treemap.ZoomOut();
        else if (e.ChangedButton == MouseButton.XButton1) await _viewModel.BackAsync();
        else await _viewModel.ForwardAsync();
    }

    private async void OnNavigationKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && Treemap.IsZoomed) { e.Handled = true; Treemap.ZoomOut(); return; }
        if (key == Key.Delete && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            e.Handled = true;
            await _viewModel.DeletePermanentlyAsync(OwnerHandle, _confirmDelete);
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Alt || key is not (Key.Left or Key.Right or Key.Up)) return;
        e.Handled = true;
        if (key == Key.Left && Treemap.IsZoomed) Treemap.ZoomOut();
        else if (key == Key.Left) await _viewModel.BackAsync();
        else if (key == Key.Right) await _viewModel.ForwardAsync();
        else await _viewModel.UpAsync();
    }

    private async void OnBack(object sender, RoutedEventArgs e)
    {
        if (Treemap.IsZoomed) Treemap.ZoomOut();
        else await _viewModel.BackAsync();
    }
    private async void OnForward(object sender, RoutedEventArgs e) => await _viewModel.ForwardAsync();
    private async void OnBreadcrumb(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DiskUsageCrumb crumb }) await _viewModel.NavigateAsync(crumb.Id);
    }
    private async void OnDriveChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_viewModel.IsBusy && Drives.SelectedItem is string root && root != _viewModel.SelectedRoot)
            await _viewModel.LoadAsync(root);
    }
    private async void OnReload(object sender, RoutedEventArgs e)
    {
        var path = _viewModel.CurrentPath;
        await _viewModel.LoadAsync(_viewModel.SelectedRoot, string.IsNullOrEmpty(path) ? _preferredPath : path);
    }
    private void OnCancel(object sender, RoutedEventArgs e) => _viewModel.Cancel();
    private async void OnUp(object sender, RoutedEventArgs e) => await _viewModel.UpAsync();
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = ItemList.SelectedItem as DiskUsageItem;
        _viewModel.SetSelection(ItemList.SelectedItems.Cast<DiskUsageItem>());
        Treemap.Highlight(item?.Id);
    }
    private async void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ItemList, e.OriginalSource as DependencyObject) is ListViewItem { Content: DiskUsageItem item } && item.IsFolder)
            await _viewModel.NavigateAsync(item.Id);
    }
    private async void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ItemList.SelectedItem is DiskUsageItem { IsFolder: true } item)
        { e.Handled = true; await _viewModel.NavigateAsync(item.Id); }
        else if (e.Key == Key.Back) { e.Handled = true; await _viewModel.UpAsync(); }
    }
}
