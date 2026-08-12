# replicated-shard-rebalancer benchmark results

This directory is a portable snapshot of the precomputed GitHub Copilot CLI
benchmark.

- Working-tree base commit: `f825fd0304aad617321f40c7e43266e584f0b409`
- Hidden-case seed: `4704640833204588591`
- Hidden cases: 8
- Graded model runs: 13
- Grader version: `657df41`

## Disabled models

- `claude-haiku-4.5` - Not available in the current Copilot account

## Missing graded runs

None.

## Files

- `site\` - static smevals report with workspaces, grades, patches, and visual artifacts.
- `report.md` - terminal-style leaderboard and metric summary.
- `report.json` - machine-readable grade rows.
- `DEMO.md` - benchmark narrative plus the current report.
- `hidden_cases.json` - generated hidden bundle used for this snapshot.
- `models.json` - configured model roster and reasoning levels.
- `Serve-Results.ps1` - local static-server helper.
