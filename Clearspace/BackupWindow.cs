// Clearspace | NEW (backup): Settings > "Back up settings and tags…".
// CHANGED (backup name): the window is called "Settings and tags backup". "Backup and restore" is kept
// for backing up files, folders and drives, which comes later.
// One window, three parts, top to bottom:
//   Back up now        a line saying when the last backup was made, the button, and a label for the
//                      backup that can be changed before making it ("Made by you" unless you say otherwise)
//   Automatic backup   Off / Every day / Every week / Custom (every so many hours or days), how many to
//                      keep, and where backups go
//   Your backups       every backup in that folder, newest first, each with Restore… and a delete button;
//                      "Restore from a file…" for a backup kept somewhere else
// Restore… opens RestoreBackupWindow (below), which says what is in the backup and asks what to put back.
// What a backup is, and the rules for restoring, are in Services/BackupService.cs.
// Built in code from the shared styles in App.xaml and the theme's colours, like MessageDialog.
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

internal sealed class BackupWindow : Window
{
    private readonly IReadOnlyList<SidebarEntry> _places;
    private readonly Action<string> _showFolder;
    private readonly TextBlock _lastText;
    private readonly TextBlock _status;
    private readonly Button _backUpNow;
    private readonly StackPanel _scheduleChips = new() { Orientation = Orientation.Horizontal };
    // NEW (custom schedule): "Every [number] [Hours] [Days]", shown while Custom is chosen.
    private readonly StackPanel _customRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBox _customNumber = new() { Width = 44, MaxLength = 3, TextAlignment = TextAlignment.Center };
    private readonly StackPanel _customUnits = new() { Orientation = Orientation.Horizontal };
    private bool _customInDays;
    private bool _showingSettings; // the number box is being filled in by the window, not typed in
    // NEW (backup label): what the next backup made here is called in the list.
    private readonly TextBox _label = new() { MaxLength = BackupService.LongestNote };
    private readonly StackPanel _keepChips = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _keepNote;
    private readonly TextBlock _keepHint; // NEW (cut-off text): the sentence under the "keep" choices
    private readonly TextBlock _folderText;
    private readonly StackPanel _rows = new();
    private bool _busy;

    /// <param name="places">The window's sidebar entries, for choosing the backup folder in Clearspace's own folder chooser.</param>
    /// <param name="showFolder">Opens a folder in the main window ("Show folder").</param>
    internal BackupWindow(IReadOnlyList<SidebarEntry> places, Action<string> showFolder)
    {
        _places = places;
        _showFolder = showFolder;

        Title = "Settings and tags backup"; // CHANGED (backup name)
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Base");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(FontFamilyProperty, "UIFont");

        // Header: round icon badge + what a backup is, in two sentences.
        var glyph = new TextBlock
        {
            Text = "", // Save
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");

        var badge = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = glyph
        };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        DockPanel.SetDock(badge, Dock.Left);

        var heading = new TextBlock { Text = "Settings and tags backup", FontSize = 20, FontWeight = FontWeights.SemiBold }; // CHANGED (backup name)
        var about = Muted("A backup is one small file with your settings (pins, sidebar, folder types, columns, theme) and your tags. " +
                          "If either is ever lost, it can be put back from here. Your files themselves are not copied.");
        about.Margin = new Thickness(0, 4, 0, 0);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(heading);
        titles.Children.Add(about);

        var header = new DockPanel();
        header.Children.Add(badge);
        header.Children.Add(titles);

        // ---- 1. Back up now
        _lastText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        _backUpNow = TextButton("Back up now", primary: true);
        _backUpNow.Click += async (_, _) => await BackUpNowAsync();
        DockPanel.SetDock(_backUpNow, Dock.Right);
        var nowRow = new DockPanel();
        nowRow.Children.Add(_backUpNow);
        nowRow.Children.Add(_lastText);

        // NEW (backup label): the name this backup gets in the list. It starts as "Made by you"; type over
        // it to say what the backup is for ("Before reinstalling Windows"). Enter makes the backup.
        _label.Text = BackupService.MadeByYou;
        _label.SetResourceReference(StyleProperty, "PlainTextBox");
        _label.ToolTip = "What this backup is called in the list below. Change it to say what the backup is for.";
        _label.GotKeyboardFocus += (_, _) => _label.SelectAll();
        _label.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter)
                return;

