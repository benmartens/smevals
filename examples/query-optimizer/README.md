# Query Optimizer Benchmark

This agentic Eval asks models to implement a dependency-free .NET 10
cost-based relational query optimizer.

The grader independently validates physical plan trees and recomputes their
integer execution cost. Valid plans receive partial credit relative to a
deterministic reference optimizer; invalid plans score zero.

Run one model:

```powershell
smevals run examples\query-optimizer -c copilot -m gpt-5-mini
```

Run the complete benchmark:

```powershell
examples\query-optimizer\benchmark\Run-QueryOptimizerBenchmark.ps1
```
