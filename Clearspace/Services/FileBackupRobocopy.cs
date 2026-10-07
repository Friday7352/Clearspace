// Clearspace | NEW (file backup): copying with robocopy, the copy tool that is part of Windows.
// Clone and Add and update hand the copying itself to robocopy.exe (Windows\System32), which is what
// Windows offers for bringing a large folder or a drive up to date quickly: it copies several files at
// a time, skips everything that is already the same, carries on where an interrupted run stopped, and is
// not held up by very long paths.
//
// What Clearspace asks of it (Arguments):
//     /E                  every folder, empty ones included
//     /COPY:DAT /DCOPY:DAT   contents, attributes and dates of files and folders (not permissions: the
//                         backup is often on a drive, or for a person, they would mean nothing to)
//     /MT:8               eight files at a time
//     /R:1 /W:1           a file that cannot be read is tried once more after a second, then passed over
//     /XJ                 links to other folders are not followed
//     /FFT                dates within two seconds count as the same (FAT and exFAT drives)
//     /XA:O               cloud files that are not on this PC are left out, unless the plan includes them
//     /XD … /XF …         the names the plan leaves out
//     /NP /NDL /NC /NS /FP /UNILOG:   a log with one line per copied file: its full path
// It is never asked to remove anything (/PURGE, /MIR): what is gone from the original is removed by
// FileBackupEngine, whose rules are Clearspace's own and are tested.
//
// While robocopy runs, its log is read as it grows (it is the only output that keeps file names in every
// language right), which gives the page a count and the file being copied. Lines are told apart without
// reading any words, because robocopy writes its words in Windows' language: a line that ends in a path
// under the original is a copied file; a line with a Windows error number "(0x00000020)" is a problem.
// The exit code says how it went: 1 = files were copied, 8 = some could not be, 16 = it could not work at all.
//
// Arguments, Classify and Explain are tested (Clearspace.Tests/FileBackupTests.cs). Running robocopy itself
// is checked by hand.
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Clearspace.Services;

internal enum RobocopyLine { Other, Copied, Error }

internal static class FileBackupRobocopy
{
    internal const int Threads = 8;

    private static readonly Regex ErrorCode = new(@"\(0x[0-9A-Fa-f]{8}\)", RegexOptions.CultureInvariant);

