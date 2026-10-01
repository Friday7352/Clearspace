// Clearspace | NEW (Explorer re-lock): the part of Clearspace that looks after locking, with or without
// the main window.
//  - Explorer's "Lock / Unlock / Remove lock with Clearspace" and double-clicking a .cslock file show only
//    the password dialog — no main window. Only "Open in Clearspace" opens the main window.
//  - Things unlocked for a visit (a file, or a locked folder) are locked again once no File Explorer
//    window/tab and no Clearspace window has been showing their folder for a few seconds. Until then
//    Clearspace keeps running in the background (no window), and it exits once nothing is left unlocked.
//  - Files another program still has open are retried every 30 seconds. Anything left unlocked by a crash
//    or shutdown is locked again the next time Clearspace starts.
//  - Every Clearspace process listens for commands from Explorer (named pipe), so a click in Explorer is
//    handled by the Clearspace that's already running.
// (Moved here from MainWindow.FileLocks.cs, which now only wires the main window to this.)
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Clearspace.Services;

namespace Clearspace;

internal sealed class LockAgent
{
    internal static LockAgent Current { get; } = new();

    // After something stops being shown, how long before it's locked again. Something unlocked but never yet
    // shown (e.g. "Unlock folder" from Explorer before you go into it) gets longer.
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UnseenGrace = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InUseRetry = TimeSpan.FromSeconds(30);

    private readonly HashSet<string> _openedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _hiddenSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _relocks = new(StringComparer.OrdinalIgnoreCase);
    // FIXED (reset-proof): unlocked items whose saved key can't be read any more (Windows reset / account
    // change); they wait for the password instead of being retried.
    private readonly HashSet<string> _needsPassword = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource _listener = new();
    private int _holds; // commands and dialogs in progress keep a window-less Clearspace running
    private bool _started;

    internal MainWindow? Window { get; private set; }

    internal static FileLockService Service => new(TagService.DatabasePath, TagService.TagFilePath);

    internal void Start()
    {
        if (_started) return;
        _started = true;
        _timer.Tick += (_, _) => Check();
        _timer.Start();

        // Keep Explorer's .cslock association and menu entries pointing at this Clearspace, and give every
        // locked folder its Explorer icon.
        var exe = Environment.ProcessPath;
        if (exe is not null && Path.GetFileName(exe).Equals("Clearspace.exe", StringComparison.OrdinalIgnoreCase))
            _ = Task.Run(() =>
            {
                try { ShellIntegration.Register(exe); } catch (Exception) { }
                try { Service.EnsureFolderIcons(); } catch (Exception) { }
            });

        var dispatcher = Application.Current.Dispatcher;
        _ = ShellCommands.ListenAsync(command => dispatcher.InvokeAsync(() => Run(command)), _listener.Token);
    }

    internal void Attach(MainWindow window)
    {
        Window = window;
        Check();
    }

    internal void Detach(MainWindow window)
    {
        if (ReferenceEquals(Window, window)) Window = null;
        Check();
    }

