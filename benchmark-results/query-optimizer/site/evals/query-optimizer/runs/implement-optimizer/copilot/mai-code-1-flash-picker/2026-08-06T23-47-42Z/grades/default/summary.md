# Query-Optimizer Grading Summary

**Overall score**: 0.8486

## Metrics

| Metric | Value |
|---|---:|
| build_ok | True |
| valid_plan_rate | 1.0 |
| average_cost_ratio | 0.8486 |
| deterministic | True |
| hidden_cases_valid | 7 |
| hidden_cases_total | 7 |
| runtime_ms | 6028 |

## Per-case results

| Case | Weight | Valid | Score | Candidate cost | Reference cost | Runtime ms |
|---|---:|:---:|---:|---:|---:|---:|
| selective_index | 0.25 | yes | 1.0000 | 2981 | 2981 | 365 |
| join_order_trap | 2.0 | yes | 1.0000 | 2059725 | 2059725 | 183 |
| memory_spill | 2.0 | yes | 1.0000 | 16995 | 16995 | 183 |
| star_schema | 3.0 | yes | 0.9999 | 1157614301 | 1157497991 | 193 |
| chain_eight | 3.0 | yes | 0.6835 | 803797487 | 549370232 | 198 |
| snowflake_ten | 4.0 | yes | 0.5469 | 9183385 | 5022698 | 410 |
| dense_twelve | 4.0 | yes | 1.0000 | 14267999 | 14267999 | 1362 |
