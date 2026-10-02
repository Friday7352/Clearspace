// Clearspace | File and folder icons drawn by the app for the experimental themes.
// NEW (experimental themes): normally Clearspace shows Windows' own icons. An experimental theme can ask
// for one of the icon styles below instead; every file and folder then gets a small vector drawing chosen
// by what kind of thing it is (folder, picture, music, program ...). The drawings scale to any size, so the
// same one serves the 16 px details row and a large tile. Photos and videos still show their real picture
// in tile view. IconService, ScalableIconService and ThumbnailService ask For(item) first and fall back to
// the Windows icon when it returns null (the normal themes, or a drawing that failed).
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Clearspace.Models;

namespace Clearspace.Services;

public enum IconStyle
{
    System,   // Windows' own icons (every normal theme)
    Pixel,    // 16 x 16 pixel art in colour
    OneBit,   // the same pixel art in black and white with dithering
    OneBitDark, // NEW (1-bit Mac dark): the black-and-white pixel art with black and white swapped
    Ink,      // NEW (e-reader): outlines in the theme's ink colour over a grey wash, like an e-ink screen
    Line,     // one-colour line drawings
    Neon,     // glowing coloured line drawings
    Sketch,   // pencil outlines with a coloured-pencil fill
    Tile,     // a flat coloured tile with the file extension written on it
    Dot,      // just a small coloured dot
    Mark      // a single bold mark per kind, in old PC colours
}

internal enum IconKind { Folder, Drive, Document, Image, Video, Audio, Archive, Code, App, Other }

