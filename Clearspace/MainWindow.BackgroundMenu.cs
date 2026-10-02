// Clearspace | The right-click menu for the empty part of the file list.
// NEW (empty-area menu): right-clicking a file or folder still shows the menu defined in MainWindow.xaml.
// Right-clicking empty space in a real folder now shows this one instead, modelled on Explorer's: View,
// Sort by, Refresh, Paste, Open in Terminal, whatever installed apps add ("Open Git Bash here" ...),
// New > Folder and the Windows file templates, Disk usage and Properties. It is built fresh each time it
// opens, from ordinary MenuItems, so it takes on the active theme like every other menu.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace;

public partial class MainWindow
{
    // The menu for files and folders (the one written in MainWindow.xaml), kept so it can be put back.
    private ContextMenu? _itemMenu;

    private void InitializeBackgroundMenu()
    {
        _itemMenu = FileList.ContextMenu;
        ShellMenuService.Warm();
    }

    private ContextMenu BuildBackgroundMenu()
    {
        var folder = _viewModel.CurrentPath;
        var menu = new ContextMenu();

        // ---- View
        menu.Items.Add(Submenu("View", "\uE8A9",
            Choice("Details", _viewModel.IsDetails, () => _viewModel.SetLayout(LayoutMode.Details)),
            Choice("Tiles", _viewModel.IsGrid, () => _viewModel.SetLayout(LayoutMode.Grid))));

        // ---- Sort by. Picking the column already in use changes nothing; the direction has its own two entries.
        // (ViewModel.Sort on the current column flips the direction, on another column sorts it ascending.)
        MenuItem SortBy(string label, SortColumn column) => Choice(label, _viewModel.SortColumn == column, () =>
        {
            if (_viewModel.SortColumn != column)
                _viewModel.Sort(column);
        });
        MenuItem Direction(string label, bool descending) => Choice(label, _viewModel.SortDescending == descending, () =>
        {
            if (_viewModel.SortDescending != descending)
                _viewModel.Sort(_viewModel.SortColumn);
        });
        menu.Items.Add(Submenu("Sort by", "\uE8CB",
            SortBy("Name", SortColumn.Name),
            SortBy("Date modified", SortColumn.DateModified),
            SortBy("Date created", SortColumn.DateCreated),
            SortBy("Type", SortColumn.Type),
            SortBy("Size", SortColumn.Size),
            new Separator(),
            Direction("Ascending", false),
            Direction("Descending", true)));

        menu.Items.Add(Entry("Refresh", "\uE72C", () => _viewModel.RefreshCommand.Execute(null), "F5"));
        menu.Items.Add(new Separator());

        // ---- Paste: only when the clipboard holds files or folders.
        var paste = Entry("Paste", "\uE77F", () => _viewModel.PasteCommand.Execute(null), "Ctrl+V");
        try { paste.IsEnabled = Clipboard.ContainsFileDropList(); }
        catch (Exception) { paste.IsEnabled = false; }   // another program has the clipboard open
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());

        // ---- Terminal, then what installed apps add (Shift held = also the ones Explorer hides until Shift).
        menu.Items.Add(Entry("Open in Terminal", "\uE756", () => _viewModel.TerminalCommand.Execute(null), "Ctrl+`"));
        var showHidden = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        foreach (var verb in ShellMenuService.BackgroundVerbs(showHidden))
        {
            var entry = Entry(verb.Label, null, () => RunShellVerb(verb, folder));
            entry.Icon = (verb.IconPath is { } iconFile && IconService.ForPath(iconFile) is { } picture)
                ? new Image { Source = picture, Width = 16, Height = 16 }
                : new Border();   // an empty slot, so the text still lines up with the entries that have a picture
            menu.Items.Add(entry);
        }
        menu.Items.Add(new Separator());

        // ---- New
        var create = Submenu("New", "\uE710");
        var newFolder = Entry("Folder", "\uE8B7", () => CreateThenRename(ShellMenuService.CreateFolder), "Ctrl+Shift+N");
        create.Items.Add(newFolder);
        create.Items.Add(new Separator());
        foreach (var template in ShellMenuService.Templates)
        {
            var entry = Entry(template.Label, null, () => CreateThenRename(parent => ShellMenuService.CreateFile(template, parent)));
            entry.Icon = (IconService.ForExtension(template.Extension) is { } picture)
                ? new Image { Source = picture, Width = 16, Height = 16 }
                : new Border();
            create.Items.Add(entry);
        }
        menu.Items.Add(create);
        menu.Items.Add(new Separator());

        menu.Items.Add(Entry("Disk usage…", "\uE9F9", () => OnDiskUsage(this, new RoutedEventArgs())));
        menu.Items.Add(Entry("Properties", "\uE946", () => _viewModel.PropertiesCommand.Execute(null), "Alt+Enter"));
        return menu;
    }

    // ---- small builders

    // An entry with a symbol from the icon font (or none), an action, and an optional shortcut hint.
    private MenuItem Entry(string label, string? glyph, Action action, string? shortcut = null)
    {
        var item = new MenuItem { Header = label, InputGestureText = shortcut ?? string.Empty };
        if (glyph is not null)
            item.Icon = Symbol(glyph);
        item.Click += (_, _) => action();
        return item;
    }

    // An entry that opens a submenu.
    private MenuItem Submenu(string label, string glyph, params object[] children)
    {
        var item = new MenuItem { Header = label, Icon = Symbol(glyph) };
        foreach (var child in children)
            item.Items.Add(child);
        return item;
    }

    // A ticked / unticked choice inside a submenu.
    private static MenuItem Choice(string label, bool chosen, Action action)
    {
        var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = chosen };
        item.Click += (_, _) => action();
        return item;
    }

    private TextBlock Symbol(string glyph) => new()
    {
        Text = glyph,
        FontFamily = (FontFamily)FindResource("IconFont"),
        FontSize = 13,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    // ---- actions

    private void RunShellVerb(AppMenuEntry verb, string folder)
    {
        try
        {
            ShellMenuService.Run(verb, folder);
        }
        catch (Exception error)
        {
            MessageDialog.Show(this, $"{verb.Label} could not be started.\n\n{error.Message}", "Clearspace",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Makes the new folder or file, shows it in the list, selects it and starts renaming it, as Explorer does.
    private async void CreateThenRename(Func<string, string> create)
    {
        try
        {
            var folder = _viewModel.CurrentPath;
            if (!Directory.Exists(folder))
                return;

            string path;
            try
            {
                path = create(folder);
            }
            catch (Exception error)
            {
                MessageDialog.Show(this, error.Message, "Couldn't create it here", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await _viewModel.RefreshAsync();

            var item = _viewModel.Items.FirstOrDefault(candidate => string.Equals(candidate.FullPath, path, StringComparison.OrdinalIgnoreCase));
            if (item is null)
                return; // hidden by a search or filter; it was still created

            FileList.SelectedItem = item;
            FileList.ScrollIntoView(item);
            BeginRename(item);
        }
        catch (Exception)
        {
            // the item exists; failing to select it is not worth an error box
        }
    }
}
