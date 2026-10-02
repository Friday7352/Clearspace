using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Clearspace.Services;

internal sealed record FileLockResult(bool Locked, string? Warning = null)
{
    // NEW (Explorer integration): where the file is now — locking adds ".cslock", unlocking removes it.
    public string Path { get; init; } = string.Empty;
}

// NEW (folder locking): progress for a folder run. Index is zero-based; Name is relative to the folder.
internal sealed record FolderLockProgress(int Index, int Total, string Name);

// NEW (folder locking): outcome of locking or unlocking every file in a folder.
// Changed = files this run locked/unlocked; AlreadyDone = files that were already in the requested state;
// Skipped = files left as they were, with the reason; Warnings = files changed whose database row failed.
// NeedsPassword (reset-proof): the saved key for locking it again couldn't be read (e.g. Windows was reset).
internal sealed record FolderLockResult(
    string Folder, bool Locking, int Total, int Changed, int AlreadyDone,
    IReadOnlyList<FolderLockSkip> Skipped, int Warnings, bool Cancelled, bool NeedsPassword = false);

// CHANGED (locked folders): Retryable marks files skipped only because another program had them open,
// so re-locking a folder you left can try them again later.
internal sealed record FolderLockSkip(string Path, string Reason, bool Retryable = false);

// NEW (unlock vs remove lock): outcome of "Remove lock" on one or more files/folders (or on everything).
// DissolvedParents = locked folders that contained a selected item and stopped asking for a password
// (their other files stay locked). PasswordReset = the master password was cleared ("Remove all locks").
internal sealed record LockRemovalResult(int Removed, int AlreadyUnlocked, IReadOnlyList<FolderLockSkip> Skipped,
    IReadOnlyList<string> DissolvedParents, bool Cancelled, bool PasswordReset = false);

// NEW (unlock vs remove lock): outcome of locking a visit-unlocked file again.
internal sealed record FileRelockResult(bool Done, bool InUse, string? Error = null, bool NeedsPassword = false);

internal sealed class FileLockService(string databasePath, string? legacyPath = null)
{
    // A test seam at durable boundaries, also used to verify interruption recovery.
    internal Action<string>? Checkpoint { get; init; }

    // NEW (Explorer integration): give locked folders the lock icon in Explorer (desktop.ini). Tests turn it off.
    internal bool ExplorerIcons { get; init; } = true;

    // NEW (Explorer integration): identifies this running Clearspace ("pid:start ticks") as the owner of the
    // folders it opens, so another Clearspace window never locks them again underneath it.
    internal static string CurrentOwner { get; } = OwnerId(Process.GetCurrentProcess());

    private static string OwnerId(Process process) => $"{process.Id}:{process.StartTime.ToUniversalTime().Ticks}";

