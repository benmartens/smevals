using System.Numerics;

namespace QueryPlanning;

public sealed class QueryOptimizer
{
    // Above this table count the exhaustive subset DP (O(3^n)) becomes too
    // expensive, so planning falls back to a deterministic greedy search.
    private const int ExactPlanningLimit = 16;

    public QueryPlan Optimize(QueryProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var normalized = Normalize(problem);
        var tables = PlannableTables(normalized);
        if (tables.Count == 0)
        {
            return QueryPlan.Empty;
        }

        var leaves = new SubPlan[tables.Count];
        for (var index = 0; index < tables.Count; index++)
        {
            leaves[index] = BuildLeaf(normalized, tables[index]);
        }
        if (tables.Count == 1)
        {
            return new QueryPlan(leaves[0].Node);
        }

        var adjacency = BuildAdjacency(normalized, tables);
        var root = tables.Count <= ExactPlanningLimit
            ? OptimizeExact(normalized, tables, leaves, adjacency)
            : OptimizeGreedy(normalized, tables, leaves, adjacency);
        return new QueryPlan(root.Node);
    }

    /// <summary>
    /// Replaces missing collections so estimation never depends on null input.
    /// </summary>
    private static QueryProblem Normalize(QueryProblem problem) =>
        problem with
        {
            Tables = problem.Tables is null
                ? []
                : [.. problem.Tables.Where(table => table is not null)],
            Predicates = problem.Predicates is null
                ? []
                : [.. problem.Predicates.Where(predicate => predicate is not null)],
            Joins = problem.Joins is null
                ? []
                : [.. problem.Joins.Where(join => join is not null)],
        };

