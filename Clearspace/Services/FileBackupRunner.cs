// Clearspace | NEW (file backup): who runs a backup, and how the window follows it.
// A backup is never run by the window's own process. It is run by a second Clearspace, started without a
// window:
//     Clearspace.exe --run-backup <id>    this one backup, now ("Back up now")
//     Clearspace.exe --run-backups        every backup that is due (the schedule)
// so a backup of a large drive carries on when the window is closed, and the same code runs whether the
// window started it, the window's once-a-minute check did, or Windows' Task Scheduler did while Clearspace
// was closed (FileBackupTask.cs).
//
// The two processes talk through small files in %APPDATA%\Clearspace\File backups\state (FileBackupStore):
//     <id>.lock           held open by the process that is running the plan; nobody else can open it
//                         meanwhile, which is how "is it running?" is answered, and it is gone when
//                         that process ends, however it ends
//     <id>.progress.json  what it is doing, written a few times a second
//     <id>.cancel         made by the window's Stop button; the run sees it and stops
//     <id>.json           how the run went, written at the end
//     runner.lock         held by the process running what is due, so two never start at once
//     stop-all            made when Clearspace is being updated or removed ("Clearspace.exe --quit"):
//                         every run stops, and none starts for the next few minutes
//
// A scheduled run that finds the backup's drive (or the original) missing is not a failure: the plan
// waits, and runs when a later check finds both there. The check itself costs a look at two folders.
// Tests: Clearspace.Tests/FileBackupTests.cs (which plans are due, the lock, one run from start to end).
using System.Diagnostics;
using System.IO;

namespace Clearspace.Services;

internal static class FileBackupRunner
{
    internal const string RunDue = "--run-backups";
    internal const string RunOne = "--run-backup";

    private static readonly TimeSpan StopAllLasts = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ProgressEvery = TimeSpan.FromMilliseconds(400);

