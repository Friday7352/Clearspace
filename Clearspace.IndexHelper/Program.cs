// Clearspace | Index helper entry point.
//
// NEW (journal catch-up). ClearspaceIndexHelper.exe
//   --service     run as the Windows service (how the service control manager starts it)
//   --install     copy to Program Files and register + start the service (administrator)
//   --uninstall   stop and remove the service and its files (administrator)
//   --console     run the pipe server in this window until Enter (administrator; for testing)
//   --status      print whether the service is installed and answering

using System.ServiceProcess;
using Clearspace.Journal;

namespace Clearspace.IndexHelper;

internal static class Program
{
    private static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        // NEW: where to write the outcome for Clearspace, which starts this hidden and elevated.
        var resultIndex = Array.FindIndex(args, arg => arg.Equals("--result", StringComparison.OrdinalIgnoreCase));
        var resultPath = resultIndex >= 0 && resultIndex + 1 < args.Length ? args[resultIndex + 1] : null;

        try
        {
            switch (command)
            {
                case "--service":
                    ServiceBase.Run(new HelperService());
                    return 0;

                case "--install":
                {
                    var code = HelperInstaller.Install(quiet: args.Contains("--quiet"));
                    Report(resultPath, code == 0 ? "ok" : "Installed, but the helper is not answering yet. " + HelperInstaller.Describe());
                    return code;
                }

                case "--uninstall":
                {
                    var code = HelperInstaller.Uninstall(quiet: args.Contains("--quiet"));
                    Report(resultPath, "ok");
                    return code;
                }

                case "--console":
                {
                    using var stop = new CancellationTokenSource();
                    var server = new PipeServer(message => Console.WriteLine($"{DateTime.Now:T}  {message}"));
                    var running = Task.Run(() => server.RunAsync(stop.Token));
                    Console.WriteLine($"Listening on \\\\.\\pipe\\{JournalProtocol.PipeName}. Press Enter to stop.");
                    Console.ReadLine();
                    stop.Cancel();
                    running.Wait(TimeSpan.FromSeconds(5));
                    return 0;
                }

                case "--status":
                    Console.WriteLine(HelperInstaller.Describe());
                    return 0;

                default:
                    Console.WriteLine("Clearspace index helper. Lets Clearspace catch up on file changes from the NTFS change journal.");
                    Console.WriteLine("Usage: ClearspaceIndexHelper --install | --uninstall | --status | --console");
                    return 1;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            Report(resultPath, $"{exception.GetType().Name}: {exception.Message}");
            return 2;
        }
    }

    private static void Report(string? path, string message)
    {
        if (path is null) return;
        try { File.WriteAllText(path, message); } catch (Exception) { }
    }
}