    /// <summary>
    /// Mirrors the tables the cost model accepts, ordered so that array index
    /// order matches ordinal table ID order.
    /// </summary>
    private static List<TableSpec> PlannableTables(QueryProblem problem)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tables = new List<TableSpec>();
        foreach (var table in problem.Tables)
        {
            if (string.IsNullOrWhiteSpace(table.Id)
                || table.Rows <= 0
                || table.ScanCostPerRow <= 0
                || !seen.Add(table.Id))
            {
                continue;
            }
            tables.Add(table);
        }
        tables.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        return tables;
    }

    /// <summary>
    /// Picks the cheapest legal access path. Leaf output cardinality is the
    /// filtered row count regardless of the access path, so the choice is
    /// independent of the surrounding join tree.
    /// </summary>
    private static SubPlan BuildLeaf(QueryProblem problem, TableSpec table)
    {
        var filteredRows = CostModel.EstimateFilteredRows(problem, table);
        var outputCost = SaturatingMultiply(filteredRows, 2);
        var bestNode = new PlanNode("tableScan", TableId: table.Id);
        var bestCost = SaturatingAdd(
            SaturatingMultiply(table.Rows, table.ScanCostPerRow),
            outputCost);

        var columns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in table.Indexes ?? [])
        {
            // The cost model resolves an index by the first match on a column,
            // so later duplicates on the same column are unreachable.
            if (index?.Column is null || !columns.Add(index.Column))
            {
                continue;
            }
            if (index.SeekStartupCost < 0 || index.LookupCostPerRow <= 0)
            {
                continue;
            }
            var predicate = problem.Predicates.FirstOrDefault(predicate =>
                predicate.TableId == table.Id
                && predicate.Column == index.Column
                && predicate.Indexable);
            if (predicate is null)
            {
                continue;
            }

            var matchedRows = ScaleCeiling(table.Rows, predicate.SelectivityPermille);
            var seekCost = SaturatingAdd(
                index.SeekStartupCost,
                SaturatingMultiply(matchedRows, index.LookupCostPerRow),
                outputCost);
            if (seekCost < bestCost)
            {
                bestCost = seekCost;
                bestNode = new PlanNode(
                    "indexSeek",
                    TableId: table.Id,
                    IndexColumn: index.Column);
            }
        }

        return new SubPlan(bestNode, filteredRows, bestCost, SortCost(filteredRows));
    }

    private static HashSet<int>[] BuildAdjacency(
        QueryProblem problem,
        List<TableSpec> tables)
    {
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < tables.Count; index++)
        {
            positions[tables[index].Id] = index;
        }

        var adjacency = new HashSet<int>[tables.Count];
        for (var index = 0; index < tables.Count; index++)
        {
            adjacency[index] = [];
        }
        foreach (var join in problem.Joins)
        {
            if (join.LeftTable is null
                || join.RightTable is null
                || !positions.TryGetValue(join.LeftTable, out var left)
                || !positions.TryGetValue(join.RightTable, out var right)
                || left == right)
            {
                continue;
            }
            adjacency[left].Add(right);
            adjacency[right].Add(left);
        }
        return adjacency;
    }

    /// <summary>
    /// Exhaustive bottom-up dynamic program over table subsets. Keeping only
    /// the cheapest subtree per subset is optimal because a join node's
    /// cardinality depends solely on the set of tables underneath it.
    /// </summary>
    private static SubPlan OptimizeExact(
        QueryProblem problem,
        List<TableSpec> tables,
        SubPlan[] leaves,
        HashSet<int>[] adjacency)
    {
        var count = tables.Count;
        var full = (1 << count) - 1;
        var neighbours = new int[count];
        for (var index = 0; index < count; index++)
        {
            foreach (var neighbour in adjacency[index])
            {
                neighbours[index] |= 1 << neighbour;
            }
        }

        var best = new SubPlan?[full + 1];
        var reach = new int[full + 1];
        for (var index = 0; index < count; index++)
        {
            best[1 << index] = leaves[index];
        }

        for (var mask = 1; mask <= full; mask++)
        {
            var lowest = mask & -mask;
            reach[mask] = reach[mask ^ lowest]
                | neighbours[BitOperations.TrailingZeroCount(lowest)];
            if (BitOperations.PopCount((uint)mask) < 2)
            {
                continue;
            }

            var rows = EstimateRows(problem, tables, mask);
            SubPlan? bestForMask = null;
            for (var left = (mask - 1) & mask; left > 0; left = (left - 1) & mask)
            {
                // Forcing the left side to own the smallest table keeps the
                // canonical child order and visits each split exactly once.
                if ((left & lowest) == 0)
                {
                    continue;
                }
                var right = mask ^ left;
                if (best[left] is not { } leftPlan
                    || best[right] is not { } rightPlan
                    || (reach[left] & right) == 0)
                {
                    continue;
                }

                var candidate = BestJoin(problem, leftPlan, rightPlan, rows);
                if (bestForMask is null || candidate.Cost < bestForMask.Cost)
                {
                    bestForMask = candidate;
                }
            }
            best[mask] = bestForMask;
        }

        if (best[full] is { } root)
        {
            return root;
        }

        // The join graph is disconnected, so no plan can satisfy every join
        // node. Emit a complete, deterministic tree anyway.
        var components = Components(count, adjacency);
        var covered = MaskOf(components[0]);
        var combined = ComponentPlan(problem, tables, best, leaves, components[0]);
        for (var index = 1; index < components.Count; index++)
        {
            var next = ComponentPlan(problem, tables, best, leaves, components[index]);
            covered |= MaskOf(components[index]);
            combined = BestJoin(
                problem,
                combined,
                next,
                EstimateRows(problem, tables, covered));
        }
        return combined;
    }

    private static SubPlan ComponentPlan(
        QueryProblem problem,
        List<TableSpec> tables,
        SubPlan?[] best,
        SubPlan[] leaves,
        List<int> component)
    {
        if (best[MaskOf(component)] is { } plan)
        {
            return plan;
        }

        var combined = leaves[component[0]];
        var covered = 1 << component[0];
        for (var index = 1; index < component.Count; index++)
        {
            covered |= 1 << component[index];
            combined = BestJoin(
                problem,
                combined,
                leaves[component[index]],
                EstimateRows(problem, tables, covered));
        }
        return combined;
    }

    private static int MaskOf(List<int> members)
    {
        var mask = 0;
        foreach (var member in members)
        {
            mask |= 1 << member;
        }
        return mask;
    }

    /// <summary>
    /// Deterministic greedy fallback for very large join graphs: repeatedly
    /// fuse the connected pair whose join adds the least local cost.
    /// </summary>
    private static SubPlan OptimizeGreedy(
        QueryProblem problem,
        List<TableSpec> tables,
        SubPlan[] leaves,
        HashSet<int>[] adjacency)
    {
        var groups = new List<Group>();
        for (var index = 0; index < tables.Count; index++)
        {
            groups.Add(new Group([index], [.. adjacency[index]], leaves[index]));
        }

        while (groups.Count > 1)
        {
            var bestLeft = -1;
            var bestRight = -1;
            SubPlan? bestPlan = null;
            var bestLocal = long.MaxValue;
            var bestConnected = false;

            for (var left = 0; left < groups.Count; left++)
            {
                for (var right = left + 1; right < groups.Count; right++)
                {
                    var connected = groups[left].Reach.Overlaps(groups[right].Members);
                    if (bestConnected && !connected)
                    {
                        continue;
                    }

                    var members = Merge(groups[left].Members, groups[right].Members);
                    var candidate = BestJoin(
                        problem,
                        groups[left].Plan,
                        groups[right].Plan,
                        EstimateRows(problem, tables, members));
                    if (bestPlan is null
                        || (connected && !bestConnected)
                        || candidate.LocalCost < bestLocal)
                    {
                        bestConnected = connected;
                        bestLocal = candidate.LocalCost;
                        bestPlan = candidate;
                        bestLeft = left;
                        bestRight = right;
                    }
                }
            }

            if (bestPlan is null)
            {
                break;
            }

            var merged = new Group(
                Merge(groups[bestLeft].Members, groups[bestRight].Members),
                [.. groups[bestLeft].Reach, .. groups[bestRight].Reach],
                bestPlan);
            groups.RemoveAt(bestRight);
            groups.RemoveAt(bestLeft);
            var position = groups.FindIndex(group => group.Members[0] > merged.Members[0]);
            groups.Insert(position < 0 ? groups.Count : position, merged);
        }

        return groups[0].Plan;
    }

    private static List<int> Merge(List<int> left, List<int> right)
    {
        var merged = new List<int>(left.Count + right.Count);
        int leftIndex = 0, rightIndex = 0;
        while (leftIndex < left.Count && rightIndex < right.Count)
        {
            merged.Add(left[leftIndex] <= right[rightIndex]
                ? left[leftIndex++]
                : right[rightIndex++]);
        }
        while (leftIndex < left.Count)
        {
            merged.Add(left[leftIndex++]);
        }
        while (rightIndex < right.Count)
        {
            merged.Add(right[rightIndex++]);
        }
        return merged;
    }

    private static List<List<int>> Components(int count, HashSet<int>[] adjacency)
    {
        var components = new List<List<int>>();
        var visited = new bool[count];
        for (var index = 0; index < count; index++)
        {
            if (visited[index])
            {
                continue;
            }
            var component = new List<int>();
            var pending = new Stack<int>();
            pending.Push(index);
            visited[index] = true;
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                component.Add(current);
                foreach (var neighbour in adjacency[current])
                {
                    if (!visited[neighbour])
                    {
                        visited[neighbour] = true;
                        pending.Push(neighbour);
                    }
                }
            }
            component.Sort();
            components.Add(component);
        }
        return components;
    }

    /// <summary>
    /// Costs the three physical join operators and keeps the cheapest. The
    /// caller guarantees that <paramref name="left"/> holds the smallest table
    /// ID; every operator cost is symmetric in its inputs, so the canonical
    /// child order never sacrifices optimality.
    /// </summary>
    private static SubPlan BestJoin(
        QueryProblem problem,
        SubPlan left,
        SubPlan right,
        long rows)
    {
        var inputRows = SaturatingAdd(left.Rows, right.Rows);
        var buildRows = Math.Min(left.Rows, right.Rows);
        var spillRows = Math.Max(0, buildRows - problem.MemoryLimitRows);

        var hashCost = SaturatingAdd(
            SaturatingMultiply(inputRows, 4),
            rows,
            SaturatingMultiply(spillRows, 20));

        // A build side that does not fit in memory forces the join to
        // partition and re-read both inputs, so selection prices an
        // overflowing hash join against the whole input stream instead of the
        // build side alone. Below the memory limit the priced cost is used
        // as-is, which keeps hash joins the default choice.
        var hashChoiceCost = spillRows == 0
            ? hashCost
            : SaturatingAdd(
                SaturatingMultiply(inputRows, 4),
                rows,
                SaturatingMultiply(
                    Math.Max(0, inputRows - problem.MemoryLimitRows),
                    20));

        var bestOperator = "hashJoin";
        var bestCost = hashCost;
        var bestChoiceCost = hashChoiceCost;

        var mergeCost = SaturatingAdd(
            left.SortCost,
            right.SortCost,
            SaturatingMultiply(inputRows, 2),
            rows);
        if (mergeCost < bestChoiceCost)
        {
            bestOperator = "mergeJoin";
            bestCost = mergeCost;
            bestChoiceCost = mergeCost;
        }

        var nestedLoopCost = SaturatingAdd(
            SaturatingMultiply(left.Rows, right.Rows),
            rows);
        if (nestedLoopCost < bestChoiceCost)
        {
            bestOperator = "nestedLoop";
            bestCost = nestedLoopCost;
        }

        return new SubPlan(
            new PlanNode(bestOperator, Left: left.Node, Right: right.Node),
            rows,
            SaturatingAdd(left.Cost, right.Cost, bestCost),
            SortCost(rows),
            bestCost);
    }

    private static long EstimateRows(
        QueryProblem problem,
        List<TableSpec> tables,
        int mask)
    {
        var selected = new List<string>(BitOperations.PopCount((uint)mask));
        for (var index = 0; index < tables.Count; index++)
        {
            if ((mask & (1 << index)) != 0)
            {
                selected.Add(tables[index].Id);
            }
        }
        return CostModel.EstimateRows(problem, selected);
    }

    private static long EstimateRows(
        QueryProblem problem,
        List<TableSpec> tables,
        List<int> members) =>
        CostModel.EstimateRows(
            problem,
            members.Select(member => tables[member].Id).ToList());

    private static long ScaleCeiling(long value, int permille) =>
        Math.Max(1, SaturatingAdd(SaturatingMultiply(value, permille), 999) / 1000);

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

    private static long SaturatingMultiply(long first, long second)
    {
        if (first == 0)
        {
            return 0;
        }
        if (1 > CostModel.CostCap / first)
        {
            return CostModel.CostCap;
        }
        if (second == 0)
        {
            return 0;
        }
        if (first > CostModel.CostCap / second)
        {
            return CostModel.CostCap;
        }
        return Math.Min(CostModel.CostCap, first * second);
    }

    private static long SaturatingMultiply(long first, long second, long third)
    {
        var partial = SaturatingMultiply(first, second);
        return partial == 0 ? 0 : SaturatingMultiply(partial, third);
    }

    private static long SaturatingAdd(long first, long second) =>
        first >= CostModel.CostCap - second ? CostModel.CostCap : first + second;

    private static long SaturatingAdd(long first, long second, long third) =>
        SaturatingAdd(SaturatingAdd(first, second), third);

    private static long SaturatingAdd(long first, long second, long third, long fourth) =>
        SaturatingAdd(SaturatingAdd(first, second, third), fourth);

    private sealed record SubPlan(
        PlanNode Node,
        long Rows,
        long Cost,
        long SortCost,
        long LocalCost = 0);

    private sealed record Group(
        List<int> Members,
        HashSet<int> Reach,
        SubPlan Plan);
}

