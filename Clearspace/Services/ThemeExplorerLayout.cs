using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace Clearspace.Services;

// Both the actual Explorer and studio's isolated Explorer use this layout function.
// CHANGED (complex themes): rewritten so more of the window can move, and so it can be called again at any
// time (a theme change, the preview pane opening) and always lands on the same result.
//   - Sidebar left or right, or hidden.                         Explorer.SidebarOnRight / SidebarVisibility
//   - Preview pane right or left of the file list.             Explorer.PreviewOnLeft
//   - Toolbar (with the tabs) at the top or the bottom.         Explorer.ToolbarBottom
//   - Tabs above or below the toolbar, or hidden.               Explorer.TabsBelowToolbar / TabsVisibility
//   - Status bar under or over the file list, or hidden.        Explorer.StatusBarTop / StatusBarVisibility
//   - NEW (shelf and inspector): the column holding the Inspector and the Shelf goes on the side away from the
//     sidebar, at the window's edge (it is shown and hidden by MainWindow.Dock.cs, not by the theme).
// How: rather than moving elements between numbered rows and columns (and keeping every number in step),
// the row and column definitions themselves are put in a new order. Each element stays with the definition it
// was in, so whatever else sits in a column (the rename and category panels, the band-select layer that
// MainWindow.BandSelect.cs adds at run time) moves with it. The original order is remembered per grid.
internal static class ThemeExplorerLayout
{
    private static readonly ConditionalWeakTable<Grid, RowDefinition[]> OriginalRows = new();
    private static readonly ConditionalWeakTable<Grid, ColumnDefinition[]> OriginalColumns = new();
    private const double Gap = 14;

