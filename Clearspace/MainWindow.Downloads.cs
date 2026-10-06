// Clearspace | NEW (downloads): what a Downloads folder does beyond listing its files.
//  - "Just arrived": a strip above the list with the newest few files as tiles (Services/JustArrived.cs),
//    each with the things you do next: open, unpack (a .zip), open the page it came from, move, delete.
//  - Go back to the page: "Open the page this came from" and "Copy download link", on a tile and in the
//    right-click menu of any file that still carries the note Windows attached when it was downloaded
//    (Services/DownloadOrigin.cs). These two work in every folder, not only Downloads.
//  - Learned filing: when files like this one have been moved to the same folder before, the tile and the
//    right-click menu offer "Move to <folder>" (Services/FilingMemory.cs). It only offers; you choose.
//  - Installers: a line in the strip counts the setup files in the folder and says how many are for
//    programs already on the PC; "Review" opens InstallersWindow, where the ticked ones can be removed.
// The strip is rebuilt when the list changes (a download finishing shows up by itself), but only if what
// it shows would be different. It is hidden during a search, and can be turned off from the menu of the
// empty area of the list ("Show just arrived").
// The strip's frame is the Border named ArrivalsStrip in MainWindow.xaml; what is in it is built here.
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Clearspace.Models;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

public partial class MainWindow
{
    private bool _arrivalsQueued;
    private int _arrivalsRequest;            // goes up with every rebuild; one that finishes for an older request is dropped
    private string _arrivalsShown = string.Empty; // what the strip shows now, to skip rebuilding it unchanged
    private string _installersFor = string.Empty; // the folder and installer files `_installers` was worked out for
    private IReadOnlyList<InstallerFile> _installers = [];

