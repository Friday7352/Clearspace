// Clearspace | NEW (file backup): setting up a backup, and changing one ("New backup" and "Edit…" on the
// Backup and restore page; "Back up…" in a right-click menu opens it with what was clicked filled in).
//
// CHANGED (simpler editor): the dialog was one long form with a paragraph under most fields. It now shows
// only what a backup needs, with short labels, and keeps the rest behind "More options". What used to be
// written out under the fields is in their tooltips.
//
//   Always on show
//     Back up       a file, a folder or a drive: typed, or picked in Clearspace's own chooser
//     Save to       a folder; the backup gets a folder of its own inside it
//                   NEW (backup drives): under it, a button for each drive worth offering
//                   (Services/FileBackupDrives.cs): drives that already hold your backups first, with a
//                   filled symbol, a bold name and how many backups; then external drives without any,
//                   plainer, with their free room. A click fills in the folder. The buttons follow drives
//                   being plugged in and taken out while the window is open (Services/DriveWatch.cs).
//                   NEW (backup contents): and a line of warning when the folder is on the same drive
//                   as what is backed up.
//     Name          what the backup is called on the page (and its folder, the first time)
//     Contents      NEW (backup contents): Documents / Photos & media / Projects / A mix. Choosing one
//                   sets the options recommended for it (Services/FileBackupContents.cs): how many
//                   backups are kept, what else is kept, how often (unless that was chosen already), and
//                   what is skipped. One line under the buttons says what was set. Clearspace looks at
//                   what was picked to back up and chooses the button by itself, until a button or one
//                   of those options is chosen by hand; after that nothing changes unless a button is
//                   clicked.
//                   CHANGED: this replaces the "Protects against" row (Drive failure / Deleted files /
//                   Corrupted files), which asked what could go wrong instead of what is in the folder.
//     How often     Manual / Hourly / Daily / Weekly / Custom
//     Keep          NEW (retention): how many of the backup's runs are kept: Latest only / Last 5 /
//                   Last 10 / Last 30, or every run of the last 30 days / 90 days / 1 year. Older ones are
//                   deleted after each run; the newest always stays.
//     Zip to save space   NEW (zip tick box): unticked, a run makes a normal copy; ticked, one .zip file.
//   Keep and Zip together decide the kind (FileBackupRules.Shape), so the kinds need not be known:
//                   latest only, not zipped   Clone (or Add and update, under More options)
//                   more than one, not zipped Versions: a dated copy per run, unchanged files shared
//                   zipped                    Zip archive, one zip or several
//   More options (closed until asked for)
//     Kind          Clone / Add and update while only the latest is kept; otherwise what it is
//     Also keep     Clone, Add and update: files a run removes, earlier versions of changed files, and
//                   for how long
//     Skip          names to leave out, wherever they are found
//     Include online-only cloud files
// Save, or Save and back up now.
//
// For a backup that already exists, what is backed up, where to and the kind (zip or not) are shown but
// cannot be changed: its copies are where they are, and pointing a Clone at another folder would empty it.
// How many runs it keeps can be changed for the kinds that keep several (Versions, Zip archive).
//
// Save checks the plan, that what is backed up and the folder it goes to are there, and that the backup's
// own folder can be used (FileBackupRules.Prepare), off the UI thread.
// Built in code from the shared styles in App.xaml and the theme's colours (FileBackupUi).
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

internal sealed class FileBackupEditWindow : Window
{
    private readonly FileBackupPlan _plan;          // a copy: what the dialog is showing
    private readonly bool _isNew;
    private readonly IReadOnlyList<FileBackupPlan> _others;
    private readonly IReadOnlyList<SidebarEntry> _places;

