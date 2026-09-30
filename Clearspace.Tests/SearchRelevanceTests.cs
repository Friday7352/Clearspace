using System.IO;
using Clearspace.Models;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

// NEW (search relevance): known-item checks for "type what you remember". Each test states what a
// person types and which item should come back (and roughly where). The index tests run the same
// queries through IndexSearch and check it agrees with SearchQuery/SearchRanker.
[TestClass]
public sealed class SearchRelevanceTests
{
    private readonly TagTestScope _scope = new();
    private TagStore Tags() => _scope.Open();

    [TestCleanup]
    public void Cleanup() => _scope.Dispose();

    private static FileSystemItem File(string path) => SearchCoordinatorTests.Item(path);
    private static FileSystemItem Folder(string path) => SearchCoordinatorTests.Item(path, FileAttributes.Directory);

    private static string[] Ranked(SearchQuery query, params FileSystemItem[] items)
    {
        var matches = items.Where(query.Matches).ToList();
        SearchRanker.Rank(matches, query, null);
        return matches.Select(item => item.FullPath).ToArray();
    }

    // ------------------------------------------------------------------ interpretation and ranking

    [TestMethod]
    public void PlainTagNamePutsTaggedItemsAboveFilenameMatches()
    {
        var tags = Tags();
        tags.Assign(@"C:\Clients", "work");
        tags.Assign(@"C:\Clients\contract.pdf", "work");
        var ranked = Ranked(SearchQuery.Parse("work", tags),
            File(@"C:\Notes\work log.txt"), File(@"C:\Clients\contract.pdf"), Folder(@"C:\Clients"),
            File(@"C:\Clients\untagged.txt"), File(@"C:\Other\unrelated.txt"));
        CollectionAssert.AreEqual(new[] { @"C:\Clients", @"C:\Clients\contract.pdf", @"C:\Notes\work log.txt" }, ranked,
            "Tagged items first; a lone tag word does not pull in everything inside a tagged folder.");
    }

    [TestMethod]
    public void TagWordAndFilenameWordCombine()
    {
        var tags = Tags();
        tags.Assign(@"C:\A\budget.xlsx", "work");
        tags.Assign(@"C:\Clients", "work");
        var ranked = Ranked(SearchQuery.Parse("work budget", tags),
            File(@"C:\B\Work Budget old.xlsx"), File(@"C:\C\budget.xlsx"),
            File(@"C:\Clients\budget 2025.xlsx"), File(@"C:\A\budget.xlsx"));
        CollectionAssert.AreEqual(new[] { @"C:\A\budget.xlsx", @"C:\Clients\budget 2025.xlsx", @"C:\B\Work Budget old.xlsx" }, ranked);
    }

    [TestMethod]
    public void FolderNamesAnswerWordsMissingFromTheFilename()
    {
        var query = SearchQuery.Parse("school budget", Tags());
        Assert.IsTrue(query.Matches(File(@"C:\School\2025\budget.xlsx")));
        Assert.IsFalse(query.Matches(File(@"C:\Home\budget.xlsx")));
        Assert.IsFalse(query.Matches(File(@"C:\School\2025\notes.txt")), "At least one word must match the item itself.");
    }

    [TestMethod]
    public void TypeWordsCombineWithFolderNames()
    {
        var query = SearchQuery.Parse("photos beach", Tags());
        Assert.IsTrue(query.Matches(File(@"C:\Pictures\Beach Trip\IMG_0001.jpg")));
        Assert.IsFalse(query.Matches(File(@"C:\Pictures\Beach Trip\notes.txt")));
        Assert.IsFalse(query.Matches(File(@"C:\Docs\beach notes.txt")));
        var ranked = Ranked(query, File(@"C:\Pictures\Beach Trip\IMG_0001.jpg"), File(@"C:\Pictures\beach.jpg"));
        Assert.AreEqual(@"C:\Pictures\beach.jpg", ranked[0], "A name match beats folder context.");
    }

