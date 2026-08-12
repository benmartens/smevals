# Replicated-Shard-Rebalancer Grading Summary

**Overall score**: 0.9267

## Metrics

| Metric | Value |
|---|---:|
| build_ok | True |
| valid_target_rate | 1.0 |
| average_objective_score | 0.9267 |
| deterministic | True |
| hidden_cases_valid | 8 |
| hidden_cases_total | 8 |
| runtime_ms | 4544 |

## Per-case results

| Case | Category | Weight | Valid | Score | Runtime ms |
|---|---|---:|:---:|---:|---:|
| overloaded_pair | overload | 2.0 | yes | 1.0000 | 217 |
| uneven_shard_sizes | uneven shard sizes | 3.0 | yes | 1.0000 | 221 |
| zone_scarcity | zone scarcity | 2.0 | yes | 0.8100 | 215 |
| maintenance_exclusions | exclusions | 2.5 | yes | 0.7750 | 215 |
| three_zone_anti_affinity | anti-affinity | 3.0 | yes | 1.0000 | 210 |
| movement_balance_tradeoff | movement/balance tradeoff | 4.0 | yes | 0.8868 | 203 |
| coordinated_swaps | coordinated swaps | 4.0 | yes | 1.0000 | 213 |
| movement_tiebreak | movement tie-breaking | 2.5 | yes | 0.8833 | 205 |
