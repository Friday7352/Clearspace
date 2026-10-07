# Clearspace

A file manager that owns its own list, so it can be measured, tuned, and extended.

Build and run:

```
dotnet build -c Debug
dotnet run -c Debug
```

Requires the .NET 10 SDK. Targets `net10.0-windows`, x64.

## Why WPF and not WinUI 3

Startup time is a stated goal. A packaged WinUI 3 app pays for package activation
and a heavier framework load before the first frame; WPF starts in a fraction of
that and needs no packaging to run. WPF also virtualizes long lists well, which is
the case that matters here.

## Where the speed comes from

Three decisions, all in the load path:

**One pass over the directory.** `DirectoryEnumerator` calls `FindFirstFileEx` with
`FindExInfoBasic` and `FIND_FIRST_EX_LARGE_FETCH`. The find data already carries the
size, attributes, and timestamps, so a finished row costs no extra disk access.
`Directory.EnumerateFileSystemEntries` hands back strings, which then need a stat
call each.

**Icons are cached by extension, not by file.** A folder with 80,000 photos makes one
shell call, not 80,000. `SHGFI_USEFILEATTRIBUTES` tells the shell to answer from the
registry rather than open the file. Only types that carry their own icon (`.exe`,
`.lnk`, `.ico`) bypass the cache.

**Loads are cancellable.** Navigating away cancels the in-flight listing, so a slow
network share never blocks the next folder.

The status bar prints the load time for each folder, so regressions are visible
while you work.

## Adding a feature

Everything the app can do is an `IAction`. To add one:

1. Write a class implementing `IAction` (see `Commands/Actions/`). Declare a label,
   a glyph, a hotkey, and when it is executable.
2. Add one line to `CommandManager.Register`.

The key binding, the enabled/disabled state, the tooltip, and the menu entry all
follow from what the action declared. `MainWindow.OnPreviewKeyDown` is the entire
keyboard layer and never needs editing.

Files uses a Roslyn generator to remove even step 2. Worth doing once the shape of
the command list settles.

## Access and elevation

Clearspace runs as the invoking user, and the manifest says `asInvoker`. Marking it
`requireAdministrator` would make every folder readable and the app worse in three
ways that are hard to walk back: UIPI blocks drag and drop from a normal-rights
Explorer window into an elevated one, drive letters the user mapped are invisible
to the elevated token, and every ordinary browsing session then runs with rights it
does not need. Explorer runs unelevated for exactly these reasons.

Instead, `DirectoryEnumerator` translates `ERROR_ACCESS_DENIED` into a real
`UnauthorizedAccessException` (it used to `yield break`, which made a protected
folder look identical to an empty one), `MainViewModel` records the refused path,
and `ElevationService` launches a *second* process with `Verb = "runas"` pointed at
that folder. Two processes, two tokens, and the everyday one stays unprivileged.

Not every refusal is fixable by elevating. `System Volume Information` wants SYSTEM,
and the compatibility junctions (`C:\Users\All Users`, `AppData\Local\Application
Data`) carry a deny rule no token gets past. The banner hides its button when the
current process is already elevated.

## Cloud files

OneDrive's Files On-Demand state is not an API call: it lives in file attributes
(`PINNED`, `UNPINNED`, `RECALL_ON_DATA_ACCESS`), none of which exist in
`System.IO.FileAttributes` but all of which survive the marshal into the
`WIN32_FIND_DATA` the enumerator already reads. So a folder's entire sync status
costs nothing beyond the listing that was happening anyway.

`CloudStorageService` handles both halves. Discovery reads the registry rather than
guessing at `%UserProfile%\OneDrive`: `Software\Microsoft\OneDrive\Accounts` for the
friendly account names, then `Explorer\SyncRootManager`, which is the shell's own
list and therefore picks up Dropbox, Google Drive, and iCloud for free. Pinning
writes the attribute pair, which is what the shell's own menu items do and avoids a
hard dependency on `cldapi.dll`.

One trap worth remembering: touching a dehydrated file's *contents* makes the sync
engine download it. `ThumbnailService` therefore adds `SIIGBF_INCACHEONLY` for
online-only items, or scrolling one grid of an online-only Pictures folder would
quietly pull gigabytes over the network.

## Themes

<!-- NEW (themes) -->
Dark (the default), OLED Black, Light, Blueprint, Terminal and Retro, picked on the Themes page
(Settings > Choose a theme…) and remembered in `settings.json`.

<!-- NEW (themes page) -->
- **The Themes page** (`ThemesView.cs`, opened and closed by `MainWindow.Themes.cs`) replaces the list of
  themes the Settings menu used to hold. It sits in `ThemesHost`, shown in place of the explorer like the
  other pages. Each theme is a card with a `Controls/ThemePreview.cs`: a drawing of the window made of
  boxes that ask for the same keys the real window uses (`Base`, `Surface`, `Ink`, `Accent`, `Selected`,
  `R8`, `UIFont` ...). The preview's own `Resources` hold the theme (`ThemeService.PreviewResources`: the
  app's fallback dictionaries with that theme's dictionary on top), so the look-up stops at the preview
  and never reaches the theme on screen. Cards are made at once; the previews are filled in one per idle
  turn, because each means opening a theme file. The page listens to `ThemeService.Changed` (which card
  is ticked) and `ThemeService.ListChanged` (import, remove, the folder watcher).
- **Dark is built in.** `ThemeLibrary` always lists Dark. While `Dark.zip` (or a `Dark` folder) is in the
  Themes folder and its `theme.json` can be read, that is what is listed and shown. Otherwise the entry
  is `ThemeLibrary.BuiltInDark` (`BuiltIn`, no `Path`), and showing it removes the active dictionary so
  that only the fallback from `App.xaml` is left, which is the Dark look. A `Dark.zip` whose `theme.xaml`
  cannot be shown falls back the same way when Dark is picked (while it is being edited and saved, the
  status bar says what is wrong instead). So no state of the Themes folder leaves Clearspace without a theme.
- **Removing** (`ThemeService.Remove`) moves the theme's file or folder to the Recycle Bin through
  `FileOperationService.Delete` and switches to Dark when it was the theme in use. `CanRemove` is false
  for Dark only. Clearspace's other themes can be removed; an update installs them again.

<!-- CHANGED (theme files): themes are files in a folder, not XAML compiled into the app. -->
- A theme is one file: a zip with `theme.json` (name, author, and the switches below) and `theme.xaml`
  (every colour, the four text fonts and the corner sizes, under the keys `Base`, `Surface`, `Ink`,
  `Accent`, `R8`, `UIFont`, ...), plus optional `fonts\` and `images\`. `Services/ThemePackage.cs` reads one;
  a folder with the same files is read the same way. `docs/Making-a-theme.md` is the guide for people
  making one.
- All themes live in one folder, `Themes` beside `Clearspace.exe` (`ThemeLibrary`), Clearspace's own
  included. Their sources are the folders in `Clearspace/Themes/<Name>`; `Clearspace.csproj` zips each
  into `Themes\<Name>.zip` in the output on every build and publish, and does not compile their XAML.
  The file name is the theme's id and what `settings.json` remembers. `ThemeLibrary.Shipped` lists the
  ones that come with Clearspace, in the order the page shows them; anything else in the folder is listed
  under *Your themes*.
- **A theme file is untrusted.** XAML can create any .NET object and call methods while it loads, and
  theme files are passed around. `ThemeMarkup.Prepare` therefore reads `theme.xaml` as plain XML first and
  lets through only a fixed list of elements (brushes, drawings, geometries, pens, transforms, `Color`,
  `CornerRadius`, `Thickness`, `Visibility`, `FontFamily`) with plain values: nothing in `{ }`, no other
  namespaces, no `Source`, and no character that could form a path or address (`/ \ :`). Fonts and
  pictures can only be named as `fonts/#Family` and `images/name.png`; those two forms are rewritten to
  the folder `ThemeCache` unpacked the theme's own files into (`%LocalAppData%\Clearspace\ThemeCache`).
  A theme of nothing but flat colours, fonts and sizes (most of them) is then built straight from the
  checked XML (`ThemeLoader.Plain`), which takes a few milliseconds where the XAML reader takes far
  longer, and this runs before the first window opens; gradients, patterns and pictures go to
  `XamlReader`, as XAML rebuilt from the checked XML. `ThemeLoader` then checks every key
  against `ThemeKeys`: an unknown key is dropped, and a known key with the wrong kind of value (a brush
  under `R8`) refuses the theme, since that would stop windows from drawing. Change the allow-list only
  with that in mind.
- `App.xaml` merges `Themes/Defaults.xaml` and the Dark theme's `theme.xaml` (the only theme XAML still
  compiled in) as the fallback. `ThemeService` merges the active theme after them and `Apply` swaps that
  one entry, so a theme may leave keys out, and a missing Themes folder still gives a working dark app.
  Windows, menus, popups, tooltips and dialogs all refer to the keys with `DynamicResource`, so they
  repaint at once. A new piece of XAML must use `DynamicResource` for these keys, never
  `StaticResource`, or it will stay in the theme it was created in. A new key needs a value in Dark's
  `theme.xaml` (or `Defaults.xaml`) and an entry in `ThemeKeys`.
- A theme that cannot be read never replaces the look on screen: `Apply` returns false and
  `ThemeService.LastProblem` holds a sentence for the person, with the line of `theme.xaml` at fault.
- `ThemeLibrary` watches the folder. A file dropped in shows up on the Themes page (which also reads the
  folder again each time it opens), and when the active theme's file or folder is
  saved again the theme is shown again, which is how someone making a theme sees their changes.
- Code that draws by hand (the disk usage map and 3D views, status colours in the file list, drag-drop
  highlights) reads frozen brushes from `ThemeService` (`ThemeService.Ink`, `.Good`, `.CardFill`, ...).
- `ThemeService.ApplyTitleBar` gives each window the matching title bar: dark or light, round or square
  corners, and on Windows 11 the caption colours a theme asks for (`Theme.CaptionColor`).
