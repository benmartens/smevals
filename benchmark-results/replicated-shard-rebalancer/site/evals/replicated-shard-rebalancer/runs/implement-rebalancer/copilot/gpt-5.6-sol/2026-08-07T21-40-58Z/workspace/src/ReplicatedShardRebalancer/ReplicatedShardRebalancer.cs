using System.Numerics;

namespace ReplicatedShardRebalancer;

public sealed class ReplicatedShardRebalancer
{
    public RebalanceResult Rebalance(RebalanceProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new Solver(problem).Solve();
    }

    private sealed class Solver
    {
        private readonly NodeSpec[] nodes;
        private readonly ShardInfo[] shards;
        private readonly long[] loads;
        private readonly Candidate?[] selected;
        private readonly BigInteger totalReplicaBytes;
        private readonly BigInteger totalCapacity;
        private readonly BigInteger[,] suffixPossibleLoads;
        private readonly BigInteger[,] suffixMandatoryLoads;
        private readonly BigInteger[] suffixReplicaBytes;
        private readonly BigInteger[] suffixMinimumMovedBytes;
        private readonly int[] suffixMinimumMovedReplicas;
        private readonly Dictionary<LoadKey, MovementCost>[] seenStates;

        private Objective? bestObjective;
        private Candidate?[]? bestSelection;
        private long[]? bestLoadLimits;

        public Solver(RebalanceProblem problem)
        {
            nodes = ReadNodes(problem);
            var nodeIndexes = nodes
                .Select((node, index) => (node.Id, index))
                .ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);

            var shardSpecs = ReadShards(problem);
            var shardById = shardSpecs.ToDictionary(
                shard => shard.Id,
                StringComparer.Ordinal);
            var exclusions = ReadExclusions(problem, nodeIndexes, shardById);
            var current = ReadCurrentPlacements(problem, nodeIndexes, shardById);

            shards = new ShardInfo[shardSpecs.Length];
            BigInteger replicaBytes = 0;
            for (var index = 0; index < shardSpecs.Length; index++)
            {
                var shard = shardSpecs[index];
                shards[index] = BuildShardInfo(
                    shard,
                    current[shard.Id],
                    exclusions);
                replicaBytes += (BigInteger)shard.Size * shard.ReplicationFactor;
            }

            totalReplicaBytes = replicaBytes;
            totalCapacity = nodes.Aggregate(
                BigInteger.Zero,
                (sum, node) => sum + node.Capacity);
            loads = new long[nodes.Length];
            selected = new Candidate?[shards.Length];

            suffixPossibleLoads = new BigInteger[shards.Length + 1, nodes.Length];
            suffixMandatoryLoads = new BigInteger[shards.Length + 1, nodes.Length];
            suffixReplicaBytes = new BigInteger[shards.Length + 1];
            suffixMinimumMovedBytes = new BigInteger[shards.Length + 1];
            suffixMinimumMovedReplicas = new int[shards.Length + 1];

            for (var shardIndex = shards.Length - 1; shardIndex >= 0; shardIndex--)
            {
                var info = shards[shardIndex];
                suffixReplicaBytes[shardIndex] =
                    suffixReplicaBytes[shardIndex + 1]
                    + ((BigInteger)info.Spec.Size * info.Spec.ReplicationFactor);
                suffixMinimumMovedBytes[shardIndex] =
                    suffixMinimumMovedBytes[shardIndex + 1]
                    + info.MinimumMovedBytes;
                suffixMinimumMovedReplicas[shardIndex] = checked(
                    suffixMinimumMovedReplicas[shardIndex + 1]
                    + info.MinimumMovedReplicas);

                for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
                {
                    suffixPossibleLoads[shardIndex, nodeIndex] =
                        suffixPossibleLoads[shardIndex + 1, nodeIndex]
                        + (info.PossibleNodes[nodeIndex] ? info.Spec.Size : 0);
                    suffixMandatoryLoads[shardIndex, nodeIndex] =
                        suffixMandatoryLoads[shardIndex + 1, nodeIndex]
                        + (info.MandatoryNodes[nodeIndex] ? info.Spec.Size : 0);
                }
            }

