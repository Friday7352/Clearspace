# Clearspace regression tests

Run on Windows with the .NET 10 SDK:

```powershell
dotnet test Clearspace.Tests/Clearspace.Tests.csproj
```

Run from the repository root. MSTest discovers the tests automatically.

- `FileOperationTests`: native outcome translation, shell request construction,
  validation, and retained operation feedback.
- `SearchCoordinatorTests`: (also: only a slow search leaves a trace line, and it never holds the search text;
  only results from a drive whose index is behind are checked on the disk, up to a limit; the Windows
  folders are asked for once and then remembered; nothing reaches the list while typing continues; this
  folder's matches and the index's answer arrive together, or the folder's first when the index is slow
  or a drive has to be read; an update with the same rows hands over the same list)
  source selection, cancellation, deduplication,
  filter-only fallback on indexed volumes, status restoration after clearing
  searches, final batches, missing items, hidden filtering, and result limits.
- `TagAndQueryTests`: tag persistence/cleanup and search parsing/matching; one lookup answering for many
  paths with a single read of the tags; tags following several renames at once (names shifted along or
  swapped keep their own tags) and following what is inside a renamed folder.
- `NavigationAndSidebarTests`: load lifetimes, history, sorting, breadcrumbs,
  and sidebar composition.
- `FileIndexTests`: path reconstruction, filtering, cancellation, compaction,
  capacity growth, and result limits.
- `IndexingOverviewTests`: browsing actual indexed entries, removed-entry filtering,
  pagination without dropped entries, cancellation, bounded scan diagnostics, and
  truthful coverage details for older saved indexes. `DiskUsageWindowTests` also
  checks the overview's navigation, background loading, disposal, and wide/compact
  layout captures.
- `TraversalAndCoverageTests`: (also: routine churn of temporary files no longer drops a drive from the
  index once the changes have been applied, and only the applied paths are forgotten)
  native traversal through 48 nested folders,
  cancellation, live subtree insertion, rename/delete replay, publication during
  concurrent changes, shared reparse policy, and isolated per-drive recovery.
- `AlgorithmLayoutTests`: deterministic compact layouts, proportionality,
  non-overlap, subtree ranges, stable placement, and snapshot cache replacement.
- `LiveFolderTests`: merging a changed folder into the list on screen (unchanged rows keep their
  objects, new rows land where the sort puts them), preparing only the new rows, and the folder watcher
  reporting a burst once, reporting a file that keeps growing, and stopping when told to. The watcher
  tests use real files in a temporary folder and wait on the clock.
- `BatchRenameTests`: the naming pattern, find and replace, capitals, kept endings (including locked
  files), names Windows refuses, clashes, swapped names going through temporary names, and putting
  every name back after a failure. Runs against a stand-in for the disk.
- `ArchiveTests`: compress then extract, never overwriting, names that try to leave the folder they are
  unpacked into, stopping, and what is left behind afterwards; files that are compressed already
  (photos, video) being stored as they are. Uses real files in a temporary folder.
- `ArchiveBrowseTests`: a ZIP file opened like a folder. The address of a place inside one, how it is
  shown and typed, Up and the address bar's trail; listing (folders a ZIP only implies, names that try to
  leave it, a ZIP that changed on the disk); taking things out without overwriting, with names Windows
  refuses, and stopping; the copies made to open or copy a file, tidying them, and removing them when
  their folder is locked; and that the inside of a ZIP is read-only (which actions are off, and that Open
  never hands an address inside a ZIP to Windows). Uses real files in a temporary folder.
- `PreviewAndOpenerTests`: telling text from other files for the preview pane, and reading the path
  Windows passes when a drive is opened with Clearspace.
- `DuplicateTests`: copies found whatever they are called and wherever they are, files that only start
  the same, empty and hidden files, subfolders on and off, ordering, progress and stopping; and which
  ticked files may be removed (a group always keeps a copy; nothing that changed since the search).
  Also the list of files taken from the index (an index built in memory): the same files as reading
  the folders would give, a list that is out of date being checked against the disk, and links not
  counting as copies. The search uses real files in a temporary folder; nothing is deleted.
