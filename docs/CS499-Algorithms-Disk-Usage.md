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
enough on screen (prefetch begins at 10 px) to need them. Initial layout,
folder-path navigation, and detail loading run on background threads with a
per-snapshot cancellation token and at most six speculative
loads in flight, largest on-screen folder first; results fade in over 220 ms.
A folder with more than 150 items gives the largest 120 their own tiles and
splits the rest into up to 100 balanced "smaller items" blocks of roughly equal
count. Those blocks are layout partitions, not filesystem levels, and expand
(asynchronously, from memory) as they grow. Every level is exact: children
exactly tile their parent, so areas remain byte-proportional within each folder.

The camera is a world rectangle with the view's aspect ratio. Flights between
two camera rectangles interpolate about their **fixed point**, the one world
point that occupies the same screen position in both views. The scale changes
exponentially around it (constant perceived zoom speed), so a folder grows
straight out of where it sits instead of drifting. When neither view contains
the other (for example Back to a different branch), the flight rises to an
overview containing both and then descends. Durations scale with the zoom
ratio and pan distance (320–680 ms) and use a CSS-style ease curve. Wheel zoom
eases toward its target with a 45 ms time constant using the same interpolation.

Color encodes structure: every item directly inside the folder being viewed takes
its own hue and everything within it shares that hue, so a region is readable as a
region. Within a branch the value comes from a hash of each item's name over a wide
range, which keeps siblings individually visible instead of ramping in size order or
fusing into a flat field. (Coloring by file type was tried and reverted: on a Windows
drive most files have no recognised extension, so the map went grey, and mixing type
hues with branch hues left nothing to separate structure from content.) Gutters grow
with tile size to a capped one pixel, and tiles down to 8 px on their shorter side
draw them.

Two user-visible performance options, persisted in settings, cover machines without
capable graphics. Turning off GPU acceleration disposes the Direct3D renderer and
returns to the CPU rasterizer, which is also what runs when no adapter can be created;
the settings panel reports the adapter actually in use, read from the D3D9 adapter
identifier. Low detail mode makes `OpenAmount` return zero for any node off the open
path, so traversal visits one level regardless of the depth or size of the tree. Both
take effect on the next frame: the first replaces the presentation path, the second
bumps the detail revision that invalidates the cached geometry layer.

What the map looks like is decided by its expansion thresholds far more than by its palette.
A folder began opening at 24 px on screen and was fully open at 56 px, so a block of about
30 px - a small project at drive-level zoom - was a fifth open and drew as a flat slab. Opening
from 9 px to 22 px makes that same block fully expanded and shows two or three more levels of
nesting at the same camera. The tile budget rose with it, and is now chosen by renderer: each
block costs five rectangles, which the GPU draws in well under a millisecond at 32,000 blocks,
while the software rasterizer fills them span by span and keeps a 10,000 budget. A folder more
than one level below the labelled one also barely darkened (0.8), so its frame disappeared and
everything under the second level read as one field; every level now recesses.

Where a frame is prepared turned out to matter more than what it costs. With the Direct3D path a
whole frame - walk, labels, upload and draw - measured about a millisecond, while frames arrived
22 ms apart and a counter recorded hundreds of frames the compositor had declined to release its
surface for. The cause was the input-friendly frame queue: handing the work to a
background-priority dispatcher operation placed it after WPF had already taken the composition
surface, so `D3DImage.TryLock` failed and the frame was dropped. The GPU path now renders inside
the `CompositionTarget.Rendering` callback, which is the point in the frame before the surface is
taken; the queue is kept for the software rasterizer, whose frame is heavy enough to starve input.
The zero-length lock wait became a few milliseconds, so incidental contention costs a wait rather
than a whole vsync. Separately, the map animates against a heap dominated by the file index, where
one blocking gen2 collection is a quarter-second stall regardless of frame cost; the view requests
`SustainedLowLatency` while it is open and restores the previous mode on close.

