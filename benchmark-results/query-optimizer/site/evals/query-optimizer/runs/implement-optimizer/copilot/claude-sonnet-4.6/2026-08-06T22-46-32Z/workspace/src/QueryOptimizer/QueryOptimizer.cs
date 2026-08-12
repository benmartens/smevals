namespace QueryPlanning;

public sealed class QueryOptimizer
{
    public QueryPlan Optimize(QueryProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var tables = problem.Tables
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .ToList();
        int n = tables.Count;
        if (n == 0) return QueryPlan.Empty;

        var tableIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
            tableIndex[tables[i].Id] = i;

        int totalMasks = 1 << n;
        var dp = new DpEntry?[totalMasks];

        // Base case: single-table access paths
        for (int i = 0; i < n; i++)
        {
            int mask = 1 << i;
            dp[mask] = BestLeafPlan(problem, tables[i]);
        }

        // DP over subsets of increasing size
        for (int size = 2; size <= n; size++)
        {
            for (int mask = 1; mask < totalMasks; mask++)
            {
                if (PopCount(mask) != size) continue;

                DpEntry? best = null;

                // Enumerate all non-empty proper subsets as left side
                for (int sub = (mask - 1) & mask; sub > 0; sub = (sub - 1) & mask)
                {
                    int leftMask = sub;
                    int rightMask = mask ^ sub;
                    if (rightMask == 0) break;
                    // Process each unordered pair once
                    if (leftMask > rightMask) continue;

                    if (!HasCrossingEdge(problem, leftMask, rightMask, tableIndex)) continue;

                    var leftEntry = dp[leftMask];
                    var rightEntry = dp[rightMask];
                    if (leftEntry is null || rightEntry is null) continue;

                    // Enforce canonical order: smaller minTableId on left
                    DpEntry canonLeft, canonRight;
                    if (StringComparer.Ordinal.Compare(leftEntry.MinTableId, rightEntry.MinTableId) < 0)
                    { canonLeft = leftEntry; canonRight = rightEntry; }
                    else
                    { canonLeft = rightEntry; canonRight = leftEntry; }

                    var allIds = GetTableIds(mask, tables);
                    long rows = CostModel.EstimateRows(problem, allIds);

                    foreach (var op in JoinOps)
                    {
                        long localCost = JoinLocalCost(op, canonLeft.Rows, canonRight.Rows, rows, problem.MemoryLimitRows);
                        long totalCost = SaturatingAdd(canonLeft.Cost, canonRight.Cost, localCost);

                        if (best is null || totalCost < best.Cost ||
                            (totalCost == best.Cost && OpRank(op) < OpRank(best.Node.Operator)))
                        {
                            var node = new PlanNode(op, Left: canonLeft.Node, Right: canonRight.Node);
                            best = new DpEntry(node, totalCost, rows, canonLeft.MinTableId);
                        }
                    }
                }

                dp[mask] = best;
            }
        }

        int fullMask = totalMasks - 1;
        return dp[fullMask] is { } entry ? new QueryPlan(entry.Node) : QueryPlan.Empty;
    }

    private static readonly string[] JoinOps = ["nestedLoop", "hashJoin", "mergeJoin"];

    private static int OpRank(string op) => op switch
    {
        "nestedLoop" => 0,
        "hashJoin"   => 1,
        "mergeJoin"  => 2,
        _            => 3
    };

    private static long JoinLocalCost(string op, long leftRows, long rightRows, long outRows, int memLimit)
    {
        long inputRows = SaturatingAdd(leftRows, rightRows);
        return op switch
        {
            "nestedLoop" => SaturatingAdd(SaturatingMultiply(leftRows, rightRows), outRows),
            "hashJoin" =>
                SaturatingAdd(
                    SaturatingMultiply(inputRows, 4),
                    outRows,
                    SaturatingMultiply(Math.Max(0L, Math.Min(leftRows, rightRows) - memLimit), 50)),
            _ => // mergeJoin
                SaturatingAdd(
                    SortCost(leftRows),
                    SortCost(rightRows),
                    SaturatingMultiply(inputRows, 2),
                    outRows)
        };
    }

    private static DpEntry BestLeafPlan(QueryProblem problem, TableSpec table)
    {
        long filteredRows = CostModel.EstimateFilteredRows(problem, table);

        // tableScan cost
        long scanCost = SaturatingAdd(
            SaturatingMultiply(table.Rows, table.ScanCostPerRow),
            SaturatingMultiply(filteredRows, 2));

        DpEntry best = new(new PlanNode("tableScan", TableId: table.Id), scanCost, filteredRows, table.Id);

        // Try each usable index
        foreach (var index in table.Indexes)
        {
            var predicate = problem.Predicates.FirstOrDefault(p =>
                p.TableId == table.Id && p.Column == index.Column && p.Indexable);
            if (predicate is null) continue;

            long matchedRows = ScaleCeiling(table.Rows, predicate.SelectivityPermille);
            long seekCost = SaturatingAdd(
                index.SeekStartupCost,
                SaturatingMultiply(matchedRows, index.LookupCostPerRow),
                SaturatingMultiply(filteredRows, 2));

            if (seekCost < best.Cost)
                best = new(new PlanNode("indexSeek", TableId: table.Id, IndexColumn: index.Column), seekCost, filteredRows, table.Id);
        }

        return best;
    }

    private static bool HasCrossingEdge(QueryProblem problem, int leftMask, int rightMask,
        IReadOnlyDictionary<string, int> tableIndex)
    {
        foreach (var join in problem.Joins)
        {
            if (!tableIndex.TryGetValue(join.LeftTable, out int li)) continue;
            if (!tableIndex.TryGetValue(join.RightTable, out int ri)) continue;
            if (((leftMask >> li & 1) == 1 && (rightMask >> ri & 1) == 1) ||
                ((leftMask >> ri & 1) == 1 && (rightMask >> li & 1) == 1))
                return true;
        }
        return false;
    }

    private static List<string> GetTableIds(int mask, List<TableSpec> tables)
    {
        var ids = new List<string>();
        for (int i = 0; i < tables.Count; i++)
            if ((mask >> i & 1) == 1) ids.Add(tables[i].Id);
        return ids;
    }

    private static int PopCount(int x)
    {
        int count = 0;
        while (x != 0) { count += x & 1; x >>= 1; }
        return count;
    }

    private static long SortCost(long rows)
    {
        int levels = 0;
        long value = Math.Max(1, rows);
        while (value > 1) { value = (value + 1) / 2; levels++; }
        return SaturatingMultiply(rows, Math.Max(1, levels), 2);
    }

    private static long ScaleCeiling(long value, int permille) =>
        Math.Max(1, SaturatingAdd(SaturatingMultiply(value, permille), 999) / 1000);

    private static long SaturatingMultiply(params long[] values)
    {
        const long cap = CostModel.CostCap;
        long result = 1;
        foreach (var v in values)
        {
            if (v == 0) return 0;
            if (result > cap / v) return cap;
            result *= v;
        }
        return Math.Min(cap, result);
    }

    private static long SaturatingAdd(params long[] values)
    {
        const long cap = CostModel.CostCap;
        long result = 0;
        foreach (var v in values)
        {
            if (result >= cap - v) return cap;
            result += v;
        }
        return result;
    }

    private sealed record DpEntry(PlanNode Node, long Cost, long Rows, string MinTableId);
}

