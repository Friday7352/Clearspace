using System.Windows;
using System.Windows.Controls;

namespace Clearspace.Controls;

public sealed class SquareViewport : Decorator
{
    protected override Size MeasureOverride(Size constraint)
    {
        var side = Math.Min(constraint.Width, constraint.Height);
        if (double.IsInfinity(side)) side = 400;
        Child?.Measure(new Size(side, side));
        return new Size(side, side);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var side = Math.Min(size.Width, size.Height);
        Child?.Arrange(new Rect((size.Width - side) / 2, 0, side, side));
        return size;
    }
}