- Retro's selected rows are solid navy, so text inside them must turn light (Terminal does the same for
  its green block). The theme lists
  replacements as `SelectedRow.Ink`, `SelectedRow.InkMuted`, ...; `ThemeService` merges them into a row's
  own resources while it is selected, and everything inside the row picks them up.
- `MessageDialog` is the themed stand-in for the Windows message box.

<!-- NEW (experimental themes) -->
**Experimental themes** (Pixel, DOS Commander, Wireframe, Neon HUD, Glass, Sketchbook, Sketchbook (dark),
Type tiles, Zen, 1-bit Mac, 1-bit Mac (dark), E-reader) sit under their own heading on the Themes page
and go beyond colour:

- `ThemeService.Themes` lists every theme in the Themes folder as a `ThemeInfo`, read from its
  `theme.json`: its file name, menu label, whether it is
  experimental, which icon style it uses, whether the toolbar fades (Zen) and whether the window is
  shown as an e-ink panel (E-reader). That last one is a pixel shader over the whole window
  (`Services/EInkScreen.cs`: 16 greys, paper and ink tones) driven by `MainWindow.EInk.cs` (page turns that
  morph the old page into the new one).
- `ThemeIcons` draws the icon sets. `IconService`, `ScalableIconService` and `ThumbnailService` ask
  `ThemeIcons.For(item)` first and use Windows' icon when it returns null. The drawings are vectors chosen
  by kind of file (folder, picture, music, program ...), so one drawing serves every size. Photos and
  videos still show their real picture in tile view.
- When a switch changes the icon style, `MainWindow` clears the tile-picture and folder-listing caches
  and reads the current folder again, because each row carries the icon it was given.
