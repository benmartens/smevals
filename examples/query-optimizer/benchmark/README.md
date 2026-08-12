# Query optimizer benchmark workflow

The benchmark runs each configured model in an isolated copy of the
dependency-free .NET 10 starter. Hidden workloads are generated only after all
new model sessions complete.

Calibrate:

```powershell
.\examples\query-optimizer\benchmark\Calibrate-QueryOptimizer.ps1
```

Run all configured models, grade them, and build reports:

```powershell
.\examples\query-optimizer\benchmark\Run-QueryOptimizerBenchmark.ps1
```

Generated hidden cases, logs, reports, and the static site are written under
`benchmark\private`, which is ignored by Git.
