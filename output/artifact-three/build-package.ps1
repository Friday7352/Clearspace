$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location -LiteralPath $workspaceRoot
try {
    $baseline = 'd810ca8304ae7294a80d7fc49777e4995e48435a'
    $sourceRoots = @('Clearspace', 'Clearspace.Tests', 'docs', 'installer', 'README.md', 'VERSION', '.gitignore', '.gitattributes',
        'Build Clearspace.cmd', 'Build Installer.cmd', 'Run Clearspace.cmd', 'Clean.cmd')
    $original = Join-Path $PSScriptRoot 'Clearspace-Artifact3-Original.zip'
    & git archive --format=zip "--output=$original" $baseline -- @sourceRoots
    if ($LASTEXITCODE -ne 0) { throw 'Could not archive the baseline source.' }

    function Write-SourceZip([string] $zipPath, [string[]] $relativePaths) {
        $stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($relative in ($relativePaths | Sort-Object -Unique)) {
                $full = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $relative))
                if (-not $full.StartsWith($workspaceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Source path leaves the workspace: $relative"
                }
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $full, $relative.Replace('\', '/'),
                    [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        } finally { $archive.Dispose(); $stream.Dispose() }
    }

    $sourceFiles = @(& git ls-files -- @sourceRoots) + @(
        'Clearspace/Services/TagDatabase.cs', 'Clearspace.Tests/TagDatabaseTests.cs', 'Clearspace.Tests/TagTestScope.cs',
        'docs/CS499-Artifact-Three-Databases.md', 'docs/CS499-Artifact-Three-Narrative.md')
    if ($LASTEXITCODE -ne 0) { throw 'Could not list the source files.' }
    Write-SourceZip (Join-Path $PSScriptRoot 'Clearspace-Artifact3-Enhanced.zip') $sourceFiles
    $evidence = @('docs/CS499-Artifact-Three-Databases.md', 'docs/CS499-Artifact-Three-Narrative.md',
        'output/test-results/artifact-three/artifact-three.trx', 'output/artifact-three/README.md')
    Write-SourceZip (Join-Path $PSScriptRoot 'Clearspace-Artifact3-Evidence.zip') $evidence

    [xml] $report = Get-Content -Raw -LiteralPath (Join-Path $workspaceRoot 'output/test-results/artifact-three/artifact-three.trx')
    $counts = $report.TestRun.ResultSummary.Counters
    if ([int]$counts.failed -ne 0 -or [int]$counts.total -ne [int]$counts.passed) { throw 'The test report is not fully passing.' }
    $archives = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.zip' | ForEach-Object {
        $check = [IO.Compression.ZipFile]::OpenRead($_.FullName)
        try { $entryCount = $check.Entries.Count } finally { $check.Dispose() }
        [pscustomobject][ordered]@{ name = $_.Name; entries = $entryCount; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }
    [ordered]@{
        artifact = 'Clearspace Artifact 3 - SQLite tags and assignments'
        baselineCommit = $baseline
        branch = (& git branch --show-current)
        sourceState = 'Working source including uncommitted Artifact 3 changes'
        createdUtc = [DateTime]::UtcNow.ToString('O')
        tests = [ordered]@{ total = [int]$counts.total; passed = [int]$counts.passed; failed = [int]$counts.failed; skipped = [int]$counts.notExecuted }
        releasePublish = 'Succeeded: dist/artifact-three'
        manualVerification = [ordered]@{
            status = 'User reported the supplied manual checklist worked'
            reportedOn = '2026-09-29'
            source = 'Payton: all those tests seem to work'
            independentlyObserved = $false
        }
        archives = @($archives)
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'MANIFEST.json') -Encoding utf8
    $archives | Format-Table name, entries, bytes
} finally { Pop-Location }
