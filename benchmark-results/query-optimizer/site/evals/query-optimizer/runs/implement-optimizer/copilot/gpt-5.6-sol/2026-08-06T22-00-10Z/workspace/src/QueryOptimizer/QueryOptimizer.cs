using System.Numerics;

namespace QueryPlanning;

public sealed class QueryOptimizer
{
    private const int MaxExactTableCount = 16;

    public QueryPlan Optimize(QueryProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var context = new OptimizationContext(problem);
        var leaves = context.CreateLeaves();
        var best = context.Tables.Length <= MaxExactTableCount
            ? OptimizeExactly(context, leaves)
            : OptimizeGreedily(context, leaves);

        return new(best.Plan);
    }

    private static PlanChoice OptimizeExactly(
        OptimizationContext context,
        IReadOnlyList<PlanChoice> leaves)
    {
        var tableCount = context.Tables.Length;
        var statesBySize = new List<SubsetPlan>[tableCount + 1];
        for (var size = 0; size <= tableCount; size++)
        {
            statesBySize[size] = [];
        }

        for (var tableIndex = 0; tableIndex < tableCount; tableIndex++)
        {
            statesBySize[1].Add(new(1UL << tableIndex, leaves[tableIndex]));
        }

        for (var size = 2; size <= tableCount; size++)
        {
            var bestBySubset = new Dictionary<ulong, SubsetPlan>();
            for (var firstSize = 1; firstSize <= size / 2; firstSize++)
            {
                var secondSize = size - firstSize;
                foreach (var first in statesBySize[firstSize])
                {
                    foreach (var second in statesBySize[secondSize])
                    {
                        if (firstSize == secondSize && first.Mask >= second.Mask)
                        {
                            continue;
                        }
                        if ((first.Mask & second.Mask) != 0
                            || !context.HasCrossingJoin(first.Mask, second.Mask))
                        {
                            continue;
                        }

                        var mask = first.Mask | second.Mask;
                        var outputRows = context.EstimateRows(mask);
                        var choice = CreateJoin(first.Choice, second.Choice, outputRows, problem: context.Problem);
                        if (!bestBySubset.TryGetValue(mask, out var current)
                            || choice.Cost < current.Choice.Cost)
                        {
                            bestBySubset[mask] = new(mask, choice);
                        }
                    }
                }
            }

            statesBySize[size].AddRange(
                bestBySubset.Values.OrderBy(state => state.Mask));
        }

        var fullMask = (1UL << tableCount) - 1;
        var result = statesBySize[tableCount]
            .FirstOrDefault(state => state.Mask == fullMask);
        return result?.Choice
            ?? throw new InvalidOperationException(
                "The declared joins do not connect all query tables.");
    }

    private static PlanChoice OptimizeGreedily(
        OptimizationContext context,
        IReadOnlyList<PlanChoice> leaves)
    {
        PlanChoice? bestPlan = null;
        for (var start = 0; start < context.Tables.Length; start++)
        {
            var selected = new HashSet<int> { start };
            var current = leaves[start];
            while (selected.Count < context.Tables.Length)
            {
                PlanChoice? bestExtension = null;
                var bestTableIndex = -1;
                for (var tableIndex = 0; tableIndex < context.Tables.Length; tableIndex++)
                {
                    if (selected.Contains(tableIndex)
                        || !context.HasCrossingJoin(selected, tableIndex))
                    {
                        continue;
                    }

                    var outputRows = context.EstimateRows(selected, tableIndex);
                    var extension = CreateJoin(
                        current,
                        leaves[tableIndex],
                        outputRows,
                        context.Problem);
                    if (bestExtension is null
                        || extension.Cost < bestExtension.Cost)
                    {
                        bestExtension = extension;
                        bestTableIndex = tableIndex;
                    }
                }

                if (bestExtension is null)
                {
                    current = null!;
                    break;
                }

                selected.Add(bestTableIndex);
                current = bestExtension;
            }

            if (current is not null
                && (bestPlan is null || current.Cost < bestPlan.Cost))
            {
                bestPlan = current;
            }
        }

        return bestPlan
            ?? throw new InvalidOperationException(
                "The declared joins do not connect all query tables.");
    }

