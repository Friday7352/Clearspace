// Clearspace | NEW (Explorer integration): how locked items look and behave in Windows Explorer.
//  - LockNames: locked files carry a ".cslock" extension (photo.jpg -> photo.jpg.cslock) so Explorer can
//    show them with a lock icon and send a double-click to Clearspace instead of the normal app.
//  - Register: per-user (HKCU, no admin) file association for .cslock plus right-click entries
//    ("Lock with Clearspace" on files, a "Clearspace" submenu on folders). Rewritten on every start so it
//    always points at the Clearspace that ran last; nothing is written when it is already correct.
//  - Folder icon: a locked folder gets a desktop.ini that gives it the locked-folder icon in Explorer.
//    Any desktop.ini that was already there is kept and restored when the folder is unlocked.
//  - NEW (folder opener): "Open folders with Clearspace" (Settings menu) makes Clearspace the program
//    Windows starts for a folder or a drive, and for Win+E. See SetFolderOpener.
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

        // NEW (folder opener): if "Open folders with Clearspace" is on, keep it pointing at this Clearspace
        // (it may have been installed somewhere else since). Nothing is written when it already does.
        if (IsFolderOpener() && !FolderOpenerIsCurrent(exePath))
            SetFolderOpener(true, exePath);

        // REMOVED (Explorer folder gate, reverted): clean up the sign-in entry that build may have added.
        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        run?.DeleteValue("Clearspace locked folders", throwOnMissingValue: false);
    }

    // NEW (logo): Windows keeps its own cache of every program's icon and does not always notice that a
    // rebuilt or updated Clearspace.exe carries a different one, so a pinned taskbar button, the Start menu
    // and Task Manager can go on showing the old logo. The first time a changed Clearspace.exe runs, tell
    // Windows to read its icon again and to reload its icon cache. A small stamp file next to the lock icons
    // remembers which Clearspace.exe was seen last, so this happens once per build or update, not every start.
    internal static void RefreshAppIconIfChanged(string exePath)
    {
        var file = new FileInfo(exePath);
        var current = $"{exePath}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        var stamp = Path.Combine(IconsFolder, "app-icon.stamp");
        if (File.Exists(stamp) && File.ReadAllText(stamp) == current) return;

        Directory.CreateDirectory(IconsFolder);
        File.WriteAllText(stamp, current);
        Refresh(exePath);
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
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
        // NEW (folder opener): removing Clearspace hands folders back to File Explorer.
        try { SetFolderOpener(false, string.Empty); }
        catch (Exception) { }

        // NEW (file backup): and takes its entry out of Windows' Task Scheduler, which would otherwise go on
        // starting a program that is no longer there. The backups themselves, and their settings, stay.
        try { FileBackupTask.RemoveForGood(); }
        catch (Exception) { }

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

    // ---- NEW (folder opener): Clearspace as the program Windows opens folders with ----
    //
    // Windows decides what opens a folder from the "default verb" of the Directory and Drive classes. With
    // the setting on, both name a verb of ours whose command is the same "--open" Explorer's "Open in
    // Clearspace" entry uses, so the folder opens in a tab of the Clearspace that is already running (or
    // starts one). Win+E and the taskbar's File Explorer button go through a class of their own, which gets
    // the same command pointed at your user folder.
    //
    // Everything is written under HKEY_CURRENT_USER (this Windows account only, no administrator rights),
    // and what was there before is put back when the setting is turned off or Clearspace is uninstalled.
    // File Explorer itself is not changed or removed: its own windows, the desktop and the file pickers
    // inside other programs keep working, and "Show in folder" in programs that ask Explorer by name
    // still opens Explorer.

    private const string OpenerVerb = "Clearspace.OpenFolder";
    private const string PreviousDefault = "ClearspacePreviousDefault";
    private const string ExplorerLauncher = @"CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";
    private const string ExplorerLauncherVerb = ExplorerLauncher + @"\shell\opennewwindow";
    private const string OpenerMarker = "Clearspace";
    private static readonly string[] OpenerClasses = ["Directory", "Drive"];

    // CHANGED (Explorer stays Explorer): --open-folder, not --open, so Clearspace can tell Windows opening a
    // folder (which may come from inside File Explorer, and then stays there: ExplorerHandoff) from someone
    // choosing "Open in Clearspace". A setting that was on before is rewritten to this the next time
    // Clearspace starts (Register: FolderOpenerIsCurrent compares the whole command).
    private static string FolderOpenerCommand(string exePath) => $"\"{exePath}\" --open-folder \"%1\"";

    private static string LauncherCommand(string exePath) =>
        $"\"{exePath}\" --open \"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\"";

    // True when folders open in Clearspace (the tick in the Settings menu).
    internal static bool IsFolderOpener()
    {
        using var shell = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell");
        return string.Equals(shell?.GetValue(null) as string, OpenerVerb, StringComparison.OrdinalIgnoreCase);
    }

    private static bool FolderOpenerIsCurrent(string exePath)
    {
        foreach (var name in OpenerClasses)
        {
            using var command = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{name}\shell\{OpenerVerb}\command");
            if (command?.GetValue(null) as string != FolderOpenerCommand(exePath))
                return false;
        }

        using var launcher = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ExplorerLauncherVerb + @"\command");
        return launcher?.GetValue(null) as string == LauncherCommand(exePath);
    }

    internal static void SetFolderOpener(bool on, string exePath)
    {
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes", writable: true);

        foreach (var name in OpenerClasses)
        {
            // Turning it off only looks at what is there; it never makes a key just to find it empty.
            using var shell = on
                ? classes.CreateSubKey(name + @"\shell", writable: true)
                : classes.OpenSubKey(name + @"\shell", writable: true);
            if (shell is null)
                continue;

            var current = shell.GetValue(null) as string;
            var isOurs = string.Equals(current, OpenerVerb, StringComparison.OrdinalIgnoreCase);

            if (on)
            {
                using (var verb = shell.CreateSubKey(OpenerVerb, writable: true))
                {
                    // CHANGED (Explorer stays Explorer): shown in Explorer's menu as plain "Open", because from
                    // inside Explorer it now opens the folder in Explorer. "Open in Clearspace" is its own entry.
                    verb.SetValue(string.Empty, "Open");
                    verb.DeleteValue("Icon", throwOnMissingValue: false); // CHANGED (Explorer stays Explorer): a plain "Open", like Explorer's own
                    // Remember what was the default before (another file manager's verb, usually nothing).
                    if (!isOurs)
                    {
                        if (string.IsNullOrEmpty(current)) verb.DeleteValue(PreviousDefault, throwOnMissingValue: false);
                        else verb.SetValue(PreviousDefault, current);
                    }
                    using var command = verb.CreateSubKey("command", writable: true);
                    command.SetValue(string.Empty, FolderOpenerCommand(exePath));
                }
                shell.SetValue(string.Empty, OpenerVerb);
            }
            else
            {
                string? previous;
                using (var verb = shell.OpenSubKey(OpenerVerb)) previous = verb?.GetValue(PreviousDefault) as string;
                if (isOurs)
                {
                    if (string.IsNullOrEmpty(previous)) shell.DeleteValue(string.Empty, throwOnMissingValue: false);
                    else shell.SetValue(string.Empty, previous);
                }
                shell.DeleteSubKeyTree(OpenerVerb, throwOnMissingSubKey: false);
            }
        }

        if (!on)
        {
            // "Drive" had nothing of Clearspace's before; leave no empty keys behind.
            DeleteIfEmpty(classes, @"Drive\shell");
            DeleteIfEmpty(classes, "Drive");
        }

        // Win+E and the taskbar's File Explorer button.
        if (on)
        {
            using var command = classes.CreateSubKey(ExplorerLauncherVerb + @"\command", writable: true);
            command.SetValue(string.Empty, LauncherCommand(exePath));
            command.SetValue("DelegateExecute", string.Empty); // without this Windows ignores the command above
            command.SetValue(OpenerMarker, "1");               // so turning it off only removes what Clearspace wrote
        }
        else
        {
            bool ours;
            using (var command = classes.OpenSubKey(ExplorerLauncherVerb + @"\command"))
                ours = command?.GetValue(OpenerMarker) is not null;

            if (ours)
            {
                classes.DeleteSubKeyTree(ExplorerLauncherVerb, throwOnMissingSubKey: false);
                DeleteIfEmpty(classes, ExplorerLauncher + @"\shell");
                DeleteIfEmpty(classes, ExplorerLauncher);
            }
        }

        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    private static void DeleteIfEmpty(RegistryKey parent, string name)
    {
        using (var key = parent.OpenSubKey(name))
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0) return;
        parent.DeleteSubKey(name, throwOnMissingSubKey: false);
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
