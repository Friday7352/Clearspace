// Clearspace | Project strip contents: pinned files and recent work anywhere in a project.
//
// NEW (folder types, step 2). Recent files come from a bounded background walk of the project folder
// that skips build output and dependency folders, cached briefly so moving around inside a project does
// not walk it again.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;

namespace Clearspace.Services;

public sealed record ProjectStripItem(string Name, string FullPath, string Detail, bool IsPinned, bool IsFolder)
{
    public string Glyph => IsPinned ? "\uE718" : "\uE823"; // Pin / Recent
}

// Folders produced by builds and package managers. Code folders dim them; project walks skip them.
internal static class GeneratedFolders
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "dist", "build", "out", "target", "packages", "coverage",
        "__pycache__", "venv", ".venv", ".git", ".vs", ".idea", ".vscode-test", ".next", ".nuxt",
        ".svelte-kit", ".gradle", ".pytest_cache", ".mypy_cache", ".cache", ".parcel-cache", ".turbo"
    };

    public static bool IsGenerated(ReadOnlySpan<char> name)
        => Names.GetAlternateLookup<ReadOnlySpan<char>>().Contains(name);
}

public static class ProjectFiles
{
    private const int MaxEntries = 150_000;
    private static readonly TimeSpan MaxTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, (DateTime Utc, List<(string Path, DateTime Modified)> Files)> Cache
        = new(StringComparer.OrdinalIgnoreCase);

    // Pinned items that still exist, then the most recently changed files (not pinned), newest first.
    public static IReadOnlyList<ProjectStripItem> Build(string root, IReadOnlyList<string> pins, int recentCount, CancellationToken token)
    {
        var items = new List<ProjectStripItem>();

        foreach (var pin in pins)
        {
            var isFolder = Directory.Exists(pin);
            if (!isFolder && !File.Exists(pin))
                continue;

            items.Add(new ProjectStripItem(Path.GetFileName(pin.TrimEnd('\\')), pin, "Pinned \u00b7 " + Where(root, pin), true, isFolder));
        }

        var pinned = new HashSet<string>(pins, StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var (path, modified) in Recent(root, recentCount + pinned.Count, token))
        {
            if (added >= recentCount)
                break;
            if (pinned.Contains(path))
                continue;

            items.Add(new ProjectStripItem(Path.GetFileName(path), path, $"{Ago(modified)} \u00b7 {Where(root, path)}", false, false));
            added++;
        }

        return items;
    }

    internal static List<(string Path, DateTime Modified)> Recent(string root, int take, CancellationToken token)
    {
        if (Cache.TryGetValue(root, out var cached) && DateTime.UtcNow - cached.Utc < CacheFor)
            return [.. cached.Files.Take(take)];

        var newest = new PriorityQueue<(string, DateTime), DateTime>();
        var keep = Math.Max(take, 16);
        var watch = Stopwatch.StartNew();
        var seen = 0;

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
            };

            var walk = new FileSystemEnumerable<(string Path, DateTime Modified)>(root,
                (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.LastWriteTimeUtc.LocalDateTime), options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
                ShouldRecursePredicate = (ref FileSystemEntry entry)
                    => !GeneratedFolders.IsGenerated(entry.FileName) && !(entry.FileName.Length > 0 && entry.FileName[0] == '.')
            };

            foreach (var file in walk)
            {
                if (token.IsCancellationRequested || ++seen > MaxEntries || watch.Elapsed > MaxTime)
                    break;

                newest.Enqueue(file, file.Modified);
                if (newest.Count > keep)
                    newest.Dequeue();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        token.ThrowIfCancellationRequested();

        var files = new List<(string Path, DateTime Modified)>(newest.Count);
        while (newest.Count > 0)
            files.Add(newest.Dequeue());
        files.Reverse();

        if (Cache.Count > 64) Cache.Clear();
        Cache[root] = (DateTime.UtcNow, files);
        return [.. files.Take(take)];
    }

    public static void Invalidate(string root) => Cache.TryRemove(root, out _);

    private static string Where(string root, string path)
    {
        var folder = Path.GetDirectoryName(path) ?? root;
        var relative = Path.GetRelativePath(root, folder);
        return relative == "." ? Path.GetFileName(root.TrimEnd('\\')) : relative;
    }

    internal static string Ago(DateTime modified)
    {
        var age = DateTime.Now - modified;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        if (age < TimeSpan.FromDays(7)) return $"{(int)age.TotalDays} d ago";
        return modified.ToString("d");
    }
}
