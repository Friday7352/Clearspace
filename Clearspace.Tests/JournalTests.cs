using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using Clearspace.Journal;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

// NEW (journal catch-up): the parts of journal catch-up that run without administrator rights.
// Reading a real journal is covered by the manual check in docs/Search-and-Indexing-Plan.md.
[TestClass]
public sealed class JournalTests
{
    private static readonly UInt128 Root = 5u, Docs = 100u, Old = 200u, Photos = 300u;

    private static JournalRecord Record(UInt128 id, UInt128 parent, string name, uint reasons, bool folder = false, long usn = 0)
        => new(id, parent, name, reasons, folder ? 0x10u : 0x20u, usn);

    private static Func<UInt128, string?> Disk(Dictionary<UInt128, string> existing) => id => existing.GetValueOrDefault(id);

    [TestMethod]
    public void CollectsEachChangedPathOnceInFirstSeenOrder()
    {
        var records = new[]
        {
            Record(1000u, Docs, "report.docx", UsnJournal.FileCreate),
            Record(1000u, Docs, "report.docx", UsnJournal.DataExtend),
            Record(1001u, Docs, "notes.txt", UsnJournal.DataOverwrite),
            Record(1000u, Docs, "report.docx", UsnJournal.DataExtend | 0x80000000) // + CLOSE
        };
        var (paths, unresolved) = ChangePlanner.Collect(records, Disk(new() { [Docs] = @"C:\Docs" }));
        CollectionAssert.AreEqual(new[] { @"C:\Docs\report.docx", @"C:\Docs\notes.txt" }, paths);
        Assert.AreEqual(0, unresolved);
    }

    [TestMethod]
    public void RenamesProduceBothTheOldAndTheNewPath()
    {
        var records = new[]
        {
            Record(1000u, Docs, "draft.txt", UsnJournal.RenameOldName),
            Record(1000u, Photos, "final.txt", UsnJournal.RenameNewName)
        };
        var (paths, _) = ChangePlanner.Collect(records, Disk(new() { [Docs] = @"C:\Docs", [Photos] = @"C:\Photos" }));
        CollectionAssert.AreEqual(new[] { @"C:\Docs\draft.txt", @"C:\Photos\final.txt" }, paths,
            "The old path is re-checked (and removed); the new one is added.");
    }

