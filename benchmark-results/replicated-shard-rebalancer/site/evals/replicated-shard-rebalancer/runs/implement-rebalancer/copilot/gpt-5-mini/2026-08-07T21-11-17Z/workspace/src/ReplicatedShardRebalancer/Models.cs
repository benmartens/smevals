namespace ReplicatedShardRebalancer;

public sealed record NodeSpec(
    string Id,
    string Zone,
    long Capacity);

public sealed record ShardSpec(
    string Id,
    long Size,
    int ReplicationFactor);

public sealed record ShardPlacement(
    string ShardId,
    List<string> NodeIds);

public sealed record PlacementExclusion(
    string ShardId,
    string NodeId);

public sealed record RebalanceProblem(
    List<NodeSpec> Nodes,
    List<ShardSpec> Shards,
    List<ShardPlacement> CurrentPlacements,
    List<PlacementExclusion> Exclusions);

public sealed record RebalanceResult(
    List<ShardPlacement> TargetPlacements)
{
    public static RebalanceResult Empty { get; } = new([]);
}

public sealed record ValidationIssue(string Code, string Message);

/// <summary>
/// Validation issues and objective values derived from target placements.
/// Treat objective values as meaningful only when <see cref="IsValid"/> is true.
/// </summary>
public sealed record ValidationReport(
    List<ValidationIssue> Issues,
    IReadOnlyDictionary<string, long> NodeLoads,
    double MaximumNodeUtilization,
    double UtilizationSpread,
    long MovedBytes,
    int MovedReplicaCount)
{
    public bool IsValid => Issues.Count == 0;
}
