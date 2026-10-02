// Clearspace | NEW (updates): the main window's side of updating.
//  - Settings > "Check for updates…" checks now and says what it found.
//  - Settings > "About Clearspace…" (AboutWindow.cs) shows the version and details, has the same check as a
//    button, and holds the "Check for updates automatically" switch. CHANGED (about): that switch used to
//    be a Settings menu item.
//  - The automatic check (on by default): a quiet check a few seconds after the window opens and every 6
//    hours after that. It never interrupts: a newer version only puts an "Update available" chip in the
//    status bar, next to the index chip. Clicking the chip opens the update dialog.
// The checking, downloading and installing live in Services/UpdateService.cs; the dialog is UpdateWindow.cs.
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Clearspace.Services;

namespace Clearspace;

public partial class MainWindow
{
    private DispatcherTimer? _updateTimer;
    private bool _checkingForUpdates;

    private void InitializeUpdates()
    {
        UpdateService.Changed += OnUpdateFound;
        Loaded += (_, _) =>
        {
            ShowUpdateChip(); // another window may already have found one
            // The first check waits a few seconds so it never competes with opening the first folder.
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _updateTimer.Tick += async (_, _) =>
            {
                _updateTimer!.Interval = UpdateService.AutomaticInterval;
                if (!SettingsService.GetCheckForUpdates() || _checkingForUpdates)
                    return;

                // Quiet on purpose: being offline is not news.
                try { await UpdateService.CheckAsync(); }
                catch (Exception) { }
            };
            _updateTimer.Start();
        };
        Closed += (_, _) =>
        {
            UpdateService.Changed -= OnUpdateFound;
            _updateTimer?.Stop();
        };
    }

    // UpdateService raises this on whatever thread its check finished on.
    private void OnUpdateFound() => Dispatcher.BeginInvoke(new Action(ShowUpdateChip));

    private void ShowUpdateChip()
    {
        var update = UpdateService.Notify;
        UpdateChip.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
        if (update is not null)
            UpdateChipText.Text = $"Update available · {update.VersionText}";
    }

    private void OnUpdateChip(object sender, RoutedEventArgs e)
    {
        if (UpdateService.Notify is { } update)
            ShowUpdate(update);
    }

    private void ShowUpdate(UpdateInfo update)
    {
        new UpdateWindow(update) { Owner = this }.ShowDialog();
        ShowUpdateChip(); // "Skip this version" hides it
    }

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        if (_checkingForUpdates)
            return;

        _checkingForUpdates = true;
        try
        {
            UpdateInfo? update;
            try
            {
                update = await UpdateService.CheckAsync();
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or IOException)
            {
                MessageDialog.Show(this,
                    "Clearspace couldn't reach GitHub to look for a newer version. Check your internet connection and try again.",
                    "Check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (update is null)
                MessageDialog.Show(this, $"Clearspace {UpdateService.CurrentText} is the newest version.",
                    "You're up to date", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                ShowUpdate(update); // also shown when this version was skipped earlier: you asked
        }
        finally
        {
            _checkingForUpdates = false;
        }
    }

    // NEW (about)
    private void OnAbout(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
        ShowUpdateChip(); // a check or "Skip this version" in there may have changed it
    }
}
