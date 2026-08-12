namespace ReplicatedShardRebalancer;

public sealed class ReplicatedShardRebalancer
{
    private sealed record Candidate(
        int[] NodeIndexes,
        long MovedBytes,
        int MovedReplicas);

    private sealed record ShardState(
        ShardSpec Shard,
        Candidate[] Candidates);

    private readonly record struct OrderedCandidate(
        int CandidateIndex,
        long ProjectedMaxNumerator,
        long ProjectedMaxDenominator,
        long MovedBytes,
        int MovedReplicas);

    public RebalanceResult Rebalance(RebalanceProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var nodes = (problem.Nodes ?? [])
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .ToArray();
        var shards = (problem.Shards ?? [])
            .OrderBy(shard => shard.Id, StringComparer.Ordinal)
            .ToArray();

        if (shards.Length == 0)
        {
            return RebalanceResult.Empty;
        }

        if (nodes.Length == 0)
        {
            throw new InvalidOperationException(
                "Cannot place shards when no nodes are available.");
        }

        var nodeIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        var nodeCapacities = new long[nodes.Length];
        var zoneIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var nodeZoneIndexes = new int[nodes.Length];

        long totalCapacity = 0;
        for (var i = 0; i < nodes.Length; i++)
        {
            nodeIndexById[nodes[i].Id] = i;
            nodeCapacities[i] = nodes[i].Capacity;
            totalCapacity = checked(totalCapacity + nodes[i].Capacity);

            if (!zoneIndexByName.TryGetValue(nodes[i].Zone, out var zoneIndex))
            {
                zoneIndex = zoneIndexByName.Count;
                zoneIndexByName[nodes[i].Zone] = zoneIndex;
            }
            nodeZoneIndexes[i] = zoneIndex;
        }

        var exclusions = new HashSet<(string ShardId, string NodeId)>();
        foreach (var exclusion in problem.Exclusions ?? [])
        {
            exclusions.Add((exclusion.ShardId, exclusion.NodeId));
        }

        var currentPlacements = new Dictionary<string, HashSet<string>>(
            StringComparer.Ordinal);
        foreach (var placement in problem.CurrentPlacements ?? [])
        {
            currentPlacements[placement.ShardId] = new HashSet<string>(
                placement.NodeIds ?? [],
                StringComparer.Ordinal);
        }

        var shardStates = new ShardState[shards.Length];
        long totalReplicaBytes = 0;
        for (var shardIndex = 0; shardIndex < shards.Length; shardIndex++)
        {
            var shard = shards[shardIndex];
            totalReplicaBytes = checked(totalReplicaBytes + checked(shard.Size * shard.ReplicationFactor));

            var currentNodes = currentPlacements.TryGetValue(shard.Id, out var current)
                ? current
                : new HashSet<string>(StringComparer.Ordinal);
            var candidates = BuildCandidates(
                shard,
                nodes,
                nodeZoneIndexes,
                exclusions,
                currentNodes);
            if (candidates.Length == 0)
            {
                throw new InvalidOperationException(
                    $"No feasible placements found for shard '{shard.Id}'.");
            }
            shardStates[shardIndex] = new(shard, candidates);
        }

        var searchOrder = Enumerable
            .Range(0, shards.Length)
            .OrderBy(index => shardStates[index].Candidates.Length)
            .ThenByDescending(index => shardStates[index].Shard.Size)
            .ThenBy(index => shardStates[index].Shard.Id, StringComparer.Ordinal)
            .ToArray();

        var suffixMinMovedBytes = new long[shards.Length + 1];
        var suffixMinMovedReplicas = new int[shards.Length + 1];
        for (var depth = shards.Length - 1; depth >= 0; depth--)
        {
            var shardIndex = searchOrder[depth];
            var minMovedBytes = shardStates[shardIndex]
                .Candidates
                .Min(candidate => candidate.MovedBytes);
            var minMovedReplicas = shardStates[shardIndex]
                .Candidates
                .Min(candidate => candidate.MovedReplicas);
            suffixMinMovedBytes[depth] = checked(
                suffixMinMovedBytes[depth + 1] + minMovedBytes);
            suffixMinMovedReplicas[depth] =
                suffixMinMovedReplicas[depth + 1] + minMovedReplicas;
        }

        var selectedCandidateByShard = new int[shards.Length];
        var bestCandidateByShard = new int[shards.Length];
        Array.Fill(selectedCandidateByShard, -1);
        Array.Fill(bestCandidateByShard, -1);

        var loads = new long[nodes.Length];
        var avgNumerator = totalReplicaBytes;
        var avgDenominator = totalCapacity;

        var hasBest = false;
        long bestMaxNumerator = 0;
        long bestMaxDenominator = 1;
        var bestSpreadNumerator = System.Numerics.BigInteger.Zero;
        var bestSpreadDenominator = System.Numerics.BigInteger.One;
        long bestMovedBytes = 0;
        int bestMovedReplicas = 0;

        Search(
            depth: 0,
            movedBytes: 0,
            movedReplicas: 0,
            currentMaxNumerator: 0,
            currentMaxDenominator: 1);

        if (!hasBest)
        {
            throw new InvalidOperationException("No feasible target placement found.");
        }

        var placements = new List<ShardPlacement>(shards.Length);
        for (var shardIndex = 0; shardIndex < shards.Length; shardIndex++)
        {
            var candidate = shardStates[shardIndex]
                .Candidates[bestCandidateByShard[shardIndex]];
            var nodeIds = candidate.NodeIndexes
                .Select(nodeIndex => nodes[nodeIndex].Id)
                .ToList();
            placements.Add(new(shards[shardIndex].Id, nodeIds));
        }

        return new(placements);

        void Search(
            int depth,
            long movedBytes,
            int movedReplicas,
            long currentMaxNumerator,
            long currentMaxDenominator)
        {
            var lowerMaxNumerator = currentMaxNumerator;
            var lowerMaxDenominator = currentMaxDenominator;
            if (CompareRatio(
                    avgNumerator,
                    avgDenominator,
                    lowerMaxNumerator,
                    lowerMaxDenominator) > 0)
            {
                lowerMaxNumerator = avgNumerator;
                lowerMaxDenominator = avgDenominator;
            }

            if (hasBest)
            {
                var maxComparison = CompareRatio(
                    lowerMaxNumerator,
                    lowerMaxDenominator,
                    bestMaxNumerator,
                    bestMaxDenominator);
                if (maxComparison > 0)
                {
                    return;
                }

                if (maxComparison == 0)
                {
                    var spreadBoundComparison = CompareSpreadLowerBoundToBest(
                        lowerMaxNumerator,
                        lowerMaxDenominator,
                        avgNumerator,
                        avgDenominator,
                        bestSpreadNumerator,
                        bestSpreadDenominator);
                    if (spreadBoundComparison > 0)
                    {
                        return;
                    }
                    if (spreadBoundComparison == 0)
                    {
                        var movedBytesBound = checked(
                            movedBytes + suffixMinMovedBytes[depth]);
                        if (movedBytesBound > bestMovedBytes)
                        {
                            return;
                        }
                        if (movedBytesBound == bestMovedBytes)
                        {
                            var movedReplicaBound =
                                movedReplicas + suffixMinMovedReplicas[depth];
                            if (movedReplicaBound > bestMovedReplicas)
                            {
                                return;
                            }
                        }
                    }
                }
            }

            if (depth == shards.Length)
            {
                EvaluateLeaf(
                    movedBytes,
                    movedReplicas,
                    currentMaxNumerator,
                    currentMaxDenominator);
                return;
            }

            var shardIndex = searchOrder[depth];
            var shardState = shardStates[shardIndex];
            var shardSize = shardState.Shard.Size;

            var orderedCandidates = BuildOrderedFeasibleCandidates(
                shardState.Candidates,
                loads,
                nodeCapacities,
                shardSize,
                currentMaxNumerator,
                currentMaxDenominator);

            foreach (var orderedCandidate in orderedCandidates)
            {
                var candidate = shardState.Candidates[orderedCandidate.CandidateIndex];
                selectedCandidateByShard[shardIndex] = orderedCandidate.CandidateIndex;

                foreach (var nodeIndex in candidate.NodeIndexes)
                {
                    loads[nodeIndex] = checked(loads[nodeIndex] + shardSize);
                }

                if (!hasBest
                    && depth + 1 < shards.Length
                    && !CanEachRemainingShardStillFit(depth + 1))
                {
                    foreach (var nodeIndex in candidate.NodeIndexes)
                    {
                        loads[nodeIndex] -= shardSize;
                    }
                    selectedCandidateByShard[shardIndex] = -1;
                    continue;
                }

                Search(
                    depth + 1,
                    checked(movedBytes + candidate.MovedBytes),
                    movedReplicas + candidate.MovedReplicas,
                    orderedCandidate.ProjectedMaxNumerator,
                    orderedCandidate.ProjectedMaxDenominator);

                foreach (var nodeIndex in candidate.NodeIndexes)
                {
                    loads[nodeIndex] -= shardSize;
                }
                selectedCandidateByShard[shardIndex] = -1;
            }
        }

        bool CanEachRemainingShardStillFit(int startDepth)
        {
            for (var depth = startDepth; depth < shards.Length; depth++)
            {
                var shardIndex = searchOrder[depth];
                var shardState = shardStates[shardIndex];
                var shardSize = shardState.Shard.Size;

                var foundFeasible = false;
                foreach (var candidate in shardState.Candidates)
                {
                    if (Fits(candidate.NodeIndexes, shardSize))
                    {
                        foundFeasible = true;
                        break;
                    }
                }

                if (!foundFeasible)
                {
                    return false;
                }
            }

            return true;
        }

        bool Fits(int[] nodeIndexes, long shardSize)
        {
            foreach (var nodeIndex in nodeIndexes)
            {
                if (loads[nodeIndex] + shardSize > nodeCapacities[nodeIndex])
                {
                    return false;
                }
            }
            return true;
        }

        void EvaluateLeaf(
            long movedBytes,
            int movedReplicas,
            long maxNumerator,
            long maxDenominator)
        {
            long minNumerator = 0;
            long minDenominator = 1;
            if (nodes.Length > 0)
            {
                minNumerator = loads[0];
                minDenominator = nodeCapacities[0];
                for (var nodeIndex = 1; nodeIndex < nodes.Length; nodeIndex++)
                {
                    if (CompareRatio(
                            loads[nodeIndex],
                            nodeCapacities[nodeIndex],
                            minNumerator,
                            minDenominator) < 0)
                    {
                        minNumerator = loads[nodeIndex];
                        minDenominator = nodeCapacities[nodeIndex];
                    }
                }
            }

            var spreadNumerator =
                (System.Numerics.BigInteger)maxNumerator * minDenominator
                - (System.Numerics.BigInteger)minNumerator * maxDenominator;
            var spreadDenominator =
                (System.Numerics.BigInteger)maxDenominator * minDenominator;

            if (!hasBest
                || IsStrictlyBetterThanBest(
                    maxNumerator,
                    maxDenominator,
                    spreadNumerator,
                    spreadDenominator,
                    movedBytes,
                    movedReplicas))
            {
                hasBest = true;
                bestMaxNumerator = maxNumerator;
                bestMaxDenominator = maxDenominator;
                bestSpreadNumerator = spreadNumerator;
                bestSpreadDenominator = spreadDenominator;
                bestMovedBytes = movedBytes;
                bestMovedReplicas = movedReplicas;
                Array.Copy(
                    selectedCandidateByShard,
                    bestCandidateByShard,
                    selectedCandidateByShard.Length);
            }
        }

        bool IsStrictlyBetterThanBest(
            long maxNumerator,
            long maxDenominator,
            System.Numerics.BigInteger spreadNumerator,
            System.Numerics.BigInteger spreadDenominator,
            long movedBytes,
            int movedReplicas)
        {
            var maxComparison = CompareRatio(
                maxNumerator,
                maxDenominator,
                bestMaxNumerator,
                bestMaxDenominator);
            if (maxComparison != 0)
            {
                return maxComparison < 0;
            }

            var spreadComparison = CompareFraction(
                spreadNumerator,
                spreadDenominator,
                bestSpreadNumerator,
                bestSpreadDenominator);
            if (spreadComparison != 0)
            {
                return spreadComparison < 0;
            }

            if (movedBytes != bestMovedBytes)
            {
                return movedBytes < bestMovedBytes;
            }

            if (movedReplicas != bestMovedReplicas)
            {
                return movedReplicas < bestMovedReplicas;
            }

            return IsLexicographicallySmaller(
                selectedCandidateByShard,
                bestCandidateByShard,
                shardStates);
        }
    }

