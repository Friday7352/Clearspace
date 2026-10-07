// NEW (zip folders): opening a ZIP file in the list like a folder.
//  - Services/ArchiveLocation.cs: the address of a place inside a ZIP, how it is shown and typed, and
//    what is above it; the address bar's trail and Up.
//  - Services/ArchiveBrowser.cs: listing a place inside a ZIP (folders the ZIP only implies, names that
//    try to leave it, a ZIP that changed), taking things out (never overwriting, names Windows refuses,
//    stopping), and the copies made to open or copy a file.
//  - That the inside of a ZIP is read-only: which actions are off, and that Open never hands an address
//    inside a ZIP to Windows.
// These tests work on real files, in a folder of their own under the temporary folder that is removed
// again after each test.
using System.IO;
using System.IO.Compression;
using Clearspace.Commands;
using Clearspace.Commands.Actions;
using Clearspace.Models;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class ArchiveBrowseTests
{
    private static readonly DateTime Stamp = new(2024, 5, 6, 7, 8, 10);

    private string _root = string.Empty;
    private string _tempBefore = string.Empty;

    [TestInitialize]
    public void MakeFolder()
    {
        _root = Path.Combine(Path.GetTempPath(), "Clearspace zip tests " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _tempBefore = ArchiveBrowser.TempRoot;
        ArchiveBrowser.TempRoot = Path.Combine(_root, "copies");
        ArchiveBrowser.Forget();
    }

    [TestCleanup]
    public void RemoveFolder()
    {
        ArchiveBrowser.TempRoot = _tempBefore;
        ArchiveBrowser.Forget();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal); // copies made for opening are read-only
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string At(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    // A ZIP file with these entries. A name ending in "/" is a folder entry; content null means "no content".
    private string MakeZip(string name, params (string Entry, string? Content)[] entries)
    {
        var path = At(name);
        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            var made = zip.CreateEntry(entry);
            made.LastWriteTime = new DateTimeOffset(Stamp);
            if (content is null)
                continue;

            using var writer = new StreamWriter(made.Open());
            writer.Write(content);
        }

        return path;
    }

    private string Photos() => MakeZip("Photos.zip",
        ("readme.txt", "read me"),
        ("Trip/2024/a.jpg", "picture a"),
        ("Trip/2024/b.jpg", "picture bb"),
        ("Trip/notes.txt", "notes"),
        ("Empty/", null),
        ("Win\\c.txt", "backslashes"));

    private static List<string> Names(IEnumerable<FileSystemItem> items)
        => [.. items.Select(item => (item.IsFolder ? "[" + item.Name + "]" : item.Name)).OrderBy(name => name, StringComparer.Ordinal)];

    // ---- the address

    [TestMethod]
    public void AnAddressInsideAZipIsTheZipFileThenThePathInsideIt()
    {
        var top = ArchiveLocation.For(@"C:\Users\you\Downloads\Photos.zip");
        var inner = ArchiveLocation.For(@"C:\Users\you\Downloads\Photos.zip", @"Trip\2024\");

        Assert.AreEqual(@"clearspace://zip/C:\Users\you\Downloads\Photos.zip", top);
        Assert.AreEqual(@"clearspace://zip/C:\Users\you\Downloads\Photos.zip/Trip/2024", inner);

        Assert.IsTrue(ArchiveLocation.TrySplit(inner, out var archive, out var inside));
        Assert.AreEqual(@"C:\Users\you\Downloads\Photos.zip", archive);
        Assert.AreEqual("Trip/2024", inside);

        Assert.IsTrue(ArchiveLocation.TrySplit(top, out archive, out inside));
        Assert.AreEqual(@"C:\Users\you\Downloads\Photos.zip", archive);
        Assert.AreEqual(string.Empty, inside);

        Assert.AreEqual(ArchiveLocation.For(@"C:\Users\you\Downloads\Photos.zip", "Trip/2024/a.jpg"), ArchiveLocation.Child(inner, "a.jpg"));
        Assert.AreEqual("2024", ArchiveLocation.NameOf(inner));
        Assert.AreEqual("Photos.zip", ArchiveLocation.NameOf(top));
    }

    [TestMethod]
    public void OnlyAddressesInsideAZipCount()
    {
        Assert.IsTrue(ArchiveLocation.Is(@"clearspace://zip/C:\a.zip"));
        Assert.IsFalse(ArchiveLocation.Is(@"C:\a.zip"));
        Assert.IsFalse(ArchiveLocation.Is(ExplorerLocations.MyPcPath));
        Assert.IsFalse(ArchiveLocation.Is(null));
        Assert.IsFalse(ArchiveLocation.TrySplit(ExplorerLocations.YourFilesPath, out _, out _));

        // Everything that asks "is this a path on a disk?" must hear no.
        Assert.IsFalse(Path.IsPathFullyQualified(ArchiveLocation.For(@"C:\a.zip", "Trip")));
    }

    [TestMethod]
    public void AnAddressIsShownAndTypedLikeAPath()
    {
        Assert.AreEqual(@"C:\Downloads\Photos.zip", ArchiveLocation.Display(ArchiveLocation.For(@"C:\Downloads\Photos.zip")));
        Assert.AreEqual(@"C:\Downloads\Photos.zip\Trip\2024", ArchiveLocation.Display(ArchiveLocation.For(@"C:\Downloads\Photos.zip", "Trip/2024")));
        Assert.AreEqual(@"C:\Downloads", ArchiveLocation.Display(@"C:\Downloads"), "a real folder is left as it is");
        Assert.AreEqual(ExplorerLocations.MyPcPath, ArchiveLocation.Display(ExplorerLocations.MyPcPath));
    }

    [TestMethod]
    public void ATypedPathFindsTheZipFileOnTheDisk()
    {
        var zip = Photos();

        Assert.AreEqual(ArchiveLocation.For(zip), ArchiveLocation.FromTyped(zip));
        Assert.AreEqual(ArchiveLocation.For(zip), ArchiveLocation.FromTyped("\"" + zip + "\\\""));
        Assert.AreEqual(ArchiveLocation.For(zip, "Trip/2024"), ArchiveLocation.FromTyped(zip + @"\Trip\2024"));
        Assert.AreEqual(ArchiveLocation.For(zip, "Trip/2024"), ArchiveLocation.FromTyped(zip.Replace('\\', '/') + "/Trip/2024/"));

        Assert.IsNull(ArchiveLocation.FromTyped(At("Missing.zip")), "no such file");
        Assert.IsNull(ArchiveLocation.FromTyped(_root), "a folder");
        File.WriteAllText(At("notes.zipped"), "x");
        Assert.IsNull(ArchiveLocation.FromTyped(At("notes.zipped")), "not a .zip ending");

        // A folder whose name ends in .zip is a folder; the ZIP file inside it is still found.
        Directory.CreateDirectory(At("old.zip"));
        File.Copy(zip, At("old.zip", "Inner.zip"));
        Assert.IsNull(ArchiveLocation.FromTyped(At("old.zip")));
        Assert.AreEqual(ArchiveLocation.For(At("old.zip", "Inner.zip"), "Trip"), ArchiveLocation.FromTyped(At("old.zip", "Inner.zip", "Trip")));
    }

    [TestMethod]
    public void AboveAFolderInAZipIsTheFolderAboveAndAboveItsTopTheRealFolder()
    {
        var inner = ArchiveLocation.For(@"C:\Downloads\Photos.zip", "Trip/2024");

        var trip = ArchiveLocation.Parent(inner);
        Assert.AreEqual(ArchiveLocation.For(@"C:\Downloads\Photos.zip", "Trip"), trip);
        var top = ArchiveLocation.Parent(trip!);
        Assert.AreEqual(ArchiveLocation.For(@"C:\Downloads\Photos.zip"), top);
        Assert.AreEqual(@"C:\Downloads", ArchiveLocation.Parent(top!));
        Assert.AreEqual(@"C:\", ArchiveLocation.Parent(ArchiveLocation.For(@"C:\Photos.zip")));
        Assert.IsNull(ArchiveLocation.Parent(@"C:\Downloads"));
    }

    [TestMethod]
    public void UpWalksOutOfTheZipIntoTheFolderItIsIn()
    {
        var navigation = new NavigationService();
        navigation.Navigate(ArchiveLocation.For(@"C:\Downloads\Photos.zip", "Trip/2024"));

        Assert.IsTrue(navigation.CanGoUp);
        navigation.GoUp();
        Assert.AreEqual(ArchiveLocation.For(@"C:\Downloads\Photos.zip", "Trip"), navigation.CurrentPath);
        navigation.GoUp();
        Assert.AreEqual(ArchiveLocation.For(@"C:\Downloads\Photos.zip"), navigation.CurrentPath);
        Assert.IsTrue(navigation.CanGoUp);
        navigation.GoUp();
        Assert.AreEqual(@"C:\Downloads", navigation.CurrentPath);

        // Clearspace's other pages still have nothing above them.
        navigation.Navigate(ExplorerLocations.MyPcPath);
        Assert.IsFalse(navigation.CanGoUp);
    }

    [TestMethod]
    public void TheTrailGoesThroughTheZipFile()
    {
        var crumbs = NavigationBreadcrumbs.BuildBreadcrumbs(ArchiveLocation.For(@"C:\Users\you\Photos.zip", "Trip/2024"));

        CollectionAssert.AreEqual(new[] { "C:", "Users", "you", "Photos.zip", "Trip", "2024" }, crumbs.Select(crumb => crumb.Name).ToArray());
        Assert.AreEqual(@"C:\Users\you", crumbs[2].Path, "a real folder");
        Assert.AreEqual(ArchiveLocation.For(@"C:\Users\you\Photos.zip"), crumbs[3].Path, "the top of the ZIP");
        Assert.AreEqual(ArchiveLocation.For(@"C:\Users\you\Photos.zip", "Trip"), crumbs[4].Path);
        Assert.AreEqual(ArchiveLocation.For(@"C:\Users\you\Photos.zip", "Trip/2024"), crumbs[5].Path);

        Assert.AreEqual("Photos.zip", ViewModels.ExplorerTab.DefaultTitle(ArchiveLocation.For(@"C:\Users\you\Photos.zip")), "the name on a tab");
    }

    // ---- looking inside

    [TestMethod]
    public void TheTopOfAZipListsItsFilesAndTheFoldersItsNamesImply()
    {
        var zip = Photos();

        var top = ArchiveBrowser.List(ArchiveLocation.For(zip));

        CollectionAssert.AreEqual(new[] { "[Empty]", "[Trip]", "[Win]", "readme.txt" }, Names(top));

        var readme = top.Single(item => item.Name == "readme.txt");
        Assert.AreEqual(ArchiveLocation.For(zip, "readme.txt"), readme.FullPath);
        Assert.IsTrue(readme.IsInArchive);
        Assert.AreEqual(7, readme.Size);
        Assert.AreEqual(Stamp, readme.DateModified);
        Assert.AreEqual(".txt", readme.Extension);

        var trip = top.Single(item => item.Name == "Trip");
        Assert.IsTrue(trip.IsFolder);
        Assert.AreEqual(ArchiveLocation.For(zip, "Trip"), trip.FullPath);
        Assert.AreEqual(DateTime.MinValue, trip.DateModified, "the ZIP has no entry for this folder, so no date");
        Assert.AreEqual(Stamp, top.Single(item => item.Name == "Empty").DateModified, "this one it has");
    }

    [TestMethod]
    public void FoldersInsideAZipOpenLikeFolders()
    {
        var zip = Photos();

        CollectionAssert.AreEqual(new[] { "[2024]", "notes.txt" }, Names(ArchiveBrowser.List(ArchiveLocation.For(zip, "Trip"))));
        CollectionAssert.AreEqual(new[] { "a.jpg", "b.jpg" }, Names(ArchiveBrowser.List(ArchiveLocation.For(zip, "Trip/2024"))));
        CollectionAssert.AreEqual(new[] { "c.txt" }, Names(ArchiveBrowser.List(ArchiveLocation.For(zip, "Win"))), "a name written with backslashes");
        Assert.AreEqual(0, ArchiveBrowser.List(ArchiveLocation.For(zip, "Empty")).Count);

        // A typed address may differ in capitals.
        CollectionAssert.AreEqual(new[] { "a.jpg", "b.jpg" }, Names(ArchiveBrowser.List(ArchiveLocation.For(zip, "trip/2024"))));

        Assert.IsTrue(ArchiveBrowser.Find(ArchiveLocation.For(zip))!.IsFolder);
        Assert.IsTrue(ArchiveBrowser.Find(ArchiveLocation.For(zip, "Trip"))!.IsFolder);
        Assert.IsFalse(ArchiveBrowser.Find(ArchiveLocation.For(zip, "Trip/notes.txt"))!.IsFolder);
        Assert.IsNull(ArchiveBrowser.Find(ArchiveLocation.For(zip, "Trip/gone.txt")));
        Assert.IsNull(ArchiveBrowser.Find(ArchiveLocation.For(At("Missing.zip"), "Trip")));
    }

    [TestMethod]
    public void WhatCannotBeShownSaysWhy()
    {
        var zip = Photos();

        Assert.ThrowsException<DirectoryNotFoundException>(() => ArchiveBrowser.List(ArchiveLocation.For(zip, "Nowhere")));
        Assert.ThrowsException<DirectoryNotFoundException>(() => ArchiveBrowser.List(ArchiveLocation.For(zip, "readme.txt")), "a file is not a folder");
        Assert.ThrowsException<FileNotFoundException>(() => ArchiveBrowser.List(ArchiveLocation.For(At("Missing.zip"))));

        File.WriteAllText(At("Broken.zip"), "this is not a zip file at all");
        var problem = Assert.ThrowsException<IOException>(() => ArchiveBrowser.List(ArchiveLocation.For(At("Broken.zip"))));
        StringAssert.Contains(problem.Message, "can't read this ZIP file");
    }

    [TestMethod]
    public void NamesThatTryToLeaveTheZipAreNotShown()
    {
        var zip = MakeZip("Hostile.zip",
            ("../outside.txt", "x"),
            ("ok/../../outside.txt", "x"),
            ("..\\outside.txt", "x"),
            ("./dot.txt", "x"),
            ("/rooted.txt", "kept, without the slash"),
            ("ok/fine.txt", "fine"));

        CollectionAssert.AreEqual(new[] { "[ok]", "rooted.txt" }, Names(ArchiveBrowser.List(ArchiveLocation.For(zip))));
        CollectionAssert.AreEqual(new[] { "fine.txt" }, Names(ArchiveBrowser.List(ArchiveLocation.For(zip, "ok"))));

        var made = ArchiveBrowser.Extract([ArchiveLocation.For(zip, "ok"), ArchiveLocation.For(zip, "rooted.txt")], At("out"), null, CancellationToken.None);
        Assert.AreEqual(2, made.Count);
        CollectionAssert.AreEquivalent(new[] { "fine.txt", "rooted.txt" },
            Directory.EnumerateFiles(At("out"), "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray());
        Assert.IsFalse(File.Exists(At("outside.txt")));
    }

    [TestMethod]
    public void AFileAndAFolderWithOneNameAndTheSameNameTwice()
    {
        var zip = MakeZip("Odd.zip",
            ("thing", "a file"),
            ("thing/inside.txt", "can't be: thing is a file"),
            ("twice.txt", "first"),
            ("twice.txt", "second, the newer"));

        var top = ArchiveBrowser.List(ArchiveLocation.For(zip));
        CollectionAssert.AreEqual(new[] { "thing", "twice.txt" }, Names(top));

        var made = ArchiveBrowser.Extract([ArchiveLocation.For(zip, "twice.txt")], At("out"), null, CancellationToken.None);
        Assert.AreEqual("second, the newer", File.ReadAllText(made[0]));
    }

    [TestMethod]
    public void AZipThatChangedOnTheDiskIsReadAgain()
    {
        var zip = MakeZip("Grows.zip", ("one.txt", "1"));
        CollectionAssert.AreEqual(new[] { "one.txt" }, Names(ArchiveBrowser.List(ArchiveLocation.For(zip))));

        MakeZip("Grows.zip", ("one.txt", "1"), ("two.txt", "22"), ("more/three.txt", "333"));
        File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddMinutes(1));

        CollectionAssert.AreEqual(new[] { "[more]", "one.txt", "two.txt" }, Names(ArchiveBrowser.List(ArchiveLocation.For(zip))));
    }

    [TestMethod]
    public void MeasuringCountsEverythingInsideChosenFolders()
    {
        var zip = Photos();

        Assert.AreEqual((1, 7L), ArchiveBrowser.Measure([ArchiveLocation.For(zip, "readme.txt")]));
        Assert.AreEqual((3, 9L + 10L + 5L), ArchiveBrowser.Measure([ArchiveLocation.For(zip, "Trip")]));
        Assert.AreEqual((0, 0L), ArchiveBrowser.Measure([ArchiveLocation.For(zip, "Empty"), ArchiveLocation.For(zip, "gone")]));
        Assert.AreEqual((0, 0L), ArchiveBrowser.Measure([ArchiveLocation.For(zip)]), "the top of a ZIP is not something inside it");
    }

    // ---- taking things out

    [TestMethod]
    public void ExtractWritesTheChosenFilesAndFoldersAndNothingElse()
    {
        var zip = Photos();
        var reports = new List<ArchiveProgress>();

        var made = ArchiveBrowser.Extract(
            [ArchiveLocation.For(zip, "Trip"), ArchiveLocation.For(zip, "readme.txt"), ArchiveLocation.For(zip, "Empty")],
            At("out"), new Recorder(reports), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { At("out", "Trip"), At("out", "readme.txt"), At("out", "Empty") }, made.ToArray());
        Assert.AreEqual("read me", File.ReadAllText(At("out", "readme.txt")));
        Assert.AreEqual("picture a", File.ReadAllText(At("out", "Trip", "2024", "a.jpg")));
        Assert.AreEqual("picture bb", File.ReadAllText(At("out", "Trip", "2024", "b.jpg")));
        Assert.AreEqual("notes", File.ReadAllText(At("out", "Trip", "notes.txt")));
        Assert.IsTrue(Directory.Exists(At("out", "Empty")));
        Assert.IsFalse(Directory.Exists(At("out", "Win")), "not chosen");
        Assert.AreEqual(Stamp, File.GetLastWriteTime(At("out", "readme.txt")), "the file keeps its date");

        Assert.AreEqual(31, reports[^1].BytesTotal);
        Assert.AreEqual(31, reports[^1].BytesDone);
    }

    [TestMethod]
    public void ExtractNeverReplacesWhatIsAlreadyThere()
    {
        var zip = Photos();
        Directory.CreateDirectory(At("out"));
        File.WriteAllText(At("out", "readme.txt"), "mine");
        Directory.CreateDirectory(At("out", "Trip"));
        File.WriteAllText(At("out", "Trip", "notes.txt"), "mine too");

        var made = ArchiveBrowser.Extract([ArchiveLocation.For(zip, "readme.txt"), ArchiveLocation.For(zip, "Trip")],
            At("out"), null, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { At("out", "readme (2).txt"), At("out", "Trip (2)") }, made.ToArray());
        Assert.AreEqual("mine", File.ReadAllText(At("out", "readme.txt")));
        Assert.AreEqual("mine too", File.ReadAllText(At("out", "Trip", "notes.txt")));
        Assert.AreEqual("read me", File.ReadAllText(At("out", "readme (2).txt")));
        Assert.AreEqual("notes", File.ReadAllText(At("out", "Trip (2)", "notes.txt")));
    }

    [TestMethod]
    public void StoppingAnExtractLeavesNothingBehind()
    {
        var zip = Photos();
        using var stop = new CancellationTokenSource();

        // Stopped before it starts ...
        stop.Cancel();
        Assert.ThrowsException<OperationCanceledException>(() =>
            ArchiveBrowser.Extract([ArchiveLocation.For(zip, "Trip")], At("out"), null, stop.Token));
        Assert.AreEqual(0, Directory.Exists(At("out")) ? Directory.GetFileSystemEntries(At("out")).Length : 0);

        // ... and part of the way through: one small file is out, a large one is being written.
        var large = At("Large.zip");
        using (var stream = new FileStream(large, FileMode.Create))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var small = archive.CreateEntry("first.txt").Open())
                small.Write("small"u8);

            using var big = archive.CreateEntry("big.bin", CompressionLevel.NoCompression).Open();
            var block = new byte[1 << 20];
            for (var count = 0; count < 64; count++)
                big.Write(block);
        }

        using var midway = new CancellationTokenSource();
        var job = Task.Run(() => ArchiveBrowser.Extract(
            [ArchiveLocation.For(large, "first.txt"), ArchiveLocation.For(large, "big.bin")], At("out2"), null, midway.Token));

        while (!job.IsCompleted && !File.Exists(At("out2", "big.bin")))
            Thread.Yield();
        midway.Cancel();

        try
        {
            job.GetAwaiter().GetResult();
            Assert.Inconclusive("the files were out before the stop arrived");
        }
        catch (OperationCanceledException)
        {
            Assert.AreEqual(0, Directory.GetFileSystemEntries(At("out2")).Length, "the finished file and the half-written one are both removed");
        }
    }

    [TestMethod]
    public void NamesWindowsRefusesAreMadeUsable()
    {
        Assert.AreEqual("report.txt", ArchiveBrowser.SafeName("report.txt"));
        Assert.AreEqual("12_30 notes_.txt", ArchiveBrowser.SafeName("12:30 notes?.txt"));
        Assert.AreEqual("name", ArchiveBrowser.SafeName("name. "));
        Assert.AreEqual("_", ArchiveBrowser.SafeName("..."));
        Assert.AreEqual("_CON", ArchiveBrowser.SafeName("CON"));
        Assert.AreEqual("_nul.txt", ArchiveBrowser.SafeName("nul.txt"));
        Assert.AreEqual("console.txt", ArchiveBrowser.SafeName("console.txt"));

        var zip = MakeZip("Mac.zip", ("Notes 12:30?.txt", "from another system"), ("a*b/c|d.txt", "deeper"));
        CollectionAssert.AreEqual(new[] { "Notes 12:30?.txt", "[a*b]" }.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            Names(ArchiveBrowser.List(ArchiveLocation.For(zip))), "shown as the ZIP has them");

        var made = ArchiveBrowser.Extract([ArchiveLocation.For(zip, "Notes 12:30?.txt"), ArchiveLocation.For(zip, "a*b")], At("out"), null, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { At("out", "Notes 12_30_.txt"), At("out", "a_b") }, made.ToArray());
        Assert.AreEqual("deeper", File.ReadAllText(At("out", "a_b", "c_d.txt")));
    }

    [TestMethod]
    public void TwoNamesWindowsReadsAsOneBothComeOut()
    {
        var zip = MakeZip("Case.zip", ("docs/Readme.txt", "capital"), ("docs/README.TXT", "shouting"));

        var made = ArchiveBrowser.Extract([ArchiveLocation.For(zip, "docs")], At("out"), null, CancellationToken.None);

        var files = Directory.GetFiles(made[0]);
        Assert.AreEqual(2, files.Length);
        CollectionAssert.AreEquivalent(new[] { "capital", "shouting" }, files.Select(File.ReadAllText).ToArray());
    }

    // ---- copies for opening and copying

    [TestMethod]
    public void ACopyForOpeningIsAReadOnlyFileInClearspacesOwnFolder()
    {
        var zip = Photos();
        var location = ArchiveLocation.For(zip, "Trip/2024/a.jpg");

        var copy = ArchiveBrowser.CopyForOpening(location);

        Assert.AreEqual("a.jpg", Path.GetFileName(copy), "the program that opens it sees the real name");
        StringAssert.StartsWith(copy, ArchiveBrowser.TempRoot);
        Assert.AreEqual("picture a", File.ReadAllText(copy));
        Assert.IsTrue(File.GetAttributes(copy).HasFlag(FileAttributes.ReadOnly), "changes would not go back into the ZIP");
        Assert.AreEqual(Stamp, File.GetLastWriteTime(copy));

        // Asked again, the same copy is used: it is not written a second time.
        File.SetAttributes(copy, FileAttributes.Normal);
        File.WriteAllText(copy, "PICTURE A");
        File.SetLastWriteTime(copy, Stamp);
        Assert.AreEqual(copy, ArchiveBrowser.CopyForOpening(location));
        Assert.AreEqual("PICTURE A", File.ReadAllText(copy));

        // A copy that no longer matches the ZIP (another size) is made again.
        File.WriteAllText(copy, "tampered with, and longer");
        File.SetLastWriteTime(copy, Stamp);
        File.SetAttributes(copy, FileAttributes.ReadOnly);
        Assert.AreEqual(copy, ArchiveBrowser.CopyForOpening(location));
        Assert.AreEqual("picture a", File.ReadAllText(copy));

        Assert.ThrowsException<FileNotFoundException>(() => ArchiveBrowser.CopyForOpening(ArchiveLocation.For(zip, "Trip")), "a folder is not opened this way");
        Assert.ThrowsException<FileNotFoundException>(() => ArchiveBrowser.CopyForOpening(ArchiveLocation.For(zip, "gone.txt")));
    }

    [TestMethod]
    public void CopiesForPastingKeepNamesAndFoldersAndAreNotReadOnly()
    {
        var zip = Photos();
        var other = MakeZip("Other.zip", ("readme.txt", "another readme"));

        var copies = ArchiveBrowser.Copies(
            [ArchiveLocation.For(zip, "Trip"), ArchiveLocation.For(zip, "readme.txt"), ArchiveLocation.For(other, "readme.txt")],
            forOpening: false, null, CancellationToken.None);

        Assert.AreEqual(3, copies.Count);
        Assert.AreEqual("Trip", Path.GetFileName(copies[0]));
        Assert.AreEqual("picture bb", File.ReadAllText(Path.Combine(copies[0], "2024", "b.jpg")));
        Assert.AreEqual("notes", File.ReadAllText(Path.Combine(copies[0], "notes.txt")));
        Assert.AreEqual("readme.txt", Path.GetFileName(copies[1]));
        Assert.AreEqual("read me", File.ReadAllText(copies[1]));
        Assert.AreEqual("another readme", File.ReadAllText(copies[2]), "the same name from another ZIP has a place of its own");
        Assert.IsFalse(File.GetAttributes(copies[1]).HasFlag(FileAttributes.ReadOnly), "what is pasted should be an ordinary file");

        // Two files with one name from different folders of the same ZIP do not collide either.
        var same = MakeZip("Same.zip", ("a/x.txt", "from a"), ("b/x.txt", "from b"));
        var both = ArchiveBrowser.Copies([ArchiveLocation.For(same, "a/x.txt"), ArchiveLocation.For(same, "b/x.txt")], false, null, CancellationToken.None);
        Assert.AreNotEqual(both[0], both[1]);
        Assert.AreEqual("from a", File.ReadAllText(both[0]));
        Assert.AreEqual("from b", File.ReadAllText(both[1]));
    }

    [TestMethod]
    public void OldCopiesAreTidiedAwayAndFreshOnesKept()
    {
        var zip = Photos();
        var old = ArchiveBrowser.CopyForOpening(ArchiveLocation.For(zip, "readme.txt"));
        var oldFolder = Directory.GetParent(Path.GetDirectoryName(old)!)!.FullName;

        var other = MakeZip("Other.zip", ("fresh.txt", "fresh"));
        var fresh = ArchiveBrowser.CopyForOpening(ArchiveLocation.For(other, "fresh.txt"));

        Directory.SetLastWriteTimeUtc(oldFolder, DateTime.UtcNow.AddDays(-3));
        ArchiveBrowser.Tidy(ArchiveBrowser.TempRoot, TimeSpan.FromDays(1));

        Assert.IsFalse(Directory.Exists(oldFolder), "removed, read-only file and all");
        Assert.IsTrue(File.Exists(fresh));

        ArchiveBrowser.Tidy(At("no such folder"), TimeSpan.FromDays(1)); // nothing to do is not an error
    }

    [TestMethod]
    public void TheCameFromTheInternetMarkFollowsAFileOpenedStraightFromTheZip()
    {
        var zip = Photos();
        try
        {
            File.WriteAllText(zip + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/Photos.zip\r\n");
            File.ReadAllText(zip + ":Zone.Identifier");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Assert.Inconclusive("this disk keeps no marks");
        }

        var opened = ArchiveBrowser.CopyForOpening(ArchiveLocation.For(zip, "readme.txt"));
        var mark = File.ReadAllText(opened + ":Zone.Identifier");
        StringAssert.Contains(mark, "ZoneId=3");
        Assert.IsFalse(mark.Contains("example.com"), "only the zone is carried over");
        Assert.AreEqual(Stamp, File.GetLastWriteTime(opened), "writing the mark did not change the file's date");

        // What is extracted or copied out stays unmarked, as Extract always has (a marked file can't be locked).
        var made = ArchiveBrowser.Extract([ArchiveLocation.For(zip, "readme.txt")], At("out"), null, CancellationToken.None);
        Assert.IsFalse(File.Exists(made[0] + ":Zone.Identifier"));
        var copied = ArchiveBrowser.Copies([ArchiveLocation.For(zip, "readme.txt")], forOpening: false, null, CancellationToken.None);
        Assert.IsFalse(File.Exists(copied[0] + ":Zone.Identifier"));

        // A ZIP from this computer (no mark, or a trusted zone) leaves no mark.
        var local = MakeZip("Local.zip", ("x.txt", "x"));
        Assert.IsNull(ZoneMark.Read(local));
        File.WriteAllText(local + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=1\r\n");
        Assert.IsNull(ZoneMark.Read(local));
    }

    [TestMethod]
    public void LockingAFolderRemovesTheCopiesTakenOutOfItsZipFiles()
    {
        // A ZIP in the folder that is about to be locked, with a ZIP inside it, and one somewhere else.
        Directory.CreateDirectory(At("Private"));
        var inner = MakeZip("Inner.zip", ("deep.txt", "deep secret"));
        string secret;
        using (var stream = new FileStream(At("Private", "Secret.zip"), FileMode.Create))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("diary.txt").Open()))
                writer.Write("a secret");
            archive.CreateEntryFromFile(inner, "Inner.zip");
            secret = stream.Name;
        }

        var other = MakeZip("Other.zip", ("plain.txt", "nothing private"));

        var diary = ArchiveBrowser.CopyForOpening(ArchiveLocation.For(secret, "diary.txt"));
        var pasted = ArchiveBrowser.Copies([ArchiveLocation.For(secret, "diary.txt")], forOpening: false, null, CancellationToken.None)[0];
        var innerCopy = ArchiveBrowser.CopyForOpening(ArchiveLocation.For(secret, "Inner.zip"));
        var deep = ArchiveBrowser.CopyForOpening(ArchiveLocation.For(innerCopy, "deep.txt"));
        var plain = ArchiveBrowser.CopyForOpening(ArchiveLocation.For(other, "plain.txt"));
        Assert.AreEqual("deep secret", File.ReadAllText(deep));

        ArchiveBrowser.RemoveCopiesFrom(At("Private"));

        Assert.IsFalse(File.Exists(diary), "the copy that was opened");
        Assert.IsFalse(File.Exists(pasted), "the copy that was made for pasting");
        Assert.IsFalse(File.Exists(innerCopy), "the ZIP that was inside it");
        Assert.IsFalse(File.Exists(deep), "and what was opened out of that one");
        Assert.IsTrue(File.Exists(plain), "copies from ZIP files elsewhere are left alone");

        ArchiveBrowser.RemoveCopiesFrom(At("no such folder")); // nothing to do is not an error
    }

    // ---- the inside of a ZIP is read-only

    [TestMethod]
    public void ThingsInsideAZipHaveNoTagsOrLocks()
    {
        var item = FileSystemItem.FromArchiveEntry(ArchiveLocation.For(@"C:\a.zip", "secret.txt.cslock"), "secret.txt.cslock", false, 10, Stamp);

        Assert.IsTrue(item.IsInArchive);
        item.RefreshTags();   // would look the address up in the tag database and the lock list otherwise
        item.RefreshLock();
        Assert.IsFalse(item.HasTags);
        Assert.IsFalse(item.HasLockBadge, "a .cslock name inside a ZIP is only a name");

        Assert.IsFalse(FileSystemItem.FromArchiveEntry(ArchiveLocation.For(@"C:\a.zip", "Trip"), "Trip", true, 0, DateTime.MinValue).IsDriveRoot);
        Assert.AreEqual("3 folders, 1 files",
            SearchCoordinator.DescribeBrowsing(ArchiveLocation.For(@"C:\a.zip"), ArchiveBrowser.List(ArchiveLocation.For(Photos()))));
    }

    [TestMethod]
    public void CutRenameDeleteAndPasteAreOffInsideAZip()
    {
        var zip = Photos();
        var context = new ExplorerContext { Navigation = new NavigationService() };
        context.CurrentPath = ArchiveLocation.For(zip);
        context.SelectedItems = ArchiveBrowser.List(ArchiveLocation.For(zip));

        Assert.IsTrue(context.SelectionInArchive);
        Assert.IsFalse(new DeleteAction(context).IsExecutable);
        Assert.IsFalse(new DeletePermanentlyAction(context).IsExecutable);
        Assert.IsFalse(new RenameAction(context).IsExecutable);
        Assert.IsFalse(new CutItemAction(context).IsExecutable);
        Assert.IsFalse(new PasteItemAction(context).IsExecutable);
        Assert.IsFalse(new NewFolderAction(context).IsExecutable);
        Assert.IsFalse(new ShowPropertiesAction(context).IsExecutable);
        Assert.IsTrue(new CopyItemAction(context).IsExecutable, "copying out is allowed");
        Assert.IsTrue(new OpenItemAction(context).IsExecutable);

        // With nothing selected, Properties is about the ZIP file itself.
        context.SelectedItems = [];
        Assert.IsTrue(new ShowPropertiesAction(context).IsExecutable);

        // Outside a ZIP nothing changed.
        context.CurrentPath = _root;
        context.SelectedItems = [FileSystemItem.FromLocation(zip)!];
        Assert.IsFalse(context.SelectionInArchive);
        Assert.IsTrue(new DeleteAction(context).IsExecutable);
        Assert.IsTrue(new RenameAction(context).IsExecutable);
        Assert.IsTrue(new CutItemAction(context).IsExecutable);
    }

    [TestMethod]
    public async Task OpeningAZipFileGoesInsideIt()
    {
        var zip = Photos();
        var navigation = new NavigationService();
        var context = new ExplorerContext { Navigation = navigation };
        navigation.Navigate(_root);

        context.SelectedItems = [FileSystemItem.FromLocation(zip)!];
        await new OpenItemAction(context).ExecuteAsync();
        Assert.AreEqual(ArchiveLocation.For(zip), navigation.CurrentPath);

        // A folder inside it opens like any folder.
        context.SelectedItems = [ArchiveBrowser.List(ArchiveLocation.For(zip)).Single(item => item.Name == "Trip")];
        await new OpenItemAction(context).ExecuteAsync();
        Assert.AreEqual(ArchiveLocation.For(zip, "Trip"), navigation.CurrentPath);
    }

    [TestMethod]
    public async Task OpeningAFileInsideAZipAsksForACopyAndNeverStartsTheAddressItself()
    {
        var outer = MakeZip("Outer.zip", ("notes.txt", "notes"), ("Inner.zip", "stands in for a zip"));
        var navigation = new NavigationService();
        var context = new ExplorerContext { Navigation = navigation };
        navigation.Navigate(ArchiveLocation.For(outer));
        var items = ArchiveBrowser.List(ArchiveLocation.For(outer));

        // No window to make copies (as here): nothing happens, and above all nothing is started.
        context.SelectedItems = [items.Single(item => item.Name == "notes.txt")];
        await new OpenItemAction(context).ExecuteAsync();
        Assert.AreEqual(ArchiveLocation.For(outer), navigation.CurrentPath);

        // A ZIP inside a ZIP opens in the list from its copy.
        var asked = new List<(string Name, bool ForOpening)>();
        var copy = At("copy of Inner.zip");
        context.ArchiveCopies = (wanted, forOpening) =>
        {
            asked.Add((wanted[0].Name, forOpening));
            return [copy];
        };
        context.SelectedItems = [items.Single(item => item.Name == "Inner.zip")];
        await new OpenItemAction(context).ExecuteAsync();

        CollectionAssert.AreEqual(new[] { ("Inner.zip", true) }, asked);
        Assert.AreEqual(ArchiveLocation.For(copy), navigation.CurrentPath);

        // The window could not make the copy (it has said why): still nothing is started.
        context.ArchiveCopies = (_, _) => null;
        context.SelectedItems = [items.Single(item => item.Name == "notes.txt")];
        await new OpenItemAction(context).ExecuteAsync();
        Assert.AreEqual(ArchiveLocation.For(copy), navigation.CurrentPath);
    }

    private sealed class Recorder(List<ArchiveProgress> reports) : IProgress<ArchiveProgress>
    {
        public void Report(ArchiveProgress value) => reports.Add(value);
    }
}
