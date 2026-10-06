// NEW (duplicates): finding files that are exact copies (Services/DuplicateFinder.cs), and deciding
// which of the ticked ones may really be removed. The search works on real files, in a folder of its own
// under the temporary folder that is removed again after each test. Nothing here deletes a file: removal
// goes through the Recycle Bin in the app and is not part of the finder.
using System.IO;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class DuplicateTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void MakeFolder()
    {
        _root = Path.Combine(Path.GetTempPath(), "Clearspace duplicate tests " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void RemoveFolder()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string At(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    private string Write(byte[] content, params string[] parts)
    {
        var path = At(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    // `length` bytes that depend on `seed`; `changeAt` flips one byte, for files that differ only there.
    private static byte[] Bytes(int length, int seed, int changeAt = -1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        if (changeAt >= 0)
            bytes[changeAt] ^= 0xFF;
        return bytes;
    }

    private static string[] Names(DuplicateGroup group) => group.Files.Select(file => file.Name).Order(StringComparer.Ordinal).ToArray();

    [TestMethod]
    public void CopiesAreFoundWhateverTheyAreCalledAndWhereverTheyAre()
    {
        Write(Bytes(5_000, 1), "report.pdf");
        Write(Bytes(5_000, 1), "report (1).pdf");
        Write(Bytes(5_000, 1), "Old", "Deeper", "final-report.bin");
        Write(Bytes(5_000, 2), "same size, other contents.pdf");
        Write(Bytes(700, 3), "alone.txt");

        var scan = DuplicateFinder.Find(_root, new DuplicateOptions());

        Assert.AreEqual(1, scan.Groups.Count);
        CollectionAssert.AreEqual(new[] { "final-report.bin", "report (1).pdf", "report.pdf" }, Names(scan.Groups[0]));
        Assert.AreEqual(5_000, scan.Groups[0].Size);
        Assert.AreEqual(10_000, scan.Groups[0].Wasted, "two of the three copies could go");
        Assert.AreEqual(10_000, scan.Wasted);
        Assert.AreEqual(2, scan.Copies);
        Assert.AreEqual(5, scan.FilesChecked);
        Assert.AreEqual(0, scan.Skipped);
    }

    [TestMethod]
    public void FilesThatOnlyStartOrOnlyEndTheSameAreNotCopies()
    {
        var size = DuplicateFinder.HeadBytes * 3;

        // The same first 64 KB, a different byte near the end: only reading the whole file tells them apart.
        Write(Bytes(size, 7), "video a.mp4");
        Write(Bytes(size, 7, changeAt: size - 10), "video b.mp4");

        // A different first byte: told apart from the start alone.
        Write(Bytes(size, 7, changeAt: 0), "video c.mp4");

        // And a real pair of that size, so the search has something to find.
        Write(Bytes(size, 9), "backup.zip");
        Write(Bytes(size, 9), "backup copy.zip");

        var scan = DuplicateFinder.Find(_root, new DuplicateOptions());

        Assert.AreEqual(1, scan.Groups.Count);
        CollectionAssert.AreEqual(new[] { "backup copy.zip", "backup.zip" }, Names(scan.Groups[0]));
    }

    [TestMethod]
    public void EmptyFilesHiddenFilesAndSubfoldersFollowTheOptions()
    {
        Write([], "empty a.txt");
        Write([], "empty b.txt");
        Write(Bytes(900, 4), "notes.txt");
        Write(Bytes(900, 4), "Inside", "notes.txt");
        var hidden = Write(Bytes(900, 4), "notes hidden.txt");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        var isHidden = (File.GetAttributes(hidden) & FileAttributes.Hidden) != 0; // only Windows has the attribute

        var everything = DuplicateFinder.Find(_root, new DuplicateOptions(Subfolders: true, Hidden: true));
        Assert.AreEqual(1, everything.Groups.Count, "empty files are never duplicates of each other");
        Assert.AreEqual(3, everything.Groups[0].Files.Count);

        var visible = DuplicateFinder.Find(_root, new DuplicateOptions(Subfolders: true, Hidden: false));
        Assert.AreEqual(isHidden ? 2 : 3, visible.Groups[0].Files.Count);

        var thisFolderOnly = DuplicateFinder.Find(_root, new DuplicateOptions(Subfolders: false, Hidden: true));
        Assert.AreEqual(1, thisFolderOnly.Groups.Count);
        CollectionAssert.AreEqual(new[] { "notes hidden.txt", "notes.txt" }, Names(thisFolderOnly.Groups[0]));

        var nothing = DuplicateFinder.Find(At("Inside"), new DuplicateOptions());
        Assert.AreEqual(0, nothing.Groups.Count);
        Assert.AreEqual(0, nothing.Wasted);
    }

    [TestMethod]
    public void TheMostWastefulGroupComesFirstAndTheOldestCopyFirstInIt()
    {
        var old = Write(Bytes(2_000, 5), "small old.dat");
        var recent = Write(Bytes(2_000, 5), "small new.dat");
        File.SetLastWriteTime(old, new DateTime(2020, 1, 1));
        File.SetLastWriteTime(recent, new DateTime(2026, 1, 1));
        Write(Bytes(90_000, 6), "big 1.dat");
        Write(Bytes(90_000, 6), "big 2.dat");

        var scan = DuplicateFinder.Find(_root, new DuplicateOptions());

        Assert.AreEqual(2, scan.Groups.Count);
        Assert.AreEqual(90_000, scan.Groups[0].Size);
        Assert.AreEqual("small old.dat", scan.Groups[1].Files[0].Name);

        CollectionAssert.AreEqual(new[] { "small old.dat" }, DuplicateFinder.AllButNewest(scan.Groups[1]).Select(file => file.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "small new.dat" }, DuplicateFinder.AllButOldest(scan.Groups[1]).Select(file => file.Name).ToArray());
    }

    [TestMethod]
    public void ProgressEndsCompleteAndTheSearchCanBeStopped()
    {
        for (var i = 0; i < 6; i++)
            Write(Bytes(DuplicateFinder.HeadBytes * 2, 11), $"copy {i}.bin");

        var reports = new List<DuplicateProgress>();
        DuplicateFinder.Find(_root, new DuplicateOptions(), new Recorder(reports));

        Assert.IsTrue(reports.Count > 0);
        Assert.AreEqual("Comparing", reports[^1].Stage);
        Assert.AreEqual(reports[^1].Total, reports[^1].Done, "the bar is full when the search ends");
        Assert.IsTrue(reports.All(report => report.Done <= report.Total || report.Total == 0));

        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Assert.ThrowsException<OperationCanceledException>(() => DuplicateFinder.Find(_root, new DuplicateOptions(), null, stopped.Token));
    }

    [TestMethod]
    public void AListThatIsOutOfDateIsCheckedAgainstTheDisk()
    {
        var a = Write(Bytes(3_000, 21), "a.bin");
        var b = Write(Bytes(3_000, 21), "b.bin");
        var grown = Write(Bytes(4_000, 21), "grown.bin");       // the list still says 3,000
        var real = new DateTime(2024, 3, 1, 8, 0, 0);
        File.SetLastWriteTime(b, real);

        // What an index that is a little behind might hand over: a wrong date, a wrong size, a file that is gone.
        DuplicateListing Stale(string folder, DuplicateOptions options, CancellationToken token) => new(
        [
            new DuplicateFile(a, 3_000, File.GetLastWriteTime(a)),
            new DuplicateFile(b, 3_000, new DateTime(2020, 1, 1)),
            new DuplicateFile(grown, 3_000, File.GetLastWriteTime(grown)),
            new DuplicateFile(At("deleted.bin"), 3_000, real)
        ], Checked: 50, FromIndex: true);

        var scan = DuplicateFinder.Find(_root, new DuplicateOptions(), null, default, Stale);

        Assert.IsTrue(scan.FromIndex);
        Assert.AreEqual(50, scan.FilesChecked);
        Assert.AreEqual(1, scan.Groups.Count);
        CollectionAssert.AreEqual(new[] { "a.bin", "b.bin" }, Names(scan.Groups[0]));
        Assert.AreEqual(2, scan.Skipped, "the file that grew and the file that is gone");
        Assert.AreEqual(real, scan.Groups[0].Files.Single(file => file.Name == "b.bin").Modified, "the date is the disk's, not the list's");

        // No list from the index (the folder is not indexed): the folders are read.
        var read = DuplicateFinder.Find(_root, new DuplicateOptions(), null, default, (_, _, _) => null);
        Assert.IsFalse(read.FromIndex);
        Assert.AreEqual(3, read.FilesChecked);
        Assert.AreEqual(1, read.Groups.Count);
    }

    [TestMethod]
    public void LinksAreNotCopies()
    {
        var original = Write(Bytes(2_500, 31), "original.dat");
        Write(Bytes(2_500, 31), "Real folder", "copy.dat");

        try
        {
            File.CreateSymbolicLink(At("link to original.dat"), original);
            Directory.CreateSymbolicLink(At("Link folder"), At("Real folder"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Making links needs a permission this account does not have: " + error.Message);
        }

        var scan = DuplicateFinder.Find(_root, new DuplicateOptions());

        // Removing "original.dat" and keeping the link would leave nothing: the link is not a second copy.
        // And the linked folder holds the same file as the real one, not another.
        Assert.AreEqual(1, scan.Groups.Count);
        CollectionAssert.AreEqual(new[] { "copy.dat", "original.dat" }, Names(scan.Groups[0]));
        Assert.IsTrue(scan.Groups[0].Files.All(file => !file.FullPath.Contains("Link folder")));
    }

    // ---- the list of files from the index (an index built in memory; no disk)

    private static (VolumeIndex Volume, string Folder) Indexed()
    {
        const FileAttributes file = FileAttributes.Archive;
        var volume = new VolumeIndex(@"C:\", 1);
        var root = volume.Add(-1, @"C:\", 0, 0, 0, FileAttributes.Directory);
        var downloads = volume.Add(root, "Downloads", 0, 0, 0, FileAttributes.Directory);
        var inner = volume.Add(downloads, "Old", 0, 0, 0, FileAttributes.Directory);
        var hidden = volume.Add(downloads, "Cache", 0, 0, 0, FileAttributes.Directory | FileAttributes.Hidden);
        var elsewhere = volume.Add(root, "Documents", 0, 0, 0, FileAttributes.Directory);

        volume.Add(downloads, "a.zip", 500, 0, 0, file);
        volume.Add(inner, "a copy.zip", 500, 0, 0, file);
        volume.Add(downloads, "unique.zip", 777, 0, 0, file);
        volume.Add(downloads, "empty 1.txt", 0, 0, 0, file);
        volume.Add(downloads, "empty 2.txt", 0, 0, 0, file);
        volume.Add(downloads, "hidden.zip", 500, 0, 0, file | FileAttributes.Hidden);
        volume.Add(hidden, "in hidden folder.zip", 500, 0, 0, file);
        volume.Add(downloads, "in the cloud.zip", 500, 0, 0, file | FileAttributes.Offline);
        volume.Add(downloads, "special.zip", 500, 0, 0, file | FileAttributes.ReparsePoint);
        volume.Add(elsewhere, "not here.zip", 500, 0, 0, file);
        return (volume, @"C:\Downloads");
    }

    // The name alone: the index joins paths with this system's separator, which differs where the tests run.
    private static string[] Listed(DuplicateListing listing)
        => listing.Files.Select(file => file.FullPath[(file.FullPath.LastIndexOfAny(['\\', '/']) + 1)..]).Order(StringComparer.Ordinal).ToArray();

    [TestMethod]
    public void TheIndexGivesTheFilesThatShareASizeUnderTheFolder()
    {
        var (volume, folder) = Indexed();

        var listing = DuplicateIndex.ListFromVolume(volume, folder, new DuplicateOptions());

        Assert.IsNotNull(listing);
        Assert.IsTrue(listing.FromIndex);
        CollectionAssert.AreEqual(new[] { "a copy.zip", "a.zip", "special.zip" }, Listed(listing),
            "not the one of its own size, the empty ones, the hidden ones, the cloud one, or the one in another folder");
        Assert.AreEqual(4, listing.Checked, "unique.zip was looked at too");
        Assert.IsTrue(listing.Files.Single(file => file.FullPath.EndsWith("special.zip")).MayBeLink, "a link or a cloud file: found out when it is opened");
        Assert.IsFalse(listing.Files.First(file => file.FullPath.EndsWith("a.zip")).MayBeLink);
        Assert.IsTrue(listing.Files.All(file => file.Size == 500));
    }

    [TestMethod]
    public void TheIndexFollowsTheSameOptionsAsReadingTheFolders()
    {
        var (volume, folder) = Indexed();

        var withHidden = DuplicateIndex.ListFromVolume(volume, folder, new DuplicateOptions(Hidden: true));
        CollectionAssert.AreEqual(new[] { "a copy.zip", "a.zip", "hidden.zip", "in hidden folder.zip", "special.zip" }, Listed(withHidden!));

        var thisFolderOnly = DuplicateIndex.ListFromVolume(volume, folder, new DuplicateOptions(Subfolders: false));
        CollectionAssert.AreEqual(new[] { "a.zip", "special.zip" }, Listed(thisFolderOnly!));

        var wholeDrive = DuplicateIndex.ListFromVolume(volume, @"C:\", new DuplicateOptions());
        CollectionAssert.Contains(Listed(wholeDrive!), "not here.zip");

        // A file removed since the index was built is marked, not taken out; it is passed over.
        volume.Remove(@"C:\Downloads\Old\a copy.zip");
        var afterRemoval = DuplicateIndex.ListFromVolume(volume, folder, new DuplicateOptions());
        CollectionAssert.AreEqual(new[] { "a.zip", "special.zip" }, Listed(afterRemoval!));
    }

    [TestMethod]
    public void AFolderTheIndexHasNotSeenIsReadFromTheDiskInstead()
    {
        var (volume, _) = Indexed();

        Assert.IsNull(DuplicateIndex.ListFromVolume(volume, @"C:\Nowhere", new DuplicateOptions()));
        Assert.IsNull(DuplicateIndex.ListFromVolume(volume, @"D:\Downloads", new DuplicateOptions()), "another drive");
        Assert.IsNull(DuplicateIndex.ListFromVolume(volume, @"C:\Downloads\a.zip", new DuplicateOptions()), "a file is not a folder");

        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Assert.ThrowsException<OperationCanceledException>(() => DuplicateIndex.ListFromVolume(volume, @"C:\Downloads", new DuplicateOptions(), stopped.Token));
    }

    // ---- what may be removed (no disk: the files are a table)

    private static readonly DateTime Day = new(2026, 10, 2, 9, 0, 0);

    private static DuplicateGroup Group(params string[] names)
        => new(100, names.Select((name, i) => new DuplicateFile(Path.Combine("folder", name), 100, Day.AddDays(i))).ToList());

    private static Func<string, (long Size, DateTime Modified)?> Disk(DuplicateGroup group, Action<Dictionary<string, (long, DateTime)?>>? change = null)
    {
        var table = group.Files.ToDictionary(file => file.FullPath, file => ((long, DateTime)?)(file.Size, file.Modified));
        change?.Invoke(table);
        return path => table.TryGetValue(path, out var now) ? now : null;
    }

    private static HashSet<string> Ticked(DuplicateGroup group, params string[] names)
        => group.Files.Where(file => names.Contains(file.Name)).Select(file => file.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [TestMethod]
    public void AGroupAlwaysKeepsOneCopy()
    {
        var group = Group("a", "b", "c");
        var ticked = Ticked(group, "a", "b");

        Assert.IsFalse(DuplicateFinder.CanTick(group, group.Files[2], ticked), "c is the last copy left");
        Assert.IsTrue(DuplicateFinder.CanTick(group, group.Files[0], ticked), "a is ticked already; ticking it again changes nothing");
        Assert.IsTrue(DuplicateFinder.CanTick(group, group.Files[2], Ticked(group, "a")));

        // Even if every copy ends up ticked some other way, none of them is removed.
        var all = Ticked(group, "a", "b", "c");
        var remove = DuplicateFinder.Removable([group], all, Disk(group), out var heldBack);
        Assert.AreEqual(0, remove.Count);
        Assert.AreEqual(3, heldBack);
    }

    [TestMethod]
    public void TickedFilesAreRemovedOnlyIfNothingChangedSinceTheSearch()
    {
        var group = Group("a", "b", "c");

        var remove = DuplicateFinder.Removable([group], Ticked(group, "a", "b"), Disk(group), out var heldBack);
        CollectionAssert.AreEqual(new[] { "a", "b" }, remove.Select(file => file.Name).ToArray());
        Assert.AreEqual(0, heldBack);

        // b was edited after the search: it is no longer known to be a copy, so it stays.
        var edited = Disk(group, table => table[group.Files[1].FullPath] = (250, Day.AddDays(30)));
        remove = DuplicateFinder.Removable([group], Ticked(group, "a", "b"), edited, out heldBack);
        CollectionAssert.AreEqual(new[] { "a" }, remove.Select(file => file.Name).ToArray());
        Assert.AreEqual(1, heldBack);

        // The copy that was going to be kept is gone (or changed): nothing in the group is removed.
        var keptIsGone = Disk(group, table => table[group.Files[2].FullPath] = null);
        remove = DuplicateFinder.Removable([group], Ticked(group, "a", "b"), keptIsGone, out heldBack);
        Assert.AreEqual(0, remove.Count);
        Assert.AreEqual(2, heldBack);

        // A group with nothing ticked is left alone and not counted.
        remove = DuplicateFinder.Removable([group, Group("x", "y")], Ticked(group, "a"), Disk(group), out heldBack);
        Assert.AreEqual(1, remove.Count);
        Assert.AreEqual(0, heldBack);
    }

    private sealed class Recorder(List<DuplicateProgress> reports) : IProgress<DuplicateProgress>
    {
        public void Report(DuplicateProgress value) => reports.Add(value);
    }
}
