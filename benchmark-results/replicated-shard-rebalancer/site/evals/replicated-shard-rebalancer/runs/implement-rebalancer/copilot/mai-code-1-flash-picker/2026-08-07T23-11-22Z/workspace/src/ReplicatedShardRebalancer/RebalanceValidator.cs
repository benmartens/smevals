namespace ReplicatedShardRebalancer;

public static class RebalanceValidator
{
    public static ValidationReport Validate(
        RebalanceProblem problem,
        RebalanceResult result)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(result);

        var issues = new List<ValidationIssue>();
        var nodes = new Dictionary<string, NodeSpec>(StringComparer.Ordinal);
        var shards = new Dictionary<string, ShardSpec>(StringComparer.Ordinal);
        var current = new Dictionary<string, HashSet<string>>(
            StringComparer.Ordinal);
        var exclusions = new HashSet<(string ShardId, string NodeId)>();
        ValidateProblem(problem, nodes, shards, current, exclusions, issues);

        var loads = nodes.Keys.ToDictionary(
            id => id,
            _ => 0L,
            StringComparer.Ordinal);
        var seenShards = new HashSet<string>(StringComparer.Ordinal);
        long movedBytes = 0;
        var movedReplicas = 0;

        if (result.TargetPlacements is null)
        {
            issues.Add(new(
                "missing_target_placements",
                "targetPlacements must be an array."));
            return BuildReport(issues, nodes, loads, 0, 0);
        }

        if (!result.TargetPlacements.SequenceEqual(
                result.TargetPlacements.OrderBy(
                    placement => placement.ShardId,
                    StringComparer.Ordinal)))
        {
            issues.Add(new(
                "noncanonical_shard_order",
                "Target placements must be sorted by shard ID."));
        }

        foreach (var placement in result.TargetPlacements)
        {
            if (!shards.TryGetValue(placement.ShardId, out var shard))
            {
                issues.Add(new(
                    "unknown_shard",
                    $"Unknown shard ID '{placement.ShardId}'."));
                continue;
            }

            if (!seenShards.Add(placement.ShardId))
            {
                issues.Add(new(
                    "duplicate_shard",
                    $"Shard '{placement.ShardId}' appears more than once."));
            }

            if (placement.NodeIds is null)
            {
                issues.Add(new(
                    "missing_node_ids",
                    $"Shard '{placement.ShardId}' has no nodeIds array."));
                continue;
            }

            if (placement.NodeIds.Count != shard.ReplicationFactor)
            {
                issues.Add(new(
                    "replica_count",
                    $"Shard '{shard.Id}' requires {shard.ReplicationFactor} replicas."));
            }

            if (!placement.NodeIds.SequenceEqual(
                    placement.NodeIds.OrderBy(id => id, StringComparer.Ordinal)))
            {
                issues.Add(new(
                    "noncanonical_node_order",
                    $"Node IDs for shard '{shard.Id}' must be sorted."));
            }

            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var nodeId in placement.NodeIds)
            {
                if (!unique.Add(nodeId))
                {
                    issues.Add(new(
                        "duplicate_node",
                        $"Shard '{shard.Id}' repeats node '{nodeId}'."));
                    continue;
                }
                if (!nodes.TryGetValue(nodeId, out _))
                {
                    issues.Add(new(
                        "unknown_node",
                        $"Shard '{shard.Id}' targets unknown node '{nodeId}'."));
                    continue;
                }
                if (exclusions.Contains((shard.Id, nodeId)))
                {
                    issues.Add(new(
                        "excluded_node",
                        $"Shard '{shard.Id}' is excluded from node '{nodeId}'."));
                }

                loads[nodeId] = checked(loads[nodeId] + shard.Size);
                if (!current.GetValueOrDefault(shard.Id, []).Contains(nodeId))
                {
                    movedBytes = checked(movedBytes + shard.Size);
                    movedReplicas++;
                }
            }

