// Clearspace | NEW (menu by type): what the right-click menu offers for the kind of thing that is selected.
// The menu of a file or folder used to be the same whatever it was. Now the entries that fit come first:
//     a sound file      Play in Clearspace
//     a picture         View in Clearspace; further down Copy image, Set as desktop background, Print
//     a ZIP file        the two Extract entries (they were lower down)
//     a program         Run as administrator
//     a shortcut        Open file location, Run as administrator
//     a document        Print (when the program that opens it can print)
//     a folder          Open in Terminal (beside Open in new tab)
//     any other file    Open with…
// Cut, Copy, Paste, Rename and Delete moved out of the list into a row of icons at the bottom of the menu.
//
// ItemMenu.For decides which entries fit a selection; it reads nothing from the disk, so it is tested
// directly (Clearspace.Tests/ItemMenuTests.cs). ShellActions does the things themselves, each by asking
// Windows the same way File Explorer does. The window side is MainWindow.ItemMenu.cs.
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Clearspace.Models;

namespace Clearspace.Services;

/// <summary>One selected item, reduced to what the menu depends on.</summary>
/// <param name="Extension">The file's ending with its dot; empty for a folder.</param>
/// <param name="OnDisk">A real path (not an entry of a page such as This PC's hubs).</param>
/// <param name="Lock">CHANGED (only what applies): whether it is locked with a password, and open for a visit.</param>
/// <param name="Cloud">Its OneDrive (or other cloud) state; None when it is not a cloud file.</param>
/// <param name="IsDriveRoot">A drive (C:\), which is a folder but can't be locked, pinned twice or given a type.</param>
/// <param name="IsPinned">Already pinned to the sidebar.</param>
internal readonly record struct MenuSubject(string Extension, bool IsFolder, bool OnDisk,
    LockState Lock = LockState.None, CloudSyncState Cloud = CloudSyncState.None, bool IsDriveRoot = false, bool IsPinned = false);

// NEW (only what applies): the entries every menu used to show whatever was selected. Each is now shown
// only when it can do something for the selection (ItemMenu.Common).
[Flags]
internal enum CommonActions
{
    None = 0,
    Lock = 1,             // Lock with password…
    Unlock = 2,           // Unlock…
    RemoveLock = 4,       // Remove lock…
    ChangePassword = 8,   // Change password…
    Pin = 16,             // Pin to sidebar
    FolderTools = 32,     // Disk usage…, Find duplicates…
    KeepOnDevice = 64,    // Always keep on this device
    FreeUpSpace = 128,    // Free up space
    SetFolderType = 256   // Set folder type
}

[Flags]
internal enum TypeActions
{
    None = 0,
    Play = 1,
    ViewPhoto = 2,
    CopyImage = 4,
    SetWallpaper = 8,
    RunAsAdministrator = 16,
    OpenLocation = 32,
    OpenWith = 64,
    Terminal = 128,
    Print = 256
}

internal static class ItemMenu
{
    internal const int MostToPrint = 10; // more files than this selected: Print is not offered, to spare the printer a slip

    // Pictures that can go on the clipboard and on the desktop. Windows' own decoders read these on every PC.
    private static readonly HashSet<string> PlainPictures = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"
    };

    private static readonly HashSet<string> Programs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd"
    };

    private static readonly HashSet<string> Printable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".rtf", ".txt", ".odt", ".xls", ".xlsx", ".ppt", ".pptx",
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"
    };

    /// <summary>
    /// NEW (only what applies): which of the general entries can do something for the selection.
    ///  - Lock with password: one thing on a disk that has no lock yet. Unlock: one thing that is locked
    ///    right now. Remove lock and Change password: anything selected has a lock (locked, or open for a visit).
    ///  - Pin to sidebar, Disk usage and Find duplicates: one folder (pinning: one that is not pinned yet).
    ///  - Always keep on this device: a cloud file that is not kept already. Free up space: a cloud file
    ///    that is on this PC.
    ///  - Set folder type: a folder that is not a drive.
    /// </summary>
    internal static CommonActions Common(IReadOnlyList<MenuSubject> selection)
    {
        var actions = CommonActions.None;
        if (selection.Count == 0)
            return actions;

        if (selection.Any(item => item.Lock != LockState.None))
            actions |= CommonActions.RemoveLock | CommonActions.ChangePassword;

        if (selection.Any(item => item.Cloud is CloudSyncState.OnlineOnly or CloudSyncState.Available))
            actions |= CommonActions.KeepOnDevice;

        if (selection.Any(item => item.Cloud is CloudSyncState.Available or CloudSyncState.AlwaysAvailable))
            actions |= CommonActions.FreeUpSpace;

        if (selection.Any(item => item is { IsFolder: true, OnDisk: true, IsDriveRoot: false }))
            actions |= CommonActions.SetFolderType;

        if (selection.Count != 1 || !selection[0].OnDisk)
            return actions;

        var only = selection[0];

        if (only.Lock == LockState.Locked)
            actions |= CommonActions.Unlock;
        else if (only.Lock == LockState.None && !only.IsDriveRoot)
            actions |= CommonActions.Lock;

        if (only.IsFolder)
        {
            actions |= CommonActions.FolderTools;
            if (!only.IsPinned)
                actions |= CommonActions.Pin;
        }

        return actions;
    }

    private const string Shortcut = ".lnk";
    private const string LockedFile = ".cslock"; // Clearspace opens these itself; no other program can

    /// <summary>
    /// The entries that fit the selection. <paramref name="canPrint"/> says whether the program that opens
    /// files with an ending knows how to print them (asked only for endings that are printable at all).
    /// </summary>
    internal static TypeActions For(IReadOnlyList<MenuSubject> selection, Func<string, bool> canPrint)
    {
        var actions = TypeActions.None;
        if (selection.Count == 0)
            return actions;

        // Print: a few files at once is fine, as long as every one of them can be printed.
        if (selection.Count <= MostToPrint &&
            selection.All(item => item is { IsFolder: false, OnDisk: true } && Printable.Contains(item.Extension) && canPrint(item.Extension)))
            actions |= TypeActions.Print;

        // Everything else is about the one thing that was clicked.
        if (selection.Count != 1 || !selection[0].OnDisk)
            return actions;

        var only = selection[0];
        if (only.IsFolder)
            return actions | TypeActions.Terminal;

        var ending = only.Extension;
        if (MediaTypes.IsAudio(ending))
            actions |= TypeActions.Play;

        if (MediaTypes.IsImage(ending))
            actions |= TypeActions.ViewPhoto;

        if (PlainPictures.Contains(ending))
            actions |= TypeActions.CopyImage | TypeActions.SetWallpaper;

        var program = Programs.Contains(ending);
        var shortcut = ending.Equals(Shortcut, StringComparison.OrdinalIgnoreCase);

        if (program || shortcut)
            actions |= TypeActions.RunAsAdministrator;
        if (shortcut)
            actions |= TypeActions.OpenLocation;
        if (!program && !shortcut && !ending.Equals(LockedFile, StringComparison.OrdinalIgnoreCase))
            actions |= TypeActions.OpenWith;

        return actions;
    }
}

