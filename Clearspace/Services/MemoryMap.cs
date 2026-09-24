// Clearspace | What is in the computer's memory, for drawing it.
//
// NEW (round 59): zoomed into the memory on the motherboard, the disk map shows what the memory holds the
// way it shows a drive: the programs running, each sized by the memory it takes, and inside each one the
// files it has loaded - its .exe, the libraries it pulled in, data files it has mapped - sized by how much
// of each is actually in memory, beside its own working data.
//
// Everything here works without administrator rights:
//  - the process list, with every process's working set, comes from NtQuerySystemInformation, which
//    answers for every process (Task Manager's source);
//  - for each process this user can open, its working set is read page by page (QueryWorkingSet), and
//    each page is put to what it belongs to - a file mapped into the process (GetMappedFileName), shared
//    memory, or the process's own data (VirtualQueryEx). A page shared with other processes counts its
//    share (a page used by four processes counts a quarter in each), so the programs add up to about the
//    memory in use rather than counting common libraries dozens of times;
//  - processes Windows keeps closed to other programs (services, protected processes) are sized from
//    their working set alone, and cannot be looked inside.
// A process whose working set has barely changed since it was last read keeps its last reading, so after
// the first pass only what changed is read again.
// CHANGED (round 60): read every second, live. The process list and every working set cost a few
// milliseconds; reading a process page by page costs more, so each reading re-reads only the processes
// that changed most, within a time budget, and sizes the rest from their last page-by-page reading scaled
// to their working set now. Every size is therefore current each second, and what each is made of catches
// up within a few seconds.
// Windows counts a page's sharers only up to seven. A page of a library every process loads (ntdll,
// kernel32...) says "seven or more", so those pages are kept apart and divided by the number of processes
// that actually hold them, counted across the whole reading.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Clearspace.Services;

internal enum MemoryFileKind : byte { Data, Program, Library, Mapped, Shared, Closed }

/// <summary>Something in a program's memory: a file (with its path), its working data, or shared memory.</summary>
internal sealed record MemoryFile(string Name, string? Path, long Bytes, MemoryFileKind Kind);

/// <summary>A running program: all of its processes together (by executable), and what they hold.
/// Opened is false when Windows let none of its processes be looked inside.</summary>
internal sealed record MemoryProgram(string Key, string Name, string? Path, int Processes, long Bytes, MemoryFile[] Files, bool Opened);

/// <summary>The memory Windows can use (Total), how much of it is available, and the programs in it.</summary>
internal sealed record MemorySnapshot(long Total, long Available, MemoryProgram[] Programs);

