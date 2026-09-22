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
        CancellationToken token,
        Action<VolumeIndex>? started = null)
    {
        var index = new VolumeIndex(root, serialNumber);

        var rootIndex = index.Add(-1, root.AsSpan(), 0, 0, 0, FileAttributes.Directory);
        // NEW: expose the index while it fills, so the disk usage view can show it growing.
        // Appends publish their count last, so a concurrent reader always sees whole entries.
        started?.Invoke(index);

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


    // NEW (live index): add the contents of a folder that appeared after the last full scan
    // (created, or renamed/moved into place). The folder's own entry must already exist.
    // Each directory is enumerated under the index's WriteGate so readers see whole entries.
    internal static void ScanSubtree(VolumeIndex index, string directory, int folderIndex, CancellationToken token)
    {
        var pending = new Stack<(string Path, int Parent, int Depth)>();
        pending.Push((directory, folderIndex, 0));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (path, parent, depth) = pending.Pop();
            lock (index.WriteGate)
                Scan(index, path, parent, depth, pending, token);
        }
        index.MarkChanged();
    }

    // NEW: a folder the scanner may enter (plain folder, or a cloud-sync placeholder folder).
    internal static bool CanDescend(FileAttributes attributes, uint reparseTag)
        => (attributes & FileAttributes.Directory) != 0 &&
           ((attributes & FileAttributes.ReparsePoint) == 0 || IsCloudFilesTag(reparseTag));

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

            // CHANGED: OneDrive (and other cloud-sync providers) mark every synced folder as a
            // reparse point with a "cloud files" tag. Skipping all reparse points hid Desktop,
            // Documents, etc. whenever they were backed up to OneDrive. Cloud folders are now
            // scanned; symbolic links, junctions and mount points are still skipped (loops).
            if ((attributes & FileAttributes.Directory) != 0 &&
                depth < MaxDepth &&
                ((attributes & FileAttributes.ReparsePoint) == 0 || IsCloudFilesTag(data.dwReserved0)))
            {
                pending.Push((Join(directory, name), child, depth + 1));
            }
        }
        while (NativeMethods.FindNextFileW(handle, out data));
    }


    // NEW: IO_REPARSE_TAG_CLOUD and its variants IO_REPARSE_TAG_CLOUD_1..F (0x9000x01A).
    // For a reparse point, FindFirstFileEx reports the tag in dwReserved0.
    private static bool IsCloudFilesTag(uint tag) => (tag & 0xFFFF0FFF) == 0x9000001A;

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
