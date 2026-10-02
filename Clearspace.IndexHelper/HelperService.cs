// Clearspace | The index helper as a Windows service.
// NEW (journal catch-up): hosts PipeServer while the service runs.

using System.Diagnostics;
using System.ServiceProcess;
using Clearspace.Journal;

namespace Clearspace.IndexHelper;

internal sealed class HelperService : ServiceBase
{
    private CancellationTokenSource? _stop;
    private Task? _running;

    public HelperService()
    {
        ServiceName = JournalProtocol.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        _stop = new CancellationTokenSource();
        var server = new PipeServer(message => Trace.WriteLine($"ClearspaceIndexHelper: {message}"));
        _running = Task.Run(() => server.RunAsync(_stop.Token));
    }

    protected override void OnStop() => Shutdown();

    protected override void OnShutdown() => Shutdown();

    private void Shutdown()
    {
        _stop?.Cancel();
        try { _running?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _stop?.Dispose();
        _stop = null;
    }
}