    private static Candidate[] BuildCandidates(
        ShardSpec shard,
        NodeSpec[] nodes,
        int[] nodeZoneIndexes,
        HashSet<(string ShardId, string NodeId)> exclusions,
        HashSet<string> currentNodes)
    {
        var eligible = new List<int>(nodes.Length);
        var eligibleZones = new HashSet<int>();
        for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
        {
            var node = nodes[nodeIndex];
            if (node.Capacity < shard.Size
                || exclusions.Contains((shard.Id, node.Id)))
            {
                continue;
            }

            eligible.Add(nodeIndex);
            eligibleZones.Add(nodeZoneIndexes[nodeIndex]);
        }

        var requiredZoneCount = Math.Min(
            shard.ReplicationFactor,
            eligibleZones.Count);
        var buffer = new int[shard.ReplicationFactor];
        var candidates = new List<Candidate>();

        void Enumerate(int depth, int start)
        {
            if (depth == shard.ReplicationFactor)
            {
                if (CountDistinctZones(buffer, nodeZoneIndexes) != requiredZoneCount)
                {
                    return;
                }

                var movedReplicas = 0;
                foreach (var nodeIndex in buffer)
                {
                    if (!currentNodes.Contains(nodes[nodeIndex].Id))
                    {
                        movedReplicas++;
                    }
                }

                candidates.Add(new(
                    (int[])buffer.Clone(),
                    checked(shard.Size * movedReplicas),
                    movedReplicas));
                return;
            }

            var needed = shard.ReplicationFactor - depth;
            for (var index = start; index <= eligible.Count - needed; index++)
            {
                buffer[depth] = eligible[index];
                Enumerate(depth + 1, index + 1);
            }
        }

        Enumerate(0, 0);
        return candidates.ToArray();
    }

