// Clearspace | NEW (undo): Ctrl+Z and Ctrl+Y for what you do to files, as in File Explorer.
// Recorded (by FileOperationService, the New folder / New file entries and Rename several items):
//   - a move (paste after cut, dragging, the shelf, "Move to ...")  -> undo moves each item back, under its old name
//   - a copy                                                      -> undo sends the copies to the Recycle Bin
//   - a rename, one item or several at once                       -> undo gives back the old names
//   - a delete to the Recycle Bin                                 -> undo takes the items back out of it
//   - a new folder or new file                                    -> undo sends it to the Recycle Bin
// Redo does the same thing again. Where undo sent something to the Recycle Bin, redo takes it back out, so a
// redone copy is the same copy, and a new file comes back with whatever was typed into it.
// Not recorded: deleting for good (nothing to bring back), what replaced a file of the same name (the old file
// is gone), and Clearspace's own housekeeping (removing a theme file, restoring a backup) - those run inside
// UndoHistory.Pause().
// One history for the whole app, shared by every window and tab, like Explorer's. The newest 50 changes are
// kept, and it is not saved: it starts empty each time Clearspace opens.
// Each step checks the disk before it acts: an item that was moved, renamed or deleted since (by Clearspace or
// another program) is left alone, and the status line says so.
// Tests: Clearspace.Tests/UndoTests.cs.
using System.IO;

namespace Clearspace.Services;

internal static class UndoHistory
{
    internal const int MostKept = 50;

    private static readonly List<UndoStep> Undos = [];
    private static readonly List<UndoStep> Redos = [];
    private static int _paused;

    // Raised (on the thread that made the change) when what can be undone or redone changed.
    internal static event EventHandler? Changed;

    internal static bool CanUndo => Undos.Count > 0;
    internal static bool CanRedo => Redos.Count > 0;

    // "move of report.docx": for "Undo move of report.docx" in a menu. Null when there is nothing.
    internal static string? UndoWords => Undos.Count > 0 ? Undos[^1].Words : null;
    internal static string? RedoWords => Redos.Count > 0 ? Redos[^1].Words : null;

    internal static bool IsPaused => Volatile.Read(ref _paused) > 0;

    // Changes made inside `using (UndoHistory.Pause())` are not recorded.
    internal static IDisposable Pause()
    {
        Interlocked.Increment(ref _paused);
        return new Resume();
    }

