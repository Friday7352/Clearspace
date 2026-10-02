// Clearspace | NEW (tabs): tab command actions. NEW (new window): and the two window actions, at the end.
// The same shortcuts as File Explorer and browsers: Ctrl+T new tab, Ctrl+W close tab, Ctrl+Tab and
// Ctrl+Shift+Tab next / previous tab, Ctrl+Shift+T reopen the tab closed last. Ctrl+1 ... Ctrl+9 (jump to
// a tab by position) are nine keys for one job, so MainWindow.Tabs.cs handles those directly.

using System.Windows.Input;

namespace Clearspace.Commands.Actions;

public sealed class NewTabAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.NewTab;
    public string Label => "New tab";
    public string Description => "Open a new tab";
    public string Glyph => "\uE710";
    public HotKey HotKey => new(Key.T, ModifierKeys.Control);
    public bool IsExecutable => context.Tabs is not null;

    public Task ExecuteAsync(object? parameter = null)
    {
        context.Tabs?.Open();
        return Task.CompletedTask;
    }
}

public sealed class CloseTabAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.CloseTab;
    public string Label => "Close tab";
    public string Description => "Close this tab (the window, when it is the only tab)";
    public string Glyph => "\uE711";
    public HotKey HotKey => new(Key.W, ModifierKeys.Control);
    public bool IsExecutable => context.Tabs is not null;

    public Task ExecuteAsync(object? parameter = null)
    {
        if (context.Tabs is { } tabs)
            tabs.Close(tabs.Active);

        return Task.CompletedTask;
    }
}

public sealed class NextTabAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.NextTab;
    public string Label => "Next tab";
    public string Description => "Switch to the tab on the right";
    public HotKey HotKey => new(Key.Tab, ModifierKeys.Control);
    public bool IsExecutable => context.Tabs is { Count: > 1 };

    public Task ExecuteAsync(object? parameter = null)
    {
        context.Tabs?.Cycle(1);
        return Task.CompletedTask;
    }
}

public sealed class PreviousTabAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.PreviousTab;
    public string Label => "Previous tab";
    public string Description => "Switch to the tab on the left";
    public HotKey HotKey => new(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift);
    public bool IsExecutable => context.Tabs is { Count: > 1 };

    public Task ExecuteAsync(object? parameter = null)
    {
        context.Tabs?.Cycle(-1);
        return Task.CompletedTask;
    }
}

public sealed class ReopenClosedTabAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.ReopenClosedTab;
    public string Label => "Reopen closed tab";
    public string Description => "Bring back the tab that was closed last";
    public HotKey HotKey => new(Key.T, ModifierKeys.Control | ModifierKeys.Shift);
    public bool IsExecutable => context.Tabs is { CanReopen: true };

    public Task ExecuteAsync(object? parameter = null)
    {
        context.Tabs?.ReopenClosed();
        return Task.CompletedTask;
    }
}

public sealed class DuplicateTabAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.DuplicateTab;
    public string Label => "Duplicate tab";
    public string Description => "Open this folder in a second tab";
    public bool IsExecutable => context.Tabs is not null;

    public Task ExecuteAsync(object? parameter = null)
    {
        if (context.Tabs is { } tabs)
            tabs.Duplicate(tabs.Active);

        return Task.CompletedTask;
    }
}

// "Open in new tab" on the selected folders. The tabs open in the background, as in Explorer, so several
// folders can be opened one after another without losing your place.
public sealed class OpenInNewTabAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.OpenInNewTab;
    public string Label => "Open in new tab";
    public string Description => "Open the selected folders in new tabs";
    public string Glyph => "\uE8A7";
    public bool IsExecutable => context.Tabs is not null && context.SelectedItems.Any(item => item.IsFolder);

    public Task ExecuteAsync(object? parameter = null)
    {
        if (context.Tabs is not { } tabs)
            return Task.CompletedTask;

        foreach (var item in context.SelectedItems.Where(item => item.IsFolder).ToArray())
            tabs.Open(item.FullPath, activate: false);

        return Task.CompletedTask;
    }
}

// NEW (new window): Ctrl+N, as in Explorer: another window on the folder you are in. The window belongs to
// the Clearspace that is already running, so the file index and every cache are shared with it.
public sealed class NewWindowAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.NewWindow;
    public string Label => "New window";
    public string Description => "Open this folder in another window";
    public string Glyph => "\uE78B";
    public HotKey HotKey => new(Key.N, ModifierKeys.Control);
    public bool IsExecutable => context.OpenWindow is not null && context.CurrentPath.Length > 0;

    public Task ExecuteAsync(object? parameter = null)
    {
        if (context.CurrentPath.Length > 0)
            context.OpenWindow?.Invoke(context.CurrentPath);

        return Task.CompletedTask;
    }
}

// NEW (new window): "Open in new window" on the selected folders (one window each).
public sealed class OpenInNewWindowAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.OpenInNewWindow;
    public string Label => "Open in new window";
    public string Description => "Open the selected folders in new windows";
    public string Glyph => "\uE78B";
    public bool IsExecutable => context.OpenWindow is not null && context.SelectedItems.Any(item => item.IsFolder);

    public Task ExecuteAsync(object? parameter = null)
    {
        if (context.OpenWindow is not { } open)
            return Task.CompletedTask;

        foreach (var item in context.SelectedItems.Where(item => item.IsFolder).ToArray())
            open(item.FullPath);

        return Task.CompletedTask;
    }
}