    private static int CountDistinctZones(
        IReadOnlyList<int> nodeIndexes,
        IReadOnlyList<int> nodeZoneIndexes)
    {
        var zones = new HashSet<int>();
        foreach (var nodeIndex in nodeIndexes)
        {
            zones.Add(nodeZoneIndexes[nodeIndex]);
        }
        return zones.Count;
    }

    private static List<OrderedCandidate> BuildOrderedFeasibleCandidates(
        Candidate[] candidates,
        IReadOnlyList<long> loads,
        IReadOnlyList<long> capacities,
        long shardSize,
        long currentMaxNumerator,
        long currentMaxDenominator)
    {
        var ordered = new List<OrderedCandidate>(candidates.Length);
        for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            var feasible = true;
            var projectedMaxNumerator = currentMaxNumerator;
            var projectedMaxDenominator = currentMaxDenominator;

            foreach (var nodeIndex in candidate.NodeIndexes)
            {
                var nextLoad = loads[nodeIndex] + shardSize;
                var capacity = capacities[nodeIndex];
                if (nextLoad > capacity)
                {
                    feasible = false;
                    break;
                }

                if (CompareRatio(
                        nextLoad,
                        capacity,
                        projectedMaxNumerator,
                        projectedMaxDenominator) > 0)
                {
                    projectedMaxNumerator = nextLoad;
                    projectedMaxDenominator = capacity;
                }
            }

            if (feasible)
            {
                ordered.Add(new(
                    candidateIndex,
                    projectedMaxNumerator,
                    projectedMaxDenominator,
                    candidate.MovedBytes,
                    candidate.MovedReplicas));
            }
        }

