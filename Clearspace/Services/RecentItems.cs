// Clearspace | NEW (recent): the files and folders you opened lately, for the Recent page and the sidebar.
//
// Windows already keeps this list: every time a file or folder is opened (in File Explorer, in a program's
// Open box, from a jump list...) it puts a shortcut to it in the Recent folder
// (%AppData%\Microsoft\Windows\Recent) and updates that shortcut's date. It is the list File Explorer's own
// "Recent" shows. Reading it means the Recent page covers things opened in any program, not only here.
//  - Read: the shortcuts newest first, each turned into the file or folder it points to. Ones that point
//    to nothing on a disk (a settings page, a Store app) or to something that is gone are left out, and
//    each thing is listed once.
//  - Record: Clearspace adds to the list the way File Explorer does (SHAddToRecentDocs) when you open a file
//    or go into a folder, so what you do in Clearspace shows up there and in Windows' jump lists too.
// If "Show recently opened items in Start, Jump Lists, and File Explorer" is off in Windows' settings,
// Windows keeps no list: Record does nothing and the page says how to turn it on.
//
// NEW (recent sidebar): the sidebar's Recent section lists the last few folders (SidebarFolders), like File
// Explorer's Quick access. They are read in the background - when Clearspace starts, a moment after you go
// into a folder, and whenever the Recent page is read - and SidebarFoldersChanged tells the sidebars.
//
// FIXED (recent freeze): anything on a drive that can be slow to answer (a network share or mapped network
// drive, a USB stick, a disc) is given a short time and then skipped, together with the rest of that drive,
// instead of holding everything up. It used to be only \\server\share paths.
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Clearspace.Models;
using Microsoft.Win32;

namespace Clearspace.Services;

internal static class RecentItems
{
    // Windows keeps a few hundred at most; the newest this many are shown.
    internal const int MostShown = 200;

    // NEW (recent sidebar): how many folders the sidebar shows, and how many shortcuts are looked at to
    // find them (most recent items are files).
    internal const int SidebarCount = 5;
    private const int SidebarLinksLooked = 80;

    // Something on a slow drive is given this long once; after that its drive is skipped for the rest of the read.
    private static readonly TimeSpan SlowDriveWait = TimeSpan.FromMilliseconds(1200);

    internal static string Folder => Environment.GetFolderPath(Environment.SpecialFolder.Recent);

    /// <summary>True when Windows' setting to keep recently opened items is off.</summary>
    internal static bool IsOffInWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return key?.GetValue("Start_TrackDocs") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// For the Recent page: newest first, each item's LastOpened set. Call off the UI thread. Also brings the
    /// sidebar's folders up to date, since the list has just been read anyway.
    /// </summary>
    internal static List<FileSystemItem> Read(CancellationToken token)
    {
        var items = ReadCore(MostShown, int.MaxValue, foldersOnly: false, token);

        // NEW (recent sidebar)
        Publish(items.Where(item => item.IsFolder).Take(SidebarCount).Select(item => item.FullPath).ToList());
        return items;
    }

    // CHANGED (recent sidebar): the reading itself, shared by the page (everything) and the sidebar (folders).
    private static List<FileSystemItem> ReadCore(int most, int linksToLook, bool foldersOnly, CancellationToken token)
    {
        var items = new List<FileSystemItem>();
        FileInfo[] links;
        try
        {
            links = new DirectoryInfo(Folder).GetFiles("*.lnk");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return items;
        }

        Array.Sort(links, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slowDrives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new LinkReader();

        var looked = 0;
        foreach (var link in links)
        {
            token.ThrowIfCancellationRequested();
            if (items.Count >= most || ++looked > linksToLook)
                break;

            var target = reader.Target(link.FullName);
            if (target is null || !Path.IsPathFullyQualified(target) || IsOwnCopy(target))
                continue;

            target = Path.TrimEndingDirectorySeparator(target);
            if (target.Length == 2 && target[1] == ':')
                target += "\\"; // a drive ("C:") stays "C:\"
            if (!seen.Add(target))
                continue;

            if (Look(target, slowDrives) is not { } item)
                continue; // gone, or out of reach
            if (foldersOnly && !item.IsFolder)
                continue;

            item.LastOpened = link.LastWriteTime;
            items.Add(item);
        }

        return items;
    }

    private static bool IsDriveRoot(string path) => Path.GetPathRoot(path) is { } root && root.Length >= path.Length;

    // The file or folder as a list row, or null when it is no longer there or out of reach.
    // CHANGED (recent freeze): every drive that can be slow is given a short while, not only \\server paths.
    private static FileSystemItem? Look(string path, HashSet<string> slowDrives)
    {
        if (!MayBeSlow(path))
            return FileSystemItem.FromLocation(path);

        var drive = Path.GetPathRoot(path) ?? path;
        if (slowDrives.Contains(drive))
            return null;

        var look = Task.Run(() => FileSystemItem.FromLocation(path));
        if (look.Wait(SlowDriveWait))
            return look.Result;

        slowDrives.Add(drive);
        return null;
    }

    // NEW (recent freeze): a network share or mapped network drive, a USB stick or card, a disc, or a drive
    // letter that isn't there. Asking Windows what kind of drive it is doesn't touch the drive.
    private static bool MayBeSlow(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;

        try
        {
            return new DriveInfo(Path.GetPathRoot(path)!).DriveType is not DriveType.Fixed;
        }
        catch (Exception)
        {
            return true;
        }
    }

    // Copies Clearspace took out of ZIP files to open them live in its temporary folder; the ZIP file itself
    // is what was opened.
    private static bool IsOwnCopy(string path)
        => path.StartsWith(ArchiveBrowser.TempRoot, StringComparison.OrdinalIgnoreCase);

    // ---- recording

    /// <summary>A file was opened from Clearspace.</summary>
    internal static void RecordFile(string path) => Record(path, folder: false);

    /// <summary>Clearspace went into a folder.</summary>
    internal static void RecordFolder(string path) => Record(path, folder: true);

    private static void Record(string path, bool folder)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || IsOwnCopy(path))
            return;
        if (folder && IsDriveRoot(path))
            return; // a drive itself is not worth a place on the list