- `Themes/Defaults.xaml` is merged before the active theme and holds the "off" value of extras only
  experimental themes use: `OverlayFill` / `OverlayVisibility` (Neon HUD's scanlines) and
  `FunctionKeysVisibility` (DOS Commander's key bar). A theme turns one on by defining the same key.
  `ListFill` (NEW, list backdrop) is the same kind of extra: a brush `MainWindow.xaml` paints behind
  `FileList` only, see-through unless a theme sets it. `OverlayFill` lies over the whole window, the
  other pages included, so a picture meant for the file list belongs in `ListFill`.
- A theme whose `Base` is not a flat colour (Glass: gradient, Sketchbook: graph paper) also defines
  `Theme.BaseColor` for code. The text, accent, status and card colours must stay flat colours in every
  theme, because `ThemeService` reads them for the hand-drawn parts (`ThemeLoader` refuses a theme that
  gives one of them a gradient or a pattern).

<!-- CHANGED (theme files) -->
To add a theme that comes with Clearspace: make a folder for it in `Clearspace/Themes` (copy `Dark`),
change `theme.json` and the values in `theme.xaml`, and add the folder's name to `ThemeLibrary.Shipped`
where it should appear on the Themes page. `ThemeTests` checks that the list and the folders agree and that
every one of them loads. Nothing in `MainWindow.xaml` or `ThemeService` names a theme any more.

## Installer

`installer/Clearspace.iss` (Inno Setup) builds one setup file that installs, updates, repairs and
removes Clearspace. `Build Installer.cmd` publishes both programs self-contained first, so the setup
carries every dependency; the script refuses to build a payload that isn't.

The installer and uninstaller talk to the installed `Clearspace.exe` through three switches
(`Services/InstallerCommands.cs`). None opens the main window; each answers with its exit code:

- `--lock-count` — how many locked files and folders are on record.
- `--remove-all-locks` — shows only the *Remove all locks* dialog; `0` when nothing is locked afterwards.
- `--quit` — asks every running Clearspace to close over the same named pipe Explorer's commands use
  (`ShellVerb.Quit`). `LockAgent.Quit` locks again whatever is unlocked for a visit, then shuts down. The
  sender waits, and closes any process that didn't answer.

Setup uses `--quit` before replacing files (only when the installed version is 1.2.0 or newer; older
ones are closed by Windows). The uninstaller uses all three: it asks about locks only when the count is
above zero, then about keeping or removing the data folders. `VERSION` at the repository root is the
single version number: the installer reads it, and both project files stamp it into the executables.
`VERSIONING.md` next to it says what each part of the number means and when to raise it. <!-- NEW (versioning) -->
The dark look is Inno Setup's dark style with the Dark theme's `Base` color as `WizardBackColor`.

The logo (a folder with a C on it, no background) is drawn by `installer/make-icons.py` (Python with Pillow), which writes `Assets/Clearspace.ico`
at every size Windows asks for and the installer's two pictures. `docs/images/clearspace-logo.svg` is the
same drawing as a vector file. The lock icons (`LockedFile.ico`, `LockedFolder.ico`) are separate.

## Updates

`Services/UpdateService.cs` asks GitHub for the newest release of `Friday7352/Clearspace`
(`releases/latest`), compares its tag (`v1.3.0`) with the running version, and if it is newer keeps it
in `UpdateService.Latest`. `MainWindow.Updates.cs` runs that check quietly (a few seconds after start,
then every 6 hours, unless *Check for updates automatically* in the About window is off) and shows an
*Update available* chip in the status bar; Settings > *Check for updates…* and the button in
`AboutWindow.cs` (Settings > *About Clearspace…*: version, install and data folders, Windows and .NET
versions) run it on demand. `UpdateWindow.cs` shows the
release notes, downloads `ClearspaceSetup.exe` to `%LOCALAPPDATA%\Clearspace\Updates`, and refuses a
file whose size or SHA-256 differs from what GitHub lists for the asset. It then starts the installer
with `/SILENT /relaunch=1` and closes Clearspace through `LockAgent.Quit`; the installer's `[Run]`
entry with `Check: RelaunchRequested` opens Clearspace again.

Publishing an update therefore means: raise `VERSION` (by the rules in `VERSIONING.md`), run `Build Installer.cmd`, and create a GitHub
release tagged `v<VERSION>` with `release\ClearspaceSetup.exe` attached under that exact name. The tag
must match `VERSION`: a tag higher than the number built into the app would be offered again and again.

### Going back to an earlier version

<!-- NEW (go back) -->
`AboutWindow` > "Earlier versions…" opens `VersionsWindow.cs`, which asks GitHub for the list of releases
(`UpdateService.EarlierAsync`: `releases?per_page=50`, each read by the same `Parse` as the newest one)
and shows those older than the running version. Picking one opens `UpdateWindow` with `goingBack: true`:
other wording, no "Skip this version", no release notes. The download, the size and SHA-256 check and
`/SILENT /relaunch=1` are unchanged; every installer since 1.2.0 replaces a newer Clearspace when asked.

- **What may be offered.** An older Clearspace refuses a tag database newer than it knows, and that
  database also holds the lock records. So `Earlier` never goes below `UpdateService.OldestToGoBackTo`
  (1.2.0 today: the schema, the lock file format and the index format have not changed since, and it
  is the first version that can update itself). `TagDatabase.SchemaVersion` and
  `UpdateService.GoBackTagSchema` must be equal; `UpdateTests` fails when the schema number is raised
  without `OldestToGoBackTo` being moved to the release that ships it.
- **Before the old installer runs** (`UpdateService.PrepareToGoBack`): the version being left (or a newer
  one already found) is written as the skipped update, a setting every version since 1.2.0 reads, so the
  old version does not announce it the moment it starts; and "Open folders with Clearspace" is turned
  off when the target is older than `FolderOpenerSince` (1.4.0), because an older Clearspace would not
  understand the folder Windows hands it.
- A new feature that changes something in Windows that an older version cannot undo or answer needs the
  same treatment here: a "since" version and a step in `PrepareToGoBack`.
- Tests: `Clearspace.Tests/UpdateTests.cs` (which releases are listed, reading the list). Installing an
  earlier release is checked by hand.

## Settings and tags backup

<!-- NEW (backup) -->
Settings > "Back up settings and tags…" (`BackupWindow.cs`, with `RestoreBackupWindow` in the same file) backs up
what Clearspace itself keeps and puts it back. `Services/BackupService.cs` holds the rules and the file
format; `MainWindow.Backup.cs` has the menu entry and the timer for automatic backups.

- **The file.** `Clearspace backup <local date and time>.csbackup` is a ZIP with `manifest.json`
  (`BackupManifest`: format number, app version, time, kind, counts, a fingerprint of the contents, and
  `Note`, the label the person typed for it, if any),
  `settings.json` (`SettingsService.CurrentJson`, the settings as they are in memory) and `tags.json`
  (`TagStore.Export`; the layout of the old `tags.json`: `Definitions` and `Assignments`). It is written
  as `.part` and renamed, so a half-written backup never looks like a good one.
- **What is left out, on purpose.** Lock records live in the same database as the tags, which is why
  the database file itself is never copied. A locked file carries its key in its own header, and the
  record of what is locked or open right now must not be rolled back. `TagStore.Restore` touches the
  `Tags`, `Paths` and `PathTags` tables only. The search index is left out because it rebuilds itself.
- **Restoring tags** (`TagStore.Restore`, one transaction). A tag of the backup that still exists, by id
  or failing that by name, is used as it is now; a missing one is created with its old id, name and
  colour. Each item gets back the tags it had. `exact` first clears all tagging and deletes tags the
  backup does not have. `BackupService.ItemsToRestore` decides which items are passed in: those that
  still exist, and those on a drive or share that is not connected (nobody can tell, so they are kept).
  The disk is checked off the UI thread; the restore itself runs on it, because the window listens for
  `TagService.Changed`.
- **Restoring settings** (`SettingsService.ReplaceWith`). The backup's `settings.json` goes through the
  same `Read` as at start-up, replaces the settings in memory and is saved. The backup settings
  (schedule and its custom interval, folder, how many to keep, when the last one was) are carried over from today's, so
  restoring an old backup cannot switch automatic backups off. Most of Clearspace reads settings once,
  so the window offers a restart (a new `Clearspace.exe`, then `LockAgent.Quit`).
- **Undo.** Every restore first writes a backup of the current state, kind `BeforeRestore`.
- **Automatic backups.** `BackupService.IsDue` (daily: the last is 20 hours old; weekly: 6.5 days;
  <!-- NEW (custom schedule) --> custom: `SettingsData.BackupEveryHours` hours, from 1 hour to a year) is
  asked 25 seconds after a window opens and hourly after that. `RunAutomaticIfDueAsync` copies settings
  and tags on the UI thread and writes on another; only one runs at a time across windows. If the
  newest backup in the folder has the same fingerprint, nothing is written and only the clock is reset.
  `Prune` deletes automatic backups beyond the newest N and never any other kind. A failure (folder on
  a drive that is not connected) is kept in `LastAutomaticError` and shown in the window.
- <!-- NEW (backup label) --> **Labels.** `BackupService.Label` is what a row in the list is called: the
  manifest's `Note` when there is one, otherwise its kind ("Made by you", "Automatic", "Made before a
  restore"). The window's Label box starts as "Made by you"; left like that, no `Note` is written.
  `CleanNote` makes a label one line of at most 80 characters. Backups from before labels have no `Note`
  and are named by their kind.
- <!-- NEW (pick a file) --> **Restore from a file…** opens `FolderPickerWindow` with
  `FolderPickerOptions.FileExtension` set to `.csbackup`, not Windows' file picker (see Just arrived
  below for the chooser).
- **Reading other people's files.** `Read` refuses a backup whose format number is newer than this
  build's, and a part larger than 256 MB.
- Tests: `Clearspace.Tests/BackupTests.cs` (when a backup is due, labels, the file round trip, listing, thinning
  out, which items are restored, and tag restores against a database in memory). The window, the
  settings restore and the restart are checked by hand.

## Backup and restore (files, folders, drives)

<!-- NEW (file backup) -->
Settings > "Backup and restore…" opens `FileBackupView.cs`, a page shown in place of the explorer (the
`FileBackupHost` in `MainWindow.xaml`, opened and closed by `MainWindow.FileBackup.cs`). It is separate
from the settings and tags backup above and shares no code with it. The page and its dialogs
(`FileBackupEditWindow.cs`; `FileBackupRestoreWindow.cs`, which also holds `FileVersionsWindow`) are built
in code from `FileBackupUi` (end of `FileBackupView.cs`). Everything else is in `Services/FileBackup*.cs`
and opens no window:

| File | What is in it |
|---|---|
| `FileBackupPlan.cs` | `FileBackupPlan` (one backup), `FileBackupSchedule`, the names inside a backup's folder (`FileBackupLayout`), path helpers, the rules a plan must keep (`FileBackupRules`), `FileBackupStore` (what is kept on this PC), `FileBackupWords` (what the page says) |
| `FileBackupEngine.cs` | one run of a plan: the walk through the original, setting earlier versions aside, removing, copying, versions, zips, thinning out |
| `FileBackupRobocopy.cs` | the command robocopy is given, reading its log and exit code |
| `FileBackupRestore.cs` | what a plan can be restored from, restoring, the earlier versions of one file |
| `FileBackupRunner.cs` | the process that runs backups, and the files it and the window talk through |
| `FileBackupTask.cs` | the entry in Windows' Task Scheduler |

- **A plan** is a source (file, folder or drive), a destination folder, a kind, a schedule and what to
  keep. Clearspace makes one folder of its own per plan inside the destination (`plan.Root` =
  `Destination\FolderName`; the name is fixed when the plan is made, so renaming a plan loses nothing) and
  writes `Clearspace backup.json` into it (`FileBackupMarker`). **Nothing is ever written, moved or
  deleted outside that folder, and a folder is only used when it is missing, empty, or carries the
  plan's marker** (`FileBackupLayout.WhyNotRoot`). This is the rule that keeps a Clone from emptying
  somebody's folder. A marker of a lost plan with the same source and kind is taken over, so a backup
  set up again after a reinstall carries on in its old folder. The editor does not let the source,
  destination or kind of an existing plan be changed, for the same reason.
- **Inside the folder.** Clone and Add and update: `Files\` (the copy) and `Earlier versions\<date>\`
  (files a run replaced or removed, at the same place in the tree). Versions: one `<date>\` per run.
  Zip archive: `<name> <date>.zip`. Dated names are `yyyy-MM-dd HH.mm.ss`, local time.
- **Unchanged** means the same size and a modified date within two seconds (FAT and exFAT keep dates
  to two seconds; robocopy is given `/FFT` for the same reason).
- **Clone and Add and update** (`FileBackupEngine.Copy`), in this order:
  1. `SetAside` (only when the plan keeps earlier versions or removed files) walks `Files\` beside the
     original and moves what the copy is about to replace or what is gone from the original into
     `Earlier versions\<date of this run>\`. A move inside one drive is a rename.
  2. `Purge` (a Clone that does not keep removed files) deletes from `Files\` what is gone from the original.
  3. The copy: robocopy (`/E /COPY:DAT /DCOPY:DAT /MT:8 /R:1 /W:1 /XJ /FFT`, plus `/XA:O` and the
     left-out names), or `ManagedCopy` where `robocopy.exe` is not there. **Robocopy is never given
     `/PURGE` or `/MIR`.** What it would remove in an odd case (a cloud file that went online-only, a
     folder it could not list) is not something to find out from somebody's backup; removing is done
     by the two steps above, whose rules are tested.
  4. `PutBackUncopied`: a file that was set aside to be replaced but is not in `Files\` afterwards (its
     new version was open in a program) gets its earlier version back.
  5. `PruneHistory` deletes `Earlier versions\<date>\` folders older than the plan keeps them.
- **Rules of removing** (`SetAside`, `Purge`): a folder of the original that cannot be listed is skipped,
  never read as "everything in it is gone"; names the plan leaves out are not touched on either side; a
  cloud file that is online-only in the original keeps the copy the backup has; and a Clone whose
  original is completely empty while its copy is not refuses to run.
- **What is not copied** (`FileBackupWalk`, and the matching robocopy switches): links to other folders
  (`LinkTarget` tells them from cloud folders, as in Duplicates), cloud files that are not on the device
  unless the plan includes them, `System Volume Information`, `$RECYCLE.BIN` and the page files, and the
  plan's own left-out names (names with `*` and `?`, never paths: they are handed to robocopy as they
  are, and `FileBackupRules.Problem` refuses anything with a slash or a colon).
- **Versions.** A fingerprint (SHA-256 over every relative path, size and date, in a fixed order) is
  compared with the one in the marker; equal means no new version. Otherwise `<date>.partial\` is
  filled: a file that is unchanged from the newest version is hard-linked to it (`CreateHardLinkW`),
  anything else is copied, four files at a time; then the folder is renamed. Where hard links are not
  possible (FAT, exFAT, the 1023-links limit) the file is copied. Because versions share files,
  **changing a file inside a version changes it in every version that shares it**; nothing in
  Clearspace does. Versions beyond the newest N are deleted, which only removes names: the data goes
  when its last name does.
- **Zip archive.** The same fingerprint check, then `System.IO.Compression` (fastest level) into
  `…zip.cspart`, renamed when whole.
- <!-- NEW (resumable zip) --> **A zip is carried on with** (`Services/FileBackupZip.cs`,
  `FileBackupZipWriter`). A zip's list of contents (central directory) comes at its end, so a zip that is
  cut off before the end cannot be opened, and `ZipArchive` cannot add to one without holding new files
  in memory. So the zip is written in stretches: every 30 seconds (`Save`) the `ZipArchive` over the
  file is closed, the central-directory records it wrote for that stretch are read back (checked by
  signature and count) and appended to `…zip.dir.cspart`, the zip is cut back to the end of the packed
  files, both are flushed to the drive (`Flush(true)`), and only then a line with the two lengths is
  appended to `…zip.log.cspart`, after one line per packed file (size, modified ticks, where its record
  is). The next stretch is a new `ZipArchive` over the same stream at that position; the offsets it
  records are absolute. `Finish` writes the wanted records after the packed files, then the end records
  itself (`WriteEnd`; Zip64 when there are 65,535 entries or more, or 4 GB), opens the zip to check the
  count, renames it and deletes the two files beside it. `Resume` believes the notes up to their last
  whole "saved" line, refuses anything that does not fit (file shorter than noted, a record that does
  not start with the directory signature, another header) and cuts the three files back to what the
  notes say. Nothing carried over is in the finished zip unless the run's walk asked for it with
  `Keep` (same size and modified ticks) or `KeepFolder`; a changed file is packed again and the older
  record is dropped, a deleted file's record is never written. Their packed bytes stay in the file as
  unused space. A file that cannot be read to its end, or a Stop in the middle of one, ends the stretch
  without that file and cuts its bytes off. `MakeZip` carries on with the newest unfinished zip that
  `Resume` accepts and clears away any other `*.cspart`; a zip that fails its own checks
  (`InvalidDataException`) is discarded and made again by the next run. The finished zips were checked
  with Python's `zipfile` and Info-ZIP's `unzip -t` as well, including one of 9 GB with a file over 4 GB,
  one with 70,000 entries and one carried on with after changes.
- <!-- NEW (backup contents) --> **What suits what is backed up** (`Services/FileBackupContents.cs`).
  The editor's "Contents" row has four buttons: Documents, Photos & media, Projects, A mix. `Advise`
  gives the options for one: Documents keep the last 30 runs; Photos & media keep one copy (Clone) with
  removed and changed files kept 90 days; Projects keep the last 10 runs and skip the folders their tools
  rebuild (`Rebuilt`, by the tools found: Unity, .NET, Node, Python, Unreal, Godot; never `bin` or
  `Build`, which can hold what nobody can rebuild); A mix keeps one copy with removed files kept 30 days.
  Where dated copies cannot share unchanged files (not NTFS or ReFS, asked through
  `DriveInfo.DriveFormat`), Documents and Projects get a Clone with file history instead. `Apply` sets a
  plan up from it: never zipped, daily unless a schedule is on or was chosen by hand, and the skip list
  merged (the previous recommendation's names out, typed names kept). `Detect` chooses the button: a
  whole drive is A mix; a folder is Projects when it is a project (`Project`, from the names straight in
  it: `Assets` + `ProjectSettings`, `*.sln`, `package.json`, `.git`, …) or at least half of its folders
  are or hold one; otherwise whatever more than half of a sample of its files is by extension (`Guess`).
  It looks three folders deep, at 300 folders and 2,000 files at most, for two seconds at most, never
  into a project, and opens no file. The editor runs it off the UI thread when the source changes
  (450 ms after the last keystroke) and applies the result until a Contents button, or an option a
  recommendation sets, is chosen by hand. This replaced the "Protects against" row
  (`FileBackupGoals.cs`, removed). What is left of it is the line under "Save to" when the backup would
  be on the drive it backs up (`FileBackupPaths.OnSameDrive`).
- <!-- NEW (retention) --> **Retention.** A Versions or Zip archive plan keeps its newest `KeepCount`
  runs, or, when `MaxAgeDays` is above 0, every run of the last so many days (`FileBackupEngine.Expired`;
  `ThinOut` deletes them after each run, also after a run that made nothing new, so age goes on). The
  newest run is never deleted. In the editor this is the "Keep" row, always on show (`FileBackupRetention`:
  Latest only, Last 5 / 10 / 30, 30 days / 90 days / 1 year), and together with the zip tick box it decides
  a new plan's kind (`FileBackupRules.Shape`): latest only and not zipped is a Clone (or Add and update,
  chosen under More options), more than the latest and not zipped is Versions, zipped is Zip archive.
  An existing plan keeps its kind; for Versions and Zip archive the row changes how many runs are kept
  (`SetRetention`), for the other two it reads "Latest only". Retention covers every run of a backup,
  manual ones included (unlike the settings and tags backup, where only automatic ones are thinned out).
  What a Clone keeps besides its copy (removed files, earlier versions, for how long) is "Also keep"
  under More options.
- <!-- NEW (zip tick box, simpler editor) --> **The editor** (`FileBackupEditWindow.cs`) shows what to back
  up, where to, a name, how often and one tick box, "Zip to save space"; the kind, what is kept and the
  names to skip are behind "More options", and explanations are tooltips. The tick box is the Zip archive
  kind under another name (`FileBackupRules.Shape`; with "Latest only" it keeps one zip, and the new zip
  is written whole before the old one is deleted, so there is always one good zip). The page's cards follow the same
  rule of few words: `FileBackupWords.Status` and `Summary` are one short line each, and what a backup
  keeps (`Keeps`) is a tooltip.
- **Files are never half written under their real name** by Clearspace's own code: every file it
  copies or unpacks, and every zip, is written as `name.cspart` and renamed. robocopy (Clone, Add and
  update) writes under the real name; a file it was in the middle of when the run was cut is copied
  again by the next run, because its size or date differs.
- **Who runs a backup.** Never the window. "Back up now" starts `Clearspace.exe --run-backup <id>`; what
  is due is run by `Clearspace.exe --run-backups`, started by the window's once-a-minute check
  (`MainWindow.CheckFileBackups`) or by Task Scheduler. `App.OnStartup` hands both switches to
  `FileBackupRunner.Run` before anything else and exits. So a backup carries on when the window closes,
  and there is one code path. The processes talk through files in `%APPDATA%\Clearspace\File backups\state`:
  `<id>.lock` (held open with no sharing while the plan runs, deleted on close: "is it running?" is
  whether the file can be opened), `<id>.progress.json` (rewritten every 400 ms), `<id>.cancel` (the Stop
  button), `<id>.json` (`FileBackupState`, how the last run went), `runner.lock` (one process runs what is
  due) and `stop-all` (written by `--quit`, honoured for three minutes: an update or uninstall is
  replacing the program). The page reads these off the UI thread every 1.2 seconds. A progress file
  with nobody holding the lock means the run was cut short, which the page says.
- **Plans are not in `settings.json`** but in `File backups\backups.json`, because two processes read
  them and `settings.json` is owned by the window's process (which writes it as a whole from memory).
  They are therefore not part of the settings and tags backup yet.
- **When a plan is due** (`FileBackupSchedule.IsDue`): never ran, or the interval has passed (every
  hour, custom), or the set time of day has come since the last run started (daily, weekly: a PC that
  was off catches up), or the last run failed and an hour has passed. A due plan whose source or
  destination is not there is not run and not failed: its state says `Waiting`, and it runs when a
  later check finds both (`FileBackupRunner.Due`).
- **Task Scheduler** (`FileBackupTask`). While "Run scheduled backups when Clearspace is closed" is on
  and a plan has a schedule, one task named `Clearspace backups (<user>)` runs
  `Clearspace.exe --run-backups` every 15 minutes as the signed-in user, with ordinary rights, also on
  battery, with no time limit and never twice at once. It is registered with `schtasks /Create /XML`
  (no administrator rights are needed for a task that runs as oneself). `Sync` keeps it in step and
  notes what it registered in `File backups\task.txt`, so Task Scheduler is only asked when something
  changed. It is removed by `ShellIntegration.Unregister` (the uninstaller) and by
  `UpdateService.PrepareToGoBack` when going back past `UpdateService.FileBackupsSince`, since an older
  Clearspace would open a window every 15 minutes. **`FileBackupsSince` must be the number of the
  release that first ships this** (1.6.0 as written).
- **Restoring** (`FileBackupRestore.Restore`) only adds or replaces: a file that is there and the same
  is skipped, one that differs is replaced only when asked, nothing is deleted, and a zip entry that
  would land outside the folder is refused (`Inside`), as in `ArchiveService`.
- **Earlier versions of one file.** `VersionsOf` looks in every plan that covers the path (`Files\`,
  each `Earlier versions\<date>\`, each version, each zip's entry list), keeps one line per different
  state and drops the state the file is in now. The menu entry is decided without the disk
  (`Offers`: a plan with file history covers the path). `FileVersionsWindow` restores by moving the
  current file to the Recycle Bin first (`FileOperationService.Delete`); if it is still there afterwards,
  nothing is replaced.
- <!-- NEW (backup drives) --> **Drives on offer** (`Services/FileBackupDrives.cs`). Under "Save to" the
  editor shows up to four buttons. `Look` (off the UI thread) reads the connected fixed and removable
  drives, finds backups on each by their marker at the top of the drive and one level down
  (`FindBackups`; plus the folders of the plans this PC knows), and asks `DriveHardwareProbe` whether a
  fixed drive is on USB. `Suggest` (no disk, tested) orders them: drives with backups first, any drive
  but the one being backed up, offering the folder most of those backups are in (`FolderFor`); then
  external drives without backups, never the Windows drive, offering `<drive>\Backups`. That folder is
  made on Save, and only because Clearspace offered it (`FileBackupRules.Prepare(createDestination)`); a
  typed folder that is missing is still refused. The two sorts look different on purpose (accent symbol,
  bold name and a count; plain symbol and free room). The buttons are rebuilt on `DriveWatch.Changed`.
  Network drives are not looked at.
- **Pickers.** "What to back up" and "Where to keep it" use `FolderPickerWindow`. For picking any file,
  `FolderPickerOptions.FileExtension` may now be `""`: every file is listed, under the folders.
- Known limits: robocopy's words are localized, so its log is read by shape (a line ending in a path
  under the source is a copied file, a line with `(0x…)` is an error); if a Windows version writes the
  log differently the page says "New and changed files were copied" without a count. A backup that runs
  while a password-locked file is unlocked for a visit copies it unlocked. A file that is open for
  writing in another program is reported and skipped (no shadow copies). A drive is copied file by
  file, not as an image.
- Tests: `Clearspace.Tests/FileBackupTests.cs`, with Clearspace's own copy between temporary folders;
  one test starts the real robocopy on Windows. The page, the dialogs, starting the second process and
  Task Scheduler are checked by hand.

## Live drives

<!-- NEW (live drives) -->
The sidebar and the This PC page read the drives when a window opened (and after mapping a network
drive), so a USB stick plugged in later did not show. Now they follow what is connected.

- **Noticing.** Windows sends every top-level window `WM_DEVICECHANGE` when hardware changes; for drives
  no registration is needed. `MainWindow.Drives.cs` hooks the window's messages (`HwndSource.AddHook`) and
  only passes that one on to `DriveWatch.DeviceChanged` (`Services/DriveWatch.cs`).
- **Looking.** The message arrives in bursts and often before the drive can be read, so nothing is read
  out of it. Half a second after the last one, `DriveWatch` reads the ready drives on a timer thread
  (`Read`: fixed, removable and optical; **not network drives**, whose readiness can take seconds to ask
  and belongs to `NetworkDrives`) and compares them with the list before (`Diff`: arrived, removed,
  renamed). A look that finds nothing is repeated up to twice, 1.5 seconds apart, for a drive that is
  slow to come up. A change is raised as `DriveWatch.Changed` on the UI thread.
- **Acting** (`MainWindow.OnDrivesPluggedOrUnplugged`). Once for all windows:
  `SidebarViewModel.ReloadDrivesEverywhere` and, when a drive arrived, the check for file backups that
  are due (one may have been waiting for that drive). Per window: tabs on a drive that is gone step out
  to This PC (`ExplorerTabs.MoveOut`, after `Directory.Exists` has confirmed it is gone), a drives page on
  screen reloads, and the status line says what was connected or removed. `DriveHardwareProbe.Forget`
  drops what was known about the letter.
- **Not done:** the file index is not told. A drive plugged in mid-session is searched by reading it, as
  before, and indexed from the next start. Indexing on arrival would hold handles on the drive and get
  in the way of "Safely remove"; that needs `DBT_DEVICEQUERYREMOVE` handling first. For the same reason
  a drive whose folder is on screen (watched by `FolderWatcher`) may refuse to eject until the tab
  leaves it, as with File Explorer.
- Tests: `Clearspace.Tests/DriveWatchTests.cs` (`Diff`). The message hook and the timing are checked by hand.

## Tabs

<!-- NEW (tabs) -->
There is one file list and one `MainViewModel`, however many tabs are open. A tab
(`ViewModels/ExplorerTabs.cs`) is a `NavigationHistory` (its own back / forward list) plus what the
window puts aside for it while it is in the background: search text, scroll position and selection.

- `NavigationService` works on whichever history is attached. Switching tabs calls `Attach`, which raises
  `Navigated` like any other move, so the tab's folder loads through `MainViewModel.LoadAsync` and comes
  straight from `FolderSnapshotCache` when it was shown recently. A background tab costs nothing: nothing
  is loaded, watched or kept in memory for it beyond its history.
- `ExplorerTabs` (open, close, switch, reorder, duplicate, reopen) has no WPF in it and is covered by
  `Clearspace.Tests/TabTests.cs`.
- `MainWindow.Tabs.cs` is the mouse and keyboard side, and saves and restores each tab's scroll position
  and selection. The strip is XAML at the top of `MainWindow.xaml`, laid out by `Controls/TabStripPanel.cs`.
- The tab commands are `IAction`s (`Commands/Actions/TabActions.cs`): Ctrl+T, Ctrl+W, Ctrl+Tab,
  Ctrl+Shift+Tab, Ctrl+Shift+T. `MainWindow.TryHandleTabKey` runs them before the "typing in a text box"
  check, so they work from the search and address boxes, and handles Ctrl+1 ... Ctrl+9 itself.
- Locked folders: `NavigationService.MayEnter` puts the password gate in front of a tab that sits in a
  locked folder before it is shown. `LockAgent` counts the folder of every tab that has been shown
  (`MainWindow.ShownLocations`) when it decides what to lock again, and `MainWindow.LeaveFolder` moves
  every tab out of a folder that is being locked.

What a tab does not keep while it is in the background: a folder load or a search in progress. Both are
cancelled when you leave the tab and started again when you come back (the search text is kept).

## More than one window

<!-- NEW (new window) -->
"Open in new window" and Ctrl+N open another `MainWindow` in the same process (`MainWindow.Windows.cs`),
never a second `Clearspace.exe`. That is what keeps a second window cheap: the file index, the
`FolderSnapshotCache`, the icon and thumbnail caches, the tag database and `LockAgent` are static and
exist once per process. A window owns only its controls, its `MainViewModel` (tabs, history, the listing
on screen), its sidebar, music player and photo viewer. Starting `Clearspace.exe` again from a shortcut
is still a separate process with its own copy of the index.

- `LockAgent` keeps every open window (`Attach` / `Activated` / `Detach`); `Window` is the one used last,
  which is where password prompts attach and messages go. Clearspace exits when the last window closes
  (and nothing unlocked for a visit is left to lock again).
- `SidebarViewModel` rebuilds every window's sidebar when one of them changes a pin, a category or Your
  files, and `MainViewModel.Dispose` stops a closed window's music and takes its sidebar off that list.
- The E-reader screen effect is one per window (`EInkScreen.CreateForWindow`), so a page turn in one
  window does not show over another.
- A tab can leave its window for one of its own: drag it off the tab strip and let go, or "Move to new
  window" in its menu (`MainWindow.MoveTabToNewWindow`). `ExplorerTabs.Detach` takes it out without
  counting it as closed, and the new window's `NavigationService` starts on the tab's own
  `NavigationHistory`, so back / forward, search, scroll position and selection all come along. There
  is no dropping a tab into another window yet.
- Known limit: `ThumbnailService.CancelPending` and `MediaPropertyService.CancelPending` are process-wide.
  Opening a folder in one window drops tile pictures another window had asked for but not yet received;
  they come back when those tiles are scrolled or the folder is refreshed.

## Your files

<!-- NEW (your files) -->
The Your files page and the sidebar section of the same name show Windows' six folders
(`LocationCatalog.BuildUserFileEntries`) followed by folders the user added. Those are a plain list of
paths in `settings.json` (`SettingsData.LibraryFolders`, list logic in `Services/LibraryFolders.cs`,
tests in `Clearspace.Tests/YourFilesTests.cs`). On that page New folder (toolbar, Ctrl+Shift+N, or the
empty-area menu) creates a folder in the user folder, adds it to the list and starts renaming it;
"Add a folder you already have" lists an existing one. Renaming a listed folder in Clearspace keeps it
listed (`MainViewModel.ApplyRename`); deleting it drops it the next time the page loads (a folder whose
whole drive is missing is kept). "Remove from Your files" only takes it off the list.

## Date order

<!-- NEW (date order) -->
`ItemComparer` lists folders before files whatever the sort. A folder type that sorts by date modified
and groups by day (Downloads, Screenshots, Archives & Backups) turns that off
(`MainViewModel.FoldersFirst`, passed to the loader as `FolderLoadOptions.FoldersFirst`): folders and
files are sorted together, so the newest thing is first and the day groups come out in order (Today,
Yesterday ...). Before this, a Downloads folder with 400 subfolders showed all of them above today's
download. Sorting such a folder by another column puts folders first again.

## Search upkeep and the search trace

<!-- NEW (search fix, search trace) -->
`IndexOverlay` remembers the changes the watcher has seen that the index has not applied yet. It used to
remember them for as long as Clearspace was open, which hurt search in two ways:

- `FileIndexService.Search` looks up every remembered new path on the disk (they are not in the index
  yet), on each key press. The list only grew.
- More than 256 remembered deletions marked the drive as lost (`Overflowed`). A lost drive is not
  "covered", so `SearchCoordinator` crawls it from the disk until the change journal catch-up recovers it.
  A few minutes of temporary files were enough, so this kept happening.

`FileIndexUpdater.Handled` now reports every path of a batch it has dealt with, and
`IndexOverlay.Settle` forgets those paths. The overlay holds a fraction of a second of changes; the caps
are only reached when the index really cannot keep up. `Search` also looks up at most 500 new paths per
search, and `SearchSources.FinishIndexAsync` (icons, tags and the does-it-still-exist check for up to
10,000 results) stops when the search has been replaced by a newer one.

`Services/SearchTrace.cs` writes `%APPDATA%\Clearspace\search-trace.log`. `SearchCoordinator` times its
stages (first results, the wait before the index is asked, resolving the places to search, the index
scan with `FileIndexService.LastSearchDetail`, sorting, the checks, Windows Search, the crawl, and the
time spent handing results to the window) and writes one line for a search of 1.5 s or more. A drive
dropping out of, and returning to, live coverage is written too, with the reason. No search text is
recorded. A "wait" far longer than was asked for means the UI thread or the thread pool was busy.

The first trace from a real search settled where the time went: index scan 46 ms, sort 16 ms, showing
6 ms, "checks" 50,659 ms, for 10,000 results - about 5 ms a result. That stage (`FinishIndexAsync`) did
three things for every result, and that trace did not time them apart, so all three were taken out and
the trace now does time them apart ("icons, types and tags ... ms, on-disk checks ... ms"):

1. `FileIndexService.PruneMissing` asked the disk whether the result still exists. On a hard disk that
   is a seek, about 5 ms, which is the likeliest reading of the figure; and a backlog of seeks stalls
   everything else that touches the drive, the window's own calls included.
2. `FileSystemItem.RefreshTags` asked the tag database, in a transaction, whether anything had changed.
3. Every folder among the results asked Windows where Desktop, Documents, Downloads, Pictures, Music and
   Videos are (`FolderIconService.ResolveKind`), six questions that Windows answers by checking each
   folder is there, plus one more check of Downloads on the disk.

- **Results on a live drive are not checked on the disk.** The watcher keeps a live drive's index right,
  and `IndexOverlay.IsRemoved` covers the moment in between. Only results from a drive that is still
  being caught up (they come from an older snapshot) are checked, and of those the top
  `MaxExistenceChecks` (1,000; results arrive best first).
- **The index scan leaves two cores free** (`IndexSearch.ScanThreads`). On all cores it competed with the
  UI thread and delayed thread-pool timers (a 35 ms timer measured 600 ms late under four scans).
- **Tags are read once per list.** `TagStore.Current()` asks SQLite `PRAGMA data_version` in a transaction
  under the store's lock on every call, and `FileSystemItem.RefreshTags` called it per row: every row of
  a folder load and every search result. The UI thread needs the same lock to parse what was typed.
  `TagStore.Lookup()` takes the snapshot once; `NavigationCoordinator.Prepare`,
  `SearchSources.Populate` and `MainViewModel.RefreshVisibleTags` pass it to every row.

- **The Windows folders are remembered for 30 seconds** (`KnownFolders.Remember`). A folder list of any
  size asks Windows once, on a folder load as well as a search. A folder moved in Windows is picked up
  within that time.

### Typing in the search box

<!-- NEW (search fix) -->
With the checks gone no search took 1.5 s any more, but typing still stuttered. The next trace had the
window not responding for 1.1 s and 5.9 s, each ending just after results were handed to it, with no key
press and no hand-over taking 150 ms of the search's own code. What that leaves is what WPF does after
`Items` is replaced: the `ListView` throws away every row container and builds the rows on screen again
(a new `ItemsSource` is a reset, and a reset empties the recycling queue as well), and the letter just
typed is drawn in the same pass, so it shows only when the rows are done. One finished search replaced
the list four times: this folder's matches on the key press itself, "Searching..." after the wait, the
index's answer, and the finished status. Two of the four held the same rows as the one before. How long
one rebuild takes on a given PC was not measured by that trace; `SearchTrace.ListDrawn` now writes it.

- **Nothing is handed to the window on the key press.** `SearchCoordinator` waits `TypingPause` (150 ms)
  first; a newer key press cancels the wait, so typing faster than that leaves the list alone.
- **This folder's matches wait for the index** (`HoldForIndex`, 120 ms) when the index covers every
  place searched, and go to the list together with its answer. If the index is slower they are shown
  without it. A place that has to be read from the disk shows them straight away, as before.
- **The same rows are handed over as the same array** (`SameRows`). `MainViewModel.Items` then does not
  change and the list is not rebuilt. A search the index answers now replaces the list once.
- **The list's grouping is not touched on each letter.** `SearchText` used to announce `ListGrouping`,
  which took the date or kind groups off the folder's list (a reset of its own) a moment before the
  results replaced it. `MainWindow.ApplyGrouping` runs whenever `ItemsSource` changes, which covers
  starting and clearing a search.

The cost is that results follow the last letter by about a fifth of a second instead of appearing with
it. `TypingPause` is the one number to change if that trade should go the other way.

`SearchTrace.WatchWindow` runs a background thread that posts a no-op to the dispatcher five times a
second; an answer later than 400 ms is written as "the window did not respond for N ms". While it waits
it looks every 20 ms at what the UI thread is doing and adds that up in the line ("busy with: laying out
and drawing the window 3,100 ms, input: TextInput 600 ms, ..."). The label comes from the window's own
events, each of which costs one field write: `Dispatcher.Hooks` (the queued task that is running, named
through `DispatcherOperation.Name` only when sampled), `ComponentDispatcher.ThreadPreprocessMessage`
(the Windows message), `InputManager.PreProcessInput` (the input event), and `SearchTrace.Step` /
`SearchTrace.During` around the search's own UI-thread work. The line also has the time the garbage
collector stopped the app, the number of times the list was replaced, and the last search step. A key
press whose own handling took 150 ms or more is written with its parts, and so is a list that took
50 ms or more to lay out after being replaced (`MainWindow` times `ItemsSource` changed to the next
`LayoutUpdated`). The log file itself is written from the thread pool.

## Live folder

<!-- NEW (live folder) -->
The folder on screen is watched, so files other programs put there (a download finishing) appear without
F5. It used to be read on navigation only.

- `Services/FolderWatcher.cs` wraps a `FileSystemWatcher` on that one folder (not its subfolders) and
  only says "something changed": 350 ms after the changes go quiet, and every 2 seconds at most while
  they keep coming (a file that is still downloading). `MainViewModel.WatchFolderOnScreen` points it at
  the folder each time one is loaded, off the UI thread, and at nothing on pages that are not folders.
- `MainViewModel.MergeFolderChangesAsync` then reads the folder again in the background
  (`NavigationCoordinator.ReadChangesAsync`) and `Services/FolderMerge.cs` works out the difference. Rows
  that did not change stay the same objects, so the selection, the scroll position and the tile pictures
  are untouched; only new or changed rows are prepared (icons, tags, lock badges). Reading the folder
  again, instead of trusting each event, also covers the events Windows drops when too many arrive.
- A folder row is not replaced because its date changed (that happens whenever anything inside it does),
  so the date shown for a subfolder is as of the last load.
- While a search is showing, the change is remembered and merged when the search is cleared. A change
  that arrives during a load waits for it (`FolderWatcher.Nudge`). A background tab is not watched; it is
  read again when you return to it.
- Tests: `Clearspace.Tests/LiveFolderTests.cs`.

## Batch rename

<!-- NEW (batch rename) -->
Rename (F2) with several items selected opens `BatchRenameWindow.cs` instead of the single-name box
(`RenameAction` hands the selection to `ExplorerContext.BeginBatchRename`; `MainWindow.BatchRename.cs`
puts the items in list order, so `{n}` counts the way the list reads).

- `Services/BatchRename.cs` has no windows in it. `Plan` turns each item into a `RenameStep` (old name, new
  name, and a `Problem` when the name can't be used); `Apply` renames what the plan changes. Tests:
  `Clearspace.Tests/BatchRenameTests.cs`.
- The ending is kept, and a locked file keeps both of its endings (`photo.jpg.cslock`), so it is still a
  `.jpg` once unlocked.
- A new name that another item in the batch still has (a swap, 1 -> 2 while 2 -> 3, or only the capitals
  changing) can't be taken directly, so every item first steps aside to a temporary name. Any failure
  undoes the renames already made, in reverse order.
- The dialog reads the folders involved once when it opens and checks new names against that list, not
  the disk, on each key press.
- Afterwards `MainViewModel.ApplyBatchRenameAsync` moves the items' tags and their places in Your files,
  then reads the folder again.

## Archives

<!-- NEW (archives) -->
`Services/ArchiveService.cs` unpacks and makes `.zip` files with .NET's own `System.IO.Compression`
(nothing to install; no 7z or RAR). `MainWindow.Archives.cs` adds the right-click entries and
`ProgressWindow.cs` shows the progress and holds the Cancel button. Tests: `Clearspace.Tests/ArchiveTests.cs`.

- Extract always makes a new folder (`FolderFor`: "name", then "name (2)" ...), so it never overwrites.
- `DestinationFor` refuses any entry whose name would land outside that folder (`..\..\Windows\x`, a
  drive letter). An archive's entry names are untrusted text.
- A file is never allowed to unpack to more than the archive said it would, and the free space is checked
  before anything is written.
- Compress does not follow links and junctions, and opens the new `.zip` with `CreateNew`, so an existing
  file is never replaced.
- Stopping or failing deletes the new folder, or the unfinished `.zip`.

### Opening a ZIP like a folder

<!-- NEW (zip folders) -->
A `.zip` opens in the list (`OpenItemAction`), read-only. Tests: `Clearspace.Tests/ArchiveBrowseTests.cs`.

- **The address.** A place inside a ZIP is `clearspace://zip/<the ZIP's path>/<path inside>`
  (`Services/ArchiveLocation.cs`): the ZIP's own path with backslashes, then the path inside it with
  `/`, so the first `/` after the prefix is always where the ZIP ends. It was made a `clearspace://`
  address on purpose. Everything that asks "is this a folder on a disk?" (`Path.IsPathFullyQualified`, a
  `clearspace://` check, `Directory.Exists`) already says no for the other pages, so the folder watcher,
  tags, locks, pinning, backups, the shell file operations and the right-click entries leave the inside
  of a ZIP alone without each being told to. A path that looked real (`C:\a.zip\Trip`) would have passed
  those checks. People only see the path form: `ArchiveLocation.Display` for the address bar, tab tooltip
  and Copy path, `FromTyped` for what is typed or passed on the command line.
- **Navigation.** `NavigationService.CanGoUp`/`GoUp` and `NavigationBreadcrumbs` go through
  `ArchiveLocation.Parent`: up inside the ZIP, then out to the folder the ZIP file is in.
  `MainViewModel.LoadAsync` has one more branch (`LoadArchiveAsync`) beside the drives pages and the hubs.
- **Reading.** `Services/ArchiveBrowser.cs` reads the ZIP's central directory once and keeps the result
  for the last four ZIP files, keyed by path, size and date (a ZIP that changed is read again). Folders
  are worked out from entry names, since many ZIPs have no entries for them. Entries with a `.` or `..`
  part are dropped there, so they can't be listed, opened or extracted. The list is not live: F5 reads again.
- **Items.** `FileSystemItem.FromArchiveEntry`; `IsInArchive` is true for them. `RefreshTags`,
  `RefreshLock`, `ThumbnailService.Request` and `MediaPropertyService` skip them, and `IconService` asks
  for their large icon by kind instead of by path.
- **Read-only.** `ExplorerContext.SelectionInArchive` turns off Delete, Rename and Cut; Paste and New
  folder are off because the address is not a folder. Drops onto rows inside a ZIP are refused in
  `ResolveFileDropEffects`.
- **Getting things out.** `ArchiveBrowser.Extract` writes chosen entries into a real folder with the same
  rules as `ArchiveService.Extract` (never overwrite, stay inside the folder, stop at the size the ZIP
  claimed, clean up on failure), and makes names from other systems usable (`SafeName`).
  `ArchiveBrowser.Copies` writes them under `%TEMP%\Clearspace\Zip` instead: `Open\…` for a double-click
  and the preview pane (read-only files, reused while they match the ZIP), `Copy\…` for Ctrl+C and
  dragging out. `ExplorerContext.ArchiveCopies` is how the actions ask the window for them
  (`MainWindow.Archives.cs`, `CopyOutOfArchive`): up to 48 MB or 300 files straight away, more behind
  `ProgressWindow`; a drag never waits behind a window, so a large one is refused with a line in the status bar.
- **The download mark.** A copy made for opening gets the ZIP's `Zone.Identifier` zone (`ZoneMark`), so
  SmartScreen still asks about a program run straight from a downloaded ZIP. Extracted and copied-out
  files are not marked, which is what `ArchiveService.Extract` has always done: `FileLockSafety.CheckStreams`
  refuses to lock a file with a second data stream.
- **Locked folders.** A tab inside a ZIP counts as being where the ZIP file is
  (`ArchiveLocation.OnDisk`): the password gate, "is anyone still inside?" in `LockAgent`, and moving
  tabs out when a folder is locked again all use it. Each folder of temporary copies has a `source.txt`
  naming its ZIP; `FileLockService.RunFolder` calls `ArchiveBrowser.RemoveCopiesFrom` when it locks a
  folder, so copies of what the folder protects do not stay in the temporary folder. Other copies are
  removed a day after they were last used (`ArchiveBrowser.TidyLater`, at start-up).
- **Not done.** Changing a ZIP (rename, delete, paste into), tile pictures for what is inside one,
  searching below the folder on screen, other formats, and password-protected entries.

## Preview pane

<!-- NEW (preview pane) -->
Columns 3 and 4 of the main grid in `MainWindow.xaml` are the pane's splitter and the pane; both are 0
wide until `MainWindow.Preview.cs` shows it (Alt+P, `TogglePreviewPaneAction`). `Controls/PreviewPane.cs`
is made the first time the pane is shown and decides what to draw:

- a `.zip`: `ArchiveService.List`
- <!-- NEW (zip folders) --> a file inside a ZIP that is open in the list: a copy is taken out first
  (`ArchiveBrowser.CopyForOpening`, up to 32 MB) and previewed like any file
- a picture, video or document type the tiles already draw (`MediaTypes.IsThumbnailCandidate`), a folder,
  and the fallback for everything else: `ThumbnailService.PreviewAsync`, which runs the same `Extract` a
  tile uses, at a large size, on a short-lived STA thread of its own
- any other file: `TextPreview.Read` looks at the first 48 KB and shows it when it is text (no zero
  bytes, next to no control characters, or a byte-order mark). No list of file endings is kept.

A load starts 140 ms after the selection settles and its result is dropped if the selection has moved
on. Cloud files that are not on the device and locked files are not read.

## Folder opener

<!-- NEW (folder opener) -->
Settings > "Open folders with Clearspace" (`MainWindow.FolderOpener.cs`, `ShellIntegration.SetFolderOpener`)
writes, under `HKCU\Software\Classes`:

- `Directory\shell` and `Drive\shell`: the default value names the verb `Clearspace.OpenFolder`, whose
  command is `Clearspace.exe --open "%1"`, the same command as the "Open in Clearspace" entry. The value
  that was the default before is kept in the verb's key and put back when the setting is turned off.
- `CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow\command` (what Win+E and the
  taskbar's File Explorer button start): the same command for the user folder, with an empty
  `DelegateExecute` so Windows uses the command.

`ShellIntegration.Register`, which runs at every start, refreshes the paths if Clearspace has moved, and
`Unregister` (the uninstaller's `--unregister-shell`) turns the setting off. `ShellCommands.Parse` puts
the backslash back on a drive, which arrives as `C:"` because of how Windows splits `"C:\"`.

## Duplicates

<!-- NEW (duplicates) -->
`DuplicatesView.cs` is a page shown in place of the explorer (the `DuplicatesHost` in `MainWindow.xaml`,
opened and closed by `MainWindow.Duplicates.cs`), built in code like the dialogs. The search is
`Services/DuplicateFinder.cs`; tests: `Clearspace.Tests/DuplicateTests.cs`.

- Three steps, each only for files the step before could not tell apart: same size (no file is opened),
  same first 64 KB, same whole file. The comparisons use SHA-256.
- The first step takes its list of files from the file index where it can (`Services/DuplicateIndex.cs`,
  passed to `Find` as `fromIndex`): one pass over the volume's entries, keeping the files under the
  folder (membership is worked out up the parent chain and remembered per folder) and returning only
  those whose size occurs more than once. It returns null, and the folders are read instead, when
  `FileIndexService.Covers` is false, the volume is still being built, or the folder is not in the index.
- Whatever the list came from, `Hash` checks each file as it opens it: a size that differs from the list
  drops the file, and the modified date is taken from the open handle. So a stale index can only make
  the search miss a copy, never report one that is not there.
- Not candidates: empty files, links (symbolic links, junctions, mounted drives; `LinkTarget` tells them
  from cloud folders, which are reparse points too and are searched), cloud files that are not on the
  device (`Offline` / recall attributes), hidden and system entries unless hidden items are shown. Two
  hard links to one file count as one copy, since removing one frees nothing.
- The view never offers to remove the last copy of a group (`CanTick`), and `Removable` checks each ticked
  file again at the moment of removing: its size and date must be what the search saw, and an unticked
  copy must still be there. Removal is `FileOperationService.Delete` (the Recycle Bin).
- The view lists 60 groups at a time ("Show more"); the tick state lives in a set of paths, so the "keep
  the newest / oldest of each" buttons cover the groups that are not listed yet.

## Downloads folder

<!-- NEW (downloads) -->
`MainWindow.Downloads.cs` adds four things to a folder whose resolved type has the Downloads base
(`MainViewModel.IsDownloadsFolder`). The strip's frame is the `ArrivalsStrip` border in `MainWindow.xaml`
(row 0 of the content card, shared with the hub banner and the project strip); its content is built in
code. Tests for all four: `Clearspace.Tests/DownloadsTests.cs`.

- **Just arrived** (`Services/JustArrived.cs`): the newest five files of the last week, by the later of
  the created and modified dates. Rebuilt when `Items` changes, but only if what it would show differs
  (a key of the picks and the folder's setup files), so a growing download does not redraw it.
  <!-- NEW (drag from just arrived) --> A tile can be dragged out as its file (`MainWindow.DragArrival`):
  the same `FileDrop` data a drag from the list carries, so other programs and Clearspace's own drop
  targets treat it alike. Checked by hand.
- **Where a file came from** (`Services/DownloadOrigin.cs`): the page (`ReferrerUrl`) and the file's own
  address (`HostUrl`) from the `Zone.Identifier` stream. Only `http` and `https` addresses are accepted,
  because the address is handed to the shell to open; the file's own address is only ever copied, never
  opened.
- <!-- NEW (move in Clearspace) --> **Move to a folder…** on a tile opens `FolderPickerWindow.cs`,
  Clearspace's own folder chooser, not Windows' picker: the window's sidebar entries as places, the
  folders of the open place (`ReadFolders`, tests in `Clearspace.Tests/FolderPickerTests.cs`), a path
  box that can be typed in, and New folder. A click goes into a folder; the main button chooses the one
  that is open. The dialog knows nothing about moving: `FolderPickerOptions` carries the button's label
  and two callbacks, why a folder may not be entered (locked with a password) and why the open one may
  not be chosen (the file is already there). <!-- NEW (pick a file) --> With `FileExtension` set the
  same dialog picks a file instead: files with that ending are listed above the folders, newest first
  (`ReadFiles`), a click marks one, and the main button or a double click chooses it; New folder is
  hidden. The other folder choices (Extract to…, Add a folder you
  already have, Change location…, Browse… when mapping a drive) still use Windows' picker.
- <!-- NEW (recommended folders) --> **Recommended folders** in that chooser (`Services/FilingAdvisor.cs`).
  `Rank` gives every candidate folder points and keeps the best three: habit (FilingMemory's counts for
  the same site and kind, the site, the kind; every folder noted counts here, not only a clear
  majority), title (`Words` / `NameMatch`: a folder whose name is in the file's name, among your pins,
  your own folders, every folder moved to before, and folders the index finds for the file's three
  longest words through `FileIndexService.Search("<word> is:folder")`), kind (Pictures, Music, Videos,
  Documents, and folders given the matching folder type), and recent (`SettingsData.RecentMoveFolders`,
  the last eight folders any move in Clearspace went to, noted by `FileOperationService.Move`). Points
  from several counts add up; the reason shown is the one that earned the most. `Notes` copies the
  settings on the UI thread, `Recommend` asks the index and the disk on another, and the chooser opens
  in the first recommendation. Folders under Windows, Program Files, AppData, `node_modules` and
  dot-folders are never candidates (`IsNoise`), nor is a locked folder. Tests:
  `Clearspace.Tests/FilingAdvisorTests.cs`. "Stop suggesting" also takes a folder off the recent list.
- **Learned filing** (`Services/FilingMemory.cs`): `FileOperationService.Move` calls `FilingMemory.Watch`
  before a move and `Moved` after it. For files leaving a Downloads folder it counts the destination
  against `both:<site>|<ending>`, `site:<site>` and `kind:<ending>` in `SettingsData.FilingHabits`. One
  move counts once however many files it carried. A folder is offered after 2 moves (3 for the ending
  alone) when it has at least 60% of them, still exists, and is not where the file already is.
- **Installers** (`Services/InstallerShelf.cs`, `InstallersWindow.cs`): `.msi`/`.msix`/`.appx` files, and
  `.exe` files whose name or description says setup/install or carries a version or platform marker.
  Each is matched by name against the Uninstall keys (HKLM 64-bit and 32-bit, HKCU). `Match` sets aside
  the words that are not part of a name (setup words always; build words such as `win` or `x` only when
  they stand alone) and then requires the two word sets to be equal, or the names to read the same run
  together. The publisher's words are left out only when the file's own company matches the publisher.
  When the version in the file's name (else the one inside the file) is later than the installed one,
  the file is marked as newer and not ticked. Only "looks installed" rows start ticked.

## Scroll while dragging

<!-- NEW (scroll while dragging) -->
`MainWindow.DragScroll.cs` scrolls the file list and the sidebar during a drag, whichever program it
started in. Tests for the arithmetic: `Clearspace.Tests/DragScrollTests.cs`.

- **Edges.** The window's tunnelling `PreviewDragOver` (which Windows raises many times a second during
  a drag, also while the pointer is still) asks `Services/EdgeScroll.cs` for a speed: 0 outside the 44
  pixel bands at the top and bottom, rising towards the edge. The top band starts below the column
  headers. Scrolling starts after 220 ms in a band and moves by speed x elapsed time, with the target
  offset kept in the window because the list only moves at its next layout.
- **Wheel.** A drag has the mouse to itself, so no `MouseWheel` event arrives. `Services/DragWheel.cs`
  sets a low-level mouse hook (`WH_MOUSE_LL`) from the start of a drag (just before `DoDragDrop`, or the
  first `PreviewDragOver` of a drag from elsewhere) until the drop, `DoDragDrop` returning, or the first
  mouse event with no button held. A wheel turn calls `MouseWheelUp`/`MouseWheelDown` on the `IScrollInfo`
  of the list under the pointer, so the step is the ordinary one. The hook passes every event on and
  runs on the UI thread, so its callback does nothing slow.
- Only the window the drag was last over (`s_dragWindow`) answers the wheel.

## Drag to select

<!-- NEW (drag to select) -->
`MainWindow.BandSelect.cs` draws a selection rectangle over the file list; `Services/BandSelect.cs` holds
the arithmetic (tests: `Clearspace.Tests/BandSelectTests.cs`). The mouse handling has not been run by a
test; it is checked by hand.

- **Where a drag starts.** `OnFileListPreviewMouseLeftButtonDown` decides. On a tile, or on a row's icon
  or text, it is a file drag as before. On the row's own background (the template's `Root` border), a
  group heading, a gap or the space below the rows, it is a rectangle. The text cells of the details
  view are aligned left for this: a stretched `TextBlock` answers the mouse across its whole column.
- **Starting.** After the usual drag distance, `BeginBand` notes the files in the order shown (through
  the groups when grouped), puts the selection back to what it was before the button went down (the
  list has by then selected, or with Ctrl flipped, the pressed row), and takes the mouse for a `Canvas`
  that sits over the list. Taking it from the `ListView` also stops WPF's own drag-selection.
- **Details view.** The start of the drag is kept as a row and an offset inside it (`BandPoint`), not as
  pixels, so it stays right when the list scrolls and when group headings come and go. Each refresh
  reads the rows on screen from the visual tree, finds the pointer's place among them (`Locate`), and
  selects the rows between the two places (`Rows`). Rows that have scrolled away need no container.
- **Tile view.** `VirtualizingWrapPanel` has a fixed cell size and column count, so `Tiles` works out the
  touched tiles from the rectangle in the grid's own pixels.
- **Changing the selection.** Only files whose place in or out of the rectangle changed are touched. A
  few are added or removed one by one; more than 64 at once go through `ListBox.SetSelectedItems`
  (protected, reached by reflection), because the list walks its whole selection for every single
  change. `OnSelectionChanged` is quiet during the drag; the view model is told at most every 120 ms
  and once at the end.
- **Scrolling.** A 15 ms timer asks `BandSelect.ScrollSpeed` (nothing in the middle, rising from 16
  pixels inside an edge and on past it) and never moves more than half the list's height in one step.
  A drag that starts at an edge does not scroll until the pointer has been clear of it. The wheel is
  passed to the list's `IScrollInfo`.
- **Ending.** Button up, lost capture, the window losing focus, the list getting new contents, or Esc
  (which restores the earlier selection). Keys other than Esc are swallowed while the rectangle is out.

## Mapping a network drive

<!-- NEW (map network drive) -->
A network folder could be browsed once it had a drive letter, but giving it one meant going to File
Explorer. Now Clearspace maps and disconnects drives itself.

- `Services/NetworkMapping.cs` has no windows in it: `TryNormalize` turns what was typed into
  `\\server\share` (or says why it is not a network folder), `FreeLetters` lists the letters that can be
  given out, `Map` / `MapAsync` call `WNetAddConnection3`, `Disconnect` calls `WNetCancelConnection2`, and
  `Describe` words Windows' error codes. Tests: `Clearspace.Tests/NetworkMappingTests.cs` (nothing in
  them goes to a network).
- **Clearspace never handles a password.** The connection is made with `CONNECT_INTERACTIVE` and no user
  name or password, so Windows tries the signed-in account and, when the server wants something else,
  shows its own sign-in box, whose "Remember my credentials" keeps them in Credential Manager.
  "Connect using different credentials" in the dialog adds `CONNECT_PROMPT`, which makes Windows ask
  even when the signed-in account would have worked.
- **Neither call runs on the UI thread.** A server that does not answer can hold `WNetAddConnection3`
  for many seconds. `MapAsync` uses a thread of its own (STA, because the sign-in box is shown from
  it); the window calls `Disconnect` through `Task.Run`. Closing the dialog while Windows is still
  trying is allowed: if the answer then turns out to be a success, the drive is disconnected again.
- **Letters.** A letter Windows remembers a folder for (`HKCU\Network\<letter>`) counts as taken even
  while that folder is out of reach, because Windows refuses to give it out twice.
- `MapNetworkDriveWindow.cs` is the dialog (built in code, like `MessageDialog`).
  `MainWindow.NetworkDrives.cs` has the menus. Mapping is one `IAction` (`MapNetworkDriveAction`),
  reached from the Network page's banner button, the menu of the empty part of the This PC and Network
  pages, and the sidebar menus of those two headings (`SidebarEntry.OffersMapDrive`). After mapping,
  the new drive opens, or is selected when you were on the Network page.
- **Disconnect** is in a network drive's menu (sidebar entry, or its tile on the This PC or Network
  page). The folder on screen is watched (`FolderWatcher`), which keeps the drive busy, so every tab on
  the drive in every window first steps out to the Network page (`MainWindow.LeaveDrive`). If Windows
  still reports open files, the user is asked before it is forced. It is disconnected with
  `CONNECT_UPDATE_PROFILE`, so Windows forgets it and it does not come back at sign-in.