- `DownloadsTests`: which files the Just arrived strip shows and how their age is worded, reading the
  page and download address from a file's note (web addresses only), telling installers from other files
  and matching them to installed programs, comparing versions, and when a folder is offered for filing.
  No disk, registry or settings file: each part is given its data.
- `DragScrollTests`: how fast a list scrolls while something is dragged near its top or bottom edge (the
  still middle, faster towards the edge, the band below the column headers, very short lists, no jumps
  after a pause). The dragging itself and the mouse wheel during a drag are checked by hand.
- `BandSelectTests`: dragging a rectangle to select: where a point is among the rows on screen (inside a
  row, on a group heading, below the last row), that it stays put after scrolling, which rows and which
  tiles a rectangle touches, what Ctrl and Shift do, and how fast the list scrolls with the pointer held
  at an edge. The mouse handling, drawing and scrolling are checked by hand.
- `NetworkMappingTests`: the Map network drive dialog's rules: the ways a network folder can be typed
  (`\\server\share`, forward slashes, `smb://`, quotes, a trailing slash), what is refused and the reason
  shown, which drive letters are offered, how Windows' answers are worded, and which sidebar headings
  offer mapping. No test connects to a server or changes this PC's drives; mapping and disconnecting
  a real share are checked by hand.
- `UpdateTests`: (also: which earlier versions are offered to go back to - older than the running one, not
  below the oldest safe one, each once - reading GitHub's list of releases, and that the tag database
  schema has not moved past what going back was checked against; and that going back past the release
  that introduced file backups removes their Task Scheduler entry)
  how a GitHub release is read and compared; no network.
- `ThemeTests`: themes as files in the Themes folder. Dark is always listed and usable (an empty or
  missing folder, `Dark.zip` deleted, `Dark.zip` damaged), nothing can take the name Dark, and every
  theme except Dark can be removed. Every theme that comes with Clearspace loads from
  its source folder and uses only keys Clearspace knows, and Dark (the fallback under every other theme)
  defines them all; reading `theme.json` and what is refused in it; a zip with the files at the top or
  one folder down; that `theme.xaml` is refused when it holds anything that could run code or reach a
  file or the network (bindings, other namespaces, styles and controls, addresses in any value) and
  let through with gradients, patterns, bundled fonts and bundled pictures; a key holding the wrong kind
  of value; that a plain theme built without WPF's XAML reader comes out the same as with it; listing the folder (Clearspace's own first, a folder in place of a zip, a broken theme with
  its reason); and importing (a bad file adds nothing, a taken name is replaced only when agreed, never
  one of Clearspace's own). Uses temporary folders. Switching themes in a window, the Settings menu and
  showing a theme again when its file is saved are checked by hand.
- `DriveWatchTests`: what changed between two looks at the drives: plugged in, taken out, another stick
  under the same letter, several at once, and that the order drives are listed in does not matter. The
  window message and the timing are checked by hand.
- `FileBackupTests`: backups of files, folders and drives. When a backup is due (every hour, a custom
  interval, daily and weekly at a set time, catching up after the PC was off, a failed run tried again
  after an hour, a clock set back) and how a schedule reads; names and folders (a folder name Windows
  allows, a free one beside what is already there, what "inside" means, which files a plan covers, plans
  that would back up into themselves or share a folder, what may be typed to leave out, times of day as
  typed); a backup only ever using a folder with its own marker. Runs between two temporary folders with
  Clearspace's own copy: Clone (copies, updates, removes), the safety net and file history (what goes
  under Earlier versions, a file that became a folder, an earlier version going back when its new one
  could not be copied, thinning out by age), Add and update, the refusal to clone an emptied original,
  left-out names, one file by itself, waiting for a missing drive; Versions (a run without changes makes
  none, unchanged files are linked, full copies where the drive has no hard links, only the newest are
  kept, an unfinished version is cleared away); Zip archive (contents, nothing new makes nothing, only
  the backup's own zips are thinned out; that "Keep" and "Zip to save space" together decide a plan's
  kind, that a zipped backup kept "latest only" has one zip that each run replaces, and retention:
  thinning out by number or by age, the newest run always staying, and age going on when a run finds
  nothing new; and a zip that is carried on with: written in stretches it is an ordinary zip, a zip cut
  short at any point is carried on with from its last save, only files that are still the same stay in
  it, a damaged or foreign unfinished zip is refused and made again, a file that cannot be read to its
  end is left out, stopping inside a file keeps everything before it, more than 65,535 files, the end
  records, and whole runs that are stopped and carried on with). What suits what is backed up (telling documents, photos and
  code apart by file names; a project by what is in its folder; which folders are safe to skip; the
  recommendation for each kind of content and how it differs on a drive that stores dated copies in
  full; setting a plan up from one without touching a chosen schedule or typed skip names; finding a
  folder of Unity projects, photos in folders below, and a few projects among many folders, in
  temporary folders; and when a backup is on the drive it backs up).
  Drives offered for a new backup (finding backups
  at the top of a drive and one level down, the folder a new one goes in, drives with backups before
  empty external ones, never the drive being backed up, the Windows drive only when it holds backups,
  and that only a folder Clearspace offered is made on saving). Restoring (only what is missing unless asked, to another
  folder, from a zip, never into the backup, a zip entry that would land outside the folder) and the
  earlier versions of one file from several backups, without duplicates. Robocopy: the command it is
  given (and that it is never asked to remove anything), reading its log without reading its words, its
  exit codes, a growing UTF-16 log; and `ARealRunThroughRobocopyCopiesWhatChanged`, which on Windows
  starts the real `robocopy.exe` between temporary folders. The windowless run: its switches, which plans
  are due and can run, the lock that keeps a plan from running twice, what a run leaves behind, stopping,
  and stopping everything during an update. The Task Scheduler entry's XML and when it is wanted. What
  the page says about a backup. Not covered by a test: the page and its dialogs, starting the second
  process, and talking to Task Scheduler (checked by hand).
