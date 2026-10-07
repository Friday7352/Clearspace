// Clearspace | NEW (file backup): scheduled backups while Clearspace is closed.
// With "Run scheduled backups when Clearspace is closed" on (the Backup and restore page) and at least one
// backup on a schedule, Clearspace keeps one entry in Windows' Task Scheduler:
//     name      Clearspace backups (<your Windows user name>)
//     runs      Clearspace.exe --run-backups   every 15 minutes while you are signed in
//     as        you, with your ordinary rights (no administrator rights are asked for or used)
// That process has no window, looks whether a backup is due, runs it if so, and ends (FileBackupRunner.cs).
// When nothing is due it is gone again in a moment. The entry also runs on battery, is not stopped after
// a time limit (a first backup of a drive can take hours), and never starts twice.
//
// Sync keeps the entry in step with the settings: made when it is wanted, made again when Clearspace has
// moved, removed when the setting is off or no backup has a schedule. What was last registered is noted in
// "task.txt" beside the plans, so nothing is asked of Task Scheduler when nothing has changed.
// The entry is also removed by "Clearspace.exe --unregister-shell" (the uninstaller) and before going back
// to a Clearspace older than the one that introduced it (UpdateService.PrepareToGoBack): that version
// would open a window every 15 minutes instead.
//
// It is made with schtasks.exe from a description in XML (Xml, tested in Clearspace.Tests/FileBackupTests.cs).
// Talking to Task Scheduler itself is checked by hand.
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace Clearspace.Services;

internal static class FileBackupTask
{
    internal const int EveryMinutes = 15;
    private const string Layout = "1"; // goes up when Xml changes, so the entry is made again after an update

    /// <summary>"Clearspace backups (payton)". One per Windows account, since the list of tasks is shared by the PC.</summary>
    internal static string NameFor(string user)
    {
        var clean = new string((user ?? string.Empty).Where(letter => char.IsLetterOrDigit(letter) || letter is ' ' or '-' or '_' or '.').ToArray()).Trim();
        return clean.Length == 0 ? "Clearspace backups" : $"Clearspace backups ({clean})";
    }

    internal static string Name => NameFor(Environment.UserName);

    /// <summary>The task as Task Scheduler reads it.</summary>
    /// <param name="userId">The account it runs as: its SID, or DOMAIN\name.</param>
    internal static string Xml(string exePath, string userId, DateTime startLocal)
    {
        static string Text(string value) => SecurityElement.Escape(value) ?? string.Empty;

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>Clearspace</Author>
                <Description>Runs the scheduled file backups set up in Clearspace (Settings, Backup and restore) while Clearspace is closed. Turn it off there, not here: Clearspace puts this entry back while that setting is on.</Description>
              </RegistrationInfo>
              <Triggers>
                <TimeTrigger>
                  <Repetition>
                    <Interval>PT{EveryMinutes}M</Interval>
                    <StopAtDurationEnd>false</StopAtDurationEnd>
                  </Repetition>
                  <StartBoundary>{startLocal.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}</StartBoundary>
                  <Enabled>true</Enabled>
                </TimeTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Text(userId)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Text(exePath)}</Command>
                  <Arguments>{FileBackupRunner.RunDue}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>Whether the entry should be there: the setting is on and some backup has a schedule.</summary>
    internal static bool Wanted(FileBackupSettings settings) => settings.RunWhenClosed && settings.AnyScheduled;

    /// <summary>What Sync notes when it has registered the entry for this program.</summary>
    internal static string Record(string exePath) => Layout + "|" + exePath;

    /// <summary>
    /// Makes Task Scheduler match the settings. Returns null when it does (or nothing had to be done), and
    /// otherwise words for the page. Starts schtasks.exe when something has to change, so call it off the UI thread.
    /// </summary>
    internal static string? Sync(FileBackupSettings settings, FileBackupStore? store = null, string? exePath = null)
    {
        store ??= FileBackupStore.Default;
        exePath ??= Environment.ProcessPath;
        var note = Path.Combine(store.Folder, "task.txt");

        try
        {
            var registered = File.Exists(note) ? File.ReadAllText(note).Trim() : null;
            if (!Wanted(settings))
            {
                if (registered is null)
                    return null;
                if (Remove())
                    FileBackupStore.TryDelete(note);
                return null;
            }

            if (exePath is null || !OperatingSystem.IsWindows())
                return "Scheduled backups run while Clearspace is open.";
            if (registered == Record(exePath))
                return null;

            if (!Register(exePath))
                return "Windows did not let Clearspace add its entry to Task Scheduler, so scheduled backups run only while Clearspace is open.";

            Directory.CreateDirectory(store.Folder);
            File.WriteAllText(note, Record(exePath));
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "Task Scheduler could not be reached, so scheduled backups run only while Clearspace is open. (" + error.Message + ")";
        }
    }

    /// <summary>Takes the entry out of Task Scheduler and forgets it was registered (uninstalling, going back to an older Clearspace).</summary>
    internal static void RemoveForGood(FileBackupStore? store = null)
    {
        store ??= FileBackupStore.Default;
        try
        {
            var note = Path.Combine(store.Folder, "task.txt");
            if (!File.Exists(note))
                return; // never registered from this account: nothing to ask Task Scheduler for
            if (Remove())
                FileBackupStore.TryDelete(note);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private static bool Register(string exePath)
    {
        var user = OperatingSystem.IsWindows()
            ? System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserDomainName + "\\" + Environment.UserName
            : Environment.UserName;

        // schtasks reads the description from a file, which has to be UTF-16 as its first line says.
        var file = Path.Combine(Path.GetTempPath(), "Clearspace task " + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(file, Xml(exePath, user, DateTime.Today), Encoding.Unicode);
            return Schtasks("/Create", "/TN", Name, "/XML", file, "/F") == 0;
        }
        finally
        {
            FileBackupStore.TryDelete(file);
        }
    }

    // True when the entry is gone afterwards: it was removed, or was not there.
    private static bool Remove()
    {
        if (!OperatingSystem.IsWindows())
            return true;
        if (Schtasks("/Query", "/TN", Name) != 0)
            return true;
        return Schtasks("/Delete", "/TN", Name, "/F") == 0;
    }

    private static int Schtasks(params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("schtasks.exe could not be started.");
        // Read what it says so it never waits on a full pipe; the exit code is the answer.
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20_000))
        {
            try { process.Kill(); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return -1;
        }

        Task.WaitAll([output, errors], 2_000);
        return process.ExitCode;
    }
}
