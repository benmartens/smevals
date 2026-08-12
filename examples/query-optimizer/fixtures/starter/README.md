# Query Optimizer Challenge

Implement `QueryOptimizer.Optimize` in
`src\QueryOptimizer\QueryOptimizer.cs`.

The dependency-free solution targets .NET 10. The JSON models, CLI, integer
cost model, and visible validator are provided. Hidden grading uses an
independent Python implementation and generated workloads.

## Run the visible tests

```powershell
dotnet run --project visible-tests\QueryOptimizer.VisibleTests
```

## Plan contract

The result contains a recursive `plan` node. Leaf operators are:

- `tableScan` with `tableId`;
- `indexSeek` with `tableId` and `indexColumn`.

Binary operators are `nestedLoop`, `hashJoin`, and `mergeJoin`, each with
`left` and `right` children. Join nodes must not set leaf fields.

Every table must appear exactly once. Every join node needs at least one
declared join edge crossing its children. To make equivalent plans
deterministic, the smallest table ID in the left subtree must be
lexicographically smaller than the smallest table ID in the right subtree.

An index seek is legal only when the table has the named index and the query
has an indexable predicate on that column.

## Objective

Minimize `CostModel.ValidateAndCost(...).Metrics.TotalCost`. The model includes
base access cost, filtered cardinalities, nested-loop work, hash-build work and
spill penalties, merge-join sorting, memory limits, and output processing.
All arithmetic is integer, saturating, and deterministic.

Hidden workloads include selective indexes, join-order traps, low-memory hash
spills, star and chain joins, and cases where locally cheap choices lead to
expensive intermediate results.
