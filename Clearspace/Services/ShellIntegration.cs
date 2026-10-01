// Clearspace | NEW (Explorer integration): how locked items look and behave in Windows Explorer.
//  - LockNames: locked files carry a ".cslock" extension (photo.jpg -> photo.jpg.cslock) so Explorer can
//    show them with a lock icon and send a double-click to Clearspace instead of the normal app.
//  - Register: per-user (HKCU, no admin) file association for .cslock plus right-click entries
//    ("Lock with Clearspace" on files, a "Clearspace" submenu on folders). Rewritten on every start so it
//    always points at the Clearspace that ran last; nothing is written when it is already correct.
//  - Folder icon: a locked folder gets a desktop.ini that gives it the locked-folder icon in Explorer.
//    Any desktop.ini that was already there is kept and restored when the folder is unlocked.
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Clearspace.Services;

internal static class LockNames
{
    internal const string Extension = ".cslock";

    internal static bool HasLockedName(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
    internal static string Locked(string path) => HasLockedName(path) ? path : path + Extension;
    internal static string Unlocked(string path) => HasLockedName(path) ? path[..^Extension.Length] : path;
}

internal static class ShellIntegration
{
    private const string ProgId = "Clearspace.LockedFile";

    internal static string IconsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clearspace", "Icons");
    internal static string LockedFileIcon => Path.Combine(IconsFolder, "LockedFile.ico");
    internal static string LockedFolderIcon => Path.Combine(IconsFolder, "LockedFolder.ico");

    // Writes the two lock icons next to Clearspace's other per-user data (Explorer needs real .ico files).
    internal static void EnsureIcons()
    {
        Directory.CreateDirectory(IconsFolder);
        Extract("Clearspace.LockedFile.ico", LockedFileIcon);
        Extract("Clearspace.LockedFolder.ico", LockedFolderIcon);
    }

    private static void Extract(string resource, string target)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                           ?? throw new FileNotFoundException("Missing embedded icon " + resource);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (File.Exists(target) && new FileInfo(target).Length == bytes.Length && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
            return;
        File.WriteAllBytes(target, bytes);
    }

    // ---- Registry ----

    internal static void Register(string exePath)
    {
        EnsureIcons();
        var exe = $"\"{exePath}\"";
        string Command(string verb) => $"{exe} --{verb} \"%1\"";
        var changed = false;
        void Set(string key, string? name, string value)
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + key, writable: true);
            if (k.GetValue(name) as string == value) return;
            k.SetValue(name ?? string.Empty, value);
            changed = true;
        }

        // .cslock files: lock icon, double-click opens them in Clearspace.
        Set(LockNames.Extension, null, ProgId);
        Set(ProgId, null, "Clearspace locked file");
        Set(ProgId, "FriendlyTypeName", "Clearspace locked file");
        Set(ProgId + @"\DefaultIcon", null, LockedFileIcon + ",0");
        Set(ProgId + @"\shell", null, "open");
        Set(ProgId + @"\shell\open", null, "Open with Clearspace");
        Set(ProgId + @"\shell\open", "Icon", exePath);
        Set(ProgId + @"\shell\open\command", null, Command("open"));
        // CHANGED (unlock vs remove lock): Unlock = for a visit; Remove lock = for good. Keys are numbered so
        // Explorer lists them in this order; the older single "Clearspace.Unlock" entry is removed.
        DeleteKey(ProgId + @"\shell\Clearspace.Unlock", ref changed);
        Set(ProgId + @"\shell\Clearspace.1Unlock", "MUIVerb", "Unlock with Clearspace");
        Set(ProgId + @"\shell\Clearspace.1Unlock", "Icon", LockedFileIcon);
        Set(ProgId + @"\shell\Clearspace.1Unlock\command", null, Command("unlock"));
        Set(ProgId + @"\shell\Clearspace.2RemoveLock", "MUIVerb", "Remove lock with Clearspace");
        Set(ProgId + @"\shell\Clearspace.2RemoveLock\command", null, Command("remove-lock"));
        Set(ProgId + @"\shell\Clearspace.3ChangePassword", "MUIVerb", "Change password with Clearspace"); // NEW (change password)
        Set(ProgId + @"\shell\Clearspace.3ChangePassword\command", null, Command("change-password"));

        // Any file: Lock with Clearspace.
        Set(@"*\shell\Clearspace.Lock", "MUIVerb", "Lock with Clearspace");
        Set(@"*\shell\Clearspace.Lock", "Icon", LockedFileIcon);
        Set(@"*\shell\Clearspace.Lock\command", null, Command("lock"));

