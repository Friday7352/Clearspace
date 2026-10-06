// Clearspace | NEW (duplicates): the "Find duplicates" page.
// Opens over the file list, like Disk usage, on the folder you were in (or the folder you right-clicked).
// On an indexed drive the list of files comes from the file index, so the search starts comparing at once.
// It looks for files that are exact copies of each other (Services/DuplicateFinder.cs), lists them in
// groups with the most wasteful first, and lets you tick the copies you don't want. "Move to Recycle Bin"
// then removes the ticked ones. Two rules are never broken:
//   - one copy of every file always stays: the last one left in a group can't be ticked
//   - a ticked file is only removed if it has not changed since the search and an unticked copy is still
//     there (checked again at the moment of removing)
// "Changed" means its size or its modified date differs; the file is not read a second time.
// Removed files go to the Recycle Bin, so they can be put back (on a drive without one, Windows asks
// before deleting for good, and the question here says so).
// Built in code from the shared styles in App.xaml and the theme's colours, so it follows every theme.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace;

internal sealed class DuplicatesView : UserControl, IDisposable
{
    private const int GroupsPerPage = 60;   // more are added with "Show more"
    private const int RowsPerGroup = 100;   // a group with more copies than this lists the first hundred

    private readonly string _folder;
    // Paths are compared exactly as the search returned them: a folder can be set to tell "Foo" from "foo".
    private readonly HashSet<string> _ticked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _boxes = new(StringComparer.Ordinal);
    private readonly StackPanel _list = new();
    private readonly TextBlock _glyph;
    private readonly TextBlock _headline;
    private readonly TextBlock _detail;
    private readonly ProgressBar _bar;
    private readonly Button _stop;
    private readonly Button _again;
    private readonly CheckBox _subfolders;
    private readonly Border _bottom;
    private readonly TextBlock _tickedText;
    private readonly Button _remove;
    private List<DuplicateGroup> _groups = [];
    private CancellationTokenSource? _cancel;
    private int _run;          // goes up with every search; a search that ends for an older one is dropped
    private int _shownGroups;
    private bool _disposed;

    public event EventHandler? CloseRequested;

    // Files were moved to the Recycle Bin: the file list behind this page is out of date.
    public event EventHandler? FileOperationCompleted;

    // "Show in folder" on a row: the window closes this page and goes to the file.
    public event EventHandler<string>? ShowFileRequested;

    internal DuplicatesView(string folder)
    {
        _folder = folder;
        UseLayoutRounding = true;
        FontSize = 13;
        Focusable = true;
        SetResourceReference(BackgroundProperty, "Base");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(FontFamilyProperty, "UIFont");

        // ---- header: Back, the title, and the search options
        var back = TextButton("Back", primary: false);
        back.Margin = new Thickness(0, 0, 18, 0);
        back.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        DockPanel.SetDock(back, Dock.Left);

        _subfolders = new CheckBox
        {
            Content = "Look in subfolders too",
            IsChecked = SettingsService.GetDuplicatesIncludeSubfolders(),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0)
        };
        _subfolders.SetResourceReference(StyleProperty, "ViewerCheck");
        _subfolders.Click += (_, _) =>
        {
            SettingsService.SetDuplicatesIncludeSubfolders(_subfolders.IsChecked == true);
            StartSearch();
        };

        // CHANGED (duplicates from the index): after a search whose file list came from the index, this
        // button offers the slower search that reads the folders themselves, in case the index missed something.
        _again = TextButton("Search again", primary: false);
        _again.Click += (_, _) => StartSearch(fromDisk: _listedFromIndex);

