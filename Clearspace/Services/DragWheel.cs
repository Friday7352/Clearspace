// Clearspace | NEW (scroll while dragging): hears the mouse wheel while something is being dragged.
// During a drag Windows does not deliver the wheel to the window under the pointer (the drag has the
// mouse to itself), so a list cannot be scrolled with the wheel in the ordinary way while you hold a file.
// This listens for the wheel one level lower (a low-level mouse hook) for as long as a drag lasts, and
// reports each turn with where the pointer was. MainWindow.DragScroll.cs scrolls the list under it.
// The hook is on only between On() and Off(), reads nothing but wheel turns, and passes every mouse event
// on unchanged. It also turns itself off as soon as no mouse button is held, which is how a drag that
// started in another program (and so has no "finished" call here) ends.
using System.Runtime.InteropServices;

namespace Clearspace.Services;

internal sealed class DragWheel : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    private readonly Action<int, int, int> _turned; // notches x 120 (up is positive), screen x, screen y in pixels
    private readonly Action _ended;                 // no button is held any more: asks the owner to call Off()
    private readonly HookProc _callback;            // kept in a field so it is not collected while Windows holds it
    private IntPtr _hook;

    internal DragWheel(Action<int, int, int> turned, Action ended)
    {
        _turned = turned;
        _ended = ended;
        _callback = OnMouse;
    }

    internal bool IsOn => _hook != IntPtr.Zero;

    // Safe to call when already on. If Windows refuses the hook, the wheel simply does nothing during a drag.
    internal void On()
    {
        if (_hook != IntPtr.Zero || !OperatingSystem.IsWindows())
            return;

        try
        {
            _hook = SetWindowsHookExW(WH_MOUSE_LL, _callback, GetModuleHandleW(null), 0);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            _hook = IntPtr.Zero;
        }
    }

    internal void Off()
    {
        if (_hook == IntPtr.Zero)
            return;

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    public void Dispose() => Off();

    // Called by Windows on the thread that called On(), for every mouse event anywhere. Kept short, and
    // nothing may escape it: an exception here would end the program.
    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        var hook = _hook;
        try
        {
            if (code >= 0)
            {
                if ((int)message == WM_MOUSEWHEEL)
                {
                    var info = Marshal.PtrToStructure<MouseEvent>(data);
                    _turned((short)(info.MouseData >> 16), info.X, info.Y);
                }
                else if (GetAsyncKeyState(VK_LBUTTON) >= 0 && GetAsyncKeyState(VK_RBUTTON) >= 0)
                {
                    _ended(); // both buttons are up: whatever was being dragged has been let go
                }
            }
        }
        catch (Exception)
        {
        }

        return CallNextHookEx(hook, code, message, data);
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseEvent
    {
        internal int X, Y;
        internal uint MouseData, Flags, Time;
        internal IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int hook, HookProc callback, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
}
