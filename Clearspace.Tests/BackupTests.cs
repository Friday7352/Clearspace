// NEW (backup): backing up settings and tags, and putting them back (Services/BackupService.cs,
// TagStore.Export / Restore). The files are written to a temporary folder that is removed again; the tags
// live in a database in memory. Nothing here touches the real settings, tags or backup folder.
using System.IO;
using System.IO.Compression;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class BackupTests
{
    private static readonly DateTime Noon = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    private string _folder = string.Empty;

    [TestInitialize]
    public void MakeFolder()
    {
        _folder = Path.Combine(Path.GetTempPath(), "Clearspace backups " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TestCleanup]
    public void RemoveFolder()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static BackupSnapshot Snapshot(string settings = """{ "Theme": "Dark" }""") => new(
        settings,
        [new TagDefinition("work", "Work", "#5B8DD9"), new TagDefinition("clients", "Clients", "#D3A15F")],
        new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Work\report.pdf"] = ["work", "clients"],
            [@"C:\Work\notes.txt"] = ["work"]
        });

    private BackupFile Write(BackupKind kind, DateTime when, BackupSnapshot? snapshot = null)
        => BackupService.Write(snapshot ?? Snapshot(), _folder, kind, when, "1.5.0");

    // ---- when one is due

    [TestMethod]
    public void NothingIsEverDueWhenAutomaticBackupsAreOff()
    {
        Assert.IsFalse(BackupService.IsDue("Off", null, Noon));
        Assert.IsFalse(BackupService.IsDue("Off", Noon.AddYears(-1), Noon));
        Assert.IsFalse(BackupService.IsDue("something else", null, Noon));
    }

    [TestMethod]
    public void ADailyBackupIsDueOnceADay()
    {
        Assert.IsTrue(BackupService.IsDue("Daily", null, Noon), "Never backed up.");
        Assert.IsFalse(BackupService.IsDue("Daily", Noon.AddHours(-3), Noon));
        Assert.IsTrue(BackupService.IsDue("Daily", Noon.AddHours(-21), Noon), "Yesterday afternoon counts as yesterday.");
        Assert.IsTrue(BackupService.IsDue("Daily", Noon.AddHours(5), Noon), "The clock was set back.");
    }

    [TestMethod]
    public void AWeeklyBackupIsDueOnceAWeek()
    {
        Assert.IsFalse(BackupService.IsDue("Weekly", Noon.AddDays(-3), Noon));
        Assert.IsTrue(BackupService.IsDue("Weekly", Noon.AddDays(-7), Noon));
    }

    // NEW (custom schedule): "Custom" is every so many hours, to the hour.
    [TestMethod]
    public void ACustomBackupIsDueEverySoManyHours()
    {
        Assert.IsTrue(BackupService.IsDue("Custom", null, Noon, customHours: 6), "Never backed up.");
        Assert.IsFalse(BackupService.IsDue("Custom", Noon.AddHours(-5), Noon, customHours: 6));
        Assert.IsTrue(BackupService.IsDue("Custom", Noon.AddHours(-6), Noon, customHours: 6));
        Assert.IsFalse(BackupService.IsDue("Custom", Noon.AddDays(-2), Noon, customHours: 72), "Every three days.");
        Assert.IsTrue(BackupService.IsDue("Custom", Noon.AddDays(-3), Noon, customHours: 72));
        Assert.IsTrue(BackupService.IsDue("Custom", Noon.AddHours(1), Noon, customHours: 6), "The clock was set back.");
    }

    [TestMethod]
    public void ACustomScheduleWithoutANumberIsNeverDue()
    {
        Assert.IsFalse(BackupService.IsDue("Custom", null, Noon));
        Assert.IsFalse(BackupService.IsDue("Custom", Noon.AddYears(-1), Noon, customHours: 0));
    }

    [TestMethod]
    public void TheCustomNumberMakesNoDifferenceToTheOtherSchedules()
    {
        Assert.IsFalse(BackupService.IsDue("Off", null, Noon, customHours: 1));
        Assert.IsFalse(BackupService.IsDue("Daily", Noon.AddHours(-3), Noon, customHours: 1));
        Assert.IsFalse(BackupService.IsDue("Weekly", Noon.AddDays(-3), Noon, customHours: 1));
    }

    // ---- NEW (backup label): what a backup is called in the list

    [TestMethod]
    public void EachKindOfBackupIsNamedWithACapitalFirstLetter()
    {
        Assert.AreEqual("Made by you", BackupService.Label(Write(BackupKind.Manual, Noon).Manifest));
        Assert.AreEqual("Automatic", BackupService.Label(Write(BackupKind.Automatic, Noon.AddMinutes(1)).Manifest));
        Assert.AreEqual("Made before a restore", BackupService.Label(Write(BackupKind.BeforeRestore, Noon.AddMinutes(2)).Manifest));
    }

    [TestMethod]
    public void ALabelTypedForABackupIsKeptInTheFile()
    {
        var written = BackupService.Write(Snapshot(), _folder, BackupKind.Manual, Noon, "1.5.0", "  Before reinstalling Windows ");

        var read = BackupService.Read(written.Path);

        Assert.AreEqual("Before reinstalling Windows", read.Manifest.Note);
        Assert.AreEqual("Before reinstalling Windows", BackupService.Label(read.Manifest));
        Assert.AreEqual("Before reinstalling Windows", BackupService.Label(BackupService.List(_folder).Single().Manifest));
    }

    [TestMethod]
    public void ABackupWithoutALabelHasNoneInTheFile()
    {
        Assert.IsNull(Write(BackupKind.Manual, Noon).Manifest.Note);
        Assert.IsNull(BackupService.Write(Snapshot(), _folder, BackupKind.Manual, Noon.AddMinutes(1), "1.5.0", "   ").Manifest.Note);
    }

    [TestMethod]
    public void ALabelIsOneLineAndNotTooLong()
    {
        Assert.IsNull(BackupService.CleanNote(null));
        Assert.IsNull(BackupService.CleanNote(" \r\n\t "));
        Assert.AreEqual("Before the move", BackupService.CleanNote("  Before the move  "));
        Assert.AreEqual("Before the move to the new PC", BackupService.CleanNote("Before the move\r\nto the new PC"));
        Assert.AreEqual(BackupService.LongestNote, BackupService.CleanNote(new string('a', 300))!.Length);
    }

    [TestMethod]
    public void ABackupMadeBeforeLabelsExistedIsNamedByItsKind()
    {
        // Its manifest has no Note, and a kind this Clearspace does not know reads as one made by hand.
        Assert.AreEqual("Automatic", BackupService.Label(new BackupManifest { Kind = "Automatic" }));
        Assert.AreEqual("Made by you", BackupService.Label(new BackupManifest { Kind = "SomethingNewer" }));
    }

    // ---- the file

    [TestMethod]
    public void ABackupComesBackAsItWentIn()
    {
        var written = Write(BackupKind.Manual, Noon);

        var read = BackupService.Read(written.Path);

        Assert.AreEqual("""{ "Theme": "Dark" }""", read.SettingsJson);
        CollectionAssert.AreEqual(new[] { "work", "clients" }, read.Tags.Select(tag => tag.Id).ToArray());
        Assert.AreEqual("Clients", read.Tags[1].Name);
        Assert.AreEqual("#D3A15F", read.Tags[1].Color);
        CollectionAssert.AreEqual(new[] { "work", "clients" }, read.Assignments[@"c:\work\REPORT.pdf"], "Capitals in a path make no difference.");
        Assert.AreEqual(2, read.Assignments.Count);

        Assert.AreEqual(BackupKind.Manual, read.Manifest.KindValue);
        Assert.AreEqual(Noon, read.Manifest.CreatedUtc);
        Assert.AreEqual("1.5.0", read.Manifest.AppVersion);
        Assert.AreEqual(2, read.Manifest.Tags);
        Assert.AreEqual(2, read.Manifest.TaggedItems);
        Assert.IsTrue(read.Manifest.HasSettings);
    }

    [TestMethod]
    public void ABackupIsAnOrdinaryZipWithThreeTexts()
    {
        var written = Write(BackupKind.Automatic, Noon);

        StringAssert.EndsWith(written.Path, "(automatic).csbackup");
        using var zip = ZipFile.OpenRead(written.Path);
        CollectionAssert.AreEquivalent(new[] { "manifest.json", "settings.json", "tags.json" }, zip.Entries.Select(entry => entry.FullName).ToArray());
        Assert.AreEqual(0, Directory.GetFiles(_folder, "*.part").Length, "Nothing half-written is left behind.");
    }

    [TestMethod]
    public void TheSameSettingsAndTagsAlwaysHaveTheSameFingerprint()
    {
        var first = Write(BackupKind.Manual, Noon);
        var later = Write(BackupKind.Automatic, Noon.AddDays(1));
        var changed = Write(BackupKind.Manual, Noon.AddDays(2), Snapshot("""{ "Theme": "Light" }"""));

        Assert.AreEqual(first.Manifest.ContentHash, later.Manifest.ContentHash, "When and how it was made is not part of it.");
        Assert.AreNotEqual(first.Manifest.ContentHash, changed.Manifest.ContentHash);
    }

    [TestMethod]
    public void BackupsAreListedNewestFirstAndOtherFilesArePassedOver()
    {
        Write(BackupKind.Manual, Noon.AddDays(-2));
        Write(BackupKind.Automatic, Noon);
        Write(BackupKind.BeforeRestore, Noon.AddDays(-1));
        File.WriteAllText(Path.Combine(_folder, "broken.csbackup"), "not a zip at all");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not a backup");

        var listed = BackupService.List(_folder);

        CollectionAssert.AreEqual(new[] { BackupKind.Automatic, BackupKind.BeforeRestore, BackupKind.Manual },
            listed.Select(backup => backup.Manifest.KindValue).ToArray());
        Assert.AreEqual(0, BackupService.List(Path.Combine(_folder, "not there")).Count);
    }

    [TestMethod]
    public void AFileThatIsNotABackupSaysSo()
    {
        var path = Path.Combine(_folder, "holiday.csbackup");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            zip.CreateEntry("photo.jpg");

        var error = Assert.ThrowsException<InvalidDataException>(() => BackupService.Read(path));
        StringAssert.Contains(error.Message, "not a Clearspace backup");
    }

    [TestMethod]
    public void ABackupFromANewerClearspaceIsNotGuessedAt()
    {
        var path = Path.Combine(_folder, "future.csbackup");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write("""{ "Format": 99, "AppVersion": "9.0.0", "CreatedUtc": "2030-01-01T00:00:00Z", "Kind": "Manual" }""");
        }

        var error = Assert.ThrowsException<InvalidDataException>(() => BackupService.Read(path));
        StringAssert.Contains(error.Message, "newer Clearspace");
    }

    // ---- thinning out

    [TestMethod]
    public void OnlyAutomaticBackupsAreThinnedOutAndTheNewestAreKept()
    {
        var mine = Write(BackupKind.Manual, Noon.AddDays(-30));
        var before = Write(BackupKind.BeforeRestore, Noon.AddDays(-20));
        var oldest = Write(BackupKind.Automatic, Noon.AddDays(-3));
        var middle = Write(BackupKind.Automatic, Noon.AddDays(-2));
        var newest = Write(BackupKind.Automatic, Noon.AddDays(-1));

        var going = BackupService.ToPrune([mine, before, oldest, middle, newest], keep: 2);
        CollectionAssert.AreEqual(new[] { oldest.Path }, going.Select(backup => backup.Path).ToArray());

        Assert.AreEqual(1, BackupService.Prune(_folder, keep: 2));
        Assert.IsFalse(File.Exists(oldest.Path));
        Assert.IsTrue(File.Exists(mine.Path) && File.Exists(before.Path) && File.Exists(middle.Path) && File.Exists(newest.Path));

        Assert.AreEqual(1, BackupService.Prune(_folder, keep: 0), "At least one automatic backup always stays.");
        Assert.IsTrue(File.Exists(newest.Path));
    }

    // ---- which items get their tags back

    [TestMethod]
    public void TagsGoBackOnWhatStillExistsOrCannotBeChecked()
    {
        static bool Exists(string path) => path.EndsWith("there.txt", StringComparison.Ordinal);
        static bool Connected(string root) => root.StartsWith("C", StringComparison.OrdinalIgnoreCase);

        Assert.IsTrue(BackupService.ShouldRestore(@"C:\Docs\there.txt", Exists, Connected));
        Assert.IsFalse(BackupService.ShouldRestore(@"C:\Docs\deleted.txt", Exists, Connected), "On a connected drive and gone.");
        Assert.IsTrue(BackupService.ShouldRestore(@"E:\Stick\deleted.txt", Exists, Connected), "The drive is not connected: nobody can tell.");
        Assert.IsTrue(BackupService.ShouldRestore(@"\\nas\media\deleted.txt", Exists, Connected));
        Assert.IsFalse(BackupService.ShouldRestore("relative\\path.txt", Exists, Connected));
        Assert.IsFalse(BackupService.ShouldRestore("  ", Exists, Connected));
    }

    // ---- putting tags back

    private const string Report = @"C:\Work\report.pdf";
    private const string Notes = @"C:\Work\notes.txt";
    private const string Newer = @"C:\New\idea.txt";

    // A store with a tag of its own on two items, what a backup of it holds, and then the losses a
    // restore is for: a tag deleted, an item's tags cleared, and something tagged after the backup.
    private static (TagStore Store, TagDefinition[] Tags, Dictionary<string, List<string>> Assignments, string Clients) Lost()
    {
        var store = new TagStore(":memory:");
        var clients = store.Create("Clients").Id;
        store.Assign(Report, clients);
        store.Assign(Report, "work");
        store.Assign(Notes, "work");
        var (tags, assignments) = store.Export();

        store.Delete(clients);
        store.ClearTags([Notes]);
        store.Assign(Newer, "todo");
        return (store, tags, assignments, clients);
    }

    [TestMethod]
    public void LostTagsArePutBackAndNothingIsTakenAway()
    {
        var (store, tags, assignments, clients) = Lost();
        using var owned = store;
        Assert.IsNull(store.Find(clients));

        var result = store.Restore(tags, assignments, exact: false);

        Assert.AreEqual(new TagRestoreResult(TagsCreated: 1, TagsPutBack: 2, Items: 2), result);
        Assert.AreEqual("Clients", store.Find(clients)?.Name);
        Assert.IsTrue(store.HasTag(Report, clients) && store.HasTag(Report, "work") && store.HasTag(Notes, "work"));
        Assert.IsTrue(store.HasTag(Newer, "todo"), "Tagging done since the backup stays.");

        Assert.AreEqual(new TagRestoreResult(0, 0, 0), store.Restore(tags, assignments, exact: false), "A second time there is nothing left to do.");
    }

    [TestMethod]
    public void ExactlyAsInTheBackupAlsoRemovesWhatCameAfter()
    {
        var (store, tags, assignments, clients) = Lost();
        using var owned = store;
        var later = store.Create("Later").Id;

        store.Restore(tags, assignments, exact: true);

        Assert.IsTrue(store.HasTag(Report, clients) && store.HasTag(Notes, "work"));
        Assert.IsFalse(store.HasTag(Newer, "todo"), "Tagged after the backup.");
        Assert.IsNull(store.Find(later), "A tag made after the backup.");
        Assert.IsNotNull(store.Find("todo"), "A tag the backup had stays, with or without items.");
    }

    [TestMethod]
    public void ATagMadeAgainUnderTheSameNameIsTheSameTag()
    {
        using var store = new TagStore(":memory:");
        TagDefinition[] tags = [new("old-id-for-work", "Work", "#000000")];
        var assignments = new Dictionary<string, List<string>> { [Report] = ["old-id-for-work", "a tag nobody knows"] };

        var result = store.Restore(tags, assignments, exact: false);

        Assert.AreEqual(new TagRestoreResult(TagsCreated: 0, TagsPutBack: 1, Items: 1), result);
        Assert.IsTrue(store.HasTag(Report, "work"));
        Assert.IsNull(store.Find("old-id-for-work"));
    }
}
