# Replicated-Shard-Rebalancer Grading Summary

**Overall score**: 0.7776

## Metrics

| Metric | Value |
|---|---:|
| build_ok | True |
| valid_target_rate | 0.875 |
| average_objective_score | 0.8016 |
| deterministic | False |
| hidden_cases_valid | 7 |
| hidden_cases_total | 8 |
| runtime_ms | 4525 |

## Per-case results

| Case | Category | Weight | Valid | Score | Runtime ms |
|---|---|---:|:---:|---:|---:|
| overloaded_pair | overload | 2.0 | yes | 1.0000 | 214 |
| uneven_shard_sizes | uneven shard sizes | 3.0 | yes | 1.0000 | 220 |
| zone_scarcity | zone scarcity | 2.0 | yes | 1.0000 | 218 |
| maintenance_exclusions | exclusions | 2.5 | yes | 0.7750 | 229 |
| three_zone_anti_affinity | anti-affinity | 3.0 | yes | 1.0000 | 223 |
| movement_balance_tradeoff | movement/balance tradeoff | 4.0 | yes | 1.0000 | 217 |
| coordinated_swaps | coordinated swaps | 4.0 | no | 0.0000 | 216 |
| movement_tiebreak | movement tie-breaking | 2.5 | yes | 1.0000 | 213 |