    // A command from Explorer (or the one this process was started with).
    internal void RunLater(ShellCommand command)
    {
        _holds++;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try { Run(command); }
            finally { _holds--; Check(); }
        });
    }

    internal void Run(ShellCommand command)
    {
        _holds++;
        try
        {
            var path = command.Path;
            // A .cslock file that's unlocked right now is back under its original name.
            if (!File.Exists(path) && !Directory.Exists(path) && File.Exists(LockNames.Unlocked(path)))
                path = LockNames.Unlocked(path);
            var isFolder = Directory.Exists(path);
            if (!isFolder && !File.Exists(path))
            {
                MessageBox.Show($"{Path.GetFileName(path)} no longer exists.", "Clearspace", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            switch (command.Verb)
            {
                case ShellVerb.Open when isFolder:
                    ShowMainWindow(path); // "Open in Clearspace": the only command that shows the main window
                    break;
                case ShellVerb.Open:
                    if (UnlockFile(path, attach: false) is { } opened && !FileLockService.TryIsLocked(opened)) OpenWithShell(opened);
                    break;
                case ShellVerb.Unlock when isFolder:
                    OpenFolderPrompt(Path.TrimEndingDirectorySeparator(path), attach: false);
                    break;
                case ShellVerb.Unlock:
                    UnlockFile(path, attach: false);
                    break;
                case ShellVerb.Lock:
                    LockItem(path, isFolder, attach: false);
                    break;
                case ShellVerb.RemoveLock:
                    RemoveLocks([path], attach: false);
                    break;
                case ShellVerb.ChangePassword:
                    ChangePassword([path], attach: false);
                    break;
            }
        }
        finally
        {
            _holds--;
            AfterChange();
        }
    }

    private void ShowMainWindow(string folder)
    {
        if (Window is { } window)
        {
            window.BringToFront();
            window.NavigateTo(folder);
            return;
        }
        App.StartupPath = folder;
        new MainWindow().Show();
    }

    // ---- Dialogs ----

    // attach = belongs to the main window (opened from its menus); otherwise it stands alone on screen.
    private bool? Show(FileLockWindow dialog, bool attach)
    {
        _holds++;
        try
        {
            if (attach && Window is { IsVisible: true } owner) dialog.Owner = owner;
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.ShowInTaskbar = true;
                dialog.Topmost = true;
                dialog.Loaded += (_, _) => { dialog.Activate(); dialog.Topmost = false; };
            }
            return dialog.ShowDialog();
        }
        finally { _holds--; }
    }

    private void Report(string message) => Window?.ReportLockStatus(message);

    private void AfterChange(string? message = null)
    {
        FileLockRegistry.Reload();
        if (message is not null) Report(message);
        Window?.RefreshAfterLockChange(message);
        Check();
    }

    private void OpenWithShell(string path)
    {
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception exception) { Report("Could not open " + Path.GetFileName(path) + ": " + exception.Message); }
    }

    // ---- Lock ----

    internal void LockItem(string path, bool isFolder, bool attach)
    {
        if (!isFolder)
        {
            if (FileLockRegistry.Governing(path) is { } outerFolder)
            {
                Tell($"{Path.GetFileName(path)} is inside the locked folder {Path.GetFileName(outerFolder.Folder)}, which already protects it.", attach);
                return;
            }
            if (FileLockRegistry.StateOf(path, isFolder: false) == LockState.Open)
            {
                _ = StartRelockFile(path); // unlocked for a visit: "Lock" just locks it again now
                return;
            }
            var dialog = new FileLockWindow(path, LockDialogMode.LockFile, Service);
            Show(dialog, attach);
            if (dialog.ResultMessage is { } message) AfterChange(message);
            return;
        }

        var folder = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(folder);
        var governing = FileLockRegistry.Governing(folder);
        var isRecord = governing is { } g && FileLockService.SamePath(g.Folder, folder);
        if (governing is { } outer && !isRecord)
        {
            Tell($"{name} is inside the locked folder {Path.GetFileName(outer.Folder)}, which already protects it.", attach);
            return;
        }
        if (isRecord)
        {
            if (governing!.Value.State == LockState.Open)
            {
                // "Lock" on a folder that's open right now: leave it if the main window is inside, lock it now.
                if (Window?.CurrentLocation is { } current && (FileLockService.SamePath(current, folder) || FileLockService.IsInside(current, folder)))
                    Window.NavigateTo(Path.GetDirectoryName(folder) ?? ExplorerLocations.MyPcPath);
                _ = StartRelock(folder);
                Report($"Locking {name}…");
            }
            else Tell($"{name} is already locked.", attach);
            return;
        }
        var folderDialog = new FileLockWindow(folder, LockDialogMode.LockFolder, Service);
        Show(folderDialog, attach);
        if (folderDialog.ResultMessage is { } done) AfterChange(done);
    }

    private void Tell(string message, bool attach)
    {
        if (attach && Window is not null) Report(message);
        else MessageBox.Show(message, "Clearspace", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- Unlock (for a visit) ----

    internal bool IsOpenHere(string folder) =>
        _openedFolders.Contains(folder) && !_relocks.ContainsKey(folder) &&
        FileLockRegistry.StateOf(folder, isFolder: true) == LockState.Open &&
        FileLockRegistry.OwnerOf(folder) == FileLockService.CurrentOwner;

    // The password prompt for a locked folder (entering it in Clearspace, or "Unlock folder" in Explorer).
    internal bool OpenFolderPrompt(string folder, bool attach)
    {
        if (IsOpenHere(folder)) return true;
        // Still open from a Clearspace that crashed, or being locked again right now: finish that first.
        // (Open in another running Clearspace: opening it here takes it over.)
        var state = FileLockRegistry.StateOf(folder, isFolder: true);
        Task? pending = _relocks.TryGetValue(folder, out var running) ? running
            : state == LockState.Open && !FileLockService.IsOwnerAlive(FileLockRegistry.OwnerOf(folder)) ? StartRelock(folder) : null;

        var dialog = new FileLockWindow(folder, LockDialogMode.OpenFolder, Service, pending is null ? null : () => pending);
        var opened = Show(dialog, attach) == true;
        if (opened)
        {
            _openedFolders.Add(folder);
            _seen.Remove(folder);
            _hiddenSince.Remove(folder);
        }
        AfterChange(dialog.ResultMessage);
        return opened;
    }

    // Unlocks a file until no window shows its folder. Returns the unlocked path, or null if it stayed locked.
    internal string? UnlockFile(string path, bool attach)
    {
        var folder = Path.GetDirectoryName(path);
        if (folder is null) return null;

        // Inside a locked folder: unlocking the folder unlocks it (one password for everything in it).
        if (FileLockRegistry.Governing(path) is { } governing)
        {
            var open = IsOpenHere(governing.Folder);
            if (!open)
            {
                if (attach && Window is { } window) { window.NavigateTo(folder); open = IsOpenHere(governing.Folder); }
                else open = OpenFolderPrompt(governing.Folder, attach);
            }
            if (!open) return null;
            var unlockedInFolder = LockNames.Unlocked(path);
            return File.Exists(unlockedInFolder) ? unlockedInFolder : File.Exists(path) ? path : null;
        }

        if (!LockNames.HasLockedName(path) && !FileLockService.TryIsLocked(path)) return path; // not locked

        var dialog = new FileLockWindow(path, LockDialogMode.OpenFile, Service);
        Show(dialog, attach);
        if (dialog.ResultMessage is null) return null;
        if (dialog.ResultPath is { } unlocked)
        {
            _seen.Remove(unlocked);
            _hiddenSince.Remove(unlocked);
        }
        AfterChange(dialog.ResultMessage);
        return dialog.ResultPath;
    }

    // In-app open (double-click / Open on a locked file): unlock for the visit, then open it.
    // Returns true when this handled the open.
    internal async Task<bool> OpenLockedFile(string path)
    {
        var governed = FileLockRegistry.Governing(path) is { } governing && !IsOpenHere(governing.Folder);
        if (!governed && !LockNames.HasLockedName(path) && !await Task.Run(() => FileLockService.TryIsLocked(path)))
            return false; // not locked: open it the normal way
        if (UnlockFile(path, attach: true) is { } unlocked && File.Exists(unlocked) && !FileLockService.TryIsLocked(unlocked))
            OpenWithShell(unlocked);
        return true;
    }

    // ---- Remove lock (for good) ----

    internal void RemoveLocks(IReadOnlyList<string> paths, bool attach)
    {
        var parents = paths
            .Select(path => FileLockRegistry.Governing(path))
            .Where(governing => governing is { } g && !paths.Any(path => FileLockService.SamePath(path, g.Folder)))
            .Select(governing => governing!.Value.Folder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        string? warning = null;
        if (parents.Count > 0)
        {
            var names = string.Join(", ", parents.Select(parent => Path.GetFileName(parent)));
            warning = (parents.Count == 1 ? $"This also removes the lock from the folder {names}" : $"This also removes the lock from the folders {names}")
                      + ": it will stop asking for a password. The other files inside stay locked, and each one needs Unlock or Remove lock on its own.";
        }
        var dialog = new FileLockWindow(paths, LockDialogMode.RemoveLocks, Service, warning: warning);
        Show(dialog, attach);
        if (dialog.ResultMessage is null) return;
        foreach (var path in paths) _openedFolders.Remove(Path.TrimEndingDirectorySeparator(path));
        foreach (var parent in dialog.RemovalResult?.DissolvedParents ?? []) _openedFolders.Remove(parent);
        AfterChange(dialog.ResultMessage);
    }

    // NEW (change password): move locked files from the password they use to your current one.
    internal void ChangePassword(IReadOnlyList<string> paths, bool attach)
    {
        var dialog = new FileLockWindow(paths, LockDialogMode.ChangePassword, Service);
        Show(dialog, attach);
        if (dialog.ResultMessage is not null) AfterChange(dialog.ResultMessage);
    }

    internal void RemoveAll()
    {
        var dialog = new FileLockWindow(string.Empty, LockDialogMode.RemoveAll, Service);
        Show(dialog, attach: true);
        if (dialog.ResultMessage is null) return;
        _openedFolders.Clear();
        AfterChange(dialog.ResultMessage);
    }

    // ---- Locking again ----

    // Runs every 2 seconds and after every navigation: locks whatever this Clearspace unlocked (or a crashed
    // one left unlocked) once no Explorer or Clearspace window has shown its folder for the grace period.
    // Without a main window and with nothing left to look after, Clearspace exits.
    internal void Check()
    {
        if (!_started) return;
        FileLockRegistry.Reload();
        var now = DateTime.UtcNow;
        List<string>? shownCache = null;
        List<string> Shown() => shownCache ??= ShownLocations(); // only ask Explorer when something is unlocked

        foreach (var folder in FileLockRegistry.OpenFolders)
            Consider(folder, folder, FileLockRegistry.OwnerOf(folder), isFolder: true);
        foreach (var (file, directory, owner) in FileLockRegistry.OpenFiles)
            Consider(file, directory, owner, isFolder: false);

        if (Window is null && _holds == 0 && _relocks.Count == 0 && !AnythingToLookAfter())
        {
            _timer.Stop();
            _listener.Cancel();
            Application.Current.Shutdown();
        }

        void Consider(string item, string scope, string? owner, bool isFolder)
        {
            if (_relocks.ContainsKey(item) || _needsPassword.Contains(item) || !IsMine(owner)) return;
            if (_retryAfter.TryGetValue(item, out var retry) && now < retry) return;
            var visible = Shown().Any(location => FileLockService.SamePath(location, scope) || FileLockService.IsInside(location, scope));
            if (visible)
            {
                _seen.Add(item);
                _hiddenSince.Remove(item);
                return;
            }
            if (!_hiddenSince.TryGetValue(item, out var since)) _hiddenSince[item] = since = now;
            var grace = _seen.Contains(item) ? Grace : UnseenGrace;
            if (now - since >= grace)
            {
                if (isFolder) _ = StartRelock(item);
                else _ = StartRelockFile(item);
            }
        }
    }

    private List<string> ShownLocations()
    {
        var shown = new List<string>(ExplorerWindows.Folders());
        if (Window?.CurrentLocation is { } current && !current.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase))
        {
            try { shown.Add(Path.GetFullPath(current)); } catch (Exception) { }
        }
        return shown;
    }

    // Ours, or left by a Clearspace that isn't running any more.
    private static bool IsMine(string? owner) =>
        owner == FileLockService.CurrentOwner || !FileLockService.IsOwnerAlive(owner);

    private bool AnythingToLookAfter() =>
        FileLockRegistry.OpenFolders.Any(folder => IsMine(FileLockRegistry.OwnerOf(folder))) ||
        FileLockRegistry.OpenFiles.Any(open => IsMine(open.Owner));

    internal Task StartRelock(string folder)
    {
        if (_relocks.TryGetValue(folder, out var running)) return running;
        var task = RelockFolderAsync(folder);
        if (!task.IsCompleted) _relocks[folder] = task;
        return task;
    }

    private async Task RelockFolderAsync(string folder)
    {
        _openedFolders.Remove(folder);
        var name = Path.GetFileName(folder);
        try
        {
            var result = await Task.Run(() => Service.RelockFolder(folder));
            if (result.NeedsPassword) { AskToRelock(folder, isFolder: true); return; }
            var inUse = result.Skipped.Count(skip => skip.Retryable);
            var other = result.Skipped.Count - inUse;
            if (inUse > 0)
            {
                _retryAfter[folder] = DateTime.UtcNow + InUseRetry;
                Report($"Locked {name} again, but {Files(inUse)} {(inUse == 1 ? "is" : "are")} open in another program and stayed unlocked. Clearspace will try again.");
            }
            else if (other > 0)
                Report($"Locked {name} again. {Files(other)} couldn't be locked (over 64 MB, linked, or with extra data streams).");
            else if (result.Total > 0 || result.Changed > 0)
                Report($"Locked {name} again.");
        }
        catch (Exception exception) { Report($"Couldn't lock {name} again: {exception.Message}"); }
        finally { AfterRelock(folder); }
    }

    internal Task StartRelockFile(string file)
    {
        if (_relocks.TryGetValue(file, out var running)) return running;
        var task = RelockFileAsync(file);
        if (!task.IsCompleted) _relocks[file] = task;
        return task;
    }

    private async Task RelockFileAsync(string file)
    {
        var name = Path.GetFileName(file);
        try
        {
            var result = await Task.Run(() => Service.RelockFile(file));
            if (result.NeedsPassword) { AskToRelock(file, isFolder: false); return; }
            if (result.InUse)
            {
                _retryAfter[file] = DateTime.UtcNow + InUseRetry;
                Report($"{name} is open in another program, so it stayed unlocked. Clearspace will try again.");
            }
            else if (result.Error is { } error) Report($"Couldn't lock {name} again: {error}");
            else if (result.Done) Report($"Locked {name} again.");
        }
        catch (Exception exception) { Report($"Couldn't lock {name} again: {exception.Message}"); }
        finally { AfterRelock(file); }
    }

    // FIXED (reset-proof): the saved key for locking this again can't be read (Windows was reset or the account
    // changed since it was unlocked), so ask for the password once. Cancel leaves it readable; Lock in the menu
    // locks it later. Nothing is lost either way.
    private void AskToRelock(string path, bool isFolder)
    {
        if (!_needsPassword.Add(path)) return;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var dialog = new FileLockWindow(path, isFolder ? LockDialogMode.LockFolder : LockDialogMode.LockFile, Service,
                warning: $"Clearspace can't lock {Path.GetFileName(path)} again on its own: Windows was reset or your account changed since it was unlocked. Enter your password to lock it now.");
            Show(dialog, attach: true);
            if (dialog.ResultMessage is not null) _needsPassword.Remove(path);
            else Report($"{Path.GetFileName(path)} is still unlocked. Use Lock with password when you're ready.");
            AfterChange(dialog.ResultMessage);
        });
    }

    private void AfterRelock(string path)
    {
        _relocks.Remove(path);
        if (FileLockRegistry.StateOf(path, Directory.Exists(path)) != LockState.Open)
        {
            _seen.Remove(path);
            _hiddenSince.Remove(path);
            _retryAfter.Remove(path);
        }
        FileLockRegistry.Reload();
        Window?.RefreshLockBadges();
        // Files were renamed to/from .cslock; show the new names if they're on screen.
        if (Window?.CurrentLocation is { } current)
        {
            var parent = Path.GetDirectoryName(path) ?? "";
            if (FileLockService.SamePath(current, path) || FileLockService.IsInside(current, path) || FileLockService.SamePath(current, parent))
                Window.RefreshAfterLockChange(null);
        }
    }

    private static string Files(int count) => count == 1 ? "1 file" : $"{count:N0} files";
}
