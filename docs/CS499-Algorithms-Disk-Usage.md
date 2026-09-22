# CS 499: Algorithms and data structures — disk usage visualizer

## Implemented enhancement

Clearspace now has an embedded disk usage view accessible from the toolbar or the
file-list context menu. It opens at the selected folder (or current location)
when that folder exists in the captured index. Otherwise it shows an indexed
drive root and explains the fallback. It replaces the browser content inside the
existing Clearspace window; Back to files restores the browser and its location.

The view provides a nested treemap of the whole drive, a descending size list,
folder drill-down, Up and breadcrumb navigation, drive selection, cancellation,
and explicit refresh. Back/Forward buttons and the mouse's XButton1/XButton2
follow visited locations, including across drives. Alt+Left/Right navigate
history; Alt+Up goes to the parent. History stores paths rather than retaining
old indexes. New navigation after Back discards the forward branch, while refresh
preserves it. History advances only after a successful load; unavailable paths
and cancellation preserve the current view and history position.
Select a file for its full path, exact byte count, and percentage of the current
folder. Keyboard users can navigate the list with Enter and Backspace; + and −
zoom the focused map. Zero-byte items stay in the list. Rows are 32 pixels tall
with the name and size on one line; counts, snapshot information, and
limitations are under the info button. Selection reveals deletion controls.

**One continuous space (revised).** The first version drew one folder level at a
time and animated folder entry by cross-fading a newly packed square into the
old one, while the wheel used a separate camera. Entering a folder therefore
re-laid out its contents in a different shape than the tile the user clicked,
and every zoom step rebuilt hundreds of WPF buttons. The revised map lays out
the entire drive once in fixed world coordinates: each folder's children are
placed inside that folder's own rectangle and never move. The view is a single
camera over that world. Clicking a block, Back/Forward, Up, breadcrumbs, list
navigation and the wheel all move the same camera, so every transition is
continuous and reversible by construction, and the neighbors of the open folder
remain visible behind a veil that glides to the new folder.

Clicks act one level at a time: inside the current folder, a click opens (or,
for a file, selects) the child on the path to what was hit. The wheel zooms at
the pointer with exponential smoothing; successive ticks accumulate on the
running target while the point under the pointer stays fixed. A folder is
entered once the camera has zoomed in past its parent and the folder nearly
fills the view around the pointer; the list follows after the zoom settles for
140 ms so passing through folders does not churn it. Zooming back out past an
opened folder returns to its parent. Dragging pans. Hovering shows a card with
the item's type, size, share of its folder and what a click will do.

Blocks are colored by file type (shared with list icons and a legend), so a
tile keeps its color at every zoom level and in every folder. Open folders show
a small name tag that fades out once the folder fills the view. Labels are laid
out in the visible part of a tile and fade in as space appears.

Files and folders can be permanently deleted through the map's right-click
menu or a selection in the list (including Ctrl/Shift multi-selection), using
the Delete permanently button, list context menu, or Shift+Delete. A default-No
confirmation names the target paths, explains that the Recycle Bin is bypassed,
and states that folder deletion includes current contents missing from the index.
Drive roots and the synthetic grouped tile cannot be deleted. Windows executes
the confirmed operation with the existing shell service; tests inject a fake.

After every attempted operation, including cancellation or failure, the view
checks the selected indexed subtrees. Only paths confirmed missing are excluded;
permission or I/O uncertainty leaves entries visible. Subtree totals and history
are then updated, and the main browser refreshes. A session-wide removal ledger
prevents older captured indexes from restoring deleted entries on Refresh or
reopening the analyzer. An index started after the removal can include a newly
created item at that path. The ledger is not persisted across application exits.

## Data structure and aggregation

The existing immutable `VolumeIndex` stores file metadata and parent indexes in
compact arrays. `DiskUsageSnapshot` retains a single published volume and adds
five arrays: subtree bytes, subtree file counts, first child, next sibling, and
an exclusion flag. The additional payload is approximately 25 bytes per index entry, plus
array headers. It does not duplicate the filename pool or modify the persisted
index format. The active snapshot keeps that index version alive until reload
or closing the analyzer; a concurrent index rebuild can temporarily increase memory.

The hierarchy is validated before aggregation: there must be one directory root,
and each remaining parent must be an earlier directory entry. This matches the
existing builder's parent-before-child insertion order and rules out cycles.
Invalid name offsets and overflowing totals fail with a visible error instead
of presenting a plausible but incorrect total.

File sizes initialize leaf weights. Folder metadata sizes are ignored. A reverse
pass adds each entry's accumulated bytes and file count to its parent exactly
once. This evaluates recursive subtree sums using an iterative bottom-up
traversal, without consuming the call stack. It takes **O(n) time and O(n)
additional space**, compared with **O(n × d)** for walking up to `d` ancestors
for every file. A folder's immediate children are found through the sibling
arrays in O(k) time and sorted by size, name, then index in **O(k log k)** time.
Sorting and aggregation run off the UI thread. Cancellation and request identity
prevent canceled or superseded work from publishing over newer results.

## Treemap layout and camera

`SquarifiedTreemap` fills each level with byte-proportional rectangles, using
greedy row construction in O(k) time after O(k log k) sorting (the snapshot
already returns children sorted, which the layout detects in O(k) and reuses).
`DiskUsageTreemap` applies it recursively and lazily: a folder's children are
laid out inside its own world rectangle the first time the folder becomes large
enough on screen (about 26 px) to need them. Loading and layout run on a
background thread with a per-snapshot cancellation token and at most three
loads in flight, largest on-screen folder first; results fade in over 220 ms.
A folder with more than 150 items gives the largest 120 their own tiles and
splits the rest into up to 100 balanced "smaller items" blocks of roughly equal
count. Those blocks are layout partitions, not filesystem levels, and expand
(synchronously, from memory) as they grow. Every level is exact: children
exactly tile their parent, so areas remain byte-proportional within each folder.

