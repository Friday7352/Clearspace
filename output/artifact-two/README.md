# Clearspace — Artifact 2 review package

Prepared September 23, 2026. All three ZIP archives were opened and checked for
CRC errors. `MANIFEST.json` records their SHA-256 checksums and the test counts.

- **Clearspace-Artifact2-Original.zip**: source from commit
  `03a7b023e24306a503c14177500ed0681d610181` (ArtifactOneAdditions), before
  the disk-usage/algorithms enhancement. This is the pre-enhancement baseline,
  not a claim that it is the instructor's separately approved original upload.
- **Clearspace-Artifact2-Enhanced.zip**: current source, including uncommitted
  and untracked files, tests, build scripts and documentation. Build outputs,
  personal IDE settings and Git metadata are excluded. The internal
  `SOURCE-MANIFEST.json` records a hash for each packaged file.
- **Clearspace-Artifact2-Evidence.zip**: the final 150/150 passing test report,
  its synthetic WPF screenshot attachments, the original planning PDFs and two
  historical performance reports. Historical reports are retained as earlier
  evidence and are not the final test result. The dense screenshot changes only
  the map's test source; its surrounding sidebar remains the earlier synthetic
  source and should not be presented as a real-drive screenshot.

Start with these files inside the enhanced archive:

1. `docs/CS499-Artifact-Two-Completion.md`: requirement mapping, algorithms,
   verification, measurements and limitations.
2. `docs/CS499-Artifact-Two-Narrative.md`: narrative draft to review against the
   rubric and adapt to your own reflection.

To reproduce tests after extracting the enhanced source on Windows with the
.NET 10 SDK (the first run may need to restore NuGet dependencies):

```powershell
dotnet test Clearspace.Tests/Clearspace.Tests.csproj -c Release
```

The new executable invalidates old depth-limited indexes and rebuilds them in
the background once. The disk viewer's layout, colors and interactions are
preserved; busy-frame hit testing and snapshot replacement correctness were
fixed. This package is source and evidence, not an installer.

Before course submission: confirm the baseline and outcome mapping against the
approved plan, complete the actual-drive interactive walkthrough, review the
narrative in your own voice, and update the ePortfolio. Nothing has been
submitted or published externally.
