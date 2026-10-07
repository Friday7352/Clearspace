// NEW (file backup): backing up files, folders and drives, and getting them back
// (Services/FileBackupPlan.cs, FileBackupEngine.cs, FileBackupRobocopy.cs, FileBackupRestore.cs,
// FileBackupRunner.cs, FileBackupTask.cs).
// Every run here copies between two folders inside a temporary folder that is removed again, with
// Clearspace's own copy (robocopy is not started: what is tested of it is the command it is given and how
// its log and exit code are read). Nothing touches the real backups, their settings or Task Scheduler.
using System.Globalization;
using System.IO;
using System.IO.Compression;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class FileBackupTests
{
    private static readonly DateTime Noon = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Local); // a Tuesday
    private static readonly DateTime Old = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private string _top = string.Empty;
    private string _original = string.Empty;
    private string _backups = string.Empty;

    [TestInitialize]
    public void MakeFolders()
    {
        _top = Path.Combine(Path.GetTempPath(), "Clearspace file backups " + Guid.NewGuid().ToString("N"));
        _original = Path.Combine(_top, "Photos");
        _backups = Path.Combine(_top, "Backups");
        Directory.CreateDirectory(_original);
        Directory.CreateDirectory(_backups);
        FileBackupNative.Override = null;
    }

    [TestCleanup]
    public void RemoveFolders()
    {
        FileBackupNative.Override = null;
        try { FileBackupEngine.DeleteTree(_top); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- helpers

    private FileBackupPlan Plan(FileBackupType type, Action<FileBackupPlan>? change = null)
    {
        var plan = new FileBackupPlan
        {
            Name = "Photos",
            Source = _original,
            Destination = _backups,
            FolderName = "Photos",
            Type = type,
            KeepRemoved = false,
            CreatedUtc = Old
        };
        change?.Invoke(plan);
        return plan;
    }

    private static FileBackupResult Run(FileBackupPlan plan, DateTime? now = null)
        => FileBackupEngine.Run(new FileBackupRun { Plan = plan, NowLocal = now ?? Noon, UseRobocopy = false });

    // Writes a file of the original with a date of its own, so "changed" never depends on how fast the test runs.
    private string Write(string relative, string text, int minutes = 0)
    {
        var path = Path.Combine(_original, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, Old.AddMinutes(minutes));
        return path;
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(parts));

    private static bool There(params string[] parts) => File.Exists(Path.Combine(parts));

    // ------------------------------------------------------------------ when a backup is due

    private static FileBackupSchedule Schedule(string kind, int atMinute = 12 * 60, int days = 0, int everyHours = 6)
        => new() { Kind = kind, AtMinute = atMinute, Days = days, EveryHours = everyHours };

    [TestMethod]
    public void ABackupWithoutAScheduleIsNeverDue()
    {
        Assert.IsFalse(Schedule(FileBackupSchedule.Off).IsDue(null, false, Noon));
        Assert.IsFalse(Schedule(FileBackupSchedule.Off).IsDue(Noon.AddYears(-1), true, Noon));
        Assert.IsFalse(Schedule("something else").IsDue(null, false, Noon));
        Assert.IsNull(Schedule(FileBackupSchedule.Off).NextRun(null, false, Noon));
    }

    [TestMethod]
    public void ABackupThatNeverRanIsDueAtOnce()
    {
        foreach (var kind in new[] { FileBackupSchedule.Hourly, FileBackupSchedule.Daily, FileBackupSchedule.Custom })
            Assert.IsTrue(Schedule(kind).IsDue(null, false, Noon), kind);
        Assert.IsTrue(Schedule(FileBackupSchedule.Weekly, days: 1 << (int)DayOfWeek.Friday).IsDue(null, false, Noon));
    }

    [TestMethod]
    public void HourlyAndCustomGoByTheTimeSinceTheLastRun()
    {
        var hourly = Schedule(FileBackupSchedule.Hourly);
        Assert.IsFalse(hourly.IsDue(Noon.AddMinutes(-59), false, Noon));
        Assert.IsTrue(hourly.IsDue(Noon.AddMinutes(-60), false, Noon));

        var sixHours = Schedule(FileBackupSchedule.Custom, everyHours: 6);
        Assert.IsFalse(sixHours.IsDue(Noon.AddHours(-5), false, Noon));
        Assert.IsTrue(sixHours.IsDue(Noon.AddHours(-6), false, Noon));
        Assert.AreEqual(Noon.AddHours(1), sixHours.NextRun(Noon.AddHours(-5), false, Noon));
    }

    [TestMethod]
    public void ADailyBackupIsDueOnceItsTimeHasCome()
    {
        var daily = Schedule(FileBackupSchedule.Daily, atMinute: 12 * 60);
        var yesterday = Noon.AddDays(-1).AddMinutes(1);

        Assert.IsFalse(daily.IsDue(yesterday, false, Noon.AddMinutes(-1)), "Not yet noon.");
        Assert.IsTrue(daily.IsDue(yesterday, false, Noon), "Noon.");
        Assert.IsFalse(daily.IsDue(Noon.AddSeconds(30), false, Noon.AddHours(5)), "It ran today.");
        Assert.AreEqual(Noon.AddDays(1), daily.NextRun(Noon.AddSeconds(30), false, Noon.AddHours(5)));
    }

    [TestMethod]
    public void ADailyBackupCatchesUpWhenThePcWasOffAtItsTime()
    {
        var daily = Schedule(FileBackupSchedule.Daily, atMinute: 12 * 60);
        Assert.IsTrue(daily.IsDue(Noon.AddDays(-3), false, Noon.AddHours(-3)), "Yesterday's noon was missed.");
    }

    [TestMethod]
    public void AWeeklyBackupRunsOnItsDaysOnly()
    {
        // Noon is a Tuesday. The backup runs on Mondays and Thursdays at 18:00.
        var weekly = Schedule(FileBackupSchedule.Weekly, atMinute: 18 * 60, days: (1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Thursday));
        var monday = Noon.Date.AddDays(-1).AddHours(18);
        var thursday = Noon.Date.AddDays(2).AddHours(18);

        Assert.AreEqual(monday, weekly.LatestOccurrence(Noon));
        Assert.AreEqual(thursday, weekly.NextOccurrence(Noon));
        Assert.IsFalse(weekly.IsDue(monday.AddMinutes(5), false, Noon), "It ran on Monday evening.");
        Assert.IsTrue(weekly.IsDue(monday.AddMinutes(5), false, thursday), "Thursday 18:00.");
        Assert.IsTrue(weekly.IsDue(monday.AddDays(-7), false, Noon), "Monday was missed.");
        Assert.IsFalse(Schedule(FileBackupSchedule.Weekly, days: 0).IsOn, "No day chosen is no schedule.");
    }

    [TestMethod]
    public void AFailedRunIsTriedAgainAfterAnHourNotSooner()
    {
        var daily = Schedule(FileBackupSchedule.Daily, atMinute: 12 * 60);
        Assert.IsFalse(daily.IsDue(Noon, true, Noon.AddMinutes(30)));
        Assert.IsTrue(daily.IsDue(Noon, true, Noon.AddMinutes(60)));
        Assert.IsFalse(daily.IsDue(Noon, false, Noon.AddMinutes(60)), "A run that worked waits for tomorrow.");
        Assert.AreEqual(Noon.AddHours(1), daily.NextRun(Noon, true, Noon.AddMinutes(30)));
    }

    [TestMethod]
    public void AClockThatWasSetBackCountsAsDue()
    {
        Assert.IsTrue(Schedule(FileBackupSchedule.Hourly).IsDue(Noon.AddDays(2), false, Noon));
    }

    [TestMethod]
    public void ASchedulesReadsAsASentence()
    {
        var english = CultureInfo.InvariantCulture;
        Assert.AreEqual("Only when you ask", Schedule(FileBackupSchedule.Off).Describe(english));
        Assert.AreEqual("Every hour", Schedule(FileBackupSchedule.Hourly).Describe(english));
        Assert.AreEqual("Every day at 12:00", Schedule(FileBackupSchedule.Daily).Describe(english));
        Assert.AreEqual("Every Monday and Thursday at 18:30",
            Schedule(FileBackupSchedule.Weekly, atMinute: 18 * 60 + 30, days: (1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Thursday)).Describe(english));
        Assert.AreEqual("Every 6 hours", Schedule(FileBackupSchedule.Custom, everyHours: 6).Describe(english));
        Assert.AreEqual("Every 3 days", Schedule(FileBackupSchedule.Custom, everyHours: 72).Describe(english));
    }

    // ------------------------------------------------------------------ names, folders, rules

    [TestMethod]
    public void ABackupsNameBecomesAFolderNameWindowsAllows()
    {
        Assert.AreEqual("Photos 2026", FileBackupPaths.CleanFolderName("  Photos: 2026?  "));
        Assert.AreEqual("My files", FileBackupPaths.CleanFolderName("My/files..."));
        Assert.AreEqual("Backup", FileBackupPaths.CleanFolderName(" ?? "));
        Assert.AreEqual("CON backup", FileBackupPaths.CleanFolderName("CON"));
        Assert.IsTrue(FileBackupPaths.CleanFolderName(new string('x', 200)).Length <= 60);
    }

    [TestMethod]
    public void ANewBackupGetsAFolderNameThatIsNotTaken()
    {
        var others = new[] { Plan(FileBackupType.Clone, plan => plan.FolderName = "Music") };

        Assert.AreEqual("Photos", FileBackupPaths.FreeFolderName(_backups, "Photos", others, _ => false));
        Assert.AreEqual("Music backup", FileBackupPaths.FreeFolderName(_backups, "Music", others, _ => false), "Another backup uses it.");
        Assert.AreEqual("Photos backup", FileBackupPaths.FreeFolderName(_backups, "Photos", others,
            path => path.EndsWith("Photos", StringComparison.Ordinal)), "A folder of that name is already there.");
        Assert.AreEqual("Photos backup 2", FileBackupPaths.FreeFolderName(_backups, "Photos", others,
            path => path.EndsWith("Photos", StringComparison.Ordinal) || path.EndsWith("Photos backup", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void InsideMeansTheFolderItselfOrAnythingUnderIt()
    {
        var sub = Path.Combine(_original, "Holiday");
        Assert.IsTrue(FileBackupPaths.IsInside(sub, _original));
        Assert.IsTrue(FileBackupPaths.IsInside(_original, _original));
        Assert.IsTrue(FileBackupPaths.IsInside(_original.ToUpperInvariant() + Path.DirectorySeparatorChar, _original), "Capitals and a slash at the end do not matter.");
        Assert.IsFalse(FileBackupPaths.IsInside(_original + " 2", _original), "A folder whose name only starts the same.");
        Assert.IsFalse(FileBackupPaths.IsInside(_top, _original));
        Assert.AreEqual(Path.Combine("Holiday", "beach.jpg"), FileBackupPaths.Relative(Path.Combine(sub, "beach.jpg"), _original));
    }

    [TestMethod]
    public void APlanCoversWhatIsInItsFolderAndNotWhatItLeavesOut()
    {
        var plan = Plan(FileBackupType.Clone, change => change.Exclude = ["node_modules", "*.tmp"]);

        Assert.AreEqual(Path.Combine("Holiday", "beach.jpg"), plan.RelativeOf(Path.Combine(_original, "Holiday", "beach.jpg")));
        Assert.IsNull(plan.RelativeOf(Path.Combine(_top, "Elsewhere", "beach.jpg")));
        Assert.IsNull(plan.RelativeOf(_original), "The folder itself is not a file in it.");
        Assert.IsNull(plan.RelativeOf(Path.Combine(_original, "node_modules", "a.js")), "Inside a folder that is left out.");
        Assert.IsNull(plan.RelativeOf(Path.Combine(_original, "draft.TMP")), "A name that is left out, whatever its capitals.");

        var oneFile = Plan(FileBackupType.Clone, change => { change.Source = Path.Combine(_original, "report.docx"); change.SourceIsFile = true; });
        Assert.AreEqual("report.docx", oneFile.RelativeOf(Path.Combine(_original, "report.docx")));
        Assert.IsNull(oneFile.RelativeOf(Path.Combine(_original, "other.docx")));
    }

    [TestMethod]
    public void APlanThatWouldBackUpIntoItselfIsRefused()
    {
        Assert.IsNull(FileBackupRules.Problem(Plan(FileBackupType.Clone), []));

        var inside = Plan(FileBackupType.Clone, plan => plan.Destination = _original);
        StringAssert.Contains(FileBackupRules.Problem(inside, []), "inside what it backs up");

        // The backup's own folder would be the very folder that is backed up.
        var itself = Plan(FileBackupType.Clone, plan => { plan.Destination = _top; plan.FolderName = "Photos"; });
        Assert.IsNotNull(FileBackupRules.Problem(itself, []));

        var around = Plan(FileBackupType.Clone, plan => { plan.Source = Path.Combine(_backups, "Photos", "Files", "x"); });
        StringAssert.Contains(FileBackupRules.Problem(around, []), "inside the folder this backup would use");
    }

    [TestMethod]
    public void TwoPlansCannotShareAFolder()
    {
        var first = Plan(FileBackupType.Clone);
        var second = Plan(FileBackupType.Versions, plan => plan.Source = Path.Combine(_top, "Music"));
        StringAssert.Contains(FileBackupRules.Problem(second, [first]), "already uses that folder");

        second.FolderName = "Music";
        Assert.IsNull(FileBackupRules.Problem(second, [first]));
        Assert.IsNull(FileBackupRules.Problem(first, [first]), "A plan is not in its own way when it is edited.");
    }

    [TestMethod]
    public void WhatIsTypedToLeaveOutHasToBeNames()
    {
        CollectionAssert.AreEqual(new[] { "node_modules", "*.tmp", "Thumbs.db" }, FileBackupRules.ParseExclude(" node_modules, *.tmp ;Thumbs.db\n*.TMP "));
        Assert.AreEqual(0, FileBackupRules.ParseExclude("  ").Count);

        var path = Plan(FileBackupType.Clone, plan => plan.Exclude = [@"Holiday\old"]);
        StringAssert.Contains(FileBackupRules.Problem(path, []), "not a path");
        var slash = Plan(FileBackupType.Clone, plan => plan.Exclude = ["/PURGE"]);
        Assert.IsNotNull(FileBackupRules.Problem(slash, []), "Nothing typed here may read as an instruction to robocopy.");
    }

    [TestMethod]
    public void AScheduleHasToMakeSense()
    {
        var noDay = Plan(FileBackupType.Clone, plan => plan.Schedule = Schedule(FileBackupSchedule.Weekly, days: 0));
        StringAssert.Contains(FileBackupRules.Problem(noDay, []), "at least one day");
        var never = Plan(FileBackupType.Clone, plan => plan.Schedule = Schedule(FileBackupSchedule.Custom, everyHours: 0));
        Assert.IsNotNull(FileBackupRules.Problem(never, []));
        var none = Plan(FileBackupType.Zip, plan => plan.KeepCount = 0);
        Assert.IsNotNull(FileBackupRules.Problem(none, []));
    }

    [TestMethod]
    public void DatedNamesReadBackAsTheirDate()
    {
        var when = new DateTime(2026, 10, 6, 16, 30, 5);
        Assert.AreEqual("2026-10-06 16.30.05", FileBackupLayout.Stamp(when));
        Assert.IsTrue(FileBackupLayout.TryParseStamp("2026-10-06 16.30.05", out var read));
        Assert.AreEqual(when, read);
        Assert.IsFalse(FileBackupLayout.TryParseStamp("Files", out _));
        Assert.IsFalse(FileBackupLayout.TryParseStamp("2026-10-06 16.30.05.partial", out _), "A version that was not finished is not a version.");

        Assert.AreEqual("Photos 2026-10-06 16.30.05.zip", FileBackupLayout.ZipName("Photos", when));
        Assert.IsTrue(FileBackupLayout.TryParseZip("Photos 2026-10-06 16.30.05.zip", "Photos", out read));
        Assert.AreEqual(when, read);
        Assert.IsFalse(FileBackupLayout.TryParseZip("Holiday.zip", "Photos", out _), "A zip somebody put there is not one of the backup's.");
    }

    [TestMethod]
    public void ABackupOnlyUsesAFolderThatIsItsOwn()
    {
        var plan = Plan(FileBackupType.Clone);
        Assert.IsNull(FileBackupLayout.WhyNotRoot(plan), "Not there yet.");

        Directory.CreateDirectory(plan.Root);
        Assert.IsNull(FileBackupLayout.WhyNotRoot(plan), "Empty.");

        File.WriteAllText(Path.Combine(plan.Root, "holiday.jpg"), "someone's file");
        StringAssert.Contains(FileBackupLayout.WhyNotRoot(plan), "already has other things in it");
        Assert.AreEqual(FileBackupState.Failed, Run(plan).Outcome, "A run refuses too.");
        Assert.IsTrue(There(plan.Root, "holiday.jpg"));
        Assert.IsFalse(Directory.Exists(plan.FilesFolder), "Nothing was written into a folder that is not the backup's.");
    }

    [TestMethod]
    public void ABackupsFolderIsRecognisedByItsMarker()
    {
        Write("a.txt", "one");
        var plan = Plan(FileBackupType.Clone);
        Assert.AreEqual(FileBackupState.Ok, Run(plan).Outcome);
        Assert.AreEqual(plan.Id, FileBackupLayout.ReadMarker(plan.Root)!.PlanId);
        Assert.IsNull(FileBackupLayout.WhyNotRoot(plan));

        // The same backup set up again (its settings were lost): carried on.
        var again = Plan(FileBackupType.Clone);
        Assert.IsNull(FileBackupLayout.WhyNotRoot(again));

        // Another backup that happens to want the same folder: refused.
        var other = Plan(FileBackupType.Clone, change => change.Source = Path.Combine(_top, "Music"));
        StringAssert.Contains(FileBackupLayout.WhyNotRoot(other), "already holds another backup");
    }

    [TestMethod]
    public void SavingANewBackupFindsItAFolderOfItsOwn()
    {
        Write("a.txt", "one");
        var plan = Plan(FileBackupType.Clone, change => { change.FolderName = string.Empty; change.SourceIsFile = true; });

        Assert.IsNull(FileBackupRules.Prepare(plan, [], isNew: true));
        Assert.AreEqual("Photos", plan.FolderName);
        Assert.IsFalse(plan.SourceIsFile, "It is a folder, whatever was assumed before.");

        // A folder of that name with someone's files in it is in the way: the backup goes beside it.
        Directory.CreateDirectory(Path.Combine(_backups, "Photos"));
        File.WriteAllText(Path.Combine(_backups, "Photos", "holiday.jpg"), "someone's file");
        var second = Plan(FileBackupType.Clone, change => change.FolderName = string.Empty);
        Assert.IsNull(FileBackupRules.Prepare(second, [], isNew: true));
        Assert.AreEqual("Photos backup", second.FolderName);

        // The backup's own folder from before (its settings were lost) is taken up again.
        Run(second);
        var again = Plan(FileBackupType.Clone, change => change.FolderName = string.Empty);
        Assert.IsNull(FileBackupRules.Prepare(again, [], isNew: true));
        Assert.AreEqual("Photos backup", again.FolderName);

        var oneFile = Plan(FileBackupType.Clone, change => { change.Source = Path.Combine(_original, "a.txt"); change.Name = "a"; change.FolderName = string.Empty; });
        Assert.IsNull(FileBackupRules.Prepare(oneFile, [], isNew: true));
        Assert.IsTrue(oneFile.SourceIsFile);
    }

    [TestMethod]
    public void ANewBackupOfSomethingThatIsNotThereIsNotSaved()
    {
        var noOriginal = Plan(FileBackupType.Clone, change => change.Source = Path.Combine(_top, "Not there"));
        StringAssert.Contains(FileBackupRules.Prepare(noOriginal, [], isNew: true), "is not there");

        var noPlace = Plan(FileBackupType.Clone, change => change.Destination = Path.Combine(_top, "Not plugged in"));
        StringAssert.Contains(FileBackupRules.Prepare(noPlace, [], isNew: true), "is not there");

        Assert.AreEqual("Choose what to back up.", FileBackupRules.Prepare(Plan(FileBackupType.Clone, change => change.Source = string.Empty), [], isNew: true));
        Assert.AreEqual("Choose where the backup is kept.", FileBackupRules.Prepare(Plan(FileBackupType.Clone, change => change.Destination = string.Empty), [], isNew: true));
        Assert.AreEqual("Give the backup a name.", FileBackupRules.Prepare(Plan(FileBackupType.Clone, change => change.Name = string.Empty), [], isNew: true));

        // A backup that exists is not asked to be reachable to change its schedule (its drive may be unplugged).
        Assert.IsNull(FileBackupRules.Prepare(noPlace, [], isNew: false));
    }

    [TestMethod]
    public void ATimeOfDayIsReadTheWayPeopleTypeIt()
    {
        var english = CultureInfo.InvariantCulture;
        foreach (var (typed, expected) in new[] { ("14:30", 870), (" 9 ", 540), ("0930", 570), ("930", 570), ("2:30 PM", 870), ("00:00", 0), ("23.59", 1439) })
        {
            Assert.IsTrue(FileBackupRules.TryParseTime(typed, out var minutes, english), typed);
            Assert.AreEqual(expected, minutes, typed);
        }

        foreach (var typed in new[] { "", "soon", "25:00", "2460", "10/6/2026" })
            Assert.IsFalse(FileBackupRules.TryParseTime(typed, out _, english), typed);

        Assert.AreEqual("14:30", FileBackupRules.TimeText(870, english));
    }

    [TestMethod]
    public void WhatABackupKeepsIsSaidInWords()
    {
        Assert.AreEqual("keeps removed files for 30 days", FileBackupWords.Keeps(Plan(FileBackupType.Clone, plan => plan.KeepRemoved = true)));
        Assert.AreEqual("keeps removed files and earlier versions for a year",
            FileBackupWords.Keeps(Plan(FileBackupType.Clone, plan => { plan.KeepRemoved = true; plan.KeepVersions = true; plan.KeepDays = 365; })));
        Assert.AreEqual("removed files are gone from the backup too", FileBackupWords.Keeps(Plan(FileBackupType.Clone)));
        Assert.AreEqual("keeps earlier versions for good", FileBackupWords.Keeps(Plan(FileBackupType.AddAndUpdate, plan => { plan.KeepVersions = true; plan.KeepDays = 0; })));
        Assert.AreEqual("nothing is ever removed", FileBackupWords.Keeps(Plan(FileBackupType.AddAndUpdate, plan => plan.KeepRemoved = true)), "Add and update removes nothing, so there is nothing removed to keep.");
        Assert.AreEqual("keeps the last 10 versions", FileBackupWords.Keeps(Plan(FileBackupType.Versions)));
        Assert.AreEqual("keeps the newest zip only", FileBackupWords.Keeps(Plan(FileBackupType.Zip, plan => plan.KeepCount = 1)));

        var daily = Plan(FileBackupType.Versions, plan => plan.Schedule = Schedule(FileBackupSchedule.Daily));
        Assert.AreEqual("Versions  ·  Every day at 12:00  ·  keeps last 10", FileBackupWords.Summary(daily, CultureInfo.InvariantCulture));

        Assert.IsTrue(Plan(FileBackupType.Versions).HasFileHistory);
        Assert.IsTrue(Plan(FileBackupType.Zip).HasFileHistory);
        Assert.IsFalse(Plan(FileBackupType.Clone, plan => plan.KeepRemoved = true).HasFileHistory, "The safety net keeps removed files, not earlier versions.");
        Assert.IsTrue(Plan(FileBackupType.AddAndUpdate, plan => plan.KeepVersions = true).HasFileHistory);
    }

    [TestMethod]
    public void ThePageSaysHowABackupStandsInFewWords()
    {
        static string Day(DateTime _) => "today, 12:01";
        var manual = Plan(FileBackupType.Clone);
        var scheduled = Plan(FileBackupType.Clone, plan => plan.Schedule = Schedule(FileBackupSchedule.Daily));
        var ran = Noon.ToUniversalTime();

        Assert.AreEqual(("Not backed up yet.", FileBackupTone.Warn), FileBackupWords.Status(manual, new FileBackupState(), false, Day));
        Assert.AreEqual(("Backed up today, 12:01. 3 files copied.", FileBackupTone.Good),
            FileBackupWords.Status(manual, new FileBackupState { Outcome = FileBackupState.Ok, Message = "3 files copied.", LastRunUtc = ran, LastEndUtc = ran }, false, Day));
        Assert.AreEqual(("Up to date. Checked today, 12:01.", FileBackupTone.Good),
            FileBackupWords.Status(manual, new FileBackupState { Outcome = FileBackupState.Nothing, Message = "Nothing had changed.", LastEndUtc = ran }, false, Day));
        Assert.AreEqual(("Backed up today, 12:01, with problems. 2 files could not be copied; see the report.", FileBackupTone.Warn),
            FileBackupWords.Status(manual, new FileBackupState { Outcome = FileBackupState.Problems, Message = "2 files could not be copied; see the report.", LastEndUtc = ran }, false, Day));
        Assert.AreEqual(("Failed today, 12:01. The disk is full.", FileBackupTone.Bad),
            FileBackupWords.Status(manual, new FileBackupState { Outcome = FileBackupState.Failed, Message = "The disk is full.", LastEndUtc = ran }, false, Day));
        Assert.AreEqual(("Stopped today, 12:01.", FileBackupTone.Warn),
            FileBackupWords.Status(manual, new FileBackupState { Outcome = FileBackupState.Stopped, Message = "Stopped. What was copied…", LastEndUtc = ran }, false, Day));
        Assert.AreEqual("Waiting for E:, which is not connected.",
            FileBackupWords.Status(manual, new FileBackupState { Outcome = FileBackupState.Waiting, Message = "Waiting for E:, which is not connected." }, false, Day).Text);
        StringAssert.StartsWith(FileBackupWords.Status(manual, new FileBackupState { Outcome = FileBackupState.Ok, LastEndUtc = ran }, cutShort: true, Day).Text, "The last run was interrupted");

        Assert.IsNull(FileBackupWords.Next(manual, new FileBackupState(), Noon, Day), "No schedule, no next time.");
        Assert.AreEqual("Next: any minute", FileBackupWords.Next(scheduled, new FileBackupState(), Noon, Day));
        Assert.AreEqual("Next: today, 12:01", FileBackupWords.Next(scheduled, new FileBackupState { Outcome = FileBackupState.Ok, LastRunUtc = ran }, Noon.AddHours(1), Day));
        Assert.IsNull(FileBackupWords.Next(scheduled, new FileBackupState { Outcome = FileBackupState.Waiting }, Noon, Day), "A backup waiting for its drive has no time to promise.");

        Assert.AreEqual("Copying…", FileBackupWords.Progress(new FileBackupProgress { Phase = "Copying" }, _ => "x"));
        Assert.AreEqual("Copying…  1 file", FileBackupWords.Progress(new FileBackupProgress { Phase = "Copying", Files = 1 }, _ => "x"));
        Assert.AreEqual("Copying…  12 files (2 GB)", FileBackupWords.Progress(new FileBackupProgress { Phase = "Copying", Files = 12, Bytes = 5 }, _ => "2 GB"));
    }

    // NEW (retention, zip tick box)
    [TestMethod]
    public void KeepAndZipTogetherDecideWhatKindOfBackupItIs()
    {
        var plan = Plan(FileBackupType.Clone);

        FileBackupRules.Shape(plan, zipped: false, FileBackupRetention.Latest);
        Assert.AreEqual(FileBackupType.Clone, plan.Type, "Latest only, not zipped: a normal clone.");

        FileBackupRules.Shape(plan, zipped: false, FileBackupRetention.Latest, FileBackupType.AddAndUpdate);
        Assert.AreEqual(FileBackupType.AddAndUpdate, plan.Type, "Or the other kind that keeps one copy, when chosen.");

        FileBackupRules.Shape(plan, zipped: true, FileBackupRetention.Latest);
        Assert.AreEqual(FileBackupType.Zip, plan.Type);
        Assert.AreEqual(1, plan.KeepCount, "Zipped, latest only: one zip, replaced by the next run's.");

        FileBackupRules.Shape(plan, zipped: false, new FileBackupRetention(10));
        Assert.AreEqual(FileBackupType.Versions, plan.Type, "Keeping several without zipping means dated copies.");
        Assert.AreEqual(10, plan.KeepCount);
        Assert.AreEqual(0, plan.MaxAgeDays);

        FileBackupRules.Shape(plan, zipped: true, new FileBackupRetention(1, Days: 90));
        Assert.AreEqual(FileBackupType.Zip, plan.Type);
        Assert.AreEqual(90, plan.MaxAgeDays, "By age: the number no longer decides.");

        FileBackupRules.Shape(plan, zipped: false, FileBackupRetention.Latest);
        Assert.AreEqual(FileBackupType.Clone, plan.Type);
        Assert.AreEqual(0, plan.MaxAgeDays, "Back to one copy: nothing of the age rule is left behind.");
    }

    [TestMethod]
    public void WhatABackupKeepsReadsBackAsTheSameChoice()
    {
        Assert.AreEqual(FileBackupRetention.Latest, FileBackupRetention.Of(Plan(FileBackupType.Clone, plan => plan.KeepCount = 10)), "A clone has one copy whatever the number says.");
        Assert.AreEqual(new FileBackupRetention(10), FileBackupRetention.Of(Plan(FileBackupType.Versions)));
        Assert.AreEqual(new FileBackupRetention(1, 30), FileBackupRetention.Of(Plan(FileBackupType.Zip, plan => { plan.KeepCount = 7; plan.MaxAgeDays = 30; })));

        Assert.AreEqual("Latest only", FileBackupRetention.Latest.Label);
        Assert.AreEqual("Last 10", new FileBackupRetention(10).Label);
        Assert.AreEqual("30 days", new FileBackupRetention(1, 30).Label);
        Assert.AreEqual("1 year", new FileBackupRetention(1, 365).Label);

        var daily = Schedule(FileBackupSchedule.Daily);
        Assert.AreEqual("Versions  ·  Every day at 12:00  ·  keeps last 10",
            FileBackupWords.Summary(Plan(FileBackupType.Versions, plan => plan.Schedule = daily), CultureInfo.InvariantCulture));
        Assert.AreEqual("Zip archive  ·  Every day at 12:00  ·  keeps 30 days",
            FileBackupWords.Summary(Plan(FileBackupType.Zip, plan => { plan.Schedule = daily; plan.MaxAgeDays = 30; }), CultureInfo.InvariantCulture));
        Assert.AreEqual("Clone  ·  Every day at 12:00", FileBackupWords.Summary(Plan(FileBackupType.Clone, plan => plan.Schedule = daily), CultureInfo.InvariantCulture));
        Assert.AreEqual("keeps versions for 30 days", FileBackupWords.Keeps(Plan(FileBackupType.Versions, plan => plan.MaxAgeDays = 30)));
        Assert.AreEqual("keeps zips for a year", FileBackupWords.Keeps(Plan(FileBackupType.Zip, plan => plan.MaxAgeDays = 365)));
    }

    [TestMethod]
    public void OldRunsAreThinnedOutByNumberOrByAgeAndTheNewestAlwaysStays()
    {
        var runs = new[] { 1, 10, 40, 100, 400 }.Select(days => ("made " + days + " days ago", Noon.AddDays(-days))).ToList(); // newest first

        var byNumber = Plan(FileBackupType.Versions, plan => plan.KeepCount = 2);
        CollectionAssert.AreEqual(runs.Skip(2).ToList(), FileBackupEngine.Expired(runs, byNumber, Noon));

        var byAge = Plan(FileBackupType.Versions, plan => { plan.KeepCount = 1; plan.MaxAgeDays = 90; });
        CollectionAssert.AreEqual(runs.Skip(3).ToList(), FileBackupEngine.Expired(runs, byAge, Noon), "Everything of the last 90 days stays, however many that is.");

        var stale = runs.Skip(3).ToList(); // a backup that has not run for months
        CollectionAssert.AreEqual(stale.Skip(1).ToList(), FileBackupEngine.Expired(stale, byAge, Noon), "The newest is kept although it is past its age: it is the only copy.");
        Assert.AreEqual(0, FileBackupEngine.Expired([], byAge, Noon).Count);
    }

    [TestMethod]
    public void AgeGoesOnAlthoughNothingChanged()
    {
        Write("a.txt", "one");
        var plan = Plan(FileBackupType.Versions, change => { change.KeepCount = 1; change.MaxAgeDays = 30; });
        Run(plan, Noon.AddDays(-50));
        Write("a.txt", "two!", minutes: 10);
        Run(plan, Noon.AddDays(-45));
        Write("a.txt", "three", minutes: 20);
        Run(plan, Noon.AddDays(-10));
        Assert.AreEqual(1, FileBackupEngine.Versions(plan.Root).Count, "The two from more than 30 days before were deleted when the third was made.");

        Write("a.txt", "four!!", minutes: 30);
        Run(plan, Noon.AddDays(-5));
        Assert.AreEqual(2, FileBackupEngine.Versions(plan.Root).Count);

        // Two months on, nothing has changed: no new version, but the one that is now too old goes.
        var later = Run(plan, Noon.AddDays(40));
        Assert.AreEqual(FileBackupState.Nothing, later.Outcome);
        Assert.AreEqual(1, FileBackupEngine.Versions(plan.Root).Count);
        Assert.AreEqual("four!!", Read(FileBackupEngine.Versions(plan.Root)[0].Path, "a.txt"));
    }

    [TestMethod]
    public void AZippedBackupKeepsOneZipAndReplacesItEachRun()
    {
        Write("report.txt", "first");
        var plan = Plan(FileBackupType.Clone);
        FileBackupRules.Shape(plan, zipped: true, FileBackupRetention.Latest);

        Run(plan);
        Write("report.txt", "second!", minutes: 10);
        var result = Run(plan, Noon.AddHours(1));

        Assert.AreEqual(FileBackupState.Ok, result.Outcome, result.Message);
        var zips = FileBackupEngine.Zips(plan);
        Assert.AreEqual(1, zips.Count);
        using var zip = ZipFile.OpenRead(zips[0].Path);
        using var reader = new StreamReader(zip.GetEntry("report.txt")!.Open());
        Assert.AreEqual("second!", reader.ReadToEnd());
    }

    // ------------------------------------------------------------------ NEW (backup contents): what suits what is backed up

    // A file of what is backed up, in folders of its own; what is in it does not matter here.
    private void Touch(params string[] parts)
    {
        var path = Path.Combine([_original, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    [TestMethod]
    public void WhatFilesMostlyAreIsToldByTheirNames()
    {
        Assert.AreEqual(FileBackupContent.Media, FileBackupContents.Guess(["a.jpg", "b.JPG", "c.mp4", "song.flac", "notes.txt"]));
        Assert.AreEqual(FileBackupContent.Documents, FileBackupContents.Guess(["report.docx", "budget.xlsx", "scan.pdf", "photo.png"]));
        Assert.AreEqual(FileBackupContent.Projects, FileBackupContents.Guess(["Program.cs", "main.py", "app.ts", "readme.md"]));
        Assert.AreEqual(FileBackupContent.Mixed, FileBackupContents.Guess(["a.jpg", "b.docx", "c.cs", "d.iso"]), "No kind is more than half.");
        Assert.AreEqual(FileBackupContent.Mixed, FileBackupContents.Guess(["a.jpg", "b.docx"]), "Half is not most.");
        Assert.AreEqual(FileBackupContent.Mixed, FileBackupContents.Guess([]));

        Assert.AreEqual(FileBackupContent.Media, FileBackupContents.Guess(["a.cr3", "a.xmp", "b.cr3", "b.xmp", "list.txt"]),
            "What a camera or an editor writes beside a photo counts with the photos.");
        Assert.AreEqual(FileBackupContent.Documents, FileBackupContents.Guess(["report.docx", "~$report.docx", "desktop.ini", "Thumbs.db"]),
            "Lock files and Windows' own files say nothing about a folder.");
    }

    [TestMethod]
    public void AProjectIsToldByWhatIsInItsFolder()
    {
        Assert.IsTrue(FileBackupContents.Project(["Assets", "ProjectSettings", "Library", "Packages"], ["Game.sln", "Assembly-CSharp.csproj"], out var unity));
        Assert.AreEqual(FileBackupTools.Unity | FileBackupTools.DotNet, unity);

        Assert.IsTrue(FileBackupContents.Project(["src", "node_modules"], ["package.json"], out var node));
        Assert.AreEqual(FileBackupTools.Node, node);

        Assert.IsTrue(FileBackupContents.Project([".git", "docs"], ["README.md"], out var plain), "A repository is a project, whatever it is written in.");
        Assert.AreEqual(FileBackupTools.None, plain);

        Assert.IsTrue(FileBackupContents.Project(["Content", "Source"], ["Shooter.uproject"], out var unreal));
        Assert.AreEqual(FileBackupTools.Unreal, unreal);
        Assert.IsTrue(FileBackupContents.Project([], ["project.godot"], out var godot));
        Assert.AreEqual(FileBackupTools.Godot, godot);
        Assert.IsTrue(FileBackupContents.Project([], ["pyproject.toml"], out var python));
        Assert.AreEqual(FileBackupTools.Python, python);

        Assert.IsFalse(FileBackupContents.Project(["Holiday", "Assets"], ["beach.jpg", "list.txt"], out var none), "A folder called Assets alone is not a Unity project.");
        Assert.AreEqual(FileBackupTools.None, none);
    }

    [TestMethod]
    public void OnlyFoldersThatGetRebuiltAreLeftOut()
    {
        CollectionAssert.AreEqual(new[] { "Library", "Temp", "Logs", "obj", "MemoryCaptures", ".vs" }, FileBackupContents.Rebuilt(FileBackupTools.Unity | FileBackupTools.DotNet),
            "Named once, though Unity and .NET both have them.");
        CollectionAssert.AreEqual(new[] { ".vs", "obj", "node_modules" }, FileBackupContents.Rebuilt(FileBackupTools.DotNet | FileBackupTools.Node));
        Assert.AreEqual(0, FileBackupContents.Rebuilt(FileBackupTools.None).Count);

        var every = FileBackupContents.Rebuilt((FileBackupTools)0x3F);
        foreach (var name in new[] { "bin", "Build", "Builds", "Saved", "Assets", "Packages" })
            Assert.IsFalse(every.Contains(name, StringComparer.OrdinalIgnoreCase), name + " can hold what nobody can rebuild.");
    }

    [TestMethod]
    public void DocumentsKeepOlderBackupsAndPhotosKeepOneCopy()
    {
        var documents = FileBackupContents.Advise(FileBackupContent.Documents, FileBackupTools.None, sharesSpace: true);
        Assert.AreEqual(new FileBackupRetention(30), documents.Keep);
        CollectionAssert.AreEqual(new[] { "~$*", "*.tmp" }, documents.Skip.ToArray());
        StringAssert.Contains(documents.Why, "last 30 backups");

        var media = FileBackupContents.Advise(FileBackupContent.Media, FileBackupTools.None, sharesSpace: true);
        Assert.IsTrue(media.Keep.LatestOnly, "Large files that rarely change: one copy.");
        Assert.IsTrue(media.KeepRemoved && media.KeepVersions, "A photo deleted or overwritten by mistake can still be had back.");
        Assert.AreEqual(90, media.KeepDays);
        Assert.AreEqual(media with { Skip = media.Skip }, FileBackupContents.Advise(FileBackupContent.Media, FileBackupTools.None, sharesSpace: false) with { Skip = media.Skip },
            "One copy is the same on any drive.");

        var mixed = FileBackupContents.Advise(FileBackupContent.Mixed, FileBackupTools.Unity, sharesSpace: true);
        Assert.IsTrue(mixed.Keep.LatestOnly && mixed.KeepRemoved && !mixed.KeepVersions);
        Assert.AreEqual(0, mixed.Skip.Count, "Nothing is left out of a mix, whatever was found in it.");

        foreach (var content in FileBackupContents.All)
            Assert.AreEqual(FileBackupSchedule.Daily, FileBackupContents.Advise(content, FileBackupTools.None, true).Schedule);
    }

    [TestMethod]
    public void ProjectsKeepTenBackupsAndSkipWhatIsRebuilt()
    {
        var unity = FileBackupContents.Advise(FileBackupContent.Projects, FileBackupTools.Unity, sharesSpace: true);
        Assert.AreEqual(new FileBackupRetention(10), unity.Keep);
        CollectionAssert.Contains(unity.Skip.ToArray(), "Library");
        StringAssert.Contains(unity.Why, "Skips folders that get rebuilt: Library, Temp, Logs…");

        var unknown = FileBackupContents.Advise(FileBackupContent.Projects, FileBackupTools.None, sharesSpace: true);
        Assert.AreEqual(0, unknown.Skip.Count, "Nothing is left out on a guess.");
        Assert.IsFalse(unknown.Why.Contains("Skips"));

        var node = FileBackupContents.Advise(FileBackupContent.Projects, FileBackupTools.Node, sharesSpace: true);
        StringAssert.EndsWith(node.Why, "Skips folders that get rebuilt: node_modules.");
    }

    [TestMethod]
    public void ADriveThatStoresEveryDatedCopyInFullGetsOneCopyWithEarlierVersions()
    {
        foreach (var content in new[] { FileBackupContent.Documents, FileBackupContent.Projects })
        {
            var advice = FileBackupContents.Advise(content, FileBackupTools.None, sharesSpace: false);
            Assert.IsTrue(advice.Keep.LatestOnly, $"{content}: thirty whole copies would not fit on a stick.");
            Assert.IsTrue(advice.KeepVersions && advice.KeepRemoved, $"{content}: what changed can still be gone back to.");
            StringAssert.Contains(advice.Why, "earlier versions");
        }

        Assert.AreEqual(90, FileBackupContents.Advise(FileBackupContent.Documents, FileBackupTools.None, false).KeepDays);
    }

    [TestMethod]
    public void TakingARecommendationSetsTheBackupUp()
    {
        var plan = new FileBackupPlan { Type = FileBackupType.Zip, KeepCount = 3, KeepRemoved = false };

        FileBackupContents.Apply(plan, FileBackupContents.Advise(FileBackupContent.Documents, FileBackupTools.None, sharesSpace: true));
        Assert.AreEqual(FileBackupType.Versions, plan.Type, "A normal copy, a dated one per run.");
        Assert.AreEqual(30, plan.KeepCount);
        Assert.AreEqual(0, plan.MaxAgeDays);
        Assert.AreEqual(FileBackupSchedule.Daily, plan.Schedule.Kind);
        CollectionAssert.AreEqual(new[] { "~$*", "*.tmp" }, plan.Exclude);

        FileBackupContents.Apply(plan, FileBackupContents.Advise(FileBackupContent.Media, FileBackupTools.None, true));
        Assert.AreEqual(FileBackupType.Clone, plan.Type);
        Assert.IsTrue(plan.KeepRemoved && plan.KeepVersions);
        Assert.AreEqual(90, plan.KeepDays);
        Assert.IsNull(FileBackupRules.Problem(Plan(FileBackupType.Clone, change =>
        {
            FileBackupContents.Apply(change, FileBackupContents.Advise(FileBackupContent.Projects, FileBackupTools.Unity, true));
        }), []), "What is recommended is a backup that can be saved.");
    }

    [TestMethod]
    public void ARecommendationLeavesAChosenScheduleAlone()
    {
        var weekly = new FileBackupPlan { Schedule = Schedule(FileBackupSchedule.Weekly, days: 1 << (int)DayOfWeek.Friday) };
        FileBackupContents.Apply(weekly, FileBackupContents.Advise(FileBackupContent.Mixed, FileBackupTools.None, true));
        Assert.AreEqual(FileBackupSchedule.Weekly, weekly.Schedule.Kind);

        var manual = new FileBackupPlan();
        FileBackupContents.Apply(manual, FileBackupContents.Advise(FileBackupContent.Mixed, FileBackupTools.None, true), previous: null, schedule: false);
        Assert.AreEqual(FileBackupSchedule.Off, manual.Schedule.Kind, "Manual, chosen by hand, stays manual.");
    }

    [TestMethod]
    public void AnotherRecommendationTakesItsOwnSkippedNamesBackAndKeepsTypedOnes()
    {
        var plan = new FileBackupPlan { Exclude = ["Scratch"] };
        var unity = FileBackupContents.Advise(FileBackupContent.Projects, FileBackupTools.Unity, true);
        FileBackupContents.Apply(plan, unity);
        CollectionAssert.AreEqual(new[] { "Scratch", "Library", "Temp", "Logs", "obj", "MemoryCaptures", ".vs" }, plan.Exclude);

        plan.Exclude.Add("Renders"); // typed in the Skip box afterwards
        var media = FileBackupContents.Advise(FileBackupContent.Media, FileBackupTools.Unity, true);
        FileBackupContents.Apply(plan, media, previous: unity);
        CollectionAssert.AreEqual(new[] { "Scratch", "Renders", "Thumbs.db" }, plan.Exclude);

        FileBackupContents.Apply(plan, media, previous: media);
        CollectionAssert.AreEqual(new[] { "Scratch", "Renders", "Thumbs.db" }, plan.Exclude, "The same one twice changes nothing.");
    }

    [TestMethod]
    public void AFolderOfUnityProjectsIsFoundToBeProjects()
    {
        foreach (var project in new[] { "Platformer", "Racing", "Puzzle" })
        {
            Touch(project, "Assets", "Scenes", "Main.unity");
            Touch(project, "ProjectSettings", "ProjectVersion.txt");
            Touch(project, "Library", "ArtifactDB");
        }
        Touch("Notes", "ideas.txt");
        Touch("readme.txt");

        var found = FileBackupContents.Detect(_original);

        Assert.IsNotNull(found);
        Assert.AreEqual(FileBackupContent.Projects, found.Value.Content);
        Assert.IsTrue(found.Value.Tools.HasFlag(FileBackupTools.Unity));
    }

    [TestMethod]
    public void AProjectAndProjectsOneFolderFurtherDownAreFound()
    {
        Touch("Game.sln");
        Touch("Game", "Game.csproj");
        Assert.AreEqual(new FileBackupFinding(FileBackupContent.Projects, FileBackupTools.DotNet), FileBackupContents.Detect(_original), "The folder itself is a project.");

        File.Delete(Path.Combine(_original, "Game.sln"));
        Directory.Delete(Path.Combine(_original, "Game"), recursive: true);
        Touch("Web", "shop", "package.json");
        Touch("Web", "blog", "package.json");
        Assert.AreEqual(new FileBackupFinding(FileBackupContent.Projects, FileBackupTools.Node), FileBackupContents.Detect(_original), "Its only folder holds projects.");
    }

    [TestMethod]
    public void AFewProjectsAmongManyFoldersDoNotMakeItProjects()
    {
        Touch("Tool", "package.json");
        foreach (var year in new[] { "2023", "2024", "2025" })
        {
            Touch(year, "letter.docx");
            Touch(year, "taxes.xlsx");
        }

        var found = FileBackupContents.Detect(_original);

        Assert.AreEqual(FileBackupContent.Documents, found!.Value.Content);
        Assert.AreEqual(FileBackupTools.Node, found.Value.Tools, "What was found is still known, should Projects be chosen by hand.");
    }

    [TestMethod]
    public void PhotosAreFoundInFoldersBelowAndASingleFileByItsName()
    {
        Touch("2025", "Holiday", "beach.jpg");
        Touch("2025", "Holiday", "dunes.jpg");
        Touch("2025", "clip.mp4");
        Touch("list.txt");
        Assert.AreEqual(new FileBackupFinding(FileBackupContent.Media, FileBackupTools.None), FileBackupContents.Detect(_original));

        Assert.AreEqual(FileBackupContent.Documents, FileBackupContents.Detect(Path.Combine(_original, "list.txt"))!.Value.Content);
        Assert.AreEqual(FileBackupContent.Media, FileBackupContents.Detect(Path.Combine(_original, "2025", "clip.mp4"))!.Value.Content);
    }

    [TestMethod]
    public void WhatIsNotThereIsNotGuessedAt()
    {
        Assert.IsNull(FileBackupContents.Detect(Path.Combine(_original, "not there")));
        Assert.IsNull(FileBackupContents.Detect(""));
        Assert.IsNull(FileBackupContents.Detect("half a pa"));
        Assert.AreEqual(FileBackupContent.Mixed, FileBackupContents.Detect(_original)!.Value.Content, "An empty folder is a mix until something is in it.");
    }

    [TestMethod]
    public void TheSameDriveMeansTheSameRoot()
    {
        Assert.IsTrue(FileBackupPaths.OnSameDrive(_original, _backups), "Both are in the same temporary folder.");
        Assert.IsFalse(FileBackupPaths.OnSameDrive(_original, ""), "Nothing is said until both are known.");
        Assert.IsFalse(FileBackupPaths.OnSameDrive("", _backups));
        Assert.IsFalse(FileBackupPaths.OnSameDrive(_original, "not a full path"));
        if (OperatingSystem.IsWindows())
        {
            Assert.IsFalse(FileBackupPaths.OnSameDrive(@"C:\Users\me\Pictures", @"E:\Backups"));
            Assert.IsTrue(FileBackupPaths.OnSameDrive(@"c:\Users\me\Pictures", @"C:\Backups"));
        }
    }

    // ------------------------------------------------------------------ NEW (backup drives): drives on offer

    [TestMethod]
    public void BackupsOnADriveAreFoundAtItsTopAndOneLevelDown()
    {
        // _top stands in for a drive: a backup straight in it, two in a "Backups" folder, one too deep to be looked for.
        string Backup(params string[] parts)
        {
            var folder = Path.Combine([_top, .. parts]);
            Directory.CreateDirectory(folder);
            FileBackupLayout.WriteMarker(folder, new FileBackupMarker { PlanId = "x", Name = parts[^1] });
            return folder;
        }

        var top = Backup("Drive D");
        var photos = Backup("Backups", "Photos");
        var music = Backup("Backups", "Music");
        Backup("A", "B", "Too deep");
        Directory.CreateDirectory(Path.Combine(photos, "Files", "Not a backup"));

        var found = FileBackupDrives.FindBackups(_top);

        CollectionAssert.AreEquivalent(new[] { top, photos, music }, found);
        Assert.AreEqual(_backups, FileBackupDrives.FolderFor(_top, found), "The new backup goes where most of the others are.");
        Assert.AreEqual(Path.Combine(_top, "Backups"), FileBackupDrives.FolderFor(_top, []), "A drive without backups gets a Backups folder.");
    }

    [TestMethod]
    public void DrivesWithBackupsAreOfferedFirstThenEmptyExternalOnes()
    {
        var here = Path.GetPathRoot(_top)!; // the drive that is being backed up
        var drives = new[]
        {
            new BackupDriveFacts("stick", "USB DISK", External: true, System: false, FreeBytes: 50, Backups: []),
            new BackupDriveFacts("big stick", "BIG USB", External: true, System: false, FreeBytes: 500, Backups: []),
            new BackupDriveFacts("inside", "Data", External: false, System: false, FreeBytes: 900, Backups: []),
            new BackupDriveFacts("passport", "My Passport", External: true, System: false, FreeBytes: 10,
                Backups: [new BackupOnDrive(Path.Combine("passport", "Backups", "Photos"), "Photos"), new BackupOnDrive(Path.Combine("passport", "Backups", "Music"), "Music")]),
            new BackupDriveFacts("second", "Second disk", External: false, System: false, FreeBytes: 10, Backups: [new BackupOnDrive(Path.Combine("second", "Old"), "Old backup")]),
            new BackupDriveFacts(here, "This one", External: true, System: false, FreeBytes: 999, Backups: [new BackupOnDrive(Path.Combine(here, "Backups", "X"), "X")])
        };

        var offered = FileBackupDrives.Suggest(drives, Path.Combine(_original, "a.txt"));

        CollectionAssert.AreEqual(new[] { "My Passport", "Second disk", "BIG USB", "USB DISK" }, offered.Select(drive => drive.Label).ToArray(),
            "With backups first (most first), then external drives without, the roomiest first. Not the built-in drive without backups, and never the drive being backed up.");
        Assert.IsTrue(offered[0].HasBackups);
        Assert.AreEqual(2, offered[0].Backups);
        CollectionAssert.AreEqual(new[] { "Photos", "Music" }, offered[0].Names.ToArray());
        StringAssert.EndsWith(offered[0].Folder, Path.Combine("passport", "Backups"), "Beside the backups that are there.");
        Assert.IsFalse(offered[2].HasBackups);
        StringAssert.EndsWith(offered[2].Folder, Path.Combine("big stick", "Backups"));
        Assert.AreEqual("My Passport (E:)", new BackupDriveSuggestion(@"E:\", "My Passport", @"E:\Backups", 2, 0, []).Title);
    }

    [TestMethod]
    public void TheWindowsDriveIsOnlyOfferedWhenItAlreadyHoldsBackups()
    {
        var plain = new BackupDriveFacts("windows", "Windows", External: true, System: true, FreeBytes: 100, Backups: []);
        var used = plain with { Backups = [new BackupOnDrive(Path.Combine("windows", "Backups", "Photos"), "Photos")] };

        Assert.AreEqual(0, FileBackupDrives.Suggest([plain], null).Count);
        Assert.AreEqual(1, FileBackupDrives.Suggest([used], null).Count);
        Assert.AreEqual(FileBackupDrives.Most, FileBackupDrives.Suggest(
            Enumerable.Range(0, 9).Select(index => new BackupDriveFacts("stick " + index, "USB " + index, true, false, index, [])), null).Count,
            "A handful of buttons, not a list.");
    }

    [TestMethod]
    public void AFolderClearspaceOfferedIsMadeAndOneThatWasTypedIsNot()
    {
        Write("a.txt", "one");
        var offered = Path.Combine(_top, "New drive", "Backups");
        Directory.CreateDirectory(Path.Combine(_top, "New drive"));

        var typed = Plan(FileBackupType.Clone, change => change.Destination = offered);
        StringAssert.Contains(FileBackupRules.Prepare(typed, [], isNew: true), "is not there");
        Assert.IsFalse(Directory.Exists(offered));

        var clicked = Plan(FileBackupType.Clone, change => change.Destination = offered);
        Assert.IsNull(FileBackupRules.Prepare(clicked, [], isNew: true, createDestination: true));
        Assert.IsTrue(Directory.Exists(offered));
    }

    // ------------------------------------------------------------------ Clone

    [TestMethod]
    public void ACloneMakesTheBackupTheSameAsTheOriginal()
    {
        Write("a.txt", "one");
        Write(Path.Combine("Holiday", "beach.jpg"), "sand");
        Directory.CreateDirectory(Path.Combine(_original, "Empty"));
        var plan = Plan(FileBackupType.Clone);

        var first = Run(plan);
        Assert.AreEqual(FileBackupState.Ok, first.Outcome, first.Message);
        Assert.AreEqual(2, first.Copied);
        Assert.AreEqual("one", Read(plan.FilesFolder, "a.txt"));
        Assert.AreEqual("sand", Read(plan.FilesFolder, "Holiday", "beach.jpg"));
        Assert.IsTrue(Directory.Exists(Path.Combine(plan.FilesFolder, "Empty")), "An empty folder is copied too.");
        Assert.AreEqual(File.GetLastWriteTimeUtc(Path.Combine(_original, "a.txt")), File.GetLastWriteTimeUtc(Path.Combine(plan.FilesFolder, "a.txt")),
            "The copy keeps the file's date; that is how a later run knows it is unchanged.");

        var second = Run(plan, Noon.AddHours(1));
        Assert.AreEqual(FileBackupState.Nothing, second.Outcome);
        Assert.AreEqual(0, second.Copied);

        Write("a.txt", "one, changed", minutes: 10);
        File.Delete(Path.Combine(_original, "Holiday", "beach.jpg"));
        Write("new.txt", "new");
        var third = Run(plan, Noon.AddHours(2));
        Assert.AreEqual(FileBackupState.Ok, third.Outcome, third.Message);
        Assert.AreEqual(2, third.Copied);
        Assert.AreEqual(1, third.Removed);
        Assert.AreEqual("one, changed", Read(plan.FilesFolder, "a.txt"));
        Assert.IsFalse(There(plan.FilesFolder, "Holiday", "beach.jpg"), "Gone from the original, gone from the clone.");
        Assert.IsFalse(Directory.Exists(plan.HistoryFolder), "This plan keeps nothing.");
    }

    [TestMethod]
    public void ACloneWithTheSafetyNetKeepsWhatItRemoves()
    {
        Write("a.txt", "one");
        Write(Path.Combine("Holiday", "beach.jpg"), "sand");
        Write(Path.Combine("Old", "x.txt"), "x");
        var plan = Plan(FileBackupType.Clone, change => change.KeepRemoved = true);
        Run(plan);

        File.Delete(Path.Combine(_original, "Holiday", "beach.jpg"));
        Directory.Delete(Path.Combine(_original, "Old"), recursive: true);
        Write("a.txt", "one, changed", minutes: 10);
        var result = Run(plan, Noon.AddHours(1));

        Assert.AreEqual(2, result.SetAside, "The removed file and the file in the removed folder.");
        var kept = Path.Combine(plan.HistoryFolder, FileBackupLayout.Stamp(Noon.AddHours(1)));
        Assert.AreEqual("sand", Read(kept, "Holiday", "beach.jpg"));
        Assert.AreEqual("x", Read(kept, "Old", "x.txt"));
        Assert.IsFalse(There(plan.FilesFolder, "Holiday", "beach.jpg"));
        Assert.IsFalse(Directory.Exists(Path.Combine(plan.FilesFolder, "Old")));
        Assert.IsFalse(There(kept, "a.txt"), "A changed file is not kept: this plan only keeps removed ones.");
        Assert.AreEqual("one, changed", Read(plan.FilesFolder, "a.txt"));
    }

    [TestMethod]
    public void FileHistoryKeepsTheVersionAFileHadBeforeItChanged()
    {
        Write("report.txt", "first draft");
        var plan = Plan(FileBackupType.Clone, change => change.KeepVersions = true);
        Run(plan);

        Write("report.txt", "second draft", minutes: 10);
        var result = Run(plan, Noon.AddHours(1));

        Assert.AreEqual(1, result.SetAside);
        Assert.AreEqual("second draft", Read(plan.FilesFolder, "report.txt"));
        Assert.AreEqual("first draft", Read(plan.HistoryFolder, FileBackupLayout.Stamp(Noon.AddHours(1)), "report.txt"));

        // A run in which nothing changed leaves no empty dated folder behind.
        Run(plan, Noon.AddHours(2));
        Assert.IsFalse(Directory.Exists(Path.Combine(plan.HistoryFolder, FileBackupLayout.Stamp(Noon.AddHours(2)))));
    }

    [TestMethod]
    public void AnEarlierVersionGoesBackWhenTheNewOneCouldNotBeCopied()
    {
        // report.txt changed and was set aside, but the copy could not read the new one (open in a program):
        // the backup must still have the file, as it was.
        var files = Path.Combine(_top, "Files");
        var history = Path.Combine(_top, "Earlier", "2026-10-06 12.00.00");
        Directory.CreateDirectory(Path.Combine(history, "Work"));
        Directory.CreateDirectory(Path.Combine(files, "Work"));
        File.WriteAllText(Path.Combine(history, "Work", "report.txt"), "earlier");
        File.WriteAllText(Path.Combine(history, "copied.txt"), "earlier");
        File.WriteAllText(Path.Combine(files, "copied.txt"), "new");
        var result = new FileBackupResult { SetAside = 2 };

        FileBackupEngine.PutBackUncopied(files, history, [Path.Combine("Work", "report.txt"), "copied.txt"], result);

        Assert.AreEqual("earlier", Read(files, "Work", "report.txt"));
        Assert.AreEqual(1, result.SetAside);
        Assert.AreEqual("earlier", Read(history, "copied.txt"), "A file that was copied keeps its earlier version.");
        Assert.IsFalse(Directory.Exists(Path.Combine(history, "Work")), "No empty folder is left behind.");
    }

    [TestMethod]
    public void AddAndUpdateNeverRemovesAnything()
    {
        Write("a.txt", "one");
        Write("b.txt", "two");
        var plan = Plan(FileBackupType.AddAndUpdate);
        Run(plan);

        File.Delete(Path.Combine(_original, "b.txt"));
        Write("a.txt", "one, changed", minutes: 10);
        var result = Run(plan, Noon.AddHours(1));

        Assert.AreEqual(0, result.Removed);
        Assert.AreEqual("two", Read(plan.FilesFolder, "b.txt"), "Deleted from the original, still in the backup.");
        Assert.AreEqual("one, changed", Read(plan.FilesFolder, "a.txt"));
    }

    [TestMethod]
    public void ACloneRefusesToEmptyTheBackupWhenTheOriginalIsEmpty()
    {
        Write("a.txt", "one");
        var plan = Plan(FileBackupType.Clone);
        Run(plan);

        File.Delete(Path.Combine(_original, "a.txt"));
        var result = Run(plan, Noon.AddHours(1));

        Assert.AreEqual(FileBackupState.Failed, result.Outcome);
        StringAssert.Contains(result.Message, "is empty now");
        Assert.AreEqual("one", Read(plan.FilesFolder, "a.txt"));
    }

    [TestMethod]
    public void AnOriginalWithOnlyWindowsOwnFoldersCountsAsEmpty()
    {
        // A drive that was formatted, or swapped for another under the same letter, still has these.
        Write("a.txt", "one");
        var plan = Plan(FileBackupType.Clone, change => change.KeepRemoved = true);
        Run(plan);

        File.Delete(Path.Combine(_original, "a.txt"));
        Directory.CreateDirectory(Path.Combine(_original, "System Volume Information"));
        Directory.CreateDirectory(Path.Combine(_original, "$RECYCLE.BIN"));
        var result = Run(plan, Noon.AddHours(1));

        Assert.AreEqual(FileBackupState.Failed, result.Outcome);
        Assert.AreEqual("one", Read(plan.FilesFolder, "a.txt"));
        Assert.IsFalse(Directory.Exists(plan.HistoryFolder), "Nothing was moved aside either.");
    }

    [TestMethod]
    public void NamesThatAreLeftOutAreNeitherCopiedNorRemoved()
    {
        Write("a.txt", "one");
        Write(Path.Combine("node_modules", "big.js"), "js");
        Write("draft.tmp", "tmp");
        var plan = Plan(FileBackupType.Clone, change => change.Exclude = ["node_modules", "*.tmp"]);
        Run(plan);

        Assert.IsTrue(There(plan.FilesFolder, "a.txt"));
        Assert.IsFalse(Directory.Exists(Path.Combine(plan.FilesFolder, "node_modules")));
        Assert.IsFalse(There(plan.FilesFolder, "draft.tmp"));

        // Something of a left-out name that is in the backup already (from before the name was left out) stays.
        File.WriteAllText(Path.Combine(plan.FilesFolder, "notes.tmp"), "kept");
        Write("b.txt", "two");
        Run(plan, Noon.AddHours(1));
        Assert.IsTrue(There(plan.FilesFolder, "notes.tmp"));
    }

    [TestMethod]
    public void AFileThatBecameAFolderIsReplaced()
    {
        Write("thing", "a file");
        var plan = Plan(FileBackupType.Clone, change => change.KeepVersions = true);
        Run(plan);

        File.Delete(Path.Combine(_original, "thing"));
        Write(Path.Combine("thing", "inside.txt"), "now a folder");
        var result = Run(plan, Noon.AddHours(1));

        Assert.AreEqual(FileBackupState.Ok, result.Outcome, result.Message);
        Assert.AreEqual("now a folder", Read(plan.FilesFolder, "thing", "inside.txt"));
        Assert.AreEqual("a file", Read(plan.HistoryFolder, FileBackupLayout.Stamp(Noon.AddHours(1)), "thing"), "The file it was is kept as an earlier version.");
    }

    [TestMethod]
    public void EarlierVersionsAreDeletedWhenTheyAreOlderThanThePlanKeepsThem()
    {
        var plan = Plan(FileBackupType.Clone, change => { change.KeepVersions = true; change.KeepDays = 30; });
        foreach (var days in new[] { 45, 31, 29, 1 })
            Directory.CreateDirectory(Path.Combine(plan.HistoryFolder, FileBackupLayout.Stamp(Noon.AddDays(-days))));
        Directory.CreateDirectory(Path.Combine(plan.HistoryFolder, "Not a date"));

        Assert.AreEqual(2, FileBackupEngine.PruneHistory(plan, Noon));
        Assert.AreEqual(2, FileBackupEngine.History(plan).Count);
        Assert.IsTrue(Directory.Exists(Path.Combine(plan.HistoryFolder, "Not a date")), "Only the backup's own dated folders are ever deleted.");

        plan.KeepDays = 0; // for good
        Assert.AreEqual(0, FileBackupEngine.PruneHistory(plan, Noon.AddYears(5)));
    }

    [TestMethod]
    public void OneFileCanBeBackedUpByItself()
    {
        var file = Write("report.docx", "first");
        Write("other.docx", "not part of it");
        var plan = Plan(FileBackupType.Clone, change => { change.Source = file; change.SourceIsFile = true; change.KeepVersions = true; });

        Assert.AreEqual(1, Run(plan).Copied);
        Assert.AreEqual("first", Read(plan.FilesFolder, "report.docx"));
        Assert.IsFalse(There(plan.FilesFolder, "other.docx"));

        Write("report.docx", "second!", minutes: 10);
        var result = Run(plan, Noon.AddHours(1));
        Assert.AreEqual(1, result.Copied);
        Assert.AreEqual("second!", Read(plan.FilesFolder, "report.docx"));
        Assert.AreEqual("first", Read(plan.HistoryFolder, FileBackupLayout.Stamp(Noon.AddHours(1)), "report.docx"));
    }

    [TestMethod]
    public void ABackupWaitsWhileItsDriveIsNotThere()
    {
        Write("a.txt", "one");
        var gone = Plan(FileBackupType.Clone, change => change.Destination = Path.Combine(_top, "Not plugged in"));
        Assert.AreEqual(FileBackupState.Waiting, Run(gone).Outcome);
        Assert.IsFalse(Directory.Exists(gone.Destination), "The missing drive's folder is not made up on the spot.");

        var noOriginal = Plan(FileBackupType.Clone, change => change.Source = Path.Combine(_top, "Not there"));
        Assert.AreEqual(FileBackupState.Waiting, Run(noOriginal).Outcome);
    }

    // ------------------------------------------------------------------ Versions

    [TestMethod]
    public void EveryRunWithChangesMakesAVersionAndARunWithoutMakesNone()
    {
        Write("a.txt", "one");
        Write(Path.Combine("Holiday", "beach.jpg"), "sand");
        var plan = Plan(FileBackupType.Versions);

        var first = Run(plan);
        Assert.AreEqual(FileBackupState.Ok, first.Outcome, first.Message);
        Assert.IsTrue(first.Made);
        Assert.AreEqual(2, first.Copied);
        var one = Path.Combine(plan.Root, FileBackupLayout.Stamp(Noon));
        Assert.AreEqual("sand", Read(one, "Holiday", "beach.jpg"));

        var second = Run(plan, Noon.AddHours(1));
        Assert.AreEqual(FileBackupState.Nothing, second.Outcome);
        Assert.IsFalse(second.Made);
        Assert.AreEqual(1, FileBackupEngine.Versions(plan.Root).Count);

        Write("a.txt", "one, changed", minutes: 10);
        var third = Run(plan, Noon.AddHours(2));
        Assert.IsTrue(third.Made);
        Assert.AreEqual(1, third.Copied, "Only the file that changed is copied.");
        Assert.AreEqual(1, third.Linked, "The other one is the same file as in the version before.");
        var two = Path.Combine(plan.Root, FileBackupLayout.Stamp(Noon.AddHours(2)));
        Assert.AreEqual("one, changed", Read(two, "a.txt"));
        Assert.AreEqual("sand", Read(two, "Holiday", "beach.jpg"), "A version is a full copy to look at.");
        Assert.AreEqual("one", Read(one, "a.txt"), "The version before still has the file as it was.");
        Assert.AreEqual(0, Directory.GetDirectories(plan.Root, "*" + FileBackupLayout.Partial).Length);
    }

    [TestMethod]
    public void ADeletedFileIsAChangeToo()
    {
        Write("a.txt", "one");
        Write("b.txt", "two");
        var plan = Plan(FileBackupType.Versions);
        Run(plan);

        File.Delete(Path.Combine(_original, "b.txt"));
        var result = Run(plan, Noon.AddHours(1));

        Assert.IsTrue(result.Made);
        Assert.AreEqual(0, result.Copied);
        Assert.IsFalse(There(plan.Root, FileBackupLayout.Stamp(Noon.AddHours(1)), "b.txt"));
        Assert.IsTrue(There(plan.Root, FileBackupLayout.Stamp(Noon), "b.txt"), "It is still in the version from before.");
    }

    [TestMethod]
    public void OnADriveWithoutHardLinksEveryVersionIsAFullCopy()
    {
        FileBackupNative.Override = (_, _) => false;
        Write("a.txt", "one");
        Write("b.txt", "two");
        var plan = Plan(FileBackupType.Versions);
        Run(plan);

        Write("a.txt", "one, changed", minutes: 10);
        var result = Run(plan, Noon.AddHours(1));

        Assert.AreEqual(2, result.Copied);
        Assert.AreEqual(0, result.Linked);
        Assert.AreEqual("two", Read(plan.Root, FileBackupLayout.Stamp(Noon.AddHours(1)), "b.txt"));
    }

    [TestMethod]
    public void OnlyTheNewestVersionsAreKept()
    {
        var plan = Plan(FileBackupType.Versions, change => change.KeepCount = 2);
        for (var run = 0; run < 4; run++)
        {
            Write("a.txt", "state " + run, minutes: run * 10);
            Run(plan, Noon.AddHours(run));
        }

        var versions = FileBackupEngine.Versions(plan.Root);
        Assert.AreEqual(2, versions.Count);
        Assert.AreEqual(Noon.AddHours(3), versions[0].When, "Newest first.");
        Assert.AreEqual("state 2", Read(versions[1].Path, "a.txt"));
    }

    [TestMethod]
    public void AVersionThatWasNotFinishedIsClearedAwayByTheNextRun()
    {
        Write("a.txt", "one");
        var plan = Plan(FileBackupType.Versions);
        Run(plan);

        var leftover = Path.Combine(plan.Root, FileBackupLayout.Stamp(Noon.AddMinutes(5)) + FileBackupLayout.Partial);
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "half.txt"), "half");
        Write("a.txt", "one, changed", minutes: 10);
        Run(plan, Noon.AddHours(1));

        Assert.IsFalse(Directory.Exists(leftover));
        Assert.AreEqual(2, FileBackupEngine.Versions(plan.Root).Count);
    }

    // ------------------------------------------------------------------ Zip archive

    [TestMethod]
    public void EveryRunWithChangesMakesAZip()
    {
        Write("a.txt", "one");
        Write(Path.Combine("Holiday", "beach.jpg"), "sand");
        Directory.CreateDirectory(Path.Combine(_original, "Empty"));
        var plan = Plan(FileBackupType.Zip, change => change.KeepCount = 2);

        var first = Run(plan);
        Assert.AreEqual(FileBackupState.Ok, first.Outcome, first.Message);
        var path = Path.Combine(plan.Root, FileBackupLayout.ZipName("Photos", Noon));
        Assert.AreEqual(path, first.MadePath);
        using (var zip = ZipFile.OpenRead(path))
        {
            CollectionAssert.AreEquivalent(new[] { "a.txt", "Holiday/", "Holiday/beach.jpg", "Empty/" }, zip.Entries.Select(entry => entry.FullName).ToArray());
            using var reader = new StreamReader(zip.GetEntry("Holiday/beach.jpg")!.Open());
            Assert.AreEqual("sand", reader.ReadToEnd());
        }

        Assert.AreEqual(FileBackupState.Nothing, Run(plan, Noon.AddHours(1)).Outcome);
        Assert.AreEqual(1, FileBackupEngine.Zips(plan).Count);

        for (var run = 2; run < 5; run++)
        {
            Write("a.txt", "state " + run, minutes: run * 10);
            Run(plan, Noon.AddHours(run));
        }

        Assert.AreEqual(2, FileBackupEngine.Zips(plan).Count, "Only the newest two are kept.");
        Assert.AreEqual(0, Directory.GetFiles(plan.Root, "*" + FileBackupLayout.Part).Length);
    }

    // ------------------------------------------------------------------ NEW (resumable zip): a zip that is carried on with

    private static readonly DateTime Packed = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private string Unfinished => Path.Combine(_backups, "Photos 2026-10-06 12.00.00.zip" + FileBackupLayout.Part);
    private string Finished => Path.Combine(_backups, "Photos 2026-10-06 12.00.00.zip");

    private static void Pack(FileBackupZipWriter zip, string name, string text, DateTime? modifiedUtc = null)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var modified = modifiedUtc ?? Packed;
        zip.Add(name, new MemoryStream(bytes), bytes.Length, modified, FileBackupEngine.ZipDate(modified), CancellationToken.None);
    }

    private static bool Kept(FileBackupZipWriter zip, string name, string text, DateTime? modifiedUtc = null)
        => zip.Keep(name, System.Text.Encoding.UTF8.GetByteCount(text), modifiedUtc ?? Packed);

    // What a zip holds: every name with its text ("" for a folder).
    private static Dictionary<string, string> Unpack(string path)
    {
        var holds = new Dictionary<string, string>();
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            holds.Add(entry.FullName, reader.ReadToEnd());
        }

        return holds;
    }

    private static void AssertHolds(string path, params (string Name, string Text)[] expected)
    {
        var holds = Unpack(path);
        CollectionAssert.AreEquivalent(expected.Select(one => one.Name).ToArray(), holds.Keys.ToArray());
        foreach (var (name, text) in expected)
            Assert.AreEqual(text, holds[name], name);
    }

    // A file that gives some of itself and then cannot be read any further (or stops the run).
    private sealed class BreaksOff(byte[] bytes, int after, Action then) : MemoryStream(bytes)
    {
        private bool _done;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position < after)
                return base.Read(buffer, offset, (int)Math.Min(count, after - Position));

            if (!_done)
            {
                _done = true;
                then();
            }

            return base.Read(buffer, offset, count);
        }
    }

    [TestMethod]
    public void AZipWrittenInStretchesIsAnOrdinaryZip()
    {
        using (var zip = FileBackupZipWriter.Start(Unfinished))
        {
            Pack(zip, "a.txt", "one");
            zip.AddFolder("Holiday/");
            Pack(zip, "Holiday/beach.jpg", "sand");
            zip.Save();
            zip.Save(); // nothing new: nothing happens
            zip.AddFolder("Empty/");
            Pack(zip, "big.txt", new string('x', 300_000));
            zip.Save();
            Pack(zip, "last.txt", "the end");

            Assert.IsTrue(File.Exists(FileBackupZipWriter.DirPath(Unfinished)) && File.Exists(FileBackupZipWriter.LogPath(Unfinished)));
            Assert.AreEqual(6, zip.Finish(Finished));
        }

        AssertHolds(Finished, ("a.txt", "one"), ("Holiday/", ""), ("Holiday/beach.jpg", "sand"), ("Empty/", ""), ("big.txt", new string('x', 300_000)), ("last.txt", "the end"));
        Assert.AreEqual(0, Directory.GetFiles(_backups, "*" + FileBackupLayout.Part).Length, "Nothing is left beside the finished zip.");
        using var read = ZipFile.OpenRead(Finished);
        Assert.AreEqual(Packed, read.GetEntry("a.txt")!.LastWriteTime.UtcDateTime);
    }

    [TestMethod]
    public void AZipThatWasCutShortIsCarriedOnWith()
    {
        var zip = FileBackupZipWriter.Start(Unfinished);
        Pack(zip, "a.txt", "one");
        zip.AddFolder("Holiday/");
        Pack(zip, "Holiday/beach.jpg", "sand");
        zip.Save();
        Pack(zip, "c.txt", "packed, but the power went before it was made safe");
        zip.Cut();

        // What a power cut can leave after the last save: more in every file than the notes say.
        File.AppendAllText(Unfinished, "half a packed file");
        File.AppendAllText(FileBackupZipWriter.DirPath(Unfinished), "half a line");
        File.AppendAllText(FileBackupZipWriter.LogPath(Unfinished), "F\t12\t34\t9999");

        using (var again = FileBackupZipWriter.Resume(Unfinished)!)
        {
            Assert.IsNotNull(again);
            Assert.AreEqual(3, again.Carried);
            Assert.IsTrue(Kept(again, "a.txt", "one"));
            Assert.IsTrue(again.KeepFolder("Holiday/"));
            Assert.IsTrue(Kept(again, "Holiday/beach.jpg", "sand"));
            Assert.IsFalse(Kept(again, "c.txt", "packed, but the power went before it was made safe"), "It was not made safe, so it is packed again.");
            Pack(again, "c.txt", "packed, but the power went before it was made safe");
            Pack(again, "d.txt", "new");
            again.Finish(Finished);
        }

        AssertHolds(Finished, ("a.txt", "one"), ("Holiday/", ""), ("Holiday/beach.jpg", "sand"), ("c.txt", "packed, but the power went before it was made safe"), ("d.txt", "new"));
    }

    [TestMethod]
    public void AZipCanBeCutShortMoreThanOnce()
    {
        var first = FileBackupZipWriter.Start(Unfinished);
        Pack(first, "a.txt", "one");
        first.Save();
        first.Cut();

        var second = FileBackupZipWriter.Resume(Unfinished)!;
        Assert.IsTrue(Kept(second, "a.txt", "one"));
        Pack(second, "b.txt", "two");
        second.Save();
        Pack(second, "c.txt", "three");
        second.Cut();

        using (var third = FileBackupZipWriter.Resume(Unfinished)!)
        {
            Assert.AreEqual(2, third.Carried);
            Assert.IsTrue(Kept(third, "a.txt", "one") && Kept(third, "b.txt", "two"));
            Pack(third, "c.txt", "three");
            third.Finish(Finished);
        }

        AssertHolds(Finished, ("a.txt", "one"), ("b.txt", "two"), ("c.txt", "three"));
    }

    [TestMethod]
    public void OnlyWhatIsStillTheSameStaysInAZipThatIsCarriedOnWith()
    {
        var zip = FileBackupZipWriter.Start(Unfinished);
        Pack(zip, "same.txt", "same");
        Pack(zip, "changed.txt", "before");
        Pack(zip, "newer.txt", "same size", Packed);
        Pack(zip, "deleted.txt", "gone from the folder since");
        zip.AddFolder("Deleted folder/");
        zip.Save();
        zip.Cut();

        using (var again = FileBackupZipWriter.Resume(Unfinished)!)
        {
            Assert.IsTrue(Kept(again, "same.txt", "same"));
            Assert.IsFalse(Kept(again, "changed.txt", "after, and longer"));
            Pack(again, "changed.txt", "after, and longer");
            Assert.IsFalse(Kept(again, "newer.txt", "same size", Packed.AddMinutes(5)), "The same size with another date is another file.");
            Pack(again, "newer.txt", "SAME SIZE", Packed.AddMinutes(5));
            Assert.IsFalse(again.KeepFolder("same.txt"), "A file is not a folder.");
            // deleted.txt and its folder are not in the folder any more: the run never asks for them
            Assert.AreEqual(3, again.Finish(Finished));
        }

        AssertHolds(Finished, ("same.txt", "same"), ("changed.txt", "after, and longer"), ("newer.txt", "SAME SIZE"));
    }

    [TestMethod]
    public void AnUnfinishedZipThatIsNotWhatItsNotesSayIsNotCarriedOnWith()
    {
        void Make(bool save = true)
        {
            var zip = FileBackupZipWriter.Start(Unfinished);
            Pack(zip, "a.txt", "one");
            Pack(zip, "b.txt", "two");
            if (save)
                zip.Save();
            zip.Cut();
        }

        Assert.IsNull(FileBackupZipWriter.Resume(Unfinished), "There is none.");

        Make(save: false);
        Assert.IsNull(FileBackupZipWriter.Resume(Unfinished), "Nothing in it was made safe.");

        Make();
        File.Delete(FileBackupZipWriter.DirPath(Unfinished));
        Assert.IsNull(FileBackupZipWriter.Resume(Unfinished), "Its list of contents is gone.");

        Make();
        using (var data = new FileStream(Unfinished, FileMode.Open))
            data.SetLength(data.Length - 1);
        Assert.IsNull(FileBackupZipWriter.Resume(Unfinished), "The zip is shorter than the notes say.");

        Make();
        File.WriteAllBytes(FileBackupZipWriter.DirPath(Unfinished), new byte[new FileInfo(FileBackupZipWriter.DirPath(Unfinished)).Length]);
        Assert.IsNull(FileBackupZipWriter.Resume(Unfinished), "Its list of contents is not one.");

        Make();
        File.WriteAllText(FileBackupZipWriter.LogPath(Unfinished), "Some other notes\n" + File.ReadAllText(FileBackupZipWriter.LogPath(Unfinished)));
        Assert.IsNull(FileBackupZipWriter.Resume(Unfinished), "The notes are not Clearspace's.");

        Make();
        using (var fine = FileBackupZipWriter.Resume(Unfinished))
            Assert.IsNotNull(fine);

        FileBackupZipWriter.Discard(Unfinished);
        Assert.AreEqual(0, Directory.GetFiles(_backups).Length);
    }

    [TestMethod]
    public void AFileThatCannotBeReadToItsEndIsLeftOutAndPackingGoesOn()
    {
        using (var zip = FileBackupZipWriter.Start(Unfinished))
        {
            Pack(zip, "a.txt", "one");
            var bytes = new byte[200_000];
            Random.Shared.NextBytes(bytes);
            var broken = new BreaksOff(bytes, after: 150_000, () => throw new IOException("The drive did not answer."));
            var error = Assert.ThrowsException<FileBackupUnreadableException>(() => zip.Add("broken.bin", broken, bytes.Length, Packed, FileBackupEngine.ZipDate(Packed), CancellationToken.None));
            Assert.AreEqual("The drive did not answer.", error.Message);

            var cut = new FileInfo(Unfinished).Length;
            Pack(zip, "b.txt", "two");
            zip.Save();
            Assert.IsTrue(new FileInfo(Unfinished).Length < cut + 1000, "What was packed of the broken file is cut off again, not carried along.");
            Assert.AreEqual(2, zip.Finish(Finished));
        }

        AssertHolds(Finished, ("a.txt", "one"), ("b.txt", "two"));
    }

    [TestMethod]
    public void StoppingInTheMiddleOfAFileKeepsEverythingBeforeIt()
    {
        using var stop = new CancellationTokenSource();
        var zip = FileBackupZipWriter.Start(Unfinished);
        Pack(zip, "a.txt", "one");
        Pack(zip, "b.txt", "two"); // not saved yet: closing the writer saves it
        var bytes = new byte[200_000];
        var huge = new BreaksOff(bytes, after: 100_000, stop.Cancel);
        Assert.ThrowsException<OperationCanceledException>(() => zip.Add("huge.bin", huge, bytes.Length, Packed, FileBackupEngine.ZipDate(Packed), stop.Token));
        zip.Dispose();

        using (var again = FileBackupZipWriter.Resume(Unfinished)!)
        {
            Assert.AreEqual(2, again.Carried);
            Assert.IsTrue(Kept(again, "a.txt", "one") && Kept(again, "b.txt", "two"));
            Assert.IsFalse(again.Keep("huge.bin", bytes.Length, Packed));
            again.Finish(Finished);
        }

        AssertHolds(Finished, ("a.txt", "one"), ("b.txt", "two"));
    }

    [TestMethod]
    public void AZipCanHoldMoreFilesThanAnOldZipCanCount()
    {
        const int files = 70_000; // an old zip counts to 65,535
        var zip = FileBackupZipWriter.Start(Unfinished);
        for (var index = 0; index < 40_000; index++)
            Pack(zip, $"Folder {index % 50}/file {index}.txt", index.ToString(CultureInfo.InvariantCulture));
        zip.Save();
        zip.Cut();

        using (var again = FileBackupZipWriter.Resume(Unfinished)!)
        {
            Assert.AreEqual(40_000, again.Carried);
            for (var index = 0; index < files; index++)
            {
                var name = $"Folder {index % 50}/file {index}.txt";
                var text = index.ToString(CultureInfo.InvariantCulture);
                if (!Kept(again, name, text))
                    Pack(again, name, text);
            }

            Assert.AreEqual(files, again.Finish(Finished));
        }

        using var read = ZipFile.OpenRead(Finished);
        Assert.AreEqual(files, read.Entries.Count);
        using var reader = new StreamReader(read.GetEntry("Folder 49/file 69999.txt")!.Open());
        Assert.AreEqual("69999", reader.ReadToEnd());
    }

    [TestMethod]
    public void TheEndOfAZipSaysWhereItsListOfContentsIs()
    {
        var small = new MemoryStream();
        FileBackupZipWriter.WriteEnd(small, count: 3, dirSize: 150, dirOffset: 1000);
        var end = small.ToArray();
        Assert.AreEqual(22, end.Length);
        Assert.AreEqual(0x06054b50u, BitConverter.ToUInt32(end, 0));
        Assert.AreEqual((ushort)3, BitConverter.ToUInt16(end, 10));
        Assert.AreEqual(150u, BitConverter.ToUInt32(end, 12));
        Assert.AreEqual(1000u, BitConverter.ToUInt32(end, 16));

        // Past 4 GB (or 65,535 files) the numbers go in a longer record in front, and the short one says "see there".
        var large = new MemoryStream();
        FileBackupZipWriter.WriteEnd(large, count: 3, dirSize: 150, dirOffset: 5_000_000_000);
        end = large.ToArray();
        Assert.AreEqual(56 + 20 + 22, end.Length);
        Assert.AreEqual(0x06064b50u, BitConverter.ToUInt32(end, 0));
        Assert.AreEqual(3L, BitConverter.ToInt64(end, 32));
        Assert.AreEqual(150L, BitConverter.ToInt64(end, 40));
        Assert.AreEqual(5_000_000_000L, BitConverter.ToInt64(end, 48));
        Assert.AreEqual(0x07064b50u, BitConverter.ToUInt32(end, 56));
        Assert.AreEqual(5_000_000_150L, BitConverter.ToInt64(end, 64), "Where the longer record starts: right after the list.");
        Assert.AreEqual(0x06054b50u, BitConverter.ToUInt32(end, 76));
        Assert.AreEqual((ushort)3, BitConverter.ToUInt16(end, 86), "A number that fits is still written.");
        Assert.AreEqual(uint.MaxValue, BitConverter.ToUInt32(end, 92), "The one that does not fit says so.");
    }

    [TestMethod]
    public void ARunThatIsStoppedIsCarriedOnWithByTheNextRun()
    {
        Write("a.txt", "one");
        Write("b.txt", "two");
        Write("c.txt", "three");
        Write("d.txt", "four");
        Write(Path.Combine("Holiday", "beach.jpg"), "sand");
        var plan = Plan(FileBackupType.Zip);

        using var stop = new CancellationTokenSource();
        var stopped = FileBackupEngine.Run(new FileBackupRun
        {
            Plan = plan,
            NowLocal = Noon,
            UseRobocopy = false,
            Token = stop.Token,
            Progress = progress =>
            {
                if (progress.Phase == "Packing" && progress.Files == 3)
                    stop.Cancel();
            }
        });

        Assert.AreEqual(FileBackupState.Stopped, stopped.Outcome, stopped.Message);
        Assert.AreEqual(0, FileBackupEngine.Zips(plan).Count, "No zip that is not whole is ever offered.");
        Assert.AreEqual(3, Directory.GetFiles(plan.Root, "*" + FileBackupLayout.Part).Length, "The unfinished zip, its list of contents and its notes.");

        // Before the next run: one packed file changes, one is deleted, one is new.
        Write("a.txt", "one, changed", minutes: 10);
        File.Delete(Path.Combine(_original, "b.txt"));
        Write("e.txt", "five");

        var report = Path.Combine(_top, "report.txt");
        var packed = new List<string>();
        var result = FileBackupEngine.Run(new FileBackupRun { Plan = plan, NowLocal = Noon.AddHours(1), UseRobocopy = false, LogPath = report, Progress = progress => packed.Add(progress.Current) });

        Assert.AreEqual(FileBackupState.Ok, result.Outcome, result.Message);
        Assert.AreEqual(Path.Combine(plan.Root, FileBackupLayout.ZipName("Photos", Noon.AddHours(1))), result.MadePath, "The zip is named for the run that finished it.");
        AssertHolds(result.MadePath!, ("a.txt", "one, changed"), ("c.txt", "three"), ("d.txt", "four"), ("e.txt", "five"), ("Holiday/", ""), ("Holiday/beach.jpg", "sand"));
        Assert.AreEqual(5, result.Copied, "Every file in the zip counts, whichever run packed it.");
        Assert.AreEqual(0, Directory.GetFiles(plan.Root, "*" + FileBackupLayout.Part).Length);
        StringAssert.Contains(File.ReadAllText(report), "Carrying on with the zip");

        Assert.AreEqual(FileBackupState.Nothing, Run(plan, Noon.AddHours(2)).Outcome, "The finished zip is the folder as it is.");
    }

    [TestMethod]
    public void AnUnfinishedZipThatCannotBeCarriedOnWithIsMadeAgain()
    {
        Write("a.txt", "one");
        Write("b.txt", "two");
        var plan = Plan(FileBackupType.Zip);
        Run(plan);
        Write("b.txt", "two, changed", minutes: 10);

        // Left by an older Clearspace, or damaged: an unfinished zip without notes, and notes without a zip.
        var old = Path.Combine(plan.Root, FileBackupLayout.ZipName("Photos", Noon.AddMinutes(30)) + FileBackupLayout.Part);
        File.WriteAllText(old, "half a zip");
        File.WriteAllText(FileBackupZipWriter.LogPath(Path.Combine(plan.Root, "Photos 2026-10-01 08.00.00.zip" + FileBackupLayout.Part)), "notes");

        var result = Run(plan, Noon.AddHours(1));

        Assert.AreEqual(FileBackupState.Ok, result.Outcome, result.Message);
        AssertHolds(result.MadePath!, ("a.txt", "one"), ("b.txt", "two, changed"));
        Assert.AreEqual(0, Directory.GetFiles(plan.Root, "*" + FileBackupLayout.Part).Length, "What could not be used was cleared away.");
    }

    [TestMethod]
    public void AnUnfinishedZipIsClearedAwayWhenNothingIsLeftToPack()
    {
        Write("a.txt", "one");
        var plan = Plan(FileBackupType.Zip);
        Run(plan);

        // A run was cut short packing a change that has been undone since.
        var zip = FileBackupZipWriter.Start(Path.Combine(plan.Root, FileBackupLayout.ZipName("Photos", Noon.AddHours(1)) + FileBackupLayout.Part));
        Pack(zip, "a.txt", "one, changed");
        zip.Save();
        zip.Cut();

        Assert.AreEqual(FileBackupState.Nothing, Run(plan, Noon.AddHours(2)).Outcome);
        Assert.AreEqual(0, Directory.GetFiles(plan.Root, "*" + FileBackupLayout.Part).Length);
        Assert.AreEqual(1, FileBackupEngine.Zips(plan).Count);
    }

    [TestMethod]
    public void AZipSomeoneElsePutInTheFolderIsNeverThinnedOut()
    {
        Write("a.txt", "one");
        var plan = Plan(FileBackupType.Zip, change => change.KeepCount = 1);
        Run(plan);
        File.WriteAllText(Path.Combine(plan.Root, "Holiday.zip"), "not ours");

        Write("a.txt", "two!", minutes: 10);
        Run(plan, Noon.AddHours(1));

        Assert.IsTrue(There(plan.Root, "Holiday.zip"));
        Assert.AreEqual(1, FileBackupEngine.Zips(plan).Count);
    }

    // ------------------------------------------------------------------ restoring

    [TestMethod]
    public void RestoringBringsBackWhatIsMissingAndLeavesWhatIsThere()
    {
        Write("a.txt", "one");
        Write(Path.Combine("Holiday", "beach.jpg"), "sand");
        var plan = Plan(FileBackupType.Clone);
        Run(plan);

        File.Delete(Path.Combine(_original, "Holiday", "beach.jpg"));   // lost
        Write("a.txt", "one, worked on since", minutes: 30);             // newer than the backup
        Write("new.txt", "made after the backup");

        var point = FileBackupRestore.Points(plan).Single();
        Assert.AreEqual(FileBackupPointKind.Latest, point.Kind);
        var result = FileBackupRestore.Restore(plan, point, _original, replace: false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Restored);
        Assert.AreEqual(1, result.Left);
        Assert.AreEqual("sand", Read(_original, "Holiday", "beach.jpg"));
        Assert.AreEqual("one, worked on since", Read(_original, "a.txt"), "A file that is there is not replaced unless asked.");
        Assert.IsTrue(There(_original, "new.txt"), "Restoring never deletes.");

        result = FileBackupRestore.Restore(plan, point, _original, replace: true, null, CancellationToken.None);
        Assert.AreEqual(1, result.Restored);
        Assert.AreEqual(1, result.Same);
        Assert.AreEqual("one", Read(_original, "a.txt"));
    }

    [TestMethod]
    public void ARestoreCanGoToAnotherFolder()
    {
        Write("a.txt", "one");
        Write(Path.Combine("Holiday", "beach.jpg"), "sand");
        var plan = Plan(FileBackupType.Versions);
        Run(plan);
        var elsewhere = Path.Combine(_top, "Elsewhere");

        var result = FileBackupRestore.Restore(plan, FileBackupRestore.Points(plan).Single(), elsewhere, replace: false, null, CancellationToken.None);

        Assert.AreEqual(2, result.Restored);
        Assert.AreEqual("sand", Read(elsewhere, "Holiday", "beach.jpg"));
        Assert.ThrowsException<InvalidDataException>(() =>
            FileBackupRestore.Restore(plan, FileBackupRestore.Points(plan).Single(), Path.Combine(plan.Root, "x"), replace: false, null, CancellationToken.None));
    }

    [TestMethod]
    public void AZipIsRestoredLikeAFolder()
    {
        Write("a.txt", "one");
        Write(Path.Combine("Holiday", "beach.jpg"), "sand");
        Directory.CreateDirectory(Path.Combine(_original, "Empty"));
        var plan = Plan(FileBackupType.Zip);
        Run(plan);
        var elsewhere = Path.Combine(_top, "Elsewhere");

        var point = FileBackupRestore.Points(plan).Single();
        Assert.AreEqual(FileBackupPointKind.Zip, point.Kind);
        var result = FileBackupRestore.Restore(plan, point, elsewhere, replace: false, null, CancellationToken.None);

        Assert.AreEqual(2, result.Restored);
        Assert.AreEqual("sand", Read(elsewhere, "Holiday", "beach.jpg"));
        Assert.IsTrue(Directory.Exists(Path.Combine(elsewhere, "Empty")));

        result = FileBackupRestore.Restore(plan, point, elsewhere, replace: false, null, CancellationToken.None);
        Assert.AreEqual(0, result.Restored);
        Assert.AreEqual(2, result.Same, "Unpacked files keep their date, so a second restore finds them unchanged.");
    }

    [TestMethod]
    public void AZipEntryCannotLandOutsideTheFolder()
    {
        var folder = Path.Combine(_top, "Elsewhere");
        Assert.AreEqual(Path.Combine(folder, "Holiday", "beach.jpg"), FileBackupRestore.Inside(folder, "Holiday/beach.jpg"));
        Assert.IsNull(FileBackupRestore.Inside(folder, "../outside.txt"));
        Assert.IsNull(FileBackupRestore.Inside(folder, "Holiday/../../outside.txt"));
        Assert.IsNull(FileBackupRestore.Inside(folder, "C:/Windows/x.dll"));
        Assert.IsNull(FileBackupRestore.Inside(folder, "/etc/x"));
        Assert.IsNull(FileBackupRestore.Inside(folder, ""));
    }

    // ------------------------------------------------------------------ earlier versions of one file

    [TestMethod]
    public void EarlierVersionsOfAFileComeFromEveryBackupThatHasThem()
    {
        var file = Write("report.txt", "first");
        var history = Plan(FileBackupType.Clone, change => change.KeepVersions = true);
        var versions = Plan(FileBackupType.Versions, change => { change.Name = "Photos versions"; change.FolderName = "Photos versions"; });
        var zips = Plan(FileBackupType.Zip, change => { change.Name = "Photos zips"; change.FolderName = "Photos zips"; });
        var plans = new[] { history, versions, zips };
        foreach (var plan in plans)
            Run(plan);

        Assert.AreEqual(0, FileBackupRestore.VersionsOf(file, plans).Count, "Every backup holds the file as it is now: nothing earlier.");

        Write("report.txt", "second!", minutes: 10);
        foreach (var plan in plans)
            Run(plan, Noon.AddHours(1));
        Write("report.txt", "third!!!", minutes: 20);
        foreach (var plan in plans)
            Run(plan, Noon.AddHours(2));
        Write("report.txt", "fourth, not backed up yet", minutes: 30);

        var found = FileBackupRestore.VersionsOf(file, plans);
        CollectionAssert.AreEqual(new long[] { 8, 7, 5 }, found.Select(version => version.Size).ToArray(),
            "One line per state, newest first, although three backups hold each of them.");
        Assert.AreEqual(Old.AddMinutes(20), found[0].ModifiedUtc);

        Assert.IsTrue(FileBackupRestore.Offers(file, plans));
        Assert.IsFalse(FileBackupRestore.Offers(file, [Plan(FileBackupType.Clone)]), "A plain Clone keeps no history, so the menu has nothing to offer.");
        Assert.IsFalse(FileBackupRestore.Offers(Path.Combine(_top, "elsewhere.txt"), plans));
    }

    [TestMethod]
    public void AnEarlierVersionCanBePutBackFromAFolderOrAZip()
    {
        var file = Write("report.txt", "first");
        var versions = Plan(FileBackupType.Versions);
        var zips = Plan(FileBackupType.Zip, change => { change.Name = "Photos zips"; change.FolderName = "Photos zips"; });
        Run(versions);
        Run(zips);
        Write("report.txt", "second!", minutes: 10);

        var fromFolder = FileBackupRestore.VersionsOf(file, [versions]).Single();
        Assert.IsNull(fromFolder.ZipEntry);
        var copy = Path.Combine(_original, "copy.txt");
        FileBackupRestore.RestoreVersion(fromFolder, copy);
        Assert.AreEqual("first", File.ReadAllText(copy));
        Assert.AreEqual(Old, File.GetLastWriteTimeUtc(copy));

        var fromZip = FileBackupRestore.VersionsOf(file, [zips]).Single();
        Assert.AreEqual("report.txt", fromZip.ZipEntry);
        FileBackupRestore.RestoreVersion(fromZip, file);
        Assert.AreEqual("first", File.ReadAllText(file));
    }

    [TestMethod]
    public void ACopyOfAnEarlierVersionGetsANameThatSaysWhenItIsFrom()
    {
        var path = Path.Combine(_original, "report.docx");
        var when = new DateTime(2026, 10, 3, 14, 30, 0);
        Assert.AreEqual(Path.Combine(_original, "report (from 3 Oct 2026 14.30).docx"), FileBackupRestore.CopyName(path, when, _ => false));
        Assert.AreEqual(Path.Combine(_original, "report (from 3 Oct 2026 14.30) (2).docx"),
            FileBackupRestore.CopyName(path, when, candidate => candidate.EndsWith("14.30).docx", StringComparison.Ordinal)));
    }

    // ------------------------------------------------------------------ robocopy: what it is told, and how it answers

    [TestMethod]
    public void RobocopyIsToldToCopyAndNeverToRemove()
    {
        var plan = Plan(FileBackupType.Clone, change => change.Exclude = ["node_modules", "*.tmp"]);
        var log = Path.Combine(_top, "run.robocopy.log");
        var arguments = FileBackupRobocopy.Arguments(plan, plan.FilesFolder, FileBackupFilter.For(plan), log);

        Assert.AreEqual(_original, arguments[0]);
        Assert.AreEqual(plan.FilesFolder, arguments[1]);
        foreach (var expected in new[] { "/E", "/COPY:DAT", "/DCOPY:DAT", "/MT:8", "/XJ", "/FFT", "/R:1", "/W:1", "/XA:O", "/UNILOG:" + log })
            CollectionAssert.Contains(arguments, expected);
        foreach (var never in new[] { "/MIR", "/PURGE", "/MOVE", "/MOV" })
            CollectionAssert.DoesNotContain(arguments, never, "Removing is Clearspace's own job, with its own rules.");

        var leftOut = arguments.Skip(arguments.IndexOf("/XD") + 1).TakeWhile(argument => argument != "/XF").ToList();
        CollectionAssert.Contains(leftOut, "node_modules");
        CollectionAssert.Contains(leftOut, "*.tmp");
        CollectionAssert.Contains(leftOut, "System Volume Information");
        CollectionAssert.AreEqual(leftOut, arguments.Skip(arguments.IndexOf("/XF") + 1).ToList(), "The same names are left out as files and as folders.");

        var cloud = Plan(FileBackupType.Clone, change => change.IncludeCloudOnly = true);
        CollectionAssert.DoesNotContain(FileBackupRobocopy.Arguments(cloud, cloud.FilesFolder, FileBackupFilter.For(cloud), null), "/XA:O");
    }

    [TestMethod]
    public void RobocopyCopiesOneFileByNamingItsFolderAndTheFile()
    {
        var file = Path.Combine(_original, "report.docx");
        var plan = Plan(FileBackupType.Clone, change => { change.Source = file; change.SourceIsFile = true; });
        var arguments = FileBackupRobocopy.Arguments(plan, plan.FilesFolder, FileBackupFilter.For(plan), null);

        CollectionAssert.AreEqual(new[] { _original, plan.FilesFolder, "report.docx" }, arguments.Take(3).ToList());
        CollectionAssert.DoesNotContain(arguments, "/E");
        CollectionAssert.DoesNotContain(arguments, "/XD");
    }

    [TestMethod]
    public void RobocopysLogIsReadWithoutReadingItsWords()
    {
        var plan = Plan(FileBackupType.Clone);
        var file = Path.Combine(_original, "Holiday", "beach.jpg");

        Assert.AreEqual(RobocopyLine.Copied, FileBackupRobocopy.Classify("\t\t\t" + file, plan, out var path));
        Assert.AreEqual(file, path);
        Assert.AreEqual(RobocopyLine.Copied, FileBackupRobocopy.Classify("100%\tNeue Datei\t\t  4096\t" + file, plan, out path), "Whatever stands in front of the path, in any language.");
        Assert.AreEqual(file, path);

        Assert.AreEqual(RobocopyLine.Other, FileBackupRobocopy.Classify("   Source : " + _original + Path.DirectorySeparatorChar, plan, out _), "The heading that names the folder is not a file.");
        Assert.AreEqual(RobocopyLine.Other, FileBackupRobocopy.Classify("\t\t\t" + _original + Path.DirectorySeparatorChar, plan, out _));
        Assert.AreEqual(RobocopyLine.Other, FileBackupRobocopy.Classify("\t\t\t" + Path.Combine(plan.FilesFolder, "extra.txt"), plan, out _), "A path in the backup is not a copied file.");
        Assert.AreEqual(RobocopyLine.Other, FileBackupRobocopy.Classify("------------------------------", plan, out _));
        Assert.AreEqual(RobocopyLine.Other, FileBackupRobocopy.Classify("", plan, out _));

        Assert.AreEqual(RobocopyLine.Error, FileBackupRobocopy.Classify("2026/10/06 16:30:05 FEHLER 32 (0x00000020) Datei wird kopiert " + file, plan, out path));
        StringAssert.EndsWith(path, file);
    }

    [TestMethod]
    public void RobocopysExitCodeSaysHowItWent()
    {
        Assert.AreEqual((false, false, false), FileBackupRobocopy.Explain(0));  // nothing to do
        Assert.AreEqual((false, false, true), FileBackupRobocopy.Explain(1));   // copied
        Assert.AreEqual((false, false, true), FileBackupRobocopy.Explain(3));   // copied, and the backup has extra files
        Assert.AreEqual((false, true, true), FileBackupRobocopy.Explain(9));    // copied, some could not be
        Assert.AreEqual((true, false, false), FileBackupRobocopy.Explain(16));  // could not work at all
        Assert.AreEqual((true, false, false), FileBackupRobocopy.Explain(-1));  // closed from outside
    }

    [TestMethod]
    public void AGrowingLogIsReadLineByLine()
    {
        var path = Path.Combine(_top, "growing.log");
        var tail = new FileBackupRobocopy.LogTail(path);
        Assert.AreEqual(0, tail.NewLines().Count, "Not there yet.");

        using var writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), System.Text.Encoding.Unicode) { AutoFlush = true };
        writer.Write("first line\r\nsecond, with ünïcödé\r\nhalf a li");
        CollectionAssert.AreEqual(new[] { "first line", "second, with ünïcödé" }, tail.NewLines().Select(line => line.Trim('\uFEFF')).ToList());
        Assert.AreEqual(0, tail.NewLines().Count);

        writer.Write("ne\r\n");
        CollectionAssert.AreEqual(new[] { "half a line" }, tail.NewLines());
    }

    [TestMethod]
    public void ARealRunThroughRobocopyCopiesWhatChanged()
    {
        // On Windows this starts the real robocopy.exe between two temporary folders (elsewhere Clearspace's
        // own copy stands in). It is the one test of the command Clearspace gives it and of reading its log.
        Write("a.txt", "one");
        Write(Path.Combine("Holiday photos", "strand – été.jpg"), "sand");
        Write(Path.Combine("node_modules", "big.js"), "left out");
        Directory.CreateDirectory(Path.Combine(_original, "Empty"));
        var plan = Plan(FileBackupType.Clone, change => { change.KeepVersions = true; change.KeepRemoved = true; change.Exclude = ["node_modules"]; });
        var store = new FileBackupStore(Path.Combine(_top, "store"));

        var first = FileBackupEngine.Run(new FileBackupRun { Plan = plan, NowLocal = Noon, LogPath = store.NewLogPath(plan.Id, Noon) });
        Assert.AreEqual(FileBackupState.Ok, first.Outcome, first.Message);
        Assert.AreEqual("one", Read(plan.FilesFolder, "a.txt"));
        Assert.AreEqual("sand", Read(plan.FilesFolder, "Holiday photos", "strand – été.jpg"));
        Assert.IsTrue(Directory.Exists(Path.Combine(plan.FilesFolder, "Empty")));
        Assert.IsFalse(Directory.Exists(Path.Combine(plan.FilesFolder, "node_modules")));
        Assert.AreEqual(Old, File.GetLastWriteTimeUtc(Path.Combine(plan.FilesFolder, "a.txt")));
        Assert.AreEqual(2, first.Copied, "The files copied are counted from robocopy's log. " + first.Message);

        var second = FileBackupEngine.Run(new FileBackupRun { Plan = plan, NowLocal = Noon.AddHours(1), LogPath = store.NewLogPath(plan.Id, Noon.AddHours(1)) });
        Assert.AreEqual(FileBackupState.Nothing, second.Outcome, second.Message);

        Write("a.txt", "one, changed", minutes: 10);
        File.Delete(Path.Combine(_original, "Holiday photos", "strand – été.jpg"));
        var third = FileBackupEngine.Run(new FileBackupRun { Plan = plan, NowLocal = Noon.AddHours(2), LogPath = store.NewLogPath(plan.Id, Noon.AddHours(2)) });
        Assert.AreEqual(FileBackupState.Ok, third.Outcome, third.Message);
        Assert.AreEqual("one, changed", Read(plan.FilesFolder, "a.txt"));
        Assert.IsFalse(There(plan.FilesFolder, "Holiday photos", "strand – été.jpg"));
        var kept = Path.Combine(plan.HistoryFolder, FileBackupLayout.Stamp(Noon.AddHours(2)));
        Assert.AreEqual("one", Read(kept, "a.txt"));
        Assert.AreEqual("sand", Read(kept, "Holiday photos", "strand – été.jpg"));
        Assert.AreEqual(1, third.Copied, third.Message);
        Assert.AreEqual(2, third.SetAside);
    }

    // ------------------------------------------------------------------ running: due, locks, stopping

    [TestMethod]
    public void TheWindowlessRunIsStartedByItsSwitches()
    {
        Assert.IsTrue(FileBackupRunner.IsSwitch(["--run-backups"]));
        Assert.IsTrue(FileBackupRunner.IsSwitch(["--run-backup", "abc"]));
        Assert.IsFalse(FileBackupRunner.IsSwitch(["--run-backup"]));
        Assert.IsFalse(FileBackupRunner.IsSwitch(["--run-backups", "abc"]));
        Assert.IsFalse(FileBackupRunner.IsSwitch([@"C:\Some folder"]));
        Assert.IsFalse(FileBackupRunner.IsSwitch([]));
    }

    [TestMethod]
    public void OnlyPlansThatAreDueAndCanRunAreRun()
    {
        var store = new FileBackupStore(Path.Combine(_top, "store"));
        FileBackupPlan Named(string name, Action<FileBackupPlan>? change = null) => Plan(FileBackupType.Clone, plan =>
        {
            plan.Name = plan.FolderName = name;
            plan.Schedule = Schedule(FileBackupSchedule.Hourly);
            change?.Invoke(plan);
        });

        var never = Named("Never ran");
        var recent = Named("Ran ten minutes ago");
        var failed = Named("Failed ten minutes ago");
        var failedLongAgo = Named("Failed two hours ago", plan => plan.Schedule = Schedule(FileBackupSchedule.Daily));
        var manual = Named("Only when asked", plan => plan.Schedule = new FileBackupSchedule());
        var unplugged = Named("Drive not there", plan => plan.Destination = Path.Combine(_top, "Not plugged in"));
        store.SaveState(recent.Id, new FileBackupState { Outcome = FileBackupState.Ok, LastRunUtc = Noon.AddMinutes(-10).ToUniversalTime() });
        store.SaveState(failed.Id, new FileBackupState { Outcome = FileBackupState.Failed, LastRunUtc = Noon.AddMinutes(-10).ToUniversalTime() });
        store.SaveState(failedLongAgo.Id, new FileBackupState { Outcome = FileBackupState.Failed, LastRunUtc = Noon.AddHours(-2).ToUniversalTime() });
        var settings = new FileBackupSettings { Plans = [never, recent, failed, failedLongAgo, manual, unplugged] };

        var due = FileBackupRunner.Due(settings, store, Noon);

        CollectionAssert.AreEquivalent(new[] { "Never ran", "Failed two hours ago" }, due.Select(plan => plan.Name).ToArray());
        var waiting = store.State(unplugged.Id);
        Assert.AreEqual(FileBackupState.Waiting, waiting.Outcome, "A missing drive is waited for, not failed on.");
        Assert.IsNull(waiting.LastRunUtc, "Waiting is not a run, so the backup stays due.");
    }

    [TestMethod]
    public void ARunSaysHowItWentAndLeavesNothingBehind()
    {
        Write("a.txt", "one");
        var store = new FileBackupStore(Path.Combine(_top, "store"));
        var plan = Plan(FileBackupType.Clone);

        var result = FileBackupRunner.RunPlan(plan, store, Noon, useRobocopy: false);

        Assert.AreEqual(FileBackupState.Ok, result!.Outcome);
        var state = store.State(plan.Id);
        Assert.AreEqual(FileBackupState.Ok, state.Outcome);
        Assert.AreEqual(1, state.Copied);
        Assert.IsNotNull(state.LastRunUtc);
        Assert.IsNotNull(state.LastGoodUtc);
        StringAssert.Contains(File.ReadAllText(state.LogPath!), plan.Root);
        Assert.IsFalse(FileBackupRunner.IsRunning(plan.Id, store));
        Assert.IsFalse(FileBackupRunner.WasCutShort(plan.Id, store));
        CollectionAssert.AreEqual(new[] { plan.Id + ".json" }, Directory.GetFiles(store.StateFolder).Select(Path.GetFileName).ToArray(),
            "The lock, the progress and any Stop request are gone when the run ends.");
    }

    [TestMethod]
    public void APlanThatIsRunningIsNotStartedASecondTime()
    {
        Write("a.txt", "one");
        var store = new FileBackupStore(Path.Combine(_top, "store"));
        var plan = Plan(FileBackupType.Clone);
        Directory.CreateDirectory(store.StateFolder);
        Assert.IsFalse(FileBackupRunner.IsRunning(plan.Id, store));

        using (new FileStream(store.LockPath(plan.Id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.IsTrue(FileBackupRunner.IsRunning(plan.Id, store));
            Assert.IsNull(FileBackupRunner.RunPlan(plan, store, Noon, useRobocopy: false));
            Assert.IsFalse(Directory.Exists(plan.Root));

            plan.Schedule = Schedule(FileBackupSchedule.Hourly);
            Assert.AreEqual(0, FileBackupRunner.Due(new FileBackupSettings { Plans = [plan] }, store, Noon).Count);
        }

        Assert.IsFalse(FileBackupRunner.IsRunning(plan.Id, store), "A lock file nobody holds is a leftover, not a run.");
        Assert.IsNotNull(FileBackupRunner.RunPlan(plan, store, Noon, useRobocopy: false));
    }

    [TestMethod]
    public void ARunThatIsStoppedSaysSoAndKeepsWhatItCopied()
    {
        Write("a.txt", "one");
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        foreach (var type in new[] { FileBackupType.Clone, FileBackupType.Versions, FileBackupType.Zip })
        {
            var plan = Plan(type, change => change.FolderName = type.ToString());
            var result = FileBackupEngine.Run(new FileBackupRun { Plan = plan, NowLocal = Noon, UseRobocopy = false, Token = stop.Token });
            Assert.AreEqual(FileBackupState.Stopped, result.Outcome, type.ToString());
            Assert.AreEqual(0, FileBackupEngine.Versions(plan.Root).Count, "A version that was not finished is not offered.");
            Assert.AreEqual(0, FileBackupEngine.Zips(plan).Count);
        }
    }

    [TestMethod]
    public void BeingUpdatedStopsEveryRunForAFewMinutes()
    {
        var store = new FileBackupStore(Path.Combine(_top, "store"));
        FileBackupRunner.AskAllToStop(store);
        Assert.IsFalse(FileBackupRunner.StopAllAsked(store), "Nothing was ever set up here, so nothing is written.");

        store.Save(new FileBackupSettings { Plans = [Plan(FileBackupType.Clone)] });
        FileBackupRunner.AskAllToStop(store);
        Assert.IsTrue(FileBackupRunner.StopAllAsked(store));
        Assert.AreEqual(3, FileBackupRunner.Run(["--run-backups"], store));

        File.SetLastWriteTimeUtc(store.StopAllPath, DateTime.UtcNow.AddMinutes(-10));
        Assert.IsFalse(FileBackupRunner.StopAllAsked(store), "The update is long over.");
        Assert.IsFalse(File.Exists(store.StopAllPath));
        Assert.AreEqual(2, FileBackupRunner.Run(["--run-backup", "no such backup"], store));
    }

    // ------------------------------------------------------------------ Task Scheduler

    [TestMethod]
    public void TheTaskSchedulerEntryRunsWhatIsDueEveryQuarterOfAnHour()
    {
        var xml = FileBackupTask.Xml(@"C:\Program Files\Tom & Jerry\Clearspace.exe", "S-1-5-21-1-2-3-1001", new DateTime(2026, 10, 6));
        Assert.IsTrue(xml.StartsWith("<?xml", StringComparison.Ordinal), "Task Scheduler wants the declaration first.");

        var task = System.Xml.Linq.XDocument.Parse(xml); // well formed, with the & in the path escaped
        System.Xml.Linq.XNamespace space = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string Value(string name) => task.Descendants(space + name).Single().Value;

        Assert.AreEqual(@"C:\Program Files\Tom & Jerry\Clearspace.exe", Value("Command"));
        Assert.AreEqual("--run-backups", Value("Arguments"));
        Assert.AreEqual("PT15M", Value("Interval"));
        Assert.AreEqual("2026-10-06T00:00:00", Value("StartBoundary"));
        Assert.AreEqual("S-1-5-21-1-2-3-1001", Value("UserId"));
        Assert.AreEqual("LeastPrivilege", Value("RunLevel"), "No administrator rights.");
        Assert.AreEqual("IgnoreNew", Value("MultipleInstancesPolicy"));
        Assert.AreEqual("false", Value("DisallowStartIfOnBatteries"));
        Assert.AreEqual("PT0S", Value("ExecutionTimeLimit"), "A first backup of a drive may take hours.");
    }

    [TestMethod]
    public void TheTaskSchedulerEntryIsOnlyWantedWhenSomethingIsScheduled()
    {
        var scheduled = Plan(FileBackupType.Clone, plan => plan.Schedule = Schedule(FileBackupSchedule.Daily));
        Assert.IsFalse(FileBackupTask.Wanted(new FileBackupSettings { Plans = [Plan(FileBackupType.Clone)] }), "No backup has a schedule.");
        Assert.IsTrue(FileBackupTask.Wanted(new FileBackupSettings { Plans = [Plan(FileBackupType.Clone), scheduled] }));
        Assert.IsFalse(FileBackupTask.Wanted(new FileBackupSettings { RunWhenClosed = false, Plans = [scheduled] }), "Turned off on the page.");

        Assert.AreEqual("Clearspace backups (payton)", FileBackupTask.NameFor("payton"));
        Assert.AreEqual("Clearspace backups (Tom Jerry)", FileBackupTask.NameFor(@"Tom\/:*?<>| Jerry"));
        Assert.AreEqual("Clearspace backups", FileBackupTask.NameFor(""));

        // Nothing was ever registered and nothing is wanted: Task Scheduler is not even asked.
        var store = new FileBackupStore(Path.Combine(_top, "store"));
        Assert.IsNull(FileBackupTask.Sync(new FileBackupSettings { RunWhenClosed = false, Plans = [scheduled] }, store, @"C:\Clearspace.exe"));
        Assert.IsFalse(File.Exists(Path.Combine(store.Folder, "task.txt")));
    }

    // ------------------------------------------------------------------ what is kept on this PC

    [TestMethod]
    public void PlansAndTheirStateAreKeptAndReadBack()
    {
        var store = new FileBackupStore(Path.Combine(_top, "store"));
        Assert.AreEqual(0, store.Load().Plans.Count);
        Assert.IsTrue(store.Load().RunWhenClosed, "On until the person turns it off.");

        var plan = Plan(FileBackupType.Versions, change =>
        {
            change.Schedule = Schedule(FileBackupSchedule.Weekly, atMinute: 90, days: 5);
            change.Exclude = ["*.tmp"];
            change.KeepCount = 7;
        });
        store.Save(new FileBackupSettings { RunWhenClosed = false, Plans = [plan] });

        var read = new FileBackupStore(store.Folder).Load();
        Assert.IsFalse(read.RunWhenClosed);
        var back = read.Plans.Single();
        Assert.AreEqual(plan.Id, back.Id);
        Assert.AreEqual(FileBackupType.Versions, back.Type);
        Assert.AreEqual(FileBackupSchedule.Weekly, back.Schedule.Kind);
        Assert.AreEqual(90, back.Schedule.AtMinute);
        Assert.AreEqual(5, back.Schedule.Days);
        Assert.AreEqual(7, back.KeepCount);
        CollectionAssert.AreEqual(new[] { "*.tmp" }, back.Exclude);
        StringAssert.Contains(File.ReadAllText(store.PlansPath), "\"Versions\"", "The kind is written as a word, so the file can be read by a person.");

        Assert.IsFalse(store.State(plan.Id).HasRun);
        store.SaveState(plan.Id, new FileBackupState { Outcome = FileBackupState.Problems, Message = "2 files", LastRunUtc = Old, Errors = 2 });
        var state = store.State(plan.Id);
        Assert.AreEqual(FileBackupState.Problems, state.Outcome);
        Assert.AreEqual(Old, state.LastRunUtc);
        Assert.AreEqual(2, state.Errors);

        store.Forget(plan.Id);
        Assert.IsFalse(store.State(plan.Id).HasRun);
    }

    [TestMethod]
    public void OnlyTheReportsOfTheLastRunsAreKept()
    {
        var store = new FileBackupStore(Path.Combine(_top, "store"));
        for (var run = 0; run < 5; run++)
        {
            var log = store.NewLogPath("plan", Noon.AddHours(run));
            File.WriteAllText(log, "report");
            File.WriteAllText(Path.ChangeExtension(log, ".robocopy.log"), "robocopy's own");
        }

        store.PruneLogs("plan", keep: 2);

        var left = Directory.GetFiles(Path.Combine(store.LogFolder, "plan")).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[]
        {
            FileBackupLayout.Stamp(Noon.AddHours(3)) + ".log", FileBackupLayout.Stamp(Noon.AddHours(3)) + ".robocopy.log",
            FileBackupLayout.Stamp(Noon.AddHours(4)) + ".log", FileBackupLayout.Stamp(Noon.AddHours(4)) + ".robocopy.log"
        }, left, "A run's report and robocopy's own log go together.");
    }
}
