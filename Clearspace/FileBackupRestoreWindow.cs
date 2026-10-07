// Clearspace | NEW (file backup): getting files back out of a backup, and the earlier versions of one file.
// Two dialogs, both opened from other places:
//
//   FileBackupRestoreWindow    "Restore…" on a backup's card (Backup and restore page). Asks three things:
//       Which copy              the backup as it is now, or one of its dated versions, zips, or sets of
//                               files a run replaced or removed
//       Where to                where the files came from, or another folder (Clearspace's own chooser)
//       Files already there     leave them and only bring back what is missing, or replace them
//     then copies (Services/FileBackupRestore.cs), showing how far it is, with a Stop button. Restoring
//     only adds or replaces; it never deletes anything from the folder it restores to.
//
//   FileVersionsWindow         "Earlier versions…" in a file's right-click menu, when a backup that keeps
//                              file history covers the file. Lists every different state of the file the
//                              backups hold, newest first, each with:
//       Restore                 puts that version in the file's place. The file as it is now goes to the
//                               Recycle Bin first, so this can be undone.
//       Save a copy             writes that version beside the file as "name (from <date>).ext".
//
// Built in code from the shared styles in App.xaml and the theme's colours (FileBackupUi).
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Clearspace.Models;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

internal sealed class FileBackupRestoreWindow : Window
{
    private readonly FileBackupPlan _plan;
    private readonly IReadOnlyList<SidebarEntry> _places;
    private readonly Action<string> _showFolder;
    private readonly StackPanel _points = new();
    private readonly StackPanel _targets = new();
    private readonly StackPanel _existing = new();
    private readonly TextBlock _status;
    private readonly ProgressBar _bar;
    private readonly Button _restore;
    private readonly Button _close;
    private readonly Button _show;
    private List<FileBackupPoint> _found = [];
    private FileBackupPoint? _point;
    private bool _toOriginal = true;
    private string? _otherFolder;
    private bool _replace;
    private CancellationTokenSource? _cancel;
    private string? _restoredTo;

