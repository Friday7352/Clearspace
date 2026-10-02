// Clearspace | Virtualized wrapping layout for file tiles.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Clearspace.Controls;

public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(
            nameof(ItemWidth),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(160d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(
            nameof(ItemHeight),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(170d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _columns = 1;

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null || ItemContainerGenerator is null)
            return availableSize;

        var itemCount = owner.Items.Count;
        var width = ResolveViewportWidth(availableSize.Width);
        var height = ResolveViewportHeight(availableSize.Height);
        var cellWidth = Math.Max(1d, ItemWidth);
        var cellHeight = Math.Max(1d, ItemHeight);

        _columns = Math.Max(1, (int)Math.Floor(width / cellWidth));
        var rowCount = itemCount == 0 ? 0 : (int)Math.Ceiling(itemCount / (double)_columns);
        UpdateScrollInfo(new Size(width, rowCount * cellHeight), new Size(width, height));

        if (itemCount == 0)
        {
            CleanUpItems(0, -1);
            return availableSize;
        }

        var firstRow = Math.Max(0, (int)Math.Floor(VerticalOffset / cellHeight) - 1);
        var visibleRows = Math.Max(1, (int)Math.Ceiling(height / cellHeight) + 2);
        var firstIndex = Math.Min(itemCount - 1, firstRow * _columns);
        var lastIndex = Math.Min(itemCount - 1, ((firstRow + visibleRows) * _columns) - 1);

        RealizeItems(firstIndex, lastIndex, new Size(cellWidth, cellHeight));
        CleanUpItems(firstIndex, lastIndex);
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cellWidth = Math.Max(1d, ItemWidth);
        var cellHeight = Math.Max(1d, ItemHeight);
        var generator = ItemContainerGenerator;

        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var child = InternalChildren[childIndex];
            var itemIndex = IndexFromContainer(child);
            if (itemIndex < 0)
                continue;

            var column = itemIndex % _columns;
            var row = itemIndex / _columns;
            child.Arrange(new Rect(
                column * cellWidth,
                (row * cellHeight) - VerticalOffset,
                cellWidth,
                cellHeight));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0)
            return;

        var row = index / Math.Max(1, _columns);
        var top = row * Math.Max(1d, ItemHeight);
        var bottom = top + Math.Max(1d, ItemHeight);
        if (top < VerticalOffset)
            SetVerticalOffset(top);
        else if (bottom > VerticalOffset + ViewportHeight)
            SetVerticalOffset(bottom - ViewportHeight);
    }

    private void RealizeItems(int firstIndex, int lastIndex, Size childSize)
    {
        var generator = ItemContainerGenerator;
        if (generator is null || firstIndex < 0 || lastIndex < firstIndex)
            return;

        var startPosition = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;

        using (generator.StartAt(startPosition, GeneratorDirection.Forward, true))
        {
            for (var itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
            {
                var generated = generator.GenerateNext(out var newlyRealized);
                if (generated is not UIElement child)
                    break;

                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count)
                        AddInternalChild(child);
                    else
                        InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }

                child.Measure(childSize);
            }
        }
    }

    private void CleanUpItems(int firstIndex, int lastIndex)
    {
        var generator = ItemContainerGenerator;
        if (generator is null)
            return;

        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var child = InternalChildren[childIndex];
            var itemIndex = IndexFromContainer(child);
            if (itemIndex >= firstIndex && itemIndex <= lastIndex)
                continue;

            var position = new GeneratorPosition(childIndex, 0);
            generator.Remove(position, 1);
            RemoveInternalChildRange(childIndex, 1);
        }
    }

    private double ResolveViewportWidth(double availableWidth)
    {
        if (!double.IsInfinity(availableWidth) && availableWidth > 0)
            return availableWidth;
        if (ScrollOwner?.ViewportWidth > 0)
            return ScrollOwner.ViewportWidth;
        return ActualWidth > 0 ? ActualWidth : Math.Max(1d, ItemWidth);
    }

    private double ResolveViewportHeight(double availableHeight)
    {
        if (!double.IsInfinity(availableHeight) && availableHeight > 0)
            return availableHeight;
        if (ScrollOwner?.ViewportHeight > 0)
            return ScrollOwner.ViewportHeight;
        return ActualHeight > 0 ? ActualHeight : Math.Max(1d, ItemHeight) * 4;
    }

    private void UpdateScrollInfo(Size extent, Size viewport)
    {
        var changed = !AreClose(_extent.Width, extent.Width)
                      || !AreClose(_extent.Height, extent.Height)
                      || !AreClose(_viewport.Width, viewport.Width)
                      || !AreClose(_viewport.Height, viewport.Height);

        _extent = extent;
        _viewport = viewport;
        _offset.X = CoerceOffset(_offset.X, ExtentWidth, ViewportWidth);
        _offset.Y = CoerceOffset(_offset.Y, ExtentHeight, ViewportHeight);

        if (changed)
            ScrollOwner?.InvalidateScrollInfo();
    }

    private static double CoerceOffset(double value, double extent, double viewport) =>
        Math.Max(0d, Math.Min(value, Math.Max(0d, extent - viewport)));

    private static bool AreClose(double left, double right) => Math.Abs(left - right) < 0.1d;

    private int IndexFromContainer(UIElement element) =>
        ItemsControl.GetItemsOwner(this)?.ItemContainerGenerator.IndexFromContainer(element) ?? -1;

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(VerticalOffset - Math.Max(20d, ItemHeight / 4d));
    public void LineDown() => SetVerticalOffset(VerticalOffset + Math.Max(20d, ItemHeight / 4d));
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - Math.Max(48d, ItemHeight / 2d));
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + Math.Max(48d, ItemHeight / 2d));
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void PageLeft() { }
    public void PageRight() { }

    public void SetHorizontalOffset(double offset)
    {
        if (!CanHorizontallyScroll)
            offset = 0;
        var coerced = CoerceOffset(offset, ExtentWidth, ViewportWidth);
        if (AreClose(coerced, _offset.X))
            return;
        _offset.X = coerced;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateArrange();
    }

    public void SetVerticalOffset(double offset)
    {
        var coerced = CoerceOffset(offset, ExtentHeight, ViewportHeight);
        if (AreClose(coerced, _offset.Y))
            return;
        _offset.Y = coerced;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is UIElement element)
        {
            var index = IndexFromContainer(element);
            if (index >= 0)
                BringIndexIntoView(index);
        }
        return rectangle;
    }
}
