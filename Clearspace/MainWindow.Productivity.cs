// Clearspace | NEW (productivity): the window's side of the Productivity page and tab groups.
//  - The page (ProductivityView.cs) is shown over the file list while the tab is at clearspace://productivity
//    (the Productivity row at the top of the sidebar), so Back, tabs and the address bar work as for any place.
//  - A tab group is a saved set of tabs (Services/ProductivityStore.cs).
//  - "Save tabs as a group" (Ctrl+Shift+G, the tab bar's menu, or the page's button) saves this window's tabs.
//  - Every window writes its tabs as "the last session" a moment after each change and when it closes.
//
// CHANGED (groups add, not replace): opening a group ADDS its tabs at the end of the tab bar, together. Tabs
// you have open stay open; only a tab showing the same folder as one of the group's tabs makes way for it
// (to "Reopen closed tab"), so nothing is open twice. "Reopen last session" works the same way.
// CHANGED (groups stay as saved): a group never changes by itself any more. It changes only when you edit it
// (on the page), add or remove a tab with the tab menu, or choose "Save these tabs to the group", which
// writes the group's tabs as they are now over the saved ones. (It used to follow its window: "keep up to
// date" is gone.)
// Each tab remembers the group it came from (ExplorerTab.GroupId). CHANGED (group capsule): a group's tabs sit
// in one tinted capsule with the group's name at its start; the name collapses and expands the group.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

public partial class MainWindow
{
    private DispatcherTimer? _tabsSettleTimer;   // one session save a moment after a burst of tab changes
    private ProductivityView? _productivityView;
    private string? _editGroupOnPage;            // a group the page should open for editing when it is next shown
    // REMOVED (groups add, not replace): _tabGroupId, the one group a window had open. Each tab now knows its own.

