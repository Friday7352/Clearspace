# Artifact 3 — SQLite tags and file locking

## Scope and baseline

Clearspace's database enhancement replaces JSON persistence for tag definitions and
file/folder tag assignments. The starting commit is
`d810ca8304ae7294a80d7fc49777e4995e48435a` on `ArtifactTwo`.
Implementation branch: `codex/artifact-three-sqlite`.

The September 29 implementation covered tags and assignments. After checking the
original Module One plan, Payton requested the planned file-lock feature on October
1. The current enhancement also provides password-based encryption and SQLite lock
metadata. See [file-locking notes](CS499-File-Locking.md) for the schema version 2
upgrade, encryption design, recovery behavior, tests, and initial size/type limits.
The file-search index and disk usage data retain their separate storage. SQLite is
used through `Microsoft.Data.Sqlite` 10.0.12. The sections below describe the tag
portion; the new notes document the added file-lock portion.

## Problem and resulting behavior

Previously, the store loaded all of `tags.json` into collections and rewrote the
whole document for each change. Reverse tag lookup scanned assignments. Failed
saves could leave the in-memory state ahead of the saved file, and malformed JSON
silently fell back to defaults.

The new store queries indexed tables and commits changes before reporting success.
For example, assigning a tag to two selected files either saves both assignments
or saves neither. Creating a new tag for a selection also saves its definition and
assignments in one transaction. Deleting a tag automatically removes its assignments
through foreign keys. Existing tag names, colors, ordering, and case-insensitive
matching are retained.

## Schema and indexes

| Table | Purpose | Keys and constraints |
| --- | --- | --- |
| `Tags` | ID, display name, color | ID primary key; required nonblank values |
| `Paths` | Each path that has tags | Path primary key; required nonblank path |
| `PathTags` | Many-to-many assignments | Composite `(Path, TagId)` primary key; foreign keys to both tables with cascade deletion |

`IX_Tags_Name` supports resolving display names. `IX_PathTags_TagId_Path` supports
finding paths by tag and covers that query without a second table lookup. The
composite assignment primary key supports finding tags by path and prevents
duplicate assignments. Display names are not unique: the old rename behavior
allowed duplicate names, so imposing uniqueness would reject otherwise valid data.

Every connection registers `CLEARSPACE_NOCASE`, backed by .NET's
`StringComparer.OrdinalIgnoreCase`. SQLite's built-in `NOCASE` only folds ASCII;
the custom collation preserves the application's existing Unicode comparisons.
Queries bind user values as parameters. SQL-like text in names and paths remains
literal data. Foreign keys are explicitly enabled on every application connection.

## First-run migration and recovery

The application opens `%APPDATA%\Clearspace\tags.db`. The legacy source remains
`%APPDATA%\Clearspace\tags.json`.

1. Open the database and inspect `PRAGMA user_version` inside a transaction.
2. For a new database, create the schema and import definitions and assignments.
3. Install the lock tables and set schema version 2, committing schema and import together.
4. Upgrade existing version 1 databases transactionally, without reimporting JSON;
   reopen version 2 directly on later runs.

The original JSON is never edited or deleted. A missing JSON file initializes the
seven default tags. An intentionally empty legacy tag list remains empty. Repeated
case-equivalent assignments collapse into one relation. Paths on offline drives
are imported without existence checks.

Malformed JSON, missing required fields, conflicting tag IDs, unknown tag
references, or unreadable input stop the migration. No partial schema or import
is committed. After correcting the source, retrying the same store or restarting
the application retries the import. Existing corrupt databases, unrecognized
schemas, and unsupported future versions are reported; they are never silently
replaced with defaults. Startup shows the storage error and closes if tags cannot
be opened. This intentionally favors preserving metadata over opening a session
with an empty replacement store.

The retained JSON is a pre-migration recovery source, not a synchronized backup of
later SQLite changes. For a current backup, close Clearspace and copy `tags.db`.
Do not delete the database to retry an import after making new changes in SQLite:
that would discard those changes. Older Clearspace builds still use JSON and do
not see changes made by this version.

