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
- Watching the folder for changes with `ReadDirectoryChangesW`

Credit: the command architecture is modelled on the Files project
(https://github.com/files-community/Files, MIT). The code here is original.
