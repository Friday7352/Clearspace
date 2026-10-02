using System.Security.Cryptography;

namespace Clearspace.Services;

internal sealed class FileLockStore(string databasePath, string? legacyPath = null) : IDisposable
{
    // CHANGED (no password rules): the only limit left is the PasswordBox's own 1,024-character cap.
    internal const int MaxPasswordLength = 1024;

    private readonly TagDatabase _database = new(databasePath, legacyPath);
    internal bool HasPassword => _database.Run(false, sql => sql.Number("SELECT COUNT(*) FROM LockSettings") != 0).Value;

    internal void CheckOrCreatePassword(ReadOnlySpan<char> password)
    {
        // CHANGED (no password rules): any non-empty password is accepted. The old 12-character minimum is gone.
        if (password.IsEmpty)
            throw new ArgumentException("Enter a password.");
        if (password.Length > MaxPasswordLength)
            throw new ArgumentException("Passwords can be up to 1,024 characters.");
        var settings = ReadSettings();
        var salt = settings.Salt ?? RandomNumberGenerator.GetBytes(32);
        var verifier = Verifier(password, salt);
        try
        {
            if (settings.Salt is not null)
            {
                if (!CryptographicOperations.FixedTimeEquals(verifier, settings.Verifier!))
                    throw new CryptographicException("The master password is incorrect.");
                return;
            }
            _database.Run(true, sql => sql.Execute("INSERT INTO LockSettings(Id,Salt,Iterations,Verifier) VALUES(1,$p0,$p1,$p2)",
                salt, FileLockFormat.Iterations, verifier));
        }
        finally { CryptographicOperations.ZeroMemory(verifier); }
    }

    // NEW (folder locking): checks a password against the saved master password without creating one.
    // True when no master password exists yet, so locked files brought from another install can still be tried.
    internal bool Matches(ReadOnlySpan<char> password)
    {
        var settings = ReadSettings();
        if (settings.Salt is null) return true;
        var verifier = Verifier(password, settings.Salt);
        try { return CryptographicOperations.FixedTimeEquals(verifier, settings.Verifier!); }
        finally { CryptographicOperations.ZeroMemory(verifier); }
    }

    // NEW: shared by CheckOrCreatePassword and Matches.
    private (byte[]? Salt, byte[]? Verifier) ReadSettings() => _database.Run(false, sql => sql.Query("SELECT Salt, Verifier FROM LockSettings WHERE Id = 1",
        r => ((byte[]?)r[0], (byte[]?)r[1]))).Value.SingleOrDefault();

