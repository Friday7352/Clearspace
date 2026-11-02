// Clearspace | Persistent file-index storage.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Clearspace.Services;

internal static class FileIndexStore
{
    private const int Magic = 0x58495343; // 'CSIX'

    // Version 3 invalidates depth-limited indexes. The binary layout is unchanged;
    // a one-time background rebuild includes descendants beyond the former 32-level limit.
    // CHANGED (round 65, memory): version 4 is the compact layout (28-byte entries, one-byte ASCII names,
    // breadth-first order; see VolumeIndex). A version 3 file is converted on load instead of being thrown
    // away, so updating Clearspace does not mean indexing every drive again.
    private const int FormatVersion = 4;
    private const int LegacyFormatVersion = 3;

    private const int MaxEntries = 40_000_000;
    private const int MaxPool = 1_600_000_000;   // CHANGED (round 65): bytes now (was 800 million UTF-16 characters)

    private static readonly string Directory_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clearspace");

    internal static string FilePath => Path.Combine(Directory_, "index.db");

    // NEW: periodic background saves and the save on exit must not write the file at once.
    private static readonly object SaveGate = new();

    // NEW (round 65): set when Load converted an old save file; the caller saves the new layout and hands
    // the conversion's scratch memory back to Windows.
    internal static bool LoadedLegacyFormat { get; private set; }

    // CHANGED (journal catch-up): reports success, so change-journal positions are only recorded
    // alongside an index file that was actually written.
    public static bool Save(IReadOnlyList<VolumeIndex> volumes)
    {
        lock (SaveGate)
            return SaveCore(volumes);
    }

