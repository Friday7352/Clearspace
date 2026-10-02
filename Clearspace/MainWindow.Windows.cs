// Clearspace | NEW (new window): more than one main window.
// "Open in new window" (the right-click menus) and Ctrl+N open another MainWindow inside the Clearspace
// that is already running. That is what keeps it cheap: the file index, the remembered folder listings,
// the icon and thumbnail caches, the tag database and the lock agent are all static and exist once per
// process, so a second window adds only its own controls and the folder it is showing. Starting
// Clearspace.exe a second time is different: that is a second process with its own copy of the index.
// What each window owns: its tabs and history, its file list, its sidebar (kept in step with the others
// by SidebarViewModel), its music player and photo viewer.
// NEW (tab to new window): a tab can leave its window for one of its own: drag it off the tab strip and
// let go (MainWindow.Tabs.cs), or "Move to new window" in its right-click menu. See MoveTabToNewWindow.
// LockAgent knows every open window (see LockAgent.Attach) and Clearspace exits when the last one closes,
// unless something unlocked for a visit still has to be locked again.
using System.Windows;
using Clearspace.ViewModels;

namespace Clearspace;

public partial class MainWindow
{
    // How far a new window is shifted from the one it was opened from, so both title bars stay visible.
    private const double CascadeOffset = 32;

    private void InitializeWindows()
    {
        _viewModel.Context.OpenWindow = OpenWindow;
    }

    // Opens another window on a folder (or on one of Clearspace's own pages, like This PC).
    internal void OpenWindow(string path)
    {
        // A locked folder asks for its password here, before anything opens, so cancelling the prompt
        // does not leave an empty window behind. Once it is open, the new window goes straight in.
        if (!_viewModel.Navigation.MayEnter(path))
            return;

        var window = new MainWindow(path);
        Place(window, at: null);
        window.Show();
    }

    // NEW (tab to new window): takes a tab out of this window and gives it a window of its own, with its
    // back / forward list, search, scroll position and selection. `at` is where it was dropped (a point on
    // the screen, in device pixels); without one (the tab menu's "Move to new window") the new window is
    // placed like any other new window. Nothing happens for the only tab: that would just be the window.
    internal void MoveTabToNewWindow(ExplorerTab tab, Point? at = null)
    {
        if (Tabs.Count < 2)
            return;

        // A background tab that sits in a locked folder asks for the password here, as it would if you
        // switched to it; the new window then shows it without asking again.
        if (!ReferenceEquals(tab, Tabs.Active) && !_viewModel.Navigation.MayEnter(tab.Location))
            return;

        // Leaving this window: when it is the tab on screen, switching away from it is what records its
        // scroll position and selection (OnTabDeactivating), so they travel with it.
        if (!Tabs.Detach(tab))
            return;

        var window = new MainWindow(startTab: tab);
        Place(window, at);
        window.Show();
    }

    // CHANGED (tab to new window): the placement, taken out of OpenWindow so both ways of making a window
    // share it. The new window is the size of this one. With a drop point it appears there, the left end of its
    // title bar under the pointer; otherwise one step down and to the right of this window (back at the top left
    // corner of the desktop when that would run off its far edge), and maximized if this one is.
    private void Place(MainWindow window, Point? at)
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            return; // keeps its own default size and position (centre of the screen)

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Width = bounds.Width;
        window.Height = bounds.Height;

        if (at is { } dropped)
        {
            // Screen pixels to WPF units (they differ when Windows' display scale is not 100%).
            var point = PresentationSource.FromVisual(this)?.CompositionTarget is { } target
                ? target.TransformFromDevice.Transform(dropped)
                : dropped;

            window.Left = point.X - 90;
            window.Top = point.Y - 22;
            return;
        }

        var right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        var left = bounds.Left + CascadeOffset;
        var top = bounds.Top + CascadeOffset;

        window.Left = left + bounds.Width <= right ? left : SystemParameters.VirtualScreenLeft + CascadeOffset;
        window.Top = top + bounds.Height <= bottom ? top : SystemParameters.VirtualScreenTop + CascadeOffset;

        if (WindowState == WindowState.Maximized)
            window.WindowState = WindowState.Maximized;
    }

    // NEW (tab to new window): runs in the new window once it has started on the moved tab's folder: what
    // the tab had put aside (search text, scroll position, selection) is put back, the same way it is
    // when you switch to a tab.
    private void ResumeMovedTab(ExplorerTab moved)
    {
        var tab = Tabs.Active;
        tab.SearchText = moved.SearchText;
        tab.ScrollOffset = moved.ScrollOffset;
        tab.SelectedPaths = moved.SelectedPaths;

        _viewModel.SearchText = tab.SearchText;
        OnTabActivated(this, tab);
    }

    private void OnSidebarOpenInNewWindow(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { DataContext: SidebarEntry entry } && CanBrowse(entry.Path))
            OpenWindow(entry.Path);
    }
}