        // Off the UI thread: Windows writes a shortcut file each time.
        _ = Task.Run(() =>
        {
            try
            {
                if (folder ? Directory.Exists(path) : File.Exists(path))
                    SHAddToRecentDocs(SHARD_PATHW, path);
            }
            catch (Exception)
            {
                // the list is a convenience; nothing to tell anyone
            }
        });

        // REMOVED (productivity): refreshing the sidebar's Recent folders here. The sidebar's Productivity row
        // lists your tab groups instead; the page reads its folders from ProductivityStore.
    }

    private const uint SHARD_PATHW = 0x00000003;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHAddToRecentDocs(uint flags, string path);

    // ---- NEW (recent sidebar): the last few folders

    private static IReadOnlyList<string> s_sidebarFolders = [];
    private static readonly object s_sidebarGate = new();
    private static Timer? s_sidebarTimer;
    private static int s_sidebarReading;
    private static int s_sidebarStarted;

    /// <summary>The newest folders, for the sidebar's Recent section. Empty until first read.</summary>
    internal static IReadOnlyList<string> SidebarFolders => Volatile.Read(ref s_sidebarFolders);

    /// <summary>Raised (on a background thread) when SidebarFolders has changed.</summary>
    internal static event Action? SidebarFoldersChanged;

    /// <summary>Reads the sidebar's folders once, the first time a sidebar is made.</summary>
    internal static void StartSidebar()
    {
        if (Interlocked.Exchange(ref s_sidebarStarted, 1) == 0)
            RefreshSidebarSoon(TimeSpan.Zero);
    }

    private static void RefreshSidebarSoon(TimeSpan delay)
    {
        lock (s_sidebarGate)
        {
            s_sidebarTimer ??= new Timer(_ => RefreshSidebarNow());
            s_sidebarTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private static void RefreshSidebarNow()
    {
        // One read at a time; one that comes in meanwhile tries again shortly.
        if (Interlocked.Exchange(ref s_sidebarReading, 1) == 1)
        {
            RefreshSidebarSoon(TimeSpan.FromSeconds(1));
            return;
        }

        try
        {
            var folders = ReadCore(SidebarCount, SidebarLinksLooked, foldersOnly: true, CancellationToken.None);
            Publish(folders.Select(item => item.FullPath).ToList());
        }
        catch (Exception)
        {
            // the sidebar keeps what it had
        }
        finally
        {
            Volatile.Write(ref s_sidebarReading, 0);
        }
    }

    private static void Publish(IReadOnlyList<string> folders)
    {
        if (folders.SequenceEqual(SidebarFolders, StringComparer.OrdinalIgnoreCase))
            return;

        Volatile.Write(ref s_sidebarFolders, folders);
        SidebarFoldersChanged?.Invoke();
    }

    // ---- reading a shortcut: Windows' own ShellLink object, made once for the whole list

    private sealed class LinkReader : IDisposable
    {
        private readonly object? _link;
        private readonly StringBuilder _buffer = new(32768);

        internal LinkReader()
        {
            try
            {
                if (Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")) is { } type)
                    _link = Activator.CreateInstance(type);
            }
            catch (Exception)
            {
                _link = null;
            }
        }

        // What the shortcut points to, or null when it is not a file or folder or can't be read.
        internal string? Target(string shortcut)
        {
            if (_link is not IPersistFile file || _link is not IShellLinkW link)
                return null;

            try
            {
                file.Load(shortcut, 0); // STGM_READ
                _buffer.Clear();
                if (link.GetPath(_buffer, _buffer.Capacity, IntPtr.Zero, 0) != 0)
                    return null;
                var target = _buffer.ToString();
                return target.Length == 0 ? null : target;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_link is not null && Marshal.IsComObject(_link))
                Marshal.ReleaseComObject(_link);
        }
    }

    // IShellLinkW, as far as GetPath (its first method) - the rest are not used.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
    }
}
