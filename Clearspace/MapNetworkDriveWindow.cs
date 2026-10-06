// Clearspace | NEW (map network drive): the "Map network drive" dialog.
// Gives a shared folder on another computer or a NAS a drive letter without going through File Explorer.
// You type (or browse to) the folder, pick one of the free letters, and choose whether Windows should
// connect it again every time you sign in. "Connect using different credentials" makes Windows ask for a
// user name and password in its own sign-in box; Clearspace never sees the password.
// The rules and the Windows calls are in Services/NetworkMapping.cs. Connecting can take a while when
// the server does not answer, so it runs in the background and the dialog stays usable (Cancel closes it).
// Built in code from the shared styles in App.xaml and the theme's colours, like MessageDialog.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Clearspace.Services;

namespace Clearspace;

internal sealed class MapNetworkDriveWindow : Window
{
    private readonly TextBox _folder;
    private readonly WrapPanel _letters = new();
    private readonly CheckBox _reconnect;
    private readonly CheckBox _otherCredentials;
    private readonly TextBlock _status;
    private readonly Button _browse;
    private readonly Button _map;
    private char _letter;      // '\0' when every letter is in use
    private bool _connecting;
    private bool _closed;

    /// <summary>The new drive ("Z:\"); null until one was mapped.</summary>
    internal string? MappedRoot { get; private set; }

    /// <param name="suggestedFolder">A network folder to start with (the one on screen, when it is one).</param>
    internal MapNetworkDriveWindow(string? suggestedFolder = null)
    {
        Title = "Map network drive";
        Width = 580;
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
            Text = "\uE8CE", // Map drive
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

        var heading = new TextBlock { Text = "Map network drive", FontSize = 20, FontWeight = FontWeights.SemiBold };
        var about = Muted("Gives a shared folder on another computer or a NAS a drive letter, so it opens like a drive on this PC.");
        about.Margin = new Thickness(0, 3, 0, 0);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(heading);
        titles.Children.Add(about);

        var header = new DockPanel();
        header.Children.Add(badge);
        header.Children.Add(titles);

        // Folder: the box, Browse, and an example underneath.
        _folder = new TextBox { Text = suggestedFolder ?? string.Empty };
        _folder.SetResourceReference(StyleProperty, "PlainTextBox");
        _folder.TextChanged += (_, _) =>
        {
            if (!_connecting)
                ShowStatus(string.Empty, problem: false); // what was wrong with the old text no longer applies
        };
        var folderFrame = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(10, 7, 10, 7), Child = _folder };
        folderFrame.SetResourceReference(Border.BackgroundProperty, "Base");
        folderFrame.SetResourceReference(Border.BorderBrushProperty, "EdgeSunken");
        folderFrame.SetResourceReference(Border.CornerRadiusProperty, "R8");

        _browse = TextButton("Browse…", primary: false);
        _browse.ToolTip = "Pick a shared folder under Network";
        _browse.Click += (_, _) => Browse();
        Grid.SetColumn(_browse, 1);

        var folderRow = new Grid();
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.Children.Add(folderFrame);
        folderRow.Children.Add(_browse);

        var example = Muted($"For example {NetworkMapping.Example}, or {NetworkMapping.Example}\\folder to start further in.");
        example.Margin = new Thickness(0, 6, 0, 0);
        example.FontSize = 11.5;

        var folderCell = new StackPanel();
        folderCell.Children.Add(folderRow);
        folderCell.Children.Add(example);

