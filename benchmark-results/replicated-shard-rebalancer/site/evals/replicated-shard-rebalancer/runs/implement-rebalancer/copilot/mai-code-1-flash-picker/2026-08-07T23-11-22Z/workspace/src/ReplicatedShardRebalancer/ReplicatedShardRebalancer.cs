namespace ReplicatedShardRebalancer;

public sealed class ReplicatedShardRebalancer
{
    public RebalanceResult Rebalance(RebalanceProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var nodes = (problem.Nodes ?? [])
            .Where(node => !string.IsNullOrWhiteSpace(node.Id))
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .ToList();
        var nodeIds = nodes.Select(node => node.Id).ToList();

        var shards = (problem.Shards ?? [])
            .Where(shard => !string.IsNullOrWhiteSpace(shard.Id))
            .OrderBy(shard => shard.Id, StringComparer.Ordinal)
            .ToList();

        var exclusions = new HashSet<(string ShardId, string NodeId)>(
            (problem.Exclusions ?? [])
                .Where(exclusion => !string.IsNullOrWhiteSpace(exclusion.ShardId) && !string.IsNullOrWhiteSpace(exclusion.NodeId))
                .Select(exclusion => (exclusion.ShardId, exclusion.NodeId)),
            EqualityComparer<(string ShardId, string NodeId)>.Default);

        var currentPlacements = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var placement in problem.CurrentPlacements ?? [])
        {
            if (!currentPlacements.TryGetValue(placement.ShardId, out var nodeSet))
            {
                nodeSet = new HashSet<string>(StringComparer.Ordinal);
                currentPlacements[placement.ShardId] = nodeSet;
            }
            if (placement.NodeIds is not null)
            {
                foreach (var nodeId in placement.NodeIds)
                {
                    nodeSet.Add(nodeId);
                }
            }
        }

        var canonicalShardOrder = shards
            .Select(shard => shard.Id)
            .ToList();
        var canonicalPositionByShardId = canonicalShardOrder
            .Select((shardId, index) => (shardId, index))
            .ToDictionary(pair => pair.shardId, pair => pair.index, StringComparer.Ordinal);

        var candidatesByShardId = new Dictionary<string, List<PlacementChoice>>(StringComparer.Ordinal);
        foreach (var shard in shards)
        {
            var eligibleNodes = nodes
                .Where(node => node.Capacity >= shard.Size && !exclusions.Contains((shard.Id, node.Id)))
                .ToList();
            if (eligibleNodes.Count < shard.ReplicationFactor)
            {
                throw new InvalidOperationException($"Shard '{shard.Id}' has no feasible placement.");
            }

            var requiredZones = Math.Min(
                shard.ReplicationFactor,
                eligibleNodes.Select(node => node.Zone).Distinct(StringComparer.Ordinal).Count());

            var choices = new List<PlacementChoice>();
            var selected = new List<int>(shard.ReplicationFactor);
            void Search(int startIndex)
            {
                if (selected.Count == shard.ReplicationFactor)
                {
                    var zones = selected
                        .Select(index => eligibleNodes[index].Zone)
                        .Distinct(StringComparer.Ordinal)
                        .Count();
                    if (zones != requiredZones)
                    {
                        return;
                    }

                    var chosenNodeIds = selected
                        .Select(index => eligibleNodes[index].Id)
                        .ToList();
                    var movedReplicaCount = 0;
                    var movedBytes = 0L;
                    if (currentPlacements.TryGetValue(shard.Id, out var currentNodeSet))
                    {
                        foreach (var nodeId in chosenNodeIds)
                        {
                            if (!currentNodeSet.Contains(nodeId))
                            {
                                movedReplicaCount++;
                                movedBytes += shard.Size;
                            }
                        }
                    }
                    else
                    {
                        movedReplicaCount = shard.ReplicationFactor;
                        movedBytes = shard.Size * shard.ReplicationFactor;
                    }

                    choices.Add(new PlacementChoice(chosenNodeIds, selected.ToArray(), movedReplicaCount, movedBytes));
                    return;
                }

                for (var index = startIndex; index < eligibleNodes.Count; index++)
                {
                    selected.Add(index);
                    Search(index + 1);
                    selected.RemoveAt(selected.Count - 1);
                }
            }

            Search(0);
            choices.Sort((left, right) => ComparePlacementChoices(left, right));
            candidatesByShardId[shard.Id] = choices;
        }

        var searchOrder = shards
            .OrderBy(shard => candidatesByShardId[shard.Id].Count)
            .ThenByDescending(shard => shard.Size)
            .ThenBy(shard => shard.Id, StringComparer.Ordinal)
            .ToList();

        var memo = new Dictionary<string, CompletionResult>(StringComparer.Ordinal);
        var nodeCapacities = nodes.Select(node => node.Capacity).ToArray();
        var loads = new long[nodes.Count];
        var searchOrderIds = searchOrder.Select(shard => shard.Id).ToList();

