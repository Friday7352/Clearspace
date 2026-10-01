// Clearspace | The index helper's named-pipe server.
//
// NEW (journal catch-up). Security model:
//   * The pipe accepts connections from signed-in (authenticated) users; only SYSTEM and
//     administrators may create further instances, so another program cannot impersonate it
//     while the service runs.
//   * Reading the journal (which needs the volume) runs as the service. Turning file IDs into paths
//     runs while impersonating the caller, so a caller only ever receives paths their own account can
//     open. The helper never reads file contents and never changes anything on disk.
//   * One small request per connection; requests are size-limited and only "C:\"-style roots are accepted.

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Clearspace.Journal;

namespace Clearspace.IndexHelper;

internal sealed class PipeServer(Action<string> log)
{
    private const int MaxInstances = 4;
    private const int MaxRequestChars = 4096;

    public async Task RunAsync(CancellationToken token)
    {
        var active = new List<Task>();

        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;

            try
            {
                pipe = NamedPipeServerStreamAcl.Create(JournalProtocol.PipeName, PipeDirection.InOut, MaxInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, CreateSecurity());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // All instances busy (or the name is briefly unavailable): wait and try again.
                log($"Pipe not available yet: {exception.Message}");
                await Task.WhenAny(active.Count > 0 ? Task.WhenAny(active) : Task.Delay(200, token), Task.Delay(1000, token))
                    .ConfigureAwait(false);
                active.RemoveAll(task => task.IsCompleted);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }

            active.RemoveAll(task => task.IsCompleted);
            active.Add(Task.Run(() => HandleAsync(pipe, token), token));
        }

        try { await Task.WhenAll(active).ConfigureAwait(false); } catch (Exception) { }
    }

    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));

                var line = await ReadLineAsync(pipe, timeout.Token).ConfigureAwait(false);
                var request = line is null ? null : JsonSerializer.Deserialize<JournalRequest>(line, JournalProtocol.Json);
                JournalResponse response;

                if (request is null)
                    response = new JournalResponse { Message = "Empty or oversized request." };
                else if (request.Version != JournalProtocol.Version)
                    response = new JournalResponse { Message = $"Clearspace and the helper disagree on protocol version ({request.Version} vs {JournalProtocol.Version}). Reinstall the helper from Clearspace." };
                else if (request.Op == JournalProtocol.Scan)
                {
                    // NEW (file-table scan): JSON header line, then the table in binary.
                    timeout.CancelAfter(TimeSpan.FromMinutes(20)); // a large hard drive can take a while
                    FileTable? table = null;
                    try
                    {
                        (var header, table) = JournalOperations.ScanVolume(request.Root, work => pipe.RunAsClient(() => work()), timeout.Token);
                        log($"scan {request.Root}: {header.Status}, {header.Count:N0} entries from {header.Records:N0} records in {header.Seconds:0.0} s");
                        await WriteHeaderAsync(pipe, header, timeout.Token).ConfigureAwait(false);
                        if (table is not null) FileTableTransfer.Write(pipe, table);
                        pipe.WaitForPipeDrain();
                        return;
                    }
                    finally
                    {
                        // NEW (memory): a scan of a large drive needs a few gigabytes for a few seconds. Give it
                        // back to Windows rather than letting the service sit on it until the next scan.
                        table = null;
                        ReleaseMemory();
                    }
                }
                else
                {
                    response = JournalOperations.Execute(request, work => pipe.RunAsClient(() => work()), timeout.Token);
                    log($"{request.Op} {request.Root}: {response.Status}, {response.Records:N0} records, {response.Paths.Count:N0} paths");
                }

                await WriteHeaderAsync(pipe, response, timeout.Token).ConfigureAwait(false);
                pipe.WaitForPipeDrain();
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException or InvalidOperationException)
            {
                log($"Request abandoned: {exception.Message}");
            }
        }
    }

    private static void ReleaseMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static async Task WriteHeaderAsync(Stream pipe, JournalResponse response, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, JournalProtocol.Json) + "\n");
        await pipe.WriteAsync(bytes, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }

    // One UTF-8 line, at most MaxRequestChars characters.
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>();
        var one = new byte[1];

        while (bytes.Count <= MaxRequestChars * 4)
        {
            var read = await stream.ReadAsync(one, token).ConfigureAwait(false);

            if (read == 0 || one[0] == (byte)'\n')
                break;

            bytes.Add(one[0]);
        }

        if (bytes.Count == 0 || bytes.Count > MaxRequestChars * 4)
            return null;

        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
