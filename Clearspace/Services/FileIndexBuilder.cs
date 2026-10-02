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

        var pending = new Stack<(string Path, int Parent)>();
        pending.Push((root, rootIndex));

        var sinceReport = 0;

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();

            var (directory, parent) = pending.Pop();
            diagnostics.Visit(directory);
            Scan(index, directory, parent, pending, token, diagnostics);
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
                Scan(index, path, parent, pending, token);
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
        Stack<(string Path, int Parent)> pending,
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
                pending.Push((Join(directory, name), child));
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
