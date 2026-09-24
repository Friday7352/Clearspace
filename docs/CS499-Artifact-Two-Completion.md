# Artifact 2 — implementation and evidence

Updated September 23, 2026. Scope: algorithms and data structures in Clearspace.

## Original plan and delivered changes

The original code-review speaking guide (page 13) planned two related changes:
remove or configure arbitrary traversal-depth limits, and aggregate folder sizes
with a bottom-up pass over parent relationships. The review outline (page 8)
also called for index health to be tracked per volume. Planning sources are in
`output/pdf/Clearspace_CS499_Detailed_Speaking_Guide.pdf` and
`output/pdf/Clearspace_CS499_Code_Review_Outline.pdf`.

| Requirement | Current implementation | Evidence |
| --- | --- | --- |
| Consistent deep traversal | Index builder and live subtree scans use an explicit stack; fallback search uses per-volume concurrent queues. Neither has an arbitrary depth limit. | Native 48-level fixture finds the same file through both paths, then verifies rename/deletion and live subtree insertion. |
| Responsive cancellation and safe traversal | Cancellation is checked during enumeration and between directories. Search joins its workers before releasing their semaphores. Both paths share a reparse-tag policy: cloud placeholders are allowed; junctions and symbolic links are not followed. | Cancellation during both traversals; shared policy cases for cloud tags, junctions and symbolic links. |
| Per-drive index health | Each volume has its own bounded overlay, lost-event flag and recovery generation. Failed watchers are disposed and retried. Failed or offline rescans stay pending. A successful replacement cannot erase a newer failure. | Overlay capacity, watcher failure, recovery generation and selective fallback tests. |
| Changes during recovery | Event recording, replay, publication and live updates coordinate through the updater gate. A file arriving during the index swap reaches the published index. | Concurrent publication regression. |
| Folder aggregation | A reverse pass adds each accumulated subtree total to its parent once; parallel arrays retain totals and child links. | Existing nested totals, overflow, cancellation and 100,000-level synthetic hierarchy tests. |
| Compact, stable layouts | The flat layout stores geometry and subtree ranges in primitive arrays. StableTreemap retains row membership during small changes in weights. | New deterministic-layout, proportionality, non-overlap, subtree range and position-stability tests. |
| Cache correctness | Reuse requires the same source volume instance and exclusion signature. A replaced index cannot borrow stale totals from an older snapshot. | Cache reuse/replacement/exclusion regression. |

The viewer retains its current layout, palette and navigation. The only rendering
change in this completion pass makes hit testing use the camera of the displayed
frame when the GPU defers a newer frame. The existing busy-buffer regression
failed before this fix and passes afterward.

The file-index storage version advances from 2 to 3 without changing record
layout. Old depth-limited indexes are rebuilt once in the background. On later
launches, saved volumes remain available to the map but undergo a catch-up scan
before search treats them as live coverage; files created while the app was
closed cannot be discovered by checking old results for existence.

## Algorithms and tradeoffs

- Traversal examines each reachable entry once, O(n) enumeration work excluding
  filesystem latency and metadata/path costs. Stack/queue memory depends on the
  pending directory frontier, up to O(n), rather than using the call stack.
  Removing a cap improves completeness but allows more work on deep trees;
  cancellation and the existing index memory budget still bound operation.
- Aggregation is O(n) time and O(n) additional space, versus walking every file's
  ancestor chain, O(n × d). Snapshot array payload is approximately 25 bytes per
  indexed item, excluding the source index, headers and temporary allocations.
- A flat-layout subtree occupies a contiguous range `[entry, End(entry))`, so
  culling skips it with one jump. Arrays use 27 payload bytes per allocated slot;
  capacity slack, groups, build buffers and the source index cost extra. Child
  sorting costs O(k log k) per folder. Rendering still depends on visible work;
  cached geometry does not make GPU rasterization constant-time.
- Stable layout trades occasional longer rectangles for spatial continuity.
  Significant new items or excessive aspect ratios trigger a fresh arrangement.
- Overlay storage has a shared 100,000-path ceiling and a per-volume removed-tree
  ceiling. An overflowing volume releases its retained paths and falls back to
  crawling, while other volumes keep their state. Recovery costs a scan; repeat
  recovery attempts are throttled to avoid rescanning continuously under load.

