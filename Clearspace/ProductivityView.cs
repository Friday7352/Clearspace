// Clearspace | NEW (productivity): the Productivity page - your saved tab groups, what you opened lately, and
// the folders you go to most and last. Shown over the file list (MainWindow.Productivity.cs) while a tab is
// at clearspace://productivity.
//  - Tab groups: a card for each saved group with its tabs, Open (adds its tabs), New window and Edit. Edit
//    turns the card into an editor: rename, pick a colour, see every tab, remove one, move one up or down,
//    and add a folder (Clearspace's own chooser) or the folder on screen. The ⋯ button has Rename, Colour and
//    Delete. CHANGED (groups stay as saved): "keep up to date" is gone; a group changes only when edited.
//  - Recently opened: the newest files from Windows' recent list (Services/RecentItems.cs), by day; a click
//    opens one, right-click for Show in its folder. "See all" goes to the full Recent page.
//  - Folders: most visited in the last two weeks and last visited (Services/ProductivityStore.cs).
// Built in code from the shared styles in App.xaml and the theme's colours, so it follows every theme.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Clearspace.Models;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

/// <summary>What the page asks the window to do.</summary>
internal sealed record ProductivityActions(
    Action<string> Navigate,
    Action<string> OpenGroup,
    Action<string> OpenGroupInNewWindow,
    Action SaveOpenTabs,
    Func<int> OpenTabCount,
    Action ReopenSession,
    Func<string?> CurrentFolder,
    Func<string, string?> PickFolder,
    Action<string> OpenFile,
    Action<string> ShowInFolder,
    Action<string> Report);

internal sealed class ProductivityView : UserControl, IDisposable
{
    private const int TabsShownOnCard = 6;
    private const int RecentFilesShown = 10;
    private const double CardWidth = 300;

    private readonly ProductivityActions _actions;
    private readonly WrapPanel _groups = new();
    private readonly TextBlock _groupCount;
    private readonly StackPanel _recent = new();
    private readonly StackPanel _folders = new();
    private readonly Button _reopen;
    private readonly Button _save;
    private string? _editing;          // the group whose card is open as an editor
    private CancellationTokenSource? _recentLoad;
    private bool _disposed;

    internal ProductivityView(ProductivityActions actions)
    {
        _actions = actions;
        UseLayoutRounding = true;
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Part.Files.Fill");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(FontFamilyProperty, "UIFont");

        // ---- header
        var title = new TextBlock { Text = "Productivity", FontSize = 30, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        var subtitle = Muted("Your saved tab groups and what you worked on lately.");
        subtitle.Margin = new Thickness(0, 4, 0, 0);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        titles.Children.Add(title);
        titles.Children.Add(subtitle);

        _reopen = TextButton("Reopen last session", primary: false);
        _reopen.Margin = new Thickness(0, 0, 8, 0);
        _reopen.Click += (_, _) => _actions.ReopenSession();
        _save = TextButton("Save open tabs as a group", primary: true);
        _save.Click += (_, _) => _actions.SaveOpenTabs();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        buttons.Children.Add(_reopen);
        buttons.Children.Add(_save);
        DockPanel.SetDock(buttons, Dock.Right);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 24) };
        header.Children.Add(buttons);
        header.Children.Add(titles);

        // ---- tab groups
        _groupCount = Faint(string.Empty);
        _groupCount.Margin = new Thickness(10, 0, 0, 2);
        _groupCount.VerticalAlignment = VerticalAlignment.Bottom;
        var groupsTitle = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        groupsTitle.Children.Add(SectionTitle("Tab groups"));
        groupsTitle.Children.Add(_groupCount);

        // ---- recent files and folders, side by side
        var recentCard = Card(_recent);
        var foldersCard = Card(_folders);
        foldersCard.Width = 340;
        foldersCard.Margin = new Thickness(16, 0, 0, 0);
        foldersCard.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(foldersCard, Dock.Right);
        var lower = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        lower.Children.Add(foldersCard);
        lower.Children.Add(recentCard);