    private readonly TextBox _source = new();
    private readonly TextBox _destination = new();
    // NEW (backup drives): the drives offered under "Save to", and the folders their buttons fill in.
    private readonly WrapPanel _suggestions = new() { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly HashSet<string> _suggestedFolders = new(StringComparer.OrdinalIgnoreCase);
    private string _suggestedForDrive = string.Empty;
    private int _suggestRun;
    private readonly TextBox _name = new() { MaxLength = 60 };
    private readonly WrapPanel _scheduleChips = new();
    private readonly StackPanel _scheduleDetail = new();
    private readonly TextBox _time = new() { Width = 64, MaxLength = 8, TextAlignment = TextAlignment.Center };
    private readonly TextBox _everyNumber = new() { Width = 44, MaxLength = 3, TextAlignment = TextAlignment.Center };
    private bool _everyInDays;
    // NEW (backup contents): the "Contents" row, and what it has set.
    private readonly StackPanel _contents = new();
    private readonly TextBlock _sameDrive = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly System.Windows.Threading.DispatcherTimer _detectTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private FileBackupContent? _content;            // the button that is lit; null until something is known
    private FileBackupTools _tools;                 // Projects: the tools found in what is backed up
    private FileBackupAdvice? _applied;             // the recommendation the options were last set from
    private bool _appliedShares = true;             // and whether it was for a drive that shares unchanged files
    private bool _contentPicked;                    // a Contents button was clicked: Clearspace stops choosing one
    private bool _touched;                          // what a recommendation sets was changed by hand since
    private bool _scheduleChosen;                   // "How often" was chosen by hand: a recommendation leaves it alone
    private int _detectRun;
    private bool _closed;
    private readonly StackPanel _retentionCell = new();     // NEW (retention): the "Keep" row
    private FileBackupRetention _retention;
    private readonly CheckBox _zip;                 // NEW (zip tick box)
    private FileBackupType _latestKind;             // Clone or Add and update: the kind while only the latest is kept
    private TextBlock? _alsoKeepLabel;              // the "Also keep" row, hidden for kinds it does not apply to
    private Border? _alsoKeepCell;
    private readonly Button _moreToggle;
    private readonly StackPanel _more;
    private readonly StackPanel _kind = new();
    private readonly StackPanel _keep = new();
    private readonly TextBox _exclude = new();
    private readonly CheckBox _cloud;
    private readonly TextBlock _status;
    private readonly Button _save;
    private readonly Button _saveAndRun;
    private bool _nameTyped;
    private bool _filling;      // the dialog is writing into its own boxes
    private bool _busy;

    /// <summary>The plan as saved; null when the dialog was cancelled.</summary>
    internal FileBackupPlan? Result { get; private set; }

    /// <summary>"Save and back up now" was chosen.</summary>
    internal bool RunNow { get; private set; }

    /// <param name="existing">The backup to change; null for a new one.</param>
    /// <param name="all">Every backup there is, to keep two from using the same folder.</param>
    /// <param name="source">For a new backup: what to back up, when it is known already.</param>
    internal FileBackupEditWindow(FileBackupPlan? existing, IReadOnlyList<FileBackupPlan> all, IReadOnlyList<SidebarEntry> places, string? source)
    {
        _isNew = existing is null;
        _plan = existing?.Copy() ?? new FileBackupPlan { Source = source ?? string.Empty };
        _others = all.Where(plan => plan.Id != _plan.Id).ToList();
        _places = places;
        _latestKind = _plan.Type == FileBackupType.AddAndUpdate ? FileBackupType.AddAndUpdate : FileBackupType.Clone;
        _retention = _isNew ? FileBackupRetention.Latest : FileBackupRetention.Of(_plan);

        FileBackupUi.Dialog(this, _isNew ? "New backup" : "Edit backup", 600);
        var header = FileBackupUi.Header("", _isNew ? "New backup" : _plan.Name, string.Empty); // Save

        // ---- what is always on show
        var main = FileBackupUi.Fields();
        if (_isNew)
        {
            _source.Text = _plan.Source;
            _source.ToolTip = "A file, a folder with everything in it, or a whole drive (its files; not an image Windows can start from).";
            _source.TextChanged += (_, _) => SourceChanged();
            var folder = FileBackupUi.TextButton("Folder…", primary: false);
            folder.ToolTip = "Pick a folder or a drive";
            folder.Click += (_, _) => PickSource(file: false);
            var file = FileBackupUi.TextButton("File…", primary: false);
            file.ToolTip = "Pick a single file";
            file.Click += (_, _) => PickSource(file: true);
            FileBackupUi.AddField(main, "Back up", PathRow(_source, file, folder));

            _destination.ToolTip = "The backup gets a folder of its own inside this one. Another drive is best: a copy on the same drive is lost with it.";
            var choose = FileBackupUi.TextButton("Choose…", primary: false);
            choose.Click += (_, _) => PickDestination();
            // NEW (backup drives): the drives worth offering, under the box.
            var saveTo = new StackPanel();
            saveTo.Children.Add(PathRow(_destination, choose));
            // NEW (backup contents): said only when it is so.
            _sameDrive.Text = "Same drive as the original: if that drive fails, both are lost.";
            _sameDrive.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
            saveTo.Children.Add(_sameDrive);
            saveTo.Children.Add(_suggestions);
            FileBackupUi.AddField(main, "Save to", saveTo);
        }
        else
        {
            FileBackupUi.AddField(main, "Back up", Fixed(_plan.Source));
            FileBackupUi.AddField(main, "Saved in", Fixed(_plan.Root));
        }

        _name.Text = _plan.Name;
        _name.TextChanged += (_, _) =>
        {
            if (!_filling)
                _nameTyped = true;
        };
        FileBackupUi.AddField(main, "Name", FileBackupUi.Framed(_name));
        if (_isNew)
        {
            // NEW (backup contents): what is in it, which sets the options recommended for that.
            FileBackupUi.AddField(main, "Contents", _contents);
            _destination.TextChanged += (_, _) => DestinationChanged();
            _detectTimer.Tick += async (_, _) =>
            {
                _detectTimer.Stop();
                await DetectAsync();
            };
        }

        var schedule = new StackPanel();
        schedule.Children.Add(_scheduleChips);
        schedule.Children.Add(_scheduleDetail);
        _time.ToolTip = "If the PC is off at that time, the backup runs when it is next on.";
        _time.LostKeyboardFocus += (_, _) => ReadTime(quiet: true);
        _everyNumber.TextChanged += (_, _) =>
        {
            if (!_filling)
                ReadEvery(quiet: true);
        };
        FileBackupUi.AddField(main, "How often", schedule);
        FileBackupUi.AddField(main, "Keep", _retentionCell); // NEW (retention)

        // NEW (zip tick box): a normal copy, or one .zip per run.
        _zip = FileBackupUi.Tick("Zip to save space",
            "Packs the copy into one .zip file instead of a folder of files.\n" +
            "Smaller for documents and the like; photos, videos and music hardly shrink.\n" +
            "Every run packs everything again, so it suits small and medium folders.");
        _zip.IsChecked = _plan.Type == FileBackupType.Zip;
        _zip.Click += (_, _) =>
        {
            Touched();
            Reshape();
        };
        if (_isNew)
            FileBackupUi.AddField(main, string.Empty, _zip);

        // ---- more options
        _exclude.Text = string.Join(", ", _plan.Exclude);
        _exclude.ToolTip = "Names of files and folders to leave out, separated by commas.\nA name is left out wherever it is found.\nFor example: node_modules, *.tmp, Thumbs.db";
        _cloud = FileBackupUi.Tick("Include online-only cloud files",
            "OneDrive and similar files that are not on this PC are left out, because copying them downloads them first.");
        _cloud.IsChecked = _plan.IncludeCloudOnly;
        _cloud.Margin = new Thickness(0, 10, 0, 0);
        var skip = new StackPanel();
        skip.Children.Add(FileBackupUi.Framed(_exclude));
        skip.Children.Add(_cloud);

        var options = FileBackupUi.Fields();
        FileBackupUi.AddField(options, "Kind", _kind);
        // CHANGED (retention): "Keep" is on show above now; what a Clone keeps besides its copy is "Also keep".
        FileBackupUi.AddField(options, "Also keep", _keep);
        _alsoKeepLabel = options.Children.OfType<TextBlock>().Last();
        _alsoKeepCell = options.Children.OfType<Border>().Last();
        FileBackupUi.AddField(options, "Skip", skip);
        _more = FileBackupUi.Section(null, options);
        _more.Margin = new Thickness(0, 10, 0, 0);
        _more.Visibility = Visibility.Collapsed;

        _moreToggle = FileBackupUi.TextButton("More options", primary: false);
        _moreToggle.HorizontalAlignment = HorizontalAlignment.Left;
        _moreToggle.Margin = new Thickness(0, 12, 0, 0);
        _moreToggle.Click += (_, _) =>
        {
            var open = _more.Visibility != Visibility.Visible;
            _more.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            _moreToggle.Content = open ? "Fewer options" : "More options";
        };

        var sections = new StackPanel();
        sections.Children.Add(FileBackupUi.Section(null, main));
        sections.Children.Add(_moreToggle);
        sections.Children.Add(_more);
        var scroll = new ScrollViewer
        {
            Content = sections,
            MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 300),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        // ---- bottom
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        var cancel = FileBackupUi.TextButton("Cancel", primary: false);
        cancel.IsCancel = true; // Esc
        cancel.Click += (_, _) => Close();
        // A new backup is usually wanted at once, so that is the main button (and Enter); a change to an
        // existing one is usually just saved.
        _save = FileBackupUi.TextButton("Save", primary: !_isNew);
        _save.IsDefault = !_isNew;
        _save.Click += async (_, _) => await SaveAsync(runNow: false);
        _saveAndRun = FileBackupUi.TextButton("Save and back up now", primary: _isNew);
        _saveAndRun.IsDefault = _isNew;
        _saveAndRun.Click += async (_, _) => await SaveAsync(runNow: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_save);
        buttons.Children.Add(_saveAndRun);
        DockPanel.SetDock(buttons, Dock.Right);
        var bottom = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(scroll);
        stack.Children.Add(bottom);
        FileBackupUi.Fill(this, stack);

        _everyInDays = _plan.Schedule.EveryHours % 24 == 0;
        ShowKind();
        ShowSchedule();
        if (_isNew)
            SourceChanged();

        if (_isNew)
        {
            ShowContents();
            // NEW (backup drives): offered when the window opens, and again when a drive comes or goes.
            Loaded += async (_, _) => await SuggestDrivesAsync();
            // NEW (backup contents): what was clicked to open the window is looked at right away.
            Loaded += async (_, _) => await DetectAsync();
            DriveWatch.Changed += OnDrivesChanged;
            Closed += (_, _) =>
            {
                DriveWatch.Changed -= OnDrivesChanged;
                _closed = true;
                _detectTimer.Stop();
            };
        }

        Loaded += (_, _) =>
        {
            if (_isNew && _source.Text.Length == 0)
                _source.Focus();
            else if (_isNew && _destination.Text.Length == 0)
                _destination.Focus();
            else
                _name.Focus();
        };
    }

    // ------------------------------------------------------------------ what and where

    // A path box with its buttons to the right.
    private static DockPanel PathRow(TextBox box, params Button[] buttons)
    {
        var row = new DockPanel();
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
        }

        row.Children.Add(FileBackupUi.Framed(box));
        return row;
    }

