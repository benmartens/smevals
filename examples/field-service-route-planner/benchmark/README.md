# Benchmark workflow

`Run-FieldServiceRoutePlannerBenchmark.ps1` delegates to the shared coding
benchmark runner. Model workspaces are completed before a shared generated
hidden bundle is created and grading begins.

Prerequisites are an authenticated GitHub Copilot CLI, `smevals`,
`smevals-copilot`, .NET 10, and Python 3.10+.

```powershell
.\examples\field-service-route-planner\benchmark\Calibrate-FieldServiceRoutePlanner.ps1
.\examples\field-service-route-planner\benchmark\Run-FieldServiceRoutePlannerBenchmark.ps1
```

The wrapper also accepts `-ModelsPath`, `-ModelTimeoutSeconds`,
`-PreflightTimeoutSeconds`, `-SkipModelPreflight`, `-PreflightOnly`, and
`-BuildOnly`.

Generated hidden inputs, logs, reports, sites, and calibration output live
under `benchmark\private` and are ignored. If a hidden bundle already exists,
the shared runner prevents it from being exposed to new model sessions.
