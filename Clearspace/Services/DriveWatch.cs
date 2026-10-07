// Clearspace | NEW (live drives): noticing a drive being plugged in or taken out while Clearspace is open.
// The sidebar and the This PC page used to read the drives when a window opened and after mapping a
// network drive, and not otherwise: a USB stick plugged in later only showed after a restart or F5.
//
// Windows tells every top-level window when hardware changes (the WM_DEVICECHANGE message; no sign-up is
// needed for drives). Each main window passes that on to DeviceChanged (MainWindow.Drives.cs). The message
// says little and arrives in bursts, often before the new drive can be read, so nothing is taken from it
// but "look again": half a second after the last one, the ready drives are read off the UI thread and
// compared with the list from before (Diff). When a drive has come, gone or been renamed, Changed is
// raised on the UI thread with what changed. A look that finds nothing new is repeated twice a little
// later, for a drive that takes a moment to come up.
//
// Network drives are left out here: asking whether one is ready can take many seconds, and
// Services/NetworkDrives.cs already keeps an eye on them.
// What this does not do: tell the file index. A drive plugged in while Clearspace is open is searched by
// reading it, as before, and is indexed from the next start; indexing it at once would also keep it busy
// and in the way of "Safely remove".
// Tests: Clearspace.Tests/DriveWatchTests.cs (Diff). The message and the timing are checked by hand.
using System.IO;
using System.Windows;

namespace Clearspace.Services;

/// <summary>A drive that can be read right now.</summary>
/// <param name="Root">"E:\"</param>
/// <param name="Label">Its name as the sidebar shows it ("USB DISK"; "Local Disk" when it has none).</param>
internal sealed record DriveSeen(string Root, string Label, DriveType Type)
{
    /// <summary>"USB DISK (E:)"</summary>
    public string Title => $"{Label} ({Root.TrimEnd('\\', '/')})";
}

/// <summary>What changed between two looks at the drives.</summary>
internal sealed record DriveChange(long Id, IReadOnlyList<DriveSeen> Arrived, IReadOnlyList<DriveSeen> Removed, IReadOnlyList<DriveSeen> Renamed)
{
    public bool Any => Arrived.Count > 0 || Removed.Count > 0 || Renamed.Count > 0;
}

internal static class DriveWatch
{
    internal const int WM_DEVICECHANGE = 0x0219;

    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LookAgain = TimeSpan.FromMilliseconds(1500);
    private const int MostLooks = 3;

    private static readonly Lock Gate = new();
    private static Timer? _timer;
    private static List<DriveSeen>? _known;
    private static int _looksLeft;
    private static long _changes;

    /// <summary>Raised on the UI thread when a drive has been plugged in, taken out or renamed.</summary>
    internal static event Action<DriveChange>? Changed;

    /// <summary>The drives as they were at the last look (empty before the first).</summary>
    internal static IReadOnlyList<DriveSeen> Known
    {
        get { lock (Gate) return _known is null ? [] : [.. _known]; }
    }

    /// <summary>Takes the first look, so the first change has something to be compared with. Safe to call often.</summary>
    internal static void Start()
    {
        lock (Gate)
        {
            if (_known is not null || _timer is not null)
                return;
            _timer = new Timer(_ => Look(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Windows said some hardware changed (any window's WM_DEVICECHANGE): look at the drives once things settle.</summary>
    internal static void DeviceChanged()
    {
        lock (Gate)
        {
            _looksLeft = MostLooks;
            _timer ??= new Timer(_ => Look(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(Settle, Timeout.InfiniteTimeSpan);
        }
    }

    // On a timer thread: reads the drives, and says so when they differ from last time.
    private static void Look()
    {
        List<DriveSeen> now;
        try { now = Read(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }

        DriveChange? change = null;
        lock (Gate)
        {
            if (_known is null)
            {
                _known = now; // the first look: nothing to compare with
                return;
            }

            var found = Diff(_known, now, _changes + 1);
            _known = now;
            if (found.Any)
            {
                _changes++;
                _looksLeft = 0;
                change = found;
            }
            else if (--_looksLeft > 0)
            {
                _timer?.Change(LookAgain, Timeout.InfiniteTimeSpan); // a drive that is not up yet
            }
        }

        if (change is null)
            return;

        // The letter may stand for another device now.
        foreach (var drive in change.Arrived.Concat(change.Removed))
            DriveHardwareProbe.Forget(drive.Root);

        var app = Application.Current;
        if (app is null)
            return;
        app.Dispatcher.BeginInvoke(() => Changed?.Invoke(change));
    }

    /// <summary>The drives that can be read right now, network drives left out. Asks each drive, so call it off the UI thread.</summary>
    internal static List<DriveSeen> Read()
    {
        var found = new List<DriveSeen>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Network || !drive.IsReady)
                    continue;

                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
                found.Add(new DriveSeen(drive.RootDirectory.FullName, label, drive.DriveType));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // taken out between being listed and being asked
            }
        }

        return found;
    }

    /// <summary>What is in <paramref name="after"/> and not in <paramref name="before"/>, the other way round, and what has another name.</summary>
    internal static DriveChange Diff(IReadOnlyList<DriveSeen> before, IReadOnlyList<DriveSeen> after, long id = 0)
    {
        var was = before.ToDictionary(drive => drive.Root, StringComparer.OrdinalIgnoreCase);
        var isNow = after.ToDictionary(drive => drive.Root, StringComparer.OrdinalIgnoreCase);

        return new DriveChange(id,
            after.Where(drive => !was.ContainsKey(drive.Root)).ToList(),
            before.Where(drive => !isNow.ContainsKey(drive.Root)).ToList(),
            after.Where(drive => was.TryGetValue(drive.Root, out var old) && (old.Label != drive.Label || old.Type != drive.Type)).ToList());
    }
}
