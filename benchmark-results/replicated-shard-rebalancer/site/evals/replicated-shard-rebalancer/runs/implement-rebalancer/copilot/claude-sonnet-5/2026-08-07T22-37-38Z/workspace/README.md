# Replicated Shard Rebalancer Challenge

Implement `ReplicatedShardRebalancer.Rebalance` in
`src/ReplicatedShardRebalancer/ReplicatedShardRebalancer.cs`.

The dependency-free solution targets .NET 10. Public models, a JSON CLI, a
visible validator, and a console test harness are supplied. Hidden grading uses
an independent Python validator and exact bounded reference solver.

## Run the visible tests

```powershell
dotnet run --project visible-tests\ReplicatedShardRebalancer.VisibleTests
```

The starter intentionally fails engine tests until `Rebalance` is implemented.

## Try the CLI

```powershell
dotnet run --project src\ReplicatedShardRebalancer.Cli -- `
  scenarios\small-cluster.json result.json
```

The result contains one canonical target placement per shard:

```json
{
  "targetPlacements": [
    {
      "shardId": "orders",
      "nodeIds": ["node-a", "node-c"]
    }
  ]
}
```

## Hard constraints

- Return exactly one target placement for every input shard.
- Return exactly `replicationFactor` distinct node IDs for each shard.
- Every node ID must exist and must not be excluded for that shard.
- A node's load is the sum of `size` for all shard replicas assigned to it and
  must not exceed the node's `capacity`.
- Each shard must use the maximum feasible zone diversity. Eligible nodes are
  non-excluded nodes whose individual capacity can hold that shard. Required
  diversity is the smaller of the replication factor and the number of
  distinct zones among those eligible nodes.
- Sort target placements by ordinal shard ID and each `nodeIds` array by
  ordinal node ID.
- Repeated calls with the same problem must return identical output.

Generated benchmark inputs guarantee that all hard constraints can be met.
Current placements describe movement cost; they may be overloaded or violate
new exclusions.

## Lexicographic objective

The validator derives node loads and movement. Minimize, in this exact order:

1. maximum node utilization (`load / capacity`);
2. utilization spread (`maximum utilization - minimum utilization`);
3. moved bytes (the shard size for each target replica not on a current node);
4. moved replica count.

Only when all four values tie, choose the ordinally smallest complete target
placement. Improving a later item never compensates for worsening an earlier
one.
