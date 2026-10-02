using System.IO;
using Clearspace.Models;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class TagAndQueryTests
{
    private readonly TagTestScope _scope = new();
    private TagStore Tags() => _scope.Open();
    [TestCleanup]
    public void Cleanup() => _scope.Dispose();

    [TestMethod]
    public void TagAssignmentIsCaseInsensitiveAndDoesNotDuplicate()
    {
        var tags = Tags();
        tags.Assign(@"C:\File.txt", "work");
        tags.Assign(@"c:\FILE.txt", "WORK");
        Assert.AreEqual(1, tags.TagIdsFor(@"C:\FILE.txt").Count);
        Assert.IsTrue(tags.HasTag(@"c:\file.txt", "WORK"));
        tags.Assign(@"C:\File.txt", "does-not-exist");
        Assert.AreEqual(1, tags.TagIdsFor(@"C:\FILE.txt").Count);
    }

    [TestMethod]
    public void DeleteRemovesAssignmentsWithoutRemovingOtherTags()
    {
        var tags = Tags();
        tags.Assign("one", "work"); tags.Assign("one", "personal"); tags.Assign("two", "work");
        tags.Delete("WORK");
        Assert.IsNull(tags.Find("work"));
        Assert.IsTrue(tags.HasTag("one", "personal"));
        Assert.AreEqual(0, tags.TagIdsFor("two").Count);
        Assert.IsFalse(tags.Assignments.Any(pair => pair.Key == "two"));
    }

    [TestMethod]
    public void ToggleMixedSelectionAddsToAllThenRemovesFromAll()
    {
        var tags = Tags(); tags.Assign("one", "work");
        tags.ToggleForAll(["one", "two"], "work");
        Assert.IsTrue(tags.HasTag("one", "work") && tags.HasTag("two", "work"));
        tags.ToggleForAll(["one", "two"], "work");
        Assert.AreEqual(0, tags.Assignments.Count());
    }

    [TestMethod]
    public void MoveMergesDestinationTagsAndRemovesOldAssignment()
    {
        var tags = Tags();
        tags.Assign("old", "work"); tags.Assign("new", "personal"); tags.Assign("new", "work");
        tags.MovePath("old", "new");
        Assert.AreEqual(0, tags.TagIdsFor("old").Count);
        Assert.AreEqual(2, tags.TagIdsFor("new").Count);
    }

    [TestMethod]
    public void DatabaseRoundTripPreservesAnIntentionallyEmptyTagList()
    {
        var tags = Tags();
        foreach (var tag in tags.All.ToArray()) tags.Delete(tag.Id);
        tags.Dispose();
        var loaded = Tags();
        Assert.AreEqual(0, loaded.All.Count);
    }

    [TestMethod]
    public void DatabaseRoundTripRetainsCaseInsensitiveAssignments()
    {
        var tags = Tags();
        tags.Assign(@"C:\File.txt", "work");
        tags.Dispose();
        var loaded = Tags();
        Assert.IsTrue(loaded.HasTag(@"c:\FILE.txt", "WORK"));
    }

    [TestMethod]
    public void SaveFailureIsReportedAndClearedAfterRecovery()
    {
        var tags = Tags();
        _ = tags.All;
        _scope.Execute("CREATE TRIGGER FailWrite BEFORE INSERT ON PathTags BEGIN SELECT RAISE(ABORT, 'Disk unavailable'); END");
        Assert.ThrowsException<InvalidOperationException>(() => tags.Assign("one", "work"));
        StringAssert.Contains(tags.LastSaveError!, "Disk unavailable");
        Assert.IsFalse(tags.HasTag("one", "work"));
        _scope.Execute("DROP TRIGGER FailWrite");
        tags.Assign("two", "work");
        Assert.IsNull(tags.LastSaveError);
    }

    [TestMethod]
    public void MissingPathsArePrunedUsingIsolatedExistenceCheck()
    {
        var tags = _scope.Open(path => path == "exists");
        tags.Assign("exists", "work"); tags.Assign("gone", "work");
        Assert.AreEqual(1, tags.PruneMissing());
        Assert.IsTrue(tags.HasTag("exists", "work"));
        Assert.AreEqual(0, tags.TagIdsFor("gone").Count);
    }

    [TestMethod]
    public void CreateReusesCaseInsensitiveNamesAndGeneratesUniqueIds()
    {
        var tags = Tags();
        Assert.AreEqual("work", tags.Create(" WORK ").Id);
        Assert.AreNotEqual(tags.Create("a-b").Id, tags.Create("a b").Id);
        Assert.ThrowsException<ArgumentException>(() => tags.Create(" "));
    }

    [TestMethod]
    public void MalformedJsonReportsErrorWithoutOverwritingInput()
    {
        File.WriteAllText(_scope.LegacyPath, "{not json");
        var tags = Tags();
        Assert.ThrowsException<InvalidOperationException>(() => tags.Find("work"));
        Assert.AreEqual("{not json", File.ReadAllText(_scope.LegacyPath));
        Assert.AreEqual(0L, _scope.Number("PRAGMA user_version"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    public void EmptyQueryHasNoFilters(string? text)
        => Assert.IsTrue(SearchQuery.Parse(text, Tags()).IsEmpty);

    [TestMethod]
    public void QuotedTermsAndExtensionFiltersAreParsedTogether()
    {
        var query = SearchQuery.Parse("\"annual report\" ext:PDF", Tags());
        CollectionAssert.AreEqual(new[] { "annual report" }, query.Terms.ToArray());
        Assert.IsTrue(query.Matches(SearchCoordinatorTests.Item(@"C:\annual report.pdf")));
        Assert.IsFalse(query.Matches(SearchCoordinatorTests.Item(@"C:\annual report.txt")));
    }

    [TestMethod]
    public void MultipleTermsAreAndedAndExtensionsAreAlternatives()
    {
        var query = SearchQuery.Parse("annual report ext:txt ext:.pdf", Tags());
        Assert.IsTrue(query.Matches(SearchCoordinatorTests.Item(@"C:\annual report.TXT")));
        Assert.IsFalse(query.Matches(SearchCoordinatorTests.Item(@"C:\annual.txt")));
    }

    [TestMethod]
    [DataRow("is:unknown")]
    [DataRow("type:999")]
    [DataRow("unknown:value")]
    public void UnsupportedFiltersRemainLiteralInsteadOfMatchingEverything(string text)
    {
        var query = SearchQuery.Parse(text, Tags());
        Assert.IsFalse(query.IsEmpty);
        Assert.IsFalse(query.Matches(SearchCoordinatorTests.Item(@"C:\unrelated.txt")));
        CollectionAssert.Contains(query.Terms.ToArray(), text);
    }

    [TestMethod]
    public void ExplicitUnknownTagMatchesNothingAndKnownTagUsesInjectedStore()
    {
        var tags = Tags(); var file = SearchCoordinatorTests.Item(@"C:\report.txt");
        tags.Assign(file.FullPath, "work");
        Assert.IsTrue(SearchQuery.Parse("tag:WORK", tags).Matches(file));
        Assert.IsFalse(SearchQuery.Parse("tag:not-existing", tags).Matches(file));
    }

    [TestMethod]
    public void KindFilterDistinguishesFilesFromDirectories()
    {
        var tags = Tags();
        var folder = SearchCoordinatorTests.Item(@"C:\folder", FileAttributes.Directory);
        var file = SearchCoordinatorTests.Item(@"C:\file.txt");
        Assert.IsTrue(SearchQuery.Parse("is:folder", tags).Matches(folder));
        Assert.IsFalse(SearchQuery.Parse("is:folder", tags).Matches(file));
        Assert.IsFalse(SearchQuery.Parse("is:file", tags).Matches(folder));
    }
}
