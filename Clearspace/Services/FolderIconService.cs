// Clearspace | Folder icons and type badges.

using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Clearspace.Models;

namespace Clearspace.Services;

internal static class FolderIconService
{
    private enum FolderKind
    {
        Generic,
        Desktop,
        Documents,
        Downloads,
        Pictures,
        Music,
        Videos
    }

    internal static bool HasType(FileSystemItem item)
        => ResolveKind(item) != FolderKind.Generic;

    private static readonly ConcurrentDictionary<(FolderKind Kind, ImageSource Base), ImageSource> BadgeCache = new();

    internal static ImageSource? AddTypeBadge(FileSystemItem item, ImageSource? baseIcon)
    {
        var kind = ResolveKind(item);
        if (kind == FolderKind.Generic || baseIcon is null)
            return baseIcon;

        if (BadgeCache.TryGetValue((kind, baseIcon), out var reused))
            return reused;

        var drawing = new DrawingGroup();
        drawing.Children.Add(new ImageDrawing(baseIcon, new Rect(0, 0, 1, 1)));

        var (badge, ink) = kind switch
        {
            FolderKind.Desktop => (Color.FromRgb(82, 163, 213), Color.FromRgb(239, 250, 255)),
            FolderKind.Documents => (Color.FromRgb(115, 132, 154), Color.FromRgb(244, 248, 252)),
            FolderKind.Downloads => (Color.FromRgb(78, 159, 143), Color.FromRgb(239, 255, 251)),
            FolderKind.Pictures => (Color.FromRgb(73, 151, 186), Color.FromRgb(238, 250, 255)),
            FolderKind.Music => (Color.FromRgb(177, 96, 137), Color.FromRgb(255, 243, 249)),
            FolderKind.Videos => (Color.FromRgb(131, 103, 188), Color.FromRgb(248, 244, 255)),
            _ => (Color.FromRgb(100, 100, 100), Color.FromRgb(255, 255, 255))
        };

        drawing.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromArgb(95, 0, 0, 0)),
            null,
            new RectangleGeometry(new Rect(.635, .545, .265, .265), .052, .052)));
        drawing.Children.Add(new GeometryDrawing(
            new SolidColorBrush(badge),
            null,
            new RectangleGeometry(new Rect(.62, .53, .265, .265), .052, .052)));
        AddBadgeMark(drawing, kind, new SolidColorBrush(ink), new Point(.59, .52));

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();

        if (BadgeCache.Count > 64)
            BadgeCache.Clear();

        BadgeCache[(kind, baseIcon)] = image;
        return image;
    }

    private static FolderKind ResolveKind(FileSystemItem item)
    {
        if (!item.IsStandardFolder)
            return FolderKind.Generic;

        var saved = SettingsService.GetFolderViewProfile(item.FullPath);
        if (saved is not null && Enum.TryParse<DirectoryViewProfile>(saved, true, out var profile))
            return profile switch
            {
                DirectoryViewProfile.Desktop => FolderKind.Desktop,
                DirectoryViewProfile.Documents => FolderKind.Documents,
                DirectoryViewProfile.Downloads => FolderKind.Downloads,
                DirectoryViewProfile.Photos => FolderKind.Pictures,
                DirectoryViewProfile.Music => FolderKind.Music,
                DirectoryViewProfile.Videos => FolderKind.Videos,
                _ => FolderKind.Generic
            };

        if (SamePath(item.FullPath, KnownFolders.Desktop)) return FolderKind.Desktop;
        if (SamePath(item.FullPath, KnownFolders.Documents)) return FolderKind.Documents;
        if (SamePath(item.FullPath, KnownFolders.Downloads)) return FolderKind.Downloads;
        if (SamePath(item.FullPath, KnownFolders.Pictures)) return FolderKind.Pictures;
        if (SamePath(item.FullPath, KnownFolders.Music)) return FolderKind.Music;
        if (SamePath(item.FullPath, KnownFolders.Videos)) return FolderKind.Videos;

        return AutomaticFolderTypeDetector.DetectFromName(item.FullPath) switch
        {
            DirectoryViewProfile.Photos => FolderKind.Pictures,
            DirectoryViewProfile.Music => FolderKind.Music,
            _ => FolderKind.Generic
        };
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(left),
                Path.TrimEndingDirectorySeparator(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void AddBadgeMark(DrawingGroup drawing, FolderKind kind, Brush ink, Point origin)
    {
        var x = origin.X;
        var y = origin.Y;

        switch (kind)
        {
            case FolderKind.Desktop:
                drawing.Children.Add(new GeometryDrawing(ink, null, new RectangleGeometry(new Rect(x + .015, y + .045, .18, .11), .012, .012)));
                drawing.Children.Add(new GeometryDrawing(ink, null, new RectangleGeometry(new Rect(x + .085, y + .165, .04, .025), 0, 0)));
                break;

            case FolderKind.Documents:
                drawing.Children.Add(new GeometryDrawing(ink, null, new RectangleGeometry(new Rect(x + .05, y + .025, .12, .18), .01, .01)));
                drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(110, 60, 80, 105)), null, new RectangleGeometry(new Rect(x + .07, y + .07, .08, .014), 0, 0)));
                drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(110, 60, 80, 105)), null, new RectangleGeometry(new Rect(x + .07, y + .115, .08, .014), 0, 0)));
                break;

            case FolderKind.Downloads:
                var arrow = new StreamGeometry();
                using (var context = arrow.Open())
                {
                    context.BeginFigure(new Point(x + .105, y + .025), true, true);
                    context.LineTo(new Point(x + .105, y + .13), true, false);
                    context.LineTo(new Point(x + .06, y + .095), true, false);
                    context.LineTo(new Point(x + .105, y + .175), true, false);
                    context.LineTo(new Point(x + .15, y + .095), true, false);
                    context.LineTo(new Point(x + .105, y + .13), true, false);
                }
                arrow.Freeze();
                drawing.Children.Add(new GeometryDrawing(ink, null, arrow));
                drawing.Children.Add(new GeometryDrawing(ink, new Pen(ink, .018), new LineGeometry(new Point(x + .04, y + .195), new Point(x + .17, y + .195))));
                break;

            case FolderKind.Pictures:
                drawing.Children.Add(new GeometryDrawing(ink, null, new EllipseGeometry(new Point(x + .165, y + .045), .026, .026)));
                var mountains = new StreamGeometry();
                using (var context = mountains.Open())
                {
                    context.BeginFigure(new Point(x - .005, y + .18), true, true);
                    context.LineTo(new Point(x + .065, y + .095), true, false);
                    context.LineTo(new Point(x + .11, y + .14), true, false);
                    context.LineTo(new Point(x + .145, y + .105), true, false);
                    context.LineTo(new Point(x + .22, y + .18), true, false);
                }
                mountains.Freeze();
                drawing.Children.Add(new GeometryDrawing(ink, null, mountains));
                break;

            case FolderKind.Music:
                drawing.Children.Add(new GeometryDrawing(ink, new Pen(ink, .022), new LineGeometry(new Point(x + .11, y + .025), new Point(x + .11, y + .145))));
                drawing.Children.Add(new GeometryDrawing(ink, new Pen(ink, .022), new LineGeometry(new Point(x + .11, y + .025), new Point(x + .185, y + .045))));
                drawing.Children.Add(new GeometryDrawing(ink, null, new EllipseGeometry(new Point(x + .065, y + .16), .042, .032)));
                break;

            case FolderKind.Videos:
                var play = new StreamGeometry();
                using (var context = play.Open())
                {
                    context.BeginFigure(new Point(x + .065, y + .05), true, true);
                    context.LineTo(new Point(x + .065, y + .18), true, false);
                    context.LineTo(new Point(x + .175, y + .115), true, false);
                }
                play.Freeze();
                drawing.Children.Add(new GeometryDrawing(ink, null, play));
                break;
        }
    }
}
