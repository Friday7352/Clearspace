# Clearspace — Artifact 3 review package

September 29, 2026 · Tags and assignments migrated to SQLite

- **Clearspace-Artifact3-Original.zip**: application, tests, build files, and docs from
  `d810ca8304ae7294a80d7fc49777e4995e48435a`, before the database enhancement.
- **Clearspace-Artifact3-Enhanced.zip**: the corresponding working source plus the
  new database implementation, tests, and documentation. These changes are local
  and uncommitted on `codex/artifact-three-sqlite`.
- **Clearspace-Artifact3-Evidence.zip**: the final 180/180 passing Release test
  report, technical notes, and narrative draft.
- **MANIFEST.json**: SHA-256 checksums, baseline commit, and verification results.

The archives exclude generated binaries, earlier evidence packages, and unrelated
untracked files. The packaged application is separately available at
`dist/artifact-three/Clearspace.exe` and requires the .NET 10 Windows Desktop runtime.

Start with `docs/CS499-Artifact-Three-Databases.md` and
`docs/CS499-Artifact-Three-Narrative.md`. The narrative is a draft for review against
the assignment rubric. Automated migration tests used isolated fixtures. Payton
also reported that the supplied manual application checklist worked on September
29, 2026. The technical notes record the checklist and identify this as user-reported
verification.

## Proposed ePortfolio update

**Databases — SQLite tag storage**

Implemented SQLite storage for Clearspace's tag definitions and file/folder
assignments. Related tables, indexes, foreign keys, and parameterized queries
support consistent data and efficient lookups. Transactions make bulk changes
atomic, and a first-run migration preserves the original JSON file. Automated
checks cover migration, rollback, Unicode matching, concurrent access, and index
use. The full Release suite passed 180 tests.

File-lock metadata was removed from the planned scope after confirming that the
current application has no file-lock feature. The file-search index retains its
existing storage. Publish this update with the final code and narrative links
after reviewing the submission; this package does not publish the ePortfolio.
