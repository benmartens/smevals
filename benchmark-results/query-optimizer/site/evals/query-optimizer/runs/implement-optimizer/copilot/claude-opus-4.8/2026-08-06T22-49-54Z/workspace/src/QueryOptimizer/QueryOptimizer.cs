using System.Numerics;

namespace QueryPlanning;

public sealed class QueryOptimizer
{
    private const long CostCap = 9_000_000_000_000_000;
    private const int DpMaxTables = 20;

    public QueryPlan Optimize(QueryProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        // Deterministic, de-duplicated table list ordered by ordinal id. Bit
        // index order therefore matches ordinal order, which the canonical
        // child-ordering rule (smallest table id on the left) relies on.
        var tables = problem.Tables
            .Where(t => t is not null
                && !string.IsNullOrWhiteSpace(t.Id)
                && t.Rows > 0
                && t.ScanCostPerRow > 0)
            .GroupBy(t => t.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .ToList();

        int n = tables.Count;
        if (n == 0)
        {
            return QueryPlan.Empty;
        }

        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            indexOf[tables[i].Id] = i;
        }

        var neighbors = new HashSet<int>[n];
        for (int i = 0; i < n; i++)
        {
            neighbors[i] = new HashSet<int>();
        }
        foreach (var join in problem.Joins)
        {
            if (join is null || join.LeftTable == join.RightTable)
            {
                continue;
            }
            if (!indexOf.TryGetValue(join.LeftTable, out var a)
                || !indexOf.TryGetValue(join.RightTable, out var b))
            {
                continue;
            }
            neighbors[a].Add(b);
            neighbors[b].Add(a);
        }

        if (n == 1)
        {
            return new QueryPlan(BestLeaf(problem, tables[0]).Node);
        }

        if (n <= DpMaxTables)
        {
            var dp = SubsetDp(problem, tables, neighbors);
            if (dp is not null)
            {
                return new QueryPlan(dp);
            }
        }

        return new QueryPlan(GreedyPlan(problem, tables, neighbors));
    }

    private static PlanNode? SubsetDp(
        QueryProblem problem,
        List<TableSpec> tables,
        HashSet<int>[] neighbors)
    {
        int n = tables.Count;
        int size = 1 << n;
        var adj = new int[n];
        for (int i = 0; i < n; i++)
        {
            foreach (var j in neighbors[i])
            {
                adj[i] |= 1 << j;
            }
        }

        var cost = new long[size];
        var nodes = new PlanNode?[size];
        var rows = new long[size];
        var connected = new sbyte[size];

        for (int i = 0; i < n; i++)
        {
            int m = 1 << i;
            var (node, leafCost) = BestLeaf(problem, tables[i]);
            nodes[m] = node;
            cost[m] = leafCost;
            rows[m] = CostModel.EstimateFilteredRows(problem, tables[i]);
            connected[m] = 1;
        }

        for (int mask = 1; mask < size; mask++)
        {
            if (BitOperations.PopCount((uint)mask) < 2)
            {
                continue;
            }
            if (!IsConnected(mask, adj, connected))
            {
                continue;
            }

            long card = EstimateRows(problem, tables, mask);
            rows[mask] = card;

            long best = long.MaxValue;
            PlanNode? bestNode = null;
            int low = mask & (-mask);

            for (int left = (mask - 1) & mask; left > 0; left = (left - 1) & mask)
            {
                if ((left & low) == 0)
                {
                    continue; // Left subtree must contain the smallest table id.
                }
                int right = mask ^ left;
                if (nodes[left] is not { } leftNode || nodes[right] is not { } rightNode)
                {
                    continue; // A side is not a connected (plannable) subset.
                }

                long leftRows = rows[left];
                long rightRows = rows[right];
                long baseCost = SaturatingAdd(cost[left], cost[right]);

                foreach (var (op, localCost) in JoinCandidates(
                    leftRows, rightRows, card, problem.MemoryLimitRows))
                {
                    long total = SaturatingAdd(baseCost, localCost);
                    if (total < best)
                    {
                        best = total;
                        bestNode = new PlanNode(op, Left: leftNode, Right: rightNode);
                    }
                }
            }

            if (bestNode is not null)
            {
                cost[mask] = best;
                nodes[mask] = bestNode;
            }
        }

        return nodes[size - 1];
    }

    private static PlanNode? GreedyPlan(
        QueryProblem problem,
        List<TableSpec> tables,
        HashSet<int>[] neighbors)
    {
        int n = tables.Count;
        var inPlan = new bool[n];
        var planMask = new HashSet<int> { 0 };
        inPlan[0] = true;
        var current = BestLeaf(problem, tables[0]).Node;
        long currentRows = CostModel.EstimateFilteredRows(problem, tables[0]);

        for (int step = 1; step < n; step++)
        {
            int chosen = -1;
            string chosenOp = "nestedLoop";
            long chosenIncremental = long.MaxValue;
            long chosenCard = 0;

            for (int i = 0; i < n; i++)
            {
                if (inPlan[i])
                {
                    continue;
                }
                bool adjacent = false;
                foreach (var member in planMask)
                {
                    if (neighbors[i].Contains(member))
                    {
                        adjacent = true;
                        break;
                    }
                }
                if (!adjacent)
                {
                    continue;
                }

                long leafCost = BestLeaf(problem, tables[i]).Cost;
                long card = EstimateRows(problem, tables, planMask, i);
                long leafRows = CostModel.EstimateFilteredRows(problem, tables[i]);

                foreach (var (op, localCost) in JoinCandidates(
                    currentRows, leafRows, card, problem.MemoryLimitRows))
                {
                    long incremental = SaturatingAdd(leafCost, localCost);
                    if (chosen == -1 || incremental < chosenIncremental)
                    {
                        chosen = i;
                        chosenOp = op;
                        chosenIncremental = incremental;
                        chosenCard = card;
                    }
                }
            }

            if (chosen == -1)
            {
                return null; // Disconnected join graph: no valid full plan.
            }

            current = new PlanNode(
                chosenOp,
                Left: current,
                Right: BestLeaf(problem, tables[chosen]).Node);
            currentRows = chosenCard;
            inPlan[chosen] = true;
            planMask.Add(chosen);
        }

        return current;
    }

