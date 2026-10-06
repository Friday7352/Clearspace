// Clearspace | Known Windows folder locations.
//
// CHANGED (search fix): the answers are kept for a short while. Every folder row asks for all six of these
// when it gets its icon, and each question went to Windows (which checks the folder is there) and, for
// Downloads, to the disk again. With thousands of folders in a list of search results, or a disk that
// is busy, those questions were what the window waited on.

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

    // NEW (search fix): one remembered answer per folder. A folder moved in Windows (Properties > Location)
    // is picked up within KeepFor.
    internal static readonly TimeSpan KeepFor = TimeSpan.FromSeconds(30);

    internal sealed record Kept(string Folder, long At);
    private static readonly Kept?[] Remembered = new Kept?[7];

    internal static string Remember(int slot, Func<string> ask, long now, Kept?[]? store = null)
    {
        store ??= Remembered;
        var kept = Volatile.Read(ref store[slot]);
        if (kept is not null && now - kept.At < (long)KeepFor.TotalMilliseconds && now >= kept.At)
            return kept.Folder;

        var path = ask();
        Volatile.Write(ref store[slot], new Kept(path, now));
        return path;
    }

    private static string Remember(int slot, Func<string> ask) => Remember(slot, ask, Environment.TickCount64);

    // Forgets every remembered answer (tests, and anything that knows a folder has just moved).
    internal static void Forget() => Array.Clear(Remembered);

    public static string Profile => Remember(0, () => Resolve(ref _profile, Environment.SpecialFolder.UserProfile));
    public static string Desktop => Remember(1, () => Resolve(ref _desktop, Environment.SpecialFolder.DesktopDirectory));
    public static string Documents => Remember(2, () => Resolve(ref _documents, Environment.SpecialFolder.MyDocuments));
    public static string Pictures => Remember(3, () => Resolve(ref _pictures, Environment.SpecialFolder.MyPictures));
    public static string Music => Remember(4, () => Resolve(ref _music, Environment.SpecialFolder.MyMusic));
    public static string Videos => Remember(5, () => Resolve(ref _videos, Environment.SpecialFolder.MyVideos));

    public static string Downloads => Remember(6, () =>
    {
        var path = Resolve(ref _downloads, Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            return path;

        var guess = Path.Combine(Profile, "Downloads");
        return Directory.Exists(guess) ? guess : Profile;
    });

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