    // NEW: shared by CheckOrCreatePassword and Matches.
    private static byte[] Verifier(ReadOnlySpan<char> password, byte[] salt)
    {
        var key = FileLockFormat.Derive(password, salt);
        try { return HMACSHA256.HashData(key, "Clearspace master password verifier v1"u8); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal void Record(string path, byte[] header, string state) => _database.Run(true, sql => sql.Execute("""
        INSERT INTO LockedFiles(FilePath,FileId,Header,State,UpdatedUtc) VALUES($p0,$p1,$p2,$p3,$p4)
        ON CONFLICT(FilePath) DO UPDATE SET FileId=excluded.FileId,Header=excluded.Header,State=excluded.State,UpdatedUtc=excluded.UpdatedUtc
        """, path, new Guid(header.AsSpan(8, 16)).ToString("N"), header, state, DateTime.UtcNow.ToString("O")));

    // NEW (Explorer integration): keeps a locked file's row when it is renamed to "<name>.cslock".
    internal void MoveFileRecord(string oldPath, string newPath) => _database.Run(true, sql => sql.Execute(
        "UPDATE OR REPLACE LockedFiles SET FilePath=$p1 WHERE FilePath=$p0", oldPath, newPath));

    internal void Forget(string path) => _database.Run(true, sql => sql.Execute("DELETE FROM LockedFiles WHERE FilePath=$p0", path));
    internal string? State(string path) => _database.Run(false, sql => sql.Query("SELECT State FROM LockedFiles WHERE FilePath=$p0", r => r.GetString(0), path)).Value.SingleOrDefault();
    // NEW (locked folders): folder records. See TagDatabase schema versions 3 and 4.
    // CHANGED (Explorer integration): `owner` is the Clearspace process that opened the folder (null when locked).
    internal void SetFolder(string folder, string state, byte[]? sessionKey, string? owner = null) => _database.Run(true, sql => sql.Execute("""
        INSERT INTO LockedFolders(FolderPath,State,SessionKey,UpdatedUtc,Owner) VALUES($p0,$p1,$p2,$p3,$p4)
        ON CONFLICT(FolderPath) DO UPDATE SET State=excluded.State,SessionKey=excluded.SessionKey,UpdatedUtc=excluded.UpdatedUtc,Owner=excluded.Owner
        """, folder, state, (object?)sessionKey ?? DBNull.Value, DateTime.UtcNow.ToString("O"), (object?)owner ?? DBNull.Value));

    internal (string State, byte[]? SessionKey, string? Owner)? Folder(string folder) => _database.Run(false, sql => sql.Query(
        "SELECT State, SessionKey, Owner FROM LockedFolders WHERE FolderPath=$p0",
        r => ((string State, byte[]? SessionKey, string? Owner)?)(r.GetString(0), r.IsDBNull(1) ? null : (byte[])r[1], r.IsDBNull(2) ? null : r.GetString(2)),
        folder)).Value.SingleOrDefault();

    internal void ForgetFolder(string folder) => _database.Run(true, sql => sql.Execute("DELETE FROM LockedFolders WHERE FolderPath=$p0", folder));

    internal IReadOnlyList<(string Path, string State, string? Owner)> Folders() => _database.Run(false, sql => sql.Query(
        "SELECT FolderPath, State, Owner FROM LockedFolders", r => (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)))).Value;

    // NEW (Explorer integration): locking adds ".cslock" to a file's name, so its tags follow it to the new name
    // (same statements as TagStore.MovePath; TagStore notices the change through PRAGMA data_version).
    internal void MoveTags(string oldPath, string newPath)
    {
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return;
        _database.Run(true, sql =>
        {
            if (sql.Number("SELECT COUNT(*) FROM Paths WHERE Path = $p0", oldPath) == 0) return 0;
            sql.Execute("INSERT INTO Paths(Path) VALUES($p0) ON CONFLICT DO NOTHING", newPath);
            sql.Execute("""
                INSERT INTO PathTags(Path, TagId) SELECT $p0, TagId FROM PathTags WHERE Path = $p1
                ON CONFLICT DO NOTHING
                """, newPath, oldPath);
            sql.Execute("DELETE FROM Paths WHERE Path = $p0", oldPath);
            return 0;
        });
    }

    // NEW (unlock vs remove lock): single files unlocked for a visit. See TagDatabase schema version 5.
    internal void SetOpenFile(string path, string directory, byte[] sessionKey, string owner) => _database.Run(true, sql => sql.Execute("""
        INSERT INTO OpenFiles(FilePath,Directory,SessionKey,Owner,UpdatedUtc) VALUES($p0,$p1,$p2,$p3,$p4)
        ON CONFLICT(FilePath) DO UPDATE SET Directory=excluded.Directory,SessionKey=excluded.SessionKey,Owner=excluded.Owner,UpdatedUtc=excluded.UpdatedUtc
        """, path, directory, sessionKey, owner, DateTime.UtcNow.ToString("O")));

    internal (string Directory, byte[] SessionKey, string? Owner)? OpenFile(string path) => _database.Run(false, sql => sql.Query(
        "SELECT Directory, SessionKey, Owner FROM OpenFiles WHERE FilePath=$p0",
        r => ((string Directory, byte[] SessionKey, string? Owner)?)(r.GetString(0), (byte[])r[1], r.IsDBNull(2) ? null : r.GetString(2)),
        path)).Value.SingleOrDefault();

    internal IReadOnlyList<(string Path, string Directory, string? Owner)> OpenFiles() => _database.Run(false, sql => sql.Query(
        "SELECT FilePath, Directory, Owner FROM OpenFiles", r => (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)))).Value;

    internal void ForgetOpenFile(string path) => _database.Run(true, sql => sql.Execute("DELETE FROM OpenFiles WHERE FilePath=$p0", path));

    // NEW (remove all locks): forgets the master password so the next lock creates a new one.
    internal void ResetPassword() => _database.Run(true, sql => sql.Execute("DELETE FROM LockSettings"));

    // NEW (lock icons): every file currently recorded as locked.
    internal IReadOnlyList<string> LockedFilePaths() => _database.Run(false, sql => sql.Query(
        "SELECT FilePath FROM LockedFiles WHERE State='Locked'", r => r.GetString(0))).Value;

    public void Dispose() => _database.Dispose();
}