    /// <summary>robocopy.exe, or null where there is none (then Clearspace copies by itself).</summary>
    internal static string? Find()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "robocopy.exe");
            return File.Exists(path) ? path : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>What robocopy is started with to copy a plan's original into <paramref name="target"/>.</summary>
    internal static List<string> Arguments(FileBackupPlan plan, string target, FileBackupFilter filter, string? logPath)
    {
        var source = FileBackupPaths.Normalize(plan.Source);
        var arguments = new List<string>();

        if (plan.SourceIsFile)
        {
            // One file: its folder, the target, and the file's name as the only thing to copy.
            arguments.Add(Path.GetDirectoryName(source) ?? source);
            arguments.Add(target);
            arguments.Add(Path.GetFileName(source));
            arguments.Add("/COPY:DAT");
        }
        else
        {
            arguments.Add(source);
            arguments.Add(target);
            arguments.Add("/E");
            arguments.Add("/COPY:DAT");
            arguments.Add("/DCOPY:DAT");
            arguments.Add("/MT:" + Threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
            arguments.Add("/XJ");
        }

        arguments.AddRange(["/R:1", "/W:1", "/FFT", "/NP", "/NDL", "/NC", "/NS", "/FP"]);
        if (logPath is not null)
            arguments.Add("/UNILOG:" + logPath);
        else
            arguments.AddRange(["/NFL", "/NJH", "/NJS"]);

        if (!filter.IncludeCloudOnly)
            arguments.Add("/XA:O");

        if (!plan.SourceIsFile && filter.Patterns.Count > 0)
        {
            arguments.Add("/XD");
            arguments.AddRange(filter.Patterns);
            arguments.Add("/XF");
            arguments.AddRange(filter.Patterns);
        }

        return arguments;
    }

    /// <summary>What an exit code means: it could not work at all; some files could not be copied; files were copied.</summary>
    internal static (bool Failed, bool SomeNotCopied, bool Copied) Explain(int exitCode)
    {
        if (exitCode is < 0 or > 31)
            return (true, false, false); // not one of robocopy's own: it was closed, or never got going
        return ((exitCode & 16) != 0, (exitCode & 8) != 0, (exitCode & 1) != 0);
    }

    /// <summary>
    /// What a line of robocopy's log says, without reading its words. A copied file's line ends in the
    /// file's full path (after a tab, when robocopy puts anything in front of it).
    /// </summary>
    internal static RobocopyLine Classify(string line, FileBackupPlan plan, out string path)
    {
        path = string.Empty;
        var text = line.Trim().Trim('\uFEFF');
        if (text.Length == 0)
            return RobocopyLine.Other;

        if (ErrorCode.Match(text) is { Success: true } code)
        {
            path = text[(code.Index + code.Length)..].Trim(); // "Copying File D:\Photos\a.jpg", in Windows' language
            return RobocopyLine.Error;
        }

        var last = text[(text.LastIndexOf('\t') + 1)..].Trim();
        var source = FileBackupPaths.Normalize(plan.Source);
        if (plan.SourceIsFile)
        {
            if (!string.Equals(last, source, StringComparison.OrdinalIgnoreCase))
                return RobocopyLine.Other;
        }
        else
        {
            var prefix = source.EndsWith('\\') || source.EndsWith('/') ? source : source + Path.DirectorySeparatorChar;
            if (last.Length <= prefix.Length || !last.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return RobocopyLine.Other;
        }

        path = last;
        return RobocopyLine.Copied;
    }

    /// <summary>
    /// Runs robocopy for a plan and waits for it. Adds what it copied to <paramref name="result"/> and
    /// what it could not copy to <paramref name="log"/>. Throws IOException when it could not work at all,
    /// and OperationCanceledException (having closed robocopy) when the run is stopped.
    /// </summary>
    internal static void Run(string robocopy, FileBackupPlan plan, string target, FileBackupFilter filter, string? logPath,
        FileBackupResult result, FileBackupLog log, CancellationToken token, Action<long, long, string>? report)
    {
        var info = new ProcessStartInfo { FileName = robocopy, UseShellExecute = false, CreateNoWindow = true };
        var arguments = Arguments(plan, target, filter, logPath);
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        log.Line("Copying with robocopy:  " + string.Join(" ", arguments.Select(argument => argument.Contains(' ') ? "\"" + argument + "\"" : argument)));
        if (logPath is not null)
        {
            log.Line("Robocopy's own log:     " + logPath);
            FileBackupStore.TryDelete(logPath);
        }

        using var process = Process.Start(info) ?? throw new IOException("Windows' copy tool (robocopy) could not be started.");
        var tail = logPath is null ? null : new LogTail(logPath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? unexplained = null; // an error line whose reason (the next line) has not been read yet
        string? lastReason = null;

        void Read()
        {
            if (tail is null)
                return;

            foreach (var line in tail.NewLines())
            {
                if (unexplained is not null && line.Trim().Length > 0)
                {
                    lastReason = line.Trim();
                    if (seen.Add(unexplained)) // the second try of a file writes the same line again
                        log.Problem(unexplained, lastReason);
                    unexplained = null;
                    continue;
                }

                switch (Classify(line, plan, out var path))
                {
                    case RobocopyLine.Error:
                        unexplained = path.Length > 0 ? path : line.Trim();
                        break;
                    case RobocopyLine.Copied:
                        result.Copied++;
                        try { result.CopiedBytes += new FileInfo(path).Length; }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
                        report?.Invoke(result.Copied, result.CopiedBytes, path);
                        break;
                }
            }
        }

        try
        {
            while (!process.WaitForExit(400))
            {
                if (token.IsCancellationRequested)
                {
                    Close(process);
                    token.ThrowIfCancellationRequested();
                }

                Read();
            }

            process.WaitForExit();
            Read();
        }
        catch
        {
            Close(process);
            throw;
        }

        if (unexplained is not null && seen.Add(unexplained))
            log.Problem(unexplained, "could not be copied");

        var (failed, someNotCopied, copied) = Explain(process.ExitCode);
        log.Line($"robocopy ended with {process.ExitCode}.");
        if (failed)
        {
            throw new IOException(
                "Windows' copy tool (robocopy) could not do the copy" +
                (lastReason is null ? "." : ": " + lastReason) +
                (logPath is null ? string.Empty : " Its log has the details."));
        }

        if (someNotCopied && log.Problems == 0)
            log.Problem(plan.Source, "some files could not be copied; robocopy's own log lists them");
        if (copied && result.Copied == 0)
            result.CopiedSome = true; // its log could not be read as expected; it did copy
    }

    private static void Close(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    /// <summary>Reads the lines added to a growing UTF-16 text file since the last time it was asked.</summary>
    internal sealed class LogTail(string path)
    {
        private readonly Decoder _decoder = Encoding.Unicode.GetDecoder();
        private readonly StringBuilder _unfinished = new();
        private readonly byte[] _bytes = new byte[1 << 16];
        private readonly char[] _letters = new char[(1 << 16) + 2];
        private long _position;

        internal List<string> NewLines()
        {
            var lines = new List<string>();
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < _position)
                    _position = 0; // the file was started again
                stream.Seek(_position, SeekOrigin.Begin);

                int read;
                while ((read = stream.Read(_bytes, 0, _bytes.Length)) > 0)
                {
                    _position += read;
                    var count = _decoder.GetChars(_bytes, 0, read, _letters, 0, flush: false);
                    for (var index = 0; index < count; index++)
                    {
                        var letter = _letters[index];
                        if (letter == '\n')
                        {
                            lines.Add(_unfinished.ToString().TrimEnd('\r'));
                            _unfinished.Clear();
                        }
                        else
                        {
                            _unfinished.Append(letter);
                        }
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // not there yet, or busy this moment: read at the next look
            }

            return lines;
        }
    }
}
