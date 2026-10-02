// Clearspace | NEW (installer): the switches Setup and Uninstall start Clearspace with. None of them opens
// the main window, and each one answers through the process exit code.
//   --lock-count        exit code = how many locked files and folders Clearspace has on record (0 = none).
//   --remove-all-locks  shows only the "Remove all locks" password dialog.
//                       Exit code 0 = nothing is locked any more, 1 = closed without removing anything,
//                       2 = something is still locked (wrong password, stopped, or files in use).
//   --quit              asks every running Clearspace to close, waits for them, and closes any that did not
//                       answer. Each one first locks again whatever it has unlocked for a visit. Exit code 0.
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Clearspace.Services;

internal static class InstallerCommands
{
    internal const string LockCount = "--lock-count";
    internal const string RemoveAllLocks = "--remove-all-locks";
    internal const string Quit = "--quit";

    // How long --quit waits for the other Clearspace windows before closing them itself. A Clearspace that
    // is locking things again gives itself 20 seconds (LockAgent.Quit), so this is a little longer.
    private static readonly TimeSpan QuitWait = TimeSpan.FromSeconds(25);

    internal static bool IsInstallerSwitch(IReadOnlyList<string> args) =>
        args.Count == 1 && args[0].ToLowerInvariant() is LockCount or RemoveAllLocks or Quit;

    // Runs the switch and returns the exit code. Called from App.OnStartup, which then ends the process.
    internal static int Run(string argument)
    {
        try
        {
            switch (argument.ToLowerInvariant())
            {
                case LockCount: return CountLocks();
                case Quit: return QuitOthers();
                default: return RemoveAllLocksNow();
            }
        }
        catch (Exception)
        {
            // Setup treats anything but 0 from --remove-all-locks as "still locked"; the other two are
            // questions with a safe answer of 0.
            return argument.Equals(RemoveAllLocks, StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        }
    }

    // Locked folders, plus locked files that are not inside one of those folders. Items unlocked for a visit
    // count too: they are still under a lock. Records for things that no longer exist are left out.
    // Never creates the database: on a PC where nothing was ever locked there is nothing to count.
    internal static int CountLocks()
    {
        try
        {
            if (!File.Exists(TagService.DatabasePath)) return 0;
            using var store = new FileLockStore(TagService.DatabasePath, TagService.TagFilePath);
            var folders = store.Folders().Select(folder => folder.Path).Where(Directory.Exists).ToList();
            var files = store.LockedFilePaths()
                .Concat(store.OpenFiles().Select(open => open.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .Count(file => !folders.Any(folder => FileLockService.IsInside(file, folder)));
            return Math.Min(folders.Count + files, 60_000); // exit codes are small numbers; "a lot" is enough
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // The same dialog as Settings > "Remove all locks and reset password…", on its own in the middle of the
    // screen (the uninstaller is waiting behind it).
    private static int RemoveAllLocksNow()
    {
        if (CountLocks() == 0) return 0;

        var dialog = new FileLockWindow(string.Empty, LockDialogMode.RemoveAll, LockAgent.Service)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
            Topmost = true
        };
        dialog.Loaded += (_, _) => { dialog.Activate(); dialog.Topmost = false; };
        dialog.ShowDialog();

        try { TagService.Store.Dispose(); } catch (Exception) { } // close the database before the process ends
        if (dialog.ResultMessage is null) return 1;
        return CountLocks() == 0 ? 0 : 2;
    }

    private static int QuitOthers()
    {
        var exe = Environment.ProcessPath ?? AppContext.BaseDirectory;

        // Every running Clearspace listens on the same pipe name and one message reaches one of them. Each
        // stops listening as soon as it is asked to close, so keep asking until nobody picks up.
        for (var i = 0; i < 40 && OthersRunning() > 0; i++)
        {
            if (!ShellCommands.TryForward(new ShellCommand(ShellVerb.Quit, exe))) break;
            Thread.Sleep(200);
        }

        var deadline = DateTime.UtcNow + QuitWait;
        while (OthersRunning() > 0 && DateTime.UtcNow < deadline) Thread.Sleep(250);

        // Anything left did not answer (an older Clearspace that doesn't know this request, or one that is
        // stuck). Its files are about to be replaced or removed, so close it. Locking is written so that
        // being interrupted never loses a file's contents (see FileLockService).
        foreach (var process in Others())
        {
            try
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            catch (Exception) { }
            finally { process.Dispose(); }
        }
        return 0;
    }

    private static int OthersRunning()
    {
        var others = Others();
        foreach (var process in others) process.Dispose();
        return others.Count;
    }

    // Other Clearspace processes of this Windows user (same sign-in session), not counting this one.
    private static List<Process> Others()
    {
        var found = new List<Process>();
        using var me = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("Clearspace"))
        {
            var other = false;
            try { other = process.Id != me.Id && process.SessionId == me.SessionId && !process.HasExited; }
            catch (Exception) { }
            if (other) found.Add(process);
            else process.Dispose();
        }
        return found;
    }
}
