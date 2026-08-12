# Carton Packing Benchmark Results

This directory is a portable snapshot of the precomputed GitHub Copilot CLI
carton-packing benchmark.

- Source commit: `06d8e13a0d67687b5c3e2c07619f7daa0b6cf53d`
- Hidden-case seed: `1340041565`
- Hidden scenarios: 8
- Graded model Runs: 13
- Claude Haiku 4.5 remains listed in `models.json` but was disabled because it
  was unavailable in the current Copilot account.

## View the interactive site

From this directory:

```powershell
.\Serve-Results.ps1
```

Then open `http://127.0.0.1:8000`.

You can also use any static HTTP server:

```powershell
Set-Location site
python -m http.server 8000
```

Opening `site\index.html` directly may not work because browsers restrict local
JSON fetches.

## Files

- `site\` - self-contained smevals static site with model workspaces, grades,
  SVG layouts, source patches, and output artifacts.
- `report.md` - terminal-style leaderboard and metric summary.
- `report.json` - machine-readable grade rows.
- `DEMO.md` - presentation sequence plus the current benchmark report.
- `hidden_cases.json` - generated hidden bundle used for this grading snapshot.
- `models.json` - configured model roster and reasoning levels.
