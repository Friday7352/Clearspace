// Clearspace | NEW (Explorer integration): the commands Explorer's menu entries and .cslock double-clicks
// send to Clearspace: --open, --lock, --unlock or --remove-lock followed by a path. If Clearspace is already running, the
// new process hands the command to it over a named pipe (only this Windows user can connect) and exits,
// so locked folders are always managed by the window you already have open.
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace Clearspace.Services;

// CHANGED (unlock vs remove lock): Unlock = for a visit (locks again when you leave the folder);
// RemoveLock = for good.
internal enum ShellVerb { Open, Lock, Unlock, RemoveLock, ChangePassword } // + ChangePassword (change password)

internal sealed record ShellCommand(ShellVerb Verb, string Path);

internal static class ShellCommands
{
    private static string PipeName => "Clearspace-Shell-" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

    internal static ShellCommand? Parse(IReadOnlyList<string> args)
    {
        if (args.Count < 2) return null;
        ShellVerb? verb = args[0].ToLowerInvariant() switch
        {
            "--open" => ShellVerb.Open,
            "--lock" => ShellVerb.Lock,
            "--unlock" => ShellVerb.Unlock,
            "--remove-lock" => ShellVerb.RemoveLock, // NEW (unlock vs remove lock)
            "--change-password" => ShellVerb.ChangePassword, // NEW (change password)
            _ => null
        };
        var path = args[1].Trim().Trim('"');
        if (verb is null || path.Length == 0 || !System.IO.Path.IsPathFullyQualified(path)) return null;
        return new ShellCommand(verb.Value, path);
    }

    private static string Serialize(ShellCommand command) => command.Verb + "\n" + command.Path;

    private static ShellCommand? Deserialize(string text)
    {
        var parts = text.Split('\n', 2);
        if (parts.Length != 2 || !Enum.TryParse<ShellVerb>(parts[0], out var verb)) return null;
        var path = parts[1].Trim();
        return path.Length is > 0 and < 32768 && System.IO.Path.IsPathFullyQualified(path) ? new ShellCommand(verb, path) : null;
    }

    // True when a running Clearspace accepted the command.
    internal static bool TryForward(ShellCommand command)
    {
        try
        {
            // NEW (Explorer re-lock): this process was started by a click in Explorer, so Windows lets it bring
            // windows to the front; pass that on so the running Clearspace's password prompt isn't hidden.
            AllowSetForegroundWindow(-1);
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(700);
            var bytes = Encoding.UTF8.GetBytes(Serialize(command));
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    // Runs until cancelled, handing each received command to `handle` (on a background thread).
    internal static async Task ListenAsync(Action<ShellCommand> handle, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(token);
                if (Deserialize(text) is { } command) handle(command);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(500, token).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }
}
