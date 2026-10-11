using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clearspace.Services;

namespace Clearspace;

// CHANGED (edit in place): moved out of ThemeStudioWindow.cs, which is gone; the colour picker, the picture
// editor, the pattern maker, the layout builder and edit mode use these. SuggestedColors moved here too.
// NEW (theme studio, round 2): the small pieces the studio and its colour picker share.
internal static class StudioUi
{
    internal static readonly Brush Checker = MakeChecker();

    /// <summary>The colours the colour picker suggests (were ThemeStudioWindow.SuggestedColors).</summary>
    internal static IReadOnlyList<string> SuggestedColors { get; } = ["#AFA1FF", "#64D8CF", "#FF9DCB", "#F6C56F", "#426B41", "#A54432", "#326CC0", "#FFFFFF", "#171D30", "#000000"];

    private static Brush MakeChecker()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        var squares = new GeometryGroup();
        squares.Children.Add(new RectangleGeometry(new Rect(0, 0, 4, 4)));
        squares.Children.Add(new RectangleGeometry(new Rect(4, 4, 4, 4)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(204, 204, 204)), null, squares));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }

    /// <summary>A small framed colour sample; the checkerboard shows through see-through colours.</summary>
    internal static (Border Outer, Border Inner) Swatch(double size)
    {
        var inner = new Border { CornerRadius = new CornerRadius(5) };
        var outer = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Background = Checker, Child = inner, SnapsToDevicePixels = true };
        outer.SetResourceReference(Border.BorderBrushProperty, "Line");
        return (outer, inner);
    }

    /// <summary>A whole row that acts as one button: hover fill, click, Enter / Space.</summary>
    internal static Border ClickRow(UIElement content, string name, Action click)
    {
        var row = new Border { Padding = new Thickness(8, 7, 8, 7), Child = content, Cursor = Cursors.Hand, Background = Brushes.Transparent, Focusable = true };
        row.SetResourceReference(Border.CornerRadiusProperty, "R6");
        void Lit(bool on) { if (on) row.SetResourceReference(Border.BackgroundProperty, "Hover"); else row.Background = Brushes.Transparent; }
        row.MouseEnter += (_, _) => Lit(true);
        row.MouseLeave += (_, _) => Lit(row.IsKeyboardFocused);
        row.GotKeyboardFocus += (_, _) => Lit(true);
        row.LostKeyboardFocus += (_, _) => Lit(row.IsMouseOver);
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        row.KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { e.Handled = true; click(); } };
        AutomationProperties.SetName(row, name);
        return row;
    }

    internal static TextBlock Heading(string text)
    {
        var heading = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 6) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        return heading;
    }

    /// <summary>A heading over a card holding the given pieces, top to bottom.</summary>
    internal static StackPanel Card(string title, params UIElement[] children)
    {
        var inside = new StackPanel();
        foreach (var child in children) inside.Children.Add(child);
        // CHANGED (live canvas, round 2): cards sit in the tool panels (Surface) now, so they are a shade darker
        // with a line round them, like the tiles in a part's panel.
        var card = new Border { Padding = new Thickness(12), BorderThickness = new Thickness(1), Child = inside, CornerRadius = new CornerRadius(10) };
        card.SetResourceReference(Border.BackgroundProperty, "Base");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        block.Children.Add(Heading(title));
        block.Children.Add(card);
        return block;
    }

    internal static Border Divider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 8) };
        line.SetResourceReference(Border.BackgroundProperty, "LineFaint");
        return line;
    }

    internal static string Hex(Color c, bool opaque = false) => $"#{(opaque ? (byte)255 : c.A):X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    internal static Color TextOn(Color background) =>
        ThemeDesign.Contrast(background, Colors.White) > ThemeDesign.Contrast(background, Colors.Black) ? Colors.White : Colors.Black;

    /// <summary>A colour laid over another, as the screen shows it.</summary>
    internal static Color Over(Color top, Color under)
    {
        var a = top.A / 255d;
        byte Mix(byte t, byte u) => (byte)Math.Round(t * a + u * (1 - a));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }
}
