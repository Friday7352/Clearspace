// Clearspace | Handing memory back to Windows after large, one-off work.
//
// NEW (memory). Building or replacing a drive's index briefly holds two copies of it (the one in use and
// the new one), and the one-time file-table check builds a third. Once that work is done the old copies
// are garbage, but .NET keeps the space it grew into for reuse, so Task Manager went on showing the peak
// for the rest of the session. An aggressive collection after such work compacts the large arrays and
// returns the free space. It pauses the app briefly, so it runs only after work that already took
// seconds or minutes, and at most once a minute.

using System.Diagnostics;
using System.Runtime;

namespace Clearspace.Services;

internal static class MemoryRelief
{
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(1);
    private static long _lastTicks;

    public static void Release()
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastTicks);
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < MinimumGap) return;
        if (Interlocked.CompareExchange(ref _lastTicks, now, last) != last) return;

        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Clearspace: could not release memory. {exception.Message}");
        }
    }
}
