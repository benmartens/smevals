namespace QueryPlanning;

public sealed class QueryOptimizer
{
    public QueryPlan Optimize(QueryProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        if (problem.Tables.Count == 0)
        {
            return QueryPlan.Empty;
        }

        var orderedTables = problem.Tables
            .OrderBy(table => table.Id, StringComparer.Ordinal)
            .ToArray();
        var tableCount = orderedTables.Length;
        if (tableCount >= 31)
        {
            return QueryPlan.Empty;
        }

        // Exponential DP is exact but only practical for moderate table counts.
        if (tableCount > 20)
        {
            return BuildGreedyPlan(problem, orderedTables);
        }

        var tableIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < tableCount; i++)
        {
            if (!tableIndexById.TryAdd(orderedTables[i].Id, i))
            {
                return QueryPlan.Empty;
            }
        }

        var indexablePredicates = BuildFirstIndexablePredicateMap(problem.Predicates);
        var filteredRows = new long[tableCount];
        var singleTablePlans = new Candidate[tableCount];
        for (var i = 0; i < tableCount; i++)
        {
            var table = orderedTables[i];
            filteredRows[i] = CostModel.EstimateFilteredRows(problem, table);
            singleTablePlans[i] = BestLeafCandidate(
                table,
                filteredRows[i],
                indexablePredicates);
        }

        var joins = BuildSortedJoinEntries(problem.Joins, tableIndexById);
        var adjacency = BuildAdjacency(tableCount, joins);

        var stateCount = 1 << tableCount;
        var subsetRows = PrecomputeSubsetRows(tableCount, filteredRows, joins, stateCount);
        var bestBySubset = new Candidate?[stateCount];

        for (var i = 0; i < tableCount; i++)
        {
            bestBySubset[1 << i] = singleTablePlans[i];
        }

        var masksBySize = GroupMasksBySize(tableCount, stateCount);
        for (var size = 2; size <= tableCount; size++)
        {
            foreach (var subset in masksBySize[size])
            {
                Candidate? best = null;
                for (var leftSubset = (subset - 1) & subset;
                     leftSubset > 0;
                     leftSubset = (leftSubset - 1) & subset)
                {
                    var rightSubset = subset ^ leftSubset;
                    if (rightSubset == 0)
                    {
                        continue;
                    }

                    var leftMin = LowestTableIndex(leftSubset);
                    var rightMin = LowestTableIndex(rightSubset);
                    if (leftMin >= rightMin)
                    {
                        continue;
                    }

                    var left = bestBySubset[leftSubset];
                    var right = bestBySubset[rightSubset];
                    if (left is null || right is null)
                    {
                        continue;
                    }

                    if (!HasCrossingJoin((uint)leftSubset, (uint)rightSubset, adjacency))
                    {
                        continue;
                    }

                    var rows = subsetRows[subset];
                    var leftRows = subsetRows[leftSubset];
                    var rightRows = subsetRows[rightSubset];
                    var inputRows = SaturatingAdd(leftRows, rightRows);

                    ConsiderJoin(
                        ref best,
                        left,
                        right,
                        "nestedLoop",
                        SaturatingAdd(SaturatingMultiply(leftRows, rightRows), rows));

                    var buildRows = Math.Min(leftRows, rightRows);
                    var spillRows = Math.Max(0, buildRows - problem.MemoryLimitRows);
                    ConsiderJoin(
                        ref best,
                        left,
                        right,
                        "hashJoin",
                        SaturatingAdd(
                            SaturatingMultiply(inputRows, 4),
                            rows,
                            SaturatingMultiply(spillRows, 40)));

                    ConsiderJoin(
                        ref best,
                        left,
                        right,
                        "mergeJoin",
                        SaturatingAdd(
                            SortCost(leftRows),
                            SortCost(rightRows),
                            SaturatingMultiply(inputRows, 2),
                            rows));
                }

                bestBySubset[subset] = best;
            }
        }