    internal static bool IsSwitch(IReadOnlyList<string> args)
        => (args.Count == 1 && args[0].Equals(RunDue, StringComparison.OrdinalIgnoreCase)) ||
           (args.Count == 2 && args[0].Equals(RunOne, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs the switch and returns the exit code (0 = done or nothing to do, 1 = something went wrong,
    /// 2 = no such backup, 3 = Clearspace is being updated). Called from App.OnStartup, which then ends the process.
    /// </summary>
    internal static int Run(IReadOnlyList<string> args, FileBackupStore? store = null)
    {
        store ??= FileBackupStore.Default;
        try
        {
            if (StopAllAsked(store))
                return 3;

            if (args[0].Equals(RunOne, StringComparison.OrdinalIgnoreCase))
            {
                var plan = store.Load().Plans.FirstOrDefault(found => found.Id.Equals(args[1], StringComparison.OrdinalIgnoreCase));
                if (plan is null)
                    return 2;

                RunPlan(plan, store, DateTime.Now);
                return 0;
            }

            // What is due, one after the other. Only one process does this at a time.
            using var only = TryLock(store.RunnerLockPath, store);
            if (only is null)
                return 0;

            Quieten();
            foreach (var plan in Due(store.Load(), store, DateTime.Now))
            {
                if (StopAllAsked(store))
                    return 3;
                RunPlan(plan, store, DateTime.Now);
            }

            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    // A scheduled backup should not get in the way of what the person is doing. robocopy, started from
    // here, runs at the same lower priority.
    private static void Quieten()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            me.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or PlatformNotSupportedException)
        {
        }
    }

    /// <summary>
    /// The scheduled plans that should run now and can: due by their schedule, not running, and with both
    /// their original and the place their backup goes in reach. A due plan that has to wait is noted as
    /// waiting in its state. Looks at the disk.
    /// </summary>
    internal static List<FileBackupPlan> Due(FileBackupSettings settings, FileBackupStore store, DateTime nowLocal)
    {
        var due = new List<FileBackupPlan>();
        foreach (var plan in settings.Plans)
        {
            if (!plan.Schedule.IsOn)
                continue;

            var state = store.State(plan.Id);
            var last = state.LastRunUtc is { } utc ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime() : (DateTime?)null;
            if (!plan.Schedule.IsDue(last, state.LastFailed, nowLocal) || IsRunning(plan.Id, store))
                continue;

            if (FileBackupRules.WhyWaiting(plan) is { } waiting)
            {
                NoteWaiting(plan.Id, state, waiting, store);
                continue;
            }

            due.Add(plan);
        }

        return due;
    }

    private static void NoteWaiting(string id, FileBackupState state, string why, FileBackupStore store)
    {
        if (state.Outcome == FileBackupState.Waiting && state.Message == why)
            return;

        state.Outcome = FileBackupState.Waiting;
        state.Message = why;
        try { store.SaveState(id, state); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>One run of one plan, from taking its lock to writing how it went. Does nothing when the plan is running already.</summary>
    internal static FileBackupResult? RunPlan(FileBackupPlan plan, FileBackupStore store, DateTime nowLocal, bool useRobocopy = true)
    {
        Directory.CreateDirectory(store.StateFolder);
        using var held = TryLock(store.LockPath(plan.Id), store);
        if (held is null)
            return null;

        FileBackupStore.TryDelete(store.CancelPath(plan.Id)); // a Stop meant for an earlier run
        var started = DateTime.UtcNow;
        var shown = new FileBackupProgress { Phase = "Starting", StartedUtc = started };
        store.SaveProgress(plan.Id, shown);

        using var stop = new CancellationTokenSource();
        using var watch = new Timer(_ =>
        {
            try
            {
                if (File.Exists(store.CancelPath(plan.Id)) || StopAllAsked(store))
                    stop.Cancel();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
            }
        }, null, ProgressEvery, ProgressEvery);

        // Progress arrives for every file, from several threads; the file is written a few times a second.
        var gate = new Lock();
        var written = Stopwatch.StartNew();
        void Show(FileBackupProgress progress)
        {
            lock (gate)
            {
                if (progress.Phase == shown.Phase && written.Elapsed < ProgressEvery)
                    return;

                progress.StartedUtc = started;
                shown = progress;
                written.Restart();
                store.SaveProgress(plan.Id, progress);
            }
        }

        var logPath = store.NewLogPath(plan.Id, nowLocal);
        var result = FileBackupEngine.Run(new FileBackupRun
        {
            Plan = plan,
            NowLocal = nowLocal,
            LogPath = logPath,
            Progress = Show,
            Token = stop.Token,
            UseRobocopy = useRobocopy
        });

        var state = store.State(plan.Id);
        state.Outcome = result.Outcome;
        state.Message = result.Message;
        if (result.Outcome == FileBackupState.Waiting)
        {
            FileBackupStore.TryDelete(logPath); // nothing was done; the message says why
        }
        else
        {
            state.LastRunUtc = started;
            state.LastEndUtc = DateTime.UtcNow;
            state.Copied = result.Copied;
            state.CopiedBytes = result.CopiedBytes;
            state.Removed = result.Removed;
            state.SetAside = result.SetAside;
            state.Errors = result.Errors;
            state.LogPath = logPath;
            if (result.Outcome is FileBackupState.Ok or FileBackupState.Nothing or FileBackupState.Problems)
                state.LastGoodUtc = state.LastEndUtc;
        }

        try { store.SaveState(plan.Id, state); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

        store.ClearProgress(plan.Id);
        FileBackupStore.TryDelete(store.CancelPath(plan.Id));
        store.PruneLogs(plan.Id);
        return result;
    }

    // Opens a file so that nobody else can while it is held; null when somebody already has. The file
    // removes itself when it is let go.
    private static FileStream? TryLock(string path, FileBackupStore store)
    {
        try
        {
            Directory.CreateDirectory(store.StateFolder);
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsHeld(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false; // it opened: a leftover nobody holds
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // ------------------------------------------------------------------ for the window

    /// <summary>Whether some process is running this plan right now.</summary>
    internal static bool IsRunning(string id, FileBackupStore? store = null) => IsHeld((store ?? FileBackupStore.Default).LockPath(id));

    /// <summary>Whether a process is working through what is due.</summary>
    internal static bool IsRunningDue(FileBackupStore? store = null) => IsHeld((store ?? FileBackupStore.Default).RunnerLockPath);

    /// <summary>
    /// Whether a run of this plan ended without saying how (Clearspace was closed by force, the PC lost
    /// power): its progress is still there, and nobody is running it.
    /// </summary>
    internal static bool WasCutShort(string id, FileBackupStore? store = null)
    {
        store ??= FileBackupStore.Default;
        return File.Exists(store.ProgressPath(id)) && !IsRunning(id, store);
    }

    /// <summary>Starts a second Clearspace, without a window, that runs this plan now. False when it could not be started.</summary>
    internal static bool Start(string id) => Launch(RunOne, id);

    /// <summary>
    /// The window's once-a-minute check: when a scheduled plan is due and nobody is at it, starts a second
    /// Clearspace that runs what is due. Looks at the disk, so call it off the UI thread.
    /// </summary>
    internal static void StartDueIfAny(FileBackupStore? store = null)
    {
        store ??= FileBackupStore.Default;
        try
        {
            var settings = store.Load();
            if (!settings.AnyScheduled || StopAllAsked(store) || IsRunningDue(store))
                return;
            if (Due(settings, store, DateTime.Now).Count > 0)
                Launch(RunDue);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // the next check tries again
        }
    }

    private static bool Launch(params string[] arguments)
    {
        try
        {
            if (Environment.ProcessPath is not { } exe)
                return false;

            var info = new ProcessStartInfo { FileName = exe, UseShellExecute = false, CreateNoWindow = true };
            // Started as "dotnet Clearspace.dll" (a developer's run): the program to run is the first argument.
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } program)
                info.ArgumentList.Add(program);
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>Asks the run of this plan to stop. It stops within a second or so and says "Stopped".</summary>
    internal static void RequestStop(string id, FileBackupStore? store = null)
    {
        store ??= FileBackupStore.Default;
        try
        {
            Directory.CreateDirectory(store.StateFolder);
            File.WriteAllText(store.CancelPath(id), string.Empty);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Asks every run to stop and keeps new ones from starting for a few minutes. Used when Clearspace is
    /// about to be updated or removed: its files are replaced, so nothing of it may be running.
    /// </summary>
    internal static void AskAllToStop(FileBackupStore? store = null)
    {
        store ??= FileBackupStore.Default;
        try
        {
            if (!Directory.Exists(store.Folder))
                return; // no backups were ever set up here
            Directory.CreateDirectory(store.StateFolder);
            File.WriteAllText(store.StopAllPath, string.Empty);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static bool StopAllAsked(FileBackupStore store)
    {
        try
        {
            var file = new FileInfo(store.StopAllPath);
            if (!file.Exists)
                return false;
            if (DateTime.UtcNow - file.LastWriteTimeUtc < StopAllLasts)
                return true;

            FileBackupStore.TryDelete(store.StopAllPath); // asked a while ago: the update is long over
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
