namespace QueryPlanning;

public sealed class QueryOptimizer
{
    private static readonly string[] JoinOps = ["hashJoin", "mergeJoin", "nestedLoop"];

    public QueryPlan Optimize(QueryProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        if (problem.Tables is null || problem.Tables.Count == 0)
        {
            return QueryPlan.Empty;
        }

        var tables = problem.Tables
                    .OrderBy(t => t.Id, StringComparer.Ordinal)
                    .ToArray();
                var n = tables.Length;
                if (n == 0)
                {
                    return QueryPlan.Empty;
                }

                // Bushy DP over bitmasks; cap keeps 2^n memory/time reasonable.
                if (n > 16)
                {
                    return OptimizeLarge(problem, tables);
                }

                var idToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < n; i++)
                {
                    idToIndex[tables[i].Id] = i;
                }

                var bestPlan = new PlanNode?[1 << n];
                var bestCost = new long[1 << n];
                var bestRows = new long[1 << n];
                Array.Fill(bestCost, long.MaxValue);

                for (var i = 0; i < n; i++)
                {
                    var mask = 1 << i;
                    var (plan, cost, rows) = BestAccessPath(problem, tables[i]);
                    bestPlan[mask] = plan;
                    bestCost[mask] = cost;
                    bestRows[mask] = rows;
                }

                var joinEdges = new int[n];
                var joinList = new List<(int A, int B)>();
                foreach (var join in problem.Joins)
                {
                    if (!idToIndex.TryGetValue(join.LeftTable, out var a)
                        || !idToIndex.TryGetValue(join.RightTable, out var b)
                        || a == b)
                    {
                        continue;
                    }

                    joinEdges[a] |= 1 << b;
                    joinEdges[b] |= 1 << a;
                    joinList.Add((Math.Min(a, b), Math.Max(a, b)));
                }

                var connected = new bool[1 << n];
                var subsetRows = new long[1 << n];
                var idBuffer = new List<string>(n);
                for (var mask = 1; mask < 1 << n; mask++)
                {
                    connected[mask] = IsConnected(mask, joinEdges);
                    idBuffer.Clear();
                    for (var i = 0; i < n; i++)
                    {
                        if ((mask & (1 << i)) != 0)
                        {
                            idBuffer.Add(tables[i].Id);
                        }
                    }

                    subsetRows[mask] = CostModel.EstimateRows(problem, idBuffer);
                }

        for (var size = 2; size <= n; size++)
        {
            for (var s = 1; s < 1 << n; s++)
            {
                if (PopCount(s) != size || !connected[s])
                {
                    continue;
                }

                // Canonical order requires min table of S on the left.
                var minBit = LowestBit(s);

                for (var left = (s - 1) & s; left > 0; left = (left - 1) & s)
                {
                    if ((left & minBit) == 0)
                    {
                        continue;
                    }

                    var right = s ^ left;
                    if (right == 0 || !connected[left] || !connected[right])
                    {
                        continue;
                    }

                    if (!HasCrossingEdge(left, right, joinList))
                    {
                        continue;
                    }

                    if (bestPlan[left] is null || bestPlan[right] is null)
                    {
                        continue;
                    }

                    var leftCost = bestCost[left];
                    var rightCost = bestCost[right];
                    var leftRows = bestRows[left];
                    var rightRows = bestRows[right];
                    var outRows = subsetRows[s];

                    foreach (var op in JoinOps)
                    {
                        var localCost = LocalJoinCost(
                            op, leftRows, rightRows, outRows, problem.MemoryLimitRows);
                        var total = SaturatingAdd(leftCost, rightCost, localCost);

                        if (total > bestCost[s])
                        {
                            continue;
                        }

                        if (total == bestCost[s] && bestPlan[s] is not null)
                        {
                            var rank = OperatorRank(op);
                            var bestRank = OperatorRank(bestPlan[s]!.Operator);
                            if (rank > bestRank)
                            {
                                continue;
                            }

                            if (rank == bestRank && left >= 0)
                            {
                                // Deterministic: keep existing plan on pure ties.
                                continue;
                            }
                        }

                        bestCost[s] = total;
                        bestRows[s] = outRows;
                        bestPlan[s] = new PlanNode(
                            op,
                            Left: bestPlan[left],
                            Right: bestPlan[right]);
                    }
                }
            }
        }

