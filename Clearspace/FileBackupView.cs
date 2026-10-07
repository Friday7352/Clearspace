// Clearspace | NEW (file backup): the "Backup and restore" page.
// Opens over the file list, like Disk usage and Find duplicates, from Settings > "Backup and restore…" or
// from "Back up…" in the right-click menu of a file, a folder or a drive. It lists your backups, one card
// each:
//     the name, what is backed up and where to
//     its kind, its schedule and what it keeps, in one line
//     how the last run went (and when the next one is), or what a run in progress is doing
//     Back up now (Stop while it runs), Restore…, and a menu with Edit…, Open the backup's folder,
//     Show the last report and Remove
// "New backup" opens the editor (FileBackupEditWindow.cs); Restore… opens FileBackupRestoreWindow.cs.
// CHANGED (simpler page): a card is four short lines now (name, from and to, how it stands, kind and
// schedule with the next run); what a backup keeps and the longer explanations are tooltips.
// At the bottom: whether scheduled backups also run while Clearspace is closed (FileBackupTask.cs).
//
// The page never runs a backup itself. "Back up now" starts a second Clearspace without a window
// (FileBackupRunner.cs), so the backup carries on when this page, or Clearspace, is closed. The page looks
// a little more than once a second at the small files that process writes (is it running, how far is it,
// how did it go) and shows them; that reading is done off the UI thread.
//
// FileBackupUi at the end of this file holds the small builders the page and its three dialogs share.
// Built in code from the shared styles in App.xaml and the theme's colours, so it follows every theme.
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Clearspace.Models;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

internal sealed class FileBackupView : UserControl, IDisposable
{
    private readonly FileBackupStore _store = FileBackupStore.Default;
    private readonly IReadOnlyList<SidebarEntry> _places;
    private readonly StackPanel _list = new();
    private readonly Dictionary<string, PlanCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly CheckBox _whenClosed;
    private readonly TextBlock _status;
    private readonly DispatcherTimer _timer;
    private bool _looking;
    private bool _disposed;

    public event EventHandler? CloseRequested;

    // "Open the backup's folder": the window closes this page and goes there.
    public event EventHandler<string>? ShowFolderRequested;

    /// <param name="places">The window's sidebar entries, for Clearspace's own folder chooser.</param>
    internal FileBackupView(IReadOnlyList<SidebarEntry> places)
    {
        _places = places;
        UseLayoutRounding = true;
        FontSize = 13;
        Focusable = true;
        SetResourceReference(BackgroundProperty, "Base");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(FontFamilyProperty, "UIFont");

        // ---- header: Back, the title, New backup
        var back = FileBackupUi.TextButton("Back", primary: false);
        back.Margin = new Thickness(0, 0, 18, 0);
        back.VerticalAlignment = VerticalAlignment.Top;
        back.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        DockPanel.SetDock(back, Dock.Left);

        var add = FileBackupUi.TextButton("New backup", primary: true);
        add.VerticalAlignment = VerticalAlignment.Top;
        add.Click += (_, _) => NewBackup(null);
        DockPanel.SetDock(add, Dock.Right);

        var title = new TextBlock { Text = "Backup and restore", FontSize = 26, FontWeight = FontWeights.SemiBold };
        var about = FileBackupUi.Muted("Copies of your files, folders and drives."); // CHANGED (simpler page)
        about.Margin = new Thickness(0, 5, 0, 0);
        var titles = new StackPanel();
        titles.Children.Add(title);
        titles.Children.Add(about);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
        header.Children.Add(back);
        header.Children.Add(add);
        header.Children.Add(titles);

        // ---- the backups
        var scroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        // ---- bottom: running while Clearspace is closed, and what just happened
        // CHANGED (simpler page): one line; what it means is its tooltip.
        _whenClosed = new CheckBox
        {
            Content = "Run scheduled backups when Clearspace is closed",
            IsChecked = _store.Load().RunWhenClosed,
            ToolTip = "Uses one entry in Windows' Task Scheduler that looks every 15 minutes whether a backup is due.\n" +
                      "Off, scheduled backups run only while Clearspace is open."
        };
        _whenClosed.SetResourceReference(StyleProperty, "ViewerCheck");
        _whenClosed.Click += (_, _) => Change(settings => settings.RunWhenClosed = _whenClosed.IsChecked == true);

        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        var bottomStack = new StackPanel();
        bottomStack.Children.Add(_whenClosed);
        bottomStack.Children.Add(_status);
        var bottom = FileBackupUi.Card(bottomStack);
        bottom.Padding = new Thickness(18, 14, 18, 14);
        bottom.Margin = new Thickness(0, 14, 0, 0);

        var page = new Grid { Margin = new Thickness(28, 24, 28, 24), MaxWidth = 1040 };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(scroll, 1);
        Grid.SetRow(bottom, 2);
        page.Children.Add(header);
        page.Children.Add(scroll);
        page.Children.Add(bottom);
        Content = page;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _timer.Tick += async (_, _) => await LookAsync();

        Loaded += (_, _) =>
        {
            if (_disposed)
                return;

            Focus();
            _timer.Start();
        };

        Rebuild();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
    }

