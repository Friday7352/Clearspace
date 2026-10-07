// NEW (move in Clearspace): which folders Clearspace's own folder chooser lists (FolderPickerWindow.ReadFolders).
// NEW (pick a file): and which files, when it is picking a file such as a backup (FolderPickerWindow.ReadFiles).
// Uses real folders in a temporary folder that is removed again after each test.
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class FolderPickerTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void MakeFolder()
    {
        _root = Path.Combine(Path.GetTempPath(), "Clearspace folder picker " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void RemoveFolder()
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(_root))
                new DirectoryInfo(folder).Attributes = FileAttributes.Directory; // hidden ones delete like the rest
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Make(string name, FileAttributes extra = 0)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        if (extra != 0)
            new DirectoryInfo(path).Attributes |= extra;
        return path;
    }

    private string[] Names(bool showHidden)
        => FolderPickerWindow.ReadFolders(_root, showHidden).Select(path => Path.GetFileName(path)!).ToArray();

    [TestMethod]
    public void FoldersAreListedAndFilesAreNot()
    {
        Make("Invoices");
        Make("Photos");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not a folder");

        CollectionAssert.AreEqual(new[] { "Invoices", "Photos" }, Names(showHidden: false));
    }

    [TestMethod]
    public void NumbersInNamesCountAsNumbers()
    {
        Make("Batch 10");
        Make("Batch 2");
        Make("batch 1");

        CollectionAssert.AreEqual(new[] { "batch 1", "Batch 2", "Batch 10" }, Names(showHidden: false));
    }

    [TestMethod]
    public void HiddenFoldersShowOnlyWhenAskedFor()
    {
        Make("Seen");
        Make("Tucked away", FileAttributes.Hidden);

        CollectionAssert.AreEqual(new[] { "Seen" }, Names(showHidden: false));
        CollectionAssert.AreEqual(new[] { "Seen", "Tucked away" }, Names(showHidden: true));
    }

    [TestMethod]
    public void FoldersWindowsProtectsNeverShow()
    {
        Make("Seen");
        Make("Protected", FileAttributes.Hidden | FileAttributes.System);

        CollectionAssert.AreEqual(new[] { "Seen" }, Names(showHidden: true));
    }

    [TestMethod]
    public void AFolderMarkedSystemAloneStillShows()
    {
        // Windows marks folders with a custom icon this way; they are ordinary folders.
        Make("Customised", FileAttributes.System);

        CollectionAssert.AreEqual(new[] { "Customised" }, Names(showHidden: false));
    }

    [TestMethod]
    public void AnEmptyFolderGivesAnEmptyList()
        => Assert.AreEqual(0, Names(showHidden: false).Length);

    [TestMethod]
    public void AFolderThatIsNotThereIsReportedNotSwallowed()
        => Assert.ThrowsException<DirectoryNotFoundException>(
            () => FolderPickerWindow.ReadFolders(Path.Combine(_root, "missing"), showHidden: false));

    // ---- NEW (pick a file): the files listed when the chooser is picking a file

    private static readonly DateTime Monday = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

    private void MakeFile(string name, DateTime changedUtc, FileAttributes extra = 0)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, changedUtc);
        if (extra != 0)
            File.SetAttributes(path, File.GetAttributes(path) | extra);
    }

    private string[] FileNames(bool showHidden)
        => FolderPickerWindow.ReadFiles(_root, ".csbackup", showHidden).Select(path => Path.GetFileName(path)!).ToArray();

    [TestMethod]
    public void OnlyFilesWithTheEndingAreListedNewestFirst()
    {
        MakeFile("oldest.csbackup", Monday.AddDays(-9));
        MakeFile("newest.CSBACKUP", Monday);
        MakeFile("between.csbackup", Monday.AddDays(-2));
        MakeFile("notes.txt", Monday);
        MakeFile("almost.csbackup2", Monday);
        Make("A folder.csbackup");

        CollectionAssert.AreEqual(new[] { "newest.CSBACKUP", "between.csbackup", "oldest.csbackup" }, FileNames(showHidden: false));
    }

    [TestMethod]
    public void HiddenFilesShowOnlyWhenAskedFor()
    {
        MakeFile("seen.csbackup", Monday);
        MakeFile("tucked away.csbackup", Monday.AddDays(-1), FileAttributes.Hidden);

        CollectionAssert.AreEqual(new[] { "seen.csbackup" }, FileNames(showHidden: false));
        CollectionAssert.AreEqual(new[] { "seen.csbackup", "tucked away.csbackup" }, FileNames(showHidden: true));
    }

    [TestMethod]
    public void AFolderWithNoSuchFilesGivesNoFiles()
    {
        MakeFile("notes.txt", Monday);
        Make("Invoices");

        Assert.AreEqual(0, FileNames(showHidden: false).Length);
    }
}
