using System.Numerics;

namespace QueryPlanning;

public sealed class QueryOptimizer
{
    // Kept in sync with CostModel.CostCap so intermediate arithmetic saturates
    // identically to the validator's own cost computation.
    private const long CostCap = CostModel.CostCap;

    // Exact DP considers all 3^n (subset, split) pairs. 14 tables keeps that
    // well under a second while covering every realistic workload; larger
    // inputs fall back to a greedy join order so we never hang.
    private const int ExactDpTableLimit = 14;

    public QueryPlan Optimize(QueryProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var tables = SelectPlannableTables(problem);
        if (tables.Count == 0)
        {
            return QueryPlan.Empty;
        }

        var n = tables.Count;
        var filteredRows = new long[n];
        var leafPlan = new PlanNode[n];
        var leafCost = new long[n];
        for (var i = 0; i < n; i++)
        {
            filteredRows[i] = CostModel.EstimateFilteredRows(problem, tables[i]);
            var (plan, cost) = BestLeafPlan(problem, tables[i], filteredRows[i]);
            leafPlan[i] = plan;
            leafCost[i] = cost;
        }

        if (n == 1)
        {
            return new QueryPlan(leafPlan[0]);
        }

        var adjacency = BuildAdjacency(problem, tables);

        var root = n <= ExactDpTableLimit
            ? OptimizeExact(problem, tables, filteredRows, leafPlan, leafCost, adjacency)
            : OptimizeGreedy(problem, tables, filteredRows, leafPlan, leafCost, adjacency);

        return new QueryPlan(root);
    }

    // Mirrors CostModel.ValidateProblem's population of its table dictionary:
    // invalid tables are skipped entirely and duplicate IDs keep the first
    // occurrence, so the set of tables we must cover exactly matches what the
    // validator will require for table_coverage.
    private static List<TableSpec> SelectPlannableTables(QueryProblem problem)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TableSpec>();
        foreach (var table in problem.Tables)
        {
            if (string.IsNullOrWhiteSpace(table.Id)
                || table.Rows <= 0
                || table.ScanCostPerRow <= 0)
            {
                continue;
            }
            if (!seen.Add(table.Id))
            {
                continue;
            }
            result.Add(table);
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return result;
    }

    // Picks the cheapest leaf: a full table scan, or a seek through whichever
    // index (with a matching indexable predicate) yields the lowest cost.
    // Ties keep the earlier candidate in enumeration order (scan first, then
    // indexes in their declared order), which keeps Optimize deterministic.
    private static (PlanNode Plan, long Cost) BestLeafPlan(
        QueryProblem problem,
        TableSpec table,
        long filteredRows)
    {
        var bestPlan = new PlanNode("tableScan", TableId: table.Id);
        var bestCost = SaturatingAdd(
            SaturatingMultiply(table.Rows, table.ScanCostPerRow),
            SaturatingMultiply(filteredRows, 2));

        foreach (var index in table.Indexes)
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
            var seekCost = SaturatingAdd(
                index.SeekStartupCost,
                SaturatingMultiply(matchedRows, index.LookupCostPerRow),
                SaturatingMultiply(filteredRows, 2));
            if (seekCost < bestCost)
            {
                bestCost = seekCost;
                bestPlan = new PlanNode(
                    "indexSeek",
                    TableId: table.Id,
                    IndexColumn: index.Column);
            }
        }

