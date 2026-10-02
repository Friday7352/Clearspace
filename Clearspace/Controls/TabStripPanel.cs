// Clearspace | NEW (tabs): lays the tabs out in one row.
// Every tab gets the same width: as wide as MaxTabWidth while there is room, narrower as more tabs are
// opened (down to MinTabWidth), the way Explorer's and a browser's tabs shrink. ReservedWidth is kept free
// to the right of the tabs for the "new tab" button, so the button never gets pushed out of the window.

using System.Windows;
using System.Windows.Controls;

namespace Clearspace.Controls;

public sealed class TabStripPanel : Panel
{
    public double MaxTabWidth { get; set; } = 220;

    public double MinTabWidth { get; set; } = 64;

    public double ReservedWidth { get; set; }

    // The width each tab was given in the last layout pass (0 before the first). MainWindow.Tabs.cs uses
    // it to work out which position a tab is being dragged to.
    public double TabWidth { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = InternalChildren.Count;
        if (count == 0)
        {
            TabWidth = 0;
            return new Size(0, 0);
        }

        var room = double.IsInfinity(availableSize.Width)
            ? MaxTabWidth * count
            : Math.Max(0, availableSize.Width - ReservedWidth);

        TabWidth = Math.Clamp(Math.Floor(room / count), MinTabWidth, MaxTabWidth);

        var height = 0d;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(TabWidth, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        // With more tabs than fit even at the smallest width, the row is as wide as the room there is and
        // the tabs past the edge are cut off (ClipToBounds on the strip).
        return new Size(Math.Min(TabWidth * count, room), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(x, 0, TabWidth, finalSize.Height));
            x += TabWidth;
        }

        return finalSize;
    }
}
