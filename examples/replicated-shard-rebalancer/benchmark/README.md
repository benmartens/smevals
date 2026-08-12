# Replicated Shard Rebalancer Benchmark Runner

`Run-ReplicatedShardRebalancerBenchmark.ps1` is a thin wrapper over the shared
Copilot coding benchmark runner. It performs tool/model preflight, creates an
isolated starter workspace for each model, runs the implementation task without
hidden inputs, generates one shared hidden bundle afterward, grades completed
runs, and writes reports beneath `benchmark\private`.

```powershell
.\Run-ReplicatedShardRebalancerBenchmark.ps1
```

Useful switches:

- `-PreflightOnly`: verify the environment and model roster only.
- `-SkipModelPreflight`: skip per-model availability probes.
- `-BuildOnly`: prepare/build benchmark inputs without model evaluations.
- `-ModelsPath <path>`: use another model roster.
- `-ModelTimeoutSeconds <seconds>`: change the per-model timeout.

Generate a reproducible hidden bundle directly:

```powershell
.\Generate-HiddenCases.ps1 -Seed 8675309
```

Calibrate independent baselines:

```powershell
.\Calibrate-ReplicatedShardRebalancer.ps1 -Seed 8675309
```

Hidden cases cover overload repair, uneven sizes, zone scarcity, exclusions,
three-zone anti-affinity, balance-versus-movement, coordinated swaps, and
movement tie-breaking. The Python reference uses exact rational comparisons
and bounded branch-and-bound; candidate code never receives the reference
placements or hidden environment path.