## Transactions, errors, and search

All mutations use explicit transactions, including bulk toggle/clear, creation for
a selection, deletion with cleanup, and merging assignments during a path move.
A store serializes use of its connection; separate stores rely on SQLite's writer
locking. A two-second busy timeout bounds lock contention. Readers receive
materialized results, with no open reader escaping the store.

`Changed` is raised only after a commit with actual changes. Failed writes throw a
descriptive error and populate `LastSaveError`; a later successful write clears it.
Selection actions show the failure in the status area and reload tag checkboxes
from committed data. Search catches database-read failures and reports incomplete
results. Connections are disposed at application exit and after tests.

The current TagStore serves tag definitions and assignments from a cached snapshot,
refreshing after its own writes or a change detected with SQLite data_version.
Parsed searches capture the relevant assignments for matching in memory. Changing
tags in the current window restarts an active search with a fresh snapshot. This
avoids querying the database separately for every entry in a drive scan.

## Verification

The test suite uses temporary databases and legacy fixtures, independent of the
user's tag files. Coverage includes:

- First-run import, retained source bytes, empty data, one-time migration, and restart persistence.
- Unicode/case equivalence, duplicate assignments, and literal SQL-like input.
- Malformed/unreadable sources, conflicting IDs, unknown references, corruption, and future versions.
- Foreign-key enforcement, cascade deletion, and unique assignment constraints.
- Forced failures midway through bulk assignment, creation, deletion, and path moves.
- No success notification after rollback, recovery, and concurrent writes through two stores.
- Matching after closing the database, and graceful search failure when parsing cannot read tags.
- A 10,000-assignment fixture whose query plan uses the reverse covering index.

Historical tag-only Release suite: **180 passed, 0 failed, 0 skipped** on September 29, 2026.
Current full Release suite with file locking: **251 passed, 0 failed, 0 skipped** on
October 1, 2026, recorded in `output/test-results/milestone-four/milestone-four.trx`.
Release publishing also completed successfully.
Test report: `output/test-results/artifact-three/artifact-three.trx`.
Run command:

```powershell
dotnet test Clearspace.Tests/Clearspace.Tests.csproj -c Release --no-restore --logger "trx;LogFileName=artifact-three.trx" --results-directory output/test-results/artifact-three
dotnet publish Clearspace/Clearspace.csproj -c Release --no-restore -o dist/artifact-three
```

The published build is in `dist/artifact-three`. Automated migration checks used
isolated fixtures. After receiving the manual checklist, Payton reported on
September 29, 2026: "all those tests seem to work." This covers checking existing
tags and migration files, creating and assigning a test tag to two files, tag
search, restart persistence, rename retention, removing one assignment, deleting
the test tag, and verifying deletion after another restart. These are user-reported
results; no independent UI recording or live database inspection was collected.

Filesystem renames and database commits remain separate
operations; this work does not claim an atomic transaction across the filesystem
and SQLite. `PruneMissing` retains its existing explicit-call semantics and should
not be used on temporarily unavailable paths. Cross-process changes become visible
to fresh queries, but do not automatically notify another open window.

## Source map

- `Clearspace/Services/TagDatabase.cs`: connection ownership, schema, import, SQL helpers.
- `Clearspace/Services/TagStore.cs`: tag operations and transactions.
- `Clearspace/Services/TagService.cs`: application storage paths and facade.
- `Clearspace/Models/SearchQuery.cs`: indexed tag snapshots for matching.
- `Clearspace/Services/SearchCoordinator.cs`: search error handling.
- `Clearspace/ViewModels/MainViewModel.cs`: bulk creation and failure feedback.
- `Clearspace/App.xaml.cs`: startup migration and connection disposal.
- `Clearspace.Tests/TagDatabaseTests.cs`: migration, integrity, rollback, and query-plan evidence.

## References

- [Microsoft.Data.Sqlite parameters](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/parameters)
- [Microsoft.Data.Sqlite transactions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions)
- [Microsoft.Data.Sqlite collations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/collation)
