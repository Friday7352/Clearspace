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