- **Afterwards** (`MainWindow.DrivesChanged`): the sidebar's drives are read once when a window starts,
  so `SidebarViewModel.ReloadDrivesEverywhere` makes every open window read them again; This PC and
  Network pages on screen reload; `NetworkDrives.ProbeSoon` lets network indexing (when it is on) take
  the drive up or let it go; `DriveHardwareProbe.Forget` drops what the disk map knew about the letter;
  and `SHChangeNotify` tells File Explorer's own windows.
- Known limits: a remembered drive whose server is out of reach is not listed anywhere in Clearspace
  (the drive lists skip drives that are not ready), so it cannot be disconnected from here; and a
  network *location* (a shortcut under This PC without a letter) cannot be added.

## Dragging a selection of several files

<!-- FIXED (drag several) -->
Dragging several selected files moved only the one under the pointer. A WPF list in `Extended` mode
answers the button going *down* on a selected row by selecting that row alone, so the selection was
already gone when `OnFileListPreviewMouseMove` read it to start the drag.

- `OnFileListPreviewMouseLeftButtonDown` now marks the press handled when it lands on a file that is
  already selected, more than one is selected and neither Ctrl nor Shift is held. The list never sees
  it, the selection stays, and the drag takes all of it. The row is given the keyboard focus by hand,
  since the list would have done that.
