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

        if (shards.Length == 0)
        {
            return new RebalanceResult([]);
        }

        var exclusions = new HashSet<(string ShardId, string NodeId)>();
        foreach (var exclusion in problem.Exclusions ?? [])
        {
            exclusions.Add((exclusion.ShardId, exclusion.NodeId));
        }

        var current = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var placement in problem.CurrentPlacements ?? [])
        {
            current[placement.ShardId] = new HashSet<string>(
                placement.NodeIds ?? [],
                StringComparer.Ordinal);
        }

        var candidates = new int[shards.Length][][];
        var moveBytes = new long[shards.Length][];
        var moveCounts = new int[shards.Length][];

        for (var s = 0; s < shards.Length; s++)
        {
            var shard = shards[s];
            var eligible = new List<int>();
            for (var n = 0; n < nodes.Length; n++)
            {
                var node = nodes[n];
                if (node.Capacity >= shard.Size
                    && !exclusions.Contains((shard.Id, node.Id)))
                {
                    eligible.Add(n);
                }
            }

            var requiredZones = Math.Min(
                shard.ReplicationFactor,
                eligible.Select(i => nodes[i].Zone)
                    .Distinct(StringComparer.Ordinal)
                    .Count());

            var combos = new List<int[]>();
            var buffer = new int[shard.ReplicationFactor];
            void Recurse(int offset, int chosen)
            {
                if (chosen == shard.ReplicationFactor)
                {
                    var zones = new HashSet<string>(StringComparer.Ordinal);
                    for (var i = 0; i < buffer.Length; i++)
                    {
                        zones.Add(nodes[buffer[i]].Zone);
                    }

                    if (zones.Count == requiredZones)
                    {
                        combos.Add((int[])buffer.Clone());
                    }

                    return;
                }

                for (var i = offset; i < eligible.Count; i++)
                {
                    buffer[chosen] = eligible[i];
                    Recurse(i + 1, chosen + 1);
                }
            }

            Recurse(0, 0);

            candidates[s] = combos.ToArray();
            moveBytes[s] = new long[combos.Count];
            moveCounts[s] = new int[combos.Count];
            current.TryGetValue(shard.Id, out var cur);
            cur ??= new HashSet<string>(StringComparer.Ordinal);

            for (var c = 0; c < combos.Count; c++)
            {
                long bytes = 0;
                var count = 0;
                foreach (var nodeIdx in combos[c])
                {
                    if (!cur.Contains(nodes[nodeIdx].Id))
                    {
                        bytes += shard.Size;
                        count++;
                    }
                }

                moveBytes[s][c] = bytes;
                moveCounts[s][c] = count;
            }
        }

        var sizes = shards.Select(shard => shard.Size).ToArray();
        var capacities = nodes.Select(node => node.Capacity).ToArray();
        var loads = new long[nodes.Length];
        var choice = new int[shards.Length];
        var bestChoice = new int[shards.Length];
        Array.Fill(bestChoice, -1);

        // Lex objective: min maxUtil, max minUtil (= min spread at fixed max),
        // min movedBytes, min movedReplicas; then ordinal-smallest placement.
        double bestMaxUtil = double.PositiveInfinity;
        double bestMinUtil = double.NegativeInfinity;
        long bestMovedBytes = long.MaxValue;
        var bestMovedReplicas = int.MaxValue;
        var found = false;

        void ConsiderComplete(long movedB, int movedR)
        {
            double maxUtil = 0;
            double minUtil = double.PositiveInfinity;
            for (var i = 0; i < loads.Length; i++)
            {
                var util = capacities[i] > 0 ? (double)loads[i] / capacities[i] : 0;
                if (util > maxUtil)
                {
                    maxUtil = util;
                }

                if (util < minUtil)
                {
                    minUtil = util;
                }
            }

            if (loads.Length == 0)
            {
                minUtil = 0;
            }

            var better = !found
                || maxUtil < bestMaxUtil - 1e-15
                || (Math.Abs(maxUtil - bestMaxUtil) <= 1e-15
                    && (minUtil > bestMinUtil + 1e-15
                        || (Math.Abs(minUtil - bestMinUtil) <= 1e-15
                            && (movedB < bestMovedBytes
                                || (movedB == bestMovedBytes
                                    && movedR < bestMovedReplicas)))));

            // Ordered DFS yields the first (ordinally smallest) placement for a
            // given metric vector; only replace on strictly better metrics.
            if (!better)
            {
                return;
            }

            found = true;
            bestMaxUtil = maxUtil;
            bestMinUtil = minUtil;
            bestMovedBytes = movedB;
            bestMovedReplicas = movedR;
            Array.Copy(choice, bestChoice, choice.Length);
        }

        void Search(int shardIdx, long movedB, int movedR)
        {
            if (shardIdx == shards.Length)
            {
                ConsiderComplete(movedB, movedR);
                return;
            }

            var combos = candidates[shardIdx];
            var size = sizes[shardIdx];

            if (found)
            {
                double curMax = 0;
                double curMin = double.PositiveInfinity;
                for (var i = 0; i < loads.Length; i++)
                {
                    var util = capacities[i] > 0 ? (double)loads[i] / capacities[i] : 0;
                    if (util > curMax)
                    {
                        curMax = util;
                    }

                    if (util < curMin)
                    {
                        curMin = util;
                    }
                }

                if (curMax > bestMaxUtil + 1e-15)
                {
                    return;
                }

                // Final min util >= curMin. If already at best max/min and move
                // cost cannot improve, prune.
                if (Math.Abs(curMax - bestMaxUtil) <= 1e-15
                    && curMin + 1e-15 >= bestMinUtil
                    && movedB > bestMovedBytes)
                {
                    return;
                }

                if (Math.Abs(curMax - bestMaxUtil) <= 1e-15
                    && curMin + 1e-15 >= bestMinUtil
                    && movedB == bestMovedBytes
                    && movedR >= bestMovedReplicas)
                {
                    return;
                }
            }

            for (var c = 0; c < combos.Length; c++)
            {
                var combo = combos[c];
                var ok = true;
                for (var i = 0; i < combo.Length; i++)
                {
                    var n = combo[i];
                    if (loads[n] + size > capacities[n])
                    {
                        ok = false;
                        break;
                    }
                }

                if (!ok)
                {
                    continue;
                }

                if (found)
                {
                    double branchMax = 0;
                    for (var i = 0; i < loads.Length; i++)
                    {
                        var load = loads[i];
                        for (var j = 0; j < combo.Length; j++)
                        {
                            if (combo[j] == i)
                            {
                                load += size;
                                break;
                            }
                        }

                        var util = capacities[i] > 0 ? (double)load / capacities[i] : 0;
                        if (util > branchMax)
                        {
                            branchMax = util;
                        }
                    }

                    if (branchMax > bestMaxUtil + 1e-15)
                    {
                        continue;
                    }
                }

                for (var i = 0; i < combo.Length; i++)
                {
                    loads[combo[i]] += size;
                }

                choice[shardIdx] = c;
                Search(
                    shardIdx + 1,
                    movedB + moveBytes[shardIdx][c],
                    movedR + moveCounts[shardIdx][c]);

                for (var i = 0; i < combo.Length; i++)
                {
                    loads[combo[i]] -= size;
                }
            }
        }

        Search(0, 0, 0);

        if (!found)
        {
            return RebalanceResult.Empty;
        }

        var targets = new List<ShardPlacement>(shards.Length);
        for (var s = 0; s < shards.Length; s++)
        {
            var combo = candidates[s][bestChoice[s]];
            var nodeIds = combo.Select(i => nodes[i].Id).ToList();
            targets.Add(new ShardPlacement(shards[s].Id, nodeIds));
        }

        return new RebalanceResult(targets);
    }
}