    /// <param name="showFolder">Opens a folder in the main window ("Show the folder").</param>
    internal FileBackupRestoreWindow(FileBackupPlan plan, IReadOnlyList<SidebarEntry> places, Action<string> showFolder)
    {
        _plan = plan;
        _places = places;
        _showFolder = showFolder;

        FileBackupUi.Dialog(this, "Restore", 640);
        var header = FileBackupUi.Header("", $"Restore from \"{plan.Name}\"", // UpdateRestore
            "Nothing is deleted from the folder you restore to."); // CHANGED (simpler page): shorter

        var pointsScroll = new ScrollViewer
        {
            Content = _points,
            MaxHeight = 190,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _points.Children.Add(FileBackupUi.Muted("Looking in the backup…"));

        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        _bar = new ProgressBar { Height = 3, IsIndeterminate = true, BorderThickness = new Thickness(0), Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
        _bar.SetResourceReference(ForegroundProperty, "Accent");
        _bar.SetResourceReference(BackgroundProperty, "Line");

        _show = FileBackupUi.TextButton("Show the folder", primary: false);
        _show.Visibility = Visibility.Collapsed;
        _show.Click += (_, _) =>
        {
            if (_restoredTo is { } folder && Directory.Exists(folder))
            {
                _showFolder(folder);
                Close();
            }
        };
        _close = FileBackupUi.TextButton("Cancel", primary: false);
        _close.IsCancel = true; // Esc
        _close.Click += (_, _) => CloseOrStop();
        _restore = FileBackupUi.TextButton("Restore", primary: true);
        _restore.IsEnabled = false;
        _restore.Click += async (_, _) => await RestoreAsync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        buttons.Children.Add(_show);
        buttons.Children.Add(_close);
        buttons.Children.Add(_restore);
        DockPanel.SetDock(buttons, Dock.Right);
        var bottom = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(FileBackupUi.Section("Which copy", pointsScroll));
        stack.Children.Add(FileBackupUi.Section("Where to", _targets));
        stack.Children.Add(FileBackupUi.Section("Files that are already there", _existing));
        stack.Children.Add(_bar);
        stack.Children.Add(bottom);
        FileBackupUi.Fill(this, stack);

        ShowTargets();
        ShowExisting();
        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, e) =>
        {
            // The title bar's X while files are being written: ask the copy to stop and wait for it.
            if (_cancel is not null)
            {
                e.Cancel = true;
                _cancel.Cancel();
            }
        };
    }

    private async Task LoadAsync()
    {
        var plan = _plan;
        _found = await Task.Run(() => FileBackupRestore.Points(plan));
        _point = _found.FirstOrDefault();
        ShowPoints();

        if (_found.Count == 0)
        {
            ShowStatus(Directory.Exists(plan.Destination)
                ? "This backup has nothing in it yet. Choose Back up now first."
                : $"{plan.Destination} is not there right now. Connect the drive the backup is on, then open this again.", problem: true);
        }
    }

    private void ShowPoints()
    {
        _points.Children.Clear();
        if (_found.Count == 0)
        {
            _points.Children.Add(FileBackupUi.Muted("Nothing to restore from."));
            _restore.IsEnabled = false;
            return;
        }

        foreach (var point in _found)
        {
            var (label, detail) = Words(point);
            _points.Children.Add(FileBackupUi.Option(label, detail, ReferenceEquals(point, _point), () =>
            {
                _point = point;
                ShowPoints();
            }));
        }

        _restore.IsEnabled = _cancel is null && _point is not null;
    }

    private static (string Label, string Detail) Words(FileBackupPoint point) => point.Kind switch
    {
        FileBackupPointKind.Latest => ("The backup as it is now", "Last run " + FileBackupUi.Day(point.When)),
        FileBackupPointKind.Version => ("Version of " + FileBackupUi.Day(point.When), "Everything as it was then"),
        FileBackupPointKind.Zip => ("Zip of " + FileBackupUi.Day(point.When), SizeOf(point.Path)),
        _ => ("Earlier versions set aside " + FileBackupUi.Day(point.When), "Only the files that run replaced or removed")
    };

    private static string SizeOf(string file)
    {
        try { return FileSystemItem.FormatSize(new FileInfo(file).Length) + ", everything as it was then"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return "Everything as it was then"; }
    }

    private void ShowTargets()
    {
        var original = FileBackupRestore.OriginalFolder(_plan);
        _targets.Children.Clear();
        _targets.Children.Add(FileBackupUi.Option("Where the files came from", original, _toOriginal, () =>
        {
            _toOriginal = true;
            ShowTargets();
        }));
        _targets.Children.Add(FileBackupUi.Option("Another folder…", _otherFolder, !_toOriginal, ChooseOther));
    }

    private void ChooseOther()
    {
        var start = _otherFolder is not null && Directory.Exists(_otherFolder) ? _otherFolder : KnownFolders.Profile;
        var picker = new FolderPickerWindow(new FolderPickerOptions(
            "Restore to another folder",
            "Open the folder the files should go into, then choose Restore here.",
            "Restore here",
            start)
        {
            Places = _places,
            WhyNotChoose = folder => FileBackupPaths.IsInside(folder, _plan.Root) ? "This folder is part of the backup itself." : null
        })
        {
            Owner = this
        };

        if (picker.ShowDialog() == true && picker.Chosen is { } chosen)
        {
            _otherFolder = chosen;
            _toOriginal = false;
        }

        ShowTargets();
    }

    private void ShowExisting()
    {
        _existing.Children.Clear();
        _existing.Children.Add(FileBackupUi.Option("Leave them", "Only what is missing is brought back.", !_replace, () =>
        {
            _replace = false;
            ShowExisting();
        }));
        _existing.Children.Add(FileBackupUi.Option("Replace them with the backup's copy", "Files that differ from the backup are overwritten.", _replace, () =>
        {
            _replace = true;
            ShowExisting();
        }));
    }

    private async Task RestoreAsync()
    {
        if (_cancel is not null || _point is not { } point)
            return;

        var target = _toOriginal ? FileBackupRestore.OriginalFolder(_plan) : _otherFolder;
        if (string.IsNullOrWhiteSpace(target))
        {
            ShowStatus("Choose the folder to restore into.", problem: true);
            return;
        }

        if (_replace)
        {
            var answer = MessageDialog.Show(this,
                $"Replace files in {target} with the copies from the backup?\n\nA file that differs from the backup is overwritten, and what it holds now is lost. Files the backup does not have are left alone.",
                "Replace files?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        var cancel = _cancel = new CancellationTokenSource();
        _restore.IsEnabled = false;
        _show.Visibility = Visibility.Collapsed;
        _points.IsEnabled = _targets.IsEnabled = _existing.IsEnabled = false;
        _close.Content = "Stop";
        _bar.Visibility = Visibility.Visible;
        ShowStatus("Restoring…", problem: false);

        var progress = new Progress<(long Count, string Path)>(report =>
        {
            if (ReferenceEquals(_cancel, cancel))
                ShowStatus($"Restoring…  {report.Count:N0} {(report.Count == 1 ? "file" : "files")}   {Path.GetFileName(report.Path)}", problem: false);
        });

        try
        {
            var plan = _plan;
            var replace = _replace;
            var last = 0L;
            var result = await Task.Run(() => FileBackupRestore.Restore(plan, point, target, replace, (count, path) =>
            {
                // A line a few times a second is plenty; every file would flood the window.
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref last) < 150)
                    return;
                Interlocked.Exchange(ref last, now);
                ((IProgress<(long, string)>)progress).Report((count, path));
            }, cancel.Token));

            _cancel = null; // before the summary, so a progress line that is still on its way does not replace it
            _restoredTo = target;
            _show.Visibility = Visibility.Visible;
            ShowStatus(Summary(result), problem: result.Errors > 0);
            if (result.Problems.Count > 0)
                _status.ToolTip = string.Join("\n", result.Problems.Take(20));
        }
        catch (OperationCanceledException)
        {
            _cancel = null;
            ShowStatus("Stopped. The files restored so far stay where they are.", problem: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            _cancel = null;
            ShowStatus("The restore could not be done: " + error.Message, problem: true);
        }
        finally
        {
            _cancel = null;
            cancel.Dispose();
            _bar.Visibility = Visibility.Collapsed;
            _points.IsEnabled = _targets.IsEnabled = _existing.IsEnabled = true;
            _close.Content = "Close";
            _restore.IsEnabled = _point is not null;
        }
    }

    internal static string Summary(FileBackupRestoreResult result)
    {
        static string Files(long count) => count == 1 ? "1 file" : $"{count:N0} files";

        var parts = new List<string> { result.Restored == 0 ? "Nothing had to be restored." : $"Restored {Files(result.Restored)}." };
        if (result.Same > 0)
            parts.Add($"{Files(result.Same)} {(result.Same == 1 ? "was" : "were")} already there and the same.");
        if (result.Left > 0)
            parts.Add($"{Files(result.Left)} {(result.Left == 1 ? "is" : "are")} there but different, and {(result.Left == 1 ? "was" : "were")} left as {(result.Left == 1 ? "it is" : "they are")}.");
        if (result.Errors > 0)
            parts.Add($"{Files(result.Errors)} could not be restored (hover here for which).");
        return string.Join(" ", parts);
    }

    private void CloseOrStop()
    {
        if (_cancel is { } running)
            running.Cancel();
        else
            Close();
    }

    private void ShowStatus(string text, bool problem)
    {
        _status.Text = text;
        _status.ToolTip = null;
        _status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "Danger" : "InkMuted");
    }
}

internal sealed class FileVersionsWindow : Window
{
    private readonly string _path;
    private readonly IReadOnlyList<FileBackupPlan> _plans;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status;
    private bool _busy;