        ordered.Sort((left, right) =>
        {
            var maxComparison = CompareRatio(
                left.ProjectedMaxNumerator,
                left.ProjectedMaxDenominator,
                right.ProjectedMaxNumerator,
                right.ProjectedMaxDenominator);
            if (maxComparison != 0)
            {
                return maxComparison;
            }

            var movedBytesComparison = left.MovedBytes.CompareTo(right.MovedBytes);
            if (movedBytesComparison != 0)
            {
                return movedBytesComparison;
            }

            var movedReplicaComparison = left.MovedReplicas.CompareTo(right.MovedReplicas);
            if (movedReplicaComparison != 0)
            {
                return movedReplicaComparison;
            }

            return left.CandidateIndex.CompareTo(right.CandidateIndex);
        });

        return ordered;
    }

    private static bool IsLexicographicallySmaller(
        IReadOnlyList<int> leftSelection,
        IReadOnlyList<int> rightSelection,
        IReadOnlyList<ShardState> shardStates)
    {
        for (var shardIndex = 0; shardIndex < leftSelection.Count; shardIndex++)
        {
            var leftCandidate = shardStates[shardIndex]
                .Candidates[leftSelection[shardIndex]];
            var rightCandidate = shardStates[shardIndex]
                .Candidates[rightSelection[shardIndex]];
            var comparison = CompareNodeIndexSequences(
                leftCandidate.NodeIndexes,
                rightCandidate.NodeIndexes);
            if (comparison != 0)
            {
                return comparison < 0;
            }
        }
        return false;
    }

    private static int CompareNodeIndexSequences(
        IReadOnlyList<int> left,
        IReadOnlyList<int> right)
    {
        for (var index = 0; index < left.Count && index < right.Count; index++)
        {
            var comparison = left[index].CompareTo(right[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return left.Count.CompareTo(right.Count);
    }

    private static int CompareSpreadLowerBoundToBest(
        long maxNumerator,
        long maxDenominator,
        long avgNumerator,
        long avgDenominator,
        System.Numerics.BigInteger bestSpreadNumerator,
        System.Numerics.BigInteger bestSpreadDenominator)
    {
        if (CompareRatio(maxNumerator, maxDenominator, avgNumerator, avgDenominator) <= 0)
        {
            return bestSpreadNumerator.IsZero ? 0 : -1;
        }

        var lowerBoundNumerator =
            (System.Numerics.BigInteger)maxNumerator * avgDenominator
            - (System.Numerics.BigInteger)avgNumerator * maxDenominator;
        var lowerBoundDenominator =
            (System.Numerics.BigInteger)maxDenominator * avgDenominator;
        return CompareFraction(
            lowerBoundNumerator,
            lowerBoundDenominator,
            bestSpreadNumerator,
            bestSpreadDenominator);
    }

    private static int CompareRatio(
        long leftNumerator,
        long leftDenominator,
        long rightNumerator,
        long rightDenominator)
    {
        var left = (System.Numerics.BigInteger)leftNumerator * rightDenominator;
        var right = (System.Numerics.BigInteger)rightNumerator * leftDenominator;
        return left.CompareTo(right);
    }

    private static int CompareFraction(
        System.Numerics.BigInteger leftNumerator,
        System.Numerics.BigInteger leftDenominator,
        System.Numerics.BigInteger rightNumerator,
        System.Numerics.BigInteger rightDenominator)
    {
        var left = leftNumerator * rightDenominator;
        var right = rightNumerator * leftDenominator;
        return left.CompareTo(right);
    }
}
