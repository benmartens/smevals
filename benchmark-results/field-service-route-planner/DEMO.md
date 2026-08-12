# Field-Service Route Planner Demo

1. Show the incomplete `RoutePlanner.Plan` and visible tests.
2. Explain that hidden cases are generated after model runs.
3. Compare valid-route rate, served-value ratio, travel quality, and score.
4. Open `showcase-route.svg` to compare technician timelines.
5. Compare `solution.patch` and `summary.md` for weak and strong runs.

## Current benchmark results

# field-service-route-planner

- Grader: default (version f2af056)
- Graded: 13 runs (1 failed, 0 ungraded)
- Generated: 2026-08-07T22:35:15Z

## Leaderboard

 1. 1.00  claude-opus-4.8          (copilot, 1 run)
 1. 1.00  claude-opus-5            (copilot, 1 run)
 1. 1.00  claude-sonnet-4.6        (copilot, 1 run)
 1. 1.00  gemini-3.6-flash         (copilot, 1 run)
 1. 1.00  gpt-5-mini               (copilot, 1 run)
 1. 1.00  gpt-5.3-codex            (copilot, 1 run)
 1. 1.00  gpt-5.6-luna             (copilot, 1 run)
 1. 1.00  gpt-5.6-sol              (copilot, 1 run)
 1. 1.00  gpt-5.6-terra            (copilot, 1 run)
 1. 1.00  grok-4.5                 (copilot, 1 run)
 1. 1.00  mai-code-1-flash-picker  (copilot, 1 run)
 1. 1.00  claude-sonnet-5          (copilot, 1 run)
13. 0.00  gemini-3.1-pro-preview   (copilot, 1 run, 1 fail)

## Tags

-  1/13   (8%)  asymmetric_travel
-  1/13   (8%)  clustering
-  1/13   (8%)  cross_technician
-  1/13   (8%)  invalid_routes
-  1/13   (8%)  ordering
-  1/13   (8%)  skills
-  1/13   (8%)  time_windows
-  1/13   (8%)  value_tradeoff
-  1/13   (8%)  value_trap
-  1/13   (8%)  waiting

## claude-opus-4.8 (copilot)

- score: 1.00 over 1 run
- average_travel_ratio: 1.00
- average_value_ratio: 1.00
- build_succeeded: 100%
- deterministic: 100%
- valid_route_rate: 1.00

## claude-opus-5 (copilot)

- score: 1.00 over 1 run
- average_travel_ratio: 1.00
- average_value_ratio: 1.00
- build_succeeded: 100%
- deterministic: 100%
- valid_route_rate: 1.00

## claude-sonnet-4.6 (copilot)

- score: 1.00 over 1 run
- average_travel_ratio: 1.00
- average_value_ratio: 1.00
- build_succeeded: 100%
- deterministic: 100%
- valid_route_rate: 1.00

## gemini-3.6-flash (copilot)

- score: 1.00 over 1 run
- average_travel_ratio: 1.00
- average_value_ratio: 1.00
- build_succeeded: 100%
- deterministic: 100%
- valid_route_rate: 1.00

## gpt-5-mini (copilot)

- score: 1.00 over 1 run
- average_travel_ratio: 1.00
- average_value_ratio: 1.00
- build_succeeded: 100%
- deterministic: 100%
- valid_route_rate: 1.00

## gpt-5.3-codex (copilot)

- score: 1.00 over 1 run
- average_travel_ratio: 1.00
- average_value_ratio: 1.00
- build_succeeded: 100%
- deterministic: 100%
- valid_route_rate: 1.00

## gpt-5.6-luna (copilot)

- score: 1.00 over 1 run
- average_travel_ratio: 1.00
- average_value_ratio: 1.00
- build_succeeded: 100%
- deterministic: 100%
- valid_route_rate: 1.00

## gpt-5.6-sol (copilot)

- score: 1.00 over 1 run
- average_travel_ratio: 1.00
- average_value_ratio: 1.00
- build_succeeded: 100%
- deterministic: 100%
- valid_route_rate: 1.00

## gpt-5.6-terra (copilot)

- score: 1.00 over 1 run
- average_travel_ratio: 1.00
- average_value_ratio: 1.00
- build_succeeded: 100%
- deterministic: 100%
- valid_route_rate: 1.00

## grok-4.5 (copilot)

- score: 1.00 over 1 run
- average_travel_ratio: 1.00
- average_value_ratio: 1.00
- build_succeeded: 100%
- deterministic: 100%
- valid_route_rate: 1.00

## mai-code-1-flash-picker (copilot)

- score: 1.00 over 1 run
- average_travel_ratio: 1.00
- average_value_ratio: 1.00
- build_succeeded: 100%
- deterministic: 100%
- valid_route_rate: 1.00

## claude-sonnet-5 (copilot)

- score: 1.00 over 1 run
- average_travel_ratio: 1.00
- average_value_ratio: 1.00
- build_succeeded: 100%
- deterministic: 100%
- valid_route_rate: 1.00

## gemini-3.1-pro-preview (copilot)

- score: 0.00 over 1 run, 1 fail
- average_travel_ratio: 0.00
- average_value_ratio: 0.00
- build_succeeded: 100%
- deterministic: 100%
- valid_route_rate: 0.00
- tags: asymmetric_travel, clustering, cross_technician, invalid_routes, ordering, skills, time_windows, value_tradeoff, value_trap, waiting

