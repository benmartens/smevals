using System.Numerics;

namespace QueryPlanning;

public sealed class QueryOptimizer
{
    private const long CostCap = 9_000_000_000_000_000;

    private sealed record PlanInfo(
        PlanNode Node,
        long Cost,
        long Rows,
        long PeakMemoryRows,
        int OperatorCount,
        List<string> TableIds)
    {
        public string MinTableId => TableIds.Min(StringComparer.Ordinal)!;
    }

    public QueryPlan Optimize(QueryProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        if (problem.Tables is null || problem.Tables.Count == 0 || problem.MemoryLimitRows <= 0)
        {
            return QueryPlan.Empty;
        }

        var sortedTables = problem.Tables
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .ToArray();

        int n = sortedTables.Length;
        if (n > 30)
        {
            return QueryPlan.Empty;
        }

        var tableIndexMap = new Dictionary<string, int>(n, StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            if (!tableIndexMap.TryAdd(sortedTables[i].Id, i))
            {
                return QueryPlan.Empty;
            }
        }

        ulong[] joinAdj = new ulong[n];
        foreach (var join in problem.Joins)
        {
            if (join.LeftTable != join.RightTable
                && tableIndexMap.TryGetValue(join.LeftTable, out int u)
                && tableIndexMap.TryGetValue(join.RightTable, out int v))
            {
                joinAdj[u] |= (1UL << v);
                joinAdj[v] |= (1UL << u);
            }
        }

        int maskLimit = 1 << n;
        ulong[] adjOfMask = new ulong[maskLimit];
        for (int s = 1; s < maskLimit; s++)
        {
            int lsb = BitOperations.TrailingZeroCount((uint)s);
            adjOfMask[s] = adjOfMask[s & (s - 1)] | joinAdj[lsb];
        }

        var dp = new PlanInfo?[maskLimit];

        for (int i = 0; i < n; i++)
        {
            var table = sortedTables[i];
            ulong mask = 1UL << i;
            PlanInfo? bestLeaf = null;

            var scanNode = new PlanNode("tableScan", TableId: table.Id);
            var scanInfo = EvaluateLeaf(problem, table, scanNode, "tableScan", null);
            if (scanInfo is not null)
            {
                if (bestLeaf is null || IsBetterPlan(scanInfo, bestLeaf))
                {
                    bestLeaf = scanInfo;
                }
            }

            foreach (var index in table.Indexes)
            {
                var seekNode = new PlanNode("indexSeek", TableId: table.Id, IndexColumn: index.Column);
                var seekInfo = EvaluateLeaf(problem, table, seekNode, "indexSeek", index.Column);
                if (seekInfo is not null)
                {
                    if (bestLeaf is null || IsBetterPlan(seekInfo, bestLeaf))
                    {
                        bestLeaf = seekInfo;
                    }
                }
            }

            dp[mask] = bestLeaf;
        }

        if (n == 1)
        {
            return dp[1] is not null ? new QueryPlan(dp[1]!.Node) : QueryPlan.Empty;
        }

        string[] joinOps = ["nestedLoop", "hashJoin", "mergeJoin"];

        for (int k = 2; k <= n; k++)
        {
            for (int s = 1; s < maskLimit; s++)
            {
                if (BitOperations.PopCount((uint)s) != k)
                {
                    continue;
                }

                ulong S = (ulong)s;
                int m = BitOperations.TrailingZeroCount((uint)s);
                ulong S_prime = S ^ (1UL << m);

                PlanInfo? bestJoin = null;

                for (ulong R = S_prime; R > 0; R = (R - 1) & S_prime)
                {
                    ulong L = S ^ R;

                    var leftPlan = dp[L];
                    var rightPlan = dp[R];
                    if (leftPlan is null || rightPlan is null)
                    {
                        continue;
                    }

                    if ((L & adjOfMask[R]) == 0)
                    {
                        continue;
                    }

                    foreach (var op in joinOps)
                    {
                        var joinNode = new PlanNode(op, Left: leftPlan.Node, Right: rightPlan.Node);
                        var joinInfo = EvaluateJoin(problem, op, leftPlan, rightPlan, joinNode);
                        if (joinInfo is not null)
                        {
                            if (bestJoin is null || IsBetterPlan(joinInfo, bestJoin))
                            {
                                bestJoin = joinInfo;
                            }
                        }
                    }
                }

                dp[S] = bestJoin;
            }
        }

        ulong fullMask = (1UL << n) - 1;
        var rootPlan = dp[fullMask];
        return rootPlan is not null ? new QueryPlan(rootPlan.Node) : QueryPlan.Empty;
    }

    private static bool IsBetterPlan(PlanInfo candidate, PlanInfo current)
    {
        if (candidate.Cost != current.Cost)
        {
            return candidate.Cost < current.Cost;
        }
        if (candidate.PeakMemoryRows != current.PeakMemoryRows)
        {
            return candidate.PeakMemoryRows < current.PeakMemoryRows;
        }
        if (candidate.OperatorCount != current.OperatorCount)
        {
            return candidate.OperatorCount < current.OperatorCount;
        }
        return CompareNodes(candidate.Node, current.Node) < 0;
    }