    // False when the owner process has exited (e.g. Clearspace crashed), so its open folders can be locked.
    internal static bool IsOwnerAlive(string? owner)
    {
        if (owner is null) return false;
        if (owner == CurrentOwner) return true;
        var parts = owner.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var pid)) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!string.Equals(process.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase)) return false;
            return OwnerId(process) == owner;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    internal bool HasPassword { get { using var store = new FileLockStore(databasePath, legacyPath); return store.HasPassword; } }

    internal static bool IsLocked(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> magic = stackalloc byte[8];
        return file.Read(magic) == 8 && magic.SequenceEqual(FileLockFormat.Magic);
    }

    // NEW: IsLocked for callers that must never throw (opening a file the normal way should still work
    // when the file can't be read here, e.g. another program holds it exclusively).
    internal static bool TryIsLocked(string path)
    {
        try { return IsLocked(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // CHANGED (folder locking): the single-file entry point now opens the store and a one-use key cache,
    // then runs the shared Transform below. Behaviour for one file is unchanged.
    internal FileLockResult Transform(string path, char[] password, bool locking, CancellationToken token = default)
    {
        using var store = new FileLockStore(databasePath, legacyPath);
        using var keys = new FileLockKeys(password);
        return Transform(path, keys, store, locking ? password : null, locking, token);
    }

    // NEW (folder locking): locks or unlocks every file in a folder and its subfolders with one password.
    // The password is checked once up front (locking creates the master password the first time; unlocking
    // stops immediately if it doesn't match the saved one). Each file is then handled exactly like a single
    // file, so a file that can't be locked is skipped and reported instead of stopping the rest.
    // (Files only — it does not create or remove a locked-folder record; see LockFolder / OpenFolder below.)
    internal FolderLockResult TransformFolder(string folder, char[] password, bool locking,
        IProgress<FolderLockProgress>? progress = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        folder = FileLockSafety.ValidateFolder(folder);
        using var store = new FileLockStore(databasePath, legacyPath);
        using var keys = new FileLockKeys(password);
        if (locking) CheckPassword(store, password, locking: true);
        else VerifyUnlockPassword(store, keys, password, LockedCandidates([folder])); // FIXED (reset-proof)
        return RunFolder(folder, keys, store, locking, progress, token);
    }

    // NEW (locked folders): locks a folder so it asks for the password before it opens. Encrypts every
    // file inside, then records the folder as Locked. Locked folders already inside it are absorbed into it.
    internal FolderLockResult LockFolder(string folder, char[] password,
        IProgress<FolderLockProgress>? progress = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        folder = FileLockSafety.ValidateFolder(folder);
        using var store = new FileLockStore(databasePath, legacyPath);
        var enclosing = EnclosingLockedFolder(store, folder);
        if (enclosing is not null && !SamePath(enclosing, folder))
            throw new IOException("This folder is inside a locked folder (" + Path.GetFileName(enclosing) + "), which already protects it.");
        CheckPassword(store, password, locking: true);
        foreach (var (path, _, _) in store.Folders())
        {
            if (!IsInside(path, folder)) continue;
            store.ForgetFolder(path);
            if (ExplorerIcons) TryFolderIcon(path, set: false);
        }
        // Record first so an interrupted run still asks for the password next time.
        store.SetFolder(folder, "Locked", null);
        if (ExplorerIcons) TryFolderIcon(folder, set: true); // NEW (Explorer integration)
        using var keys = new FileLockKeys(password);
        return RunFolder(folder, keys, store, locking: true, progress, token);
    }

    // NEW (locked folders): opens a locked folder for one visit. The password is checked against the master
    // password first; then the visit's key is saved (DPAPI-protected) BEFORE any file is decrypted, so if
    // Clearspace stops part-way the folder can still be locked again on the next start.
    // CHANGED (switch to current password): with `switchTo` (your current Clearspace password), files are
    // opened with `password` (an older one) or the current one, and the folder locks again with the current one.
    internal FolderLockResult OpenFolder(string folder, char[] password,
        IProgress<FolderLockProgress>? progress = null, CancellationToken token = default, char[]? switchTo = null)
    {
        token.ThrowIfCancellationRequested();
        folder = FileLockSafety.ValidateFolder(folder);
        using var store = new FileLockStore(databasePath, legacyPath);
        if (store.Folder(folder) is null) throw new IOException("This folder is not locked.");
        using var keys = new FileLockKeys(password);
        VerifyUnlockPassword(store, keys, password, LockedCandidates([folder])); // FIXED (reset-proof)
        using var current = SwitchKeys(store, keys, switchTo);
        var session = (current ?? keys).ExportSessionKey();
        try { store.SetFolder(folder, "Open", SessionKeyProtector.Protect(session), CurrentOwner); } // CHANGED: + owner
        finally { CryptographicOperations.ZeroMemory(session); }
        return RunFolder(folder, keys, store, locking: false, progress, token);
    }

    // NEW (locked folders): locks an opened folder again using the visit's saved key (no password needed).
    // Files another program still has open are skipped as Retryable and the folder stays Open so the
    // caller can try again; otherwise the folder goes back to Locked and the saved key is deleted.
    internal FolderLockResult RelockFolder(string folder, IProgress<FolderLockProgress>? progress = null, CancellationToken token = default)
    {
        using var store = new FileLockStore(databasePath, legacyPath);
        var record = store.Folder(folder);
        if (record is not { State: "Open" } open)
            return new FolderLockResult(folder, true, 0, 0, 0, [], 0, false);
        // NEW (Explorer integration): another running Clearspace has this folder open; leave it to that one.
        if (open.Owner != CurrentOwner && IsOwnerAlive(open.Owner))
            return new FolderLockResult(folder, true, 0, 0, 0, [], 0, false);
        if (!Directory.Exists(folder))
        {
            store.ForgetFolder(folder); // moved or deleted outside Clearspace
            return new FolderLockResult(folder, true, 0, 0, 0, [], 0, false);
        }
        // FIXED (reset-proof): after a Windows reset or account change the saved key can't be read; ask for the
        // password instead of failing (the folder's files stay readable until then, nothing is lost).
        byte[] session;
        try
        {
            if (open.SessionKey is null) throw new CryptographicException("The saved key for this folder is missing.");
            session = SessionKeyProtector.Unprotect(open.SessionKey);
        }
        catch (CryptographicException)
        {
            return new FolderLockResult(folder, true, 0, 0, 0, [], 0, false, NeedsPassword: true);
        }
        FolderLockResult result;
        try
        {
            using var keys = FileLockKeys.FromSessionKey(session);
            result = RunFolder(folder, keys, store, locking: true, progress, token);
        }
        finally { CryptographicOperations.ZeroMemory(session); }
        if (!result.Cancelled && !result.Skipped.Any(skip => skip.Retryable))
            store.SetFolder(folder, "Locked", null);
        if (ExplorerIcons) TryFolderIcon(folder, set: true); // FIXED: folders locked before icons existed get one too
        return result;
    }

    // NEW (locked folders): Unlock on a locked folder — decrypts everything and stops asking for a password.
    internal FolderLockResult RemoveFolderLock(string folder, char[] password,
        IProgress<FolderLockProgress>? progress = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        folder = FileLockSafety.ValidateFolder(folder);
        using var store = new FileLockStore(databasePath, legacyPath);
        using var keys = new FileLockKeys(password);
        VerifyUnlockPassword(store, keys, password, LockedCandidates([folder])); // FIXED (reset-proof)
        var result = RunFolder(folder, keys, store, locking: false, progress, token);
        if (!result.Cancelled)
        {
            store.ForgetFolder(folder);
            if (ExplorerIcons) TryFolderIcon(folder, set: false); // NEW (Explorer integration)
        }
        return result;
    }

    // ---- NEW (unlock vs remove lock) ----

    // "Unlock": decrypts one locked file for a visit. Like OpenFolder, the visit's key is saved (DPAPI) before
    // the file is touched, so it can be locked again without the password when Clearspace leaves its folder.
    // CHANGED (switch to current password): `switchTo` as in OpenFolder.
    internal FileLockResult OpenFile(string path, char[] password, CancellationToken token = default, char[]? switchTo = null)
    {
        token.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        using var store = new FileLockStore(databasePath, legacyPath);
        if (!IsLocked(path)) throw new IOException("This file isn't locked.");
        var unlocked = LockNames.Unlocked(path);
        using var keys = new FileLockKeys(password);
        var usesMaster = VerifyUnlockPassword(store, keys, password, [path]); // FIXED (reset-proof)
        using var current = SwitchKeys(store, keys, switchTo);
        var session = (current ?? keys).ExportSessionKey();
        try { store.SetOpenFile(unlocked, Path.GetDirectoryName(path)!, SessionKeyProtector.Protect(session), CurrentOwner); }
        finally { CryptographicOperations.ZeroMemory(session); }
        try
        {
            var result = Transform(path, keys, store, null, locking: false, token);
            // NEW (change password): say so when the file uses an older password.
            if (current is not null)
                return result with { Warning = result.Warning ?? $"Unlocked {Path.GetFileName(result.Path)}. It locks with your current Clearspace password from now on." };
            return usesMaster || !store.HasPassword || result.Warning is not null ? result
                : result with { Warning = $"Unlocked {Path.GetFileName(result.Path)}. It still uses its older password." };
        }
        catch
        {
            store.ForgetOpenFile(unlocked); // nothing was unlocked, so there's nothing to lock again
            throw;
        }
    }

    // Locks a visit-unlocked file again with the saved key. Files another program has open report InUse and
    // stay unlocked for a later retry; files opened by another running Clearspace are left to it.
    internal FileRelockResult RelockFile(string unlockedPath)
    {
        using var store = new FileLockStore(databasePath, legacyPath);
        if (store.OpenFile(unlockedPath) is not { } row) return new FileRelockResult(true, false);
        if (row.Owner != CurrentOwner && IsOwnerAlive(row.Owner)) return new FileRelockResult(false, false);
        if (!File.Exists(unlockedPath) || TryIsLocked(unlockedPath))
        {
            // Moved, deleted, or already locked some other way: nothing left to do.
            if (File.Exists(unlockedPath)) NormalizeName(unlockedPath, locking: true, store);
            store.ForgetOpenFile(unlockedPath);
            return new FileRelockResult(true, false);
        }
        byte[] session;
        try { session = SessionKeyProtector.Unprotect(row.SessionKey); }
        catch (CryptographicException) { return new FileRelockResult(false, false, NeedsPassword: true); } // FIXED (reset-proof)
        try
        {
            using var keys = FileLockKeys.FromSessionKey(session);
            Transform(unlockedPath, keys, store, null, locking: true, default);
            store.ForgetOpenFile(unlockedPath);
            return new FileRelockResult(true, false);
        }
        catch (Exception e) when (e is IOException && (e.HResult & 0xFFFF) is 32 or 33)
        {
            return new FileRelockResult(false, true, "Open in another program.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or
            CryptographicException or Win32Exception or SqliteException)
        {
            return new FileRelockResult(false, false, e.Message);
        }
        finally { CryptographicOperations.ZeroMemory(session); }
    }

    // "Remove lock" on any mix of files and folders: decrypts them for good and stops asking for a password.
    // A selected item inside a locked folder means that folder stops being a locked folder (its other files
    // stay locked one by one); if it was open for a visit, its files are locked again first.
    internal LockRemovalResult RemoveLocks(IReadOnlyList<string> paths, char[] password,
        IProgress<FolderLockProgress>? progress = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var targets = paths.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        using var store = new FileLockStore(databasePath, legacyPath);
        using var keys = new FileLockKeys(password);
        VerifyUnlockPassword(store, keys, password, LockedCandidates(targets), allowIfNothingLocked: true); // FIXED (reset-proof)
        var skipped = new List<FolderLockSkip>();
        var dissolved = new List<string>();
        int removed = 0, already = 0;
        var cancelled = false;

        // 1. Locked folders that contain a selected item (and aren't selected themselves) stop asking.
        foreach (var parent in targets.Select(path => EnclosingLockedFolder(store, path))
                     .OfType<string>()
                     .Where(parent => !targets.Any(path => SamePath(path, parent)))
                     .Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (store.Folder(parent) is { State: "Open" })
            {
                var relocked = RelockFolder(parent, null, token);
                skipped.AddRange(relocked.Skipped.Select(skip => skip with { Reason = skip.Reason + " (left unlocked)" }));
            }
            store.ForgetFolder(parent);
            if (ExplorerIcons) TryFolderIcon(parent, set: false);
            dissolved.Add(parent);
        }

        // 2. The selected items themselves.
        for (var i = 0; i < targets.Count && !cancelled; i++)
        {
            if (token.IsCancellationRequested) { cancelled = true; break; }
            var path = targets[i];
            if (Directory.Exists(path))
            {
                foreach (var (folder, _, _) in store.Folders())
                {
                    if (!SamePath(folder, path) && !IsInside(folder, path)) continue;
                    store.ForgetFolder(folder);
                    if (ExplorerIcons) TryFolderIcon(folder, set: false);
                }
                var result = RunFolder(path, keys, store, locking: false, progress, token);
                removed += result.Changed;
                already += result.AlreadyDone;
                skipped.AddRange(result.Skipped);
                cancelled = result.Cancelled;
                foreach (var (open, _, _) in store.OpenFiles())
                    if (IsInside(open, path)) store.ForgetOpenFile(open);
                continue;
            }

            progress?.Report(new FolderLockProgress(i, targets.Count, Path.GetFileName(path)));
            var unlocked = LockNames.Unlocked(path);
            if (store.OpenFile(unlocked) is not null)
            {
                store.ForgetOpenFile(unlocked); // unlocked for a visit: it simply isn't locked again
                if (File.Exists(unlocked) && !TryIsLocked(unlocked)) { removed++; continue; }
            }
            // The parent folder was locked again in step 1, so the file may now carry its .cslock name.
            if (!File.Exists(path) && File.Exists(LockNames.Locked(path))) path = LockNames.Locked(path);
            if (!File.Exists(path)) { skipped.Add(new FolderLockSkip(path, "No longer exists.")); continue; }
            try
            {
                if (!IsLocked(path)) { NormalizeName(path, locking: false, store); already++; continue; }
                Transform(path, keys, store, null, locking: false, token);
                removed++;
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or
                CryptographicException or Win32Exception or SqliteException or ArgumentException)
            {
                var inUse = e is IOException && (e.HResult & 0xFFFF) is 32 or 33;
                skipped.Add(new FolderLockSkip(path, inUse ? "Open in another program." : e.Message, inUse));
            }
        }
        return new LockRemovalResult(removed, already, skipped, dissolved, cancelled);
    }

    // NEW (change password): moves locked files (or every locked file in the selected folders) from the password
    // they use now to your current Clearspace password — e.g. files copied from another PC, or locked before a
    // reset. Each file is re-encrypted in one step (fresh keys, never readable on disk in between). If you have
    // no Clearspace password yet, `newPassword` becomes it. Files already on it are counted, not rewritten.
    internal LockRemovalResult ChangePassword(IReadOnlyList<string> paths, char[] oldPassword, char[] newPassword,
        IProgress<FolderLockProgress>? progress = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (newPassword.Length == 0) throw new ArgumentException("Enter your Clearspace password.");
        var targets = paths.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        using var store = new FileLockStore(databasePath, legacyPath);
        using var oldKeys = new FileLockKeys(oldPassword);
        VerifyUnlockPassword(store, oldKeys, oldPassword, LockedCandidates(targets));
        store.CheckOrCreatePassword(newPassword);
        using var newKeys = new FileLockKeys(newPassword);

        var files = targets
            .SelectMany(target => Directory.Exists(target) ? FileLockSafety.EnumerateFolder(target)
                : File.Exists(target) ? [target] : File.Exists(LockNames.Locked(target)) ? [LockNames.Locked(target)] : [target])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var skipped = new List<FolderLockSkip>();
        int changed = 0, already = 0;
        var cancelled = false;
        for (var i = 0; i < files.Count; i++)
        {
            if (token.IsCancellationRequested) { cancelled = true; break; }
            var file = files[i];
            progress?.Report(new FolderLockProgress(i, files.Count, Path.GetFileName(file)));
            try
            {
                if (!File.Exists(file)) { skipped.Add(new FolderLockSkip(file, "No longer exists.")); continue; }
                if (ReadHeader(file) is not { } header) continue; // not locked: nothing to change
                if (FileLockFormat.Opens(header, newKeys)) { already++; continue; }
                Transform(file, oldKeys, store, null, locking: false, token, rekeyTo: newKeys);
                changed++;
            }
            catch (OperationCanceledException) { cancelled = true; break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or
                CryptographicException or Win32Exception or SqliteException or ArgumentException)
            {
                var inUse = e is IOException && (e.HResult & 0xFFFF) is 32 or 33;
                var reason = inUse ? "Open in another program." : e is CryptographicException ? "Uses a different password than the one entered." : e.Message;
                skipped.Add(new FolderLockSkip(file, reason, inUse));
            }
        }
        progress?.Report(new FolderLockProgress(files.Count, files.Count, string.Empty));
        return new LockRemovalResult(changed, already, skipped, [], cancelled);
    }

    // "Remove all locks and reset password": removes the lock from every locked folder and file Clearspace
    // knows about, then clears the master password so the next lock creates a new one. The password is only
    // cleared when everything was unlocked, so nothing is left locked with a password that's been forgotten.
    internal LockRemovalResult RemoveAllLocks(char[] password, IProgress<FolderLockProgress>? progress = null, CancellationToken token = default)
    {
        List<string> targets;
        using (var store = new FileLockStore(databasePath, legacyPath))
        {
            CheckPassword(store, password, locking: false);
            var folders = store.Folders().Select(folder => folder.Path).Where(Directory.Exists).ToList();
            targets = folders
                .Concat(store.LockedFilePaths().Concat(store.OpenFiles().Select(open => open.Path))
                    .Where(file => !folders.Any(folder => IsInside(file, folder))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        var result = RemoveLocks(targets, password, progress, token);
        using (var store = new FileLockStore(databasePath, legacyPath))
        {
            // Rows for things that no longer exist (moved or deleted outside Clearspace).
            foreach (var (folder, _, _) in store.Folders()) if (!Directory.Exists(folder)) store.ForgetFolder(folder);
            foreach (var (open, _, _) in store.OpenFiles()) if (!File.Exists(open)) store.ForgetOpenFile(open);
            foreach (var file in store.LockedFilePaths()) if (!File.Exists(file)) store.Forget(file);
            var missing = result.Skipped.Count(skip => skip.Reason == "No longer exists.");
            if (!result.Cancelled && result.Skipped.Count == missing)
            {
                store.ResetPassword();
                result = result with { PasswordReset = true };
            }
        }
        return result;
    }

    // FIXED (Explorer integration): gives every locked folder its Explorer lock icon. Folders locked by an
    // earlier build had no desktop.ini, and "Lock" on an already-locked folder doesn't run again, so
    // Clearspace applies any missing icons when it starts. Already-marked folders are skipped quickly.
    internal void EnsureFolderIcons()
    {
        if (!ExplorerIcons) return;
        using var store = new FileLockStore(databasePath, legacyPath);
        foreach (var (folder, _, _) in store.Folders())
            if (Directory.Exists(folder)) TryFolderIcon(folder, set: true);
    }

    // NEW (locked folders): the outermost locked-folder record that is, or contains, `path`.
    internal string? EnclosingLockedFolder(string path)
    {
        using var store = new FileLockStore(databasePath, legacyPath);
        return EnclosingLockedFolder(store, path);
    }

    // NEW (Explorer integration): the folder icon is cosmetic; never fail a lock because of it.
    private static void TryFolderIcon(string folder, bool set)
    {
        try
        {
            if (set) ShellIntegration.SetFolderIcon(folder);
            else ShellIntegration.ClearFolderIcon(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string? EnclosingLockedFolder(FileLockStore store, string path) => store.Folders()
        .Where(record => SamePath(record.Path, path) || IsInside(path, record.Path))
        .OrderBy(record => record.Path.Length)
        .Select(record => record.Path)
        .FirstOrDefault();

    internal static bool SamePath(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    // True when `path` is strictly inside `folder` (separator-checked: C:\Sam is not inside C:\Sa).
    internal static bool IsInside(string path, string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(folder);
        return path.Length > folder.Length + 1 &&
               path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
               (path[folder.Length] == Path.DirectorySeparatorChar || folder.EndsWith(Path.DirectorySeparatorChar));
    }

    // FIXED (reset-proof): unlocking accepts the password a file was actually locked with, not only the saved
    // master password. After a Windows reset, a new PC, or "Remove all locks and reset password", the master
    // password can differ from older files' passwords; those files must still open with their own. The master
    // check stays as the quick path; otherwise the password is tried on a few of the locked files in scope
    // (one key derivation per distinct salt, at most five) before anything is changed.
    // Returns true when the password is the current master password (or there is none yet).
    // `allowIfNothingLocked`: Remove lock may go ahead when nothing in scope is locked any more; opening for a
    // visit may not, because the visit's password is what the files get locked with again afterwards.
    private static bool VerifyUnlockPassword(FileLockStore store, FileLockKeys keys, char[] password, IEnumerable<string> files,
        bool allowIfNothingLocked = false)
    {
        if (password.Length == 0) throw new CryptographicException("Enter a password.");
        // Only a real saved password is a shortcut: with none saved (new PC, after a reset) the password must open
        // one of the files, so a typo can never become the key that files are locked again with.
        if (store.HasPassword && store.Matches(password)) return true;
        var salts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (ReadHeader(file) is not { } header) continue;
            if (!salts.Add(Convert.ToHexString(header.AsSpan(36, 32)))) continue;
            if (FileLockFormat.Opens(header, keys)) return false;
            if (salts.Count >= 5) break;
        }
        if (salts.Count == 0 && allowIfNothingLocked) return false;
        throw new CryptographicException("The password is incorrect. Nothing was unlocked.");
    }

    // NEW (switch to current password): tells the prompt whether a password is the current Clearspace password
    // (true) or an older one that still opens these files (false). Throws when it opens neither.
    internal bool IsCurrentPassword(IReadOnlyList<string> paths, char[] password)
    {
        using var store = new FileLockStore(databasePath, legacyPath);
        using var keys = new FileLockKeys(password);
        return VerifyUnlockPassword(store, keys, password, LockedCandidates(paths.Select(Path.GetFullPath)));
    }

    // NEW (switch to current password): checks (or, with none saved yet, creates) the current password and adds
    // its keys as a fallback, so files already on it still open. Returns the keys to lock with afterwards.
    private static FileLockKeys? SwitchKeys(FileLockStore store, FileLockKeys keys, char[]? switchTo)
    {
        if (switchTo is null) return null;
        store.CheckOrCreatePassword(switchTo);
        var current = new FileLockKeys(switchTo);
        keys.Fallback = current;
        return current;
    }

    // The locked files a password check can try for these targets (folders are searched lazily).
    private static IEnumerable<string> LockedCandidates(IEnumerable<string> targets)
    {
        foreach (var target in targets)
        {
            if (Directory.Exists(target))
            {
                foreach (var file in FileLockSafety.EnumerateFolder(target)) yield return file;
            }
            else
            {
                yield return target;
                yield return LockNames.Locked(target);
            }
        }
    }

    private static byte[]? ReadHeader(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < FileLockFormat.HeaderSize) return null;
            var header = new byte[FileLockFormat.HeaderSize];
            stream.ReadExactly(header);
            return header.AsSpan(0, 8).SequenceEqual(FileLockFormat.Magic) ? header : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void CheckPassword(FileLockStore store, char[] password, bool locking)
    {
        if (locking) store.CheckOrCreatePassword(password);
        else if (password.Length == 0 || !store.Matches(password))
            throw new CryptographicException("The password is incorrect. Nothing was unlocked.");
    }

    // CHANGED (locked folders): the per-file loop, shared by every folder operation.
    private FolderLockResult RunFolder(string folder, FileLockKeys keys, FileLockStore store, bool locking,
        IProgress<FolderLockProgress>? progress, CancellationToken token)
    {
        var files = FileLockSafety.EnumerateFolder(folder).ToList();
        var skipped = new List<FolderLockSkip>();
        int changed = 0, already = 0, warnings = 0;
        var cancelled = false;
        for (var i = 0; i < files.Count; i++)
        {
            if (token.IsCancellationRequested) { cancelled = true; break; }
            var file = files[i];
            progress?.Report(new FolderLockProgress(i, files.Count, Path.GetRelativePath(folder, file)));
            try
            {
                if (IsLocked(file) == locking)
                {
                    already++;
                    NormalizeName(file, locking, store); // NEW (Explorer integration)
                    continue;
                }
                var result = Transform(file, keys, store, null, locking, token);
                changed++;
                if (result.Warning is not null) warnings++;
            }
            catch (OperationCanceledException) { cancelled = true; break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or
                CryptographicException or Win32Exception or SqliteException or ArgumentException)
            {
                // 32 = sharing violation, 33 = lock violation: another program has the file open.
                var inUse = e is IOException && (e.HResult & 0xFFFF) is 32 or 33;
                skipped.Add(new FolderLockSkip(file, inUse ? "Open in another program." : e.Message, inUse));
            }
        }
        progress?.Report(new FolderLockProgress(files.Count, files.Count, string.Empty));
        return new FolderLockResult(folder, locking, files.Count, changed, already, skipped, warnings, cancelled);
    }

    // NEW (Explorer integration): a file already in the requested state gets the matching name, e.g. a file
    // locked before ".cslock" names existed gains the extension. Skipped quietly if the name is taken.
    private static void NormalizeName(string file, bool locking, FileLockStore store)
    {
        var target = locking ? LockNames.Locked(file) : LockNames.Unlocked(file);
        if (SamePath(target, file) || File.Exists(target) || Directory.Exists(target)) return;
        try
        {
            File.Move(file, target);
            store.MoveFileRecord(file, target);
            store.MoveTags(file, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException) { }
    }

    // CHANGED (folder locking): shared by both entry points. `masterPassword` is checked/created only
    // when non-null (single-file locks); folder runs check it once before the loop instead.
    // CHANGED (change password): with `rekeyTo`, an unlock re-encrypts straight to those keys instead of
    // writing the readable contents — one replacement, so the file is never readable on disk in between.
    private FileLockResult Transform(string path, FileLockKeys keys, FileLockStore store, char[]? masterPassword,
        bool locking, CancellationToken token, FileLockKeys? rekeyTo = null)
    {
        token.ThrowIfCancellationRequested();
        path = FileLockSafety.ValidatePath(path);
        var endsLocked = locking || rekeyTo is not null;
        // NEW (Explorer integration): locked files are named "<name>.cslock"; refuse up front if that name is taken.
        var finalPath = endsLocked ? LockNames.Locked(path) : LockNames.Unlocked(path);
        if (!SamePath(finalPath, path) && (File.Exists(finalPath) || Directory.Exists(finalPath)))
            throw new IOException($"Can't {(endsLocked ? "lock" : "unlock")} this file: \"{Path.GetFileName(finalPath)}\" already exists in the same folder.");
        // Deny writers throughout preparation; allow replacement of this file's directory entry.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var identity = FileLockSafety.Identity(source);
        FileLockSafety.CheckStreams(path);
        if (source.Length > FileLockFormat.MaxFileSize + (locking ? 0 : FileLockFormat.HeaderSize))
            throw new IOException("File locking supports files up to 64 MB.");
        var timestamp = File.GetLastWriteTimeUtc(path);
        var attributes = File.GetAttributes(path);
        var input = new byte[checked((int)source.Length)];
        byte[]? plaintext = null;
        byte[]? output = null;
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, FileLockSafety.TemporaryPrefix + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            source.ReadExactly(input);
            byte[] header;
            byte[]? finalHeader = null; // NEW (change password): the header the file ends up with
            if (locking)
            {
                if (input.AsSpan().StartsWith(FileLockFormat.Magic)) throw new IOException("This file is already locked. Choose Unlock file.");
                if (masterPassword is not null) store.CheckOrCreatePassword(masterPassword);
                plaintext = input;
                var encrypted = FileLockFormat.Encrypt(plaintext, keys); // CHANGED: shared key cache
                header = encrypted.Header;
                output = new byte[header.Length + encrypted.Ciphertext.Length];
                header.CopyTo(output, 0);
                encrypted.Ciphertext.CopyTo(output, header.Length);
            }
            else
            {
                if (input.Length < FileLockFormat.HeaderSize) throw new InvalidDataException("This is not a complete Clearspace locked file.");
                header = input[..FileLockFormat.HeaderSize];
                FileLockFormat.Validate(header, input.LongLength);
                plaintext = FileLockFormat.Decrypt(header, input[FileLockFormat.HeaderSize..], keys); // CHANGED: shared key cache
                output = plaintext;
                if (rekeyTo is not null)
                {
                    // NEW (change password): fresh data key, nonces and salt under the new password.
                    var encrypted = FileLockFormat.Encrypt(plaintext, rekeyTo);
                    finalHeader = encrypted.Header;
                    output = new byte[finalHeader.Length + encrypted.Ciphertext.Length];
                    finalHeader.CopyTo(output, 0);
                    encrypted.Ciphertext.CopyTo(output, finalHeader.Length);
                }
            }
            finalHeader ??= header;

            token.ThrowIfCancellationRequested();
            // Commit recovery metadata BEFORE replacing the file. The authenticated header also
            // travels with the file, so stale/missing database rows never make it undecryptable.
            store.Record(path, header, locking ? "Preparing" : "Unlocking");
            Checkpoint?.Invoke("Recorded");
            using (var destination = FileLockSafety.CreatePrivateTemporary(temporary))
            {
                destination.Write(output);
                destination.Flush(flushToDisk: true);
            }
            using (var check = File.OpenRead(temporary))
            {
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(check), SHA256.HashData(output)))
                    throw new IOException("The prepared file failed verification. The original file was kept.");
            }
            File.SetLastWriteTimeUtc(temporary, timestamp);
            File.SetAttributes(temporary, attributes);
            Checkpoint?.Invoke("Prepared");
            token.ThrowIfCancellationRequested();
            // Refuse a moved/replaced target; the open source handle also prevents concurrent writes.
            using (var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                if (!identity.SameFile(FileLockSafety.Identity(current)))
                    throw new IOException("The file moved or changed during preparation. Please try again.");
            FileLockSafety.ValidatePath(path);
            FileLockSafety.CheckStreams(path);
            File.Replace(temporary, path, null, ignoreMetadataErrors: false);
            Checkpoint?.Invoke("Replaced");
            // NEW (Explorer integration): rename to/from "<name>.cslock". The contents are already final, so a
            // failed rename only leaves the old name (the file is still recognised by its header).
            var location = path;
            string? warning = null;
            if (!SamePath(finalPath, path))
            {
                source.Dispose(); // our own read handle would block the rename
                try { File.Move(path, finalPath); location = finalPath; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    warning = $"The file was {(endsLocked ? "locked" : "unlocked")}, but it couldn't be renamed to \"{Path.GetFileName(finalPath)}\": {e.Message}";
                }
            }
            // Replacement is the commit point. A bookkeeping failure must not be reported as an
            // encryption failure or prompt the user to repeat a destructive operation.
            try
            {
                if (endsLocked) store.Record(location, finalHeader, "Locked");
                if (!endsLocked || !SamePath(location, path)) store.Forget(path);
                if (!SamePath(location, path)) store.MoveTags(path, location);
                if (locking) store.ForgetOpenFile(path); // FIXED (reset-proof): a visit-unlocked file locked by hand
                return new FileLockResult(endsLocked, warning) { Path = location };
            }
            catch (Exception e) when (e is SqliteException or IOException)
            {
                return new FileLockResult(endsLocked, "The file was " + (endsLocked ? "locked" : "unlocked") +
                    ", but its database status could not be updated. Its contents remain recoverable with the password.") { Path = location };
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            // Delete only the randomly named temporary file created by this operation.
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
