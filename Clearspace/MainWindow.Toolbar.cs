// Clearspace | NEW (toolbar fit): the toolbar fits any window width without cutting anything off.
//
// The buttons on the right are an Auto column, so they used to take all the room they wanted and the address
// bar (the * column) got what was left, which in a narrow window was nothing; past that, the buttons
// themselves were clipped at the window's edge. Now, whenever the toolbar or its buttons change size, the
// toolbar is measured and gives up room one step at a time until the address bar has a comfortable width:
//   1. the words beside Disk usage and Duplicates (icons and tooltips stay)
//   2. the words beside Everywhere and File contents
//   3. the search box narrows (200 -> 150)
//   4. New folder and Open in Terminal move into the More (…) menu
//   5. the view buttons (columns, details/tiles, preview pane, Inspector, Shelf, hidden items) move there too
//   6. then Disk usage and Duplicates
// The search box, the search scope, the folder type, Settings and the More button itself always stay. Each
// step is undone as soon as there is room again. Groups are hidden through their own panels (ToolsGroup,
// ViewGroup, NewGroup in MainWindow.xaml), so the buttons' own Visibility bindings are left alone.
//
// FIXED (toolbar fit freeze): resizing the window could lock up the whole computer. Measuring the buttons
// after hiding or showing some of them gave their width from BEFORE the change (WPF only measures again
// what has been marked as changed, and a label several levels down doesn't mark the toolbar), so every fit
// came out the opposite of the last one: everything shown made it too wide, everything hidden made it fit,
// and back again, for ever. Each round laid out the whole window, and the fits ran ahead of mouse and
// keyboard input. Now:
//   - everything a step changes is marked as changed all the way up to the toolbar (MarkChanged), so the
//     measurement is of what will really be on screen;
//   - the fit waits behind input (DispatcherPriority.Input, was Loaded), so the window always answers;
//   - and as a safety net, if the toolbar keeps refitting at the same width (more than a few times in half a
//     second), it stops until the width changes.
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Clearspace;

public partial class MainWindow
{
    // What the address bar should have before any button gives up its room.
    private const double AddressRoom = 260;

    private const int FitSteps = 6;
    private bool _toolbarFitQueued;

    // NEW (toolbar fit freeze): the safety net against refitting in circles.
    private double _lastFitWidth = -1;
    private int _fitsAtThisWidth;
    private long _fitBurstStarted;
    private const int MostFitsPerBurst = 4;
    private static readonly TimeSpan FitBurst = TimeSpan.FromMilliseconds(500);

