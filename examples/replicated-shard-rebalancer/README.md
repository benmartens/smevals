# Replicated Shard Rebalancer Benchmark

This agentic Eval asks models to implement a dependency-free .NET 10 engine
that chooses target nodes for every replica of every shard.

The challenge combines heterogeneous node capacities, failure zones,
per-shard exclusions, anti-affinity, and movement-aware balancing. Models can
run a visible console test harness. Exact hidden scenarios are generated only
after all model sessions complete. The benchmark uses a 500-credit per-session
soft cap.

## Run one model manually

```powershell
$env:Path = "$PWD\.venv\Scripts;$env:Path"
smevals run examples\replicated-shard-rebalancer -c copilot -m gpt-5-mini
```

Do not add `-g` until a hidden bundle has been generated and
`REPLICATED_SHARD_REBALANCER_HIDDEN_DIR` points to it.

## Run the benchmark

```powershell
examples\replicated-shard-rebalancer\benchmark\Run-ReplicatedShardRebalancerBenchmark.ps1
```

The wrapper delegates model isolation, post-run hidden-case generation,
grading, reporting, and site generation to the shared benchmark helper.
Calibration gives the exact reference 1.0, invalid output 0.0, and separates a
simple first-feasible baseline from a stronger balance-aware heuristic.

See [benchmark/README.md](benchmark/README.md) for benchmark options.