    private void InitializeDownloads()
    {
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.Items) or nameof(MainViewModel.HasSearch) or
                nameof(MainViewModel.ResolvedFolderType) or nameof(MainViewModel.IsLoading))
                QueueArrivals();
        };
    }

    // Several things change together when a folder opens; the strip is rebuilt once, after them.
    private void QueueArrivals()
    {
        if (_arrivalsQueued)
            return;

        _arrivalsQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _arrivalsQueued = false;
            _ = RebuildArrivalsAsync();
        });
    }

    private void HideArrivals()
    {
        _arrivalsRequest++;
        _arrivalsShown = string.Empty;
        ArrivalsStrip.Visibility = Visibility.Collapsed;
        ArrivalsStrip.Child = null;
    }

    private async Task RebuildArrivalsAsync()
    {
        try
        {
            if (!_viewModel.ShowsJustArrived || !SettingsService.GetShowJustArrived())
            {
                HideArrivals();
                return;
            }

            if (_viewModel.IsLoading && _viewModel.Items.Count == 0)
                return; // the folder is still being read; this is called again when it has been

            var folder = _viewModel.CurrentPath;
            var items = _viewModel.Items;
            var now = DateTime.Now;
            var picks = JustArrived.Pick(items, now);

            // The installers are worked out again only when the setup-like files in the folder changed.
            var installerKey = folder + "|" + string.Join("|", items
                .Where(item => !item.IsFolder && InstallerShelf.CouldBeInstaller(item.Name))
                .Select(item => $"{item.Name}*{item.Size}"));

            var key = installerKey + "#" + string.Join("|", picks.Select(item => $"{item.FullPath}*{item.Size}*{JustArrived.Ago(JustArrived.ArrivedAt(item), now)}"));
            if (key == _arrivalsShown)
                return;

            var request = ++_arrivalsRequest;
            var paths = picks.Select(item => item.FullPath).ToList();
            var needInstallers = installerKey != _installersFor;

            // Off the UI thread: the note on each file, the folder it is offered, and (when needed) the
            // details of every setup file plus the list of installed programs.
            var (origins, offers, installers) = await Task.Run(() =>
            {
                var origin = paths.Select(DownloadOrigin.Read).ToList();
                var offer = paths.Select((path, i) => FilingMemory.SuggestFor(path, origin[i]?.Site)).ToList();
                var shelf = needInstallers ? InstallerShelf.Scan(folder) : null;
                return (origin, offer, shelf);
            });

            if (request != _arrivalsRequest || !_viewModel.ShowsJustArrived ||
                !string.Equals(folder, _viewModel.CurrentPath, StringComparison.OrdinalIgnoreCase))
                return;

            if (installers is not null)
            {
                _installers = installers;
                _installersFor = installerKey;
            }

            if (picks.Count == 0 && _installers.Count == 0)
            {
                HideArrivals();
                _arrivalsShown = key;
                return;
            }

            ArrivalsStrip.Child = BuildArrivals(picks, origins, offers, now);
            ArrivalsStrip.Visibility = Visibility.Visible;
            _arrivalsShown = key;
        }
        catch (Exception)
        {
            HideArrivals(); // the strip is an extra: whatever went wrong, the list below it is unaffected
        }
    }

    // ---- building the strip

    private StackPanel BuildArrivals(IReadOnlyList<FileSystemItem> picks, IReadOnlyList<DownloadOrigin?> origins,
        IReadOnlyList<FilingSuggestion?> offers, DateTime now)
    {
        var title = new TextBlock { Text = "Just arrived", FontSize = 14 };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "SerifFont");

        var hint = SmallMuted(picks.Count == 0 ? "Nothing new in the last week" : "Your newest downloads");
        hint.Margin = new Thickness(10, 2, 0, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;

        var hide = new Button { Content = "\uE711", Width = 24, Height = 24, FontSize = 10, ToolTip = "Hide this strip. Right-click the empty area of the list and choose \"Show just arrived\" to bring it back." };
        hide.SetResourceReference(StyleProperty, "IconButton");
        hide.Click += (_, _) =>
        {
            SettingsService.SetShowJustArrived(false);
            HideArrivals();
        };
        DockPanel.SetDock(hide, Dock.Right);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = false };
        header.Children.Add(hide);
        if (InstallersLine() is { } installers)
        {
            DockPanel.SetDock(installers, Dock.Right);
            header.Children.Add(installers);
        }
        header.Children.Add(title);
        header.Children.Add(hint);

        var strip = new StackPanel();
        strip.Children.Add(header);

        if (picks.Count > 0)
        {
            // One row of tiles; in a narrow window the ones that do not fit are simply not shown.
            var tiles = new WrapPanel { MaxHeight = ArrivalTileHeight + 8, ClipToBounds = true };
            for (var i = 0; i < picks.Count; i++)
                tiles.Children.Add(ArrivalTile(picks[i], origins[i], offers[i], now));
            strip.Children.Add(tiles);
        }

        return strip;
    }

    private const double ArrivalTileHeight = 98;

    private Border ArrivalTile(FileSystemItem item, DownloadOrigin? origin, FilingSuggestion? offer, DateTime now)
    {
        // The picture: the large icon at once, then (a moment later) what a tile would show - the photo
        // itself, a video's frame, a program's own icon.
        var picture = new Image
        {
            Width = 32,
            Height = 32,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Source = item.Thumbnail ?? IconService.GetLargeIcon(item) ?? item.Icon
        };
        _ = LoadTilePictureAsync(picture, item);
        DockPanel.SetDock(picture, Dock.Left);

        var name = new TextBlock { Text = item.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var facts = new List<string> { FileSystemItem.FormatSize(item.Size) };
        if (origin is not null)
            facts.Add(origin.Site);
        facts.Add(JustArrived.Ago(JustArrived.ArrivedAt(item), now));
        var detail = SmallMuted(string.Join("  ·  ", facts));
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        detail.Margin = new Thickness(0, 2, 0, 0);

        var words = new StackPanel();
        words.Children.Add(name);
        words.Children.Add(detail);

        var top = new DockPanel();
        top.Children.Add(picture);
        top.Children.Add(words);

        // The actions, left to right: the ones that apply to this file.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 6, 0, 0) };
        actions.Children.Add(TileAction("\uE8E5", "Open", () => WithSelected(item, () => _viewModel.OpenCommand.Execute(null))));
        if (IsArchiveItem(item))
            actions.Children.Add(TileAction("\uE7B8", "Extract to a new folder here", () => WithSelected(item, () => OnExtractArchives(this, new RoutedEventArgs()))));
        if (origin is not null)
            actions.Children.Add(TileAction("\uE774", PageLabel(origin), () => OpenAddress(origin.PageToOpen)));
        actions.Children.Add(offer is null
            ? TileAction("\uE8DE", "Move to a folder…", () => MoveWithPicker(item))
            : MoveOfferButton(item, offer));
        actions.Children.Add(TileAction("\uE74D", "Delete (to the Recycle Bin)", () => WithSelected(item, () => _viewModel.DeleteCommand.Execute(null))));

        var content = new StackPanel();
        content.Children.Add(top);
        content.Children.Add(actions);

        var tile = new Border
        {
            Width = 244,
            Height = ArrivalTileHeight,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(12, 10, 8, 6),
            BorderThickness = new Thickness(1),
            Child = content,
            ToolTip = item.Name,
            ContextMenu = ArrivalMenu(item, origin, offer)
        };
        tile.SetResourceReference(Border.BackgroundProperty, "Tint");
        tile.SetResourceReference(Border.BorderBrushProperty, "EdgeRaised");
        tile.SetResourceReference(Border.CornerRadiusProperty, "R8");

        // A click on the tile itself (not on one of its buttons) finds the file in the list; a double-click opens it.
        tile.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
                WithSelected(item, () => _viewModel.OpenCommand.Execute(null));
            else
                WithSelected(item, () => FileList.Focus());
            e.Handled = true;
        };
        return tile;
    }

    private static async Task LoadTilePictureAsync(Image picture, FileSystemItem item)
    {
        if (item.Thumbnail is not null || item.IsOnlineOnly)
            return;

        if (await ThumbnailService.PreviewAsync(item, 96) is { } better)
            picture.Source = better;
    }

    private Button TileAction(string glyph, string tip, Action action)
    {
        var button = new Button { Content = glyph, Width = 28, Height = 28, ToolTip = tip };
        button.SetResourceReference(StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        return button;
    }

    // "Move to <folder>" in words, because which folder is the whole point of the offer.
    private Button MoveOfferButton(FileSystemItem item, FilingSuggestion offer)
    {
        var label = new TextBlock { Text = $"Move to {offer.FolderName}", FontSize = 11.5, MaxWidth = 118, TextTrimming = TextTrimming.CharacterEllipsis };
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"{offer.Folder}\n{offer.Why}. Right-click the tile to choose another folder or to stop this suggestion."
        };
        button.SetResourceReference(StyleProperty, "ProjectChip");
        button.Click += (_, e) =>
        {
            MoveTo(item, offer.Folder);
            e.Handled = true;
        };
        return button;
    }

    private ContextMenu ArrivalMenu(FileSystemItem item, DownloadOrigin? origin, FilingSuggestion? offer)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Entry("Open", "\uE8E5", () => WithSelected(item, () => _viewModel.OpenCommand.Execute(null))));
        menu.Items.Add(Entry("Show in the list", "\uE8B3", () => WithSelected(item, () => FileList.Focus())));
        if (IsArchiveItem(item))
            menu.Items.Add(Entry("Extract to a new folder", "\uE7B8", () => WithSelected(item, () => OnExtractArchives(this, new RoutedEventArgs()))));

        if (origin is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Entry(PageLabel(origin), "\uE774", () => OpenAddress(origin.PageToOpen)));
            menu.Items.Add(Entry("Copy download link", "\uE71B", () => CopyAddress(origin)));
        }

        menu.Items.Add(new Separator());
        if (offer is not null)
            menu.Items.Add(Entry($"Move to \"{MenuText(offer.FolderName)}\"", "\uE8DE", () => MoveTo(item, offer.Folder)));
        menu.Items.Add(Entry(offer is null ? "Move to a folder…" : "Move to another folder…", offer is null ? "\uE8DE" : null, () => MoveWithPicker(item)));
        if (offer is not null)
        {
            menu.Items.Add(Entry($"Stop suggesting \"{MenuText(offer.FolderName)}\"", null, () =>
            {
                FilingMemory.ForgetFolder(offer.Folder);
                _arrivalsShown = string.Empty;
                QueueArrivals();
            }));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Entry("Delete", "\uE74D", () => WithSelected(item, () => _viewModel.DeleteCommand.Execute(null)), "Del"));
        return menu;
    }

    // "14 installers, 2.1 GB (9 look installed)  Review": null when the folder has none.
    private StackPanel? InstallersLine()
    {
        if (_installers.Count == 0)
            return null;

        var installed = _installers.Count(file => file.State == InstallerState.Installed);
        var text = $"{_installers.Count:N0} {(_installers.Count == 1 ? "installer" : "installers")}, {FileSystemItem.FormatSize(_installers.Sum(file => file.Size))}";
        if (installed > 0)
            text += $" · {installed:N0} already installed";

        var words = SmallMuted(text);
        words.VerticalAlignment = VerticalAlignment.Center;

        var review = new Button { Content = new TextBlock { Text = "Review", FontSize = 11.5 }, Margin = new Thickness(10, 0, 8, 0), ToolTip = "See the setup files in this folder and remove the ones you no longer need" };
        review.SetResourceReference(StyleProperty, "ProjectChip");
        review.Click += (_, _) => ReviewInstallers();

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(words);
        line.Children.Add(review);
        return line;
    }

    private static TextBlock SmallMuted(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 11 };
        block.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        return block;
    }

    // A menu reads "_" as "underline the next letter", so underscores in a name are doubled.
    private static string MenuText(string text) => text.Replace("_", "__");

    private static string PageLabel(DownloadOrigin origin)
        => origin.KnowsPage ? "Open the page this came from" : $"Open {origin.Site} (the page is not known)";

    // ---- the actions

    // Selects the tile's file in the list, then does `then`: the commands (Open, Delete, Extract) work on
    // the selection. The row is looked up by path, because the list's rows are new objects after every
    // refresh while the tile still holds the one it was built from.
    private void WithSelected(FileSystemItem item, Action then)
    {
        var row = _viewModel.Items.FirstOrDefault(candidate => string.Equals(candidate.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            _viewModel.ReportStatus($"{item.Name} is no longer in this folder.");
            _arrivalsShown = string.Empty;
            QueueArrivals();
            return;
        }

        FileList.SelectedItems.Clear();
        FileList.SelectedItem = row;
        FileList.ScrollIntoView(row);
        then();
    }

    private void OpenAddress(Uri address)
    {
        try
        {
            // Only ever an http or https address (DownloadOrigin takes nothing else), handed to the browser.
            Process.Start(new ProcessStartInfo { FileName = address.AbsoluteUri, UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            _viewModel.ReportStatus($"Couldn't open the page: {error.Message}");
        }
    }

    private void CopyAddress(DownloadOrigin origin)
    {
        try
        {
            Clipboard.SetText((origin.File ?? origin.Page!).AbsoluteUri);
            _viewModel.ReportStatus(origin.File is null ? "Copied the page's address (the download link is not known)." : "Copied the download link.");
        }
        catch (ExternalException)
        {
            _viewModel.ReportStatus("Couldn't copy: another program is using the clipboard.");
        }
    }

    private void MoveWithPicker(FileSystemItem item)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Move {item.Name} to",
            InitialDirectory = MainViewModel.LibraryRoot,
            Multiselect = false
        };

        if (picker.ShowDialog(this) == true)
            MoveTo(item, picker.FolderName);
    }

    private void MoveTo(FileSystemItem item, string folder)
    {
        if (!Directory.Exists(folder))
        {
            _viewModel.ReportStatus($"{folder} is no longer there.");
            return;
        }

        var result = FileOperationService.Move([item.FullPath], folder, _viewModel.Context.OwnerHandle);
        _viewModel.Context.ReportFileOperation(result);
        if (result.Succeeded)
            _viewModel.ReportStatus($"Moved {item.Name} to {Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))}.");

        _arrivalsShown = string.Empty; // what was learned from this move may change the other tiles' offers
        _ = _viewModel.RefreshAsync();
    }

    private bool _reviewingInstallers;

    private async void ReviewInstallers()
    {
        if (_reviewingInstallers)
            return; // a second click while the list is being put together, or while it is open

        _reviewingInstallers = true;
        try
        {
            var folder = _viewModel.CurrentPath;
            if (!Directory.Exists(folder))
                return;

            // Looked up again, so the list is what is in the folder now.
            var installers = await Task.Run(() => InstallerShelf.Scan(folder));
            if (!string.Equals(folder, _viewModel.CurrentPath, StringComparison.OrdinalIgnoreCase))
                return;

            if (installers.Count == 0)
            {
                MessageDialog.Show(this, "There are no setup files in this folder.", "Installers", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new InstallersWindow(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), installers) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Chosen.Count == 0)
                return;

            var result = FileOperationService.Delete(dialog.Chosen, _viewModel.Context.OwnerHandle);
            _viewModel.Context.ReportFileOperation(result);

            var gone = dialog.Chosen.Count(path => !File.Exists(path));
            if (gone > 0)
                _viewModel.ReportStatus($"Moved {gone:N0} {(gone == 1 ? "installer" : "installers")} to the Recycle Bin.");

            _installersFor = string.Empty;
            _arrivalsShown = string.Empty;
            await _viewModel.RefreshAsync();
        }
        catch (Exception error)
        {
            MessageDialog.Show(this, error.Message, "Installers", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _reviewingInstallers = false;
        }
    }

    // ---- the right-click menu of a file in the list

    private DownloadOrigin? _menuOrigin;
    private FilingSuggestion? _menuOffer;

    // The files-and-folders menu is opening (called from OnItemMenuOpened): show the entries that fit.
    private void UpdateDownloadMenu()
    {
        _menuOrigin = null;
        _menuOffer = null;

        var selected = _viewModel.Context.SelectedItems;
        if (selected.Count == 1 && !selected[0].IsFolder && IsOnDisk(selected[0]))
        {
            // One small read, and only for a single selected file: fine to do while the menu opens.
            _menuOrigin = DownloadOrigin.Read(selected[0].FullPath);
            if (_viewModel.IsDownloadsFolder)
                _menuOffer = FilingMemory.SuggestFor(selected[0].FullPath, _menuOrigin?.Site, trustNetworkFolders: true);
        }

        OriginPageMenuItem.Visibility = _menuOrigin is null ? Visibility.Collapsed : Visibility.Visible;
        OriginLinkMenuItem.Visibility = _menuOrigin?.File is null ? Visibility.Collapsed : Visibility.Visible;
        FilingMenuItem.Visibility = _menuOffer is null ? Visibility.Collapsed : Visibility.Visible;

        if (_menuOrigin is not null)
            OriginPageMenuItem.Header = PageLabel(_menuOrigin);
        if (_menuOffer is not null)
        {
            FilingMenuItem.Header = $"Move to \"{MenuText(_menuOffer.FolderName)}\"";
            FilingMenuItem.ToolTip = $"{_menuOffer.Folder}\n{_menuOffer.Why}";
        }
    }

    private void OnOpenOriginPage(object sender, RoutedEventArgs e)
    {
        if (_menuOrigin is not null)
            OpenAddress(_menuOrigin.PageToOpen);
    }

    private void OnCopyOriginLink(object sender, RoutedEventArgs e)
    {
        if (_menuOrigin is not null)
            CopyAddress(_menuOrigin);
    }

    private void OnMoveToSuggestedFolder(object sender, RoutedEventArgs e)
    {
        if (_menuOffer is not null && _viewModel.Context.SelectedItems is [var item])
            MoveTo(item, _menuOffer.Folder);
    }

    // ---- the menu of the empty area of the list (called from BuildBackgroundMenu, in Downloads folders)

    private void AddDownloadsEntries(ContextMenu menu)
    {
        menu.Items.Add(Choice("Show just arrived", SettingsService.GetShowJustArrived(), () =>
        {
            SettingsService.SetShowJustArrived(!SettingsService.GetShowJustArrived());
            _arrivalsShown = string.Empty;
            QueueArrivals();
        }));
        menu.Items.Add(Entry("Review installers…", "\uE7B8", ReviewInstallers));
    }
}
