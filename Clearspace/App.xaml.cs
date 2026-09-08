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

    public static string? StartupPath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0)
        {
            var candidate = e.Args[0].Trim('"');

            if (Directory.Exists(candidate))
                StartupPath = candidate;
        }

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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.FileIndexService.Stop();

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