        CompletionResult Solve(int shardIndex, IReadOnlyList<long> currentLoads)
        {
            var key = BuildStateKey(shardIndex, currentLoads);
            if (memo.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (shardIndex == searchOrderIds.Count)
            {
                var state = BuildTerminalResult(currentLoads, nodes);
                memo[key] = state;
                return state;
            }

            var shard = searchOrder[shardIndex];
            var best = default(CompletionResult);
            foreach (var choice in candidatesByShardId[shard.Id])
            {
                var newLoads = currentLoads.ToArray();
                foreach (var index in choice.NodeIndices)
                {
                    newLoads[index] += shard.Size;
                    if (newLoads[index] > nodeCapacities[index])
                    {
                        newLoads = null!;
                        break;
                    }
                }
                if (newLoads is null)
                {
                    continue;
                }

                var child = Solve(shardIndex + 1, newLoads);
                var candidateResult = new CompletionResult(
                    child.MaxUtilization,
                    child.UtilizationSpread,
                    child.MovedBytes + choice.MovedBytes,
                    child.MovedReplicaCount + choice.MovedReplicaCount,
                    InsertPlacement(child.Placements, shard.Id, choice.NodeIds, canonicalPositionByShardId));

                if (best is null || IsBetter(candidateResult, best))
                {
                    best = candidateResult;
                }
            }

            if (best is null)
            {
                throw new InvalidOperationException($"No feasible placement found for shard '{shard.Id}'.");
            }

            memo[key] = best;
            return best;
        }

        var result = Solve(0, loads);
        var targetPlacements = result.Placements
            .Select(placement => new ShardPlacement(placement.ShardId, placement.NodeIds.ToList()))
            .ToList();

        return new RebalanceResult(targetPlacements);
    }

    private static string BuildStateKey(int shardIndex, IReadOnlyList<long> loads)
    {
        return string.Join('|', new[] { shardIndex.ToString() }.Concat(loads.Select(load => load.ToString())));
    }

    private static CompletionResult BuildTerminalResult(IReadOnlyList<long> loads, IReadOnlyList<NodeSpec> nodes)
    {
        decimal maxUtilization = 0m;
        decimal minUtilization = 0m;
        for (var index = 0; index < nodes.Count; index++)
        {
            var utilization = (decimal)loads[index] / nodes[index].Capacity;
            if (index == 0)
            {
                maxUtilization = utilization;
                minUtilization = utilization;
            }
            else
            {
                if (utilization > maxUtilization)
                {
                    maxUtilization = utilization;
                }
                if (utilization < minUtilization)
                {
                    minUtilization = utilization;
                }
            }
        }

        return new CompletionResult(maxUtilization, maxUtilization - minUtilization, 0, 0, []);
    }

    private static bool IsBetter(CompletionResult left, CompletionResult right)
    {
        if (left.MaxUtilization != right.MaxUtilization)
        {
            return left.MaxUtilization < right.MaxUtilization;
        }
        if (left.UtilizationSpread != right.UtilizationSpread)
        {
            return left.UtilizationSpread < right.UtilizationSpread;
        }
        if (left.MovedBytes != right.MovedBytes)
        {
            return left.MovedBytes < right.MovedBytes;
        }
        if (left.MovedReplicaCount != right.MovedReplicaCount)
        {
            return left.MovedReplicaCount < right.MovedReplicaCount;
        }
        return ComparePlacements(left.Placements, right.Placements) < 0;
    }

    private static List<PlacementAssignment> InsertPlacement(
        IReadOnlyList<PlacementAssignment> placements,
        string shardId,
        IReadOnlyList<string> nodeIds,
        IReadOnlyDictionary<string, int> canonicalPositionByShardId)
    {
        var result = new List<PlacementAssignment>(placements.Count + 1);
        var newPlacement = new PlacementAssignment(shardId, nodeIds.ToList());
        var position = canonicalPositionByShardId[shardId];
        var inserted = false;
        foreach (var placement in placements)
        {
            if (!inserted && canonicalPositionByShardId[placement.ShardId] > position)
            {
                result.Add(newPlacement);
                inserted = true;
            }
            result.Add(placement);
        }
        if (!inserted)
        {
            result.Add(newPlacement);
        }
        return result;
    }

    private static int ComparePlacements(
        IReadOnlyList<PlacementAssignment> left,
        IReadOnlyList<PlacementAssignment> right)
    {
        var maxCount = Math.Min(left.Count, right.Count);
        for (var index = 0; index < maxCount; index++)
        {
            var leftPlacement = left[index];
            var rightPlacement = right[index];
            var shardOrder = StringComparer.Ordinal.Compare(leftPlacement.ShardId, rightPlacement.ShardId);
            if (shardOrder != 0)
            {
                return shardOrder;
            }
            var nodeOrder = CompareNodeIds(leftPlacement.NodeIds, rightPlacement.NodeIds);
            if (nodeOrder != 0)
            {
                return nodeOrder;
            }
        }
        return left.Count.CompareTo(right.Count);
    }

    private static int CompareNodeIds(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var maxCount = Math.Min(left.Count, right.Count);
        for (var index = 0; index < maxCount; index++)
        {
            var order = StringComparer.Ordinal.Compare(left[index], right[index]);
            if (order != 0)
            {
                return order;
            }
        }
        return left.Count.CompareTo(right.Count);
    }

    private static int ComparePlacementChoices(PlacementChoice left, PlacementChoice right)
    {
        var shardOrder = CompareNodeIds(left.NodeIds, right.NodeIds);
        if (shardOrder != 0)
        {
            return shardOrder;
        }
        var movedReplicaOrder = left.MovedReplicaCount.CompareTo(right.MovedReplicaCount);
        if (movedReplicaOrder != 0)
        {
            return movedReplicaOrder;
        }
        return left.MovedBytes.CompareTo(right.MovedBytes);
    }

    private sealed record PlacementChoice(
        List<string> NodeIds,
        int[] NodeIndices,
        int MovedReplicaCount,
        long MovedBytes);

    private sealed record PlacementAssignment(
        string ShardId,
        List<string> NodeIds);

    private sealed record CompletionResult(
        decimal MaxUtilization,
        decimal UtilizationSpread,
        long MovedBytes,
        int MovedReplicaCount,
        List<PlacementAssignment> Placements);
}
