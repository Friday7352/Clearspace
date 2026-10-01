# CS 499 Milestone Four submission

Payton Castle | October 1, 2026

Submit these two files:

1. `Payton_Castle_CS499_Milestone_Four_Code.zip` — original and enhanced technical files.
2. `Payton_Castle_CS499_Milestone_Four_Narrative.docx` — the accompanying narrative.

The ZIP contains:

- `Original/`: the pre-database-enhancement source from commit
  `d810ca8304ae7294a80d7fc49777e4995e48435a`, preserved from the verified original archive.
- `Enhanced/`: the current application source, including SQLite tag migration,
  schema version 2, file encryption, password dialog, helper source, and tests.
  Existing indexing and folder-type improvements are also included so the project
  is complete; the database and file-lock changes are the focus of this milestone.
- `Evidence/`: the 251-test Release report, implementation notes, UI verification
  record, and rendered dialog previews. The test report's file paths identify the
  machine on which it was produced; the tests themselves create isolated fixtures.
- `MANIFEST.json`: source provenance and SHA-256 hashes of the packaged files.

## Review and run

Start with `Enhanced/docs/CS499-File-Locking.md` and
`Enhanced/docs/CS499-Artifact-Three-Databases.md`.

On Windows with the .NET 10 SDK:

```powershell
dotnet test Enhanced/Clearspace.Tests/Clearspace.Tests.csproj -c Release
dotnet run --project Enhanced/Clearspace/Clearspace.csproj -c Release
```

The optional index helper is not needed for file locking. No private databases,
passwords, personal files, runtime build folders, or Git history are included.

## File locking walkthrough

1. Close any older Clearspace build. Use the current build and create a disposable
   text file on a local NTFS drive, outside cloud or linked folders.
2. Right-click it and choose **Lock file with password**. On first use, enter and
   confirm a master password of at least 12 characters.
3. Try opening the locked file in Clearspace. It should request the password.
4. Enter an incorrect password. The encrypted file should remain unchanged.
5. Enter the correct password. Its original text should be restored.
6. Lock it again, restart Clearspace, and verify that the correct password still
   unlocks it. Existing tag assignments should remain intact.

This version handles one eligible file at a time, up to 64 MiB. The limits and
recovery boundaries are documented in the narrative and technical notes. The
master password cannot be reset through the feature. Locking changes the current
file contents; it does not erase earlier copies or backups.

The narrative addresses all four assignment prompts and the actual Module One
outcome plan. It describes the included file-lock work and its initial limits
without claiming prior instructor approval of those implementation limits.
Review the personal reflection before uploading. Nothing has been submitted to
the course site or published to the ePortfolio by this preparation step.
