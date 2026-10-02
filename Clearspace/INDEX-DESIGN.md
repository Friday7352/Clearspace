# The file index

A design for instant search across every drive, built quietly in the background,
with no elevation at any point.

Status: built.

September 23 update: indexing and fallback search now traverse without arbitrary
depth caps. Search coverage and recovery are tracked per volume; a failed drive
does not force a crawl of healthy drives. Loaded indexes receive a background
catch-up scan before claiming live search coverage. See
`../docs/CS499-Artifact-Two-Completion.md` for the current implementation and tests.
The design discussion below also contains historical measurements and proposals.

`Services/FileIndex.cs` (store), `FileIndexBuilder.cs` (background walk),
`FileIndexStore.cs` (persistence), `FileIndexService.cs` (ownership and queries),
`IndexOverlay.cs` (changes since the walk) and `FileIndexWatcher.cs` (watching).

When the index is live and covers every search root, the disk crawl does not run
at all - the answer comes entirely from memory.

Measured on an 11.3 million file machine: 3,877 matches across all drives in
317 ms before the latency work below, from an index that walks in the background
at low I/O priority and reloads from disk in under a second.

### One gap, and why it is the shape it is

A watcher only runs while Clearspace does. Nothing observes the filesystem while
the app is closed, and without the USN journal - which needs administrator rights
this design has ruled out - there is no record to catch up from afterwards.

A directory's modified time was going to be the trick: walk the tree, skip any
folder that has not changed since the index was built. It does not work. Adding a
file deep in a tree updates only its immediate parent's timestamp, not its
ancestors', so pruning on mtime would skip precisely the subtrees that changed.
Finding out what happened costs a full walk either way.

Saved volumes now enter per-volume recovery on startup. Their saved data remains
available to the disk map, while searches crawl the affected roots until the
background catch-up scan publishes a replacement. Checking that existing hits
still exist cannot discover files created while the app was closed, so pruning
alone is not treated as complete coverage. During the session, watcher errors
queue recovery for their own volume, throttled to avoid repeated scans during
heavy activity. Queries still prune vanished local hits as a secondary check.

## What actually makes search instant

Everything (voidtools) is the reference point, and the usual explanation for its
speed is that it reads the NTFS Master File Table. That is half right, and the
half it gets wrong matters here.

Reading the MFT is what makes Everything's *index build* fast: one pass over
`FSCTL_ENUM_USN_DATA` hands back every file on a volume in seconds, with no
directory traversal. But the MFT is not what makes its *searches* fast. Searches
are fast because a million filenames sit in RAM in one compact array, and
scanning a compact array is a few milliseconds.

Those are separable. The array is the product; the MFT is one way to fill it.
And filling it that way is precisely the part that needs administrator rights,
because it requires raw read access to the volume - which is why Everything
ships a service running as SYSTEM.