    private sealed class Resume : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                Interlocked.Decrement(ref _paused);
        }
    }

    internal static void Record(UndoStep step)
    {
        if (IsPaused)
            return;

        lock (Undos)
        {
            Undos.Add(step);
            if (Undos.Count > MostKept)
                Undos.RemoveAt(0);
            Redos.Clear(); // as everywhere: a new change ends what could be redone
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    // Words for the status line: what was undone, and anything that could not be.
    internal static string Undo(IntPtr owner) => Step(Undos, Redos, owner, undo: true);

    internal static string Redo(IntPtr owner) => Step(Redos, Undos, owner, undo: false);

    internal static void Clear()
    {
        lock (Undos)
        {
            Undos.Clear();
            Redos.Clear();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static string Step(List<UndoStep> from, List<UndoStep> to, IntPtr owner, bool undo)
    {
        UndoStep step;
        lock (Undos)
        {
            if (from.Count == 0)
                return undo ? "Nothing to undo." : "Nothing to redo.";
            step = from[^1];
            from.RemoveAt(from.Count - 1);
        }

        string? problem;
        using (Pause()) // what undo does is not itself a new change
        {
            try
            {
                problem = undo ? step.Undo(owner) : step.Redo(owner);
            }
            catch (Exception error)
            {
                problem = error.Message;
            }
        }

        // It goes over to the other side even when part of it could not be done, so Ctrl+Y / Ctrl+Z can
        // still do the part that was.
        lock (Undos)
        {
            to.Add(step);
            if (to.Count > MostKept)
                to.RemoveAt(0);
        }

        Changed?.Invoke(null, EventArgs.Empty);

        var done = $"{(undo ? "Undid" : "Redid")} the {step.Words}.";
        return problem is null ? done : $"{done} {problem}";
    }

    // ---- shared by the steps

    internal static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    internal static string Name(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

    // "report.docx" or "3 items"
    internal static string What(IReadOnlyCollection<string> paths)
        => paths.Count == 1 ? Name(paths.First()) : $"{paths.Count:N0} items";

    // "2 items are no longer where they were, so they were left alone."
    internal static string? LeftAlone(int count, string why)
        => count == 0 ? null : $"{(count == 1 ? "1 item" : $"{count:N0} items")} {why}, so {(count == 1 ? "it was" : "they were")} left alone.";
}

// One change that can be undone and done again. Undo and Redo return null when everything went, or words
// saying what could not be done.
internal abstract class UndoStep
{
    internal abstract string Words { get; }
    internal abstract string? Undo(IntPtr owner);
    internal abstract string? Redo(IntPtr owner);
}

// Items moved, From -> To (To is the full new path, with the name Windows gave on a clash).
internal sealed class MovedStep(IReadOnlyList<(string From, string To)> moves) : UndoStep
{
    internal IReadOnlyList<(string From, string To)> Moves { get; } = moves;

    internal override string Words => $"move of {UndoHistory.What(Moves.Select(move => move.From).ToList())}";

    internal override string? Undo(IntPtr owner) => Carry(Moves.Select(move => (Now: move.To, Then: move.From)).ToList(), owner);

    internal override string? Redo(IntPtr owner) => Carry(Moves.Select(move => (Now: move.From, Then: move.To)).ToList(), owner);

    private static string? Carry(List<(string Now, string Then)> moves, IntPtr owner)
    {
        // Only what is still where the move left it, with nothing new in its old place.
        var doable = moves.Where(move => UndoHistory.Exists(move.Now) && !UndoHistory.Exists(move.Then)).ToList();
        var skipped = moves.Count - doable.Count;
        if (doable.Count > 0)
        {
            var result = FileOperationService.MoveExact(doable.Select(move => move.Now).ToList(), doable.Select(move => move.Then).ToList(), owner);
            if (!result.Succeeded && !result.Canceled)
                return result.Message;
        }

        return UndoHistory.LeftAlone(skipped, "had been moved, renamed or deleted since, or something else now has the name");
    }
}

// Items copied, Source -> Copy. Undo sends the copies to the Recycle Bin; redo takes them back out (or, if
// they are not there any more, copies the sources again).
internal sealed class CopiedStep(IReadOnlyList<(string Source, string Copy)> copies) : UndoStep
{
    private DateTime _recycledAt = DateTime.MinValue;

    internal IReadOnlyList<(string Source, string Copy)> Copies { get; } = copies;

    internal override string Words => $"copy of {UndoHistory.What(Copies.Select(copy => copy.Source).ToList())}";

    internal override string? Undo(IntPtr owner)
    {
        var there = Copies.Select(copy => copy.Copy).Where(UndoHistory.Exists).ToList();
        _recycledAt = DateTime.UtcNow;
        if (there.Count > 0)
        {
            var result = FileOperationService.Recycle(there, owner);
            if (!result.Succeeded && !result.Canceled)
                return result.Message;
        }

        return UndoHistory.LeftAlone(Copies.Count - there.Count, "had been moved, renamed or deleted since");
    }

    internal override string? Redo(IntPtr owner)
    {
        var again = new List<(string Source, string Copy)>();
        foreach (var copy in Copies)
        {
            if (UndoHistory.Exists(copy.Copy))
                continue;
            if (RecycleBin.Restore(copy.Copy, _recycledAt) is not null && UndoHistory.Exists(copy.Source))
                again.Add(copy); // not in the Recycle Bin any more: copy it again
        }

        if (again.Count > 0)
        {
            var result = FileOperationService.CopyExact(again.Select(copy => copy.Source).ToList(), again.Select(copy => copy.Copy).ToList(), owner);
            if (!result.Succeeded && !result.Canceled)
                return result.Message;
        }

        var missing = Copies.Count(copy => !UndoHistory.Exists(copy.Copy));
        return UndoHistory.LeftAlone(missing, "could not be brought back (not in the Recycle Bin, and the original is gone)");
    }
}

// Items renamed, From -> To: one, or several at once (Rename several items). Done directly on the disk, with
// a stop at a temporary name when the names are swapped round.
internal sealed class RenamedStep(IReadOnlyList<(string From, string To)> renames) : UndoStep
{
    internal IReadOnlyList<(string From, string To)> Renames { get; } = renames;

    internal override string Words => Renames.Count == 1
        ? $"rename of {UndoHistory.Name(Renames[0].From)}"
        : $"rename of {Renames.Count:N0} items";

    internal override string? Undo(IntPtr owner) => RenameAll(Renames.Select(rename => (Now: rename.To, Then: rename.From)).ToList());

    internal override string? Redo(IntPtr owner) => RenameAll(Renames.Select(rename => (Now: rename.From, Then: rename.To)).ToList());

    internal static string? RenameAll(List<(string Now, string Then)> renames, Action<string, string>? move = null)
    {
        move ??= MoveOnDisk;
        var sources = renames.Select(rename => rename.Now).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // An item can go back to its old name when that name is free, or is about to be freed by another
        // item of this same step (a swap), or differs only in capitals from its own.
        var doable = renames.Where(rename => UndoHistory.Exists(rename.Now) &&
            (!UndoHistory.Exists(rename.Then) || sources.Contains(rename.Then))).ToList();
        var skipped = renames.Count - doable.Count;

        var parked = new List<(string Parked, string Then)>();
        try
        {
            foreach (var (now, then) in doable)
            {
                var stop = Path.Combine(Path.GetDirectoryName(now)!, $"~clearspace-undo-{Guid.NewGuid():N}");
                move(now, stop);
                parked.Add((stop, then));
            }

            foreach (var (stop, then) in parked.ToList())
            {
                move(stop, then);
                parked.Remove((stop, then));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Whatever is still at a temporary name goes back to the name it had before this undo.
            foreach (var (stop, then) in parked)
            {
                var before = doable.First(rename => rename.Then == then).Now;
                try { move(stop, before); }
                catch (Exception) { }
            }

            return error.Message;
        }

        return UndoHistory.LeftAlone(skipped, "had been renamed, moved or deleted since, or something else now has the name");
    }

    private static void MoveOnDisk(string from, string to)
    {
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);
    }
}

// Items sent to the Recycle Bin at `at` (UTC). Undo takes them back out; redo sends them again.
internal sealed class RecycledStep(IReadOnlyList<string> paths, DateTime at) : UndoStep
{
    private DateTime _at = at;

    internal IReadOnlyList<string> Paths { get; } = paths;

    internal override string Words => $"delete of {UndoHistory.What(Paths)}";

    internal override string? Undo(IntPtr owner)
    {
        var problems = RecycleBin.RestoreAll(Paths, _at); // the Recycle Bin is read once, however many items
        return problems.Count == 0 ? null : string.Join(" ", problems.Take(3)) + (problems.Count > 3 ? $" (and {problems.Count - 3} more)" : "");
    }

    internal override string? Redo(IntPtr owner)
    {
        var there = Paths.Where(UndoHistory.Exists).ToList();
        _at = DateTime.UtcNow;
        if (there.Count > 0)
        {
            var result = FileOperationService.Recycle(there, owner);
            if (!result.Succeeded && !result.Canceled)
                return result.Message;
        }

        return UndoHistory.LeftAlone(Paths.Count - there.Count, "had been moved or deleted since");
    }
}

// A new folder or file (New folder, New > Text Document ...). Undo sends it to the Recycle Bin, so redo can
// bring it back with whatever was put in it.
internal sealed class CreatedStep(string path) : UndoStep
{
    private DateTime _recycledAt = DateTime.MinValue;

    internal string Path { get; } = path;

    internal override string Words => $"new {(Directory.Exists(Path) ? "folder" : "item")} {UndoHistory.Name(Path)}";

    internal override string? Undo(IntPtr owner)
    {
        if (!UndoHistory.Exists(Path))
            return UndoHistory.LeftAlone(1, "had been renamed, moved or deleted since");

        _recycledAt = DateTime.UtcNow;
        var result = FileOperationService.Recycle([Path], owner);
        return result.Succeeded || result.Canceled ? null : result.Message;
    }

    internal override string? Redo(IntPtr owner)
        => UndoHistory.Exists(Path) ? null : RecycleBin.Restore(Path, _recycledAt);
}