        return (bestPlan, bestCost);
    }

    // Bitmask of tables directly connected to each table by a declared join
    // edge; used to test whether a candidate split has a crossing join.
    private static int[] BuildAdjacency(QueryProblem problem, List<TableSpec> tables)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < tables.Count; i++)
        {
            index[tables[i].Id] = i;
        }

        var adjacency = new int[tables.Count];
        foreach (var join in problem.Joins)
        {
            if (index.TryGetValue(join.LeftTable, out var li)
                && index.TryGetValue(join.RightTable, out var ri)
                && li != ri)
            {
                adjacency[li] |= 1 << ri;
                adjacency[ri] |= 1 << li;
            }
        }
        return adjacency;
    }

    private static List<string> TableIds(List<TableSpec> tables, int mask)
    {
        var ids = new List<string>();
        for (var i = 0; i < tables.Count; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                ids.Add(tables[i].Id);
            }
        }
        return ids;
    }

    // Classic bitmask DP over subsets ("DPsub"): for every subset of tables,
    // try every way to split it into two non-empty parts that have a
    // declared join edge crossing them, and keep the cheapest combination of
    // (best left plan, best right plan, join operator). Row estimates from
    // CostModel.EstimateRows depend only on the table subset, not on plan
    // shape, so this DP is exact: bestCost[mask] is the true minimum
    // achievable cost for joining exactly that subset of tables.
    private static PlanNode OptimizeExact(
        QueryProblem problem,
        List<TableSpec> tables,
        long[] filteredRows,
        PlanNode[] leafPlan,
        long[] leafCost,
        int[] adjacency)
    {
        var n = tables.Count;
        var fullMask = (1 << n) - 1;
        var subsetCount = 1 << n;

        var rows = new long[subsetCount];
        var minTableId = new string?[subsetCount];
        var subsetAdjacency = new int[subsetCount];
        var bestCost = new long[subsetCount];
        var bestSplit = new int[subsetCount];
        var bestOperator = new string?[subsetCount];

        for (var i = 0; i < n; i++)
        {
            var mask = 1 << i;
            rows[mask] = filteredRows[i];
            minTableId[mask] = tables[i].Id;
            subsetAdjacency[mask] = adjacency[i];
            bestCost[mask] = leafCost[i];
        }

        for (var mask = 1; mask < subsetCount; mask++)
        {
            if (BitOperations.PopCount((uint)mask) <= 1)
            {
                continue;
            }

            var lowBit = mask & -mask;
            var lowIndex = BitOperations.TrailingZeroCount(lowBit);
            var rest = mask ^ lowBit;

            rows[mask] = CostModel.EstimateRows(problem, TableIds(tables, mask));
            minTableId[mask] =
                string.CompareOrdinal(tables[lowIndex].Id, minTableId[rest]) < 0
                    ? tables[lowIndex].Id
                    : minTableId[rest];
            subsetAdjacency[mask] = adjacency[lowIndex] | subsetAdjacency[rest];

            bestCost[mask] = long.MaxValue;
            for (var sub = (mask - 1) & mask; sub > 0; sub = (sub - 1) & mask)
            {
                var other = mask ^ sub;
                if (sub > other)
                {
                    continue; // each unordered split is only evaluated once
                }
                if ((subsetAdjacency[sub] & other) == 0)
                {
                    continue; // no declared join edge crosses this split
                }

                var leftRows = rows[sub];
                var rightRows = rows[other];
                var combinedRows = rows[mask];
                var baseCost = SaturatingAdd(bestCost[sub], bestCost[other]);

                var nestedTotal = SaturatingAdd(baseCost, NestedLoopCost(leftRows, rightRows, combinedRows));
                if (nestedTotal < bestCost[mask])
                {
                    bestCost[mask] = nestedTotal;
                    bestSplit[mask] = sub;
                    bestOperator[mask] = "nestedLoop";
                }

                var hashTotal = SaturatingAdd(baseCost, HashJoinCost(leftRows, rightRows, combinedRows, problem.MemoryLimitRows));
                if (hashTotal < bestCost[mask])
                {
                    bestCost[mask] = hashTotal;
                    bestSplit[mask] = sub;
                    bestOperator[mask] = "hashJoin";
                }

                var mergeTotal = SaturatingAdd(baseCost, MergeJoinCost(leftRows, rightRows, combinedRows));
                if (mergeTotal < bestCost[mask])
                {
                    bestCost[mask] = mergeTotal;
                    bestSplit[mask] = sub;
                    bestOperator[mask] = "mergeJoin";
                }
            }

            if (bestOperator[mask] is null)
            {
                // Degenerate input: no split of this subset has a declared
                // crossing join edge, so no plan over it can ever satisfy the
                // cross_join rule. Fall back to an arbitrary split so we still
                // return a structurally well-formed tree instead of null.
                var sub = lowBit;
                var other = mask ^ sub;
                bestCost[mask] = SaturatingAdd(
                    bestCost[sub],
                    bestCost[other],
                    NestedLoopCost(rows[sub], rows[other], rows[mask]));
                bestSplit[mask] = sub;
                bestOperator[mask] = "nestedLoop";
            }
        }

        var cache = new PlanNode?[subsetCount];
        return Build(fullMask);

        PlanNode Build(int mask)
        {
            if (cache[mask] is { } cached)
            {
                return cached;
            }
            if (BitOperations.PopCount((uint)mask) == 1)
            {
                return leafPlan[BitOperations.TrailingZeroCount(mask)];
            }

            var subA = bestSplit[mask];
            var subB = mask ^ subA;
            var (leftMask, rightMask) =
                string.CompareOrdinal(minTableId[subA], minTableId[subB]) < 0
                    ? (subA, subB)
                    : (subB, subA);

            var node = new PlanNode(
                bestOperator[mask]!,
                Left: Build(leftMask),
                Right: Build(rightMask));
            cache[mask] = node;
            return node;
        }
    }

    // Polynomial fallback for pathologically large inputs where the exact
    // O(3^n) DP would be too slow: greedily merges whichever pair of
    // partial plans yields the cheapest combined result at each step,
    // preferring pairs with a declared crossing join edge so the result
    // stays valid whenever the join graph makes that possible.
    private static PlanNode OptimizeGreedy(
        QueryProblem problem,
        List<TableSpec> tables,
        long[] filteredRows,
        PlanNode[] leafPlan,
        long[] leafCost,
        int[] adjacency)
    {
        var n = tables.Count;
        var components = new List<Component>(n);
        for (var i = 0; i < n; i++)
        {
            components.Add(new Component(1 << i, adjacency[i], tables[i].Id, leafPlan[i], leafCost[i], filteredRows[i]));
        }

        while (components.Count > 1)
        {
            var bestI = 0;
            var bestJ = 1;
            var bestEffective = long.MaxValue;
            var bestOp = "nestedLoop";
            var bestLocalCost = 0L;
            var bestRows = 0L;

            for (var i = 0; i < components.Count; i++)
            {
                for (var j = i + 1; j < components.Count; j++)
                {
                    var a = components[i];
                    var b = components[j];
                    var crossing = (a.Adjacency & b.Mask) != 0;
                    var combinedMask = a.Mask | b.Mask;
                    var rows = CostModel.EstimateRows(problem, TableIds(tables, combinedMask));

                    var nestedCost = NestedLoopCost(a.Rows, b.Rows, rows);
                    var hashCost = HashJoinCost(a.Rows, b.Rows, rows, problem.MemoryLimitRows);
                    var mergeCost = MergeJoinCost(a.Rows, b.Rows, rows);
                    var (op, localCost) = MinOp(nestedCost, hashCost, mergeCost);
                    var total = SaturatingAdd(a.Cost, b.Cost, localCost);
                    // Heavily penalize non-crossing merges so they are only
                    // ever chosen when no crossing alternative exists.
                    var effective = crossing ? total : SaturatingAdd(total, CostCap / 2);

                    if (effective < bestEffective)
                    {
                        bestEffective = effective;
                        bestI = i;
                        bestJ = j;
                        bestOp = op;
                        bestLocalCost = localCost;
                        bestRows = rows;
                    }
                }
            }

            var left = components[bestI];
            var right = components[bestJ];
            var (leftComp, rightComp) =
                string.CompareOrdinal(left.MinId, right.MinId) < 0 ? (left, right) : (right, left);
            var node = new PlanNode(bestOp, Left: leftComp.Plan, Right: rightComp.Plan);
            var combined = new Component(
                left.Mask | right.Mask,
                left.Adjacency | right.Adjacency,
                leftComp.MinId,
                node,
                SaturatingAdd(left.Cost, right.Cost, bestLocalCost),
                bestRows);

            components.RemoveAt(bestJ);
            components.RemoveAt(bestI);
            components.Add(combined);
        }

        return components[0].Plan;
    }

    private static (string Op, long Cost) MinOp(long nested, long hash, long merge)
    {
        var op = "nestedLoop";
        var cost = nested;
        if (hash < cost)
        {
            op = "hashJoin";
            cost = hash;
        }
        if (merge < cost)
        {
            op = "mergeJoin";
            cost = merge;
        }
        return (op, cost);
    }

    // The following replicate CostModel's private cost formulas exactly
    // (same saturating integer arithmetic) so the plan this optimizer judges
    // cheapest is actually the one CostModel.ValidateAndCost will score
    // lowest.
    private static long NestedLoopCost(long leftRows, long rightRows, long rows) =>
        SaturatingAdd(SaturatingMultiply(leftRows, rightRows), rows);

    private static long HashJoinCost(long leftRows, long rightRows, long rows, int memoryLimitRows)
    {
        var buildRows = Math.Min(leftRows, rightRows);
        var spillRows = Math.Max(0L, buildRows - memoryLimitRows);
        var inputRows = SaturatingAdd(leftRows, rightRows);
        return SaturatingAdd(
            SaturatingMultiply(inputRows, 4),
            rows,
            SaturatingMultiply(spillRows, 20));
    }

    private static long MergeJoinCost(long leftRows, long rightRows, long rows)
    {
        var inputRows = SaturatingAdd(leftRows, rightRows);
        return SaturatingAdd(
            SortCost(leftRows),
            SortCost(rightRows),
            SaturatingMultiply(inputRows, 2),
            rows);
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
        Math.Max(1, SaturatingAdd(SaturatingMultiply(value, permille), 999) / 1000);

    private static long SaturatingMultiply(long a, long b)
    {
        if (a == 0 || b == 0)
        {
            return 0;
        }
        if (a > CostCap / b)
        {
            return CostCap;
        }
        return Math.Min(CostCap, a * b);
    }

    private static long SaturatingMultiply(long a, long b, long c) =>
        SaturatingMultiply(SaturatingMultiply(a, b), c);

    private static long SaturatingAdd(long a, long b)
    {
        if (a >= CostCap - b)
        {
            return CostCap;
        }
        return a + b;
    }

    private static long SaturatingAdd(long a, long b, long c) =>
        SaturatingAdd(SaturatingAdd(a, b), c);

    private static long SaturatingAdd(long a, long b, long c, long d) =>
        SaturatingAdd(SaturatingAdd(a, b, c), d);

    private sealed record Component(
        int Mask,
        int Adjacency,
        string MinId,
        PlanNode Plan,
        long Cost,
        long Rows);
}

