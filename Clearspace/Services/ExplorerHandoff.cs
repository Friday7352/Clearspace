// Clearspace | NEW (Explorer stays Explorer): with "Open folders with Clearspace" on, Windows starts Clearspace
// for every folder that is opened: from the desktop, from another program, and also from inside a File
// Explorer window (Explorer uses the same default "Open" for a double-click on a folder in its own list).
// That last one should not leave Explorer: if you chose to use Explorer, it should behave as it always has.
//
// So when Windows asks Clearspace to open a folder (--open-folder), Clearspace first looks at the window that
// is in front. If it is a File Explorer window, the folder is shown in that window instead (on Windows 11, in
// the tab you are looking at), and Clearspace closes again without showing anything. Anywhere else (the
// desktop, another program, the Run box) the folder opens in Clearspace as before. Win+E and the taskbar's
// File Explorer button are not affected; they always open Clearspace (ShellIntegration.SetFolderOpener).
// "Open in Clearspace" in Explorer's right-click menu sends --open, not --open-folder, so it still opens
// Clearspace from inside Explorer too.
// If anything here fails, the folder simply opens in Clearspace, as it did before this existed.
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Clearspace.Services;

internal static class ExplorerHandoff
{
    // True when the folder was shown in the File Explorer window in front, and Clearspace has nothing to do.
    internal static bool TryKeepInExplorer(string path)
    {
        try
        {
            var front = GetForegroundWindow();
            if (front == IntPtr.Zero || !IsExplorerWindow(front) || !Directory.Exists(path))
                return false;

            if (Type.GetTypeFromProgID("Shell.Application") is not { } type || Activator.CreateInstance(type) is not { } shell)
                return false;

            try
            {
                // Every open Explorer window (and on Windows 11 every tab: the tabs of one window share its handle).
                dynamic windows = ((dynamic)shell).Windows();
                int count = windows.Count;
                object? anyTab = null;
                object? shownTab = null;
                for (var i = 0; i < count && shownTab is null; i++)
                {
                    object? window = windows.Item(i);
                    if (window is null)
                        continue;

                    long handle;
                    try { handle = Convert.ToInt64((object)((dynamic)window).HWND); }
                    catch (Exception) { continue; } // not a folder window (an old Internet Explorer one, say)

                    if (new IntPtr(handle) != front)
                        continue;

                    anyTab ??= window;
                    if (IsShownTab(window))
                        shownTab = window;
                }

                if ((shownTab ?? anyTab) is not { } target)
                    return false;

                ((dynamic)target).Navigate2(path);
                return true;
            }
            finally
            {
                if (Marshal.IsComObject(shell))
                    Marshal.ReleaseComObject(shell);
            }
        }
        catch (Exception)
        {
            return false; // open it in Clearspace instead
        }
    }

    private static bool IsExplorerWindow(IntPtr window)
    {
        var name = new StringBuilder(64);
        return GetClassNameW(window, name, name.Capacity) > 0 &&
               name.ToString() is "CabinetWClass" or "ExploreWClass";
    }

    // Windows 11 puts several tabs in one Explorer window; only the tab being looked at is visible.
    // Before Windows 11 there is one per window, and it is the window itself (visible as well).
    private static bool IsShownTab(object window)
    {
        try
        {
            if (window is not IComServiceProvider provider)
                return true;

            var service = TopLevelBrowser;
            var asked = typeof(IShellBrowserWindow).GUID;
            if (provider.QueryService(ref service, ref asked, out var pointer) != 0 || pointer == IntPtr.Zero)
                return true;

            try
            {
                var browser = (IShellBrowserWindow)Marshal.GetObjectForIUnknown(pointer);
                return browser.GetWindow(out var tab) != 0 || tab == IntPtr.Zero || IsWindowVisible(tab);
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr result);
    }

    // IShellBrowser; only its first method (IOleWindow.GetWindow) is used.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowserWindow
    {
        [PreserveSig] int GetWindow(out IntPtr window);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
