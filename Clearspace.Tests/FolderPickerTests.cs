// NEW (move in Clearspace): which folders Clearspace's own folder chooser lists (FolderPickerWindow.ReadFolders).
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
}
