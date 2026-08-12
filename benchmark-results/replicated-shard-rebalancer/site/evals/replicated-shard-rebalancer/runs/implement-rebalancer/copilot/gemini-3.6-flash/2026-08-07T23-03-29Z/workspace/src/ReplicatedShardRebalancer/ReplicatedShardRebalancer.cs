namespace ReplicatedShardRebalancer;

public sealed class ReplicatedShardRebalancer
{
    public RebalanceResult Rebalance(RebalanceProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        if (problem.Nodes == null || problem.Nodes.Count == 0 ||
            problem.Shards == null || problem.Shards.Count == 0)
        {
            return RebalanceResult.Empty;
        }

        // 1. Sort nodes and shards ordinally
        var nodes = problem.Nodes
            .Where(n => !string.IsNullOrWhiteSpace(n.Id) && !string.IsNullOrWhiteSpace(n.Zone) && n.Capacity > 0)
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .ToList();

        var shards = problem.Shards
            .Where(s => !string.IsNullOrWhiteSpace(s.Id) && s.Size > 0 && s.ReplicationFactor > 0)
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();

        if (nodes.Count == 0 || shards.Count == 0)
        {
            return RebalanceResult.Empty;
        }

        int nodeCount = nodes.Count;
        int shardCount = shards.Count;

        var nodeIdToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < nodeCount; i++)
        {
            nodeIdToIndex[nodes[i].Id] = i;
        }