    private static TextBlock Fixed(string path)
    {
        var text = new TextBlock { Text = path, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = path, Margin = new Thickness(0, 6, 0, 0) };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        text.FontSize = 12;
        return text;
    }

    // Until a name is typed, the backup is named after what it backs up.
    private void SourceChanged()
    {
        if (!_nameTyped)
        {
            _filling = true;
            _name.Text = FileBackupPaths.DefaultName(_source.Text);
            _filling = false;
        }

        // NEW (backup contents): what it holds is looked at once the typing stops.
        ShowSameDrive();
        if (IsLoaded)
        {
            _detectTimer.Stop();
            _detectTimer.Start();
        }

        // NEW (backup drives): the drive being backed up is never offered, so another drive means another offer.
        if (IsLoaded && SourceDrive() != _suggestedForDrive)
            _ = SuggestDrivesAsync();
    }

    // ------------------------------------------------------------------ NEW (backup drives): drives on offer

    private string SourceDrive()
    {
        var source = _source.Text.Trim();
        try { return source.Length > 0 && Path.IsPathFullyQualified(source) ? Path.GetPathRoot(source) ?? string.Empty : string.Empty; }
        catch (ArgumentException) { return string.Empty; }
    }

    private async void OnDrivesChanged(DriveChange change)
    {
        try { await SuggestDrivesAsync(); }
        catch (Exception) { } // an offer that could not be worked out is no offer; the box still takes any folder
    }