    // ------------------------------------------------------------------ the list

    // Every card again, from what is saved. Called after anything about the plans changes.
    private void Rebuild()
    {
        var settings = _store.Load();
        _whenClosed.IsChecked = settings.RunWhenClosed;
        _list.Children.Clear();
        _cards.Clear();

        if (settings.Plans.Count == 0)
        {
            _list.Children.Add(Empty());
            return;
        }

        foreach (var plan in settings.Plans)
        {
            var card = new PlanCard(this, plan);
            _cards[plan.Id] = card;
            _list.Children.Add(card.Root);
        }

        _ = LookAsync();
    }

    private Border Empty()
    {
        var badge = FileBackupUi.Badge(""); // Save
        badge.HorizontalAlignment = HorizontalAlignment.Center;
        badge.Margin = new Thickness(0, 0, 0, 14);

        var headline = new TextBlock { Text = "No backups yet", FontSize = 17, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        var text = FileBackupUi.Muted("Copy a file, a folder or a whole drive to another place, by hand or on a schedule."); // CHANGED (simpler page)
        text.TextAlignment = TextAlignment.Center;
        text.MaxWidth = 520;
        text.Margin = new Thickness(0, 6, 0, 16);

        var add = FileBackupUi.TextButton("New backup", primary: true);
        add.HorizontalAlignment = HorizontalAlignment.Center;
        add.Click += (_, _) => NewBackup(null);

        var stack = new StackPanel { Margin = new Thickness(0, 26, 0, 26) };
        stack.Children.Add(badge);
        stack.Children.Add(headline);
        stack.Children.Add(text);
        stack.Children.Add(add);
        return FileBackupUi.Card(stack);
    }

    /// <summary>What the page reads about one plan each time it looks.</summary>
    private sealed record Live(string Id, bool Running, FileBackupProgress? Progress, FileBackupState State, bool CutShort);

    // Reads, off the UI thread, how every plan on the page is doing, and shows it.
    private async Task LookAsync()
    {
        if (_looking || _disposed || _cards.Count == 0)
            return;

        _looking = true;
        try
        {
            var ids = _cards.Keys.ToList();
            var store = _store;
            var live = await Task.Run(() => ids.Select(id =>
            {
                var running = FileBackupRunner.IsRunning(id, store);
                return new Live(id, running, running ? store.Progress(id) : null, store.State(id),
                    !running && FileBackupRunner.WasCutShort(id, store));
            }).ToList());

            if (_disposed)
                return;

            foreach (var item in live)
            {
                if (_cards.TryGetValue(item.Id, out var card))
                    card.Show(item);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // shown at the next look
        }
        finally
        {
            _looking = false;
        }
    }

    // ------------------------------------------------------------------ doing things

    /// <summary>
    /// "Back up…" from the right-click menu: opens the editor for this file, folder or drive, unless a
    /// backup of exactly that is already here, which is then said instead.
    /// </summary>
    internal void NewBackupFor(string source)
    {
        var existing = _store.Load().Plans.FirstOrDefault(plan => FileBackupPaths.Same(plan.Source, source));
        if (existing is not null)
        {
            ShowStatus($"{source} is already backed up by \"{existing.Name}\".", problem: false);
            return;
        }

        NewBackup(source);
    }

    private void NewBackup(string? source)
    {
        var editor = new FileBackupEditWindow(null, _store.Load().Plans, _places, source) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() != true || editor.Result is not { } plan)
            return;

        Change(settings => settings.Plans.Add(plan));
        if (editor.RunNow)
            Start(plan.Id);
        else
            ShowStatus(plan.Schedule.IsOn ? $"\"{plan.Name}\" saved. Its first backup starts in a minute or two." : $"\"{plan.Name}\" saved.", problem: false);
    }

    private void Edit(FileBackupPlan plan)
    {
        var editor = new FileBackupEditWindow(plan, _store.Load().Plans, _places, null) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() != true || editor.Result is not { } changed)
            return;

        Change(settings =>
        {
            var index = settings.Plans.FindIndex(found => found.Id == changed.Id);
            if (index >= 0)
                settings.Plans[index] = changed;
        });
        if (editor.RunNow)
            Start(changed.Id);
        else
            ShowStatus($"\"{changed.Name}\" saved.", problem: false);
    }

