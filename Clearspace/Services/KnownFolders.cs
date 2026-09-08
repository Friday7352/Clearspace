// Clearspace | Known Windows folder locations.

using System.IO;
using System.Runtime.InteropServices;
using Clearspace.Native;

namespace Clearspace.Services;

public static class KnownFolders
{
    private static Guid _profile = new("5E6C858F-0E22-4760-9AFE-EA3317B67173");
    private static Guid _desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    private static Guid _documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    private static Guid _downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    private static Guid _pictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    private static Guid _music = new("4BD8D571-6D19-48D3-BE97-422220080E43");
    private static Guid _videos = new("18989B1D-99B5-41FC-B7DB-A883A7E8AC0F");

    public static string Profile => Resolve(ref _profile, Environment.SpecialFolder.UserProfile);
    public static string Desktop => Resolve(ref _desktop, Environment.SpecialFolder.DesktopDirectory);
    public static string Documents => Resolve(ref _documents, Environment.SpecialFolder.MyDocuments);
    public static string Pictures => Resolve(ref _pictures, Environment.SpecialFolder.MyPictures);
    public static string Music => Resolve(ref _music, Environment.SpecialFolder.MyMusic);
    public static string Videos => Resolve(ref _videos, Environment.SpecialFolder.MyVideos);

    public static string Downloads
    {
        get
        {
            var path = Resolve(ref _downloads, Environment.SpecialFolder.UserProfile);

            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                return path;

            var guess = Path.Combine(Profile, "Downloads");
            return Directory.Exists(guess) ? guess : Profile;
        }
    }

    public static bool IsWithinPictures(string path)
    {
        var pictures = Pictures;

        if (string.IsNullOrEmpty(pictures) || string.IsNullOrEmpty(path))
            return false;

        return path.StartsWith(pictures, StringComparison.OrdinalIgnoreCase);
    }

    private static string Resolve(ref Guid folderId, Environment.SpecialFolder fallback)
    {
        var pointer = IntPtr.Zero;

        try
        {
            var result = NativeMethods.SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out pointer);

            if (result >= 0 && pointer != IntPtr.Zero)
            {
                var path = Marshal.PtrToStringUni(pointer);
                if (!string.IsNullOrEmpty(path))
                    return path;
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            if (pointer != IntPtr.Zero)
                NativeMethods.CoTaskMemFree(pointer);
        }

        return Environment.GetFolderPath(fallback);
    }
}
