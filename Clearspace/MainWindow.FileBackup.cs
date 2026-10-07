// Clearspace | NEW (file backup): the main window's side of backing up files, folders and drives.
//  - Settings > "Backup and restore…" opens the page (FileBackupView.cs) in place of the file list, the
//    same way Disk usage and Find duplicates do. Esc, the mouse's back button or the page's own Back
//    button return to the files.
//  - "Back up…" in the right-click menu of one file, folder or drive (and "Back up this folder…" in the
//    menu of the empty part of the list) opens the page with a new backup of it started.
//  - "Earlier versions…" in the right-click menu of one file opens FileVersionsWindow. It is only there
//    when a backup that keeps file history covers the file (FileBackupRestore.Offers), which is asked
//    from the plans in memory, not from the disk.
//  - Scheduled backups: a check 40 seconds after the window opens and every minute after that. When a
//    backup is due, a second Clearspace without a window is started to run it (FileBackupRunner.cs); the
//    window itself never copies anything. With several windows open only one check runs at a time, and
//    only one process ever runs what is due. The first check also brings the entry in Windows' Task
//    Scheduler in step with the settings (FileBackupTask.Sync), in case Clearspace has moved.
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Clearspace.Services;

namespace Clearspace;

public partial class MainWindow
{
    private FileBackupView? _fileBackupView;
    private DispatcherTimer? _fileBackupTimer;
    private static int s_fileBackupCheck; // 1 while a check is running, whichever window started it

    private void InitializeFileBackup()
    {
        Loaded += (_, _) =>
        {
            // The first check waits, so it never competes with opening the first folder.
            var first = true;
            _fileBackupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
            _fileBackupTimer.Tick += (_, _) =>
            {
                _fileBackupTimer!.Interval = TimeSpan.FromMinutes(1);
                CheckFileBackups(alsoTaskScheduler: first);
                first = false;
            };
            _fileBackupTimer.Start();
        };
        Closed += (_, _) =>
        {
            _fileBackupTimer?.Stop();
            _fileBackupView?.Dispose();
        };
    }

    // Looks, off the UI thread, whether a scheduled backup is due and starts the process that runs it.
    private static void CheckFileBackups(bool alsoTaskScheduler)
    {
        if (Interlocked.Exchange(ref s_fileBackupCheck, 1) == 1)
            return;

        Task.Run(() =>
        {
            try
            {
                if (alsoTaskScheduler)
                    FileBackupTask.Sync(FileBackupStore.Default.Load());
                FileBackupRunner.StartDueIfAny();
            }
            catch (Exception)
            {
                // the next check tries again; the page says how each backup stands
            }
            finally
            {
                Interlocked.Exchange(ref s_fileBackupCheck, 0);
            }
        });
    }

    private void OnFileBackup(object sender, RoutedEventArgs e) => OpenFileBackup(null);

    // "Back up…" on the one selected file, folder or drive.
    private void OnBackUpSelected(object sender, RoutedEventArgs e)
    {
        if (OnlySelected() is { } item && Path.IsPathFullyQualified(item.FullPath))
            OpenFileBackup(item.FullPath);
    }

    // "Back up this folder…" in the menu of the empty part of the list.
    private void OnBackUpThisFolder()
    {
        var folder = _viewModel.CurrentPath;
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
        {
            _viewModel.ReportStatus("Open a folder first, then back it up.");
            return;
        }

        OpenFileBackup(folder);
    }

    /// <param name="source">What a new backup is to be started for; null to just show the page.</param>
    private void OpenFileBackup(string? source)
    {
        if (_diskUsageView is not null || _indexingView is not null || _duplicatesView is not null)
            return;

        if (_fileBackupView is null)
        {
            _fileBackupView = new FileBackupView([.. _viewModel.Sidebar]);
            _fileBackupView.CloseRequested += (_, _) => CloseFileBackup();
            _fileBackupView.ShowFolderRequested += (_, folder) =>
            {
                CloseFileBackup();
                _viewModel.Navigation.Navigate(folder);
            };

            FileBackupHost.Content = _fileBackupView;
            ExplorerShell.Visibility = Visibility.Collapsed;
            FileBackupHost.Visibility = Visibility.Visible;
            FlashPage(); // the e-reader theme refreshes its "page"
        }

        if (source is null)
            return;

        // Once the page is on screen (and the menu this came from has closed), the editor opens over it.
        var page = _fileBackupView;
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(page, _fileBackupView))
                page.NewBackupFor(source);
        }, DispatcherPriority.Background);
    }

    private void CloseFileBackup()
    {
        if (_fileBackupView is null)
            return;

        _fileBackupView.Dispose(); // stops the page looking at how the backups are doing; the backups carry on
        _fileBackupView = null;
        FileBackupHost.Content = null;
        FileBackupHost.Visibility = Visibility.Collapsed;
        ExplorerShell.Visibility = Visibility.Visible;
        FileList.Focus();
        FlashPage();
    }

    // The files-and-folders menu is opening (called from OnItemMenuOpened): "Back up…" for one thing that
    // is really on a disk, "Earlier versions…" for one file a backup with file history covers.
    private void UpdateFileBackupMenu()
    {
        var only = OnlySelected();
        var onDisk = only is not null && Path.IsPathFullyQualified(only.FullPath);

        BackUpMenuItem.Visibility = onDisk ? Visibility.Visible : Visibility.Collapsed;
        EarlierVersionsMenuItem.Visibility = onDisk && !only!.IsFolder && FileBackupRestore.Offers(only.FullPath, FileBackupStore.Default.Load().Plans)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnEarlierVersions(object sender, RoutedEventArgs e)
    {
        if (OnlySelected() is not { IsFolder: false } item || !Path.IsPathFullyQualified(item.FullPath))
            return;

        var versions = new FileVersionsWindow(item.FullPath, FileBackupStore.Default.Load().Plans) { Owner = this };
        versions.ShowDialog();
        if (versions.Changed)
            _ = _viewModel.RefreshAsync(); // the file was put back, or a copy now sits beside it
    }
}
