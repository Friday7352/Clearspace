// Clearspace | Access to NTFS change journals, through the helper service or directly.
//
// NEW (journal catch-up). Three ways in, tried in order:
//   1. Clearspace itself runs as administrator: read the journal in-process.
//   2. The Clearspace Index Helper service is installed and answering: ask it over its pipe.
//   3. Neither: journal catch-up is unavailable; the index falls back to full rescans as before.

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Clearspace.Journal;
using Microsoft.Win32;

namespace Clearspace.Services;

internal enum JournalAccess
{
    Unknown,       // not checked yet
    Direct,        // Clearspace is elevated
    Helper,        // helper service answering
    HelperStopped, // installed but not answering
    NotInstalled,
    Unsupported    // helper not shipped next to Clearspace (a development build)
}

internal static class IndexJournal
{
    public const string HelperExeName = "ClearspaceIndexHelper.exe";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(5);

    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, HelperExeName);

    public static bool HelperShipped => File.Exists(HelperPath);

    public static bool HelperInstalled
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{JournalProtocol.ServiceName}");
                return key is not null;
            }
            catch (Exception) { return false; }
        }
    }

    // How Clearspace would read a journal right now. Cheap: at most one short pipe connection.
    public static JournalAccess Access()
    {
        if (ElevationService.IsElevated) return JournalAccess.Direct;
        if (HelperInstalled) return Ping() ? JournalAccess.Helper : JournalAccess.HelperStopped;
        return HelperShipped ? JournalAccess.NotInstalled : JournalAccess.Unsupported;
    }

    // NEW: the Indexing page refreshes every second; it does not need to ping the helper that often.
    // CHANGED: a check that started before ForgetCachedAccess() must not store its stale answer.
    private static (JournalAccess Access, DateTime Utc, int Generation)? _cached;
    private static int _generation;

    public static JournalAccess CachedAccess()
    {
        var generation = Volatile.Read(ref _generation);

        if (_cached is { } cached && cached.Generation == generation && DateTime.UtcNow - cached.Utc < TimeSpan.FromSeconds(10))
            return cached.Access;

        var access = Access();
        if (Volatile.Read(ref _generation) == generation)
            _cached = (access, DateTime.UtcNow, generation);
        return access;
    }

    public static void ForgetCachedAccess()
    {
        Interlocked.Increment(ref _generation);
        _cached = null;
    }

    public static string Describe(JournalAccess access) => access switch
    {
        JournalAccess.Direct => "On · Clearspace is running as administrator and reads change journals directly",
        JournalAccess.Helper => "On · the Clearspace Index Helper service is running",
        JournalAccess.HelperStopped => "Installed, but the helper service is not answering",
        JournalAccess.NotInstalled => "Off · drives are rescanned in full when changes may have been missed",
        JournalAccess.Unknown => "Checking…",
        _ => "Unavailable in this build · the helper was not found next to Clearspace"
    };

    public static bool IsAvailable => Access() is JournalAccess.Direct or JournalAccess.Helper;

    public static JournalResponse Query(string root, CancellationToken token)
        => Send(new JournalRequest { Op = JournalProtocol.Query, Root = root }, token);

    public static JournalResponse CatchUp(string root, ulong journalId, long fromUsn, CancellationToken token)
        => Send(new JournalRequest { Op = JournalProtocol.CatchUp, Root = root, JournalId = journalId, FromUsn = fromUsn }, token);

    private static JournalResponse Send(JournalRequest request, CancellationToken token)
    {
        if (!UsnJournal.IsDriveRoot(request.Root))
            return new JournalResponse { Status = JournalProtocol.Unsupported, Message = "Not a local drive." };

        if (ElevationService.IsElevated)
            return JournalOperations.Execute(request, null, token);

        try
        {
            using var pipe = new NamedPipeClientStream(".", JournalProtocol.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            pipe.Connect((int)ConnectTimeout.TotalMilliseconds);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(AnswerTimeout);

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JournalProtocol.Json) + "\n");
            pipe.WriteAsync(bytes, timeout.Token).AsTask().GetAwaiter().GetResult();
            pipe.Flush();

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
            var line = reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();

            return line is null
                ? new JournalResponse { Message = "The helper closed the connection without answering." }
                : JsonSerializer.Deserialize<JournalResponse>(line, JournalProtocol.Json)
                  ?? new JournalResponse { Message = "The helper sent an empty answer." };
        }
        catch (TimeoutException)
        {
            return new JournalResponse { Status = JournalProtocol.Denied, Message = "The index helper is not running." };
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new JournalResponse { Message = $"Could not talk to the index helper: {exception.Message}" };
        }
    }

    // NEW (file-table scan): the whole drive from its file table, through the helper (or directly when
    // elevated). Null table with a message when it cannot be used; the caller then walks folders.
    public static (JournalResponse Header, FileTable? Table) ScanVolume(string root, CancellationToken token)
    {
        if (!UsnJournal.IsDriveRoot(root))
            return (new JournalResponse { Status = JournalProtocol.Unsupported, Message = "Not a local drive." }, null);

        if (ElevationService.IsElevated)
            return JournalOperations.ScanVolume(root, null, token);

        try
        {
            using var pipe = new NamedPipeClientStream(".", JournalProtocol.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            pipe.Connect((int)ConnectTimeout.TotalMilliseconds);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            using var cancel = timeout.Token.Register(() => { try { pipe.Dispose(); } catch (Exception) { } });

            var request = new JournalRequest { Op = JournalProtocol.Scan, Root = root };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JournalProtocol.Json) + "\n");
            pipe.Write(bytes);
            pipe.Flush();

            // The header is one line; read it byte by byte so nothing of the binary part is buffered away.
            var line = new List<byte>();
            for (var next = pipe.ReadByte(); next is >= 0 and not 10; next = pipe.ReadByte()) // 10 = newline
            {
                line.Add((byte)next);
                if (line.Count > 1 << 20) throw new InvalidDataException("The helper's answer header is too long.");
            }

            var header = JsonSerializer.Deserialize<JournalResponse>(Encoding.UTF8.GetString(line.ToArray()), JournalProtocol.Json)
                         ?? new JournalResponse { Message = "The helper sent an empty answer." };

            if (header.Status != JournalProtocol.Ok || header.Count <= 0)
                return (header, null);

            return (header, FileTableTransfer.Read(pipe, header.Count, header.NameChars, timeout.Token));
        }
        catch (TimeoutException)
        {
            return (new JournalResponse { Status = JournalProtocol.Denied, Message = "The index helper is not running." }, null);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        {
            token.ThrowIfCancellationRequested();
            return (new JournalResponse { Message = $"The file-table scan did not complete: {exception.Message}" }, null);
        }
    }

    private static bool Ping()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", JournalProtocol.PipeName, PipeDirection.InOut);
            pipe.Connect(150);
            return true;
        }
        catch (Exception) { return false; }
    }

    // Runs the helper elevated (one Windows permission prompt). Returns null on success, else a message.
    public static async Task<string?> RunHelperElevatedAsync(bool install)
    {
        if (!HelperShipped)
            return $"{HelperExeName} was not found next to Clearspace. Build with \"Build Clearspace.cmd\" or reinstall.";

        // CHANGED: the elevated helper runs hidden, so it writes its outcome (or its error) to this file.
        var result = Path.Combine(Path.GetTempPath(), $"clearspace-helper-{Guid.NewGuid():N}.txt");

        try
        {
            using var process = Process.Start(new ProcessStartInfo(HelperPath,
                $"{(install ? "--install" : "--uninstall")} --quiet --result \"{result}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process is null) return "Windows did not start the helper.";
            await process.WaitForExitAsync().ConfigureAwait(false);

            var outcome = File.Exists(result) ? (await File.ReadAllTextAsync(result).ConfigureAwait(false)).Trim() : null;

            return process.ExitCode == 0 ? null
                : !string.IsNullOrEmpty(outcome) && outcome != "ok" ? outcome
                : $"The helper exited with code {process.ExitCode} without saying why.";
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return "Cancelled at the Windows permission prompt.";
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
        finally
        {
            try { File.Delete(result); } catch (Exception) { }
            ForgetCachedAccess();
        }
    }
}
