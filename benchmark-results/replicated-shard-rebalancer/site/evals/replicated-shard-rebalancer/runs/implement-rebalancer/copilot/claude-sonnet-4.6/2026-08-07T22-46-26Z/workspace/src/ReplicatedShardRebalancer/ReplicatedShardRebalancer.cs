namespace ReplicatedShardRebalancer;

public sealed class ReplicatedShardRebalancer
{
    public RebalanceResult Rebalance(RebalanceProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var nodeMap = problem.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        // Ordinal-sorted node and shard ID lists for determinism
        var sortedNodeIds = problem.Nodes.Select(n => n.Id)
            .OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var sortedShards = problem.Shards
            .OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();

        var exclusions = new HashSet<(string ShardId, string NodeId)>(
            (problem.Exclusions ?? []).Select(e => (e.ShardId, e.NodeId)));

        var currentMap = (problem.CurrentPlacements ?? []).ToDictionary(
            p => p.ShardId,
            p => new HashSet<string>(p.NodeIds ?? [], StringComparer.Ordinal),
            StringComparer.Ordinal);

        // Track node loads as we assign shards
        var loads = sortedNodeIds.ToDictionary(id => id, _ => 0L, StringComparer.Ordinal);

        // Process shards largest-first so the hardest-to-place shards get first pick
        var processOrder = sortedShards
            .OrderByDescending(s => s.Size)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToArray();

        var placementMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var shard in processOrder)
        {
            var eligible = sortedNodeIds
                .Where(id => nodeMap[id].Capacity >= shard.Size
                             && !exclusions.Contains((shard.Id, id)))
                .ToArray();

            int requiredZones = RebalanceValidator.MaximumZoneDiversity(
                shard, nodeMap.Values, exclusions);

            var currentNodes = currentMap.GetValueOrDefault(shard.Id)
                               ?? new HashSet<string>(StringComparer.Ordinal);

            string[]? bestCombo = null;
            double bestMaxUtil = double.MaxValue;
            double bestSpread = double.MaxValue;
            long bestMovedBytes = long.MaxValue;
            int bestMovedCount = int.MaxValue;

            foreach (var combo in Combinations(eligible, shard.ReplicationFactor))
            {
                // Zone diversity check
                int zones = combo.Select(id => nodeMap[id].Zone)
                    .Distinct(StringComparer.Ordinal).Count();
                if (zones < requiredZones) continue;

                // Compute utilization metrics across all nodes
                double maxUtil = 0, minUtil = double.MaxValue;
                foreach (var id in sortedNodeIds)
                {
                    bool inCombo = Array.IndexOf(combo, id) >= 0;
                    double util = (double)(loads[id] + (inCombo ? shard.Size : 0))
                                  / nodeMap[id].Capacity;
                    if (util > maxUtil) maxUtil = util;
                    if (util < minUtil) minUtil = util;
                }
                double spread = maxUtil - minUtil;

                long movedBytes = 0;
                int movedCount = 0;
                foreach (var id in combo)
                {
                    if (!currentNodes.Contains(id))
                    {
                        movedBytes += shard.Size;
                        movedCount++;
                    }
                }

                // Lex compare: (maxUtil, spread, movedBytes, movedCount, combo)
                int cmp = CompareObjective(
                    maxUtil, spread, movedBytes, movedCount, combo,
                    bestMaxUtil, bestSpread, bestMovedBytes, bestMovedCount, bestCombo);

                if (cmp < 0)
                {
                    bestCombo = combo;
                    bestMaxUtil = maxUtil;
                    bestSpread = spread;
                    bestMovedBytes = movedBytes;
                    bestMovedCount = movedCount;
                }
            }

            // bestCombo should always be non-null for valid problems
            if (bestCombo is not null)
            {
                foreach (var id in bestCombo)
                    loads[id] += shard.Size;

                var sortedNodeList = bestCombo
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
                placementMap[shard.Id] = sortedNodeList;
            }
        }

        var targetPlacements = sortedShards
            .Select(s => new ShardPlacement(s.Id, placementMap[s.Id]))
            .ToList();

        return new RebalanceResult(targetPlacements);
    }

    private static int CompareObjective(
        double maxUtil, double spread, long movedBytes, int movedCount, string[] combo,
        double bMaxUtil, double bSpread, long bMovedBytes, int bMovedCount, string[]? bCombo)
    {
        if (bCombo is null) return -1;
        int c = maxUtil.CompareTo(bMaxUtil);
        if (c != 0) return c;
        c = spread.CompareTo(bSpread);
        if (c != 0) return c;
        c = movedBytes.CompareTo(bMovedBytes);
        if (c != 0) return c;
        c = movedCount.CompareTo(bMovedCount);
        if (c != 0) return c;
        // Lex compare combo arrays (already in sorted order from eligible)
        int len = Math.Min(combo.Length, bCombo.Length);
        for (int i = 0; i < len; i++)
        {
            c = StringComparer.Ordinal.Compare(combo[i], bCombo[i]);
            if (c != 0) return c;
        }
        return combo.Length.CompareTo(bCombo.Length);
    }

    /// <summary>
    /// Yields all k-element subsets of <paramref name="items"/> in lexicographic order.
    /// Items must already be sorted; each yielded array is a new instance.
    /// </summary>
    private static IEnumerable<string[]> Combinations(string[] items, int k)
    {
        if (k == 0) { yield return []; yield break; }
        if (k > items.Length) yield break;

        var buf = new string[k];
        var indices = new int[k];
        for (int i = 0; i < k; i++) indices[i] = i;

        while (true)
        {
            for (int i = 0; i < k; i++) buf[i] = items[indices[i]];
            yield return (string[])buf.Clone();

            // Advance to next combination
            int pos = k - 1;
            while (pos >= 0 && indices[pos] == items.Length - k + pos) pos--;
            if (pos < 0) break;
            indices[pos]++;
            for (int i = pos + 1; i < k; i++) indices[i] = indices[i - 1] + 1;
        }
    }
}