    private static IEnumerable<(string Op, long LocalCost)> JoinCandidates(
        long leftRows,
        long rightRows,
        long card,
        int memoryLimitRows)
    {
        long inputRows = SaturatingAdd(leftRows, rightRows);

        yield return (
            "nestedLoop",
            SaturatingAdd(SaturatingMultiply(leftRows, rightRows), card));

        long buildRows = Math.Min(leftRows, rightRows);
        long spillRows = Math.Max(0L, buildRows - memoryLimitRows);
        if (spillRows == 0)
        {
            // Only offer a hash join when its build side fits in memory; a
            // spilling hash join is never preferred under this cost model.
            yield return (
                "hashJoin",
                SaturatingAdd(SaturatingMultiply(inputRows, 4), card));
        }

        yield return (
            "mergeJoin",
            SaturatingAdd(
                SortCost(leftRows),
                SortCost(rightRows),
                SaturatingMultiply(inputRows, 2),
                card));
    }

    private static (PlanNode Node, long Cost) BestLeaf(
        QueryProblem problem,
        TableSpec table)
    {
        long filtered = CostModel.EstimateFilteredRows(problem, table);

        PlanNode bestNode = new("tableScan", TableId: table.Id);
        long bestCost = SaturatingAdd(
            SaturatingMultiply(table.Rows, table.ScanCostPerRow),
            SaturatingMultiply(filtered, 2));

        var columns = table.Indexes
            .Where(i => !string.IsNullOrWhiteSpace(i.Column))
            .Select(i => i.Column)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal);

        foreach (var column in columns)
        {
            var index = table.Indexes.FirstOrDefault(i => i.Column == column);
            var predicate = problem.Predicates.FirstOrDefault(p =>
                p.TableId == table.Id && p.Column == column && p.Indexable);
            if (index is null || predicate is null)
            {
                continue;
            }

            long matched = ScaleCeiling(table.Rows, predicate.SelectivityPermille);
            long seekCost = SaturatingAdd(
                index.SeekStartupCost,
                SaturatingMultiply(matched, index.LookupCostPerRow),
                SaturatingMultiply(filtered, 2));

            if (seekCost < bestCost)
            {
                bestCost = seekCost;
                bestNode = new PlanNode(
                    "indexSeek",
                    TableId: table.Id,
                    IndexColumn: column);
            }
        }

        return (bestNode, bestCost);
    }

    private static long EstimateRows(
        QueryProblem problem,
        List<TableSpec> tables,
        int mask)
    {
        var ids = new List<string>();
        int m = mask;
        while (m != 0)
        {
            int bit = m & (-m);
            m ^= bit;
            ids.Add(tables[BitOperations.TrailingZeroCount(bit)].Id);
        }
        return CostModel.EstimateRows(problem, ids);
    }

    private static long EstimateRows(
        QueryProblem problem,
        List<TableSpec> tables,
        HashSet<int> current,
        int addition)
    {
        var ids = new List<string>(current.Count + 1);
        foreach (var i in current)
        {
            ids.Add(tables[i].Id);
        }
        ids.Add(tables[addition].Id);
        return CostModel.EstimateRows(problem, ids);
    }

    private static bool IsConnected(int mask, int[] adj, sbyte[] cache)
    {
        if (cache[mask] != 0)
        {
            return cache[mask] == 1;
        }

        int start = mask & (-mask);
        int seen = start;
        int frontier = start;
        while (frontier != 0)
        {
            int next = 0;
            int f = frontier;
            while (f != 0)
            {
                int bit = f & (-f);
                f ^= bit;
                next |= adj[BitOperations.TrailingZeroCount(bit)] & mask;
            }
            next &= ~seen;
            seen |= next;
            frontier = next;
        }

        bool result = seen == mask;
        cache[mask] = (sbyte)(result ? 1 : 2);
        return result;
    }

    private static long SortCost(long rows)
    {
        int levels = 0;
        long value = Math.Max(1, rows);
        while (value > 1)
        {
            value = (value + 1) / 2;
            levels++;
        }
        return SaturatingMultiply(rows, Math.Max(1, levels), 2);
    }

    private static long ScaleCeiling(long value, int permille) =>
        Math.Max(1, SaturatingAdd(SaturatingMultiply(value, permille), 999) / 1000);

    private static long SaturatingMultiply(params long[] values)
    {
        long result = 1;
        foreach (var value in values)
        {
            if (value == 0)
            {
                return 0;
            }
            if (result > CostCap / value)
            {
                return CostCap;
            }
            result *= value;
        }
        return Math.Min(CostCap, result);
    }

    private static long SaturatingAdd(params long[] values)
    {
        long result = 0;
        foreach (var value in values)
        {
            if (result >= CostCap - value)
            {
                return CostCap;
            }
            result += value;
        }
        return result;
    }
}

