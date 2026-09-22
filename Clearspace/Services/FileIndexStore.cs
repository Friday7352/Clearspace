// Clearspace | Persistent file-index storage.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Clearspace.Services;

internal static class FileIndexStore
{
    private const int Magic = 0x58495343; // 'CSIX'

    // CHANGED: 2 = cloud-sync (OneDrive) folders are now scanned. The layout is unchanged; the
    // bump just discards indexes built without them so they are rebuilt on the next launch.
    private const int FormatVersion = 2;

    private const int MaxEntries = 40_000_000;
    private const int MaxPool = 800_000_000;

    private static readonly string Directory_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clearspace");

    internal static string FilePath => Path.Combine(Directory_, "index.db");

    // NEW: periodic background saves and the save on exit must not write the file at once.
    private static readonly object SaveGate = new();

    public static void Save(IReadOnlyList<VolumeIndex> volumes)
    {
        lock (SaveGate)
            SaveCore(volumes);
    }

    private static void SaveCore(IReadOnlyList<VolumeIndex> volumes)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory_);

            var temporary = FilePath + ".tmp";

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(volumes.Count);

                foreach (var live in volumes)
                {
                    // CHANGED (live index): hold the volume's write lock so live updates can't
                    // change it mid-write, and drop entries removed since the last full scan.
                    lock (live.WriteGate)
                    {
                        var volume = live.RemovedCount > 0 ? live.CompactedCopyLocked() : live;
                        writer.Write(volume.Root);
                        writer.Write(volume.SerialNumber);
                        writer.Write(volume.BuiltUtc.Ticks);
                        writer.Write(volume.Count);
                        writer.Write(volume.PoolLength);
                        writer.Flush();

                        stream.Write(MemoryMarshal.AsBytes(volume.Entries.AsSpan(0, volume.Count)));
                        stream.Write(MemoryMarshal.AsBytes(volume.Names.AsSpan(0, volume.PoolLength)));
                    }
                }
            }

            File.Move(temporary, FilePath, overwrite: true);
            foreach (var volume in volumes) volume.MarkSaved(); // NEW
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Clearspace: could not save the file index. {exception.Message}");
        }
    }

    public static List<VolumeIndex> Load()
    {
        var volumes = new List<VolumeIndex>();

        try
        {
            if (!File.Exists(FilePath))
                return volumes;

            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            if (reader.ReadInt32() != Magic || reader.ReadInt32() != FormatVersion)
                return volumes;

            var count = reader.ReadInt32();

            if (count is < 0 or > 64)
                return volumes;

            for (var i = 0; i < count; i++)
            {
                var root = reader.ReadString();
                var serial = reader.ReadUInt32();
                var builtTicks = reader.ReadInt64();
                var entryCount = reader.ReadInt32();
                var poolLength = reader.ReadInt32();

                if (entryCount is < 0 or > MaxEntries || poolLength is < 0 or > MaxPool)
                    return volumes;

                var entries = new IndexEntry[Math.Max(1, entryCount)];
                var names = new char[Math.Max(1, poolLength)];

                stream.ReadExactly(MemoryMarshal.AsBytes(entries.AsSpan(0, entryCount)));
                stream.ReadExactly(MemoryMarshal.AsBytes(names.AsSpan(0, poolLength)));

                if (FileIndexBuilder.GetSerialNumber(root) != serial)
                    continue;

                volumes.Add(new VolumeIndex(
                    root,
                    serial,
                    new DateTime(builtTicks, DateTimeKind.Utc),
                    entries,
                    entryCount,
                    names,
                    poolLength));
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Clearspace: could not load the file index. {exception.Message}");
            return [];
        }

        return volumes;
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception)
        {
        }
    }
}