Clearspace runs `asInvoker` on purpose (see ARCHITECTURE.md, "Access and
elevation"), and that decision stands. So: build the array, fill it with an
ordinary directory walk, and accept a slower first build in exchange for never
asking for administrator, working on every filesystem rather than only NTFS, and
having no service to install.

The first build costs minutes instead of seconds. It happens once, in the
background, at low I/O priority. Every launch after that loads from disk.

## Goals

- A name/path query over a million entries answers in single-digit milliseconds
- Building it is not perceptible while using the machine - CPU *and* disk
- No elevation, no service, no installer changes
- Works on NTFS, exFAT, FAT32, and network shares alike
- Survives restart; catching up is cheap, not a rebuild
- Degrades safely: a missing, stale, or partial index never breaks search, it
  just falls back to the crawl that exists today

## Non-goals

- File contents. That stays with `WindowsSearchService`, which has the Windows
  index and its IFilters behind it and can answer questions a name index cannot.
- MFT and USN journal reading. Ruled out by the no-elevation goal above.
- Matching Everything's first-build time. We are trading that away deliberately.

## The store

Struct-of-arrays, because a million small objects on the GC heap is a different
kind of program than a million entries in two flat buffers.

```csharp
internal struct IndexEntry
{
    public long Size;
    public long ModifiedTicks;
    public int NameOffset;      // into the name pool
    public int ParentIndex;     // into the entry array; -1 for a volume root
    public FileAttributes Attributes;
    public ushort NameLength;
}
```

Thirty bytes of fields, thirty-two with alignment. A million files is 32 MB of
entries.

Names live in one `char[]` pool, appended in build order, `\0` separated. At an
average of twenty characters that is another 40 MB per million files.

A second pool holds the same names pre-lowercased. This is not redundancy: it is
the difference between matching with `StringComparison.Ordinal` and
`OrdinalIgnoreCase`, and ordinal comparison is several times faster. Folding once
at build time instead of on every keystroke is the entire reason a query stays
under ten milliseconds. It is not persisted - regenerating it on load is faster
than reading it back from disk.

Around 112 MB for a million files, against Everything's ~100 MB for the same.
Close enough to say the approach is sound.

If that ever needs halving, the pools can become UTF-8 `byte[]` - most filenames
are ASCII, so it is close to a straight 2x saving, and `ReadOnlySpan<byte>.IndexOf`
is if anything faster. It complicates case folding for non-ASCII names, so it is
a tuning step, not a starting point.

### Paths are not stored

`ParentIndex` chains to the volume root. A full path is built by walking up,
collecting names, and reversing - done only for results actually displayed, never
for the index as a whole. Storing a full path per entry would roughly triple the
pool for no gain.

### Volumes

```csharp
internal sealed class IndexedVolume
{
    public required string Root;         // "C:\", "\\server\share\"
    public int RootEntryIndex;
    public DateTime LastBuilt;
    public uint SerialNumber;            // from GetVolumeInformationW
}
```

The serial number is the guard against a reformatted drive or a recycled letter.
On load, a mismatch drops that volume's entries and queues a rebuild rather than
serving results for files that no longer exist.

## Building

### Not on the thread pool

The same lesson as `FileSearchService`: a worker that spends its life blocked on
directory reads does not belong in the shared pool, where it starves everything
else. One dedicated thread per volume, at most two volumes at a time.

Note the contrast with search, which runs up to sixteen workers on an NVMe drive.
That is correct there - a user waiting on a search wants the disk saturated. It
is wrong here. Nobody is waiting on the index, so throughput is worth nothing and
invisibility is worth everything.

### Background I/O priority is the whole trick

```csharp
SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN); // 0x00010000
try { /* walk */ }
finally { SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END); } // 0x00020000
```

`ThreadPriority.Lowest` alone only lowers CPU priority, and this job is not CPU
bound - it is disk bound. `THREAD_MODE_BACKGROUND_BEGIN` puts the thread into
Windows' background mode, which lowers its **I/O** priority as well, so the disk
serves foreground work first. It applies to the calling thread only, which is why
it is called from inside the worker rather than on the handle from outside.

This single call is the difference between an index that builds unnoticed and one
that makes the machine feel unwell. It is worth being precise about it.

### The walk

`DirectoryEnumerator` already does the right thing - `FindFirstFileEx` with
`FindExInfoBasic` and `FIND_FIRST_EX_LARGE_FETCH`, and find data that already
carries size, dates, and attributes, so a finished entry costs no extra disk
access. The builder reuses it unchanged.

Reparse points are not descended into, for the same reason the search crawl skips
them: junctions and symlinks form cycles.

Hidden and system files *are* indexed. Filtering happens at query time against
`ShowHiddenItems`, so toggling that setting is instant rather than a rebuild.

Nothing else is skipped. `C:\Windows\WinSxS` is enormous and boring, but an index
with holes in it is an index you cannot trust, and the whole promise here is that
a result not found means the file is not there.

### When it runs

- Ten seconds after startup, so it never competes with the first window
- Only when there is no saved index, a volume is unknown, or a volume is stale
- Per-volume generations: the builder fills its own arrays and swaps a finished
  volume in atomically, so C: becomes searchable while D: is still building

There is no attempt to make a half-built volume queryable. The complexity is real
and the payoff is a few seconds of partial results on first run only.

## Staying current

One `ReadDirectoryChangesW` watcher per volume root, `bWatchSubtree = TRUE`, on
`FILE_NAME | DIR_NAME | SIZE | LAST_WRITE`, with a 64 KB buffer (the ceiling for
network paths).

Applying deltas:

| Change | Action |
| --- | --- |
| Added | Append an entry, resolve the parent |
| Removed | Tombstone the entry |
| Renamed | Append the new name to the pool, repoint the entry |
| Modified | Update size and date in place |

The pool is append-only, so renames leak a little; compaction happens when the
index is written to disk. Keeping the hot path free of bookkeeping is worth a few
stale characters between saves.

Tombstones rather than compaction on delete, for the same reason: every
`ParentIndex` in the array is a positional reference, and compacting would mean
rewriting all of them.

### The overflow case

If changes arrive faster than the buffer drains, Windows drops them and reports
`ERROR_NOTIFY_ENUM_DIR`. There is no way to recover the lost notifications, so
that volume is marked dirty and rebuilt in the background. Everything's own
folder indexing has exactly this limitation and handles it the same way.

### Catching up after a restart

The pruned walk this section originally proposed does not work, and the reason is
worth keeping: a directory's modified time changes when an entry is added to or
removed from *it*, but not when something changes further down. Ancestors are
untouched. Pruning on mtime would therefore skip the very subtrees that changed.

What replaced it is verification at the result, described under Status above.

## Searching

```csharp
FileIndexSearchService.Search(SearchQuery query, int limit, CancellationToken token)
```

1. Fold the query's terms once, up front.
2. `Parallel.For` over ranges of the entry array. Each worker matches against its
   slice of the folded pool and keeps a local result list; the lists merge at the
   end. Cancellable, and stops early at `limit`.
3. Materialise `FileSystemItem`s for the capped result set only - **from index
   data**, since size, dates, and attributes are all already there.
4. Run the existing `query.Matches` over those items for the full filter
   semantics: extensions, folder types, tags, everything the search language
   already supports.

Step 3 is the one worth dwelling on. Materialising from the index means a search
touches the disk zero times. Compare `FileSystemItem.FromLocation`, which costs
several stat calls per hit - the thing that was freezing the window on the
Windows-index path until it was moved off the UI thread. Here the problem does
not exist, because the data never left memory.

Step 4 means none of the query language has to be reimplemented against index
fields. The scan does cheap name and attribute matching over a million entries;
the expensive, expressive matching runs over the few hundred that survived.

### Where it fits in the existing search

`RunTreeSearchAsync` becomes, in order:

1. Local matches from `_directoryItems` - unchanged, still instant
2. **The index** - instant, and replaces the crawl for every indexed volume
3. The Windows index - unchanged, still the only thing that reads file contents
4. The crawl - now only for volumes that are *not* indexed, or still building

The crawl stays as the completeness guarantee. It stops being the common path.

Which is worth noting against the work already done on that path: coalescing
result publishing, moving icon lookups off the UI thread, LIFO thumbnails. Those
fixed the symptoms of streaming thousands of results in from a live disk walk.
Once volumes are indexed, there is no walk and nothing to stream - the results
are already known, so they arrive in one publish. The streaming machinery stays
for un-indexed volumes and stops being exercised in the ordinary case.

## Persistence

`%LOCALAPPDATA%\Clearspace\index.db`

```
magic + format version
volume records (root, serial, last built, root entry index)
entry count, pool length
entry array      <- MemoryMarshal.AsBytes, one write
name pool        <- one write
```

The folded pool is regenerated on load. Saved on exit, and every five minutes
while dirty, so a crash costs minutes rather than the whole index. The format
version means a change to the layout triggers a rebuild instead of a crash.

Loading is a couple of large sequential reads into buffers - well under a second
for a million files, against minutes to rebuild. That asymmetry is what makes the
first build acceptable.

## New files

```
Services/FileIndex.cs           entries, pools, add/remove/rename, path building
Services/FileIndexBuilder.cs    background walk, background I/O priority, generations
Services/FileIndexWatcher.cs    ReadDirectoryChangesW per volume, delta application
Services/FileIndexStore.cs      index.db read and write
Services/FileIndexSearch.cs     the query path
```

Additions to `Native/NativeMethods.cs`: `SetThreadPriority`, `GetCurrentThread`,
`ReadDirectoryChangesW`, `GetVolumeInformationW`.

Touched: `MainViewModel` (start the builder, consult the index in
`RunTreeSearchAsync`), `SettingsService` (enabled, which volumes, last build),
and `ARCHITECTURE.md` once it exists.

## Expected numbers

To be replaced with measurements, not trusted as written.

| | |
| --- | --- |
| Memory, 1M files | ~112 MB |
| On disk, 1M files | ~72 MB |
| First build, 1M files | 2-6 minutes at background I/O priority |
| Load on startup | well under a second |
| Query over 1M entries | single-digit milliseconds |

## Risks

- **Memory on a multi-drive machine.** Four large drives is not 112 MB, it is
  half a gigabyte. Needs a per-volume opt-out and a ceiling, with the largest
  volumes off by default rather than a surprise.
- **Network shares.** Crawls are slow and watchers are unreliable over SMB.
  Indexed only when explicitly asked for, never automatically.
- **Watcher overflow under heavy churn** - a build, an unzip, an installer - costs
  a background volume rebuild. Acceptable, but it should be visible in the status
  bar rather than silent.
- **The staleness window** between launch and catch-up completing. Small, but real,
  and worth a status line rather than pretending it does not exist.
