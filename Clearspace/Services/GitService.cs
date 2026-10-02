// Clearspace | Git branch and file status for Code folders.
//
// NEW (folder types, step 2). The branch is read straight from .git/HEAD (no git needed). File status
// comes from `git status --porcelain=v1 -z` in the background when git is installed; without git the
// toolbar still shows the branch and the Git column stays empty.

using System.Diagnostics;
using System.IO;
using System.Text;
using Clearspace.Models;

namespace Clearspace.Services;

public sealed record GitSnapshot(
    string Root,
    string? Branch,
    IReadOnlyDictionary<string, char> Files,
    IReadOnlySet<string> ChangedFolders,
    int ChangedCount,
    bool HasStatus);

public static class GitService
{
    private const int MaxLevelsUp = 40;
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(8);

    // The folder at or above `path` that holds .git (a folder, or a file for worktrees and submodules).
    public static string? FindRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var current = path.TrimEnd('\\', '/');

            for (var level = 0; level <= MaxLevelsUp && !string.IsNullOrEmpty(current); level++)
            {
                var marker = Path.Combine(current, ".git");
                if (Directory.Exists(marker) || File.Exists(marker))
                    return current;

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return null;
    }

    // "main", or "a1b2c3d (detached)"; null when HEAD cannot be read.
    public static string? ReadBranch(string root)
    {
        try
        {
            var gitDir = Path.Combine(root, ".git");

            if (File.Exists(gitDir))
            {
                var pointer = File.ReadAllText(gitDir).Trim();
                if (!pointer.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                    return null;
                gitDir = Path.GetFullPath(Path.Combine(root, pointer["gitdir:".Length..].Trim()));
            }

            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            return ParseHead(head);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string? ParseHead(string head)
    {
        const string prefix = "ref: refs/heads/";

        if (head.StartsWith(prefix, StringComparison.Ordinal))
            return head[prefix.Length..];

        if (head.StartsWith("ref: ", StringComparison.Ordinal))
            return head[5..];

        return head.Length >= 7 ? $"{head[..7]} (detached)" : null;
    }

    // Branch and status for the repository containing `folder`; null when it is not in one.
    public static async Task<GitSnapshot?> ReadAsync(string folder, CancellationToken token)
    {
        var root = await Task.Run(() => FindRoot(folder), token).ConfigureAwait(false);
        if (root is null)
            return null;

        var branch = await Task.Run(() => ReadBranch(root), token).ConfigureAwait(false);
        var output = await RunStatusAsync(root, token).ConfigureAwait(false);

        if (output is null)
            return new GitSnapshot(root, branch, new Dictionary<string, char>(), new HashSet<string>(), 0, HasStatus: false);

        var (files, folders, count) = ParsePorcelain(output);
        return new GitSnapshot(root, branch, files, folders, count, HasStatus: true);
    }

    private static async Task<string?> RunStatusAsync(string root, CancellationToken token)
    {
        Process? process = null;

        try
        {
            var start = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = root
            };

            foreach (var argument in new[] { "-C", root, "status", "--porcelain=v1", "-z", "--untracked-files=normal" })
                start.ArgumentList.Add(argument);

            // Never take the index lock: a status refresh must not get in the way of the person's own git.
            start.Environment["GIT_OPTIONAL_LOCKS"] = "0";

            process = Process.Start(start);
            if (process is null)
                return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(StatusTimeout);

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);

            return process.ExitCode == 0 ? output : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null; // timed out
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // git is not installed
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception) { }
                process.Dispose();
            }
        }
    }

    // Parses `git status --porcelain=v1 -z`. Keys are repository-relative with forward slashes; an
    // untracked folder is reported once as "dir/" and stored as "dir".
    internal static (Dictionary<string, char> Files, HashSet<string> Folders, int Count) ParsePorcelain(string output)
    {
        var files = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        var fields = output.Split('\0');

        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (field.Length < 4 || field[2] != ' ')
                continue;

            var x = field[0];
            var y = field[1];
            var path = field[3..].TrimEnd('/');

            // A rename or copy is followed by its original path as the next field.
            if (x is 'R' or 'C')
                i++;

            if (path.Length == 0)
                continue;

            var code = Classify(x, y);
            files[path] = code;
            if (code != '!')
                count++;

            for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
                folders.Add(path[..slash]);
        }

        return (files, folders, count);
    }

    private static char Classify(char x, char y)
    {
        if (x == '?' && y == '?') return '?';
        if (x == '!' && y == '!') return '!';
        if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D')) return 'U';
        if (x == 'A') return 'A';
        if (x == 'D' || y == 'D') return 'D';
        if (x == 'R' || y == 'R') return 'R';
        return 'M';
    }

    // Sets each item's Git status (items outside the repository are cleared).
    public static void Apply(GitSnapshot? snapshot, IEnumerable<FileSystemItem> items)
    {
        foreach (var item in items)
            item.SetGitStatus(snapshot is null ? '\0' : StatusOf(snapshot, item.FullPath, item.IsFolder));
    }

    internal static char StatusOf(GitSnapshot snapshot, string fullPath, bool isFolder)
    {
        string relative;

        try { relative = Path.GetRelativePath(snapshot.Root, fullPath).Replace('\\', '/'); }
        catch (ArgumentException) { return '\0'; }

        if (relative == "." || relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." || Path.IsPathRooted(relative))
            return '\0';

        if (snapshot.Files.TryGetValue(relative, out var code))
            return code;

        return isFolder && snapshot.ChangedFolders.Contains(relative) ? 'C' : '\0';
    }

    // Toolbar text: "git: main \u00b7 3 changed".
    public static string Describe(GitSnapshot snapshot)
    {
        var branch = snapshot.Branch ?? "git";

        if (!snapshot.HasStatus)
            return $"git: {branch}";

        return snapshot.ChangedCount == 0
            ? $"git: {branch} \u00b7 clean"
            : $"git: {branch} \u00b7 {snapshot.ChangedCount:N0} changed";
    }
}
