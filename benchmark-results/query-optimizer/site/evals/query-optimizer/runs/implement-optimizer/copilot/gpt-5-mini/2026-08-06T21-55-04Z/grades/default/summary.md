# Query-Optimizer Grading Summary

**Overall score**: 0.0000

## Metrics

| Metric | Value |
|---|---:|
| build_ok | True |
| valid_plan_rate | 0.0 |
| average_cost_ratio | 0.0 |
| deterministic | True |
| hidden_cases_valid | 0 |
| hidden_cases_total | 7 |
| runtime_ms | 4076 |

## Per-case results

| Case | Weight | Valid | Score | Candidate cost | Reference cost | Runtime ms |
|---|---:|:---:|---:|---:|---:|---:|
| selective_index | 0.25 | no | 0.0000 | - | 2981 | 333 |
| join_order_trap | 2.0 | no | 0.0000 | - | 2059725 | 174 |
| memory_spill | 2.0 | no | 0.0000 | - | 16995 | 167 |
| star_schema | 3.0 | no | 0.0000 | - | 1157497991 | 170 |
| chain_eight | 3.0 | no | 0.0000 | - | 549370232 | 172 |
| snowflake_ten | 4.0 | no | 0.0000 | - | 5022698 | 168 |
| dense_twelve | 4.0 | no | 0.0000 | - | 14267999 | 167 |
