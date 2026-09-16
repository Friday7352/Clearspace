# Milestone Two: software engineering and design

## Completed coding scope

The software engineering work from the review plan is implemented. This change
separates independent responsibilities, preserves the existing view bindings,
adds meaningful error results, and introduces isolated regression tests.

| Responsibility | Implementation |
| --- | --- |
| Search orchestration | `SearchCoordinator` owns debounce, cancellation, result merging, source selection, progress, and completion. `ISearchSources` separates this workflow from the real Windows/index/crawler services. |
| Navigation loading | `NavigationCoordinator` owns load lifetimes, cancellation, sorting, and background preparation. `NavigationBreadcrumbs` builds breadcrumbs independently. `MainViewModel` retains screen-specific layout and status presentation. |
| Sidebar state | `SidebarViewModel` owns the collection, drive discovery, pins, categories, and settings commands. `SidebarComposer` builds entries from supplied data and can be tested without changing settings. |
| File-operation results | `FileOperationResult` distinguishes success, cancellation, invalid input, and Windows failures. All copy/move/rename/delete callers consume the result. |
| Tag behavior | `TagStore` owns the existing JSON persistence and tag operations. `TagService` remains the application's shared entry point. Tests supply isolated storage instead of using personal tags. |

Supporting location constants, hub materialization, sidebar entries, tag options,
and breadcrumbs have their own files. `MainViewModel` delegates to these
components instead of containing their implementations. Window closure cancels
pending search/navigation work and removes its subscriptions to shared services.

## Bugs corrected and covered by regression tests

- The Windows Search preference now controls whether that source is called.
- A failing private index allows the crawler fallback even when coverage was reported.
- Filter-only queries such as `ext:txt` and `is:folder` use the crawler even on
  indexed volumes: the private filename index cannot answer queries without terms.
- Clearing a pending or completed search restores the normal folder/hub status
  as well as its items. Navigation updates can still preserve their own status.
- Canceled or superseded searches cannot publish late results over a newer search.
- The last crawler batch is consumed before final results are published.
- Duplicate paths are removed case-insensitively across sources; hidden/system
  filtering and the existing overall result cap are applied consistently.
- Unsupported structured filters remain literal terms instead of accidentally
  broadening the query to match everything.
- Deleting every tag persists an empty list instead of recreating defaults on reload.
- Moving a tag assignment merges with destination tags; a successful save clears
  a previous save error.
- An empty compacted file index can grow again when its first entry is added.
- Old folder-load failures and queued partial batches cannot replace newer navigation.

## Verification

87 automated tests passed in Release after the search fixes (the earlier
75-test suite also passed in Debug). The WPF application and test project
compiled successfully. Tests exercise controlled
source responses and in-memory storage; they do not perform real copy, move,
rename, or delete operations on user files or modify personal tags/settings.

```powershell
dotnet test Clearspace.Tests/Clearspace.Tests.csproj --verbosity minimal
dotnet test Clearspace.Tests/Clearspace.Tests.csproj -c Release --logger "trx;LogFileName=module-two.trx" --results-directory output/test-results/module-two
```

The saved Release report is `output/test-results/module-two/module-two.trx`.
To build and launch the development app, use the existing `Run Clearspace.cmd`.
It closes an existing Clearspace process before rebuilding, so finish any work
in that instance first.

An initial manual walkthrough checked folder navigation, hidden-item visibility,
filename search, copy/rename/move feedback, copy cancellation, This PC, and pinning
with disposable files. It exposed the two search bugs documented above; their
fixes are verified by regression tests but have not been rechecked in the GUI.
Remaining manual checks include rapid search switching, the Windows Search
preference, tags, deletion, restart persistence, and layout resizing. Automated
tests do not replace native Windows dialog or visual checks.

## Assignment boundary

The planned coding scope for software engineering/design is complete. The search
depth changes, folder-size aggregation, index-health features, and SQLite
migration belong to subsequent algorithm/database enhancements and remain planned.

The full milestone submission still needs its reflective Word narrative and a
ZIP containing the original and enhanced technical artifact files. These coding
notes support the narrative but are not a substitute for that reflection or the
Module One outcome-coverage plan approved by the instructor.
