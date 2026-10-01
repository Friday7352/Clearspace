// Clearspace | Lock / unlock dialog code-behind (layout lives in FileLockWindow.xaml).
// CHANGED (lock dialog redesign): this file used to build the whole window in code with hard-coded
// colors. It is now the code-behind for FileLockWindow.xaml, which uses the app's shared styles.
// CHANGED (no password rules): any non-empty password can be created; only the confirmation must match.
// NEW (folder locking): handles folders too — shows progress, can stop between files, and lists skipped files.
// CHANGED (locked folders): one dialog for every locking job (see LockDialogMode), including the password
// prompt shown when you open a locked folder.
// CHANGED (unlock vs remove lock): "Unlock" (for a visit) and "Remove lock" (for good, one or many items)
// are separate jobs, plus "Remove all locks" which also resets the master password.
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Clearspace.Native;
using Clearspace.Services;

namespace Clearspace;

// NEW (folder locking): one row in the skipped-files list.
public sealed record FileLockSkipRow(string Name, string Reason);

internal enum LockDialogMode
{
    LockFile,     // encrypt one file
    LockFolder,   // encrypt a folder's files and make the folder ask for the password
    OpenFolder,   // password prompt when entering a locked folder; unlocked until you leave it
    OpenFile,     // NEW (unlock vs remove lock): unlock one file until Clearspace leaves its folder
    RemoveLocks,  // NEW (unlock vs remove lock): decrypt one or more files/folders for good
    RemoveAll,    // NEW (unlock vs remove lock): remove every lock and reset the master password
    ChangePassword // NEW (change password): move locked files from their password to your current one
}

