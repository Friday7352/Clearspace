// Clearspace | NEW (shelf and inspector): the small pieces the Inspector and the Shelf are built from, so the two
// look alike: a heading with a close button, buttons with a symbol and a word, and quiet text.
// Built in code from the theme's colours (DynamicResource keys), so they follow every theme.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Clearspace.Controls;

internal static class DockUi
{
    /// <summary>"[symbol] Title ......... [extra] [×]" across the top of a panel.</summary>
    internal static DockPanel Heading(string glyph, string title, string closeTip, Action close, params UIElement[] extra)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };

        var closeButton = IconButton("\uE711", closeTip, close);
        DockPanel.SetDock(closeButton, Dock.Right);
        row.Children.Add(closeButton);
        for (var i = extra.Length - 1; i >= 0; i--)
        {
            DockPanel.SetDock(extra[i], Dock.Right);
            row.Children.Add(extra[i]);
        }

        var symbol = new TextBlock { Text = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
        symbol.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        symbol.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        DockPanel.SetDock(symbol, Dock.Left);
        row.Children.Add(symbol);

        var words = new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        words.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        row.Children.Add(words);
        return row;
    }

    /// <summary>A small square button with a symbol (the toolbar's look).</summary>
    internal static Button IconButton(string glyph, string tip, Action click)
    {
        var button = new Button { Content = glyph, ToolTip = tip, Width = 28, Height = 28, FontSize = 11 };
        button.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A button with a symbol and a word or two.</summary>
    internal static Button TextButton(string glyph, string text, string tip, Action click)
    {
        var symbol = new TextBlock { Text = glyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        symbol.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        var words = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        words.SetResourceReference(TextBlock.FontFamilyProperty, "UIFont");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(symbol);
        content.Children.Add(words);

        var button = new Button { Content = content, ToolTip = tip, Height = 30, Margin = new Thickness(0, 0, 4, 4) };
        button.SetResourceReference(FrameworkElement.StyleProperty, "LabelButton");
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Quiet text (muted unless <paramref name="key"/> says otherwise).</summary>
    internal static TextBlock Text(string text, string key = "InkMuted", double size = 12)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, key);
        return block;
    }

    /// <summary>A big, faint symbol over a line of text, for an empty panel.</summary>
    internal static StackPanel Empty(string glyph, string text)
    {
        var symbol = new TextBlock { Text = glyph, FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center };
        symbol.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        symbol.SetResourceReference(TextBlock.ForegroundProperty, "InkFaint");
        var line = Text(text);
        line.TextAlignment = TextAlignment.Center;
        line.Margin = new Thickness(0, 10, 0, 0);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12) };
        stack.Children.Add(symbol);
        stack.Children.Add(line);
        return stack;
    }
}