        var fullSubset = stateCount - 1;
        var fullPlan = bestBySubset[fullSubset];
        return fullPlan is null ? QueryPlan.Empty : new(fullPlan.Plan);
    }

    private static QueryPlan BuildGreedyPlan(
        QueryProblem problem,
        IReadOnlyList<TableSpec> orderedTables)
    {
        var tableIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < orderedTables.Count; i++)
        {
            if (!tableIndexById.TryAdd(orderedTables[i].Id, i))
            {
                return QueryPlan.Empty;
            }
        }

        var indexablePredicates = BuildFirstIndexablePredicateMap(problem.Predicates);
        var groups = new List<Group>(orderedTables.Count);
        for (var i = 0; i < orderedTables.Count; i++)
        {
            var table = orderedTables[i];
            var filtered = CostModel.EstimateFilteredRows(problem, table);
            var candidate = BestLeafCandidate(table, filtered, indexablePredicates);
            groups.Add(new(
                1u << i,
                candidate.Plan,
                candidate.Cost));
        }

        if (groups.Count == 0)
        {
            return QueryPlan.Empty;
        }

        var joins = BuildSortedJoinEntries(problem.Joins, tableIndexById);
        var adjacency = BuildAdjacency(orderedTables.Count, joins);

        while (groups.Count > 1)
        {
            CandidateJoin? best = null;
            for (var i = 0; i < groups.Count; i++)
            {
                for (var j = i + 1; j < groups.Count; j++)
                {
                    var leftMask = groups[i].Subset;
                    var rightMask = groups[j].Subset;
                    if (!HasCrossingJoin(leftMask, rightMask, adjacency))
                    {
                        continue;
                    }

                    var left = groups[i];
                    var right = groups[j];
                    if (LowestTableIndex((int)left.Subset) > LowestTableIndex((int)right.Subset))
                    {
                        (left, right) = (right, left);
                    }

                    var leftRows = CostModel.EstimateRows(problem, ExpandTables(left.Subset, orderedTables));
                    var rightRows = CostModel.EstimateRows(problem, ExpandTables(right.Subset, orderedTables));
                    var mergedMask = left.Subset | right.Subset;
                    var rows = CostModel.EstimateRows(problem, ExpandTables(mergedMask, orderedTables));
                    var inputRows = SaturatingAdd(leftRows, rightRows);

                    var options = new[]
                    {
                        ("nestedLoop", SaturatingAdd(SaturatingMultiply(leftRows, rightRows), rows)),
                        ("hashJoin", SaturatingAdd(
                            SaturatingMultiply(inputRows, 4),
                            rows,
                            SaturatingMultiply(Math.Max(0, Math.Min(leftRows, rightRows) - problem.MemoryLimitRows), 40))),
                        ("mergeJoin", SaturatingAdd(
                            SortCost(leftRows),
                            SortCost(rightRows),
                            SaturatingMultiply(inputRows, 2),
                            rows)),
                    };

                    foreach (var (op, localCost) in options)
                    {
                        var plan = new PlanNode(op, Left: left.Plan, Right: right.Plan);
                        var totalCost = SaturatingAdd(left.Cost, right.Cost, localCost);
                        var candidate = new Candidate(plan, totalCost);
                        if (best is null || IsBetter(candidate, best.Candidate))
                        {
                            best = new(i, j, left.Subset | right.Subset, candidate);
                        }
                    }
                }
            }

            if (best is null)
            {
                return QueryPlan.Empty;
            }

            var join = best;
            groups[join.RightIndex] = groups[^1];
            groups.RemoveAt(groups.Count - 1);
            groups[join.LeftIndex] = new(join.MergedSubset, join.Candidate.Plan, join.Candidate.Cost);
        }

        return new(groups[0].Plan);
    }

    private static List<string> ExpandTables(uint subset, IReadOnlyList<TableSpec> orderedTables)
    {
        var tables = new List<string>();
        for (var i = 0; i < orderedTables.Count; i++)
        {
            if ((subset & (1u << i)) != 0)
            {
                tables.Add(orderedTables[i].Id);
            }
        }
        return tables;
    }

    private static Dictionary<(string TableId, string Column), PredicateSpec> BuildFirstIndexablePredicateMap(
        IReadOnlyList<PredicateSpec> predicates)
    {
        var map = new Dictionary<(string TableId, string Column), PredicateSpec>();
        foreach (var predicate in predicates)
        {
            if (!predicate.Indexable)
            {
                continue;
            }

            var key = (predicate.TableId, predicate.Column);
            if (!map.ContainsKey(key))
            {
                map[key] = predicate;
            }
        }
        return map;
    }

    private static Candidate BestLeafCandidate(
        TableSpec table,
        long filteredRows,
        IReadOnlyDictionary<(string TableId, string Column), PredicateSpec> indexablePredicates)
    {
        Candidate best = new(
            new PlanNode("tableScan", TableId: table.Id),
            SaturatingAdd(
                SaturatingMultiply(table.Rows, table.ScanCostPerRow),
                SaturatingMultiply(filteredRows, 2)));

        var firstIndexesByColumn = new Dictionary<string, IndexSpec>(StringComparer.Ordinal);
        foreach (var index in table.Indexes)
        {
            if (!firstIndexesByColumn.ContainsKey(index.Column))
            {
                firstIndexesByColumn.Add(index.Column, index);
            }
        }

        foreach (var (column, index) in firstIndexesByColumn)
        {
            if (!indexablePredicates.TryGetValue((table.Id, column), out var predicate))
            {
                continue;
            }

            var matchedRows = ScaleCeiling(table.Rows, predicate.SelectivityPermille);
            var candidate = new Candidate(
                new PlanNode("indexSeek", TableId: table.Id, IndexColumn: column),
                SaturatingAdd(
                    index.SeekStartupCost,
                    SaturatingMultiply(matchedRows, index.LookupCostPerRow),
                    SaturatingMultiply(filteredRows, 2)));

            if (IsBetter(candidate, best))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static List<JoinEntry> BuildSortedJoinEntries(
        IReadOnlyList<JoinSpec> joins,
        IReadOnlyDictionary<string, int> tableIndexById)
    {
        var result = new List<JoinEntry>(joins.Count);
        for (var i = 0; i < joins.Count; i++)
        {
            var join = joins[i];
            if (!tableIndexById.TryGetValue(join.LeftTable, out var left)
                || !tableIndexById.TryGetValue(join.RightTable, out var right))
            {
                continue;
            }
            var min = Math.Min(left, right);
            var max = Math.Max(left, right);
            result.Add(new(left, right, min, max, join.SelectivityPermille, i));
        }

        result.Sort(static (a, b) =>
        {
            var minCompare = a.MinIndex.CompareTo(b.MinIndex);
            if (minCompare != 0)
            {
                return minCompare;
            }

            var maxCompare = a.MaxIndex.CompareTo(b.MaxIndex);
            if (maxCompare != 0)
            {
                return maxCompare;
            }

            return a.Order.CompareTo(b.Order);
        });

        return result;
    }

    private static uint[] BuildAdjacency(int tableCount, IReadOnlyList<JoinEntry> joins)
    {
        var adjacency = new uint[tableCount];
        foreach (var join in joins)
        {
            if (join.LeftIndex == join.RightIndex)
            {
                continue;
            }

            adjacency[join.LeftIndex] |= 1u << join.RightIndex;
            adjacency[join.RightIndex] |= 1u << join.LeftIndex;
        }
        return adjacency;
    }

    private static long[] PrecomputeSubsetRows(
        int tableCount,
        IReadOnlyList<long> filteredRows,
        IReadOnlyList<JoinEntry> joins,
        int stateCount)
    {
        var rowsBySubset = new long[stateCount];
        rowsBySubset[0] = 1;

        for (var subset = 1; subset < stateCount; subset++)
        {
            long rows = 1;
            for (var i = 0; i < tableCount; i++)
            {
                if ((subset & (1 << i)) != 0)
                {
                    rows = SaturatingMultiply(rows, filteredRows[i]);
                }
            }

            foreach (var join in joins)
            {
                var leftIncluded = (subset & (1 << join.LeftIndex)) != 0;
                var rightIncluded = (subset & (1 << join.RightIndex)) != 0;
                if (leftIncluded && rightIncluded)
                {
                    rows = ScaleCeiling(rows, join.SelectivityPermille);
                }
            }

            rowsBySubset[subset] = Math.Max(1, rows);
        }

        return rowsBySubset;
    }

    private static List<int>[] GroupMasksBySize(int tableCount, int stateCount)
    {
        var result = new List<int>[tableCount + 1];
        for (var i = 0; i <= tableCount; i++)
        {
            result[i] = [];
        }

        for (var subset = 1; subset < stateCount; subset++)
        {
            var size = PopCount(subset);
            result[size].Add(subset);
        }

        return result;
    }

    private static void ConsiderJoin(
        ref Candidate? best,
        Candidate left,
        Candidate right,
        string op,
        long localCost)
    {
        var candidate = new Candidate(
            new PlanNode(op, Left: left.Plan, Right: right.Plan),
            SaturatingAdd(left.Cost, right.Cost, localCost));

        if (best is null || IsBetter(candidate, best))
        {
            best = candidate;
        }
    }

    private static bool HasCrossingJoin(uint leftSubset, uint rightSubset, IReadOnlyList<uint> adjacency)
    {
        var probeSubset = PopCount((int)leftSubset) <= PopCount((int)rightSubset)
            ? leftSubset
            : rightSubset;
        var targetSubset = probeSubset == leftSubset ? rightSubset : leftSubset;

        while (probeSubset != 0)
        {
            var bit = probeSubset & (uint)-(int)probeSubset;
            var index = LowestTableIndex((int)bit);
            if ((adjacency[index] & targetSubset) != 0)
            {
                return true;
            }

            probeSubset &= probeSubset - 1;
        }

        return false;
    }

    private static int LowestTableIndex(int subset)
    {
        var index = 0;
        while ((subset & 1) == 0)
        {
            subset >>= 1;
            index++;
        }
        return index;
    }

    private static int PopCount(int value)
    {
        var count = 0;
        var current = value;
        while (current != 0)
        {
            current &= current - 1;
            count++;
        }
        return count;
    }

    private static bool IsBetter(Candidate candidate, Candidate currentBest)
    {
        if (candidate.Cost != currentBest.Cost)
        {
            return candidate.Cost < currentBest.Cost;
        }

        return ComparePlans(candidate.Plan, currentBest.Plan) < 0;
    }

    private static int ComparePlans(PlanNode? left, PlanNode? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }
        if (left is null)
        {
            return -1;
        }
        if (right is null)
        {
            return 1;
        }

        var opCompare = StringComparer.Ordinal.Compare(left.Operator, right.Operator);
        if (opCompare != 0)
        {
            return opCompare;
        }

        var tableCompare = CompareOptionalText(left.TableId, right.TableId);
        if (tableCompare != 0)
        {
            return tableCompare;
        }

        var columnCompare = CompareOptionalText(left.IndexColumn, right.IndexColumn);
        if (columnCompare != 0)
        {
            return columnCompare;
        }

        var leftCompare = ComparePlans(left.Left, right.Left);
        if (leftCompare != 0)
        {
            return leftCompare;
        }

        return ComparePlans(left.Right, right.Right);
    }

    private static int CompareOptionalText(string? left, string? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }
        if (left is null)
        {
            return -1;
        }
        if (right is null)
        {
            return 1;
        }

        return StringComparer.Ordinal.Compare(left, right);
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

    private sealed record Candidate(
        PlanNode Plan,
        long Cost);

    private sealed record JoinEntry(
        int LeftIndex,
        int RightIndex,
        int MinIndex,
        int MaxIndex,
        int SelectivityPermille,
        int Order);

    private sealed record Group(
        uint Subset,
        PlanNode Plan,
        long Cost);

    private sealed record CandidateJoin(
        int LeftIndex,
        int RightIndex,
        uint MergedSubset,
        Candidate Candidate);
}
