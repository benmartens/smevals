# Query-Optimizer Grading Summary

**Overall score**: 0.9469

## Metrics

| Metric | Value |
|---|---:|
| build_ok | True |
| valid_plan_rate | 1.0 |
| average_cost_ratio | 0.9469 |
| deterministic | True |
| hidden_cases_valid | 7 |
| hidden_cases_total | 7 |
| runtime_ms | 4861 |

## Per-case results

| Case | Weight | Valid | Score | Candidate cost | Reference cost | Runtime ms |
|---|---:|:---:|---:|---:|---:|---:|
| selective_index | 0.25 | yes | 1.0000 | 2981 | 2981 | 412 |
| join_order_trap | 2.0 | yes | 1.0000 | 2059725 | 2059725 | 206 |
| memory_spill | 2.0 | yes | 0.5151 | 32995 | 16995 | 215 |
| star_schema | 3.0 | yes | 1.0000 | 1157497991 | 1157497991 | 203 |
| chain_eight | 3.0 | yes | 1.0000 | 549370232 | 549370232 | 195 |
| snowflake_ten | 4.0 | yes | 1.0000 | 5022698 | 5022698 | 220 |
| dense_twelve | 4.0 | yes | 1.0000 | 14267999 | 14267999 | 258 |
