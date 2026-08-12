[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$EvalDirectory,
    [Parameter(Mandatory)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$evalDirectoryPath = (Resolve-Path $EvalDirectory).Path
$repoRoot = (Resolve-Path (Join-Path $evalDirectoryPath '..\..')).Path
$benchmarkDirectory = Join-Path $evalDirectoryPath 'benchmark'
$privateDirectory = Join-Path $benchmarkDirectory 'private'
$hiddenBundle = Join-Path $privateDirectory 'hidden\hidden_cases.json'
$required = @(
    (Join-Path $privateDirectory 'report.md'),
    (Join-Path $privateDirectory 'report.json'),
    (Join-Path $privateDirectory 'DEMO.md'),
    (Join-Path $privateDirectory 'site'),
    $hiddenBundle,
    (Join-Path $benchmarkDirectory 'models.json')
)
foreach ($path in $required) {
    if (-not (Test-Path $path)) {
        throw "Required benchmark output does not exist: $path"
    }
}

$destinationPath = [System.IO.Path]::GetFullPath($Destination)
if (Test-Path $destinationPath) {
    throw "Snapshot destination already exists: $destinationPath"
}

New-Item -ItemType Directory -Path $destinationPath | Out-Null
Copy-Item (Join-Path $privateDirectory 'site') $destinationPath -Recurse
Copy-Item (Join-Path $privateDirectory 'report.md') $destinationPath
Copy-Item (Join-Path $privateDirectory 'report.json') $destinationPath
Copy-Item (Join-Path $privateDirectory 'DEMO.md') $destinationPath
Copy-Item $hiddenBundle (Join-Path $destinationPath 'hidden_cases.json')
Copy-Item (
    Join-Path $benchmarkDirectory 'models.json'
) (Join-Path $destinationPath 'models.json')
Copy-Item (
    Join-Path $PSScriptRoot 'Serve-BenchmarkResults.ps1'
) (Join-Path $destinationPath 'Serve-Results.ps1')

$eval = Get-Content (
    Join-Path $evalDirectoryPath 'eval.yaml'
) -Raw -Encoding UTF8
$evalName = if ($eval -match '(?m)^name:\s*(.+?)\s*$') {
    $Matches[1]
} else {
    Split-Path $evalDirectoryPath -Leaf
}
$report = Get-Content (
    Join-Path $privateDirectory 'report.json'
) -Raw -Encoding UTF8 | ConvertFrom-Json
$hidden = Get-Content $hiddenBundle -Raw -Encoding UTF8 | ConvertFrom-Json
$models = Get-Content (
    Join-Path $benchmarkDirectory 'models.json'
) -Raw -Encoding UTF8 | ConvertFrom-Json
$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
$disabled = @(
    $models.models | Where-Object { $_.enabled -eq $false }
)
$reportedModels = @($report.rows | ForEach-Object { $_.model })
$missingModels = @(
    $models.models |
        Where-Object {
            $_.enabled -ne $false -and $_.id -notin $reportedModels
        }
)
$disabledText = if ($disabled.Count -eq 0) {
    'None.'
} else {
    ($disabled | ForEach-Object {
        '- `{0}` - {1}' -f $_.id, $_.unavailableReason
    }) -join "`n"
}
$missingText = if ($missingModels.Count -eq 0) {
    'None.'
} else {
    ($missingModels | ForEach-Object {
        '- `{0}` - no graded row (for example, a timeout or failed Run)' -f $_.id
    }) -join "`n"
}
$caseCount = @($hidden.cases).Count
$readme = @"
# $evalName benchmark results

This directory is a portable snapshot of the precomputed GitHub Copilot CLI
benchmark.

- Working-tree base commit: ``$sourceCommit``
- Hidden-case seed: ``$($hidden.seed)``
- Hidden cases: $caseCount
- Graded model runs: $(@($report.rows).Count)
- Grader version: ``$($report.grader_version)``

## Disabled models

$disabledText

## Missing graded runs

$missingText

## Files

- ``site\`` - static smevals report with workspaces, grades, patches, and visual artifacts.
- ``report.md`` - terminal-style leaderboard and metric summary.
- ``report.json`` - machine-readable grade rows.
- ``DEMO.md`` - benchmark narrative plus the current report.
- ``hidden_cases.json`` - generated hidden bundle used for this snapshot.
- ``models.json`` - configured model roster and reasoning levels.
- ``Serve-Results.ps1`` - local static-server helper.
"@
$readme | Set-Content (
    Join-Path $destinationPath 'README.md'
) -Encoding UTF8

Write-Host "Benchmark snapshot: $destinationPath"
