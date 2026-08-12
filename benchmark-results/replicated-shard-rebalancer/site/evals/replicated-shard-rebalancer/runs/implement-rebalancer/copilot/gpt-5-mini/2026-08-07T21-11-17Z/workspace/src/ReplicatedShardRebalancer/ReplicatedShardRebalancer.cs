namespace ReplicatedShardRebalancer;

public sealed class ReplicatedShardRebalancer
{
    public RebalanceResult Rebalance(RebalanceProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        // Prepare canonical ordering and quick lookups.
        var nodes = (problem.Nodes ?? new List<NodeSpec>())
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .ToList();
        var nodeById = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        var shards = (problem.Shards ?? new List<ShardSpec>())
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();

        var current = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var placement in problem.CurrentPlacements ?? new List<ShardPlacement>())
        {
            current[placement.ShardId] = new HashSet<string>(placement.NodeIds ?? [], StringComparer.Ordinal);
        }

        var exclusions = new HashSet<(string ShardId, string NodeId)>();
        foreach (var ex in problem.Exclusions ?? new List<PlacementExclusion>())
        {
            exclusions.Add((ex.ShardId, ex.NodeId));
        }

        // Precompute eligible nodes and candidate combos per shard.
        var eligibleNodesPerShard = new List<List<NodeSpec>>();
        var requiredZonesPerShard = new List<int>();
        var combosPerShard = new List<List<List<string>>>();
        var minMovedBytesPerShard = new List<long>();
        var minMovedReplicasPerShard = new List<int>();

        foreach (var shard in shards)
        {
            var eligible = nodes
                .Where(n => n.Capacity >= shard.Size && !exclusions.Contains((shard.Id, n.Id)))
                .ToList();
            eligibleNodesPerShard.Add(eligible);

            var requiredZones = RebalanceValidator.MaximumZoneDiversity(shard, nodes, exclusions);
            requiredZonesPerShard.Add(requiredZones);

            var nodeIds = eligible.Select(n => n.Id).ToList();
            var combos = new List<List<string>>();
            // generate combinations of replicationFactor from nodeIds in lexicographic order
            GenerateCombinations(nodeIds, shard.ReplicationFactor, combo =>
            {
                // check zone diversity requirement
                var usedZones = combo.Select(id => nodeById[id].Zone).Distinct(StringComparer.Ordinal).Count();
                if (usedZones == requiredZones)
                {
                    combos.Add(combo.ToList());
                }
            });

            // each combo's nodeIds should already be ordered since nodeIds input is ordered
            combosPerShard.Add(combos);

            // compute minimal moved bytes/replicas for optimistic pruning
            long minMovedBytes = long.MaxValue;
            int minMovedReplicas = int.MaxValue;
            foreach (var c in combos)
            {
                var moved = c.Count(id => !current.GetValueOrDefault(shard.Id, new HashSet<string>()).Contains(id));
                minMovedBytes = Math.Min(minMovedBytes, moved * shard.Size);
                minMovedReplicas = Math.Min(minMovedReplicas, moved);
            }
            if (combos.Count == 0)
            {
                // No eligible combos -> problem is infeasible. Return empty to let validator catch it.
                return RebalanceResult.Empty;
            }
            minMovedBytesPerShard.Add(minMovedBytes == long.MaxValue ? 0 : minMovedBytes);
            minMovedReplicasPerShard.Add(minMovedReplicas == int.MaxValue ? 0 : minMovedReplicas);
        }

        // Precompute totals for lower bounds
        long totalCapacity = nodes.Sum(n => n.Capacity);
        long totalRemainingBytesAll = 0;
        for (int i = 0; i < shards.Count; i++)
        {
            totalRemainingBytesAll += shards[i].Size * shards[i].ReplicationFactor;
        }

        // Greedy seed solution to get an initial best for pruning.
        RebalanceResult? bestResult = null;
        (double maxUtil, double spread, long movedBytes, int movedReplicas) bestMetrics = (double.MaxValue, double.MaxValue, long.MaxValue, int.MaxValue);

