[CmdletBinding()]
param(
    [string]$ModelsPath = (Join-Path $PSScriptRoot 'models.json'),
    [int]$ModelTimeoutSeconds = 1800,
    [int]$PreflightTimeoutSeconds = 180,
    [switch]$SkipModelPreflight,
    [switch]$PreflightOnly,
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
if ($PreflightOnly -and $BuildOnly) {
    throw "PreflightOnly and BuildOnly cannot be used together."
}

function Get-ModelSlug {
    param([Parameter(Mandatory)][string]$Model)
    return ($Model -replace '[^a-zA-Z0-9._-]+', '-').Trim('-')
}

function Invoke-ExternalProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [Parameter(Mandatory)][string]$StdoutPath,
        [Parameter(Mandatory)][string]$StderrPath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Could not start $FilePath."
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        $process.WaitForExit()
        $timedOut = $true
    } else {
        $timedOut = $false
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    [System.IO.File]::WriteAllText($StdoutPath, $stdout)
    [System.IO.File]::WriteAllText($StderrPath, $stderr)

    return [pscustomobject]@{
        ExitCode = if ($timedOut) { $null } else { $process.ExitCode }
        TimedOut = $timedOut
        Stdout = $stdout
        Stderr = $stderr
    }
}

function Test-SuccessfulModelRun {
    param(
        [Parameter(Mandatory)][string]$RunsRoot,
        [Parameter(Mandatory)][string]$Model
    )

    $modelRoot = Join-Path $RunsRoot (
        "implement-packer\copilot\$(Get-ModelSlug $Model)"
    )
    if (-not (Test-Path $modelRoot)) {
        return $false
    }

    foreach ($runFile in Get-ChildItem $modelRoot -Recurse -Filter run.yaml) {
        if (Select-String -Path $runFile.FullName -Pattern '^exit_code:\s+0$' -Quiet) {
            return $true
        }
    }
    return $false
}

function Write-ModelRunMetadata {
    param(
        [Parameter(Mandatory)][string]$RunsRoot,
        [Parameter(Mandatory)]$Model
    )

    $modelRoot = Join-Path $RunsRoot (
        "implement-packer\copilot\$(Get-ModelSlug $Model.id)"
    )
    if (-not (Test-Path $modelRoot)) {
        return
    }
    $runFile = Get-ChildItem $modelRoot -Recurse -Filter run.yaml |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $runFile) {
        return
    }
    [ordered]@{
        model = $Model.id
        label = $Model.label
        effort = $Model.effort
        maxAiCredits = 500
    } |
        ConvertTo-Json |
        Set-Content (
            Join-Path $runFile.Directory.FullName 'benchmark-model.json'
        ) -Encoding UTF8
}

$benchmarkDirectory = $PSScriptRoot
$evalDirectory = Split-Path $benchmarkDirectory -Parent
$repoRoot = (Resolve-Path (Join-Path $evalDirectory '..\..')).Path
$privateDirectory = Join-Path $benchmarkDirectory 'private'
$hiddenDirectory = Join-Path $privateDirectory 'hidden'
$logsDirectory = Join-Path $privateDirectory 'logs'
$siteDirectory = Join-Path $privateDirectory 'site'
$runsDirectory = Join-Path $evalDirectory 'runs'

New-Item -ItemType Directory -Force $privateDirectory, $logsDirectory |
    Out-Null

$venvScripts = Join-Path $repoRoot '.venv\Scripts'
if (Test-Path $venvScripts) {
    $env:Path = "$venvScripts;$env:Path"
}

$copilot = (Get-Command copilot -ErrorAction Stop).Source
$smevals = (Get-Command smevals -ErrorAction Stop).Source
$python = (Get-Command python -ErrorAction Stop).Source
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source

$dotnetVersion = & $dotnet --version
if (-not $dotnetVersion.StartsWith('10.')) {
    throw "The benchmark requires .NET 10; found $dotnetVersion."
}

$configuration = Get-Content $ModelsPath -Raw -Encoding UTF8 |
    ConvertFrom-Json
$disabledModels = @(
    $configuration.models | Where-Object { $_.enabled -eq $false }
)
$models = @(
    $configuration.models | Where-Object { $_.enabled -ne $false }
)
if ($models.Count -eq 0) {
    throw "No models are configured in $ModelsPath."
}
foreach ($model in $disabledModels) {
    Write-Warning "Disabled model: $($model.id) - $($model.unavailableReason)"
}

