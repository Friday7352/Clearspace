// Clearspace | NEW (just arrived trace): a small log of what the "Just arrived" strip of a Downloads
// folder decided each time it looked at the folder, and why. The strip sometimes did not show up and the
// reason could not be told from the outside (the folder still loading, nothing counted as new, a rebuild
// replaced by a newer one, something failing while the tiles were made ...). Each rebuild leaves a few
// lines here so the cause can be read off instead of guessed.
// The log is %APPDATA%\Clearspace\just-arrived-trace.log, on this PC only, kept under 200 KB (the older
// half is dropped when it grows past that). It holds the names and dates of the newest few files in the
// Downloads folder; nothing is sent anywhere.
using System.IO;

namespace Clearspace.Services;

internal static class ArrivalsTrace
{
    private const long MaxBytes = 200 * 1024;
    private static readonly Lock Gate = new();

    internal static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clearspace", "just-arrived-trace.log");

    internal static void Write(string line)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}{Environment.NewLine}";

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                lock (Gate)
                {
                    var path = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                    var file = new FileInfo(path);
                    if (file.Exists && file.Length > MaxBytes)
                    {
                        var lines = File.ReadAllLines(path);
                        File.WriteAllLines(path, lines.Skip(lines.Length / 2));
                    }

                    File.AppendAllText(path, text);
                }
            }
            catch (Exception)
            {
                // a log for finding a fault; it never gets in the way
            }
        });
    }

    // "error.txt  created 14:02:11  modified 2019-03-01 10:00  (left out: hidden)"
    internal static string Describe(Models.FileSystemItem item, DateTime now)
    {
        string? leftOut = item.IsFolder ? "folder"
            : item.IsHidden ? "hidden"
            : JustArrived.IsUnfinished(item.Name) ? "download not finished"
            : null;

        return $"{item.Name}  created {item.DateCreated:yyyy-MM-dd HH:mm:ss}  modified {item.DateModified:yyyy-MM-dd HH:mm:ss}" +
               (leftOut is null ? string.Empty : $"  (left out: {leftOut})");
    }
}
