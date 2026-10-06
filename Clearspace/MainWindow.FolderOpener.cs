// Clearspace | NEW (folder opener): Settings > "Open folders with Clearspace".
// Ticked: a folder opened from the desktop, from another program, or with Win+E opens in Clearspace
// instead of File Explorer. Unticked: Windows goes back to what it did before. The first time it is
// turned on a short explanation asks for confirmation, because it changes how Windows behaves outside
// Clearspace. The registry side is ShellIntegration.SetFolderOpener; uninstalling switches it off.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Clearspace.Services;

namespace Clearspace;

public partial class MainWindow
{
    // The Settings menu is opening: the tick shows what Windows is set to right now.
    private void TickFolderOpener()
    {
        try { FolderOpenerMenuItem.IsChecked = ShellIntegration.IsFolderOpener(); }
        catch (Exception) { FolderOpenerMenuItem.IsChecked = false; }
    }

    private void OnToggleFolderOpener(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item)
            return;

        var turnOn = item.IsChecked; // the click has already flipped the tick
        var exe = Environment.ProcessPath;

        // Only the installed Clearspace.exe may take this over: a development build run from the build
        // folder would leave Windows pointing at a file that the next build moves or deletes.
        if (turnOn && (exe is null || !Path.GetFileName(exe).Equals("Clearspace.exe", StringComparison.OrdinalIgnoreCase)))
        {
            item.IsChecked = false;
            MessageDialog.Show(this, "This can only be turned on from Clearspace.exe itself.", "Open folders with Clearspace",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (turnOn)
        {
            var answer = MessageDialog.Show(this,
                "Folders you open from the desktop, from other programs and with Win+E will open in Clearspace instead of File Explorer.\n\n" +
                "File Explorer stays on this PC and still opens when a program asks for it by name. You can switch back here at any time, and uninstalling Clearspace switches back too.",
                "Open folders with Clearspace?", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
            {
                item.IsChecked = false;
                return;
            }
        }

        try
        {
            ShellIntegration.SetFolderOpener(turnOn, exe ?? string.Empty);
            _viewModel.ReportStatus(turnOn
                ? "Folders now open in Clearspace."
                : "Folders open in File Explorer again.");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            item.IsChecked = !turnOn;
            MessageDialog.Show(this, "Windows did not allow the change.\n\n" + error.Message, "Open folders with Clearspace",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