Sub-pixel seams make multisampling load-bearing rather than cosmetic. Once a seam is a fraction
of a pixel wide, a renderer without coverage sampling either snaps it to a whole pixel or drops
it, so the GPU path showed jagged small blocks and seams that vanished between neighbours while
the software rasterizer, which computes fractional edge coverage, did not. The Direct3D path
multisamples again. Separately, the device is now created on the adapter driving the monitor the
window is on, found through `MonitorFromWindow` and `GetAdapterMonitor`, rather than on adapter
zero: when WPF composes on a different adapter than the shared surface lives on, every frame is
copied between them and the software rasterizer measures faster. The F3 readout counts frames the
compositor declined to release the surface for, which separates contention from drawing cost.

Seams are not drawn at all. A folder paints its whole surface once, and each child paints a
single inset rectangle on top of it; what shows between two children is the surface underneath.
This replaced four border strips plus a fill per block - five rectangles - with one, which is
what pays for the density above, and it makes an unpainted region structurally impossible: the
earlier black blocks were holes where an inset had consumed a block entirely and the degenerate
rectangle was returned undrawn, or where a child with no measurable area was skipped. The
invariant weakens from "every point painted exactly once" to "every point painted at least once
and at most a bounded number of times", which the regression now checks. Drawing a seam as a
line in its own right had also made it far too heavy: 74% shading applied to a folder that had
already darkened to 42% gave a near-black seam several pixels wide. Separation now comes from
the line being thin rather than dark.

Three separate causes of visual jitter were removed. A cross-fade puts two copies of the
same scene, at different scales, on screen together, so every edge is doubled and offset;
the blend is now reserved for a change of level, where the colours genuinely differ, while
rebuilds caused by a finished load or by camera movement swap outright. The gutter is handed
down by the folder instead of derived from each block's own size, so a large block and a
small one no longer meet with two different half-gaps. And a child's screen rectangle is
built by mapping the layout coordinates of all four of its edges, rather than a mapped
origin plus a separately scaled width, so two touching blocks resolve to the same value
instead of a fraction apart.

An open folder insets its contents and paints the ring between its own rectangle and
that inset in its own (darkened) color, as four disjoint strips. Together with the
sibling gutter this gives two nested frames - one separating a folder from its
siblings, one separating it from the blocks inside it - while still partitioning the
parent rectangle exactly, so no region is painted twice and none is left uncovered.

Replacing the source no longer blanks the map. `SetSource` runs on a drive change, on
the post-resize relayout, and on first load; each used to clear the cached picture and
show the bare background for as long as the background build took. The outgoing scene
cache is now kept as a detached layer that draws frozen at its own camera, and the
incoming one cross-fades over it through the same two-layer blend used for detail
changes. A detached layer is dropped after four seconds so a failed build cannot leave
a stale picture up. Cached geometry also carries the viewport it was built for, so only
a change of aspect ratio - not any resize - invalidates it.

The dominant cost was never the node tree. A folder of N children produces at most about
220 nodes - 120 named and up to 100 grouped blocks - but each grouped block held an
`ArraySegment` over the folder's whole sorted child array, so one expanded folder pinned a
`DiskUsageItem`, object and name, for every file beneath it: on the order of a hundred bytes
times N, for as long as the folder stayed expanded. A grouped block now stores only its
members' ids, four bytes each, and resolves them from the snapshot when the block is opened.
The sorted item array becomes garbage as soon as the layout returns. For a folder of 500,000
files that is about 2 MB retained instead of 50 MB, and membership tests become an integer
scan rather than a projection over objects.

Nothing collapsed what had been expanded. A laid-out item costs roughly half a kilobyte
- the node, its `DiskUsageItem`, its name - and a folder's grouped tail holds the entire
child array open, so exploring a large drive grew the tree without any bound. Nodes now
record when they were last on screen, and a sweep collapses any folder whose whole
subtree has been off screen for twenty seconds, dropping its child nodes, their grouped
tails and the item array those tails held; they reload on demand exactly as they did the
first time. The ceiling is 120,000 nodes (30,000 when conserving memory), the sweep waits
for a pause unless the tree is at twice the ceiling, and everything the control still
points at - the open path, focus, hover, highlight, colour levels - is protected first, so
a click can never land on a node that has left the tree.