            seenStates = Enumerable.Range(0, shards.Length + 1)
                .Select(_ => new Dictionary<LoadKey, MovementCost>())
                .ToArray();
        }

        public RebalanceResult Solve()
        {
            if (!FindInitialSolution())
            {
                throw new InvalidOperationException(
                    "No placement satisfies all rebalancing constraints.");
            }

            Array.Clear(loads);
            Array.Clear(selected);
            Search(0, 0, 0);

            if (bestSelection is null)
            {
                throw new InvalidOperationException(
                    "No placement satisfies all rebalancing constraints.");
            }

            var placements = new List<ShardPlacement>(shards.Length);
            for (var shardIndex = 0; shardIndex < shards.Length; shardIndex++)
            {
                var candidate = bestSelection[shardIndex]
                    ?? throw new InvalidOperationException("Incomplete solver result.");
                placements.Add(new(
                    shards[shardIndex].Spec.Id,
                    candidate.Nodes.Select(nodeIndex => nodes[nodeIndex].Id).ToList()));
            }

            return new(placements);
        }

        private static NodeSpec[] ReadNodes(RebalanceProblem problem)
        {
            if (problem.Nodes is null)
            {
                throw new ArgumentException("nodes must be an array.", nameof(problem));
            }

            var result = problem.Nodes
                .OrderBy(node => node.Id, StringComparer.Ordinal)
                .ToArray();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in result)
            {
                if (node is null
                    || string.IsNullOrWhiteSpace(node.Id)
                    || string.IsNullOrWhiteSpace(node.Zone)
                    || node.Capacity <= 0)
                {
                    throw new ArgumentException("All nodes must be valid.", nameof(problem));
                }

                if (!ids.Add(node.Id))
                {
                    throw new ArgumentException(
                        $"Node ID '{node.Id}' is duplicated.",
                        nameof(problem));
                }
            }

