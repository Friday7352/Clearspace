# Human-centered search and fast indexing

Design discussion · September 29, 2026 · Proposed work after Artifact 3

## Product intent

Find the files and folders the person most likely means using ordinary words.
Recognize tags, file types, filenames, folder context, and words inside documents.
Explicit filters remain available for precise refinement. Index new files and
refresh changed files quickly while keeping typing and navigation responsive.

This document proposes future behavior. It does not change the completed
Artifact 3 implementation or claim that these features have been built.

## Search behavior

| Input | Proposed interpretation and result order |
| --- | --- |
| `work` when a Work tag exists | Show a Work tag action first, then files/folders assigned that tag, followed by other relevant name/path/content matches. |
| `work budget` | Prefer budget-related items with the Work tag; retain plausible filename and path interpretations. |
| `home repairs quote` with a Home Repairs tag | Recognize the whole tag name and use quote as the remaining search term. |
| `photos beach` | Prefer images and relevant photo folders associated with beach; also retain strong literal filename matches. |
| `school budget` | Match a budget file in a School folder even if school is absent from its filename. |
| `renewal clause` | Find supported documents containing these words and show a matching excerpt. |
| `tag:work ext:pdf budget` | Enforce Work and PDF constraints, then rank budget matches. |

Plain words provide evidence about intent. Explicit syntax applies strict
constraints. Avoid silently turning an ambiguous ordinary word into a constraint
that hides otherwise strong matches. Quoted phrases retain phrase meaning.
Recognize multiword tags before interpreting each word independently.

The tag action is a distinct navigation/refinement result, not a fictional folder.
Selecting it narrows to that tag. Tagged file and folder results are real items
and remain directly openable. Initially show a small best-results area with access
to the full results. Keep keyboard selection stable as slower results arrive.

Each result should have a clear filename, containing folder, type, and a brief
match explanation when useful: Tagged Work, In School, or a content excerpt.
Display understandable labels instead of exposing scores or query machinery.

## Retrieval and ranking

Represent the interpreted query once and use it across all search sources.
Represent match evidence separately from FileSystemItem so name, tag, path, type,
and content matches can contribute to relevance without pretending all hits are
filename matches. Exact tag queries have the explicit priority described above.

For other searches, combine exact/phrase/prefix name matches, exact versus partial
tag matches, path context, inferred file types, and content relevance. Current
folder and modification time can break close ties; they should not overwhelm an
unambiguous match. Consider typo tolerance after exact matching is reliable.
Personalized history-based ranking would be a separate product decision.

Apply scope and explicit constraints before truncating candidates. Compare useful
matches across every relevant drive before finalizing the best results. Preserve
bounded memory through a bounded best-results collection and further-result
retrieval. Cancellation must reach every source. Avoid selecting the first
10,000 encountered items and ranking only that subset.

Tag, type, and extension searches must work directly from indexed data even when
there is no filename term. Support terms whose evidence comes from different
fields, such as a tag plus a filename, or a path plus document text.

## Indexing architecture

### Fast file metadata

Make names, paths, types, sizes, and timestamps searchable as soon as batches are
discovered. Prioritize the current folder and likely user locations during a
first build, then complete the configured scope. Benchmark bounded parallel
enumeration per physical device; more workers do not necessarily improve an HDD.
Avoid duplicate traversal of overlapping roots.

Continue serving the previous complete snapshot during reconciliation, with
explicit freshness information and live changes applied. Do not present stale
coverage as fully current. Refresh one affected file or subtree when possible.
Persist changes in batches rather than rewriting every volume after a small edit.

For supported NTFS volumes, investigate filesystem metadata enumeration for the
initial build and the USN change journal for subsequent catch-up. Persist the
volume identity, journal identity, and processed checkpoint together with changes.
Use stable file identities to handle renames. Verify available permissions and
fall back to ordinary enumeration and watchers when these APIs are unavailable.
If journal history is missing or replaced, reconcile the affected volume.

### File contents

