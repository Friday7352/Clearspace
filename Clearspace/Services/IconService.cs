// Clearspace | Windows file icons and type names.

using System.Collections.Concurrent;
using System.IO;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clearspace.Models;
using Clearspace.Native;

namespace Clearspace.Services;

public static class IconService
{
    private static readonly ConcurrentDictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ImageSource?> LargeIconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> TypeNameCache = new(StringComparer.OrdinalIgnoreCase);

    private const string FolderKey = "\\__folder__";

    public static ImageSource? GetIcon(FileSystemItem item)
    {
        if (item.IsDriveRoot)
            return IconCache.GetOrAdd($"drive:{item.FullPath}", _ => LoadIcon(item.FullPath, isFolder: true, useAttributes: false, large: true));

        if (item.IsFolder)
        {
            var folder = IconCache.GetOrAdd(FolderKey, _ => LoadIcon(item.FullPath, isFolder: true, useAttributes: true));
            return FolderIconService.AddTypeBadge(item, folder);
        }

        var extension = item.Extension;

        if (string.IsNullOrEmpty(extension))
            return IconCache.GetOrAdd(".__none__", _ => LoadIcon("file", isFolder: false, useAttributes: true));

        return IconCache.GetOrAdd(extension, ext => LoadIcon("file" + ext, isFolder: false, useAttributes: true));
    }

    public static string GetTypeName(FileSystemItem item)
    {
        if (item.IsDriveRoot)
            return item.DriveKind ?? "Drive";

        if (item.IsFolder)
            return "File folder";

        var extension = item.Extension;
        if (string.IsNullOrEmpty(extension))
            return "File";

        return TypeNameCache.GetOrAdd(extension, ext =>
        {
            var info = new NativeMethods.SHFILEINFO();
            var result = NativeMethods.SHGetFileInfoW(
                "file" + ext,
                NativeMethods.FILE_ATTRIBUTE_NORMAL,
                ref info,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
                NativeMethods.SHGFI_TYPENAME | NativeMethods.SHGFI_USEFILEATTRIBUTES);

            if (result == IntPtr.Zero || string.IsNullOrWhiteSpace(info.szTypeName))
                return ext.TrimStart('.').ToUpperInvariant() + " File";

            return info.szTypeName;
        });
    }

    public static ImageSource? GetLargeIcon(FileSystemItem item)
    {
        var key = item.IsFolder
            ? item.IsDriveRoot ? $"drive:{item.FullPath}" : FolderKey
            : string.IsNullOrEmpty(item.Extension) ? ".__none__" : item.Extension;

        return LargeIconCache.GetOrAdd(key, _ => LoadLargeIcon(item));
    }

    private static ImageSource? LoadLargeIcon(FileSystemItem item)
    {
        var info = new NativeMethods.SHFILEINFO();
        var result = NativeMethods.SHGetFileInfoW(
            item.FullPath,
            item.IsFolder ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL,
            ref info,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
            NativeMethods.SHGFI_SYSICONINDEX);

        if (result == IntPtr.Zero || info.iIcon < 0)
            return null;

        NativeMethods.IImageList? imageList = null;
        var icon = IntPtr.Zero;

        try
        {
            var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
            if (NativeMethods.SHGetImageList(NativeMethods.SHIL_JUMBO, ref iid, out imageList) < 0 || imageList is null)
                return null;

            if (imageList.GetIcon(info.iIcon, 1, out icon) < 0 || icon == IntPtr.Zero)
                return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (icon != IntPtr.Zero)
                NativeMethods.DestroyIcon(icon);
            if (imageList is not null)
                System.Runtime.InteropServices.Marshal.ReleaseComObject(imageList);
        }
    }

    private static ImageSource? LoadIcon(string path, bool isFolder, bool useAttributes, bool large = false)
    {
        var info = new NativeMethods.SHFILEINFO();

        var flags = NativeMethods.SHGFI_ICON |
                    (large ? NativeMethods.SHGFI_LARGEICON : NativeMethods.SHGFI_SMALLICON);
        if (useAttributes)
            flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;

        var attributes = isFolder
            ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY
            : NativeMethods.FILE_ATTRIBUTE_NORMAL;

        var result = NativeMethods.SHGetFileInfoW(
            path,
            attributes,
            ref info,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
            flags);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            NativeMethods.DestroyIcon(info.hIcon);
        }
    }

    public static void Populate(IReadOnlyList<FileSystemItem> items)
    {
        for (var i = 0; i < items.Count; i++)
            items[i].Icon = GetIcon(items[i]);
    }

    public static void PopulateTypeNames(IReadOnlyList<FileSystemItem> items)
    {
        for (var i = 0; i < items.Count; i++)
            items[i].TypeName = GetTypeName(items[i]);
    }
}
