namespace ReplicatedShardRebalancer;

/// <summary>
/// Deterministic rebalancer. For every shard we enumerate the candidate
/// target node sets that satisfy the hard rules (eligibility, replica
/// count, maximum zone diversity), then run an exact branch-and-bound
/// search across shards to find the assignment that lexicographically
/// minimizes (maximum node utilization, utilization spread, moved bytes,
/// moved replica count), tie-broken by the ordinally smallest complete
/// placement.
/// </summary>
public sealed class ReplicatedShardRebalancer
{
    public RebalanceResult Rebalance(RebalanceProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var nodes = new Dictionary<string, NodeSpec>(StringComparer.Ordinal);
        foreach (var node in problem.Nodes ?? [])
        {
            nodes[node.Id] = node;
        }

        var shards = (problem.Shards ?? [])
            .OrderBy(shard => shard.Id, StringComparer.Ordinal)
            .ToList();

        var current = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var placement in problem.CurrentPlacements ?? [])
        {
            current[placement.ShardId] = new HashSet<string>(
                placement.NodeIds ?? [],
                StringComparer.Ordinal);
        }

        var exclusions = new HashSet<(string ShardId, string NodeId)>();
        foreach (var exclusion in problem.Exclusions ?? [])
        {
            exclusions.Add((exclusion.ShardId, exclusion.NodeId));
        }

