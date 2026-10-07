// Clearspace | NEW (move in Clearspace): Clearspace's own "choose a folder" dialog.
// "Move to a folder…" on a Just arrived tile used to open Windows' folder picker, which is File Explorer
// in a small window. This one is Clearspace: the places from your sidebar on the left (Your files,
// Favorites, your categories, the drives), the folders inside the place you are in on the right, and the
// path at the top, which can also be typed or pasted. A click on a folder goes into it; the button at the
// bottom chooses the folder that is open. "New folder" makes one there and goes into it.
// The dialog knows nothing about moving: the caller says what the button is called and which folders
// may not be entered or chosen (FolderPickerOptions), and reads Chosen afterwards.
// Built in code from the shared styles in App.xaml and the theme's colours, like MessageDialog.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clearspace.Native;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

/// <summary>What a folder picker shows and allows.</summary>
/// <param name="Heading">The large line at the top ("Move report.pdf").</param>
/// <param name="Detail">One line under it saying what to do.</param>
/// <param name="ChooseLabel">The main button ("Move here").</param>
/// <param name="StartFolder">The folder open at first. One that is not there falls back to the user folder.</param>
internal sealed record FolderPickerOptions(string Heading, string Detail, string ChooseLabel, string StartFolder)
{
    /// <summary>The window's sidebar entries, shown as the places to jump to. Pages that are not folders are left out.</summary>
    public IReadOnlyList<SidebarEntry> Places { get; init; } = [];

    /// <summary>Whether hidden folders are listed (the main window's "Show hidden items").</summary>
    public bool ShowHidden { get; init; }

    /// <summary>Why a folder may not be opened here, or null when it may.</summary>
    public Func<string, string?>? WhyNotEnter { get; init; }

    /// <summary>Why the folder that is open may not be chosen, or null when it may. Shown beside the button.</summary>
    public Func<string, string?>? WhyNotChoose { get; init; }

    /// <summary>NEW (recommended folders): folders to offer above the list, best first, each with its
    /// reason (Services/FilingAdvisor.cs). A click opens one; nothing is chosen until the main button.</summary>
    public IReadOnlyList<FolderRecommendation> Recommended { get; init; } = [];

    /// <summary>NEW (pick a file): when set (".csbackup"), the chooser picks a FILE with this ending
    /// instead of a folder. Files with the ending are listed under the folders; a click marks one, the
    /// main button (or a double click) chooses it, and Chosen is the file's path. Used for "Restore from
    /// a file…", so that too stays in Clearspace instead of opening Windows' file picker.
    /// CHANGED (file backup): an empty ending ("") picks any file; the folders are then listed first.</summary>
    public string? FileExtension { get; init; }

    /// <summary>What such a file is called in the hints ("backup").</summary>
    public string FileWord { get; init; } = "file";
}

internal sealed class FolderPickerWindow : Window
{
    private const int MostRows = 1500; // more folders than this in one place: the rest are counted, not listed

    private readonly FolderPickerOptions _options;
    private readonly TextBox _path;
    private readonly Button _up;
    private readonly StackPanel _folders = new();
    private readonly ScrollViewer _foldersScroll;
    private readonly Border _newRow;
    private readonly TextBox _newName;
    private readonly TextBlock _status;
    private readonly Button _choose;
    private string _current = string.Empty;
    private int _request; // goes up with every folder opened; a listing that arrives for an older one is dropped
    private string? _pickedFile;  // NEW (pick a file): the file marked in the list, when files are being picked
    private Button? _pickedRow;

    private bool PicksFile => _options.FileExtension is not null;

    // CHANGED (file backup): picking a file of any kind ("What to back up" > File…), not one with a set ending.
    private bool AnyFile => _options.FileExtension is { Length: 0 };

    /// <summary>The folder that was chosen (or the file, when files are being picked); null when the dialog was cancelled.</summary>
    internal string? Chosen { get; private set; }

