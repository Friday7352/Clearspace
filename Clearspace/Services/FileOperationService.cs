// Clearspace | Windows shell file operations.
// CHANGED (undo): Delete (to the Recycle Bin), Copy, Move and Rename now tell UndoHistory what they did,
// so Ctrl+Z can put it back. Every caller gets this without changing (paste, drag and drop, the shelf,
// tabs, the second file pane, Find duplicates, the Downloads strip ...). What is recorded is read off the
// disk after Windows has finished, so a copy or move that was stopped halfway is undone for the part that
// happened. Permanent deletes are never recorded: there is nothing to bring back.
// The undo itself goes through MoveExact / CopyExact / Recycle below, which are not recorded.

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

        // CHANGED (undo): a delete to the Recycle Bin can be undone; what went there is noted with the time.
        var since = DateTime.UtcNow;
        var result = Run(FileOperationKind.Delete, paths, null, flags, owner);
        if (!permanent && !UndoHistory.IsPaused)
        {
            var recycled = paths.Select(Trim).Where(path => !Exists(path) && HasRecycleBin(path)).ToList();
            if (recycled.Count > 0)
                UndoHistory.Record(new RecycledStep(recycled, since));
        }

        return result;
    }

    // CHANGED (pre-release review): keepBoth gives a copy a new name ("name - Copy") when something with its
    // name is already in the destination, instead of asking or refusing. Paste uses it for copying files
    // into the folder they are already in, which Windows otherwise refuses outright.
    public static FileOperationResult Copy(IReadOnlyList<string> paths, string destinationFolder, IntPtr owner, bool keepBoth = false)
    {
        // CHANGED (undo): what was already in the destination is noted first. A copy that replaced
        // something there is left out of the undo (the file it replaced can't be brought back), and so is
        // anything that was not copied after all.
        var targets = paths.Select(path => Path.Combine(destinationFolder, Path.GetFileName(Trim(path)))).ToList();
        var there = targets.Where(Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = Run(FileOperationKind.Copy, paths, destinationFolder,
            keepBoth ? (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_RENAMEONCOLLISION) : NativeMethods.FOF_ALLOWUNDO,
            owner, mappings: renamed);

        if (!UndoHistory.IsPaused)
        {
            var copies = new List<(string Source, string Copy)>();
            for (var i = 0; i < paths.Count; i++)
            {
                var source = Trim(paths[i]);
                var landed = renamed.TryGetValue(source, out var newName) ? newName : targets[i];
                if (Exists(landed) && (renamed.ContainsKey(source) || !there.Contains(landed)))
                    copies.Add((source, landed));
            }

            if (copies.Count > 0)
                UndoHistory.Record(new CopiedStep(copies));
        }

        return result;
    }

    // CHANGED (downloads): a move out of a Downloads folder is noted, so the same folder can be offered for
    // the next file like it (Services/FilingMemory.cs). The move itself is as before.
    public static FileOperationResult Move(IReadOnlyList<string> paths, string destinationFolder, IntPtr owner)
    {
        var filing = FilingMemory.Watch(paths, destinationFolder);
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // NEW (undo)
        var result = Run(FileOperationKind.Move, paths, destinationFolder, NativeMethods.FOF_ALLOWUNDO, owner, mappings: renamed);
        filing?.Moved(); // counts only the files that really left, so a move stopped halfway is handled too
        if (result.Succeeded)
            FilingMemory.NoteDestination(destinationFolder); // NEW (recommended folders): the folder chooser offers recent folders

        // NEW (undo): each item that left its old place and is in the new one.
        if (!UndoHistory.IsPaused)
        {
            var moves = paths.Select(Trim)
                .Select(source => (From: source, To: renamed.TryGetValue(source, out var newName) ? newName : Path.Combine(destinationFolder, Path.GetFileName(source))))
                .Where(move => !Exists(move.From) && Exists(move.To))
                .ToList();
            if (moves.Count > 0)
                UndoHistory.Record(new MovedStep(moves));
        }

        return result;
    }

    public static FileOperationResult Rename(string path, string newFullPath, IntPtr owner)
    {
        var result = Run(FileOperationKind.Rename, [path], newFullPath, NativeMethods.FOF_ALLOWUNDO, owner);

        // NEW (undo): only the capitals changing leaves the old path "there" too (Windows ignores case).
        var from = Trim(path);
        var to = Trim(newFullPath);
        var capitalsOnly = from.Equals(to, StringComparison.OrdinalIgnoreCase);
        if (result.Succeeded && !UndoHistory.IsPaused && Exists(to) && (capitalsOnly || !Exists(from)) &&
            !from.Equals(to, StringComparison.Ordinal))
            UndoHistory.Record(new RenamedStep([(from, to)]));

        return result;
    }

    // ---- NEW (undo): what undo and redo use. Not recorded themselves.

    // Moves each item to its own exact path (`to[i]` for `from[i]`): back to where it was, under the name it
    // had. A folder that has gone in the meantime is made again without asking.
    internal static FileOperationResult MoveExact(IReadOnlyList<string> from, IReadOnlyList<string> to, IntPtr owner)
        => RunEach(FileOperationKind.Move, from, to, owner);

    // Copies each item to its own exact path; redo of a copy whose copy is no longer in the Recycle Bin.
    internal static FileOperationResult CopyExact(IReadOnlyList<string> from, IReadOnlyList<string> to, IntPtr owner)
        => RunEach(FileOperationKind.Copy, from, to, owner);

    // Sends items to the Recycle Bin without asking (undo was asked for). Windows still warns when one
    // can't go there and would be deleted for good.
    internal static FileOperationResult Recycle(IReadOnlyList<string> paths, IntPtr owner)
        => Run(FileOperationKind.Delete, paths, null,
            (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_WANTNUKEWARNING), owner);

    private static FileOperationResult RunEach(FileOperationKind operation, IReadOnlyList<string> from, IReadOnlyList<string> to, IntPtr owner)
    {
        if (from.Count == 0 || from.Count != to.Count)
            return FileOperationResult.InvalidInput(operation, "No files or folders were given.");

        if (from.Any(path => !IsValidShellPath(path)) || to.Any(path => !IsValidShellPath(path)))
            return FileOperationResult.InvalidInput(operation,
                "Use full file or folder paths without wildcards or embedded null characters.");

        return Execute(operation, ToDoubleNullTerminated(from), ToDoubleNullTerminated(to),
            (ushort)(NativeMethods.FOF_MULTIDESTFILES | NativeMethods.FOF_NOCONFIRMMKDIR), owner, null, null);
    }

    private static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    // NEW (duplicates): whether a deleted file from this path can be expected to land in the Recycle Bin.
    // Fixed drives have one; network folders do not, and removable drives may not. Windows asks before
    // deleting for good either way (FOF_WANTNUKEWARNING); this is for wording the app's own question.
    internal static bool HasRecycleBin(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && !root.StartsWith(@"\\", StringComparison.Ordinal) &&
                   new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal delegate int ShellOperation(ref NativeMethods.SHFILEOPSTRUCT operation);

    // Injection is per call, so tests can exercise the native boundary without changing files
    // or replacing global application state.
    // CHANGED (undo): `mappings`, when given, is filled with the new name of each item Windows had to rename
    // because its name was taken (old path -> new path).
    internal static FileOperationResult Run(FileOperationKind operation, IReadOnlyList<string> from,
        string? to, ushort flags, IntPtr owner, ShellOperation? invoke = null, Dictionary<string, string>? mappings = null)
    {
        if (from.Count == 0)
            return FileOperationResult.InvalidInput(operation, "No files or folders were selected.");

        if (from.Any(path => !IsValidShellPath(path)) ||
            (operation != FileOperationKind.Delete && !IsValidShellPath(to)))
            return FileOperationResult.InvalidInput(operation,
                "Use full file or folder paths without wildcards or embedded null characters.");

        return Execute(operation, ToDoubleNullTerminated(from), to is null ? null : ToDoubleNullTerminated([to]),
            flags, owner, invoke, mappings);
    }

    // CHANGED (undo): the call itself, shared by Run and RunEach.
    private static FileOperationResult Execute(FileOperationKind operation, string from, string? to, ushort flags,
        IntPtr owner, ShellOperation? invoke, Dictionary<string, string>? mappings)
    {
        if (mappings is not null)
            flags |= NativeMethods.FOF_WANTMAPPINGHANDLE;

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
            pFrom = from,
            pTo = to,
            fFlags = flags,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = null
        };

        var result = (invoke ?? NativeMethods.SHFileOperationW)(ref op);
        if (mappings is not null)
            ReadMappings(op.hNameMappings, mappings);
        return FileOperationResult.FromShellResult(operation, result, op.fAnyOperationsAborted);
    }

    // NEW (undo): the list Windows hands back with FOF_WANTMAPPINGHANDLE. Its layout is
    // { UINT count; SHNAMEMAPPINGW* entries } and each entry { LPWSTR old; LPWSTR new; int; int }.
    private static void ReadMappings(IntPtr handle, Dictionary<string, string> into)
    {
        if (handle == IntPtr.Zero)
            return;

        try
        {
            var count = Marshal.ReadInt32(handle);
            var entries = Marshal.ReadIntPtr(handle, IntPtr.Size); // the UINT is padded to a pointer's width
            var size = 2 * IntPtr.Size + 2 * sizeof(int);
            for (var i = 0; i < count && entries != IntPtr.Zero; i++)
            {
                var entry = entries + i * size;
                var oldPath = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry));
                var newPath = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, IntPtr.Size));
                if (!string.IsNullOrEmpty(oldPath) && !string.IsNullOrEmpty(newPath))
                    into[Trim(oldPath)] = Trim(newPath);
            }
        }
        catch (Exception)
        {
            // only the undo record misses out; the files themselves were handled by Windows
        }
        finally
        {
            NativeMethods.SHFreeNameMappings(handle);
        }
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