            e.Handled = true;
            await BackUpNowAsync();
        };
        var labelCaption = Muted("Label");
        labelCaption.VerticalAlignment = VerticalAlignment.Center;
        labelCaption.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(labelCaption, Dock.Left);
        var labelRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        labelRow.Children.Add(labelCaption);
        labelRow.Children.Add(Framed(_label));

        var nowCell = new StackPanel();
        nowCell.Children.Add(nowRow);
        nowCell.Children.Add(labelRow);

        // NEW (custom schedule): the number and its unit, under the schedule choices.
        _customNumber.SetResourceReference(StyleProperty, "PlainTextBox");
        _customNumber.TextChanged += (_, _) => SaveCustomInterval();
        var every = Muted("Every");
        every.VerticalAlignment = VerticalAlignment.Center;
        every.Margin = new Thickness(0, 0, 10, 0);
        var numberFrame = Framed(_customNumber);
        numberFrame.Padding = new Thickness(6, 4, 6, 4);
        numberFrame.Margin = new Thickness(0, 0, 10, 0);
        _customRow.Children.Add(every);
        _customRow.Children.Add(numberFrame);
        _customRow.Children.Add(_customUnits);
        var scheduleCell = new StackPanel();
        scheduleCell.Children.Add(_scheduleChips);
        scheduleCell.Children.Add(_customRow);

        // ---- 2. Automatic backup
        // FIXED (cut-off text): the sentence after the choices ran off the right edge of the card, because
        // a row that lays its parts side by side never wraps them. "automatic backups" stays beside the
        // choices; the rest is a line of its own underneath, which wraps when it has to.
        _keepNote = Muted("automatic backups");
        _keepNote.VerticalAlignment = VerticalAlignment.Center;
        _keepNote.Margin = new Thickness(6, 0, 0, 0);
        var keepRow = new StackPanel { Orientation = Orientation.Horizontal };
        keepRow.Children.Add(_keepChips);
        keepRow.Children.Add(_keepNote);
        _keepHint = Muted("Older ones are deleted. Backups you make yourself are kept until you delete them.");
        _keepHint.FontSize = 11.5;
        _keepHint.Margin = new Thickness(0, 6, 0, 0);
        var keepCell = new StackPanel();
        keepCell.Children.Add(keepRow);
        keepCell.Children.Add(_keepHint);

        _folderText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var change = TextButton("Change…", primary: false);
        change.Click += (_, _) => ChangeFolder();
        var show = TextButton("Show folder", primary: false);
        show.Click += (_, _) =>
        {
            var folder = BackupService.Folder;
            if (!Directory.Exists(folder))
            {
                ShowStatus("That folder is not there yet. It is made with the first backup.", problem: false);
                return;
            }

            _showFolder(folder);
            Close();
        };
        DockPanel.SetDock(change, Dock.Right);
        DockPanel.SetDock(show, Dock.Right);
        var folderRow = new DockPanel();
        folderRow.Children.Add(show);
        folderRow.Children.Add(change);
        folderRow.Children.Add(_folderText);

        var automatic = new Grid();
        automatic.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        automatic.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddField(automatic, "How often", scheduleCell); // CHANGED (custom schedule): was the chips alone
        AddField(automatic, "Keep the last", keepCell); // CHANGED (cut-off text): was the row alone
        AddField(automatic, "Kept in", folderRow);

        // ---- 3. Your backups
        var scroll = new ScrollViewer
        {
            Content = _rows,
            MaxHeight = 232,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        // Bottom row: what just happened on the left; restore from elsewhere and Close on the right.
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        var fromFile = TextButton("Restore from a file…", primary: false);
        fromFile.ToolTip = "A backup kept somewhere else: a USB stick, another PC's folder";
        fromFile.Click += async (_, _) => await RestoreFromFileAsync();
        var close = TextButton("Close", primary: false);
        close.IsCancel = true; // Esc
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        buttons.Children.Add(fromFile);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Right);
        var bottom = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(Section(null, nowCell)); // CHANGED (backup label): the button's row and the label under it
        stack.Children.Add(Section("Automatic backup", automatic));
        stack.Children.Add(Section("Your backups", scroll));
        stack.Children.Add(bottom);

        var card = new Border
        {
            Margin = new Thickness(14),
            Padding = new Thickness(22, 20, 22, 18),
            Child = stack
        };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.CornerRadiusProperty, "R12");
        Content = card;
        EInkScreen.Frame(this); // the E-reader theme's panel look reaches this dialog too

        ShowSettings();
        ShowStatus(BackupService.LastAutomaticError is { } failed ? "The last automatic backup could not be made: " + failed : string.Empty,
            problem: BackupService.LastAutomaticError is not null);

        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        Loaded += async (_, _) => await LoadBackupsAsync();
        BackupService.Changed += OnBackupsChanged; // an automatic backup made while this is open
        Closed += (_, _) => BackupService.Changed -= OnBackupsChanged;
    }

    private async void OnBackupsChanged()
    {
        try { await LoadBackupsAsync(); }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ the settings part

    // The schedule and "keep" choices as they are saved, the chosen one filled; and the folder.
    private void ShowSettings()
    {
        var schedule = SettingsService.GetBackupSchedule();
        _scheduleChips.Children.Clear();
        // CHANGED (custom schedule): + Custom, every so many hours or days.
        foreach (var (value, label) in new[] { ("Off", "Off"), ("Daily", "Every day"), ("Weekly", "Every week"), ("Custom", "Custom") })
        {
            _scheduleChips.Children.Add(Chip(label, schedule == value, () =>
            {
                SettingsService.SetBackupSchedule(value);
                ShowSettings();
                if (value != "Off")
                    ShowStatus("Automatic backups are on. The first is made within the hour, and then when Clearspace is open and one is due.", problem: false);
            }));
        }

        // NEW (custom schedule): the number and unit as saved. A whole number of days is shown in days.
        _customRow.Visibility = schedule == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        var hours = SettingsService.GetBackupEveryHours();
        if (!_customNumber.IsKeyboardFocusWithin)
        {
            _customInDays = hours % 24 == 0;
            _showingSettings = true;
            _customNumber.Text = (_customInDays ? hours / 24 : hours).ToString(CultureInfo.CurrentCulture);
            _showingSettings = false;
        }
        _customUnits.Children.Clear();
        foreach (var (days, label) in new[] { (false, "Hours"), (true, "Days") })
        {
            _customUnits.Children.Add(Chip(label, _customInDays == days, () =>
            {
                _customInDays = days;
                SaveCustomInterval();
                ShowSettings();
            }));
        }

        var keep = SettingsService.GetBackupsToKeep();
        _keepChips.Children.Clear();
        foreach (var count in new[] { 5, 10, 30 })
        {
            _keepChips.Children.Add(Chip(count.ToString(CultureInfo.CurrentCulture), keep == count, () =>
            {
                SettingsService.SetBackupsToKeep(count);
                ShowSettings();
            }));
        }

        _keepChips.IsEnabled = schedule != "Off";
        _keepNote.Opacity = schedule == "Off" ? 0.55 : 1;
        _keepHint.Opacity = _keepNote.Opacity;

        var folder = BackupService.Folder;
        _folderText.Text = folder;
        _folderText.ToolTip = folder;

        var last = SettingsService.GetLastBackupUtc();
        _lastText.Text = last is { } moment
            ? "Last backup: " + Day(moment) + "."
            : "No backup has been made yet.";
    }

    // NEW (custom schedule): keeps what is in the number box (1 to 999 hours, or 1 to 365 days) as hours.
    // Anything that is not such a number is left unsaved until it is one.
    private void SaveCustomInterval()
    {
        if (_showingSettings)
            return;

        // Days stop at a year, the longest the setting keeps, so the line below never promises more than is saved.
        var most = _customInDays ? SettingsService.LongestBackupInterval / 24 : 999;
        if (!int.TryParse(_customNumber.Text.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out var count) || count < 1 || count > most)
        {
            ShowStatus($"Type a number from 1 to {most} for how often.", problem: true);
            return;
        }

        SettingsService.SetBackupEveryHours(_customInDays ? count * 24 : count);
        var unit = _customInDays ? (count == 1 ? "day" : "days") : (count == 1 ? "hour" : "hours");
        ShowStatus($"A backup is made every {count:N0} {unit} while Clearspace is open, or when it is next opened after that.", problem: false);
    }

    // Clearspace's own folder chooser, opened on the folder backups are kept in now.
    private void ChangeFolder()
    {
        var current = BackupService.Folder;
        var picker = new FolderPickerWindow(new FolderPickerOptions(
            "Where backups are kept",
            "Open the folder to keep backups in, then choose Use this folder. A folder that OneDrive syncs keeps them safe if this PC is lost.",
            "Use this folder",
            Directory.Exists(current) ? current : KnownFolders.Documents)
        {
            Places = _places
        })
        {
            Owner = this
        };

        if (picker.ShowDialog() != true || picker.Chosen is not { } chosen)
            return;

        SettingsService.SetBackupFolder(string.Equals(chosen, BackupService.DefaultFolder, StringComparison.OrdinalIgnoreCase) ? null : chosen);
        ShowSettings();
        _ = LoadBackupsAsync();
        ShowStatus("New backups go to " + chosen + ". Backups already made stay where they are.", problem: false);
    }

    // ------------------------------------------------------------------ the list

    private async Task LoadBackupsAsync()
    {
        var folder = BackupService.Folder;
        IReadOnlyList<BackupFile> backups;
        try { backups = await Task.Run(() => BackupService.List(folder)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            backups = [];
        }

        _rows.Children.Clear();
        if (backups.Count == 0)
        {
            var none = Muted("No backups in this folder yet. Choose Back up now to make the first.");
            none.Margin = new Thickness(0, 4, 0, 4);
            _rows.Children.Add(none);
            return;
        }

        foreach (var backup in backups)
            _rows.Children.Add(Row(backup));
    }

    // One backup: when, what kind, what is in it; Restore… and delete.
    private DockPanel Row(BackupFile backup)
    {
        var manifest = backup.Manifest;

        var delete = new Button { Content = "", Width = 30, Height = 30, ToolTip = "Delete this backup", VerticalAlignment = VerticalAlignment.Center };
        delete.SetResourceReference(StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(delete, "Delete this backup");
        delete.Click += async (_, _) => await DeleteAsync(backup);
        DockPanel.SetDock(delete, Dock.Right);

        var restore = TextButton("Restore…", primary: false);
        restore.VerticalAlignment = VerticalAlignment.Center;
        restore.Click += async (_, _) => await RestoreAsync(backup.Path);
        DockPanel.SetDock(restore, Dock.Right);

        // CHANGED (backup label): the person's own label when they gave one; otherwise what kind it is,
        // now with a capital first letter ("Made by you", "Automatic", "Made before a restore").
        var kind = BackupService.Label(manifest);
        var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        title.Inlines.Add(new System.Windows.Documents.Run(Day(manifest.CreatedUtc)) { FontWeight = FontWeights.SemiBold });
        title.Inlines.Add(new System.Windows.Documents.Run("  ·  " + kind));

        var inside = Muted(Inside(manifest));
        inside.FontSize = 11.5;
        inside.Margin = new Thickness(0, 2, 0, 0);
        inside.TextWrapping = TextWrapping.NoWrap;
        inside.TextTrimming = TextTrimming.CharacterEllipsis;

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, ToolTip = backup.Path };
        words.Children.Add(title);
        words.Children.Add(inside);

        var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
        row.Children.Add(delete);
        row.Children.Add(restore);
        row.Children.Add(words);
        return row;
    }

    internal static string Inside(BackupManifest manifest)
    {
        var tags = manifest.Tags == 1 ? "1 tag" : $"{manifest.Tags:N0} tags";
        var items = manifest.TaggedItems == 1 ? "1 item" : $"{manifest.TaggedItems:N0} items";
        var text = $"{tags} on {items}";
        if (manifest.HasSettings)
            text += "  ·  settings";
        if (manifest.AppVersion.Length > 0)
            text += "  ·  Clearspace " + manifest.AppVersion;
        return text;
    }

    // "today, 16:30", "yesterday, 09:12", "3 October 2026, 18:05"
    internal static string Day(DateTime utc)
    {
        var local = utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        var days = (DateTime.Now.Date - local.Date).Days;
        return days switch
        {
            0 => "today, " + time,
            1 => "yesterday, " + time,
            _ => local.ToString("d MMMM yyyy", CultureInfo.CurrentCulture) + ", " + time
        };
    }

    // ------------------------------------------------------------------ doing things

    private async Task BackUpNowAsync()
    {
        if (!Begin("Backing up…"))
            return;

        try
        {
            var snapshot = BackupService.Capture();
            var folder = BackupService.Folder;
            var now = DateTime.UtcNow;
            // CHANGED (backup label): the label as typed, unless it was left as "Made by you".
            var typed = BackupService.CleanNote(_label.Text);
            var note = string.Equals(typed, BackupService.MadeByYou, StringComparison.OrdinalIgnoreCase) ? null : typed;
            var made = await Task.Run(() => BackupService.Write(snapshot, folder, BackupKind.Manual, now, UpdateService.CurrentText, note));
            _label.Text = BackupService.MadeByYou; // the next one starts from the usual label again

            SettingsService.SetLastBackupUtc(now);
            ShowSettings();
            await LoadBackupsAsync();
            ShowStatus("Backed up: " + Inside(made.Manifest).Replace("  ·  ", ", ") + ".", problem: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            ShowStatus("The backup could not be made: " + error.Message, problem: true);
        }
        finally
        {
            End();
        }
    }

    private async Task DeleteAsync(BackupFile backup)
    {
        if (_busy)
            return;

        var answer = MessageDialog.Show(this,
            $"Delete the backup of {Day(backup.Manifest.CreatedUtc)}?\n\nThe file is deleted for good. Your settings and tags as they are now are not touched.",
            "Delete this backup?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            File.Delete(backup.Path);
            ShowStatus("Deleted that backup.", problem: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowStatus("That backup could not be deleted: " + error.Message, problem: true);
        }

        await LoadBackupsAsync();
    }

    private async Task RestoreFromFileAsync()
    {
        if (_busy)
            return;

        // CHANGED (pick a file): Clearspace's own chooser, picking a .csbackup file, where this used to
        // open Windows' file picker (File Explorer in a small window).
        var folder = BackupService.Folder;
        var picker = new FolderPickerWindow(new FolderPickerOptions(
            "Restore from a file",
            "Open the folder the backup is in, click the backup, then choose Open.",
            "Open",
            Directory.Exists(folder) ? folder : KnownFolders.Documents)
        {
            Places = _places,
            FileExtension = BackupService.Extension,
            FileWord = "backup"
        })
        {
            Owner = this
        };

        if (picker.ShowDialog() == true && picker.Chosen is { } file)
            await RestoreAsync(file);
    }

    // Reads the backup, asks what to put back, saves how things are now, then restores.
    private async Task RestoreAsync(string path)
    {
        if (!Begin("Reading the backup…"))
            return;

        var restart = false;
        try
        {
            var contents = await Task.Run(() => BackupService.Read(path));

            var ask = new RestoreBackupWindow(contents) { Owner = this };
            if (ask.ShowDialog() != true || (!ask.RestoreTags && !ask.RestoreSettings))
            {
                ShowStatus(string.Empty, problem: false);
                return;
            }

            // First a backup of how things are now, so this restore can be undone from the list.
            ShowStatus("Saving how things are now…", problem: false);
            var before = BackupService.Capture();
            var folder = BackupService.Folder;
            var now = DateTime.UtcNow;
            await Task.Run(() => BackupService.Write(before, folder, BackupKind.BeforeRestore, now, UpdateService.CurrentText));

            var done = new List<string>();

            if (ask.RestoreTags)
            {
                ShowStatus("Putting tags back…", problem: false);
                var (keep, skipped) = await Task.Run(() => BackupService.ItemsToRestore(contents.Assignments));
                // On this thread: the rest of the window listens for tag changes and redraws.
                var result = TagService.Store.Restore(contents.Tags, keep, ask.ExactTags);

                done.Add(result.TagsPutBack == 0 && result.TagsCreated == 0 && !ask.ExactTags
                    ? "Every tag in the backup was already in place."
                    : ask.ExactTags
                        ? $"Tags are as they were in the backup: {keep.Count:N0} tagged items."
                        : $"Put tags back on {result.Items:N0} item{(result.Items == 1 ? string.Empty : "s")}" +
                          (result.TagsCreated > 0 ? $", and made {result.TagsCreated:N0} tag{(result.TagsCreated == 1 ? string.Empty : "s")} again." : "."));
                if (skipped > 0)
                    done.Add($"{skipped:N0} tagged item{(skipped == 1 ? " no longer exists and was" : "s no longer exist and were")} left out.");
            }

            if (ask.RestoreSettings)
            {
                if (contents.SettingsJson is { } json && SettingsService.ReplaceWith(json))
                {
                    done.Add("Settings are as they were in the backup.");
                    restart = true;
                }
                else
                {
                    done.Add("The settings in this backup could not be read, so today's settings were kept.");
                }
            }

            ShowSettings();
            await LoadBackupsAsync();
            ShowStatus(string.Join(" ", done), problem: false);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            ShowStatus(error.Message, problem: true);
        }
        finally
        {
            End();
        }

        if (!restart)
            return;

        // Most of Clearspace reads its settings when it starts.
        var answer = MessageDialog.Show(this,
            "The settings were restored. Clearspace has to start again to use them everywhere (sidebar, theme, folder types).\n\nRestart Clearspace now?",
            "Restart to finish", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            if (Environment.ProcessPath is { } exe)
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true })?.Dispose();
        }
        catch (Exception)
        {
            // it still closes; opening it again by hand does the same
        }

        LockAgent.Current.Quit(); // locks again whatever is unlocked for a visit, then closes every window
    }

    private bool Begin(string doing)
    {
        if (_busy)
            return false;

        _busy = true;
        _backUpNow.IsEnabled = false;
        _rows.IsEnabled = false;
        ShowStatus(doing, problem: false);
        return true;
    }

    private void End()
    {
        _busy = false;
        _backUpNow.IsEnabled = true;
        _rows.IsEnabled = true;
    }

    private void ShowStatus(string text, bool problem)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "Danger" : "InkMuted");
    }

    // ------------------------------------------------------------------ small builders

    // A titled block: a small heading (or none) over a tinted card.
    private static StackPanel Section(string? title, UIElement content)
    {
        var block = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
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

    private static void AddField(Grid table, string label, UIElement field)
    {
        var row = table.RowDefinitions.Count;
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // CHANGED (cut-off text): the label sits beside the first line of its field (the choices), not
        // half-way down a field that now has a second line under them.
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

    // One of a row of choices; the chosen one is filled, like a dialog's main button.
    private static Button Chip(string label, bool chosen, Action pick)
    {
        var chip = new Button { Content = label, MinWidth = 44, Height = 28, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 6, 0) };
        chip.SetResourceReference(StyleProperty, chosen ? "ViewerTextButtonPrimary" : "ViewerTextButton");
        chip.Click += (_, _) => pick();
        return chip;
    }

    private static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        return block;
    }

    private static Button TextButton(string label, bool primary)
    {
        var button = new Button { Content = label, MinWidth = 76 };
        button.SetResourceReference(StyleProperty, primary ? "ViewerTextButtonPrimary" : "ViewerTextButton");
        return button;
    }

    // NEW (backup label, custom schedule): the outline round a text box, as in the other dialogs.
    private static Border Framed(TextBox box)
    {
        var frame = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 6), Child = box };
        frame.SetResourceReference(Border.BackgroundProperty, "Base");
        frame.SetResourceReference(Border.BorderBrushProperty, "EdgeSunken");
        frame.SetResourceReference(Border.CornerRadiusProperty, "R8");
        return frame;
    }
}