- `BackupTests`: backups of settings and tags: when an automatic backup is due (daily, weekly, or every
  so many hours), what a backup is called in the list and a label typed for it, a backup read back as
  it was written, the list in a folder, thinning out automatic backups only, refusing files that are not
  backups or are from a newer Clearspace, which tagged items are restored, and putting tags back (lost
  tags return, nothing is taken away unless asked, a tag made again under the same name is the same tag).
  Uses a temporary folder and a tag database in memory; the real settings and tags are never touched.
- `ItemMenuTests`: which right-click entries fit a selection: sound files, pictures, programs,
  shortcuts, folders, documents that can be printed, several items at once, and things that are not on
  a disk. Carrying the entries out is checked by hand.
- `FilingAdvisorTests`: which folders the folder chooser recommends for a file and the reason shown:
  the telling words of a file's name, a folder named like the file, where files like it went before,
  the folder for its kind, recently used folders, reasons adding up, and what is never recommended (the
  folder the file is in, folders that are gone, system and program folders). Also the short list of
  recently used folders. The clues are handed in; no disk, index or settings file is read.
- `FolderPickerTests`: which folders Clearspace's own folder chooser lists: folders only, numbers in
  names counting as numbers, hidden folders only when asked for, and folders Windows protects never.
  Also the files it lists when it is picking a file: only those with the ending, newest first.
  Uses real folders in a temporary folder. The dialog itself is checked by hand.
- `FramedIconTests`: telling a small icon that Windows returned inside a big empty picture from an icon
  that fills its picture: where it is cut out, the size limit, shadows and specks, and pictures with a
  background of their own, which are left alone. The pictures are made in memory.
- `DiskUsagePerformanceTests` and `DiskUsageWindowTests`: dense scenes, CPU/GPU
  presentation, camera input, busy-buffer hit testing, navigation, and image captures.

Native file operations are simulated. Tag storage is isolated in memory and
folder preparation uses supplied test sources. Native traversal tests create
and clean up only GUID-named temporary fixtures. These tests do not replace a
visual walkthrough or interactive checks of Windows file-operation dialogs.