    internal FolderPickerWindow(FolderPickerOptions options)
    {
        _options = options;

        Title = options.Heading;
        Width = 760;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Base");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(FontFamilyProperty, "UIFont");

        // Header: round icon badge + what is being done.
        var glyph = new TextBlock
        {
            Text = "", // Move to folder
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

        var heading = new TextBlock { Text = options.Heading, FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var detail = Muted(options.Detail);
        detail.Margin = new Thickness(0, 3, 0, 0);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(heading);
        titles.Children.Add(detail);

        var header = new DockPanel();
        header.Children.Add(badge);
        header.Children.Add(titles);

        // The path row: up, the path (it can be typed), new folder.
        _up = IconButton("", "Up to the folder this one is in");
        _up.Click += (_, _) =>
        {
            if (ParentOf(_current) is { } parent)
                _ = OpenAsync(parent);
        };

        _path = new TextBox();
        _path.SetResourceReference(StyleProperty, "PlainTextBox");
        _path.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;

            e.Handled = true; // Enter in the path box goes there; it does not press the main button
            _ = OpenAsync(_path.Text);
        };
        var pathFrame = Framed(_path);
        pathFrame.Margin = new Thickness(6, 0, 0, 0);

        var newFolder = TextButton("New folder", primary: false);
        newFolder.Click += (_, _) => ShowNewFolderRow(true);
        if (options.FileExtension is not null)
            newFolder.Visibility = Visibility.Collapsed; // NEW (pick a file): nothing is being put anywhere
        DockPanel.SetDock(_up, Dock.Left);
        DockPanel.SetDock(newFolder, Dock.Right);

        var pathRow = new DockPanel { Margin = new Thickness(0, 20, 0, 0) };
        pathRow.Children.Add(_up);
        pathRow.Children.Add(newFolder);
        pathRow.Children.Add(pathFrame);

        // The row for naming a new folder, shown only while one is being made.
        _newName = new TextBox();
        _newName.SetResourceReference(StyleProperty, "PlainTextBox");
        _newName.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CreateFolder();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true; // Esc closes this row, not the dialog
                ShowNewFolderRow(false);
            }
        };
        var create = TextButton("Create", primary: true);
        create.Click += (_, _) => CreateFolder();
        var dismiss = TextButton("Cancel", primary: false);
        dismiss.Click += (_, _) => ShowNewFolderRow(false);
        var newLabel = Muted("Name");
        newLabel.VerticalAlignment = VerticalAlignment.Center;
        newLabel.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(newLabel, Dock.Left);
        DockPanel.SetDock(create, Dock.Right);
        DockPanel.SetDock(dismiss, Dock.Right);
        var newPanel = new DockPanel();
        newPanel.Children.Add(newLabel);
        newPanel.Children.Add(create);
        newPanel.Children.Add(dismiss);
        newPanel.Children.Add(Framed(_newName));
        _newRow = new Border { Margin = new Thickness(0, 10, 0, 0), Child = newPanel, Visibility = Visibility.Collapsed };

