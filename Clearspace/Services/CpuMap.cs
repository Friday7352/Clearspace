// Clearspace | The processor as it is built, and what it is doing, for drawing it.
//
// NEW (round 62): zoomed into the processor on the motherboard, the disk map opens its lid onto the die:
// its core complexes, the cores in them with their threads and caches, each lit by how busy it is, and
// beside it what is running - every program sized by the share of the processor it is using. All of it
// comes from Windows without administrator rights:
//  - how the chip is built, from GetLogicalProcessorInformationEx: every core and the threads (logical
//    processors) on it, whether it is a performance or an efficiency core (hybrid chips), and every cache
//    with its size and the threads that share it. A Ryzen's L3 caches are its core complexes; an Intel's
//    efficiency cores come in fours that share an L2. Read once;
//  - how busy each thread is, from the time it spent idle since the last reading
//    (SystemProcessorPerformanceInformation, asked per processor group);
//  - each thread's clock, from the "% Processor Performance" counter (the one Task Manager's speed uses),
//    against the processor's base clock; where that is not available, the clock Windows reports;
//  - each program's share, from the processor time its processes used since the last reading
//    (SystemProcessInformation - the same list the memory view reads).
// Temperatures need a kernel driver, so they are not read.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Clearspace.Services;

/// <summary>A core: its number, the logical processors (threads) on it, and its efficiency class
/// (higher is faster; all the same on a chip that is not hybrid).</summary>
internal sealed record CpuCore(int Number, int[] Threads, int Efficiency);

/// <summary>A cache: level, kind (0 unified, 1 instructions, 2 data), size, and the threads sharing it.</summary>
internal sealed record CpuCache(int Level, int Kind, long Bytes, int[] Threads);

/// <summary>How the processor is built. Threads are logical processor numbers (group × 64 + number).</summary>
internal sealed record CpuTopology(CpuCore[] Cores, CpuCache[] Caches, int[] Threads, int BaseMhz)
{
    public bool Hybrid => Cores.Select(core => core.Efficiency).Distinct().Count() > 1;
    public int FastClass => Cores.Length == 0 ? 0 : Cores.Max(core => core.Efficiency);
}

/// <summary>A program's share of the whole processor (0 to 1) over the last second, all its processes together.</summary>
internal sealed record CpuProgram(string Key, string Name, string? Path, int Processes, double Share);

/// <summary>One reading: how busy each thread is (0 to 1) and its clock (MHz, 0 when unknown), by thread
/// number; the whole processor's load; and the programs using it.</summary>
internal sealed record CpuSnapshot(CpuTopology Topology, Dictionary<int, double> Load, Dictionary<int, double> Mhz, double Total, CpuProgram[] Programs);

internal static class CpuMap
{
    private static readonly Lock Gate = new();
    private static CpuTopology? _topology;
    private static Dictionary<int, (long Idle, long Busy)>? _threadTimes;
    private static Dictionary<(int Pid, long Started), long>? _processTimes;
    private static long _readAt;
    private static readonly Dictionary<(int Pid, long Started), string?> Paths = [];
    private static IntPtr _query, _performance, _frequency;
    private static bool _counterTried;
    private static long _sampledAt;   // system time (FILETIME) of the last reading, for processes started since

    private readonly record struct ProcessTime(int Pid, long Started, string Name, long Time);

    /// <summary>Reads the processor now. The first reading waits a moment to have something to compare
    /// with; after that it takes a few milliseconds. Call it off the UI thread.</summary>
    public static CpuSnapshot Read()
    {
        lock (Gate)
        {
            _topology ??= ReadTopology();
            // A first reading, or one after a long look elsewhere, starts from a fresh baseline: shares averaged
            // over minutes away would say nothing about now.
            if (_threadTimes is null || Stopwatch.GetElapsedTime(_readAt).TotalSeconds > 5)
            {
                Sample(out _, out _);
                StartClocks();   // the clock counters are rates too: they start over with the same window
                Thread.Sleep(400);
            }
            var total = Sample(out var load, out var programs);
            return load is null ? new CpuSnapshot(_topology, [], [], 0, []) : new CpuSnapshot(_topology, load, Clocks(_topology), total, programs);
        }
    }