// What to put back from one backup. Says what is in it, and asks three things: tags, whether tags added
// since should go too, and settings. Nothing is changed here; BackupWindow reads the answers.
internal sealed class RestoreBackupWindow : Window
{
    private readonly CheckBox _tags;
    private readonly CheckBox _exact;
    private readonly CheckBox _settings;
    private readonly Button _restore;

    internal bool RestoreTags => _tags.IsChecked == true && _tags.IsEnabled;
    internal bool ExactTags => RestoreTags && _exact.IsChecked == true;
    internal bool RestoreSettings => _settings.IsChecked == true && _settings.IsEnabled;

    internal RestoreBackupWindow(BackupContents contents)
    {
        var manifest = contents.Manifest;

        Title = "Restore";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Base");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(FontFamilyProperty, "UIFont");

        var glyph = new TextBlock
        {
            Text = "", // Update / restore
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");

        var badge = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = glyph
        };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        DockPanel.SetDock(badge, Dock.Left);

        var heading = new TextBlock { Text = "Restore from this backup", FontSize = 20, FontWeight = FontWeights.SemiBold };
        var about = Muted($"Made {BackupWindow.Day(manifest.CreatedUtc)}. It holds {BackupWindow.Inside(manifest).Replace("  ·  ", ", ")}.");
        about.Margin = new Thickness(0, 4, 0, 0);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(heading);
        titles.Children.Add(about);

        var header = new DockPanel();
        header.Children.Add(badge);
        header.Children.Add(titles);

        // Tags: on by default. It is what goes missing, and putting them back takes nothing away.
        var hasTags = contents.Tags.Length > 0;
        _tags = Check("Tags", hasTags
            ? "Puts each tag back on the files and folders that had it. Tags you have now stay."
            : "This backup has no tags in it.");
        _tags.IsChecked = hasTags;
        _tags.IsEnabled = hasTags;

        _exact = Check("Also remove tags added since this backup",
            "Leaves the tags exactly as they were then: tags and tagging made after this backup are removed.");
        _exact.Margin = new Thickness(27, 10, 0, 0);

        // Settings: off by default. They replace today's as a whole and need a restart.
        var hasSettings = !string.IsNullOrWhiteSpace(contents.SettingsJson);
        _settings = Check("Settings", hasSettings
            ? "Replaces today's settings (pins, sidebar, folder types, columns, theme) with the ones in this backup. Clearspace restarts to use them."
            : "This backup has no settings in it.");
        _settings.Margin = new Thickness(0, 16, 0, 0);
        _settings.IsEnabled = hasSettings;

        var choices = new StackPanel();
        choices.Children.Add(_tags);
        choices.Children.Add(_exact);
        choices.Children.Add(_settings);
        var choicesCard = new Border { Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(14, 14, 14, 14), Child = choices };
        choicesCard.SetResourceReference(Border.BackgroundProperty, "Tint");
        choicesCard.SetResourceReference(Border.CornerRadiusProperty, "R8");

        var safety = Muted("Before anything changes, Clearspace saves a backup of how things are now, so you can undo this from the list. " +
                           "Locked files and folders are not affected.");
        safety.Margin = new Thickness(0, 14, 0, 0);

        var cancel = TextButton("Cancel", primary: false);
        cancel.IsCancel = true; // Esc
        cancel.Click += (_, _) => Close();
        _restore = TextButton("Restore", primary: true);
        _restore.IsDefault = true; // Enter
        _restore.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_restore);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(choicesCard);
        stack.Children.Add(safety);
        stack.Children.Add(buttons);

