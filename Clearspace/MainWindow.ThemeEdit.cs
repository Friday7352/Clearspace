// Clearspace | NEW (edit in place): the main window's side of making a theme.
// "Create a theme" and "Edit theme…" on the Themes page close the page and put this window into edit mode
// (ThemeEditor.cs). There is no separate theme studio any more.
//   - The editor's bar goes across the top of the window, above everything else.
//   - CHANGED (side panel): the editor's panel goes in its own column to the right of the window's content, so it
//     never covers the explorer. The explorer is a little narrower while editing.
//   - NEW (hand of cards): the theme's cards go in a row of their own under the window's content, beside the panel.
//   - The editor's picking layer is laid over the window's content (ScreenContent): it is see-through, and only
//     draws outlines, so clicks pick parts of the window instead of opening files.
//   - Keys go to the editor (Ctrl+Z, Ctrl+Y, Ctrl+S, Esc, Delete); the explorer's own shortcuts are off.
//   - Closing the window while editing asks first if there are changes, and puts the theme from before back.
// When editing ends (Done or Cancel) the bar, the panel and the picking layer go away and the window is as it was.
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clearspace.Services;

namespace Clearspace;

public partial class MainWindow
{
    private ThemeEditor? _themeEdit;   // the editor, while the window is in edit mode
    private Grid? _themeEditHost;      // holds the bar above, and the panel beside, the window's own content meanwhile
    private UIElement? _themeEditContent;

    internal bool EditingTheme => _themeEdit is not null;

    /// <summary>For tests: the editor while the window is in edit mode.</summary>
    internal ThemeEditor? ThemeEdit => _themeEdit;

    private void StartThemeEdit(ThemeInfo? theme)
    {
        if (_themeEdit is not null) return;
        ThemeEditor editor;
        try { editor = new ThemeEditor(this, this, theme); }
        catch (Exception error) when (error is ThemeFormatException or System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageDialog.Show(this, error.Message, "This theme could not be opened", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // CHANGED (side panel, hand of cards): the bar across the top; the window's own content with the hand of
        // cards under it, and the panel beside both.
        if (Content is not UIElement content) return;
        _themeEditContent = content;
        Content = null;
        var host = new Grid();
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // NEW (hand of cards)
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(editor.Banner, 0); Grid.SetColumnSpan(editor.Banner, 2);
        Grid.SetRow(content, 1); Grid.SetColumn(content, 0);
        Grid.SetRow(editor.SidePanel, 1); Grid.SetColumn(editor.SidePanel, 1); Grid.SetRowSpan(editor.SidePanel, 2);
        Grid.SetRow(editor.Hand, 2); Grid.SetColumn(editor.Hand, 0);   // NEW (hand of cards): under the window, not over it
        host.Children.Add(editor.Banner);
        host.Children.Add(content);
        host.Children.Add(editor.SidePanel);
        host.Children.Add(editor.Hand);
        _themeEditHost = host;
        Content = host;

        // The picking layer lies over everything in the window's content, the explorer included.
        Panel.SetZIndex(editor, 1000);
        Grid.SetRowSpan(editor, Math.Max(1, ScreenContent.RowDefinitions.Count));
        Grid.SetColumnSpan(editor, Math.Max(1, ScreenContent.ColumnDefinitions.Count));
        ScreenContent.Children.Add(editor);
        _themeEdit = editor;
        editor.Finished += (_, saved) => EndThemeEdit(saved);
        editor.Start();
    }

    private void EndThemeEdit(string? saved)
    {
        if (_themeEdit is not { } editor) return;
        _themeEdit = null;
        ScreenContent.Children.Remove(editor);
        if (_themeEditHost is { } host && _themeEditContent is { } content)
        {
            Content = null;
            host.Children.Clear();
            Grid.SetRow(content, 0); Grid.SetColumn(content, 0);
            Content = content;
        }
        _themeEditHost = null; _themeEditContent = null;
        _viewModel.ReportStatus(saved is null ? "The theme was left as it was." : $"{saved} is saved and now in use.");
        FileList.Focus();
        FlashPage();
    }

    // Called first thing from OnPreviewKeyDown: while editing, every key goes to the editor.
    private bool ThemeEditKey(KeyEventArgs e)
    {
        if (_themeEdit is null) return false;
        // The editor lets the theme's name type as usual, and lets Tab, arrows, Enter and Space work in its panel.
        _themeEdit.OnKey(e);
        return true;
    }

    // Closing the window while editing: ask first if there are changes; the theme from before comes back.
    private void ThemeEditClosing(CancelEventArgs e)
    {
        if (_themeEdit is { } editor && !editor.Cancel(ask: true)) e.Cancel = true;
    }
}
