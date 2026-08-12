# Query Optimizer Benchmark Results

This directory contains the persisted output of the deterministic query
optimizer benchmark run generated on 2026-08-07.

- `report.md` and `report.json` contain the 12-model leaderboard.
- `hidden_cases.json` contains the generated grading workload and exact
  reference plans.
- `site` contains the static smevals report with source patches, grader
  summaries, and SVG physical plans.
- `DEMO.md` combines the benchmark description and current report.

Gemini 3.1 Pro Preview timed out twice, including a retry with a 40-minute
limit, so it is not included in the graded leaderboard.

Serve the static report:

```powershell
.\benchmark-results\query-optimizer\Serve-Results.ps1
```

