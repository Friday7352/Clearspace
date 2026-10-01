// Clearspace | A message / confirmation dialog that follows the app's theme.
// NEW (themes): replaces the Windows message box for the app's own questions ("Delete the Work tag?",
// "Delete permanently?") so they match the active theme like every other window. It takes the same
// buttons and icons and returns the same results as MessageBox.Show. Built in code from the shared
// styles in App.xaml and the theme's colours; it looks like the lock dialog.
// (App.xaml.cs still uses the Windows message box for crash and start-up errors, on purpose: an error
// report should not depend on the app's own windows working.)
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clearspace.Services;

namespace Clearspace;

internal sealed class MessageDialog : Window
{
    private MessageBoxResult _result;

    private MessageDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Base");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(FontFamilyProperty, "UIFont");

        // What closing the window with Esc or the title bar's X means.
        var cancelResult = buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel
        };
        _result = cancelResult;

        // Header: round icon badge + title (the same pattern as the lock dialog).
        var glyph = new TextBlock
        {
            Text = image switch
            {
                MessageBoxImage.Question => "\uE897",
                MessageBoxImage.Warning => "\uE7BA",
                MessageBoxImage.Error => "\uE783",
                _ => "\uE946"
            },
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
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        var header = new DockPanel();
        header.Children.Add(badge);
        header.Children.Add(heading);

        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            Margin = new Thickness(0, 18, 0, 0)
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");

        // Buttons, right-aligned, the confirming one last and filled.
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0)
        };
        Button? first = null;
        foreach (var (label, result, primary) in ButtonsFor(buttons))
        {
            var button = new Button { Content = label, MinWidth = 76 };
            button.SetResourceReference(StyleProperty, primary ? "ViewerTextButtonPrimary" : "ViewerTextButton");
            button.Click += (_, _) => { _result = result; Close(); };
            if (result == defaultResult)
            {
                button.IsDefault = true;   // Enter presses it
                first = button;
            }
            row.Children.Add(button);
        }

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(body);
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
        EInkScreen.Frame(this); // NEW (e-ink): the E-reader theme's panel look reaches this dialog too

        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        Loaded += (_, _) => (first ?? row.Children.OfType<Button>().LastOrDefault())?.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;

            e.Handled = true;
            _result = cancelResult;
            Close();
        };
    }

    // The buttons for each kind of question, left to right: label, what it returns, and whether it is the filled one.
    private static (string Label, MessageBoxResult Result, bool Primary)[] ButtonsFor(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OKCancel => new[] { ("Cancel", MessageBoxResult.Cancel, false), ("OK", MessageBoxResult.OK, true) },
        MessageBoxButton.YesNo => new[] { ("No", MessageBoxResult.No, false), ("Yes", MessageBoxResult.Yes, true) },
        MessageBoxButton.YesNoCancel => new[] { ("Cancel", MessageBoxResult.Cancel, false), ("No", MessageBoxResult.No, false), ("Yes", MessageBoxResult.Yes, true) },
        _ => new[] { ("OK", MessageBoxResult.OK, true) }
    };

    // Shows the dialog and waits for an answer. "owner" may be null (for example when Explorer asked
    // Clearspace to do something and no Clearspace window is open): the dialog then centres on the screen
    // and comes to the front by itself. "defaultResult" is the button Enter presses; when left out it is
    // the confirming button (OK / Yes).
    internal static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        if (defaultResult == MessageBoxResult.None)
            defaultResult = buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel ? MessageBoxResult.Yes : MessageBoxResult.OK;

        var dialog = new MessageDialog(message, title, buttons, image, defaultResult);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;
            dialog.Topmost = true;
            dialog.Loaded += (_, _) => { dialog.Activate(); dialog.Topmost = false; };
        }

        dialog.ShowDialog();
        return dialog._result;
    }
}
