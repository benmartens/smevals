param(
    [long]$Seed = 8675309,
    [string]$Output
)

$arguments = @(
    (Join-Path $PSScriptRoot 'calibrate.py'),
    '--seed',
    $Seed
)
if ($Output) {
    $arguments += @('--output', $Output)
}
python @arguments
exit $LASTEXITCODE
