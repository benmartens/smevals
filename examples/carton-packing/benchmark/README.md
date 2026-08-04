# Benchmark workflow

`Run-CartonPackingBenchmark.ps1` creates a fully precomputed demonstration.
The exact hidden cases are absent during model sessions and generated only
before grading.

## Prerequisites

- GitHub Copilot CLI authenticated with `copilot login`
- `smevals` and `smevals-copilot` on `PATH`
- .NET 10 SDK
- Python 3.10+

The repository-local `.venv\Scripts` directory is added to `PATH`
automatically when present.

## Calibrate the scorer

```powershell
.\examples\carton-packing\benchmark\Calibrate-CartonPacking.ps1
```

Calibration compares the generated reference, empty output, two simple greedy
strategies, and an invalid-placement mutant. Its temporary hidden bundle is
deleted before the command exits.

## Run

```powershell
.\examples\carton-packing\benchmark\Run-CartonPackingBenchmark.ps1
```

Useful options:

```powershell
# Use a custom model file
.\examples\carton-packing\benchmark\Run-CartonPackingBenchmark.ps1 `
  -ModelsPath C:\path\models.json

# Skip the lightweight model-ID probe
.\examples\carton-packing\benchmark\Run-CartonPackingBenchmark.ps1 `
  -SkipModelPreflight

# Validate the configured model IDs without starting the coding benchmark
.\examples\carton-packing\benchmark\Run-CartonPackingBenchmark.ps1 `
  -PreflightOnly

# Rebuild reports/site from existing Runs and hidden cases
.\examples\carton-packing\benchmark\Run-CartonPackingBenchmark.ps1 `
  -BuildOnly
```

Generated local files are under `benchmark\private` and are ignored:

- `hidden\bundle.json`
- `logs\`
- `report.md`
- `report.json`
- `site\`
- `DEMO.md`

Serve the finished static site locally:

```powershell
Set-Location examples\carton-packing\benchmark\private\site
python -m http.server 8000
```

Open `http://127.0.0.1:8000`.

If the hidden bundle already exists, the script refuses to launch a model that
does not already have a successful Run. This keeps exact hidden inputs absent
during all new model sessions.
