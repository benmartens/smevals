# Field-Service Route Planner Benchmark

This agentic Eval asks models to implement a dependency-free .NET 10 planner
for assigning and sequencing field-service jobs.

The challenge includes technician skills, shifts, job time windows, waiting,
asymmetric travel, assignment trade-offs, and deterministic output. Models can
run a visible console test harness. Exact hidden scenarios are generated only
after all model sessions finish.

```powershell
examples\field-service-route-planner\benchmark\Run-FieldServiceRoutePlannerBenchmark.ps1
```

The score rewards valid plans by served-value ratio, with travel efficiency as
the tie-breaking quality signal. Calibration places a simple first-feasible
baseline below the 0.70 pass threshold while a bounded beam heuristic passes.

See [benchmark/README.md](benchmark/README.md) for workflow details.
