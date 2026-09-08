// Clearspace | Selection command actions.

using System.Windows.Input;

namespace Clearspace.Commands.Actions;

public sealed class SelectAllAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.SelectAll;
    public string Label => "Select all";
    public string Description => "Select every item in this folder";
    public string Glyph => "\uE8B3";
    public HotKey HotKey => new(Key.A, ModifierKeys.Control);

    public Task ExecuteAsync(object? parameter = null)
    {
        context.SelectAll?.Invoke();
        return Task.CompletedTask;
    }
}

public sealed class ClearSelectionAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.ClearSelection;
    public string Label => "Clear selection";
    public string Description => "Deselect everything";
    public string Glyph => "\uE8E6";
    public HotKey HotKey => new(Key.Escape);
    public bool IsExecutable => context.HasSelection;

    public Task ExecuteAsync(object? parameter = null)
    {
        context.ClearSelection?.Invoke();
        return Task.CompletedTask;
    }
}

public sealed class InvertSelectionAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.InvertSelection;
    public string Label => "Invert selection";
    public string Description => "Select what is not selected";
    public string Glyph => "\uE8B6";
    public HotKey HotKey => new(Key.I, ModifierKeys.Control | ModifierKeys.Shift);

    public Task ExecuteAsync(object? parameter = null)
    {
        context.InvertSelection?.Invoke();
        return Task.CompletedTask;
    }
}
