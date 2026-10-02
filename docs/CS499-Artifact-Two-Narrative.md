# Artifact 2 narrative draft — Clearspace algorithms and data structures

This draft describes verifiable implementation decisions. Review it against the
assignment rubric and approved outcome plan, and add your own reflection before
submitting it.

## Artifact and purpose

Clearspace is a Windows file manager written in C# using WPF. This enhancement
extends its existing file index into a disk-usage analyzer and improves the
algorithms used to discover and search files. The original enhancement plan
identified two concrete needs: consistent traversal of deep folder structures
and efficient calculation of total folder sizes. Per-drive index health was
also identified as a reliability improvement.

The original index builder stopped descending after a fixed depth of 32, while
fallback search used a different limit of 24. That meant search results could
depend on the route used to find them. Both paths now traverse without arbitrary
depth counters. An explicit stack drives indexing, and concurrent per-volume
queues drive fallback search. These structures avoid recursive call-stack
growth while retaining cancellation and reparse-point checks. Supported cloud
placeholders can be enumerated; junctions and symbolic links are not followed,
which avoids directory-alias cycles.

## Algorithms and representation

The disk analyzer reuses each indexed entry's parent relationship and file size.
Because parents precede their children in the index, a reverse pass can add each
completed subtree total to its parent exactly once. This bottom-up aggregation
requires O(n) work rather than repeatedly walking every file's ancestor chain.
Parallel arrays hold byte totals, file counts and child/sibling links without
allocating an independent collection for every file. Validation rejects invalid
hierarchies and overflowing byte totals.

The visualization introduces additional data-structure tradeoffs. Its compact
layout stores rectangles and subtree endpoints in primitive arrays. A subtree
occupies a contiguous range, allowing the renderer to skip an entire off-screen
branch efficiently. Geometry can be reused while the camera moves, reducing
repeated layout work and uploads. StableTreemap retains row membership through
small changes in weights, trading occasional less-square rectangles for a map
that remains easier to follow.

Caching also requires explicit correctness rules. A cached snapshot is reusable
only for the same source volume instance and deletion exclusions. Replacing an
index must not reuse totals derived from the previous one. A regression test
now verifies that boundary. Cache capacity limits and background work reduce
repeated calculations without treating memory as unlimited.

## Reliability and validation

Index synchronization now has a per-volume recovery generation. A watcher error
on one drive leaves other drives available for indexed search. Only affected
roots are crawled. Recovery publishes a replacement after replaying recorded
changes, and a newer failure cannot be cleared by an older scan completing.
Publication and live updates coordinate so that events arriving during the swap
are not applied only to an index that is about to be discarded.

The final Release suite passed all 150 tests with no skipped cases. New tests
exercise actual 48-level directory structures, cancellation, renames, deletions,
live subtree insertion, concurrent index publication, isolated watcher failures,
and layout/cache invariants. Existing viewer tests exercise proportional areas,
navigation, dense folders, GPU contention and fullscreen rendering. The
contention test exposed hit coordinates advancing ahead of a deferred image;
using the displayed frame's camera corrected that inconsistency.

In the final synthetic fullscreen GPU fixture, synchronous frame work had a
4.45 ms p95 and reused geometry 687 times against 12 builds. These measurements
support an explanation of cache reuse, but do not measure visible GPU latency
or prove a universal frame-rate guarantee. The completion report records the
environment, results, historical comparisons and limitations.

## Evaluation and remaining limits

The enhancement demonstrates algorithm selection, compact hierarchical data
representation, complexity analysis, concurrency control, and verification of
observable behavior. Its main lesson is that speed and correctness must be
evaluated together: a fast index with incomplete coverage, a cache with stale
totals, or click targets detached from their tiles still produces an unreliable
experience.

Remaining limits are explicit. Access-denied folders are skipped; per-volume
health does not prove access to every directory. Indexed logical lengths can
differ from physically allocated space. Removing depth limits can increase
indexing work, and old saved indexes need a one-time rebuild. An interactive
walkthrough on real drives and a final mapping to the instructor-approved
course outcomes remain part of submission preparation.
