using FieldServiceRoutePlanner;

var helperTests = new (string Name, Action Body)[]
{
    ("validator recomputes waiting and return", TestWaitingAndReturn),
    ("validator rejects missing skills", TestMissingSkills),
    ("validator rejects duplicate assignment", TestDuplicateAssignment),
    ("validator enforces canonical routes", TestCanonicalRoutes),
};
var engineTests = new (string Name, Action Body)[]
{
    ("plans a basic job", TestBasicPlan),
    ("assigns by skill", TestSkillAssignment),
    ("prefers combined value", TestValueChoice),
    ("returns deterministic output", TestDeterminism),
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
foreach (var failure in failures)
{
    Console.WriteLine($"- {failure}");
}
return failures.Count == 0 ? 0 : 1;

static void TestWaitingAndReturn()
{
    var problem = Problem(
        [new("tech", ["repair"], 0, 100)],
        [new("job", "a", ["repair"], 10, 30, 50, 7)],
        Matrix(("depot", "a", 5), ("a", "depot", 9)));
    var report = RouteValidator.Validate(
        problem,
        new([new("tech", ["job"])]));
    True(report.IsValid, Issues(report));
    Equal(14, report.TotalTravel);
    Equal(40, report.RouteTimings[0].Stops[0].ServiceEnd);
    Equal(49, report.RouteTimings[0].ReturnTime);
}

static void TestMissingSkills()
{
    var problem = Problem(
        [new("tech", ["plumbing"], 0, 100)],
        [new("job", "a", ["electrical"], 5, 0, 80, 10)],
        Matrix(("depot", "a", 5), ("a", "depot", 5)));
    var report = RouteValidator.Validate(
        problem,
        new([new("tech", ["job"])]));
    True(
        report.Issues.Any(issue => issue.Code == "missing_skills"),
        "Missing skill was accepted.");
}

static void TestDuplicateAssignment()
{
    var problem = Problem(
        [new("a", ["repair"], 0, 100), new("b", ["repair"], 0, 100)],
        [new("job", "x", ["repair"], 5, 0, 80, 10)],
        Matrix(("depot", "x", 5), ("x", "depot", 5)));
    var report = RouteValidator.Validate(
        problem,
        new([new("a", ["job"]), new("b", ["job"])]));
    True(
        report.Issues.Any(issue => issue.Code == "duplicate_job"),
        "Duplicate job was accepted.");
}

static void TestCanonicalRoutes()
{
    var problem = Problem(
        [new("a", [], 0, 100), new("b", [], 0, 100)],
        [],
        Matrix());
    var report = RouteValidator.Validate(
        problem,
        new([new("b", []), new("a", [])]));
    True(
        report.Issues.Any(issue => issue.Code == "noncanonical_routes"),
        "Noncanonical route order was accepted.");
}

static void TestBasicPlan()
{
    var problem = Problem(
        [new("tech", ["repair"], 0, 100)],
        [new("job", "a", ["repair"], 10, 0, 80, 20)],
        Matrix(("depot", "a", 5), ("a", "depot", 5)));
    var result = new RoutePlanner().Plan(problem);
    var report = RouteValidator.Validate(problem, result);
    True(report.IsValid, Issues(report));
    Equal(20, report.ServedValue);
}

static void TestSkillAssignment()
{
    var problem = Problem(
        [
            new("electric", ["electrical"], 0, 100),
            new("plumber", ["plumbing"], 0, 100),
        ],
        [new("job", "a", ["plumbing"], 10, 0, 80, 20)],
        Matrix(("depot", "a", 5), ("a", "depot", 5)));
    var result = new RoutePlanner().Plan(problem);
    var report = RouteValidator.Validate(problem, result);
    True(report.IsValid, Issues(report));
    Equal(["job"], result.Routes.Single(r => r.TechnicianId == "plumber").JobIds);
}

static void TestValueChoice()
{
    var problem = Problem(
        [new("tech", ["repair"], 0, 45)],
        [
            new("high", "h", ["repair"], 20, 0, 40, 12),
            new("left", "l", ["repair"], 5, 0, 35, 8),
            new("right", "r", ["repair"], 5, 0, 35, 8),
        ],
        Matrix(
            ("depot", "h", 10), ("h", "depot", 10),
            ("depot", "l", 5), ("l", "r", 5), ("r", "depot", 5),
            ("depot", "r", 5), ("r", "l", 5), ("l", "depot", 5),
            ("h", "l", 20), ("h", "r", 20),
            ("l", "h", 20), ("r", "h", 20)));
    var result = new RoutePlanner().Plan(problem);
    var report = RouteValidator.Validate(problem, result);
    True(report.IsValid, Issues(report));
    Equal(16, report.ServedValue);
}

static void TestDeterminism()
{
    var problem = Problem(
        [new("tech", ["repair"], 0, 100)],
        [
            new("a", "x", ["repair"], 5, 0, 80, 10),
            new("b", "y", ["repair"], 5, 0, 80, 10),
        ],
        Matrix(
            ("depot", "x", 5), ("x", "depot", 5),
            ("depot", "y", 5), ("y", "depot", 5),
            ("x", "y", 5), ("y", "x", 5)));
    var planner = new RoutePlanner();
    var first = planner.Plan(problem);
    var second = planner.Plan(problem);
    True(
        first.Routes.Count == second.Routes.Count
        && first.Routes.Zip(second.Routes).All(pair =>
            pair.First.TechnicianId == pair.Second.TechnicianId
            && pair.First.JobIds.SequenceEqual(
                pair.Second.JobIds,
                StringComparer.Ordinal)),
        "Repeated calls returned different routes.");
}

static RoutePlanningProblem Problem(
    List<Technician> technicians,
    List<ServiceJob> jobs,
    Dictionary<string, Dictionary<string, int>> matrix) =>
    new("depot", matrix, technicians, jobs);

static Dictionary<string, Dictionary<string, int>> Matrix(
    params (string From, string To, int Minutes)[] edges)
{
    var locations = edges.SelectMany(edge => new[] { edge.From, edge.To })
        .Append("depot")
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    var matrix = locations.ToDictionary(
        location => location,
        _ => locations.ToDictionary(other => other, _ => 50));
    foreach (var location in locations)
    {
        matrix[location][location] = 0;
    }
    foreach (var edge in edges)
    {
        matrix[edge.From][edge.To] = edge.Minutes;
    }
    return matrix;
}

static string Issues(RouteValidationReport report) =>
    string.Join("; ", report.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));

static void Equal<T>(T expected, T actual)
    where T : notnull
{
    if (expected is IEnumerable<string> expectedItems
        && actual is IEnumerable<string> actualItems)
    {
        if (!expectedItems.SequenceEqual(actualItems, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Sequences differ.");
        }
        return;
    }
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
