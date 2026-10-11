// Clearspace | Application startup and error reporting.

using System.Windows;
using System.Windows.Threading;
using System.IO;

namespace Clearspace;

public partial class App : Application
{
    private static readonly object ErrorLock = new();
    private static string? _lastErrorSignature;
    private static DateTime _lastErrorAt;

    // CHANGED (Explorer re-lock): LockAgent sets it when "Open in Clearspace" has to create the main window.
    public static string? StartupPath { get; internal set; }

    // NEW (Explorer re-lock): a lock / unlock / remove-lock command from Explorer that this process runs without
    // a main window (only the password dialog shows).
    private Services.ShellCommand? _windowlessCommand;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // NEW (installer): questions and requests from Setup / Uninstall (--lock-count, --remove-all-locks,
        // --quit). They never open the main window and answer through the exit code, so the process ends
        // right here instead of going through OnExit (which would open the tag database just to close it).
        if (Services.InstallerCommands.IsInstallerSwitch(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Only --remove-all-locks shows a window (the password dialog); give it the saved theme.
            if (e.Args[0].Equals(Services.InstallerCommands.RemoveAllLocks, StringComparison.OrdinalIgnoreCase))
            {
                try { Services.ThemeService.Initialize(); }
                catch (Exception) { }
            }
            Environment.Exit(Services.InstallerCommands.Run(e.Args[0]));
            return;
        }

        // NEW (file backup): "Clearspace.exe --run-backup <id>" and "--run-backups" run file backups without
        // a window (Services/FileBackupRunner.cs): started by the Backup and restore page, by the window's
        // once-a-minute check, or by Windows' Task Scheduler while Clearspace is closed. Like the installer
        // switches, the process ends right here.
        if (Services.FileBackupRunner.IsSwitch(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Environment.Exit(Services.FileBackupRunner.Run(e.Args));
            return;
        }

        // NEW (Explorer integration): installer hooks.
        if (e.Args.Length == 1 && e.Args[0] is "--register-shell" or "--unregister-shell")
        {
            try
            {
                if (e.Args[0] == "--register-shell") Services.ShellIntegration.Register(Environment.ProcessPath!);
                else Services.ShellIntegration.Unregister();
            }
            catch (Exception) { }
            Shutdown(0);
            return;
        }

        // REMOVED (Explorer folder gate, reverted): a leftover sign-in entry may still start Clearspace with
        // --background; do nothing (Register removes that entry the next time Clearspace opens).
        if (e.Args.Length == 1 && e.Args[0] == "--background")
        {
            Shutdown(0);
            return;
        }

        // NEW (Explorer stays Explorer): Windows asked Clearspace to open a folder (the setting "Open folders
        // with Clearspace"). When that came from inside a File Explorer window, the folder is shown there
        // instead, and nothing of Clearspace opens (Services/ExplorerHandoff.cs).
        if (e.Args.Length >= 2 && e.Args[0].Equals("--open-folder", StringComparison.OrdinalIgnoreCase) &&
            Services.ShellCommands.Parse(e.Args) is { } folderAsked &&
            Services.ExplorerHandoff.TryKeepInExplorer(folderAsked.Path))
        {
            Shutdown(0);
            return;
        }

        // NEW (Explorer integration): a command from Explorer goes to the Clearspace that's already running.
        if (Services.ShellCommands.Parse(e.Args) is { } command)
        {
            if (Services.ShellCommands.TryForward(command))
            {
                Shutdown(0);
                return;
            }
            // CHANGED (Explorer re-lock): only "Open in Clearspace" (on a folder) shows the main window; everything
            // else shows just its dialog.
            if (command.Verb == Services.ShellVerb.Open && Directory.Exists(command.Path)) StartupPath = command.Path;
            else _windowlessCommand = command;
        }
        else if (e.Args.Length > 0)
        {
            var candidate = e.Args[0].Trim('"');

            // CHANGED (zip folders): a ZIP file can be given as well as a folder; the window opens inside it
            // (MainViewModel.Start turns the path into the address of the ZIP's top).
            if (Directory.Exists(candidate) || (File.Exists(candidate) && Services.ArchiveService.IsArchive(candidate)))
                StartupPath = candidate;
        }

        // NEW (themes): put the saved theme in place before any window or dialog opens (including the
        // password dialog Explorer can ask for without a main window). A theme problem never stops the app.
        try { Services.ThemeService.Initialize(); }
        catch (Exception) { }

        // NEW (zip folders): copies taken out of ZIP files to open or copy them are kept in the temporary
        // folder for a day; older ones are removed now, in the background.
        Services.ArchiveBrowser.TidyLater();

        // NEW (logo): every Clearspace window (the main window and each dialog) is given the app icon
        // directly, so the title bar, the taskbar button and Alt+Tab show the logo at the right size from
        // Clearspace.ico itself instead of whatever Windows has cached for the .exe. Task Manager, shortcuts
        // and Explorer read the icon built into Clearspace.exe (ApplicationIcon in Clearspace.csproj), which
        // is the same file.
        try
        {
            var icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Clearspace.ico"));
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) =>
                {
                    if (sender is Window { Icon: null } window) window.Icon = icon;
                }));
        }
        catch (Exception) { }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                Report(exception, "Background error");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            Report(args.Exception, "Unobserved task error");
        };

        try { _ = Services.TagService.All; }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message + "\n\nTag storage: " + Services.TagService.DatabasePath,
                "Clearspace could not open tags", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return; // CHANGED: don't open the window after a failed start
        }

        // CHANGED (Explorer re-lock): Clearspace may run with no window (after a password prompt from Explorer,
        // until what it unlocked is locked again), so LockAgent decides when it exits, not the last window.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        LockAgent.Current.Start();
        if (_windowlessCommand is { } windowless)
        {
            LockAgent.Current.RunLater(windowless);
            return;
        }

        // CHANGED (Explorer integration): replaces StartupUri="MainWindow.xaml".
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.FileIndexService.Stop();
        Services.TagService.Store.Dispose();

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report(e.Exception, "Clearspace hit an error");

        e.Handled = true;
    }

    private static void Report(Exception exception, string title)
    {
        System.Diagnostics.Debug.WriteLine($"{title}: {exception}");

        var detail = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerException ?? aggregate
            : exception;

        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Clearspace", "Logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(
                Path.Combine(folder, "errors.log"),
                $"[{DateTime.Now:O}] {title}{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }

        var signature = $"{detail.GetType().FullName}|{detail.Message}";
        lock (ErrorLock)
        {
            var now = DateTime.UtcNow;
            if (signature == _lastErrorSignature && now - _lastErrorAt < TimeSpan.FromSeconds(5))
                return;

            _lastErrorSignature = signature;
            _lastErrorAt = now;
        }

        MessageBox.Show(
            $"{detail.GetType().Name}\n\n{detail.Message}\n\n{detail.StackTrace}",
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