    /// <summary>Set when the file was replaced or a copy was made beside it: the list behind the dialog is out of date.</summary>
    internal bool Changed { get; private set; }

    /// <param name="path">The file whose earlier versions are wanted.</param>
    /// <param name="plans">Every backup there is; the ones that cover the file are looked through.</param>
    internal FileVersionsWindow(string path, IReadOnlyList<FileBackupPlan> plans)
    {
        _path = path;
        _plans = plans;

        FileBackupUi.Dialog(this, "Earlier versions", 620);
        var header = FileBackupUi.Header("", "Earlier versions of " + Path.GetFileName(path), // History
            "From your backups, newest first.");

        var scroll = new ScrollViewer
        {
            Content = _rows,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _rows.Children.Add(FileBackupUi.Muted("Looking in your backups…"));

        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        var close = FileBackupUi.TextButton("Close", primary: false);
        close.IsCancel = true; // Esc
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        var bottom = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        bottom.Children.Add(close);
        bottom.Children.Add(_status);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(FileBackupUi.Section(null, scroll));
        stack.Children.Add(bottom);
        FileBackupUi.Fill(this, stack);

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var path = _path;
        var plans = _plans;
        List<FileBackupVersion> versions;
        List<string> unreachable = [];
        try
        {
            (versions, unreachable) = await Task.Run(() =>
            {
                var found = FileBackupRestore.VersionsOf(path, plans);
                // Backups that cover the file but cannot be looked into right now (their drive is not connected).
                var away = plans.Where(plan => plan.RelativeOf(path) is not null && !Directory.Exists(plan.Root)).Select(plan => plan.Name).ToList();
                return (found, away);
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            versions = [];
            ShowStatus(error.Message, problem: true);
        }

        _rows.Children.Clear();
        if (versions.Count == 0)
        {
            var none = FileBackupUi.Muted(unreachable.Count > 0
                ? $"No earlier version can be reached right now. The backup{(unreachable.Count == 1 ? string.Empty : "s")} {string.Join(", ", unreachable.Select(name => "\"" + name + "\""))} " +
                  $"{(unreachable.Count == 1 ? "is" : "are")} on a drive that is not connected."
                : "No earlier version yet. Your backups hold this file as it is now, or have not run since it was made.");
            none.Margin = new Thickness(0, 4, 0, 4);
            _rows.Children.Add(none);
            return;
        }

        foreach (var version in versions)
            _rows.Children.Add(Row(version));
    }

