// Clearspace | NEW (about): Settings > "About Clearspace…".
// Shows what this copy is (version, where it is installed, where its data lives, the Windows and .NET it
// runs on, the theme) and holds everything about updating in one place: the "Check for updates" button with
// its answer underneath, and the "Check for updates automatically" switch. Built in code from the shared
// styles in App.xaml and the theme's colours, like MessageDialog, so it follows every theme.
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clearspace.Services;

namespace Clearspace;

internal sealed class AboutWindow : Window
{
    private readonly List<(string Label, string Value)> _details;
    private readonly TextBlock _updateStatus;
    private readonly Button _check;
    private UpdateInfo? _found; // a newer version: the button then opens the update dialog
    private bool _checking;

    internal AboutWindow()
    {
        Title = "About Clearspace";
        // FIXED (about): the window had a fixed width of 500, which cut off the "Copy details" button in
        // themes with a wide font (1-bit Mac, Pixel). It now grows to fit its widest row, between 500 and
        // 760; past that the paths wrap and the link buttons move onto a second line instead of being cut.
        MinWidth = 500;
        MaxWidth = 760;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Base");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(FontFamilyProperty, "UIFont");

        var stack = new StackPanel();

        // Header: the app icon, the name, the version.
        var name = new TextBlock { Text = "Clearspace", FontSize = 22, FontWeight = FontWeights.SemiBold };
        var version = new TextBlock { Text = "Version " + UpdateService.CurrentText, Margin = new Thickness(0, 2, 0, 0) };
        version.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(name);
        titles.Children.Add(version);

        var header = new DockPanel();
        if (Logo() is { } logo)
        {
            var picture = new Image { Source = logo, Width = 56, Height = 56, Margin = new Thickness(0, 0, 16, 0) };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            DockPanel.SetDock(picture, Dock.Left);
            header.Children.Add(picture);
        }
        header.Children.Add(titles);
        stack.Children.Add(header);

        // Details: one row each, label on the left.
        _details = Details();
        var table = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (label, value) in _details)
        {
            var row = table.RowDefinitions.Count;
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var left = new TextBlock { Text = label, Margin = new Thickness(0, 3, 18, 3) };
            left.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
            Grid.SetRow(left, row);
            table.Children.Add(left);

            var right = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3), ToolTip = value };
            Grid.SetRow(right, row);
            Grid.SetColumn(right, 1);
            table.Children.Add(right);
        }
        stack.Children.Add(table);

        // Updates: what the last check found, the button, and the automatic check.
        _updateStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        _check = TextButton("Check for updates", primary: true);
        _check.Click += async (_, _) => await CheckOrUpdateAsync();
        DockPanel.SetDock(_check, Dock.Right);
        var updateRow = new DockPanel();
        updateRow.Children.Add(_check);
        updateRow.Children.Add(_updateStatus);

        var automatic = new CheckBox
        {
            Content = "Check for updates automatically",
            IsChecked = SettingsService.GetCheckForUpdates(),
            Margin = new Thickness(0, 12, 0, 0),
            ToolTip = "Looks quietly a few seconds after Clearspace starts and every 6 hours. A newer version shows as a chip in the status bar."
        };
        automatic.SetResourceReference(StyleProperty, "ViewerCheck");
        automatic.Checked += (_, _) => SettingsService.SetCheckForUpdates(true);
        automatic.Unchecked += (_, _) => SettingsService.SetCheckForUpdates(false);

        var updates = new StackPanel();
        updates.Children.Add(updateRow);
        updates.Children.Add(automatic);
        var updatesCard = new Border { Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(14, 12, 14, 12), Child = updates };
        updatesCard.SetResourceReference(Border.BackgroundProperty, "Tint");
        updatesCard.SetResourceReference(Border.CornerRadiusProperty, "R8");
        stack.Children.Add(updatesCard);
        ShowUpdateState(UpdateService.Latest, justChecked: false); // the quiet check may already have found one

        // Bottom row: links on the left, Close on the right.
        var website = TextButton("GitHub page", primary: false);
        website.Click += (_, _) => Open(UpdateService.ProjectPage);
        var releases = TextButton("Release notes", primary: false);
        releases.Click += (_, _) => Open(UpdateService.ReleasesPage);
        var copy = TextButton("Copy details", primary: false);
        copy.ToolTip = "Copies the version and system details, for a bug report";
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(DetailsText());
                copy.Content = "Copied";
            }
            catch (Exception) { } // the clipboard is busy now and then; nothing to report
        };
        var close = TextButton("Close", primary: false);
        close.IsCancel = true; // Esc
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);

        var links = new WrapPanel { Orientation = Orientation.Horizontal }; // FIXED (about): wraps, never clips
        links.Children.Add(website);
        links.Children.Add(releases);
        links.Children.Add(copy);
        var bottom = new DockPanel { Margin = new Thickness(0, 22, 0, 0) };
        bottom.Children.Add(close);
        bottom.Children.Add(links);
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
        Loaded += (_, _) => _check.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;

            e.Handled = true;
            Close();
        };
    }

    private static Button TextButton(string label, bool primary)
    {
        var button = new Button { Content = label, MinWidth = 76 };
        button.SetResourceReference(StyleProperty, primary ? "ViewerTextButtonPrimary" : "ViewerTextButton");
        return button;
    }

    // The largest picture in the app icon.
    private static ImageSource? Logo()
    {
        try
        {
            var decoder = new IconBitmapDecoder(new Uri("pack://application:,,,/Assets/Clearspace.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderByDescending(frame => frame.PixelWidth).FirstOrDefault();
        }
        catch (Exception)
        {
            return null; // the window is fine without the picture
        }
    }

    private static List<(string Label, string Value)> Details()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Environment.OSVersion.Version;
        var theme = ThemeService.Themes.FirstOrDefault(known => known.Name == ThemeService.Current)?.Label ?? ThemeService.Current;

        return
        [
            ("Version", UpdateService.CurrentText),
            ("Installed in", Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)),
            ("Settings and tags", Path.Combine(roaming, "Clearspace")),
            ("Index and logs", Path.Combine(local, "Clearspace")),
            // Windows 11 still calls itself 10.0; the build number tells them apart.
            ("Windows", $"{(windows.Build >= 22000 ? "Windows 11" : "Windows 10")} (build {windows.Build})"),
            (".NET", $"{Environment.Version} (built in)"),
            ("Theme", theme),
        ];
    }

    private string DetailsText()
    {
        var text = new StringBuilder("Clearspace").AppendLine();
        foreach (var (label, value) in _details)
            text.Append(label).Append(": ").AppendLine(value);
        return text.ToString();
    }

    private static void Open(string page)
    {
        try { Process.Start(new ProcessStartInfo { FileName = page, UseShellExecute = true })?.Dispose(); }
        catch (Exception) { }
    }

    // ---- Updates ----

    private void ShowUpdateState(UpdateInfo? update, bool justChecked)
    {
        _found = update;
        if (update is not null)
        {
            _updateStatus.Text = $"Clearspace {update.VersionText} is available.";
            _updateStatus.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            _check.Content = "Update…";
        }
        else
        {
            _updateStatus.Text = justChecked
                ? $"You're up to date. {UpdateService.CurrentText} is the newest version."
                : "Updates come from Clearspace's releases on GitHub.";
            _updateStatus.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
            _check.Content = "Check for updates";
        }
    }

    // The one button: looks for an update, or, once one has been found, opens the update dialog.
    private async Task CheckOrUpdateAsync()
    {
        if (_checking)
            return;

        if (_found is { } ready)
        {
            new UpdateWindow(ready) { Owner = this }.ShowDialog();
            return;
        }

        _checking = true;
        _check.IsEnabled = false;
        _updateStatus.Text = "Checking…";
        _updateStatus.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        try
        {
            ShowUpdateState(await UpdateService.CheckAsync(), justChecked: true);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            _updateStatus.Text = "Couldn't reach GitHub. Check your internet connection and try again.";
            _updateStatus.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        }
        finally
        {
            _checking = false;
            _check.IsEnabled = true;
        }
    }
}