            return result;
        }

        private static ShardSpec[] ReadShards(RebalanceProblem problem)
        {
            if (problem.Shards is null)
            {
                throw new ArgumentException("shards must be an array.", nameof(problem));
            }

            var result = problem.Shards
                .OrderBy(shard => shard.Id, StringComparer.Ordinal)
                .ToArray();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var shard in result)
            {
                if (shard is null
                    || string.IsNullOrWhiteSpace(shard.Id)
                    || shard.Size <= 0
                    || shard.ReplicationFactor <= 0)
                {
                    throw new ArgumentException("All shards must be valid.", nameof(problem));
                }

                if (!ids.Add(shard.Id))
                {
                    throw new ArgumentException(
                        $"Shard ID '{shard.Id}' is duplicated.",
                        nameof(problem));
                }
            }

            return result;
        }

        private static HashSet<(string ShardId, string NodeId)> ReadExclusions(
            RebalanceProblem problem,
            IReadOnlyDictionary<string, int> nodeIndexes,
            IReadOnlyDictionary<string, ShardSpec> shardById)
        {
            if (problem.Exclusions is null)
            {
                throw new ArgumentException("exclusions must be an array.", nameof(problem));
            }

            var result = new HashSet<(string ShardId, string NodeId)>();
            foreach (var exclusion in problem.Exclusions)
            {
                if (exclusion is null
                    || !shardById.ContainsKey(exclusion.ShardId)
                    || !nodeIndexes.ContainsKey(exclusion.NodeId))
                {
                    throw new ArgumentException(
                        "Every exclusion must reference a known shard and node.",
                        nameof(problem));
                }

                if (!result.Add((exclusion.ShardId, exclusion.NodeId)))
                {
                    throw new ArgumentException(
                        $"Exclusion '{exclusion.ShardId}/{exclusion.NodeId}' is duplicated.",
                        nameof(problem));
                }
            }

            return result;
        }

        private static Dictionary<string, HashSet<string>> ReadCurrentPlacements(
            RebalanceProblem problem,
            IReadOnlyDictionary<string, int> nodeIndexes,
            IReadOnlyDictionary<string, ShardSpec> shardById)
        {
            if (problem.CurrentPlacements is null)
            {
                throw new ArgumentException(
                    "currentPlacements must be an array.",
                    nameof(problem));
            }

            var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var placement in problem.CurrentPlacements)
            {
                if (placement is null
                    || !shardById.TryGetValue(placement.ShardId, out var shard)
                    || placement.NodeIds is null)
                {
                    throw new ArgumentException(
                        "Every current placement must reference a known shard.",
                        nameof(problem));
                }

                var nodeIds = new HashSet<string>(
                    placement.NodeIds,
                    StringComparer.Ordinal);
                if (placement.NodeIds.Count != shard.ReplicationFactor
                    || nodeIds.Count != shard.ReplicationFactor
                    || nodeIds.Any(nodeId => !nodeIndexes.ContainsKey(nodeId)))
                {
                    throw new ArgumentException(
                        $"Current placement for '{shard.Id}' is invalid.",
                        nameof(problem));
                }

                if (!result.TryAdd(shard.Id, nodeIds))
                {
                    throw new ArgumentException(
                        $"Current placement for '{shard.Id}' is duplicated.",
                        nameof(problem));
                }
            }

            foreach (var shardId in shardById.Keys)
            {
                if (!result.ContainsKey(shardId))
                {
                    throw new ArgumentException(
                        $"Current placement for '{shardId}' is missing.",
                        nameof(problem));
                }
            }

            return result;
        }

        private ShardInfo BuildShardInfo(
            ShardSpec shard,
            ISet<string> currentNodes,
            ISet<(string ShardId, string NodeId)> exclusions)
        {
            var eligible = Enumerable.Range(0, nodes.Length)
                .Where(nodeIndex =>
                    nodes[nodeIndex].Capacity >= shard.Size
                    && !exclusions.Contains((shard.Id, nodes[nodeIndex].Id)))
                .ToArray();
            if (eligible.Length < shard.ReplicationFactor)
            {
                throw new InvalidOperationException(
                    $"Shard '{shard.Id}' has too few eligible nodes.");
            }

            var requiredZones = Math.Min(
                shard.ReplicationFactor,
                eligible.Select(nodeIndex => nodes[nodeIndex].Zone)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
            var candidates = GenerateCandidates(
                shard,
                eligible,
                requiredZones,
                currentNodes);
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Shard '{shard.Id}' has no zone-valid placement.");
            }

            var possibleNodes = new bool[nodes.Length];
            var mandatoryNodes = Enumerable.Repeat(true, nodes.Length).ToArray();
            foreach (var candidate in candidates)
            {
                var inCandidate = new bool[nodes.Length];
                foreach (var nodeIndex in candidate.Nodes)
                {
                    possibleNodes[nodeIndex] = true;
                    inCandidate[nodeIndex] = true;
                }

                for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
                {
                    mandatoryNodes[nodeIndex] &= inCandidate[nodeIndex];
                }
            }

            var minimumMovedReplicas = candidates.Min(candidate => candidate.MovedReplicas);
            return new(
                shard,
                candidates,
                possibleNodes,
                mandatoryNodes,
                (BigInteger)shard.Size * minimumMovedReplicas,
                minimumMovedReplicas);
        }

        private List<Candidate> GenerateCandidates(
            ShardSpec shard,
            IReadOnlyList<int> eligible,
            int requiredZones,
            ISet<string> currentNodes)
        {
            var result = new List<Candidate>();
            var chosen = new int[shard.ReplicationFactor];
            var zoneCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            void Generate(int start, int depth)
            {
                var remaining = shard.ReplicationFactor - depth;
                if (remaining == 0)
                {
                    if (zoneCounts.Count != requiredZones)
                    {
                        return;
                    }

                    var nodeIndexes = (int[])chosen.Clone();
                    var movedReplicas = nodeIndexes.Count(
                        nodeIndex => !currentNodes.Contains(nodes[nodeIndex].Id));
                    result.Add(new(
                        nodeIndexes,
                        movedReplicas,
                        (BigInteger)shard.Size * movedReplicas));
                    return;
                }

                if (eligible.Count - start < remaining
                    || zoneCounts.Count > requiredZones
                    || zoneCounts.Count + remaining < requiredZones)
                {
                    return;
                }

                var lastStart = eligible.Count - remaining;
                for (var eligibleIndex = start; eligibleIndex <= lastStart; eligibleIndex++)
                {
                    var nodeIndex = eligible[eligibleIndex];
                    var zone = nodes[nodeIndex].Zone;
                    zoneCounts.TryGetValue(zone, out var oldCount);
                    zoneCounts[zone] = oldCount + 1;
                    if (zoneCounts.Count <= requiredZones)
                    {
                        chosen[depth] = nodeIndex;
                        Generate(eligibleIndex + 1, depth + 1);
                    }

                    if (oldCount == 0)
                    {
                        zoneCounts.Remove(zone);
                    }
                    else
                    {
                        zoneCounts[zone] = oldCount;
                    }
                }
            }

            Generate(0, 0);
            return result;
        }

        private bool FindInitialSolution()
        {
            if (shards.Length == 0)
            {
                ConsiderSolution(0, 0);
                return true;
            }

            var assigned = new bool[shards.Length];

            bool PlaceNext(int depth, BigInteger movedBytes, int movedReplicas)
            {
                if (depth == shards.Length)
                {
                    ConsiderSolution(movedBytes, movedReplicas);
                    return true;
                }

                var selectedShardIndex = -1;
                List<Candidate>? fittingCandidates = null;
                for (var shardIndex = 0; shardIndex < shards.Length; shardIndex++)
                {
                    if (assigned[shardIndex])
                    {
                        continue;
                    }

                    var fitting = shards[shardIndex].Candidates
                        .Where(candidate => FitsCapacity(shards[shardIndex], candidate))
                        .ToList();
                    if (fitting.Count == 0)
                    {
                        return false;
                    }

                    if (fittingCandidates is null
                        || fitting.Count < fittingCandidates.Count
                        || (fitting.Count == fittingCandidates.Count
                            && CompareInitialShardPriority(
                                shardIndex,
                                selectedShardIndex) < 0))
                    {
                        selectedShardIndex = shardIndex;
                        fittingCandidates = fitting;
                    }
                }

                if (selectedShardIndex < 0 || fittingCandidates is null)
                {
                    return false;
                }

                var info = shards[selectedShardIndex];
                var scored = fittingCandidates
                    .Select(candidate => new ScoredCandidate(
                        candidate,
                        ProjectedMaximum(info, candidate)))
                    .ToList();
                scored.Sort((left, right) =>
                {
                    var comparison = left.Maximum.CompareTo(right.Maximum);
                    if (comparison != 0)
                    {
                        return comparison;
                    }

                    comparison = left.Candidate.MovedBytes.CompareTo(
                        right.Candidate.MovedBytes);
                    return comparison != 0
                        ? comparison
                        : CompareCandidates(left.Candidate, right.Candidate);
                });

                assigned[selectedShardIndex] = true;
                foreach (var item in scored)
                {
                    selected[selectedShardIndex] = item.Candidate;
                    Apply(info, item.Candidate);
                    var found = PlaceNext(
                        depth + 1,
                        movedBytes + item.Candidate.MovedBytes,
                        checked(movedReplicas + item.Candidate.MovedReplicas));
                    Undo(info, item.Candidate);
                    selected[selectedShardIndex] = null;
                    if (found)
                    {
                        assigned[selectedShardIndex] = false;
                        return true;
                    }
                }

                assigned[selectedShardIndex] = false;
                return false;
            }

            return PlaceNext(0, 0, 0);
        }

        private int CompareInitialShardPriority(int leftIndex, int rightIndex)
        {
            if (rightIndex < 0)
            {
                return -1;
            }

            var comparison = shards[rightIndex].Spec.Size.CompareTo(
                shards[leftIndex].Spec.Size);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(
                    shards[leftIndex].Spec.Id,
                    shards[rightIndex].Spec.Id);
        }

        private Rational ProjectedMaximum(ShardInfo info, Candidate candidate)
        {
            var maximum = CurrentMaximum();
            foreach (var nodeIndex in candidate.Nodes)
            {
                maximum = Rational.Max(
                    maximum,
                    new Rational(
                        (BigInteger)loads[nodeIndex] + info.Spec.Size,
                        nodes[nodeIndex].Capacity));
            }

            return maximum;
        }

        private void Search(int depth, BigInteger movedBytes, int movedReplicas)
        {
            if (!CanStillBeatBest(depth, movedBytes, movedReplicas))
            {
                return;
            }

            var state = new LoadKey(loads);
            var movement = new MovementCost(movedBytes, movedReplicas);
            if (seenStates[depth].TryGetValue(state, out var seen)
                && seen.CompareTo(movement) <= 0)
            {
                return;
            }

            seenStates[depth][state] = movement;
            if (depth == shards.Length)
            {
                ConsiderSolution(movedBytes, movedReplicas);
                return;
            }

            var info = shards[depth];
            foreach (var candidate in info.Candidates)
            {
                if (!FitsBestLimit(info, candidate))
                {
                    continue;
                }

                selected[depth] = candidate;
                Apply(info, candidate);
                Search(
                    depth + 1,
                    movedBytes + candidate.MovedBytes,
                    checked(movedReplicas + candidate.MovedReplicas));
                Undo(info, candidate);
                selected[depth] = null;
            }
        }

        private bool CanStillBeatBest(
            int depth,
            BigInteger movedBytes,
            int movedReplicas)
        {
            if (bestObjective is not { } best || bestLoadLimits is null)
            {
                return true;
            }

            BigInteger residualCapacity = 0;
            var lowerMaximum = totalCapacity == 0
                ? Rational.Zero
                : new Rational(totalReplicaBytes, totalCapacity);
            var upperMinimum = nodes.Length == 0
                ? Rational.Zero
                : new Rational(BigInteger.One, BigInteger.One);

            for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
            {
                if (loads[nodeIndex] > bestLoadLimits[nodeIndex])
                {
                    return false;
                }

                residualCapacity += bestLoadLimits[nodeIndex] - loads[nodeIndex];
                var mandatoryLoad =
                    (BigInteger)loads[nodeIndex]
                    + suffixMandatoryLoads[depth, nodeIndex];
                if (mandatoryLoad > bestLoadLimits[nodeIndex])
                {
                    return false;
                }

                lowerMaximum = Rational.Max(
                    lowerMaximum,
                    new Rational(mandatoryLoad, nodes[nodeIndex].Capacity));

                var possibleLoad = BigInteger.Min(
                    nodes[nodeIndex].Capacity,
                    (BigInteger)loads[nodeIndex]
                    + suffixPossibleLoads[depth, nodeIndex]);
                upperMinimum = Rational.Min(
                    upperMinimum,
                    new Rational(possibleLoad, nodes[nodeIndex].Capacity));
            }

            if (residualCapacity < suffixReplicaBytes[depth])
            {
                return false;
            }

            var maximumComparison = lowerMaximum.CompareTo(best.Maximum);
            if (maximumComparison > 0)
            {
                return false;
            }

            if (maximumComparison == 0)
            {
                var lowerSpread = lowerMaximum - upperMinimum;
                if (lowerSpread.Numerator.Sign < 0)
                {
                    lowerSpread = Rational.Zero;
                }

                var spreadComparison = lowerSpread.CompareTo(best.Spread);
                if (spreadComparison > 0)
                {
                    return false;
                }

                if (spreadComparison == 0)
                {
                    var minimumMovedBytes =
                        movedBytes + suffixMinimumMovedBytes[depth];
                    if (minimumMovedBytes > best.MovedBytes)
                    {
                        return false;
                    }

                    if (minimumMovedBytes == best.MovedBytes)
                    {
                        var minimumMovedReplicas = checked(
                            movedReplicas + suffixMinimumMovedReplicas[depth]);
                        if (minimumMovedReplicas > best.MovedReplicas)
                        {
                            return false;
                        }

                        if (minimumMovedReplicas == best.MovedReplicas
                            && ComparePrefixWithBest(depth) > 0)
                        {
                            return false;
                        }
                    }
                }
            }

            for (var shardIndex = depth; shardIndex < shards.Length; shardIndex++)
            {
                var info = shards[shardIndex];
                if (!info.Candidates.Any(candidate => FitsBestLimit(info, candidate)))
                {
                    return false;
                }
            }

            return true;
        }

        private void ConsiderSolution(BigInteger movedBytes, int movedReplicas)
        {
            var maximum = Rational.Zero;
            var minimum = Rational.Zero;
            if (nodes.Length > 0)
            {
                minimum = new Rational(loads[0], nodes[0].Capacity);
                for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
                {
                    var utilization = new Rational(
                        loads[nodeIndex],
                        nodes[nodeIndex].Capacity);
                    maximum = Rational.Max(maximum, utilization);
                    minimum = Rational.Min(minimum, utilization);
                }
            }

            var objective = new Objective(
                maximum,
                maximum - minimum,
                movedBytes,
                movedReplicas);
            var objectiveComparison = bestObjective is { } oldObjective
                ? objective.CompareTo(oldObjective)
                : -1;
            if (objectiveComparison > 0
                || (objectiveComparison == 0 && CompareSelectionWithBest() >= 0))
            {
                return;
            }

            var maximumImproved = bestObjective is null
                || objective.Maximum.CompareTo(bestObjective.Value.Maximum) < 0;
            bestObjective = objective;
            bestSelection = (Candidate?[])selected.Clone();
            if (maximumImproved || bestLoadLimits is null)
            {
                bestLoadLimits = nodes
                    .Select(node => checked((long)objective.Maximum.FloorMultiply(
                        node.Capacity)))
                    .ToArray();
            }
        }

        private bool FitsCapacity(ShardInfo info, Candidate candidate)
        {
            foreach (var nodeIndex in candidate.Nodes)
            {
                if (loads[nodeIndex] > nodes[nodeIndex].Capacity - info.Spec.Size)
                {
                    return false;
                }
            }

            return true;
        }

        private bool FitsBestLimit(ShardInfo info, Candidate candidate)
        {
            if (bestLoadLimits is null)
            {
                return FitsCapacity(info, candidate);
            }

            foreach (var nodeIndex in candidate.Nodes)
            {
                if (loads[nodeIndex] > bestLoadLimits[nodeIndex] - info.Spec.Size)
                {
                    return false;
                }
            }

            return true;
        }

        private void Apply(ShardInfo info, Candidate candidate)
        {
            foreach (var nodeIndex in candidate.Nodes)
            {
                loads[nodeIndex] = checked(loads[nodeIndex] + info.Spec.Size);
            }
        }

        private void Undo(ShardInfo info, Candidate candidate)
        {
            foreach (var nodeIndex in candidate.Nodes)
            {
                loads[nodeIndex] -= info.Spec.Size;
            }
        }

        private Rational CurrentMaximum()
        {
            var maximum = Rational.Zero;
            for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
            {
                maximum = Rational.Max(
                    maximum,
                    new Rational(loads[nodeIndex], nodes[nodeIndex].Capacity));
            }

            return maximum;
        }

        private int ComparePrefixWithBest(int depth)
        {
            if (bestSelection is null)
            {
                return -1;
            }

            for (var shardIndex = 0; shardIndex < depth; shardIndex++)
            {
                var comparison = CompareCandidates(
                    selected[shardIndex]
                        ?? throw new InvalidOperationException("Incomplete search prefix."),
                    bestSelection[shardIndex]
                        ?? throw new InvalidOperationException("Incomplete best result."));
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return 0;
        }

        private int CompareSelectionWithBest()
        {
            if (bestSelection is null)
            {
                return -1;
            }

            for (var shardIndex = 0; shardIndex < shards.Length; shardIndex++)
            {
                var comparison = CompareCandidates(
                    selected[shardIndex]
                        ?? throw new InvalidOperationException("Incomplete solver result."),
                    bestSelection[shardIndex]
                        ?? throw new InvalidOperationException("Incomplete best result."));
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return 0;
        }

        private static int CompareCandidates(Candidate left, Candidate right)
        {
            for (var index = 0; index < left.Nodes.Length; index++)
            {
                var comparison = left.Nodes[index].CompareTo(right.Nodes[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return 0;
        }

        private sealed record ShardInfo(
            ShardSpec Spec,
            List<Candidate> Candidates,
            bool[] PossibleNodes,
            bool[] MandatoryNodes,
            BigInteger MinimumMovedBytes,
            int MinimumMovedReplicas);

        private sealed record Candidate(
            int[] Nodes,
            int MovedReplicas,
            BigInteger MovedBytes);

        private readonly record struct ScoredCandidate(
            Candidate Candidate,
            Rational Maximum);

        private readonly record struct MovementCost(BigInteger Bytes, int Replicas)
            : IComparable<MovementCost>
        {
            public int CompareTo(MovementCost other)
            {
                var comparison = Bytes.CompareTo(other.Bytes);
                return comparison != 0
                    ? comparison
                    : Replicas.CompareTo(other.Replicas);
            }
        }

        private readonly record struct Objective(
            Rational Maximum,
            Rational Spread,
            BigInteger MovedBytes,
            int MovedReplicas)
            : IComparable<Objective>
        {
            public int CompareTo(Objective other)
            {
                var comparison = Maximum.CompareTo(other.Maximum);
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = Spread.CompareTo(other.Spread);
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = MovedBytes.CompareTo(other.MovedBytes);
                return comparison != 0
                    ? comparison
                    : MovedReplicas.CompareTo(other.MovedReplicas);
            }
        }

        private sealed class LoadKey : IEquatable<LoadKey>
        {
            private readonly long[] values;
            private readonly int hashCode;

            public LoadKey(long[] loads)
            {
                // The omitted final load is determined by depth and the other loads.
                values = loads.Length <= 1
                    ? []
                    : loads[..^1];
                var hash = 17;
                foreach (var value in values)
                {
                    hash = unchecked((hash * 31) + value.GetHashCode());
                }

                hashCode = hash;
            }

            public bool Equals(LoadKey? other) =>
                other is not null && values.AsSpan().SequenceEqual(other.values);

            public override bool Equals(object? obj) =>
                obj is LoadKey other && Equals(other);

            public override int GetHashCode() => hashCode;
        }

        private readonly struct Rational : IComparable<Rational>
        {
            public static Rational Zero { get; } =
                new(BigInteger.Zero, BigInteger.One);

            public Rational(BigInteger numerator, BigInteger denominator)
            {
                if (denominator.Sign <= 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(denominator),
                        "A rational denominator must be positive.");
                }

                if (numerator.IsZero)
                {
                    Numerator = BigInteger.Zero;
                    Denominator = BigInteger.One;
                    return;
                }

                var divisor = BigInteger.GreatestCommonDivisor(
                    BigInteger.Abs(numerator),
                    denominator);
                Numerator = numerator / divisor;
                Denominator = denominator / divisor;
            }

            public BigInteger Numerator { get; }

            private BigInteger Denominator { get; }

            public int CompareTo(Rational other) =>
                (Numerator * other.Denominator).CompareTo(
                    other.Numerator * Denominator);

            public BigInteger FloorMultiply(long value) =>
                (Numerator * value) / Denominator;

            public static Rational Max(Rational left, Rational right) =>
                left.CompareTo(right) >= 0 ? left : right;

            public static Rational Min(Rational left, Rational right) =>
                left.CompareTo(right) <= 0 ? left : right;

            public static Rational operator -(Rational left, Rational right) =>
                new(
                    (left.Numerator * right.Denominator)
                    - (right.Numerator * left.Denominator),
                    left.Denominator * right.Denominator);
        }
    }
}
