// Clearspace | Background file-index builder.

using System.IO;
using Clearspace.Native;

namespace Clearspace.Services;

// Iterative traversal has no arbitrary depth cap; reparse-point checks prevent link cycles.
internal static class FileIndexBuilder
{

    // Background mode lowers I/O priority as well as CPU priority.
    public static void EnterBackgroundMode()
        => NativeMethods.SetThreadPriority(
            NativeMethods.GetCurrentThread(),
            NativeMethods.THREAD_MODE_BACKGROUND_BEGIN);

    public static void ExitBackgroundMode()
        => NativeMethods.SetThreadPriority(
            NativeMethods.GetCurrentThread(),
            NativeMethods.THREAD_MODE_BACKGROUND_END);


    // CHANGED (round 65, memory): breadth-first. Folders are opened in the order they were added, so every
    // folder's children sit next to each other and the index needs no child-link arrays (VolumeIndex).
    // The queue holds entry numbers (4 bytes a folder), and a folder's path is rebuilt from the index when
    // its turn comes, instead of keeping a path string for every folder waiting to be opened.
    public static VolumeIndex? Build(
        string root,
        uint serialNumber,
        long maxBytes,
        Action<int>? progress,
        CancellationToken token,
        Action<VolumeIndex>? started = null)
    {
        token.ThrowIfCancellationRequested();
        var index = new VolumeIndex(root, serialNumber);
        var diagnostics = index.ScanDetails = new IndexScanTracker();

        var rootIndex = index.Add(-1, root.AsSpan(), 0, 0, 0, FileAttributes.Directory);
        // NEW: expose the index while it fills, so the disk usage view can show it growing.
        // Appends publish their count last, so a concurrent reader always sees whole entries.
        started?.Invoke(index);

        var folders = new Queue<int>();
        folders.Enqueue(rootIndex);

        var sinceReport = 0;

        while (folders.Count > 0)
        {
            token.ThrowIfCancellationRequested();

            var parent = folders.Dequeue();
            var directory = parent == rootIndex ? root : index.GetPath(parent);
            diagnostics.Visit(directory);
            Scan(index, directory, parent, null, folders, token, diagnostics);
            diagnostics.FinishFolder();

            if (index.EstimatedBytes > maxBytes)
                return null;

            if (++sinceReport >= 64)
            {
                sinceReport = 0;
                progress?.Invoke(index.Count);
            }
        }

        index.Compact();
        diagnostics.Complete();
        progress?.Invoke(index.Count);
        return index;
    }

    // NEW (round 65, memory): what a full walk would put in the index - files, folders, bytes - without
    // keeping any of it. The one-time file-table check used to build a whole second index just to count it.
    // The same folders are entered and skipped as in Build; the numbers match FileTableIndex.Summarize.
    public static FileTableIndex.Summary Summarize(string root, CancellationToken token)
    {
        long files = 0, folders = 0, bytes = 0;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            var pattern = directory.EndsWith(Path.DirectorySeparatorChar) ? directory + "*" : directory + @"\*";

            using var handle = NativeMethods.FindFirstFileExW(
                pattern,
                NativeMethods.FINDEX_INFO_LEVELS.FindExInfoBasic,
                out var data,
                NativeMethods.FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                IntPtr.Zero,
                NativeMethods.FIND_FIRST_EX_LARGE_FETCH);

            if (handle.IsInvalid)
            {
                var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (directory == root && error is not (NativeMethods.ERROR_FILE_NOT_FOUND or NativeMethods.ERROR_NO_MORE_FILES))
                    throw new IOException($"Cannot enumerate {directory}.", new System.ComponentModel.Win32Exception(error));
                continue;
            }

            do
            {
                var name = data.cFileName;

                if (name is "." or "..")
                    continue;

                var attributes = data.dwFileAttributes;

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    folders++;
                    if (CanDescend(attributes, data.dwReserved0))
                        pending.Push(Join(directory, name));
                }
                else
                {
                    files++;
                    // As stored in the index (sizes are kept up to IndexEntry.MaxSize).
                    bytes += Math.Min(((long)data.nFileSizeHigh << 32) | data.nFileSizeLow, IndexEntry.MaxSize);
                }
            }
            while (NativeMethods.FindNextFileW(handle, out data));
        }

        return new FileTableIndex.Summary(files, folders, bytes);
    }


    // NEW (live index): add the contents of a folder that appeared after the last full scan
    // (created, or renamed/moved into place). The folder's own entry must already exist.
    // Each directory is enumerated under the index's WriteGate so readers see whole entries.
    internal static void ScanSubtree(VolumeIndex index, string directory, int folderIndex, CancellationToken token)
    {
        var pending = new Stack<(string Path, int Parent)>();
        pending.Push((directory, folderIndex));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (path, parent) = pending.Pop();
            lock (index.WriteGate)
                Scan(index, path, parent, pending, null, token);
        }
        index.MarkChanged();
    }

    // NEW: a folder the scanner may enter (plain folder, or a cloud-sync placeholder folder).
    internal static bool CanDescend(FileAttributes attributes, uint reparseTag)
        => (attributes & FileAttributes.Directory) != 0 &&
           ((attributes & FileAttributes.ReparsePoint) == 0 || IsCloudFilesTag(reparseTag));

    // CHANGED (round 65): folders to open next go either on `pending` (with their path, depth-first, for a
    // live subtree) or on `queue` (by entry number, breadth-first, for a full build).
    private static void Scan(
        VolumeIndex index,
        string directory,
        int parent,
        Stack<(string Path, int Parent)>? pending,
        Queue<int>? queue,
        CancellationToken token,
        IndexScanTracker? diagnostics = null)
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
        {
            // Do not publish an empty replacement when a drive disappeared during recovery.
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (parent == 0 && error is not (NativeMethods.ERROR_FILE_NOT_FOUND or NativeMethods.ERROR_NO_MORE_FILES))
                throw new IOException($"Cannot enumerate {directory}.", new System.ComponentModel.Win32Exception(error));
            if (error is not (NativeMethods.ERROR_FILE_NOT_FOUND or NativeMethods.ERROR_NO_MORE_FILES))
                diagnostics?.Skip(directory, new System.ComponentModel.Win32Exception(error).Message);
            return;
        }

        do
        {
            token.ThrowIfCancellationRequested();

            var name = data.cFileName;

            if (name is "." or "..")
                continue;

            // NEW (round 65): a name longer than the index accepts (Windows' own limit is 255) is skipped
            // rather than stopping the whole scan.
            if (name.Length > VolumeIndex.MaxNameChars)
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
            if (CanDescend(attributes, data.dwReserved0))
            {
                if (queue is not null) queue.Enqueue(child);
                else pending?.Push((Join(directory, name), child));
                diagnostics?.DiscoverFolder();
            }
            else if ((attributes & FileAttributes.Directory) != 0)
                diagnostics?.Skip(Join(directory, name), "Folder link not followed (prevents loops)");
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
