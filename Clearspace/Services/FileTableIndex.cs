// Clearspace | Turning a file-table scan into a VolumeIndex, and deciding when to trust it.
//
// NEW (instant first index). FileTableIndex builds the same VolumeIndex a folder walk builds, in one
// pass over arrays (no per-entry allocation). FileTableTrust remembers, per drive, whether its first
// file-table scan matched a normal folder walk; a drive where they disagreed goes back to walking.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Clearspace.Journal;

namespace Clearspace.Services;

internal static class FileTableIndex
{
    // CHANGED (round 65): the compact layout - 28-byte entries, about one byte per name character.
    public static long EstimatedBytes(int count, int nameChars)
        => (long)count * VolumeIndex.EntrySize + nameChars;

    // CHANGED (round 65): adds the scan's entries in their order (breadth-first, which is what the index
    // keeps), then trims. Used when Clearspace itself reads the file table (elevated) and by tests.
    public static VolumeIndex ToVolumeIndex(FileTable table, string root, uint serial)
    {
        var index = new VolumeIndex(root, serial);
        var offset = 0;

        for (var i = 0; i < table.Count; i++)
        {
            var length = table.NameLength[i];
            var name = table.Names.AsSpan(offset, length);
            offset += length;

            if (length > VolumeIndex.MaxNameChars)
                throw new InvalidDataException("A name in the file table is too long.");

            index.Add(table.Parent[i], name, table.Size[i], table.Modified[i], table.Created[i],
                (FileAttributes)(table.Attributes[i] & 0x7FFFFFFF));
        }

        index.Compact();
        return index;
    }

    // NEW (round 65, memory): reads the helper's file table (FileTableTransfer's format) straight into a
    // compact index, without a FileTable in between. The table used to be read whole (34 bytes an entry
    // plus every name in UTF-16) and then copied into the index, so for a moment both were in memory.
    public static VolumeIndex ReadIndex(Stream stream, int count, int nameChars, string root, uint serial, CancellationToken token)
    {
        if (count < 1 || nameChars < 0)
            throw new InvalidDataException("The helper sent an empty file table.");

        using var reader = new BinaryReader(new BufferedStream(stream, 1 << 20), Encoding.Unicode, leaveOpen: true);
        var entries = new IndexEntry[(count + VolumeIndex.EntryMask) >> VolumeIndex.EntryShift][];
        long total = 0;

        for (var i = 0; i < count; i++)
        {
            if ((i & 0xFFFF) == 0) token.ThrowIfCancellationRequested();

            var chunk = entries[i >> VolumeIndex.EntryShift] ??=
                new IndexEntry[Math.Min(VolumeIndex.EntryChunk, count - (i & ~VolumeIndex.EntryMask))];
            ref var entry = ref chunk[i & VolumeIndex.EntryMask];

            var parent = reader.ReadInt32();
            var attributes = reader.ReadUInt32();
            entry.Size = reader.ReadInt64();
            entry.ModifiedTicks = reader.ReadInt64();
            entry.CreatedTicks = reader.ReadInt64();
            var length = reader.ReadUInt16();

            if (i == 0 ? parent != -1 : parent < 0 || parent >= i)
                throw new InvalidDataException("The helper sent entries out of order.");

            if (length > VolumeIndex.MaxNameChars)
                throw new InvalidDataException("The helper sent a name that is too long.");

            entry.ParentIndex = parent;
            entry.Attributes = (FileAttributes)(attributes & 0x7FFFFFFF);
            entry.SetName(0, length, false);   // the length in characters until the names arrive
            total += length;
        }

        if (total != nameChars)
            throw new InvalidDataException("The helper's names do not add up.");

        // The names, one at a time, into the index's pool.
        var names = new List<byte[]>();
        var scratch = new byte[VolumeIndex.MaxNameChars * 2];
        var position = 0;

        for (var i = 0; i < count; i++)
        {
            if ((i & 0xFFFF) == 0) token.ThrowIfCancellationRequested();

            ref var entry = ref entries[i >> VolumeIndex.EntryShift][i & VolumeIndex.EntryMask];
            var length = entry.NameBytes;
            var raw = scratch.AsSpan(0, length * 2);

            for (var read = 0; read < raw.Length;)
            {
                var got = reader.Read(raw[read..]);
                if (got <= 0) throw new EndOfStreamException("The helper stopped sending the file table.");
                read += got;
            }

            var chars = MemoryMarshal.Cast<byte, char>(raw);
            var wide = !Ascii.IsValid(chars);
            var bytes = wide ? raw.Length : chars.Length;
            position = VolumeIndex.PlaceName(position, bytes, wide);

            var chunkIndex = position >> VolumeIndex.NameShift;
            while (names.Count <= chunkIndex) names.Add(new byte[VolumeIndex.NameChunk]);
            var target = names[chunkIndex].AsSpan(position & VolumeIndex.NameMask, bytes);

            if (wide) raw.CopyTo(target);
            else Ascii.FromUtf16(chars, target, out _);

            entry.SetName(position, bytes, wide);
            position += bytes;
        }

        // The last chunk, trimmed to what it holds.
        if (position == 0)
        {
            names.Clear();
            names.Add([]);
        }
        else
        {
            var used = ((position - 1) & VolumeIndex.NameMask) + 1;
            var last = names[^1];
            if (last.Length > used) names[^1] = last[..used];
        }

        return new VolumeIndex(root, serial, DateTime.UtcNow, entries, count, [.. names], position);
    }

