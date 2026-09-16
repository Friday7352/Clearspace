using System.IO;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class FileIndexTests
{
    private static VolumeIndex Index()
    {
        var index = new VolumeIndex(@"C:\", 1);
        var root = index.Add(-1, @"C:\", 0, 0, 0, FileAttributes.Directory);
        var folder = index.Add(root, "Reports", 0, 0, 0, FileAttributes.Directory);
        index.Add(folder, "Annual.TXT", 10, 0, 0, FileAttributes.Normal);
        index.Add(folder, "Hidden.TXT", 20, 0, 0, FileAttributes.Hidden);
        index.Add(folder, "System.TXT", 30, 0, 0, FileAttributes.System);
        return index;
    }

    [TestMethod]
    public void IndexBuildsParentPathsAndRejectsOutOfRangeLookups()
    {
        var index = Index();
        Assert.AreEqual(@"C:\Reports\Annual.TXT", index.GetPath(2));
        Assert.AreEqual("", index.GetPath(-1));
        Assert.AreEqual("", index.GetPath(index.Count));
    }

    [TestMethod]
    public void FoldedSearchFiltersHiddenAndSystemEntries()
    {
        var index = Index();
        var visible = index.Search([VolumeIndex.Fold("TXT")], false, false, true, 20, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { 2 }, visible);
        Assert.AreEqual(3, index.Search(["txt"], true, false, true, 20, CancellationToken.None).Count);
    }

    [TestMethod]
    public void SearchHonorsKindTermsLimitAndCancellation()
    {
        var index = Index();
        Assert.AreEqual(0, index.Search(["txt"], true, true, false, 20, CancellationToken.None).Count);
        Assert.AreEqual(1, index.Search(["annual", ".txt"], true, false, true, 20, CancellationToken.None).Count);
        Assert.AreEqual(1, index.Search(["txt"], true, false, false, 1, CancellationToken.None).Count);
        Assert.AreEqual(0, index.Search(["txt"], true, false, false, 0, CancellationToken.None).Count);
        Assert.AreEqual(0, index.Search([], true, false, false, 10, CancellationToken.None).Count);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.AreEqual(0, index.Search(["txt"], true, false, false, 10, cancellation.Token).Count);
    }

    [TestMethod]
    public void CompactionPreservesSearchAndSupportsFurtherEntries()
    {
        var index = Index(); index.Compact();
        var added = index.Add(1, "Later.txt", 5, 0, 0, FileAttributes.Normal);
        Assert.AreEqual(@"C:\Reports\Later.txt", index.GetPath(added));
        Assert.AreEqual(2, index.Search(["txt"], false, false, false, 10, CancellationToken.None).Count);
    }

    [TestMethod]
    public void EmptyCompactedIndexCanAcceptItsFirstEntry()
    {
        var index = new VolumeIndex(@"C:\", 1); index.Compact();
        var root = index.Add(-1, @"C:\", 0, 0, 0, FileAttributes.Directory);
        Assert.AreEqual(@"C:\", index.GetPath(root));
    }

    [TestMethod]
    public void EntryAndNamePoolsGrowWithoutLosingData()
    {
        var index = new VolumeIndex(@"C:\", 1);
        var root = index.Add(-1, @"C:\", 0, 0, 0, FileAttributes.Directory);
        for (var i = 0; i < 5000; i++) index.Add(root, $"long-document-name-{i}.txt", i, 0, 0, FileAttributes.Normal);
        Assert.AreEqual(5001, index.Count);
        Assert.AreEqual(@"C:\long-document-name-4999.txt", index.GetPath(5000));
        Assert.AreEqual(1, index.Search(["4999"], true, false, true, 10, CancellationToken.None).Count);
    }
}
