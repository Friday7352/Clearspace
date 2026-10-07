// Clearspace | NEW (themes page): a theme shown as a very small Clearspace window.
// The Themes page (ThemesView.cs) shows one of these for every theme. It is a drawing of the main window
// reduced to what tells themes apart: the window's background, a toolbar with the address box and one
// accent-coloured button, the sidebar, and a file list with one row selected. Files and folders are plain
// bars; the only words are the ones in the address box, so the theme's own font can be seen.
//
// Nothing here knows any theme's colours. Every part asks for a key ("Base", "Surface", "Ink", "Accent",
// "R8" ...) the same way the real window does, and the theme's resources are put in this element's own
// Resources (ThemeService.PreviewResources), so the look-up stops here instead of reaching the theme that
// is on screen. A theme that paints its window with a gradient or a pattern shows that too.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Clearspace.Controls;

internal sealed class ThemePreview : Border
{
    /// <summary>How large a preview is drawn.</summary>
    internal const double PreviewWidth = 216;
    internal const double PreviewHeight = 136;

    /// <param name="theme">The theme's resources, from ThemeService.PreviewResources.</param>
    /// <param name="crispText">The theme draws its text with hard pixel edges (Pixel).</param>
    /// <param name="words">What the address box says; null leaves a bar there instead.</param>
    internal ThemePreview(ResourceDictionary theme, bool crispText = false, string? words = "Documents")
    {
        Resources = theme;
        Width = PreviewWidth;
        Height = PreviewHeight;
        BorderThickness = new Thickness(1);
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        IsHitTestVisible = false; // it is a picture; the card it sits on takes the clicks
        SetResourceReference(BackgroundProperty, "Base");
        SetResourceReference(BorderBrushProperty, "Line");
        SetResourceReference(CornerRadiusProperty, "R8");

        var window = new Grid();
        window.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });
        window.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = Toolbar(crispText, words);
        var body = Body();
        Grid.SetRow(body, 1);
        window.Children.Add(toolbar);
        window.Children.Add(body);
        Child = window;

        // A Border does not cut its content off at its round corners; the sidebar reaches into one.
        SizeChanged += (_, _) => RoundOff();
        Loaded += (_, _) => RoundOff();
    }

    private void RoundOff()
    {
        var radius = TryFindResource("R8") is CornerRadius corner ? corner.TopLeft : 0;
        Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), radius, radius);
    }

    // Back and forward, the address box, and a button in the accent colour.
    private static Grid Toolbar(bool crispText, string? words)
    {
        var toolbar = new Grid { Margin = new Thickness(9, 7, 9, 6) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var arrows = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        arrows.Children.Add(Bar("InkMuted", 7, 7, new Thickness(0, 0, 4, 0), "R2"));
        arrows.Children.Add(Bar("InkFaint", 7, 7, new Thickness(0, 0, 7, 0), "R2"));

        var address = new Border { Padding = new Thickness(6, 0, 6, 0), BorderThickness = new Thickness(1) };
        address.SetResourceReference(BackgroundProperty, "Surface");
        address.SetResourceReference(BorderBrushProperty, "EdgeSunken");
        address.SetResourceReference(CornerRadiusProperty, "R4");
        if (words is null)
        {
            address.Child = Bar("InkMuted", 44, 3, default, "R2", HorizontalAlignment.Left);
        }
        else
        {
            var text = new TextBlock
            {
                Text = words,
                FontSize = 8,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            text.SetResourceReference(TextBlock.FontFamilyProperty, "UIFont");
            text.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
            TextOptions.SetTextRenderingMode(text, crispText ? TextRenderingMode.Aliased : TextRenderingMode.Auto);
            address.Child = text;
        }
        Grid.SetColumn(address, 1);

        var button = Bar("Accent", 22, 14, new Thickness(7, 0, 0, 0), "R4");
        Grid.SetColumn(button, 2);

        toolbar.Children.Add(arrows);
        toolbar.Children.Add(address);
        toolbar.Children.Add(button);
        return toolbar;
    }

    // The sidebar on the left, the file list on the right.
    private Grid Body()
    {
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- sidebar: a heading and four places, the second one being where the window is
        var places = new StackPanel { Margin = new Thickness(7, 7, 6, 0) };
        places.Children.Add(Bar("InkFaint", 22, 3, new Thickness(3, 0, 0, 6), "R2", HorizontalAlignment.Left));
        double[] widths = [30, 24, 34, 20];
        for (var index = 0; index < widths.Length; index++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(Bar(index == 1 ? "Accent" : "InkFaint", 5, 5, new Thickness(0, 0, 4, 0), "R2"));
            row.Children.Add(Bar(index == 1 ? "Ink" : "InkMuted", widths[index], 3, default, "R2"));

            var place = new Border { Height = 12, Margin = new Thickness(0, 0, 0, 2), Child = row };
            place.SetResourceReference(CornerRadiusProperty, "R4");
            if (index == 1)
                place.SetResourceReference(BackgroundProperty, "Hover");
            places.Children.Add(place);
        }

        var sidebar = new Border { Child = places };
        sidebar.SetResourceReference(BackgroundProperty, "Surface");

        // ---- file list: column headings, a line, then folders and files with one row selected
        var list = new StackPanel { Margin = new Thickness(8, 6, 8, 0) };

        var headings = new Grid { Margin = new Thickness(4, 0, 4, 4) };
        headings.Children.Add(Bar("InkFaint", 18, 3, default, "R2", HorizontalAlignment.Left));
        headings.Children.Add(Bar("InkFaint", 14, 3, default, "R2", HorizontalAlignment.Right));
        list.Children.Add(headings);

        var line = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 3) };
        line.SetResourceReference(BackgroundProperty, "Line");
        list.Children.Add(line);

        // A theme whose selected row is a solid colour names the text colour to use on it.
        var selectedInk = Resources.Contains("SelectedRow.Ink") ? "SelectedRow.Ink" : "Ink";
        var selectedFaint = Resources.Contains("SelectedRow.InkFaint") ? "SelectedRow.InkFaint"
            : Resources.Contains("SelectedRow.Ink") ? "SelectedRow.Ink" : "InkFaint";
        var selectedAccent = Resources.Contains("SelectedRow.Accent") ? "SelectedRow.Accent" : "Accent";

        (double Name, double Size, bool Folder)[] rows = [(34, 0, true), (46, 0, true), (58, 14, false), (40, 18, false), (52, 12, false)];
        for (var index = 0; index < rows.Length; index++)
        {
            var selected = index == 1;
            var content = new Grid { Margin = new Thickness(4, 0, 4, 0) };
            var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(Bar(!rows[index].Folder ? "InkMuted" : selected ? selectedAccent : "Accent", 8, 7, new Thickness(0, 0, 5, 0), "R2"));
            name.Children.Add(Bar(selected ? selectedInk : "Ink", rows[index].Name, 3, default, "R2"));
            content.Children.Add(name);
            if (rows[index].Size > 0)
                content.Children.Add(Bar(selected ? selectedFaint : "InkFaint", rows[index].Size, 3, default, "R2", HorizontalAlignment.Right));

            var row = new Border { Height = 14, Margin = new Thickness(0, 0, 0, 1), Child = content };
            row.SetResourceReference(CornerRadiusProperty, "R4");
            if (selected)
                row.SetResourceReference(BackgroundProperty, "Selected");
            list.Children.Add(row);
        }

        Grid.SetColumn(list, 1);
        body.Children.Add(sidebar);
        body.Children.Add(list);
        return body;
    }

    // A small filled box: a line of text, an icon, a button. Its colour and corners come from the theme.
    private static Border Bar(string brush, double width, double height, Thickness margin, string corner,
        HorizontalAlignment across = HorizontalAlignment.Stretch)
    {
        var bar = new Border
        {
            Width = width,
            Height = height,
            Margin = margin,
            HorizontalAlignment = across,
            VerticalAlignment = VerticalAlignment.Center
        };
        bar.SetResourceReference(BackgroundProperty, brush);
        bar.SetResourceReference(CornerRadiusProperty, corner);
        return bar;
    }
}
