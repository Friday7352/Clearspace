// NEW (downloads): what the Downloads folder does beyond listing files.
//  - JustArrived: which files the strip at the top shows, and how their age is worded.
//  - DownloadOrigin: reading the page and the download address from the note Windows attaches to a file.
//  - InstallerShelf: which files are installers, and matching them to the installed programs.
//  - FilingMemory: learning where downloads are moved to, and when a folder is offered.
// None of these touch the disk, the registry or the settings file: they are given their data.
using System.IO;
using Clearspace.Models;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class DownloadsTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 15, 0, 0);

    // ---- JustArrived

    private static FileSystemItem Row(string name, DateTime modified, DateTime? created = null, FileAttributes attributes = FileAttributes.Archive) => new()
    {
        Name = name,
        FullPath = Path.Combine("Downloads", name),
        Size = 10,
        DateModified = modified,
        DateCreated = created ?? modified,
        Attributes = attributes
    };

    [TestMethod]
    public void TheNewestFewFilesAreShownNewestFirst()
    {
        FileSystemItem[] items =
        [
            Row("three days.zip", Now.AddDays(-3)),
            Row("an hour.pdf", Now.AddHours(-1)),
            Row("last month.iso", Now.AddDays(-30)),
            Row("a minute.png", Now.AddMinutes(-1)),
            Row("New folder", Now, attributes: FileAttributes.Directory),
            Row("movie.mkv.crdownload", Now),
            Row("page.part", Now),
            Row("desktop.ini", Now),
            Row("secret.txt", Now, attributes: FileAttributes.Hidden)
        ];

        var shown = JustArrived.Pick(items, Now);

        // CHANGED (always shown): old files count too; the strip shows the newest whatever their age.
        CollectionAssert.AreEqual(new[] { "a minute.png", "an hour.pdf", "three days.zip", "last month.iso" }, shown.Select(item => item.Name).ToArray(),
            "no folders, no unfinished downloads, nothing hidden");

        var many = Enumerable.Range(0, 12).Select(i => Row($"file {i:00}.txt", Now.AddMinutes(-i))).ToList();
        Assert.AreEqual(JustArrived.Most, JustArrived.Pick(many, Now).Count);
        Assert.AreEqual("file 00.txt", JustArrived.Pick(many, Now)[0].Name);
        Assert.AreEqual(1, JustArrived.Pick([Row("old.txt", Now.AddDays(-8))], Now).Count, "CHANGED (always shown): an old file is still shown");
    }

    [TestMethod]
    public void AFileWithAnOldDateStillCountsFromWhenItReachedThisPc()
    {
        // The publisher last changed it in 2019; it was downloaded ten minutes ago.
        var driver = Row("driver.exe", modified: new DateTime(2019, 5, 1), created: Now.AddMinutes(-10));
        var today = Row("notes.txt", Now.AddHours(-2));

        Assert.AreEqual(Now.AddMinutes(-10), JustArrived.ArrivedAt(driver));
        CollectionAssert.AreEqual(new[] { "driver.exe", "notes.txt" }, JustArrived.Pick([today, driver], Now).Select(item => item.Name).ToArray());
    }

    [TestMethod]
    public void AgesAreWordedTheWayYouWouldSayThem()
    {
        Assert.AreEqual("just now", JustArrived.Ago(Now.AddSeconds(-20), Now));
        Assert.AreEqual("just now", JustArrived.Ago(Now.AddMinutes(3), Now), "a date a little ahead of this PC's clock");
        Assert.AreEqual("12 min ago", JustArrived.Ago(Now.AddMinutes(-12), Now));
        Assert.AreEqual("1 hour ago", JustArrived.Ago(Now.AddMinutes(-80), Now));
        Assert.AreEqual("5 hours ago", JustArrived.Ago(Now.AddHours(-5), Now));
        Assert.AreEqual("yesterday", JustArrived.Ago(Now.AddHours(-20), Now), "20 hours back from 3 pm is yesterday evening");
        Assert.AreEqual("yesterday", JustArrived.Ago(Now.Date.AddMinutes(-1), Now));
        Assert.AreEqual("3 days ago", JustArrived.Ago(Now.AddDays(-3), Now));
    }

    // ---- DownloadOrigin

    [TestMethod]
    public void ThePageAndTheDownloadAddressAreReadFromTheNote()
    {
        var origin = DownloadOrigin.Parse(
            "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://www.github.com/Friday7352/Clearspace/releases\r\n" +
            "HostUrl=https://objects.githubusercontent.com/x/Clearspace-Setup.exe?token=abc\r\n");

        Assert.IsNotNull(origin);
        Assert.IsTrue(origin.KnowsPage);
        Assert.AreEqual("https://www.github.com/Friday7352/Clearspace/releases", origin.Page!.AbsoluteUri);
        Assert.AreEqual("https://objects.githubusercontent.com/x/Clearspace-Setup.exe?token=abc", origin.File!.AbsoluteUri);
        Assert.AreEqual("github.com", origin.Site, "the page's site, without www");
        Assert.AreEqual(origin.Page, origin.PageToOpen);
    }

    [TestMethod]
    public void WithoutAPageTheSiteOfTheFileIsOpenedNeverTheFileItself()
    {
        var origin = DownloadOrigin.Parse("[ZoneTransfer]\nZoneId=3\nHostUrl=https://cdn.example.org/files/big.iso\n");

        Assert.IsNotNull(origin);
        Assert.IsFalse(origin.KnowsPage);
        Assert.AreEqual("cdn.example.org", origin.Site);
        Assert.AreEqual("https://cdn.example.org/", origin.PageToOpen.AbsoluteUri, "opening the file's own address would download it again");
    }

    [TestMethod]
    public void OnlyWebAddressesCount()
    {
        Assert.IsNull(DownloadOrigin.Parse("[ZoneTransfer]\r\nZoneId=3\r\n"), "a private window leaves no addresses");
        Assert.IsNull(DownloadOrigin.Parse("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=about:internet\r\n"));
        Assert.IsNull(DownloadOrigin.Parse("ReferrerUrl=file:///C:/Windows/System32/calc.exe\r\nHostUrl=javascript:alert(1)\r\n"));
        Assert.IsNull(DownloadOrigin.Parse("HostUrl=C:\\Windows\\System32\\calc.exe\r\n"));
        Assert.IsNull(DownloadOrigin.Parse("HostUrl=ms-settings:privacy\r\n"));
        Assert.IsNull(DownloadOrigin.Parse(string.Empty));

        // One usable address is enough; the other is dropped.
        var origin = DownloadOrigin.Parse("ReferrerUrl=about:blank\r\nHostUrl=http://example.com/a.zip\r\n");
        Assert.IsNotNull(origin);
        Assert.IsNull(origin.Page);
        Assert.AreEqual("example.com", origin.Site);
    }

    // ---- InstallerShelf

    private static readonly InstalledProgram[] Installed =
    [
        new("Git", "2.45.1"),
        new("GitHub Desktop", "3.4.2"),
        new("Discord", "1.0.9156"),
        new("Microsoft Visual Studio Code (User)", "1.90.2"),
        new("VLC media player", "3.0.20"),
        new("7-Zip 24.08 (x64)", "24.08"),
        new("Microsoft Edge WebView2 Runtime", "126.0.2592.87"),
        new("FL Studio 2025", "25.2.3"),
        new("Malwarebytes version 5.1", "5.1"),
        new("Git LFS", "3.5.1", "GitHub, Inc."),
        new("Google Chrome", "126.0.6478.127", "Google LLC"),
        new("Mozilla Firefox (x64 en-US)", "130.0", "Mozilla"),
        new("Python 3.10.6 (64-bit)", "3.10.6150.0", "Python Software Foundation"),
        new("Python 3.13.1 (64-bit)", "3.13.1150.0", "Python Software Foundation"),
        new("Python Launcher", "3.13.1150.0", "Python Software Foundation"),
        new("Driver Booster 11", "11.0"),
        new("OBS Studio", "31.0.0", "OBS Project"),
        new("PowerToys (Preview) x64", "0.75.1", "Microsoft Corporation")
    ];

    [TestMethod]
    public void InstallersAreToldFromOtherFiles()
    {
        Assert.IsTrue(InstallerShelf.IsInstaller("node-v20.14.0-x64.msi"), "a package is always an installer");
        Assert.IsTrue(InstallerShelf.IsInstaller("Notes.msixbundle"));
        Assert.IsTrue(InstallerShelf.IsInstaller("DiscordSetup.exe"));
        Assert.IsTrue(InstallerShelf.IsInstaller("Firefox Installer.exe"));
        Assert.IsTrue(InstallerShelf.IsInstaller("vlc-3.0.20-win64.exe"), "a version number and the kind of PC it is for");
        Assert.IsTrue(InstallerShelf.IsInstaller("Git-2.45.1-64-bit.exe"));
        Assert.IsTrue(InstallerShelf.IsInstaller("tool.exe", description: "Tool Setup"), "the file says so itself");
        Assert.IsTrue(InstallerShelf.IsInstaller("MBSetup (1).exe"));
        Assert.IsTrue(InstallerShelf.IsInstaller("dxwebsetup.exe"), "run together with the rest of the name");
        Assert.IsTrue(InstallerShelf.IsInstaller("serverinstall_100_12632.exe"));

        Assert.IsFalse(InstallerShelf.IsInstaller("rufus.exe"));
        Assert.IsFalse(InstallerShelf.IsInstaller("setup notes.pdf"), "not a program at all");
        Assert.IsFalse(InstallerShelf.IsInstaller("holiday-setup.zip"));
    }

    [TestMethod]
    public void AnInstallerIsMatchedToAProgramOnlyWhenTheNamesReallyAgree()
    {
        Assert.AreEqual("Git", InstallerShelf.Match("Git-2.45.1-64-bit", Installed)?.Name, "Git, not GitHub Desktop");
        Assert.AreEqual("GitHub Desktop", InstallerShelf.Match("GitHubDesktopSetup-x64", Installed)?.Name);
        Assert.AreEqual("Discord", InstallerShelf.Match("DiscordSetup", Installed)?.Name);
        Assert.AreEqual("Microsoft Visual Studio Code (User)", InstallerShelf.Match("Visual Studio Code", Installed)?.Name,
            "by the product name the file carries");

        Assert.AreEqual("FL Studio 2025", InstallerShelf.Match("flstudio_win64_25.2.5.5319", Installed)?.Name, "the same name, run together");
        Assert.AreEqual("Discord", InstallerShelf.Match("DiscordSetup (4)", Installed)?.Name, "the browser's (4) is not part of the name");

        CollectionAssert.AreEqual(new[] { "vs", "code", "user", "setup", "x" }, InstallerShelf.Words("VSCodeUserSetup-x64-1.90.2", keepAll: true).ToArray());
        CollectionAssert.AreEqual(new[] { "mb", "setup" }, InstallerShelf.Words("MBSetup", keepAll: true).ToArray());
        CollectionAssert.AreEqual(new[] { "win", "zip" }, InstallerShelf.Words("WinZip28").ToArray(), "Win is part of the name here");
        CollectionAssert.AreEqual(new[] { "vlc" }, InstallerShelf.Words("vlc-3.0.20-win64").ToArray(), "and a build detail here");

        // With the publisher's words left out, when the file says it is from that publisher.
        Assert.AreEqual("Google Chrome", InstallerShelf.Match("ChromeSetup", Installed, company: "Google LLC")?.Name);
        Assert.AreEqual("Mozilla Firefox (x64 en-US)", InstallerShelf.Match("Firefox Installer", Installed, company: "Mozilla")?.Name);
        Assert.IsNull(InstallerShelf.Match("ChromeSetup", Installed), "the name alone is not enough for that");
        Assert.IsNull(InstallerShelf.Match("ChromeSetup", Installed, company: "Somebody Else LLC"));
        Assert.IsNull(InstallerShelf.Match("studio_setup", Installed, company: "FooSoft"), "not OBS Studio by OBS Project");
        Assert.AreEqual("PowerToys (Preview) x64", InstallerShelf.Match("PowerToysUserSetup-0.75.1-x64", Installed)?.Name);
        Assert.AreEqual("OBS Studio", InstallerShelf.Match("OBS-Studio-32.0.4-Windows-x64-Installer", Installed)?.Name);

        // Of several installed versions, the highest.
        Assert.AreEqual("Python 3.13.1 (64-bit)", InstallerShelf.Match("python-3.11.9-amd64", Installed)?.Name, "not the Python Launcher");

        // Sharing a word with a program is not being that program.
        Assert.IsNull(InstallerShelf.Match("WinZip28", Installed), "not 7-Zip");
        Assert.IsNull(InstallerShelf.Match("driver_setup", Installed), "not Driver Booster");
        Assert.IsNull(InstallerShelf.Match("VisualStudioSetup", Installed), "Visual Studio is not Visual Studio Code");
        Assert.IsNull(InstallerShelf.Match("studio_setup", Installed), "not OBS Studio");
        Assert.IsNull(InstallerShelf.Match("GoogleDriveSetup", Installed), "not Google Chrome");
        Assert.IsNull(InstallerShelf.Match("MBSetup", Installed), "MB could be anything");
        Assert.IsNull(InstallerShelf.Match("VSCodeUserSetup-x64-1.90.2", Installed), "the file name alone does not say Visual Studio Code");
        Assert.IsNull(InstallerShelf.Match("MicrosoftEdgeSetup", Installed), "Edge is not the WebView2 Runtime");
        Assert.IsNull(InstallerShelf.Match("vlc-3.0.20-win64", Installed), "one word out of three is not enough");
        Assert.IsNull(InstallerShelf.Match("7z2408-x64", Installed));
        Assert.IsNull(InstallerShelf.Match("setup", Installed), "no words left that name a program");
        Assert.IsNull(InstallerShelf.Match("SlackSetup", Installed));
        Assert.IsNull(InstallerShelf.Match("Git", []));
    }

    [TestMethod]
    public void TheShelfListsInstallersAndSaysWhichProgramsAreAlreadyThere()
    {
        var day = new DateTime(2026, 9, 1);
        InstallerCandidate[] files =
        [
            new(Path.Combine("Downloads", "DiscordSetup.exe"), 100, day.AddDays(1), Product: "Discord", Version: "1.0.9156"),
            new(Path.Combine("Downloads", "Git-2.50.0-64-bit.exe"), 200, day.AddDays(2), Product: "Git", Version: "2.50.0"),
            new(Path.Combine("Downloads", "vlc-3.0.20-win64.exe"), 300, day.AddDays(3), Product: "VLC media player", Version: "3.0.20"),
            new(Path.Combine("Downloads", "SlackSetup.exe"), 400, day.AddDays(4), Product: "Slack"),
            new(Path.Combine("Downloads", "node-v20.14.0-x64.msi"), 500, day.AddDays(5)),
            new(Path.Combine("Downloads", "rufus.exe"), 600, day.AddDays(6), Product: "Rufus", Description: "Rufus"),
            new(Path.Combine("Downloads", "holiday.zip"), 700, day.AddDays(7))
        ];

        var shelf = InstallerShelf.Find(files, Installed);

        CollectionAssert.AreEqual(
            new[] { "node-v20.14.0-x64.msi", "SlackSetup.exe", "vlc-3.0.20-win64.exe", "Git-2.50.0-64-bit.exe", "DiscordSetup.exe" },
            shelf.Select(file => file.Name).ToArray(), "installers only, newest first");

        InstallerFile Named(string name) => shelf.Single(file => file.Name == name);

        Assert.AreEqual(InstallerState.Installed, Named("DiscordSetup.exe").State);
        Assert.AreEqual("Looks installed: Discord 1.0.9156", Named("DiscordSetup.exe").Note);

        Assert.AreEqual(InstallerState.NewerThanInstalled, Named("Git-2.50.0-64-bit.exe").State, "an update that has not been run yet");
        Assert.AreEqual("Newer than the installed Git 2.45.1", Named("Git-2.50.0-64-bit.exe").Note);

        Assert.AreEqual(InstallerState.Installed, Named("vlc-3.0.20-win64.exe").State, "matched by the product name in the file");
        Assert.AreEqual(InstallerState.NotFound, Named("SlackSetup.exe").State);
        Assert.AreEqual(InstallerState.NotFound, Named("node-v20.14.0-x64.msi").State);
        Assert.IsNull(Named("SlackSetup.exe").Program);

        // A file that carries no version of its own (an .msi, many setup tools): the one in its name counts.
        InstallerCandidate[] updates =
        [
            new(Path.Combine("Downloads", "OBS-Studio-32.0.4-Windows-x64-Installer.exe"), 100, day, Version: "1.0.0.0"),
            new(Path.Combine("Downloads", "OBS-Studio-30.1.0-Windows-Installer.exe"), 100, day)
        ];
        var obs = InstallerShelf.Find(updates, Installed);
        Assert.AreEqual(InstallerState.NewerThanInstalled, obs.Single(file => file.Name.Contains("32.0.4")).State, "not the setup tool's own 1.0.0.0");
        Assert.AreEqual(InstallerState.Installed, obs.Single(file => file.Name.Contains("30.1.0")).State);
    }

    [TestMethod]
    public void VersionsAreComparedByTheirNumbers()
    {
        Assert.IsTrue(InstallerShelf.IsNewer("2.50.0", "2.45.1"));
        Assert.IsTrue(InstallerShelf.IsNewer("1.10", "1.9"), "ten is more than nine");
        Assert.IsFalse(InstallerShelf.IsNewer("1.0.9156.0", "1.0.9156"), "the same version, written longer");
        Assert.IsFalse(InstallerShelf.IsNewer("2.45.1.windows.1", "2.45.1"));
        Assert.IsFalse(InstallerShelf.IsNewer("3.0", "3.1 (64-bit)"));
        Assert.IsFalse(InstallerShelf.IsNewer(null, "1.0"), "unknown is never newer");
        Assert.IsFalse(InstallerShelf.IsNewer("2.0", null));
        Assert.IsFalse(InstallerShelf.IsNewer("latest", "1.0"));
        Assert.IsFalse(InstallerShelf.IsNewer("\uFF11.\uFF12", "1.0"), "digits from another script are not read as a version");
        Assert.IsTrue(InstallerShelf.IsNewer("25.2.5.5319.7", "25.2.3"), "only the first four numbers count");

        // The version in a file's name.
        Assert.AreEqual("2.53.0", InstallerShelf.NameVersion("Git-2.53.0-64-bit"));
        Assert.AreEqual("25.2.5.5319", InstallerShelf.NameVersion("flstudio_win64_25.2.5.5319"));
        Assert.AreEqual("1.4.1.1032", InstallerShelf.NameVersion("Everything-1.4.1.1032.x86-Setup"));
        Assert.IsNull(InstallerShelf.NameVersion("DiscordSetup (4)"));
        Assert.IsNull(InstallerShelf.NameVersion("jdk-17_windows-x64_bin"));
    }

    // ---- FilingMemory

    private static readonly string Music = Path.GetFullPath(Path.Combine("Home", "Music", "Ai Music"));
    private static readonly string School = Path.GetFullPath(Path.Combine("Home", "Documents", "School"));
    private static readonly string Other = Path.GetFullPath(Path.Combine("Home", "Other"));
    private static readonly string DownloadsFolder = Path.GetFullPath(Path.Combine("Home", "Downloads"));

    private static Dictionary<string, Dictionary<string, int>> Habits() => new(StringComparer.Ordinal);

    private static void Move(Dictionary<string, Dictionary<string, int>> habits, string? site, string ending, string folder, int times = 1)
    {
        for (var i = 0; i < times; i++)
            FilingMemory.Learn(habits, [(site, ending)], folder);
    }

    [TestMethod]
    public void AFolderIsOfferedOnceFilesLikeThisHaveGoneThereBefore()
    {
        var habits = Habits();
        Assert.IsNull(FilingMemory.Suggest(habits, "suno.com", ".mp3", _ => true), "nothing learned yet");

        Move(habits, "suno.com", ".mp3", Music);
        Assert.IsNull(FilingMemory.Suggest(habits, "suno.com", ".mp3", _ => true), "once could be a one-off");

        Move(habits, "suno.com", ".mp3", Music);
        var offer = FilingMemory.Suggest(habits, "suno.com", ".mp3", _ => true);
        Assert.IsNotNull(offer);
        Assert.AreEqual(Music, offer.Folder);
        Assert.AreEqual("Ai Music", offer.FolderName);
        Assert.AreEqual(".mp3 files from suno.com usually go here", offer.Why);

        // Another kind of file from the same site: the site is enough.
        var wav = FilingMemory.Suggest(habits, "SUNO.com", ".WAV", _ => true);
        Assert.AreEqual(Music, wav?.Folder);
        Assert.AreEqual("Files from SUNO.com usually go here", wav?.Why);

        // The kind alone (.mp3 from anywhere) needs a third move.
        Assert.IsNull(FilingMemory.Suggest(habits, "elsewhere.org", ".mp3", _ => true));
        Assert.IsNull(FilingMemory.Suggest(habits, null, ".mp3", _ => true));
        Move(habits, "suno.com", ".mp3", Music);
        Assert.AreEqual(".mp3 files usually go here", FilingMemory.Suggest(habits, null, ".mp3", _ => true)?.Why);
    }

    [TestMethod]
    public void NoFolderIsOfferedWithoutAClearFavourite()
    {
        var habits = Habits();
        Move(habits, "canvas.edu", ".pdf", School, times: 2);
        Move(habits, "canvas.edu", ".pdf", Other, times: 2);
        Assert.IsNull(FilingMemory.Suggest(habits, "canvas.edu", ".pdf", _ => true), "half and half");

        Move(habits, "canvas.edu", ".pdf", School, times: 2);
        Assert.AreEqual(School, FilingMemory.Suggest(habits, "canvas.edu", ".pdf", _ => true)?.Folder, "four of six");

        Assert.IsNull(FilingMemory.Suggest(habits, "canvas.edu", ".pdf", folder => folder != School), "the folder has been deleted since");
        Assert.IsNull(FilingMemory.Suggest(habits, "canvas.edu", ".pdf", _ => true, currentFolder: School), "the file is there already");
        Assert.AreEqual(School, FilingMemory.Suggest(habits, "canvas.edu", ".pdf", _ => true, currentFolder: DownloadsFolder)?.Folder);
    }

    [TestMethod]
    public void OneMoveOfManyFilesCountsOnce()
    {
        var habits = Habits();
        var forty = Enumerable.Range(0, 40).Select(_ => ((string?)"canvas.edu", ".pdf")).ToList();

        FilingMemory.Learn(habits, forty, School);
        Assert.IsNull(FilingMemory.Suggest(habits, "canvas.edu", ".pdf", _ => true), "one drag of forty files is one decision");
        Assert.AreEqual(1, habits["site:canvas.edu"][School]);

        // Files with nothing in common each leave their own note.
        FilingMemory.Learn(habits, [("a.com", ".zip"), (null, ".txt"), ("b.com", string.Empty)], Other);
        CollectionAssert.IsSubsetOf(
            new[] { "both:a.com|.zip", "site:a.com", "kind:.zip", "kind:.txt", "site:b.com" },
            habits.Keys.ToArray());
        Assert.IsFalse(habits.ContainsKey("kind:"), "a file without an ending has no kind");
    }

    [TestMethod]
    public void AFolderCanBeForgottenAndTheNotesStaySmall()
    {
        var habits = Habits();
        Move(habits, "suno.com", ".mp3", Music, times: 3);
        Move(habits, "suno.com", ".mp3", Other);

        Assert.IsTrue(FilingMemory.Forget(habits, Music + Path.DirectorySeparatorChar));
        Assert.IsNull(FilingMemory.Suggest(habits, "suno.com", ".mp3", _ => true), "what is left went there once only");
        Assert.IsFalse(habits.Values.Any(folders => folders.ContainsKey(Music)));
        Assert.IsFalse(FilingMemory.Forget(habits, Music), "nothing left to forget");

        Assert.IsTrue(FilingMemory.Forget(habits, Other));
        Assert.AreEqual(0, habits.Count, "a note with no folders left is dropped");

        // Many different destinations for one site: only the most used are kept.
        for (var i = 0; i < 20; i++)
            Move(habits, "busy.com", ".zip", Path.GetFullPath(Path.Combine("Home", $"Folder {i}")), times: i < 2 ? 5 : 1);
        Assert.IsTrue(habits["site:busy.com"].Count <= 6);
        Assert.IsTrue(habits["site:busy.com"].ContainsKey(Path.GetFullPath(Path.Combine("Home", "Folder 0"))));

        // Many different sites: the notes do not grow without limit.
        for (var i = 0; i < 400; i++)
            Move(habits, $"site{i}.com", ".bin", Other);
        Assert.IsTrue(habits.Count <= 300);
    }

    [TestMethod]
    public void OnlyMovesOutOfADownloadsFolderAreWatched()
    {
        // Files that are not there are passed over, so nothing is read from the disk here.
        Assert.IsNull(FilingMemory.Watch([Path.Combine(DownloadsFolder, "missing.pdf")], School, _ => true));
        Assert.IsNull(FilingMemory.Watch([], School, _ => true));
    }
}