        var shardIdToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int j = 0; j < shardCount; j++)
        {
            shardIdToIndex[shards[j].Id] = j;
        }

        var exclusions = new HashSet<(string ShardId, string NodeId)>();
        if (problem.Exclusions != null)
        {
            foreach (var ex in problem.Exclusions)
            {
                if (!string.IsNullOrEmpty(ex.ShardId) && !string.IsNullOrEmpty(ex.NodeId))
                {
                    exclusions.Add((ex.ShardId, ex.NodeId));
                }
            }
        }

        var currentPlacements = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (problem.CurrentPlacements != null)
        {
            foreach (var cp in problem.CurrentPlacements)
            {
                if (!string.IsNullOrEmpty(cp.ShardId))
                {
                    currentPlacements[cp.ShardId] = new HashSet<string>(cp.NodeIds ?? [], StringComparer.Ordinal);
                }
            }
        }

        // 2. Generate candidates for each shard
        var candidatesPerShard = new List<Candidate[]>();
        for (int j = 0; j < shardCount; j++)
        {
            var shard = shards[j];
            int reqZones = RebalanceValidator.MaximumZoneDiversity(shard, nodes, exclusions);

            var eligibleNodes = nodes
                .Where(n => n.Capacity >= shard.Size && !exclusions.Contains((shard.Id, n.Id)))
                .ToList();

            if (eligibleNodes.Count < shard.ReplicationFactor)
            {
                return RebalanceResult.Empty;
            }

            var currentSet = currentPlacements.GetValueOrDefault(shard.Id, []);

            var combos = new List<Candidate>();
            GenerateCombinations(
                eligibleNodes,
                shard.ReplicationFactor,
                0,
                new List<NodeSpec>(),
                combos,
                shard,
                reqZones,
                currentSet,
                nodeIdToIndex);

            if (combos.Count == 0)
            {
                return RebalanceResult.Empty;
            }

            for (int k = 0; k < combos.Count; k++)
            {
                combos[k].CandidateIndex = k;
            }

            candidatesPerShard.Add(combos.ToArray());
        }

        // 3. Compute remaining lower bounds for moved bytes and moved replicas
        var minRemBytes = new long[shardCount + 1];
        var minRemReplicas = new int[shardCount + 1];
        for (int j = shardCount - 1; j >= 0; j--)
        {
            long minB = long.MaxValue;
            int minR = int.MaxValue;
            foreach (var c in candidatesPerShard[j])
            {
                if (c.MovedBytes < minB) minB = c.MovedBytes;
                if (c.MovedReplicas < minR) minR = c.MovedReplicas;
            }
            minRemBytes[j] = minRemBytes[j + 1] + minB;
            minRemReplicas[j] = minRemReplicas[j + 1] + minR;
        }

        // 4. Optimization Solver: Warm Start + Branch & Bound
        var solver = new Solver(nodes, shards, candidatesPerShard, minRemBytes, minRemReplicas);
        var bestSolution = solver.Solve();

        if (bestSolution == null || !bestSolution.IsValid)
        {
            return RebalanceResult.Empty;
        }

        // 5. Construct TargetPlacements
        var targetPlacements = new List<ShardPlacement>(shardCount);
        for (int j = 0; j < shardCount; j++)
        {
            var chosenCandidate = candidatesPerShard[j][bestSolution.Choice[j]];
            targetPlacements.Add(new ShardPlacement(shards[j].Id, [.. chosenCandidate.NodeIds]));
        }

        return new RebalanceResult(targetPlacements);
    }

    private static void GenerateCombinations(
        List<NodeSpec> eligibleNodes,
        int k,
        int start,
        List<NodeSpec> current,
        List<Candidate> results,
        ShardSpec shard,
        int reqZones,
        HashSet<string> currentSet,
        Dictionary<string, int> nodeIdToIndex)
    {
        if (current.Count == k)
        {
            int distinctZones = current.Select(n => n.Zone).Distinct(StringComparer.Ordinal).Count();
            if (distinctZones == reqZones)
            {
                var nodeIndices = current.Select(n => nodeIdToIndex[n.Id]).ToArray();
                var nodeIds = current.Select(n => n.Id).ToArray();
                long movedBytes = 0;
                int movedReplicas = 0;
                foreach (var n in current)
                {
                    if (!currentSet.Contains(n.Id))
                    {
                        movedBytes += shard.Size;
                        movedReplicas++;
                    }
                }
                results.Add(new Candidate(nodeIndices, nodeIds, movedBytes, movedReplicas));
            }
            return;
        }

        for (int i = start; i <= eligibleNodes.Count - (k - current.Count); i++)
        {
            current.Add(eligibleNodes[i]);
            GenerateCombinations(eligibleNodes, k, i + 1, current, results, shard, reqZones, currentSet, nodeIdToIndex);
            current.RemoveAt(current.Count - 1);
        }
    }

    private sealed class Candidate
    {
        public int[] NodeIndices { get; }
        public string[] NodeIds { get; }
        public long MovedBytes { get; }
        public int MovedReplicas { get; }
        public int CandidateIndex { get; set; }

        public Candidate(int[] nodeIndices, string[] nodeIds, long movedBytes, int movedReplicas)
        {
            NodeIndices = nodeIndices;
            NodeIds = nodeIds;
            MovedBytes = movedBytes;
            MovedReplicas = movedReplicas;
        }
    }

    private sealed class SolutionEvaluation : IComparable<SolutionEvaluation>
    {
        public double MaxUtilization { get; }
        public double UtilizationSpread { get; }
        public long MovedBytes { get; }
        public int MovedReplicaCount { get; }
        public int[] Choice { get; }
        public bool IsValid { get; }

        public SolutionEvaluation(
            double maxUtilization,
            double utilizationSpread,
            long movedBytes,
            int movedReplicaCount,
            int[] choice,
            bool isValid = true)
        {
            MaxUtilization = maxUtilization;
            UtilizationSpread = utilizationSpread;
            MovedBytes = movedBytes;
            MovedReplicaCount = movedReplicaCount;
            Choice = (int[])choice.Clone();
            IsValid = isValid;
        }

        public int CompareTo(SolutionEvaluation? other)
        {
            if (other is null) return -1;
            if (IsValid != other.IsValid) return IsValid ? -1 : 1;

            int cmp = MaxUtilization.CompareTo(other.MaxUtilization);
            if (cmp != 0) return cmp;

            cmp = UtilizationSpread.CompareTo(other.UtilizationSpread);
            if (cmp != 0) return cmp;

            cmp = MovedBytes.CompareTo(other.MovedBytes);
            if (cmp != 0) return cmp;

            cmp = MovedReplicaCount.CompareTo(other.MovedReplicaCount);
            if (cmp != 0) return cmp;

            for (int i = 0; i < Choice.Length; i++)
            {
                cmp = Choice[i].CompareTo(other.Choice[i]);
                if (cmp != 0) return cmp;
            }

            return 0;
        }
    }

    private sealed class Solver
    {
        private readonly List<NodeSpec> _nodes;
        private readonly List<ShardSpec> _shards;
        private readonly List<Candidate[]> _candidates;
        private readonly long[] _minRemBytes;
        private readonly int[] _minRemReplicas;
        private readonly int _nodeCount;
        private readonly int _shardCount;

        private SolutionEvaluation? _bestSolution;
        private readonly long[] _currentLoads;
        private readonly int[] _choice;
        private long _currentMovedBytes;
        private int _currentMovedReplicas;

        private readonly Dictionary<LoadVectorKey, (long Bytes, int Replicas)>[] _memo;
        private int _nodeVisitCount;
        private const int NodeVisitLimit = 2_000_000;

        public Solver(
            List<NodeSpec> nodes,
            List<ShardSpec> shards,
            List<Candidate[]> candidates,
            long[] minRemBytes,
            int[] minRemReplicas)
        {
            _nodes = nodes;
            _shards = shards;
            _candidates = candidates;
            _minRemBytes = minRemBytes;
            _minRemReplicas = minRemReplicas;
            _nodeCount = nodes.Count;
            _shardCount = shards.Count;

            _currentLoads = new long[_nodeCount];
            _choice = new int[_shardCount];

            _memo = new Dictionary<LoadVectorKey, (long Bytes, int Replicas)>[_shardCount + 1];
            for (int j = 0; j <= _shardCount; j++)
            {
                _memo[j] = new Dictionary<LoadVectorKey, (long Bytes, int Replicas)>();
            }
        }

        public SolutionEvaluation? Solve()
        {
            // Warm start with local search
            WarmStart();

            // Run Branch & Bound
            Dfs(0);

            return _bestSolution;
        }

        private void WarmStart()
        {
            // Try greedy heuristics
            TryGreedy(strategy: 0); // Min resulting max util
            TryGreedy(strategy: 1); // Min moved bytes
            TryGreedy(strategy: 2); // Match current placement

            if (_bestSolution != null)
            {
                LocalSearch();
            }
        }

        private void TryGreedy(int strategy)
        {
            var loads = new long[_nodeCount];
            var choice = new int[_shardCount];
            long movedBytes = 0;
            int movedReplicas = 0;

            for (int j = 0; j < _shardCount; j++)
            {
                var cands = _candidates[j];
                long shardSize = _shards[j].Size;

                int bestCandIdx = -1;
                double bestCost = double.MaxValue;

                for (int k = 0; k < cands.Length; k++)
                {
                    var cand = cands[k];
                    // Check capacity
                    bool feasible = true;
                    foreach (int nIdx in cand.NodeIndices)
                    {
                        if (loads[nIdx] + shardSize > _nodes[nIdx].Capacity)
                        {
                            feasible = false;
                            break;
                        }
                    }
                    if (!feasible) continue;

                    double cost = 0;
                    if (strategy == 0) // Min max util
                    {
                        double maxU = 0;
                        foreach (int nIdx in cand.NodeIndices)
                        {
                            double u = (double)(loads[nIdx] + shardSize) / _nodes[nIdx].Capacity;
                            if (u > maxU) maxU = u;
                        }
                        cost = maxU * 1e9 + cand.MovedBytes;
                    }
                    else if (strategy == 1) // Min moved bytes
                    {
                        cost = cand.MovedBytes * 1e6 + cand.MovedReplicas;
                    }
                    else // Match current
                    {
                        cost = cand.MovedBytes == 0 ? 0 : (1e6 + cand.MovedBytes);
                    }

                    if (bestCandIdx == -1 || cost < bestCost)
                    {
                        bestCost = cost;
                        bestCandIdx = k;
                    }
                }

                if (bestCandIdx == -1) return; // Infeasible greedy run

                var chosen = cands[bestCandIdx];
                choice[j] = bestCandIdx;
                foreach (int nIdx in chosen.NodeIndices)
                {
                    loads[nIdx] += shardSize;
                }
                movedBytes += chosen.MovedBytes;
                movedReplicas += chosen.MovedReplicas;
            }

            var eval = EvaluateChoice(choice, loads, movedBytes, movedReplicas);
            if (eval.IsValid && (_bestSolution == null || eval.CompareTo(_bestSolution) < 0))
            {
                _bestSolution = eval;
            }
        }

        private void LocalSearch()
        {
            if (_bestSolution == null) return;

            int[] currentChoice = (int[])_bestSolution.Choice.Clone();
            bool improved = true;

            while (improved)
            {
                improved = false;
                for (int j = 0; j < _shardCount; j++)
                {
                    int oldCand = currentChoice[j];
                    for (int k = 0; k < _candidates[j].Length; k++)
                    {
                        if (k == oldCand) continue;
                        currentChoice[j] = k;

                        var eval = EvaluateFullChoice(currentChoice);
                        if (eval.IsValid && eval.CompareTo(_bestSolution) < 0)
                        {
                            _bestSolution = eval;
                            improved = true;
                            break;
                        }
                    }
                    if (!improved)
                    {
                        currentChoice[j] = oldCand;
                    }
                }
            }
        }

        private SolutionEvaluation EvaluateFullChoice(int[] choice)
        {
            var loads = new long[_nodeCount];
            long movedBytes = 0;
            int movedReplicas = 0;

            for (int j = 0; j < _shardCount; j++)
            {
                var cand = _candidates[j][choice[j]];
                long size = _shards[j].Size;
                foreach (int nIdx in cand.NodeIndices)
                {
                    loads[nIdx] += size;
                    if (loads[nIdx] > _nodes[nIdx].Capacity)
                    {
                        return new SolutionEvaluation(0, 0, 0, 0, choice, isValid: false);
                    }
                }
                movedBytes += cand.MovedBytes;
                movedReplicas += cand.MovedReplicas;
            }

            return EvaluateChoice(choice, loads, movedBytes, movedReplicas);
        }

        private SolutionEvaluation EvaluateChoice(
            int[] choice,
            long[] loads,
            long movedBytes,
            int movedReplicas)
        {
            double maxU = 0;
            double minU = double.MaxValue;

            for (int i = 0; i < _nodeCount; i++)
            {
                double u = (double)loads[i] / _nodes[i].Capacity;
                if (u > maxU) maxU = u;
                if (u < minU) minU = u;
            }

            double spread = maxU - minU;
            return new SolutionEvaluation(maxU, spread, movedBytes, movedReplicas, choice);
        }

        private void Dfs(int depth)
        {
            _nodeVisitCount++;
            if (_nodeVisitCount > NodeVisitLimit) return;

            if (depth == _shardCount)
            {
                var eval = EvaluateChoice(_choice, _currentLoads, _currentMovedBytes, _currentMovedReplicas);
                if (_bestSolution == null || eval.CompareTo(_bestSolution) < 0)
                {
                    _bestSolution = eval;
                }
                return;
            }

            // Pruning 1: Max Utilization lower bound check
            if (_bestSolution != null)
            {
                double currentMaxU = 0;
                for (int i = 0; i < _nodeCount; i++)
                {
                    double u = (double)_currentLoads[i] / _nodes[i].Capacity;
                    if (u > currentMaxU) currentMaxU = u;
                }

                if (currentMaxU > _bestSolution.MaxUtilization + 1e-15)
                {
                    return;
                }

                // Pruning 2: Movement lower bound check
                if (Math.Abs(currentMaxU - _bestSolution.MaxUtilization) < 1e-15)
                {
                    long minBytes = _currentMovedBytes + _minRemBytes[depth];
                    if (minBytes > _bestSolution.MovedBytes)
                    {
                        return;
                    }
                    if (minBytes == _bestSolution.MovedBytes)
                    {
                        int minReplicas = _currentMovedReplicas + _minRemReplicas[depth];
                        if (minReplicas > _bestSolution.MovedReplicaCount)
                        {
                            return;
                        }
                    }
                }
            }

            // Pruning 3: Memoization
            var memoKey = new LoadVectorKey(_currentLoads);
            if (_memo[depth].TryGetValue(memoKey, out var prevCost))
            {
                if (_currentMovedBytes > prevCost.Bytes ||
                    (_currentMovedBytes == prevCost.Bytes && _currentMovedReplicas >= prevCost.Replicas))
                {
                    return;
                }
            }
            _memo[depth][memoKey] = (_currentMovedBytes, _currentMovedReplicas);

            // Explore candidates
            long shardSize = _shards[depth].Size;
            var cands = _candidates[depth];

            for (int k = 0; k < cands.Length; k++)
            {
                var cand = cands[k];

                // Capacity check
                bool overload = false;
                foreach (int nIdx in cand.NodeIndices)
                {
                    if (_currentLoads[nIdx] + shardSize > _nodes[nIdx].Capacity)
                    {
                        overload = true;
                        break;
                    }
                }
                if (overload) continue;

                // Make choice
                _choice[depth] = k;
                foreach (int nIdx in cand.NodeIndices) _currentLoads[nIdx] += shardSize;
                _currentMovedBytes += cand.MovedBytes;
                _currentMovedReplicas += cand.MovedReplicas;

                Dfs(depth + 1);

                // Backtrack
                foreach (int nIdx in cand.NodeIndices) _currentLoads[nIdx] -= shardSize;
                _currentMovedBytes -= cand.MovedBytes;
                _currentMovedReplicas -= cand.MovedReplicas;

                if (_nodeVisitCount > NodeVisitLimit) break;
            }
        }
    }

    private readonly struct LoadVectorKey : IEquatable<LoadVectorKey>
    {
        private readonly long[] _loads;
        private readonly int _hashCode;

        public LoadVectorKey(long[] loads)
        {
            _loads = (long[])loads.Clone();
            var hc = new HashCode();
            foreach (long l in _loads) hc.Add(l);
            _hashCode = hc.ToHashCode();
        }

        public bool Equals(LoadVectorKey other)
        {
            if (_loads.Length != other._loads.Length) return false;
            for (int i = 0; i < _loads.Length; i++)
            {
                if (_loads[i] != other._loads[i]) return false;
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is LoadVectorKey other && Equals(other);
        public override int GetHashCode() => _hashCode;
    }
}
