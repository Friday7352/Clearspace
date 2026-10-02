// Clearspace | Mapped network drives that can be indexed.
//
// NEW (round 50): network drives are indexed only when the user turns that on, and only while they
// answer. Asking Windows whether a mapped drive is ready goes out to the server, and a sleeping NAS or a
// share on a network you have left can take many seconds to say no - so that question is never asked on
// the UI thread or by the indexer. It is asked here, in the background, with a time limit, once a minute;
// everyone else reads the last answer.

using System.IO;

namespace Clearspace.Services;

internal static class NetworkDrives
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(4);
    private static volatile string[] _ready = [];
    private static readonly Lock Gate = new();
    private static Timer? _timer;
    private static int _probing;
    // A sleeping NAS can take longer than the time limit to wake, so a drive counts as gone only after
    // two checks in a row that did not answer; one slow answer does not make it leave and come back.
    private static readonly Dictionary<string, int> Misses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised (on a background thread) when the set of reachable network drives changes.</summary>
    public static event Action? Changed;

    /// <summary>Whether network drives are indexed at all (the user's setting).</summary>
    public static bool Enabled => SettingsService.GetIndexNetworkDrives();

    /// <summary>Mapped network drives that answered the last check, when network indexing is on.</summary>
    public static string[] Ready => Enabled ? _ready : [];

    public static bool IsReady(string root)
        => Array.Exists(Ready, ready => ready.Equals(root, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a root is a mapped network drive. Fast: it reads the mapping, not the server.</summary>
    public static bool IsNetworkRoot(string root)
    {
        try { return new DriveInfo(root).DriveType == DriveType.Network; }
        catch (Exception) { return false; }
    }

    /// <summary>Starts checking in the background (idempotent), and checks now.</summary>
    public static void Start()
    {
        lock (Gate) _timer ??= new Timer(_ => Probe(), null, TimeSpan.Zero, Every);
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
        if (_ready.Length == 0) return;
        _ready = [];
        Changed?.Invoke();
    }

    /// <summary>Checks again now, without waiting for the minute.</summary>
    public static void ProbeSoon() => ThreadPool.QueueUserWorkItem(_ => Probe());

    private static void Probe()
    {
        if (!Enabled || Interlocked.Exchange(ref _probing, 1) == 1) return;
        try
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (Exception) { return; }
            var ready = new List<string>();
            foreach (var drive in drives)
            {
                string root;
                try
                {
                    if (drive.DriveType != DriveType.Network) continue;
                    root = drive.RootDirectory.FullName;
                }
                catch (Exception) { continue; }
                // A check that does not answer in time counts as not ready; its thread is left to finish
                // (or not) on its own, which is the only way to stop waiting on an unreachable server.
                var check = Task.Run(() =>
                {
                    try { return drive.IsReady && drive.TotalSize > 0; }
                    catch (Exception) { return false; }
                });
                if (check.Wait(Patience) && check.Result) { ready.Add(root); Misses.Remove(root); }
                else if (Array.Exists(_ready, known => known.Equals(root, StringComparison.OrdinalIgnoreCase)) &&
                         (Misses[root] = Misses.GetValueOrDefault(root) + 1) < 2)
                    ready.Add(root);   // missed once: give it another minute
            }
            var next = ready.OrderBy(root => root, StringComparer.OrdinalIgnoreCase).ToArray();
            if (next.SequenceEqual(_ready, StringComparer.OrdinalIgnoreCase)) return;
            _ready = next;
            Changed?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _probing, 0);
        }
    }
}
