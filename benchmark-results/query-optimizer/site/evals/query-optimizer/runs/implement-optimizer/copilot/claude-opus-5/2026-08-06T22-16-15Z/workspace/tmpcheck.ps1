$ErrorActionPreference = 'Stop'
$cli = '.\src\QueryOptimizer.Cli\bin\Release\net10.0\QueryOptimizer.Cli.exe'
$out = '.\tmpcheck'
New-Item -ItemType Directory -Force -Path $out | Out-Null

function New-Problem {
  param([string]$Name, [int]$N, [string]$Shape, [int]$Memory)
  $rand = [System.Random]::new($N * 31 + $Shape.Length)
  $tables = @(); $preds = @(); $joins = @()
  for ($i = 0; $i -lt $N; $i++) {
    $id = 't{0:d2}' -f $i
    $idx = @()
    if ($rand.Next(2) -eq 0) { $idx = @(@{ column = 'c0'; seekStartupCost = $rand.Next(0, 60); lookupCostPerRow = $rand.Next(1, 5) }) }
    $tables += @{ id = $id; rows = $rand.Next(20, 200000); scanCostPerRow = $rand.Next(1, 6); indexes = $idx }
    if ($rand.Next(3) -ne 0) { $preds += @{ tableId = $id; column = 'c0'; selectivityPermille = $rand.Next(1, 1000); indexable = $true } }
  }
  for ($i = 1; $i -lt $N; $i++) {
    $left = if ($Shape -eq 'star') { 't00' } else { 't{0:d2}' -f ($i - 1) }
    $joins += @{ leftTable = $left; rightTable = ('t{0:d2}' -f $i); selectivityPermille = $rand.Next(1, 200) }
  }
  if ($Shape -eq 'mixed' -and $N -gt 4) {
    $joins += @{ leftTable = 't00'; rightTable = ('t{0:d2}' -f ($N - 1)); selectivityPermille = 50 }
  }
  $path = Join-Path $out "$Name.json"
  @{ memoryLimitRows = $Memory; tables = $tables; predicates = $preds; joins = $joins } |
    ConvertTo-Json -Depth 8 | Set-Content -Path $path -Encoding UTF8
  return $path
}

$cases = @(
  @{ n = 2;  s = 'chain'; m = 5 },
  @{ n = 5;  s = 'star';  m = 1 },
  @{ n = 8;  s = 'chain'; m = 1000 },
  @{ n = 10; s = 'star';  m = 50 },
  @{ n = 12; s = 'mixed'; m = 200 },
  @{ n = 16; s = 'mixed'; m = 10 },
  @{ n = 16; s = 'star';  m = 100000 },
  @{ n = 20; s = 'chain'; m = 30 }
)

foreach ($c in $cases) {
  $name = "p$($c.n)$($c.s)"
  $path = New-Problem -Name $name -N $c.n -Shape $c.s -Memory $c.m
  $a = Join-Path $out "$name.a.json"; $b = Join-Path $out "$name.b.json"
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  & $cli $path $a; $code = $LASTEXITCODE
  $sw.Stop()
  & $cli $path $b | Out-Null
  $same = (Get-FileHash $a).Hash -eq (Get-FileHash $b).Hash
  Write-Host ("{0,-12} exit={1} deterministic={2} ms={3}" -f $name, $code, $same, $sw.ElapsedMilliseconds)
}
