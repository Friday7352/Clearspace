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
Dark (the default), OLED Black, Light, Blueprint, Terminal and Retro, picked from the Settings menu
and remembered in `settings.json`.

- Every colour, the four text fonts and the corner sizes live in one file per theme: `Themes/Dark.xaml`,
  `Themes/Light.xaml`, `Themes/Retro.xaml` and so on. Every theme file defines the same keys (`Base`, `Surface`, `Ink`,
  `Accent`, `R8`, `UIFont`, ...).
- `App.xaml` merges `Dark.xaml`. `ThemeService.Apply` swaps that one merged dictionary for another
  theme's. Windows, menus, popups, tooltips and dialogs all refer to the keys with `DynamicResource`, so
  they repaint at once. A new piece of XAML must use `DynamicResource` for these keys, never
  `StaticResource`, or it will stay in the theme it was created in.
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
Type tiles, Zen, 1-bit Mac, 1-bit Mac (dark), E-reader) sit in their own submenu and go beyond colour:

- `ThemeService.Themes` lists every theme as a `ThemeInfo`: its file name, menu label, whether it is
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
- A theme whose `Base` is not a flat colour (Glass: gradient, Sketchbook: graph paper) also defines
  `Theme.BaseColor` for code. The text, accent, status and card colours must stay flat colours in every
  theme, because `ThemeService` reads them for the hand-drawn parts.

To add a theme: copy `Themes/Dark.xaml`, change the values, keep every key, add an entry to
`ThemeService.Themes`, and add a menu item for it in the Settings menu in `MainWindow.xaml`.

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

## Preview pane

<!-- NEW (preview pane) -->
Columns 3 and 4 of the main grid in `MainWindow.xaml` are the pane's splitter and the pane; both are 0
wide until `MainWindow.Preview.cs` shows it (Alt+P, `TogglePreviewPaneAction`). `Controls/PreviewPane.cs`
is made the first time the pane is shown and decides what to draw:

- a `.zip`: `ArchiveService.List`
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
- **Where a file came from** (`Services/DownloadOrigin.cs`): the page (`ReferrerUrl`) and the file's own
  address (`HostUrl`) from the `Zone.Identifier` stream. Only `http` and `https` addresses are accepted,
  because the address is handed to the shell to open; the file's own address is only ever copied, never
  opened.
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

## Layout

```
Native/       FindFirstFileEx enumeration and Win32 declarations
Models/       FileSystemItem, natural-order sorting
Services/     Icons, shell file operations, navigation history, themes (ThemeService)
Themes/       One resource file per theme: colours, fonts, corner sizes
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
