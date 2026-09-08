// Clearspace | Windows shell file operations.

using System.Runtime.InteropServices;
using Clearspace.Native;

namespace Clearspace.Services;

// CS499: File operations do not protect file contents, hide Windows error details, and lack automated tests.
public static class FileOperationService
{

    public static bool Delete(IReadOnlyList<string> paths, IntPtr owner, bool permanent = false)
    {
        if (paths.Count == 0)
            return false;

        ushort flags = permanent
            ? NativeMethods.FOF_WANTNUKEWARNING
            : (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_WANTNUKEWARNING);

        return Run(NativeMethods.FO_DELETE, paths, null, flags, owner);
    }

    public static bool Copy(IReadOnlyList<string> paths, string destinationFolder, IntPtr owner)
        => Run(NativeMethods.FO_COPY, paths, destinationFolder, NativeMethods.FOF_ALLOWUNDO, owner);

    public static bool Move(IReadOnlyList<string> paths, string destinationFolder, IntPtr owner)
        => Run(NativeMethods.FO_MOVE, paths, destinationFolder, NativeMethods.FOF_ALLOWUNDO, owner);

    public static bool Rename(string path, string newFullPath, IntPtr owner)
        => Run(NativeMethods.FO_RENAME, [path], newFullPath, NativeMethods.FOF_ALLOWUNDO, owner);
    // CS499: This boolean result hides the Windows error code.
    private static bool Run(uint operation, IReadOnlyList<string> from, string? to, ushort flags, IntPtr owner)
    {
        var op = new NativeMethods.SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = operation,
            pFrom = ToDoubleNullTerminated(from),
            pTo = to is null ? null : ToDoubleNullTerminated([to]),
            fFlags = flags,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = null
        };

        var result = NativeMethods.SHFileOperationW(ref op);
        return result == 0 && !op.fAnyOperationsAborted;
    }

    // The shell API expects a null-separated list with one extra null at the end.
    private static string ToDoubleNullTerminated(IReadOnlyList<string> paths)
        => string.Join('\0', paths) + "\0\0";


    public static bool ShowProperties(string path, IntPtr owner) => InvokeVerb("properties", path, owner);

    public static bool OpenWith(string path, IntPtr owner) => InvokeVerb("openas", path, owner);

    private static bool InvokeVerb(string verb, string path, IntPtr owner)
    {
        var info = new NativeMethods.SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.SHELLEXECUTEINFO>(),
            fMask = NativeMethods.SEE_MASK_INVOKEIDLIST,
            hwnd = owner,
            lpVerb = verb,
            lpFile = path,
            nShow = NativeMethods.SW_SHOW
        };

        return NativeMethods.ShellExecuteExW(ref info);
    }
}
