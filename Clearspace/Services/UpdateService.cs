// Clearspace | NEW (updates): finds, downloads and starts Clearspace updates.
//  - Where updates come from: the newest release on GitHub (github.com/Friday7352/Clearspace/releases). A
//    release counts when its tag is a version number ("v1.3.0") and it has the installer attached under the
//    name ClearspaceSetup.exe - exactly what "Build Installer.cmd" produces.
//  - Check: one small request to GitHub's public API. Nothing about this PC is sent beyond what any web
//    request carries. Clearspace checks shortly after it starts and every 6 hours while it is open, unless
//    "Check for updates automatically" is off (Settings > About Clearspace…); Settings > "Check for
//    updates…" and the button in the About window check on demand.
//  - Download: the installer is saved to %LOCALAPPDATA%\Clearspace\Updates and must match the size and the
//    SHA-256 fingerprint GitHub publishes for that file, or it is deleted and never run.
//  - Install: the installer runs with /SILENT (only its progress window shows) and /relaunch=1 (it starts
//    Clearspace again when done; see installer\Clearspace.iss). Clearspace then closes itself the same way
//    it does for Setup: whatever is unlocked for a visit is locked again first.
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Clearspace.Services;

// CHANGED (go back): + Published, the day the release came out (shown in the list of earlier versions).
internal sealed record UpdateInfo(Version Version, string Notes, string PageUrl, string DownloadUrl, long Size, string? Sha256,
    DateTimeOffset? Published = null)
{
    internal string VersionText => UpdateService.Text(Version);
}

internal static class UpdateService
{
    private const string Repository = "Friday7352/Clearspace";
    private const string SetupName = "ClearspaceSetup.exe";
    internal const string ProjectPage = "https://github.com/" + Repository; // NEW (about): the "GitHub page" link
    internal const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    private const string LatestRelease = "https://api.github.com/repos/" + Repository + "/releases/latest";
    // Only an installer from this repository's own releases is ever downloaded.
    private const string DownloadPrefix = "https://github.com/" + Repository + "/releases/download/";

    internal static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(6);

    private static HttpClient? _http;