        var nodeIds = nodes.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var nodeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < nodeIds.Length; i++)
        {
            nodeIndex[nodeIds[i]] = i;
        }

        var capacities = nodeIds.Select(id => nodes[id].Capacity).ToArray();
        var nodeCount = nodeIds.Length;

        var contexts = new List<ShardContext>(shards.Count);
        foreach (var shard in shards)
        {
            var eligible = nodeIds
                .Where(id =>
                    nodes[id].Capacity >= shard.Size
                    && !exclusions.Contains((shard.Id, id)))
                .ToArray();
            var distinctZones = eligible
                .Select(id => nodes[id].Zone)
                .Distinct(StringComparer.Ordinal)
                .Count();
            var required = Math.Min(shard.ReplicationFactor, distinctZones);
            var currentSet = current.GetValueOrDefault(shard.Id) ?? [];

            var candidates = GenerateCandidates(
                eligible,
                nodes,
                shard,
                required,
                currentSet,
                nodeIndex);

            candidates.Sort(CompareNodeIdArrays);

            var minBytes = candidates.Count > 0
                ? candidates.Min(candidate => candidate.MovedBytes)
                : 0L;
            var minReplicas = candidates.Count > 0
                ? candidates.Min(candidate => candidate.MovedReplicas)
                : 0;

            contexts.Add(new ShardContext(shard, candidates, minBytes, minReplicas));
        }

        if (contexts.Any(context => context.Candidates.Count == 0))
        {
            // Should not happen given the input feasibility guarantee, but
            // avoid throwing: there is no sound placement to return.
            return RebalanceResult.Empty;
        }

        var order = Enumerable.Range(0, contexts.Count)
            .OrderBy(i => contexts[i].Candidates.Count)
            .ThenBy(i => contexts[i].Shard.Id, StringComparer.Ordinal)
            .ToArray();

        var solver = new Solver(contexts, order, capacities, nodeCount);
        var assigned = solver.Run();

        var placements = shards
            .Select(shard => new ShardPlacement(
                shard.Id,
                assigned[shard.Id].NodeIds.ToList()))
            .OrderBy(placement => placement.ShardId, StringComparer.Ordinal)
            .ToList();

        return new RebalanceResult(placements);
    }

    private static int CompareNodeIdArrays(Candidate a, Candidate b)
    {
        for (var i = 0; i < a.NodeIds.Length; i++)
        {
            var c = string.CompareOrdinal(a.NodeIds[i], b.NodeIds[i]);
            if (c != 0)
            {
                return c;
            }
        }
        return 0;
    }

    private static List<Candidate> GenerateCandidates(
        string[] eligible,
        Dictionary<string, NodeSpec> nodes,
        ShardSpec shard,
        int required,
        HashSet<string> currentSet,
        Dictionary<string, int> nodeIndex)
    {
        var output = new List<Candidate>();
        var n = eligible.Length;
        var rf = shard.ReplicationFactor;
        if (rf > n || rf <= 0)
        {
            return output;
        }

        // Suffix distinct-zone counts give an admissible upper bound on how
        // many *new* zones remain reachable from a given start index, which
        // lets us prune combinations that can never reach the exact
        // required diversity.
        var suffixZones = new int[n + 1];
        var seenSuffix = new HashSet<string>(StringComparer.Ordinal);
        for (var i = n - 1; i >= 0; i--)
        {
            seenSuffix.Add(nodes[eligible[i]].Zone);
            suffixZones[i] = seenSuffix.Count;
        }

        var chosen = new int[rf];
        var zoneCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        void Recurse(int startIdx, int depth, int zonesUsed)
        {
            if (zonesUsed > required)
            {
                return;
            }

            var remainingSlots = rf - depth;
            if (remainingSlots == 0)
            {
                if (zonesUsed == required)
                {
                    var ids = new string[rf];
                    var idxs = new int[rf];
                    long movedBytes = 0;
                    var movedReplicas = 0;
                    for (var k = 0; k < rf; k++)
                    {
                        ids[k] = eligible[chosen[k]];
                        idxs[k] = nodeIndex[ids[k]];
                        if (!currentSet.Contains(ids[k]))
                        {
                            movedBytes += shard.Size;
                            movedReplicas++;
                        }
                    }
                    output.Add(new Candidate(ids, idxs, movedBytes, movedReplicas));
                }
                return;
            }

            // Upper bound on final zone diversity reachable from here.
            var maxAdditional = Math.Min(remainingSlots, suffixZones[startIdx]);
            if (zonesUsed + maxAdditional < required)
            {
                return;
            }

            for (var i = startIdx; i <= n - remainingSlots; i++)
            {
                chosen[depth] = i;
                var zone = nodes[eligible[i]].Zone;
                var isNewZone = !zoneCounts.TryGetValue(zone, out var zc) || zc == 0;
                zoneCounts[zone] = zoneCounts.GetValueOrDefault(zone) + 1;

                Recurse(i + 1, depth + 1, isNewZone ? zonesUsed + 1 : zonesUsed);

                zoneCounts[zone]--;
            }
        }

        Recurse(0, 0, 0);
        return output;
    }

    private sealed record Candidate(
        string[] NodeIds,
        int[] NodeIndexes,
        long MovedBytes,
        int MovedReplicas);

    private sealed record ShardContext(
        ShardSpec Shard,
        List<Candidate> Candidates,
        long MinMovedBytes,
        int MinMovedReplicas);

    /// <summary>
    /// Exact branch-and-bound search across shards' candidate placements.
    /// A greedy pass first seeds a valid complete assignment so pruning is
    /// effective from the start; the exhaustive search then explores every
    /// remaining possibility (bounded by a deterministic node-visit budget)
    /// and keeps the lexicographically best complete assignment found.
    /// </summary>
    private sealed class Solver
    {
        private const long NodeBudget = 4_000_000;

        private readonly List<ShardContext> _contexts;
        private readonly int[] _order;
        private readonly long[] _capacities;
        private readonly int _nodeCount;
        private readonly long[] _loads;
        private readonly Dictionary<string, Candidate> _assigned =
            new(StringComparer.Ordinal);
        private readonly List<ShardContext> _shardIdOrder;

        private double _bestMaxUtil = double.PositiveInfinity;
        private double _bestSpread = double.PositiveInfinity;
        private long _bestMovedBytes = long.MaxValue;
        private int _bestMovedReplicas = int.MaxValue;
        private Dictionary<string, Candidate>? _bestAssignment;
        private long _nodesVisited;

        public Solver(
            List<ShardContext> contexts,
            int[] order,
            long[] capacities,
            int nodeCount)
        {
            _contexts = contexts;
            _order = order;
            _capacities = capacities;
            _nodeCount = nodeCount;
            _loads = new long[nodeCount];
            _shardIdOrder = contexts
                .OrderBy(context => context.Shard.Id, StringComparer.Ordinal)
                .ToList();
        }

        public Dictionary<string, Candidate> Run()
        {
            SeedGreedy();
            Search(0, 0L, 0);
            return _bestAssignment
                ?? throw new InvalidOperationException(
                    "No feasible assignment was found.");
        }

        private void SeedGreedy()
        {
            var loads = new long[_nodeCount];
            var assigned = new Dictionary<string, Candidate>(StringComparer.Ordinal);
            long movedBytes = 0;
            var movedReplicas = 0;
            var ok = true;

            foreach (var idx in _order)
            {
                var ctx = _contexts[idx];
                Candidate? bestCand = null;
                var bestMax = double.PositiveInfinity;
                var bestSpr = double.PositiveInfinity;
                var bestBytes = long.MaxValue;
                var bestRep = int.MaxValue;

                foreach (var cand in ctx.Candidates)
                {
                    var feasible = true;
                    foreach (var ni in cand.NodeIndexes)
                    {
                        if (loads[ni] + ctx.Shard.Size > _capacities[ni])
                        {
                            feasible = false;
                            break;
                        }
                    }
                    if (!feasible)
                    {
                        continue;
                    }

                    foreach (var ni in cand.NodeIndexes)
                    {
                        loads[ni] += ctx.Shard.Size;
                    }

                    var max = 0.0;
                    var min = double.PositiveInfinity;
                    for (var i = 0; i < _nodeCount; i++)
                    {
                        var u = _capacities[i] > 0 ? (double)loads[i] / _capacities[i] : 0;
                        if (u > max)
                        {
                            max = u;
                        }
                        if (u < min)
                        {
                            min = u;
                        }
                    }

                    foreach (var ni in cand.NodeIndexes)
                    {
                        loads[ni] -= ctx.Shard.Size;
                    }

                    var spr = _nodeCount > 0 ? max - min : 0;
                    var mb = movedBytes + cand.MovedBytes;
                    var mr = movedReplicas + cand.MovedReplicas;

                    if (max < bestMax
                        || (max == bestMax && spr < bestSpr)
                        || (max == bestMax && spr == bestSpr && mb < bestBytes)
                        || (max == bestMax && spr == bestSpr && mb == bestBytes
                            && mr < bestRep))
                    {
                        bestMax = max;
                        bestSpr = spr;
                        bestBytes = mb;
                        bestRep = mr;
                        bestCand = cand;
                    }
                }

                if (bestCand is null)
                {
                    ok = false;
                    break;
                }

                foreach (var ni in bestCand.NodeIndexes)
                {
                    loads[ni] += ctx.Shard.Size;
                }
                assigned[ctx.Shard.Id] = bestCand;
                movedBytes += bestCand.MovedBytes;
                movedReplicas += bestCand.MovedReplicas;
            }

            if (ok && assigned.Count == _contexts.Count)
            {
                var max = 0.0;
                var min = double.PositiveInfinity;
                for (var i = 0; i < _nodeCount; i++)
                {
                    var u = _capacities[i] > 0 ? (double)loads[i] / _capacities[i] : 0;
                    if (u > max)
                    {
                        max = u;
                    }
                    if (u < min)
                    {
                        min = u;
                    }
                }
                var spread = _nodeCount > 0 ? max - min : 0;
                UpdateBestIfBetter(max, spread, movedBytes, movedReplicas, assigned);
            }
        }

        private void Search(int pos, long partialMovedBytes, int partialMovedReplicas)
        {
            _nodesVisited++;
            if (_nodesVisited > NodeBudget)
            {
                return;
            }

            if (pos == _order.Length)
            {
                var max = 0.0;
                var min = double.PositiveInfinity;
                for (var i = 0; i < _nodeCount; i++)
                {
                    var u = _capacities[i] > 0 ? (double)_loads[i] / _capacities[i] : 0;
                    if (u > max)
                    {
                        max = u;
                    }
                    if (u < min)
                    {
                        min = u;
                    }
                }
                var spread = _nodeCount > 0 ? max - min : 0;
                UpdateBestIfBetter(max, spread, partialMovedBytes, partialMovedReplicas, _assigned);
                return;
            }

            if (_bestAssignment is not null)
            {
                var lbMax = 0.0;
                for (var i = 0; i < _nodeCount; i++)
                {
                    var u = _capacities[i] > 0 ? (double)_loads[i] / _capacities[i] : 0;
                    if (u > lbMax)
                    {
                        lbMax = u;
                    }
                }

                if (lbMax > _bestMaxUtil)
                {
                    return;
                }

                if (lbMax == _bestMaxUtil)
                {
                    var lbBytes = partialMovedBytes;
                    for (var k = pos; k < _order.Length; k++)
                    {
                        lbBytes += _contexts[_order[k]].MinMovedBytes;
                    }
                    if (lbBytes > _bestMovedBytes)
                    {
                        return;
                    }

                    if (lbBytes == _bestMovedBytes)
                    {
                        var lbRep = partialMovedReplicas;
                        for (var k = pos; k < _order.Length; k++)
                        {
                            lbRep += _contexts[_order[k]].MinMovedReplicas;
                        }
                        if (lbRep > _bestMovedReplicas)
                        {
                            return;
                        }
                    }
                }
            }

            var ctx = _contexts[_order[pos]];
            foreach (var cand in ctx.Candidates)
            {
                var feasible = true;
                foreach (var ni in cand.NodeIndexes)
                {
                    if (_loads[ni] + ctx.Shard.Size > _capacities[ni])
                    {
                        feasible = false;
                        break;
                    }
                }
                if (!feasible)
                {
                    continue;
                }

                foreach (var ni in cand.NodeIndexes)
                {
                    _loads[ni] += ctx.Shard.Size;
                }
                _assigned[ctx.Shard.Id] = cand;

                Search(
                    pos + 1,
                    partialMovedBytes + cand.MovedBytes,
                    partialMovedReplicas + cand.MovedReplicas);

                foreach (var ni in cand.NodeIndexes)
                {
                    _loads[ni] -= ctx.Shard.Size;
                }
                _assigned.Remove(ctx.Shard.Id);

                if (_nodesVisited > NodeBudget)
                {
                    return;
                }
            }
        }

        private void UpdateBestIfBetter(
            double max,
            double spread,
            long movedBytes,
            int movedReplicas,
            Dictionary<string, Candidate> assignment)
        {
            bool better;
            if (_bestAssignment is null)
            {
                better = true;
            }
            else if (max != _bestMaxUtil)
            {
                better = max < _bestMaxUtil;
            }
            else if (spread != _bestSpread)
            {
                better = spread < _bestSpread;
            }
            else if (movedBytes != _bestMovedBytes)
            {
                better = movedBytes < _bestMovedBytes;
            }
            else if (movedReplicas != _bestMovedReplicas)
            {
                better = movedReplicas < _bestMovedReplicas;
            }
            else
            {
                better = CompareAssignments(assignment, _bestAssignment) < 0;
            }

            if (better)
            {
                _bestMaxUtil = max;
                _bestSpread = spread;
                _bestMovedBytes = movedBytes;
                _bestMovedReplicas = movedReplicas;
                _bestAssignment = new Dictionary<string, Candidate>(
                    assignment,
                    StringComparer.Ordinal);
            }
        }

        private int CompareAssignments(
            Dictionary<string, Candidate> a,
            Dictionary<string, Candidate> b)
        {
            foreach (var ctx in _shardIdOrder)
            {
                var ca = a[ctx.Shard.Id];
                var cb = b[ctx.Shard.Id];
                for (var i = 0; i < ca.NodeIds.Length; i++)
                {
                    var c = string.CompareOrdinal(ca.NodeIds[i], cb.NodeIds[i]);
                    if (c != 0)
                    {
                        return c;
                    }
                }
            }
            return 0;
        }
    }
}
