using System.Buffers.Binary;
using System.IO;
using System.Text;
using Clearspace.Journal;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

// NEW (instant first index): the file-table reader, fed hand-built NTFS records. Reading a real drive
// needs administrator rights; that path is also guarded at run time by the one-time comparison with a
// folder walk (FileTableTrust).
[TestClass]
public sealed class FileTableTests
{
    private const int RecordSize = 1024;
    private const ushort Check = 0x0A0B;

    // A FILE record with the update-sequence fixups applied the way NTFS writes them to disk.
    private static byte[] Record(ushort flags, ulong baseRecord = 0, ushort sequence = 1, params byte[][] attributes)
    {
        var record = new byte[RecordSize];
        var span = record.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0x454C4946);        // "FILE"
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 0x30);         // update sequence array offset
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], 3);            // check value + 2 sectors
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x10..], sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x14..], 0x38);      // first attribute
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x16..], flags);
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x20..], baseRecord);

        var at = 0x38;
        foreach (var attribute in attributes)
        {
            attribute.CopyTo(span[at..]);
            at += attribute.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(span[at..], 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x18..], (uint)(at + 8));

        // Move each sector's last two bytes into the array and stamp the check value there.
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x30..], Check);
        for (var i = 1; i <= 2; i++)
        {
            var end = i * 512 - 2;
            BinaryPrimitives.WriteUInt16LittleEndian(span[(0x30 + 2 * i)..], BinaryPrimitives.ReadUInt16LittleEndian(span[end..]));
            BinaryPrimitives.WriteUInt16LittleEndian(span[end..], Check);
        }
        return record;
    }

    private static byte[] Resident(uint type, byte[] value)
    {
        var length = (0x18 + value.Length + 7) & ~7;
        var attribute = new byte[length];
        var span = attribute.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, type);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x10..], (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x14..], 0x18);
        value.CopyTo(span[0x18..]);
        return attribute;
    }

    private static byte[] Standard(uint attributes = 0x20, uint securityId = 1, long modified = 1000)
    {
        var value = new byte[72];
        BinaryPrimitives.WriteInt64LittleEndian(value, 500);            // created
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(8), modified);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(32), attributes);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(52), securityId);
        return Resident(0x10, value);
    }

    private static byte[] Name(ulong parent, string name, byte nameSpace = 1, uint flags = 0, uint reparseTag = 0)
    {
        var chars = Encoding.Unicode.GetBytes(name);
        var value = new byte[66 + chars.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(value, parent | (1UL << 48));
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0x38), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0x3C), reparseTag);
        value[64] = (byte)name.Length;
        value[65] = nameSpace;
        chars.CopyTo(value, 66);
        return Resident(0x30, value);
    }

    private static byte[] Data(int size) => Resident(0x80, new byte[size]);

    private const ushort InUse = 1, InUseFolder = 3;

    [TestMethod]
    public void FixupsRestoreSectorEndsAndRejectTornRecords()
    {
        var record = Record(InUse, attributes: [Standard(), Name(5, "a.txt")]);
        Assert.IsTrue(MftReader.ApplyFixups(record));
        Assert.AreNotEqual(Check, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510)));

        var torn = Record(InUse, attributes: [Standard()]);
        BinaryPrimitives.WriteUInt16LittleEndian(torn.AsSpan(1022), 0x1234); // second sector not written
        Assert.IsFalse(MftReader.ApplyFixups(torn));
    }

    [TestMethod]
    public void DataRunsDecodeRelativeAndNegativeOffsets()
    {
        // 16 clusters at LCN 256, then 8 clusters 16 before that (LCN 240).
        var runs = MftReader.DecodeRuns([0x21, 0x10, 0x00, 0x01, 0x11, 0x08, 0xF0, 0x00]);
        CollectionAssert.AreEqual(new[] { (256L, 16L), (240L, 8L) }, runs.Select(run => (run.Lcn, run.Clusters)).ToArray());
    }

    private static FileTable Build(Dictionary<long, byte[]> records, HashSet<ulong>? blocked = null)
    {
        const long capacity = 64;
        var parsed = new MftReader.Records(capacity);
        foreach (var (number, record) in records.OrderBy(pair => pair.Key))
            MftReader.Parse(record.AsSpan(), number, parsed);
        return MftReader.Assemble(@"C:\", parsed, capacity, _ => blocked ?? [], CancellationToken.None);
    }

    private static Dictionary<string, int> Paths(FileTable table)
    {
        var paths = new string[table.Count];
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var at = 0;
        for (var i = 0; i < table.Count; i++)
        {
            var name = new string(table.Names, at, table.NameLength[i]);
            at += table.NameLength[i];
            paths[i] = i == 0 ? name : Path.Join(paths[table.Parent[i]], name);
            result[paths[i]] = i;
            if (i > 0) Assert.IsTrue(table.Parent[i] < i, "Parents come before children.");
        }
        return result;
    }

    [TestMethod]
    public void BuildsTheSameTreeAFolderListingWouldShow()
    {
        var table = Build(new()
        {
            [5] = Record(InUseFolder, attributes: [Standard(0x16), Name(5, ".")]),
            [6] = Record(InUse, attributes: [Standard(0x06), Name(5, "$Bitmap")]),           // metadata: hidden from listings
            [16] = Record(InUseFolder, attributes: [Standard(0), Name(5, "Docs")]),
            [17] = Record(InUse, attributes: [Standard(0x20, modified: 42), Name(16, "report.txt"), Data(123)]),
            // Long name plus its DOS alias: listed once, by the long name.
            [18] = Record(InUse, attributes: [Standard(), Name(16, "LONGFI~1.TXT", nameSpace: 2), Name(16, "Long file name.txt", nameSpace: 1), Data(5)]),
            // A hard link: one file, two names in two folders.
            [19] = Record(InUse, attributes: [Standard(), Name(5, "linked.txt"), Name(16, "linked.txt"), Data(7)]),
            // A junction: listed, never entered. A cloud-sync folder: entered.
            [20] = Record(InUseFolder, attributes: [Standard(0x400), Name(5, "Junction", flags: 0x400, reparseTag: 0xA0000003)]),
            [21] = Record(InUse, attributes: [Standard(), Name(20, "inside-junction.txt")]),
            [22] = Record(InUseFolder, attributes: [Standard(0x400), Name(5, "OneDrive", flags: 0x400, reparseTag: 0x9000701A)]),
            [23] = Record(InUse, attributes: [Standard(), Name(22, "synced.txt"), Data(9)]),
            // Not in use (deleted): ignored.
            [24] = Record(0, attributes: [Standard(), Name(5, "deleted.txt")])
        });

        var paths = Paths(table);
        CollectionAssert.AreEquivalent(new[]
        {
            @"C:\", @"C:\Docs", @"C:\Docs\report.txt", @"C:\Docs\Long file name.txt", @"C:\linked.txt", @"C:\Docs\linked.txt",
            @"C:\Junction", @"C:\OneDrive", @"C:\OneDrive\synced.txt"
        }, paths.Keys.ToArray());

        var report = paths[@"C:\Docs\report.txt"];
        Assert.AreEqual(123L, table.Size[report]);
        Assert.AreEqual(42L, table.Modified[report]);
        Assert.AreEqual(0x10u, table.Attributes[paths[@"C:\Docs"]] & 0x10u, "Folders carry the directory attribute.");
        Assert.AreEqual(0L, table.Size[paths[@"C:\Docs"]]);
    }

    [TestMethod]
    public void FoldersTheCallerCannotListKeepTheirEntryButNotTheirContents()
    {
        var table = Build(new()
        {
            [5] = Record(InUseFolder, attributes: [Standard(0x16), Name(5, ".")]),
            [16] = Record(InUseFolder, attributes: [Standard(0, securityId: 99), Name(5, "Other user")]),
            [17] = Record(InUse, attributes: [Standard(), Name(16, "private.txt")]),
            [18] = Record(InUseFolder, attributes: [Standard(0, securityId: 7), Name(5, "Mine")]),
            [19] = Record(InUse, attributes: [Standard(), Name(18, "mine.txt")])
        }, blocked: [99]);

        var paths = Paths(table);
        Assert.IsTrue(paths.ContainsKey(@"C:\Other user"));
        Assert.IsFalse(paths.ContainsKey(@"C:\Other user\private.txt"));
        Assert.IsTrue(paths.ContainsKey(@"C:\Mine\mine.txt"));
        Assert.AreEqual(1, table.DeniedFolders);
    }

    [TestMethod]
    public void NamesInExtensionRecordsBelongToTheirBaseRecord()
    {
        var table = Build(new()
        {
            [5] = Record(InUseFolder, attributes: [Standard(0x16), Name(5, ".")]),
            [16] = Record(InUse, attributes: [Standard(), Data(11)]),
            [30] = Record(InUse, baseRecord: 16, attributes: [Name(5, "many-links.txt")])
        });
        var paths = Paths(table);
        Assert.AreEqual(11L, table.Size[paths[@"C:\many-links.txt"]]);
    }

    [TestMethod]
    public void TransferRoundTripsAndBuildsAWalkShapedIndex()
    {
        var table = Build(new()
        {
            [5] = Record(InUseFolder, attributes: [Standard(0x16), Name(5, ".")]),
            [16] = Record(InUseFolder, attributes: [Standard(0), Name(5, "Docs")]),
            [17] = Record(InUse, attributes: [Standard(), Name(16, "report.txt"), Data(123)])
        });

        using var stream = new MemoryStream();
        FileTableTransfer.Write(stream, table);
        Assert.IsTrue(stream.CanWrite, "The caller's stream stays open (the helper still drains its pipe).");
        stream.Position = 0;
        var copy = FileTableTransfer.Read(stream, table.Count, table.NameChars, CancellationToken.None);

        var index = FileTableIndex.ToVolumeIndex(copy, @"C:\", 1);
        Assert.AreEqual(@"C:\Docs\report.txt", index.GetPath(2));
        Assert.AreEqual(123L, index.Entry(2).Size);
        var summary = FileTableIndex.Summarize(index);
        Assert.AreEqual(1L, summary.Files);
        Assert.AreEqual(1L, summary.Folders);

        // The search index works on it directly.
        var hits = index.Search(["report"], true, false, true, 10, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { 2 }, hits);
    }

    [TestMethod]
    public void OneTimeCheckToleratesSmallDriftButNotRealDisagreement()
    {
        var table = new FileTableIndex.Summary(1_000_000, 100_000, 500L << 30);
        Assert.IsTrue(FileTableIndex.Agree(table, table with { Files = 1_003_000 }, out _), "Files changed between the two reads.");
        Assert.IsFalse(FileTableIndex.Agree(table, table with { Files = 800_000 }, out var detail));
        StringAssert.Contains(detail, "folder walk");
    }
}
