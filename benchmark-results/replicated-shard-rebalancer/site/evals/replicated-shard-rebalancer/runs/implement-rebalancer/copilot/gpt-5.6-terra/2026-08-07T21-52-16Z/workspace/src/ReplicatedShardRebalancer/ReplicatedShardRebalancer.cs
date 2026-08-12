using System.Numerics;
using System.Text;

namespace ReplicatedShardRebalancer;

public sealed class ReplicatedShardRebalancer
{
    public RebalanceResult Rebalance(RebalanceProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var sourceNodes = problem.Nodes
            ?? throw new ArgumentException("Nodes must be provided.", nameof(problem));
        var sourceShards = problem.Shards
            ?? throw new ArgumentException("Shards must be provided.", nameof(problem));

        ValidateNodes(sourceNodes);
        ValidateShards(sourceShards);

        var nodes = sourceNodes
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .ToArray();
        var shards = sourceShards
            .OrderBy(shard => shard.Id, StringComparer.Ordinal)
            .ToArray();

        if (shards.Length == 0)
        {
            return new([]);
        }

        var nodeIndexes = nodes
            .Select((node, index) => (node.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        var zones = BuildZoneIndexes(nodes);
        var exclusions = BuildExclusions(problem.Exclusions, nodeIndexes, shards);
        var current = BuildCurrentPlacements(problem.CurrentPlacements, shards);

        var canonicalShards = new ShardWork[shards.Length];
        for (var shardIndex = 0; shardIndex < shards.Length; shardIndex++)
        {
            canonicalShards[shardIndex] = BuildShardWork(
                shardIndex,
                shards[shardIndex],
                nodes,
                zones,
                exclusions,
                current);
        }

        var searchOrder = canonicalShards
            .OrderBy(work => work.Options.Length)
            .ThenByDescending(work => work.Shard.Size)
            .ThenByDescending(work => work.Shard.ReplicationFactor)
            .ThenBy(work => work.Shard.Id, StringComparer.Ordinal)
            .ToArray();

        return new Search(nodes, canonicalShards, searchOrder).Solve();
    }

    private static void ValidateNodes(IEnumerable<NodeSpec> nodes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node is null
                || string.IsNullOrWhiteSpace(node.Id)
                || string.IsNullOrWhiteSpace(node.Zone)
                || node.Capacity <= 0)
            {
                throw new ArgumentException("Every node must have an ID, zone, and positive capacity.");
            }

            if (!seen.Add(node.Id))
            {
                throw new ArgumentException($"Node ID '{node.Id}' is duplicated.");
            }
        }
    }