        // Folders: a "Clearspace" submenu with Open / Lock / Unlock.
        Set(@"Directory\shell\Clearspace", "MUIVerb", "Clearspace");
        Set(@"Directory\shell\Clearspace", "Icon", exePath);
        Set(@"Directory\shell\Clearspace", "SubCommands", string.Empty);
        Set(@"Directory\shell\Clearspace\shell\1Open", "MUIVerb", "Open in Clearspace");
        Set(@"Directory\shell\Clearspace\shell\1Open\command", null, Command("open"));
        Set(@"Directory\shell\Clearspace\shell\2Lock", "MUIVerb", "Lock folder");
        Set(@"Directory\shell\Clearspace\shell\2Lock", "Icon", LockedFolderIcon);
        Set(@"Directory\shell\Clearspace\shell\2Lock\command", null, Command("lock"));
        Set(@"Directory\shell\Clearspace\shell\3Unlock", "MUIVerb", "Unlock folder");
        Set(@"Directory\shell\Clearspace\shell\3Unlock\command", null, Command("unlock"));
        Set(@"Directory\shell\Clearspace\shell\4RemoveLock", "MUIVerb", "Remove lock"); // NEW (unlock vs remove lock)
        Set(@"Directory\shell\Clearspace\shell\4RemoveLock\command", null, Command("remove-lock"));
        Set(@"Directory\shell\Clearspace\shell\5ChangePassword", "MUIVerb", "Change password"); // NEW (change password)
        Set(@"Directory\shell\Clearspace\shell\5ChangePassword\command", null, Command("change-password"));

        if (changed) SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

        // REMOVED (Explorer folder gate, reverted): clean up the sign-in entry that build may have added.
        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        run?.DeleteValue("Clearspace locked folders", throwOnMissingValue: false);
    }

    private static void DeleteKey(string key, ref bool changed)
    {
        using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
        using (var existing = classes?.OpenSubKey(key))
            if (existing is null) return;
        classes!.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        changed = true;
    }

    internal static void Unregister()
    {
        using (var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true))
        {
            if (classes is null) return;
            string? current;
            using (var extension = classes.OpenSubKey(LockNames.Extension)) current = extension?.GetValue(null) as string;
            if (current == ProgId) classes.DeleteSubKeyTree(LockNames.Extension, throwOnMissingSubKey: false);
            classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
            classes.DeleteSubKeyTree(@"*\shell\Clearspace.Lock", throwOnMissingSubKey: false);
            classes.DeleteSubKeyTree(@"Directory\shell\Clearspace", throwOnMissingSubKey: false);
        }
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    // ---- Locked-folder icon (desktop.ini) ----

    internal static void SetFolderIcon(string folder)
    {
        EnsureIcons();
        var ini = Path.Combine(folder, "desktop.ini");
        if (File.Exists(ini) && Read(ini, "Clearspace", "Applied") == "1") return;
        var existed = File.Exists(ini);
        if (!existed) File.WriteAllBytes(ini, [0xFF, 0xFE]); // UTF-16 so any path in IconResource is kept intact
        else File.SetAttributes(ini, File.GetAttributes(ini) & ~FileAttributes.ReadOnly);

        var previous = Read(ini, ".ShellClassInfo", "IconResource");
        Write(ini, "Clearspace", "Applied", "1");
        Write(ini, "Clearspace", "Created", existed ? "0" : "1");
        if (previous is not null) Write(ini, "Clearspace", "PreviousIconResource", previous);
        Write(ini, ".ShellClassInfo", "IconResource", LockedFolderIcon + ",0");
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);

        // Explorer only reads desktop.ini in folders marked ReadOnly or System.
        var attributes = File.GetAttributes(folder);
        if ((attributes & (FileAttributes.ReadOnly | FileAttributes.System)) == 0)
        {
            File.SetAttributes(folder, attributes | FileAttributes.ReadOnly);
            Write(ini, "Clearspace", "AddedReadOnly", "1");
        }
        Refresh(folder);
    }

    internal static void ClearFolderIcon(string folder)
    {
        var ini = Path.Combine(folder, "desktop.ini");
        if (!File.Exists(ini) || Read(ini, "Clearspace", "Applied") != "1") return;
        var addedReadOnly = Read(ini, "Clearspace", "AddedReadOnly") == "1";
        if (Read(ini, "Clearspace", "Created") == "1")
        {
            File.SetAttributes(ini, FileAttributes.Normal);
            File.Delete(ini);
        }
        else
        {
            var attributes = File.GetAttributes(ini);
            File.SetAttributes(ini, FileAttributes.Normal);
            Write(ini, ".ShellClassInfo", "IconResource", Read(ini, "Clearspace", "PreviousIconResource"));
            Write(ini, "Clearspace", null, null); // removes the [Clearspace] section
            File.SetAttributes(ini, attributes);
        }
        if (addedReadOnly)
            File.SetAttributes(folder, File.GetAttributes(folder) & ~FileAttributes.ReadOnly);
        Refresh(folder);
    }

    // Tells Explorer an item changed so it redraws its icon.
    internal static void Refresh(string path)
    {
        var buffer = Marshal.StringToHGlobalUni(path);
        try { SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, buffer, IntPtr.Zero); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string? Read(string ini, string section, string key)
    {
        var buffer = new char[2048];
        var length = GetPrivateProfileStringW(section, key, "\u0001", buffer, buffer.Length, ini);
        var value = new string(buffer, 0, length);
        return value == "\u0001" ? null : value;
    }

    private static void Write(string ini, string section, string? key, string? value)
    {
        if (!WritePrivateProfileStringW(section, key, value, ini))
            throw new IOException("Could not update " + ini, Marshal.GetHRForLastWin32Error());
    }

    private const int SHCNE_UPDATEITEM = 0x00002000;
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;
    private const uint SHCNF_PATHW = 0x0005;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetPrivateProfileStringW(string section, string key, string defaultValue, char[] returned, int size, string file);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrivateProfileStringW(string section, string? key, string? value, string file);
}