Memory is dominated by text, not by geometry. A frame of 13,806 blocks is about 330 KB
of vertices and measured 0.7 ms to draw; a single labelled node holds four WPF
`FormattedText` layouts and four frozen glyph `Drawing`s, kilobytes each, and nothing
released them, so a session of zooming accumulated them without bound. Nodes now record
when they were last on screen, registering in a list when they first create text; a
sweep hands that text back after five seconds off screen and enforces a hard ceiling
(2,500 labelled nodes, 700 when conserving memory), evicting the longest-unseen first.
A reclaimed node re-creates its text through the same per-frame allowance that limits
new labels, so the ceiling costs a brief fade rather than a stall. The F3 readout
reports heap size, labels held against the ceiling, cached rectangles and GPU buffer
size, so the claim above is checkable rather than asserted.

Snapshots are cached per volume (`DiskUsageSnapshotCache`) rather than aggregated on
every open. The invalidation rule is the interesting part: a live index edit does not
invalidate, because a drive under continuous change would otherwise make every open
slow again; the view's own background refresh replaces the stored snapshot while it is
already open. A snapshot is discarded only when the user chooses Refresh or when a
volume's last full scan time changes, and the cache holds at most two volumes, least
recently used dropped. Snapshots are prewarmed on a background thread once an index
exists, so the first open after launch is immediate too.

The geometry cache keeps its layer while the camera scale stays within [0.62, 1.6] of
the scale it was built at, and the layer is built with 35% overscan so that window is
reached before the cached tiles run out of viewport. Widening it from the original
[0.8, 1.25] removed the case where zooming out left the coverage test failing every
frame, which forced an ungated rebuild per frame.

Rendering uses batched Direct3D tiles, with a parallel bitmap rasterizer as the
fallback, plus separate WPF label and overlay layers. There are no per-tile
controls. The traversal culls tiles outside the view and below 1.6 px on their
shorter side, blends folders into their contents between 24 and 56 px, and
scales gaps with tile size. Brushes, pens, text, and caption strings are cached;
a hard 12,000-tile limit bounds traversal independently of monitor size. Distant
groups stay intact when their children cannot fit their share of the budget;
detail fades across a range of allowances instead of switching at a threshold.
Sibling allowances depend on visible area, not on how much detail an earlier
sibling happened to consume. Focused paths remain open. Captions use reusable value records
instead of a closure allocation per visited node. Frames run on
`CompositionTarget.Rendering` only while something is moving or fading.
The rendering event coalesces work into one background-priority dispatcher
operation, below mouse/window input. Live layout notifications use the same
queue instead of preparing another expensive scene during resize. The camera,
blocks, labels, and overlays update in the same queued operation. Text preparation
is limited to eight new labels per moving frame, with less frequent width
reformatting during motion. Pixel resolution is unchanged.
Names that already fit retain their text layout as their tiles grow. Hover text
is cached per target, and the F3 readout refreshes four times per second.
Speculative detail loading is limited to one folder during movement and two
when settled; barely visible folders no longer trigger whole-directory loads.
Resizing keeps the camera; a large aspect change re-lays out the world once,
220 ms after resizing stops.

Both image paths use a zero-wait buffer lock: a busy compositor retains the
previous image and schedules a retry rather than blocking input.
GPU frames reserve the surface before preparing labels or hit targets, keeping
all three layers on the same camera when the compositor is busy. Duplicate
render notifications for the same compositor timestamp are ignored. Selection
lookups are cached instead of rescanning grouped members on every frame.
Closing the analyzer cancels loads, stops timers/render callbacks, and releases
tree caches and graphics surfaces. Old loads cannot overwrite a newer drive
or navigation request.

The sidebar uses an explicit dark ListView template for both enabled and loading
states, avoiding the system theme's light disabled background. Map-driven list
updates wait for the gesture to settle, both before preparing and before
publishing the listing. The former 640 ms timeout no longer forces a list rebind
during a long gesture. Returning to the currently listed folder cancels any
older deferred listing.

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

