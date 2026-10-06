// NEW (recommended folders): which folders the folder chooser recommends for a file, and why
// (Services/FilingAdvisor.cs), plus the list of recently used folders (FilingMemory.NoteRecent).
// Nothing here reads the disk, the index or the settings file: the clues are handed in.
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class FilingAdvisorTests
{
    private const string Downloads = @"C:\Users\me\Downloads";
    private const string Repos = @"E:\Repos";
    private const string PlayLedger = @"E:\Repos\PlayLedger";
    private const string Pictures = @"C:\Users\me\Pictures";
    private const string School = @"D:\School";
    private const string Taxes = @"D:\Taxes";

    private static bool Anywhere(string folder) => true;

    private static string[] Folders(IReadOnlyList<FolderRecommendation> picks) => picks.Select(pick => pick.Folder).ToArray();

    // ---- names

    [TestMethod]
    public void TheTellingWordsOfANameAreKept()
    {
        CollectionAssert.AreEqual(new[] { "playledger" }, FilingAdvisor.Words("PlayLedger-main").ToArray());
        CollectionAssert.AreEqual(new[] { "chatgpt" }, FilingAdvisor.Words("ChatGPT Image Oct 4, 2026, 01_38_32 PM").ToArray());
        CollectionAssert.AreEqual(new[] { "lilith", "carter", "gothic", "character", "reference" },
            FilingAdvisor.Words("Lilith Carter Gothic Character Reference").ToArray());
        CollectionAssert.AreEqual(new[] { "muru", "larens" }, FilingAdvisor.Words("MURU_2.3_Larens_v2").ToArray());
        Assert.AreEqual(0, FilingAdvisor.Words("New folder (2)").Count);
    }

    [TestMethod]
    public void AFolderNamedInTheFileIsAStrongMatch()
    {
        var words = FilingAdvisor.Words("PlayLedger-main");

        Assert.IsTrue(FilingAdvisor.NameMatch(words, "PlayLedger") >= FilingAdvisor.StrongMatch);
        Assert.IsTrue(FilingAdvisor.NameMatch(words, "playledger-main") >= FilingAdvisor.StrongMatch, "Filler words are not counted against it.");
        Assert.IsTrue(FilingAdvisor.NameMatch(words, "Play Ledger") >= FilingAdvisor.StrongMatch, "The same name written apart.");
        Assert.AreEqual(FilingAdvisor.WeakMatch, FilingAdvisor.NameMatch(words, "PlayLedger docs"));
        Assert.AreEqual(0, FilingAdvisor.NameMatch(words, "Ledgers"));
        Assert.AreEqual(0, FilingAdvisor.NameMatch(words, "2026"));
        Assert.AreEqual(0, FilingAdvisor.NameMatch([], "PlayLedger"));
    }

    // ---- ranking

    [TestMethod]
    public void AFolderNamedLikeTheFileIsRecommended()
    {
        var clues = new FilingClues("PlayLedger-main.zip")
        {
            CurrentFolder = Downloads,
            NamedFolders = [Repos, PlayLedger, School]
        };

        var picks = FilingAdvisor.Rank(clues, Anywhere);

        CollectionAssert.AreEqual(new[] { PlayLedger }, Folders(picks));
        Assert.AreEqual("Its name is in the file's name", picks[0].Why);
        Assert.AreEqual("PlayLedger", picks[0].FolderName);
    }

    [TestMethod]
    public void WhereFilesLikeItWentBeforeComesFirst()
    {
        var habits = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        FilingMemory.Learn(habits, [("canvas.edu", ".pdf")], School);
        FilingMemory.Learn(habits, [("canvas.edu", ".pdf")], School);
        FilingMemory.Learn(habits, [("irs.gov", ".pdf")], Taxes);

        var clues = new FilingClues("syllabus.pdf")
        {
            Site = "canvas.edu",
            CurrentFolder = Downloads,
            Habits = habits,
            KindFolders = [(@"C:\Users\me\Documents", "Documents, for a document")]
        };

        var picks = FilingAdvisor.Rank(clues, Anywhere);

        Assert.AreEqual(School, picks[0].Folder);
        Assert.AreEqual(".pdf files from canvas.edu have gone here 2 times", picks[0].Why);
        // Taxes has only ever had a .pdf from elsewhere: it is still worth a line, below the others' sum.
        CollectionAssert.Contains(Folders(picks), Taxes);
        CollectionAssert.Contains(Folders(picks), @"C:\Users\me\Documents");
    }

    [TestMethod]
    public void WithNothingElseKnownTheKindOfFileDecides()
    {
        var clues = new FilingClues("holiday.jpg") { CurrentFolder = Downloads, KindFolders = [(Pictures, "Pictures, for an image")] };

        var picks = FilingAdvisor.Rank(clues, Anywhere);

        CollectionAssert.AreEqual(new[] { Pictures }, Folders(picks));
        Assert.AreEqual("Pictures, for an image", picks[0].Why);
    }

    [TestMethod]
    public void RecentFoldersAreOfferedLatestFirstAndOnlyTheLatestFew()
    {
        var clues = new FilingClues("thing.bin")
        {
            CurrentFolder = Downloads,
            RecentFolders = [School, Taxes, Repos, Pictures, PlayLedger]
        };

        var picks = FilingAdvisor.Rank(clues, Anywhere, most: 10);

        CollectionAssert.AreEqual(new[] { School, Taxes, Repos }, Folders(picks), "The fourth and fifth are too long ago to be worth a line.");
        Assert.AreEqual("You moved a file here recently", picks[0].Why);
    }

    [TestMethod]
    public void SeveralReasonsAddUpAndTheStrongestIsShown()
    {
        var clues = new FilingClues("PlayLedger-main.zip")
        {
            CurrentFolder = Downloads,
            NamedFolders = [PlayLedger],
            RecentFolders = [School, PlayLedger]
        };

        var picks = FilingAdvisor.Rank(clues, Anywhere);

        Assert.AreEqual(PlayLedger, picks[0].Folder, "Named like the file and used recently beats used most recently.");
        Assert.AreEqual("Its name is in the file's name", picks[0].Why);
        Assert.AreEqual(School, picks[1].Folder);
        Assert.IsTrue(picks[0].Score > picks[1].Score);
    }

    [TestMethod]
    public void TheFolderTheFileIsInAndFoldersThatAreGoneAreNeverRecommended()
    {
        var clues = new FilingClues("holiday.jpg")
        {
            CurrentFolder = Pictures + @"\",
            KindFolders = [(Pictures, "Pictures, for an image")],
            RecentFolders = [School, Taxes]
        };

        var picks = FilingAdvisor.Rank(clues, folder => folder != School);

        CollectionAssert.AreEqual(new[] { Taxes }, Folders(picks));
    }

    [TestMethod]
    public void OnlyTheBestFewAreReturnedAndOneFolderIsListedOnce()
    {
        var clues = new FilingClues("report.pdf")
        {
            CurrentFolder = Downloads,
            RecentFolders = [School, @"d:\school\", Taxes, Repos],
            KindFolders = [(School, "One of your Documents folders")]
        };

        var picks = FilingAdvisor.Rank(clues, Anywhere, most: 2);

        Assert.AreEqual(2, picks.Count);
        Assert.AreEqual(School, picks[0].Folder);
        Assert.AreEqual(1, picks.Count(pick => pick.Folder.Equals(School, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void NothingKnownMeansNothingRecommended()
        => Assert.AreEqual(0, FilingAdvisor.Rank(new FilingClues("thing.bin") { CurrentFolder = Downloads }, Anywhere).Count);

    [TestMethod]
    public void FoldersNobodyFilesIntoAreLeftOut()
    {
        Assert.IsTrue(FilingAdvisor.IsNoise(@"C:\Windows\System32\PlayLedger"));
        Assert.IsTrue(FilingAdvisor.IsNoise(@"C:\Users\me\AppData\Local\PlayLedger"));
        Assert.IsTrue(FilingAdvisor.IsNoise(@"E:\Repos\app\node_modules\playledger"));
        Assert.IsTrue(FilingAdvisor.IsNoise(@"E:\Repos\PlayLedger\.git\refs"));
        Assert.IsTrue(FilingAdvisor.IsNoise(@"C:\$Recycle.Bin\S-1-5\PlayLedger"));
        Assert.IsFalse(FilingAdvisor.IsNoise(PlayLedger));
        Assert.IsFalse(FilingAdvisor.IsNoise(@"C:\Users\me\Documents\Windows notes"));
    }

    // ---- the recently used list

    [TestMethod]
    public void TheLatestFolderGoesToTheFrontOnce()
    {
        var recent = new List<string>();

        Assert.IsTrue(FilingMemory.NoteRecent(recent, School));
        Assert.IsTrue(FilingMemory.NoteRecent(recent, Taxes));
        Assert.IsFalse(FilingMemory.NoteRecent(recent, Taxes + @"\"), "Already at the front: nothing to save.");
        Assert.IsTrue(FilingMemory.NoteRecent(recent, @"d:\school"));

        Assert.AreEqual(2, recent.Count);
        Assert.IsTrue(recent[0].Equals(School, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(Taxes, recent[1]);
    }

    [TestMethod]
    public void TheRecentListStaysShort()
    {
        var recent = new List<string>();
        for (var i = 0; i < FilingMemory.MostRecentFolders + 5; i++)
            FilingMemory.NoteRecent(recent, $@"D:\Folder {i}");

        Assert.AreEqual(FilingMemory.MostRecentFolders, recent.Count);
        Assert.AreEqual($@"D:\Folder {FilingMemory.MostRecentFolders + 4}", recent[0]);
    }
}