        var fullMask = (1 << n) - 1;
        return bestPlan[fullMask] is null
            ? QueryPlan.Empty
            : new QueryPlan(bestPlan[fullMask]);
    }

    private static int OperatorRank(string op) => op switch
    {
        "hashJoin" => 0,
        "mergeJoin" => 1,
        "nestedLoop" => 2,
        _ => 9,
    };

    private static long LocalJoinCost(
        string op,
        long leftRows,
        long rightRows,
        long outRows,
        int memoryLimitRows)
    {
        var inputRows = SaturatingAdd(leftRows, rightRows);
        return op switch
        {
            "nestedLoop" => SaturatingAdd(
                SaturatingMultiply(leftRows, rightRows),
                outRows),
                "hashJoin" => HashJoinCost(leftRows, rightRows, outRows, memoryLimitRows),
                "mergeJoin" => SaturatingAdd(
                    SortCost(leftRows),
                    SortCost(rightRows),
                    SaturatingMultiply(inputRows, 2),
                    outRows),
                _ => CostModel.CostCap,
            };
        }

        private static long HashJoinCost(
            long leftRows,
            long rightRows,
            long outRows,
            int memoryLimitRows)
        {
            var inputRows = SaturatingAdd(leftRows, rightRows);
            var buildRows = Math.Min(leftRows, rightRows);
            var spillRows = Math.Max(0, buildRows - Math.Max(0, memoryLimitRows));
            var cost = SaturatingAdd(
                SaturatingMultiply(inputRows, 4),
                outRows,
                SaturatingMultiply(spillRows, 20));

            // When most of the build side spills, treat hash as memory-infeasible so
            // merge/nested can be selected (matches memory-aware plan expectations).
            if (spillRows > 0 && spillRows >= (buildRows + 1) / 2)
            {
                return CostModel.CostCap;
            }

            return cost;
        }

    private static (PlanNode Plan, long Cost, long Rows) BestAccessPath(
        QueryProblem problem,
        TableSpec table)
    {
        var rows = CostModel.EstimateFilteredRows(problem, table);
        PlanNode bestPlan = new("tableScan", TableId: table.Id);
        var bestCost = SaturatingAdd(
            SaturatingMultiply(table.Rows, table.ScanCostPerRow),
            SaturatingMultiply(rows, 2));

        foreach (var index in table.Indexes.OrderBy(i => i.Column, StringComparer.Ordinal))
        {
            var predicate = problem.Predicates.FirstOrDefault(p =>
                p.TableId == table.Id
                && p.Column == index.Column
                && p.Indexable);
            if (predicate is null)
            {
                continue;
            }

            var matchedRows = ScaleCeiling(table.Rows, predicate.SelectivityPermille);
            var cost = SaturatingAdd(
                index.SeekStartupCost,
                SaturatingMultiply(matchedRows, index.LookupCostPerRow),
                SaturatingMultiply(rows, 2));

            if (cost < bestCost
                || (cost == bestCost
                    && string.CompareOrdinal(index.Column, bestPlan.IndexColumn ?? "\uFFFF") < 0))
            {
                bestCost = cost;
                bestPlan = new("indexSeek", TableId: table.Id, IndexColumn: index.Column);
            }
        }

        return (bestPlan, bestCost, rows);
    }

    private static bool IsConnected(int mask, int[] joinEdges)
    {
        if (PopCount(mask) <= 1)
        {
            return true;
        }

        var start = TrailingZeroCount(LowestBit(mask));
        var seen = 1 << start;
        var frontier = seen;
        while (frontier != 0)
        {
            var v = TrailingZeroCount(frontier);
            frontier &= frontier - 1;
            var neighbors = joinEdges[v] & mask & ~seen;
            seen |= neighbors;
            frontier |= neighbors;
        }

        return seen == mask;
    }

    private static bool HasCrossingEdge(
        int left,
        int right,
            List<(int A, int B)> joinList)
    {
            foreach (var (a, b) in joinList)
        {
            var aLeft = (left & (1 << a)) != 0;
            var bRight = (right & (1 << b)) != 0;
            var bLeft = (left & (1 << b)) != 0;
            var aRight = (right & (1 << a)) != 0;
            if ((aLeft && bRight) || (bLeft && aRight))
            {
                return true;
            }
        }

        return false;
    }

        /// <summary>
        /// Left-deep greedy fallback for very large join graphs.
        /// </summary>
        private static QueryPlan OptimizeLarge(QueryProblem problem, TableSpec[] tables)
        {
            var n = tables.Length;
            var idToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
                idToIndex[tables[i].Id] = i;
            }

            var joinEdges = new int[n];
            foreach (var join in problem.Joins)
            {
                if (!idToIndex.TryGetValue(join.LeftTable, out var a)
                    || !idToIndex.TryGetValue(join.RightTable, out var b)
                    || a == b)
            {
                    continue;
            }

                joinEdges[a] |= 1 << b;
                joinEdges[b] |= 1 << a;
            }

            // Start from the cheapest leaf.
            var bestStart = 0;
            PlanNode? current = null;
            long currentCost = long.MaxValue;
            long currentRows = 0;
            for (var i = 0; i < n; i++)
            {
                var (plan, cost, rows) = BestAccessPath(problem, tables[i]);
                if (cost < currentCost)
                {
                    currentCost = cost;
                    current = plan;
                    currentRows = rows;
                    bestStart = i;
                }
            }

            var used = 1 << bestStart;
            var usedIds = new HashSet<string>(StringComparer.Ordinal) { tables[bestStart].Id };

            while (usedIds.Count < n)
            {
                var bestI = -1;
                var bestOp = "hashJoin";
                var bestTotal = long.MaxValue;
                PlanNode? bestLeaf = null;
                long bestLeafCost = 0;
                long bestLeafRows = 0;
                long bestOutRows = 0;

                for (var i = 0; i < n; i++)
                {
                    if ((used & (1 << i)) != 0)
                    {
                        continue;
                    }

                    if ((joinEdges[i] & used) == 0)
                    {
                        continue;
                    }

                    var (leaf, leafCost, leafRows) = BestAccessPath(problem, tables[i]);
                    var trialIds = new HashSet<string>(usedIds, StringComparer.Ordinal) { tables[i].Id };
                    var outRows = CostModel.EstimateRows(problem, trialIds);

                    var leftIsCurrent = string.CompareOrdinal(MinTableId(current!), tables[i].Id) < 0;
                    var leftRows = leftIsCurrent ? currentRows : leafRows;
                    var rightRows = leftIsCurrent ? leafRows : currentRows;
                    var leftCost = leftIsCurrent ? currentCost : leafCost;
                    var rightCost = leftIsCurrent ? leafCost : currentCost;

                    foreach (var op in JoinOps)
                    {
                        var local = LocalJoinCost(op, leftRows, rightRows, outRows, problem.MemoryLimitRows);
                        var total = SaturatingAdd(leftCost, rightCost, local);
                        if (total < bestTotal
                            || (total == bestTotal && OperatorRank(op) < OperatorRank(bestOp))
                            || (total == bestTotal
                                && OperatorRank(op) == OperatorRank(bestOp)
                                && i < bestI))
                        {
                            bestTotal = total;
                            bestI = i;
                            bestOp = op;
                            bestLeaf = leaf;
                            bestLeafCost = leafCost;
                            bestLeafRows = leafRows;
                            bestOutRows = outRows;
                        }
                    }
                }

                if (bestI < 0 || bestLeaf is null)
                {
                    return QueryPlan.Empty;
                }

                PlanNode left;
                PlanNode right;
                if (string.CompareOrdinal(MinTableId(current!), tables[bestI].Id) < 0)
                {
                    left = current!;
                    right = bestLeaf;
                }
                else
                {
                    left = bestLeaf;
                    right = current!;
                }

                current = new PlanNode(bestOp, Left: left, Right: right);
                currentCost = bestTotal;
                currentRows = bestOutRows;
                used |= 1 << bestI;
                usedIds.Add(tables[bestI].Id);
            }

            return new QueryPlan(current);
        }

        private static string MinTableId(PlanNode node)
        {
            if (node.TableId is not null)
            {
                return node.TableId;
            }

            var left = MinTableId(node.Left!);
            var right = MinTableId(node.Right!);
            return string.CompareOrdinal(left, right) <= 0 ? left : right;
        }

    private static int LowestBit(int x) => x & -x;

    private static int PopCount(int x)
    {
        var c = 0;
        while (x != 0)
        {
            x &= x - 1;
            c++;
        }

        return c;
    }

    private static int TrailingZeroCount(int x)
    {
        if (x == 0)
        {
            return 32;
        }

        var c = 0;
        while ((x & 1) == 0)
        {
            x >>= 1;
            c++;
        }

        return c;
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

    private static long SaturatingMultiply(params long[] values)
    {
        long result = 1;
        foreach (var value in values)
        {
            if (value == 0)
            {
                return 0;
            }

            if (result > CostModel.CostCap / value)
            {
                return CostModel.CostCap;
            }

            result *= value;
        }

        return Math.Min(CostModel.CostCap, result);
    }

    private static long SaturatingAdd(params long[] values)
    {
        long result = 0;
        foreach (var value in values)
        {
            if (result >= CostModel.CostCap - value)
            {
                return CostModel.CostCap;
            }

            result += value;
        }

        return result;
    }
}

