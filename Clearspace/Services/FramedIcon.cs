// Clearspace | FIXED (tiny tile icons): icons Windows hands back small inside a big, empty picture.
//
// A tile asks Windows for a file's icon at the tile's size. When the program that owns the file type only
// ships small icons (16 to 48 pixels: common for archive tools and older programs), Windows does not
// enlarge one. It returns a picture of the size asked for that is empty apart from a thin frame at its
// edge and the small icon in the middle. In a tile that showed as a speck inside a square (ZIP files
// opened by such a tool, for example).
//
// Find spots that case from the pixels alone and cuts the icon out; TryEnlarge then either draws it at a
// useful size, or says it is too small to enlarge without turning to mush, in which case the tile uses
// Clearspace's own drawn page with the type's name on it (ScalableIconService.File), as it already does
// for types Windows has no icon for. Used by ThumbnailService.GetShellIcon.
//
// Deliberately cautious: only a picture whose background is see-through counts. If Windows ever fills the
// frame with a colour, nothing is recognised and the tile simply looks as it did before.

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clearspace.Services;

internal static class FramedIcon
{
    /// <summary>Pictures smaller than this are small icons shown small; there is nothing to rescue.</summary>
    internal const int MinSide = 64;

    /// <summary>An icon taking up no more than this share of the picture's side counts as "small in a frame".</summary>
    internal const double TinyShare = 0.42;

    /// <summary>An icon at least this many pixels across still looks right enlarged to a tile.</summary>
    internal const int SharpEnough = 40;

    private const int FaintAlpha = 40;        // the background may be this visible at most
    private const int SolidAlpha = 48;        // ... and the icon's pixels at least this much more
    private const double Fill = 0.84;         // how much of the tile picture the enlarged icon takes
    private const double MaxEnlargement = 4;  // for icons that are enlarged whatever their size

    /// <summary>The small icon cut out of its picture: 32-bit pixels (blue, green, red, alpha), row by row.</summary>
    internal readonly record struct Found(int[] Pixels, int Width, int Height);

    /// <summary>
    /// Looks for a small icon in an otherwise empty picture. <paramref name="pixels"/> are 32-bit, alpha in
    /// the top byte. Null when the picture is an ordinary icon (it fills its space), is not see-through
    /// around the middle, or is small itself.
    /// </summary>
    internal static Found? Find(int[] pixels, int width, int height)
    {
        var side = Math.Min(width, height);
        if (side < MinSide || pixels.Length < width * height)
            return null;

        // Four points well away from the middle and from the frame: all of them must be (nearly) empty.
        var background = 0;
        foreach (var (x, y) in new[] { (width / 8, height / 8), (width * 7 / 8, height / 8), (width / 8, height * 7 / 8), (width * 7 / 8, height * 7 / 8) })
        {
            var alpha = Alpha(pixels[y * width + x]);
            if (alpha > FaintAlpha)
                return null;
            background = Math.Max(background, alpha);
        }

        // The box around everything that is clearly drawn, leaving out a margin for the frame at the edge.
        var solid = background + SolidAlpha;
        var edge = Math.Max(2, side / 16);
        int left = width, top = height, right = -1, bottom = -1;

        for (var y = edge; y < height - edge; y++)
        {
            var row = y * width;
            for (var x = edge; x < width - edge; x++)
            {
                if (Alpha(pixels[row + x]) < solid)
                    continue;

                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }

        if (right < 0)
            return null; // nothing drawn at all

        var longest = Math.Max(right - left + 1, bottom - top + 1);
        if (longest < 8 || longest > side * TinyShare)
            return null; // a speck of noise, or an icon that fills its picture as it should

        // One more pixel all round, for the soft edge the test above left out.
        left = Math.Max(0, left - 1);
        top = Math.Max(0, top - 1);
        right = Math.Min(width - 1, right + 1);
        bottom = Math.Min(height - 1, bottom + 1);

        var iconWidth = right - left + 1;
        var iconHeight = bottom - top + 1;
        var icon = new int[iconWidth * iconHeight];
        for (var y = 0; y < iconHeight; y++)
            Array.Copy(pixels, (top + y) * width + left, icon, y * iconWidth, iconWidth);

        return new Found(icon, iconWidth, iconHeight);
    }

    private static int Alpha(int pixel) => (pixel >> 24) & 0xFF;

    /// <summary>
    /// False: the picture is fine as it is. True: it was a small icon in a frame, and
    /// <paramref name="enlarged"/> is the picture to show instead - or null when the icon is too small to
    /// enlarge well and <paramref name="evenIfBlurry"/> is off (the caller then shows something else).
    /// <paramref name="size"/> is the size the tile asked for. Draws, so call it on an STA thread (the
    /// thumbnail worker is one).
    /// </summary>
    internal static bool TryEnlarge(BitmapSource source, int size, bool evenIfBlurry, out ImageSource? enlarged)
    {
        enlarged = null;

        try
        {
            var width = source.PixelWidth;
            var height = source.PixelHeight;
            var side = Math.Min(width, height);
            if (side < MinSide)
                return false;

            // Read the pixels in the format the picture is in (or plain 32-bit when it is neither kind),
            // and build the cut-out in the same one, so colours come out exactly as they went in.
            var format = source.Format == PixelFormats.Pbgra32 ? PixelFormats.Pbgra32 : PixelFormats.Bgra32;
            BitmapSource readable = source.Format == format ? source : new FormatConvertedBitmap(source, format, null, 0);
            var pixels = new int[width * height];
            readable.CopyPixels(pixels, width * 4, 0);

            if (Find(pixels, width, height) is not { } found)
                return false;

            var longest = Math.Max(found.Width, found.Height);
            if (!evenIfBlurry && longest < SharpEnough)
                return true; // framed, but too small: enlarged stays null

            var canvas = Math.Clamp(size, 48, side);
            var scale = canvas * Fill / longest;
            if (evenIfBlurry)
                scale = Math.Min(scale, MaxEnlargement);

            var icon = BitmapSource.Create(found.Width, found.Height, 96, 96, format, null, found.Pixels, found.Width * 4);
            icon.Freeze();

            var drawnWidth = found.Width * scale;
            var drawnHeight = found.Height * scale;
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var drawing = visual.RenderOpen())
                drawing.DrawImage(icon, new Rect((canvas - drawnWidth) / 2, (canvas - drawnHeight) / 2, drawnWidth, drawnHeight));

            var result = new RenderTargetBitmap(canvas, canvas, 96, 96, PixelFormats.Pbgra32);
            result.Render(visual);
            result.Freeze();
            enlarged = result;
            return true;
        }
        catch (Exception)
        {
            enlarged = null;
            return false; // the picture stays as Windows gave it
        }
    }
}