The camera is a world rectangle with the view's aspect ratio. Flights between
two camera rectangles interpolate about their **fixed point**, the one world
point that occupies the same screen position in both views. The scale changes
exponentially around it (constant perceived zoom speed), so a folder grows
straight out of where it sits instead of drifting. When neither view contains
the other (for example Back to a different branch), the flight rises to an
overview containing both and then descends. Durations scale with the zoom
ratio and pan distance (320–680 ms) and use a CSS-style ease curve. Wheel zoom
eases toward its target with a 75 ms time constant using the same interpolation.

Rendering is a single `OnRender` pass with no per-tile controls. The traversal
culls tiles outside the view and below about one square pixel, blends each
folder from a solid block into its contents as its on-screen size grows from 44
to 96 px, draws pixel-constant gaps and folder edges, and pins each large tile's
shading to the whole tile so it does not shift while zooming. Brushes, pens and
text are cached and frozen; a per-frame budget bounds primitives. Frames run on
`CompositionTarget.Rendering` only while something is moving or fading.
Resizing keeps the camera; a large aspect change re-lays out the world once,
220 ms after resizing stops.

The sidebar uses an explicit dark ListView template for both enabled and loading
states, avoiding the system theme's light disabled background.

## Snapshot semantics and limitations

- These are **indexed logical file lengths**, not physical allocation, free
  space, or a fresh full-drive scan. Hard links can be counted more than once;
  sparse, compressed, and cloud-placeholder files can differ from disk usage.
- Hidden and system entries already in the index are included in totals.
- The existing index builder skips inaccessible folders and directory reparse
  targets and has a depth limit. The visualizer cannot recover missing entries
  or claim complete coverage. The UI states those exclusions.
- The displayed timestamp is when the index build started. Search overlay
  events are intentionally not merged with older subtree sizes. Refresh captures
  the latest *published* index; it does not force a rebuild or filesystem scan.
- If no index is available, the view explains how to retry after indexing.
  Browsing does not open file contents. Permanent deletion is an explicit,
  confirmed action; after partial deletion, surviving file lengths still come
  from the saved index.

Removing the index-builder depth limit, tracking exact scan coverage, and adding
an explicit on-demand rescan are separate improvements. The database migration
also remains separate from this artifact.

## Verification and portfolio evidence

Run the Release suite:

```powershell
dotnet test Clearspace.Tests/Clearspace.Tests.csproj -c Release --logger "trx;LogFileName=algorithms.trx" --results-directory output/test-results/algorithms
```

Tests cover nested totals, hidden entries, empty items, stable sorting, path
boundaries, a 100,000-level synthetic hierarchy, invalid trees, numeric overflow,
cancellation, snapshot reload, drive changes, and superseded results. Layout
tests check total area, proportionality, bounds, non-overlap, equal-weight
squares, large weights, and empty or invalid inputs.

The original visualizer's Release suite passed all 121 tests on September 22,
2026. **The continuous-map revision has not yet been built or run; rerun the
suite and record the new counts here.** (The nested-scene tests were removed with
`NestedDiskMap`; the window test was rewritten for the camera model.) No timing
threshold or claim about real-drive scan performance is inferred from the
synthetic tests.

A WPF integration test uses the application's actual resources and a synthetic
index. Animations are driven with a deterministic clock (`AdvanceTime`) rather
than wall time. It renders the embedded view at two sizes and an empty folder;
checks that root tile areas match byte shares and fill the view; clicks a folder
and verifies that it grows continuously mid-flight, ends up filling the view,
and keeps exactly the same world geometry it had before entry; waits for a
nested folder to load and appear; sends both side mouse buttons; verifies wheel
anchoring (the world point under the pointer does not move), wheel entry into a
folder and back out to its parent, and Back returning to the whole folder after a
zoom; exercises history across drives, drive-picker synchronization, and
deletion with injected confirmation. A 100,000-file folder is rendered and
zoomed to confirm the per-frame budget holds and individual files appear.
PNGs are attached to the test report. This does not start the real indexing
service, scan personal files, or replace an interactive check on actual drives.
Deletion tests cover permanent shell flags, target validation, rejection of
confirmation, partial cancellation, access failures, updated totals, history
cleanup, and removal retention across Refresh and reopened views. No test
deletes real files.

The implementation files to explain in the artifact are:

1. `Clearspace/Services/DiskUsageSnapshot.cs` — hierarchy, validation, aggregation.
2. `Clearspace/Services/SquarifiedTreemap.cs` — one level of squarified layout.
3. `Clearspace/Controls/DiskUsageTreemap.cs` — lazy nested layout, fixed-point
   camera, culling renderer, and input.
4. `Clearspace/Services/DiskUsagePalette.cs` — file-type colors shared by map, list and legend.
5. `Clearspace/ViewModels/DiskUsageViewModel.cs` and `Clearspace/DiskUsageView.xaml`
   — asynchronous snapshot lifecycle and user-facing limits.
6. `Clearspace.Tests/DiskUsageTests.cs` and `DiskUsageWindowTests.cs` — evidence.

Before final course submission, perform an interactive walkthrough with the
actual index, prepare the reflective narrative against the assignment rubric,
package original and enhanced source, and update the ePortfolio. This document
records the implementation; it is not the student's reflective narrative.


