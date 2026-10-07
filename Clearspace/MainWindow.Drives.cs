// Clearspace | NEW (live drives): the main window's side of noticing drives being plugged in and taken out.
// Windows tells every window when hardware changes; the window passes that to Services/DriveWatch.cs,
// which looks at the drives and raises DriveWatch.Changed when one has come, gone or been renamed. Then:
//  - every window's sidebar lists the drives again, and a This PC page on screen reloads;
//  - a tab that was on a drive that is gone steps out to This PC (the folder it showed no longer exists);
//  - the status line says what was connected or removed;
//  - a file backup that was waiting for that drive is looked at straight away instead of at the next
//    once-a-minute check (MainWindow.FileBackup.cs).
// With several windows open each gets the change; what concerns all of them is done once.
using System.Windows;
using System.Windows.Interop;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

public partial class MainWindow
{
    private static long s_driveChangeHandled; // the last change whose once-for-all part has been done

    private void InitializeDriveWatch()
    {
        SourceInitialized += (_, _) =>
        {
            (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(OnWindowMessage);
            DriveWatch.Start();
        };
        DriveWatch.Changed += OnDrivesPluggedOrUnplugged;
        Closed += (_, _) => DriveWatch.Changed -= OnDrivesPluggedOrUnplugged;
    }

    // Every message Windows sends this window passes here; only "hardware changed" is of interest, and
    // it is only passed on (nothing slow happens in here).
    private static IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == DriveWatch.WM_DEVICECHANGE)
            DriveWatch.DeviceChanged();
        return IntPtr.Zero;
    }

    private void OnDrivesPluggedOrUnplugged(DriveChange change)
    {
        // Once, whichever window gets here first: the sidebars (each window's listens for this), and
        // backups that were waiting for a drive.
        if (Interlocked.Exchange(ref s_driveChangeHandled, change.Id) != change.Id)
        {
            SidebarViewModel.ReloadDrivesEverywhere();
            if (change.Arrived.Count > 0)
                CheckFileBackups(alsoTaskScheduler: false);
        }

        // This window: leave a drive that is gone, show the drives page again, say what happened.
        foreach (var gone in change.Removed)
        {
            // Asked once more before a tab is moved: a drive that only hesitated is not left.
            var root = gone.Root;
            if (System.IO.Directory.Exists(root))
                continue;
            Tabs.MoveOut(location => location.StartsWith(root, StringComparison.OrdinalIgnoreCase), MainViewModel.MyPcPath);
        }

        if (_viewModel.IsDrivesPage)
            _ = _viewModel.RefreshAsync();

        if (change.Arrived.Count > 0)
            _viewModel.ReportStatus(string.Join(", ", change.Arrived.Select(drive => drive.Title)) + (change.Arrived.Count == 1 ? " was connected." : " were connected."));
        else if (change.Removed.Count > 0)
            _viewModel.ReportStatus(string.Join(", ", change.Removed.Select(drive => drive.Title)) + (change.Removed.Count == 1 ? " was removed." : " were removed."));
    }
}
