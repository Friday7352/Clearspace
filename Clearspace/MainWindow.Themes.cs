// Clearspace | NEW (theme files): the main window's side of themes.
// Themes are files in the Themes folder beside Clearspace.exe (Services/ThemePackage.cs).
// CHANGED (themes page): the Settings menu no longer lists them. It used to be rebuilt each time it opened:
// six themes in the menu itself, an "Experimental themes" submenu, and a "Your themes" submenu that also
// held "Import a theme…" and "Open themes folder". Now the menu has one entry, "Choose a theme…", with the
// name of the theme in use beside it, and that opens the Themes page (ThemesView.cs) in place of the file
// list, the same way Backup and restore and Find duplicates open. The page shows a small preview of every
// theme and has Import and Open themes folder on it. Esc, the mouse's back button or the page's own Back
// button return to the files.
// This file opens and closes the page and does the things the page asks the window for:
//   - NEW (edit in place): Create a theme and Edit theme close the page and put the window into edit mode
//     (MainWindow.ThemeEdit.cs).
//   - Import asks for a theme file in Clearspace's own chooser, checks it, copies it into the Themes folder
//     and switches to it. A theme file put into the folder by hand shows up the same way, without importing.
//   - Open themes folder (and "Show its file" on a theme) goes there in Clearspace itself.
using System.IO;
using System.Windows;
using Clearspace.Services;

namespace Clearspace;

public partial class MainWindow
{
    private static string? s_lastThemeFolder;   // where the last theme was imported from, for the next import

    private ThemesView? _themesView;            // NEW (themes page): the page, while it is open

    // REMOVED (themes page): ShowThemesInMenu and ThemeMenuItem, which filled the Settings menu with one
    // entry per theme, and PickTheme, which switched to the one that was picked. The page does both.

    // NEW (themes page): the Settings menu is opening: say which theme is in use beside "Choose a theme…".
    private void ShowThemeInMenu() => ThemesMenuItem.InputGestureText = ThemeService.CurrentLabel;

    // NEW (themes page): Settings > "Choose a theme…".
    private void OnOpenThemes(object sender, RoutedEventArgs e) => OpenThemes();

    private void OpenThemes()
    {
        if (_themeEdit is not null || // NEW (edit in place)
            _themesView is not null || _diskUsageView is not null || _indexingView is not null ||
            _duplicatesView is not null || _fileBackupView is not null)
            return;

        _themesView = new ThemesView();
        _themesView.CloseRequested += (_, _) => CloseThemes();
        _themesView.ImportRequested += (_, _) => ImportTheme();
        _themesView.FolderRequested += (_, _) => OpenThemesFolder();
        _themesView.PasteCodeRequested += (_, _) => PasteThemeCode();   // NEW (theme codes)
        // NEW (edit in place): "Create a theme" and "Edit theme…" close the page and put the window into edit mode.
        _themesView.EditRequested += (_, theme) =>
        {
            CloseThemes();
            StartThemeEdit(theme); // MainWindow.ThemeEdit.cs
        };
        _themesView.ShowFileRequested += (_, file) =>
        {
            CloseThemes();
            RevealFile(file); // goes to the Themes folder and selects the theme's file (MainWindow.Duplicates.cs)
        };

        ThemesHost.Content = _themesView;
        ExplorerShell.Visibility = Visibility.Collapsed;
        ThemesHost.Visibility = Visibility.Visible;
        FlashPage(); // the e-reader theme refreshes its "page"
    }

    private void CloseThemes()
    {
        if (_themesView is null || !_themesView.TryClose())
            return;

        _themesView.Dispose(); // stops it listening for theme changes
        _themesView = null;
        ThemesHost.Content = null;
        ThemesHost.Visibility = Visibility.Collapsed;
        ExplorerShell.Visibility = Visibility.Visible;
        FileList.Focus();
        FlashPage();
    }

