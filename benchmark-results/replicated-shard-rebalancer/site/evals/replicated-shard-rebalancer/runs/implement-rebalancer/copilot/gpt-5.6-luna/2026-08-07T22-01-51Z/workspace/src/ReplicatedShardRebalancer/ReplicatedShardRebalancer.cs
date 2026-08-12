using System.Numerics;

namespace ReplicatedShardRebalancer;

public sealed class ReplicatedShardRebalancer
{
    public RebalanceResult Rebalance(RebalanceProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var nodes = (problem.Nodes ?? [])
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .ToArray();
        var shards = (problem.Shards ?? [])
            .OrderBy(shard => shard.Id, StringComparer.Ordinal)
            .ToArray();
        var exclusions = (problem.Exclusions ?? [])
            .Select(exclusion => (exclusion.ShardId, exclusion.NodeId))
            .ToHashSet();
        var currentPlacements = (problem.CurrentPlacements ?? [])
            .GroupBy(placement => placement.ShardId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().NodeIds is null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : group.First().NodeIds.ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        var shardData = shards
            .Select(shard => CreateShardData(
                shard,
                nodes,
                exclusions,
                currentPlacements.GetValueOrDefault(
                    shard.Id,
                    new HashSet<string>(StringComparer.Ordinal))))
            .ToArray();

        if (shardData.Any(data => data.Candidates.Count == 0))
        {
            throw new InvalidOperationException(
                "At least one shard has no placement satisfying its hard constraints.");
        }

        var solver = new Solver(nodes, shardData);
        var choices = solver.Solve();
        var targetPlacements = new List<ShardPlacement>(shardData.Length);
        for (var shardIndex = 0; shardIndex < shardData.Length; shardIndex++)
        {
            var nodeIds = choices[shardIndex].NodeIndices
                .Select(nodeIndex => nodes[nodeIndex].Id)
                .ToList();
            targetPlacements.Add(new(shardData[shardIndex].Id, nodeIds));
        }

        return new(targetPlacements);
    }

    private static ShardData CreateShardData(
        ShardSpec shard,
        IReadOnlyList<NodeSpec> nodes,
        ISet<(string ShardId, string NodeId)> exclusions,
        ISet<string> currentNodes)
    {
        var eligible = Enumerable.Range(0, nodes.Count)
            .Where(index =>
                nodes[index].Capacity >= shard.Size
                && !exclusions.Contains((shard.Id, nodes[index].Id)))
            .ToArray();
        var requiredZones = Math.Min(
            shard.ReplicationFactor,
            eligible
                .Select(index => nodes[index].Zone)
                .Distinct(StringComparer.Ordinal)
                .Count());
        var candidates = new List<Candidate>();
        var selected = new int[shard.ReplicationFactor];
        var usedZones = new HashSet<string>(StringComparer.Ordinal);

        void Generate(int start, int depth, int zoneCount)
        {
            if (depth == selected.Length)
            {
                if (zoneCount != requiredZones)
                {
                    return;
                }

                var nodeIndices = selected.ToArray();
                var movedReplicas = nodeIndices.Count(
                    index => !currentNodes.Contains(nodes[index].Id));
                candidates.Add(new(
                    nodeIndices,
                    checked(shard.Size * movedReplicas),
                    movedReplicas));
                return;
            }

            var nodesStillNeeded = selected.Length - depth;
            for (var eligibleIndex = start;
                 eligibleIndex <= eligible.Length - nodesStillNeeded;
                 eligibleIndex++)
            {
                var nodeIndex = eligible[eligibleIndex];
                var zone = nodes[nodeIndex].Zone;
                var addedZone = usedZones.Add(zone);
                var nextZoneCount = zoneCount + (addedZone ? 1 : 0);
                if (nextZoneCount <= requiredZones)
                {
                    selected[depth] = nodeIndex;
                    Generate(
                        eligibleIndex + 1,
                        depth + 1,
                        nextZoneCount);
                }

                if (addedZone)
                {
                    usedZones.Remove(zone);
                }
            }
        }

        Generate(0, 0, 0);
        candidates.Sort((left, right) =>
            CompareNodeArrays(left.NodeIndices, right.NodeIndices));
        return new(
            shard.Id,
            shard.Size,
            candidates,
            candidates.Count == 0
                ? 0
                : candidates.Min(candidate => candidate.MovedBytes),
            candidates.Count == 0
                ? 0
                : candidates.Min(candidate => candidate.MovedReplicas));
    }

    private static int CompareNodeArrays(
        IReadOnlyList<int> left,
        IReadOnlyList<int> right)
    {
        for (var index = 0; index < Math.Min(left.Count, right.Count); index++)
        {
            var comparison = left[index].CompareTo(right[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Count.CompareTo(right.Count);
    }

    private sealed class Solver
    {
        private readonly IReadOnlyList<NodeSpec> _nodes;
        private readonly IReadOnlyList<ShardData> _shards;
        private readonly int[] _searchOrder;
        private readonly long[] _remainingReplicaBytes;
        private readonly long[] _minimumMovedBytes;
        private readonly int[] _minimumMovedReplicas;
        private readonly Candidate[] _currentChoices;
        private Solution? _best;
        private OptimizationStage _stage;
        private Fraction? _targetMaximum;
        private Fraction? _targetSpread;
        private long _targetMovedBytes;
        private int _targetMovedReplicas;

        public Solver(
            IReadOnlyList<NodeSpec> nodes,
            IReadOnlyList<ShardData> shards)
        {
            _nodes = nodes;
            _shards = shards;
            _searchOrder = Enumerable.Range(0, shards.Count)
                .OrderBy(index => shards[index].Candidates.Count)
                .ThenByDescending(index => shards[index].Size)
                .ThenBy(index => shards[index].Id, StringComparer.Ordinal)
                .ToArray();
            _remainingReplicaBytes = new long[shards.Count + 1];
            _minimumMovedBytes = new long[shards.Count + 1];
            _minimumMovedReplicas = new int[shards.Count + 1];
            for (var depth = shards.Count - 1; depth >= 0; depth--)
            {
                var shard = shards[_searchOrder[depth]];
                _remainingReplicaBytes[depth] = checked(
                    _remainingReplicaBytes[depth + 1]
                    + checked(shard.Size * shard.ReplicationFactor));
                _minimumMovedBytes[depth] = checked(
                    _minimumMovedBytes[depth + 1] + shard.MinimumMovedBytes);
                _minimumMovedReplicas[depth] =
                    _minimumMovedReplicas[depth + 1]
                    + shard.MinimumMovedReplicas;
            }

            _currentChoices = new Candidate[shards.Count];
        }

        public Candidate[] Solve()
        {
            var initialLoads = new long[_nodes.Count];

            _stage = OptimizationStage.MaximumUtilization;
            _best = null;
            Search(0, initialLoads, 0, 0);
            var maximumSolution = _best
                ?? throw new InvalidOperationException(
                    "No feasible target placement was found.");

            _targetMaximum = maximumSolution.Metrics.Maximum;

            _stage = OptimizationStage.UtilizationSpread;
            _best = maximumSolution;
            Search(
                0,
                new long[_nodes.Count],
                0,
                0);
            var spreadSolution = _best
                ?? throw new InvalidOperationException(
                    "No target placement matched the maximum utilization.");
            _targetSpread = spreadSolution.Metrics.Spread;

            _stage = OptimizationStage.MovedBytes;
            _targetMovedBytes = spreadSolution.Metrics.MovedBytes;
            _best = spreadSolution;
            Search(
                0,
                new long[_nodes.Count],
                0,
                0);
            var movedBytesSolution = _best
                ?? throw new InvalidOperationException(
                    "No target placement matched the utilization objectives.");
            _targetMovedBytes = movedBytesSolution.Metrics.MovedBytes;

            _stage = OptimizationStage.MovedReplicas;
            _targetMovedReplicas = movedBytesSolution.Metrics.MovedReplicas;
            _best = movedBytesSolution;
            Search(
                0,
                new long[_nodes.Count],
                0,
                0);
            var movedReplicasSolution = _best
                ?? throw new InvalidOperationException(
                    "No target placement matched the movement objectives.");
            _targetMovedReplicas = movedReplicasSolution.Metrics.MovedReplicas;

            _stage = OptimizationStage.LexicographicPlacement;
            _best = movedReplicasSolution;
            Search(
                0,
                new long[_nodes.Count],
                0,
                0);

            return _best!.Choices.ToArray();
        }

        private void Search(
            int depth,
            long[] loads,
            long movedBytes,
            int movedReplicas)
        {
            if (!CanPotentiallyImprove(depth, loads, movedBytes, movedReplicas))
            {
                return;
            }

            if (depth == _searchOrder.Length)
            {
                ConsiderSolution(
                    loads,
                    movedBytes,
                    movedReplicas);
                return;
            }

            var shardIndex = _searchOrder[depth];
            var shard = _shards[shardIndex];
            foreach (var candidateIndex in OrderedCandidates(shardIndex, loads))
            {
                var candidate = shard.Candidates[candidateIndex];
                if (!CanApply(shard, candidate, loads))
                {
                    continue;
                }

                foreach (var nodeIndex in candidate.NodeIndices)
                {
                    loads[nodeIndex] = checked(
                        loads[nodeIndex] + shard.Size);
                }
                _currentChoices[shardIndex] = candidate;

                Search(
                    depth + 1,
                    loads,
                    checked(movedBytes + candidate.MovedBytes),
                    movedReplicas + candidate.MovedReplicas);

                foreach (var nodeIndex in candidate.NodeIndices)
                {
                    loads[nodeIndex] -= shard.Size;
                }
            }
        }

        private bool CanPotentiallyImprove(
            int depth,
            IReadOnlyList<long> loads,
            long movedBytes,
            int movedReplicas)
        {
            if (_best is null)
            {
                return true;
            }

            var maximumLimit = _stage == OptimizationStage.MaximumUtilization
                ? _best.Metrics.Maximum
                : _targetMaximum!.Value;
            if (CurrentMaximum(loads).CompareTo(maximumLimit) > 0
                || !CanCompleteWithinMaximum(
                    loads,
                    _remainingReplicaBytes[depth],
                    maximumLimit))
            {
                return false;
            }

            if (_stage == OptimizationStage.MaximumUtilization)
            {
                return true;
            }

            var lowerSpread = LowerBoundSpread(
                loads,
                _remainingReplicaBytes[depth],
                maximumLimit);
            var spreadLimit = _stage == OptimizationStage.UtilizationSpread
                ? _best.Metrics.Spread
                : _targetSpread!.Value;
            var spreadComparison = lowerSpread.CompareTo(spreadLimit);
            if (spreadComparison > 0)
            {
                return false;
            }

            if (_stage < OptimizationStage.MovedBytes
                || spreadComparison != 0)
            {
                return true;
            }

            var minimumMovedBytes = checked(
                movedBytes + _minimumMovedBytes[depth]);
            if (minimumMovedBytes > (_stage == OptimizationStage.MovedBytes
                    ? _best.Metrics.MovedBytes
                    : _targetMovedBytes))
            {
                return false;
            }

            if (_stage < OptimizationStage.MovedReplicas)
            {
                return true;
            }

            var minimumMovedReplicas = movedReplicas
                + _minimumMovedReplicas[depth];
            if (minimumMovedBytes != _targetMovedBytes
                || minimumMovedReplicas > (_stage == OptimizationStage.MovedReplicas
                    ? _best.Metrics.MovedReplicas
                    : _targetMovedReplicas))
            {
                return minimumMovedBytes <= _targetMovedBytes;
            }

            return true;
        }

        private void ConsiderSolution(
            IReadOnlyList<long> loads,
            long movedBytes,
            int movedReplicas)
        {
            var metrics = CalculateMetrics(loads, movedBytes, movedReplicas);
            if (!MatchesFixedObjectives(metrics))
            {
                return;
            }

            if (_best is null || IsBetter(metrics, _currentChoices, _best))
            {
                _best = new(
                    _currentChoices.ToArray(),
                    loads.ToArray(),
                    metrics);
            }
        }

        private bool MatchesFixedObjectives(Metrics metrics)
        {
            if (_stage >= OptimizationStage.UtilizationSpread
                && metrics.Maximum.CompareTo(_targetMaximum!.Value) > 0)
            {
                return false;
            }
            if (_stage >= OptimizationStage.MovedBytes
                && metrics.Spread.CompareTo(_targetSpread!.Value) > 0)
            {
                return false;
            }
            if (_stage >= OptimizationStage.MovedReplicas
                && metrics.MovedBytes > _targetMovedBytes)
            {
                return false;
            }
            if (_stage >= OptimizationStage.LexicographicPlacement
                && metrics.MovedReplicas > _targetMovedReplicas)
            {
                return false;
            }

            return true;
        }

        private bool IsBetter(
            Metrics metrics,
            IReadOnlyList<Candidate> choices,
            Solution best)
        {
            switch (_stage)
            {
                case OptimizationStage.MaximumUtilization:
                    return metrics.Maximum.CompareTo(best.Metrics.Maximum) < 0;
                case OptimizationStage.UtilizationSpread:
                    return metrics.Spread.CompareTo(best.Metrics.Spread) < 0;
                case OptimizationStage.MovedBytes:
                    return metrics.MovedBytes < best.Metrics.MovedBytes;
                case OptimizationStage.MovedReplicas:
                    return metrics.MovedReplicas < best.Metrics.MovedReplicas;
                case OptimizationStage.LexicographicPlacement:
                    return CompareChoices(choices, best.Choices) < 0;
                default:
                    throw new InvalidOperationException(
                        $"Unknown optimization stage '{_stage}'.");
            }
        }

        private int[] OrderedCandidates(
            int shardIndex,
            IReadOnlyList<long> loads)
        {
            var shard = _shards[shardIndex];
            var order = Enumerable.Range(0, shard.Candidates.Count).ToArray();
            Array.Sort(order, (left, right) =>
            {
                var comparison = CompareProjectedMaximum(
                    shard,
                    shard.Candidates[left],
                    shard.Candidates[right],
                    loads);
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = shard.Candidates[left].MovedBytes.CompareTo(
                    shard.Candidates[right].MovedBytes);
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = shard.Candidates[left].MovedReplicas.CompareTo(
                    shard.Candidates[right].MovedReplicas);
                return comparison != 0
                    ? comparison
                    : CompareNodeArrays(
                        shard.Candidates[left].NodeIndices,
                        shard.Candidates[right].NodeIndices);
            });
            return order;
        }

        private int CompareProjectedMaximum(
            ShardData shard,
            Candidate left,
            Candidate right,
            IReadOnlyList<long> loads)
        {
            Fraction? leftMaximum = null;
            Fraction? rightMaximum = null;
            for (var nodeIndex = 0; nodeIndex < _nodes.Count; nodeIndex++)
            {
                var leftLoad = loads[nodeIndex]
                    + (left.NodeIndices.Contains(nodeIndex) ? shard.Size : 0);
                var rightLoad = loads[nodeIndex]
                    + (right.NodeIndices.Contains(nodeIndex) ? shard.Size : 0);
                var leftRatio = new Fraction(
                    leftLoad,
                    _nodes[nodeIndex].Capacity);
                var rightRatio = new Fraction(
                    rightLoad,
                    _nodes[nodeIndex].Capacity);
                if (leftMaximum is null
                    || leftRatio.CompareTo(leftMaximum.Value) > 0)
                {
                    leftMaximum = leftRatio;
                }
                if (rightMaximum is null
                    || rightRatio.CompareTo(rightMaximum.Value) > 0)
                {
                    rightMaximum = rightRatio;
                }
            }

            return leftMaximum!.Value.CompareTo(rightMaximum!.Value);
        }

        private bool CanApply(
            ShardData shard,
            Candidate candidate,
            IReadOnlyList<long> loads)
        {
            foreach (var nodeIndex in candidate.NodeIndices)
            {
                if (loads[nodeIndex] > _nodes[nodeIndex].Capacity - shard.Size)
                {
                    return false;
                }
            }

            return true;
        }

        private bool CanCompleteWithinMaximum(
            IReadOnlyList<long> loads,
            long remainingBytes,
            Fraction maximum)
        {
            BigInteger available = 0;
            for (var nodeIndex = 0; nodeIndex < _nodes.Count; nodeIndex++)
            {
                var load = loads[nodeIndex];
                var node = _nodes[nodeIndex];
                var numerator = maximum.Numerator * node.Capacity
                    - (BigInteger)load * maximum.Denominator;
                if (numerator <= 0)
                {
                    continue;
                }

                var ratioAllowance = numerator / maximum.Denominator;
                var capacityAllowance = node.Capacity - load;
                available += BigInteger.Min(
                    ratioAllowance,
                    capacityAllowance);
                if (available >= remainingBytes)
                {
                    return true;
                }
            }

            return available >= remainingBytes;
        }

        private Fraction LowerBoundSpread(
            IReadOnlyList<long> loads,
            long remainingBytes,
            Fraction maximum)
        {
            Fraction? minimumPossible = null;
            for (var nodeIndex = 0; nodeIndex < _nodes.Count; nodeIndex++)
            {
                var possibleLoad = (BigInteger)loads[nodeIndex] + remainingBytes;
                var possibleMinimum = new Fraction(
                    possibleLoad,
                    _nodes[nodeIndex].Capacity);
                if (minimumPossible is null
                    || possibleMinimum.CompareTo(minimumPossible.Value) < 0)
                {
                    minimumPossible = possibleMinimum;
                }
            }

            if (minimumPossible is null)
            {
                return Fraction.Zero;
            }

            return Fraction.Subtract(
                maximum,
                minimumPossible!.Value);
        }

        private Fraction CurrentMaximum(IReadOnlyList<long> loads)
        {
            Fraction? maximum = null;
            for (var nodeIndex = 0; nodeIndex < _nodes.Count; nodeIndex++)
            {
                var ratio = new Fraction(
                    loads[nodeIndex],
                    _nodes[nodeIndex].Capacity);
                if (maximum is null || ratio.CompareTo(maximum.Value) > 0)
                {
                    maximum = ratio;
                }
            }

            return maximum ?? Fraction.Zero;
        }

        private Metrics CalculateMetrics(
            IReadOnlyList<long> loads,
            long movedBytes,
            int movedReplicas)
        {
            Fraction? maximum = null;
            Fraction? minimum = null;
            for (var nodeIndex = 0; nodeIndex < _nodes.Count; nodeIndex++)
            {
                var ratio = new Fraction(
                    loads[nodeIndex],
                    _nodes[nodeIndex].Capacity);
                if (maximum is null || ratio.CompareTo(maximum.Value) > 0)
                {
                    maximum = ratio;
                }
                if (minimum is null || ratio.CompareTo(minimum.Value) < 0)
                {
                    minimum = ratio;
                }
            }

            return new(
                maximum ?? Fraction.Zero,
                Fraction.Subtract(
                    maximum ?? Fraction.Zero,
                    minimum ?? Fraction.Zero),
                movedBytes,
                movedReplicas);
        }

        private static int CompareChoices(
            IReadOnlyList<Candidate> left,
            IReadOnlyList<Candidate> right)
        {
            for (var shardIndex = 0; shardIndex < left.Count; shardIndex++)
            {
                var comparison = CompareNodeArrays(
                    left[shardIndex].NodeIndices,
                    right[shardIndex].NodeIndices);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return 0;
        }
    }

    private sealed record ShardData(
        string Id,
        long Size,
        List<Candidate> Candidates,
        long MinimumMovedBytes,
        int MinimumMovedReplicas)
    {
        public int ReplicationFactor =>
            Candidates.Count == 0 ? 0 : Candidates[0].NodeIndices.Length;
    }

    private readonly record struct Candidate(
        int[] NodeIndices,
        long MovedBytes,
        int MovedReplicas);

    private sealed record Solution(
        Candidate[] Choices,
        long[] Loads,
        Metrics Metrics);

    private readonly record struct Metrics(
        Fraction Maximum,
        Fraction Spread,
        long MovedBytes,
        int MovedReplicas);

    private enum OptimizationStage
    {
        MaximumUtilization,
        UtilizationSpread,
        MovedBytes,
        MovedReplicas,
        LexicographicPlacement,
    }

    private readonly record struct Fraction(
        BigInteger Numerator,
        BigInteger Denominator)
    {
        public static Fraction Zero => new(0, 1);

        public Fraction(long numerator, long denominator)
            : this(new BigInteger(numerator), new BigInteger(denominator))
        {
        }

        public int CompareTo(Fraction other)
        {
            return (Numerator * other.Denominator).CompareTo(
                other.Numerator * Denominator);
        }

        public static Fraction Subtract(Fraction left, Fraction right)
        {
            var numerator = left.Numerator * right.Denominator
                - right.Numerator * left.Denominator;
            return numerator <= 0
                ? Zero
                : new(
                    numerator,
                    left.Denominator * right.Denominator);
        }
    }
}
