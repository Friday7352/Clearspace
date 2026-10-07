// NEW (updates): how a GitHub release is read and compared. No network: the JSON is inline.
using System.Text.Json;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class UpdateTests
{
    private static UpdateInfo? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return UpdateService.Parse(document.RootElement);
    }

    private const string Release = """
        {
          "tag_name": "v1.3.0", "draft": false, "prerelease": false,
          "html_url": "https://github.com/Friday7352/Clearspace/releases/tag/v1.3.0",
          "body": "## What's new\r\n\r\n- **Faster** search\r\n- Fixes",
          "assets": [
            { "name": "notes.txt", "size": 10, "browser_download_url": "https://github.com/Friday7352/Clearspace/releases/download/v1.3.0/notes.txt" },
            { "name": "ClearspaceSetup.exe", "size": 52553580, "digest": "sha256:f7c23471aebe8aeb8c69c7f1cf0cb46bbf42299aaa9782078ce36243df2603a2",
              "browser_download_url": "https://github.com/Friday7352/Clearspace/releases/download/v1.3.0/ClearspaceSetup.exe" }
          ]
        }
        """;

    [TestMethod]
    public void ReleaseWithInstallerIsRead()
    {
        var update = Parse(Release);
        Assert.IsNotNull(update);
        Assert.AreEqual(new Version(1, 3, 0), update.Version);
        Assert.AreEqual("1.3.0", update.VersionText);
        Assert.AreEqual(52553580L, update.Size);
        Assert.AreEqual("f7c23471aebe8aeb8c69c7f1cf0cb46bbf42299aaa9782078ce36243df2603a2", update.Sha256);
        Assert.AreEqual("https://github.com/Friday7352/Clearspace/releases/download/v1.3.0/ClearspaceSetup.exe", update.DownloadUrl);
    }

    [TestMethod]
    public void ReleasesClearspaceCannotInstallAreIgnored()
    {
        Assert.IsNull(Parse(Release.Replace("\"prerelease\": false", "\"prerelease\": true")));
        Assert.IsNull(Parse(Release.Replace("\"tag_name\": \"v1.3.0\"", "\"tag_name\": \"nightly\"")));
        Assert.IsNull(Parse(Release.Replace("\"name\": \"ClearspaceSetup.exe\"", "\"name\": \"Other.exe\"")));
        // An installer hosted anywhere but this repository's releases is never offered.
        Assert.IsNull(Parse(Release.Replace(
            "https://github.com/Friday7352/Clearspace/releases/download/v1.3.0/ClearspaceSetup.exe",
            "https://example.com/ClearspaceSetup.exe")));
    }

    [TestMethod]
    public void TagsAreComparedAsVersions()
    {
        Assert.IsTrue(UpdateService.TryParseVersion("v1.10.0", out var ten));
        Assert.IsTrue(UpdateService.TryParseVersion("1.9", out var nine));
        Assert.IsTrue(ten > nine); // 1.10 is newer than 1.9, which comparing as text would get wrong
        Assert.AreEqual(new Version(1, 9, 0), nine);
        Assert.IsFalse(UpdateService.TryParseVersion("latest", out _));
        Assert.IsFalse(UpdateService.TryParseVersion(null, out _));
    }

    // ---- NEW (go back): the list of earlier versions

    private static UpdateInfo ReleaseOf(string number) => new(System.Version.Parse(number), string.Empty,
        UpdateService.ReleasesPage, "https://github.com/Friday7352/Clearspace/releases/download/v" + number + "/ClearspaceSetup.exe", 1, null);

    private static string[] Numbers(IEnumerable<UpdateInfo> releases) => releases.Select(release => release.VersionText).ToArray();

    [TestMethod]
    public void EarlierVersionsAreTheOlderOnesNewestFirst()
    {
        var all = new[] { ReleaseOf("1.3.0"), ReleaseOf("1.5.0"), ReleaseOf("1.2.0"), ReleaseOf("1.4.0"), ReleaseOf("1.6.0") };

        CollectionAssert.AreEqual(new[] { "1.4.0", "1.3.0", "1.2.0" }, Numbers(UpdateService.Earlier(all, new Version(1, 5, 0))),
            "Not the version that is running, and not a newer one: that is an update.");
    }

    [TestMethod]
    public void VersionsThatCannotUpdateThemselvesAreNotOffered()
    {
        var all = new[] { ReleaseOf("1.0.0"), ReleaseOf("1.1.0"), ReleaseOf("1.2.0"), ReleaseOf("1.3.0") };

        CollectionAssert.AreEqual(new[] { "1.3.0", "1.2.0" }, Numbers(UpdateService.Earlier(all, new Version(1, 5, 0))));
        Assert.AreEqual(0, UpdateService.Earlier(all, UpdateService.OldestToGoBackTo).Count, "Nothing below the oldest one that is safe.");
    }

    [TestMethod]
    public void AVersionReleasedTwiceIsListedOnce()
    {
        var all = new[] { ReleaseOf("1.3.0"), ReleaseOf("1.3.0"), ReleaseOf("1.2.0") };

        CollectionAssert.AreEqual(new[] { "1.3.0", "1.2.0" }, Numbers(UpdateService.Earlier(all, new Version(1, 4, 0))));
    }

    [TestMethod]
    public void TheListOfReleasesIsReadLikeTheNewestOne()
    {
        var older = Release.Replace("v1.3.0", "v1.2.0");
        var draft = Release.Replace("v1.3.0", "v1.4.0").Replace("\"draft\": false", "\"draft\": true");
        var dated = Release.Replace("\"draft\": false", "\"draft\": false, \"published_at\": \"2026-10-02T04:05:00Z\"");
        using var document = JsonDocument.Parse("[" + dated + "," + older + "," + draft + ", 5]");

        var all = UpdateService.ParseAll(document.RootElement);

        CollectionAssert.AreEqual(new[] { "1.3.0", "1.2.0" }, Numbers(all), "A draft is not a release, and neither is the stray number.");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 2, 4, 5, 0, TimeSpan.Zero), all[0].Published);
        Assert.IsNull(all[1].Published, "A release without a date is still a release.");
        StringAssert.EndsWith(all[1].DownloadUrl, "/v1.2.0/ClearspaceSetup.exe");

        using var notAList = JsonDocument.Parse(Release);
        Assert.AreEqual(0, UpdateService.ParseAll(notAList.RootElement).Count);
    }

    [TestMethod]
    public void GoingBackPastOpenFoldersWithClearspaceTurnsItOff()
    {
        Assert.IsTrue(UpdateService.TurnsOffFolderOpener(new Version(1, 3, 0)));
        Assert.IsFalse(UpdateService.TurnsOffFolderOpener(UpdateService.FolderOpenerSince));
        Assert.IsFalse(UpdateService.TurnsOffFolderOpener(new Version(1, 5, 0)));
    }

    // NEW (file backup): an older Clearspace would open a window every time Task Scheduler starts it.
    [TestMethod]
    public void GoingBackPastFileBackupsRemovesTheirTaskSchedulerEntry()
    {
        Assert.IsTrue(UpdateService.RemovesBackupTask(new Version(1, 5, 0)));
        Assert.IsFalse(UpdateService.RemovesBackupTask(UpdateService.FileBackupsSince));
        Assert.IsTrue(UpdateService.FileBackupsSince > UpdateService.OldestToGoBackTo);
    }

    // An older Clearspace refuses a tag database newer than it knows: no tags, and locked files it cannot
    // unlock. If this fails, the schema number went up. Set UpdateService.OldestToGoBackTo to the release
    // that ships the new schema and UpdateService.GoBackTagSchema to the new number, in the same change.
    [TestMethod]
    public void EarlierVersionsNeverReachBehindTheTagDatabase()
        => Assert.AreEqual(TagDatabase.SchemaVersion, UpdateService.GoBackTagSchema,
            "The tag database changed: raise UpdateService.OldestToGoBackTo to the release that introduces it.");

    [TestMethod]
    public void ReleaseNotesLoseTheirMarkdownMarks()
    {
        var tidy = UpdateWindow.Tidy("## What's new\r\n\r\n\r\n- **Faster** search\r\n* Fixes\r\n");
        Assert.AreEqual("What's new\n\n\u2022 Faster search\n\u2022 Fixes", tidy);
    }
}