    [TestMethod]
    public void MultiwordTagNameIsRecognizedBeforeSingleWords()
    {
        var tags = Tags();
        var repairs = tags.Create("Home Repairs");
        tags.Assign(@"C:\House", repairs.Id);
        var query = SearchQuery.Parse("home repairs quote", tags);
        CollectionAssert.AreEqual(new[] { "home repairs", "quote" }, query.Terms.ToArray());
        Assert.IsTrue(query.Matches(File(@"C:\House\quote.pdf")));
        Assert.IsFalse(query.Matches(File(@"C:\Other\quote.pdf")));
    }

    [TestMethod]
    public void TagPrefixesHelpWhileTypingButSubstringsDoNot()
    {
        var tags = Tags();
        tags.Assign(@"C:\x\contract.pdf", "work");
        tags.Assign(@"C:\x\a.txt", "important");
        Assert.IsTrue(SearchQuery.Parse("wor", tags).Matches(File(@"C:\x\contract.pdf")));
        Assert.IsFalse(SearchQuery.Parse("port", tags).Matches(File(@"C:\x\a.txt")), "\"port\" is inside Important but is not a word start.");
    }

    [TestMethod]
    public void QuotedTextIsLiteralEvenWithAColon()
    {
        var query = SearchQuery.Parse("\"tag:work\"", Tags());
        CollectionAssert.AreEqual(new[] { "tag:work" }, query.Terms.ToArray());
        Assert.AreEqual(0, query.TagIds.Count);
    }

    [TestMethod]
    public void ExplicitFiltersStayStrict()
    {
        var tags = Tags();
        tags.Assign(@"C:\A\budget.xlsx", "work");
        var query = SearchQuery.Parse("tag:work budget", tags);
        Assert.IsTrue(query.Matches(File(@"C:\A\budget.xlsx")));
        Assert.IsFalse(query.Matches(File(@"C:\B\budget.xlsx")));
        Assert.IsFalse(SearchQuery.Parse("budget ext:pdf", tags).Matches(File(@"C:\A\budget.xlsx")));
    }

    [TestMethod]
    public void ResultsInsideTheCurrentFolderRankHigher()
    {
        var query = SearchQuery.Parse("budget", Tags());
        var items = new List<FileSystemItem> { File(@"C:\Else\budget.xlsx"), File(@"C:\Here\budget.xlsx") };
        SearchRanker.Rank(items, query, @"C:\Here");
        Assert.AreEqual(@"C:\Here\budget.xlsx", items[0].FullPath);
    }

    // ------------------------------------------------------------------ the index scan

    private static VolumeIndex Volume(params string[] paths)
    {
        var index = new VolumeIndex(@"C:\", 1);
        var folders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\"] = index.Add(-1, @"C:\", 0, 0, 0, FileAttributes.Directory)
        };

        int FolderIndex(string path)
        {
            if (folders.TryGetValue(path, out var known))
                return known;

            var added = index.Add(FolderIndex(Path.GetDirectoryName(path)!), Path.GetFileName(path), 0, 0, 0, FileAttributes.Directory);
            folders[path] = added;
            return added;
        }

        foreach (var path in paths)
        {
            if (path.EndsWith('\\'))
            {
                FolderIndex(path.TrimEnd('\\'));
                continue;
            }

            index.Add(FolderIndex(Path.GetDirectoryName(path)!), Path.GetFileName(path), 0, 0, 0, FileAttributes.Normal);
        }

        return index;
    }