    // Looks at the connected drives off the UI thread and shows a button for each one worth offering.
    private async Task SuggestDrivesAsync()
    {
        var run = ++_suggestRun;
        var source = _source.Text.Trim();
        _suggestedForDrive = SourceDrive();
        var plans = _others;

        List<BackupDriveSuggestion> found;
        try { found = await Task.Run(() => FileBackupDrives.Look(source, plans)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            found = [];
        }

        if (run != _suggestRun)
            return; // a newer look is on its way

        _suggestions.Children.Clear();
        _suggestedFolders.Clear();
        foreach (var drive in found)
        {
            _suggestedFolders.Add(drive.Folder);
            _suggestions.Children.Add(DriveButton(drive));
        }

        _suggestions.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // A drive that holds backups already: filled symbol in the accent colour, bold name, how many backups.
    // An external drive without any: a plain drive symbol, plain name, its free room.
    private Button DriveButton(BackupDriveSuggestion drive)
    {
        var symbol = new TextBlock { Text = drive.HasBackups ? "\uE74E" : "\uE88E", FontSize = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }; // Save / USB
        symbol.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        symbol.SetResourceReference(TextBlock.ForegroundProperty, drive.HasBackups ? "Accent" : "InkMuted");

        var name = new TextBlock { Text = drive.Title, FontWeight = drive.HasBackups ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        var detail = FileBackupUi.Muted("  ·  " + (drive.HasBackups
            ? (drive.Backups == 1 ? "1 backup" : $"{drive.Backups:N0} backups")
            : Models.FileSystemItem.FormatSize(drive.FreeBytes) + " free"));
        detail.VerticalAlignment = VerticalAlignment.Center;

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(symbol);
        content.Children.Add(name);
        content.Children.Add(detail);

        var button = new Button
        {
            Content = content,
            Height = 28,
            Padding = new Thickness(10, 0, 12, 0),
            Margin = new Thickness(0, 0, 6, 6),
            ToolTip = drive.HasBackups
                ? $"Your backups are on this drive: {string.Join(", ", drive.Names.Take(6))}{(drive.Names.Count > 6 ? "…" : string.Empty)}.\nClick to keep this one beside them, in {drive.Folder}."
                : $"An external drive with no backups on it yet.\nClick to keep this one in {drive.Folder}."
        };
        button.SetResourceReference(StyleProperty, "ViewerTextButton");
        System.Windows.Automation.AutomationProperties.SetName(button, drive.Title);
        button.Click += (_, _) =>
        {
            _destination.Text = drive.Folder; // DestinationChanged shows what depends on the drive
        };
        return button;
    }

    private void PickSource(bool file)
    {
        var typed = _source.Text.Trim();
        var start = Directory.Exists(typed) ? typed : Path.GetDirectoryName(typed) is { Length: > 0 } parent && Directory.Exists(parent) ? parent : KnownFolders.Profile;
        var picker = new FolderPickerWindow(new FolderPickerOptions(
            file ? "Back up a file" : "Back up a folder or a drive",
            file
                ? "Open the folder the file is in, click the file, then choose Back up this file."
                : "Open the folder or the drive to back up, then choose Back up this folder.",
            file ? "Back up this file" : "Back up this folder",
            start)
        {
            Places = _places,
            ShowHidden = SettingsService.GetShowHiddenItems(),
            FileExtension = file ? string.Empty : null // any file
        })
        {
            Owner = this
        };

        if (picker.ShowDialog() == true && picker.Chosen is { } chosen)
            _source.Text = chosen;
    }

    private void PickDestination()
    {
        var typed = _destination.Text.Trim();
        var picker = new FolderPickerWindow(new FolderPickerOptions(
            "Where to keep the backup",
            "Open the folder the backup goes into, then choose Keep it here. It gets a folder of its own inside.",
            "Keep it here",
            Directory.Exists(typed) ? typed : KnownFolders.Profile)
        {
            Places = _places,
            WhyNotChoose = folder => InsideSource(folder) ? "This folder is inside what you are backing up." : null
        })
        {
            Owner = this
        };

        if (picker.ShowDialog() == true && picker.Chosen is { } chosen)
            _destination.Text = chosen; // DestinationChanged shows what depends on the drive
    }

    // Whether a folder is the folder being backed up, or under it (a single file has nothing under it).
    private bool InsideSource(string folder)
    {
        var source = _source.Text.Trim();
        return source.Length > 0 && Path.IsPathFullyQualified(source) && Directory.Exists(source) && FileBackupPaths.IsInside(folder, source);
    }

    // ------------------------------------------------------------------ zip or not, kind, and what is kept

    // NEW (retention, zip tick box): "Keep" or "Zip to save space" was changed. For a new backup the two
    // decide its kind; an existing one keeps its kind and only how many runs it keeps changes.
    private void Reshape()
    {
        if (_isNew)
            FileBackupRules.Shape(_plan, _zip.IsChecked == true, _retention, _latestKind);
        else if (_plan.Type is FileBackupType.Versions or FileBackupType.Zip)
            FileBackupRules.SetRetention(_plan, _retention);
        ShowKind();
    }

    // NEW (retention): the "Keep" row. One choice: only the latest run, the newest so many, or every run of
    // the last so many days.
    private void ShowRetention()
    {
        _retentionCell.Children.Clear();

        if (!_isNew && _plan.IsCopy)
        {
            // An existing Clone or Add and update has one copy, and its kind cannot be changed.
            var only = FileBackupUi.Muted("Latest only");
            only.Margin = new Thickness(0, 6, 0, 0);
            only.ToolTip = "This backup keeps one copy. To keep several, make a new backup and choose how many.";
            _retentionCell.Children.Add(only);
            return;
        }

        var choices = new List<FileBackupRetention>
        {
            FileBackupRetention.Latest, new(5), new(10), new(30), new(1, 30), new(1, 90), new(1, 365)
        };
        if (!choices.Contains(_retention))
            choices.Insert(1, _retention); // a number of its own stays as it is

        var chips = new WrapPanel();
        foreach (var choice in choices)
        {
            var chip = FileBackupUi.Chip(choice.Label, choice == _retention, () =>
            {
                _retention = choice;
                Touched();
                Reshape();
            });
            chip.ToolTip = choice.LatestOnly ? "Each run replaces the one before."
                : choice.Days > 0 ? $"Every run of the last {choice.Label} is kept. Older ones are deleted; the newest always stays."
                : $"The newest {choice.Count:N0} runs are kept. Older ones are deleted.";
            chips.Children.Add(chip);
        }
        _retentionCell.Children.Add(chips);

        // One short line when more than the latest is kept, since that is what makes a run a dated copy.
        if (_retention.LatestOnly)
            return;

        var note = "Each run makes a dated copy. Unchanged files take no extra space.";
        if (_plan.Type == FileBackupType.Zip)
            note = "Each run makes a new zip.";
        else if (FormatOf(_isNew ? _destination.Text.Trim() : _plan.Destination) is { } format &&
                 !format.Equals("NTFS", StringComparison.OrdinalIgnoreCase) && !format.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
            note = $"Each run makes a dated copy. That drive ({format}) stores every one in full.";

        var under = FileBackupUi.Muted(note);
        under.FontSize = 11.5;
        _retentionCell.Children.Add(under);
    }

    private void ShowKind()
    {
        ShowRetention();
        _kind.Children.Clear();

        // CHANGED (retention): the kind can be chosen only between the two that keep one copy; keeping
        // several, or zipping, is a kind of its own and is just named here.
        if (_isNew && _plan.IsCopy)
        {
            var chips = new WrapPanel();
            foreach (var type in new[] { FileBackupType.Clone, FileBackupType.AddAndUpdate })
            {
                var chip = FileBackupUi.Chip(FileBackupPlan.TypeName(type), _plan.Type == type, () =>
                {
                    _latestKind = type;
                    Touched();
                    Reshape();
                });
                chip.ToolTip = FileBackupPlan.TypeHint(type);
                chips.Children.Add(chip);
            }
            _kind.Children.Add(chips);
        }
        else
        {
            _kind.Children.Add(new TextBlock { Text = FileBackupPlan.TypeName(_plan.Type), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), ToolTip = FileBackupPlan.TypeHint(_plan.Type) });
        }

        var under = FileBackupUi.Muted(FileBackupPlan.TypeShort(_plan.Type));
        under.FontSize = 11.5;
        under.Margin = new Thickness(0, 2, 0, 0);
        _kind.Children.Add(under);
        ShowKeep();
    }

    // ------------------------------------------------------------------ NEW (backup contents): what is in it

    // Whether dated copies on the drive the backup goes to share their unchanged files (true while the drive is not known).
    private bool SharesSpace()
        => FormatOf(_isNew ? _destination.Text.Trim() : _plan.Destination) is not { } format ||
           format.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || format.Equals("ReFS", StringComparison.OrdinalIgnoreCase);

    // The buttons, the chosen one lit, and under them what the recommendation set (while it still stands).
    private void ShowContents()
    {
        _contents.Children.Clear();
        var chips = new WrapPanel();
        foreach (var content in FileBackupContents.All)
        {
            var chip = FileBackupUi.Chip(FileBackupContents.Name(content), _content == content, () =>
            {
                _contentPicked = true;
                _content = content;
                ApplyAdvice();
            });
            chip.ToolTip = FileBackupContents.Hint(content) + "\nClick to use the settings recommended for it.";
            chips.Children.Add(chip);
        }
        _contents.Children.Add(chips);

        if (_applied is null || _touched)
            return;

        var line = FileBackupUi.Muted(_applied.Why);
        line.FontSize = 11.5;
        if (_applied.Skip.Count > 0)
            line.ToolTip = $"Skipped wherever they are found: {string.Join(", ", _applied.Skip)}.\nChange it under More options, Skip.";
        _contents.Children.Add(line);
    }

    // Sets the options to what is recommended for the chosen contents, and shows them.
    private void ApplyAdvice()
    {
        if (!_isNew || _content is not { } content)
            return;

        ReadTime(quiet: true); // what is typed in the boxes now, before they are rebuilt
        ReadEvery(quiet: true);

        _appliedShares = SharesSpace();
        var advice = FileBackupContents.Advise(content, _tools, _appliedShares);
        _plan.Exclude = FileBackupRules.ParseExclude(_exclude.Text);
        FileBackupContents.Apply(_plan, advice, _applied, schedule: !_scheduleChosen);
        _applied = advice;
        _touched = false;

        _retention = advice.Keep;
        _latestKind = FileBackupType.Clone;
        _zip.IsChecked = false;
        _filling = true;
        _exclude.Text = string.Join(", ", _plan.Exclude);
        _filling = false;

        ShowKind();
        ShowSchedule();
        ShowContents();
    }

    // An option a recommendation sets was changed by hand: its line no longer says what is set, and
    // Clearspace changes nothing more by itself. A click on a Contents button sets it again.
    private void Touched()
    {
        if (_touched)
            return;
        _touched = true;
        if (_applied is not null)
            ShowContents();
    }

    // Looks at what is backed up, off the UI thread, and chooses the Contents button for it.
    private async Task DetectAsync()
    {
        var run = ++_detectRun;
        var source = _source.Text.Trim();
        if (source.Length == 0)
            return;

        FileBackupFinding? finding;
        try { finding = await Task.Run(() => FileBackupContents.Detect(source)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            finding = null;
        }

        if (run != _detectRun || _closed || finding is not { } found)
            return; // a newer look is on its way, or it is not there (yet)

        _tools = found.Tools;
        if (!_contentPicked)
            _content = found.Content;
        if (_touched)
            ShowContents(); // the button says what it is; the options chosen by hand stay
        else
            ApplyAdvice();  // also for a button chosen by hand: the folders to skip depend on what was found
    }

    private void ShowSameDrive()
        => _sameDrive.Visibility = FileBackupPaths.OnSameDrive(_source.Text, _destination.Text) ? Visibility.Visible : Visibility.Collapsed;

    // Where the backup goes decides whether keeping several backups is cheap, so the recommendation is
    // made again for another kind of drive; otherwise only the note under "Keep" may change.
    private void DestinationChanged()
    {
        ShowSameDrive();
        if (_applied is not null && !_touched && SharesSpace() != _appliedShares)
            ApplyAdvice();
        else
            ShowRetention();
    }

    // How a drive is formatted ("NTFS", "exFAT"); null when that cannot be told quickly (a network folder, no drive yet).
    private static string? FormatOf(string folder)
    {
        try
        {
            if (folder.Length < 3 || folder.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(folder))
                return null;
            var drive = new DriveInfo(Path.GetPathRoot(folder)!);
            return drive is { DriveType: DriveType.Fixed or DriveType.Removable, IsReady: true } ? drive.DriveFormat : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // What a Clone or Add and update keeps besides its copy ("Also keep" under More options). The other
    // kinds keep whole runs, which is the "Keep" row above, so the row is hidden for them.
    private void ShowKeep()
    {
        _keep.Children.Clear();
        var applies = _plan.IsCopy;
        if (_alsoKeepLabel is not null && _alsoKeepCell is not null)
            _alsoKeepLabel.Visibility = _alsoKeepCell.Visibility = applies ? Visibility.Visible : Visibility.Collapsed;
        if (!applies)
            return;

        if (_plan.Type == FileBackupType.Clone)
        {
            var removed = FileBackupUi.Tick("Files it removes",
                "A file that is gone from the original is moved to \"Earlier versions\" in the backup instead of being removed, so a file deleted by mistake can still be had back.");
            removed.IsChecked = _plan.KeepRemoved;
            removed.Margin = new Thickness(0, 5, 0, 0);
            removed.Click += (_, _) =>
            {
                _plan.KeepRemoved = removed.IsChecked == true;
                Touched();
                ShowKeep();
            };
            _keep.Children.Add(removed);
        }

        var versions = FileBackupUi.Tick("Earlier versions of changed files",
            "File history: before a changed file replaces the one in the backup, the one in the backup is kept.\nRight-click a file and choose Earlier versions… to go back to one.");
        versions.IsChecked = _plan.KeepVersions;
        versions.Margin = new Thickness(0, _keep.Children.Count > 0 ? 8 : 5, 0, 0);
        versions.Click += (_, _) =>
        {
            _plan.KeepVersions = versions.IsChecked == true;
            Touched();
            ShowKeep();
        };
        _keep.Children.Add(versions);

        // For how long: only while something is kept.
        if (!_plan.KeepVersions && !(_plan.Type == FileBackupType.Clone && _plan.KeepRemoved))
            return;

        var periods = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var lead = FileBackupUi.Muted("for");
        lead.VerticalAlignment = VerticalAlignment.Center;
        lead.Margin = new Thickness(0, 0, 10, 6);
        periods.Children.Add(lead);
        var choices = new List<int> { 30, 90, 365, 0 };
        if (!choices.Contains(_plan.KeepDays))
            choices.Insert(0, _plan.KeepDays);
        foreach (var days in choices)
        {
            periods.Children.Add(FileBackupUi.Chip(PeriodLabel(days), _plan.KeepDays == days, () =>
            {
                _plan.KeepDays = days;
                Touched();
                ShowKeep();
            }));
        }
        _keep.Children.Add(periods);
    }

    private static string PeriodLabel(int days) => days switch
    {
        <= 0 => "Forever",
        365 => "1 year",
        1 => "1 day",
        _ => $"{days:N0} days"
    };

    // ------------------------------------------------------------------ how often

    private void ShowSchedule()
    {
        var schedule = _plan.Schedule;
        _scheduleChips.Children.Clear();
        foreach (var (kind, label, tip) in new[]
                 {
                     (FileBackupSchedule.Off, "Manual", "Only when you choose Back up now"),
                     (FileBackupSchedule.Hourly, "Hourly", "Every hour"),
                     (FileBackupSchedule.Daily, "Daily", "Every day at a time you set"),
                     (FileBackupSchedule.Weekly, "Weekly", "On the days you pick, at a time you set"),
                     (FileBackupSchedule.Custom, "Custom", "Every so many hours or days")
                 })
        {
            var chip = FileBackupUi.Chip(label, schedule.Kind == kind || (kind == FileBackupSchedule.Off && !KnownKind(schedule.Kind)), () =>
            {
                ReadTime(quiet: true); // what is in the box now, before the box is rebuilt
                schedule.Kind = kind;
                _scheduleChosen = true; // NEW (backup contents)
                ShowSchedule();
            });
            chip.ToolTip = tip;
            _scheduleChips.Children.Add(chip);
        }

        _scheduleDetail.Children.Clear();
        _filling = true;
        _time.Text = FileBackupRules.TimeText(schedule.AtMinute);
        _everyNumber.Text = (_everyInDays ? Math.Max(1, schedule.EveryHours / 24) : schedule.EveryHours).ToString(CultureInfo.CurrentCulture);
        _filling = false;

        if (schedule.Kind is FileBackupSchedule.Daily or FileBackupSchedule.Weekly)
        {
            // One row: the days (weekly), then "at" and the time.
            var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            if (schedule.Kind == FileBackupSchedule.Weekly)
            {
                foreach (var day in Enumerable.Range(1, 7).Select(index => (DayOfWeek)(index % 7))) // Monday first
                {
                    row.Children.Add(FileBackupUi.Chip(CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day), schedule.Runs(day), () =>
                    {
                        ReadTime(quiet: true);
                        schedule.Days ^= 1 << (int)day;
                        ShowSchedule();
                    }));
                }
            }

            var at = FileBackupUi.Muted("at");
            at.VerticalAlignment = VerticalAlignment.Center;
            at.Margin = new Thickness(schedule.Kind == FileBackupSchedule.Weekly ? 6 : 0, 0, 10, 6);
            Detach(_time);
            var frame = FileBackupUi.Framed(_time);
            frame.Padding = new Thickness(6, 4, 6, 4);
            frame.Margin = new Thickness(0, 0, 0, 6);
            row.Children.Add(at);
            row.Children.Add(frame);
            _scheduleDetail.Children.Add(row);
        }
        else if (schedule.Kind == FileBackupSchedule.Custom)
        {
            var every = FileBackupUi.Muted("Every");
            every.VerticalAlignment = VerticalAlignment.Center;
            every.Margin = new Thickness(0, 0, 10, 0);
            Detach(_everyNumber);
            var frame = FileBackupUi.Framed(_everyNumber);
            frame.Padding = new Thickness(6, 4, 6, 4);
            frame.Margin = new Thickness(0, 0, 10, 0);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(every);
            row.Children.Add(frame);
            foreach (var (inDays, label) in new[] { (false, "Hours"), (true, "Days") })
            {
                var chip = FileBackupUi.Chip(label, _everyInDays == inDays, () =>
                {
                    ReadEvery(quiet: true);
                    _everyInDays = inDays;
                    ReadEvery(quiet: true); // the same number, in the other unit
                    ShowSchedule();
                });
                chip.Margin = new Thickness(0, 0, 6, 0);
                row.Children.Add(chip);
            }
            _scheduleDetail.Children.Add(row);
        }
    }

    private static bool KnownKind(string kind)
        => kind is FileBackupSchedule.Hourly or FileBackupSchedule.Daily or FileBackupSchedule.Weekly or FileBackupSchedule.Custom;

    // A text box sits in a frame; before it is put in a new one it has to leave the old one.
    private static void Detach(TextBox box)
    {
        if (box.Parent is Border frame)
            frame.Child = null;
    }

    // Reads the time box into the plan. False (and, unless quiet, a line saying so) when it is not a time.
    private bool ReadTime(bool quiet)
    {
        if (_plan.Schedule.Kind is not (FileBackupSchedule.Daily or FileBackupSchedule.Weekly))
            return true;

        if (FileBackupRules.TryParseTime(_time.Text, out var minutes))
        {
            _plan.Schedule.AtMinute = minutes;
            return true;
        }

        if (!quiet)
            ShowStatus($"\"{_time.Text.Trim()}\" is not a time of day. Write it like {FileBackupRules.TimeText(14 * 60 + 30)}.", problem: true);
        return false;
    }

    private bool ReadEvery(bool quiet)
    {
        if (_plan.Schedule.Kind != FileBackupSchedule.Custom)
            return true;

        var most = _everyInDays ? FileBackupSchedule.LongestHours / 24 : 999;
        if (int.TryParse(_everyNumber.Text.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out var count) && count >= 1 && count <= most)
        {
            _plan.Schedule.EveryHours = _everyInDays ? count * 24 : count;
            return true;
        }

        if (!quiet)
            ShowStatus($"Type a number from 1 to {most} for how often.", problem: true);
        return false;
    }

    // ------------------------------------------------------------------ saving

    private async Task SaveAsync(bool runNow)
    {
        if (_busy)
            return;
        if (!ReadTime(quiet: false) || !ReadEvery(quiet: false))
            return;

        var plan = _plan.Copy();
        plan.Name = _name.Text.Trim();
        plan.Exclude = FileBackupRules.ParseExclude(_exclude.Text);
        plan.IncludeCloudOnly = _cloud.IsChecked == true;
        if (_isNew)
        {
            plan.Source = FileBackupPaths.Normalize(_source.Text);
            plan.Destination = FileBackupPaths.Normalize(_destination.Text);
        }

        _busy = true;
        _save.IsEnabled = _saveAndRun.IsEnabled = false;
        ShowStatus("Checking…", problem: false);
        try
        {
            var isNew = _isNew;
            var others = _others;
            // NEW (backup drives): a folder Clearspace itself offered ("E:\Backups") is made if it is not there.
            var offered = _suggestedFolders.Contains(plan.Destination);
            var problem = await Task.Run(() => FileBackupRules.Prepare(plan, others, isNew, createDestination: offered));
            if (problem is not null)
            {
                ShowStatus(problem, problem: true);
                return;
            }

            Result = plan;
            RunNow = runNow;
            DialogResult = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowStatus(error.Message, problem: true);
        }
        finally
        {
            _busy = false;
            _save.IsEnabled = _saveAndRun.IsEnabled = true;
        }
    }

    private void ShowStatus(string text, bool problem)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "Danger" : "InkMuted");
    }
}
