# Clearspace regression tests

Run on Windows with the .NET 10 SDK:

```powershell
dotnet test Clearspace.Tests/Clearspace.Tests.csproj
```

Run from the repository root. MSTest discovers the tests automatically.

- `FileOperationTests`: native outcome translation, shell request construction,
  validation, and retained operation feedback.
- `SearchCoordinatorTests`: source selection, cancellation, deduplication,
  filter-only fallback on indexed volumes, status restoration after clearing
  searches, final batches, missing items, hidden filtering, and result limits.
- `TagAndQueryTests`: tag persistence/cleanup and search parsing/matching.
- `NavigationAndSidebarTests`: load lifetimes, history, sorting, breadcrumbs,
  and sidebar composition.
- `FileIndexTests`: path reconstruction, filtering, cancellation, compaction,
  capacity growth, and result limits.
- `IndexingOverviewTests`: browsing actual indexed entries, removed-entry filtering,
  pagination without dropped entries, cancellation, bounded scan diagnostics, and
  truthful coverage details for older saved indexes. `DiskUsageWindowTests` also
  checks the overview's navigation, background loading, disposal, and wide/compact
  layout captures.
- `TraversalAndCoverageTests`: native traversal through 48 nested folders,
  cancellation, live subtree insertion, rename/delete replay, publication during
  concurrent changes, shared reparse policy, and isolated per-drive recovery.
- `AlgorithmLayoutTests`: deterministic compact layouts, proportionality,
  non-overlap, subtree ranges, stable placement, and snapshot cache replacement.
- `DiskUsagePerformanceTests` and `DiskUsageWindowTests`: dense scenes, CPU/GPU
  presentation, camera input, busy-buffer hit testing, navigation, and image captures.

Native file operations are simulated. Tag storage is isolated in memory and
folder preparation uses supplied test sources. Native traversal tests create
and clean up only GUID-named temporary fixtures. These tests do not replace a
visual walkthrough or interactive checks of Windows file-operation dialogs.
