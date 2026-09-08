// Clearspace | Image rotation and crop operations.

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clearspace.Services;

public static class ImageEditService
{
    public static bool CanSaveInPlace(string path)
        => CreateEncoder(Path.GetExtension(path)) is not null;

    public static BitmapSource Rotate(BitmapSource source, int degrees)
    {
        var normalised = ((degrees % 360) + 360) % 360;

        if (normalised == 0)
            return source;

        var rotated = new TransformedBitmap(source, new RotateTransform(normalised));
        rotated.Freeze();
        return rotated;
    }

    public static BitmapSource? Crop(BitmapSource source, Int32Rect region)
    {
        var x = Math.Clamp(region.X, 0, source.PixelWidth - 1);
        var y = Math.Clamp(region.Y, 0, source.PixelHeight - 1);
        var width = Math.Clamp(region.Width, 1, source.PixelWidth - x);
        var height = Math.Clamp(region.Height, 1, source.PixelHeight - y);

        if (width < 2 || height < 2)
            return null;

        var cropped = new CroppedBitmap(source, new Int32Rect(x, y, width, height));
        cropped.Freeze();
        return cropped;
    }

    public static string? Save(BitmapSource image, string path)
    {
        var encoder = CreateEncoder(Path.GetExtension(path));

        if (encoder is null)
            return $"Clearspace cannot write {Path.GetExtension(path)} files. Save a copy as PNG or JPEG instead.";

        encoder.Frames.Add(BitmapFrame.Create(image));

        var temporary = path + ".clearspace-tmp";

        try
        {
            using (var stream = File.Create(temporary))
                encoder.Save(stream);

            if (File.Exists(path))
            {
                File.Move(temporary, path, overwrite: true);
            }
            else
            {
                File.Move(temporary, path);
            }

            return null;
        }
        catch (Exception exception)
        {
            TryDelete(temporary);
            return exception.Message;
        }
    }

    public static string NextAvailableCopyPath(string original)
    {
        var directory = Path.GetDirectoryName(original) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(original);
        var extension = Path.GetExtension(original);

        if (CreateEncoder(extension) is null)
            extension = ".png";

        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    public static bool CopyToClipboard(BitmapSource image)
    {
        try
        {
            Clipboard.SetImage(image);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static BitmapEncoder? CreateEncoder(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" => new JpegBitmapEncoder { QualityLevel = 95 },
        ".png" => new PngBitmapEncoder(),
        ".bmp" => new BmpBitmapEncoder(),
        ".gif" => new GifBitmapEncoder(),
        ".tif" or ".tiff" => new TiffBitmapEncoder(),
        ".wmp" or ".jxr" => new WmpBitmapEncoder(),
        _ => null
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
