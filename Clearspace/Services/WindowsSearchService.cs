// Clearspace | Windows Search index integration.

using System.Data.OleDb;
using System.Diagnostics;
using System.Text;
using Clearspace.Models;

namespace Clearspace.Services;

public static class WindowsSearchService
{
    private const string ConnectionString =
        "Provider=Search.CollatorDSO;Extended Properties=\"Application=Windows\"";

    private static bool? _available;
    private static DateTime _lastUnavailableCheck = DateTime.MinValue;

    private static readonly TimeSpan UnavailableRetryInterval = TimeSpan.FromSeconds(30);

    public readonly record struct Hit(string Path, bool MatchedContents);

    public static bool IsAvailable
    {
        get
        {
            if (_available == true)
                return true;

            if (_available == false && DateTime.UtcNow - _lastUnavailableCheck < UnavailableRetryInterval)
                return false;

            try
            {
                using var connection = new OleDbConnection(ConnectionString);
                connection.Open();
                _available = true;
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"Clearspace: Windows Search unavailable. {exception.Message}");
                _available = false;
                _lastUnavailableCheck = DateTime.UtcNow;
            }

            return _available.Value;
        }
    }

    public static string? LastError { get; private set; }

    public static IReadOnlyList<Hit> Search(
        SearchQuery query,
        IReadOnlyList<string> roots,
        int maxResults,
        bool matchContents,
        CancellationToken token)
    {
        if (!IsAvailable || query.Terms.Count == 0 || roots.Count == 0)
            return [];

        var sql = BuildSql(query, roots, maxResults, matchContents);

        if (sql is null)
            return [];

        var hits = new List<Hit>();

        try
        {
            using var connection = new OleDbConnection(ConnectionString);
            connection.Open();

            using var command = new OleDbCommand(sql, connection);
            command.CommandTimeout = 20;

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();

                if (reader.IsDBNull(0))
                    continue;

                hits.Add(new Hit(reader.GetString(0), MatchedContents: false));

                if (hits.Count >= maxResults)
                    break;
            }

            LastError = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            Trace.WriteLine($"Clearspace: index query failed. {exception}");
            return [];
        }

        return hits;
    }

    private static string? BuildSql(
        SearchQuery query,
        IReadOnlyList<string> roots,
        int maxResults,
        bool matchContents)
    {
        var scopes = roots
            .Where(root => !root.StartsWith(@"\\", StringComparison.Ordinal))
            .Select(root => $"SCOPE='file:{Escape(root)}'")
            .ToList();

        if (scopes.Count == 0)
            return null;

        var sql = new StringBuilder();
        sql.Append("SELECT TOP ").Append(maxResults).Append(' ');
        sql.Append("System.ItemPathDisplay FROM SystemIndex WHERE (");
        sql.Append(string.Join(" OR ", scopes));
        sql.Append(')');

        foreach (var term in query.Terms)
        {
            var like = Escape(term);
            var contains = Escape(term.Replace("\"", string.Empty));

            if (contains.Length == 0)
                continue;

            sql.Append(" AND (System.FileName LIKE '%").Append(like).Append("%'");

            if (matchContents)
                sql.Append(" OR CONTAINS(System.Search.Contents, '\"").Append(contains).Append("*\"')");

            sql.Append(')');
        }

        foreach (var extension in query.Extensions)
            sql.Append(" AND System.FileExtension = '").Append(Escape(extension)).Append('\'');

        return sql.ToString();
    }

    private static string Escape(string value) => value.Replace("'", "''");

    public static bool OpenIndexingOptions()
    {
        var attempts = new (string File, string Arguments)[]
        {
            ("control.exe", "/name Microsoft.IndexingOptions"),
            ("rundll32.exe", "shell32.dll,Control_RunDLL srchadmin.dll")
        };

        foreach (var attempt in attempts)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = attempt.File,
                    Arguments = attempt.Arguments,
                    UseShellExecute = true
                });

                return true;
            }
            catch (Exception)
            {
            }
        }

        return false;
    }
}