internal static class MemoryMap
{
    private const int PageSize = 4096;
    private static readonly Lock Gate = new();
    // What each process held when it was last read, by process id and start time (ids are reused).
    private static Dictionary<(int Pid, long Started), Reading> _readings = [];
    private static readonly ConcurrentDictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase);
    private static ulong[] _pages = [];
    private static readonly HashSet<(int Pid, long Started)> _closed = [];   // NEW (round 60): processes we may not open, not asked again
    private static readonly char[] NameBuffer = new char[32768];   // used under Gate only
    private static Dictionary<(int Pid, long Started), string?> _paths = [];   // NEW (round 60): a process's .exe does not change
    private static Dictionary<string, string> _devices = [];
    private static long _devicesAt = long.MinValue;
    private const long InspectBudgetMs = 120;   // NEW (round 60): page-by-page reading per refresh, after the first

    private sealed record Reading(long WorkingSet, Dictionary<string, Part> Parts, long ReadAt);
    // Bytes: pages counted by their share. Crowded: pages shared by seven or more processes, counted as a
    // seventh each until the reading knows how many processes hold them.
    private readonly record struct Part(string Name, string? Path, double Bytes, MemoryFileKind Kind, double Crowded = 0);
    private readonly record struct ProcessEntry(int Pid, string Name, long Started, long WorkingSet, long PrivateWorkingSet);

    /// <summary>Reads what is in memory now. Takes from tens of milliseconds to about a second (the first
    /// time); call it off the UI thread.</summary>
    public static MemorySnapshot Read()
    {
        lock (Gate)
        {
            var thread = Thread.CurrentThread;
            var priority = thread.Priority;
            try
            {
                thread.Priority = ThreadPriority.BelowNormal;   // never in the way of the map itself
                return ReadAll();
            }
            finally
            {
                thread.Priority = priority;
                if (_pages.Length > 1 << 20) _pages = [];   // do not keep a large page buffer between reads
            }
        }
    }

    private static MemorySnapshot ReadAll()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        long total = 0, available = 0;
        if (GlobalMemoryStatusEx(ref status)) { total = (long)status.TotalPhys; available = (long)status.AvailPhys; }

        var now = Environment.TickCount64;
        if (now - _devicesAt > 30_000) { _devices = DosDevices(); _devicesAt = now; }   // drive letters rarely change
        var devices = _devices;
        var first = _readings.Count == 0;
        var readings = new Dictionary<(int, long), Reading>();
        var paths = new Dictionary<(int, long), string?>();
        var seen = new List<(ProcessEntry Process, string Key, string? Path, Reading? Reading)>();
        var stale = new List<(int Index, double Urgency)>();
        foreach (var process in Processes())
        {
            if (process.Pid == 0) continue;   // the idle process holds no memory
            var id = (process.Pid, process.Started);
            if (!_paths.TryGetValue(id, out var path)) path = ImagePath(process.Pid);
            paths[id] = path;
            Reading? reading = null;
            if (_readings.TryGetValue(id, out var last))
            {
                reading = last;
                // Due again once its working set has moved, or its reading is old (staggered, so they do not all
                // fall due together); the most changed first.
                var moved = Math.Abs(last.WorkingSet - process.WorkingSet);
                var age = now - last.ReadAt;
                if (moved > Math.Max(8L << 20, process.WorkingSet / 50) || age > 30_000 + process.Pid % 16 * 1000)
                    stale.Add((seen.Count, moved + age * 1024d));
            }
            else if (!_closed.Contains(id)) reading = Inspect(process, devices, now);   // new: read it now
            if (reading is null) _closed.Add(id);
            seen.Add((process, path ?? process.Name, path, reading));
        }
        _paths = paths;   // processes that ended are forgotten
        _closed.IntersectWith(paths.Keys);

        // Re-read the most changed, while there is time; the rest keep their last reading, scaled.
        var clock = Stopwatch.StartNew();
        foreach (var (index, _) in stale.OrderByDescending(item => item.Urgency))
        {
            if (!first && clock.ElapsedMilliseconds > InspectBudgetMs) break;
            var (process, key, path, _) = seen[index];
            seen[index] = (process, key, path, Inspect(process, devices, now) ?? seen[index].Reading);
        }
        for (var i = 0; i < seen.Count; i++)
        {
            var (process, key, path, reading) = seen[i];
            if (reading is null) continue;
            readings[(process.Pid, process.Started)] = reading;
            if (reading.WorkingSet > 0 && reading.WorkingSet != process.WorkingSet)
            {
                var scale = Math.Clamp(process.WorkingSet / (double)reading.WorkingSet, .1, 10);
                seen[i] = (process, key, path, reading with
                {
                    Parts = reading.Parts.ToDictionary(pair => pair.Key, pair => pair.Value with { Bytes = pair.Value.Bytes * scale, Crowded = pair.Value.Crowded * scale }, StringComparer.OrdinalIgnoreCase),
                });
            }
        }
        _readings = readings;   // processes that ended are forgotten

        // How many processes hold each crowded page's file (or kind of memory).
        var crowds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, _, _, reading) in seen)
            if (reading is not null)
                foreach (var (partKey, part) in reading.Parts)
                    if (part.Crowded > 0) crowds[partKey] = crowds.GetValueOrDefault(partKey) + 1;

        var programs = new Dictionary<string, (string Name, string? Path, int Processes, bool Opened, Dictionary<string, Part> Parts)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (process, key, path, reading) in seen)
        {
            if (!programs.TryGetValue(key, out var program))
                program = (ProgramName(process.Name, path), path, 0, false, new Dictionary<string, Part>(StringComparer.OrdinalIgnoreCase));
            if (reading is not null)
            {
                foreach (var (partKey, part) in reading.Parts)
                {
                    var crowd = part.Crowded > 0 ? 7d / Math.Max(7, crowds.GetValueOrDefault(partKey)) : 0;
                    Add(program.Parts, partKey, part with { Bytes = part.Bytes + part.Crowded * crowd, Crowded = 0 });
                }
                program.Opened = true;
            }
            else
            {
                // Closed to us: its own pages, and a share of the ones it shares (most of those are libraries
                // every process has loaded, so a third is closer than all of them).
                var estimate = process.PrivateWorkingSet + Math.Max(0, process.WorkingSet - process.PrivateWorkingSet) / 3d;
                Add(program.Parts, "\0closed", new Part("Processes Windows keeps closed", null, estimate, MemoryFileKind.Closed));
            }
            program.Processes++;
            programs[key] = program;
        }

        var result = new List<MemoryProgram>(programs.Count);
        foreach (var (key, program) in programs)
        {
            var files = program.Parts.Values
                .Select(part => new MemoryFile(part.Name, part.Path, (long)Math.Round(part.Bytes), part.Kind))
                .Where(file => file.Bytes > 0)
                .OrderByDescending(file => file.Bytes)
                .ToArray();
            var bytes = files.Sum(file => file.Bytes);
            if (bytes <= 0) continue;
            // A program nothing could be seen in is one block; there is nothing inside to show.
            if (!program.Opened) files = [];
            result.Add(new MemoryProgram(key, program.Name, program.Path, program.Processes, bytes, files, program.Opened));
        }
        return new MemorySnapshot(total, available, [.. result.OrderByDescending(program => program.Bytes)]);
    }

    private static void Add(Dictionary<string, Part> parts, string key, Part part)
        => parts[key] = parts.TryGetValue(key, out var known) ? known with { Bytes = known.Bytes + part.Bytes, Crowded = known.Crowded + part.Crowded } : part;

    // ---------------------------------------------------------------- one process, page by page

    private static Reading? Inspect(ProcessEntry process, Dictionary<string, string> devices, long now)
    {
        using var handle = OpenProcess(QueryInformation | VmRead, false, process.Pid);
        if (handle.IsInvalid) return null;

        // The working set: one entry per page, its address in the high bits and whether (and by how many
        // processes) it is shared in the low ones. The first element is the number of entries.
        var want = (int)Math.Min(int.MaxValue / 8 - 2, process.WorkingSet / PageSize + 4096);
        for (var attempt = 0; ; attempt++)
        {
            if (_pages.Length < want + 1) _pages = new ulong[want + 1];
            if (QueryWorkingSet(handle, _pages, (int)Math.Min(int.MaxValue, (long)_pages.Length * 8))) break;
            if (Marshal.GetLastWin32Error() != BadLength || attempt >= 4) return null;
            want = (int)Math.Min(int.MaxValue / 8 - 2, (long)_pages[0] + (long)_pages[0] / 8 + 4096);
        }
        var count = (int)Math.Min((long)_pages[0], _pages.Length - 1);
        var pages = _pages.AsSpan(1, count);
        pages.Sort();   // by address: the flags are in the low twelve bits, below the page number

        var parts = new Dictionary<string, Part>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<nint, (string Key, Part Part)>();
        ulong regionEnd = 0;
        string partKey = "";
        Part current = default;
        double pending = 0, crowded = 0;
        foreach (var entry in pages)
        {
            var address = entry & ~0xFFFUL;
            if (address >= regionEnd)
            {
                if (pending > 0 || crowded > 0) Add(parts, partKey, current with { Bytes = pending, Crowded = crowded });
                pending = crowded = 0;
                if (address > HighestUserAddress)
                {
                    // The process's page tables, at kernel addresses (and sorted last): its own, and no address
                    // to ask about.
                    regionEnd = ulong.MaxValue;
                    (partKey, current) = ("\0data", new Part("Working data", null, 0, MemoryFileKind.Data));
                }
                else if (VirtualQueryEx(handle, (nint)address, out var region, Marshal.SizeOf<MemoryRegion>()) == 0)
                {
                    regionEnd = address + PageSize;
                    (partKey, current) = ("\0data", new Part("Working data", null, 0, MemoryFileKind.Data));
                }
                else
                {
                    regionEnd = (ulong)region.BaseAddress + (ulong)region.RegionSize;
                    if (regionEnd <= address) regionEnd = address + PageSize;
                    // Freed since the working set was read, or private: the process's own.
                    (partKey, current) = region.Type == MemPrivate || region.State != MemCommit
                        ? ("\0data", new Part("Working data", null, 0, MemoryFileKind.Data))
                        : Named(handle, region.AllocationBase, region.Type == MemImage, names, devices);
                }
            }
            var shared = (entry >> 8 & 1) != 0;
            var sharers = (int)(entry >> 5 & 7);
            if (shared && sharers >= 7) crowded += PageSize / 7d;
            else pending += shared && sharers > 1 ? PageSize / (double)sharers : PageSize;
        }
        if (pending > 0 || crowded > 0) Add(parts, partKey, current with { Bytes = pending, Crowded = crowded });
        return new Reading(process.WorkingSet, parts, now);
    }

    // What a mapped allocation is: the file behind it, or shared memory when there is none.
    private static (string, Part) Named(SafeProcessHandle handle, nint allocation, bool image, Dictionary<nint, (string, Part)> names, Dictionary<string, string> devices)
    {
        if (names.TryGetValue(allocation, out var known)) return known;
        var buffer = NameBuffer;
        var length = GetMappedFileNameW(handle, allocation, buffer, buffer.Length);
        (string, Part) named;
        if (length <= 0)
            named = image ? ("\0code", new Part("Program code", null, 0, MemoryFileKind.Library))
                : ("\0shared", new Part("Shared memory", null, 0, MemoryFileKind.Shared));
        else
        {
            var path = DosPath(new string(buffer, 0, length), devices);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var kind = extension == ".exe" ? MemoryFileKind.Program
                : image || extension is ".dll" or ".sys" or ".drv" or ".ocx" or ".cpl" or ".mui" ? MemoryFileKind.Library
                : MemoryFileKind.Mapped;
            named = (path, new Part(Path.GetFileName(path) is { Length: > 0 } file ? file : path, path, 0, kind));
        }
        names[allocation] = named;
        return named;
    }

    // \Device\HarddiskVolume3\Windows\System32\ntdll.dll -> C:\Windows\System32\ntdll.dll
    private static string DosPath(string device, Dictionary<string, string> devices)
    {
        const string Network = @"\Device\Mup\";   // a file on a network share: \\server\share\...
        if (device.StartsWith(Network, StringComparison.OrdinalIgnoreCase)) return @"\\" + device[Network.Length..];
        foreach (var (target, letter) in devices)
            if (device.StartsWith(target, StringComparison.OrdinalIgnoreCase) && device.Length > target.Length && device[target.Length] == '\\')
                return letter + device[target.Length..];
        return device;
    }

    private static Dictionary<string, string> DosDevices()
    {
        var devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new char[1024];
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var drive = letter + ":";
            var length = QueryDosDeviceW(drive, buffer, buffer.Length);
            if (length <= 0) continue;
            var target = new string(buffer, 0, Array.IndexOf(buffer, '\0') is var end and >= 0 && end < length ? end : length);
            if (target.Length > 0) devices[target] = drive;
        }
        return devices;
    }

    // ---------------------------------------------------------------- the process list

    private static List<ProcessEntry> Processes()
    {
        var list = new List<ProcessEntry>();
        var size = 1 << 20;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out var needed);
                if (status == InfoLengthMismatch) { size = Math.Max(size * 2, needed + (64 << 10)); continue; }
                if (status < 0) break;
                // SYSTEM_PROCESS_INFORMATION (x64): NextEntryOffset at 0, WorkingSetPrivateSize at 8, CreateTime
                // at 32, ImageName (UNICODE_STRING: length at 56, buffer at 64), UniqueProcessId at 80,
                // WorkingSetSize at 144. The name's buffer points into this same block.
                for (var offset = 0; ;)
                {
                    var entry = buffer + offset;
                    var next = Marshal.ReadInt32(entry, 0);
                    var pid = (int)Marshal.ReadIntPtr(entry, 80);
                    var nameLength = (ushort)Marshal.ReadInt16(entry, 56);
                    var namePointer = Marshal.ReadIntPtr(entry, 64);
                    var name = namePointer != 0 && nameLength > 0 ? Marshal.PtrToStringUni(namePointer, nameLength / 2) : pid == 0 ? "Idle" : "System";
                    list.Add(new ProcessEntry(pid, name, Marshal.ReadInt64(entry, 32), (long)Marshal.ReadIntPtr(entry, 144), Marshal.ReadInt64(entry, 8)));
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
        // Fallback: .NET's own list, which has the working set but not its private part.
        foreach (var process in System.Diagnostics.Process.GetProcesses())
            using (process)
            {
                try { list.Add(new ProcessEntry(process.Id, process.ProcessName + ".exe", 0, process.WorkingSet64, process.WorkingSet64 / 2)); }
                catch (Exception) { }
            }
        return list;
    }

    private static string? ImagePath(int pid)
    {
        using var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle.IsInvalid) return null;
        var buffer = NameBuffer;
        var length = buffer.Length;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref length) && length > 0 ? new string(buffer, 0, length) : null;
    }

    // What people call a program: the description in its .exe ("Google Chrome"), else its file name.
    internal static string ProgramName(string name, string? path)   // CHANGED (round 62): the processor view names programs the same way
    {
        var plain = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (name == "Memory Compression") return "Compressed memory";
        if (path is null) return plain;
        return Descriptions.GetOrAdd(path, file =>
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(file).FileDescription?.Trim();
                return description is { Length: > 1 and <= 60 } ? description : plain;
            }
            catch (Exception) { return plain; }
        });
    }

    // ---------------------------------------------------------------- Windows

    private const int SystemProcessInformation = 5;
    private const int InfoLengthMismatch = unchecked((int)0xC0000004);
    private const int BadLength = 24;
    private const uint QueryInformation = 0x0400, VmRead = 0x0010, QueryLimitedInformation = 0x1000;
    private const uint MemImage = 0x1000000, MemPrivate = 0x20000, MemCommit = 0x1000;
    private const ulong HighestUserAddress = 0x7FFF_FFFF_FFFFUL;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    // MEMORY_BASIC_INFORMATION (x64): 48 bytes.
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryRegion
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, IntPtr buffer, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, int flags, [Out] char[] name, ref int size);

    [DllImport("kernel32.dll", EntryPoint = "K32QueryWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryWorkingSet(SafeProcessHandle process, [Out] ulong[] buffer, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualQueryEx(SafeProcessHandle process, nint address, out MemoryRegion region, nint length);

    [DllImport("kernel32.dll", EntryPoint = "K32GetMappedFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetMappedFileNameW(SafeProcessHandle process, nint address, [Out] char[] name, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int QueryDosDeviceW(string device, [Out] char[] target, int size);
}
