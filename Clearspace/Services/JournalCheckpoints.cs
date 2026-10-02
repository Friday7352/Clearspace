// Clearspace | Saved change-journal positions, one per indexed drive.
//
// NEW (journal catch-up). Stored next to index.db as index.journal.json. A position means "the saved
// index already reflects every change before this USN". It is written only after index.db was saved
// successfully, and never runs ahead of it, so replaying from it can only repeat work, never miss it.

using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Clearspace.Services;

internal sealed record JournalCheckpoint(string Root, uint Serial, ulong JournalId, long Usn, DateTime SavedUtc);

internal static class JournalCheckpoints
{
    internal static string FilePath => Path.Combine(Path.GetDirectoryName(FileIndexStore.FilePath)!, "index.journal.json");

    public static Dictionary<string, JournalCheckpoint> Load(string? path = null)
    {
        var result = new Dictionary<string, JournalCheckpoint>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var file = path ?? FilePath;
            if (!File.Exists(file)) return result;

            foreach (var checkpoint in JsonSerializer.Deserialize<List<JournalCheckpoint>>(File.ReadAllText(file)) ?? [])
            {
                if (!string.IsNullOrEmpty(checkpoint.Root))
                    result[checkpoint.Root] = checkpoint;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or damaged file only means the next start rescans instead of catching up.
            Trace.WriteLine($"Clearspace: could not read journal positions. {exception.Message}");
        }

        return result;
    }

    public static bool Save(IEnumerable<JournalCheckpoint> checkpoints, string? path = null)
    {
        var file = path ?? FilePath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(checkpoints.OrderBy(c => c.Root, StringComparer.OrdinalIgnoreCase).ToList(),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, file, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Clearspace: could not save journal positions. {exception.Message}");
            return false;
        }
    }
}