    internal static void Apply(FrameworkElement root)
    {
        if (root.FindName("BrowserBody") is not Grid body || root.FindName("ExplorerShell") is not Grid shell ||
            root.FindName("NavigationArea") is not Grid navigation || root.FindName("SidebarScroll") is not ScrollViewer sidebar ||
            root.FindName("ContentCard") is not Border files || root.FindName("SidebarSplitter") is not GridSplitter splitter) return;
        // NEW (custom layouts): a theme with its own layout.json arranges the window itself (ThemeLayoutEngine).
        if (ThemeLayoutEngine.IsCustom(root)) return;
        bool On(string key) => root.TryFindResource(key) is Visibility.Visible;
        var show = On("Explorer.SidebarVisibility");
        var right = On("Explorer.SidebarOnRight");
        var bottom = On("Explorer.ToolbarBottom");
        var previewLeft = On("Explorer.PreviewOnLeft");
        var tabsBelow = On("Explorer.TabsBelowToolbar");
        var width = root.TryFindResource("Explorer.SidebarWidth") is Thickness t && double.IsFinite(t.Left) ? Math.Clamp(t.Left, 170, 400) : 228;

        // Window rows: navigation, files, function-key bar.
        if (shell.RowDefinitions.Count >= 3) Order(shell, OriginalRows, g => g.RowDefinitions, bottom ? new[] { 1, 0, 2 } : new[] { 0, 1, 2 });
        // Navigation rows: tabs, toolbar.
        if (navigation.RowDefinitions.Count >= 2) Order(navigation, OriginalRows, g => g.RowDefinitions, tabsBelow ? new[] { 1, 0 } : new[] { 0, 1 });

        // Body columns, as written in MainWindow.xaml: 0 sidebar, 1 its splitter, 2 file list, 3 preview splitter, 4 preview,
        // CHANGED (shelf and inspector): 5 the dock's splitter, 6 the dock.
        var columns = OriginalColumns.GetValue(body, g => g.ColumnDefinitions.ToArray());
        if (columns.Length < 5) return;
        var preview = root.FindName("PreviewCard") as FrameworkElement;
        var previewShown = preview is { Visibility: Visibility.Visible };
        // CHANGED (review): a hidden preview pane's two 0-wide columns go where no splitter touches them. A
        // GridSplitter resizes the first of its two neighbours that is not a star column, so with the hidden pane
        // between the file list and the dock (or the sidebar on the right) dragging that splitter opened an empty
        // gap instead of resizing the dock (or the sidebar).
        var middle = !previewShown ? new[] { 2 } : previewLeft ? new[] { 4, 3, 2 } : new[] { 2, 3, 4 };
        var spare = previewShown ? Array.Empty<int>() : new[] { 3, 4 };
        var hasDock = columns.Length >= 7;
        var order = right
            ? (hasDock ? new[] { 6, 5 } : Array.Empty<int>()).Concat(spare).Concat(middle).Concat(new[] { 1, 0 }).ToArray()
            : new[] { 0, 1 }.Concat(middle).Concat(hasDock ? new[] { 5, 6 } : Array.Empty<int>()).Concat(spare).ToArray();
        Order(body, OriginalColumns, g => g.ColumnDefinitions, order);

        var sideColumn = columns[0];
        sideColumn.MinWidth = 0; sideColumn.MaxWidth = double.PositiveInfinity;
        sideColumn.Width = new GridLength(show ? width : 0);
        if (show) { sideColumn.MinWidth = 170; sideColumn.MaxWidth = 400; }
        columns[2].Width = new GridLength(1, GridUnitType.Star);
        sidebar.Visibility = splitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        // Gaps: a card at the window's edge keeps 14 from it; between two panes the splitter is the gap.
        var dock = root.FindName("DockCard") as FrameworkElement;   // NEW (shelf and inspector)
        var dockShown = hasDock && dock is { Visibility: Visibility.Visible };
        var visible = order.Where(i => i switch { 0 => show, 2 => true, 4 => previewShown, 6 => dockShown, _ => false }).ToList();
        var top = bottom ? 12 : 0;
        var under = bottom ? 4 : Gap;
        Thickness Edge(int index) => new(visible[0] == index ? Gap : 0, top, visible[^1] == index ? Gap : 0, under);
        files.Margin = Edge(2);
        if (preview is not null && previewShown) preview.Margin = Edge(4);
        if (dock is not null && dockShown) dock.Margin = Edge(6);
        sidebar.Margin = new Thickness(0, top, 0, 0);
        if (root.FindName("SidebarPanel") is FrameworkElement panel)
        {
            panel.Visibility = sidebar.Visibility;
            panel.Margin = right ? new Thickness(2, top, 8, under) : new Thickness(8, top, 2, under);
        }

        // Tabs and toolbar: the gap between them follows which is on top.
        var tabsShown = On("Explorer.TabsVisibility");
        if (root.FindName("TabRow") is FrameworkElement tabs)
        {
            tabs.Visibility = tabsShown ? Visibility.Visible : Visibility.Collapsed;
            tabs.Margin = tabsBelow ? new Thickness(10, 0, 14, 6) : new Thickness(10, 8, 14, 0);
        }
        if (root.FindName("ToolbarRow") is FrameworkElement toolbar)
            toolbar.Margin = tabsBelow && tabsShown ? new Thickness(10, 8, 14, 4) : new Thickness(10, 6, 14, 8);

        // Status bar: under the list (its line on top) or over it (its line underneath), or hidden.
        if (files.Child is Grid area && root.FindName("StatusBar") is Border status && area.RowDefinitions.Count >= 4)
        {
            var statusTop = On("Explorer.StatusBarTop");
            // File area rows: 0 folder headers, 1 the list, 2 the status bar, 3 the music player.
            Order(area, OriginalRows, g => g.RowDefinitions, statusTop ? new[] { 2, 0, 1, 3 } : new[] { 0, 1, 2, 3 });
            status.Visibility = On("Explorer.StatusBarVisibility") ? Visibility.Visible : Visibility.Collapsed;
            status.BorderThickness = statusTop ? new Thickness(0, 0, 0, 1) : new Thickness(0, 1, 0, 0);
        }
    }

    // Puts a grid's row or column definitions in the order given (as indexes into the order they were first
    // seen in), and moves every child along with the definition it was in. A child that spans all of them
    // (the photo viewer) stays at the start.
    private static void Order<TDefinition>(Grid grid, ConditionalWeakTable<Grid, TDefinition[]> originals,
        Func<Grid, IList<TDefinition>> definitionsOf, int[] order) where TDefinition : DefinitionBase
    {
        var definitions = definitionsOf(grid);
        var original = originals.GetValue(grid, g => definitionsOf(g).ToArray());
        if (original.Length != definitions.Count || order.Any(i => i < 0 || i >= original.Length)) return;
        var wanted = order.Select(i => original[i]).ToArray();
        if (definitions.SequenceEqual(wanted)) return;

        var rows = typeof(TDefinition) == typeof(RowDefinition);
        var current = definitions.ToArray();
        var places = grid.Children.Cast<UIElement>().Select(child =>
        {
            var index = rows ? Grid.GetRow(child) : Grid.GetColumn(child);
            var span = rows ? Grid.GetRowSpan(child) : Grid.GetColumnSpan(child);
            return (Child: child, Whole: span >= current.Length, Definition: current[Math.Clamp(index, 0, current.Length - 1)]);
        }).ToArray();

        definitions.Clear();
        foreach (var definition in wanted) definitions.Add(definition);
        foreach (var (child, whole, definition) in places)
        {
            var index = whole ? 0 : definitions.IndexOf(definition);
            if (rows) Grid.SetRow(child, index); else Grid.SetColumn(child, index);
        }
    }
}
