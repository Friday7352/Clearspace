using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Clearspace.Services;

internal static class FileLockSafety
{
    // NEW (folder locking): name prefix of the temporary files Transform writes beside a file.
    internal const string TemporaryPrefix = ".clearspace-lock-";

    internal static string ValidatePath(string path)
    {
        path = ValidateLocation(path); // CHANGED: drive and parent-folder checks moved to ValidateLocation
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.ReadOnly |
            FileAttributes.Encrypted | FileAttributes.Offline)) != 0)
            throw new IOException("Choose a writable local file that is not a link, cloud placeholder, or already Windows-encrypted.");
        return path;
    }

    // NEW (folder locking): a folder can be locked when it is a real (non-linked) folder on a local NTFS
    // drive and is not one Windows or installed programs depend on.
    internal static string ValidateFolder(string path)
    {
        path = Path.TrimEndingDirectorySeparator(ValidateLocation(path));
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0)
            throw new IOException("Choose a folder.");
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked or cloud placeholder folders cannot be locked yet.");
        if (IsProtectedFolder(path))
            throw new IOException("Clearspace won't lock a whole drive, your entire user folder, or folders Windows and installed programs need (Windows, Program Files, ProgramData, AppData).");
        return path;
    }

    // NEW (folder locking): every file inside the folder and its subfolders that locking applies to.
    // Linked folders (junctions, symlinks, cloud roots) are not followed and System files such as
    // desktop.ini are left alone. Folders that can't be read are skipped rather than failing the whole run.
    internal static IEnumerable<string> EnumerateFolder(string folder) =>
        Directory.EnumerateFiles(folder, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            ReturnSpecialDirectories = false
        }).Where(file => !Path.GetFileName(file).StartsWith(TemporaryPrefix, StringComparison.OrdinalIgnoreCase));

    // NEW (folder locking): drive roots, the user folder itself (or anything above it), and the folders
    // Windows and programs keep their own files in. Subfolders of the temp folder are allowed.
    internal static bool IsProtectedFolder(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (Path.GetPathRoot(folder) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), folder, StringComparison.OrdinalIgnoreCase))
            return true;
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (IsInside(folder, temp)) return false;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0 && IsSameOrInside(profile, folder)) return true;
        foreach (var special in new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData
        })
        {
            var protectedPath = Environment.GetFolderPath(special);
            if (protectedPath.Length > 0 && (IsSameOrInside(folder, protectedPath) || IsSameOrInside(protectedPath, folder)))
                return true;
        }
        return false;
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) || IsInside(path, folder);
    }

    // Separator check so C:\Users\Sam is not treated as inside C:\Users\Sa.
    private static bool IsInside(string path, string folder) =>
        path.Length > folder.Length + 1 &&
        path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
        (path[folder.Length] == Path.DirectorySeparatorChar || folder.EndsWith(Path.DirectorySeparatorChar));

    // NEW (folder locking): the drive and parent-folder checks shared by files and folders.
    private static string ValidateLocation(string path)
    {
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path)!;
        if (root.Length != 3 || root[1] != ':' || path.AsSpan(2).Contains(':'))
            throw new IOException("Choose a file or folder on a local NTFS drive.");
        var drive = new DriveInfo(root);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || drive.DriveFormat != "NTFS")
            throw new IOException("Locking currently supports local NTFS drives.");
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Items inside linked or cloud placeholder folders cannot be locked yet.");
        return path;
    }

    internal static FileIdentity Identity(FileStream file)
    {
        if (!GetFileInformationByHandle(file.SafeFileHandle, out var info)) throw new Win32Exception();
        if (info.Links != 1) throw new IOException("Files with hard links cannot be locked because another name could retain readable contents.");
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw new IOException("Linked files cannot be locked.");
        return info;
    }

    internal static void CheckStreams(string path)
    {
        var handle = FindFirstStreamW(path, 0, out var data, 0);
        if (handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 38) return; // no streams on an empty file
            throw new Win32Exception(error);
        }
        try
        {
            do
            {
                if (data.Name != "::$DATA")
                    throw new IOException("This file has additional data streams. File locking currently supports files with one data stream.");
            } while (FindNextStreamW(handle, out data));
            if (Marshal.GetLastWin32Error() != 38) throw new Win32Exception();
        }
        finally { FindClose(handle); }
    }

    internal static FileStream CreatePrivateTemporary(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None,
            65536, FileOptions.WriteThrough, security);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileIdentity
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        internal readonly bool SameFile(FileIdentity other) => Volume == other.Volume && IndexHigh == other.IndexHigh && IndexLow == other.IndexLow;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        internal long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] internal string Name;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string name, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(IntPtr handle, out StreamData data);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);
}
