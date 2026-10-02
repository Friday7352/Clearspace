// Clearspace | NEW (tabs): the main window's side of tabs.
// The tabs themselves (which exist, which is active, each one's back / forward list) are
// ViewModels/ExplorerTabs.cs; the strip is drawn by the XAML at the top of MainWindow.xaml. This file is
// everything you do to a tab with the mouse or keyboard, plus the two things only the window knows about
// a tab: how far its list was scrolled and what was selected in it.
//  - Click a tab to switch, drag it sideways to reorder, middle-click it or use its x to close it,
//    right-click for Duplicate / Close / Close others / Close to the right / Reopen closed tab.
//  - NEW (tab to new window): drag a tab off the strip and let go to give it a window of its own, where
//    you let go. A small copy of the tab follows the pointer while it is off the strip. "Move to new
//    window" in the tab's menu does the same without dragging.
//  - Ctrl+T new tab, Ctrl+W close, Ctrl+Tab / Ctrl+Shift+Tab next / previous, Ctrl+Shift+T reopen,
//    Ctrl+1 ... Ctrl+8 that tab, Ctrl+9 the last tab. They work from the search and address boxes too.
//  - Middle-click a folder in the list, a sidebar entry or a part of the address bar to open it in a new
//    tab in the background; "Open in new tab" in the right-click menus does the same.
//  - Drag files over a tab: resting on it for a moment switches to it (so you can drop into its list);
//    dropping on the tab itself moves or copies into that tab's folder.
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives; // NEW (tab to new window): Popup
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Clearspace.Commands;
using Clearspace.Controls;
using Clearspace.Models;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

public partial class MainWindow
{
    private ExplorerTabs Tabs => _viewModel.Tabs;