    // Called once from the constructor.
    private void InitializeProductivity()
    {
        Tabs.Changed += (_, _) => { TabsChangedSoon(); ShowTabGroups(); };
        _viewModel.Navigation.Navigated += (_, _) => { TabsChangedSoon(); ShowTabGroups(); };
        ProductivityStore.GroupsChanged += OnTabGroupsChanged;
        Loaded += (_, _) => ShowTabGroups(); // a tab moved here from another window brings its group with it
        Closing += (_, _) => SaveSession(now: true);
        Closed += (_, _) =>
        {
            ProductivityStore.GroupsChanged -= OnTabGroupsChanged;
            _tabsSettleTimer?.Stop();
            _productivityView?.Dispose();
        };

        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPath))
                UpdateProductivityPage();
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.G && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                SaveTabsAsGroup();
                e.Handled = true;
            }
        };
    }

    // ---- the page

    private void UpdateProductivityPage()
    {
        if (_viewModel.IsProductivityPage)
        {
            if (_productivityView is null)
            {
                _productivityView = new ProductivityView(new ProductivityActions(
                    Navigate: path => _viewModel.Navigation.Navigate(path),
                    OpenGroup: id => OpenTabGroup(id),
                    OpenGroupInNewWindow: OpenTabGroupInNewWindow,
                    SaveOpenTabs: () => SaveTabsAsGroup(),
                    OpenTabCount: () => ProductivityStore.Clean(Tabs.Locations).Count(),
                    ReopenSession: ReopenLastSession,
                    CurrentFolder: CurrentFolderForGroup,
                    PickFolder: PickFolderForGroup,
                    OpenFile: OpenRecentFile,
                    ShowInFolder: RevealFile,
                    Report: message => _viewModel.ReportStatus(message)));
                ProductivityHost.Content = _productivityView;
            }

            ProductivityHost.Visibility = Visibility.Visible;
            _productivityView.Show(_editGroupOnPage);
            _editGroupOnPage = null;
        }
        else if (_productivityView is not null)
        {
            ProductivityHost.Visibility = Visibility.Collapsed;
            ProductivityHost.Content = null;
            _productivityView.Dispose();
            _productivityView = null;
        }
    }

    // The folder a tab in this window is showing, for "Add the folder on screen" in a group being edited.
    private string? CurrentFolderForGroup()
        => Tabs.Locations.LastOrDefault(location => !location.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase));

    // "Add a folder…" in a group being edited: Clearspace's own folder chooser.
    private string? PickFolderForGroup(string groupName)
    {
        var start = CurrentFolderForGroup() ?? MainViewModel.LibraryRoot;
        var picker = new FolderPickerWindow(new FolderPickerOptions(
            $"Add a tab to {groupName}",
            "Open the folder the tab should show, then choose Add.",
            "Add",
            System.IO.Directory.Exists(start) ? start : MainViewModel.LibraryRoot)
        {
            Places = [.. _viewModel.Sidebar],
            ShowHidden = _viewModel.ShowHiddenItems
        })
        {
            Owner = this
        };

        return picker.ShowDialog() == true ? picker.Chosen : null;
    }

    private void OpenRecentFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true })?.Dispose();
            RecentItems.RecordFile(path);
        }
        catch (Exception error)
        {
            _viewModel.ReportStatus($"Couldn't open {System.IO.Path.GetFileName(path)}: {error.Message}");
        }
    }

    // ---- tab groups

    // CHANGED (groups add, not replace): adds the group's tabs; see the top of this file.
    internal void OpenTabGroup(string id)
    {
        if (ProductivityStore.Find(id) is not { } group)
            return;

        if (group.Tabs.Count == 0)
        {
            _viewModel.ReportStatus($"{group.Name} has no tabs yet. Add some on the Productivity page.");
            return;
        }

        var before = Tabs.Count;
        if (Tabs.OpenTogether(group.Tabs, 0, group.Id) is null)
            return;

        ProductivityStore.MarkOpened(group.Id);
        var replaced = before + group.Tabs.Count - Tabs.Count;
        _viewModel.ReportStatus(replaced > 0
            ? $"Opened {group.Name}. {replaced} tab{(replaced == 1 ? " that was" : "s that were")} already open moved into it."
            : $"Opened {group.Name}.");
    }

    private void OpenTabGroupInNewWindow(string id)
    {
        if (ProductivityStore.Find(id) is not { Tabs.Count: > 0 } group)
            return;

        // The new window starts on the group's first place; opening the group there makes that tab make way
        // for the group's own, so the window ends up with exactly the group's tabs.
        var window = new MainWindow(group.Tabs[0]);
        Place(window, at: null);
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => window.OpenTabGroup(id));
        window.Show();
    }

    // Saves this window's tabs as a new group, and marks them as its tabs. From the page, the new group opens
    // for editing so it can be named; from the tab bar or Ctrl+Shift+G, the status line says where.
    internal void SaveTabsAsGroup()
    {
        var tabs = Tabs.Items.Where(tab => ProductivityStore.Clean([tab.Location]).Any()).ToList();
        if (tabs.Count == 0)
        {
            _viewModel.ReportStatus("Open the folders you want in the group first, each in its own tab.");
            return;
        }

        var first = ExplorerTab.DefaultTitle(tabs[0].Location);
        var name = tabs.Count == 1 ? first : $"{first} and {tabs.Count - 1} more";
        var group = ProductivityStore.Add(name, tabs.Select(tab => tab.Location));
        foreach (var tab in tabs)
            tab.GroupId = group.Id;
        ShowTabGroups();

        if (_viewModel.IsProductivityPage && _productivityView is not null)
            _productivityView.Show(group.Id);
        else
            _viewModel.ReportStatus($"Saved {tabs.Count} tab{(tabs.Count == 1 ? "" : "s")} as \"{group.Name}\". Rename or change it on the Productivity page.");
    }

    // NEW (groups stay as saved): the one way a group takes the tabs as they are now - on request.
    private void SaveGroupTabsOver(string id)
    {
        if (ProductivityStore.Find(id) is not { } group)
            return;

        var places = ProductivityStore.Clean(Tabs.InGroup(id).Select(tab => tab.Location)).ToList();
        if (places.Count == 0)
            return;

        ProductivityStore.Update(id, saved => saved.Tabs = places);
        _viewModel.ReportStatus($"Saved {places.Count} tab{(places.Count == 1 ? "" : "s")} to {group.Name}.");
    }

    // Opens the Productivity page in a tab of its own with a group open for editing (the tab-bar label's
    // "Edit…", the sidebar's "Edit…").
    private void EditTabGroup(string id)
    {
        _editGroupOnPage = id;
        if (_viewModel.IsProductivityPage)
        {
            UpdateProductivityPage();
            return;
        }

        var existing = Tabs.Items.FirstOrDefault(tab => tab.Location.Equals(ExplorerLocations.ProductivityPath, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            Tabs.Open(ExplorerLocations.ProductivityPath);
        else
            Tabs.Activate(existing);
    }

    // CHANGED (groups add, not replace): adds last session's tabs; tabs open now stay.
    private void ReopenLastSession()
    {
        var session = ProductivityStore.PreviousSession;
        if (session.Tabs.Count == 0)
        {
            _viewModel.ReportStatus("There is no earlier session to bring back.");
            return;
        }

        if (Tabs.OpenTogether(session.Tabs, session.Active) is not null)
            _viewModel.ReportStatus($"Brought back {session.Tabs.Count} tab{(session.Tabs.Count == 1 ? "" : "s")} from last time.");
    }

    // ---- the last session

    private void TabsChangedSoon()
    {
        _tabsSettleTimer ??= CreateTabsSettleTimer();
        _tabsSettleTimer.Stop();
        _tabsSettleTimer.Start();
    }

    private DispatcherTimer CreateTabsSettleTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SaveSession(now: false);
            // REMOVED (groups stay as saved): making the open group follow these tabs.
        };
        return timer;
    }

    private void SaveSession(bool now)
        => ProductivityStore.SaveSession(Tabs.Locations, Math.Max(0, Tabs.Items.IndexOf(Tabs.Active)), now);

    private void OnTabGroupsChanged() => Dispatcher.BeginInvoke(new Action(ShowTabGroups));

    // ---- how groups look on the tab bar
    // CHANGED (group capsule): each run of a group's tabs sits in one tinted capsule with the group's name at
    // its start (see the tab template in MainWindow.xaml). Clicking the name collapses the group to the name
    // and a count, and clicking again shows its tabs; right-click for the group's menu. A collapsed group
    // opens again by itself when one of its tabs becomes the tab you are on (Ctrl+Tab, say).

    private readonly HashSet<string> _collapsedGroups = new(StringComparer.Ordinal);

    private void ShowTabGroups()
    {
        var groups = ProductivityStore.Groups.ToDictionary(group => group.Id);
        var items = Tabs.Items;

        // A deleted group lets go of its tabs.
        foreach (var tab in items)
            if (tab.GroupId is { } id && !groups.ContainsKey(id))
                tab.GroupId = null;

        // The tab you are on is never hidden inside a collapsed group.
        if (Tabs.Active.GroupId is { } activeGroup)
            _collapsedGroups.Remove(activeGroup);

        for (var i = 0; i < items.Count; i++)
        {
            var tab = items[i];
            if (tab.GroupId is not { } id)
            {
                tab.SetGroupLook(null);
                continue;
            }

            // A run: neighbouring tabs of the same group. A group whose tabs were dragged apart has one capsule per run.
            var start = i == 0 || items[i - 1].GroupId != id;
            var end = i == items.Count - 1 || items[i + 1].GroupId != id;
            var runStart = i;
            while (runStart > 0 && items[runStart - 1].GroupId == id) runStart--;
            var runEnd = i;
            while (runEnd < items.Count - 1 && items[runEnd + 1].GroupId == id) runEnd++;

            var group = groups[id];
            var collapsed = _collapsedGroups.Contains(id);
            var count = runEnd - runStart + 1;
            tab.SetGroupLook(new GroupLook(group.Color, group.Name, start, end, collapsed, count,
                start ? ChipWidthFor(group.Name, collapsed, count) : 0));
        }
    }

    // How wide a group's name is drawn: the arrow, the name (at most 160), and the count when collapsed.
    private double ChipWidthFor(string name, bool collapsed, int count)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var font = TryFindResource("UIFont") as FontFamily ?? new FontFamily("Segoe UI");
        double TextWidth(string text, double size, FontWeight weight) => new FormattedText(
            text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;

        var width = 8 + 9 + 6 + Math.Min(160, Math.Ceiling(TextWidth(name, 12, FontWeights.SemiBold))) + 10;
        if (collapsed)
            width += 7 + Math.Max(18, Math.Ceiling(TextWidth(count.ToString(), 10.5, FontWeights.Bold)) + 10);
        return Math.Ceiling(width);
    }

    private static SolidColorBrush TryBrush(string hex)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Gray;
        }
    }

    // NEW (group capsule): a click on a group's name collapses or expands it.
    private void OnGroupChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ExplorerTab { GroupId: { } id } })
            return;

        e.Handled = true;
        if (_collapsedGroups.Remove(id))
        {
            ShowTabGroups();
            return;
        }

        // Collapsing the group of the tab you are on: move to the nearest tab outside it first.
        if (Tabs.Active.GroupId == id)
        {
            var at = Tabs.Items.IndexOf(Tabs.Active);
            var outside = Tabs.Items
                .Select((tab, index) => (tab, distance: Math.Abs(index - at)))
                .Where(pair => pair.tab.GroupId != id)
                .OrderBy(pair => pair.distance)
                .Select(pair => pair.tab)
                .FirstOrDefault();

            if (outside is null || !Tabs.Activate(outside))
            {
                _viewModel.ReportStatus("Open a tab outside the group first; a group can't hide the only tabs you have.");
                return;
            }
        }

        _collapsedGroups.Add(id);
        ShowTabGroups();
    }

    // NEW (group capsule): right-click on a group's name. Was the click on the one label before the tabs.
    // Edit, Save these tabs to the group, Close the group's tabs, Ungroup.
    private void OnGroupChipRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ExplorerTab { GroupId: { } id } } chip ||
            ProductivityStore.Find(id) is not { } group)
            return;

        var inGroup = Tabs.InGroup(id);
        var menu = new ContextMenu { PlacementTarget = chip };
        menu.Items.Add(Entry(_collapsedGroups.Contains(id) ? "Show its tabs" : "Hide its tabs", null, () => OnGroupChipClick(chip, e)));
        menu.Items.Add(Entry("Edit this group…", "\uE70F", () => EditTabGroup(id)));
        menu.Items.Add(Entry($"Save these {inGroup.Count} tabs to the group", "\uE74E", () => SaveGroupTabsOver(id)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("Close the group's tabs", "\uE711", () =>
        {
            foreach (var tab in Tabs.InGroup(id).Reverse())
                Tabs.Close(tab);
            _collapsedGroups.Remove(id);
        }));
        menu.Items.Add(Entry("Ungroup these tabs", null, () =>
        {
            foreach (var tab in Tabs.InGroup(id))
                tab.GroupId = null;
            _collapsedGroups.Remove(id);
            ShowTabGroups();
        }));

        menu.IsOpen = true;
        e.Handled = true;
    }

    // ---- the tab bar's menu

    // The entries OnTabRightClick adds for tab groups.
    private void AddTabGroupEntries(ContextMenu menu, ExplorerTab tab)
    {
        menu.Items.Add(new Separator());

        var save = Entry("Save tabs as a group", "", SaveTabsAsGroup, "Ctrl+Shift+G");
        save.IsEnabled = ProductivityStore.Clean(Tabs.Locations).Any();
        menu.Items.Add(save);

        var groups = ProductivityStore.Groups;

        // Adds the tab's place to the saved group (an edit you asked for), and the tab joins it.
        var addTo = new MenuItem { Header = "Add this tab to a group", IsEnabled = groups.Count > 0 && tab.Location.Length > 0 };
        foreach (var group in groups)
        {
            var id = group.Id;
            var item = new MenuItem { Header = group.Name.Replace("_", "__"), Icon = Dot(group.Color) };
            item.Click += (_, _) =>
            {
                ProductivityStore.Update(id, saved =>
                {
                    if (!saved.Tabs.Contains(tab.Location, StringComparer.OrdinalIgnoreCase))
                        saved.Tabs.Add(tab.Location);
                });
                tab.GroupId = id;
                ShowTabGroups();
                _viewModel.ReportStatus($"Added {tab.Title} to {group.Name}.");
            };
            addTo.Items.Add(item);
        }
        menu.Items.Add(addTo);

        // NEW (groups stay as saved): takes the tab's place out of the saved group too.
        if (tab.GroupId is { } current && groups.FirstOrDefault(group => group.Id == current) is { } own)
        {
            menu.Items.Add(Entry($"Remove from {own.Name.Replace("_", "__")}", null, () =>
            {
                ProductivityStore.Update(current, saved =>
                {
                    var at = saved.Tabs.FindIndex(place => place.Equals(tab.Location, StringComparison.OrdinalIgnoreCase));
                    if (at >= 0)
                        saved.Tabs.RemoveAt(at);
                });
                tab.GroupId = null;
                ShowTabGroups();
            }));
        }

        var open = new MenuItem { Header = "Open a tab group", IsEnabled = groups.Count > 0 };
        foreach (var group in groups)
        {
            var id = group.Id;
            var item = new MenuItem { Header = group.Name.Replace("_", "__"), Icon = Dot(group.Color) };
            item.Click += (_, _) => OpenTabGroup(id);
            open.Items.Add(item);
        }
        menu.Items.Add(open);
    }

    private static Border Dot(string hex) => new()
    {
        Width = 9,
        Height = 9,
        CornerRadius = new CornerRadius(3),
        Background = TryBrush(hex),
        VerticalAlignment = VerticalAlignment.Center
    };

    // ---- the sidebar's menu on a saved group

    private void OnSidebarOpenTabGroup(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: SidebarEntry { TabGroupId: { } id } })
            OpenTabGroup(id);
    }

    private void OnSidebarOpenTabGroupInNewWindow(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: SidebarEntry { TabGroupId: { } id } })
            OpenTabGroupInNewWindow(id);
    }

    private void OnSidebarEditTabGroup(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: SidebarEntry { TabGroupId: { } id } })
            EditTabGroup(id);
    }
}
