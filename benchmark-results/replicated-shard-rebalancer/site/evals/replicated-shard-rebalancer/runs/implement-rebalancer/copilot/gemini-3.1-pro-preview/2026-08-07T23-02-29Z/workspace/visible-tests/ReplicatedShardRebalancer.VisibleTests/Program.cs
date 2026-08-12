using ReplicatedShardRebalancer;

var helperTests = new (string Name, Action Body)[]
{
    ("valid target metrics", TestValidTargetMetrics),
    ("duplicate replica rejected", TestDuplicateReplica),
    ("exclusion rejected", TestExclusion),
    ("zone diversity required", TestZoneDiversity),
    ("capacity enforced", TestCapacity),
    ("canonical ordering required", TestCanonicalOrdering),
};
var engineTests = new (string Name, Action Body)[]
{
    ("overload is repaired", TestOverloadRepair),
    ("exclusions are honored", TestEngineExclusion),
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
    Console.WriteLine("Failures:");
    foreach (var failure in failures)
    {
        Console.WriteLine($"- {failure}");
    }
    return 1;
}
return 0;

static RebalanceProblem BasicProblem() => new(
    [
        new("a", "z1", 10),
        new("b", "z1", 10),
        new("c", "z2", 10),
        new("d", "z2", 10),
    ],
    [
        new("s1", 5, 2),
        new("s2", 5, 2),
    ],
    [
        new("s1", ["a", "c"]),
        new("s2", ["a", "c"]),
    ],
    []);

static void TestValidTargetMetrics()
{
    var result = new RebalanceResult(
        [new("s1", ["a", "c"]), new("s2", ["b", "d"])]);
    var report = RebalanceValidator.Validate(BasicProblem(), result);
    True(report.IsValid, JoinIssues(report));
    Equal(0.5, report.MaximumNodeUtilization);
    Equal(0.0, report.UtilizationSpread);
    Equal(10L, report.MovedBytes);
    Equal(2, report.MovedReplicaCount);
}

static void TestDuplicateReplica()
{
    var result = new RebalanceResult(
        [new("s1", ["a", "a"]), new("s2", ["b", "d"])]);
    HasIssue(BasicProblem(), result, "duplicate_node");
}

static void TestExclusion()
{
    var problem = BasicProblem() with
    {
        Exclusions = [new("s1", "a")],
    };
    var result = new RebalanceResult(
        [new("s1", ["a", "c"]), new("s2", ["b", "d"])]);
    HasIssue(problem, result, "excluded_node");
}

static void TestZoneDiversity()
{
    var result = new RebalanceResult(
        [new("s1", ["a", "b"]), new("s2", ["c", "d"])]);
    HasIssue(BasicProblem(), result, "zone_diversity");
}

static void TestCapacity()
{
    var result = new RebalanceResult(
        [new("s1", ["a", "c"]), new("s2", ["a", "c"])]);
    var problem = BasicProblem() with
    {
        Nodes =
        [
            new("a", "z1", 9),
            new("b", "z1", 10),
            new("c", "z2", 9),
            new("d", "z2", 10),
        ],
    };
    HasIssue(problem, result, "capacity_exceeded");
}

static void TestCanonicalOrdering()
{
    var result = new RebalanceResult(
        [new("s2", ["d", "b"]), new("s1", ["c", "a"])]);
    var report = RebalanceValidator.Validate(BasicProblem(), result);
    var codes = report.Issues.Select(issue => issue.Code).ToHashSet();
    True(codes.Contains("noncanonical_shard_order"), "Shard ordering accepted.");
    True(codes.Contains("noncanonical_node_order"), "Node ordering accepted.");
}

static void TestOverloadRepair()
{
    var result = new ReplicatedShardRebalancer.ReplicatedShardRebalancer()
        .Rebalance(BasicProblem());
    var report = RebalanceValidator.Validate(BasicProblem(), result);
    True(report.IsValid, JoinIssues(report));
    True(report.MaximumNodeUtilization <= 0.5, "Cluster was not balanced.");
}

static void TestEngineExclusion()
{
    var problem = BasicProblem() with
    {
        Exclusions = [new("s1", "a")],
    };
    var result = new ReplicatedShardRebalancer.ReplicatedShardRebalancer()
        .Rebalance(problem);
    var report = RebalanceValidator.Validate(problem, result);
    True(report.IsValid, JoinIssues(report));
}

static void TestDeterminism()
{
    var rebalancer = new ReplicatedShardRebalancer.ReplicatedShardRebalancer();
    var first = rebalancer.Rebalance(BasicProblem());
    var second = rebalancer.Rebalance(BasicProblem());
    True(
        first.TargetPlacements.Count == second.TargetPlacements.Count
        && first.TargetPlacements.Zip(second.TargetPlacements).All(pair =>
            pair.First.ShardId == pair.Second.ShardId
            && pair.First.NodeIds.SequenceEqual(pair.Second.NodeIds)),
        "Repeated calls returned different placements.");
}

static void HasIssue(
    RebalanceProblem problem,
    RebalanceResult result,
    string code)
{
    var report = RebalanceValidator.Validate(problem, result);
    True(
        report.Issues.Any(issue => issue.Code == code),
        $"Expected issue '{code}', got {JoinIssues(report)}.");
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
