// Clearspace | Background file-index builder.

using System.IO;
using Clearspace.Native;

namespace Clearspace.Services;

// CS499: Indexing uses a fixed background priority, has no partial fallback at the memory limit, and has no automated tests.
internal static class FileIndexBuilder
{
    private const int MaxDepth = 32;

    // Background mode lowers I/O priority as well as CPU priority.
    public static void EnterBackgroundMode()
        => NativeMethods.SetThreadPriority(
            NativeMethods.GetCurrentThread(),
            NativeMethods.THREAD_MODE_BACKGROUND_BEGIN);

    public static void ExitBackgroundMode()
        => NativeMethods.SetThreadPriority(
            NativeMethods.GetCurrentThread(),
            NativeMethods.THREAD_MODE_BACKGROUND_END);


    public static VolumeIndex? Build(
        string root,
        uint serialNumber,
        long maxBytes,
        Action<int>? progress,
        CancellationToken token)
    {
        var index = new VolumeIndex(root, serialNumber);

        var rootIndex = index.Add(-1, root.AsSpan(), 0, 0, 0, FileAttributes.Directory);

        var pending = new Stack<(string Path, int Parent, int Depth)>();
        pending.Push((root, rootIndex, 0));

        var sinceReport = 0;

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();

            var (directory, parent, depth) = pending.Pop();
            Scan(index, directory, parent, depth, pending, token);

            if (index.EstimatedBytes > maxBytes)
                return null;

            if (++sinceReport >= 64)
            {
                sinceReport = 0;
                progress?.Invoke(index.Count);
            }
        }

        index.Compact();
        progress?.Invoke(index.Count);
        return index;
    }


    private static void Scan(
        VolumeIndex index,
        string directory,
        int parent,
        int depth,
        Stack<(string Path, int Parent, int Depth)> pending,
        CancellationToken token)
    {
        var pattern = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory + "*"
            : directory + @"\*";

        using var handle = NativeMethods.FindFirstFileExW(
            pattern,
            NativeMethods.FINDEX_INFO_LEVELS.FindExInfoBasic,
            out var data,
            NativeMethods.FINDEX_SEARCH_OPS.FindExSearchNameMatch,
            IntPtr.Zero,
            NativeMethods.FIND_FIRST_EX_LARGE_FETCH);

        if (handle.IsInvalid)
            return;

        do
        {
            token.ThrowIfCancellationRequested();

            var name = data.cFileName;

            if (name is "." or "..")
                continue;

            var attributes = data.dwFileAttributes;
            var size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;

            var child = index.Add(
                parent,
                name.AsSpan(),
                size,
                data.ftLastWriteTime.ToLong(),
                data.ftCreationTime.ToLong(),
                attributes);

            if ((attributes & FileAttributes.Directory) != 0 &&
                depth < MaxDepth &&
                (attributes & FileAttributes.ReparsePoint) == 0)
            {
                pending.Push((Join(directory, name), child, depth + 1));
            }
        }
        while (NativeMethods.FindNextFileW(handle, out data));
    }


    private static string Join(string directory, string name)
        => directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory + name
            : directory + Path.DirectorySeparatorChar + name;

    public static uint GetSerialNumber(string root)
    {
        try
        {
            return NativeMethods.GetVolumeInformationW(
                root, IntPtr.Zero, 0, out var serial, out _, out _, IntPtr.Zero, 0)
                ? serial
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
