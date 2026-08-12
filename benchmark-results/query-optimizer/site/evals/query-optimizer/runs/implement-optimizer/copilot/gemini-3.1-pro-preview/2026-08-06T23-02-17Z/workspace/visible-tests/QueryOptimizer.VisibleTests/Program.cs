using QueryPlanning;

var helperTests = new (string Name, Action Body)[]
{
    ("scan and seek costs", TestScanAndSeekCosts),
    ("invalid cross join", TestInvalidCrossJoin),
    ("canonical child order", TestCanonicalChildOrder),
};
var engineTests = new (string Name, Action Body)[]
{
    ("single table index choice", TestSingleTableIndexChoice),
    ("three table join order", TestThreeTableJoinOrder),
    ("memory-aware join choice", TestMemoryAwareJoinChoice),
    ("deterministic output", TestDeterminism),
};
var tests = args.Contains("--helpers-only", StringComparer.Ordinal)
    ? helperTests
    : helperTests.Concat(engineTests).ToArray();

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} visible tests passed.");
if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.WriteLine($"- {failure}");
    }
    return 1;
}
return 0;

static QueryProblem Problem(
    int memory,
    List<TableSpec> tables,
    List<PredicateSpec>? predicates = null,
    List<JoinSpec>? joins = null) =>
    new(memory, tables, predicates ?? [], joins ?? []);

static TableSpec Table(
    string id,
    long rows,
    int scan = 3,
    params IndexSpec[] indexes) =>
    new(id, rows, scan, [.. indexes]);

static void TestScanAndSeekCosts()
{
    var problem = Problem(
        100,
        [Table("orders", 10_000, 3, new IndexSpec("customerId", 30, 2))],
        [new("orders", "customerId", 10)]);
    var scan = CostModel.ValidateAndCost(
        problem,
        new(new("tableScan", TableId: "orders")));
    var seek = CostModel.ValidateAndCost(
        problem,
        new(new("indexSeek", TableId: "orders", IndexColumn: "customerId")));
    True(scan.IsValid && seek.IsValid, "Expected valid leaf plans.");
    True(
        seek.Metrics!.TotalCost < scan.Metrics!.TotalCost,
        "Selective index seek should be cheaper.");
}

static void TestInvalidCrossJoin()
{
    var problem = Problem(100, [Table("a", 10), Table("b", 10)]);
    var result = new QueryPlan(new(
        "hashJoin",
        Left: new("tableScan", TableId: "a"),
        Right: new("tableScan", TableId: "b")));
    var report = CostModel.ValidateAndCost(problem, result);
    True(
        report.Issues.Any(issue => issue.Code == "cross_join"),
        "Missing join edge was accepted.");
}

static void TestCanonicalChildOrder()
{
    var problem = Problem(
        100,
        [Table("a", 10), Table("b", 10)],
        joins: [new("a", "b", 100)]);
    var result = new QueryPlan(new(
        "hashJoin",
        Left: new("tableScan", TableId: "b"),
        Right: new("tableScan", TableId: "a")));
    var report = CostModel.ValidateAndCost(problem, result);
    True(
        report.Issues.Any(issue => issue.Code == "noncanonical_children"),
        "Noncanonical children were accepted.");
}

static void TestSingleTableIndexChoice()
{
    var problem = Problem(
        100,
        [Table("events", 50_000, 4, new IndexSpec("tenant", 25, 2))],
        [new("events", "tenant", 5)]);
    var result = new QueryOptimizer().Optimize(problem);
    var report = CostModel.ValidateAndCost(problem, result);
    True(report.IsValid, JoinIssues(report));
    Equal("indexSeek", result.Plan!.Operator);
}

static void TestThreeTableJoinOrder()
{
    var problem = Problem(
        500,
        [
            Table("customers", 1_000),
            Table("orders", 80_000),
            Table("regions", 20),
        ],
        [new("customers", "region", 50)],
        [
            new("customers", "orders", 10),
            new("customers", "regions", 50),
        ]);
    var result = new QueryOptimizer().Optimize(problem);
    var report = CostModel.ValidateAndCost(problem, result);
    True(report.IsValid, JoinIssues(report));
    True(report.Metrics!.TotalCost < 2_000_000, "Plan cost is too high.");
}

static void TestMemoryAwareJoinChoice()
{
    var problem = Problem(
        10,
        [Table("a", 500), Table("b", 600)],
        joins: [new("a", "b", 10)]);

    var hjPlan = new QueryPlan(new PlanNode("hashJoin", Left: new PlanNode("tableScan", TableId: "a"), Right: new PlanNode("tableScan", TableId: "b")));
    var mjPlan = new QueryPlan(new PlanNode("mergeJoin", Left: new PlanNode("tableScan", TableId: "a"), Right: new PlanNode("tableScan", TableId: "b")));
    
    var hjReport = CostModel.ValidateAndCost(problem, hjPlan);
    var mjReport = CostModel.ValidateAndCost(problem, mjPlan);
    
    Console.WriteLine($"DEBUG TEST: HJ cost = {hjReport.Metrics?.TotalCost}, issues = {JoinIssues(hjReport)}");
    Console.WriteLine($"DEBUG TEST: MJ cost = {mjReport.Metrics?.TotalCost}, issues = {JoinIssues(mjReport)}");

    var result = new QueryOptimizer().Optimize(problem);
    var report = CostModel.ValidateAndCost(problem, result);
    True(report.IsValid, JoinIssues(report));
    Equal("mergeJoin", result.Plan!.Operator);
}

static void TestDeterminism()
{
    var problem = Problem(
        100,
        [Table("a", 100), Table("b", 200), Table("c", 300)],
        joins: [new("a", "b", 100), new("b", "c", 100)]);
    var optimizer = new QueryOptimizer();
    Equal(optimizer.Optimize(problem), optimizer.Optimize(problem));
}

static string JoinIssues(ValidationReport report) =>
    string.Join("; ", report.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));

static void Equal<T>(T expected, T actual)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

static void True(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