if (-not $BuildOnly) {
    $missingRuns = @(
        $models | Where-Object {
            -not (Test-SuccessfulModelRun $runsDirectory $_.id)
        }
    )
    $hiddenBundle = Join-Path $hiddenDirectory 'hidden_cases.json'
    if (-not $PreflightOnly -and (Test-Path $hiddenBundle) -and $missingRuns.Count -gt 0) {
        $missingIds = ($missingRuns | ForEach-Object { $_.id }) -join ', '
        throw "Hidden cases already exist, but these models need new Runs: $missingIds. Archive/remove benchmark\private\hidden before model execution."
    }

    $preflightModels = if ($PreflightOnly) { $models } else { $missingRuns }
    if (-not $SkipModelPreflight -and $preflightModels.Count -gt 0) {
        foreach ($model in $preflightModels) {
            $slug = Get-ModelSlug $model.id
            Write-Host "Preflight: $($model.label) [$($model.id)]"
            $result = Invoke-ExternalProcess `
                -FilePath $copilot `
                -Arguments @(
                    '-p', 'Reply with exactly READY.',
                    '-s',
                    '--output-format=json',
                    '--stream=off',
                    '--no-color',
                    '--no-ask-user',
                    '--no-remote',
                    '--no-remote-export',
                    '--no-auto-update',
                    '--no-experimental',
                    '--disallow-temp-dir',
                    '--no-custom-instructions',
                    '--disable-builtin-mcps',
                    '--model', $model.id,
                    '--effort', $model.effort,
                    '--max-ai-credits', '30'
                ) `
                -WorkingDirectory $repoRoot `
                -TimeoutSeconds $PreflightTimeoutSeconds `
                -StdoutPath (Join-Path $logsDirectory "preflight-$slug.out.log") `
                -StderrPath (Join-Path $logsDirectory "preflight-$slug.err.log")
            if ($result.TimedOut -or $result.ExitCode -ne 0) {
                throw "Model preflight failed for $($model.id)."
            }
        }
    }

    if ($PreflightOnly) {
        Write-Host "All configured model preflights passed."
        return
    }

    foreach ($model in $models) {
        if (Test-SuccessfulModelRun $runsDirectory $model.id) {
            Write-ModelRunMetadata $runsDirectory $model
            Write-Host "Already complete: $($model.label) [$($model.id)]"
            continue
        }

        $slug = Get-ModelSlug $model.id
        Write-Host "Running: $($model.label) [$($model.id)]"
        $previousEffort = $env:SMEVALS_COPILOT_EFFORT
        try {
            $env:SMEVALS_COPILOT_EFFORT = $model.effort
            $result = Invoke-ExternalProcess `
                -FilePath $smevals `
                -Arguments @(
                    'run', $evalDirectory,
                    '-c', 'copilot',
                    '-m', $model.id,
                    '-n', '1'
                ) `
                -WorkingDirectory $repoRoot `
                -TimeoutSeconds $ModelTimeoutSeconds `
                -StdoutPath (Join-Path $logsDirectory "run-$slug.out.log") `
                -StderrPath (Join-Path $logsDirectory "run-$slug.err.log")
        } finally {
            $env:SMEVALS_COPILOT_EFFORT = $previousEffort
        }

        if ($result.TimedOut) {
            Write-Warning "Timed out: $($model.id)"
        } elseif ($result.ExitCode -ne 0) {
            Write-Warning "Run failed ($($result.ExitCode)): $($model.id)"
        }
        Write-ModelRunMetadata $runsDirectory $model
    }

    if (-not (Test-Path (Join-Path $hiddenDirectory 'hidden_cases.json'))) {
        & $python (
            Join-Path $benchmarkDirectory 'generate_hidden_cases.py'
        ) --output $hiddenDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "Hidden case generation failed."
        }
    }

    $previousHidden = $env:CARTON_PACKING_HIDDEN_DIR
    try {
        $env:CARTON_PACKING_HIDDEN_DIR = $hiddenDirectory
        & $smevals grade $evalDirectory -g default --regrade
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Some model Grades are below threshold or grading failed. Reports will still be generated."
        }
    } finally {
        $env:CARTON_PACKING_HIDDEN_DIR = $previousHidden
    }
}

foreach ($model in $models) {
    Write-ModelRunMetadata $runsDirectory $model
}

$markdownReport = & $smevals report $evalDirectory -g default
if ($LASTEXITCODE -ne 0) {
    throw "Markdown report generation failed."
}
$markdownReport |
    Set-Content (Join-Path $privateDirectory 'report.md') -Encoding UTF8

$jsonReport = & $smevals report $evalDirectory -g default --json
if ($LASTEXITCODE -ne 0) {
    throw "JSON report generation failed."
}
$jsonReport |
    Set-Content (Join-Path $privateDirectory 'report.json') -Encoding UTF8
& $smevals build $evalDirectory -g default -o $siteDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Static site build failed."
}

$demoTemplate = Get-Content (
    Join-Path $benchmarkDirectory 'DEMO.md'
) -Raw -Encoding UTF8
$demoReport = Get-Content (
    Join-Path $privateDirectory 'report.md'
) -Raw -Encoding UTF8
$demoContent = $demoTemplate + "`n## Current benchmark results`n`n" + $demoReport
$demoContent |
    Set-Content (Join-Path $privateDirectory 'DEMO.md') -Encoding UTF8

Write-Host "Benchmark output: $privateDirectory"
