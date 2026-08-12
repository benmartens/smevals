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
        if (tables.Length == 0
            || tables.Any(table =>
                string.IsNullOrWhiteSpace(table.Id)
                || table.Rows <= 0
                || table.ScanCostPerRow <= 0)
            || tables.Select(table => table.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() != tables.Length)
        {
            return QueryPlan.Empty;
        }

        var tableBits = tables
            .Select((table, index) => (table.Id, Bit: BigInteger.One << index))
            .ToDictionary(item => item.Id, item => item.Bit, StringComparer.Ordinal);
        var joinEdges = problem.Joins
            .Where(join =>
                tableBits.ContainsKey(join.LeftTable)
                && tableBits.ContainsKey(join.RightTable))
            .Select(join => new JoinEdge(
                tableBits[join.LeftTable],
                tableBits[join.RightTable]))
            .ToArray();

        var best = new Dictionary<BigInteger, PlanState>();
        for (var index = 0; index < tables.Length; index++)
        {
            var table = tables[index];
            var filteredRows = CostModel.EstimateFilteredRows(problem, table);
            var candidates = new List<PlanState>
            {
                new(
                    new PlanNode("tableScan", TableId: table.Id),
                    filteredRows,
                    SaturatingAdd(
                        SaturatingMultiply(table.Rows, table.ScanCostPerRow),
                        SaturatingMultiply(filteredRows, 2)),
                    $"tableScan({table.Id})")
            };

            var seenIndexColumns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var indexSpec in table.Indexes)
            {
                if (!seenIndexColumns.Add(indexSpec.Column))
                {
                    continue;
                }

                var predicate = problem.Predicates.FirstOrDefault(candidate =>
                    candidate.TableId == table.Id
                    && candidate.Column == indexSpec.Column
                    && candidate.Indexable);
                if (predicate is null)
                {
                    continue;
                }

                var matchedRows = ScaleCeiling(
                    table.Rows,
                    predicate.SelectivityPermille);
                candidates.Add(new(
                    new PlanNode(
                        "indexSeek",
                        TableId: table.Id,
                        IndexColumn: indexSpec.Column),
                    filteredRows,
                    SaturatingAdd(
                        indexSpec.SeekStartupCost,
                        SaturatingMultiply(
                            matchedRows,
                            indexSpec.LookupCostPerRow),
                        SaturatingMultiply(filteredRows, 2)),
                    $"indexSeek({table.Id},{indexSpec.Column})"));
            }

            best[BigInteger.One << index] = ChooseBetter(candidates);
        }

        var allTables = (BigInteger.One << tables.Length) - BigInteger.One;
        var rowsByMask = new Dictionary<BigInteger, long>();

        long RowsFor(BigInteger mask)
        {
            if (rowsByMask.TryGetValue(mask, out var rows))
            {
                return rows;
            }

            var selectedIds = new List<string>();
            for (var index = 0; index < tables.Length; index++)
            {
                if ((mask & (BigInteger.One << index)) != BigInteger.Zero)
                {
                    selectedIds.Add(tables[index].Id);
                }
            }

            rows = CostModel.EstimateRows(problem, selectedIds);
            rowsByMask[mask] = rows;
            return rows;
        }

        for (var mask = BigInteger.One; mask <= allTables; mask++)
        {
            if (IsSingleBit(mask) || !IsConnected(mask, joinEdges))
            {
                continue;
            }

            var lowestBit = mask & -mask;
            var remaining = mask ^ lowestBit;
            PlanState? current = null;

            for (var leftExtra = remaining; ; leftExtra = (leftExtra - BigInteger.One) & remaining)
            {
                var leftMask = leftExtra | lowestBit;
                var rightMask = mask ^ leftMask;
                if (rightMask != BigInteger.Zero
                    && best.TryGetValue(leftMask, out var left)
                    && best.TryGetValue(rightMask, out var right)
                    && HasCrossingJoin(leftMask, rightMask, joinEdges))
                {
                    var rows = RowsFor(mask);
                    var inputRows = SaturatingAdd(left.Rows, right.Rows);
                    foreach (var op in JoinOperators)
                    {
                        if (op == "hashJoin"
                            && Math.Min(left.Rows, right.Rows) > problem.MemoryLimitRows)
                        {
                            continue;
                        }

                        var localCost = op switch
                        {
                            "nestedLoop" => SaturatingAdd(
                                SaturatingMultiply(left.Rows, right.Rows),
                                rows),
                            "hashJoin" => HashJoinCost(
                                left.Rows,
                                right.Rows,
                                rows,
                                problem.MemoryLimitRows),
                            _ => SaturatingAdd(
                                SortCost(left.Rows),
                                SortCost(right.Rows),
                                SaturatingMultiply(inputRows, 2),
                                rows)
                        };
                        var candidate = new PlanState(
                            new PlanNode(
                                op,
                                Left: left.Plan,
                                Right: right.Plan),
                            rows,
                            SaturatingAdd(left.Cost, right.Cost, localCost),
                            $"{op}({left.TieBreak},{right.TieBreak})");
                        if (current is null || IsBetter(candidate, current))
                        {
                            current = candidate;
                        }
                    }
                }

                if (leftExtra == BigInteger.Zero)
                {
                    break;
                }
            }

            if (current is not null)
            {
                best[mask] = current;
            }
        }

        return best.TryGetValue(allTables, out var result)
            ? new QueryPlan(result.Plan)
            : QueryPlan.Empty;
    }

    private static PlanState ChooseBetter(IEnumerable<PlanState> candidates)
    {
        PlanState? best = null;
        foreach (var candidate in candidates)
        {
            if (best is null || IsBetter(candidate, best))
            {
                best = candidate;
            }
        }

        return best!;
    }

    private static bool IsBetter(PlanState candidate, PlanState current) =>
        candidate.Cost < current.Cost
        || (candidate.Cost == current.Cost
            && StringComparer.Ordinal.Compare(
                candidate.TieBreak,
                current.TieBreak) < 0);

    private static bool IsSingleBit(BigInteger mask) =>
        mask != BigInteger.Zero
        && (mask & (mask - BigInteger.One)) == BigInteger.Zero;

    private static bool IsConnected(
        BigInteger mask,
        IReadOnlyList<JoinEdge> edges)
    {
        var reached = mask & -mask;
        while (true)
        {
            var expanded = reached;
            foreach (var edge in edges)
            {
                if ((edge.Left & reached) != BigInteger.Zero
                    && (edge.Right & mask) != BigInteger.Zero)
                {
                    expanded |= edge.Right;
                }
                if ((edge.Right & reached) != BigInteger.Zero
                    && (edge.Left & mask) != BigInteger.Zero)
                {
                    expanded |= edge.Left;
                }
            }

            if (expanded == reached)
            {
                return reached == mask;
            }

            reached = expanded;
        }
    }

    private static bool HasCrossingJoin(
        BigInteger left,
        BigInteger right,
        IReadOnlyList<JoinEdge> edges) =>
        edges.Any(edge =>
            ((edge.Left & left) != BigInteger.Zero
                && (edge.Right & right) != BigInteger.Zero)
            || ((edge.Right & left) != BigInteger.Zero
                && (edge.Left & right) != BigInteger.Zero));

    private static long HashJoinCost(
        long leftRows,
        long rightRows,
        long rows,
        int memoryLimitRows)
    {
        var inputRows = SaturatingAdd(leftRows, rightRows);
        var buildRows = Math.Min(leftRows, rightRows);
        var spillRows = Math.Max(0, buildRows - memoryLimitRows);
        return SaturatingAdd(
            SaturatingMultiply(inputRows, 4),
            rows,
            SaturatingMultiply(spillRows, 20));
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

    private static readonly string[] JoinOperators =
        ["nestedLoop", "hashJoin", "mergeJoin"];

    private sealed record JoinEdge(BigInteger Left, BigInteger Right);

    private sealed record PlanState(
        PlanNode Plan,
        long Rows,
        long Cost,
        string TieBreak);
}