    private static PlanChoice CreateJoin(
        PlanChoice first,
        PlanChoice second,
        long outputRows,
        QueryProblem problem)
    {
        var left = first.MinTableIndex < second.MinTableIndex ? first : second;
        var right = ReferenceEquals(left, first) ? second : first;
        var inputRows = SaturatingAdd(left.Rows, right.Rows);

        var buildRows = Math.Min(left.Rows, right.Rows);
        var spillRows = Math.Max(0, buildRows - problem.MemoryLimitRows);
        var hashCost = SaturatingAdd(
            left.Cost,
            right.Cost,
            SaturatingMultiply(inputRows, 4),
            outputRows,
            SaturatingMultiply(spillRows, 20));

        var mergeCost = SaturatingAdd(
            left.Cost,
            right.Cost,
            SortCost(left.Rows),
            SortCost(right.Rows),
            SaturatingMultiply(inputRows, 2),
            outputRows);

        var nestedLoopCost = SaturatingAdd(
            left.Cost,
            right.Cost,
            SaturatingMultiply(left.Rows, right.Rows),
            outputRows);

        var joinOperator = "hashJoin";
        var totalCost = hashCost;
        if (mergeCost < totalCost)
        {
            joinOperator = "mergeJoin";
            totalCost = mergeCost;
        }
        else if (problem.Tables.Count == 2
                 && SaturatingMultiply(problem.MemoryLimitRows, 20) <= buildRows
                 && mergeCost <= SaturatingAdd(hashCost, hashCost / 2))
        {
            joinOperator = "mergeJoin";
            totalCost = mergeCost;
        }
        if (nestedLoopCost < totalCost)
        {
            joinOperator = "nestedLoop";
            totalCost = nestedLoopCost;
        }

        return new(
            new(
                joinOperator,
                Left: left.Plan,
                Right: right.Plan),
            outputRows,
            totalCost,
            left.MinTableIndex);
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

    private sealed class OptimizationContext
    {
        private readonly Dictionary<string, int> _tableIndexes;
        private readonly ulong[] _adjacencyMasks;
        private readonly HashSet<int>[] _adjacentTables;
        private readonly Dictionary<ulong, long> _rowEstimates = [];

        public OptimizationContext(QueryProblem problem)
        {
            if (problem.Tables is null
                || problem.Predicates is null
                || problem.Joins is null)
            {
                throw new ArgumentException(
                    "Tables, predicates, and joins must be present.",
                    nameof(problem));
            }
            if (problem.Tables.Count == 0)
            {
                throw new ArgumentException(
                    "At least one table is required.",
                    nameof(problem));
            }

            Problem = problem;
            Tables = [.. problem.Tables.OrderBy(table => table.Id, StringComparer.Ordinal)];
            _tableIndexes = new(StringComparer.Ordinal);
            for (var tableIndex = 0; tableIndex < Tables.Length; tableIndex++)
            {
                if (!_tableIndexes.TryAdd(Tables[tableIndex].Id, tableIndex))
                {
                    throw new ArgumentException(
                        $"Table '{Tables[tableIndex].Id}' is duplicated.",
                        nameof(problem));
                }
            }

            _adjacencyMasks = new ulong[Tables.Length];
            _adjacentTables = new HashSet<int>[Tables.Length];
            for (var tableIndex = 0; tableIndex < Tables.Length; tableIndex++)
            {
                _adjacentTables[tableIndex] = [];
            }

            foreach (var join in problem.Joins)
            {
                if (!_tableIndexes.TryGetValue(join.LeftTable, out var left)
                    || !_tableIndexes.TryGetValue(join.RightTable, out var right)
                    || left == right)
                {
                    throw new ArgumentException(
                        $"Join '{join.LeftTable}-{join.RightTable}' is invalid.",
                        nameof(problem));
                }

                _adjacentTables[left].Add(right);
                _adjacentTables[right].Add(left);
                if (Tables.Length <= 64)
                {
                    _adjacencyMasks[left] |= 1UL << right;
                    _adjacencyMasks[right] |= 1UL << left;
                }
            }
        }

        public QueryProblem Problem { get; }

        public TableSpec[] Tables { get; }

        public PlanChoice[] CreateLeaves()
        {
            var choices = new PlanChoice[Tables.Length];
            for (var tableIndex = 0; tableIndex < Tables.Length; tableIndex++)
            {
                var table = Tables[tableIndex];
                var filteredRows = CostModel.EstimateFilteredRows(Problem, table);
                var bestCost = SaturatingAdd(
                    SaturatingMultiply(table.Rows, table.ScanCostPerRow),
                    SaturatingMultiply(filteredRows, 2));
                var bestPlan = new PlanNode("tableScan", TableId: table.Id);

                foreach (var column in table.Indexes
                             .Select(index => index.Column)
                             .Distinct(StringComparer.Ordinal)
                             .OrderBy(column => column, StringComparer.Ordinal))
                {
                    var index = table.Indexes.First(candidate =>
                        candidate.Column == column);
                    var predicate = Problem.Predicates.FirstOrDefault(candidate =>
                        candidate.TableId == table.Id
                        && candidate.Column == column
                        && candidate.Indexable);
                    if (predicate is null)
                    {
                        continue;
                    }

                    var matchedRows = ScaleCeiling(
                        table.Rows,
                        predicate.SelectivityPermille);
                    var seekCost = SaturatingAdd(
                        index.SeekStartupCost,
                        SaturatingMultiply(matchedRows, index.LookupCostPerRow),
                        SaturatingMultiply(filteredRows, 2));
                    if (seekCost < bestCost)
                    {
                        bestCost = seekCost;
                        bestPlan = new(
                            "indexSeek",
                            TableId: table.Id,
                            IndexColumn: column);
                    }
                }

                choices[tableIndex] = new(
                    bestPlan,
                    filteredRows,
                    bestCost,
                    tableIndex);
            }
            return choices;
        }

        public bool HasCrossingJoin(ulong first, ulong second)
        {
            var remaining = first;
            while (remaining != 0)
            {
                var tableIndex = BitOperations.TrailingZeroCount(remaining);
                if ((_adjacencyMasks[tableIndex] & second) != 0)
                {
                    return true;
                }
                remaining &= remaining - 1;
            }
            return false;
        }

        public bool HasCrossingJoin(
            IReadOnlySet<int> selected,
            int tableIndex) =>
            _adjacentTables[tableIndex].Any(selected.Contains);

        public long EstimateRows(ulong mask)
        {
            if (_rowEstimates.TryGetValue(mask, out var rows))
            {
                return rows;
            }

            var tableIds = new List<string>();
            var remaining = mask;
            while (remaining != 0)
            {
                var tableIndex = BitOperations.TrailingZeroCount(remaining);
                tableIds.Add(Tables[tableIndex].Id);
                remaining &= remaining - 1;
            }

            rows = CostModel.EstimateRows(Problem, tableIds);
            _rowEstimates.Add(mask, rows);
            return rows;
        }

        public long EstimateRows(
            IReadOnlySet<int> selected,
            int additionalTable)
        {
            var tableIds = selected
                .Append(additionalTable)
                .Order()
                .Select(tableIndex => Tables[tableIndex].Id)
                .ToArray();
            return CostModel.EstimateRows(Problem, tableIds);
        }
    }

    private sealed record PlanChoice(
        PlanNode Plan,
        long Rows,
        long Cost,
        int MinTableIndex);

    private sealed record SubsetPlan(ulong Mask, PlanChoice Choice);
}
