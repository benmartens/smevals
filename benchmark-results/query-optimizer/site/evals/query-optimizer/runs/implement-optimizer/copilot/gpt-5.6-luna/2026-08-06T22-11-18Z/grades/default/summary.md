# Query-Optimizer Grading Summary

**Overall score**: 0.4029

## Metrics

| Metric | Value |
|---|---:|
| build_ok | True |
| valid_plan_rate | 1.0 |
| average_cost_ratio | 0.4029 |
| deterministic | True |
| hidden_cases_valid | 7 |
| hidden_cases_total | 7 |
| runtime_ms | 4309 |

## Per-case results

| Case | Weight | Valid | Score | Candidate cost | Reference cost | Runtime ms |
|---|---:|:---:|---:|---:|---:|---:|
| selective_index | 0.25 | yes | 1.0000 | 2981 | 2981 | 364 |
| join_order_trap | 2.0 | yes | 1.0000 | 2059725 | 2059725 | 190 |
| memory_spill | 2.0 | yes | 0.5151 | 32995 | 16995 | 190 |
| star_schema | 3.0 | yes | 0.0860 | 13457333565 | 1157497991 | 193 |
| chain_eight | 3.0 | yes | 0.1375 | 3994133019 | 549370232 | 189 |
| snowflake_ten | 4.0 | yes | 0.4179 | 12018432 | 5022698 | 206 |
| dense_twelve | 4.0 | yes | 0.4325 | 32993251 | 14267999 | 277 |