internal sealed partial class FileLockWindow : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly FileLockService _service;
    private readonly IReadOnlyList<string> _paths;
    private readonly LockDialogMode _mode;
    private readonly bool _setup;
    private readonly bool _newMaster; // NEW (change password): no Clearspace password yet; this creates it
    private readonly Func<Task>? _waitFirst; // e.g. a re-lock of this folder still running
    private bool _working;
    private bool _finished; // a run finished with something to show; the button now reads Done/Open
    // NEW (switch to current password): an older password opened the file/folder; it's kept here (cleared on
    // close) while the prompt asks whether to switch to the current Clearspace password.
    private char[]? _olderPassword;
    internal string? ResultMessage { get; private set; }
    internal string? ResultPath { get; private set; } // a file's name after lock/unlock
    internal LockRemovalResult? RemovalResult { get; private set; } // NEW (unlock vs remove lock)

    private string FirstPath => _paths[0];
    private bool Locking => _mode is LockDialogMode.LockFile or LockDialogMode.LockFolder;
    private bool ShowsProgress => _mode is LockDialogMode.LockFolder or LockDialogMode.OpenFolder or LockDialogMode.RemoveLocks
        or LockDialogMode.RemoveAll or LockDialogMode.ChangePassword;

    internal FileLockWindow(string path, LockDialogMode mode, FileLockService service, Func<Task>? waitFirst = null, string? warning = null)
        : this([path], mode, service, waitFirst, warning) { }

    internal FileLockWindow(IReadOnlyList<string> paths, LockDialogMode mode, FileLockService service,
        Func<Task>? waitFirst = null, string? warning = null)
    {
        InitializeComponent();
        EInkScreen.Frame(this); // NEW (e-ink): the E-reader theme's panel look reaches this dialog too
        _paths = paths.Count > 0 ? paths : [string.Empty];
        _mode = mode; _service = service; _waitFirst = waitFirst;
        _setup = Locking && !service.HasPassword;
        _newMaster = mode == LockDialogMode.ChangePassword && !service.HasPassword;

        var single = _paths.Count == 1;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(FirstPath));
        if (string.IsNullOrEmpty(name)) name = FirstPath;
        var anyFolder = _paths.Any(Directory.Exists);
        Title = mode switch
        {
            LockDialogMode.LockFile => "Lock file",
            LockDialogMode.LockFolder => "Lock folder",
            LockDialogMode.OpenFolder => "Locked folder",
            LockDialogMode.OpenFile => "Unlock file",
            LockDialogMode.RemoveLocks => single ? "Remove lock" : $"Remove {_paths.Count:N0} locks",
            LockDialogMode.ChangePassword => "Change password",
            _ => "Remove all locks"
        };
        Heading.Text = Title;
        Glyph.Text = mode is LockDialogMode.LockFile or LockDialogMode.LockFolder or LockDialogMode.OpenFolder ? "" : ""; // Lock / Unlock
        ItemName.Text = mode == LockDialogMode.RemoveAll ? "Every file and folder you've locked"
            : single ? name : $"{_paths.Count:N0} items";
        ItemName.ToolTip = string.Join(Environment.NewLine, _paths.Take(20));
        SubmitButton.Content = mode switch
        {
            LockDialogMode.OpenFolder => "Open",
            LockDialogMode.OpenFile => "Unlock",
            LockDialogMode.RemoveLocks => "Remove lock",
            LockDialogMode.RemoveAll => "Remove all locks",
            LockDialogMode.ChangePassword => "Change password",
            _ => Title
        };

        Explanation.Text = mode switch
        {
            LockDialogMode.LockFolder => "Encrypts every file in this folder and its subfolders, and asks for your password whenever the folder is opened. While you're inside, its files are unlocked; Clearspace locks them again when you leave. File and folder names stay visible.",
            LockDialogMode.LockFile => "Encrypts this file in place. Copies and backups made earlier stay readable.",
            LockDialogMode.OpenFolder => "Enter your password to open this folder. Its files stay unlocked while you're inside, including subfolders, and are locked again when you leave.",
            LockDialogMode.OpenFile => "Enter your password to unlock this file. It stays unlocked while Clearspace is in this folder and is locked again when you leave it.",
            LockDialogMode.RemoveLocks => (single ? (anyFolder ? "Decrypts this folder's files" : "Decrypts this file") : "Decrypts these items")
                                          + " for good. Clearspace won't ask for a password for " + (single ? "it" : "them") + " again.",
            LockDialogMode.ChangePassword => (single && !anyFolder ? "Switches this locked file" : "Switches every locked file here")
                + " from the password it uses now to " + (_newMaster ? "a new Clearspace password, which you'll use from now on." : "your current Clearspace password."),
            _ => "Decrypts every file and folder Clearspace has locked, then clears your master password so the next lock creates a new one."
        };
        if (_setup)
            Explanation.Text += " The password you create here is used for everything you lock in Clearspace, and it can't be recovered if you forget it.";

        // CHANGED (unlock vs remove lock): this line also carries warnings (shown in the accent color).
        var limits = mode switch
        {
            LockDialogMode.LockFolder => "Files up to 64 MB on a local NTFS drive. Linked files, files with extra data streams (often downloads) and files in use are skipped and listed.",
            LockDialogMode.LockFile => "Files up to 64 MB on a local NTFS drive. Linked or cloud placeholder files aren't supported yet.",
            LockDialogMode.RemoveAll => "Locked files Clearspace doesn't know about (moved or renamed outside Clearspace, or copied from another PC) keep the current password. The password is only cleared if every lock is removed.",
            _ => null
        };
        if (warning is not null)
        {
            Limits.Text = warning;
            Limits.Foreground = (Brush)FindResource("Accent");
        }
        else Limits.Text = limits ?? string.Empty;
        Limits.Visibility = string.IsNullOrEmpty(Limits.Text) ? Visibility.Collapsed : Visibility.Visible;

        PasswordLabel.Text = _setup ? "Create a password" : mode switch
        {
            LockDialogMode.RemoveAll => "Current password",
            LockDialogMode.ChangePassword => "Password these files use now",
            _ => "Password"
        };
        ConfirmPanel.Visibility = _setup || mode == LockDialogMode.ChangePassword ? Visibility.Visible : Visibility.Collapsed;
        if (mode == LockDialogMode.ChangePassword)
        {
            ConfirmLabel.Text = _newMaster ? "New Clearspace password" : "Your current Clearspace password";
            NewConfirmPanel.Visibility = _newMaster ? Visibility.Visible : Visibility.Collapsed;
        }

        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; OnCancel(this, new RoutedEventArgs()); } };
        Closing += (_, e) =>
        {
            if (!_working) return;
            e.Cancel = true;
            _cancellation.Cancel();
            ShowStatus(ShowsProgress ? "Stopping after the current file…" : "Finishing safely before closing…", error: false);
        };
        Closed += (_, _) =>
        {
            PasswordInput.Clear(); ConfirmInput.Clear(); NewConfirmInput.Clear();
            _olderPassword?.AsSpan().Clear();
            _cancellation.Dispose();
        };
        Loaded += (_, _) => PasswordInput.Focus();
    }

    // Same title bar and corners as the main window.
    // CHANGED (themes): follows the active theme instead of always being dark.
    private void ApplyDarkTitleBar() => ThemeService.ApplyTitleBar(this);

    private static char[] ReadPassword(System.Windows.Controls.PasswordBox box)
    {
        using var secret = box.SecurePassword;
        var memory = Marshal.SecureStringToGlobalAllocUnicode(secret);
        try { var chars = new char[secret.Length]; Marshal.Copy(memory, chars, 0, chars.Length); return chars; }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(memory); }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_finished) { DialogResult = true; return; }
        if (_working)
        {
            _cancellation.Cancel();
            ShowStatus(ShowsProgress ? "Stopping after the current file…" : "Cancelling…", error: false);
            return;
        }
        Close();
    }

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        if (_finished) { DialogResult = true; return; }
        if (_working) return;
        var opening = _mode is LockDialogMode.OpenFile or LockDialogMode.OpenFolder;
        // NEW (switch to current password): on the second step the typed password is the one kept from step one.
        var password = _olderPassword is { } older ? (char[])older.Clone() : ReadPassword(PasswordInput);
        var confirmation = ReadPassword(ConfirmInput);
        var newConfirmation = ReadPassword(NewConfirmInput);
        char[]? switchTo = null;
        try
        {
            if (password.Length == 0) { ShowStatus("Enter a password.", error: true); return; }
            if (opening && _olderPassword is null)
            {
                // Step one: is this the current Clearspace password, or an older one that still opens it?
                SetWorking(true);
                ShowStatus("Checking the password…", error: false);
                var isCurrent = await Task.Run(() => _service.IsCurrentPassword(_paths, password));
                if (!isCurrent)
                {
                    ShowOlderPasswordChoice(password);
                    return;
                }
                StatusText.Visibility = Visibility.Collapsed;
            }
            else if (opening && SwitchCheck.IsChecked == true)
            {
                if (_service.HasPassword)
                {
                    if (confirmation.Length == 0) { ShowStatus("Enter your current Clearspace password.", error: true); return; }
                    switchTo = confirmation;
                }
                else switchTo = password; // no Clearspace password yet: this one becomes it
            }
            if (_setup && !password.AsSpan().SequenceEqual(confirmation)) { ShowStatus("The passwords don't match.", error: true); return; }
            // NEW (change password): second box is the Clearspace password (and the third confirms a new one).
            if (_mode == LockDialogMode.ChangePassword)
            {
                if (confirmation.Length == 0) { ShowStatus("Enter your Clearspace password.", error: true); return; }
                if (_newMaster && !confirmation.AsSpan().SequenceEqual(newConfirmation)) { ShowStatus("The new passwords don't match.", error: true); return; }
            }

            SetWorking(true);
            PasswordInput.Clear(); ConfirmInput.Clear(); NewConfirmInput.Clear();
            var token = _cancellation.Token;

            if (ShowsProgress)
            {
                ProgressPanel.Visibility = Visibility.Visible;
                Progress.IsIndeterminate = true;
                ProgressText.Text = "Checking your password…";
                StatusText.Visibility = Visibility.Collapsed;
                if (_waitFirst is not null)
                {
                    ProgressText.Text = "Finishing the previous lock of this folder…";
                    await _waitFirst();
                    ProgressText.Text = "Checking your password…";
                }
                var progress = new System.Progress<FolderLockProgress>(ReportProgress);
                switch (_mode)
                {
                    case LockDialogMode.RemoveLocks:
                        ShowRemovalResult(await Task.Run(() => _service.RemoveLocks(_paths, password, progress, token)));
                        break;
                    case LockDialogMode.RemoveAll:
                        ShowRemovalResult(await Task.Run(() => _service.RemoveAllLocks(password, progress, token)));
                        break;
                    case LockDialogMode.ChangePassword:
                        ShowRemovalResult(await Task.Run(() => _service.ChangePassword(_paths, password, confirmation, progress, token)));
                        break;
                    default:
                        var result = await Task.Run(() => _mode == LockDialogMode.LockFolder
                            ? _service.LockFolder(FirstPath, password, progress, token)
                            : _service.OpenFolder(FirstPath, password, progress, token, switchTo));
                        ShowFolderResult(result);
                        break;
                }
            }
            else
            {
                ShowStatus(_mode == LockDialogMode.LockFile ? "Encrypting and verifying the file…" : "Checking the password and unlocking the file…", error: false);
                var result = await Task.Run(() => _mode == LockDialogMode.OpenFile
                    ? _service.OpenFile(FirstPath, password, token, switchTo)
                    : _service.Transform(FirstPath, password, true, token));
                ResultPath = result.Path;
                ResultMessage = result.Warning ?? (_mode == LockDialogMode.LockFile
                    ? "File locked."
                    : $"Unlocked {Path.GetFileName(result.Path)}. It locks again when you leave this folder.");
                _working = false;
                DialogResult = true;
            }
        }
        catch (OperationCanceledException) { _working = false; Close(); }
        catch (Exception exception)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            ShowStatus(exception.Message, error: true);
        }
        finally
        {
            password.AsSpan().Clear(); confirmation.AsSpan().Clear(); newConfirmation.AsSpan().Clear();
            if (!_finished && IsLoaded) SetWorking(false);
        }
    }

    // NEW (switch to current password): step two of the prompt, only for files/folders on an older password.
    private void ShowOlderPasswordChoice(char[] password)
    {
        _olderPassword = (char[])password.Clone();
        var hasCurrent = _service.HasPassword;
        var what = _mode == LockDialogMode.OpenFolder ? "This folder's files use" : "This file uses";
        OlderNote.Text = hasCurrent
            ? $"{what} an older password, not your current Clearspace password."
            : $"{what} a password from before; Clearspace on this PC doesn't have a password yet.";
        SwitchText.Text = hasCurrent ? "Use my current Clearspace password for it from now on" : "Make this my Clearspace password";
        PasswordLabel.Visibility = Visibility.Collapsed;
        PasswordInput.Clear();
        PasswordInput.Visibility = Visibility.Collapsed;
        OlderPanel.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Collapsed;
        UpdateSwitchChoice();
        _working = false;
    }

    private void OnSwitchChanged(object sender, RoutedEventArgs e) => UpdateSwitchChoice();

    private void UpdateSwitchChoice()
    {
        if (_olderPassword is null) return;
        var needsCurrent = SwitchCheck.IsChecked == true && _service.HasPassword;
        ConfirmLabel.Text = "Your current Clearspace password";
        ConfirmPanel.Visibility = needsCurrent ? Visibility.Visible : Visibility.Collapsed;
        if (needsCurrent) Dispatcher.BeginInvoke(() => ConfirmInput.Focus());
    }

    private void ReportProgress(FolderLockProgress progress)
    {
        if (_finished || progress.Total == 0) return;
        Progress.IsIndeterminate = false;
        Progress.Maximum = progress.Total;
        Progress.Value = progress.Index;
        if (progress.Index < progress.Total)
            ProgressText.Text = $"{(Locking ? "Locking" : "Unlocking")} {progress.Index + 1:N0} of {progress.Total:N0} · {progress.Name}";
    }

    // Closes straight away on a clean run; otherwise stays open with a summary and the skipped files so
    // nothing is silently left unlocked (or locked).
    private void ShowFolderResult(FolderLockResult result)
    {
        var done = result.Locking ? "locked" : "unlocked";
        var name = Path.GetFileName(result.Folder);
        string summary;
        if (_mode == LockDialogMode.OpenFolder && result.Skipped.Count == 0)
            summary = $"Opened {name}. It locks again when you leave it.";
        else if (result.Total == 0)
            summary = _mode == LockDialogMode.LockFolder
                ? $"{name} is locked. It has no files yet; files you add will be locked when you leave it."
                : $"{name} has no files to {(result.Locking ? "lock" : "unlock")}.";
        else
        {
            summary = $"{Capitalize(done)} {Files(result.Changed)} in {name}";
            if (result.AlreadyDone > 0) summary += $" ({result.AlreadyDone:N0} already {done})";
            if (result.Skipped.Count > 0) summary += $". {Files(result.Skipped.Count)} skipped";
            if (result.Cancelled) summary += ". Stopped before finishing";
            if (result.Warnings > 0) summary += $". {Files(result.Warnings)} could not be recorded in the database but are still recoverable with the password";
            summary += ".";
        }
        ResultMessage = summary;
        _working = false;

        // Stopping while opening a folder means you don't go in; the caller locks the files that were
        // already unlocked again.
        if (_mode == LockDialogMode.OpenFolder && result.Cancelled)
        {
            ResultMessage = $"Didn't open {name}.";
            DialogResult = false;
            return;
        }

        if (result.Skipped.Count == 0 && !result.Cancelled && result.Warnings == 0)
        {
            DialogResult = true;
            return;
        }
        ShowSummary(summary, result.Folder, result.Skipped);
    }

    // NEW (unlock vs remove lock)
    private void ShowRemovalResult(LockRemovalResult result)
    {
        RemovalResult = result;
        var changing = _mode == LockDialogMode.ChangePassword;
        var parts = new List<string>
        {
            result.Removed == 0 && result.AlreadyUnlocked == 0 && result.Skipped.Count == 0
                ? (changing ? "There were no locked files to change." : "There was nothing locked to remove.")
                : changing
                    ? $"Switched {Files(result.Removed)} to your Clearspace password" + (result.AlreadyUnlocked > 0 ? $" ({result.AlreadyUnlocked:N0} already used it)." : ".")
                    : $"Removed the lock from {Files(result.Removed)}" + (result.AlreadyUnlocked > 0 ? $" ({result.AlreadyUnlocked:N0} already unlocked)." : ".")
        };
        foreach (var parent in result.DissolvedParents)
            parts.Add($"{Path.GetFileName(parent)} no longer asks for a password; its other files stay locked.");
        if (result.Skipped.Count > 0) parts.Add($"{Files(result.Skipped.Count)} couldn't be {(changing ? "changed" : "unlocked")}.");
        if (result.Cancelled) parts.Add("Stopped before finishing.");
        if (_mode == LockDialogMode.RemoveAll)
            parts.Add(result.PasswordReset
                ? "Your master password was cleared; the next lock asks you to create a new one."
                : "Your master password was kept because something is still locked.");
        var summary = string.Join(" ", parts);
        ResultMessage = summary;
        _working = false;

        if (result.Skipped.Count == 0 && !result.Cancelled && result.DissolvedParents.Count == 0 && _mode != LockDialogMode.RemoveAll)
        {
            DialogResult = true;
            return;
        }
        var root = _paths.Count == 1 && Directory.Exists(FirstPath) ? FirstPath : null;
        ShowSummary(summary, root, result.Skipped);
    }

    private void ShowSummary(string summary, string? root, IReadOnlyList<FolderLockSkip> skipped)
    {
        _finished = true;
        PasswordPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        Limits.Visibility = Visibility.Collapsed;
        ShowStatus(summary, error: false);
        if (skipped.Count > 0)
        {
            SkippedList.ItemsSource = skipped
                .Select(skip => new FileLockSkipRow(root is null ? skip.Path : Path.GetRelativePath(root, skip.Path), skip.Reason))
                .ToList();
            SkippedPanel.Visibility = Visibility.Visible;
        }
        CancelButton.Visibility = Visibility.Collapsed;
        SubmitButton.Content = _mode == LockDialogMode.OpenFolder ? "Open folder" : "Done";
        SubmitButton.IsEnabled = true;
    }

    private static string Files(int count) => count == 1 ? "1 file" : $"{count:N0} files";
    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    private void ShowStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.Foreground = error ? (Brush)FindResource("Danger") : (Brush)FindResource("InkMuted");
        StatusText.Visibility = Visibility.Visible;
    }

    private void SetWorking(bool working)
    {
        _working = working;
        SubmitButton.IsEnabled = !working;
        PasswordInput.IsEnabled = ConfirmInput.IsEnabled = NewConfirmInput.IsEnabled = !working;
    }
}
