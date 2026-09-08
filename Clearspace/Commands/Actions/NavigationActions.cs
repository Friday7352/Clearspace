// Clearspace | Navigation command actions.

using System.IO;
using System.Windows.Input;

namespace Clearspace.Commands.Actions;

public sealed class NoneAction : IAction
{
    public CommandCode Code => CommandCode.None;
    public string Label => string.Empty;
    public string Description => string.Empty;
    public bool IsExecutable => false;
    public Task ExecuteAsync(object? parameter = null) => Task.CompletedTask;
}

public sealed class NavigateBackAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.NavigateBack;
    public string Label => "Back";
    public string Description => "Go to the previous location";
    public string Glyph => "\uE72B";
    public HotKey HotKey => new(Key.Left, ModifierKeys.Alt);
    public bool IsExecutable => context.Navigation.CanGoBack;

    public Task ExecuteAsync(object? parameter = null)
    {
        context.Navigation.GoBack();
        return Task.CompletedTask;
    }
}

public sealed class NavigateForwardAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.NavigateForward;
    public string Label => "Forward";
    public string Description => "Go to the next location";
    public string Glyph => "\uE72A";
    public HotKey HotKey => new(Key.Right, ModifierKeys.Alt);
    public bool IsExecutable => context.Navigation.CanGoForward;

    public Task ExecuteAsync(object? parameter = null)
    {
        context.Navigation.GoForward();
        return Task.CompletedTask;
    }
}

public sealed class NavigateUpAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.NavigateUp;
    public string Label => "Up";
    public string Description => "Go to the parent folder";
    public string Glyph => "\uE74A";
    public HotKey HotKey => new(Key.Up, ModifierKeys.Alt);
    public bool IsExecutable => context.Navigation.CanGoUp;

    public Task ExecuteAsync(object? parameter = null)
    {
        context.Navigation.GoUp();
        return Task.CompletedTask;
    }
}

public sealed class NavigateHomeAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.NavigateHome;
    public string Label => "Home";
    public string Description => "Go to your user folder";
    public string Glyph => "\uE80F";
    public HotKey HotKey => new(Key.Home, ModifierKeys.Alt);

    public Task ExecuteAsync(object? parameter = null)
    {
        context.Navigation.Navigate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return Task.CompletedTask;
    }
}

public sealed class RefreshAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.Refresh;
    public string Label => "Refresh";
    public string Description => "Reload this folder";
    public string Glyph => "\uE72C";
    public HotKey HotKey => new(Key.F5);

    public Task ExecuteAsync(object? parameter = null)
    {
        context.RequestRefresh();
        return Task.CompletedTask;
    }
}

public sealed class FocusAddressBarAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.FocusAddressBar;
    public string Label => "Edit address";
    public string Description => "Move focus to the address bar";
    public string Glyph => "\uE8AC";
    public HotKey HotKey => new(Key.L, ModifierKeys.Control);

    public Task ExecuteAsync(object? parameter = null)
    {
        context.FocusAddressBar?.Invoke();
        return Task.CompletedTask;
    }
}

public sealed class OpenTerminalAction(ExplorerContext context) : IAction
{
    public CommandCode Code => CommandCode.OpenTerminal;
    public string Label => "Open in Terminal";
    public string Description => "Open a terminal in this folder";
    public string Glyph => "\uE756";
    public HotKey HotKey => new(Key.OemTilde, ModifierKeys.Control);
    public bool IsExecutable => Directory.Exists(context.CurrentPath);

    public Task ExecuteAsync(object? parameter = null)
    {
        var folder = context.CurrentPath;
        if (!Directory.Exists(folder))
            return Task.CompletedTask;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = $"-d \"{folder}\"",
                WorkingDirectory = folder,
                UseShellExecute = true
            });
            return Task.CompletedTask;
        }
        catch (Exception)
        {
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = folder,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
        }

        return Task.CompletedTask;
    }
}
