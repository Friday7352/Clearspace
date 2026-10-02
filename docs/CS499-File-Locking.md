# Clearspace file locking

Implemented October 1, 2026 for CS 499 Milestone Four. This completes the file-lock
storage portion of the original database plan and adds the corresponding lock and
unlock feature originally proposed under software engineering.

## Using the feature

Right-click one file or folder and select **Lock with password**. The first lock
creates a master password, entered twice so a typo can't lock files away. Any
non-empty password is accepted (the original 12-character minimum was removed on
October 1, 2026); the entry box caps it at 1,024 characters. Later locks require
that same password. Right-click **Unlock**, or use Open on a locked file, to
restore its readable contents. Cancellation before replacement keeps the original.
The password cannot be recovered or reset through this feature.

Each file must be a writable file up to 64 MiB (67,108,864 bytes) on a local NTFS
drive. Files that are read-only, hard-linked, reparse points, inside linked
ancestor folders, offline/cloud placeholders, Windows-encrypted, or that have
alternate data streams are rejected. This includes downloaded files with a
Zone.Identifier stream. It does not remove streams to make a file eligible.
Locking renames the file to `<name>.cslock` (for example `photo.jpg.cslock`) and
unlocking removes that extension; a lock or unlock is refused up front if the
target name is already taken. Tags and lock records follow the rename. A file
locked before this naming existed is renamed the next time its folder is locked.
Password changes are outside this version's scope.

### Folders

Locking a folder encrypts every file in it and its subfolders in place, one file
at a time, using the same per-file format and replacement steps as a single file,
and records the folder in the `LockedFolders` table (schema version 3). File and
folder names stay visible.

**Opening a locked folder.** Navigating into a locked folder, or anywhere inside
it (address bar, sidebar, Back/Forward, a search result), first asks for the
password. The password is checked against the master password before anything is
touched; a wrong password changes nothing. The folder's files are then decrypted
in place so every program, thumbnail and preview works normally, and its
subfolders and files need no further prompts.

**Locking again.** As soon as you navigate outside the folder, Clearspace locks
its files again in the background, including files added while it was open. It
does this without the password: when the folder is opened, the visit's 32-byte
salt and wrapping key are saved in the folder's row protected with Windows DPAPI
(current user, with application entropy), and that saved key is deleted once the
folder is locked again. Files another program still has open are skipped, the
folder stays marked open, and Clearspace retries every 30 seconds and before it
closes; on close you can retry, close anyway, or keep Clearspace open. A folder
left open by a crash or forced close is locked again the next time Clearspace
starts. While a folder is open its files are readable on disk by any program
running as you — the same exposure as opening any file.

**Unlock** on a locked folder decrypts its files for good and removes the folder
record. A folder inside a locked folder can't be locked separately; locking a
parent of a locked folder absorbs the inner one.

- Linked subfolders (junctions, symbolic links, cloud roots) are not followed, and
  System files such as `desktop.ini` are left alone.
- A file that cannot be locked (too large, extra data stream, in use, read-only)
  is skipped and listed with its reason; the remaining files are still processed.
  Files already in the requested state are counted, not changed again.
- The dialog shows progress and can stop between files. Stopping while opening a
  folder keeps you out of it and locks the already-unlocked files again.
- Drive roots, the user profile folder (or anything above it), and the Windows,
  Program Files, ProgramData and AppData folders are refused, because encrypting
  them would break Windows or installed programs. Subfolders of the temp folder
  are allowed.
- The gate is a Clearspace feature. Windows Explorer and other programs can still
  browse a locked folder's names, but its files stay encrypted. Files copied into
  a locked folder by another program stay readable until the folder is next opened
  and left in Clearspace. Moving or renaming a locked folder outside Clearspace
  loses its folder record (its files stay locked and can be unlocked one by one).

### Unlock, Remove lock, and Remove all locks

Clearspace separates unlocking for a visit from removing a lock for good:

- **Unlock** (Clearspace menu, Explorer's **Unlock with Clearspace**, or opening
  a locked file): asks for the password and decrypts the file until Clearspace
  leaves the file's folder, then locks it again without the password. This uses
  the same DPAPI-protected session key and owner as an opened folder, stored in
  the `OpenFiles` table (schema version 5); files in use are retried, and files
  left unlocked by a crash are locked on the next start. "Leaving" means no
  Explorer or Clearspace window is showing the file's folder any more.
  Unlock on a locked folder simply opens it.
