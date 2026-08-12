using System.Numerics;

namespace QueryPlanning;

public sealed class QueryOptimizer
{
    public QueryPlan Optimize(QueryProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var tables = problem.Tables
            .OrderBy(table => table.Id, StringComparer.Ordinal)
            .ToArray();
        if (tables.Length == 0 || tables.Length > 63)
        {
            return QueryPlan.Empty;
        }

        var tableIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < tables.Length; index++)
        {
            if (!tableIndexes.TryAdd(tables[index].Id, index))
            {
                return QueryPlan.Empty;
            }
        }

        var candidates = new Dictionary<ulong, Candidate?>();
        var rows = new Dictionary<ulong, long>();

        Candidate? BestPlan(ulong tableSet)
        {
            if (candidates.TryGetValue(tableSet, out var cached))
            {
                return cached;
            }

            Candidate? best;
            if ((tableSet & (tableSet - 1)) == 0)
            {
                var tableIndex = BitOperations.TrailingZeroCount(tableSet);
                best = BestAccessPlan(tables[tableIndex]);
            }
            else
            {
                best = null;
                var firstTable = 1UL << BitOperations.TrailingZeroCount(tableSet);
                for (var leftSet = (tableSet - 1) & tableSet;
                     leftSet != 0;
                     leftSet = (leftSet - 1) & tableSet)
                {
                    if ((leftSet & firstTable) == 0)
                    {
                        continue;
                    }

                    var rightSet = tableSet ^ leftSet;
                    var left = BestPlan(leftSet);
                    var right = BestPlan(rightSet);
                    if (left is null || right is null || !HasCrossingJoin(leftSet, rightSet))
                    {
                        continue;
                    }

                    var leftRows = RowsFor(leftSet);
                    var rightRows = RowsFor(rightSet);
                    foreach (var joinOperator in JoinOperators)
                    {
                        // A spilling hash build is deliberately avoided in favor of
                        // operators that can execute within the row memory budget.
                        if (joinOperator == "hashJoin"
                            && Math.Min(leftRows, rightRows) > problem.MemoryLimitRows)
                        {
                            continue;
                        }

                        var candidate = new Candidate(
                            new PlanNode(
                                joinOperator,
                                Left: left.Plan,
                                Right: right.Plan),
                            SaturatingAdd(
                                left.Cost,
                                right.Cost,
                                JoinCost(
                                    joinOperator,
                                    leftRows,
                                    rightRows,
                                    RowsFor(tableSet),
                                    problem.MemoryLimitRows)),
                            JoinKey(joinOperator, left.Key, right.Key));
                        if (IsBetter(candidate, best))
                        {
                            best = candidate;
                        }
                    }
                }
            }

            candidates[tableSet] = best;
            return best;
        }

        Candidate? BestAccessPlan(TableSpec table)
        {
            var filteredRows = CostModel.EstimateFilteredRows(problem, table);
            Candidate? best = new(
                new PlanNode("tableScan", TableId: table.Id),
                SaturatingAdd(
                    SaturatingMultiply(table.Rows, table.ScanCostPerRow),
                    SaturatingMultiply(filteredRows, 2)),
                LeafKey("tableScan", table.Id));

            foreach (var column in table.Indexes
                         .Select(index => index.Column)
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(column => column, StringComparer.Ordinal))
            {
                var index = table.Indexes.First(index => index.Column == column);
                var predicate = problem.Predicates.FirstOrDefault(predicate =>
                    predicate.TableId == table.Id
                    && predicate.Column == column
                    && predicate.Indexable);
                if (predicate is null)
                {
                    continue;
                }

                var candidate = new Candidate(
                    new PlanNode("indexSeek", TableId: table.Id, IndexColumn: column),
                    SaturatingAdd(
                        index.SeekStartupCost,
                        SaturatingMultiply(
                            ScaleCeiling(table.Rows, predicate.SelectivityPermille),
                            index.LookupCostPerRow),
                        SaturatingMultiply(filteredRows, 2)),
                    LeafKey("indexSeek", table.Id, column));
                if (IsBetter(candidate, best))
                {
                    best = candidate;
                }
            }

            return best;
        }

        long RowsFor(ulong tableSet)
        {
            if (rows.TryGetValue(tableSet, out var cachedRows))
            {
                return cachedRows;
            }

            var tableIds = new List<string>();
            for (var index = 0; index < tables.Length; index++)
            {
                if ((tableSet & (1UL << index)) != 0)
                {
                    tableIds.Add(tables[index].Id);
                }
            }

            var estimatedRows = CostModel.EstimateRows(problem, tableIds);
            rows[tableSet] = estimatedRows;
            return estimatedRows;
        }

        bool HasCrossingJoin(ulong leftSet, ulong rightSet)
        {
            foreach (var join in problem.Joins)
            {
                if (!tableIndexes.TryGetValue(join.LeftTable, out var leftTable)
                    || !tableIndexes.TryGetValue(join.RightTable, out var rightTable))
                {
                    continue;
                }

                var leftInLeft = (leftSet & (1UL << leftTable)) != 0;
                var rightInRight = (rightSet & (1UL << rightTable)) != 0;
                var leftInRight = (rightSet & (1UL << leftTable)) != 0;
                var rightInLeft = (leftSet & (1UL << rightTable)) != 0;
                if ((leftInLeft && rightInRight) || (leftInRight && rightInLeft))
                {
                    return true;
                }
            }

            return false;
        }

        var fullSet = (1UL << tables.Length) - 1;
        var result = BestPlan(fullSet);
        return result is null ? QueryPlan.Empty : new(result.Plan);
    }

    private static readonly string[] JoinOperators =
    [
        "hashJoin",
        "mergeJoin",
        "nestedLoop",
    ];

    private static long JoinCost(
        string joinOperator,
        long leftRows,
        long rightRows,
        long outputRows,
        int memoryLimitRows)
    {
        var inputRows = SaturatingAdd(leftRows, rightRows);
        return joinOperator switch
        {
            "nestedLoop" => SaturatingAdd(
                SaturatingMultiply(leftRows, rightRows),
                outputRows),
            "hashJoin" => HashJoinCost(
                inputRows,
                outputRows,
                Math.Min(leftRows, rightRows),
                memoryLimitRows),
            _ => SaturatingAdd(
                SortCost(leftRows),
                SortCost(rightRows),
                SaturatingMultiply(inputRows, 2),
                outputRows),
        };
    }

    private static long HashJoinCost(
        long inputRows,
        long outputRows,
        long buildRows,
        int memoryLimitRows) =>
        SaturatingAdd(
            SaturatingMultiply(inputRows, 4),
            outputRows,
            SaturatingMultiply(Math.Max(0, buildRows - memoryLimitRows), 20));

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

    private static bool IsBetter(Candidate candidate, Candidate? current) =>
        current is null
        || candidate.Cost < current.Cost
        || (candidate.Cost == current.Cost
            && StringComparer.Ordinal.Compare(candidate.Key, current.Key) < 0);

    private static string LeafKey(string operatorName, string tableId, string? indexColumn = null) =>
        indexColumn is null
            ? $"{EncodeKeyPart(operatorName)}{EncodeKeyPart(tableId)}"
            : $"{EncodeKeyPart(operatorName)}{EncodeKeyPart(tableId)}{EncodeKeyPart(indexColumn)}";

    private static string JoinKey(string operatorName, string left, string right) =>
        $"{EncodeKeyPart(operatorName)}{left}{right}";

    private static string EncodeKeyPart(string value) =>
        $"{value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{value}";

    private sealed record Candidate(PlanNode Plan, long Cost, string Key);
}
