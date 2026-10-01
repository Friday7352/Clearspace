# Artifact 3 narrative draft — Clearspace databases

Historical September 29 draft. Superseded by [the Milestone Four narrative](CS499-Milestone-Four-Narrative.md), which includes the implemented file-lock feature and verified Module One outcome mapping.

Payton Castle · CS 499 · September 29, 2026

## Artifact background

I created Clearspace in August 2026 as a Windows file manager using C# and Windows
Presentation Foundation. It supports navigation, search, tags, and file operations.
For this enhancement, I built on the version containing my software engineering
and algorithms improvements and focused on persistent tag data.

## Why I selected this enhancement

Tags connect files and folders to categories that users define. Previously,
Clearspace kept definitions and assignments in one JSON document. Every update
rewrote that document, finding all files with a tag required scanning assignments,
and relationships were maintained by application code. This made tags a useful
example for demonstrating relational database design in an existing application.

I replaced that storage with SQLite tables for tag definitions, paths, and their
many-to-many assignments. Primary keys prevent duplicate assignments, foreign keys
prevent references to missing records, and cascade deletion removes assignments
when a tag is deleted. Indexes support lookups by path, ID, name, and tag. Queries
bind values as parameters so names and paths containing SQL syntax remain data.

## Outcomes and implementation evidence

This enhancement demonstrates database design and the use of established computing
practices to improve reliability. Creating a tag and applying it to a selection now
uses one transaction. If any assignment fails, the definition and all assignments
roll back together. A migration imports existing JSON in one transaction and leaves
the original file intact. Invalid input produces an error instead of silently
replacing a user's tags with defaults.

The work also supports secure development through parameterized SQL, validation,
and database constraints. The implementation notes explain the schema, recovery
behavior, and limitations so another developer can review the decisions. The
original outline included file-lock metadata, but that feature did not exist in
the current application. I clarified the scope before implementation and kept the
enhancement focused on tags and assignments.

## Reflection

One challenge was preserving case-insensitive behavior for Unicode names and paths.
SQLite's default case-insensitive collation handles ASCII, while the existing C#
code used ordinal case-insensitive comparisons. Registering the same comparison
with SQLite kept these behaviors aligned.

Another tradeoff concerned search. Querying SQLite for every file during a drive
scan would add unnecessary work. I used indexed queries to capture the relevant
tagged paths once per search, then matched files using in-memory sets. This keeps
persistent storage in the database while preserving efficient file matching.

The tests cover both successful operations and failures after part of a transaction
has executed. They also check migration, restart persistence, Unicode matching,
constraints, concurrent stores, and index use with 10,000 assignments. The
implementation notes link to the full Release test report and distinguish automated
verification from manual checks. After running the provided checklist in the
application, I reported that the tests seemed to work, including tag creation,
search, persistence after restart, rename retention, and removal.

Before final submission, I will compare this draft against the instructor's rubric
and feedback, confirm the course-outcome mapping, and complete the final ePortfolio
presentation. The file-search index remains a separate design decision.
