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

Native file operations are simulated. Tag storage is isolated in memory and
folder preparation uses supplied test sources. These tests do not replace a
visual walkthrough or interactive checks of Windows file-operation dialogs.