        // Places on the left, the folders of the place that is open on the right.
        var places = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        AddPlaces(places);
        var placesScroll = new ScrollViewer
        {
            Content = places,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var placesCard = new Border { Width = 210, Margin = new Thickness(0, 0, 10, 0), Child = placesScroll };
        placesCard.SetResourceReference(Border.BackgroundProperty, "Tint");
        placesCard.SetResourceReference(Border.CornerRadiusProperty, "R8");
        DockPanel.SetDock(placesCard, Dock.Left);

        _folders.Margin = new Thickness(0, 6, 0, 6);
        _foldersScroll = new ScrollViewer
        {
            Content = _folders,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var foldersCard = new Border { Child = _foldersScroll };
        foldersCard.SetResourceReference(Border.BackgroundProperty, "Tint");
        foldersCard.SetResourceReference(Border.CornerRadiusProperty, "R8");

        // CHANGED (recommended folders): a little shorter when the recommendations sit above it, so the
        // dialog still fits a small screen.
        var recommended = RecommendedCard();
        var browser = new DockPanel { Height = recommended is null ? 340 : 264, Margin = new Thickness(0, 12, 0, 0) };
        browser.Children.Add(placesCard);
        browser.Children.Add(foldersCard);

        // Bottom row: why the open folder can't be chosen (or what went wrong) on the left, the buttons on the right.
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        var cancel = TextButton("Cancel", primary: false);
        cancel.IsCancel = true; // Esc
        cancel.Click += (_, _) => Close();
        _choose = TextButton(options.ChooseLabel, primary: true);
        _choose.IsDefault = true; // Enter
        _choose.IsEnabled = false;
        _choose.Click += (_, _) => ChooseCurrent();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_choose);
        DockPanel.SetDock(buttons, Dock.Right);
        var bottom = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(pathRow);
        stack.Children.Add(_newRow);
        if (recommended is not null)
            stack.Children.Add(recommended); // NEW (recommended folders)
        stack.Children.Add(browser);
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

        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        Loaded += async (_, _) =>
        {
            if (!await OpenAsync(options.StartFolder, quiet: true))
                await OpenAsync(KnownFolders.Profile, quiet: true);
        };
    }

    // ------------------------------------------------------------------ going to a folder

    // Opens a folder: the path box, the Up button, the main button and the list all follow. False (and the
    // reason in the status line, unless `quiet`) when it is not a folder that can be opened here.
    private async Task<bool> OpenAsync(string? folder, bool quiet = false)
    {
        var typed = (folder ?? string.Empty).Trim().Trim('"').Trim();
        if (typed.Length == 0)
            return false;

        string full;
        bool exists;
        try
        {
            full = Path.GetFullPath(typed);
            if (full.Length > 3)
                full = Path.TrimEndingDirectorySeparator(full);
            // Asking whether a folder exists can wait on a network drive, so not on this thread.
            exists = await Task.Run(() => Directory.Exists(full));
        }
        catch (Exception)
        {
            full = typed;
            exists = false;
        }

        if (!exists)
        {
            // NEW (pick a file): a file's own path typed or pasted in the box opens its folder with it marked.
            if (_options.FileExtension is { } ending && full.EndsWith(ending, StringComparison.OrdinalIgnoreCase) &&
                Path.GetDirectoryName(full) is { Length: > 0 } parent && await Task.Run(() => File.Exists(full)))
            {
                if (!await OpenAsync(parent, quiet))
                    return false;

                Pick(full, _folders.Children.OfType<Button>().FirstOrDefault(row => string.Equals(row.Tag as string, full, StringComparison.OrdinalIgnoreCase)));
                return true;
            }

            if (!quiet)
                ShowStatus($"{typed} is not a folder that can be opened.", problem: true);
            return false;
        }

        if (_options.WhyNotEnter?.Invoke(full) is { } refused)
        {
            if (!quiet)
                ShowStatus(refused, problem: true);
            return false;
        }

        _current = full;
        _pickedFile = null; // NEW (pick a file): what was marked in the folder before is not on screen any more
        _pickedRow = null;
        _path.Text = full;
        _path.CaretIndex = full.Length;
        _up.IsEnabled = ParentOf(full) is not null;
        ShowNewFolderRow(false);
        UpdateChoice();
        await LoadFoldersAsync(full);
        return true;
    }