    // This Clearspace's version: the number in the VERSION file it was built from.
    internal static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));
    internal static string CurrentText => Text(Current);

    // The newer version found by the last check, or null (none found, or not checked yet).
    internal static UpdateInfo? Latest { get; private set; }

    // What the "Update available" chip shows: Latest, unless the user chose "Skip this version" for it.
    internal static UpdateInfo? Notify =>
        Latest is { } latest && !string.Equals(latest.VersionText, SettingsService.GetSkippedUpdate(), StringComparison.Ordinal) ? latest : null;

    // Raised (on whatever thread the check finished on) when Latest or the skipped version changes.
    internal static event Action? Changed;

    internal static string Text(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private static HttpClient Http
    {
        get
        {
            if (_http is not null) return _http;
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // each request sets its own limit
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Clearspace/" + CurrentText); // GitHub requires one
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return _http = client;
        }
    }

    // ---- Check ----

    // Asks GitHub for the newest release. Returns it when it is newer than this Clearspace, otherwise null.
    // Throws (HttpRequestException, TaskCanceledException, JsonException) when GitHub can't be reached or
    // answers with something unexpected.
    internal static async Task<UpdateInfo?> CheckAsync(CancellationToken token = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(20));

        UpdateInfo? found = null;
        using (var response = await Http.GetAsync(LatestRelease, limit.Token))
        {
            if (response.StatusCode != HttpStatusCode.NotFound) // 404 = nothing has been released yet
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: limit.Token);
                found = Parse(json.RootElement);
            }
        }

        Latest = found is not null && found.Version > Current ? found : null;
        Changed?.Invoke();
        return Latest;
    }

    // Reads one release as GitHub describes it. Null when it is not something Clearspace can install: a draft
    // or pre-release, a tag that is not a version number, or no ClearspaceSetup.exe from this repository.
    internal static UpdateInfo? Parse(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object) return null;
        if (Flag(release, "draft") || Flag(release, "prerelease")) return null;
        if (!TryParseVersion(Text(release, "tag_name"), out var version)) return null;
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (!string.Equals(Text(asset, "name"), SetupName, StringComparison.OrdinalIgnoreCase)) continue;
            var url = Text(asset, "browser_download_url");
            if (url is null || !url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase)) return null;
            var size = asset.TryGetProperty("size", out var value) && value.TryGetInt64(out var bytes) ? bytes : 0;
            var digest = Text(asset, "digest"); // "sha256:<64 hex digits>"
            var sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
            // The release's web page is opened in the browser on request, so it must be a github.com page too.
            var page = Text(release, "html_url");
            if (page is null || !page.StartsWith(ReleasesPage, StringComparison.OrdinalIgnoreCase)) page = ReleasesPage;
            DateTimeOffset? published = DateTimeOffset.TryParse(Text(release, "published_at"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date) ? date : null; // NEW (go back)
            return new UpdateInfo(version, Text(release, "body") ?? string.Empty, page, url, size, sha256, published);
        }
        return null;
    }

    // "v1.3.0", "1.3" and "V2.0.1" are versions; "nightly" is not.
    internal static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var text = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(text, out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    // "Skip this version": the chip stays away until a newer version than this one is released.
    internal static void Skip(UpdateInfo update)
    {
        SettingsService.SetSkippedUpdate(update.VersionText);
        Changed?.Invoke();
    }

    // ---- NEW (go back): earlier versions ----
    // About Clearspace… > "Earlier versions…" lists the releases older than this one and installs the one
    // you pick, the same way an update is installed: its own ClearspaceSetup.exe from GitHub, checked
    // against the published size and fingerprint, run with /SILENT /relaunch=1. Every installer since
    // 1.2.0 replaces a newer Clearspace when asked to ("Replace Clearspace X with the older version Y").
    //
    // What makes going back safe is that the older version can read what this one wrote:
    //  - the tag database, which also holds the lock records (TagDatabase.SchemaVersion). An older
    //    Clearspace REFUSES a newer one: no tags, and locked files that cannot be unlocked. So the list
    //    never goes below OldestToGoBackTo. Whoever raises the schema number must set OldestToGoBackTo to
    //    the release that ships it, and GoBackTagSchema to the new number; UpdateTests fails until then.
    //  - locked files themselves (the CSLOCK01 format) and the saved index (rebuilt by itself when it
    //    does not match) have been the same since 1.2.0.
    //  - settings.json: an older version ignores what it does not know.
    // 1.2.0 is also the first version that can update itself, so nothing older is offered: there would
    // be no way back up from inside the program.

    private const string AllReleases = "https://api.github.com/repos/" + Repository + "/releases?per_page=50";

    /// <summary>The oldest release "Earlier versions" offers. See the note above before changing it.</summary>
    internal static readonly Version OldestToGoBackTo = new(1, 2, 0);

    /// <summary>The tag database schema OldestToGoBackTo was checked against.</summary>
    internal const int GoBackTagSchema = 5;

    /// <summary>The first release with "Open folders with Clearspace". An older one cannot answer when
    /// Windows hands it a folder to open, so the setting is turned off before going back past this.</summary>
    internal static readonly Version FolderOpenerSince = new(1, 4, 0);

    /// <summary>NEW (file backup): the first release with "Backup and restore" for files. An older one does
    /// not know "Clearspace.exe --run-backups" and would open a window each time Task Scheduler starts it,
    /// so the Task Scheduler entry is removed before going back past this. Set this to the number the
    /// release that first ships file backups is given.</summary>
    internal static readonly Version FileBackupsSince = new(1, 6, 0);

    // Asks GitHub for the releases and returns the ones that can be gone back to, newest first.
    // Throws like CheckAsync when GitHub can't be reached.
    internal static async Task<IReadOnlyList<UpdateInfo>> EarlierAsync(CancellationToken token = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(20));

        using var response = await Http.GetAsync(AllReleases, limit.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: limit.Token);
        return Earlier(ParseAll(json.RootElement), Current);
    }

    // Reads GitHub's list of releases, keeping the ones Clearspace can install (see Parse).
    internal static List<UpdateInfo> ParseAll(JsonElement releases)
    {
        var found = new List<UpdateInfo>();
        if (releases.ValueKind != JsonValueKind.Array)
            return found;

        foreach (var release in releases.EnumerateArray())
        {
            if (Parse(release) is { } info)
                found.Add(info);
        }

        return found;
    }

    // The releases older than `current` that it is safe to go back to, newest first, each version once.
    internal static IReadOnlyList<UpdateInfo> Earlier(IEnumerable<UpdateInfo> releases, Version current)
        => releases
            .Where(release => release.Version < current && release.Version >= OldestToGoBackTo)
            .GroupBy(release => release.Version)
            .Select(sameVersion => sameVersion.First())
            .OrderByDescending(release => release.Version)
            .ToList();

    internal static bool TurnsOffFolderOpener(Version target) => target < FolderOpenerSince;

    // NEW (file backup)
    internal static bool RemovesBackupTask(Version target) => target < FileBackupsSince;

    // Called just before an earlier version's installer starts.
    internal static void PrepareToGoBack(UpdateInfo earlier)
    {
        // The earlier version would announce the one being left as soon as it starts. It is marked as
        // skipped (a setting every version since 1.2.0 reads), so only a release newer still is announced.
        // "Check for updates…" always offers it again, skipped or not.
        SettingsService.SetSkippedUpdate(Latest is { } newest && newest.Version > Current ? newest.VersionText : CurrentText);

        // NEW (file backup): scheduled backups stop running while Clearspace is closed (the older version
        // does not know how); the backups and their settings stay for when a newer version is installed again.
        if (RemovesBackupTask(earlier.Version))
        {
            try { FileBackupTask.RemoveForGood(); }
            catch (Exception) { }
        }

        if (!TurnsOffFolderOpener(earlier.Version))
            return;

        try
        {
            if (ShellIntegration.IsFolderOpener())
                ShellIntegration.SetFolderOpener(false, Environment.ProcessPath ?? string.Empty);
        }
        catch (Exception)
        {
            // Windows keeps what it had; the Settings menu of a later version can still turn it off
        }
    }

    // ---- Download ----

    internal static string UpdatesFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clearspace", "Updates");

    // Downloads the installer and checks it against what GitHub published. Returns the file's path.
    // `progress` gets 0..1. Throws InvalidDataException when the file doesn't match (it is deleted).
    internal static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken token)
    {
        if (!update.DownloadUrl.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update isn't from Clearspace's releases.");

        Directory.CreateDirectory(UpdatesFolder);
        foreach (var old in Directory.EnumerateFiles(UpdatesFolder)) // earlier downloads are no use any more
        {
            try { File.Delete(old); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        var target = Path.Combine(UpdatesFolder, $"ClearspaceSetup-{update.VersionText}.exe");
        var partial = target + ".part";
        // A connection that stalls must not leave the dialog waiting for ever.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromMinutes(20));
        token = limit.Token;
        try
        {
            long done = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                var total = update.Size > 0 ? update.Size : response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), token);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
                }
            }

            if (update.Size > 0 && done != update.Size)
                throw new InvalidDataException("The download was cut short. Try again.");
            if (update.Sha256 is { } expected &&
                !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded installer doesn't match the one published on GitHub, so it was deleted.");

            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch
        {
            try { File.Delete(partial); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    // ---- Install ----

    // Starts the downloaded installer. The caller then closes Clearspace (LockAgent.Quit); the installer
    // waits for that, replaces the files and starts Clearspace again.
    internal static void StartInstaller(string setupPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = "/SILENT /relaunch=1",
            UseShellExecute = true
        });
    }

    internal static void OpenReleasePage(UpdateInfo? update)
    {
        try { Process.Start(new ProcessStartInfo { FileName = update?.PageUrl ?? ReleasesPage, UseShellExecute = true })?.Dispose(); }
        catch (Exception) { }
    }
}