    // What the one-time comparison looks at.
    public readonly record struct Summary(long Files, long Folders, long Bytes)
    {
        public long Entries => Files + Folders;
        public override string ToString() => $"{Files:N0} files, {Folders:N0} folders, {DiskUsageSnapshot.FormatBytes(Bytes)}";
    }

    public static Summary Summarize(VolumeIndex index)
    {
        long files = 0, folders = 0, bytes = 0;

        for (var i = 1; i < index.Count; i++)
        {
            ref readonly var entry = ref index.Entry(i);
            if ((entry.Attributes & VolumeIndex.RemovedFlag) != 0) continue;
            if (entry.IsFolder) folders++;
            else { files++; bytes += entry.Size; }
        }

        return new Summary(files, folders, bytes);
    }

    // Close enough, allowing for files that changed between the two reads (the walk takes minutes).
    public static bool Agree(Summary table, Summary walk, out string detail)
    {
        var entryGap = Math.Abs(table.Entries - walk.Entries);
        var byteGap = Math.Abs(table.Bytes - walk.Bytes);
        var entriesOk = entryGap <= Math.Max(2_000, walk.Entries / 100);             // 1 %
        var bytesOk = byteGap <= Math.Max(2L << 30, walk.Bytes / 50);                  // 2 % or 2 GiB
        detail = $"file table: {table} · folder walk: {walk}";
        return entriesOk && bytesOk;
    }
}

internal sealed record FileTableState(string Root, uint Serial, bool Verified, bool Disabled, string? Note, DateTime Utc);

internal static class FileTableTrust
{
    private static readonly Lock Gate = new();
    private static Dictionary<string, FileTableState>? _states;

    internal static string FilePath => Path.Combine(Path.GetDirectoryName(FileIndexStore.FilePath)!, "index.filetable.json");

    public static FileTableState? For(string root, uint serial)
    {
        lock (Gate)
        {
            var states = _states ??= Load();
            return states.TryGetValue(root, out var state) && state.Serial == serial ? state : null;
        }
    }

    public static bool IsDisabled(string root, uint serial) => For(root, serial)?.Disabled == true;
    public static bool IsVerified(string root, uint serial) => For(root, serial)?.Verified == true;

    public static void Set(FileTableState state)
    {
        lock (Gate)
        {
            var states = _states ??= Load();
            states[state.Root] = state;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(states.Values.OrderBy(s => s.Root).ToList(),
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"Clearspace: could not save file-table state. {exception.Message}");
            }
        }
    }

    private static Dictionary<string, FileTableState> Load()
    {
        var result = new Dictionary<string, FileTableState>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (File.Exists(FilePath))
                foreach (var state in JsonSerializer.Deserialize<List<FileTableState>>(File.ReadAllText(FilePath)) ?? [])
                    if (!string.IsNullOrEmpty(state.Root)) result[state.Root] = state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.WriteLine($"Clearspace: could not read file-table state. {exception.Message}");
        }

        return result;
    }
}
