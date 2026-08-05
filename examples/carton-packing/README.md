# Carton Packing Benchmark

This agentic Eval asks models to implement a dependency-free .NET 10 engine
that packs valued cartons into a 3D container.

The challenge includes rotations, quantities, maximum weight, upright-only
cartons, full-base support, and deterministic output. Models can run a visible
console test harness. Exact hidden scenarios are generated only after every
model session completes. The benchmark selects each configured model's highest
supported reasoning level and uses a 500-credit per-session soft cap.

## Run one model manually

```powershell
$env:Path = "$PWD\.venv\Scripts;$env:Path"
smevals run examples\carton-packing -c copilot -m gpt-5-mini
```

Do not add `-g` until a hidden bundle has been generated and
`CARTON_PACKING_HIDDEN_DIR` points to it.

## Run the precomputed benchmark

```powershell
examples\carton-packing\benchmark\Run-CartonPackingBenchmark.ps1
```

The script:

1. verifies the local toolchain and configured models;
2. runs one isolated workspace session per model without hidden cases;
3. generates a shared hidden bundle;
4. grades every completed Run;
5. writes Markdown/JSON reports and a static site under
   `benchmark\private`.

The 0.0-1.0 score is a weighted mean of capped value and volume ratios. Basic
fit/orientation cases are low-weight contract checks; weight trade-offs,
full-support stacking, and the geometric greedy trap dominate the score.
Calibration places a valid floor-only implementation below the 0.70 pass
threshold while a stronger extreme-point heuristic passes.

See [benchmark/README.md](benchmark/README.md) for options and demo workflow.
