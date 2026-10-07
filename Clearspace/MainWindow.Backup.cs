// Clearspace | NEW (backup): the main window's side of backing up.
//  - Settings > "Back up settings and tags…" opens BackupWindow.
//  - Automatic backups: a quiet check a little after the window opens and every hour after that. When one
//    is due (Services/BackupService.cs decides) it is made in the background; nothing is shown. With several
//    windows open each has this timer, and BackupService lets only one backup run at a time.
using System.Windows;
using System.Windows.Threading;
using Clearspace.Services;

namespace Clearspace;

public partial class MainWindow
{
    private DispatcherTimer? _backupTimer;

    private void InitializeBackup()
    {
        Loaded += (_, _) =>
        {
            // The first check waits, so it never competes with opening the first folder or the update check.
            _backupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
            _backupTimer.Tick += async (_, _) =>
            {
                _backupTimer!.Interval = TimeSpan.FromHours(1);
                try { await BackupService.RunAutomaticIfDueAsync(); }
                catch (Exception) { } // said in the Backup window (BackupService.LastAutomaticError), never in the way
            };
            _backupTimer.Start();
        };
        Closed += (_, _) => _backupTimer?.Stop();
    }

    private void OnBackup(object sender, RoutedEventArgs e)
        => new BackupWindow([.. _viewModel.Sidebar], folder => _viewModel.Navigation.Navigate(folder)) { Owner = this }.ShowDialog();
}