            var usedZones = unique
                .Where(nodes.ContainsKey)
                .Select(nodeId => nodes[nodeId].Zone)
                .Distinct(StringComparer.Ordinal)
                .Count();
            var requiredZones = MaximumZoneDiversity(
                shard,
                nodes.Values,
                exclusions);
            if (usedZones != requiredZones)
            {
                issues.Add(new(
                    "zone_diversity",
                    $"Shard '{shard.Id}' uses {usedZones} zones; "
                    + $"{requiredZones} are required."));
            }
        }

        foreach (var shard in shards.Values)
        {
            if (!seenShards.Contains(shard.Id))
            {
                issues.Add(new(
                    "missing_shard",
                    $"Target placement for shard '{shard.Id}' is missing."));
            }
        }

        foreach (var node in nodes.Values)
        {
            if (loads[node.Id] > node.Capacity)
            {
                issues.Add(new(
                    "capacity_exceeded",
                    $"Node '{node.Id}' load {loads[node.Id]} exceeds "
                    + $"capacity {node.Capacity}."));
            }
        }

        return BuildReport(
            issues,
            nodes,
            loads,
            movedBytes,
            movedReplicas);
    }

    public static int MaximumZoneDiversity(
        ShardSpec shard,
        IEnumerable<NodeSpec> nodes,
        ISet<(string ShardId, string NodeId)> exclusions)
    {
        var eligible = nodes
            .Where(node =>
                node.Capacity >= shard.Size
                && !exclusions.Contains((shard.Id, node.Id)))
            .ToArray();
        return Math.Min(
            shard.ReplicationFactor,
            eligible.Select(node => node.Zone)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    private static ValidationReport BuildReport(
        List<ValidationIssue> issues,
        IReadOnlyDictionary<string, NodeSpec> nodes,
        IReadOnlyDictionary<string, long> loads,
        long movedBytes,
        int movedReplicas)
    {
        var utilization = nodes.Values
            .Select(node => node.Capacity > 0
                ? (double)loads.GetValueOrDefault(node.Id) / node.Capacity
                : 0)
            .ToArray();
        var maximum = utilization.Length == 0 ? 0 : utilization.Max();
        var minimum = utilization.Length == 0 ? 0 : utilization.Min();
        return new(
            issues,
            new Dictionary<string, long>(loads, StringComparer.Ordinal),
            maximum,
            maximum - minimum,
            movedBytes,
            movedReplicas);
    }

    private static void ValidateProblem(
        RebalanceProblem problem,
        Dictionary<string, NodeSpec> nodes,
        Dictionary<string, ShardSpec> shards,
        Dictionary<string, HashSet<string>> current,
        HashSet<(string ShardId, string NodeId)> exclusions,
        List<ValidationIssue> issues)
    {
        foreach (var node in problem.Nodes ?? [])
        {
            if (string.IsNullOrWhiteSpace(node.Id)
                || string.IsNullOrWhiteSpace(node.Zone)
                || node.Capacity <= 0)
            {
                issues.Add(new("invalid_node", $"Node '{node.Id}' is invalid."));
            }
            else if (!nodes.TryAdd(node.Id, node))
            {
                issues.Add(new(
                    "duplicate_node_id",
                    $"Node ID '{node.Id}' is duplicated."));
            }
        }

        foreach (var shard in problem.Shards ?? [])
        {
            if (string.IsNullOrWhiteSpace(shard.Id)
                || shard.Size <= 0
                || shard.ReplicationFactor <= 0)
            {
                issues.Add(new(
                    "invalid_shard",
                    $"Shard '{shard.Id}' is invalid."));
            }
            else if (!shards.TryAdd(shard.Id, shard))
            {
                issues.Add(new(
                    "duplicate_shard_id",
                    $"Shard ID '{shard.Id}' is duplicated."));
            }
        }

        foreach (var exclusion in problem.Exclusions ?? [])
        {
            if (!shards.ContainsKey(exclusion.ShardId)
                || !nodes.ContainsKey(exclusion.NodeId))
            {
                issues.Add(new(
                    "invalid_exclusion",
                    $"Invalid exclusion '{exclusion.ShardId}/{exclusion.NodeId}'."));
            }
            else if (!exclusions.Add((exclusion.ShardId, exclusion.NodeId)))
            {
                issues.Add(new(
                    "duplicate_exclusion",
                    $"Duplicate exclusion '{exclusion.ShardId}/{exclusion.NodeId}'."));
            }
        }

        foreach (var placement in problem.CurrentPlacements ?? [])
        {
            if (!shards.TryGetValue(placement.ShardId, out var shard))
            {
                issues.Add(new(
                    "invalid_current_placement",
                    $"Unknown current shard '{placement.ShardId}'."));
                continue;
            }
            if (current.ContainsKey(placement.ShardId))
            {
                issues.Add(new(
                    "duplicate_current_shard",
                    $"Current shard '{placement.ShardId}' is duplicated."));
                continue;
            }
            var nodeIds = new HashSet<string>(
                placement.NodeIds ?? [],
                StringComparer.Ordinal);
            current[placement.ShardId] = nodeIds;
            if (placement.NodeIds is null
                || nodeIds.Count != shard.ReplicationFactor
                || nodeIds.Any(nodeId => !nodes.ContainsKey(nodeId)))
            {
                issues.Add(new(
                    "invalid_current_placement",
                    $"Current placement for '{shard.Id}' is invalid."));
            }
        }

        foreach (var shard in shards.Values)
        {
            if (!current.ContainsKey(shard.Id))
            {
                issues.Add(new(
                    "missing_current_shard",
                    $"Current placement for '{shard.Id}' is missing."));
            }
            var eligible = nodes.Values.Count(node =>
                node.Capacity >= shard.Size
                && !exclusions.Contains((shard.Id, node.Id)));
            if (eligible < shard.ReplicationFactor)
            {
                issues.Add(new(
                    "infeasible_shard",
                    $"Shard '{shard.Id}' has too few eligible nodes."));
            }
        }
    }
}