    private static int CompareNodes(PlanNode? a, PlanNode? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        int cmp = StringComparer.Ordinal.Compare(a.Operator ?? "", b.Operator ?? "");
        if (cmp != 0) return cmp;

        cmp = StringComparer.Ordinal.Compare(a.TableId ?? "", b.TableId ?? "");
        if (cmp != 0) return cmp;

        cmp = StringComparer.Ordinal.Compare(a.IndexColumn ?? "", b.IndexColumn ?? "");
        if (cmp != 0) return cmp;

        cmp = CompareNodes(a.Left, b.Left);
        if (cmp != 0) return cmp;

        return CompareNodes(a.Right, b.Right);
    }

    private static PlanInfo? EvaluateLeaf(
        QueryProblem problem,
        TableSpec table,
        PlanNode node,
        string op,
        string? indexColumn)
    {
        long filteredRows = CostModel.EstimateFilteredRows(problem, table);
        long cost;
        if (op == "tableScan")
        {
            cost = SaturatingAdd(
                SaturatingMultiply(table.Rows, table.ScanCostPerRow),
                SaturatingMultiply(filteredRows, 2));
        }
        else
        {
            var index = table.Indexes.FirstOrDefault(i => i.Column == indexColumn);
            var predicate = problem.Predicates.FirstOrDefault(p =>
                p.TableId == table.Id
                && p.Column == indexColumn
                && p.Indexable);

            if (index is null || predicate is null)
            {
                return null;
            }

            long matchedRows = ScaleCeiling(table.Rows, predicate.SelectivityPermille);
            cost = SaturatingAdd(
                index.SeekStartupCost,
                SaturatingMultiply(matchedRows, index.LookupCostPerRow),
                SaturatingMultiply(filteredRows, 2));
        }

        return new PlanInfo(
            node,
            cost,
            filteredRows,
            1,
            1,
            [table.Id]);
    }

    private static PlanInfo EvaluateJoin(
        QueryProblem problem,
        string op,
        PlanInfo left,
        PlanInfo right,
        PlanNode node)
    {
        var allTableIds = new List<string>(left.TableIds.Count + right.TableIds.Count);
        allTableIds.AddRange(left.TableIds);
        allTableIds.AddRange(right.TableIds);

        long rows = CostModel.EstimateRows(problem, allTableIds);
        long inputRows = SaturatingAdd(left.Rows, right.Rows);
        long localCost;
        long localMemory;

        switch (op)
        {
            case "nestedLoop":
                localCost = SaturatingAdd(
                    SaturatingMultiply(left.Rows, right.Rows),
                    rows);
                localMemory = 1;
                break;
            case "hashJoin":
                long buildRows = Math.Min(left.Rows, right.Rows);
                long spillRows = Math.Max(0, buildRows - problem.MemoryLimitRows);
                localCost = SaturatingAdd(
                    SaturatingMultiply(inputRows, 4),
                    rows,
                    SaturatingMultiply(spillRows, 40));
                localMemory = Math.Min(buildRows, problem.MemoryLimitRows);
                break;
            default: // mergeJoin
                localCost = SaturatingAdd(
                    SortCost(left.Rows),
                    SortCost(right.Rows),
                    SaturatingMultiply(inputRows, 2),
                    rows);
                localMemory = Math.Min(
                    inputRows,
                    Math.Max(1, problem.MemoryLimitRows));
                break;
        }

        long totalCost = SaturatingAdd(left.Cost, right.Cost, localCost);
        long peakMemory = Math.Max(localMemory, Math.Max(left.PeakMemoryRows, right.PeakMemoryRows));
        int opCount = left.OperatorCount + right.OperatorCount + 1;

        return new PlanInfo(
            node,
            totalCost,
            rows,
            peakMemory,
            opCount,
            allTableIds);
    }

    private static long SortCost(long rows)
    {
        var levels = 0;
        var value = Math.Max(1, rows);
        while (value > 1)
        {
            value = (value + 1) / 2;
            levels++;
        }
        return SaturatingMultiply(rows, Math.Max(1, levels), 2);
    }

    private static long ScaleCeiling(long value, int permille) =>
        Math.Max(
            1,
            SaturatingAdd(SaturatingMultiply(value, permille), 999) / 1000);

    private static long SaturatingMultiply(long a, long b)
    {
        if (a == 0 || b == 0) return 0;
        if (a > CostCap / b) return CostCap;
        return Math.Min(CostCap, a * b);
    }

    private static long SaturatingMultiply(long a, long b, long c) =>
        SaturatingMultiply(SaturatingMultiply(a, b), c);

    private static long SaturatingAdd(long a, long b)
    {
        if (a >= CostCap - b) return CostCap;
        return a + b;
    }

    private static long SaturatingAdd(long a, long b, long c) =>
        SaturatingAdd(SaturatingAdd(a, b), c);

    private static long SaturatingAdd(long a, long b, long c, long d) =>
        SaturatingAdd(SaturatingAdd(a, b, c), d);
}