        // Drive letter: one small button per free letter, Z first. Tab reaches the group once; the arrow
        // keys then move through it, and the letter that has the keyboard is the one chosen.
        var free = NetworkMapping.FreeLetters();
        foreach (var letter in free)
        {
            var chip = new Button
            {
                Content = $"{letter}:",
                Tag = letter,
                MinWidth = 40,
                Height = 28,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 0, 4, 4)
            };
            chip.Click += (_, _) => Choose(letter);
            chip.GotKeyboardFocus += (_, _) => Choose(letter);
            _letters.Children.Add(chip);
        }
        KeyboardNavigation.SetTabNavigation(_letters, KeyboardNavigationMode.Once);
        KeyboardNavigation.SetDirectionalNavigation(_letters, KeyboardNavigationMode.Cycle);

        UIElement letterCell = _letters;
        if (free.Length == 0)
            letterCell = Muted("Every drive letter is in use. Disconnect a network drive you no longer need, then try again.");

        // Options.
        _reconnect = new CheckBox
        {
            Content = "Reconnect at sign-in",
            IsChecked = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "Windows remembers the drive and connects it again each time you sign in. Off: it lasts until you sign out or restart."
        };
        _reconnect.SetResourceReference(StyleProperty, "ViewerCheck");

        _otherCredentials = new CheckBox
        {
            Content = "Connect using different credentials",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0),
            ToolTip = "Windows asks for a user name and password instead of using the account you are signed in with. " +
                      "It asks by itself anyway when the server does not accept your account."
        };
        _otherCredentials.SetResourceReference(StyleProperty, "ViewerCheck");

        var options = new StackPanel();
        options.Children.Add(_reconnect);
        options.Children.Add(_otherCredentials);

        var fields = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddField(fields, "Folder", folderCell);
        AddField(fields, "Drive letter", letterCell);
        AddField(fields, string.Empty, options);

        // Bottom row: what is happening (or what went wrong) on the left, the buttons on the right.
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        var cancel = TextButton("Cancel", primary: false);
        cancel.IsCancel = true; // Esc
        cancel.Click += (_, _) => Close();
        _map = TextButton("Map drive", primary: true);
        _map.IsDefault = true; // Enter
        _map.IsEnabled = free.Length > 0;
        _map.Click += async (_, _) => await MapNowAsync();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_map);
        DockPanel.SetDock(buttons, Dock.Right);
        var bottom = new DockPanel { Margin = new Thickness(0, 22, 0, 0) };
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(fields);
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

        if (free.Length > 0)
            Choose(free[0]);
        ShowStatus(string.Empty, problem: false);

        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        Loaded += (_, _) =>
        {
            _folder.Focus();
            _folder.CaretIndex = _folder.Text.Length;
        };
        Closed += (_, _) => _closed = true;
    }

    // Marks one letter as the chosen one (filled, like the dialog's main button).
    private void Choose(char letter)
    {
        _letter = letter;
        foreach (var chip in _letters.Children.OfType<Button>())
            chip.SetResourceReference(StyleProperty, chip.Tag is char own && own == letter ? "ViewerTextButtonPrimary" : "ViewerTextButton");
    }

    // Windows' own folder picker: Network in its list on the left shows the computers and their shared folders.
    private void Browse()
    {
        try
        {
            var picker = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose a shared folder on the network",
                Multiselect = false
            };

            if (picker.ShowDialog(this) != true)
                return;

            var chosen = picker.FolderName;

            // A folder picked on a drive that is mapped already is given by its network name.
            if (chosen.Length >= 3 && chosen[1] == ':' && NetworkMapping.RemoteName(chosen) is { } share)
                chosen = share.TrimEnd('\\') + chosen[2..];

            if (!chosen.StartsWith(@"\\", StringComparison.Ordinal))
            {
                ShowStatus("That folder is on this PC. Pick one under Network, in the list on the left of the picker.", problem: true);
                return;
            }

            _folder.Text = chosen.TrimEnd('\\');
            _folder.CaretIndex = _folder.Text.Length;
            _folder.Focus();
        }
        catch (Exception)
        {
            // the picker could not be shown; the folder can still be typed
        }
    }

    private async Task MapNowAsync()
    {
        if (_connecting || _letter == default)
            return;

        if (!NetworkMapping.TryNormalize(_folder.Text, out var unc, out var reason))
        {
            ShowStatus(reason, problem: true);
            _folder.Focus();
            return;
        }

        var letter = _letter;
        var root = $@"{letter}:\";
        SetConnecting(true);
        ShowStatus($"Connecting to {NetworkMapping.ServerOf(unc)}…", problem: false);

        var result = await NetworkMapping.MapAsync(letter, unc, _reconnect.IsChecked == true,
            _otherCredentials.IsChecked == true, new WindowInteropHelper(this).Handle);

        if (_closed)
        {
            // Cancel was pressed while Windows was still trying, and the answer arrived afterwards. A drive
            // nobody is waiting for any more is taken away again.
            if (result.Succeeded)
                _ = Task.Run(() => NetworkMapping.Disconnect(root, force: true));
            return;
        }

        SetConnecting(false);

        if (result.Succeeded)
        {
            MappedRoot = root;
            DialogResult = true; // closes the dialog
            return;
        }

        // Closing Windows' sign-in box is not an error: back to the dialog as it was.
        ShowStatus(result.Cancelled ? string.Empty : result.Message, problem: true);
    }

    // While Windows is connecting nothing can be changed, but Cancel still closes the dialog.
    private void SetConnecting(bool connecting)
    {
        _connecting = connecting;
        _folder.IsEnabled = !connecting;
        _browse.IsEnabled = !connecting;
        _letters.IsEnabled = !connecting;
        _reconnect.IsEnabled = !connecting;
        _otherCredentials.IsEnabled = !connecting;
        _map.IsEnabled = !connecting;
    }

    private void ShowStatus(string text, bool problem)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "Danger" : "InkMuted");
    }

    // ---- small builders (the same shapes as BatchRenameWindow)

    private static void AddField(Grid table, string label, UIElement field)
    {
        var row = table.RowDefinitions.Count;
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var left = Muted(label);
        left.Margin = new Thickness(0, row == 0 ? 8 : 20, 18, 0);
        left.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(left, row);
        table.Children.Add(left);

        var cell = new Border { Margin = new Thickness(0, row == 0 ? 0 : 14, 0, 0), Child = field };
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, 1);
        table.Children.Add(cell);
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
