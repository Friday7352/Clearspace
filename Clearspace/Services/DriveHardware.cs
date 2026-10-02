// Clearspace | What kind of device a drive is, for drawing it.
//
// NEW (round 51): the machine view draws each drive as the thing it is - an M.2 NVMe stick, a 2.5" SATA
// SSD, a 3.5" hard drive, a USB enclosure, or a share on a server - with a cover showing its model and
// capacity. Windows says what a volume sits on through the same storage query Device Manager uses: the bus
// (NVMe, SATA, USB...) and whether it has a seek penalty (spinning platters). A query-only handle to the
// volume is enough, so this works without administrator rights. Answers are kept for the session; the
// hardware behind a drive letter does not change while Clearspace runs.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Clearspace.Services;

internal enum DriveKind { Unknown, Hdd, SataSsd, Nvme, Usb, Network }

/// <summary>What a drive is: its kind, its model as the device reports it, and for a network drive, its
/// share (\\server\share) and server.</summary>
internal readonly record struct DriveHardware(DriveKind Kind, string Model, string? Server = null)
{
    public string KindName => Kind switch
    {
        DriveKind.Nvme => "NVMe M.2 SSD",
        DriveKind.SataSsd => "SATA SSD",
        DriveKind.Hdd => "Hard drive",
        DriveKind.Usb => "USB drive",
        DriveKind.Network => "Network share",
        _ => "Drive",
    };

    /// <summary>The maker, from the model name, for the colour of its label.</summary>
    public string Brand
    {
        get
        {
            var model = (Model ?? "").ToUpperInvariant();
            foreach (var (prefix, brand) in Brands)
                if (model.StartsWith(prefix, StringComparison.Ordinal) || (prefix.Length >= 3 && model.Contains(" " + prefix, StringComparison.Ordinal))) return brand;
            return "";
        }
    }

    // Model prefixes as drives report them (WDC WD40EZRZ, ST4000DM004, CT1000MX500SSD1...).
    private static readonly (string Prefix, string Brand)[] Brands =
    [
        ("SAMSUNG", "Samsung"), ("WDC", "WD"), ("WD", "WD"), ("WESTERN DIGITAL", "WD"), ("ST", "Seagate"), ("SEAGATE", "Seagate"),
        ("CT", "Crucial"), ("CRUCIAL", "Crucial"), ("KINGSTON", "Kingston"), ("SA400", "Kingston"), ("TOSHIBA", "Toshiba"),
        ("SANDISK", "SanDisk"), ("INTEL", "Intel"), ("SSDPE", "Intel"), ("HGST", "HGST"), ("HITACHI", "HGST"), ("MICRON", "Micron"),
        ("SK HYNIX", "SK hynix"), ("HFM", "SK hynix"), ("ADATA", "ADATA"), ("SABRENT", "Sabrent"), ("PNY", "PNY"),
        ("CORSAIR", "Corsair"), ("TEAM", "TeamGroup"), ("PATRIOT", "Patriot"), ("INLAND", "Inland"), ("LEXAR", "Lexar"),
    ];
}

internal static class DriveHardwareProbe
{
    private static readonly ConcurrentDictionary<string, DriveHardware> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What a drive is. Talks to the device (or, for a network drive, only to Windows' own table of
    /// mapped drives), so call it off the UI thread.</summary>
    public static DriveHardware For(string root, bool network)
    {
        if (Known.TryGetValue(root, out var known)) return known;
        DriveHardware found;
        try { found = network ? Share(root) : Local(root); }
        catch (Exception) { found = new DriveHardware(network ? DriveKind.Network : DriveKind.Unknown, root); }
        // A drive that could not be read this time (busy, asleep) is asked again next time.
        if (found.Kind != DriveKind.Unknown && (!network || found.Server is not null)) Known[root] = found;
        return found;
    }

    private static DriveHardware Local(string root)
    {
        if (root.Length < 2 || root[1] != ':') return new DriveHardware(DriveKind.Unknown, root);
        using var volume = CreateFileW(@"\\.\" + root[..2], 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (volume.IsInvalid) return new DriveHardware(DriveKind.Unknown, root);

        // STORAGE_DEVICE_DESCRIPTOR: VendorIdOffset at 12, ProductIdOffset at 16, BusType at 28; strings are
        // zero-terminated ASCII at those offsets into the same buffer.
        var descriptor = new byte[1024];
        var model = "";
        var bus = 0;
        if (Query(volume, 0, descriptor, out var length) && length >= 32)
        {
            bus = BitConverter.ToInt32(descriptor, 28);
            var vendor = Ascii(descriptor, BitConverter.ToInt32(descriptor, 12), length);
            var product = Ascii(descriptor, BitConverter.ToInt32(descriptor, 16), length);
            // "ATA" and "NVMe" are the transport, not the maker; product names usually carry the maker.
            if (vendor is "ATA" or "NVMe" or "NVME" || product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) vendor = "";
            model = string.Join(' ', $"{vendor} {product}".Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        // DEVICE_SEEK_PENALTY_DESCRIPTOR: IncursSeekPenalty at 8 - platters.
        var penalty = new byte[12];
        bool? spins = Query(volume, 7, penalty, out var penaltyLength) && penaltyLength >= 9 ? penalty[8] != 0 : null;

        var kind = bus switch
        {
            0x11 => DriveKind.Nvme,   // BusTypeNvme
            0x07 => DriveKind.Usb,    // BusTypeUsb
            _ when spins == true => DriveKind.Hdd,
            _ when spins == false => DriveKind.SataSsd,
            _ when model.Contains("SSD", StringComparison.OrdinalIgnoreCase) => DriveKind.SataSsd,
            _ => DriveKind.Unknown,
        };
        return new DriveHardware(kind, model.Length > 0 ? model : root);
    }

    // A mapped drive's \\server\share, from Windows' table of connections - never from the server.
    private static DriveHardware Share(string root)
    {
        var name = new StringBuilder(512);
        var length = name.Capacity;
        // 1201: a remembered connection that is not connected right now - its name is still filled in.
        var unc = WNetGetConnectionW(root[..2], name, ref length) is 0 or 1201 && name.Length > 0 ? name.ToString() : root;
        var server = unc.StartsWith(@"\\", StringComparison.Ordinal) ? unc[2..].Split('\\')[0] : null;
        return new DriveHardware(DriveKind.Network, unc, server);
    }

    private static bool Query(SafeFileHandle volume, int property, byte[] output, out int returned)
    {
        var query = new byte[12];
        BitConverter.TryWriteBytes(query.AsSpan(0), property);   // PropertyId; QueryType 0 = standard query
        return DeviceIoControl(volume, 0x002D1400, query, query.Length, output, output.Length, out returned, IntPtr.Zero);
    }

    private static string Ascii(byte[] buffer, int offset, int length)
    {
        if (offset <= 0 || offset >= length) return "";
        var end = Array.IndexOf(buffer, (byte)0, offset, length - offset);
        return Encoding.ASCII.GetString(buffer, offset, (end < 0 ? length : end) - offset).Trim();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize,
        [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnectionW(string localName, StringBuilder remoteName, ref int length);
}
