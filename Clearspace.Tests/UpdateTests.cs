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

    [TestMethod]
    public void ReleaseNotesLoseTheirMarkdownMarks()
    {
        var tidy = UpdateWindow.Tidy("## What's new\r\n\r\n\r\n- **Faster** search\r\n* Fixes\r\n");
        Assert.AreEqual("What's new\n\n\u2022 Faster search\n\u2022 Fixes", tidy);
    }
}
