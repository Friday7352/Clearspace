// Clearspace | Storage profiling for search concurrency.

using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Clearspace.Native;

namespace Clearspace.Services;

public enum VolumeKind
{
    Unknown,
    Hdd,
    Ssd,
    Nvme,
    Removable,
    Network
}

public static class VolumeProfiler
{
    private static readonly ConcurrentDictionary<string, VolumeKind> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static int ConcurrencyFor(string path) => Classify(path) switch
    {
        VolumeKind.Nvme => 16,

        VolumeKind.Ssd => 8,

        VolumeKind.Network => 8,

        VolumeKind.Hdd => 2,

        VolumeKind.Removable => 2,

        _ => 4
    };

    public static VolumeKind Classify(string path)
    {
        var root = RootOf(path);

        if (root.Length == 0)
            return VolumeKind.Unknown;

        return Cache.GetOrAdd(root, Detect);
    }

    public static string RootOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : path;
            }

            return Path.GetPathRoot(path) ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static VolumeKind Detect(string root)
    {
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
            return VolumeKind.Network;

        try
        {
            var drive = new DriveInfo(root);

            if (drive.DriveType == DriveType.Network)
                return VolumeKind.Network;

            if (drive.DriveType is DriveType.CDRom or DriveType.Ram)
                return VolumeKind.Removable;
        }
        catch (Exception)
        {
        }

        var letter = root.TrimEnd('\\', '/');
        if (letter.Length != 2 || letter[1] != ':')
            return VolumeKind.Unknown;

        try
        {
            using var handle = NativeMethods.CreateFileW(
                $@"\\.\{letter}",
                NativeMethods.NO_ACCESS,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
                return VolumeKind.Unknown;

            var bus = ReadBusType(handle);

            if (bus == NativeMethods.BusTypeNvme)
                return VolumeKind.Nvme;

            if (bus is NativeMethods.BusTypeUsb or NativeMethods.BusTypeSd or NativeMethods.BusTypeMmc)
                return VolumeKind.Removable;

            return ReadSeekPenalty(handle) switch
            {
                true => VolumeKind.Hdd,
                false => VolumeKind.Ssd,
                _ => VolumeKind.Unknown
            };
        }
        catch (Exception)
        {
            return VolumeKind.Unknown;
        }
    }

    private static int ReadBusType(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var buffer = Query(handle, NativeMethods.StorageDeviceProperty, 1024);

        if (buffer is null || buffer.Length < 32)
            return -1;

        return BitConverter.ToInt32(buffer, 28);
    }

    private static bool? ReadSeekPenalty(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var buffer = Query(handle, NativeMethods.StorageDeviceSeekPenaltyProperty, 16);

        if (buffer is null || buffer.Length < 9)
            return null;

        return buffer[8] != 0;
    }

    private static byte[]? Query(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int propertyId, int size)
    {
        var output = Marshal.AllocHGlobal(size);

        try
        {
            var query = new NativeMethods.STORAGE_PROPERTY_QUERY
            {
                PropertyId = propertyId,
                QueryType = NativeMethods.PropertyStandardQuery
            };

            var ok = NativeMethods.DeviceIoControl(
                handle,
                NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                ref query,
                Marshal.SizeOf<NativeMethods.STORAGE_PROPERTY_QUERY>(),
                output,
                size,
                out var returned,
                IntPtr.Zero);

            if (!ok || returned <= 0)
                return null;

            var buffer = new byte[returned];
            Marshal.Copy(output, buffer, 0, returned);
            return buffer;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(output);
        }
    }

    public static string Describe(VolumeKind kind) => kind switch
    {
        VolumeKind.Nvme => "NVMe",
        VolumeKind.Ssd => "SSD",
        VolumeKind.Hdd => "HDD",
        VolumeKind.Network => "network",
        VolumeKind.Removable => "removable",
        _ => "unknown"
    };
}
