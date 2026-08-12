namespace CartonPacking;

public sealed record ContainerSpec(
    int Width,
    int Depth,
    int Height,
    int MaxWeight);

public sealed record CartonType(
    string Id,
    int Width,
    int Depth,
    int Height,
    int Quantity,
    int Weight,
    int Value,
    bool KeepUpright = false);

public sealed record PackingProblem(
    ContainerSpec Container,
    List<CartonType> Cartons);

public sealed record Placement(
    string CartonId,
    int Instance,
    int X,
    int Y,
    int Z,
    int Width,
    int Depth,
    int Height);

public sealed record PackingResult(List<Placement> Placements)
{
    public static PackingResult Empty { get; } = new([]);
}

public readonly record struct OrientedDimensions(
    int Width,
    int Depth,
    int Height)
{
    public long Volume => (long)Width * Depth * Height;
}

public sealed record ValidationIssue(string Code, string Message);

/// <summary>
/// Validation issues plus raw totals for all placements with known carton IDs.
/// Treat the totals as objective values only when <see cref="IsValid"/> is true.
/// </summary>
public sealed record ValidationReport(
    List<ValidationIssue> Issues,
    long TotalWeight,
    long TotalValue,
    long TotalVolume)
{
    public bool IsValid => Issues.Count == 0;
}
