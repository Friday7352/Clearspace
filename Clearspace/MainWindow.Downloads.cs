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
// it shows would be different.
// CHANGED (just arrived, no waiting): the tiles go up at once, from the list alone. Where each file came
// from, the folder it is offered and the installers line are looked up in the background and filled in
// when they are known; the strip never waits for them. (Reading the installers of a big Downloads folder
// took the best part of a minute, and the strip used to show nothing at all until it was done.) It is hidden during a search, and can be turned off from the menu of the
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
    private string _arrivalsShown = string.Empty; // what the strip shows now, to skip rebuilding it unchanged
    private int _arrivalsBuilds;                  // NEW (just arrived trace): numbers the rebuilds in the log
    // CHANGED (just arrived, no waiting): _arrivalsRequest is gone. A rebuild no longer waits for anything,
    // so there is no older one still running for a newer one to replace.

    // CHANGED (just arrived, no waiting): the installers line is worked out on its own, one look at a time,
    // and only when the setup files in the folder changed.
    private string _installersFor = string.Empty;    // the folder and installer files `_installers` was worked out for
    private string _installersFolder = string.Empty; // NEW: the folder `_installers` belongs to
    private IReadOnlyList<InstallerFile> _installers = [];
    private bool _lookingAtInstallers;               // NEW: a look is running; the next starts after it if still needed

    // NEW (just arrived, no waiting): where each tile's file came from and the folder it is offered, looked
    // up in the background once per file (and again when the file changes) and kept here.
    private readonly Dictionary<string, (DownloadOrigin? Origin, FilingSuggestion? Offer)> _arrivalExtras = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _arrivalExtrasPending = new(StringComparer.OrdinalIgnoreCase);
    private int _arrivalExtrasGeneration; // goes up when the kept answers are forgotten; answers for an older one are dropped

    private void InitializeDownloads()
    {
        _viewModel.PropertyChanged += (_, e) =>
        {
            // CHANGED (just arrived fix): + IsAccessDenied, which ShowsJustArrived also depends on.
            if (e.PropertyName is nameof(MainViewModel.Items) or nameof(MainViewModel.HasSearch) or
                nameof(MainViewModel.ResolvedFolderType) or nameof(MainViewModel.IsLoading) or
                nameof(MainViewModel.IsAccessDenied))
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
            RebuildArrivals(); // CHANGED (just arrived, no waiting): was RebuildArrivalsAsync
        });
    }

    private void HideArrivals()
    {
        _arrivalsShown = string.Empty;
        ArrivalsStrip.Visibility = Visibility.Collapsed;
        ArrivalsStrip.Child = null;
    }

    // NEW (just arrived, no waiting): forgets the looked-up offers (and what is shown), so they are asked for
    // again: after a move has taught a new folder, or after "Stop suggesting".
    private void ForgetArrivalExtras()
    {
        _arrivalExtras.Clear();
        _arrivalExtrasGeneration++;
        _arrivalsShown = string.Empty;
    }

    // NEW (just arrived, no waiting): the same file, unchanged.
    private static string ExtrasKey(FileSystemItem item) => $"{item.FullPath}*{item.Size}*{item.DateModified.Ticks}";

    // CHANGED (just arrived, no waiting): rewritten, and no longer async. It builds the strip from the list
    // and from what has been looked up so far, and starts the lookups that are missing; each of those asks
    // for another rebuild when it is done. Every decision is written to just-arrived-trace.log.
    private void RebuildArrivals()
    {
        var folder = _viewModel.CurrentPath;
        try
        {
            if (!_viewModel.ShowsJustArrived || !SettingsService.GetShowJustArrived())
            {
                if (_viewModel.IsDownloadsFolder && ArrivalsStrip.Visibility == Visibility.Visible)
                    ArrivalsTrace.Write($"hidden in {folder}: " +
                        (!SettingsService.GetShowJustArrived() ? "turned off (\"Show just arrived\" is unticked)"
                         : _viewModel.HasSearch ? "a search is showing" : "access denied"));
                HideArrivals();
                return;
            }

            if (_viewModel.IsLoading && _viewModel.Items.Count == 0)
                return; // the folder is still being read; this is called again when it has been

            var items = _viewModel.Items;
            var now = DateTime.Now;
            var picks = JustArrived.Pick(items, now);

            // The installers are worked out again only when the setup-like files in the folder changed.
            var installerKey = folder + "|" + string.Join("|", items
                .Where(item => !item.IsFolder && InstallerShelf.CouldBeInstaller(item.Name))
                .Select(item => $"{item.Name}*{item.Size}"));
            var installers = string.Equals(_installersFolder, folder, StringComparison.OrdinalIgnoreCase) ? _installers : Array.Empty<InstallerFile>();

            // NEW: start whatever is missing. Neither is waited for.
            var missing = picks.Where(item => !_arrivalExtras.ContainsKey(ExtrasKey(item))).ToList();
            if (missing.Count > 0)
                LookUpArrivalExtras(missing);
            if (installerKey != _installersFor && !_viewModel.IsLoading)
                LookAtInstallers(folder, installerKey);

            // CHANGED: the key also says which of the lookups are in, so the strip is built again when one arrives.
            var key = $"{folder}|{installers.Count}|{installers.Sum(file => file.Size)}|{installers.Count(file => file.State == InstallerState.Installed)}#" +
                      string.Join("|", picks.Select(item => $"{item.FullPath}*{item.Size}*{JustArrived.Ago(JustArrived.ArrivedAt(item), now)}*{(_arrivalExtras.ContainsKey(ExtrasKey(item)) ? 1 : 0)}"));
            if (key == _arrivalsShown)
                return;

            var build = ++_arrivalsBuilds;
            var newest = items.Where(item => !item.IsFolder).OrderByDescending(JustArrived.ArrivedAt).Take(3)
                .Select(item => "    " + ArrivalsTrace.Describe(item, now));
            ArrivalsTrace.Write($"rebuild #{build} in {folder}: {items.Count:N0} items{(_viewModel.IsLoading ? " (still loading)" : "")}, " +
                                $"{picks.Count} new{(picks.Count > 0 ? ": " + string.Join(", ", picks.Select(item => item.Name)) : "")}, " +
                                $"{missing.Count} still being looked up" +
                                Environment.NewLine + "  newest in the list:" + Environment.NewLine + string.Join(Environment.NewLine, newest));

            // CHANGED (always shown): the strip is no longer hidden when nothing is new; it is always there
            // in a Downloads folder (an empty folder says so).

            var origins = picks.Select(item => _arrivalExtras.TryGetValue(ExtrasKey(item), out var known) ? known.Origin : null).ToList();
            var offers = picks.Select(item => _arrivalExtras.TryGetValue(ExtrasKey(item), out var known) ? known.Offer : null).ToList();

            ArrivalsStrip.Child = BuildArrivals(picks, origins, offers, now);
            ArrivalsStrip.Visibility = Visibility.Visible;
            _arrivalsShown = key;
            ArrivalsTrace.Write($"rebuild #{build}: shown, {picks.Count} tiles, {installers.Count} installers");
        }
        catch (Exception error)
        {
            // CHANGED (just arrived trace): written down instead of swallowed. The strip is an extra:
            // whatever went wrong, the list below it is unaffected.
            ArrivalsTrace.Write($"FAILED in {folder}, strip hidden: {error.GetType().Name}: {error.Message}" +
                                Environment.NewLine + error.StackTrace);
            HideArrivals();
        }
    }

    // NEW (just arrived, no waiting): where these files came from and the folder each is offered, read off
    // the UI thread. A remembered folder on a network drive is taken to be there without asking the network
    // (a drive that is not connected can take many seconds to say so); Move checks again and says so.
    private async void LookUpArrivalExtras(IReadOnlyList<FileSystemItem> files)
    {
        var wanted = files.Where(file => _arrivalExtrasPending.Add(ExtrasKey(file)))
            .Select(file => (Key: ExtrasKey(file), Path: file.FullPath)).ToList();
        if (wanted.Count == 0)
            return; // already being looked up

        var generation = _arrivalExtrasGeneration;
        var clock = Stopwatch.StartNew();
        var found = new List<(string Key, DownloadOrigin? Origin, FilingSuggestion? Offer)>();
        try
        {
            found = await Task.Run(() => wanted.Select(file =>
            {
                var origin = Optional(() => DownloadOrigin.Read(file.Path), $"reading where {Path.GetFileName(file.Path)} came from");
                var offer = Optional(() => FilingMemory.SuggestFor(file.Path, origin?.Site, trustNetworkFolders: true),
                    $"finding a folder for {Path.GetFileName(file.Path)}");
                return (file.Key, origin, offer);
            }).ToList());
        }
        catch (Exception error)
        {
            ArrivalsTrace.Write($"  looking up {wanted.Count} files failed: {error.GetType().Name}: {error.Message}");
            found = [.. wanted.Select(file => (file.Key, (DownloadOrigin?)null, (FilingSuggestion?)null))]; // not asked again and again
        }
        finally
        {
            foreach (var file in wanted)
                _arrivalExtrasPending.Remove(file.Key);
        }

        if (clock.ElapsedMilliseconds >= 1000)
            ArrivalsTrace.Write($"  looking up where {wanted.Count} files came from took {clock.ElapsedMilliseconds:N0} ms");

        if (generation == _arrivalExtrasGeneration)
        {
            if (_arrivalExtras.Count > 500)
                _arrivalExtras.Clear();
            foreach (var (key, origin, offer) in found)
                _arrivalExtras[key] = (origin, offer);
        }

        QueueArrivals();
    }

    // NEW (just arrived, no waiting): the setup files in the folder, one look at a time. When it is done the
    // strip is rebuilt; that starts another look if the setup files changed in the meantime.
    private async void LookAtInstallers(string folder, string installerKey)
    {
        if (_lookingAtInstallers)
            return;

        _lookingAtInstallers = true;
        var clock = Stopwatch.StartNew();
        IReadOnlyList<InstallerFile> found;
        try
        {
            found = await Task.Run(() => InstallerShelf.Scan(folder));
        }
        catch (Exception error)
        {
            ArrivalsTrace.Write($"  looking at the installers in {folder} failed: {error.GetType().Name}: {error.Message}");
            found = [];
        }
        finally
        {
            _lookingAtInstallers = false;
        }

        ArrivalsTrace.Write($"  installers in {folder}: {found.Count} found in {clock.ElapsedMilliseconds:N0} ms");
        _installers = found;
        _installersFor = installerKey;
        _installersFolder = folder;
        QueueArrivals();
    }

    // NEW (just arrived trace): runs one of the strip's extras; when it fails, says so in the log and the
    // strip goes on without it.
    private static T? Optional<T>(Func<T?> work, string what)
    {
        try
        {
            return work();
        }
        catch (Exception error)
        {
            ArrivalsTrace.Write($"  failed {what}, left out: {error.GetType().Name}: {error.Message}");
            return default;
        }
    }

    // ---- building the strip

    private StackPanel BuildArrivals(IReadOnlyList<FileSystemItem> picks, IReadOnlyList<DownloadOrigin?> origins,
        IReadOnlyList<FilingSuggestion?> offers, DateTime now)
    {
        var title = new TextBlock { Text = "Just arrived", FontSize = 14 };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "SerifFont");

        var hint = SmallMuted(picks.Count == 0 ? "No downloads yet" : "Your newest downloads"); // CHANGED (always shown)
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
        // NEW (play from just arrived): a sound file plays in Clearspace's own player, first in the row.
        if (item.IsAudio)
            actions.Children.Add(TileAction("\uE768", "Play in Clearspace", () => PlayArrival(item)));
        actions.Children.Add(TileAction("\uE8E5", "Open", () => WithSelected(item, () => _viewModel.OpenCommand.Execute(null))));
        if (IsArchiveItem(item))
            actions.Children.Add(TileAction("\uE7B8", "Extract to a new folder here", () => WithSelected(item, () => OnExtractArchives(this, new RoutedEventArgs()))));
        if (origin is not null)
            actions.Children.Add(TileAction("\uE774", PageLabel(origin), () => OpenAddress(origin.PageToOpen)));
        actions.Children.Add(offer is null
            ? TileAction("\uE8DE", "Move to a folder…", () => MoveWithPicker(item, origin))
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
        // NEW (drag from just arrived): the press is also where a drag of the tile may start.
        Point? pressedAt = null;
        tile.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
                WithSelected(item, () => _viewModel.OpenCommand.Execute(null));
            else
                WithSelected(item, () => FileList.Focus());
            pressedAt = e.ClickCount == 1 ? e.GetPosition(tile) : null;
            e.Handled = true;
        };

        // NEW (drag from just arrived): a tile can be dragged like the file it stands for: into another
        // program (a chat window, an e-mail, a browser's upload box), onto the desktop, or onto a folder in
        // Clearspace's list, sidebar or tab strip. The drag starts once the pointer has moved the usual
        // few pixels with the button held, or has left the tile with it held (a quick flick can leave
        // the tile before the first move is reported). A press on one of the tile's buttons never gets
        // here: the button keeps it.
        tile.MouseLeftButtonUp += (_, _) => pressedAt = null;
        tile.MouseMove += (_, e) =>
        {
            if (pressedAt is not { } from)
                return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                pressedAt = null;
                return;
            }

            var at = e.GetPosition(tile);
            if (Math.Abs(at.X - from.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(at.Y - from.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            pressedAt = null;
            DragArrival(tile, item);
        };
        tile.MouseLeave += (_, e) =>
        {
            var dragging = pressedAt is not null && e.LeftButton == MouseButtonState.Pressed;
            pressedAt = null;
            if (dragging)
                DragArrival(tile, item);
        };
        return tile;
    }

    // NEW (drag from just arrived): hands the tile's file to Windows as a dragged file, the same way a
    // drag from the list does (MainWindow.xaml.cs), so everything that accepts a file from the list or
    // from File Explorer accepts it from here. Other programs usually copy it (a chat uploads it); a
    // folder on the same drive moves it. The list scrolls under the drag as it does for its own.
    private void DragArrival(Border tile, FileSystemItem item)
    {
        var path = item.FullPath;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            _viewModel.ReportStatus($"{item.Name} is no longer in this folder.");
            _arrivalsShown = string.Empty;
            QueueArrivals();
            return;
        }

        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        BeginDragScroll();
        try { DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy | DragDropEffects.Move); }
        catch (Exception) { } // the other program refused it part-way; there is nothing to undo
        finally { EndDragScroll(); }
    }

    // NEW (play from just arrived): plays a sound file in the built-in player (the bar at the bottom), or
    // pauses and resumes it when it is the one playing. The list's own row for the file is used when
    // there is one, so Next and Previous go through the folder's other sound files as they do from the list.
    private void PlayArrival(FileSystemItem item)
    {
        var row = _viewModel.Items.FirstOrDefault(candidate => string.Equals(candidate.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase)) ?? item;

        if (ReferenceEquals(_viewModel.Player.Current, row))
            _viewModel.Player.TogglePlay();
        else
            _viewModel.PlayTrack(row);
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
        if (item.IsAudio)
            menu.Items.Add(Entry("Play in Clearspace", "\uE768", () => PlayArrival(item))); // NEW (play from just arrived)
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
        menu.Items.Add(Entry(offer is null ? "Move to a folder…" : "Move to another folder…", offer is null ? "\uE8DE" : null, () => MoveWithPicker(item, origin)));
        if (offer is not null)
        {
            menu.Items.Add(Entry($"Stop suggesting \"{MenuText(offer.FolderName)}\"", null, () =>
            {
                FilingMemory.ForgetFolder(offer.Folder);
                ForgetArrivalExtras(); // CHANGED (just arrived, no waiting): the offers are kept now; ask again
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
        // CHANGED (just arrived, no waiting): only the installers of the folder on screen.
        if (_installers.Count == 0 || !string.Equals(_installersFolder, _viewModel.CurrentPath, StringComparison.OrdinalIgnoreCase))
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

    // CHANGED (move in Clearspace): the folder is chosen in Clearspace's own dialog (FolderPickerWindow.cs),
    // not in Windows' folder picker, which is File Explorer in a small window. It shows this window's
    // sidebar places and opens where the last file was moved to (this session), or in the user folder.
    // CHANGED (recommended folders): the chooser is given up to three recommended folders with reasons
    // (Services/FilingAdvisor.cs: where files like this went before, a folder named like the file, the
    // folder for its kind, folders used lately) and opens in the best of them, so the likely case is one
    // press of Move here. They are worked out first, off the UI thread (the index is asked for folders
    // named like the file); if that fails the chooser opens without them.
    private static string? s_lastMoveFolder;
    private bool _choosingMoveFolder;

    private async void MoveWithPicker(FileSystemItem item, DownloadOrigin? origin)
    {
        if (_choosingMoveFolder)
            return; // a second click while the recommendations are being worked out

        _choosingMoveFolder = true;
        try
        {
            IReadOnlyList<FolderRecommendation> recommended = [];
            try
            {
                var notes = FilingAdvisor.Notes(item.FullPath, origin?.Site);
                recommended = await Task.Run(() => FilingAdvisor.Recommend(notes));
            }
            catch (Exception)
            {
                // recommendations are a help; choosing a folder works without them
            }

            ChooseMoveFolder(item, recommended);
        }
        catch (Exception error)
        {
            MessageDialog.Show(this, error.Message, "Couldn't open the folder chooser", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _choosingMoveFolder = false;
        }
    }

    private void ChooseMoveFolder(FileSystemItem item, IReadOnlyList<FolderRecommendation> recommended)
    {
        var self = Path.TrimEndingDirectorySeparator(item.FullPath);
        var from = Path.GetDirectoryName(self) ?? string.Empty;

        static bool Same(string one, string other) => string.Equals(
            Path.TrimEndingDirectorySeparator(one), Path.TrimEndingDirectorySeparator(other), StringComparison.OrdinalIgnoreCase);

        // The folder the file is in can be looked through (the place wanted may be inside it), not chosen.
        string? WhyNotChoose(string folder)
        {
            if (Same(folder, from))
                return $"{item.Name} is in this folder now. Open the folder it should go to.";

            if (item.IsFolder && (Same(folder, self) || folder.StartsWith(self + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                return "A folder can't be moved into itself.";

            return null;
        }

        // A folder that is locked with a password right now has nothing in it to look through.
        static string? WhyNotEnter(string folder)
        {
            try
            {
                return FileLockRegistry.Governing(folder) is { State: LockState.Locked }
                    ? "That folder is locked with a password. Unlock it first, then move the file."
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        var picker = new FolderPickerWindow(new FolderPickerOptions(
            $"Move {item.Name}",
            "Open the folder it should go in, then choose Move here.",
            "Move here",
            recommended.Count > 0 ? recommended[0].Folder : s_lastMoveFolder ?? MainViewModel.LibraryRoot)
        {
            Recommended = recommended,
            Places = [.. _viewModel.Sidebar],
            ShowHidden = _viewModel.ShowHiddenItems,
            WhyNotEnter = WhyNotEnter,
            WhyNotChoose = WhyNotChoose
        })
        {
            Owner = this
        };

        if (picker.ShowDialog() != true || picker.Chosen is not { } folder)
            return;

        s_lastMoveFolder = folder;
        MoveTo(item, folder);
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

        ForgetArrivalExtras(); // CHANGED (just arrived, no waiting): what was learned from this move may change the other tiles' offers
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