internal static class ThemeIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.Ordinal);

    // True while the active theme draws its own icons.
    internal static bool Active => ThemeService.Icons != IconStyle.System;

    // Forget every drawing (the theme changed, and some styles take their colour from the theme).
    internal static void Reset() => Cache.Clear();

    // The active theme's icon for this file or folder, or null when the theme uses Windows' icons.
    internal static ImageSource? For(FileSystemItem item)
    {
        var style = ThemeService.Icons;
        if (style == IconStyle.System)
            return null;

        var kind = KindOf(item);
        var label = style == IconStyle.Tile ? LabelFor(item, kind) : string.Empty;   // only tiles carry text
        var key = $"{(int)style}|{(int)kind}|{label}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        ImageSource icon;
        try { icon = Draw(style, kind, label); }
        catch (Exception) { return null; }   // fall back to the Windows icon rather than fail a folder listing

        if (Cache.Count > 4096)
            Cache.Clear();
        Cache[key] = icon;
        return icon;
    }

    // ---------------------------------------------------------------- what kind of file is it

    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".rtf", ".pdf", ".doc", ".docx", ".odt", ".xls", ".xlsx", ".ods", ".csv", ".ppt", ".pptx",
        ".odp", ".epub", ".mobi", ".log", ".tex", ".pages", ".numbers", ".key", ".one"
    };

    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".zst", ".cab", ".iso", ".img", ".tgz", ".jar", ".cslock"
    };

    private static readonly HashSet<string> CodeFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".csproj", ".sln", ".js", ".ts", ".jsx", ".tsx", ".py", ".java", ".c", ".h", ".cpp", ".hpp",
        ".rs", ".go", ".rb", ".php", ".html", ".htm", ".css", ".scss", ".json", ".xml", ".yml", ".yaml", ".toml",
        ".ini", ".cfg", ".sql", ".sh", ".ps1", ".lua", ".kt", ".swift", ".shader", ".unity", ".gd"
    };

    private static readonly HashSet<string> Apps = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".bat", ".cmd", ".com", ".lnk", ".dll", ".sys", ".scr", ".apk"
    };

    internal static IconKind KindOf(FileSystemItem item)
    {
        if (item.IsDriveRoot) return IconKind.Drive;
        if (item.IsFolder) return IconKind.Folder;

        var extension = item.Extension;
        if (string.IsNullOrEmpty(extension)) return IconKind.Other;
        if (MediaTypes.IsImage(extension)) return IconKind.Image;
        if (MediaTypes.IsVideo(extension)) return IconKind.Video;
        if (MediaTypes.IsAudio(extension)) return IconKind.Audio;
        if (Documents.Contains(extension)) return IconKind.Document;
        if (Archives.Contains(extension)) return IconKind.Archive;
        if (CodeFiles.Contains(extension)) return IconKind.Code;
        if (Apps.Contains(extension)) return IconKind.App;
        return IconKind.Other;
    }

    // The text on a tile: the extension (up to four letters), or the drive letter.
    private static string LabelFor(FileSystemItem item, IconKind kind)
    {
        if (kind == IconKind.Folder) return string.Empty;
        if (kind == IconKind.Drive)
            return item.FullPath.Length >= 2 && item.FullPath[1] == ':' ? item.FullPath[..2].ToUpperInvariant() : string.Empty;

        var text = item.Extension.TrimStart('.').ToUpperInvariant();
        return text.Length > 4 ? text[..4] : text;
    }

    // ---------------------------------------------------------------- colours per kind

    private static Color Rgb(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static SolidColorBrush Fill(Color color, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static Pen Stroke(Color color, double thickness, byte alpha = 255)
    {
        var pen = new Pen(Fill(color, alpha), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    // Everyday colours (pixel art, tiles, dots, sketches).
    private static Color KindColor(IconKind kind) => Rgb(kind switch
    {
        IconKind.Folder => 0xF2B33D,
        IconKind.Drive => 0x8E9BB0,
        IconKind.Document => 0x4F86E8,
        IconKind.Image => 0x3FA66B,
        IconKind.Video => 0xE5533D,
        IconKind.Audio => 0xB45FD6,
        IconKind.Archive => 0xC98A3A,
        IconKind.Code => 0x2FB8A6,
        IconKind.App => 0x5B6EE1,
        _ => 0x8A8F98
    });

    // Bright colours for the neon style.
    private static Color NeonColor(IconKind kind) => Rgb(kind switch
    {
        IconKind.Folder => 0xFFE14D,
        IconKind.Drive => 0x8FA6D8,
        IconKind.Document => 0x00F0FF,
        IconKind.Image => 0x39FFB0,
        IconKind.Video => 0xFF4D6D,
        IconKind.Audio => 0xC77DFF,
        IconKind.Archive => 0xFF9F1C,
        IconKind.Code => 0x7CFF6B,
        IconKind.App => 0xFF2BD6,
        _ => 0xEAF6FF
    });

    // The sixteen-colour PC palette for the DOS style.
    private static Color PcColor(IconKind kind) => Rgb(kind switch
    {
        IconKind.Folder => 0xFFFFFF,
        IconKind.Drive => 0xAAAAAA,
        IconKind.Document => 0x55FFFF,
        IconKind.Image => 0xFFFF55,
        IconKind.Video => 0xFF5555,
        IconKind.Audio => 0xFF55FF,
        IconKind.Archive => 0xFFAA00,
        IconKind.Code => 0xAAFFAA,
        IconKind.App => 0x55FF55,
        _ => 0xAAAAAA
    });

    // ---------------------------------------------------------------- drawing

    private static ImageSource Draw(IconStyle style, IconKind kind, string label)
    {
        var group = new DrawingGroup();
        // A see-through 16 x 16 square first, so every icon has the same box and lines up the same way.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));

        switch (style)
        {
            case IconStyle.Pixel:
                RenderOptions.SetEdgeMode(group, EdgeMode.Aliased);   // hard pixel edges, no smoothing
                DrawPixels(group, kind, oneBit: false);
                break;
            case IconStyle.OneBit:
                RenderOptions.SetEdgeMode(group, EdgeMode.Aliased);
                DrawPixels(group, kind, oneBit: true);
                break;
            case IconStyle.OneBitDark:
                RenderOptions.SetEdgeMode(group, EdgeMode.Aliased);
                DrawPixels(group, kind, oneBit: true, inverted: true);
                break;
            case IconStyle.Ink:
                DrawInk(group, kind);
                break;
            case IconStyle.Line:
                group.Children.Add(new GeometryDrawing(null, Stroke(ThemeService.Ink.Color, 1.1), Outline(kind)));
                break;
            case IconStyle.Neon:
                // A wide faint stroke under a thin bright one reads as a glow, with no blur effect to pay for.
                group.Children.Add(new GeometryDrawing(null, Stroke(NeonColor(kind), 3.4, 0x38), Outline(kind)));
                group.Children.Add(new GeometryDrawing(null, Stroke(NeonColor(kind), 2.0, 0x55), Outline(kind)));
                group.Children.Add(new GeometryDrawing(null, Stroke(NeonColor(kind), 1.0), Outline(kind)));
                break;
            case IconStyle.Sketch:
                DrawSketch(group, kind);
                break;
            case IconStyle.Tile:
                DrawTile(group, kind, label);
                break;
            case IconStyle.Dot:
                DrawDot(group, kind);
                break;
            case IconStyle.Mark:
                DrawMark(group, kind);
                break;
        }

        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    // ----- line drawings, on a 16 x 16 grid

    private const string FolderPath = "M1.5,4 L6,4 L7.5,5.8 L14.5,5.8 L14.5,13 L1.5,13 Z";
    private const string DrivePath = "M1.5,5.5 L14.5,5.5 L14.5,11.5 L1.5,11.5 Z";
    private const string DriveLight = "M11,8.5 L12.5,8.5";
    private const string PagePath = "M4,1.5 L9.5,1.5 L12.5,4.5 L12.5,14.5 L4,14.5 Z";
    private const string PageFold = "M9.5,1.5 L9.5,4.5 L12.5,4.5";

    // What is drawn on the page for each kind of file (inside x 5.5-11, y 7-12.5); null = a blank page.
    private static string? Emblem(IconKind kind) => kind switch
    {
        IconKind.Document => "M6,7.5 L10.5,7.5 M6,9.5 L10.5,9.5 M6,11.5 L9,11.5",
        IconKind.Image => "M5.5,12.5 L7.5,9.5 L9,11 L10,10 L11.5,12.5 Z M10.9,7.8 A0.8,0.8 0 1 1 9.3,7.8 A0.8,0.8 0 1 1 10.9,7.8 Z",
        IconKind.Video => "M7,7.5 L10.5,9.75 L7,12 Z",
        IconKind.Audio => "M7.6,11.4 L7.6,7.6 L10.6,7 L10.6,10.8 M7.6,11.4 A0.9,0.9 0 1 1 5.8,11.4 A0.9,0.9 0 1 1 7.6,11.4 Z M10.6,10.8 A0.9,0.9 0 1 1 8.8,10.8 A0.9,0.9 0 1 1 10.6,10.8 Z",
        IconKind.Archive => "M8.25,6.5 L8.25,12.5 M7,7.5 L9.5,7.5 M7,9.5 L9.5,9.5 M7,11.5 L9.5,11.5",
        IconKind.Code => "M7.4,8 L5.7,9.75 L7.4,11.5 M9.1,8 L10.8,9.75 L9.1,11.5",
        IconKind.App => "M5.5,7.5 L11,7.5 L11,12.5 L5.5,12.5 Z M5.5,9 L11,9",
        _ => null
    };

    // The closed outer shape of an icon (what a fill goes into).
    private static Geometry Body(IconKind kind) => Geometry.Parse(kind switch
    {
        IconKind.Folder => FolderPath,
        IconKind.Drive => DrivePath,
        _ => PagePath
    });

    // The inner lines of an icon, or null when it has none.
    private static Geometry? Detail(IconKind kind)
    {
        if (kind == IconKind.Folder) return null;
        if (kind == IconKind.Drive) return Geometry.Parse(DriveLight);

        var detail = new GeometryGroup();
        detail.Children.Add(Geometry.Parse(PageFold));
        if (Emblem(kind) is { } emblem)
            detail.Children.Add(Geometry.Parse(emblem));
        return detail;
    }

    // Body and inner lines together, for the styles that only stroke.
    private static Geometry Outline(IconKind kind)
    {
        var outline = new GeometryGroup();
        outline.Children.Add(Body(kind));
        if (Detail(kind) is { } detail)
            outline.Children.Add(detail);
        return outline;
    }

    // A pencil sketch: coloured-pencil hatching inside the shape (over a faint wash of the same colour), the
    // outline in graphite, then the outline again, lighter and slightly turned, the way a second pass of the
    // pencil never lands on the first.
    // CHANGED (sketchbook, round 2): hatched shading replaces the flat see-through fill, and the second
    // outline is turned a little instead of only shifted.
    private static void DrawSketch(DrawingGroup group, IconKind kind)
    {
        // CHANGED (dark sketchbook): graphite on light paper, chalk-white pencil on black paper.
        var pencil = ThemeService.IsDark ? Rgb(0xE8E5DC) : Rgb(0x3A3A44);
        var colour = KindColor(kind);

        // Diagonal strokes across the whole box, cut to the icon's outer shape.
        var hatch = new GeometryGroup();
        for (var start = -14.0; start < 16; start += 2.1)
            hatch.Children.Add(new LineGeometry(new Point(start, 16), new Point(start + 14, 0)));

        var shading = new DrawingGroup { ClipGeometry = Body(kind) };
        shading.Children.Add(new GeometryDrawing(Fill(colour, 0x2E), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        shading.Children.Add(new GeometryDrawing(null, Stroke(colour, 0.75, 0xD0), hatch));
        group.Children.Add(shading);

        group.Children.Add(new GeometryDrawing(null, Stroke(pencil, 0.9), Outline(kind)));

        var second = Outline(kind);
        second.Transform = new RotateTransform(-2.2, 8, 8);
        group.Children.Add(new GeometryDrawing(null, Stroke(pencil, 0.5, 0x90), second));
    }

    // NEW (e-reader): an e-ink icon has no colour, only the ink at different strengths. The shape is washed
    // with a light grey (a different strength for each kind, so kinds can still be told apart) and outlined
    // in full ink. The ink is the active theme's text colour.
    private static void DrawInk(DrawingGroup group, IconKind kind)
    {
        var ink = ThemeService.Ink.Color;
        var wash = (byte)(kind switch
        {
            IconKind.Folder => 0x50,
            IconKind.Drive => 0x40,
            IconKind.App => 0x66,
            IconKind.Video => 0x5A,
            IconKind.Archive => 0x48,
            IconKind.Audio => 0x3A,
            IconKind.Image => 0x30,
            IconKind.Code => 0x26,
            IconKind.Document => 0x16,
            _ => 0x0C
        });

        group.Children.Add(new GeometryDrawing(Fill(ink, wash), Stroke(ink, 1.0), Body(kind)));
        if (Detail(kind) is { } detail)
            group.Children.Add(new GeometryDrawing(null, Stroke(ink, 0.9), detail));
    }

    // A flat coloured tile with the extension on it; folders are a flat folder shape, drives show their letter.
    private static void DrawTile(DrawingGroup group, IconKind kind, string label)
    {
        var color = KindColor(kind);
        Geometry shape = kind == IconKind.Folder
            ? Geometry.Parse("M1,3.5 L6.2,3.5 L7.8,5.3 L15,5.3 L15,13.5 L1,13.5 Z")
            : new RectangleGeometry(new Rect(1, 1, 14, 14), 3, 3);
        group.Children.Add(new GeometryDrawing(Fill(color), null, shape));

        if (label.Length == 0)
            return;

        var size = label.Length switch { 1 => 9.0, 2 => 7.4, 3 => 5.6, _ => 4.5 };
        var text = new FormattedText(
            label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            size,
            Brushes.White,
            1);
        var letters = text.BuildGeometry(new Point(8 - text.Width / 2, 8 - text.Height / 2));
        group.Children.Add(new GeometryDrawing(Brushes.White, null, letters));
    }

    // A coloured dot. Folders are a ring and drives a square, so the three can be told apart without colour.
    private static void DrawDot(DrawingGroup group, IconKind kind)
    {
        var color = KindColor(kind);
        if (kind == IconKind.Folder)
            group.Children.Add(new GeometryDrawing(null, Stroke(color, 1.6), new EllipseGeometry(new Point(8, 8), 3.3, 3.3)));
        else if (kind == IconKind.Drive)
            group.Children.Add(new GeometryDrawing(Fill(color), null, new RectangleGeometry(new Rect(4.75, 4.75, 6.5, 6.5))));
        else
            group.Children.Add(new GeometryDrawing(Fill(color), null, new EllipseGeometry(new Point(8, 8), 3, 3)));
    }

    // One bold mark per kind: a solid arrow for folders, and the file emblems drawn large without their page.
    private static void DrawMark(DrawingGroup group, IconKind kind)
    {
        var color = PcColor(kind);
        if (kind == IconKind.Folder)
        {
            group.Children.Add(new GeometryDrawing(Fill(color), null, Geometry.Parse("M4,3 L13,8 L4,13 Z")));
            return;
        }

        if (kind == IconKind.Drive)
        {
            group.Children.Add(new GeometryDrawing(null, Stroke(color, 1.5), Outline(kind)));
            return;
        }

        if (Emblem(kind) is not { } emblem)
        {
            group.Children.Add(new GeometryDrawing(Fill(color), null, new RectangleGeometry(new Rect(6.5, 6.5, 3, 3))));
            return;
        }

        // The emblem sits in the lower middle of the page; double it and move its centre to the icon's centre.
        var mark = Geometry.Parse(emblem).Clone();
        mark.Transform = new MatrixTransform(2, 0, 0, 2, -8.5, -11.5);
        group.Children.Add(new GeometryDrawing(null, Stroke(color, 1.6), mark));
    }

    // ----- pixel art, 16 x 16
    // k = outline, w = paper, f = body colour, s = body shade, a = emblem colour, b = second emblem colour,
    // c = small detail (the drive light), . = see-through.

    private static readonly string[] FolderMap =
    [
        "................",
        "................",
        ".kkkkkk.........",
        "kffffffk........",
        "kfffffffkkkkkkk.",
        "kffffffffffffffk",
        "kssssssssssssssk",
        "kffffffffffffffk",
        "kffffffffffffffk",
        "kffffffffffffffk",
        "kffffffffffffffk",
        "kffffffffffffffk",
        "kssssssssssssssk",
        ".kkkkkkkkkkkkkk.",
        "................",
        "................"
    ];

    private static readonly string[] DriveMap =
    [
        "................",
        "................",
        "................",
        "................",
        ".kkkkkkkkkkkkkk.",
        "kffffffffffffffk",
        "kffffffffffffffk",
        "kffffffffffffffk",
        "kssssssssssssssk",
        "kfffffffffffccfk",
        "kffffffffffffffk",
        ".kkkkkkkkkkkkkk.",
        "................",
        "................",
        "................",
        "................"
    ];

    private static readonly string[] PageMap =
    [
        "................",
        "..kkkkkkkk......",
        "..kwwwwwwkk.....",
        "..kwwwwwwkwk....",
        "..kwwwwwwkkkk...",
        "..kwwwwwwwwwk...",
        "..kwwwwwwwwwk...",
        "..kwwwwwwwwwk...",
        "..kwwwwwwwwwk...",
        "..kwwwwwwwwwk...",
        "..kwwwwwwwwwk...",
        "..kwwwwwwwwwk...",
        "..kwwwwwwwwwk...",
        "..kwwwwwwwwwk...",
        "..kkkkkkkkkkk...",
        "................"
    ];

    // 7 x 7 emblems stamped onto the page at column 4, row 6 ('.' leaves the paper showing).
    private static string[]? EmblemMap(IconKind kind) => kind switch
    {
        IconKind.Document => new[] { "aaaaaaa", ".......", "aaaaa..", ".......", "aaaaaaa", ".......", "aaaa..." },
        IconKind.Image => new[] { ".....aa", ".....aa", "..b....", ".bbb..b", "bbbbbbb", "bbbbbbb", "bbbbbbb" },
        IconKind.Video => new[] { "a......", "aaa....", "aaaaa..", "aaaaaaa", "aaaaa..", "aaa....", "a......" },
        IconKind.Audio => new[] { "..aaaaa", "..a...a", "..a...a", "..a...a", "aaa.aaa", "aaa.aaa", ".a...a." },
        IconKind.Archive => new[] { "..aa...", "...aa..", "..aa...", "...aa..", "..aa...", ".aaaa..", ".aaaa.." },
        IconKind.Code => new[] { ".......", "..a.a..", ".a...a.", "a.....a", ".a...a.", "..a.a..", "......." },
        IconKind.App => new[] { "aaaaaaa", "aaaaaaa", "a.....a", "a.....a", "a.....a", "a.....a", "aaaaaaa" },
        _ => null
    };

    // CHANGED (1-bit Mac dark): "inverted" swaps black and white in the one-bit version.
    private static void DrawPixels(DrawingGroup group, IconKind kind, bool oneBit, bool inverted = false)
    {
        var rows = kind switch { IconKind.Folder => FolderMap, IconKind.Drive => DriveMap, _ => PageMap };
        var emblem = (kind is IconKind.Folder or IconKind.Drive) ? null : EmblemMap(kind);

        var black = Rgb(inverted ? 0xFFFFFFu : 0x000000u);
        var white = Rgb(inverted ? 0x000000u : 0xFFFFFFu);
        var main = KindColor(kind);
        var shade = Color.FromRgb((byte)(main.R * 3 / 4), (byte)(main.G * 3 / 4), (byte)(main.B * 3 / 4));
        var second = kind == IconKind.Image ? main : shade;                 // a picture's hills
        var first = kind == IconKind.Image ? Rgb(0xF2B33D) : main;          // a picture's sun

        var pixels = new Dictionary<Color, GeometryGroup>();
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var code = y < rows.Length && x < rows[y].Length ? rows[y][x] : '.';
                if (emblem is not null && y >= 6 && y - 6 < emblem.Length && x >= 4 && x - 4 < emblem[y - 6].Length
                    && emblem[y - 6][x - 4] != '.')
                    code = emblem[y - 6][x - 4];
                if (code == '.')
                    continue;

                var dither = (x + y) % 2 == 0 ? black : white;   // every other pixel: the 1-bit way to make grey
                var color = code switch
                {
                    'k' => oneBit ? black : Rgb(0x1B1B1F),
                    'w' => oneBit ? white : Rgb(0xF6F6F2),
                    'f' => oneBit ? white : main,
                    's' => oneBit ? dither : shade,
                    'a' => oneBit ? black : first,
                    'b' => oneBit ? dither : second,
                    'c' => oneBit ? black : Rgb(0x3BE37A),
                    _ => black
                };

                if (!pixels.TryGetValue(color, out var cells))
                {
                    cells = new GeometryGroup { FillRule = FillRule.Nonzero };
                    pixels[color] = cells;
                }
                cells.Children.Add(new RectangleGeometry(new Rect(x, y, 1, 1)));
            }
        }

        foreach (var (color, cells) in pixels)
            group.Children.Add(new GeometryDrawing(Fill(color), null, cells));
    }
}
