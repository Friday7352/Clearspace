// Clearspace | Vector icons used by the interface.

using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Clearspace.Models;

namespace Clearspace.Services;

internal static class ScalableIconService
{
    private static readonly Lazy<ImageSource> VideoIcon = new(CreateVideoIcon);
    private static readonly Lazy<ImageSource> ImageIcon = new(CreateImageIcon);
    private static readonly ConcurrentDictionary<string, ImageSource> FileIcons = new(StringComparer.OrdinalIgnoreCase);

    internal static ImageSource Video => VideoIcon.Value;
    internal static ImageSource Image => ImageIcon.Value;

    internal static ImageSource File(string extension)
    {
        var key = string.IsNullOrWhiteSpace(extension) ? "FILE" : extension.TrimStart('.').ToUpperInvariant();
        if (key.Length > 4)
            key = key[..4];
        return FileIcons.GetOrAdd(key, CreateFileIcon);
    }

    internal static void PopulateGridPlaceholders(IReadOnlyList<FileSystemItem> items)
    {
        foreach (var item in items)
        {
            if (item.IsFolder)
            {
                if (FolderIconService.HasType(item))
                    item.GridPlaceholder = FolderIconService.AddTypeBadge(item, IconService.GetLargeIcon(item));
                continue;
            }

            item.GridPlaceholder = MediaTypes.IsImage(item.Extension)
                ? Image
                : MediaTypes.IsVideo(item.Extension)
                    ? Video
                    : File(item.Extension);
        }
    }

    private static ImageSource CreateVideoIcon()
    {
        var drawing = new DrawingGroup();

        var page = new StreamGeometry();
        using (var context = page.Open())
        {
            context.BeginFigure(new Point(.17, .06), true, true);
            context.LineTo(new Point(.68, .06), true, false);
            context.LineTo(new Point(.88, .26), true, false);
            context.LineTo(new Point(.88, .94), true, false);
            context.LineTo(new Point(.17, .94), true, false);
        }
        page.Freeze();

        drawing.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(242, 242, 240)),
            new Pen(new SolidColorBrush(Color.FromRgb(154, 156, 158)), .025),
            page));

        var fold = new StreamGeometry();
        using (var context = fold.Open())
        {
            context.BeginFigure(new Point(.68, .06), true, true);
            context.LineTo(new Point(.68, .26), true, false);
            context.LineTo(new Point(.88, .26), true, false);
        }
        fold.Freeze();
        drawing.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(205, 207, 209)),
            new Pen(new SolidColorBrush(Color.FromRgb(154, 156, 158)), .018),
            fold));

        drawing.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(43, 45, 48)),
            new Pen(new SolidColorBrush(Color.FromRgb(232, 143, 74)), .055),
            new EllipseGeometry(new Point(.525, .58), .245, .245)));

        var play = new StreamGeometry();
        using (var context = play.Open())
        {
            context.BeginFigure(new Point(.46, .43), true, true);
            context.LineTo(new Point(.46, .73), true, false);
            context.LineTo(new Point(.69, .58), true, false);
        }
        play.Freeze();
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, play));

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static ImageSource CreateImageIcon()
    {
        var drawing = CreateDocumentBase();
        var frame = new RectangleGeometry(new Rect(.25, .37, .55, .38), .035, .035);
        drawing.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(43, 48, 50)),
            new Pen(new SolidColorBrush(Color.FromRgb(115, 121, 122)), .018),
            frame));
        drawing.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(236, 181, 76)),
            null,
            new EllipseGeometry(new Point(.66, .46), .065, .065)));

        var mountains = new StreamGeometry();
        using (var context = mountains.Open())
        {
            context.BeginFigure(new Point(.28, .70), true, true);
            context.LineTo(new Point(.43, .52), true, false);
            context.LineTo(new Point(.53, .63), true, false);
            context.LineTo(new Point(.62, .55), true, false);
            context.LineTo(new Point(.77, .70), true, false);
        }
        mountains.Freeze();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(110, 178, 132)), null, mountains));
        return Freeze(drawing);
    }

    private static ImageSource CreateFileIcon(string label)
    {
        var drawing = CreateDocumentBase();
        var palette = new[]
        {
            Color.FromRgb(87, 151, 205),
            Color.FromRgb(104, 166, 130),
            Color.FromRgb(198, 139, 79),
            Color.FromRgb(153, 126, 190)
        };
        var colorIndex = label.Aggregate(0, (sum, character) => sum + character) % palette.Length;
        var badge = new RectangleGeometry(new Rect(.22, .53, .61, .25), .055, .055);
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(palette[colorIndex]), null, badge));

        var text = new FormattedText(
            label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            .145,
            Brushes.White,
            1);
        var textGeometry = text.BuildGeometry(new Point(.525 - text.Width / 2, .655 - text.Height / 2));
        textGeometry.Freeze();
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, textGeometry));
        return Freeze(drawing);
    }

    private static DrawingGroup CreateDocumentBase()
    {
        var drawing = new DrawingGroup();
        var page = new StreamGeometry();
        using (var context = page.Open())
        {
            context.BeginFigure(new Point(.17, .06), true, true);
            context.LineTo(new Point(.68, .06), true, false);
            context.LineTo(new Point(.88, .26), true, false);
            context.LineTo(new Point(.88, .94), true, false);
            context.LineTo(new Point(.17, .94), true, false);
        }
        page.Freeze();
        drawing.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(242, 242, 240)),
            new Pen(new SolidColorBrush(Color.FromRgb(154, 156, 158)), .025),
            page));

        var fold = new StreamGeometry();
        using (var context = fold.Open())
        {
            context.BeginFigure(new Point(.68, .06), true, true);
            context.LineTo(new Point(.68, .26), true, false);
            context.LineTo(new Point(.88, .26), true, false);
        }
        fold.Freeze();
        drawing.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(205, 207, 209)),
            new Pen(new SolidColorBrush(Color.FromRgb(154, 156, 158)), .018),
            fold));
        return drawing;
    }

    private static ImageSource Freeze(DrawingGroup drawing)
    {
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
