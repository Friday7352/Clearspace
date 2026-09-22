# CS 499: Algorithms and data structures — disk usage visualizer

## Implemented enhancement

Clearspace now has an embedded disk usage view accessible from the toolbar or the
file-list context menu. It opens at the selected folder (or current location)
when that folder exists in the captured index. Otherwise it shows an indexed
drive root and explains the fallback. It replaces the browser content inside the
existing Clearspace window; Back to files restores the browser and its location.

The view provides a filled, single-level treemap, a descending size list, folder drill-down,
Up and breadcrumb navigation, drive selection, cancellation, and explicit refresh.
Back/Forward buttons and the mouse's XButton1/XButton2 follow visited locations,
including across drives. Alt+Left/Right navigate history; Alt+Up goes to the parent.
History stores paths rather than retaining old indexes. New navigation after Back
discards the forward branch, while refresh preserves it. History advances only
after a successful load; unavailable paths and cancellation preserve the current
view and history position.
Select a file for its full path, exact byte count, and percentage of the current
folder. Keyboard users can navigate the list with Enter and Backspace, or focus
and activate a map tile. Percentages on the blocks and matching colored icons
in the list help compare sizes. Folder icons distinguish explorable blocks, and
selecting a list item outlines its block. Zero-byte items stay in the list. Each
layout node has up to 13 entries, including a smaller-items group whose size
includes all its members. Similar-sized large collections split into balanced
groups. The list remains complete and virtualized.
Rows are 32 pixels tall with the name and size on one line. Full names and shares
appear in row tooltips; counts, snapshot information, and limitations are under
the info button. Selection reveals deletion controls. Only actionable status
messages occupy the footer, leaving the rest of the sidebar for the list.
Clicking the grouped block expands its members. Detailed snapshot limitations
are available in the “About these sizes” tooltip.

Folder entry uses the same square zoom as small-item clusters: the old map and
the folder contents share a moving camera, with the new contents appearing
inside the selected folder's square. Back, Up, and ancestor breadcrumbs reverse
the zoom; forward history and list/keyboard folder entry animate as well.
The totals, guidance, selection details, and deletion controls sit beside the
map instead of above and below it. At 1900 × 1000 the square is 876 pixels tall;
it adapts to smaller windows while preserving its aspect ratio.

Small-item zoom magnifies a square region of the existing map. The camera
retains the scene, colors, and tile coordinates; it does not repack the selected
items. The corner focus includes nearby small tiles with a bounded aspect ratio
so a long, thin strip does not select most of a neighboring large block.
Labeled adjacent blocks retain their own click actions. Uniform scale and
translation use 180 ms easing for the camera and 280 ms for folder transitions.
Folder contents fade in during the approach while the old captions fade out.
All tile areas remain byte-proportional,
and percentages continue to refer to the current folder at every magnification.
Folder previews are not drawn inside other folders: each view presents one
readable folder level without nested captions or square-packing gaps.
Zoom out, Escape, Back, or the mouse back button returns one zoom level before
navigating folder history. Resizing preserves the current group. Navigating to
a different folder or refreshing resets zoom. No file is selected for deletion
merely by zooming into its cluster.

The mouse wheel smoothly changes camera scale around the pointer. Repeated ticks
continue from the current animated pose, and approaching a hovered folder enters
it. Scrolling outward at the folder's full extent returns to its parent. The
sidebar retains ordinary wheel scrolling. Zoom-out renders the newly exposed
surroundings before moving the camera so clipped edges do not leave blank strips.

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

`NestedDiskMap` stores immutable normalized geometry for the current folder.
`SquarifiedTreemap` fills each layout node with byte-proportional rectangles,
using greedy row construction in O(k) time after O(k log k) sorting. Small
sibling items share bounded grouping nodes, with their actual members painted
inside those regions. These groups are layout partitions, not filesystem levels.

When the first item accounts for at least 80% of a layout node and space permits,
the remainder occupies a square corner. The dominant item fills the L-shaped
region around it, with a clipped cutout that exactly matches the smaller items'
combined share. This preserves areas without squeezing all other items into a
thin strip. Group members are also laid out using their actual containing aspect
ratio. Hit testing respects the cutout so the large item cannot intercept the
corner's clicks.

The sidebar uses an explicit dark ListView template for both enabled and loading
states, avoiding the system theme's light disabled background.

Zoom preserves the map and changes a normalized square camera rectangle.
Rendering intersects tile bounds with the viewport and reveals labels where
they fit. Moving the camera back restores the earlier positions exactly.
Resizing redraws the same normalized scene. Refresh, deletion, and folder
navigation prepare new scenes because their underlying data changes.

Grouping is bounded to 240 eager expansions and three levels. Large equal-size
collections split into four balanced groups to avoid a long chain of tiny tails.
Additional grouping nodes expand lazily when their bounds become visible.
The initial layout is prepared off the UI thread with cancellation. The visual
map shows a single filesystem level and does not recursively preview folders.
Small groups render as frozen drawing brushes instead of one WPF button for each
file. Their geometry and colors are reused when expanded; cropped previews use
the corresponding brush viewbox. The detail threshold scales with viewport area.
Only real items can receive an L-shaped corner cutout: a synthetic group must
remain rectangular so its child map cannot overlap neighboring tiles.

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

The Release suite passed all 121 tests on September 22, 2026 (87 existing tests
and 34 disk usage tests). The build completed successfully. No timing threshold or
claim about real-drive scan performance is inferred from the synthetic tests.

A WPF integration test uses the application's actual resources and a synthetic
index. It renders the embedded view, a smaller drill-down view, and an empty folder;
it also activates a folder tile, sends both side mouse button events, and verifies
history across drives and drive-picker synchronization. PNGs are attached to
the test report. This does not start the real indexing service, scan personal
files, or replace an interactive check on actual indexed drives.

The view test also checks rendered WPF tile areas against byte proportions,
square viewport and cluster-region dimensions, multi-item cluster zoom and return,
and creation without another Window. It exercises deletion controls
with injected confirmation and simulated file operations. Deletion tests cover
permanent shell flags, target validation, rejection of confirmation, partial
cancellation, access failures, updated totals, history cleanup, and removal
retention across Refresh and reopened views. No test deletes real files.

Regression coverage includes a skewed 200-file collection whose dominant synthetic
group previously overlapped its neighbors, repeated wheel events, pointer anchoring,
wheel folder entry and parent navigation, and a 100,000-file rendered collection.
The dense fixture keeps fewer than 650 interactive controls at both tested scales
while retaining all entries for further zoom. Rendering timings are logged as
observations, not enforced as hardware-independent performance guarantees.

The implementation files to explain in the artifact are:

1. `Clearspace/Services/DiskUsageSnapshot.cs` — hierarchy, validation, aggregation.
2. `Clearspace/Services/NestedDiskMap.cs` — persistent scenes and bounded treemap groups.
3. `Clearspace/ViewModels/DiskUsageViewModel.cs` — asynchronous snapshot lifecycle.
4. `Clearspace/Controls/DiskUsageTreemap.cs`, `Clearspace/Controls/SquareViewport.cs`, and `Clearspace/DiskUsageView.xaml`
   — interactive representation and user-facing limits.
5. `Clearspace.Tests/DiskUsageTests.cs`, `NestedDiskMapTests.cs`, and `DiskUsageWindowTests.cs` — evidence.

Before final course submission, perform an interactive walkthrough with the
actual index, prepare the reflective narrative against the assignment rubric,
package original and enhanced source, and update the ePortfolio. This document
records the implementation; it is not the student's reflective narrative.


