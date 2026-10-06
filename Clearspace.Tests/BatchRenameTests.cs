// NEW (batch rename): the naming rules and the renaming itself (Services/BatchRename.cs).
// Nothing here touches the disk: names are worked out from paths, and renaming runs against a small
// stand-in that behaves like a Windows folder (capitals don't matter, a taken name is refused).
using System.IO;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class BatchRenameTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "Clearspace batch rename");
    private static readonly DateTime Modified = new(2026, 10, 2, 9, 30, 0);

    private static string At(string name) => Path.Combine(Folder, name);
    private static RenameSource FileNamed(string name) => new(At(name), false, Modified);
    private static RenameSource FolderNamed(string name) => new(At(name), true, Modified);
    private static string[] Names(IReadOnlyList<RenameStep> steps) => steps.Select(step => step.NewName).ToArray();
    private static bool NothingThere(string path) => false;

    private static RenameStep Step(string from, string to) => new(FileNamed(from), from, to, At(to), null);

    // A folder on a Windows disk, reduced to what renaming needs.
    private sealed class Disk
    {
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? RefuseNewName { get; set; }
        public int Moves { get; private set; }

        public Disk(params string[] names)
        {
            foreach (var name in names)
                Paths.Add(At(name));
        }

        public void Move(string from, string to)
        {
            if (RefuseNewName is not null && Path.GetFileName(to).Equals(RefuseNewName, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The disk refused " + RefuseNewName + ".");
            if (!Paths.Contains(from))
                throw new FileNotFoundException("Not there: " + from);
            if (Paths.Contains(to))
                throw new IOException("Already there: " + to);

            Paths.Remove(from);
            Paths.Add(to);
            Moves++;
        }

        public string[] Names => Paths.Select(path => Path.GetFileName(path)!).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    [TestMethod]
    public void PatternNumbersItemsInOrderAndKeepsEachEnding()
    {
        var sources = new[] { FileNamed("a.jpg"), FileNamed("b.PNG"), FolderNamed("c.old") };
        var plan = BatchRename.Plan(sources, new RenameOptions { Pattern = "Holiday {n:2}", Start = 9 }, NothingThere);

        CollectionAssert.AreEqual(new[] { "Holiday 09.jpg", "Holiday 10.PNG", "Holiday 11" }, Names(plan),
            "a folder has no ending: the whole name is replaced");
        Assert.IsTrue(BatchRename.CanApply(plan));
        Assert.AreEqual(At("Holiday 09.jpg"), plan[0].NewPath);
    }

    [TestMethod]
    public void PatternWithoutNameOrNumberIsNumberedLikeExplorer()
    {
        var plan = BatchRename.Plan([FileNamed("a.jpg"), FileNamed("b.jpg")], new RenameOptions { Pattern = "Trip" }, NothingThere);

        CollectionAssert.AreEqual(new[] { "Trip (1).jpg", "Trip (2).jpg" }, Names(plan));
        Assert.AreEqual("{name}", BatchRename.EffectivePattern("   "), "an empty pattern keeps the names");
        Assert.AreEqual("{name} copy", BatchRename.EffectivePattern("{name} copy"));
    }

    [TestMethod]
    public void FindAndReplaceIgnoresCapitalsUnlessAsked()
    {
        var sources = new[] { FileNamed("IMG_001.jpg") };

        var loose = BatchRename.Plan(sources, new RenameOptions { Find = "img_", Replace = "Photo " }, NothingThere);
        Assert.AreEqual("Photo 001.jpg", loose[0].NewName);

        var exact = BatchRename.Plan(sources, new RenameOptions { Find = "img_", Replace = "Photo ", MatchCase = true }, NothingThere);
        Assert.AreEqual("IMG_001.jpg", exact[0].NewName);
        Assert.IsFalse(exact[0].Changes);
        Assert.IsFalse(BatchRename.CanApply(exact), "nothing changes, so there is nothing to rename");

        var ending = BatchRename.Plan(sources, new RenameOptions { Find = "jpg", Replace = "png" }, NothingThere);
        Assert.AreEqual("IMG_001.jpg", ending[0].NewName, "find and replace never touches the ending");
    }

    [TestMethod]
    public void DateTokenAndCapitals()
    {
        var sources = new[] { FileNamed("my summer notes.txt") };

        Assert.AreEqual("2026-10-02 my summer notes.txt",
            BatchRename.Plan(sources, new RenameOptions { Pattern = "{date} {name}" }, NothingThere)[0].NewName);
        Assert.AreEqual("MY SUMMER NOTES.txt",
            BatchRename.Plan(sources, new RenameOptions { Case = RenameCase.Upper }, NothingThere)[0].NewName);
        Assert.AreEqual("My Summer Notes.txt",
            BatchRename.Plan(sources, new RenameOptions { Case = RenameCase.Title }, NothingThere)[0].NewName);
        Assert.AreEqual("{album} my summer notes.txt",
            BatchRename.Plan(sources, new RenameOptions { Pattern = "{album} {name}" }, NothingThere)[0].NewName,
            "a brace that is not one of the tokens is kept as typed");
    }

    [TestMethod]
    public void LockedFileKeepsBothOfItsEndings()
    {
        Assert.AreEqual(("photo", ".jpg.cslock"), BatchRename.SplitName("photo.jpg.cslock", isFolder: false));
        Assert.AreEqual(("notes", ".cslock"), BatchRename.SplitName("notes.cslock", isFolder: false));
        Assert.AreEqual(("archive.tar", ".gz"), BatchRename.SplitName("archive.tar.gz", isFolder: false));
        Assert.AreEqual(("v1.2", string.Empty), BatchRename.SplitName("v1.2", isFolder: true));

        var plan = BatchRename.Plan([FileNamed("photo.jpg.cslock")], new RenameOptions { Pattern = "Beach {n}" }, NothingThere);
        Assert.AreEqual("Beach 1.jpg.cslock", plan[0].NewName);
    }

    [TestMethod]
    public void NamesWindowsRefusesAreReportedBeforeAnythingIsRenamed()
    {
        Assert.IsNull(BatchRename.Validate("Report 2026.txt"));
        Assert.IsNotNull(BatchRename.Validate("a:b.txt"));
        Assert.IsNotNull(BatchRename.Validate("   "));
        Assert.IsNotNull(BatchRename.Validate("ends with a dot."));
        Assert.IsNotNull(BatchRename.Validate("CON.txt"));
        Assert.IsNotNull(BatchRename.Validate("nul"));
        Assert.IsNull(BatchRename.Validate("CONTENTS.txt"));

        var plan = BatchRename.Plan([FileNamed("a.txt"), FileNamed("b.txt")], new RenameOptions { Pattern = "what? {n}" }, NothingThere);
        Assert.IsTrue(plan.All(step => step.Problem is not null));
        Assert.IsFalse(BatchRename.CanApply(plan));

        var emptied = BatchRename.Plan([FileNamed("a.txt")], new RenameOptions { Find = "a", Replace = "" }, NothingThere);
        Assert.IsNotNull(emptied[0].Problem, "nothing would be left in front of .txt");

        var dotfile = BatchRename.Plan([FileNamed(".gitignore"), FileNamed("b.txt")], new RenameOptions { Find = "b", Replace = "c" }, NothingThere);
        Assert.IsNull(dotfile[0].Problem, "a name that is all ending may stay as it is");
        Assert.IsTrue(BatchRename.CanApply(dotfile));
    }

    [TestMethod]
    public void ClashesAreReportedButSwapsAreNot()
    {
        // Two items would end up with the same name.
        var same = BatchRename.Plan([FileNamed("a.txt"), FileNamed("b.txt")], new RenameOptions { Find = "a", Replace = "b" }, NothingThere);
        Assert.IsNotNull(same[0].Problem);
        Assert.IsNotNull(same[1].Problem, "the item that keeps its name is in the way too");

        // The new name belongs to something that is not being renamed.
        var taken = BatchRename.Plan([FileNamed("a.txt")], new RenameOptions { Find = "a", Replace = "taken" },
            path => path.Equals(At("taken.txt"), StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(taken[0].Problem);

        // 1 -> 2 while 2 -> 3: the name "2" is free by the time it is needed, so the disk is not asked about it.
        var shifted = BatchRename.Plan([FileNamed("Page 1.txt"), FileNamed("Page 2.txt")],
            new RenameOptions { Pattern = "Page {n}", Start = 2 },
            path => path.Equals(At("Page 2.txt"), StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(shifted.All(step => step.Problem is null));
        CollectionAssert.AreEqual(new[] { "Page 2.txt", "Page 3.txt" }, Names(shifted));
    }

    [TestMethod]
    public void PlainRenamesGoStraightToTheirNewNames()
    {
        var disk = new Disk("a.txt", "b.txt");
        var outcome = BatchRename.Apply([Step("a.txt", "x.txt"), Step("b.txt", "y.txt")], disk.Move);

        Assert.IsNull(outcome.Error);
        CollectionAssert.AreEqual(new[] { "x.txt", "y.txt" }, disk.Names);
        Assert.AreEqual(2, disk.Moves, "no temporary names when nothing is in the way");
        CollectionAssert.AreEqual(new[] { (At("a.txt"), At("x.txt")), (At("b.txt"), At("y.txt")) }, outcome.Renamed.ToArray());
    }

    [TestMethod]
    public void SwappedAndShiftedNamesGoThroughTemporaryNames()
    {
        var swap = new Disk("1.txt", "2.txt");
        var swapped = BatchRename.Apply([Step("1.txt", "2.txt"), Step("2.txt", "1.txt")], swap.Move);
        Assert.IsNull(swapped.Error);
        CollectionAssert.AreEqual(new[] { "1.txt", "2.txt" }, swap.Names);
        Assert.AreEqual(4, swap.Moves, "each item steps aside, then takes its new name");

        var capitals = new Disk("readme.txt");
        var renamed = BatchRename.Apply([Step("readme.txt", "README.txt")], capitals.Move);
        Assert.IsNull(renamed.Error, "changing only the capitals is a rename onto the item's own name");
        CollectionAssert.AreEqual(new[] { "README.txt" }, capitals.Names);
    }

    [TestMethod]
    public void AFailureHalfwayPutsEveryNameBack()
    {
        var disk = new Disk("a.txt", "b.txt", "c.txt") { RefuseNewName = "y.txt" };
        var outcome = BatchRename.Apply([Step("a.txt", "x.txt"), Step("b.txt", "y.txt"), Step("c.txt", "z.txt")], disk.Move);

        Assert.IsNotNull(outcome.Error);
        Assert.AreEqual(0, outcome.Renamed.Count);
        CollectionAssert.AreEqual(new[] { "a.txt", "b.txt", "c.txt" }, disk.Names);

        var staged = new Disk("1.txt", "2.txt") { RefuseNewName = "1.txt" };
        var failed = BatchRename.Apply([Step("1.txt", "2.txt"), Step("2.txt", "1.txt")], staged.Move);
        Assert.IsNotNull(failed.Error);
        // Putting "1.txt" back is refused by the same rule, so this stand-in leaves it parked; what matters
        // is that the failure is reported and nothing claims to have been renamed.
        Assert.AreEqual(0, failed.Renamed.Count);
    }

    [TestMethod]
    public void ItemsWithAProblemOrNoChangeAreLeftAlone()
    {
        var disk = new Disk("a.txt", "b.txt");
        var steps = new[]
        {
            Step("a.txt", "a.txt"),                                  // keeps its name
            Step("b.txt", "c.txt") with { Problem = "not allowed" }  // refused by the plan
        };

        var outcome = BatchRename.Apply(steps, disk.Move);
        Assert.IsNull(outcome.Error);
        Assert.AreEqual(0, outcome.Renamed.Count);
        Assert.AreEqual(0, disk.Moves);
    }
}
