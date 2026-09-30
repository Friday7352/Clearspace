// Clearspace | Turning change-journal records into the paths the index must re-check.
//
// NEW (journal catch-up): shared by Clearspace and ClearspaceIndexHelper. The result is deliberately
// simple: a list of paths that changed in any way. Clearspace re-reads each one from disk and updates
// or removes it (FileIndexUpdater), so the final state on disk always wins, however the records were
// ordered, and replaying the same range twice is harmless.

using System.IO;
using System.Text.Json;

namespace Clearspace.Journal;

internal static class ChangePlanner
{
    // Paths touched by index-relevant records, in first-seen order, plus how many records could not be
    // placed. `pathOf` returns a folder's current path only if the caller may list it.
    // FIXED (permission review): names are only returned under folders that exist now and that the caller
    // can list. Records under a folder that has since been deleted are not placed through the journal's own
    // history any more (that would reveal names the caller never had to list); they are not needed either:
    // the deleted folder's own record is placed under its (existing) parent, and removing it from the index
    // removes everything under it.
    internal static (List<string> Paths, int Unresolved) Collect(IReadOnlyList<JournalRecord> records, Func<UInt128, string?> pathOf)
    {
        var folders = new Dictionary<UInt128, string?>();

        string? FolderPath(UInt128 id, int depth)
        {
            if (folders.TryGetValue(id, out var known))
                return known;

            var path = pathOf(id);
            folders[id] = path;
            return path;
        }

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = 0;

        foreach (var record in records)
        {
            if ((record.Reasons & UsnJournal.IndexRelevant) == 0 || record.Name.Length == 0)
                continue;

            var parent = FolderPath(record.ParentId, 0);

            if (parent is null)
            {
                unresolved++;
                continue;
            }

            var path = Path.Join(parent, record.Name);

            if (seen.Add(path))
                paths.Add(path);
        }

        return (paths, unresolved);
    }
}

// NEW: the helper's pipe protocol. One JSON request line, one JSON response line, then the pipe closes.
internal sealed class JournalRequest
{
    public int Version { get; set; } = JournalProtocol.Version;
    public string Op { get; set; } = "";
    public string Root { get; set; } = "";
    public ulong JournalId { get; set; }
    public long FromUsn { get; set; }
}

internal sealed class JournalResponse
{
    public int Version { get; set; } = JournalProtocol.Version;
    public string Status { get; set; } = JournalProtocol.Failed;
    public string? Message { get; set; }
    public ulong JournalId { get; set; }
    public long FirstUsn { get; set; }
    public long NextUsn { get; set; }
    public int Records { get; set; }
    public int Unresolved { get; set; }
    public List<string> Paths { get; set; } = [];

    // NEW (file-table scan): a binary FileTable of Count entries and NameChars name characters follows.
    public int Count { get; set; }
    public int NameChars { get; set; }
    public int DeniedFolders { get; set; }
    public double Seconds { get; set; }
}

internal static class JournalProtocol
{
    public const int Version = 1;
    public const string PipeName = "Clearspace.IndexHelper.v1";
    public const string ServiceName = "ClearspaceIndexHelper";

    public const string Query = "query";
    public const string CatchUp = "catchup";
    public const string Scan = "scan"; // NEW: read the whole file table

    public const string Ok = "ok";
    public const string Reset = "reset";              // journal recreated: its old positions mean nothing
    public const string HistoryLost = "history-lost"; // journal wrapped past the saved position
    public const string Unsupported = "unsupported";  // not NTFS/ReFS, or not a local drive
    public const string Denied = "denied";            // not running with the rights to read the journal
    public const string TooMany = "too-many";         // a full scan is cheaper
    public const string Failed = "failed";

    public static readonly JsonSerializerOptions Json = new() { MaxDepth = 8 };
}

// NEW: one request, executed wherever the journal can be read. `asCaller` runs the path-resolving
// part as the person who asked (the helper impersonates its pipe client); null runs it as ourselves.
internal static class JournalOperations
{
    private static T AsCaller<T>(Action<Action>? asCaller, Func<T> work)
    {
        if (asCaller is null)
            return work();

        T result = default!;
        asCaller(() => result = work());
        return result;
    }

