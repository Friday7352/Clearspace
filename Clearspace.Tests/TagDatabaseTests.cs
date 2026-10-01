using System.IO;
using System.Text.Json;
using Clearspace.Models;
using Clearspace.Services;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class TagDatabaseTests
{
    private readonly TagTestScope _scope = new();
    [TestCleanup]
    public void Cleanup() => _scope.Dispose();

    private void Legacy(object definitions, object assignments) => File.WriteAllText(_scope.LegacyPath,
        JsonSerializer.Serialize(new { Definitions = definitions, Assignments = assignments }));

    [TestMethod]
    public void MigrationPreservesDefinitionsColorsAndUnicodeAssignmentsWithoutChangingSource()
    {
        Legacy(new[] { new TagDefinition("étude", "Étude", "#123456") },
            new Dictionary<string, string[]> { [@"C:\École\report.txt"] = ["ÉTUDE", "étude"] });
        var original = File.ReadAllBytes(_scope.LegacyPath);
        var tags = _scope.Open();
        Assert.AreEqual(1, tags.All.Count);
        Assert.AreEqual("#123456", tags.Resolve("ÉTUDE")!.Color);
        Assert.IsTrue(tags.HasTag(@"c:\école\REPORT.TXT", "étude"));
        Assert.AreEqual(1, tags.TagIdsFor(@"C:\École\report.txt").Count);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(_scope.LegacyPath));
        Assert.AreEqual(5L, _scope.Number("PRAGMA user_version")); // CHANGED (unlock vs remove lock): schema v5
        Assert.AreEqual(1L, _scope.Number("SELECT COUNT(*) FROM Paths"));
    }

    [TestMethod]
    public void EmptyLegacyDatabaseDoesNotGetDefaultsAndImportIsNotRepeated()
    {
        Legacy(Array.Empty<TagDefinition>(), new Dictionary<string, string[]>());
        var tags = _scope.Open();
        Assert.AreEqual(0, tags.All.Count);
        tags.Create("After migration");
        tags.Dispose();
        File.WriteAllText(_scope.LegacyPath, "{broken now");
        var reopened = _scope.Open();
        Assert.AreEqual(1, reopened.All.Count);
        Assert.IsNotNull(reopened.Resolve("After migration"));
    }

    [TestMethod]
    public void UnknownLegacyTagRollsBackSchemaAndRowsThenRetriesAfterRepair()
    {
        var definitions = new[] { new TagDefinition("work", "Work", "#123456") };
        Legacy(definitions, new Dictionary<string, string[]> { ["one"] = ["work"], ["two"] = ["unknown"] });
        var original = File.ReadAllBytes(_scope.LegacyPath);
        var tags = _scope.Open();
        Assert.ThrowsException<InvalidOperationException>(() => _ = tags.All);
        Assert.IsNotNull(tags.LastSaveError);
        Assert.AreEqual(0L, _scope.Number("PRAGMA user_version"));
        Assert.AreEqual(0L, _scope.Number("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'"));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(_scope.LegacyPath));
        Legacy(definitions, new Dictionary<string, string[]> { ["one"] = ["work"], ["two"] = ["work"] });
        Assert.IsTrue(tags.HasTag("two", "WORK"));
        Assert.IsTrue(tags.HasTag("one", "work"));
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("{\"Definitions\":null,\"Assignments\":{}}")]
    [DataRow("{\"Definitions\":[],\"Assignments\":{\"one\":null}}")]
    [DataRow("{\"Definitions\":[null],\"Assignments\":{}}")]
    public void InvalidLegacyShapesAreNotSilentlyReplaced(string json)
    {
        File.WriteAllText(_scope.LegacyPath, json);
        Assert.ThrowsException<InvalidOperationException>(() => _ = _scope.Open().All);
        Assert.AreEqual(json, File.ReadAllText(_scope.LegacyPath));
        Assert.AreEqual(0L, _scope.Number("PRAGMA user_version"));
    }

    [TestMethod]
    public void ConflictingLegacyIdsRejectWholeImport()
    {
        Legacy(new[] { new TagDefinition("work", "First", "red"), new TagDefinition("WORK", "Second", "blue") },
            new Dictionary<string, string[]>());
        Assert.ThrowsException<InvalidOperationException>(() => _ = _scope.Open().All);
        Assert.AreEqual(0L, _scope.Number("PRAGMA user_version"));
    }

    [TestMethod]
    public void UnreadableLegacyFileDoesNotInitializeDefaults()
    {
        File.WriteAllText(_scope.LegacyPath, "{}");
        using (new FileStream(_scope.LegacyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsException<InvalidOperationException>(() => _ = _scope.Open().All);
        Assert.AreEqual(0L, _scope.Number("PRAGMA user_version"));
    }

    [TestMethod]
    public void FutureVersionIsRejectedWithoutModification()
    {
        _scope.Execute("PRAGMA user_version = 99");
        var exception = Assert.ThrowsException<InvalidOperationException>(() => _ = _scope.Open().All);
        StringAssert.Contains(exception.Message, "99");
        Assert.AreEqual(99L, _scope.Number("PRAGMA user_version"));
    }

    [TestMethod]
    public void CorruptDatabaseIsNotReplacedWithLegacyData()
    {
        File.WriteAllText(_scope.DatabasePath, "not a database");
        var original = File.ReadAllBytes(_scope.DatabasePath);
        Assert.ThrowsException<InvalidOperationException>(() => _ = _scope.Open().All);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(_scope.DatabasePath));
    }

    [TestMethod]
    public void ForeignKeysAndUniqueAssignmentsAreEnforcedByDatabase()
    {
        var tags = _scope.Open();
        tags.Assign("one", "work");
        Assert.ThrowsException<SqliteException>(() => _scope.Execute("INSERT INTO PathTags VALUES('one', 'unknown')"));
        Assert.ThrowsException<SqliteException>(() => _scope.Execute("INSERT INTO PathTags VALUES('missing', 'work')"));
        Assert.ThrowsException<SqliteException>(() => _scope.Execute("INSERT INTO PathTags VALUES('ONE', 'WORK')"));
        _scope.Execute("DELETE FROM Tags WHERE Id = 'WORK'");
        Assert.AreEqual(0, tags.TagIdsFor("one").Count);
        Assert.AreEqual(0L, _scope.Number("SELECT COUNT(*) FROM pragma_foreign_key_check"));
    }

    [TestMethod]
    public void SqlLookingNamesAndPathsAreStoredAsLiteralValues()
    {
        var tags = _scope.Open();
        const string name = "O'Brien'); DROP TABLE Tags; --";
        const string path = @"C:\O'Brien\'); DELETE FROM PathTags; --.txt";
        var tag = tags.CreateForPaths(name, [path]);
        Assert.AreEqual(name, tags.Find(tag.Id)!.Name);
        Assert.IsTrue(tags.HasTag(path, tag.Id));
        Assert.AreEqual(8, tags.All.Count);
        tags.Rename(tag.Id, "Renamed ' safely");
        Assert.AreEqual("Renamed ' safely", tags.Resolve("renamed ' safely")!.Name);
    }

    private void FailSecondAssignment() => _scope.Execute("""
        CREATE TRIGGER FailSecond BEFORE INSERT ON PathTags WHEN NEW.Path = 'two'
        BEGIN SELECT RAISE(ABORT, 'Simulated storage failure'); END
        """);

    [TestMethod]
    public void BulkToggleRollsBackEarlierRowsAndNotifiesOnlyAfterSuccessfulRecovery()
    {
        var tags = _scope.Open();
        _ = tags.All;
        FailSecondAssignment();
        var events = 0;
        tags.Changed += (_, _) => { events++; Assert.IsTrue(_scope.Open().HasTag("two", "work")); };
        Assert.ThrowsException<InvalidOperationException>(() => tags.ToggleForAll(["one", "two"], "work"));
        Assert.AreEqual(0L, _scope.Number("SELECT COUNT(*) FROM Paths"));
        Assert.AreEqual(0, events);
        Assert.IsNotNull(tags.LastSaveError);
        _scope.Execute("DROP TRIGGER FailSecond");
        tags.ToggleForAll(["one", "two"], "work");
        Assert.AreEqual(1, events);
        Assert.IsNull(tags.LastSaveError);
        Assert.IsTrue(tags.HasTag("one", "work"));
    }

    [TestMethod]
    public void CreateForSelectionRollsBackDefinitionAndAssignmentsTogether()
    {
        var tags = _scope.Open();
        _ = tags.All;
        FailSecondAssignment();
        Assert.ThrowsException<InvalidOperationException>(() => tags.CreateForPaths("New tag", ["one", "two"]));
        Assert.IsNull(tags.Resolve("New tag"));
        Assert.AreEqual(0, tags.Assignments.Count());
        Assert.AreEqual(0L, _scope.Number("SELECT COUNT(*) FROM Paths"));
    }

    [TestMethod]
    public void MoveRollbackKeepsOriginalAndDestinationAssignments()
    {
        var tags = _scope.Open();
        tags.Assign("old", "work");
        tags.Assign("new", "personal");
        _scope.Execute("CREATE TRIGGER FailMove BEFORE DELETE ON Paths BEGIN SELECT RAISE(ABORT, 'Stop move'); END");
        Assert.ThrowsException<InvalidOperationException>(() => tags.MovePath("old", "new"));
        Assert.IsTrue(tags.HasTag("old", "work"));
        Assert.IsTrue(tags.HasTag("new", "personal"));
        Assert.IsFalse(tags.HasTag("new", "work"));
    }

    [TestMethod]
    public void DeleteRollbackRestoresDefinitionAndCascadedAssignments()
    {
        var tags = _scope.Open();
        tags.Assign("one", "work");
        _scope.Execute("CREATE TRIGGER FailCleanup BEFORE DELETE ON Paths BEGIN SELECT RAISE(ABORT, 'Stop cleanup'); END");
        Assert.ThrowsException<InvalidOperationException>(() => tags.Delete("work"));
        Assert.IsNotNull(tags.Find("work"));
        Assert.IsTrue(tags.HasTag("one", "work"));
    }

    [TestMethod]
    public async Task TwoStoresDoNotLoseConcurrentAssignments()
    {
        var first = _scope.Open();
        var second = _scope.Open();
        _ = first.All;
        _ = second.All;
        await Task.WhenAll(Task.Run(() => first.ToggleForAll(Enumerable.Range(0, 50).Select(i => "first" + i).ToArray(), "work")),
            Task.Run(() => second.ToggleForAll(Enumerable.Range(0, 50).Select(i => "second" + i).ToArray(), "personal")));
        Assert.AreEqual(100, first.Assignments.Count());
        Assert.AreEqual(50, second.PathsWithTag("work").Count());
    }

    [TestMethod]
    public void SearchMatchesUseSnapshotAfterDatabaseIsClosed()
    {
        var tags = _scope.Open();
        tags.Assign(@"C:\report.txt", "work");
        var explicitQuery = SearchQuery.Parse("tag:work", tags);
        var implicitQuery = SearchQuery.Parse("work", tags);
        tags.Dispose();
        Assert.IsTrue(explicitQuery.Matches(SearchCoordinatorTests.Item(@"c:\REPORT.txt")));
        CollectionAssert.AreEqual(new[] { @"C:\report.txt" }, explicitQuery.IndexCandidates().ToArray());
        Assert.IsTrue(implicitQuery.Matches(SearchCoordinatorTests.Item(@"c:\REPORT.txt")));
        Assert.IsFalse(explicitQuery.Matches(SearchCoordinatorTests.Item(@"c:\other.txt")));
    }

    [TestMethod]
    public void TagLookupUsesReverseIndexOnLargeDataset()
    {
        var tags = _scope.Open();
        var paths = Enumerable.Range(0, 10000).Select(i => @"C:\data\" + i + ".txt").ToArray();
        tags.ToggleForAll(paths, "work");
        Assert.AreEqual(paths.Length, tags.PathsWithTag("WORK").Count());
        using var connection = _scope.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT Path FROM PathTags WHERE TagId = $id";
        command.Parameters.AddWithValue("$id", "work");
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        StringAssert.Contains(reader.GetString(3), "SEARCH PathTags USING COVERING INDEX IX_PathTags_TagId_Path");
    }
}
