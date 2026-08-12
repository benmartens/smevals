[CmdletBinding()]
param(
    [string]$OutputDirectory = (
        Join-Path $PSScriptRoot 'private\hidden'
    ),
    [string]$Seed
)

$ErrorActionPreference = 'Stop'
$python = (Get-Command python -ErrorAction Stop).Source
$generator = Join-Path $PSScriptRoot 'generate_hidden_cases.py'
$arguments = @($generator, '--output', $OutputDirectory)
if ($Seed) {
    $arguments += @('--seed', $Seed)
}
& $python @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Hidden case generation failed with exit code $LASTEXITCODE."
}