    private void InitializeTabs()
    {
        Tabs.Deactivating += OnTabDeactivating;
        Tabs.Activated += OnTabActivated;
        Tabs.LastTabClosed += (_, _) => Close(); // as in Explorer: closing the only tab closes the window

        // A tab's scroll position and selection go back once its folder is listed, which can be a moment
        // after the switch (and twice: the remembered listing first, then the fresh one).
        DependencyPropertyDescriptor
            .FromProperty(ItemsControl.ItemsSourceProperty, typeof(ListView))
            .AddValueChanged(FileList, (_, _) => QueueTabViewRestore());
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsLoading))
                QueueTabViewRestore();
        };

        PreviewMouseDown += OnWindowMiddleClick;
    }

    // ------------------------------------------------------------------ keyboard

    private static readonly HashSet<CommandCode> TabCommands =
    [
        CommandCode.NewTab, CommandCode.CloseTab, CommandCode.NextTab,
        CommandCode.PreviousTab, CommandCode.ReopenClosedTab,
        CommandCode.NewWindow // NEW (new window): Ctrl+N too
    ];

    // Called from OnPreviewKeyDown before it gives up on keys typed into a text box: tab shortcuts work
    // wherever the keyboard is. True when the key was a tab shortcut.
    private bool TryHandleTabKey(KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;

        // Every tab shortcut has Ctrl and none has Alt (Ctrl+Alt is how AltGr arrives: that is typing).
        if ((modifiers & ModifierKeys.Control) == 0 || (modifiers & ModifierKeys.Alt) != 0)
            return false;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (modifiers == ModifierKeys.Control && key is >= Key.D1 and <= Key.D9)
        {
            if (key == Key.D9) Tabs.ActivateLast();
            else Tabs.ActivateAt(key - Key.D1);

            e.Handled = true;
            return true;
        }

        var command = _viewModel.Commands.TryGetByHotKey(new HotKey(key, modifiers));
        if (command is null || !TabCommands.Contains(command.Code))
            return false;

        if (command.CanExecute(null))
            command.Execute(null);

        e.Handled = true; // also when it can't run (Ctrl+Tab with one tab), so WPF doesn't move focus instead
        return true;
    }

    // ------------------------------------------------------------------ scroll position and selection

    // Restoring thousands of selected rows one by one would stall the switch; a selection that large is
    // dropped instead.
    private const int MaxRestoredSelection = 500;

    private ExplorerTab? _restoreTab;   // the tab whose view is still being put back
    private string _restorePath = string.Empty;
    private bool _restoreQueued;
    private bool _restoreReached;       // the saved scroll position has been reached

    private ScrollViewer? FileListScroll => FindDescendant<ScrollViewer>(FileList);

    // Leaving a tab: its folder is still on screen, so this is the moment to note where it was.
    private void OnTabDeactivating(object? sender, ExplorerTab tab)
    {
        // A rename, a "new tag" name or a typed address in progress belongs to the folder being left.
        if (_renameTarget is not null) CancelRename();
        if (CategoryPanel.Visibility == Visibility.Visible) CloseCategoryPanel();
        if (AddressBox.Visibility == Visibility.Visible) HideAddressEditor();

        tab.ScrollOffset = FileListScroll?.VerticalOffset ?? 0;
        tab.SelectedPaths = FileList.SelectedItems.Count is > 0 and <= MaxRestoredSelection
            ? FileList.SelectedItems.Cast<FileSystemItem>().Select(item => item.FullPath).ToArray()
            : [];

        _restoreTab = null;
    }

    private void OnTabActivated(object? sender, ExplorerTab tab)
    {
        _restoreTab = tab;
        _restorePath = tab.Location;
        _restoreReached = false;
        QueueTabViewRestore();
        FileList.Focus();
    }

    // Runs RestoreTabView once the list has been laid out with whatever was just put in it.
    private void QueueTabViewRestore()
    {
        if (_restoreTab is null || _restoreQueued)
            return;

        _restoreQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _restoreQueued = false;
            RestoreTabView();
        }));
    }

    private void RestoreTabView()
    {
        if (_restoreTab is not { } tab)
            return;

        // You switched again, or moved on inside the tab, before its folder finished loading.
        if (!ReferenceEquals(tab, Tabs.Active) ||
            !string.Equals(_viewModel.CurrentPath, _restorePath, StringComparison.OrdinalIgnoreCase))
        {
            _restoreTab = null;
            return;
        }

        if (_viewModel.Items.Count > 0 || !_viewModel.IsLoading)
        {
            // Scroll. Also done for a tab that was at the top: the list keeps its position when its items
            // are replaced, so without this a new tab would open as far down as the tab you came from.
            // Done again only if the position could not be reached yet (the first rows of a folder that is
            // still being read) or the list went back to the top when the complete listing arrived.
            if (FileListScroll is { } scroll &&
                (!_restoreReached || (scroll.VerticalOffset < 1 && tab.ScrollOffset >= 1)))
            {
                scroll.ScrollToVerticalOffset(tab.ScrollOffset);
                _restoreReached = scroll.ScrollableHeight >= tab.ScrollOffset - 1;
            }

            // Selection. The rows are new objects each time the folder is read, so match by path.
            if (tab.SelectedPaths.Count > 0 && FileList.SelectedItems.Count == 0)
            {
                var wanted = new HashSet<string>(tab.SelectedPaths, StringComparer.OrdinalIgnoreCase);
                foreach (var item in _viewModel.Items)
                {
                    if (wanted.Contains(item.FullPath))
                        FileList.SelectedItems.Add(item);
                }
            }
        }

        if (!_viewModel.IsLoading)
            _restoreTab = null; // the folder is fully listed: done
    }

    // ------------------------------------------------------------------ the tab strip: click, drag, close

    private ExplorerTab? _tabDrag;
    private Point _tabDragStart;   // CHANGED (tab to new window): the whole point (was only X), a tab can now be dragged downwards too
    private bool _tabDragging;
    private bool _tabOffStrip;     // NEW (tab to new window): the dragged tab is off the strip; letting go makes it a window

    // NEW (tab to new window): how far past the strip's edges the pointer must go before the tab counts as
    // dragged off it. Enough that an unsteady sideways drag keeps reordering.
    private const double TearOffAbove = 30;
    private const double TearOffBelow = 34;
    private const double TearOffSide = 60;

    private TabStripPanel? TabLayout => FindDescendant<TabStripPanel>(TabStrip);

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ExplorerTab tab })
            return;

        e.Handled = true;
        _tabDragStart = e.GetPosition(TabStrip);
        _tabDragging = false;
        _tabOffStrip = false;

        // Switch on the press, like Explorer. A drag can follow only if the button is still down afterwards
        // (a password prompt for a locked folder takes the release).
        _tabDrag = Tabs.Activate(tab) && Mouse.LeftButton == MouseButtonState.Pressed ? tab : null;
    }

    // Dragging a tab sideways moves it to the position under the pointer as you go.
    // CHANGED (tab to new window): dragging it off the strip (up, down, or past either end) stops the
    // reordering and shows a small copy of the tab at the pointer; letting go there gives the tab a window
    // of its own (OnTabStripMouseUp). Bringing it back onto the strip carries on reordering.
    private void OnTabStripMouseMove(object sender, MouseEventArgs e)
    {
        if (_tabDrag is null)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndTabDrag();
            return;
        }

        var position = e.GetPosition(TabStrip);

        if (!_tabDragging)
        {
            // CHANGED (tab to new window): a drag can start in any direction (was sideways only).
            if (Math.Abs(position.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance * 2 &&
                Math.Abs(position.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance * 2)
                return;

            _tabDragging = true;
            TabStrip.CaptureMouse(); // keeps the drag going when the pointer leaves the strip, or the window
        }

        e.Handled = true;

        // NEW (tab to new window). The only tab can't leave: that would just be moving the window.
        _tabOffStrip = Tabs.Count > 1 && IsOffTabStrip(position);
        if (_tabOffStrip)
        {
            ShowTabGhost(_tabDrag, position);
            return;
        }

        HideTabGhost();

        if (TabLayout is { TabWidth: > 0 } panel)
            Tabs.Move(_tabDrag, (int)Math.Floor(position.X / panel.TabWidth));
    }

    // NEW (tab to new window): the strip's own area plus a margin around it, so the whole row of tabs and
    // the space beside the + button still count as "on the strip".
    private bool IsOffTabStrip(Point position)
        => position.Y < -TearOffAbove ||
           position.Y > TabStrip.ActualHeight + TearOffBelow ||
           position.X < -TearOffSide ||
           position.X > TabRow.ActualWidth + TearOffSide;

    // CHANGED (tab to new window): letting go off the strip moves the tab to a new window at that spot.
    private void OnTabStripMouseUp(object sender, MouseButtonEventArgs e)
    {
        var tab = _tabDrag;
        var tearOff = _tabDragging && _tabOffStrip;
        var dropped = tearOff ? PointToScreen(e.GetPosition(this)) : default;

        EndTabDrag();

        if (tearOff && tab is not null)
            MoveTabToNewWindow(tab, dropped);
    }

    private void OnTabStripLostCapture(object sender, MouseEventArgs e)
    {
        // Only the strip's own capture ends a drag; a close button letting go of the mouse also passes by here.
        if (ReferenceEquals(e.OriginalSource, TabStrip))
        {
            _tabDrag = null;
            _tabDragging = false;
            _tabOffStrip = false;   // NEW (tab to new window)
            HideTabGhost();         // NEW (tab to new window)
        }
    }

    private void EndTabDrag()
    {
        var wasDragging = _tabDragging;
        _tabDrag = null;
        _tabDragging = false;
        _tabOffStrip = false;   // NEW (tab to new window)
        HideTabGhost();         // NEW (tab to new window)

        if (wasDragging && TabStrip.IsMouseCaptured)
            TabStrip.ReleaseMouseCapture();
    }

    // ---- NEW (tab to new window): the copy of the tab that follows the pointer while it is off the strip.
    // A Popup, because it has to be able to leave the window. It is placed relative to the strip, in the
    // strip's own units, so it lands under the pointer at any display scale.

    private Popup? _tabGhost;
    private TextBlock? _tabGhostGlyph;
    private TextBlock? _tabGhostTitle;

    private void ShowTabGhost(ExplorerTab tab, Point position)
    {
        if (_tabGhost is null)
        {
            _tabGhostGlyph = new TextBlock
            {
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 12,
                Margin = new Thickness(10, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _tabGhostGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");

            _tabGhostTitle = new TextBlock
            {
                FontSize = 12,
                MaxWidth = 190,
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _tabGhostTitle.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            _tabGhostTitle.SetResourceReference(TextBlock.FontFamilyProperty, "UIFont");

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(_tabGhostGlyph);
            content.Children.Add(_tabGhostTitle);

            // The look of the tab you are on (see the tab template in MainWindow.xaml).
            var frame = new Border { Height = 30, BorderThickness = new Thickness(1), Child = content };
            frame.SetResourceReference(Border.BackgroundProperty, "Surface");
            frame.SetResourceReference(Border.BorderBrushProperty, "EdgeSunken");
            frame.SetResourceReference(Border.CornerRadiusProperty, "R8");

            _tabGhost = new Popup
            {
                PlacementTarget = TabStrip,
                Placement = PlacementMode.Relative,
                AllowsTransparency = true,
                IsHitTestVisible = false,
                Focusable = false,
                Child = frame
            };
        }

        _tabGhostGlyph!.Text = tab.Glyph;
        _tabGhostTitle!.Text = tab.Title;
        _tabGhost.HorizontalOffset = position.X + 14;
        _tabGhost.VerticalOffset = position.Y + 16;
        _tabGhost.IsOpen = true;
    }

    private void HideTabGhost()
    {
        if (_tabGhost is { IsOpen: true })
            _tabGhost.IsOpen = false;
    }

    private void OnTabCloseClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ExplorerTab tab })
            Tabs.Close(tab);
    }

    private void OnTabRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ExplorerTab tab } element)
            return;

        // Built each time it opens (like the empty-area menu), so what is greyed out is always current.
        var menu = new ContextMenu { PlacementTarget = element };

        menu.Items.Add(Entry("New tab", "\uE710", () => Tabs.Open(), "Ctrl+T"));
        menu.Items.Add(Entry("Duplicate tab", "\uE8C8", () => Tabs.Duplicate(tab)));

        // NEW (tab to new window): the same as dragging the tab off the strip.
        var move = Entry("Move to new window", "\uE78B", () => MoveTabToNewWindow(tab));
        move.IsEnabled = Tabs.Count > 1;
        menu.Items.Add(move);
        menu.Items.Add(new Separator());

        menu.Items.Add(Entry("Close tab", "\uE711", () => Tabs.Close(tab), "Ctrl+W"));

        var others = Entry("Close other tabs", null, () => Tabs.CloseOthers(tab));
        others.IsEnabled = Tabs.Count > 1;
        menu.Items.Add(others);

        var right = Entry("Close tabs to the right", null, () => Tabs.CloseToRight(tab));
        right.IsEnabled = Tabs.HasTabsToRight(tab);
        menu.Items.Add(right);

        menu.Items.Add(new Separator());

        var reopen = Entry("Reopen closed tab", "\uE7A7", () => Tabs.ReopenClosed(), "Ctrl+Shift+T");
        reopen.IsEnabled = Tabs.CanReopen;
        menu.Items.Add(reopen);

        menu.IsOpen = true;
        e.Handled = true;
    }

    // ------------------------------------------------------------------ middle click

    // The middle button anywhere in the window: on a tab it closes the tab; on a folder in the list, a
    // sidebar entry or a part of the address bar it opens that place in a new background tab.
    private void OnWindowMiddleClick(object sender, MouseButtonEventArgs e)
    {
        // The photo viewer pans with the middle button, and the other views have no tabs.
        if (e.ChangedButton != MouseButton.Middle ||
            _indexingView is not null || _diskUsageView is not null || _viewModel.Viewer.IsOpen)
            return;

        if (e.OriginalSource is not Visual source)
            return;

        if (FindAncestor<ItemsControl>(source) == TabStrip && TabUnder(source) is { } tab)
        {
            Tabs.Close(tab);
            e.Handled = true;
            return;
        }

        string? path = null;

        if (FindAncestor<ListViewItem>(source) is { DataContext: FileSystemItem { IsFolder: true } folder })
            path = folder.FullPath;
        else if (FindAncestor<Button>(source) is { } button)
        {
            path = button.DataContext switch
            {
                SidebarEntry entry when CanBrowse(entry.Path) => entry.Path,
                Breadcrumb crumb => crumb.Path,
                _ => null
            };
        }

        if (string.IsNullOrEmpty(path))
            return;

        Tabs.Open(path, activate: false);
        e.Handled = true;
    }

    private static ExplorerTab? TabUnder(DependencyObject? source)
    {
        for (; source is not null; source = VisualTreeHelper.GetParent(source))
        {
            if (source is FrameworkElement { DataContext: ExplorerTab tab })
                return tab;
        }

        return null;
    }

    // A place Clearspace can show: a folder that exists, or one of its own pages (This PC, a hub).
    private static bool CanBrowse(string? path)
        => !string.IsNullOrEmpty(path) &&
           (path.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase) || Directory.Exists(path));

    private void OnSidebarOpenInNewTab(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: SidebarEntry entry } && CanBrowse(entry.Path))
            Tabs.Open(entry.Path, activate: false);
    }

    // ------------------------------------------------------------------ dragging files onto a tab

    // How long files must rest on a background tab before the window switches to it.
    private static readonly TimeSpan TabHoverDelay = TimeSpan.FromMilliseconds(650);

    private DispatcherTimer? _tabHoverTimer;
    private ExplorerTab? _tabHoverTarget;
    private DateTime _tabHoverSeen;
    private Border? _tabDropBorder;

    private void OnTabDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (sender is not Border { DataContext: ExplorerTab tab } border || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        WatchTabHover(tab);

        e.Effects = TabDropEffects(e, tab, out _);
        ShowTabDropTarget(e.Effects == DragDropEffects.None ? null : border);
    }

    private void OnTabDragLeave(object sender, DragEventArgs e) => ShowTabDropTarget(null);

    private void OnTabDrop(object sender, DragEventArgs e)
    {
        ShowTabDropTarget(null);
        StopTabHover();
        e.Handled = true;

        if (sender is not Border { DataContext: ExplorerTab tab })
            return;

        var effects = TabDropEffects(e, tab, out var destination);
        if (effects == DragDropEffects.None || destination is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var sourcePaths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var owner = _viewModel.Context.OwnerHandle;

        var result = effects == DragDropEffects.Copy
            ? FileOperationService.Copy(sourcePaths, destination, owner)
            : FileOperationService.Move(sourcePaths, destination, owner);

        _viewModel.Context.ReportFileOperation(result);
        e.Effects = result.Succeeded ? effects : DragDropEffects.None;
        _ = _viewModel.RefreshAsync(); // the folder on screen may be where the files came from or went to
    }

    // Same rules as dropping into the file list (Ctrl = copy, Shift = move, otherwise move on the same
    // drive and copy across drives), with the tab's folder as the destination. Tabs on This PC, a hub or
    // a locked folder that would ask for its password take no drops.
    private DragDropEffects TabDropEffects(DragEventArgs e, ExplorerTab tab, out string? destination)
    {
        destination = null;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } sourcePaths)
            return DragDropEffects.None;

        var folder = tab.Location;
        if (folder.Length == 0 ||
            folder.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(folder) ||
            WouldAskForPassword(folder))
            return DragDropEffects.None;

        destination = folder;
        return FileDropEffects(sourcePaths, folder, e.KeyStates);
    }

    private void ShowTabDropTarget(Border? border)
    {
        if (ReferenceEquals(_tabDropBorder, border))
            return;

        _tabDropBorder?.ClearValue(Border.BorderBrushProperty); // back to what the tab's own style says
        _tabDropBorder = border;

        if (border is not null)
            border.BorderBrush = ThemeService.Accent;
    }

    // Called for every DragOver on a tab (Windows repeats it while the pointer rests there). The timer
    // only switches if the files were still over the same tab a moment before it fired.
    private void WatchTabHover(ExplorerTab tab)
    {
        _tabHoverSeen = DateTime.UtcNow;

        if (ReferenceEquals(tab, _tabHoverTarget))
            return;

        StopTabHover();

        // Never switch to a tab that would put a password prompt in the middle of a drag.
        if (ReferenceEquals(tab, Tabs.Active) || WouldAskForPassword(tab.Location))
            return;

        _tabHoverTarget = tab;
        _tabHoverTimer ??= CreateTabHoverTimer();
        _tabHoverTimer.Start();
    }

    private DispatcherTimer CreateTabHoverTimer()
    {
        var timer = new DispatcherTimer { Interval = TabHoverDelay };
        timer.Tick += (_, _) =>
        {
            var target = _tabHoverTarget;
            var stillThere = DateTime.UtcNow - _tabHoverSeen < TimeSpan.FromMilliseconds(250);
            StopTabHover();

            if (target is not null && stillThere && !WouldAskForPassword(target.Location))
                Tabs.Activate(target);
        };
        return timer;
    }

    private void StopTabHover()
    {
        _tabHoverTimer?.Stop();
        _tabHoverTarget = null;
    }

    // ------------------------------------------------------------------ used from outside the window

    // "Open in Clearspace" in Explorer while this window is already open: the folder gets a tab of its
    // own (or the tab that already shows it comes to the front) instead of replacing what you were doing.
    internal void OpenFolder(string path)
    {
        var target = NavigationService.Normalize(path);
        var existing = Tabs.Items.FirstOrDefault(tab => string.Equals(tab.Location, target, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
            Tabs.Open(target);
        else if (Tabs.Activate(existing))
            _ = _viewModel.RefreshAsync();
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
            return null;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;

            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }

        return null;
    }
}