    private static void ValidateShards(IEnumerable<ShardSpec> shards)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shard in shards)
        {
            if (shard is null
                || string.IsNullOrWhiteSpace(shard.Id)
                || shard.Size <= 0
                || shard.ReplicationFactor <= 0)
            {
                throw new ArgumentException(
                    "Every shard must have an ID, positive size, and positive replication factor.");
            }

            if (!seen.Add(shard.Id))
            {
                throw new ArgumentException($"Shard ID '{shard.Id}' is duplicated.");
            }
        }
    }

    private static Dictionary<string, int> BuildZoneIndexes(IReadOnlyList<NodeSpec> nodes)
    {
        var zones = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!zones.ContainsKey(node.Zone))
            {
                zones.Add(node.Zone, zones.Count);
            }
        }

        return zones;
    }

    private static HashSet<(string ShardId, string NodeId)> BuildExclusions(
        List<PlacementExclusion>? source,
        IReadOnlyDictionary<string, int> nodeIndexes,
        IReadOnlyList<ShardSpec> shards)
    {
        var shardIds = new HashSet<string>(
            shards.Select(shard => shard.Id),
            StringComparer.Ordinal);
        var exclusions = new HashSet<(string ShardId, string NodeId)>();

        foreach (var exclusion in source ?? [])
        {
            if (exclusion is null
                || !shardIds.Contains(exclusion.ShardId)
                || !nodeIndexes.ContainsKey(exclusion.NodeId))
            {
                throw new ArgumentException("Every exclusion must reference an existing shard and node.");
            }

            if (!exclusions.Add((exclusion.ShardId, exclusion.NodeId)))
            {
                throw new ArgumentException(
                    $"Exclusion '{exclusion.ShardId}/{exclusion.NodeId}' is duplicated.");
            }
        }

        return exclusions;
    }

    private static Dictionary<string, HashSet<string>> BuildCurrentPlacements(
        List<ShardPlacement>? source,
        IReadOnlyList<ShardSpec> shards)
    {
        var shardIds = new HashSet<string>(
            shards.Select(shard => shard.Id),
            StringComparer.Ordinal);
        var current = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var placement in source ?? [])
        {
            if (placement is null
                || !shardIds.Contains(placement.ShardId)
                || placement.NodeIds is null)
            {
                throw new ArgumentException(
                    "Every current placement must reference an existing shard and include node IDs.");
            }

            if (!current.TryAdd(
                    placement.ShardId,
                    new HashSet<string>(placement.NodeIds, StringComparer.Ordinal)))
            {
                throw new ArgumentException(
                    $"Current placement for shard '{placement.ShardId}' is duplicated.");
            }
        }

        return current;
    }

    private static ShardWork BuildShardWork(
        int canonicalIndex,
        ShardSpec shard,
        IReadOnlyList<NodeSpec> nodes,
        IReadOnlyDictionary<string, int> zones,
        ISet<(string ShardId, string NodeId)> exclusions,
        IReadOnlyDictionary<string, HashSet<string>> current)
    {
        var eligible = Enumerable.Range(0, nodes.Count)
            .Where(nodeIndex =>
                nodes[nodeIndex].Capacity >= shard.Size
                && !exclusions.Contains((shard.Id, nodes[nodeIndex].Id)))
            .ToArray();
        var requiredZones = Math.Min(
            shard.ReplicationFactor,
            eligible
                .Select(nodeIndex => zones[nodes[nodeIndex].Zone])
                .Distinct()
                .Count());

        if (eligible.Length < shard.ReplicationFactor)
        {
            throw new InvalidOperationException(
                $"Shard '{shard.Id}' has too few eligible nodes.");
        }

        var currentNodes = current.GetValueOrDefault(shard.Id)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var options = GenerateOptions(
            shard,
            nodes,
            zones,
            eligible,
            requiredZones,
            currentNodes);

        if (options.Count == 0)
        {
            throw new InvalidOperationException(
                $"Shard '{shard.Id}' has no placement with the required zone diversity.");
        }

        var canUseNode = new bool[nodes.Count];
        foreach (var option in options)
        {
            foreach (var nodeIndex in option.NodeIndexes)
            {
                canUseNode[nodeIndex] = true;
            }
        }

        return new(
            canonicalIndex,
            shard,
            options.ToArray(),
            canUseNode);
    }

    private static List<PlacementOption> GenerateOptions(
        ShardSpec shard,
        IReadOnlyList<NodeSpec> nodes,
        IReadOnlyDictionary<string, int> zones,
        IReadOnlyList<int> eligible,
        int requiredZones,
        ISet<string> currentNodes)
    {
        var options = new List<PlacementOption>();
        var selected = new int[shard.ReplicationFactor];
        var zoneCounts = new int[zones.Count];

        void Visit(int nextEligibleIndex, int selectedCount, int usedZones)
        {
            if (selectedCount == selected.Length)
            {
                if (usedZones != requiredZones)
                {
                    return;
                }

                var nodeIndexes = selected.ToArray();
                var movedReplicas = nodeIndexes.Count(
                    nodeIndex => !currentNodes.Contains(nodes[nodeIndex].Id));
                options.Add(new(
                    nodeIndexes,
                    checked(shard.Size * (long)movedReplicas),
                    movedReplicas));
                return;
            }

            var remainingToSelect = selected.Length - selectedCount;
            for (var eligibleIndex = nextEligibleIndex;
                 eligibleIndex <= eligible.Count - remainingToSelect;
                 eligibleIndex++)
            {
                var nodeIndex = eligible[eligibleIndex];
                var zoneIndex = zones[nodes[nodeIndex].Zone];
                var addsZone = zoneCounts[zoneIndex] == 0;
                if (addsZone && usedZones == requiredZones)
                {
                    continue;
                }

                selected[selectedCount] = nodeIndex;
                zoneCounts[zoneIndex]++;
                Visit(
                    eligibleIndex + 1,
                    selectedCount + 1,
                    addsZone ? usedZones + 1 : usedZones);
                zoneCounts[zoneIndex]--;
            }
        }

        Visit(0, 0, 0);
        return options;
    }

    private sealed class Search(
        IReadOnlyList<NodeSpec> nodes,
        IReadOnlyList<ShardWork> canonicalShards,
        IReadOnlyList<ShardWork> searchOrder)
    {
        private readonly IReadOnlyList<NodeSpec> _nodes = nodes;
        private readonly IReadOnlyList<ShardWork> _canonicalShards = canonicalShards;
        private readonly IReadOnlyList<ShardWork> _searchOrder = searchOrder;
        private readonly long[] _loads = new long[nodes.Count];
        private readonly PlacementOption?[] _selected =
            new PlacementOption?[canonicalShards.Count];
        private readonly Dictionary<string, MovementCost>[] _seen =
            Enumerable.Range(0, searchOrder.Count + 1)
                .Select(_ => new Dictionary<string, MovementCost>(StringComparer.Ordinal))
                .ToArray();
        private readonly long[] _minimumRemainingMovedBytes =
            new long[searchOrder.Count + 1];
        private readonly int[] _minimumRemainingMovedReplicas =
            new int[searchOrder.Count + 1];
        private readonly long[][] _maximumRemainingNodeAdditions =
            CreateMaximumRemainingNodeAdditions(nodes, searchOrder);
        private readonly Fraction _averageUtilization = CreateAverageUtilization(
            nodes,
            canonicalShards);

        private Objective? _bestObjective;
        private PlacementOption[]? _bestSelections;

        public RebalanceResult Solve()
        {
            for (var depth = _searchOrder.Count - 1; depth >= 0; depth--)
            {
                var work = _searchOrder[depth];
                _minimumRemainingMovedBytes[depth] = checked(
                    _minimumRemainingMovedBytes[depth + 1]
                    + work.Options.Min(option => option.MovedBytes));
                _minimumRemainingMovedReplicas[depth] = checked(
                    _minimumRemainingMovedReplicas[depth + 1]
                    + work.Options.Min(option => option.MovedReplicas));
            }

            Visit(0, 0, 0);

            if (_bestSelections is null)
            {
                throw new InvalidOperationException(
                    "No capacity-feasible target placement exists.");
            }

            var placements = _canonicalShards
                .Select(work =>
                {
                    var option = _bestSelections[work.CanonicalIndex];
                    return new ShardPlacement(
                        work.Shard.Id,
                        option.NodeIndexes
                            .Select(nodeIndex => _nodes[nodeIndex].Id)
                            .ToList());
                })
                .ToList();
            return new(placements);
        }

        private static long[][] CreateMaximumRemainingNodeAdditions(
            IReadOnlyList<NodeSpec> nodes,
            IReadOnlyList<ShardWork> searchOrder)
        {
            var nodeCount = nodes.Count;
            var additions = new long[searchOrder.Count + 1][];
            additions[searchOrder.Count] = new long[nodeCount];

            for (var depth = searchOrder.Count - 1; depth >= 0; depth--)
            {
                var next = additions[depth + 1];
                var current = new long[nodeCount];
                Array.Copy(next, current, nodeCount);

                var work = searchOrder[depth];
                for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
                {
                    if (work.CanUseNode[nodeIndex])
                    {
                        var remainingCapacity =
                            nodes[nodeIndex].Capacity - current[nodeIndex];
                        current[nodeIndex] += Math.Min(
                            remainingCapacity,
                            work.Shard.Size);
                    }
                }

                additions[depth] = current;
            }

            return additions;
        }

        private static Fraction CreateAverageUtilization(
            IReadOnlyList<NodeSpec> nodes,
            IReadOnlyList<ShardWork> shards)
        {
            BigInteger totalLoad = 0;
            foreach (var shard in shards)
            {
                totalLoad += (BigInteger)shard.Shard.Size
                    * shard.Shard.ReplicationFactor;
            }

            BigInteger totalCapacity = 0;
            foreach (var node in nodes)
            {
                totalCapacity += node.Capacity;
            }

            return new(totalLoad, totalCapacity);
        }

        private void Visit(int depth, long movedBytes, int movedReplicas)
        {
            if (IsDominated(depth, movedBytes, movedReplicas)
                || CannotImprove(depth, movedBytes, movedReplicas))
            {
                return;
            }

            if (depth == _searchOrder.Count)
            {
                ConsiderCurrentSelection(movedBytes, movedReplicas);
                return;
            }

            var work = _searchOrder[depth];
            var candidates = new List<Candidate>();
            foreach (var option in work.Options)
            {
                if (CanPlace(work.Shard.Size, option))
                {
                    candidates.Add(new(option, Score(work.Shard.Size, option)));
                }
            }

            candidates.Sort(CompareCandidates);
            foreach (var candidate in candidates)
            {
                var option = candidate.Option;
                Apply(work.Shard.Size, option);
                _selected[work.CanonicalIndex] = option;
                Visit(
                    depth + 1,
                    checked(movedBytes + option.MovedBytes),
                    checked(movedReplicas + option.MovedReplicas));
                _selected[work.CanonicalIndex] = null;
                Remove(work.Shard.Size, option);
            }
        }

        private bool IsDominated(int depth, long movedBytes, int movedReplicas)
        {
            var key = CreateLoadKey();
            var states = _seen[depth];
            if (!states.TryGetValue(key, out var existing))
            {
                states.Add(key, new(movedBytes, movedReplicas));
                return false;
            }

            if (existing.MovedBytes < movedBytes
                || (existing.MovedBytes == movedBytes
                    && existing.MovedReplicas < movedReplicas))
            {
                return true;
            }

            if (movedBytes < existing.MovedBytes
                || (movedBytes == existing.MovedBytes
                    && movedReplicas < existing.MovedReplicas))
            {
                states[key] = new(movedBytes, movedReplicas);
            }

            return false;
        }

        private string CreateLoadKey()
        {
            var builder = new StringBuilder(_loads.Length * 12);
            foreach (var load in _loads)
            {
                builder.Append(load);
                builder.Append('|');
            }

            return builder.ToString();
        }

        private bool CannotImprove(int depth, long movedBytes, int movedReplicas)
        {
            if (_bestObjective is null)
            {
                return false;
            }

            var lowerMaximum = Maximum(CurrentMaximum(), _averageUtilization);
            var maximumComparison = CompareFractions(
                lowerMaximum,
                _bestObjective.Value.Maximum);
            if (maximumComparison > 0)
            {
                return true;
            }

            if (maximumComparison < 0)
            {
                return false;
            }

            var lowerSpread = MinimumPossibleSpread(lowerMaximum, depth);
            var spreadComparison = CompareFractions(
                lowerSpread,
                _bestObjective.Value.Spread);
            if (spreadComparison > 0)
            {
                return true;
            }

            if (spreadComparison < 0)
            {
                return false;
            }

            var minimumMovedBytes = checked(
                movedBytes + _minimumRemainingMovedBytes[depth]);
            if (minimumMovedBytes > _bestObjective.Value.MovedBytes)
            {
                return true;
            }

            if (minimumMovedBytes < _bestObjective.Value.MovedBytes)
            {
                return false;
            }

            var minimumMovedReplicas = checked(
                movedReplicas + _minimumRemainingMovedReplicas[depth]);
            return minimumMovedReplicas > _bestObjective.Value.MovedReplicas;
        }

        private Fraction CurrentMaximum()
        {
            var maximum = new Fraction(_loads[0], _nodes[0].Capacity);
            for (var nodeIndex = 1; nodeIndex < _nodes.Count; nodeIndex++)
            {
                var utilization = new Fraction(
                    _loads[nodeIndex],
                    _nodes[nodeIndex].Capacity);
                if (CompareFractions(utilization, maximum) > 0)
                {
                    maximum = utilization;
                }
            }

            return maximum;
        }

        private Fraction MinimumPossibleSpread(Fraction lowerMaximum, int depth)
        {
            var additions = _maximumRemainingNodeAdditions[depth];
            var minimumUpperUtilization = new Fraction(
                MaximumFinalLoad(0, additions[0]),
                _nodes[0].Capacity);

            for (var nodeIndex = 1; nodeIndex < _nodes.Count; nodeIndex++)
            {
                var upperUtilization = new Fraction(
                    MaximumFinalLoad(nodeIndex, additions[nodeIndex]),
                    _nodes[nodeIndex].Capacity);
                if (CompareFractions(upperUtilization, minimumUpperUtilization) < 0)
                {
                    minimumUpperUtilization = upperUtilization;
                }
            }

            var numerator = lowerMaximum.Numerator
                * minimumUpperUtilization.Denominator
                - minimumUpperUtilization.Numerator
                    * lowerMaximum.Denominator;
            if (numerator.Sign <= 0)
            {
                return new(BigInteger.Zero, BigInteger.One);
            }

            return new(
                numerator,
                lowerMaximum.Denominator
                    * minimumUpperUtilization.Denominator);
        }

        private long MaximumFinalLoad(int nodeIndex, long possibleAddition)
        {
            var spareCapacity = _nodes[nodeIndex].Capacity - _loads[nodeIndex];
            return _loads[nodeIndex] + Math.Min(spareCapacity, possibleAddition);
        }

        private bool CanPlace(long shardSize, PlacementOption option)
        {
            foreach (var nodeIndex in option.NodeIndexes)
            {
                if (_loads[nodeIndex] > _nodes[nodeIndex].Capacity - shardSize)
                {
                    return false;
                }
            }

            return true;
        }

        private void Apply(long shardSize, PlacementOption option)
        {
            foreach (var nodeIndex in option.NodeIndexes)
            {
                _loads[nodeIndex] += shardSize;
            }
        }

        private void Remove(long shardSize, PlacementOption option)
        {
            foreach (var nodeIndex in option.NodeIndexes)
            {
                _loads[nodeIndex] -= shardSize;
            }
        }

        private CandidateScore Score(long shardSize, PlacementOption option)
        {
            var maximum = ProjectedUtilization(
                shardSize,
                option,
                findMaximum: true);
            var minimum = ProjectedUtilization(
                shardSize,
                option,
                findMaximum: false);
            return new(
                maximum,
                SubtractFractions(maximum, minimum));
        }

        private Fraction ProjectedUtilization(
            long shardSize,
            PlacementOption option,
            bool findMaximum)
        {
            var firstLoad = _loads[0]
                + (option.ContainsNode(0) ? shardSize : 0);
            var selected = new Fraction(firstLoad, _nodes[0].Capacity);

            for (var nodeIndex = 1; nodeIndex < _nodes.Count; nodeIndex++)
            {
                var load = _loads[nodeIndex]
                    + (option.ContainsNode(nodeIndex) ? shardSize : 0);
                var utilization = new Fraction(load, _nodes[nodeIndex].Capacity);
                var comparison = CompareFractions(utilization, selected);
                if ((findMaximum && comparison > 0)
                    || (!findMaximum && comparison < 0))
                {
                    selected = utilization;
                }
            }

            return selected;
        }

        private int CompareCandidates(Candidate left, Candidate right)
        {
            var comparison = CompareFractions(left.Score.Maximum, right.Score.Maximum);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareFractions(left.Score.Spread, right.Score.Spread);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Option.MovedBytes.CompareTo(right.Option.MovedBytes);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Option.MovedReplicas.CompareTo(right.Option.MovedReplicas);
            if (comparison != 0)
            {
                return comparison;
            }

            return CompareNodeIndexes(left.Option.NodeIndexes, right.Option.NodeIndexes);
        }

        private void ConsiderCurrentSelection(long movedBytes, int movedReplicas)
        {
            var maximum = CurrentMaximum();
            var minimum = new Fraction(_loads[0], _nodes[0].Capacity);
            for (var nodeIndex = 1; nodeIndex < _nodes.Count; nodeIndex++)
            {
                var utilization = new Fraction(
                    _loads[nodeIndex],
                    _nodes[nodeIndex].Capacity);
                if (CompareFractions(utilization, minimum) < 0)
                {
                    minimum = utilization;
                }
            }

            var objective = new Objective(
                maximum,
                SubtractFractions(maximum, minimum),
                movedBytes,
                movedReplicas);
            if (_bestObjective is not null)
            {
                var comparison = CompareObjectives(objective, _bestObjective.Value);
                if (comparison > 0
                    || (comparison == 0 && !IsLexicographicallySmaller()))
                {
                    return;
                }
            }

            _bestObjective = objective;
            _bestSelections = _selected
                .Select(option => option
                    ?? throw new InvalidOperationException("A shard was not selected."))
                .ToArray();
        }

        private bool IsLexicographicallySmaller()
        {
            if (_bestSelections is null)
            {
                return true;
            }

            for (var shardIndex = 0; shardIndex < _selected.Length; shardIndex++)
            {
                var selected = _selected[shardIndex]
                    ?? throw new InvalidOperationException("A shard was not selected.");
                var comparison = CompareNodeIndexes(
                    selected.NodeIndexes,
                    _bestSelections[shardIndex].NodeIndexes);
                if (comparison != 0)
                {
                    return comparison < 0;
                }
            }

            return false;
        }

        private static int CompareObjectives(Objective left, Objective right)
        {
            var comparison = CompareFractions(left.Maximum, right.Maximum);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareFractions(left.Spread, right.Spread);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.MovedBytes.CompareTo(right.MovedBytes);
            return comparison != 0
                ? comparison
                : left.MovedReplicas.CompareTo(right.MovedReplicas);
        }

        private static int CompareNodeIndexes(
            IReadOnlyList<int> left,
            IReadOnlyList<int> right)
        {
            for (var index = 0; index < left.Count; index++)
            {
                var comparison = left[index].CompareTo(right[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return 0;
        }

        private static Fraction Maximum(Fraction left, Fraction right) =>
            CompareFractions(left, right) >= 0 ? left : right;

        private static Fraction SubtractFractions(Fraction left, Fraction right) =>
            new(
                left.Numerator * right.Denominator
                    - right.Numerator * left.Denominator,
                left.Denominator * right.Denominator);

        private static int CompareFractions(Fraction left, Fraction right) =>
            (left.Numerator * right.Denominator).CompareTo(
                right.Numerator * left.Denominator);

        private readonly record struct MovementCost(
            long MovedBytes,
            int MovedReplicas);

        private readonly record struct Fraction(
            BigInteger Numerator,
            BigInteger Denominator);

        private readonly record struct Candidate(
            PlacementOption Option,
            CandidateScore Score);

        private readonly record struct CandidateScore(
            Fraction Maximum,
            Fraction Spread);

        private readonly record struct Objective(
            Fraction Maximum,
            Fraction Spread,
            long MovedBytes,
            int MovedReplicas);
    }

    private sealed record ShardWork(
        int CanonicalIndex,
        ShardSpec Shard,
        PlacementOption[] Options,
        bool[] CanUseNode);

    private sealed record PlacementOption(
        int[] NodeIndexes,
        long MovedBytes,
        int MovedReplicas)
    {
        public bool ContainsNode(int nodeIndex) =>
            Array.BinarySearch(NodeIndexes, nodeIndex) >= 0;
    }
}