    private static bool SaveCore(IReadOnlyList<VolumeIndex> volumes)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory_);

            var temporary = FilePath + ".tmp";

            // CHANGED (round 65): a 1 MB write buffer; the volumes write their chunks straight through it.
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(volumes.Count);

                foreach (var live in volumes)
                {
                    // CHANGED (live index): hold the volume's write lock so live updates can't
                    // change it mid-write, and drop entries removed since the last full scan.
                    // CHANGED (round 65): the volume writes itself (as stored, or repacked breadth-first).
                    lock (live.WriteGate)
                        live.WriteLocked(writer);
                }

                writer.Flush();
            }

            File.Move(temporary, FilePath, overwrite: true);
            foreach (var volume in volumes) volume.MarkSaved(); // NEW
            return true;
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Clearspace: could not save the file index. {exception.Message}");
            return false;
        }
    }

    public static List<VolumeIndex> Load()
    {
        var volumes = new List<VolumeIndex>();
        LoadedLegacyFormat = false;

        try
        {
            if (!File.Exists(FilePath))
                return volumes;

            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            if (reader.ReadInt32() != Magic)
                return volumes;

            var version = reader.ReadInt32();

            if (version != FormatVersion && version != LegacyFormatVersion)
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

                var built = new DateTime(builtTicks, DateTimeKind.Utc);

                // CHANGED (round 65): the volume is read (or converted) first and kept only if its drive
                // still matches; skipping it any other way would lose our place in the file.
                var volume = version == FormatVersion
                    ? ReadVolume(stream, root, serial, built, entryCount, poolLength)
                    : ReadLegacyVolume(stream, root, serial, built, entryCount, poolLength);

                if (version == LegacyFormatVersion)
                    LoadedLegacyFormat = true;

                if (volume is null)
                    continue;

                // CHANGED (round 50): a network drive's index is kept while network indexing is on, even
                // when the share is out of reach right now (its sizes are still worth seeing, and indexing
                // a NAS again is slow); it is dropped when network indexing is off. Asking an unreachable
                // server for its serial number could also take a long time, so it is not asked.
                if (NetworkDrives.IsNetworkRoot(root))
                {
                    if (!NetworkDrives.Enabled) continue;
                }
                else if (FileIndexBuilder.GetSerialNumber(root) != serial)
                    continue;

                volumes.Add(volume);
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Clearspace: could not load the file index. {exception.Message}");
            return [];
        }

        return volumes;
    }

    // NEW (round 65): format 4. Entries and names are read straight into the chunks the index uses, so
    // loading allocates exactly what the index keeps. A damaged volume is skipped (and rebuilt later).
    private static VolumeIndex? ReadVolume(Stream stream, string root, uint serial, DateTime built, int entryCount, int poolLength)
    {
        var entryChunks = new IndexEntry[(entryCount + VolumeIndex.EntryMask) >> VolumeIndex.EntryShift][];

        for (var start = 0; start < entryCount; start += VolumeIndex.EntryChunk)
        {
            var chunk = new IndexEntry[Math.Min(VolumeIndex.EntryChunk, entryCount - start)];
            stream.ReadExactly(MemoryMarshal.AsBytes(chunk.AsSpan()));
            entryChunks[start >> VolumeIndex.EntryShift] = chunk;
        }

        var nameChunks = poolLength == 0
            ? new[] { Array.Empty<byte>() }
            : new byte[(poolLength + VolumeIndex.NameMask) >> VolumeIndex.NameShift][];

        for (var start = 0; start < poolLength; start += VolumeIndex.NameChunk)
        {
            var chunk = new byte[Math.Min(VolumeIndex.NameChunk, poolLength - start)];
            stream.ReadExactly(chunk);
            nameChunks[start >> VolumeIndex.NameShift] = chunk;
        }

        if (!VolumeIndex.Validate(entryChunks, entryCount, nameChunks, poolLength))
        {
            Trace.WriteLine($"Clearspace: the saved index for {root} is damaged; it will be rebuilt.");
            return null;
        }

        return new VolumeIndex(root, serial, built, entryChunks, entryCount, nameChunks, poolLength);
    }

    // NEW (round 65): the version 3 entry, exactly as it was written (40 bytes).
    [StructLayout(LayoutKind.Sequential)]
    private struct LegacyEntry
    {
        public long Size;
        public long ModifiedTicks;
        public long CreatedTicks;
        public int NameOffset;
        public int ParentIndex;
        public FileAttributes Attributes;
        public ushort NameLength;
    }

    // NEW (round 65): a version 3 volume (40-byte entries, UTF-16 names, walk order) converted to the
    // compact layout in breadth-first order. This needs the old copy in memory for a moment, once.
    private static VolumeIndex? ReadLegacyVolume(Stream stream, string root, uint serial, DateTime built, int entryCount, int poolLength)
    {
        var legacy = new LegacyEntry[Math.Max(1, entryCount)];
        var names = new char[Math.Max(1, poolLength)];
        stream.ReadExactly(MemoryMarshal.AsBytes(legacy.AsSpan(0, entryCount)));
        stream.ReadExactly(MemoryMarshal.AsBytes(names.AsSpan(0, poolLength)));

        if (entryCount == 0 || legacy[0].ParentIndex != -1)
            return null;

        // Children of each entry, in their original order (counting sort by parent).
        var childStart = new int[entryCount + 1];

        for (var i = 1; i < entryCount; i++)
        {
            var parent = legacy[i].ParentIndex;
            if (parent < 0 || parent >= i || legacy[i].NameOffset < 0 || legacy[i].NameOffset + legacy[i].NameLength > poolLength ||
                legacy[i].NameLength > VolumeIndex.MaxNameChars)
                return null;
            childStart[parent + 1]++;
        }

        for (var i = 0; i < entryCount; i++)
            childStart[i + 1] += childStart[i];

        var children = new int[Math.Max(1, entryCount - 1)];
        var fill = (int[])childStart.Clone();

        for (var i = 1; i < entryCount; i++)
            children[fill[legacy[i].ParentIndex]++] = i;

        var index = new VolumeIndex(root, serial, built);

        LegacyEntry first = legacy[0];
        if (first.NameOffset < 0 || first.NameOffset + first.NameLength > poolLength || first.NameLength > VolumeIndex.MaxNameChars)
            return null;
        index.Add(-1, names.AsSpan(first.NameOffset, first.NameLength), first.Size, first.ModifiedTicks, first.CreatedTicks, first.Attributes);

        // Breadth-first: folders are expanded in the order they were added.
        var folders = new List<(int Old, int New)> { (0, 0) };

        for (var q = 0; q < folders.Count; q++)
        {
            var (old, added) = folders[q];

            for (var c = childStart[old]; c < childStart[old + 1]; c++)
            {
                var child = children[c];
                var entry = legacy[child];
                var position = index.Add(added, names.AsSpan(entry.NameOffset, entry.NameLength),
                    entry.Size, entry.ModifiedTicks, entry.CreatedTicks, entry.Attributes);

                if ((entry.Attributes & FileAttributes.Directory) != 0)
                    folders.Add((child, position));
            }
        }

        index.Compact();
        index.MarkChanged();   // saved in the new layout at the next save
        return index;
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