    private static string? ParentOf(string folder)
    {
        try
        {
            return folder.Length == 0 ? null : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } parent ? parent : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The main button is on when the open folder may be chosen; otherwise the status line says why not.
    private void UpdateChoice()
    {
        // NEW (pick a file): the button is on once a file is marked; until then the line says what to do.
        if (PicksFile)
        {
            _choose.IsEnabled = _pickedFile is not null;
            ShowStatus(_pickedFile is { } marked
                ? Path.GetFileName(marked)
                : AnyFile
                    ? "Click a file to pick it. Files are listed under the folders."
                    : $"Click a {_options.FileWord} to pick it. Files ending in {_options.FileExtension} are listed under the folders.", problem: false);
            return;
        }

        var reason = _current.Length == 0 ? string.Empty : _options.WhyNotChoose?.Invoke(_current);
        _choose.IsEnabled = _current.Length > 0 && reason is null;
        ShowStatus(reason ?? string.Empty, problem: false);
    }

    private void ChooseCurrent()
    {
        // NEW (pick a file)
        if (PicksFile)
        {
            if (_pickedFile is null)
                return;

            Chosen = _pickedFile;
            DialogResult = true;
            return;
        }

        if (_current.Length == 0 || _options.WhyNotChoose?.Invoke(_current) is not null)
            return;

        Chosen = _current;
        DialogResult = true; // closes the dialog
    }

    // ------------------------------------------------------------------ the folders in the open folder

    private async Task LoadFoldersAsync(string folder)
    {
        var request = ++_request;
        _folders.Children.Clear();
        _folders.Children.Add(Note("Reading…"));

        List<string> found;
        List<string> files = []; // NEW (pick a file): the files that can be picked, listed under the folders
        string? trouble = null;
        try
        {
            var showHidden = _options.ShowHidden;
            var ending = _options.FileExtension;
            var read = await Task.Run(() =>
            {
                var folders = ReadFolders(folder, showHidden);
                var matching = ending is null ? new List<string>() : ReadFiles(folder, ending, showHidden);
                return (Folders: folders, Files: matching);
            });
            found = read.Folders;
            files = read.Files;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            found = [];
            trouble = error is UnauthorizedAccessException ? "You don't have permission to look inside this folder." : error.Message;
        }

        if (request != _request)
            return; // another folder was opened meanwhile

        _folders.Children.Clear();
        _foldersScroll.ScrollToTop();

        if (trouble is not null)
        {
            _folders.Children.Add(Note(trouble));
            return;
        }

        if (found.Count == 0 && files.Count == 0)
        {
            _folders.Children.Add(Note(AnyFile ? "Nothing in here." : PicksFile ? $"No folders and no {_options.FileWord} files in here." : "No folders in here."));
            return;
        }

        // NEW (pick a file): the files first when there are any, since they are what is being looked for.
        // CHANGED (file backup): when any file can be picked, the folders lead, as in a file list: with every
        // file of a folder on show, the way further in would otherwise be below all of them.
        if (!AnyFile)
        {
            foreach (var file in files.Take(MostRows))
                _folders.Children.Add(FileRow(file));
        }

        foreach (var path in found.Take(MostRows))
            _folders.Children.Add(Row("", Path.GetFileName(path), path, indent: false));

        if (AnyFile)
        {
            foreach (var file in files.Take(MostRows))
                _folders.Children.Add(FileRow(file));
            if (files.Count > MostRows)
                _folders.Children.Add(Note($"…and {files.Count - MostRows:N0} more files. Type or paste the file's path above to pick one of them."));
        }

        if (found.Count > MostRows)
            _folders.Children.Add(Note($"…and {found.Count - MostRows:N0} more. Type or paste the path above to reach one of them."));
    }

    // The folders directly inside one folder, in the order the file list uses (numbers count as numbers).
    // Hidden ones only when asked for; the ones Windows protects (hidden and system) never.
    internal static List<string> ReadFolders(string folder, bool showHidden)
    {
        var all = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
        var found = new List<string>();

        foreach (var entry in new DirectoryInfo(folder).EnumerateDirectories("*", all))
        {
            var hidden = (entry.Attributes & FileAttributes.Hidden) != 0;
            var system = (entry.Attributes & FileAttributes.System) != 0;
            if ((hidden && system) || (hidden && !showHidden))
                continue;

            found.Add(entry.FullName);
        }

        found.Sort((x, y) => NativeMethods.StrCmpLogicalW(Path.GetFileName(x), Path.GetFileName(y)));
        return found;
    }

    // NEW (pick a file): the files with one ending directly inside a folder, newest first (a backup is
    // looked for by when it was made). Hidden ones only when asked for.
    internal static List<string> ReadFiles(string folder, string ending, bool showHidden)
    {
        var all = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
        var found = new List<FileInfo>();

        foreach (var entry in new DirectoryInfo(folder).EnumerateFiles("*" + ending, all))
        {
            // "*.abc" also matches "x.abcd" on Windows (short names), so the ending is checked again.
            if (!entry.Name.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
                continue;
            if ((entry.Attributes & FileAttributes.Hidden) != 0 && !showHidden)
                continue;

            found.Add(entry);
        }

        return found.OrderByDescending(entry => entry.LastWriteTimeUtc).Select(entry => entry.FullName).ToList();
    }

    // One file that can be picked: a click marks it, a double click chooses it.
    private Button FileRow(string path)
    {
        var picture = new TextBlock { Text = "\uE7C3", FontSize = 14, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center }; // Page
        picture.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        picture.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        var name = new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(picture);
        line.Children.Add(name);

        var row = new Button { Content = line, ToolTip = path, Tag = path, HorizontalAlignment = HorizontalAlignment.Stretch };
        row.SetResourceReference(StyleProperty, "SidebarButton");
        row.Click += (_, _) => Pick(path, row);
        row.MouseDoubleClick += (_, _) =>
        {
            Pick(path, row);
            ChooseCurrent();
        };
        return row;
    }

    // Marks a file as the one to choose (and takes the mark off the one before).
    private void Pick(string path, Button? row)
    {
        _pickedRow?.ClearValue(BackgroundProperty);
        _pickedRow?.ClearValue(FontWeightProperty);
        _pickedFile = path;
        _pickedRow = row;
        row?.SetResourceReference(BackgroundProperty, "AccentSoft");
        if (row is not null)
            row.FontWeight = FontWeights.SemiBold; // tells the marked one apart while the pointer is still on it
        UpdateChoice();
    }

    // ------------------------------------------------------------------ a new folder

    private void ShowNewFolderRow(bool show)
    {
        if (show && _current.Length == 0)
            return;

        _newRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
            return;

        _newName.Text = "New folder";
        _newName.Focus();
        _newName.SelectAll();
    }

    private void CreateFolder()
    {
        var name = _newName.Text.Trim();
        if (name.Length == 0)
            return;

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.'))
        {
            ShowStatus("A folder name can't contain any of  \\ / : * ? \" < > |  or end with a dot.", problem: true);
            return;
        }

        var path = Path.Combine(_current, name);
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                ShowStatus($"There is already something called {name} here.", problem: true);
                return;
            }

            Directory.CreateDirectory(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            ShowStatus(error is UnauthorizedAccessException ? "You don't have permission to make a folder here." : error.Message, problem: true);
            return;
        }

        _ = OpenAsync(path); // straight into it: a folder made here is nearly always the one wanted
    }

    // ------------------------------------------------------------------ the places on the left

    // The sidebar as it is: its headings as labels, the folders under them as places. Clearspace's own
    // pages (This PC, hubs) are not folders and are left out, and so is a heading with nothing under it.
    private void AddPlaces(StackPanel places)
    {
        TextBlock? heading = null;

        foreach (var entry in _options.Places)
        {
            if (entry.IsHeader)
            {
                heading = new TextBlock
                {
                    Text = entry.Name,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(20, places.Children.Count == 0 ? 4 : 12, 12, 4),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                heading.SetResourceReference(TextBlock.ForegroundProperty, "InkFaint");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Path) || entry.Path.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase))
                continue;

            if (heading is not null)
            {
                heading.Margin = new Thickness(20, places.Children.Count == 0 ? 4 : 12, 12, 4);
                places.Children.Add(heading);
                heading = null;
            }

            places.Children.Add(Row(null, entry.Name, entry.Path, indent: true));
        }

        if (places.Children.Count == 0)
            places.Children.Add(Row(null, "Your user folder", KnownFolders.Profile, indent: true));
    }