        var options = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        options.Children.Add(_subfolders);
        options.Children.Add(_again);
        DockPanel.SetDock(options, Dock.Right);

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        var title = new TextBlock
        {
            Text = name.Length > 0 ? $"Duplicates in {name}" : $"Duplicates on {folder}",
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var where = Muted(folder);
        where.Margin = new Thickness(0, 5, 0, 0);
        where.TextTrimming = TextTrimming.CharacterEllipsis;
        where.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        where.FontSize = 11.5;

        var titles = new StackPanel();
        titles.Children.Add(title);
        titles.Children.Add(where);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
        header.Children.Add(back);
        header.Children.Add(options);
        header.Children.Add(titles);

        // ---- what is happening / what was found
        _glyph = new TextBlock { FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");

        var badge = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = _glyph
        };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        DockPanel.SetDock(badge, Dock.Left);

        _stop = TextButton("Stop", primary: false);
        _stop.VerticalAlignment = VerticalAlignment.Top;
        _stop.Click += (_, _) => _cancel?.Cancel();
        DockPanel.SetDock(_stop, Dock.Right);

        _headline = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        _detail = Muted(string.Empty);
        _detail.Margin = new Thickness(0, 4, 0, 0);
        _detail.TextTrimming = TextTrimming.CharacterEllipsis;

        _bar = new ProgressBar { Height = 3, Minimum = 0, Maximum = 1, BorderThickness = new Thickness(0), Margin = new Thickness(0, 12, 0, 0) };
        _bar.SetResourceReference(ForegroundProperty, "Accent");
        _bar.SetResourceReference(BackgroundProperty, "Line");

        var words = new StackPanel();
        words.Children.Add(_headline);
        words.Children.Add(_detail);
        words.Children.Add(_bar);

        var statusRow = new DockPanel();
        statusRow.Children.Add(badge);
        statusRow.Children.Add(_stop);
        statusRow.Children.Add(words);
        var status = Card(statusRow);
        status.Margin = new Thickness(0, 0, 0, 14);

        // ---- the groups
        var scroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        // ---- bottom bar: quick ticks on the left, what is ticked and the remove button on the right
        var newest = TextButton("Keep the newest of each", primary: false);
        newest.Margin = new Thickness(0);
        newest.ToolTip = "Ticks every copy except the most recently changed one in each group";
        newest.Click += (_, _) => TickAll(DuplicateFinder.AllButNewest);
        var oldest = TextButton("Keep the oldest of each", primary: false);
        oldest.ToolTip = "Ticks every copy except the oldest one in each group (usually the original)";
        oldest.Click += (_, _) => TickAll(DuplicateFinder.AllButOldest);
        var none = TextButton("Untick all", primary: false);
        none.Click += (_, _) => TickAll(_ => []);

        var quick = new StackPanel { Orientation = Orientation.Horizontal };
        quick.Children.Add(newest);
        quick.Children.Add(oldest);
        quick.Children.Add(none);
        DockPanel.SetDock(quick, Dock.Left);

        _remove = TextButton("Move to Recycle Bin", primary: true);
        _remove.Click += (_, _) => RemoveTicked();
        DockPanel.SetDock(_remove, Dock.Right);

        _tickedText = Muted(string.Empty);
        _tickedText.VerticalAlignment = VerticalAlignment.Center;
        _tickedText.TextAlignment = TextAlignment.Right;
        _tickedText.Margin = new Thickness(14, 0, 8, 0);

        var bar = new DockPanel();
        bar.Children.Add(quick);
        bar.Children.Add(_remove);
        bar.Children.Add(_tickedText);
        _bottom = Card(bar);
        _bottom.Padding = new Thickness(16, 12, 16, 12);
        _bottom.Margin = new Thickness(0, 14, 0, 0);

        var page = new Grid { Margin = new Thickness(28, 24, 28, 24), MaxWidth = 1240 };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(status, 1);
        Grid.SetRow(scroll, 2);
        Grid.SetRow(_bottom, 3);
        page.Children.Add(header);
        page.Children.Add(status);
        page.Children.Add(scroll);
        page.Children.Add(_bottom);
        Content = page;

        Loaded += (_, _) =>
        {
            if (_disposed || _run > 0)
                return;

            Focus();
            StartSearch();
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _run++;
        _cancel?.Cancel();
        _cancel?.Dispose();
        _cancel = null;
    }

    // ---- searching

    private bool _listedFromIndex; // the last finished search took its list of files from the index

    // fromDisk: read the folders themselves even where the index could give the list of files.
    private async void StartSearch(bool fromDisk = false)
    {
        if (_disposed)
            return;

        _cancel?.Cancel();
        _cancel?.Dispose();
        var cancel = _cancel = new CancellationTokenSource();
        var run = ++_run;

        _groups = [];
        _ticked.Clear();
        _boxes.Clear();
        _list.Children.Clear();
        _shownGroups = 0;
        ShowBusy();

        var progress = new Progress<DuplicateProgress>(report =>
        {
            if (run != _run)
                return;

            if (report.Total > 0)
            {
                _bar.IsIndeterminate = false;
                _bar.Value = Math.Clamp((double)report.Done / report.Total, 0, 1);
                _headline.Text = "Comparing files…";
                _detail.Text = $"{FileSystemItem.FormatSize(report.Done)} of {FileSystemItem.FormatSize(report.Total)}   {report.Current}";
            }
            else if (report.Stage == "Listing files")
            {
                _detail.Text = $"{report.Done:N0} files so far   {report.Current}";
            }
        });

        try
        {
            var options = new DuplicateOptions(Subfolders: _subfolders.IsChecked == true, Hidden: SettingsService.GetShowHiddenItems());
            var scan = await DuplicateFinder.FindAsync(_folder, options, progress, cancel.Token,
                fromDisk ? null : DuplicateIndex.TryList);
            if (run != _run)
                return;

            _groups = [.. scan.Groups];
            ShowDone(scan);
        }
        catch (OperationCanceledException)
        {
            if (run == _run)
                ShowMessage("\uE71A", "Search stopped", "Nothing was changed. \"Search again\" starts it over.");
        }
        catch (Exception error)
        {
            // Whatever went wrong, the page must not be left saying "Listing files…" for ever.
            if (run == _run)
                ShowMessage("\uE7BA", "Couldn't search this folder", error.Message);
        }
    }

    private void ShowBusy()
    {
        _glyph.Text = "\uE721"; // Search
        _headline.Text = "Listing files…";
        _detail.Text = _folder;
        _bar.IsIndeterminate = true;
        _bar.Visibility = Visibility.Visible;
        _stop.Visibility = Visibility.Visible;
        _again.IsEnabled = false;
        _bottom.Visibility = Visibility.Collapsed;
    }

    private void ShowMessage(string glyph, string headline, string detail)
    {
        _glyph.Text = glyph;
        _headline.Text = headline;
        _detail.Text = detail;
        _bar.IsIndeterminate = false;
        _bar.Visibility = Visibility.Collapsed;
        _stop.Visibility = Visibility.Collapsed;
        _again.IsEnabled = true;
        _bottom.Visibility = _groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowDone(DuplicateScan scan)
    {
        var skipped = scan.Skipped > 0
            ? $" {scan.Skipped:N0} were left out (in use, no permission, a link, or changed during the search)."
            : string.Empty;
        if (scan.FromIndex)
            skipped += " The list of files came from the index.";

        _listedFromIndex = scan.FromIndex;
        _again.Content = scan.FromIndex ? "Search the disk instead" : "Search again";
        _again.ToolTip = scan.FromIndex
            ? "Reads every folder instead of using the index. Slower, but sees files the index has not caught up with."
            : null;

        if (scan.Groups.Count == 0)
        {
            ShowMessage("\uE73E", "No duplicates here", $"{scan.FilesChecked:N0} files checked; no two are the same.{skipped}");
            return;
        }

        ShowMessage("\uE8C8",
            $"{FileSystemItem.FormatSize(scan.Wasted)} taken up by copies",
            $"{scan.Copies:N0} extra {(scan.Copies == 1 ? "copy" : "copies")} in {scan.Groups.Count:N0} {(scan.Groups.Count == 1 ? "group" : "groups")}, from {scan.FilesChecked:N0} files checked.{skipped}");
        ShowMoreGroups();
        UpdateTicked();
    }

    // ---- the list

    private void ShowMoreGroups()
    {
        if (_list.Children.Count > 0 && _list.Children[^1] is Button more)
            _list.Children.Remove(more);

        foreach (var group in _groups.Skip(_shownGroups).Take(GroupsPerPage))
            _list.Children.Add(GroupCard(group));
        _shownGroups = Math.Min(_groups.Count, _shownGroups + GroupsPerPage);

        if (_shownGroups >= _groups.Count)
            return;

        var next = TextButton($"Show more ({_groups.Count - _shownGroups:N0} groups not shown yet)", primary: false);
        next.Margin = new Thickness(0, 4, 0, 4);
        next.HorizontalAlignment = HorizontalAlignment.Center;
        next.Click += (_, _) => ShowMoreGroups();
        _list.Children.Add(next);
    }

    private Border GroupCard(DuplicateGroup group)
    {
        var heading = new TextBlock { FontWeight = FontWeights.SemiBold, Text = $"{group.Files.Count:N0} copies, {FileSystemItem.FormatSize(group.Size)} each" };
        var frees = Muted($"keeping one frees {FileSystemItem.FormatSize(group.Wasted)}");
        frees.Margin = new Thickness(12, 0, 0, 0);

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        top.Children.Add(heading);
        top.Children.Add(frees);

        var rows = new StackPanel();
        rows.Children.Add(top);
        foreach (var file in group.Files.Take(RowsPerGroup))
            rows.Children.Add(Row(group, file));
        if (group.Files.Count > RowsPerGroup)
            rows.Children.Add(Muted($"… and {group.Files.Count - RowsPerGroup:N0} more copies not listed. The buttons below tick them too."));

        var card = Card(rows);
        card.Padding = new Thickness(16, 12, 12, 10);
        card.Margin = new Thickness(0, 0, 0, 8);
        return card;
    }

    private Grid Row(DuplicateGroup group, DuplicateFile file)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var box = new CheckBox { IsChecked = _ticked.Contains(file.FullPath), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Tick to remove this copy" };
        box.SetResourceReference(StyleProperty, "ViewerCheck");
        box.Click += (_, _) =>
        {
            if (box.IsChecked == true)
            {
                if (!DuplicateFinder.CanTick(group, file, _ticked))
                {
                    box.IsChecked = false;
                    _tickedText.Text = "One copy of each file is always kept.";
                    return;
                }

                _ticked.Add(file.FullPath);
            }
            else
            {
                _ticked.Remove(file.FullPath);
            }

            UpdateTicked();
        };
        _boxes[file.FullPath] = box;

        var name = new TextBlock { Text = file.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        var folder = Muted(file.Folder);
        folder.FontSize = 11.5;
        folder.TextWrapping = TextWrapping.NoWrap;
        folder.TextTrimming = TextTrimming.CharacterEllipsis;
        var names = new StackPanel { Margin = new Thickness(12, 0, 12, 0), ToolTip = file.FullPath };
        names.Children.Add(name);
        names.Children.Add(folder);
        Grid.SetColumn(names, 1);

        var date = Muted(file.Modified.ToString("g"));
        date.VerticalAlignment = VerticalAlignment.Center;
        date.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(date, 2);

        var show = new Button { Content = "\uE838", ToolTip = "Show in its folder" }; // FolderOpen
        show.SetResourceReference(StyleProperty, "IconButton");
        show.Click += (_, _) => ShowFileRequested?.Invoke(this, file.FullPath);
        Grid.SetColumn(show, 3);

        row.Children.Add(box);
        row.Children.Add(names);
        row.Children.Add(date);
        row.Children.Add(show);
        return row;
    }

    // ---- ticking

    // Ticks, in every group (listed or not), the files `choose` returns for it.
    private void TickAll(Func<DuplicateGroup, IEnumerable<DuplicateFile>> choose)
    {
        _ticked.Clear();
        foreach (var group in _groups)
        {
            foreach (var file in choose(group))
                _ticked.Add(file.FullPath);
        }

        foreach (var (path, box) in _boxes)
            box.IsChecked = _ticked.Contains(path);
        UpdateTicked();
    }

    private void UpdateTicked()
    {
        var size = _groups.Sum(group => group.Size * group.Files.Count(file => _ticked.Contains(file.FullPath)));
        _tickedText.Text = _ticked.Count == 0
            ? "Tick the copies you don't want."
            : $"{_ticked.Count:N0} {(_ticked.Count == 1 ? "file" : "files")} ticked, {FileSystemItem.FormatSize(size)}";
        _remove.IsEnabled = _ticked.Count > 0;
    }

    // ---- removing

    private void RemoveTicked()
    {
        var remove = DuplicateFinder.Removable(_groups, _ticked, DuplicateFinder.ProbeDisk, out var heldBack);
        if (remove.Count == 0)
        {
            _tickedText.Text = heldBack > 0
                ? "Those files have changed since the search, so nothing was removed. Search again."
                : "Tick the copies you don't want.";
            return;
        }

        // A network folder and some removable drives have no Recycle Bin: Windows then asks whether to
        // delete for good. Say so here rather than promise something that is not true there.
        var window = Window.GetWindow(this);
        var size = remove.Sum(file => file.Size);
        var bin = remove.All(file => FileOperationService.HasRecycleBin(file.FullPath));
        var count = $"{remove.Count:N0} {(remove.Count == 1 ? "file" : "files")} ({FileSystemItem.FormatSize(size)})";
        var question = bin
            ? $"Move {count} to the Recycle Bin?\n\nOne copy of each stays where it is. You can put the others back from the Recycle Bin."
            : $"Remove {count}?\n\nOne copy of each stays where it is. Some of these are on a drive that may have no Recycle Bin " +
              "(a network folder or a removable drive). Windows will ask before deleting those for good; they can't be put back.";
        if (MessageDialog.Show(window, question, "Remove duplicates", MessageBoxButton.YesNo, MessageBoxImage.Question,
                bin ? MessageBoxResult.Yes : MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        // Checked once more now that the question has been answered: the dialog may have been open a while.
        remove = DuplicateFinder.Removable(_groups, _ticked, DuplicateFinder.ProbeDisk, out heldBack);
        if (remove.Count == 0)
        {
            _tickedText.Text = "Those files have changed since the search, so nothing was removed. Search again.";
            return;
        }

        var owner = window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
        var result = FileOperationService.Delete(remove.Select(file => file.FullPath).ToList(), owner);

        // Windows may have skipped some (in use), or the question it asks itself was answered No: go by
        // what is really gone.
        var gone = remove.Where(file => !File.Exists(file.FullPath)).ToList();
        var gonePaths = gone.Select(file => file.FullPath).ToHashSet(StringComparer.Ordinal);

        _groups = _groups
            .Select(group => group.Files.Any(file => gonePaths.Contains(file.FullPath))
                ? new DuplicateGroup(group.Size, group.Files.Where(file => !gonePaths.Contains(file.FullPath)).ToList())
                : group)
            .Where(group => group.Files.Count > 1)
            .ToList();

        var left = _groups.SelectMany(group => group.Files).Select(file => file.FullPath).ToHashSet(StringComparer.Ordinal);
        _ticked.RemoveWhere(path => !left.Contains(path));

        _boxes.Clear();
        _list.Children.Clear();
        _shownGroups = 0;
        ShowMoreGroups();

        var freed = gone.Sum(file => file.Size);
        var notRemoved = remove.Count - gone.Count + heldBack;
        var summary = gone.Count == 0
            ? (result.Succeeded ? "Nothing was removed." : $"Nothing was removed. {result.Message}")
            : $"{(bin ? "Moved" : "Removed")} {gone.Count:N0} {(gone.Count == 1 ? "file" : "files")}{(bin ? " to the Recycle Bin" : string.Empty)}, freeing {FileSystemItem.FormatSize(freed)}.";
        if (gone.Count > 0 && notRemoved > 0)
            summary += $" {notRemoved:N0} left alone (changed since the search, or in use).";

        var remaining = _groups.Sum(group => group.Wasted);
        ShowMessage(_groups.Count == 0 ? "\uE73E" : "\uE8C8",
            _groups.Count == 0 ? "No duplicates left" : $"{FileSystemItem.FormatSize(remaining)} still taken up by copies",
            summary);
        UpdateTicked();

        if (gone.Count > 0)
            FileOperationCompleted?.Invoke(this, EventArgs.Empty);
    }

    // ---- small builders

    private static Border Card(UIElement child)
    {
        var card = new Border { Padding = new Thickness(20), Child = child };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.CornerRadiusProperty, "R12");
        return card;
    }

    private static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        return block;
    }

    private static Button TextButton(string label, bool primary)
    {
        var button = new Button { Content = label };
        button.SetResourceReference(StyleProperty, primary ? "ViewerTextButtonPrimary" : "ViewerTextButton");
        return button;
    }
}
