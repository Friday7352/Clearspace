using System.IO;
using Clearspace.Services;
using Microsoft.Data.Sqlite;

namespace Clearspace.Tests;

internal sealed class TagTestScope : IDisposable
{
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "Clearspace-TagTests-" + Guid.NewGuid().ToString("N"));
    internal string DatabasePath => Path.Combine(DirectoryPath, "tags.db");
    internal string LegacyPath => Path.Combine(DirectoryPath, "tags.json");
    private readonly List<TagStore> _stores = [];

    internal TagTestScope() => Directory.CreateDirectory(DirectoryPath);

    internal TagStore Open(Func<string, bool>? exists = null)
    {
        var store = new TagStore(DatabasePath, LegacyPath, exists ?? (_ => true));
        _stores.Add(store);
        return store;
    }

    internal SqliteConnection Connect()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, ForeignKeys = true, Pooling = false, DefaultTimeout = 1
        }.ToString());
        connection.Open();
        connection.CreateCollation("CLEARSPACE_NOCASE", StringComparer.OrdinalIgnoreCase.Compare);
        return connection;
    }

    internal void Execute(string sql)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal long Number(string sql)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        foreach (var store in _stores) store.Dispose();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
