[CmdletBinding()]
param(
    [int]$Seed = 8675309,
    [string]$OutputPath = (
        Join-Path $PSScriptRoot 'private\calibration-results.json'
    )
)

$ErrorActionPreference = 'Stop'
$python = (Get-Command python -ErrorAction Stop).Source
& $python (
    Join-Path $PSScriptRoot 'calibrate.py'
) --seed $Seed --output $OutputPath
if ($LASTEXITCODE -ne 0) {
    throw "Field-service route-planner calibration failed."
}