        var card = new Border
        {
            Margin = new Thickness(14),
            Padding = new Thickness(22, 20, 22, 18),
            Child = stack
        };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.CornerRadiusProperty, "R12");
        Content = card;
        EInkScreen.Frame(this);

        void Follow()
        {
            _exact.IsEnabled = _tags.IsChecked == true && _tags.IsEnabled;
            _restore.IsEnabled = RestoreTags || RestoreSettings;
        }

        foreach (var box in new[] { _tags, _exact, _settings })
        {
            box.Checked += (_, _) => Follow();
            box.Unchecked += (_, _) => Follow();
        }
        Follow();

        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        Loaded += (_, _) => _restore.Focus();
    }

    // A tick box with a line of explanation under its label.
    private static CheckBox Check(string label, string explanation)
    {
        var name = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold };
        var detail = Muted(explanation);
        detail.FontSize = 12;
        detail.Margin = new Thickness(0, 2, 0, 0);
        var words = new StackPanel();
        words.Children.Add(name);
        words.Children.Add(detail);

        var box = new CheckBox { Content = words, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.SetResourceReference(StyleProperty, "ViewerCheck");
        return box;
    }

    private static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        return block;
    }

    private static Button TextButton(string label, bool primary)
    {
        var button = new Button { Content = label, MinWidth = 76 };
        button.SetResourceReference(StyleProperty, primary ? "ViewerTextButtonPrimary" : "ViewerTextButton");
        return button;
    }
}
