// Clearspace | Installing and removing the index helper service.
//
// NEW (journal catch-up). A service runs as SYSTEM, so its executable must not live anywhere a normal
// account can write (Clearspace itself installs per user under %LocalAppData%). --install copies the
// helper to Program Files first and registers that copy. Uses sc.exe so no extra libraries are needed.

using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Clearspace.Journal;
using Microsoft.Win32;

namespace Clearspace.IndexHelper;

internal static class HelperInstaller
{
    private const string DisplayName = "Clearspace Index Helper";
    private const string Description = "Lets Clearspace catch up on file changes from the NTFS change journal instead of rescanning drives. Reads file names and folders only; never file contents.";

    private static string InstallFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Clearspace", "IndexHelper");

    private static string InstalledExe => Path.Combine(InstallFolder, "ClearspaceIndexHelper.exe");

    public static int Install(bool quiet)
    {
        RequireAdministrator();
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the helper executable.");
        var sourceFolder = Path.GetDirectoryName(source)!;

        if (IsInstalled())
        {
            Sc($"stop {JournalProtocol.ServiceName}", allowFailure: true);
            WaitForStopped(TimeSpan.FromSeconds(15));
        }

        Directory.CreateDirectory(InstallFolder);

        // A single-file publish is one .exe; a development build also has these side files.
        foreach (var file in Directory.EnumerateFiles(sourceFolder, "ClearspaceIndexHelper*")
                     .Concat(Directory.EnumerateFiles(sourceFolder, "System.ServiceProcess.ServiceController.dll")))
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), Path.GetFullPath(InstallFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(InstallFolder, Path.GetFileName(file)), overwrite: true);
        }

        var binPath = $"\"{InstalledExe}\" --service";

        if (IsInstalled())
            Sc($"config {JournalProtocol.ServiceName} binPath= \"{binPath.Replace("\"", "\\\"")}\" start= auto obj= LocalSystem");
        else
            Sc($"create {JournalProtocol.ServiceName} binPath= \"{binPath.Replace("\"", "\\\"")}\" start= auto obj= LocalSystem DisplayName= \"{DisplayName}\"");

        Sc($"description {JournalProtocol.ServiceName} \"{Description}\"", allowFailure: true);
        Sc($"failure {JournalProtocol.ServiceName} reset= 86400 actions= restart/5000/restart/30000//", allowFailure: true);
        Sc($"start {JournalProtocol.ServiceName}", allowFailure: true);

        var answering = WaitForPipe(TimeSpan.FromSeconds(15));

        if (!quiet)
            Console.WriteLine(answering ? "The Clearspace index helper is installed and running." : "Installed, but the helper is not answering yet.");

        return answering ? 0 : 3;
    }

    public static int Uninstall(bool quiet)
    {
        RequireAdministrator();

        if (IsInstalled())
        {
            Sc($"stop {JournalProtocol.ServiceName}", allowFailure: true);
            WaitForStopped(TimeSpan.FromSeconds(15));
            Sc($"delete {JournalProtocol.ServiceName}", allowFailure: true);
        }

        try
        {
            if (Directory.Exists(InstallFolder))
                Directory.Delete(InstallFolder, recursive: true);

            var parent = Path.GetDirectoryName(InstallFolder)!;
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The service can take a moment to release its executable; the folder is harmless.
            if (!quiet) Console.WriteLine($"The helper was removed; its folder could not be deleted yet: {exception.Message}");
        }

        if (!quiet) Console.WriteLine("The Clearspace index helper was removed.");
        return 0;
    }

    public static string Describe()
        => !IsInstalled() ? "Not installed."
            : WaitForPipe(TimeSpan.FromSeconds(1)) ? $"Installed at {InstalledExe} and answering."
            : "Installed but not answering (the service may be stopped).";

    internal static bool IsInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{JournalProtocol.ServiceName}");
        return key is not null;
    }

    private static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();

        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Installing or removing the helper needs administrator rights. Use Clearspace's Indexing page, which asks Windows for permission.");
    }

    private static void Sc(string arguments, bool allowFailure = false)
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not run sc.exe.");

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException($"sc.exe {arguments.Split(' ')[0]} failed ({process.ExitCode}): {output.Trim()}");
    }

    private static void WaitForStopped(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < until && WaitForPipe(TimeSpan.FromMilliseconds(200)))
            Thread.Sleep(300);

        Thread.Sleep(500); // let the process exit and release its executable
    }

    private static bool WaitForPipe(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;

        do
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", JournalProtocol.PipeName, PipeDirection.InOut);
                pipe.Connect(250);
                return true;
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }
        while (DateTime.UtcNow < until);

        return false;
    }
}