    private void Remove(FileBackupPlan plan)
    {
        if (FileBackupRunner.IsRunning(plan.Id, _store))
        {
            ShowStatus($"\"{plan.Name}\" is running. Stop it first.", problem: true);
            return;
        }

        var answer = MessageDialog.Show(Window.GetWindow(this),
            $"Remove the backup \"{plan.Name}\"?\n\nIts copies stay where they are, in {plan.Root}.",
            "Remove this backup?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        Change(settings => settings.Plans.RemoveAll(found => found.Id == plan.Id));
        _store.Forget(plan.Id);
        ShowStatus($"\"{plan.Name}\" removed. Its copies are still in {plan.Root}.", problem: false);
    }

    // Saves a change to the plans (or to "run when closed"), shows the cards again, and brings Windows'
    // Task Scheduler in step off the UI thread.
    private async void Change(Action<FileBackupSettings> change)
    {
        try
        {
            var now = _store.Load();
            var next = new FileBackupSettings { RunWhenClosed = now.RunWhenClosed, Plans = now.Plans.Select(plan => plan.Copy()).ToList() };
            change(next);
            _store.Save(next);
            Rebuild();

            var trouble = await Task.Run(() => FileBackupTask.Sync(next));
            if (trouble is not null && !_disposed)
                ShowStatus(trouble, problem: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowStatus("That could not be saved: " + error.Message, problem: true);
        }
    }

    private void Start(string id)
    {
        if (!FileBackupRunner.Start(id))
        {
            ShowStatus("The backup could not be started. Try again.", problem: true);
            return;
        }

        ShowStatus(string.Empty, problem: false);
        if (_cards.TryGetValue(id, out var card))
            card.Starting();
    }

    private void ShowStatus(string text, bool problem)
    {
        _status.Text = text;
        _status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "Danger" : "InkMuted");
    }

    private void ShowReport(FileBackupPlan plan)
    {
        var path = _store.State(plan.Id).LogPath;
        if (path is null || !File.Exists(path))
        {
            ShowStatus($"\"{plan.Name}\" has no report yet.", problem: false);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true })?.Dispose();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            ShowStatus($"The report is in {path}, but it could not be opened: {error.Message}", problem: true);
        }
    }

    // ------------------------------------------------------------------ one backup's card

    private sealed class PlanCard
    {
        private readonly FileBackupView _page;
        private readonly FileBackupPlan _plan;
        private readonly TextBlock _statusGlyph;
        private readonly TextBlock _statusText;
        private readonly TextBlock _detail;
        private readonly ProgressBar _bar;
        private readonly Button _run;
        private readonly Button _restore;
        private readonly string _keeps;         // "keeps removed files for 30 days": the small line's tooltip
        private bool _running;
        private bool _stopping;
        private DateTime _startingUntil;       // "Back up now" was just chosen: the other process is not up yet
        private DateTime? _endBeforeStart;     // the last run's end as it was then

        internal Border Root { get; }

        internal PlanCard(FileBackupView page, FileBackupPlan plan)
        {
            _page = page;
            _plan = plan;
            _keeps = FileBackupWords.Keeps(plan);

            var badge = FileBackupUi.Badge(plan.SourceIsFile ? "" : FileBackupPaths.IsDriveRoot(plan.Source) ? "" : ""); // Page / HardDrive / Folder
            DockPanel.SetDock(badge, Dock.Left);

            // ---- right: Back up now / Stop, Restore…, more
            _run = FileBackupUi.TextButton("Back up now", primary: true);
            _run.Click += (_, _) => RunOrStop();
            _restore = FileBackupUi.TextButton("Restore…", primary: false);
            _restore.Click += (_, _) =>
                new FileBackupRestoreWindow(_plan, _page._places, folder => _page.ShowFolderRequested?.Invoke(_page, folder)) { Owner = Window.GetWindow(_page) }.ShowDialog();

            var more = FileBackupUi.IconButton("", "More"); // More
            var menu = new ContextMenu();
            menu.Items.Add(Entry("Edit…", () => _page.Edit(_plan)));
            menu.Items.Add(Entry("Open the backup's folder", OpenFolder));
            menu.Items.Add(Entry("Show the last report", () => _page.ShowReport(_plan)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Entry("Remove this backup…", () => _page.Remove(_plan)));
            more.Click += (_, _) =>
            {
                menu.PlacementTarget = more;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            buttons.Children.Add(_run);
            buttons.Children.Add(_restore);
            buttons.Children.Add(more);
            DockPanel.SetDock(buttons, Dock.Right);

            // ---- middle: what it is
            var name = new TextBlock { Text = plan.Name, FontSize = 17, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };

            var route = FileBackupUi.Muted($"{plan.Source}   →   {plan.Root}");
            route.Margin = new Thickness(0, 4, 0, 0);
            route.FontSize = 11.5;
            route.TextWrapping = TextWrapping.NoWrap;
            route.TextTrimming = TextTrimming.CharacterEllipsis;
            route.ToolTip = $"From {plan.Source}\nTo {plan.Root}";
            route.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");

            // ---- how it stands
            _statusGlyph = new TextBlock { FontSize = 13, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            _statusGlyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            DockPanel.SetDock(_statusGlyph, Dock.Left);
            _statusText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var statusRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            statusRow.Children.Add(_statusGlyph);
            statusRow.Children.Add(_statusText);

            _detail = FileBackupUi.Muted(string.Empty);
            _detail.FontSize = 11.5;
            _detail.Margin = new Thickness(21, 3, 0, 0);
            _detail.TextWrapping = TextWrapping.NoWrap;
            _detail.TextTrimming = TextTrimming.CharacterEllipsis;

            _bar = new ProgressBar { Height = 3, IsIndeterminate = true, BorderThickness = new Thickness(0), Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
            _bar.SetResourceReference(ForegroundProperty, "Accent");
            _bar.SetResourceReference(BackgroundProperty, "Line");

            var words = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            words.Children.Add(name);
            words.Children.Add(route);
            words.Children.Add(statusRow);
            words.Children.Add(_detail);
            words.Children.Add(_bar);

            var row = new DockPanel();
            row.Children.Add(badge);
            row.Children.Add(buttons);
            row.Children.Add(words);

            Root = FileBackupUi.Card(row);
            Root.Margin = new Thickness(0, 0, 0, 10);
            Say("", "Looking…", FileBackupTone.Quiet, FileBackupWords.Summary(plan)); // Recent
        }

        private static MenuItem Entry(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => action();
            return item;
        }

        private void OpenFolder()
        {
            if (!Directory.Exists(_plan.Root))
            {
                _page.ShowStatus($"{_plan.Root} is not there (yet).", problem: false);
                return;
            }

            _page.ShowFolderRequested?.Invoke(_page, _plan.Root);
        }

        private void RunOrStop()
        {
            if (_running)
            {
                _stopping = true;
                FileBackupRunner.RequestStop(_plan.Id, _page._store);
                _run.IsEnabled = false;
                _run.Content = "Stopping…";
                return;
            }

            _page.Start(_plan.Id);
        }

        // "Back up now" was chosen: until the other process shows up (a second or two), say so.
        internal void Starting()
        {
            _startingUntil = DateTime.UtcNow.AddSeconds(10);
            _endBeforeStart = _page._store.State(_plan.Id).LastEndUtc;
            _run.IsEnabled = false;
            Say("", "Starting…", FileBackupTone.Quiet, string.Empty, running: true); // Sync
            _bar.Visibility = Visibility.Visible;
        }

        internal void Show(Live live)
        {
            _running = live.Running;
            if (live.Running)
            {
                _startingUntil = default;
                var progress = live.Progress ?? new FileBackupProgress();
                Say("", FileBackupWords.Progress(progress, FileSystemItem.FormatSize), FileBackupTone.Quiet, progress.Current, running: true);
                _bar.Visibility = Visibility.Visible;
                _restore.IsEnabled = false;
                if (!_stopping)
                {
                    _run.Content = "Stop";
                    _run.IsEnabled = true;
                    _run.SetResourceReference(StyleProperty, "ViewerTextButton");
                }

                return;
            }

            // Asked to start, not up yet, and nothing new has been written: still starting.
            if (_startingUntil > DateTime.UtcNow && live.State.LastEndUtc == _endBeforeStart && live.State.Outcome != FileBackupState.Waiting)
                return;

            _startingUntil = default;
            _stopping = false;
            _bar.Visibility = Visibility.Collapsed;
            _restore.IsEnabled = true;
            _run.Content = "Back up now";
            _run.IsEnabled = true;
            _run.SetResourceReference(StyleProperty, "ViewerTextButtonPrimary");

            var (text, tone) = FileBackupWords.Status(_plan, live.State, live.CutShort, FileBackupUi.Day);
            var glyph = tone switch
            {
                FileBackupTone.Good => "",  // CheckMark
                FileBackupTone.Bad => "",   // Error
                FileBackupTone.Warn => "",  // Warning
                _ => ""
            };

            // CHANGED (simpler page): one small line for kind, schedule and the next run.
            var detail = FileBackupWords.Summary(_plan);
            if (FileBackupWords.Next(_plan, live.State, DateTime.Now, FileBackupUi.Day) is { } next)
                detail += "  ·  " + next;
            if (live.State.LastFailed && live.State.LastGoodUtc is { } good)
                detail += "  ·  Last good: " + FileBackupUi.Day(good);
            Say(glyph, text, tone, detail);
        }

        private void Say(string glyph, string text, FileBackupTone tone, string detail, bool running = false)
        {
            _statusGlyph.Text = glyph;
            _statusText.Text = text;
            FileBackupUi.Tone(_statusGlyph, tone, quiet: "Accent");
            FileBackupUi.Tone(_statusText, tone == FileBackupTone.Good ? FileBackupTone.Quiet : tone, quiet: "Ink");
            _detail.Text = detail;
            _detail.ToolTip = running ? (detail.Length > 0 ? detail : null) : char.ToUpperInvariant(_keeps[0]) + _keeps[1..] + "."; // what the backup keeps
            _detail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}

/// <summary>The small pieces the Backup and restore page and its dialogs are built from.</summary>
internal static class FileBackupUi
{
    /// <summary>"today, 16:30", "yesterday, 09:12", "3 October 2026, 18:05", for a UTC or a local time.</summary>
    internal static string Day(DateTime when) => BackupWindow.Day(when);

    internal static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        return block;
    }

    internal static Button TextButton(string label, bool primary)
    {
        var button = new Button { Content = label, MinWidth = 76 };
        button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "ViewerTextButtonPrimary" : "ViewerTextButton");
        return button;
    }

    internal static Button IconButton(string glyph, string tip)
    {
        var button = new Button { Content = glyph, Width = 30, Height = 30, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
        button.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        return button;
    }

    /// <summary>One of a row of choices; the chosen one is filled, like a dialog's main button.</summary>
    internal static Button Chip(string label, bool chosen, Action pick)
    {
        var chip = new Button { Content = label, MinWidth = 44, Height = 28, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 6, 6) };
        chip.SetResourceReference(FrameworkElement.StyleProperty, chosen ? "ViewerTextButtonPrimary" : "ViewerTextButton");
        chip.Click += (_, _) => pick();
        return chip;
    }

    internal static Border Card(UIElement child)
    {
        var card = new Border { Padding = new Thickness(20), Child = child };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.CornerRadiusProperty, "R12");
        return card;
    }

    /// <summary>The round badge with a symbol that leads a card or a dialog.</summary>
    internal static Border Badge(string glyph)
    {
        var symbol = new TextBlock { Text = glyph, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        symbol.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        symbol.SetResourceReference(TextBlock.ForegroundProperty, "Accent");

        var badge = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = symbol
        };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        return badge;
    }

    /// <summary>The outline round a text box, as in the other dialogs.</summary>
    internal static Border Framed(TextBox box)
    {
        box.SetResourceReference(FrameworkElement.StyleProperty, "PlainTextBox");
        var frame = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 6), Child = box };
        frame.SetResourceReference(Border.BackgroundProperty, "Base");
        frame.SetResourceReference(Border.BorderBrushProperty, "EdgeSunken");
        frame.SetResourceReference(Border.CornerRadiusProperty, "R8");
        return frame;
    }

    /// <summary>A titled block: a small heading (or none) over a tinted card.</summary>
    internal static StackPanel Section(string? title, UIElement content)
    {
        var block = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        if (title is not null)
        {
            var label = new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 6) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "InkFaint");
            block.Children.Add(label);
        }

        var card = new Border { Padding = new Thickness(14, 12, 14, 12), Child = content };
        card.SetResourceReference(Border.BackgroundProperty, "Tint");
        card.SetResourceReference(Border.CornerRadiusProperty, "R8");
        block.Children.Add(card);
        return block;
    }

    /// <summary>A tick box with a line of explanation under its label.</summary>
    internal static CheckBox Check(string label, string explanation)
    {
        var name = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var detail = Muted(explanation);
        detail.FontSize = 12;
        detail.Margin = new Thickness(0, 2, 0, 0);
        var words = new StackPanel();
        words.Children.Add(name);
        words.Children.Add(detail);

        var box = new CheckBox { Content = words, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.SetResourceReference(FrameworkElement.StyleProperty, "ViewerCheck");
        return box;
    }

    /// <summary>NEW (simpler editor): a tick box with a short label; what it means is its tooltip.</summary>
    internal static CheckBox Tick(string label, string tip)
    {
        var box = new CheckBox { Content = label, ToolTip = tip, HorizontalAlignment = HorizontalAlignment.Left };
        box.SetResourceReference(FrameworkElement.StyleProperty, "ViewerCheck");
        return box;
    }

    /// <summary>
    /// One of a list of choices of which exactly one is chosen (a round mark, a label, a line under it).
    /// A click anywhere on it picks it.
    /// </summary>
    internal static Border Option(string label, string? detail, bool chosen, Action pick)
    {
        var mark = new TextBlock { Text = chosen ? "" : "", FontSize = 15, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top }; // RadioBtnOn / RadioBtnOff
        mark.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        mark.SetResourceReference(TextBlock.ForegroundProperty, chosen ? "Accent" : "InkMuted");
        DockPanel.SetDock(mark, Dock.Left);

        var words = new StackPanel();
        words.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontWeight = chosen ? FontWeights.SemiBold : FontWeights.Normal });
        if (!string.IsNullOrEmpty(detail))
        {
            var under = Muted(detail);
            under.FontSize = 11.5;
            under.Margin = new Thickness(0, 1, 0, 0);
            words.Children.Add(under);
        }

        var row = new DockPanel();
        row.Children.Add(mark);
        row.Children.Add(words);

        var option = new Border
        {
            Padding = new Thickness(8, 6, 8, 6),
            Child = row,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = System.Windows.Media.Brushes.Transparent, // so the whole row takes the click
            Focusable = true
        };
        option.SetResourceReference(Border.CornerRadiusProperty, "R6");
        option.MouseEnter += (_, _) => option.SetResourceReference(Border.BackgroundProperty, "Hover");
        option.MouseLeave += (_, _) => option.Background = System.Windows.Media.Brushes.Transparent;
        option.MouseLeftButtonUp += (_, _) => pick();
        option.KeyDown += (_, e) =>
        {
            if (e.Key is System.Windows.Input.Key.Space or System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                pick();
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(option, label);
        return option;
    }

    /// <summary>Colours a line by how things stand; <paramref name="quiet"/> is the colour when there is nothing to say.</summary>
    internal static void Tone(TextBlock block, FileBackupTone tone, string quiet = "InkMuted")
        => block.SetResourceReference(TextBlock.ForegroundProperty, tone switch
        {
            FileBackupTone.Good => "Good",
            FileBackupTone.Warn => "Warn",
            FileBackupTone.Bad => "Danger",
            _ => quiet
        });

    /// <summary>What every dialog of the page has in common: size, theme colours, where it opens.</summary>
    internal static void Dialog(Window window, string title, double width)
    {
        window.Title = title;
        window.Width = width;
        window.SizeToContent = SizeToContent.Height;
        window.ResizeMode = ResizeMode.NoResize;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.UseLayoutRounding = true;
        window.FontSize = 13;
        window.SetResourceReference(Control.BackgroundProperty, "Base");
        window.SetResourceReference(Control.ForegroundProperty, "Ink");
        window.SetResourceReference(Control.FontFamilyProperty, "UIFont");
        window.SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(window);
    }

    /// <summary>Puts a dialog's content on its card and gives the E-reader theme its frame.</summary>
    internal static void Fill(Window window, UIElement content)
    {
        var card = new Border { Margin = new Thickness(14), Padding = new Thickness(22, 20, 22, 18), Child = content };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.CornerRadiusProperty, "R12");
        window.Content = card;
        EInkScreen.Frame(window);
    }

    /// <summary>A dialog's top: the badge, a heading and a line under it.</summary>
    internal static DockPanel Header(string glyph, string heading, string about)
    {
        var badge = Badge(glyph);
        DockPanel.SetDock(badge, Dock.Left);

        var title = new TextBlock { Text = heading, FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(title);
        if (about.Length > 0) // CHANGED (simpler editor): a dialog may have a heading alone
        {
            var under = Muted(about);
            under.Margin = new Thickness(0, 4, 0, 0);
            titles.Children.Add(under);
        }

        var header = new DockPanel();
        header.Children.Add(badge);
        header.Children.Add(titles);
        return header;
    }

    /// <summary>A label on the left and its field on the right, as a row of a two-column grid.</summary>
    internal static void AddField(Grid table, string label, UIElement field)
    {
        var row = table.RowDefinitions.Count;
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var left = Muted(label);
        left.Margin = new Thickness(0, (row == 0 ? 0 : 12) + 6, 18, 0);
        left.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(left, row);
        table.Children.Add(left);

        var cell = new Border { Margin = new Thickness(0, row == 0 ? 0 : 12, 0, 0), Child = field };
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, 1);
        table.Children.Add(cell);
    }

    internal static Grid Fields()
    {
        var table = new Grid();
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return table;
    }
}