## Verification

Final command, run on Windows with .NET SDK 10.0.401:

```powershell
dotnet test Clearspace.Tests/Clearspace.Tests.csproj -c Release --no-restore --logger "trx;LogFileName=completion-final.trx" --results-directory output/test-results/artifact-two-completion
```

**150 passed, 0 failed, 0 skipped**, completed September 23 at 13:07 local time.
The original suite had 131 cases; this pass adds 19 cases. The full suite was
rerun after the final code change. The authoritative result is
`output/test-results/artifact-two-completion/completion-final.trx`.

Earlier failures and corrections are retained in the same evidence directory:

- Busy-GPU hit coordinates advanced while displayed tiles stayed still: fixed
  in production hit testing and the visible-tile diagnostic.
- The prefetch test assumed older thresholds and no idle prebuilding: its
  fixture now isolates moving-camera prefetch, advances frames, and still
  asserts one speculative load at a time.
- The navigation fixture assumed the old root zoom clamp and retained item
  objects. It now waits for the larger gesture to settle and supplies the same
  item resolver and snapshot key as the production view. Existing assertions
  for wheel navigation, proportionality, dense detail and deletion remain.

Measurements from the final run:

| Fixture | Result |
| --- | --- |
| Native 48-level index fixture | 4.75 ms build; 327,680 estimated index bytes including reserved capacity |
| Flat layout, 30,000 files | 25.27 ms build; 30,203 tiles; 850,392 retained array bytes |
| CPU fallback, 1280 × 800, 25,600 files | 0.49 ms median; 2.16 ms p95; 6.12 ms maximum synchronous frame work |
| CPU fallback, 4748 × 1220, 25,600 files | 1.39 ms median; 12.11 ms p95; 16.26 ms maximum synchronous frame work |
| GPU path, 4748 × 1220, 25,600 files | 0.12 ms median; 4.45 ms p95; 11.05 ms maximum synchronous frame work |
| GPU geometry reuse in that fixture | 12 builds, 687 reused frames, 12 uploads |
| 100,000-file integration fixture | 249 ms initial setup/render path; 46,224 recorded tiles; deeper zoom exposes files |

The dense timing fixtures explicitly drive 180 camera frames; additional WPF
frames also contribute to cache counters. Timings do not measure end-to-end
display latency, hardware GPU execution time, or real-drive frame pacing.
The first dense frame includes asynchronous setup and is not a steady-state
frame measurement. These results cannot establish that all freezing or flashing
on personal drives is eliminated. Historical before/after reports remain in
`output/test-results/performance/`; several changes separate those reports from
this version, so timing differences cannot be attributed solely to this pass.

By code inspection, the former 24/32-level limits excluded a file 48 directories
below the root. The new fixture demonstrates retrieval through both paths.
Similarly, the old shared watcher flag disabled coverage globally; the new test
demonstrates that only the unhealthy root is crawled. These are behavior
comparisons, not timing measurements of the old executable.

## Submission materials and remaining human review

- `CS499-Artifact-Two-Narrative.md` provides a source-grounded narrative draft.
- `output/artifact-two/` contains original/enhanced source archives, a manifest
  and the final evidence archive. The original baseline is commit
  `03a7b023e24306a503c14177500ed0681d610181` (ArtifactOneAdditions), before the
  disk-usage enhancement. The enhanced archive includes current untracked source
  files, including the new hardware views and layout algorithm.
- The official assignment rubric and approved course-outcome mapping were not
  available in the repository. Match the narrative to those documents before
  submitting; no specific grade or outcome-number compliance is asserted here.
- Perform the final interactive walkthrough on actual drives, including an
  unavailable drive and sustained fullscreen zooming. Automated captures use
  synthetic data, and the native filesystem tests only touch isolated fixtures.
- Review the narrative in your own voice and publish the selected evidence to
  the ePortfolio. Nothing was submitted or published externally by this pass.

Access-denied folders and disallowed reparse targets remain intentionally
unscanned. Per-volume health describes event synchronization; it is not a proof
of full per-directory access coverage. Logical file lengths are not physical
allocated space. No change was made to permanent-deletion confirmation.
