// Clearspace | The computer's own parts, for drawing it.
//
// NEW (round 54): zoomed all the way out, the disk map shows the computer the drives live in - its
// motherboard, processor, memory, graphics card and power supply - with the drives in their places. What
// is drawn comes from the machine itself, read once in the background: the processor's name from the
// registry, the memory modules and motherboard from the firmware's SMBIOS tables (the same source Task
// Manager and CPU-Z use), and the graphics adapter from its driver's registry entry. Nothing here needs
// administrator rights, and anything that cannot be read is simply drawn without a name.

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Clearspace.Services;

internal sealed record MemoryModule(long Bytes, int Speed, string Slot, string Maker);

internal sealed record PcParts(
    string Processor, int Threads,
    string Board, string BoardMaker,
    long MemoryBytes, int MemorySlots, MemoryModule[] Modules,
    string Graphics, long GraphicsMemory)
{
    public static readonly PcParts Unknown = new("Processor", Environment.ProcessorCount, "Motherboard", "", 0, 4, [], "", 0);
}

internal static class PcHardware
{
    private static PcParts? _parts;
    private static readonly Lock Gate = new();

    /// <summary>The computer's parts. Reads them the first time (a few milliseconds), so call it off the UI thread.</summary>
    public static PcParts Read()
    {
        lock (Gate)
        {
            if (_parts is not null) return _parts;
            try { _parts = ReadAll(); }
            catch (Exception) { _parts = PcParts.Unknown; }
            return _parts;
        }
    }

    private static PcParts ReadAll()
    {
        var processor = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
        var boardMaker = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardManufacturer", null) as string;
        var board = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct", null) as string;
        var (slots, modules) = MemoryFromFirmware();
        long installed = 0;
        try { if (GetPhysicallyInstalledSystemMemory(out var kilobytes)) installed = (long)kilobytes * 1024; } catch (Exception) { }
        if (installed <= 0) installed = modules.Sum(module => module.Bytes);
        var (graphics, graphicsMemory) = GraphicsAdapter();
        return new PcParts(
            Clean(processor) is { Length: > 0 } cpu ? cpu : "Processor", Environment.ProcessorCount,
            Clean(board) is { Length: > 0 } product ? product : "Motherboard", Clean(boardMaker),
            installed, Math.Max(slots, Math.Max(2, modules.Length)), modules,
            graphics, graphicsMemory);
    }

    // SMBIOS type 17 (memory device) structures: one per slot, with the module's size (0 = empty slot),
    // speed and where it sits. The table is read with GetSystemFirmwareTable('RSMB'); its data begins after
    // an 8-byte header, and every structure is a formatted part (type, length, handle...) followed by its
    // strings, ending with two zero bytes.
    private static (int Slots, MemoryModule[] Modules) MemoryFromFirmware()
    {
        const uint Rsmb = 0x52534D42;   // 'RSMB'
        var size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
        if (size <= 8) return (0, []);
        var table = new byte[size];
        if (GetSystemFirmwareTable(Rsmb, 0, table, size) != size) return (0, []);
        var length = (int)Math.Min(size - 8, BitConverter.ToUInt32(table, 4));
        var data = table.AsSpan(8, length);
        var slots = 0;
        var modules = new List<MemoryModule>();
        for (var at = 0; at + 4 <= data.Length;)
        {
            int type = data[at], formatted = data[at + 1];
            if (formatted < 4 || at + formatted > data.Length) break;
            var strings = at + formatted;
            var end = strings;
            while (end + 1 < data.Length && !(data[end] == 0 && data[end + 1] == 0)) end++;
            if (type == 17 && formatted >= 0x15)
            {
                slots++;
                var raw = BitConverter.ToUInt16(data[(at + 0x0C)..]);
                long bytes = raw switch
                {
                    0 or 0xFFFF => 0,
                    0x7FFF when formatted >= 0x20 => (long)(BitConverter.ToUInt32(data[(at + 0x1C)..]) & 0x7FFFFFFF) << 20,
                    _ when (raw & 0x8000) != 0 => (long)(raw & 0x7FFF) << 10,
                    _ => (long)raw << 20,
                };
                if (bytes > 0)
                {
                    var speed = formatted >= 0x17 ? BitConverter.ToUInt16(data[(at + 0x15)..]) : 0;
                    var slot = StringAt(data, strings, data[at + 0x10]);
                    var maker = formatted >= 0x18 ? StringAt(data, strings, data[at + 0x17]) : "";
                    modules.Add(new MemoryModule(bytes, speed, slot, maker));
                }
            }
            if (type == 127) break;   // end of table
            at = end + 2;
        }
        return (slots, [.. modules]);
    }

    private static string StringAt(ReadOnlySpan<byte> data, int start, int index)
    {
        if (index <= 0) return "";
        var at = start;
        for (var i = 1; at < data.Length && data[at] != 0; i++)
        {
            var end = data[at..].IndexOf((byte)0);
            if (end < 0) end = data.Length - at;
            if (i == index) return Clean(Encoding.ASCII.GetString(data.Slice(at, end)));
            at += end + 1;
        }
        return "";
    }

    // The display adapter with the most memory (the graphics card rather than the processor's own graphics).
    private static (string Name, long Memory) GraphicsAdapter()
    {
        const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        var best = ("", 0L);
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (adapters is null) return best;
            foreach (var name in adapters.GetSubKeyNames())
            {
                if (name.Length != 4 || !char.IsAsciiDigit(name[0])) continue;
                using var adapter = adapters.OpenSubKey(name);
                if (adapter?.GetValue("DriverDesc") is not string description || description.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)) continue;
                var memory = adapter.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long value => value,
                    byte[] { Length: >= 8 } value => BitConverter.ToInt64(value),
                    _ => adapter.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int value => (long)(uint)value,
                        byte[] { Length: >= 4 } value => BitConverter.ToUInt32(value),
                        _ => 0L,
                    },
                };
                if (best.Item1.Length == 0 || memory > best.Item2) best = (Clean(description), memory);
            }
        }
        catch (Exception) { }
        return best;
    }

    private static string Clean(string? text) => text is null ? "" : string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint table, byte[]? buffer, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong kilobytes);
}
