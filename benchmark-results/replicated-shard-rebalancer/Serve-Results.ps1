[CmdletBinding()]
param([int]$Port = 8000)

Set-Location (Join-Path $PSScriptRoot 'site')
python -m http.server $Port