- **Remove lock** (Clearspace menu on any number of selected files and folders,
  or Explorer's **Remove lock with Clearspace**): decrypts for good. If a selected
  item is inside a locked folder, the dialog warns first: that folder stops asking
  for a password, and its other files stay locked individually. If that folder
  was open for a visit, its files are locked again before the selected item is
  decrypted.
- **Settings > Remove all locks and reset password**: after confirming the
  current password, removes every lock Clearspace has recorded (locked folders,
  locked files, files unlocked for a visit), drops rows for items that no longer
  exist, and deletes the master password so the next lock creates a new one. The
  password is only cleared when nothing is left locked, so no file is stranded
  behind a password that has been discarded. Locked files Clearspace has no record
  of (moved outside it, or from another PC) keep the old password.

### Reinstalling, resetting, and files with other passwords

Nothing needed to unlock a file lives outside the file: each header carries its
salt, wrapped key and authentication tags. The database
(`%APPDATA%\Clearspace\tags.db`) is only bookkeeping and survives uninstalling
and reinstalling.

- **Unlocking checks the file, not only the saved password.** Unlock, Open,
  Remove lock and opening a locked folder accept the password the file was
  actually locked with. The saved master password is a quick check first; if it
  doesn't match, the password is tried on up to five distinct salts among the
  locked files in scope before anything is changed. So files locked before a
  Windows reset, on another PC, or before "Remove all locks and reset password"
  still open with their own password, even after a new master password exists.
  When there is no saved password (new PC), the password must open one of the
  files, so a typo can never become the key files are locked again with.
- **Change password** (Clearspace menu, or Explorer's *Change password with
  Clearspace* and the folder submenu) moves locked files from the password they
  use to the current Clearspace password, creating it if there isn't one. Each
  file is decrypted and re-encrypted with fresh keys in memory and swapped in
  once, so it is never readable on disk in between. Files already on the current
  password are counted, not rewritten.
- **Switching on unlock.** When Unlock or opening a locked folder is given a
  password that isn't the current Clearspace password but opens the files, the
  prompt says so and offers "Use my current Clearspace password for it from now
  on" (checked), asking for the current password. The files are then opened with
  the older password (or the current one, for files already on it, through a
  fallback key chain), and the visit's re-lock key is derived from the current
  password, so they lock again under it. With no Clearspace password saved yet,
  the choice reads "Make this my Clearspace password" and needs no second entry.
  This only appears for older passwords.
- **Unreadable re-lock key.** If Windows is reset or the account changes while
  something is unlocked for a visit, its DPAPI-protected re-lock key can't be read.
  Clearspace then asks for the password to lock it again instead of failing;
  until then the item simply stays readable.

### Lock icons

Locked files and folders show a small lock badge on their icon in the details
and tile views; an opened locked folder shows an open lock. Badges come from an
in-memory snapshot of the lock tables (`FileLockRegistry`), so listing a folder
never reads file contents. Any `.cslock` name also shows the badge.

### Windows Explorer

Each time Clearspace starts it registers, for the current user only (HKCU, no
administrator rights), the `.cslock` file type and Explorer right-click entries.
The installer also runs `Clearspace.exe --register-shell` and removes the keys on
uninstall. Registration is skipped when nothing has changed.

- `.cslock` files show the locked-file icon in Explorer. Double-clicking one opens
  Clearspace, which asks for the password, unlocks the file and opens it in its
  normal app; it is locked again when Clearspace leaves its folder. A `.cslock`
  file inside a locked folder opens that folder instead, then the file.
- Files get **Lock with Clearspace**; `.cslock` files also get **Unlock with
  Clearspace** (for a visit) and **Remove lock with Clearspace** (for good).
  Folders get a **Clearspace** submenu with Open in Clearspace, Lock folder,
  Unlock folder and Remove lock. On Windows 11 these appear under
  **Show more options**; the compact menu requires a packaged app.
- Locked folders get the locked-folder icon through a `desktop.ini`
  (`IconResource`), with the folder marked read-only so Explorer reads it. An
  existing `desktop.ini` is kept: its previous icon is saved in a `[Clearspace]`
  section and restored on unlock, and a `desktop.ini` Clearspace created is
  deleted. The icon files are written to `%LOCALAPPDATA%\Clearspace\Icons`.
- The entries run `Clearspace.exe --open|--lock|--unlock|--remove-lock "<path>"`.
  Only **Open in Clearspace** (on a folder) shows the main window. Everything
  else, including double-clicking a `.cslock` file, shows just the password
  dialog. If Clearspace is already running, the new process passes the command
  over a named pipe restricted to the current user (`PipeOptions.CurrentUserOnly`)
  and exits; it calls `AllowSetForegroundWindow` first so the running
  Clearspace's dialog can come to the front.
- **Re-locking with Explorer** (`LockAgent`). Something unlocked for a visit (a
  file, or a locked folder) is locked again once no File Explorer window or tab
  and no Clearspace window has shown its folder for 5 seconds (60 seconds if it
  was never shown, e.g. "Unlock folder" before going into it). Open Explorer
  windows are read every 2 seconds through the `Shell.Application` automation
  object, and only while something is unlocked. Until everything is locked again
  Clearspace keeps running without a window, then exits. Files still open in
  another program are retried every 30 seconds.
- Each opened folder records its owning Clearspace process (process ID and start
  time, schema version 4). Another Clearspace window never locks a folder that a
  running Clearspace has open; folders whose owner is no longer running are
  locked on the next start.

The 600,000-iteration key derivation runs once per folder operation rather than
once per file: files locked in one run share a random password salt (and so one
wrapping key), while every file still gets its own random data key and nonces.
On unlock, wrapping keys are cached by salt, so each distinct salt is derived once.

## Encryption and password handling

`FileLockFormat` uses .NET AES-256-GCM with a randomly generated 32-byte data key
for each lock operation. A password-derived 32-byte key wraps that data key using
a separate AES-GCM operation. Both authentication tags are 16 bytes. The wrapping
and content operations use independent keys and fresh 12-byte nonces. Password
derivation uses PBKDF2-HMAC-SHA256, a random 32-byte salt, and 600,000 iterations.
The parser accepts only that work factor and the supported size limit, avoiding
unbounded work requested by an untrusted header.

The 156-byte versioned header contains the magic `CSLOCK01`, a random file ID,
work factor, plaintext length, salt, wrapping nonce, wrapped data key, wrapping
tag, content nonce, and content tag. The wrapping operation authenticates the
header prefix through the salt. The content operation authenticates the header
through the content nonce. Changing header fields, ciphertext, length, or tags
causes rejection. Paths are deliberately excluded so moving or renaming an
encrypted file does not prevent recovery.

Password entry uses WPF PasswordBox. The UI copies its SecureString into a
short-lived character array, clears both password controls, and clears the array
after completion. The service clears application-owned plaintext and unwrapped
key arrays in finally blocks. This is not a guarantee that operating-system
paging or every runtime/library copy is erased.

## SQLite schema and migration

The existing `tags.db` now uses schema version 2. The upgrade from version 1 adds
two tables inside a transaction and preserves Tags, Paths, and PathTags. A fresh
database imports legacy JSON and installs both schema versions in one transaction.
An invalid import still rolls back the entire schema. Future versions are rejected.

- `LockSettings` has a single row containing a random salt, the work factor, and
  an HMAC verifier derived from the master password. It stores no plaintext password.
- `LockedFiles` maps case-insensitive FilePath values to FileId, the binary
  authenticated Header, State, and UpdatedUtc. The header contains only a wrapped
  data key. An index on FileId supports identifying records for the same envelope.
- All values are bound as SQL parameters. Constraints enforce valid header and
  verifier lengths and the three supported operation states.

The header is deliberately duplicated in the file. The database manages local
metadata, while an encrypted file remains portable and recoverable using its
password on a fresh installation. If the file is renamed outside the app, an old
path record can remain; the file header determines whether the file is locked.
Unlocking one copy removes only its path record, not records for other copies.

## Failure handling and boundaries

1. Validate the path and file, reject unsupported links/streams, and open a handle
   that denies concurrent writers.
2. Encrypt, or authenticate and decrypt, in memory. Authentication failure causes
   no file replacement. The size limit bounds the memory cost of this approach.
3. Commit a Preparing or Unlocking database record before replacing any file.
4. Write a uniquely named temporary file beside the source. Its ACL allows only
   the current Windows user and SYSTEM. Flush it to disk and verify its hash.
5. Recheck the target identity and eligibility, then use File.Replace. Preserve
   the source timestamp and require successful metadata handling.
6. Record Locked, or remove the path record after unlock. If this last database
   update fails, report that the file operation succeeded with a metadata warning.

The filesystem replacement and SQLite commit are separate operations. The design
does not claim a transaction across both systems. The embedded authenticated
header makes a completed replacement recoverable when the final database update
does not occur. A retry reads the actual file contents rather than trusting a
possibly stale state value.

Ordinary errors and cancellation delete the operation's temporary file. A process
or machine crash during unlock preparation can leave a plaintext temporary file
with the restricted ACL above; automatic crash-remnant cleanup is not implemented.
The same Windows account or an administrator can access that remnant. Replacement
also does not securely erase old disk blocks, existing backups, cached previews,
search snippets, or synchronized copies. File names remain visible. Use trusted
local directories: identity checks do not establish a security boundary against
an adversary who can continuously replace directory entries. This feature protects
the current file contents at rest; it is not a substitute for full-disk encryption.

## Verification

The full Release suite passed 251 tests on October 1, 2026. `FileLockTests` includes
empty and nonempty round trips, independent keys/nonces, wrong passwords, modified
headers and ciphertext, truncation, renamed files with a fresh database, schema
upgrade, cancellation, busy files, hard links, additional streams, size limits,
and failures around replacement and database writes. Interruption tests inject
exceptions at durable boundaries; they do not simulate a physical power loss.

A separate WPF dialog check used disposable files and a private test database.
The actual lock and unlock buttons restored the original bytes; an incorrect
password showed an error while retaining the encrypted file. Both dialog layouts
were rendered and inspected. Release publishing succeeded. No personal files
were encrypted during these checks. The interface still merits a user walkthrough
with disposable files before use on important data.

## References

- Microsoft, [AesGcm.Encrypt](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.encrypt?view=net-10.0).
- Microsoft, [FileSystemAclExtensions.Create](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemaclextensions.create?view=net-10.0).
- Microsoft, [ReplaceFile](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilea).
- OWASP, [Password Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html).
- OWASP, [Cryptographic Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html).