    // NEW (file-table scan): the whole drive, as the caller could list it. Also returns the journal
    // position from just before the read, so journal catch-up can take over from there.
    public static (JournalResponse Header, FileTable? Table) ScanVolume(string root, Action<Action>? asCaller, CancellationToken token)
    {
        if (!UsnJournal.IsDriveRoot(root))
            return (new JournalResponse { Status = JournalProtocol.Unsupported, Message = "Only local drive roots such as C:\\ are supported." }, null);

        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            using var volume = UsnJournal.OpenVolume(root);
            var header = new JournalResponse { Status = JournalProtocol.Ok };

            try
            {
                var state = UsnJournal.Query(volume);
                header.JournalId = state.JournalId;
                header.FirstUsn = state.FirstUsn;
                header.NextUsn = state.NextUsn;
            }
            catch (JournalException) { } // no journal: the scan is still useful

            var table = MftReader.Read(volume, root, groups => AsCaller(asCaller, () => FolderAccess.Denied(root, groups)), token);
            header.Count = table.Count;
            header.NameChars = table.NameChars;
            header.DeniedFolders = table.DeniedFolders;
            header.Records = (int)Math.Min(int.MaxValue, table.RecordsRead);
            header.Seconds = started.Elapsed.TotalSeconds;
            return (header, table);
        }
        catch (JournalException exception)
        {
            return (Failure(exception), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            return (new JournalResponse { Status = JournalProtocol.Failed, Message = exception.Message }, null);
        }
    }

    private static JournalResponse Failure(JournalException exception) => new()
    {
        Status = exception.Error switch
        {
            JournalError.AccessDenied => JournalProtocol.Denied,
            JournalError.NotSupported => JournalProtocol.Unsupported,
            JournalError.NotActive => JournalProtocol.Reset,
            JournalError.HistoryLost => JournalProtocol.HistoryLost,
            JournalError.TooManyChanges => JournalProtocol.TooMany,
            _ => JournalProtocol.Failed
        },
        Message = exception.Message
    };

    public static JournalResponse Execute(JournalRequest request, Action<Action>? asCaller,
        CancellationToken token)
    {
        if (!UsnJournal.IsDriveRoot(request.Root))
            return new JournalResponse { Status = JournalProtocol.Unsupported, Message = "Only local drive roots such as C:\\ are supported." };

        try
        {
            using var volume = UsnJournal.OpenVolume(request.Root);
            var state = UsnJournal.Query(volume);
            var response = new JournalResponse
            {
                Status = JournalProtocol.Ok,
                JournalId = state.JournalId,
                FirstUsn = state.FirstUsn,
                NextUsn = state.NextUsn
            };

            if (request.Op == JournalProtocol.Query)
                return response;

            if (request.Op != JournalProtocol.CatchUp)
                return new JournalResponse { Message = $"Unknown request \"{request.Op}\"." };

            if (state.JournalId != request.JournalId || request.FromUsn > state.NextUsn)
            {
                response.Status = JournalProtocol.Reset;
                response.Message = "The drive's change journal was recreated since the last catch-up.";
                return response;
            }

            if (request.FromUsn < state.FirstUsn)
            {
                response.Status = JournalProtocol.HistoryLost;
                response.Message = "The change journal no longer reaches back to the last catch-up.";
                return response;
            }

            var records = UsnJournal.Read(volume, state.JournalId, request.FromUsn, state.NextUsn, token);
            response.Records = records.Count;

            JournalResponse Resolve()
            {
                using var resolver = new FileIdResolver(request.Root);
                var (paths, unresolved) = ChangePlanner.Collect(records, resolver.PathOf);
                response.Paths = paths;
                response.Unresolved = unresolved;
                return response;
            }

            return AsCaller(asCaller, Resolve);
        }
        catch (JournalException exception)
        {
            return Failure(exception);
        }
    }
}