        // Helper: evaluate full placement
        static (double maxUtil, double spread, long movedBytes, int movedReplicas) EvaluatePlacement(
            IReadOnlyDictionary<string, NodeSpec> nodeById,
            IReadOnlyList<NodeSpec> nodesOrdered,
            List<ShardSpec> shardsOrdered,
            Dictionary<string, HashSet<string>> current,
            List<ShardPlacement> placement)
        {
            var loads = nodesOrdered.ToDictionary(n => n.Id, _ => 0L, StringComparer.Ordinal);
            long movedB = 0;
            int movedR = 0;
            for (int i = 0; i < shardsOrdered.Count; i++)
            {
                var shard = shardsOrdered[i];
                var p = placement[i];
                foreach (var nid in p.NodeIds)
                {
                    loads[nid] = checked(loads[nid] + shard.Size);
                    if (!current.GetValueOrDefault(shard.Id, new HashSet<string>()).Contains(nid))
                    {
                        movedB = checked(movedB + shard.Size);
                        movedR++;
                    }
                }
            }
            var utils = nodesOrdered.Select(n => n.Capacity > 0 ? (double)loads[n.Id] / n.Capacity : 0.0).ToArray();
            var maxU = utils.Length == 0 ? 0.0 : utils.Max();
            var minU = utils.Length == 0 ? 0.0 : utils.Min();
            return (maxU, maxU - minU, movedB, movedR);
        }

        // Build greedy by assigning each shard choosing best local combo.
        {
            var loads = nodes.ToDictionary(n => n.Id, _ => 0L, StringComparer.Ordinal);
            var placements = new List<ShardPlacement>();
            long movedB = 0;
            int movedR = 0;
            for (int si = 0; si < shards.Count; si++)
            {
                var shard = shards[si];
                List<string>? bestLocal = null;
                (double maxU, double spread, long mB, int mR) bestLocalMetrics = (double.MaxValue, double.MaxValue, long.MaxValue, int.MaxValue);
                foreach (var combo in combosPerShard[si])
                {
                    // capacity feasibility
                    bool ok = true;
                    foreach (var nid in combo)
                    {
                        if (loads[nid] + shard.Size > nodeById[nid].Capacity) { ok = false; break; }
                    }
                    if (!ok) continue;

                    // simulate
                    foreach (var nid in combo) loads[nid] += shard.Size;
                    var utils = nodes.Select(n => n.Capacity > 0 ? (double)loads[n.Id] / n.Capacity : 0.0).ToArray();
                    var maxU = utils.Length == 0 ? 0.0 : utils.Max();
                    var minU = utils.Length == 0 ? 0.0 : utils.Min();
                    long addMovedB = combo.Count(id => !current.GetValueOrDefault(shard.Id, new HashSet<string>()).Contains(id)) * shard.Size;
                    int addMovedR = combo.Count(id => !current.GetValueOrDefault(shard.Id, new HashSet<string>()).Contains(id));
                    var cand = (maxU, maxU - minU, movedB + addMovedB, movedR + addMovedR);
                    // compare lexicographically
                    if (CompareObjective(cand, bestLocalMetrics, out var cmp) < 0
                        || (cmp == 0 && IsLexicographicallySmallerPartial(placements, combo, shards, si, nodeById)))
                    {
                        bestLocal = combo.ToList();
                        bestLocalMetrics = cand;
                    }
                    // rollback simulate
                    foreach (var nid in combo) loads[nid] -= shard.Size;
                }
                if (bestLocal is null)
                {
                    // pick any combo (shouldn't happen for feasible inputs), choose first
                    bestLocal = combosPerShard[si][0];
                }
                // apply bestLocal to loads and placements
                foreach (var nid in bestLocal) loads[nid] = checked(loads[nid] + shard.Size);
                placements.Add(new ShardPlacement(shard.Id, bestLocal));
                movedB = checked(movedB + bestLocal.Count(id => !current.GetValueOrDefault(shard.Id, new HashSet<string>()).Contains(id)) * shard.Size);
                movedR = checked(movedR + bestLocal.Count(id => !current.GetValueOrDefault(shard.Id, new HashSet<string>()).Contains(id)));
            }
            var metrics = EvaluatePlacement(nodeById, nodes, shards, current, placements);
            bestResult = new RebalanceResult(placements);
            bestMetrics = metrics;
        }

        // Prepare DFS exact search with branch-and-bound.
        var bestPlacement = bestResult?.TargetPlacements.Select(p => new ShardPlacement(p.ShardId, p.NodeIds.ToList())).ToList();

