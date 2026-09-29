// Clearspace | SQLite schema, atomic legacy import, and serialized connection ownership.
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Clearspace.Services;

internal sealed class TagDatabase(string databasePath, string? legacyPath) : IDisposable
{
    private readonly object _gate = new();
    private SqliteConnection? _connection;
    private bool _disposed;

    internal (T Value, bool Changed) Run<T>(bool write, Func<TagSql, T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: !write);
            var sql = new TagSql(connection, transaction);
            var value = action(sql);
            transaction.Commit();
            return (value, sql.Changes > 0);
        }
    }

    private SqliteConnection Open()
    {
        if (_connection is not null) return _connection;
        if (databasePath != ":memory:")
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 2
        }.ToString());
        try
        {
            connection.Open();
            // Match the existing Windows path/tag comparisons, including non-ASCII letters.
            connection.CreateCollation("CLEARSPACE_NOCASE", StringComparer.OrdinalIgnoreCase.Compare);
            using var transaction = connection.BeginTransaction();
            var sql = new TagSql(connection, transaction);
            var version = sql.Number("PRAGMA user_version");
            if (version != 0 && version != 1)
                throw new InvalidDataException($"Tag database version {version} is not supported by this Clearspace version.");
            if (version == 0)
            {
                // Reject unrelated/unversioned databases instead of modifying their tables.
                if (sql.Number("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'") != 0)
                    throw new InvalidDataException("The tag database contains an unrecognized schema.");
                sql.Execute("""
                    CREATE TABLE Tags (
                        Id TEXT COLLATE CLEARSPACE_NOCASE PRIMARY KEY NOT NULL CHECK(length(trim(Id)) > 0),
                        Name TEXT COLLATE CLEARSPACE_NOCASE NOT NULL CHECK(length(trim(Name)) > 0),
                        Color TEXT NOT NULL CHECK(length(Color) > 0)
                    );
                    CREATE INDEX IX_Tags_Name ON Tags(Name);
                    CREATE TABLE Paths (
                        Path TEXT COLLATE CLEARSPACE_NOCASE PRIMARY KEY NOT NULL CHECK(length(trim(Path)) > 0)
                    );
                    CREATE TABLE PathTags (
                        Path TEXT COLLATE CLEARSPACE_NOCASE NOT NULL REFERENCES Paths(Path) ON DELETE CASCADE,
                        TagId TEXT COLLATE CLEARSPACE_NOCASE NOT NULL REFERENCES Tags(Id) ON DELETE CASCADE,
                        PRIMARY KEY (Path, TagId)
                    );
                    CREATE INDEX IX_PathTags_TagId_Path ON PathTags(TagId, Path);
                    """);
                Import(sql);
                sql.Execute("PRAGMA user_version = 1");
            }
            transaction.Commit();
            _connection = connection;
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void Import(TagSql sql)
    {
        // Only a genuinely absent source gets defaults. Malformed/unreadable input must be repaired.
        string? json = null;
        if (legacyPath is not null)
        {
            try { json = File.ReadAllText(legacyPath); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        if (json is null)
        {
            foreach (var tag in TagStore.Defaults)
                sql.Execute("INSERT INTO Tags(Id, Name, Color) VALUES($p0, $p1, $p2)", tag.Id, tag.Name, tag.Color);
            return;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("Definitions", out var definitions) || definitions.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("Assignments", out var assignments) || assignments.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("tags.json must contain Definitions and Assignments. The original file has been kept.");

        foreach (var item in definitions.EnumerateArray())
        {
            var tag = item.Deserialize<TagDefinition>() ?? throw new InvalidDataException("A legacy tag is null.");
            if (string.IsNullOrWhiteSpace(tag.Id) || string.IsNullOrWhiteSpace(tag.Name) || string.IsNullOrWhiteSpace(tag.Color))
                throw new InvalidDataException("A legacy tag has a missing ID, name, or color.");
            sql.Execute("INSERT INTO Tags(Id, Name, Color) VALUES($p0, $p1, $p2)", tag.Id, tag.Name, tag.Color);
        }
        foreach (var entry in assignments.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Value.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("A legacy tag assignment has an invalid path or tag list.");
            foreach (var item in entry.Value.EnumerateArray())
            {
                var id = item.GetString();
                if (string.IsNullOrWhiteSpace(id) || sql.Number("SELECT COUNT(*) FROM Tags WHERE Id = $p0", id) == 0)
                    throw new InvalidDataException($"A legacy assignment references an unknown tag: {id}.");
                sql.Execute("INSERT INTO Paths(Path) VALUES($p0) ON CONFLICT DO NOTHING", entry.Name);
                sql.Execute("INSERT INTO PathTags(Path, TagId) VALUES($p0, $p1) ON CONFLICT DO NOTHING", entry.Name, id);
            }
        }
        // The JSON is never deleted or rewritten. Schema, imported rows, and version commit together.
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _connection?.Dispose();
            _connection = null;
        }
    }
}

internal sealed class TagSql(SqliteConnection connection, SqliteTransaction transaction)
{
    public int Changes { get; private set; }

    private SqliteCommand Command(string text, object[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        for (var i = 0; i < values.Length; i++)
            command.Parameters.AddWithValue($"$p{i}", values[i]);
        return command;
    }

    public int Execute(string text, params object[] values)
    {
        using var command = Command(text, values);
        var count = command.ExecuteNonQuery();
        Changes += Math.Max(0, count);
        return count;
    }

    public long Number(string text, params object[] values)
    {
        using var command = Command(text, values);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public List<T> Query<T>(string text, Func<SqliteDataReader, T> read, params object[] values)
    {
        using var command = Command(text, values);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(read(reader));
        return result;
    }
}
