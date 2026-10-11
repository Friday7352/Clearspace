// Clearspace | NEW (undo): Undo (Ctrl+Z) and Redo (Ctrl+Y) of changes to files. The history is
// Services/UndoHistory.cs. They work in the file list, not while typing in a box (a rename box, the address
// bar or the search box undo their own typing as usual).
using System.Windows.Input;
using Clearspace.Services;

namespace Clearspace.Commands.Actions;

public sealed class UndoAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.Undo;
    public string Label => UndoHistory.UndoWords is { } words ? $"Undo {words}" : "Undo";
    public string Description => "Undo the last change to your files";
    public string Glyph => "";
    public HotKey HotKey => new(Key.Z, ModifierKeys.Control);
    public bool IsExecutable => UndoHistory.CanUndo;

    public Task ExecuteAsync(object? parameter = null)
    {
        // The open folder follows the disk by itself, so no refresh: that would also replace these words.
        context.Notify?.Invoke(UndoHistory.Undo(context.OwnerHandle));
        return Task.CompletedTask;
    }
}

public sealed class RedoAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.Redo;
    public string Label => UndoHistory.RedoWords is { } words ? $"Redo {words}" : "Redo";
    public string Description => "Do again what was just undone";
    public string Glyph => "";
    public HotKey HotKey => new(Key.Y, ModifierKeys.Control);
    public bool IsExecutable => UndoHistory.CanRedo;

    public Task ExecuteAsync(object? parameter = null)
    {
        // The open folder follows the disk by itself, so no refresh: that would also replace these words.
        context.Notify?.Invoke(UndoHistory.Redo(context.OwnerHandle));
        return Task.CompletedTask;
    }
}
