// Clearspace | NEW (updates): the "update available" dialog. Shows the new version and what changed in it,
// then downloads and starts the installer with a progress bar. Built in code from the shared styles in
// App.xaml and the theme's colours, like MessageDialog, so it follows every theme.
//   Update now         download, check the file, start the installer, close Clearspace (it comes back updated)
//   Later              close; the "Update available" chip stays in the status bar
//   Skip this version  close and stop mentioning this version (a newer one is announced again)
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clearspace.Services;

namespace Clearspace;

internal sealed class UpdateWindow : Window
{
    private readonly UpdateInfo _update;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Button _skip;
    private readonly Button _later;
    private readonly Button _install;
    private bool _working;
    private bool _starting; // the installer is starting: Clearspace is about to close

    internal UpdateWindow(UpdateInfo update)
    {
        _update = update;
        Title = "Clearspace update";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Base");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(FontFamilyProperty, "UIFont");

        // Header: round icon badge + title (the same pattern as the lock dialog and MessageDialog).
        var glyph = new TextBlock
        {
            Text = "\uE896", // Download
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

        var heading = new TextBlock
        {
            Text = $"Clearspace {update.VersionText} is available",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        var current = new TextBlock
        {
            Text = $"You have {UpdateService.CurrentText}. Updating keeps your settings, tags and locked files.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        current.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(heading);
        titles.Children.Add(current);

        var header = new DockPanel();
        header.Children.Add(badge);
        header.Children.Add(titles);

        var stack = new StackPanel();
        stack.Children.Add(header);

        // What's new: the release notes as written on GitHub, lightly tidied (see Tidy).
        var notes = Tidy(update.Notes);
        if (notes.Length > 0)
        {
            var notesText = new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
            notesText.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
            var scroll = new ScrollViewer
            {
                Content = notesText,
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(12, 10, 12, 10)
            };
            var notesCard = new Border { Margin = new Thickness(0, 18, 0, 0), Child = scroll };
            notesCard.SetResourceReference(Border.BackgroundProperty, "Tint");
            notesCard.SetResourceReference(Border.CornerRadiusProperty, "R8");
            stack.Children.Add(notesCard);
        }

        _progress = new ProgressBar
        {
            Height = 3,
            Minimum = 0,
            Maximum = 1,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 18, 0, 0),
            Visibility = Visibility.Collapsed
        };
        _progress.SetResourceReference(ForegroundProperty, "Accent");
        _progress.SetResourceReference(BackgroundProperty, "Line");
        stack.Children.Add(_progress);

        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        stack.Children.Add(_status);

        // Buttons, right-aligned, the confirming one last and filled.
        _skip = TextButton("Skip this version", primary: false);
        _skip.Click += (_, _) => { UpdateService.Skip(_update); Close(); };
        _later = TextButton("Later", primary: false);
        _later.Click += (_, _) => Close();
        _install = TextButton("Update now", primary: true);
        _install.IsDefault = true;
        _install.Click += async (_, _) => await InstallAsync();

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0)
        };
        row.Children.Add(_skip);
        row.Children.Add(_later);
        row.Children.Add(_install);
        stack.Children.Add(row);

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
        Loaded += (_, _) => _install.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;

            e.Handled = true;
            Close();
        };
        Closing += (_, e) =>
        {
            // Closing while downloading stops the download; once the installer is starting there is nothing to stop.
            if (_starting) e.Cancel = true;
            else if (_working) _cancel.Cancel();
        };
        Closed += (_, _) => _cancel.Dispose();
    }

    private static Button TextButton(string label, bool primary)
    {
        var button = new Button { Content = label, MinWidth = 76 };
        button.SetResourceReference(StyleProperty, primary ? "ViewerTextButtonPrimary" : "ViewerTextButton");
        return button;
    }

    private async Task InstallAsync()
    {
        if (_working) return;
        _working = true;
        _skip.Visibility = Visibility.Collapsed;
        _later.Content = "Cancel";
        _install.IsEnabled = false;
        _progress.Value = 0;
        _progress.IsIndeterminate = _update.Size <= 0;
        _progress.Visibility = Visibility.Visible;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        _status.Text = "Downloading…";
        _status.Visibility = Visibility.Visible;

        try
        {
            var megabytes = _update.Size / 1048576.0;
            var progress = new Progress<double>(fraction =>
            {
                _progress.Value = fraction;
                if (megabytes > 0) _status.Text = $"Downloading… {fraction * megabytes:0} of {megabytes:0} MB";
            });
            var setup = await UpdateService.DownloadAsync(_update, progress, _cancel.Token);

            _starting = true;
            _later.IsEnabled = false;
            _progress.IsIndeterminate = true;
            _status.Text = "Starting the installer. Clearspace closes now and opens again when the update is done.";
            UpdateService.StartInstaller(setup);
            // Close the way Setup asks Clearspace to: lock again whatever is unlocked for a visit, then exit.
            LockAgent.Current.Quit();
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            // The dialog was closed during the download; nothing to report.
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException
                                              or UnauthorizedAccessException or OperationCanceledException
                                              or System.ComponentModel.Win32Exception)
        {
            _starting = false;
            _working = false;
            _progress.Visibility = Visibility.Collapsed;
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            _status.Text = "The update couldn't be installed: " + Reason(exception) +
                           " You can also download it yourself from the Clearspace releases page on GitHub.";
            _later.Content = "Close";
            _later.IsEnabled = true;
            _install.Content = "Try again";
            _install.IsEnabled = true;
        }
    }

    private static string Reason(Exception exception) => exception switch
    {
        OperationCanceledException => "the download took too long.",
        HttpRequestException => "GitHub couldn't be reached. Check your internet connection.",
        _ => exception.Message
    };

    // Release notes are written in Markdown. Without a Markdown view, the readable thing to do is drop the
    // markup that only makes sense rendered: heading marks, bold marks, and turn list dashes into bullets.
    internal static string Tidy(string notes)
    {
        var lines = new List<string>();
        foreach (var raw in notes.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd().Replace("**", string.Empty).Replace("`", string.Empty);
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) line = trimmed.TrimStart('#').Trim();
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                line = line[..(line.Length - trimmed.Length)] + "\u2022 " + trimmed[2..];
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0)) continue; // no doubled blank lines
            lines.Add(line);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }
}