Build a local searchable text index asynchronously. Read and extract a document
when it is new or changed, rather than rereading all documents on each keystroke.
Track source identity/version and extractor version, and discard obsolete work if
the file changes during extraction. Remove content when a file is deleted.
Prioritize newly changed and relevant documents over a large initial backlog.

Proposed first format group: plain text, Markdown, source code, and CSV.
Next: Word, Excel, PowerPoint, and PDFs containing extractable text, with tested
format-specific extractors or available Windows filter handlers. SQLite itself
does not extract text from these formats. Scanned documents require a later OCR
stage. Display which formats and locations have content coverage; never describe
unsupported or unfinished content as fully searched.

Use bounded extraction concurrency, file-size/time budgets, retries for files still
being written, and isolation for document parsers. Keep placeholder-only cloud
files discoverable by metadata without downloading them merely to extract text.
Maintain separate progress for file discovery and content indexing.

SQLite FTS5 is a candidate for this content index because it supports full-text
queries, relevance scoring, highlights, and snippets. Ordinary user text must be
translated into a safely escaped FTS expression; parameter binding alone does not
make arbitrary input a valid FTS query. Store content indexing in a separate,
rebuildable database so tag metadata remains independent.

Windows Search can supplement coverage while the local content index builds.
Distinguish unavailable/failed sources from a successful search with no matches.
Merge and deduplicate evidence for the same file from multiple sources.

## Storage decision

The file index also supports the disk map. Preserve that contract while comparing
the current compact binary arrays, indexed SQLite metadata, and a combination of
SQLite persistence with a compact memory representation. Measure duplicate memory
and synchronization costs. A local FTS content index can be added independently
of replacing filename-index storage. No full metadata migration is decided here.

## Current implementation gaps

- SearchQuery already infers some tags and folder profiles, but SearchRanker scores
  filename terms and gives tag-only matches no corresponding relevance boost.
- The private index requires all plain terms in a filename, which conflicts with
  mixed tag/path/content interpretations.
- Filter-only queries bypass private-index matching and fall back to crawling.
- Candidate limits are applied before scope/structural filtering and final ranking.
- Saved volumes are marked out of sync at startup and need reconciliation.
- The initial builder enumerates folders sequentially; drive-aware parallelism is
  currently used by the search crawler, not the full-index builder.
- Changed indexes are saved by rewriting the aggregate binary file.
- File-content search depends on Windows Search; the UI disables it when that
  source is unavailable. MatchedContents is currently always false.

## Implementation order and acceptance checks

1. **Shared interpretation and relevance:** plain tags first, multiword tags, type
   aliases, mixed-field matching, strict optional filters, rank before limiting,
   and metadata-only filter queries.
2. **Search presentation:** tag actions, useful match explanations, content excerpt
   space, stable keyboard selection, immediate names followed by lazy details.
3. **Local content search:** a persistent text index, extraction workers, correct
   updates/deletions, snippets, and clearly reported format/location coverage.
4. **Faster discovery and reconciliation:** measured device-aware enumeration,
   durable incremental updates, and an NTFS journal path with tested fallback.

Use representative known-item queries and evaluate whether the intended item is
in the first 1, 5, and 10 results. Also measure first useful result latency, settled
result latency, cancellation latency, startup catch-up, create/rename/delete
visibility, changed-document visibility, index build time, disk writes, and peak
memory. Record median and slow-case timings with dataset size and hardware.

Initial targets to validate, not promises: warm metadata results within about
100 ms and ordinary local filename changes visible within about one second after
the filesystem reports them. Content refresh depends on document size, format,
and extraction backlog and must be measured separately.

Tests must cover ambiguous words, explicit-filter precedence, Unicode, multiword
tags, strong matches late in a large drive, filter-only queries, content-only hits,
cross-field terms, updates while searching, restarts, journal gaps, offline drives,
and extraction failures. No source may report complete results after silently
dropping eligible candidates or losing changes.

## Primary references

- [SQLite FTS5](https://www.sqlite.org/fts5.html)
- [Windows change journals](https://learn.microsoft.com/en-us/windows/win32/fileio/change-journals)
- [Windows filter handlers](https://learn.microsoft.com/en-us/windows/win32/search/-search-ifilter-about)
