[CmdletBinding()]
param(
    [string]$ModelsPath = (Join-Path $PSScriptRoot 'models.json'),
    [int]$ModelTimeoutSeconds = 1800,
    [int]$PreflightTimeoutSeconds = 180,
    [switch]$SkipModelPreflight,
    [switch]$PreflightOnly,
    [switch]$BuildOnly
)

$runner = Join-Path $PSScriptRoot (
    '..\..\benchmark-common\Invoke-CopilotCodingBenchmark.ps1'
)
& $runner `
    -EvalDirectory (Split-Path $PSScriptRoot -Parent) `
    -TaskName 'implement-planner' `
    -HiddenEnvironmentVariable 'FIELD_SERVICE_ROUTE_PLANNER_HIDDEN_DIR' `
    -ModelsPath $ModelsPath `
    -ModelTimeoutSeconds $ModelTimeoutSeconds `
    -PreflightTimeoutSeconds $PreflightTimeoutSeconds `
    -SkipModelPreflight:$SkipModelPreflight `
    -PreflightOnly:$PreflightOnly `
    -BuildOnly:$BuildOnly
