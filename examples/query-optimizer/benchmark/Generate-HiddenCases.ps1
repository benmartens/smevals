param(
    [string]$Output = (Join-Path $PSScriptRoot 'private\hidden'),
    [Nullable[long]]$Seed
)

$arguments = @(
    (Join-Path $PSScriptRoot 'generate_hidden_cases.py'),
    '--output',
    $Output
)
if ($null -ne $Seed) {
    $arguments += @('--seed', $Seed)
}
python @arguments
exit $LASTEXITCODE
