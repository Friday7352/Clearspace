// Clearspace | NEW (go back): About Clearspace… > "Earlier versions…".
// Lists the Clearspace releases older than the one that is running and installs the one you pick over it.
// The list comes from GitHub when the window opens (UpdateService.EarlierAsync); picking one opens the
// update dialog in its "going back" wording (UpdateWindow), which downloads that release's own installer,
// checks it and runs it, exactly as an update is installed. Settings, tags and locked files are kept.
// Only releases that are safe to go back to are listed: see the note in Services/UpdateService.cs.
// Built in code from the shared styles in App.xaml and the theme's colours, like MessageDialog.
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Clearspace.Services;

namespace Clearspace;

internal sealed class VersionsWindow : Window
{
    private readonly StackPanel _rows = new();
    private bool _loading;

    internal VersionsWindow()
    {
        Title = "Earlier versions";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Base");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(FontFamilyProperty, "UIFont");

        // Header: round icon badge + title (the same pattern as MessageDialog).
        var glyph = new TextBlock
        {
            Text = "", // History
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

        var heading = new TextBlock { Text = "Earlier versions", FontSize = 20, FontWeight = FontWeights.SemiBold };
        var about = Muted($"You have Clearspace {UpdateService.CurrentText}. If it gives you trouble, you can go back to an earlier " +
                          "version. Your settings, tags and locked files are kept, and you can update again whenever you like.");
        about.Margin = new Thickness(0, 4, 0, 0);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(heading);
        titles.Children.Add(about);

        var header = new DockPanel();
        header.Children.Add(badge);
        header.Children.Add(titles);

        // The releases, newest first; filled in once GitHub has answered.
        _rows.Margin = new Thickness(14, 8, 14, 8);
        var scroll = new ScrollViewer
        {
            Content = _rows,
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var list = new Border { Margin = new Thickness(0, 18, 0, 0), Child = scroll };
        list.SetResourceReference(Border.BackgroundProperty, "Tint");
        list.SetResourceReference(Border.CornerRadiusProperty, "R8");

        var close = TextButton("Close", primary: false);
        close.IsCancel = true; // Esc
        close.Click += (_, _) => Close();
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        bottom.Children.Add(close);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(list);
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
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading)
            return;

        _loading = true;
        _rows.Children.Clear();
        _rows.Children.Add(Note("Looking for earlier versions…"));

        try
        {
            var earlier = await UpdateService.EarlierAsync();
            _rows.Children.Clear();

            if (earlier.Count == 0)
            {
                _rows.Children.Add(Note($"There is no earlier version to go back to from {UpdateService.CurrentText}. " +
                                        $"Versions before {UpdateService.Text(UpdateService.OldestToGoBackTo)} can't update themselves, so they are not offered."));
                return;
            }

            foreach (var release in earlier)
                _rows.Children.Add(Row(release));
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            _rows.Children.Clear();
            _rows.Children.Add(Note("Clearspace couldn't reach GitHub to get the list. Check your internet connection."));

            var again = TextButton("Try again", primary: false);
            again.HorizontalAlignment = HorizontalAlignment.Left;
            again.Margin = new Thickness(0, 4, 0, 6);
            again.Click += async (_, _) => await LoadAsync();
            _rows.Children.Add(again);
        }
        finally
        {
            _loading = false;
        }
    }

    // One release: its number and the day it came out, and the button that goes back to it.
    private DockPanel Row(UpdateInfo release)
    {
        var pick = TextButton("Go back to this", primary: false);
        pick.VerticalAlignment = VerticalAlignment.Center;
        pick.Click += (_, _) => new UpdateWindow(release, goingBack: true) { Owner = this }.ShowDialog();
        DockPanel.SetDock(pick, Dock.Right);

        var name = new TextBlock { Text = "Clearspace " + release.VersionText, FontWeight = FontWeights.SemiBold };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(name);

        if (release.Published is { } day)
        {
            var released = Muted("Released " + day.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture));
            released.FontSize = 11.5;
            released.Margin = new Thickness(0, 2, 0, 0);
            words.Children.Add(released);
        }

        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        row.Children.Add(pick);
        row.Children.Add(words);
        return row;
    }

    private static TextBlock Note(string text)
    {
        var note = Muted(text);
        note.Margin = new Thickness(0, 6, 0, 6);
        return note;
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
}
