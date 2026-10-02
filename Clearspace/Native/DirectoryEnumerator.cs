// Clearspace | Fast native directory enumeration.

using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace.Native;

internal static class DirectoryEnumerator
{
    public static IEnumerable<FileSystemItem> Enumerate(
        string directory,
        bool showHidden,
        CancellationToken cancellationToken)
    {
        var pattern = Path.Combine(directory, "*");

        var inCloudRoot = CloudStorageService.IsCloudPath(directory);

        using var handle = NativeMethods.FindFirstFileExW(
            pattern,
            NativeMethods.FINDEX_INFO_LEVELS.FindExInfoBasic,
            out var data,
            NativeMethods.FINDEX_SEARCH_OPS.FindExSearchNameMatch,
            IntPtr.Zero,
            NativeMethods.FIND_FIRST_EX_LARGE_FETCH);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();

            if (error is NativeMethods.ERROR_FILE_NOT_FOUND or NativeMethods.ERROR_NO_MORE_FILES)
                yield break;

            throw Translate(error, directory);
        }

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (data.cFileName is "." or "..")
                continue;

            var attributes = data.dwFileAttributes;

            if (!showHidden &&
                (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                continue;

            yield return FileSystemItem.FromFindData(directory, in data, inCloudRoot);
        }
        while (NativeMethods.FindNextFileW(handle, out data));
    }

    public static IEnumerable<FileSystemItem> EnumerateTree(
        string root,
        bool showHidden,
        CancellationToken cancellationToken,
        int maxDepth = 24)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (directory, depth) = pending.Pop();
            List<FileSystemItem> entries;

            try
            {
                entries = Enumerate(directory, showHidden, cancellationToken).ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var item in entries)
            {
                yield return item;

                if (item.IsFolder &&
                    depth < maxDepth &&
                    (item.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push((item.FullPath, depth + 1));
                }
            }
        }
    }

    public static int CountEntries(string directory, bool showHidden, CancellationToken cancellationToken)
    {
        var count = 0;

        try
        {
            foreach (var _ in Enumerate(directory, showHidden, cancellationToken))
                count++;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return 0;
        }

        return count;
    }

    private static Exception Translate(int error, string directory) => error switch
    {
        NativeMethods.ERROR_ACCESS_DENIED =>
            new UnauthorizedAccessException($"Access to '{directory}' is denied."),
        NativeMethods.ERROR_PATH_NOT_FOUND or NativeMethods.ERROR_INVALID_NAME =>
            new DirectoryNotFoundException($"Could not find '{directory}'."),
        NativeMethods.ERROR_NOT_READY =>
            new IOException("The drive is not ready."),
        NativeMethods.ERROR_BAD_NETPATH =>
            new IOException("The network location is unavailable."),
        _ => new IOException(new Win32Exception(error).Message, error)
    };
}
