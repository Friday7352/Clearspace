// Clearspace | Windows shell file operations.

using System.Runtime.InteropServices;
using System.IO;
using Clearspace.Models;
using Clearspace.Native;

namespace Clearspace.Services;

public static class FileOperationService
{
    // Only used after the disk usage window's explicit, default-No confirmation.
    internal static FileOperationResult DeletePermanentlyConfirmed(IReadOnlyList<string> paths, IntPtr owner,
        ShellOperation? invoke = null)
        => Run(FileOperationKind.Delete, paths, null, NativeMethods.FOF_NOCONFIRMATION, owner, invoke);

    public static FileOperationResult Delete(IReadOnlyList<string> paths, IntPtr owner, bool permanent = false)
    {
        ushort flags = permanent
            ? NativeMethods.FOF_WANTNUKEWARNING
            : (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_WANTNUKEWARNING);

        return Run(FileOperationKind.Delete, paths, null, flags, owner);
    }

    public static FileOperationResult Copy(IReadOnlyList<string> paths, string destinationFolder, IntPtr owner)
        => Run(FileOperationKind.Copy, paths, destinationFolder, NativeMethods.FOF_ALLOWUNDO, owner);

    public static FileOperationResult Move(IReadOnlyList<string> paths, string destinationFolder, IntPtr owner)
        => Run(FileOperationKind.Move, paths, destinationFolder, NativeMethods.FOF_ALLOWUNDO, owner);

    public static FileOperationResult Rename(string path, string newFullPath, IntPtr owner)
        => Run(FileOperationKind.Rename, [path], newFullPath, NativeMethods.FOF_ALLOWUNDO, owner);

    internal delegate int ShellOperation(ref NativeMethods.SHFILEOPSTRUCT operation);

    // Injection is per call, so tests can exercise the native boundary without changing files
    // or replacing global application state.
    internal static FileOperationResult Run(FileOperationKind operation, IReadOnlyList<string> from,
        string? to, ushort flags, IntPtr owner, ShellOperation? invoke = null)
    {
        if (from.Count == 0)
            return FileOperationResult.InvalidInput(operation, "No files or folders were selected.");

        if (from.Any(path => !IsValidShellPath(path)) ||
            (operation != FileOperationKind.Delete && !IsValidShellPath(to)))
            return FileOperationResult.InvalidInput(operation,
                "Use full file or folder paths without wildcards or embedded null characters.");

        var op = new NativeMethods.SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = operation switch
            {
                FileOperationKind.Copy => NativeMethods.FO_COPY,
                FileOperationKind.Move => NativeMethods.FO_MOVE,
                FileOperationKind.Rename => NativeMethods.FO_RENAME,
                FileOperationKind.Delete => NativeMethods.FO_DELETE,
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            },
            pFrom = ToDoubleNullTerminated(from),
            pTo = to is null ? null : ToDoubleNullTerminated([to]),
            fFlags = flags,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = null
        };

        var result = (invoke ?? NativeMethods.SHFileOperationW)(ref op);
        return FileOperationResult.FromShellResult(operation, result, op.fAnyOperationsAborted);
    }

    private static bool IsValidShellPath(string? path)
        => !string.IsNullOrWhiteSpace(path) && path.IndexOfAny(['\0', '*', '?']) < 0 && Path.IsPathFullyQualified(path);

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