    // CHANGED (themes page): started from the page's Import button (was "Import a theme…" in the Settings
    // menu), and what happened is said on the page.
    private void ImportTheme()
    {
        var start = s_lastThemeFolder is { } last && Directory.Exists(last) ? last : KnownFolders.Downloads;
        var picker = new FolderPickerWindow(new FolderPickerOptions(
            "Import a theme",
            "Open the folder the theme file is in, click it, then choose Import.",
            "Import",
            start)
        {
            Places = [.. _viewModel.Sidebar],
            ShowHidden = _viewModel.ShowHiddenItems,
            FileExtension = ThemeLibrary.Extension,
            MoreExtensions = [ThemeDeckFile.Extension],   // NEW (deck files): a deck file is imported as a theme too
            FileWord = "theme or deck file"
        })
        {
            Owner = this
        };

        if (picker.ShowDialog() != true || picker.Chosen is not { } file)
            return;
        s_lastThemeFolder = Path.GetDirectoryName(file);

        // NEW (deck files): a deck file's cards make a theme of your own, pictures and fonts included.
        if (file.EndsWith(ThemeDeckFile.Extension, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var deck = ThemeDeckFile.Read(file);
                var id = deck.Theme().Save(ThemeService.Folder, deck.Name);
                ThemeService.Refresh();
                var shown = ThemeService.Apply(id);
                var said = shown ? $"{deck.Name} was added to your themes." : $"{deck.Name} was added, but could not be put on screen: {ThemeService.LastProblem}";
                if (_themesView is { } view) view.Report(said, !shown); else _viewModel.ReportStatus(said);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ThemeFormatException or ArgumentException)
            {
                MessageDialog.Show(this, error.Message, "That deck file cannot be used", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        var theme = ThemeService.Import(file, existing => MessageDialog.Show(this,
            $"{existing.Label} is already in your themes.\n\nReplace it? Choose No to keep both.",
            "Replace the theme?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);
        if (theme is null)
        {
            MessageDialog.Show(this, ThemeService.LastProblem ?? "The file could not be read.", "That file cannot be used as a theme",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // It was read and built once before it was copied, so switching to it works; if its file has gone
        // again in the meantime, say so.
        var added = $"{theme.Label} was added to your themes.";
        var problem = ThemeService.Apply(theme.Name) ? null : ThemeService.LastProblem ?? "The theme could not be read.";
        if (_themesView is { } page)
            page.Report(problem is null ? added : $"{added} {problem}", problem is not null);
        else
            _viewModel.ReportStatus(problem is null ? added : $"{added} {problem}");
    }

    // The folder opens in Clearspace itself, like any other folder.
    // CHANGED (themes page): started from the page (was "Open themes folder" in the Settings menu); the
    // page closes first.
    private void OpenThemesFolder()
    {
        var folder = ThemeService.Folder;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Show(this, $"{folder}\n\n{error.Message}", "The themes folder could not be opened",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CloseThemes();
        _viewModel.Navigation.Navigate(folder);
    }

    // NEW (theme codes): "Paste code" on the Themes page. A deck code becomes a theme of your own in the Themes
    // folder and is put on screen; a card code is kept in My cards, to play while making a theme.
    private void PasteThemeCode()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (System.Runtime.InteropServices.ExternalException) { _themesView?.Report("The clipboard is busy. Try again.", true); return; }
        if (!ThemeCodes.LooksLikeCode(text)) { _themesView?.Report("Copy a Clearspace code first (it starts with CS-), then choose Paste code.", true); return; }
        if (!ThemeCodes.TryRead(text, out var card, out var theme, out var name, out var problem)) { _themesView?.Report(problem, true); return; }
        try
        {
            if (theme is not null)
            {
                var id = theme.Save(ThemeService.Folder, name);
                ThemeService.Refresh();
                var shown = ThemeService.Apply(id);
                _themesView?.Report(shown ? $"{name} was added to your themes." : $"{name} was added, but could not be put on screen: {ThemeService.LastProblem}", !shown);
            }
            else if (card is not null)
            {
                ThemeCards.Keep(card);
                _themesView?.Report($"{name} is kept in My cards. Play it while making a theme (Cards › Borrow cards › My cards).");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or ThemeFormatException)
        { _themesView?.Report("The code could not be saved: " + error.Message, true); }
    }

    // The theme on screen was saved again (someone is making it) and can no longer be read: the look stays
    // as it was, and the reason goes in the status bar rather than a dialog over their editor.
    private void OnThemeReloadFailed(string message) => _viewModel.ReportStatus(message);
}