    // CHANGED (permission review): names under a folder that no longer exists are not reconstructed from
    // the journal's history. The deleted folder itself is placed under its existing parent, and removing
    // it from the index removes everything below it.
    [TestMethod]
    public void DeletedFoldersAreRemovedThroughTheirOwnRecordOnly()
    {
        var records = new[]
        {
            Record(1000u, Old, "inner.txt", UsnJournal.FileDelete),
            Record(Old, Root, "Old", UsnJournal.FileDelete, folder: true)
        };
        var (paths, unresolved) = ChangePlanner.Collect(records, Disk(new() { [Root] = @"C:\" }));
        CollectionAssert.AreEqual(new[] { @"C:\Old" }, paths);
        Assert.AreEqual(1, unresolved);
    }

    // NEW (permission review): a folder the caller cannot list (the resolver returns null for it) yields no names.
    [TestMethod]
    public void NamesUnderFoldersTheCallerCannotListAreNotReturned()
    {
        var records = new[] { Record(1000u, Docs, "salary.xlsx", UsnJournal.FileCreate) };
        var (paths, unresolved) = ChangePlanner.Collect(records, _ => null);
        Assert.AreEqual(0, paths.Count);
        Assert.AreEqual(1, unresolved);
    }

    [TestMethod]
    public void UnplaceableRecordsAreCountedNotGuessed()
    {
        var records = new[]
        {
            Record(1000u, 999u, "secret.txt", UsnJournal.FileCreate),
            Record(1001u, Docs, "ignored.txt", 0x00000800) // security change only: not index-relevant
        };
        var (paths, unresolved) = ChangePlanner.Collect(records, Disk(new() { [Docs] = @"C:\Docs" }));
        Assert.AreEqual(0, paths.Count);
        Assert.AreEqual(1, unresolved);
    }

    [TestMethod]
    public void ParsesVersion2And3Records()
    {
        var v2 = BuildRecord(2, 0x1234u, 0x5678u, "a.txt", UsnJournal.FileCreate, 0x20, 42);
        Assert.IsTrue(UsnJournal.TryParse(v2, out var first));
        Assert.AreEqual((UInt128)0x1234, first.FileId);
        Assert.AreEqual((UInt128)0x5678, first.ParentId);
        Assert.AreEqual("a.txt", first.Name);
        Assert.AreEqual(42L, first.Usn);
        Assert.AreEqual(UsnJournal.FileCreate, first.Reasons);
        Assert.IsFalse(first.IsDirectory);

        var big = ((UInt128)7u << 64) | 9u;
        var v3 = BuildRecord(3, big, 5u, "Folder", UsnJournal.RenameNewName, 0x10, 99);
        Assert.IsTrue(UsnJournal.TryParse(v3, out var second));
        Assert.AreEqual(big, second.FileId);
        Assert.AreEqual("Folder", second.Name);
        Assert.IsTrue(second.IsDirectory);

        Assert.IsFalse(UsnJournal.TryParse(v2.AsSpan(0, 40), out _), "Truncated records are rejected.");
        var bad = (byte[])v2.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(58), 500); // name offset past the end
        Assert.IsFalse(UsnJournal.TryParse(bad, out _));
    }

    private static byte[] BuildRecord(int major, UInt128 id, UInt128 parent, string name, uint reasons, uint attributes, long usn)
    {
        var fixedSize = major == 2 ? 60 : 76;
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var length = (fixedSize + nameBytes.Length + 7) & ~7;
        var bytes = new byte[length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)major);
        int at;
        if (major == 2)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span[8..], (ulong)id);
            BinaryPrimitives.WriteUInt64LittleEndian(span[16..], (ulong)parent);
            at = 24;
        }
        else
        {
            BinaryPrimitives.WriteUInt128LittleEndian(span[8..], id);
            BinaryPrimitives.WriteUInt128LittleEndian(span[24..], parent);
            at = 40;
        }
        BinaryPrimitives.WriteInt64LittleEndian(span[at..], usn);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 16)..], reasons);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 28)..], attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 32)..], (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 34)..], (ushort)fixedSize);
        nameBytes.CopyTo(span[fixedSize..]);
        return bytes;
    }

    [TestMethod]
    public void OnlyLocalDriveRootsAreAccepted()
    {
        Assert.IsTrue(UsnJournal.IsDriveRoot(@"C:\"));
        Assert.IsFalse(UsnJournal.IsDriveRoot(@"C:\Users"));
        Assert.IsFalse(UsnJournal.IsDriveRoot(@"\\server\share\"));
        Assert.IsFalse(UsnJournal.IsDriveRoot(@"\\.\C:"));
        var response = JournalOperations.Execute(new JournalRequest { Op = JournalProtocol.CatchUp, Root = @"\\?\GLOBALROOT\x" }, null, default);
        Assert.AreEqual(JournalProtocol.Unsupported, response.Status);
    }

    [TestMethod]
    public void ProtocolRoundTripsThroughJson()
    {
        var response = new JournalResponse { Status = JournalProtocol.Ok, JournalId = ulong.MaxValue, NextUsn = long.MaxValue, Paths = [@"C:\a", @"C:\ü"] };
        var copy = JsonSerializer.Deserialize<JournalResponse>(JsonSerializer.Serialize(response, JournalProtocol.Json), JournalProtocol.Json)!;
        Assert.AreEqual(ulong.MaxValue, copy.JournalId);
        Assert.AreEqual(long.MaxValue, copy.NextUsn);
        CollectionAssert.AreEqual(response.Paths, copy.Paths);
        Assert.AreEqual(JournalProtocol.Version, copy.Version);
    }

    [TestMethod]
    public void CheckpointsRoundTripAndSurviveADamagedFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Clearspace-Journal-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "index.journal.json");
        try
        {
            Assert.IsTrue(JournalCheckpoints.Save([new JournalCheckpoint(@"C:\", 1234, 99, 5000, DateTime.UtcNow)], file));
            var loaded = JournalCheckpoints.Load(file);
            Assert.AreEqual(5000L, loaded[@"c:\"].Usn, "Roots compare case-insensitively.");
            Assert.AreEqual(1234u, loaded[@"C:\"].Serial);
            File.WriteAllText(file, "{ not json");
            Assert.AreEqual(0, JournalCheckpoints.Load(file).Count, "A damaged file means a rescan, never a crash.");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void ActivityLogIsBoundedAndPendingReasonsAreNotOverwrittenByDefaults()
    {
        var root = @"Q:\activity-test\";
        IndexActivity.Pending(root, "Checking for changes made while Clearspace was closed");
        IndexActivity.Pending(root, "generic overflow", keepExisting: true);
        Assert.AreEqual("Checking for changes made while Clearspace was closed", IndexActivity.For(root).PendingReason);
        IndexActivity.CatchUpFinished(root, "read directly", 120, 7, 0, TimeSpan.FromMilliseconds(300));
        var drive = IndexActivity.For(root);
        Assert.IsNull(drive.PendingReason);
        StringAssert.Contains(drive.CatchUpText, "7 changes");
        for (var i = 0; i < 400; i++) IndexActivity.Record(root, IndexEventKind.Info, $"event {i}");
        Assert.IsTrue(IndexActivity.Events().Count <= 300);
        Assert.AreEqual("event 399", IndexActivity.Events()[0].Message, "Newest first.");
    }
}
