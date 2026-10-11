// NEW (undo): the parts of undo that can be checked without Windows' own dialogs.
//  - RecycleBin.TryReadNote: reading the $I note Windows writes for each item in the Recycle Bin
//    (where it was deleted from, and when), in both layouts.
//  - RenamedStep.RenameAll: giving old names back, including when two items swapped names, when only the
//    capitals changed, and when something else has taken a name since (that item is left alone).
//  - UndoHistory: a new change ends what could be redone; Pause() keeps housekeeping out of the history.
using System.IO;
using System.Text;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class UndoTests
{
    private static byte[] Note(long version, DateTime deletedUtc, string path)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(version));
        bytes.AddRange(BitConverter.GetBytes(1234L)); // size
        bytes.AddRange(BitConverter.GetBytes(deletedUtc.ToFileTimeUtc()));
        if (version >= 2)
        {
            bytes.AddRange(BitConverter.GetBytes(path.Length + 1));
            bytes.AddRange(Encoding.Unicode.GetBytes(path + "\0"));
        }
        else
        {
            var fixedPath = new byte[520];
            Encoding.Unicode.GetBytes(path).CopyTo(fixedPath, 0);
            bytes.AddRange(fixedPath);
        }

        return [.. bytes];
    }

    [TestMethod]
    public void TheRecycleBinsNotesAreReadInBothLayouts()
    {
        var when = new DateTime(2026, 10, 11, 19, 30, 5, DateTimeKind.Utc);

        Assert.IsTrue(RecycleBin.TryReadNote(Note(2, when, @"C:\Work\report.docx"), out var from, out var at));
        Assert.AreEqual(@"C:\Work\report.docx", from);
        Assert.AreEqual(when, at);

        Assert.IsTrue(RecycleBin.TryReadNote(Note(1, when, @"D:\Old\photo.jpg"), out from, out _), "the Windows 8 layout");
        Assert.AreEqual(@"D:\Old\photo.jpg", from);

        Assert.IsFalse(RecycleBin.TryReadNote([1, 2, 3], out _, out _), "too short to be a note");
    }

    [TestMethod]
    public void OldNamesComeBackEvenWhenTheyWereSwapped()
    {
        var folder = Directory.CreateTempSubdirectory("clearspace-undo-").FullName;
        try
        {
            var a = Path.Combine(folder, "a.txt");
            var b = Path.Combine(folder, "b.txt");
            File.WriteAllText(a, "was b");
            File.WriteAllText(b, "was a");

            // The rename swapped them: a.txt -> b.txt and b.txt -> a.txt. Undo swaps them back.
            Assert.IsNull(RenamedStep.RenameAll([(Now: b, Then: a), (Now: a, Then: b)]));
            Assert.AreEqual("was a", File.ReadAllText(a));
            Assert.AreEqual("was b", File.ReadAllText(b));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void OnlyTheCapitalsCanBeGivenBack()
    {
        var folder = Directory.CreateTempSubdirectory("clearspace-undo-").FullName;
        try
        {
            var now = Path.Combine(folder, "REPORT.txt");
            File.WriteAllText(now, "x");

            Assert.IsNull(RenamedStep.RenameAll([(Now: now, Then: Path.Combine(folder, "report.txt"))]));
            Assert.AreEqual("report.txt", Path.GetFileName(Directory.GetFiles(folder).Single()));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void AnItemWhoseOldNameIsTakenIsLeftAlone()
    {
        var folder = Directory.CreateTempSubdirectory("clearspace-undo-").FullName;
        try
        {
            var renamed = Path.Combine(folder, "new.txt");
            var taken = Path.Combine(folder, "old.txt");
            File.WriteAllText(renamed, "mine");
            File.WriteAllText(taken, "someone else's");

            var words = RenamedStep.RenameAll([(Now: renamed, Then: taken)]);
            Assert.IsNotNull(words, "says why");
            Assert.AreEqual("mine", File.ReadAllText(renamed));
            Assert.AreEqual("someone else's", File.ReadAllText(taken), "nothing is replaced");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void ANewChangeEndsWhatCouldBeRedone_AndPausedChangesAreNotKept()
    {
        UndoHistory.Clear();
        var folder = Directory.CreateTempSubdirectory("clearspace-undo-").FullName;
        try
        {
            var first = Path.Combine(folder, "one.txt");
            var second = Path.Combine(folder, "two.txt");
            File.WriteAllText(second, "x");

            UndoHistory.Record(new RenamedStep([(From: first, To: second)]));
            Assert.IsTrue(UndoHistory.CanUndo);
            Assert.AreEqual("rename of one.txt", UndoHistory.UndoWords);

            UndoHistory.Undo(IntPtr.Zero); // a rename is done on the disk directly: no window needed
            Assert.IsTrue(File.Exists(first));
            Assert.IsTrue(UndoHistory.CanRedo);

            using (UndoHistory.Pause())
                UndoHistory.Record(new RenamedStep([(From: second, To: first)]));
            Assert.IsTrue(UndoHistory.CanRedo, "a paused change is not recorded, so it does not end the redo");

            UndoHistory.Record(new RenamedStep([(From: second, To: first)]));
            Assert.IsFalse(UndoHistory.CanRedo, "a new change does");
        }
        finally
        {
            UndoHistory.Clear();
            Directory.Delete(folder, recursive: true);
        }
    }
}
