// NEW (archives): unpacking and making ZIP files (Services/ArchiveService.cs).
// These tests work on real files, in a folder of their own under the temporary folder that is removed
// again after each test.
using System.IO;
using System.IO.Compression;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class ArchiveTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void MakeFolder()
    {
        _root = Path.Combine(Path.GetTempPath(), "Clearspace archive tests " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void RemoveFolder()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string At(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    private string Write(string content, params string[] parts)
    {
        var path = At(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // NEW (pre-release review): files that are compressed already are stored as they are, and still come
    // back byte for byte.
    [TestMethod]
    public void FilesThatAreCompressedAlreadyAreStoredNotSqueezedAgain()
    {
        Assert.AreEqual(CompressionLevel.NoCompression, ArchiveService.LevelFor(@"C:\Photos\holiday.JPG"));
        Assert.AreEqual(CompressionLevel.NoCompression, ArchiveService.LevelFor("film.mkv"));
        Assert.AreEqual(CompressionLevel.Optimal, ArchiveService.LevelFor("notes.txt"));
        Assert.AreEqual(CompressionLevel.Optimal, ArchiveService.LevelFor("no ending"));

        var bytes = new byte[300_000];
        new Random(7).NextBytes(bytes);
        var picture = At("picture.jpg");
        File.WriteAllBytes(picture, bytes);
        var text = Write(new string('t', 300_000), "text.txt");

        var zip = ArchiveService.Compress([picture, text], At("Mixed.zip"), null, CancellationToken.None);

        using (var archive = ZipFile.OpenRead(zip))
        {
            var stored = archive.GetEntry("picture.jpg")!;
            var squeezed = archive.GetEntry("text.txt")!;
            Assert.IsTrue(stored.CompressedLength >= stored.Length, "stored as it is");
            Assert.IsTrue(squeezed.CompressedLength < squeezed.Length / 10, "text is still compressed");
        }

        var folder = ArchiveService.Extract(zip, _root, null, CancellationToken.None);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(folder, "picture.jpg")));
    }

    [TestMethod]
    public void CompressThenExtractBringsEverythingBack()
    {
        Write("alpha", "Project", "a.txt");
        Write(new string('b', 200_000), "Project", "sub", "b.txt");
        Directory.CreateDirectory(At("Project", "empty"));
        var note = Write("a note", "note.txt");

        var zip = At("Packed.zip");
        var reports = new List<ArchiveProgress>();
        var made = ArchiveService.Compress([At("Project"), note], zip, new Recorder(reports), CancellationToken.None);

        Assert.AreEqual(zip, made);
        Assert.IsTrue(File.Exists(zip));
        Assert.IsTrue(reports.Count > 0, "progress is reported at least once, at the end");
        Assert.AreEqual(reports[^1].BytesTotal, reports[^1].BytesDone);

        var listing = ArchiveService.List(zip);
        Assert.AreEqual(4, listing.TotalEntries, "three files and the empty folder");
        Assert.AreEqual(5 + 200_000 + 6, listing.TotalSize);

        var folder = ArchiveService.Extract(zip, _root, null, CancellationToken.None);

        Assert.AreEqual(At("Packed"), folder);
        Assert.AreEqual("alpha", File.ReadAllText(Path.Combine(folder, "Project", "a.txt")));
        Assert.AreEqual(200_000, new FileInfo(Path.Combine(folder, "Project", "sub", "b.txt")).Length);
        Assert.IsTrue(Directory.Exists(Path.Combine(folder, "Project", "empty")), "an empty folder is kept");
        Assert.AreEqual("a note", File.ReadAllText(Path.Combine(folder, "note.txt")));
    }

    [TestMethod]
    public void ExtractingTwiceMakesASecondFolderInsteadOfOverwriting()
    {
        var note = Write("first", "note.txt");
        var zip = At("Notes.zip");
        ArchiveService.Compress([note], zip, null, CancellationToken.None);

        var first = ArchiveService.Extract(zip, _root, null, CancellationToken.None);
        File.WriteAllText(Path.Combine(first, "note.txt"), "changed since");
        var second = ArchiveService.Extract(zip, _root, null, CancellationToken.None);

        Assert.AreEqual(At("Notes"), first);
        Assert.AreEqual(At("Notes (2)"), second);
        Assert.AreEqual("changed since", File.ReadAllText(Path.Combine(first, "note.txt")), "the first folder was left alone");
        Assert.AreEqual("first", File.ReadAllText(Path.Combine(second, "note.txt")));
    }

    [TestMethod]
    public void NamesForNewFoldersAndZipFiles()
    {
        Directory.CreateDirectory(At("v1.2"));
        Assert.AreEqual(At("v1.2 (2)"), ArchiveService.FolderFor(At("v1.2.zip"), _root), "a folder has no ending to keep");
        Assert.AreEqual(At("Fresh"), ArchiveService.FolderFor(At("Fresh.zip"), _root));

        var report = Write("x", "report.pdf");
        Assert.AreEqual(At("report.zip"), ArchiveService.ZipFor([report], _root), "one file: its name without the ending");
        Assert.AreEqual(At("v1.2.zip"), ArchiveService.ZipFor([At("v1.2")], _root), "one folder: its whole name");
        Assert.AreEqual(At(Path.GetFileName(_root) + ".zip"), ArchiveService.ZipFor([report, At("v1.2")], _root),
            "several items: the name of the folder they are in");

        File.WriteAllText(At("report.zip"), "taken");
        Assert.AreEqual(At("report (2).zip"), ArchiveService.ZipFor([report], _root));

        Assert.IsTrue(ArchiveService.IsArchive(@"C:\x\Photos.ZIP"));
        Assert.IsFalse(ArchiveService.IsArchive(@"C:\x\Photos.7z"));
        Assert.IsFalse(ArchiveService.IsArchive(@"C:\x\zip"));
    }

    [TestMethod]
    public void NamesThatLeaveTheFolderAreRefused()
    {
        Assert.AreEqual(At("sub", "file.txt"), ArchiveService.DestinationFor(_root, "sub/file.txt"));
        Assert.AreEqual(At("lead.txt"), ArchiveService.DestinationFor(_root, "/lead.txt"), "a leading slash is dropped");
        Assert.AreEqual(At("a", "c.txt"), ArchiveService.DestinationFor(_root, "a/b/../c.txt"), "going up is fine while it stays inside");

        Assert.ThrowsException<InvalidDataException>(() => ArchiveService.DestinationFor(_root, "../evil.txt"));
        Assert.ThrowsException<InvalidDataException>(() => ArchiveService.DestinationFor(_root, @"sub\..\..\evil.txt"));
        Assert.ThrowsException<InvalidDataException>(() => ArchiveService.DestinationFor(_root, @"C:\Windows\evil.txt"));
        Assert.ThrowsException<InvalidDataException>(() => ArchiveService.DestinationFor(_root, ""));

        // The folder next door, whose name merely starts the same way, is outside too.
        Assert.ThrowsException<InvalidDataException>(
            () => ArchiveService.DestinationFor(At("inner"), @"..\innermost\evil.txt"));
    }

    [TestMethod]
    public void AHostileArchiveIsRefusedAndLeavesNothingBehind()
    {
        var zip = At("Trap.zip");
        using (var stream = new FileStream(zip, FileMode.CreateNew))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("fine.txt").Open()))
                writer.Write("fine");
            using (var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open()))
                writer.Write("escaped");
        }

        Assert.ThrowsException<InvalidDataException>(() => ArchiveService.Extract(zip, _root, null, CancellationToken.None));

        Assert.IsFalse(File.Exists(At("escaped.txt")), "nothing was written outside the new folder");
        Assert.IsFalse(Directory.Exists(At("Trap")), "the half-made folder was removed");
    }

    [TestMethod]
    public void StoppingLeavesNothingBehind()
    {
        var note = Write("a note", "note.txt");
        var zip = At("Notes.zip");
        ArchiveService.Compress([note], zip, null, CancellationToken.None);

        using var stopped = new CancellationTokenSource();
        stopped.Cancel();

        Assert.ThrowsException<OperationCanceledException>(() => ArchiveService.Extract(zip, _root, null, stopped.Token));
        Assert.AreEqual(0, Directory.GetDirectories(_root).Length, "no folder is left from the stopped unpacking");

        var other = At("Other.zip");
        Assert.ThrowsException<OperationCanceledException>(() => ArchiveService.Compress([note], other, null, stopped.Token));
        Assert.IsFalse(File.Exists(other), "no unfinished .zip is left");
    }

    [TestMethod]
    public void AZipThatIsAlreadyThereIsNeverReplaced()
    {
        var note = Write("a note", "note.txt");
        var zip = At("Kept.zip");
        File.WriteAllText(zip, "not really a zip, but it is the user's file");

        Assert.ThrowsException<IOException>(() => ArchiveService.Compress([note], zip, null, CancellationToken.None));
        Assert.AreEqual("not really a zip, but it is the user's file", File.ReadAllText(zip));
    }

    // ---- NEW (rar and 7z)

    [TestMethod]
    public void OtherKindsAreKnownByTheirEndings()
    {
        Assert.IsTrue(ArchiveService.CanExtract(@"C:\x\Photos.7z"));
        Assert.IsTrue(ArchiveService.CanExtract(@"C:\x\Photos.RAR"));
        Assert.IsTrue(ArchiveService.CanExtract(@"C:\x\Photos.tar.gz"));
        Assert.IsTrue(ArchiveService.CanExtract(@"C:\x\Photos.zip"));
        Assert.IsFalse(ArchiveService.CanExtract(@"C:\x\Photos.gz"), "a lone .gz is one squeezed file, not an archive");
        Assert.IsFalse(ArchiveService.CanExtract(@"C:\x\Photos.txt"));

        Assert.IsTrue(ArchiveService.CanList(@"C:\x\Photos.7z"));
        Assert.IsFalse(ArchiveService.CanList(@"C:\x\Photos.tar.gz"), "listing would mean reading all of it");

        Assert.AreEqual("Photos", ArchiveService.StemOf(@"C:\x\Photos.tar.gz"));
        Assert.AreEqual("v1.2", ArchiveService.StemOf(@"C:\x\v1.2.7z"));
        Assert.AreEqual(At("Backup"), ArchiveService.FolderFor(At("Backup.tar.xz"), _root));
    }

    private string MakeWith(SharpCompress.Common.ArchiveType type, SharpCompress.Common.CompressionType compression, string name)
    {
        var path = At(name);
        using var writer = SharpCompress.Writers.WriterFactory.OpenWriter(path, type, new SharpCompress.Writers.WriterOptions(compression));
        using (var first = new MemoryStream("hello"u8.ToArray()))
            writer.Write("Top/note.txt", first, new DateTime(2020, 5, 6, 7, 8, 9));
        using (var second = new MemoryStream(new byte[100_000]))
            writer.Write("Top/Inner/zeros.bin", second, null);
        return path;
    }

    [TestMethod]
    public void SevenZipAndTarFilesAreUnpacked()
    {
        var sevenZip = MakeWith(SharpCompress.Common.ArchiveType.SevenZip, SharpCompress.Common.CompressionType.LZMA2, "Seven.7z");
        var tarGz = MakeWith(SharpCompress.Common.ArchiveType.Tar, SharpCompress.Common.CompressionType.GZip, "Tarred.tar.gz");

        foreach (var (archive, folderName) in new[] { (sevenZip, "Seven"), (tarGz, "Tarred") })
        {
            var reports = new List<ArchiveProgress>();
            var made = ArchiveService.ExtractAsync(archive, _root, new Recorder(reports), CancellationToken.None).GetAwaiter().GetResult();

            Assert.AreEqual(At(folderName), made, archive);
            Assert.AreEqual("hello", File.ReadAllText(Path.Combine(made, "Top", "note.txt")), archive);
            Assert.AreEqual(100_000, new FileInfo(Path.Combine(made, "Top", "Inner", "zeros.bin")).Length, archive);
        }

        var listing = ArchiveService.List(sevenZip);
        Assert.AreEqual(100_005, listing.TotalSize);
        Assert.IsTrue(listing.Entries.Any(entry => entry.Name.Replace('\\', '/') == "Top/note.txt"));
    }

    [TestMethod]
    public void ADamagedSevenZipFileSaysSoAndLeavesNothingBehind()
    {
        var broken = At("Broken.7z");
        File.WriteAllBytes(broken, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 1, 2, 3, 4, 5]);

        Assert.ThrowsException<InvalidDataException>(() => ArchiveService.ExtractOther(broken, _root, null, CancellationToken.None));
        Assert.IsFalse(Directory.Exists(At("Broken")), "the half-made folder is removed");
    }

    private sealed class Recorder(List<ArchiveProgress> reports) : IProgress<ArchiveProgress>
    {
        public void Report(ArchiveProgress value) => reports.Add(value);
    }
}
