using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class FileLockTests
{
    private readonly TagTestScope _scope = new();
    private static char[] Password => "a memorable test password 42".ToCharArray();
    private string FilePath => Path.Combine(_scope.DirectoryPath, "report's data.txt");
    // CHANGED (Explorer integration): tests don't write desktop.ini icons unless a test asks for them.
    private FileLockService Service => new(_scope.DatabasePath) { ExplorerIcons = false };
    // NEW (Explorer integration): where FilePath lives while locked.
    private string LockedPath => LockNames.Locked(FilePath);
    [TestCleanup] public void Cleanup() => _scope.Dispose();

    [TestMethod]
    [DataRow(0)] [DataRow(12345)]
    public void LockEncryptsBytesAndUnlockRestoresThemAfterRestart(int length)
    {
        var original = RandomNumberGenerator.GetBytes(length);
        File.WriteAllBytes(FilePath, original);
        using (var tags = _scope.Open()) tags.Assign(FilePath, "work");
        var locked = Service.Transform(FilePath, Password, true);
        Assert.IsTrue(locked.Locked);
        // CHANGED (Explorer integration): locking renames the file to "<name>.cslock" and carries its tags along.
        Assert.AreEqual(LockedPath, locked.Path);
        Assert.IsFalse(File.Exists(FilePath));
        Assert.IsTrue(FileLockService.IsLocked(LockedPath));
        Assert.AreEqual(length + FileLockFormat.HeaderSize, new FileInfo(LockedPath).Length);
        using (var store = new FileLockStore(_scope.DatabasePath)) Assert.AreEqual("Locked", store.State(LockedPath));
        using (var tags = _scope.Open()) Assert.IsTrue(tags.HasTag(LockedPath, "work"));
        var unlocked = Service.Transform(LockedPath, Password, false);
        Assert.IsFalse(unlocked.Locked);
        Assert.AreEqual(FilePath, unlocked.Path);
        Assert.IsFalse(File.Exists(LockedPath));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(FilePath));
        Assert.AreEqual(0L, _scope.Number("SELECT COUNT(*) FROM LockedFiles"));
        using (var tags = _scope.Open()) Assert.IsTrue(tags.HasTag(FilePath, "work"));
    }

    [TestMethod]
    public void WrongPasswordAndDamagedContentNeverChangeTheLockedFile()
    {
        File.WriteAllText(FilePath, "private contents");
        Service.Transform(FilePath, Password, true);
        var locked = File.ReadAllBytes(LockedPath);
        Assert.ThrowsException<CryptographicException>(() => Service.Transform(LockedPath, "wrong password".ToCharArray(), false));
        CollectionAssert.AreEqual(locked, File.ReadAllBytes(LockedPath));
        locked[^1] ^= 1;
        File.WriteAllBytes(LockedPath, locked);
        Assert.ThrowsException<CryptographicException>(() => Service.Transform(LockedPath, Password, false));
        CollectionAssert.AreEqual(locked, File.ReadAllBytes(LockedPath));
        Assert.AreEqual(0, Directory.GetFiles(_scope.DirectoryPath, ".clearspace-lock-*.tmp").Length);
    }

    [TestMethod]
    [DataRow(8)] [DataRow(36)] [DataRow(68)] [DataRow(80)] [DataRow(112)] [DataRow(128)] [DataRow(140)]
    public void HeaderTamperingIsAuthenticated(int offset)
    {
        var encrypted = FileLockFormat.Encrypt("contents"u8.ToArray(), Password);
        encrypted.Header[offset] ^= 1;
        Assert.ThrowsException<CryptographicException>(() => FileLockFormat.Decrypt(encrypted.Header, encrypted.Ciphertext, Password));
    }

    [TestMethod]
    public void TruncationLengthAndWorkFactorAreRejectedBeforeDecryption()
    {
        var encrypted = FileLockFormat.Encrypt("contents"u8.ToArray(), Password);
        Assert.ThrowsException<InvalidDataException>(() => FileLockFormat.Decrypt(encrypted.Header, encrypted.Ciphertext[..^1], Password));
        encrypted.Header[24] ^= 1;
        Assert.ThrowsException<InvalidDataException>(() => FileLockFormat.Decrypt(encrypted.Header, encrypted.Ciphertext, Password));
    }

    [TestMethod]
    public void RenamedFileCanBeUnlockedWithAnEntirelyNewDatabase()
    {
        File.WriteAllText(FilePath, "portable recovery");
        Service.Transform(FilePath, Password, true);
        var moved = Path.Combine(_scope.DirectoryPath, "renamed.txt");
        File.Move(LockedPath, moved);
        new FileLockService(Path.Combine(_scope.DirectoryPath, "fresh.db")).Transform(moved, Password, false);
        Assert.AreEqual("portable recovery", File.ReadAllText(moved));
    }

    [TestMethod]
    public void SamePasswordProducesIndependentFileKeysAndNonces()
    {
        var first = FileLockFormat.Encrypt("same"u8.ToArray(), Password);
        var second = FileLockFormat.Encrypt("same"u8.ToArray(), Password);
        Assert.IsFalse(first.Header.AsSpan(36, 32).SequenceEqual(second.Header.AsSpan(36, 32)));
        Assert.IsFalse(first.Header.AsSpan(80, 32).SequenceEqual(second.Header.AsSpan(80, 32)));
        Assert.IsFalse(first.Ciphertext.SequenceEqual(second.Ciphertext));
    }

    [TestMethod]
    public void ExistingMasterPasswordCannotBeSilentlyChanged()
    {
        using var store = new FileLockStore(_scope.DatabasePath);
        store.CheckOrCreatePassword(Password);
        Assert.ThrowsException<CryptographicException>(() => store.CheckOrCreatePassword("a different password".ToCharArray()));
        store.CheckOrCreatePassword(Password);
        Assert.AreEqual(1L, _scope.Number("SELECT COUNT(*) FROM LockSettings"));
        store.Dispose();
        Assert.IsFalse(Encoding.UTF8.GetString(File.ReadAllBytes(_scope.DatabasePath)).Contains(new string(Password)));
    }

    [TestMethod]
    [DataRow("Recorded")] [DataRow("Prepared")]
    public void FailureBeforeReplacementPreservesOriginalAndAllowsRetry(string boundary)
    {
        File.WriteAllText(FilePath, "still readable");
        var service = new FileLockService(_scope.DatabasePath) { Checkpoint = stage => { if (stage == boundary) throw new IOException("Injected interruption"); } };
        Assert.ThrowsException<IOException>(() => service.Transform(FilePath, Password, true));
        Assert.AreEqual("still readable", File.ReadAllText(FilePath));
        Assert.AreEqual(0, Directory.GetFiles(_scope.DirectoryPath, ".clearspace-lock-*.tmp").Length);
        Service.Transform(FilePath, Password, true);
        Service.Transform(LockedPath, Password, false);
        Assert.AreEqual("still readable", File.ReadAllText(FilePath));
    }

    [TestMethod]
    public void FailureAfterReplacementKeepsRecoverableFileDespitePendingDatabaseState()
    {
        File.WriteAllText(FilePath, "recover me");
        var service = new FileLockService(_scope.DatabasePath) { Checkpoint = stage => { if (stage == "Replaced") throw new IOException("Injected process interruption"); } };
        Assert.ThrowsException<IOException>(() => service.Transform(FilePath, Password, true));
        Assert.IsTrue(FileLockService.IsLocked(FilePath));
        using (var store = new FileLockStore(_scope.DatabasePath)) Assert.AreEqual("Preparing", store.State(FilePath));
        Service.Transform(FilePath, Password, false);
        Assert.AreEqual("recover me", File.ReadAllText(FilePath));
    }

    [TestMethod]
    public void DatabaseFailureBeforeReplacementLeavesOriginalUntouched()
    {
        File.WriteAllText(FilePath, "do not lose");
        using (var store = new FileLockStore(_scope.DatabasePath)) store.CheckOrCreatePassword(Password);
        _scope.Execute("CREATE TRIGGER FailLock BEFORE INSERT ON LockedFiles BEGIN SELECT RAISE(ABORT,'injected'); END");
        Assert.ThrowsException<Microsoft.Data.Sqlite.SqliteException>(() => Service.Transform(FilePath, Password, true));
        Assert.AreEqual("do not lose", File.ReadAllText(FilePath));
    }

    [TestMethod]
    public void DatabaseFailureAfterReplacementReportsSuccessWithRecoveryWarning()
    {
        File.WriteAllText(FilePath, "database failure");
        using (var store = new FileLockStore(_scope.DatabasePath)) store.CheckOrCreatePassword(Password);
        // CHANGED (Explorer integration): the final row is written under the new .cslock name (an INSERT).
        _scope.Execute("CREATE TRIGGER FailFinish BEFORE INSERT ON LockedFiles WHEN NEW.State='Locked' BEGIN SELECT RAISE(ABORT,'injected'); END");
        var result = Service.Transform(FilePath, Password, true);
        Assert.IsTrue(result.Locked);
        Assert.IsNotNull(result.Warning);
        Service.Transform(result.Path, Password, false);
        Assert.AreEqual("database failure", File.ReadAllText(FilePath));
    }

    [TestMethod]
    public void CancellationDuringPreparationPreservesOriginal()
    {
        File.WriteAllText(FilePath, "cancel me");
        using var cancellation = new CancellationTokenSource();
        var service = new FileLockService(_scope.DatabasePath) { Checkpoint = stage => { if (stage == "Prepared") cancellation.Cancel(); } };
        Assert.ThrowsException<OperationCanceledException>(() => service.Transform(FilePath, Password, true, cancellation.Token));
        Assert.AreEqual("cancel me", File.ReadAllText(FilePath));
    }

    [TestMethod]
    public void WritableOpenFileAndAdditionalDataStreamsAreRejected()
    {
        File.WriteAllText(FilePath, "busy");
        using (var writer = new FileStream(FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            Assert.ThrowsException<IOException>(() => Service.Transform(FilePath, Password, true));
        File.WriteAllText(FilePath + ":extra", "secret stream");
        Assert.ThrowsException<IOException>(() => Service.Transform(FilePath, Password, true));
        Assert.AreEqual("busy", File.ReadAllText(FilePath));
        Assert.AreEqual("secret stream", File.ReadAllText(FilePath + ":extra"));
    }

    [TestMethod]
    public void HardLinksAndOversizedFilesAreRejected()
    {
        File.WriteAllText(FilePath, "linked");
        var link = Path.Combine(_scope.DirectoryPath, "alias.txt");
        Assert.IsTrue(CreateHardLinkW(link, FilePath, IntPtr.Zero));
        Assert.ThrowsException<IOException>(() => Service.Transform(FilePath, Password, true));
        File.Delete(link);
        using (var file = File.OpenWrite(FilePath)) file.SetLength(FileLockFormat.MaxFileSize + 1L);
        Assert.ThrowsException<IOException>(() => Service.Transform(FilePath, Password, true));
        Assert.AreEqual(FileLockFormat.MaxFileSize + 1L, new FileInfo(FilePath).Length);
    }

    [TestMethod]
    public void TargetReplacementDuringPreparationIsDetected()
    {
        File.WriteAllText(FilePath, "original");
        var moved = Path.Combine(_scope.DirectoryPath, "original-moved.txt");
        var service = new FileLockService(_scope.DatabasePath) { Checkpoint = stage =>
        {
            if (stage != "Prepared") return;
            File.Move(FilePath, moved);
            File.WriteAllText(FilePath, "different file");
        } };
        Assert.ThrowsException<IOException>(() => service.Transform(FilePath, Password, true));
        Assert.AreEqual("original", File.ReadAllText(moved));
        Assert.AreEqual("different file", File.ReadAllText(FilePath));
    }

    [TestMethod]
    public void VersionOneDatabaseUpgradeKeepsTagsAndAddsLockTables()
    {
        using (var tags = _scope.Open()) tags.Assign("existing", "work");
        _scope.Execute("DROP TABLE LockedFiles; DROP TABLE LockSettings; DROP TABLE LockedFolders; DROP TABLE OpenFiles; PRAGMA user_version=1;"); // CHANGED: v3 table too
        using var upgraded = new FileLockStore(_scope.DatabasePath);
        Assert.IsFalse(upgraded.HasPassword);
        Assert.AreEqual(5L, _scope.Number("PRAGMA user_version")); // CHANGED (unlock vs remove lock): schema v5
        Assert.AreEqual(1L, _scope.Number("SELECT COUNT(*) FROM PathTags WHERE Path='existing'"));
    }

    // NEW (no password rules): any non-empty password can be created; empty is still refused.
    [TestMethod]
    public void AnyNonEmptyPasswordCanBeCreated()
    {
        using var store = new FileLockStore(_scope.DatabasePath);
        Assert.ThrowsException<ArgumentException>(() => store.CheckOrCreatePassword(ReadOnlySpan<char>.Empty));
        store.CheckOrCreatePassword("a".ToCharArray());
        Assert.IsTrue(store.Matches("a".ToCharArray()));
        Assert.IsFalse(store.Matches("b".ToCharArray()));
        File.WriteAllText(FilePath, "short password");
        Service.Transform(FilePath, "a".ToCharArray(), true);
        Service.Transform(LockedPath, "a".ToCharArray(), false);
        Assert.AreEqual("short password", File.ReadAllText(FilePath));
    }

    // NEW (folder locking)
    private string Vault => Path.Combine(_scope.DirectoryPath, "vault");

    private Dictionary<string, byte[]> CreateVault()
    {
        var files = new Dictionary<string, byte[]>
        {
            [Path.Combine(Vault, "a.txt")] = "first"u8.ToArray(),
            [Path.Combine(Vault, "empty.bin")] = [],
            [Path.Combine(Vault, "nested", "deeper", "b.dat")] = RandomNumberGenerator.GetBytes(4096),
        };
        foreach (var (path, bytes) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        return files;
    }

    [TestMethod]
    public void FolderLockLocksEveryFileAndUnlockRestoresThem()
    {
        var files = CreateVault();
        var reports = new List<FolderLockProgress>();
        var locked = Service.TransformFolder(Vault, Password, true, new SynchronousProgress(reports.Add));
        Assert.AreEqual(3, locked.Total);
        Assert.AreEqual(3, locked.Changed);
        Assert.AreEqual(0, locked.Skipped.Count);
        Assert.IsFalse(locked.Cancelled);
        Assert.IsTrue(reports.Count >= 3);
        foreach (var path in files.Keys) Assert.IsTrue(FileLockService.IsLocked(LockNames.Locked(path)), path);

        var again = Service.TransformFolder(Vault, Password, true);
        Assert.AreEqual(0, again.Changed);
        Assert.AreEqual(3, again.AlreadyDone);

        Assert.ThrowsException<CryptographicException>(() => Service.TransformFolder(Vault, "wrong".ToCharArray(), false));
        foreach (var path in files.Keys) Assert.IsTrue(FileLockService.IsLocked(LockNames.Locked(path)), path);

        var unlocked = Service.TransformFolder(Vault, Password, false);
        Assert.AreEqual(3, unlocked.Changed);
        foreach (var (path, bytes) in files) CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), path);
        Assert.AreEqual(0L, _scope.Number("SELECT COUNT(*) FROM LockedFiles"));
        Assert.AreEqual(0, Directory.GetFiles(Vault, ".clearspace-lock-*.tmp", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public void FolderLockSkipsUnsupportedFilesAndLocksTheRest()
    {
        var files = CreateVault();
        var withStream = Path.Combine(Vault, "downloaded.txt");
        File.WriteAllText(withStream, "has a stream");
        File.WriteAllText(withStream + ":Zone.Identifier", "[ZoneTransfer]");
        var result = Service.TransformFolder(Vault, Password, true);
        Assert.AreEqual(4, result.Total);
        Assert.AreEqual(3, result.Changed);
        Assert.AreEqual(1, result.Skipped.Count);
        Assert.AreEqual(withStream, result.Skipped[0].Path);
        Assert.AreEqual("has a stream", File.ReadAllText(withStream));
        foreach (var path in files.Keys) Assert.IsTrue(FileLockService.IsLocked(LockNames.Locked(path)), path);
    }

    [TestMethod]
    public void FolderLockStopsBetweenFilesWhenCancelled()
    {
        CreateVault();
        using var cancellation = new CancellationTokenSource();
        var result = Service.TransformFolder(Vault, Password, true,
            new SynchronousProgress(report => { if (report.Index == 1) cancellation.Cancel(); }), cancellation.Token);
        Assert.IsTrue(result.Cancelled);
        Assert.AreEqual(1, result.Changed);
    }

    [TestMethod]
    public void DrivesUserFolderAndSystemFoldersAreProtected()
    {
        Assert.IsTrue(FileLockSafety.IsProtectedFolder(Path.GetPathRoot(Path.GetTempPath())!));
        Assert.IsTrue(FileLockSafety.IsProtectedFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.IsTrue(FileLockSafety.IsProtectedFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.IsTrue(FileLockSafety.IsProtectedFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        Assert.IsTrue(FileLockSafety.IsProtectedFolder(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!));
        Assert.IsFalse(FileLockSafety.IsProtectedFolder(_scope.DirectoryPath));
        Assert.ThrowsException<IOException>(() => Service.TransformFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Password, true));
    }

    // NEW (locked folders)
    private (string State, byte[]? SessionKey, string? Owner)? FolderRecord(string folder) // CHANGED: + Owner
    {
        using var store = new FileLockStore(_scope.DatabasePath);
        return store.Folder(folder);
    }

    [TestMethod]
    public void LockedFolderOpensWithPasswordAndLocksAgainWithoutIt()
    {
        var files = CreateVault();
        Service.LockFolder(Vault, Password);
        Assert.AreEqual("Locked", FolderRecord(Vault)!.Value.State);
        Assert.IsNull(FolderRecord(Vault)!.Value.SessionKey);
        foreach (var path in files.Keys) Assert.IsTrue(FileLockService.IsLocked(LockNames.Locked(path)), path);

        Assert.ThrowsException<CryptographicException>(() => Service.OpenFolder(Vault, "wrong".ToCharArray()));
        Assert.AreEqual("Locked", FolderRecord(Vault)!.Value.State);
        foreach (var path in files.Keys) Assert.IsTrue(FileLockService.IsLocked(LockNames.Locked(path)), path);

        var opened = Service.OpenFolder(Vault, Password);
        Assert.AreEqual(3, opened.Changed);
        Assert.AreEqual("Open", FolderRecord(Vault)!.Value.State);
        Assert.IsNotNull(FolderRecord(Vault)!.Value.SessionKey);
        foreach (var (path, bytes) in files) CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), path);

        // A file added while the folder is open is locked too when you leave.
        var added = Path.Combine(Vault, "nested", "added while open.txt");
        File.WriteAllText(added, "new");
        var relocked = new FileLockService(_scope.DatabasePath) { ExplorerIcons = false }.RelockFolder(Vault); // a fresh service: no password anywhere
        Assert.AreEqual(4, relocked.Changed);
        Assert.AreEqual("Locked", FolderRecord(Vault)!.Value.State);
        Assert.IsNull(FolderRecord(Vault)!.Value.SessionKey);
        foreach (var path in files.Keys.Append(added)) Assert.IsTrue(FileLockService.IsLocked(LockNames.Locked(path)), path);

        // And it opens again with the same password.
        Service.OpenFolder(Vault, Password);
        Assert.AreEqual("new", File.ReadAllText(added));
    }

    [TestMethod]
    public void FilesInUseKeepTheFolderOpenUntilTheyCanBeLocked()
    {
        var files = CreateVault();
        Service.LockFolder(Vault, Password);
        Service.OpenFolder(Vault, Password);
        var busy = files.Keys.First();
        FolderLockResult result;
        using (new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = Service.RelockFolder(Vault);
        Assert.AreEqual(1, result.Skipped.Count);
        Assert.IsTrue(result.Skipped[0].Retryable);
        Assert.AreEqual("Open", FolderRecord(Vault)!.Value.State);
        Assert.IsFalse(FileLockService.IsLocked(busy));

        Service.RelockFolder(Vault);
        Assert.AreEqual("Locked", FolderRecord(Vault)!.Value.State);
        Assert.IsTrue(FileLockService.IsLocked(LockNames.Locked(busy)));
    }

    [TestMethod]
    public void NestedLockedFoldersAreRefusedOrAbsorbed()
    {
        CreateVault();
        var inner = Path.Combine(Vault, "nested");
        Service.LockFolder(inner, Password);
        Service.LockFolder(Vault, Password); // the outer lock takes over the inner one
        Assert.IsNull(FolderRecord(inner));
        Assert.AreEqual(Vault, Service.EnclosingLockedFolder(Path.Combine(inner, "deeper")));
        Assert.ThrowsException<IOException>(() => Service.LockFolder(inner, Password));
    }

    [TestMethod]
    public void UnlockFolderRestoresFilesAndStopsAsking()
    {
        var files = CreateVault();
        Service.LockFolder(Vault, Password);
        Service.RemoveFolderLock(Vault, Password);
        Assert.IsNull(FolderRecord(Vault));
        foreach (var (path, bytes) in files) CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), path);
    }

    [TestMethod]
    public void RegistryReportsLockBadgesAndTheGoverningFolder()
    {
        var files = CreateVault();
        Service.LockFolder(Vault, Password);
        File.WriteAllText(FilePath, "single");
        var single = Service.Transform(FilePath, Password, true).Path;
        FileLockRegistry.DatabasePath = _scope.DatabasePath;
        FileLockRegistry.LegacyPath = null;
        FileLockRegistry.Reload();
        Assert.AreEqual(LockState.Locked, FileLockRegistry.StateOf(Vault, isFolder: true));
        Assert.AreEqual(LockState.Locked, FileLockRegistry.StateOf(single, isFolder: false));
        Assert.AreEqual(LockState.Locked, FileLockRegistry.StateOf(Path.Combine(_scope.DirectoryPath, "elsewhere.png.cslock"), isFolder: false));
        Assert.AreEqual(LockState.None, FileLockRegistry.StateOf(_scope.DirectoryPath, isFolder: true));
        Assert.AreEqual(Vault, FileLockRegistry.Governing(Path.Combine(Vault, "nested", "deeper"))!.Value.Folder);
        Assert.IsNull(FileLockRegistry.Governing(Vault + "-other"));
        Service.OpenFolder(Vault, Password);
        FileLockRegistry.Reload();
        Assert.AreEqual(LockState.Open, FileLockRegistry.StateOf(Vault, isFolder: true));
        CollectionAssert.Contains(FileLockRegistry.OpenFolders.ToList(), Vault);
    }

    // NEW (Explorer integration)
    [TestMethod]
    public void LockingRefusesToOverwriteAnExistingCslockName()
    {
        File.WriteAllText(FilePath, "mine");
        File.WriteAllText(LockedPath, "someone else's");
        Assert.ThrowsException<IOException>(() => Service.Transform(FilePath, Password, true));
        Assert.AreEqual("mine", File.ReadAllText(FilePath));
        Assert.AreEqual("someone else's", File.ReadAllText(LockedPath));
    }

    [TestMethod]
    public void FolderRunGivesOlderLockedFilesTheCslockName()
    {
        var files = CreateVault();
        var first = files.Keys.First();
        Service.Transform(first, Password, true);
        File.Move(LockNames.Locked(first), first); // as if locked before .cslock names existed
        var result = Service.TransformFolder(Vault, Password, true);
        Assert.AreEqual(1, result.AlreadyDone);
        foreach (var path in files.Keys) Assert.IsTrue(FileLockService.IsLocked(LockNames.Locked(path)), path);
        using var store = new FileLockStore(_scope.DatabasePath);
        Assert.AreEqual("Locked", store.State(LockNames.Locked(first)));
    }

    [TestMethod]
    public void LockedFolderGetsExplorerIconAndUnlockRestoresTheFolder()
    {
        CreateVault();
        var service = new FileLockService(_scope.DatabasePath); // ExplorerIcons on
        var before = File.GetAttributes(Vault);
        service.LockFolder(Vault, Password);
        var ini = Path.Combine(Vault, "desktop.ini");
        Assert.IsTrue(File.Exists(ini));
        Assert.IsTrue(File.ReadAllText(ini).Contains("LockedFolder.ico"));
        Assert.IsTrue(File.Exists(ShellIntegration.LockedFolderIcon));
        Assert.IsTrue((File.GetAttributes(Vault) & FileAttributes.ReadOnly) != 0);
        Assert.AreEqual(3, service.TransformFolder(Vault, Password, true).AlreadyDone); // desktop.ini is never encrypted
        service.RemoveFolderLock(Vault, Password);
        Assert.IsFalse(File.Exists(ini));
        Assert.AreEqual(before, File.GetAttributes(Vault));
    }

    [TestMethod]
    public void ExistingDesktopIniIsKeptAndRestored()
    {
        CreateVault();
        var ini = Path.Combine(Vault, "desktop.ini");
        File.WriteAllText(ini, "[.ShellClassInfo]\r\nIconResource=C:\\icons\\mine.ico,0\r\nInfoTip=keep me\r\n", Encoding.Unicode);
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        var service = new FileLockService(_scope.DatabasePath);
        service.LockFolder(Vault, Password);
        StringAssert.Contains(File.ReadAllText(ini), "LockedFolder.ico");
        service.RemoveFolderLock(Vault, Password);
        var text = File.ReadAllText(ini);
        StringAssert.Contains(text, @"C:\icons\mine.ico,0");
        StringAssert.Contains(text, "keep me");
        Assert.IsFalse(text.Contains("Clearspace"));
    }

    [TestMethod]
    public void ShellCommandsParseOnlyWellFormedArguments()
    {
        Assert.AreEqual(new ShellCommand(ShellVerb.Unlock, @"C:\a\b.txt.cslock"), ShellCommands.Parse(["--unlock", "\"C:\\a\\b.txt.cslock\""]));
        Assert.AreEqual(ShellVerb.Lock, ShellCommands.Parse(["--LOCK", @"D:\folder"])!.Verb);
        Assert.IsNull(ShellCommands.Parse(["--open"]));
        Assert.IsNull(ShellCommands.Parse(["--delete", @"C:\x"]));
        Assert.IsNull(ShellCommands.Parse(["--open", @"relative\path"]));
        Assert.AreEqual(@"C:\a.txt", LockNames.Unlocked(@"C:\a.txt.CSLOCK"));
        Assert.AreEqual(@"C:\a.txt.cslock", LockNames.Locked(@"C:\a.txt.cslock"));
    }

    // NEW (unlock vs remove lock)
    [TestMethod]
    public void UnlockingAFileForAVisitLocksItAgainWithoutThePassword()
    {
        File.WriteAllText(FilePath, "visit");
        Service.Transform(FilePath, Password, true);
        Assert.ThrowsException<CryptographicException>(() => Service.OpenFile(LockedPath, "wrong".ToCharArray()));
        Assert.AreEqual(0L, _scope.Number("SELECT COUNT(*) FROM OpenFiles"));

        var opened = Service.OpenFile(LockedPath, Password);
        Assert.AreEqual(FilePath, opened.Path);
        Assert.AreEqual("visit", File.ReadAllText(FilePath));
        using (var store = new FileLockStore(_scope.DatabasePath))
            Assert.AreEqual(_scope.DirectoryPath, store.OpenFile(FilePath)!.Value.Directory);

        var relocked = new FileLockService(_scope.DatabasePath).RelockFile(FilePath);
        Assert.IsTrue(relocked.Done);
        Assert.IsTrue(FileLockService.IsLocked(LockedPath));
        Assert.AreEqual(0L, _scope.Number("SELECT COUNT(*) FROM OpenFiles"));
        Service.Transform(LockedPath, Password, false);
        Assert.AreEqual("visit", File.ReadAllText(FilePath));
    }

    [TestMethod]
    public void AVisitUnlockedFileInUseStaysUnlockedUntilItCanBeLocked()
    {
        File.WriteAllText(FilePath, "busy");
        Service.Transform(FilePath, Password, true);
        Service.OpenFile(LockedPath, Password);
        using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.IsTrue(Service.RelockFile(FilePath).InUse);
        Assert.AreEqual(1L, _scope.Number("SELECT COUNT(*) FROM OpenFiles"));
        Assert.IsTrue(Service.RelockFile(FilePath).Done);
        Assert.IsTrue(FileLockService.IsLocked(LockedPath));
    }

    [TestMethod]
    public void RemovingTheLockFromAFileInALockedFolderDissolvesTheFolderButKeepsTheOthersLocked()
    {
        var files = CreateVault();
        Service.LockFolder(Vault, Password);
        var target = files.Keys.First();
        var result = Service.RemoveLocks([LockNames.Locked(target)], Password);
        Assert.AreEqual(1, result.Removed);
        CollectionAssert.AreEqual(new[] { Vault }, result.DissolvedParents.ToArray());
        Assert.IsNull(FolderRecord(Vault));
        CollectionAssert.AreEqual(files[target], File.ReadAllBytes(target));
        foreach (var path in files.Keys.Skip(1)) Assert.IsTrue(FileLockService.IsLocked(LockNames.Locked(path)), path);
    }

    [TestMethod]
    public void RemovingALockInsideAnOpenFolderLocksItsOtherFilesFirst()
    {
        var files = CreateVault();
        Service.LockFolder(Vault, Password);
        Service.OpenFolder(Vault, Password);
        var target = files.Keys.First();
        Service.RemoveLocks([target], Password);
        Assert.IsNull(FolderRecord(Vault));
        CollectionAssert.AreEqual(files[target], File.ReadAllBytes(target));
        foreach (var path in files.Keys.Skip(1)) Assert.IsTrue(FileLockService.IsLocked(LockNames.Locked(path)), path);
    }

    [TestMethod]
    public void RemoveLockWorksOnSeveralFilesAndFoldersAtOnce()
    {
        var files = CreateVault();
        Service.LockFolder(Vault, Password);
        File.WriteAllText(FilePath, "loose file");
        Service.Transform(FilePath, Password, true);
        Assert.ThrowsException<CryptographicException>(() => Service.RemoveLocks([Vault], "wrong".ToCharArray()));
        var result = Service.RemoveLocks([Vault, LockedPath], Password);
        Assert.AreEqual(4, result.Removed);
        Assert.AreEqual(0, result.DissolvedParents.Count);
        Assert.IsNull(FolderRecord(Vault));
        Assert.AreEqual("loose file", File.ReadAllText(FilePath));
        foreach (var (path, bytes) in files) CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), path);
    }

    [TestMethod]
    public void RemoveAllLocksUnlocksEverythingAndResetsThePassword()
    {
        var files = CreateVault();
        Service.LockFolder(Vault, Password);
        File.WriteAllText(FilePath, "everything");
        Service.Transform(FilePath, Password, true);
        Assert.ThrowsException<CryptographicException>(() => Service.RemoveAllLocks("wrong".ToCharArray()));

        var result = Service.RemoveAllLocks(Password);
        Assert.IsTrue(result.PasswordReset);
        Assert.IsFalse(Service.HasPassword);
        Assert.AreEqual("everything", File.ReadAllText(FilePath));
        foreach (var (path, bytes) in files) CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), path);
        Assert.AreEqual(0L, _scope.Number("SELECT COUNT(*) FROM LockedFolders"));

        // The next lock creates a brand-new password.
        Service.Transform(FilePath, "new one".ToCharArray(), true);
        Service.Transform(LockedPath, "new one".ToCharArray(), false);
    }

    [TestMethod]
    public void RemoveAllLocksKeepsThePasswordWhenSomethingIsStillLocked()
    {
        File.WriteAllText(FilePath, "in use");
        Service.Transform(FilePath, Password, true);
        LockRemovalResult result;
        using (new FileStream(LockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = Service.RemoveAllLocks(Password);
        Assert.IsFalse(result.PasswordReset);
        Assert.IsTrue(Service.HasPassword);
        Assert.AreEqual(1, result.Skipped.Count);
    }

    [TestMethod]
    public void ShellCommandsAcceptRemoveLock() =>
        Assert.AreEqual(ShellVerb.RemoveLock, ShellCommands.Parse(["--remove-lock", @"C:\a.txt.cslock"])!.Verb);

    // NEW (reset-proof): after a reset the master password can differ from older files' passwords.
    private void ResetAndCreateNewPassword(string? newPassword = "brand new password")
    {
        using var store = new FileLockStore(_scope.DatabasePath);
        store.ResetPassword();
        if (newPassword is not null) store.CheckOrCreatePassword(newPassword.ToCharArray());
    }

    [TestMethod]
    public void FilesLockedWithAnOlderPasswordStillUnlockAfterAReset()
    {
        File.WriteAllText(FilePath, "survives a reset");
        Service.Transform(FilePath, Password, true);
        ResetAndCreateNewPassword();
        Assert.ThrowsException<CryptographicException>(() => Service.OpenFile(LockedPath, "wrong".ToCharArray()));
        var opened = Service.OpenFile(LockedPath, Password);
        StringAssert.Contains(opened.Warning, "older password");
        Assert.AreEqual("survives a reset", File.ReadAllText(FilePath));
        Service.RelockFile(FilePath); // locks again with the password it was opened with
        Service.RemoveLocks([LockedPath], Password);
        Assert.AreEqual("survives a reset", File.ReadAllText(FilePath));
    }

    [TestMethod]
    public void LockedFoldersFromBeforeAResetOpenWithTheirOwnPassword()
    {
        var files = CreateVault();
        Service.LockFolder(Vault, Password);
        ResetAndCreateNewPassword();
        Assert.ThrowsException<CryptographicException>(() => Service.OpenFolder(Vault, "wrong".ToCharArray()));
        Assert.AreEqual(3, Service.OpenFolder(Vault, Password).Changed);
        foreach (var (path, bytes) in files) CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), path);
    }

    [TestMethod]
    public void AFreshDatabaseStillOpensEverything()
    {
        var files = CreateVault();
        Service.LockFolder(Vault, Password);
        var fresh = new FileLockService(Path.Combine(_scope.DirectoryPath, "reinstalled.db")) { ExplorerIcons = false };
        var result = fresh.RemoveLocks([Vault], Password);
        Assert.AreEqual(3, result.Removed);
        foreach (var (path, bytes) in files) CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), path);
    }

    [TestMethod]
    public void OpeningAFolderWithNothingLockedStillNeedsTheRightPassword()
    {
        Directory.CreateDirectory(Vault);
        Service.LockFolder(Vault, Password); // empty folder
        Assert.ThrowsException<CryptographicException>(() => Service.OpenFolder(Vault, "typo".ToCharArray()));
    }

    [TestMethod]
    public void ChangePasswordMovesFilesToTheCurrentPassword()
    {
        var files = CreateVault();
        Service.LockFolder(Vault, Password);
        File.WriteAllText(FilePath, "single");
        Service.Transform(FilePath, Password, true);
        ResetAndCreateNewPassword(null); // like a new PC: no Clearspace password yet

        var newPassword = "my new password".ToCharArray();
        Assert.ThrowsException<CryptographicException>(() => Service.ChangePassword([Vault], "wrong".ToCharArray(), newPassword));
        var result = Service.ChangePassword([Vault, LockedPath], Password, newPassword);
        Assert.AreEqual(4, result.Removed);
        Assert.IsTrue(Service.HasPassword); // the new password became the Clearspace password
        foreach (var path in files.Keys) Assert.IsTrue(FileLockService.IsLocked(LockNames.Locked(path)), path);
        Assert.AreEqual(4, Service.ChangePassword([Vault, LockedPath], newPassword, newPassword).AlreadyUnlocked);

        Assert.ThrowsException<CryptographicException>(() => Service.OpenFolder(Vault, Password));
        Service.OpenFolder(Vault, newPassword);
        foreach (var (path, bytes) in files) CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), path);
        Service.Transform(LockedPath, newPassword, false);
        Assert.AreEqual("single", File.ReadAllText(FilePath));
    }

    [TestMethod]
    public void AnUnreadableSavedKeyAsksForThePasswordInsteadOfFailing()
    {
        File.WriteAllText(FilePath, "after reset");
        Service.Transform(FilePath, Password, true);
        Service.OpenFile(LockedPath, Password);
        _scope.Execute("UPDATE OpenFiles SET SessionKey = x'00'");
        var file = Service.RelockFile(FilePath);
        Assert.IsTrue(file.NeedsPassword);
        Assert.AreEqual("after reset", File.ReadAllText(FilePath));
        Service.Transform(FilePath, Password, true); // locking it by hand clears the visit
        Assert.AreEqual(0L, _scope.Number("SELECT COUNT(*) FROM OpenFiles"));

        CreateVault();
        Service.LockFolder(Vault, Password);
        Service.OpenFolder(Vault, Password);
        _scope.Execute("UPDATE LockedFolders SET SessionKey = x'00'");
        Assert.IsTrue(Service.RelockFolder(Vault).NeedsPassword);
        Service.LockFolder(Vault, Password);
        Assert.AreEqual("Locked", FolderRecord(Vault)!.Value.State);
    }

    // NEW (switch to current password)
    [TestMethod]
    public void UnlockingWithAnOlderPasswordCanSwitchTheFileToTheCurrentOne()
    {
        File.WriteAllText(FilePath, "switch me");
        Service.Transform(FilePath, Password, true);
        ResetAndCreateNewPassword("current one");
        Assert.IsFalse(Service.IsCurrentPassword([LockedPath], Password));
        Assert.ThrowsException<CryptographicException>(() => Service.IsCurrentPassword([LockedPath], "wrong".ToCharArray()));
        Assert.ThrowsException<CryptographicException>(() => Service.OpenFile(LockedPath, Password, switchTo: "not current".ToCharArray()));

        var opened = Service.OpenFile(LockedPath, Password, switchTo: "current one".ToCharArray());
        StringAssert.Contains(opened.Warning, "current Clearspace password");
        Service.RelockFile(FilePath);
        Assert.IsTrue(Service.IsCurrentPassword([LockedPath], "current one".ToCharArray()));
        Assert.ThrowsException<CryptographicException>(() => Service.OpenFile(LockedPath, Password));
    }

    [TestMethod]
    public void OpeningAFolderWithAnOlderPasswordCanSwitchItAndKeepsFilesAlreadyOnTheCurrentOne()
    {
        var files = CreateVault();
        Service.LockFolder(Vault, Password);
        ResetAndCreateNewPassword("current one");
        // A file added later is already on the current password: the folder now holds both.
        var newer = Path.Combine(Vault, "newer.txt");
        File.WriteAllText(newer, "newer");
        Service.Transform(newer, "current one".ToCharArray(), true);

        var opened = Service.OpenFolder(Vault, Password, switchTo: "current one".ToCharArray());
        Assert.AreEqual(4, opened.Changed);
        Assert.AreEqual("newer", File.ReadAllText(newer));
        foreach (var (path, bytes) in files) CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), path);

        Service.RelockFolder(Vault);
        Assert.IsTrue(Service.IsCurrentPassword([Vault], "current one".ToCharArray()));
        Assert.ThrowsException<CryptographicException>(() => Service.OpenFolder(Vault, Password));
    }

    [TestMethod]
    public void OnANewPcTheOlderPasswordCanBecomeTheClearspacePassword()
    {
        File.WriteAllText(FilePath, "new pc");
        Service.Transform(FilePath, Password, true);
        ResetAndCreateNewPassword(null);
        Assert.IsFalse(Service.IsCurrentPassword([LockedPath], Password));
        Service.OpenFile(LockedPath, Password, switchTo: Password);
        Assert.IsTrue(Service.HasPassword);
        Assert.IsTrue(Service.IsCurrentPassword([FilePath], Password));
        using var store = new FileLockStore(_scope.DatabasePath);
        Assert.IsTrue(store.Matches(Password));
    }

    // Progress<T> posts to a SynchronizationContext; tests need the callback to run inline.
    private sealed class SynchronousProgress(Action<FolderLockProgress> report) : IProgress<FolderLockProgress>
    {
        public void Report(FolderLockProgress value) => report(value);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string link, string existing, IntPtr attributes);
}