        var page = new StackPanel { Margin = new Thickness(28, 24, 28, 28), MaxWidth = 1240 };
        page.Children.Add(header);
        page.Children.Add(groupsTitle);
        page.Children.Add(_groups);
        page.Children.Add(lower);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = page
        };

        ProductivityStore.GroupsChanged += OnGroupsChanged;
    }

    /// <summary>Reads everything again. With a group id, that group's card opens for editing.</summary>
    internal void Show(string? editGroupId)
    {
        if (editGroupId is not null)
            _editing = editGroupId;

        var session = ProductivityStore.PreviousSession;
        _reopen.IsEnabled = session.Tabs.Count > 0;
        _reopen.ToolTip = session.Tabs.Count > 0
            ? $"{session.Tabs.Count} tab{(session.Tabs.Count == 1 ? "" : "s")} open when Clearspace last closed: {string.Join(", ", session.Tabs.Take(4).Select(ExplorerTab.DefaultTitle))}{(session.Tabs.Count > 4 ? "…" : "")}"
            : "There is no earlier session yet.";

        RenderGroups();
        RenderFolders();
        _ = LoadRecentAsync();
    }

    private void OnGroupsChanged() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!_disposed)
            RenderGroups();
    }));

    public void Dispose()
    {
        _disposed = true;
        ProductivityStore.GroupsChanged -= OnGroupsChanged;
        _recentLoad?.Cancel();
    }

    // ------------------------------------------------------------------ tab groups

    private void RenderGroups()
    {
        var groups = ProductivityStore.Groups;
        _groupCount.Text = groups.Count == 0 ? "none saved yet" : $"{groups.Count} saved";

        var open = _actions.OpenTabCount();
        _save.IsEnabled = open > 0;
        _save.ToolTip = open > 0 ? $"Saves the {open} tab{(open == 1 ? "" : "s")} you have open (Ctrl+Shift+G)" : "Open some folders in tabs first";

        _groups.Children.Clear();
        foreach (var group in groups)
            _groups.Children.Add(group.Id == _editing ? EditorCard(group) : GroupCard(group));

        // The last card: save what is open now.
        var add = new Button
        {
            Width = CardWidth,
            MinHeight = 190,
            Margin = new Thickness(0, 0, 14, 14),
            Cursor = Cursors.Hand,
            IsEnabled = open > 0,
            ToolTip = _save.ToolTip,
            Template = PlainTemplate(dashed: true)
        };
        add.SetResourceReference(ForegroundProperty, "Ink"); // FIXED (productivity): the theme's text colour
        var plus = Glyph("", 20);
        plus.HorizontalAlignment = HorizontalAlignment.Center;
        plus.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var label = Muted(open > 0 ? $"Save the {open} tab{(open == 1 ? "" : "s")} you have open" : "Open folders in tabs, then save them here");
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.TextAlignment = TextAlignment.Center;
        label.Margin = new Thickness(16, 8, 16, 0);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(plus);
        stack.Children.Add(label);
        add.Content = stack;
        add.Click += (_, _) => _actions.SaveOpenTabs();
        _groups.Children.Add(add);
    }

    private UIElement GroupCard(TabGroup group)
    {
        var body = new StackPanel();

        // name, with its colour and the ⋯ menu
        var more = new Button { Content = "", ToolTip = "More", Width = 28, Height = 28 };
        more.SetResourceReference(StyleProperty, "IconButton");
        more.Click += (_, _) => GroupMenu(group, more).IsOpen = true;
        DockPanel.SetDock(more, Dock.Right);

        var name = new TextBlock { Text = group.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(ColorDot(group.Color, 12));
        top.Children.Add(more);
        top.Children.Add(name);
        body.Children.Add(top);

        // its tabs
        if (group.Tabs.Count == 0)
            body.Children.Add(Faint("No tabs yet. Use Edit to add some."));
        foreach (var tab in group.Tabs.Take(TabsShownOnCard))
            body.Children.Add(TabLine(tab));
        if (group.Tabs.Count > TabsShownOnCard)
        {
            var extra = Faint($"+ {group.Tabs.Count - TabsShownOnCard} more");
            extra.Margin = new Thickness(24, 2, 0, 0);
            body.Children.Add(extra);
        }

        var meta = Faint(Meta(group));
        meta.Margin = new Thickness(0, 10, 0, 12);
        meta.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(meta);

        // Open, New window, Edit
        var openButton = TextButton("Open", primary: true);
        openButton.ToolTip = "Open these tabs beside the ones you have. A tab already showing one of these folders moves into the group."; // CHANGED (groups add, not replace)
        openButton.IsEnabled = group.Tabs.Count > 0;
        openButton.Click += (_, _) => _actions.OpenGroup(group.Id);
        var window = TextButton("New window", primary: false);
        window.IsEnabled = group.Tabs.Count > 0;
        window.Margin = new Thickness(6, 0, 0, 0);
        window.Click += (_, _) => _actions.OpenGroupInNewWindow(group.Id);
        var edit = TextButton("Edit", primary: false);
        edit.Margin = new Thickness(6, 0, 0, 0);
        edit.Click += (_, _) => { _editing = group.Id; RenderGroups(); };
        var actions = new WrapPanel();
        actions.Children.Add(openButton);
        actions.Children.Add(window);
        actions.Children.Add(edit);
        body.Children.Add(actions);

        var card = Card(body);
        card.Width = CardWidth;
        card.Margin = new Thickness(0, 0, 14, 14);
        return card;
    }

    private static string Meta(TabGroup group)
    {
        var parts = new List<string> { $"{group.Tabs.Count} tab{(group.Tabs.Count == 1 ? "" : "s")}", $"saved {When(group.Saved)}" };
        if (group.LastOpened is { } opened)
            parts.Add($"last opened {When(opened)}");
        // REMOVED (groups stay as saved): "kept up to date"
        return string.Join(" · ", parts);
    }

    private ContextMenu GroupMenu(TabGroup group, FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };

        var rename = new MenuItem { Header = "Rename" };
        rename.Click += (_, _) => { _editing = group.Id; RenderGroups(); };
        menu.Items.Add(rename);

        var colour = new MenuItem { Header = "Colour" };
        foreach (var (colorName, hex) in ProductivityStore.Colors)
        {
            var item = new MenuItem { Header = colorName, Icon = ColorDot(hex, 10), IsChecked = hex.Equals(group.Color, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => ProductivityStore.Update(group.Id, saved => saved.Color = hex);
            colour.Items.Add(item);
        }
        menu.Items.Add(colour);

        // REMOVED (groups stay as saved): "Keep up to date with its window's tabs".

        menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = "Delete group…" };
        delete.Click += (_, _) => AskToDelete(group);
        menu.Items.Add(delete);
        return menu;
    }

    private void AskToDelete(TabGroup group)
    {
        var answer = MessageDialog.Show(Window.GetWindow(this),
            $"Delete the tab group \"{group.Name}\"? Its folders stay where they are; only the saved group goes.",
            "Delete tab group", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        if (_editing == group.Id)
            _editing = null;
        ProductivityStore.Remove(group.Id);
        _actions.Report($"Deleted the tab group {group.Name}.");
    }

    // ---- the editor

    private UIElement EditorCard(TabGroup group)
    {
        var body = new StackPanel();

        // name
        var nameBox = new TextBox { Text = group.Name, FontSize = 14, Padding = new Thickness(8, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
        nameBox.SetResourceReference(StyleProperty, "PlainTextBox");
        void CommitName()
        {
            var typed = nameBox.Text.Trim();
            if (typed.Length > 0 && typed != group.Name)
                ProductivityStore.Update(group.Id, saved => saved.Name = typed);
        }
        nameBox.LostKeyboardFocus += (_, _) => CommitName();
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitName(); e.Handled = true; }
            if (e.Key == Key.Escape) { nameBox.Text = group.Name; e.Handled = true; }
        };
        var nameFrame = new Border { Height = 34, BorderThickness = new Thickness(1), Child = nameBox, Margin = new Thickness(0, 0, 0, 12) };
        nameFrame.SetResourceReference(Border.BorderBrushProperty, "Accent");
        nameFrame.SetResourceReference(Border.BackgroundProperty, "Base");
        nameFrame.SetResourceReference(Border.CornerRadiusProperty, "R8");
        body.Children.Add(Label("Name"));
        body.Children.Add(nameFrame);
        nameBox.Loaded += (_, _) => { if (group.Name.EndsWith(" more", StringComparison.Ordinal) || group.Tabs.Count == 0) { nameBox.Focus(); nameBox.SelectAll(); } };

        // colour
        body.Children.Add(Label("Colour"));
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        foreach (var (colorName, hex) in ProductivityStore.Colors)
        {
            var chosen = hex.Equals(group.Color, StringComparison.OrdinalIgnoreCase);
            var swatch = new Button
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = colorName,
                Cursor = Cursors.Hand,
                Template = SwatchTemplate(hex, chosen)
            };
            AutomationName(swatch, $"{colorName}{(chosen ? " (chosen)" : "")}");
            swatch.Click += (_, _) => ProductivityStore.Update(group.Id, saved => saved.Color = hex);
            swatches.Children.Add(swatch);
        }
        body.Children.Add(swatches);

        // tabs
        body.Children.Add(Label($"Tabs ({group.Tabs.Count})"));
        if (group.Tabs.Count == 0)
        {
            var none = Faint("No tabs yet. Add a folder below.");
            none.Margin = new Thickness(0, 2, 0, 8);
            body.Children.Add(none);
        }
        for (var i = 0; i < group.Tabs.Count; i++)
            body.Children.Add(EditableTab(group, i));

        // add
        var addFolder = TextButton("Add a folder…", primary: false);
        addFolder.Click += (_, _) =>
        {
            CommitName();
            if (_actions.PickFolder(group.Name) is { } folder)
                ProductivityStore.Update(group.Id, saved => saved.Tabs.Add(folder));
        };
        var onScreen = _actions.CurrentFolder();
        var addCurrent = TextButton("Add the folder on screen", primary: false);
        addCurrent.Margin = new Thickness(6, 0, 0, 0);
        addCurrent.IsEnabled = onScreen is not null;
        addCurrent.ToolTip = onScreen ?? "No folder is open in a tab";
        addCurrent.Click += (_, _) =>
        {
            CommitName();
            if (onScreen is not null)
                ProductivityStore.Update(group.Id, saved => saved.Tabs.Add(onScreen));
        };
        var adds = new WrapPanel { Margin = new Thickness(0, 8, 0, 12) };
        adds.Children.Add(addFolder);
        adds.Children.Add(addCurrent);
        body.Children.Add(adds);

        // REMOVED (groups stay as saved): the "Keep it up to date as I change these tabs" tick box.

        // delete / done
        var delete = TextButton("Delete group…", primary: false);
        delete.Click += (_, _) => AskToDelete(group);
        DockPanel.SetDock(delete, Dock.Left);
        var done = TextButton("Done", primary: true);
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.Click += (_, _) =>
        {
            CommitName();
            _editing = null;
            RenderGroups();
        };
        var bottom = new DockPanel();
        bottom.Children.Add(delete);
        bottom.Children.Add(done);
        body.Children.Add(bottom);

        var card = Card(body);
        card.Width = CardWidth * 1.4;
        card.Margin = new Thickness(0, 0, 14, 14);
        card.SetResourceReference(Border.BorderBrushProperty, "Accent");
        return card;
    }

    // One tab in the editor: its place, then Move up, Move down and Remove.
    private UIElement EditableTab(TabGroup group, int index)
    {
        var path = group.Tabs[index];

        Button Small(string glyph, string tip, bool enabled, Action<List<string>> change)
        {
            var button = new Button { Content = glyph, ToolTip = tip, Width = 26, Height = 26, IsEnabled = enabled, FontSize = 10 };
            button.SetResourceReference(StyleProperty, "IconButton");
            AutomationName(button, $"{tip}: {ExplorerTab.DefaultTitle(path)}");
            button.Click += (_, _) => ProductivityStore.Update(group.Id, saved =>
            {
                if (index < saved.Tabs.Count && saved.Tabs[index].Equals(path, StringComparison.OrdinalIgnoreCase))
                    change(saved.Tabs);
            });
            return button;
        }

        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(Small("", "Move up", index > 0, tabs => (tabs[index - 1], tabs[index]) = (tabs[index], tabs[index - 1])));
        tools.Children.Add(Small("", "Move down", index < group.Tabs.Count - 1, tabs => (tabs[index + 1], tabs[index]) = (tabs[index], tabs[index + 1])));
        tools.Children.Add(Small("", "Remove", true, tabs => tabs.RemoveAt(index)));
        DockPanel.SetDock(tools, Dock.Right);

        var name = new TextBlock { Text = ExplorerTab.DefaultTitle(path), TextTrimming = TextTrimming.CharacterEllipsis };
        var where = Faint(path.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase) ? "Clearspace page" : path);
        where.TextTrimming = TextTrimming.CharacterEllipsis;
        where.FontSize = 11.5;
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(name);
        words.Children.Add(where);

        var icon = Glyph(ExplorerTab.GlyphFor(path), 13);
        icon.Margin = new Thickness(0, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.SetResourceReference(TextBlock.ForegroundProperty, "InkFaint");

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2), MinHeight = 38, ToolTip = path };
        row.Children.Add(tools);
        row.Children.Add(icon);
        row.Children.Add(words);
        DockPanel.SetDock(icon, Dock.Left);
        return row;
    }

    private static UIElement TabLine(string path)
    {
        var icon = Glyph(ExplorerTab.GlyphFor(path), 12);
        icon.Margin = new Thickness(0, 0, 10, 0);
        icon.SetResourceReference(TextBlock.ForegroundProperty, "InkFaint");
        var name = Muted(ExplorerTab.DefaultTitle(path));
        name.TextWrapping = TextWrapping.NoWrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2), ToolTip = path };
        line.Children.Add(icon);
        line.Children.Add(name);
        return line;
    }

    // ------------------------------------------------------------------ recently opened

    private async Task LoadRecentAsync()
    {
        _recentLoad?.Cancel();
        var cancel = _recentLoad = new CancellationTokenSource();

        _recent.Children.Clear();
        _recent.Children.Add(RecentHeader());
        _recent.Children.Add(Faint("Reading your recent files…"));

        List<FileSystemItem> files;
        try
        {
            files = await Task.Run(() =>
            {
                var list = RecentItems.Read(cancel.Token).Where(item => !item.IsFolder).Take(RecentFilesShown).ToList();
                IconService.Populate(list);
                return list;
            }, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_disposed || cancel.IsCancellationRequested)
            return;

        _recent.Children.Clear();
        _recent.Children.Add(RecentHeader());
        if (files.Count == 0)
        {
            _recent.Children.Add(Faint(RecentItems.IsOffInWindows()
                ? "Windows isn't keeping a list of recent items. Turn on \"Show recently opened items\" in Windows Settings > Personalization > Start."
                : "Files you open will show here."));
            return;
        }

        string? lastDay = null;
        foreach (var file in files)
        {
            var day = DayLabel(file.LastOpened);
            if (day != lastDay)
            {
                var label = Label(day);
                label.Margin = new Thickness(0, lastDay is null ? 4 : 14, 0, 4);
                _recent.Children.Add(label);
                lastDay = day;
            }
            _recent.Children.Add(RecentRow(file));
        }
    }

    private UIElement RecentHeader()
    {
        var all = TextButton("See all", primary: false);
        all.Click += (_, _) => _actions.Navigate(ExplorerLocations.RecentPath);
        DockPanel.SetDock(all, Dock.Right);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(all);
        header.Children.Add(SectionTitle("Recently opened"));
        return header;
    }

    private UIElement RecentRow(FileSystemItem file)
    {
        var time = Faint(file.LastOpened.Date == DateTime.Today ? file.LastOpened.ToString("t") : file.LastOpened.ToString("t"));
        time.Width = 64;
        time.VerticalAlignment = VerticalAlignment.Center;

        var icon = new Image { Source = file.Icon, Width = 20, Height = 20, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = file.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        var folder = Faint(file.LocationText);
        folder.FontSize = 11.5;
        folder.TextTrimming = TextTrimming.CharacterEllipsis;
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(name);
        words.Children.Add(folder);

        var line = new DockPanel { Margin = new Thickness(8, 0, 8, 0) };
        line.Children.Add(time);
        line.Children.Add(icon);
        line.Children.Add(words);
        DockPanel.SetDock(time, Dock.Left);
        DockPanel.SetDock(icon, Dock.Left);

        var row = new Button { Content = line, MinHeight = 46, Cursor = Cursors.Hand, ToolTip = file.FullPath, Template = PlainTemplate(dashed: false), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        row.SetResourceReference(ForegroundProperty, "Ink"); // FIXED (productivity): a button's text was Windows' default black, not the theme's
        AutomationName(row, $"Open {file.Name}");
        row.Click += (_, _) => _actions.OpenFile(file.FullPath);

        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => _actions.OpenFile(file.FullPath);
        var show = new MenuItem { Header = "Show in its folder" };
        show.Click += (_, _) => _actions.ShowInFolder(file.FullPath);
        var copy = new MenuItem { Header = "Copy path" };
        copy.Click += (_, _) => { try { Clipboard.SetText(file.FullPath); } catch (Exception) { } };
        menu.Items.Add(open);
        menu.Items.Add(show);
        menu.Items.Add(copy);
        row.ContextMenu = menu;
        return row;
    }

    // ------------------------------------------------------------------ folders

    private void RenderFolders()
    {
        _folders.Children.Clear();
        _folders.Children.Add(SectionTitle("Folders"));

        var most = ProductivityStore.MostVisited(6);
        var mostLabel = Label($"Most visited · last {ProductivityStore.RecentDays} days");
        mostLabel.Margin = new Thickness(0, 10, 0, 4);
        _folders.Children.Add(mostLabel);
        if (most.Count == 0)
            _folders.Children.Add(Faint("Folders you go into often will show here."));

        var top = most.Count > 0 ? most[0].RecentVisits : 1;
        foreach (var folder in most)
        {
            var count = Faint($"{folder.RecentVisits} visit{(folder.RecentVisits == 1 ? "" : "s")}");
            DockPanel.SetDock(count, Dock.Right);
            var name = new TextBlock { Text = ExplorerTab.DefaultTitle(folder.Path), TextTrimming = TextTrimming.CharacterEllipsis };
            var words = new DockPanel();
            words.Children.Add(count);
            words.Children.Add(name);

            var bar = new Grid { Height = 3, Margin = new Thickness(0, 5, 0, 0) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(folder.RecentVisits, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, top - folder.RecentVisits), GridUnitType.Star) });
            var track = new Border { CornerRadius = new CornerRadius(2) };
            track.SetResourceReference(Border.BackgroundProperty, "Track");
            Grid.SetColumnSpan(track, 2);
            var fill = new Border { CornerRadius = new CornerRadius(2), Opacity = 0.8 };
            fill.SetResourceReference(Border.BackgroundProperty, "Accent");
            bar.Children.Add(track);
            bar.Children.Add(fill);

            var stack = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
            stack.Children.Add(words);
            stack.Children.Add(bar);
            _folders.Children.Add(FolderButton(folder.Path, stack));
        }

        var last = ProductivityStore.LastVisited(5);
        var lastLabel = Label("Last visited");
        lastLabel.Margin = new Thickness(0, 16, 0, 4);
        _folders.Children.Add(lastLabel);
        if (last.Count == 0)
            _folders.Children.Add(Faint("Folders you go into will show here."));
        foreach (var folder in last)
        {
            var when = Faint(When(folder.Last));
            DockPanel.SetDock(when, Dock.Right);
            var name = new TextBlock { Text = ExplorerTab.DefaultTitle(folder.Path), TextTrimming = TextTrimming.CharacterEllipsis };
            var line = new DockPanel { Margin = new Thickness(8, 7, 8, 7) };
            line.Children.Add(when);
            line.Children.Add(name);
            _folders.Children.Add(FolderButton(folder.Path, line));
        }

        if (most.Count > 0 || last.Count > 0)
        {
            var clear = TextButton("Forget folder visits", primary: false);
            clear.Margin = new Thickness(0, 14, 0, 0);
            clear.HorizontalAlignment = HorizontalAlignment.Left;
            clear.ToolTip = "Clears the counts behind Most visited and Last visited. Your folders are not touched.";
            clear.Click += (_, _) => { ProductivityStore.ClearVisits(); RenderFolders(); };
            _folders.Children.Add(clear);
        }
    }

    private Button FolderButton(string path, UIElement content)
    {
        var button = new Button { Content = content, Cursor = Cursors.Hand, ToolTip = path, Template = PlainTemplate(dashed: false), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        button.SetResourceReference(ForegroundProperty, "Ink"); // FIXED (productivity): the theme's text colour, not Windows' black
        AutomationName(button, $"Go to {ExplorerTab.DefaultTitle(path)}");
        button.Click += (_, _) =>
        {
            if (Directory.Exists(path))
                _actions.Navigate(path);
            else
                _actions.Report($"{path} is no longer there.");
        };
        return button;
    }

    // ------------------------------------------------------------------ small builders

    private static string When(DateTime time)
    {
        var day = time.Date;
        if (day == DateTime.Today) return time.ToString("t");
        if (day == DateTime.Today.AddDays(-1)) return "yesterday";
        if (day > DateTime.Today.AddDays(-7)) return time.ToString("dddd");
        return time.ToString("MMM d");
    }

    private static string DayLabel(DateTime time)
    {
        var day = time.Date;
        if (day == DateTime.Today) return "Today";
        if (day == DateTime.Today.AddDays(-1)) return "Yesterday";
        if (day > DateTime.Today.AddDays(-7)) return time.ToString("dddd");
        return time.ToString("MMMM d");
    }

    private static Border Card(UIElement child)
    {
        var card = new Border { Padding = new Thickness(18), BorderThickness = new Thickness(1), Child = child };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "EdgeRaised");
        card.SetResourceReference(Border.CornerRadiusProperty, "R12");
        return card;
    }

    private static TextBlock SectionTitle(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        return block;
    }

    private static TextBlock Label(string text)
    {
        var block = new TextBlock { Text = text.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "InkFaint");
        return block;
    }

    private static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        return block;
    }

    private static TextBlock Faint(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "InkFaint");
        return block;
    }

    private static TextBlock Glyph(string glyph, double size)
    {
        var block = new TextBlock { Text = glyph, FontSize = size };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        return block;
    }

    private static Border ColorDot(string hex, double size) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 3),
        Margin = new Thickness(0, 0, 10, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Background = ToBrush(hex)
    };

    private static Brush ToBrush(string hex)
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

    private static Button TextButton(string label, bool primary)
    {
        var button = new Button { Content = label };
        button.SetResourceReference(StyleProperty, primary ? "ViewerTextButtonPrimary" : "ViewerTextButton");
        return button;
    }

    private static void AutomationName(DependencyObject element, string name)
        => System.Windows.Automation.AutomationProperties.SetName(element, name);

    // A button that is just its content, with the theme's hover colour (rows and the "save" card).
    private static ControlTemplate PlainTemplate(bool dashed)
    {
        var template = new ControlTemplate(typeof(Button));
        var frame = new FrameworkElementFactory(typeof(Border), "Frame");
        frame.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        frame.SetResourceReference(Border.CornerRadiusProperty, dashed ? "R12" : "R8");
        if (dashed)
        {
            frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        }
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        frame.AppendChild(content);
        template.VisualTree = frame;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Hover"), "Frame"));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5, "Frame"));
        template.Triggers.Add(disabled);
        return template;
    }

    // A colour square; the chosen one has a ring in the text colour.
    private static ControlTemplate SwatchTemplate(string hex, bool chosen)
    {
        var template = new ControlTemplate(typeof(Button));
        var ring = new FrameworkElementFactory(typeof(Border));
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        if (chosen)
            ring.SetResourceReference(Border.BorderBrushProperty, "Ink");
        else
            ring.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        ring.SetValue(Border.PaddingProperty, new Thickness(2));
        var fill = new FrameworkElementFactory(typeof(Border));
        fill.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        fill.SetValue(Border.BackgroundProperty, ToBrush(hex));
        ring.AppendChild(fill);
        template.VisualTree = ring;
        return template;
    }
}
