# Carton Packing Demo

Before presenting, serve `benchmark\private\site` with
`python -m http.server 8000` and open `http://127.0.0.1:8000`.

1. Open `fixtures\starter\README.md` and show the incomplete
   `CartonPacker.Pack`.
2. Explain that models can run the visible .NET harness, while the exact
   benchmark cases do not exist yet.
3. Open the static site's full model-matrix leaderboard.
4. Compare `valid_layout_rate`, `average_value_ratio`, support-related tags,
   and total scores.
5. Open `showcase-layout.svg` for a high-scoring and low-scoring model.
6. Compare their `solution.patch` artifacts.
7. Show `summary.md` for one failed scenario and one strong scenario.
8. Emphasize that every model was graded against the same generated bundle and
   that the presentation performs no live model calls.

## Current benchmark results

# carton-packing

- Grader: default (version 7ea8cde)
- Graded: 13 runs (0 failed, 0 ungraded)
- Generated: 2026-08-05T14:19:34Z

## Leaderboard

 1. 1.00  claude-opus-5            (copilot, 1 run)
 1. 1.00  gemini-3.1-pro-preview   (copilot, 1 run)
 1. 1.00  gpt-5.3-codex            (copilot, 1 run)
 1. 1.00  gpt-5.6-luna             (copilot, 1 run)
 1. 1.00  gpt-5.6-sol              (copilot, 1 run)
 1. 1.00  grok-4.5                 (copilot, 1 run)
 7. 0.95  gemini-3.6-flash         (copilot, 1 run)
 7. 0.95  gpt-5.6-terra            (copilot, 1 run)
 9. 0.92  claude-sonnet-5          (copilot, 1 run)
10. 0.87  claude-opus-4.8          (copilot, 1 run)
11. 0.86  claude-sonnet-4.6        (copilot, 1 run)
11. 0.86  gpt-5-mini               (copilot, 1 run)
13. 0.71  mai-code-1-flash-picker  (copilot, 1 run)

## Tags

- 13/13 (100%)  deterministic
- 12/13  (92%)  all_valid
-  9/13  (69%)  high_score
-  1/13   (8%)  issue_run_failure

## claude-opus-5 (copilot)

- score: 1.00 over 1 run
- average_value_ratio: 1.00
- average_volume_ratio: 1.00
- build_ok: 100%
- deterministic: 100%
- hidden_cases_total: 8.00
- hidden_cases_valid: 8.00
- runtime_ms: 2702.00
- valid_layout_rate: 1.00
- tags: all_valid, deterministic, high_score

## gemini-3.1-pro-preview (copilot)

- score: 1.00 over 1 run
- average_value_ratio: 1.00
- average_volume_ratio: 1.00
- build_ok: 100%
- deterministic: 100%
- hidden_cases_total: 8.00
- hidden_cases_valid: 8.00
- runtime_ms: 3797.00
- valid_layout_rate: 1.00
- tags: all_valid, deterministic, high_score

## gpt-5.3-codex (copilot)

- score: 1.00 over 1 run
- average_value_ratio: 1.00
- average_volume_ratio: 1.00
- build_ok: 100%
- deterministic: 100%
- hidden_cases_total: 8.00
- hidden_cases_valid: 8.00
- runtime_ms: 3140.00
- valid_layout_rate: 1.00
- tags: all_valid, deterministic, high_score

## gpt-5.6-luna (copilot)

- score: 1.00 over 1 run
- average_value_ratio: 1.00
- average_volume_ratio: 1.00
- build_ok: 100%
- deterministic: 100%
- hidden_cases_total: 8.00
- hidden_cases_valid: 8.00
- runtime_ms: 4937.00
- valid_layout_rate: 1.00
- tags: all_valid, deterministic, high_score

## gpt-5.6-sol (copilot)

- score: 1.00 over 1 run
- average_value_ratio: 1.00
- average_volume_ratio: 1.00
- build_ok: 100%
- deterministic: 100%
- hidden_cases_total: 8.00
- hidden_cases_valid: 8.00
- runtime_ms: 11875.00
- valid_layout_rate: 1.00
- tags: all_valid, deterministic, high_score

## grok-4.5 (copilot)

- score: 1.00 over 1 run
- average_value_ratio: 1.00
- average_volume_ratio: 1.00
- build_ok: 100%
- deterministic: 100%
- hidden_cases_total: 8.00
- hidden_cases_valid: 8.00
- runtime_ms: 5827.00
- valid_layout_rate: 1.00
- tags: all_valid, deterministic, high_score

## gemini-3.6-flash (copilot)

- score: 0.95 over 1 run
- average_value_ratio: 0.96
- average_volume_ratio: 0.92
- build_ok: 100%
- deterministic: 100%
- hidden_cases_total: 8.00
- hidden_cases_valid: 8.00
- runtime_ms: 4922.00
- valid_layout_rate: 1.00
- tags: all_valid, deterministic, high_score

## gpt-5.6-terra (copilot)

- score: 0.95 over 1 run
- average_value_ratio: 0.96
- average_volume_ratio: 0.92
- build_ok: 100%
- deterministic: 100%
- hidden_cases_total: 8.00
- hidden_cases_valid: 8.00
- runtime_ms: 2687.00
- valid_layout_rate: 1.00
- tags: all_valid, deterministic, high_score

## claude-sonnet-5 (copilot)

- score: 0.92 over 1 run
- average_value_ratio: 0.92
- average_volume_ratio: 0.93
- build_ok: 100%
- deterministic: 100%
- hidden_cases_total: 8.00
- hidden_cases_valid: 8.00
- runtime_ms: 2327.00
- valid_layout_rate: 1.00
- tags: all_valid, deterministic, high_score

## claude-opus-4.8 (copilot)

- score: 0.87 over 1 run
- average_value_ratio: 0.88
- average_volume_ratio: 0.85
- build_ok: 100%
- deterministic: 100%
- hidden_cases_total: 8.00
- hidden_cases_valid: 8.00
- runtime_ms: 2905.00
- valid_layout_rate: 1.00
- tags: all_valid, deterministic

## claude-sonnet-4.6 (copilot)

- score: 0.86 over 1 run
- average_value_ratio: 0.87
- average_volume_ratio: 0.82
- build_ok: 100%
- deterministic: 100%
- hidden_cases_total: 8.00
- hidden_cases_valid: 8.00
- runtime_ms: 2312.00
- valid_layout_rate: 1.00
- tags: all_valid, deterministic

## gpt-5-mini (copilot)

- score: 0.86 over 1 run
- average_value_ratio: 0.87
- average_volume_ratio: 0.82
- build_ok: 100%
- deterministic: 100%
- hidden_cases_total: 8.00
- hidden_cases_valid: 8.00
- runtime_ms: 2344.00
- valid_layout_rate: 1.00
- tags: all_valid, deterministic

## mai-code-1-flash-picker (copilot)

- score: 0.71 over 1 run
- average_value_ratio: 0.71
- average_volume_ratio: 0.71
- build_ok: 100%
- deterministic: 100%
- hidden_cases_total: 8.00
- hidden_cases_valid: 7.00
- runtime_ms: 32187.00
- valid_layout_rate: 0.88
- tags: deterministic, issue_run_failure