    private DockPanel Row(FileBackupVersion version)
    {
        var restore = FileBackupUi.TextButton("Restore", primary: false);
        restore.VerticalAlignment = VerticalAlignment.Center;
        restore.ToolTip = "Puts this version in the file's place. The file as it is now goes to the Recycle Bin first.";
        restore.Click += async (_, _) => await RestoreAsync(version);
        DockPanel.SetDock(restore, Dock.Right);

        var copy = FileBackupUi.TextButton("Save a copy", primary: false);
        copy.VerticalAlignment = VerticalAlignment.Center;
        copy.ToolTip = "Writes this version beside the file under a name that says when it is from. The file itself is not changed.";
        copy.Click += async (_, _) => await CopyAsync(version);
        DockPanel.SetDock(copy, Dock.Right);

        var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        title.Inlines.Add(new System.Windows.Documents.Run(FileBackupUi.Day(version.ModifiedUtc)) { FontWeight = FontWeights.SemiBold });
        title.Inlines.Add(new System.Windows.Documents.Run("  ·  " + FileSystemItem.FormatSize(version.Size)));

        var from = FileBackupUi.Muted($"From \"{version.PlanName}\" ({FileBackupPlan.TypeName(version.Type)}), {Taken(version)}");
        from.FontSize = 11.5;
        from.Margin = new Thickness(0, 2, 0, 0);
        from.TextWrapping = TextWrapping.NoWrap;
        from.TextTrimming = TextTrimming.CharacterEllipsis;

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, ToolTip = version.ZipEntry is null ? version.StoredPath : $"{version.StoredPath}\n{version.ZipEntry}" };
        words.Children.Add(title);
        words.Children.Add(from);

        var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
        row.Children.Add(restore);
        row.Children.Add(copy);
        row.Children.Add(words);
        return row;
    }

    private static string Taken(FileBackupVersion version) => version.Kind switch
    {
        FileBackupPointKind.History => "replaced by the backup of " + FileBackupUi.Day(version.SavedLocal),
        FileBackupPointKind.Latest => "the copy in the backup now",
        _ => "backed up " + FileBackupUi.Day(version.SavedLocal)
    };

    private async Task RestoreAsync(FileBackupVersion version)
    {
        if (_busy)
            return;

        var name = Path.GetFileName(_path);
        var exists = File.Exists(_path);
        var bin = FileOperationService.HasRecycleBin(_path);
        var answer = MessageDialog.Show(this,
            $"Put the version of {FileBackupUi.Day(version.ModifiedUtc)} in place of {name}?\n\n" +
            (!exists
                ? "The file is not there any more; it is put back as it was then."
                : bin
                    ? "The file as it is now goes to the Recycle Bin first, so you can put it back from there."
                    : "This drive may have no Recycle Bin, so Windows will ask before deleting the file as it is now for good. \"Save a copy\" keeps both."),
            "Restore this version?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        _busy = true;
        _rows.IsEnabled = false;
        ShowStatus("Restoring…", problem: false);
        try
        {
            if (exists)
            {
                // The Recycle Bin first. If the file is still there afterwards (in use, or the question
                // Windows asks was answered No), nothing is replaced.
                FileOperationService.Delete([_path], new WindowInteropHelper(this).Handle);
                if (File.Exists(_path))
                {
                    ShowStatus($"{name} could not be moved to the Recycle Bin (it may be open in a program), so it was left as it is.", problem: true);
                    return;
                }
            }

            var path = _path;
            await Task.Run(() => FileBackupRestore.RestoreVersion(version, path));
            Changed = true;
            ShowStatus($"{name} is as it was {FileBackupUi.Day(version.ModifiedUtc)}." + (exists && bin ? " The version it replaced is in the Recycle Bin." : string.Empty), problem: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            ShowStatus("That version could not be restored: " + error.Message, problem: true);
        }
        finally
        {
            _busy = false;
            _rows.IsEnabled = true;
        }

        await LoadAsync(); // the list is relative to the file as it is now
    }

    private async Task CopyAsync(FileBackupVersion version)
    {
        if (_busy)
            return;

        _busy = true;
        _rows.IsEnabled = false;
        try
        {
            var target = FileBackupRestore.CopyName(_path, DateTime.SpecifyKind(version.ModifiedUtc, DateTimeKind.Utc).ToLocalTime());
            await Task.Run(() => FileBackupRestore.RestoreVersion(version, target));
            Changed = true;
            ShowStatus("Saved beside the file as " + Path.GetFileName(target) + ".", problem: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            ShowStatus("The copy could not be made: " + error.Message, problem: true);
        }
        finally
        {
            _busy = false;
            _rows.IsEnabled = true;
        }
    }

    private void ShowStatus(string text, bool problem)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "Danger" : "InkMuted");
    }
}