    private void OnToolbarActionsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // NEW (toolbar fit freeze): only the width matters, and only a real change of it.
        if (e.WidthChanged)
            QueueToolbarFit();
    }

    // Once per pass at most: several size changes in a row (a resize drag) make one fit.
    private void QueueToolbarFit()
    {
        if (_toolbarFitQueued)
            return;

        _toolbarFitQueued = true;
        // CHANGED (toolbar fit freeze): Input, not Loaded, so it never runs ahead of the mouse and keyboard.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            _toolbarFitQueued = false;
            FitToolbar();
        });
    }

    private void FitToolbar()
    {
        var available = ToolbarRow.ActualWidth;
        if (available <= 0)
            return;

        // NEW (toolbar fit freeze): at the same width, a few fits in quick succession are fine (a button came
        // or went); more than that means it is going round in circles, so leave the toolbar as it is.
        var now = Stopwatch.GetTimestamp();
        if (available != _lastFitWidth || Stopwatch.GetElapsedTime(_fitBurstStarted, now) > FitBurst)
        {
            _lastFitWidth = available;
            _fitsAtThisWidth = 0;
            _fitBurstStarted = now;
        }
        if (++_fitsAtThisWidth > MostFitsPerBurst)
            return;

        // Find the first step at which everything fits, trying them from "everything shown" up. The result
        // depends only on the width and on which buttons are in use.
        var step = 0;
        ApplyToolbarStep(0);
        while (step < FitSteps && !ToolbarFits(available))
            ApplyToolbarStep(++step);

    }

    private bool ToolbarFits(double available)
    {
        var unlimited = new Size(double.PositiveInfinity, double.PositiveInfinity);
        NavButtons.Measure(unlimited);
        ToolbarActions.Measure(unlimited);

        var need = NavButtons.DesiredSize.Width
                   + AddressRoom + AddressPill.Margin.Left + AddressPill.Margin.Right
                   + ToolbarActions.DesiredSize.Width;
        return need <= available;
    }

    // Steps 1..n are cumulative: step 3 means 1, 2 and 3 have been given up. Step 0 shows everything.
    private void ApplyToolbarStep(int step)
    {
        static Visibility ShownBelow(int step, int at) => step >= at ? Visibility.Collapsed : Visibility.Visible;

        Set(DiskUsageLabel, ShownBelow(step, 1));
        Set(DuplicatesLabel, ShownBelow(step, 1));
        Set(SearchEverywhereLabel, ShownBelow(step, 2));
        Set(SearchContentsLabel, ShownBelow(step, 2));
        Set(NewGroup, ShownBelow(step, 4));
        Set(ViewGroup, ShownBelow(step, 5));
        Set(ToolsGroup, ShownBelow(step, 6));
        Set(ToolbarMoreButton, step >= 4 ? Visibility.Visible : Visibility.Collapsed);

        var searchWidth = step >= 3 ? 150 : 200;
        if (SearchPill.Width != searchWidth)
        {
            SearchPill.Width = searchWidth;
            MarkChanged(SearchPill);
        }
    }

    // NEW (toolbar fit freeze): changes a part's Visibility only when it is different, and marks it as changed.
    private void Set(UIElement element, Visibility visibility)
    {
        if (element.Visibility == visibility)
            return;

        element.Visibility = visibility;
        MarkChanged(element);
    }

    // NEW (toolbar fit freeze): marks a part and every panel it is in, up to the toolbar, as needing to be
    // measured again, so ToolbarFits measures the toolbar as it is now and not as it was.
    private void MarkChanged(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
        {
            if (node is UIElement part)
                part.InvalidateMeasure();
            if (ReferenceEquals(node, ToolbarActions))
                break;
        }
    }

    // The More button: what the hidden groups would have offered, in the same order as on the toolbar.
    private void OnToolbarMore(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ToolbarMoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        if (ToolsGroup.Visibility != Visibility.Visible)
        {
            // The window as sender: both open on the folder you are in (a MenuItem sender means "the selected folder").
            menu.Items.Add(Entry("Disk usage", "", () => OnDiskUsage(this, new RoutedEventArgs())));
            menu.Items.Add(Entry("Find duplicates", "", () => OnFindDuplicates(this, new RoutedEventArgs())));
        }

        if (ViewGroup.Visibility != Visibility.Visible)
        {
            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());

            if (ColumnsButton.Visibility == Visibility.Visible && ColumnsButton.ContextMenu is { } columns)
                menu.Items.Add(Entry("Choose columns", "", () =>
                {
                    columns.PlacementTarget = ToolbarMoreButton; // its own button is hidden
                    columns.IsOpen = true;
                }));
            menu.Items.Add(Entry(_viewModel.IsGrid ? "Show as details" : "Show as tiles", _viewModel.IsGrid ? "" : "",
                () => _viewModel.ToggleLayout(), "Ctrl+Shift+V"));
            menu.Items.Add(Toggle("Preview pane", "", PreviewToggle.IsChecked == true, () => _viewModel.PreviewPaneCommand.Execute(null), "Alt+P"));
            menu.Items.Add(Toggle("Inspector", "", InspectorToggle.IsChecked == true, () => _viewModel.InspectorCommand.Execute(null), "Alt+I"));
            menu.Items.Add(Toggle("Shelf", "", ShelfToggle.IsChecked == true, () => _viewModel.ShelfCommand.Execute(null), "Alt+S"));
            menu.Items.Add(Toggle("Show hidden items", "", _viewModel.ShowHiddenItems, () => _viewModel.ShowHiddenItems = !_viewModel.ShowHiddenItems));
        }

        if (NewGroup.Visibility != Visibility.Visible)
        {
            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());

            var newFolder = Entry("New folder", "", () => _viewModel.NewFolderCommand.Execute(null), "Ctrl+Shift+N");
            newFolder.IsEnabled = _viewModel.NewFolderCommand.CanExecute(null);
            menu.Items.Add(newFolder);
            var terminal = Entry("Open in Terminal", "", () => _viewModel.TerminalCommand.Execute(null), "Ctrl+`");
            terminal.IsEnabled = _viewModel.TerminalCommand.CanExecute(null);
            menu.Items.Add(terminal);
        }

        menu.IsOpen = true;
    }

    // A panel or setting that is on or off: ticked while it is on, like the lit button it stands in for.
    private MenuItem Toggle(string label, string glyph, bool on, Action action, string? shortcut = null)
    {
        var item = Entry(label, glyph, action, shortcut);
        item.IsChecked = on;
        return item;
    }
}
