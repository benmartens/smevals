using CartonPacking;

var helperTests = new (string Name, Action Body)[]
{
    ("orientation permutations", TestOrientationPermutations),
    ("touching cartons do not overlap", TestTouchingIsAllowed),
    ("full support may be shared", TestSharedSupport),
    ("partial support is rejected", TestPartialSupport),
};
var engineTests = new (string Name, Action Body)[]
{
    ("exact-fit packing", TestExactFitPacking),
    ("rotation-required packing", TestRotationRequiredPacking),
    ("value under weight limit", TestWeightValuePacking),
    ("upright restriction", TestUprightRestriction),
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

static void TestOrientationPermutations()
{
    var normal = new CartonType("normal", 2, 3, 4, 1, 1, 1);
    Equal(6, OrientationGenerator.GetOrientations(normal).Count);

    var upright = normal with { Id = "upright", KeepUpright = true };
    var orientations = OrientationGenerator.GetOrientations(upright);
    Equal(2, orientations.Count);
    True(orientations.All(o => o.Height == 4), "Upright height changed.");
}

static void TestTouchingIsAllowed()
{
    var left = new Placement("box", 0, 0, 0, 0, 2, 2, 2);
    var right = new Placement("box", 1, 2, 0, 0, 2, 2, 2);
    True(!PackingValidator.Overlaps(left, right), "Touching faces overlapped.");
}

static void TestSharedSupport()
{
    var left = new Placement("base", 0, 0, 0, 0, 2, 4, 2);
    var right = new Placement("base", 1, 2, 0, 0, 2, 4, 2);
    var top = new Placement("top", 0, 0, 0, 2, 4, 4, 1);
    True(
        PackingValidator.HasFullBaseSupport(top, [left, right, top]),
        "Two lower cartons should fully support the top carton.");
}

static void TestPartialSupport()
{
    var baseCarton = new Placement("base", 0, 0, 0, 0, 2, 4, 2);
    var top = new Placement("top", 0, 0, 0, 2, 4, 4, 1);
    True(
        !PackingValidator.HasFullBaseSupport(top, [baseCarton, top]),
        "Half-supported carton was accepted.");
}

static void TestExactFitPacking()
{
    var problem = new PackingProblem(
        new(10, 10, 10, 100),
        [new("cube", 5, 5, 5, 8, 10, 10)]);
    var result = new CartonPacker().Pack(problem);
    var report = PackingValidator.Validate(problem, result);
    True(report.IsValid, JoinIssues(report));
    Equal(8, result.Placements.Count);
    Equal(80L, report.TotalValue);
}

static void TestRotationRequiredPacking()
{
    var problem = new PackingProblem(
        new(6, 4, 3, 20),
        [new("rotated", 3, 6, 4, 1, 5, 12)]);
    var result = new CartonPacker().Pack(problem);
    var report = PackingValidator.Validate(problem, result);
    True(report.IsValid, JoinIssues(report));
    Equal(1, result.Placements.Count);
}

static void TestWeightValuePacking()
{
    var problem = new PackingProblem(
        new(6, 6, 3, 10),
        [
            new("heavy", 3, 3, 3, 2, 8, 13),
            new("light", 3, 3, 3, 4, 5, 9),
        ]);
    var result = new CartonPacker().Pack(problem);
    var report = PackingValidator.Validate(problem, result);
    True(report.IsValid, JoinIssues(report));
    Equal(18L, report.TotalValue);
}

static void TestUprightRestriction()
{
    var problem = new PackingProblem(
        new(6, 4, 3, 50),
        [new("upright", 6, 3, 4, 1, 5, 20, KeepUpright: true)]);
    var result = new CartonPacker().Pack(problem);
    var report = PackingValidator.Validate(problem, result);
    True(report.IsValid, JoinIssues(report));
    Equal(0, result.Placements.Count);
}

static void TestDeterminism()
{
    var problem = new PackingProblem(
        new(8, 8, 4, 100),
        [
            new("a", 4, 4, 2, 4, 2, 4),
            new("b", 2, 2, 2, 8, 1, 2),
        ]);
    var packer = new CartonPacker();
    var first = packer.Pack(problem);
    var second = packer.Pack(problem);
    True(
        first.Placements.SequenceEqual(second.Placements),
        "Repeated calls returned different placements.");
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