/// <summary>The things the type entries do. Each returns null when it went through, or words for the status line.</summary>
internal static class ShellActions
{
    private const int Cancelled = 1223; // the "do you want to allow this app" question was answered No

    private static readonly Dictionary<string, bool> PrintVerbs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the program registered for an ending has a "print" command. Reads the registry once per ending.</summary>
    internal static bool CanPrint(string extension)
    {
        if (extension.Length == 0)
            return false;

        lock (PrintVerbs)
        {
            if (PrintVerbs.TryGetValue(extension, out var known))
                return known;

            bool can;
            try
            {
                can = new ProcessStartInfo("file" + extension).Verbs.Contains("print", StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                can = false;
            }

            return PrintVerbs[extension] = can;
        }
    }

    internal static string? Print(string path) => Start(new ProcessStartInfo
    {
        FileName = path,
        Verb = "print",
        UseShellExecute = true,
        WindowStyle = ProcessWindowStyle.Hidden
    }, "print");

    internal static string? RunAsAdministrator(string path) => Start(new ProcessStartInfo
    {
        FileName = path,
        Verb = "runas",
        UseShellExecute = true,
        WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty
    }, "start");

    // Windows' "choose a program" window. The path goes in bare: this entry point takes the rest of the
    // command line as the file's name, spaces included, and would take quotes as part of it.
    internal static string? OpenWith(string path) => Start(new ProcessStartInfo
    {
        FileName = "rundll32.exe",
        Arguments = "shell32.dll,OpenAs_RunDLL " + path,
        UseShellExecute = false
    }, "show the list of programs for");

    private static string? Start(ProcessStartInfo how, string doing)
    {
        try
        {
            Process.Start(how)?.Dispose();
            return null;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == Cancelled)
        {
            return null; // the user said no; nothing to report
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            return $"Windows couldn't {doing} that: {error.Message}";
        }
    }

    internal static string? OpenTerminal(string folder)
    {
        try
        {
            Commands.Actions.OpenTerminalAction.Launch(folder);
            return null;
        }
        catch (Exception error)
        {
            return $"Couldn't open a terminal there: {error.Message}";
        }
    }

    internal static string? SetWallpaper(string path)
    {
        try
        {
            return SystemParametersInfoW(SetDeskWallpaper, 0, path, UpdateIniFile | SendChange)
                ? null
                : "Windows didn't accept that picture as the desktop background.";
        }
        catch (Exception error)
        {
            return $"Couldn't set the desktop background: {error.Message}";
        }
    }

    /// <summary>
    /// What a shortcut (.lnk) points to, or null when that is not a file or folder (a Store app, a
    /// control panel page). Asks Windows' own shortcut reader, so call it on the UI thread.
    /// </summary>
    internal static string? ShortcutTarget(string shortcut)
    {
        object? shell = null;
        object? link = null;

        try
        {
            if (Type.GetTypeFromProgID("WScript.Shell") is not { } type)
                return null;

            shell = Activator.CreateInstance(type);
            if (shell is null)
                return null;

            link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcut]);
            var target = link?.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private const uint SetDeskWallpaper = 0x0014; // SPI_SETDESKWALLPAPER
    private const uint UpdateIniFile = 0x01;      // remember it
    private const uint SendChange = 0x02;         // tell running programs

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint parameter, string value, uint flags);
}
