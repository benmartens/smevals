# Query-Optimizer Grading Summary

**Overall score**: 0.9933

## Metrics

| Metric | Value |
|---|---:|
| build_ok | True |
| valid_plan_rate | 1.0 |
| average_cost_ratio | 0.9933 |
| deterministic | True |
| hidden_cases_valid | 7 |
| hidden_cases_total | 7 |
| runtime_ms | 4516 |

## Per-case results

| Case | Weight | Valid | Score | Candidate cost | Reference cost | Runtime ms |
|---|---:|:---:|---:|---:|---:|---:|
| selective_index | 0.25 | yes | 1.0000 | 2981 | 2981 | 365 |
| join_order_trap | 2.0 | yes | 1.0000 | 2059725 | 2059725 | 179 |
| memory_spill | 2.0 | yes | 1.0000 | 16995 | 16995 | 179 |
| star_schema | 3.0 | yes | 1.0000 | 1157497991 | 1157497991 | 182 |
| chain_eight | 3.0 | yes | 1.0000 | 549370232 | 549370232 | 182 |
| snowflake_ten | 4.0 | yes | 0.9733 | 5160493 | 5022698 | 215 |
| dense_twelve | 4.0 | yes | 0.9963 | 14320299 | 14267999 | 314 |
