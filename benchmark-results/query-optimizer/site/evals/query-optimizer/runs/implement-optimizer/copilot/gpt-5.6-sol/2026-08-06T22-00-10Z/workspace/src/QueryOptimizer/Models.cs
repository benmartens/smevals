namespace QueryPlanning;

public sealed record IndexSpec(
    string Column,
    int SeekStartupCost,
    int LookupCostPerRow);

public sealed record TableSpec(
    string Id,
    long Rows,
    int ScanCostPerRow,
    List<IndexSpec> Indexes);

public sealed record PredicateSpec(
    string TableId,
    string Column,
    int SelectivityPermille,
    bool Indexable = true);

public sealed record JoinSpec(
    string LeftTable,
    string RightTable,
    int SelectivityPermille);

public sealed record QueryProblem(
    int MemoryLimitRows,
    List<TableSpec> Tables,
    List<PredicateSpec> Predicates,
    List<JoinSpec> Joins);

public sealed record PlanNode(
    string Operator,
    string? TableId = null,
    string? IndexColumn = null,
    PlanNode? Left = null,
    PlanNode? Right = null);

public sealed record QueryPlan(PlanNode? Plan)
{
    public static QueryPlan Empty { get; } = new((PlanNode?)null);
}

public sealed record ValidationIssue(string Code, string Message);

public sealed record PlanMetrics(
    long EstimatedRows,
    long TotalCost,
    long PeakMemoryRows,
    int OperatorCount);

public sealed record ValidationReport(
    List<ValidationIssue> Issues,
    PlanMetrics? Metrics)
{
    public bool IsValid => Issues.Count == 0 && Metrics is not null;
}