        var loadsDfs = nodes.ToDictionary(n => n.Id, _ => 0L, StringComparer.Ordinal);
        long movedBytesSoFar = 0;
        int movedReplicasSoFar = 0;
        var currentPlacements = new List<ShardPlacement>(shards.Count);
        
        void DfsAssign(int si)
        {
            if (si == shards.Count)
            {                // evaluate full placement
                var metrics = EvaluatePlacement(nodeById, nodes, shards, current, currentPlacements);
                var candidate = metrics;
                if (CompareObjective(candidate, bestMetrics, out var cmp) < 0 || (cmp == 0 && IsPlacementLexicographicallySmaller(currentPlacements, bestPlacement, StringComparer.Ordinal)))
                {                    bestMetrics = candidate;
                    bestPlacement = currentPlacements.Select(p => new ShardPlacement(p.ShardId, p.NodeIds.ToList())).ToList();
                }
                return;
            }
            var shard = shards[si];
            var combos = combosPerShard[si];
            // iterate combos in lexicographic order to favor canonical tie-breaking            
            foreach (var combo in combos)
            {                // capacity check                bool ok = true;
                foreach (var nid in combo)
                {                    if (loadsDfs[nid] + shard.Size > nodeById[nid].Capacity) { ok = false; break; }
                }
                if (!ok) continue;
                // apply combo                foreach (var nid in combo) loadsDfs[nid] = checked(loadsDfs[nid] + shard.Size);
                long addedMovedBytes = combo.Count(id => !current.GetValueOrDefault(shard.Id, new HashSet<string>()).Contains(id)) * shard.Size;
                int addedMovedReplicas = combo.Count(id => !current.GetValueOrDefault(shard.Id, new HashSet<string>()).Contains(id));
                movedBytesSoFar = checked(movedBytesSoFar + addedMovedBytes);
                movedReplicasSoFar = checked(movedReplicasSoFar + addedMovedReplicas);
                currentPlacements.Add(new ShardPlacement(shard.Id, combo.ToList()));
                // compute optimistic lower bounds for pruning                var currentLoads = loadsDfs.Values.ToArray();
                var utilsNow = nodes.Select(n => n.Capacity > 0 ? (double)loadsDfs[n.Id] / n.Capacity : 0.0).ToArray();
                double currMaxUtil = utilsNow.Length == 0 ? 0.0 : utilsNow.Max();
                double lbMaxUtilFromAverage = (loadsDfs.Values.Sum() + (TotalRemainingBytesFromIndex(si + 1, shards) )) / (double)totalCapacity;
                double lbMaxUtil = Math.Max(currMaxUtil, lbMaxUtilFromAverage);
                long lbMovedBytes = movedBytesSoFar + MinSum(minMovedBytesPerShard, si + 1);                int lbMovedReplicas = movedReplicasSoFar + MinSumInt(minMovedReplicasPerShard, si + 1);
                // build lower-bound vector                var lbVector = (lbMaxUtil, 0.0, lbMovedBytes, lbMovedReplicas);
                if (!(bestPlacement is null))                {                    if (CompareObjective(lbVector, bestMetrics, out var cmp2) > 0)                    {                        // prune: optimistic LB worse than best                        // rollback and continue                        currentPlacements.RemoveAt(currentPlacements.Count - 1);                        foreach (var nid in combo) loadsDfs[nid] = checked(loadsDfs[nid] - shard.Size);                        movedBytesSoFar = checked(movedBytesSoFar - addedMovedBytes);                        movedReplicasSoFar = checked(movedReplicasSoFar - addedMovedReplicas);                        continue;                    }                }                // recurse                DfsAssign(si + 1);                // rollback                currentPlacements.RemoveAt(currentPlacements.Count - 1);                foreach (var nid in combo) loadsDfs[nid] = checked(loadsDfs[nid] - shard.Size);                movedBytesSoFar = checked(movedBytesSoFar - addedMovedBytes);                movedReplicasSoFar = checked(movedReplicasSoFar - addedMovedReplicas);            }        }
        // helper to compute remaining bytes total from shard index        long TotalRemainingBytesFromIndex(int startIndex, List<ShardSpec> shardsList)        {            long sum = 0; for (int i = startIndex; i < shardsList.Count; i++) sum += shardsList[i].Size * shardsList[i].ReplicationFactor; return sum;        }        static long MinSum(List<long> arr, int startIndex)        {            long s = 0; for (int i = startIndex; i < arr.Count; i++) s = checked(s + arr[i]); return s;        }        static int MinSumInt(List<int> arr, int startIndex)        {            int s = 0; for (int i = startIndex; i < arr.Count; i++) s = checked(s + arr[i]); return s;        }        // Compare lexicographic objective tuples: (maxUtil, spread, movedBytes, movedReplicas)        static int CompareObjective((double maxUtil, double spread, long movedBytes, int movedReplicas) a,            (double maxUtil, double spread, long movedBytes, int movedReplicas) b,            out int cmp)        {            const double eps = 1e-12;            if (Math.Abs(a.maxUtil - b.maxUtil) > eps) { cmp = a.maxUtil < b.maxUtil ? -1 : 1; return cmp; }            if (Math.Abs(a.spread - b.spread) > eps) { cmp = a.spread < b.spread ? -1 : 1; return cmp; }            if (a.movedBytes != b.movedBytes) { cmp = a.movedBytes < b.movedBytes ? -1 : 1; return cmp; }            if (a.movedReplicas != b.movedReplicas) { cmp = a.movedReplicas < b.movedReplicas ? -1 : 1; return cmp; }            cmp = 0; return 0;        }        // overload for comparing optimistic lb vector where spread is 0.0 but the signature wants the same types        static int CompareObjectiveLb((double maxUtil, double spread, long movedBytes, int movedReplicas) lb,            (double maxUtil, double spread, long movedBytes, int movedReplicas) best,            out int cmp)        {            return CompareObjective(lb, best, out cmp);        }        // Check whether partial placements + current combo is lexicographically smaller than best local (used in greedy tie-breaker)        static bool IsLexicographicallySmallerPartial(List<ShardPlacement> prefix, List<string> candidateCombo, List<ShardSpec> shards, int si, IReadOnlyDictionary<string, NodeSpec> nodeById)        {            // compare as if prefix followed by candidateCombo and then minimal remaining combos (we only need deterministic tie-break for greedy)            // For simplicity, compare prefix+candidateCombo to prefix+bestCandidate implicitly by string ordering of candidateCombo            return true; // returning true biases toward earlier combos; acceptable for seeding        }        static bool IsPlacementLexicographicallySmaller(List<ShardPlacement> a, List<ShardPlacement>? b, StringComparer comparer)        {            if (b is null) return true;            for (int i = 0; i < a.Count && i < b.Count; i++)            {                var aa = a[i].ShardId.CompareTo(b[i].ShardId);                if (aa != 0) return aa < 0;                var listA = a[i].NodeIds;                var listB = b[i].NodeIds;                for (int j = 0; j < listA.Count && j < listB.Count; j++)                {                    int c = StringComparer.Ordinal.Compare(listA[j], listB[j]);                    if (c != 0) return c < 0;                }                if (listA.Count != listB.Count) return listA.Count < listB.Count;            }            return a.Count < (b?.Count ?? 0);        }        // Start DFS        DfsAssign(0);        // If no placement found, return empty to let validator fail.        if (bestPlacement is null)        {            return RebalanceResult.Empty;        }        // Ensure canonical ordering (should already be ordered)        var finalPlacements = bestPlacement.Select(p => new ShardPlacement(p.ShardId, p.NodeIds.OrderBy(id => id, StringComparer.Ordinal).ToList())).OrderBy(p => p.ShardId, StringComparer.Ordinal).ToList();        return new RebalanceResult(finalPlacements);    }

    private static void GenerateCombinations<T>(List<T> items, int k, Action<IEnumerable<T>> handle)    {        if (k == 0) { handle(Array.Empty<T>()); return; }        var n = items.Count;        if (k > n) return;        var indices = Enumerable.Range(0, k).ToArray();        while (true)        {            handle(indices.Select(i => items[i]));            int i = k - 1;            while (i >= 0 && indices[i] == i + n - k) i--;            if (i < 0) break;            indices[i]++;            for (int j = i + 1; j < k; j++) indices[j] = indices[j - 1] + 1;        }    }}
