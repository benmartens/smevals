namespace QueryPlanning;

public static class CostModel
{
    public const long CostCap = 9_000_000_000_000_000;

    public static ValidationReport ValidateAndCost(
        QueryProblem problem,
        QueryPlan result)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(result);

        var issues = new List<ValidationIssue>();
        var tables = ValidateProblem(problem, issues);
        if (result.Plan is null)
        {
            issues.Add(new("missing_plan", "The result must contain a plan."));
            return new(issues, null);
        }

        var evaluated = EvaluateNode(problem, result.Plan, tables, issues);
        if (evaluated is not null)
        {
            var expected = tables.Keys.Order(StringComparer.Ordinal).ToArray();
            var actual = evaluated.Tables.Order(StringComparer.Ordinal).ToArray();
            if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            {
                issues.Add(new(
                    "table_coverage",
                    "The root plan must contain every table exactly once."));
            }
        }

        return issues.Count == 0 && evaluated is not null
            ? new(issues, new(
                evaluated.Rows,
                evaluated.Cost,
                evaluated.PeakMemoryRows,
                evaluated.OperatorCount))
            : new(issues, null);
    }

    public static long EstimateRows(
        QueryProblem problem,
        IReadOnlyCollection<string> tableIds)
    {
        var selected = tableIds.ToHashSet(StringComparer.Ordinal);
        long rows = 1;
        foreach (var table in problem.Tables
                     .Where(table => selected.Contains(table.Id))
                     .OrderBy(table => table.Id, StringComparer.Ordinal))
        {
            rows = SaturatingMultiply(rows, EstimateFilteredRows(problem, table));
        }

        foreach (var join in problem.Joins
                     .Where(join =>
                         selected.Contains(join.LeftTable)
                         && selected.Contains(join.RightTable))
                     .OrderBy(join => MinId(join), StringComparer.Ordinal)
                     .ThenBy(join => MaxId(join), StringComparer.Ordinal))
        {
            rows = ScaleCeiling(rows, join.SelectivityPermille);
        }

        return Math.Max(1, rows);
    }

    public static long EstimateFilteredRows(
        QueryProblem problem,
        TableSpec table)
    {
        var rows = table.Rows;
        foreach (var predicate in problem.Predicates
                     .Where(predicate => predicate.TableId == table.Id)
                     .OrderBy(predicate => predicate.Column, StringComparer.Ordinal))
        {
            rows = ScaleCeiling(rows, predicate.SelectivityPermille);
        }
        return Math.Max(1, rows);
    }

    private static NodeEvaluation? EvaluateNode(
        QueryProblem problem,
        PlanNode node,
        IReadOnlyDictionary<string, TableSpec> tables,
        List<ValidationIssue> issues)
    {
        var op = node.Operator ?? "";
        if (op is "tableScan" or "indexSeek")
        {
            return EvaluateLeaf(problem, node, op, tables, issues);
        }
        if (op is not ("nestedLoop" or "hashJoin" or "mergeJoin"))
        {
            issues.Add(new("unknown_operator", $"Unknown operator '{op}'."));
            return null;
        }
        if (node.TableId is not null || node.IndexColumn is not null)
        {
            issues.Add(new(
                "join_fields",
                $"Join operator '{op}' may not set tableId or indexColumn."));
        }
        if (node.Left is null || node.Right is null)
        {
            issues.Add(new("missing_child", $"Join operator '{op}' needs two children."));
            return null;
        }

        var left = EvaluateNode(problem, node.Left, tables, issues);
        var right = EvaluateNode(problem, node.Right, tables, issues);
        if (left is null || right is null)
        {
            return null;
        }
        if (left.Tables.Overlaps(right.Tables))
        {
            issues.Add(new("duplicate_table", "A table appears more than once in the plan."));
        }
        if (StringComparer.Ordinal.Compare(left.MinTableId, right.MinTableId) >= 0)
        {
            issues.Add(new(
                "noncanonical_children",
                "Join children must be ordered by their smallest table ID."));
        }
        if (!HasCrossingJoin(problem, left.Tables, right.Tables))
        {
            issues.Add(new(
                "cross_join",
                "Every join node must have at least one declared join edge crossing its children."));
        }

        var allTables = new HashSet<string>(left.Tables, StringComparer.Ordinal);
        allTables.UnionWith(right.Tables);
        var rows = EstimateRows(problem, allTables);
        var inputRows = SaturatingAdd(left.Rows, right.Rows);
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
                var buildRows = Math.Min(left.Rows, right.Rows);
                var spillRows = Math.Max(0, buildRows - problem.MemoryLimitRows);
                localCost = SaturatingAdd(
                    SaturatingMultiply(inputRows, 4),
                    rows,
                    SaturatingMultiply(spillRows, 40));
                localMemory = Math.Min(buildRows, problem.MemoryLimitRows);
                break;
            default:
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

        return new(
            allTables,
            rows,
            SaturatingAdd(left.Cost, right.Cost, localCost),
            Math.Max(localMemory, Math.Max(left.PeakMemoryRows, right.PeakMemoryRows)),
            left.OperatorCount + right.OperatorCount + 1);
    }

    private static NodeEvaluation? EvaluateLeaf(
        QueryProblem problem,
        PlanNode node,
        string op,
        IReadOnlyDictionary<string, TableSpec> tables,
        List<ValidationIssue> issues)
    {
        if (node.Left is not null || node.Right is not null)
        {
            issues.Add(new("leaf_children", $"Leaf operator '{op}' may not have children."));
        }
        if (string.IsNullOrWhiteSpace(node.TableId)
            || !tables.TryGetValue(node.TableId, out var table))
        {
            issues.Add(new("unknown_table", $"Unknown table '{node.TableId}'."));
            return null;
        }

        var filteredRows = EstimateFilteredRows(problem, table);
        long cost;
        if (op == "tableScan")
        {
            if (node.IndexColumn is not null)
            {
                issues.Add(new(
                    "scan_index",
                    "tableScan may not set indexColumn."));
            }
            cost = SaturatingAdd(
                SaturatingMultiply(table.Rows, table.ScanCostPerRow),
                SaturatingMultiply(filteredRows, 2));
        }
        else
        {
            var index = table.Indexes.FirstOrDefault(index =>
                index.Column == node.IndexColumn);
            var predicate = problem.Predicates.FirstOrDefault(predicate =>
                predicate.TableId == table.Id
                && predicate.Column == node.IndexColumn
                && predicate.Indexable);
            if (index is null || predicate is null)
            {
                issues.Add(new(
                    "invalid_index_seek",
                    $"Table '{table.Id}' has no usable index predicate for '{node.IndexColumn}'."));
                return null;
            }
            var matchedRows = ScaleCeiling(
                table.Rows,
                predicate.SelectivityPermille);
            cost = SaturatingAdd(
                index.SeekStartupCost,
                SaturatingMultiply(matchedRows, index.LookupCostPerRow),
                SaturatingMultiply(filteredRows, 2));
        }

        return new(
            new HashSet<string>([table.Id], StringComparer.Ordinal),
            filteredRows,
            cost,
            1,
            1);
    }

    private static Dictionary<string, TableSpec> ValidateProblem(
        QueryProblem problem,
        List<ValidationIssue> issues)
    {
        var tables = new Dictionary<string, TableSpec>(StringComparer.Ordinal);
        if (problem.MemoryLimitRows <= 0)
        {
            issues.Add(new("invalid_memory", "memoryLimitRows must be positive."));
        }
        foreach (var table in problem.Tables)
        {
            if (string.IsNullOrWhiteSpace(table.Id)
                || table.Rows <= 0
                || table.ScanCostPerRow <= 0)
            {
                issues.Add(new("invalid_table", $"Table '{table.Id}' is invalid."));
                continue;
            }
            if (!tables.TryAdd(table.Id, table))
            {
                issues.Add(new("duplicate_table_id", $"Table '{table.Id}' is duplicated."));
            }
            if (table.Indexes.Any(index =>
                    string.IsNullOrWhiteSpace(index.Column)
                    || index.SeekStartupCost < 0
                    || index.LookupCostPerRow <= 0))
            {
                issues.Add(new("invalid_index", $"Table '{table.Id}' has an invalid index."));
            }
        }
        foreach (var predicate in problem.Predicates)
        {
            if (!tables.ContainsKey(predicate.TableId)
                || string.IsNullOrWhiteSpace(predicate.Column)
                || predicate.SelectivityPermille is < 1 or > 1000)
            {
                issues.Add(new(
                    "invalid_predicate",
                    $"Predicate '{predicate.TableId}.{predicate.Column}' is invalid."));
            }
        }
        foreach (var join in problem.Joins)
        {
            if (!tables.ContainsKey(join.LeftTable)
                || !tables.ContainsKey(join.RightTable)
                || join.LeftTable == join.RightTable
                || join.SelectivityPermille is < 1 or > 1000)
            {
                issues.Add(new(
                    "invalid_join",
                    $"Join '{join.LeftTable}-{join.RightTable}' is invalid."));
            }
        }
        return tables;
    }

    private static bool HasCrossingJoin(
        QueryProblem problem,
        HashSet<string> left,
        HashSet<string> right) =>
        problem.Joins.Any(join =>
            (left.Contains(join.LeftTable) && right.Contains(join.RightTable))
            || (left.Contains(join.RightTable) && right.Contains(join.LeftTable)));

    private static string MinId(JoinSpec join) =>
        StringComparer.Ordinal.Compare(join.LeftTable, join.RightTable) <= 0
            ? join.LeftTable
            : join.RightTable;

    private static string MaxId(JoinSpec join) =>
        StringComparer.Ordinal.Compare(join.LeftTable, join.RightTable) <= 0
            ? join.RightTable
            : join.LeftTable;

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

    private sealed record NodeEvaluation(
        HashSet<string> Tables,
        long Rows,
        long Cost,
        long PeakMemoryRows,
        int OperatorCount)
    {
        public string MinTableId => Tables.Min(StringComparer.Ordinal)!;
    }
}
