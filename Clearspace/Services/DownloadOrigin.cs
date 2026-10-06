// Clearspace | NEW (downloads): where a downloaded file came from.
// When a browser saves a file, Windows attaches a small note to it (the "Zone.Identifier" stream) saying
// which page you were on and the address the file itself was fetched from. The Source column already
// shows the site from that note (MediaPropertyService); this reads the two addresses in full, for
// "Open the page this came from" and "Copy download link".
// Only http and https addresses are used. Anything else in the note (about:internet, a file path, a
// script address) is treated as not there, because the address is handed to the browser to open.
// A file saved in a private window, or copied from a drive that can't hold the note, has no origin.
// Tests: Clearspace.Tests/DownloadsTests.cs.
using System.IO;

namespace Clearspace.Services;

// Page: the page you were on when you downloaded it. File: the address the file was fetched from.
internal sealed record DownloadOrigin(Uri? Page, Uri? File)
{
    // "github.com": the page's site when known (it says more than a download server's name), else the file's.
    public string Site => Host(Page ?? File!);

    // The address "Open the page this came from" opens: the page, or the front page of the site the file
    // came from when the page is not known. Never the file's own address, which would download it again.
    public Uri PageToOpen => Page ?? new Uri(File!.GetLeftPart(UriPartial.Authority) + "/");

    public bool KnowsPage => Page is not null;

    internal static string Host(Uri address)
        => address.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? address.Host[4..] : address.Host;

    // Null when the file has no note, or the note names no web address.
    internal static DownloadOrigin? Read(string path)
    {
        try
        {
            // The note is a few lines. Anything larger is not one, so only the start is ever read.
            using var stream = new FileStream(path + ":Zone.Identifier", FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[16 * 1024];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    internal static DownloadOrigin? Parse(string note)
    {
        Uri? page = null, file = null;

        foreach (var raw in note.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("ReferrerUrl=", StringComparison.OrdinalIgnoreCase))
                page = WebAddress(line[12..]);
            else if (line.StartsWith("HostUrl=", StringComparison.OrdinalIgnoreCase))
                file = WebAddress(line[8..]);
        }

        return page is null && file is null ? null : new DownloadOrigin(page, file);
    }

    private static Uri? WebAddress(string text)
        => text.Length <= 4096 &&
           Uri.TryCreate(text.Trim(), UriKind.Absolute, out var address) &&
           (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps) &&
           address.Host.Length > 0
            ? address
            : null;
}
