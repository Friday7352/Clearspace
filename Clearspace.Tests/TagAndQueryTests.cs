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

    // NEW (search fix): a list of rows reads the tags once (Lookup) instead of asking the database per row.
    [TestMethod]
    public void OneLookupAnswersForManyPathsAsTheTagsWereWhenItWasTaken()
    {
        var tags = Tags();
        tags.Assign("one", "work"); tags.Assign("one", "personal"); tags.Assign("two", "work");

        var lookup = tags.Lookup();
        CollectionAssert.AreEqual(tags.TagsFor("one").Select(tag => tag.Id).ToArray(), lookup.TagsFor("one").Select(tag => tag.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "work" }, lookup.TagsFor("TWO").Select(tag => tag.Id).ToArray(), "paths match whatever their capitals");
        Assert.AreEqual(0, lookup.TagsFor("three").Count);

        // A change made afterwards is seen by the store, and by a lookup taken afterwards; the earlier one
        // keeps answering for the moment it was taken, which is all a list being filled needs.
        tags.Assign("three", "work");
        Assert.AreEqual(0, lookup.TagsFor("three").Count);
        Assert.AreEqual(1, tags.TagsFor("three").Count);
        Assert.AreEqual(1, tags.Lookup().TagsFor("three").Count);
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

    // NEW (pre-release review): several renames at once (batch rename), and a renamed folder.
    [TestMethod]
    public void MovePathsKeepsEachItemsOwnTagsWhenNamesShiftAlongOrSwap()
    {
        var tags = Tags();
        tags.Assign("1", "work"); tags.Assign("2", "personal"); tags.Assign("3", "todo");

        tags.MovePaths([("1", "2"), ("2", "3"), ("3", "4")]);
        Assert.AreEqual(0, tags.TagIdsFor("1").Count);
        CollectionAssert.AreEqual(new[] { "work" }, tags.TagIdsFor("2").ToArray());
        CollectionAssert.AreEqual(new[] { "personal" }, tags.TagIdsFor("3").ToArray());
        CollectionAssert.AreEqual(new[] { "todo" }, tags.TagIdsFor("4").ToArray());

        tags.MovePaths([("2", "3"), ("3", "2")]);
        CollectionAssert.AreEqual(new[] { "personal" }, tags.TagIdsFor("2").ToArray());
        CollectionAssert.AreEqual(new[] { "work" }, tags.TagIdsFor("3").ToArray());
        CollectionAssert.AreEqual(new[] { "todo" }, tags.TagIdsFor("4").ToArray(), "an item that was not renamed keeps its tags");
    }

    [TestMethod]
    public void MovePathsCarriesTheTagsOfEverythingInsideARenamedFolder()
    {
        var tags = Tags();
        tags.Assign(@"C:\Old", "work");
        tags.Assign(@"C:\Old\a.txt", "personal");
        tags.Assign(@"C:\Old\Sub\b.txt", "todo");
        tags.Assign(@"C:\Older\c.txt", "work"); // only begins with the same letters; it is not inside C:\Old

        tags.MovePaths([(@"C:\old", @"C:\New")]);

        Assert.IsTrue(tags.HasTag(@"C:\New", "work"));
        Assert.IsTrue(tags.HasTag(@"C:\New\a.txt", "personal"));
        Assert.IsTrue(tags.HasTag(@"C:\New\Sub\b.txt", "todo"));
        Assert.IsTrue(tags.HasTag(@"C:\Older\c.txt", "work"));
        Assert.AreEqual(0, tags.TagIdsFor(@"C:\Old").Count);
        Assert.AreEqual(0, tags.TagIdsFor(@"C:\Old\a.txt").Count);
        Assert.AreEqual(0, tags.TagIdsFor(@"C:\Old\Sub\b.txt").Count);
    }

    [TestMethod]
    public void MovePathsLeavesTagsAloneWhenOnlyTheCapitalsOfANameChange()
    {
        var tags = Tags();
        tags.Assign(@"C:\notes.txt", "work");
        tags.MovePaths([(@"C:\notes.txt", @"C:\Notes.TXT")]);
        Assert.IsTrue(tags.HasTag(@"C:\Notes.TXT", "work"));
        Assert.AreEqual(1, tags.Assignments.Count());
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
