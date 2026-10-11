// Clearspace | NEW (downloads): which files the "Just arrived" strip of a Downloads folder shows.
// Most trips to Downloads are for the file that has just landed, so the newest few are put at the top as
// tiles with the things you do next on them (open, unpack, move, delete).
// "Arrived" is the later of the file's created and modified dates. The list below is ordered by the
// modified date, and for a download that can be years old (the date the publisher last changed the file),
// which is exactly the file that is hard to find in the list - the created date is when it reached this PC.
// Left out: folders, hidden files and downloads the browser has not finished (.crdownload, .part ...).
// CHANGED (always shown): no longer only the last week. The newest files are shown however old they are,
// so the strip is always there in a Downloads folder.
// Tests: Clearspace.Tests/DownloadsTests.cs.
using System.IO;
using Clearspace.Models;

namespace Clearspace.Services;

internal static class JustArrived
{
    internal const int Most = 5;

    private static readonly HashSet<string> UnfinishedEndings = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".download", ".opdownload", ".tmp", ".!ut"
    };

    internal static DateTime ArrivedAt(FileSystemItem item)
        => item.DateCreated > item.DateModified ? item.DateCreated : item.DateModified;

    internal static bool IsUnfinished(string name) => UnfinishedEndings.Contains(Path.GetExtension(name));

    // The newest files first, at most `most` of them.
    internal static IReadOnlyList<FileSystemItem> Pick(IEnumerable<FileSystemItem> items, DateTime now, int most = Most)
        => items
            .Where(item => !item.IsFolder && !item.IsHidden && !IsUnfinished(item.Name) &&
                           !item.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            .Select(item => (Item: item, At: ArrivedAt(item)))
            .OrderByDescending(entry => entry.At)
            .ThenBy(entry => entry.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(most)
            .Select(entry => entry.Item)
            .ToList();

    // "just now", "12 min ago", "3 hours ago", "yesterday", "4 days ago".
    internal static string Ago(DateTime when, DateTime now)
    {
        var passed = now - when;
        if (passed < TimeSpan.FromMinutes(1)) return "just now"; // also a date slightly ahead of this PC's clock
        if (passed < TimeSpan.FromHours(1)) return $"{(int)passed.TotalMinutes} min ago";
        if (passed < TimeSpan.FromHours(2)) return "1 hour ago";
        if (when.Date == now.Date) return $"{(int)passed.TotalHours} hours ago";
        if (when.Date == now.Date.AddDays(-1)) return "yesterday";

        var days = (now.Date - when.Date).Days;
        return $"{days} days ago";
    }
}
