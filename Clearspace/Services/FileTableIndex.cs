// Clearspace | Turning a file-table scan into a VolumeIndex, and deciding when to trust it.
//
// NEW (instant first index). FileTableIndex builds the same VolumeIndex a folder walk builds, in one
// pass over arrays (no per-entry allocation). FileTableTrust remembers, per drive, whether its first
// file-table scan matched a normal folder walk; a drive where they disagreed goes back to walking.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Clearspace.Journal;

namespace Clearspace.Services;

internal static class FileTableIndex
{
    public static long EstimatedBytes(int count, int nameChars)
        => (long)count * Marshal.SizeOf<IndexEntry>() + (long)nameChars * sizeof(char);

    public static VolumeIndex ToVolumeIndex(FileTable table, string root, uint serial)
    {
        var entries = new IndexEntry[table.Count];
        var offset = 0;

        for (var i = 0; i < table.Count; i++)
        {
            entries[i] = new IndexEntry
            {
                Size = table.Size[i],
                ModifiedTicks = table.Modified[i],
                CreatedTicks = table.Created[i],
                NameOffset = offset,
                ParentIndex = table.Parent[i],
                Attributes = (FileAttributes)(table.Attributes[i] & 0x7FFFFFFF),
                NameLength = table.NameLength[i]
            };
            offset += table.NameLength[i];
        }

        var names = table.Names.Length == table.NameChars ? table.Names : table.Names[..table.NameChars];
        return new VolumeIndex(root, serial, DateTime.UtcNow, entries, table.Count, names, table.NameChars);
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
