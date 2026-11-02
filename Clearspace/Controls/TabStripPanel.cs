// Clearspace | NEW (tabs): lays the tabs out in one row.
// Every tab gets the same width: as wide as MaxTabWidth while there is room, narrower as more tabs are
// opened (down to MinTabWidth), the way Explorer's and a browser's tabs shrink. ReservedWidth is kept free
// to the right of the tabs for the "new tab" button, so the button never gets pushed out of the window.
//
// CHANGED (group capsule): a tab can ask for extra room before it (Lead: the name of a tab group, at the
// start of the group's capsule), and can be just that (LeadOnly: a collapsed group shows only its name).
// A tab whose Visibility is Collapsed (the other tabs of a collapsed group) takes no room. Leads are given
// first; the tabs share what is left. IndexAt turns a point on the strip into a tab's position, for dragging
// (it used to be x / TabWidth, which is no longer true once tabs differ in width).

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

    // ---- NEW (group capsule): set on each tab's container (MainWindow.xaml, ItemContainerStyle)

    public static readonly DependencyProperty LeadProperty = DependencyProperty.RegisterAttached(
        "Lead", typeof(double), typeof(TabStripPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static double GetLead(DependencyObject element) => (double)element.GetValue(LeadProperty);
    public static void SetLead(DependencyObject element, double value) => element.SetValue(LeadProperty, value);

    public static readonly DependencyProperty LeadOnlyProperty = DependencyProperty.RegisterAttached(
        "LeadOnly", typeof(bool), typeof(TabStripPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static bool GetLeadOnly(DependencyObject element) => (bool)element.GetValue(LeadOnlyProperty);
    public static void SetLeadOnly(DependencyObject element, bool value) => element.SetValue(LeadOnlyProperty, value);

    // The width a child takes in a row whose tabs are `tab` wide.
    private static double WidthOf(UIElement child, double tab)
    {
        if (child.Visibility == Visibility.Collapsed)
            return 0;
        var lead = Math.Max(0, GetLead(child));
        return GetLeadOnly(child) ? lead : lead + tab;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = InternalChildren.Count;
        if (count == 0)
        {
            TabWidth = 0;
            return new Size(0, 0);
        }

        // CHANGED (group capsule): leads come off the room first, and only children that show a tab share the rest.
        double leads = 0;
        var bodies = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;
            leads += Math.Max(0, GetLead(child));
            if (!GetLeadOnly(child))
                bodies++;
        }

        var room = double.IsInfinity(availableSize.Width)
            ? MaxTabWidth * Math.Max(1, bodies) + leads
            : Math.Max(0, availableSize.Width - ReservedWidth);

        TabWidth = bodies == 0 ? 0 : Math.Clamp(Math.Floor((room - leads) / bodies), MinTabWidth, MaxTabWidth);

        var height = 0d;
        double total = 0;
        foreach (UIElement child in InternalChildren)
        {
            var width = WidthOf(child, TabWidth);
            child.Measure(new Size(width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
            total += width;
        }

        // With more tabs than fit even at the smallest width, the row is as wide as the room there is and
        // the tabs past the edge are cut off (ClipToBounds on the strip).
        return new Size(Math.Min(total, room), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        foreach (UIElement child in InternalChildren)
        {
            var width = WidthOf(child, TabWidth);
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }

        return finalSize;
    }

    /// <summary>
    /// NEW (group capsule): the position of the tab under <paramref name="x"/> (strip coordinates): the one
    /// whose room contains it, the last one past the end, 0 before the start. Children taking no room are skipped.
    /// </summary>
    public int IndexAt(double x)
    {
        var start = 0d;
        var last = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var width = WidthOf(InternalChildren[i], TabWidth);
            if (width <= 0)
                continue;
            last = i;
            if (x < start + width)
                return i;
            start += width;
        }

        return last;
    }
}