The continuous-map Release suite last ran on September 22, 2026 with **131
tests**; 129 passed and the two dense-frame performance cases failed on
machine-dependent thresholds (see the geometry-cache results below).
**The suite has not been rerun since those thresholds were corrected; rerun it
and record the final counts here.**

Performance/lifecycle coverage includes slow and superseded loads,
closing during a load, dense camera frames at 1280 × 800 and 4748 × 1220,
GPU presentation/resizing, input-priority/coalescing, and continued zooming
through the frame queue in a hidden native WPF presentation source.
Additional regressions verify that GPU contention defers labels and hit targets
with the tiles, detail allowances fade gradually, zoom prefetch starts one load,
and sidebar publication waits or cancels when a gesture resumes or changes target.
The busy-buffer regression forces three unsuccessful lock attempts and then
verifies that drawing recovers with no outstanding image lock. It failed before
the fix: D3DImage increments its lock count even when TryLock returns false, so
the skipped-frame return must remain inside the Unlock finally block. This
prevents frozen blocks beneath moving labels. The test uses WPF's private
compositor event only for deterministic contention injection; production code
uses public APIs. See the [WPF implementation](https://source.dot.net/PresentationCore/System/Windows/InterOp/D3DImage.cs.html).
The GPU check presented 12/12 frames with multisampling on this host. It reports
when hardware is unavailable so the fallback remains testable on other hosts.

A 1280 × 800 CPU-fallback fixture with 25,600 files rendered 180 camera frames:
median 1.92 ms, p95 10.10 ms, maximum 13.19 ms, and up to 25,793 tiles.
UI-thread temporary allocations fell from 1,187,983 to 275,140 bytes per frame
(about 77%) after removing traversal closures and repeated caption formatting.
The before/after timing samples are variable; they do not establish a frame-rate
speedup or a guarantee of uninterrupted real-drive presentation. Timing excludes
PNG export and records synchronous frame work, not display refresh or GPU latency.
Reports are in `output/test-results/performance/`. The regression enforces a
tile bound and an allocation ceiling, not a machine-dependent timing limit.

The subsequent fullscreen check reduced peak drawn tiles from 25,793 to 11,983.
In the 4748 × 1220 fixture, traversal p95 changed from 3.38 to 2.29 ms and
UI-thread allocation from 627,847 to 543,050 bytes/frame. Full CPU-fallback
frame p95 varied from 11.02 to 12.56 ms, so this is not a demonstrated end-to-end
frame-rate improvement. The key responsiveness regression explicitly verifies
that input executes before a burst of 100 coalesced map requests and that close
cancels the queued frame. Reports: `fullscreen-before.trx` and
`fullscreen-after.trx` in the same results directory.

The zoom-stability revision's 4748 × 1220 CPU fixture recorded a 3.11 ms median,
8.13 ms p95, 12.92 ms maximum, and 8,181 peak tiles (`zoom-release.trx`). These
synthetic work timings do not measure visible GPU presentation or establish that
all real-drive stalls have been eliminated.

The geometry-cache revision keeps each detail layer's tiles in immutable,
camera-local geometry: a frame whose camera moved but whose detail did not
re-uses the same vertex buffer and changes only a projection matrix, and each
layer is flattened so every area is painted exactly once. Measured over 180
camera frames on 25,600 files (`geometry-cache.trx`): the 4748 x 1220 GPU
fixture ran a 0.38 ms median and 3.43 ms p95 with 23 geometry builds against
299 reused frames and 23 vertex uploads; the same fixture on the CPU fallback
ran 4.70 ms median, 9.71 ms p95 and 271,965 bytes/frame, down from 543,050.
The reuse regression asserts the ratio of reused to rebuilt frames rather than
an absolute count, because how many frames a contended compositor lets the
test prepare is not under the test's control. Test-driven frames now advance a
manual clock, so cache ageing cannot run ahead of camera motion on a slow host.
These are synthetic work timings and do not measure visible GPU presentation.

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