    // ------------------------------------------------------------------ NEW (recommended folders)

    // The folders the caller recommends, one line each: the folder's name, why it is offered, and where
    // it is. A click opens it, like any other folder here. Null when there is nothing to recommend.
    private Border? RecommendedCard()
    {
        if (_options.Recommended.Count == 0)
            return null;

        var title = new TextBlock { Text = "Recommended", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(20, 4, 12, 4) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "InkFaint");

        var rows = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        rows.Children.Add(title);

        foreach (var pick in _options.Recommended)
        {
            var star = new TextBlock { Text = "\uE734", FontSize = 13, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            star.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            star.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            DockPanel.SetDock(star, Dock.Left);

            var name = new TextBlock { Text = pick.FolderName, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            DockPanel.SetDock(name, Dock.Left);

            var why = new TextBlock { Text = pick.Why, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            why.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
            DockPanel.SetDock(why, Dock.Left);

            // Where it is: the folder it is in. Last, so it is what gets cut short when the line is too long.
            var place = new TextBlock
            {
                Text = ParentOf(pick.Folder) ?? pick.Folder,
                FontSize = 11.5,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            place.SetResourceReference(TextBlock.ForegroundProperty, "InkFaint");

            var line = new DockPanel();
            line.Children.Add(star);
            line.Children.Add(name);
            line.Children.Add(why);
            line.Children.Add(place);

            var folder = pick.Folder;
            var row = new Button { Content = line, ToolTip = $"{pick.Folder}\n{pick.Why}" };
            row.SetResourceReference(StyleProperty, "SidebarButton");
            row.Click += (_, _) => _ = OpenAsync(folder);
            rows.Children.Add(row);
        }

        var card = new Border { Margin = new Thickness(0, 12, 0, 0), Child = rows };
        card.SetResourceReference(Border.BackgroundProperty, "Tint");
        card.SetResourceReference(Border.CornerRadiusProperty, "R8");
        return card;
    }

    // ------------------------------------------------------------------ small builders

    // One place or folder: a click opens it. The look is the sidebar's.
    private Button Row(string? glyph, string name, string path, bool indent)
    {
        var text = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        object content = text;

        if (glyph is not null)
        {
            var picture = new TextBlock { Text = glyph, FontSize = 14, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            picture.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            picture.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(picture);
            line.Children.Add(text);
            content = line;
        }

        var row = new Button { Content = content, ToolTip = path, HorizontalAlignment = HorizontalAlignment.Stretch };
        row.SetResourceReference(StyleProperty, "SidebarButton");
        if (indent)
            row.Padding = new Thickness(12, 0, 12, 0);
        row.Click += (_, _) => _ = OpenAsync(path);
        return row;
    }

    private static TextBlock Note(string text)
    {
        var note = Muted(text);
        note.Margin = new Thickness(20, 10, 20, 10);
        return note;
    }

    private void ShowStatus(string text, bool problem)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "Danger" : "InkMuted");
    }

    private static Border Framed(TextBox box)
    {
        var frame = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(10, 7, 10, 7), Child = box };
        frame.SetResourceReference(Border.BackgroundProperty, "Base");
        frame.SetResourceReference(Border.BorderBrushProperty, "EdgeSunken");
        frame.SetResourceReference(Border.CornerRadiusProperty, "R8");
        return frame;
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

    private static Button IconButton(string glyph, string tip)
    {
        var button = new Button { Content = glyph, Width = 32, Height = 32, ToolTip = tip };
        button.SetResourceReference(StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        return button;
    }
}