    // Everything that is measured as time since the last reading: each thread's load and each program's share.
    private static double Sample(out Dictionary<int, double>? load, out CpuProgram[] programs)
    {
        var topology = _topology!;
        var now = Stopwatch.GetTimestamp();
        var systemNow = DateTime.UtcNow.ToFileTimeUtc();
        var threads = ThreadTimes(topology);
        var processes = ProcessTimes();
        var seconds = _readAt == 0 ? 0 : (now - _readAt) / (double)Stopwatch.Frequency;
        load = null;
        programs = [];
        double total = 0;
        if (_threadTimes is { } before && seconds > .05)
        {
            load = [];
            foreach (var (thread, (idle, busy)) in threads)
            {
                if (!before.TryGetValue(thread, out var last)) continue;
                var span = busy - last.Busy;   // kernel (with idle) and user time: all of it
                load[thread] = span <= 0 ? 0 : Math.Clamp(1 - (idle - last.Idle) / (double)span, 0, 1);
            }
            total = load.Count == 0 ? 0 : load.Values.Average();

            // Each program's share: its processes' time since the last reading, over all threads' time.
            var capacity = seconds * 1e7 * Math.Max(1, threads.Count);   // in 100-nanosecond units
            var shares = new Dictionary<string, (string Name, string? Path, int Processes, double Share)>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in processes)
            {
                if (process.Pid == 0) continue;   // the idle process: what is left over
                var id = (process.Pid, process.Started);
                // A process started since the last reading: all its time is new (a build's short-lived compilers).
                var used = _processTimes!.TryGetValue(id, out var earlier) ? Math.Max(0, process.Time - earlier)
                    : process.Started >= _sampledAt ? process.Time : 0;
                if (!Paths.TryGetValue(id, out var path)) Paths[id] = path = ImagePath(process.Pid);
                var key = path ?? process.Name;
                (string Name, string? Path, int Processes, double Share) known = shares.TryGetValue(key, out var found) ? found : (MemoryMap.ProgramName(process.Name, path), path, 0, 0d);
                shares[key] = (known.Name, known.Path, known.Processes + 1, known.Share + used / capacity);
            }
            programs = [.. shares.Where(pair => pair.Value.Share > 0)
                .Select(pair => new CpuProgram(pair.Key, pair.Value.Name, pair.Value.Path, pair.Value.Processes, Math.Min(1, pair.Value.Share)))
                .OrderByDescending(program => program.Share)];
        }
        _threadTimes = threads;
        _processTimes = processes.ToDictionary(process => (process.Pid, process.Started), process => process.Time);
        foreach (var gone in Paths.Keys.Where(id => !_processTimes.ContainsKey(id)).ToArray()) Paths.Remove(gone);
        _readAt = now;
        _sampledAt = systemNow;
        return total;
    }

    // ---------------------------------------------------------------- how the chip is built

    private static CpuTopology ReadTopology()
    {
        var cores = new List<CpuCore>();
        var caches = new List<CpuCache>();
        try
        {
            var length = 0;
            GetLogicalProcessorInformationEx(RelationAll, IntPtr.Zero, ref length);
            if (length > 0)
            {
                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (GetLogicalProcessorInformationEx(RelationAll, buffer, ref length))
                        for (var offset = 0; offset + 8 <= length;)
                        {
                            var entry = buffer + offset;
                            var relation = Marshal.ReadInt32(entry, 0);
                            var size = Marshal.ReadInt32(entry, 4);
                            if (size <= 0) break;
                            var body = entry + 8;
                            switch (relation)
                            {
                                case RelationProcessorCore:
                                    // PROCESSOR_RELATIONSHIP: Flags, EfficiencyClass, 20 reserved, GroupCount at 22,
                                    // then GROUP_AFFINITY (mask, group, 3 reserved: 16 bytes) from 24.
                                    cores.Add(new CpuCore(cores.Count, Masks(body + 24, Math.Max(1, (int)(ushort)Marshal.ReadInt16(body, 22))),
                                        Marshal.ReadByte(body, 1)));
                                    break;
                                case RelationCache:
                                    // CACHE_RELATIONSHIP: Level, Associativity, LineSize, CacheSize at 4, Type at 8,
                                    // reserved, GroupCount at 30 (0 on older Windows: one), GROUP_AFFINITY from 32.
                                    caches.Add(new CpuCache(Marshal.ReadByte(body, 0), Marshal.ReadInt32(body, 8), (uint)Marshal.ReadInt32(body, 4),
                                        Masks(body + 32, Math.Max(1, (int)(ushort)Marshal.ReadInt16(body, 30)))));
                                    break;
                            }
                            offset += size;
                        }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch (Exception) { }
        if (cores.Count == 0)   // could not be read: one core per logical processor
            cores.AddRange(Enumerable.Range(0, Environment.ProcessorCount).Select(n => new CpuCore(n, [n], 0)));
        var threads = cores.SelectMany(core => core.Threads).Distinct().Order().ToArray();
        return new CpuTopology([.. cores.OrderBy(core => core.Threads.Min())], [.. caches], threads, BaseMhz(threads.Length));
    }

    private static int[] Masks(IntPtr at, int count)
    {
        var threads = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var mask = (ulong)Marshal.ReadInt64(at, i * 16);
            var group = (ushort)Marshal.ReadInt16(at, i * 16 + 8);
            for (var bit = 0; bit < 64; bit++)
                if ((mask >> bit & 1) != 0) threads.Add(group * 64 + bit);
        }
        return [.. threads];
    }

    // ---------------------------------------------------------------- busy and idle time per thread

    private static Dictionary<int, (long Idle, long Busy)> ThreadTimes(CpuTopology topology)
    {
        var times = new Dictionary<int, (long, long)>();
        foreach (var group in topology.Threads.Select(thread => (ushort)(thread / 64)).Distinct())
        {
            // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION: IdleTime, KernelTime (which includes idle), UserTime,
            // DpcTime, InterruptTime, InterruptCount - 48 bytes, one per processor in the group.
            const int Entry = 48;
            var size = Entry * 64;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var input = group;
                if (NtQuerySystemInformationEx(SystemProcessorPerformanceInformation, ref input, sizeof(ushort), buffer, size, out var returned) < 0) continue;
                for (var i = 0; i < Math.Min(64, returned / Entry); i++)
                {
                    var idle = Marshal.ReadInt64(buffer, i * Entry);
                    var kernel = Marshal.ReadInt64(buffer, i * Entry + 8);
                    var user = Marshal.ReadInt64(buffer, i * Entry + 16);
                    times[group * 64 + i] = (idle, kernel + user);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return times;
    }

    // ---------------------------------------------------------------- clocks

    // The base clock: what Windows reports as each processor's "maximum" (its rated speed), in MHz.
    private static int BaseMhz(int count)
    {
        const int Entry = 24;   // PROCESSOR_POWER_INFORMATION: Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState
        var size = Entry * Math.Max(1024, Math.Max(count, Environment.ProcessorCount));   // too small fails outright
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            return CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, buffer, size) == 0 ? Marshal.ReadInt32(buffer, 4) : 0;
        }
        catch (Exception) { return 0; }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Each thread's clock now: its own rated clock ("Processor Frequency", so an efficiency core is measured
    // against its own) times "% Processor Performance" (above 100 when boosting) - the way Task Manager
    // works out its speed. Where the rated clock is missing, the processor's base clock stands in.
    private static void StartClocks()
    {
        try
        {
            if (!_counterTried)
            {
                _counterTried = true;
                if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
                if (PdhAddEnglishCounterW(_query, @"\Processor Information(*)\% Processor Performance", IntPtr.Zero, out _performance) != 0) _performance = IntPtr.Zero;
                if (PdhAddEnglishCounterW(_query, @"\Processor Information(*)\Processor Frequency", IntPtr.Zero, out _frequency) != 0) _frequency = IntPtr.Zero;
            }
            if (_query != IntPtr.Zero) PdhCollectQueryData(_query);   // a rate: this collection is only a starting point
        }
        catch (Exception) { }
    }

    private static Dictionary<int, double> Clocks(CpuTopology topology)
    {
        var clocks = new Dictionary<int, double>();
        try
        {
            StartClocks();   // (collects now: the rate since the last collection)
            if (_query == IntPtr.Zero || _performance == IntPtr.Zero) return clocks;
            var performance = Values(_performance);
            var rated = _frequency == IntPtr.Zero ? [] : Values(_frequency);
            foreach (var (thread, percent) in performance)
            {
                var base_ = rated.GetValueOrDefault(thread) is > 0 and var own ? own : topology.BaseMhz;
                if (base_ > 0 && percent > 0) clocks[thread] = base_ * percent / 100;
            }
        }
        catch (Exception) { }
        return clocks;
    }

    // A counter's value for every logical processor, by thread number.
    private static Dictionary<int, double> Values(IntPtr counter)
    {
        var values = new Dictionary<int, double>();
        uint size = 0;
        if (PdhGetFormattedCounterArrayW(counter, FormatDouble, ref size, out _, IntPtr.Zero) != MoreData || size == 0) return values;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, FormatDouble, ref size, out var count, buffer) != 0) return values;
            // PDH_FMT_COUNTERVALUE_ITEM_W: the instance name ("group,number"), then the value: status at 8,
            // the double at 16 - 24 bytes each.
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * 24;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0)) ?? "";
                if (Marshal.ReadInt32(item, 8) is not (0 or 1)) continue;   // PDH_CSTATUS_VALID_DATA / NEW_DATA
                var comma = name.IndexOf(',');
                if (comma <= 0 || !int.TryParse(name[..comma], out var group) || !int.TryParse(name[(comma + 1)..], out var number)) continue;   // skips the totals
                var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                if (double.IsFinite(value)) values[group * 64 + number] = value;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return values;
    }

    // ---------------------------------------------------------------- processor time per process

    private static List<ProcessTime> ProcessTimes()
    {
        var list = new List<ProcessTime>();
        var size = 1 << 20;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out var needed);
                if (status == InfoLengthMismatch) { size = Math.Max(size * 2, needed + (64 << 10)); continue; }
                if (status < 0) return list;
                // SYSTEM_PROCESS_INFORMATION (x64): NextEntryOffset at 0, CreateTime at 32, UserTime at 40,
                // KernelTime at 48, ImageName (length at 56, buffer at 64), UniqueProcessId at 80.
                for (var offset = 0; ;)
                {
                    var entry = buffer + offset;
                    var next = Marshal.ReadInt32(entry, 0);
                    var pid = (int)Marshal.ReadIntPtr(entry, 80);
                    var nameLength = (ushort)Marshal.ReadInt16(entry, 56);
                    var namePointer = Marshal.ReadIntPtr(entry, 64);
                    var name = namePointer != 0 && nameLength > 0 ? Marshal.PtrToStringUni(namePointer, nameLength / 2) : pid == 0 ? "Idle" : "System";
                    list.Add(new ProcessTime(pid, Marshal.ReadInt64(entry, 32), name, Marshal.ReadInt64(entry, 40) + Marshal.ReadInt64(entry, 48)));
                    if (next <= 0) break;
                    offset += next;
                    if (offset >= size) break;
                }
                return list;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return list;
    }

    private static string? ImagePath(int pid)
    {
        using var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle.IsInvalid) return null;
        var buffer = new char[1024];
        var length = buffer.Length;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref length) && length > 0 ? new string(buffer, 0, length) : null;
    }

    // ---------------------------------------------------------------- Windows

    private const int RelationProcessorCore = 0, RelationCache = 2, RelationAll = 0xFFFF;
    private const int SystemProcessInformation = 5, SystemProcessorPerformanceInformation = 8;
    private const int InfoLengthMismatch = unchecked((int)0xC0000004);
    private const int ProcessorInformation = 11;
    private const uint QueryLimitedInformation = 0x1000;
    private const uint FormatDouble = 0x00000200 | 0x00008000;   // PDH_FMT_DOUBLE | PDH_FMT_NOCAP100: boosting goes past 100%
    private const int MoreData = unchecked((int)0x800007D2);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref int length);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, IntPtr buffer, int length, out int returned);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformationEx(int informationClass, ref ushort input, int inputLength, IntPtr buffer, int length, out int returned);

    [DllImport("powrprof.dll")]
    private static extern int CallNtPowerInformation(int level, IntPtr input, int inputLength, IntPtr output, int outputLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, int flags, [Out] char[] name, ref int size);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? source, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
}