    private static List<(int Score, string Path)> IndexHits(VolumeIndex volume, SearchQuery query, string root = @"C:\", int limit = 50)
    {
        var plan = IndexSearch.Prepare(volume, query, [root]);

        if (plan is null)
            return [];

        return IndexSearch.Search(volume, query, plan, showHidden: false, limit, CancellationToken.None)
            .Select(hit => (hit.Score, volume.GetPath(hit.Index))).ToList();
    }

    [TestMethod]
    public void IndexKeepsTheBestMatchEvenWhenItIsScannedLast()
    {
        var paths = Enumerable.Range(0, 5000).Select(i => $@"C:\Docs\report-{i}.txt").Append(@"C:\Docs\report.txt").ToArray();
        var hits = IndexHits(Volume(paths), SearchQuery.Parse("report", Tags()), limit: 10);
        Assert.AreEqual(10, hits.Count);
        Assert.AreEqual(@"C:\Docs\report.txt", hits[0].Path);
    }

    [TestMethod]
    public void IndexAnswersFilterOnlyQueries()
    {
        var volume = Volume(@"C:\Docs\a.pdf", @"C:\Docs\b.txt", @"C:\Other\c.PDF");
        var hits = IndexHits(volume, SearchQuery.Parse("ext:pdf", Tags())).Select(hit => hit.Path).OrderBy(path => path).ToArray();
        CollectionAssert.AreEqual(new[] { @"C:\Docs\a.pdf", @"C:\Other\c.PDF" }, hits);
    }

    [TestMethod]
    public void IndexUsesTagsFolderNamesAndScope()
    {
        var tags = Tags();
        tags.Assign(@"C:\Clients", "work");
        var volume = Volume(@"C:\Clients\budget.xlsx", @"C:\Home\budget.xlsx", @"C:\School\2025\budget.xlsx", @"C:\School\notes.txt");

        CollectionAssert.AreEqual(new[] { @"C:\Clients\budget.xlsx" },
            IndexHits(volume, SearchQuery.Parse("work budget", tags)).Select(hit => hit.Path).ToArray());
        CollectionAssert.AreEqual(new[] { @"C:\School\2025\budget.xlsx" },
            IndexHits(volume, SearchQuery.Parse("school budget", tags)).Select(hit => hit.Path).ToArray());
        CollectionAssert.AreEqual(new[] { @"C:\School\2025\budget.xlsx" },
            IndexHits(volume, SearchQuery.Parse("budget", tags), root: @"C:\School").Select(hit => hit.Path).ToArray());
        CollectionAssert.AreEqual(new[] { @"C:\Clients" },
            IndexHits(volume, SearchQuery.Parse("tag:work", tags)).Select(hit => hit.Path).ToArray());
    }

    // NEW (review): folders deeper than 256 levels used to count as outside the search roots.
    [TestMethod]
    public void DeeplyNestedFilesAreFoundWithTheirFullPath()
    {
        var folder = @"C:\" + string.Join('\\', Enumerable.Repeat("d", 300));
        var volume = Volume(folder + @"\needle.txt");
        var hits = IndexHits(volume, SearchQuery.Parse("needle", Tags()));
        Assert.AreEqual(folder + @"\needle.txt", hits.Single().Path);
    }

    [TestMethod]
    public void IndexScoresAgreeWithItemScores()
    {
        var tags = Tags();
        tags.Assign(@"C:\Clients", "work");
        tags.Assign(@"C:\A\budget.xlsx", "work");
        var volume = Volume(@"C:\Clients\budget 2025.xlsx", @"C:\A\budget.xlsx", @"C:\B\Work Budget old.xlsx",
            @"C:\School\2025\budget.xlsx", @"C:\Users\x\AppData\budget.tmp", @"C:\Pictures\Beach Trip\IMG_1.jpg");

        foreach (var text in new[] { "work budget", "school budget", "budget", "photos beach", "ext:xlsx" })
        {
            var query = SearchQuery.Parse(text, tags).At(@"C:\School");
            var hits = IndexHits(volume, query);
            Assert.IsTrue(hits.Count > 0, text);

            foreach (var (score, path) in hits)
            {
                var item = File(path);
                Assert.IsTrue(query.Matches(item), $"{text}: {path}");
                Assert.AreEqual(SearchRanker.Score(item, query, @"C:\School"), score, $"{text}: {path}");
            }
        }
    }
}
