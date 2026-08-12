[CmdletBinding()]
param(
    [int]$Port = 8000
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot 'site')
python -m http.server $Port --bind 127.0.0.1