- A click without a drag still has to end with that one file selected. That now happens when the
  button comes *up* on the same row (`OnFileListPreviewMouseLeftButtonUp`), which is also when File
  Explorer does it. `ListBox.AnchorItem` (protected, set by reflection) is moved to the file so that a
  Shift+click afterwards counts from it.
- Not changed: a double click's second press, presses with Ctrl or Shift, and presses on a button or a
  text box inside a row (the photo tiles' "View" button, the rename box).
- Checked by hand; there is no test that moves the mouse.

## Small icons in tiles

<!-- FIXED (tiny tile icons) -->
`ThumbnailService.GetShellIcon` asks `IShellItemImageFactory` for an icon at the tile's size. For a file
type whose program only ships small icons, Windows does not enlarge one: it returns a picture of the
size asked for that is see-through apart from a thin frame at the edge and the small icon in the middle.
ZIP files showed as a speck in a square on a PC where an archive tool with 16-pixel icons owns them.

- `Services/FramedIcon.cs`: `Find` recognises the case from the pixels (see-through at four points away
  from the middle, and everything solid inside a box no wider than 42% of the picture) and cuts the icon
  out. Tests: `Clearspace.Tests/FramedIconTests.cs`, on pictures made in memory.
- `TryEnlarge` then draws an icon of 40 pixels or more at 84% of the tile picture. A smaller one would
  only blur, so the tile gets `ScalableIconService.File` (the drawn page with the type's name), the same
  as a type Windows has no icon for. A program's, shortcut's or folder's own icon is kept whatever its
  size, enlarged up to four times.
- The result goes into `ShellIconCache` like any other type icon, so it is worked out once per type and size.
- A picture with a filled background is never touched (it may be the icon's own design). If Windows
  changes how it pads small icons, tiles go back to looking as they did; nothing breaks.

## A right-click menu that fits the file

<!-- NEW (menu by type) -->
The files-and-folders menu (`MainWindow.xaml`) was the same for everything. It now leads with the
entries that fit the kind of file, and its five everyday actions are a row of icons.

- `Services/ItemMenu.cs`: `ItemMenu.For` takes the selection reduced to ending / folder / on a disk
  (`MenuSubject`) and answers with `TypeActions` flags. It reads nothing, so
  `Clearspace.Tests/ItemMenuTests.cs` covers the whole table. Whether a kind of file can be printed is
  handed in (`ShellActions.CanPrint`: does the registered program have a `print` verb, asked once per
  ending).
- `MainWindow.ItemMenu.cs`: `UpdateTypeMenu` runs from `OnItemMenuOpened` and shows the fitting entries
  (they are written in the XAML, collapsed, like the archive and download entries). The two separators
  round the groups hide when their group is empty.
- `ShellActions` carries them out the way File Explorer does: `runas` and `print` verbs through
  `Process.Start`, `rundll32 shell32.dll,OpenAs_RunDLL` for Open with…, `SystemParametersInfo` for the
  desktop background, and `WScript.Shell` (late bound) to read where a shortcut points, after which
  `RevealFile` goes there. Play and View use the built-in player and photo viewer. Copy image decodes
  the file with WPF and puts the bitmap on the clipboard. Each returns words for the status line when
  it fails; a declined administrator prompt is not a failure.
- `OpenTerminalAction.Launch` is the old body of the toolbar's terminal action, now also used for
  "Open in Terminal" on a selected folder.
- **The icon row.** Cut, Copy, Paste, Rename and Delete are `IconButton`s in a `StackPanel` at the end
  of the menu (a `ContextMenu` takes any element as an item). A button does not close a menu, so
  `OnMenuRowClick` closes it and runs the command from the dispatcher at `Background` priority, once
  the keyboard is back with the list: Rename shows a text box that would otherwise lose it. Their
  enabled state is bound to each command's `IsExecutable`. The arrow keys skip the row; the keyboard
  shortcuts are unchanged.
- Carrying the entries out is checked by hand.
- <!-- CHANGED (only what applies) --> **Nothing that does not apply.** The general entries used to show
  for everything, some greyed out. `ItemMenu.Common` now decides each from the selection (`MenuSubject`
  also carries the lock state, the cloud state, drive, pinned): Lock with password only without a lock,
  Unlock only when locked, Remove lock and Change password only when something selected has a lock,
  Pin to sidebar / Disk usage / Find duplicates only on one folder, the two cloud entries only for a
  cloud file they would change, Set folder type only with a folder selected, Reset columns only in the
  details view. The lock state of the selection is read again as the menu opens.

## Layout

```
Native/       FindFirstFileEx enumeration and Win32 declarations
Models/       FileSystemItem, natural-order sorting
Services/     Icons, shell file operations, navigation history, themes (ThemeService)
Themes/       One folder per theme (theme.json, theme.xaml, fonts); the build zips each into a theme file
Commands/     IAction, the registry, and the actions themselves
ViewModels/   MainViewModel: loading, sorting, status, sidebar; ExplorerTabs: the tab strip
```

## Not built yet

- Split panes
- Real per-file thumbnails (images and video) via `IShellItemImageFactory`
- Native shell context menus via `IContextMenu` (the current menu is Clearspace's own)
- Drag and drop
- Search
- Settings persistence

Credit: the command architecture is modelled on the Files project
(https://github.com/files-community/Files, MIT). The code here is original.
