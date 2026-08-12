namespace CartonPacking;

public static class PackingValidator
{
    public static ValidationReport Validate(
        PackingProblem problem,
        PackingResult result)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(result);

        var issues = new List<ValidationIssue>();
        var cartonById = new Dictionary<string, CartonType>(
            StringComparer.Ordinal);

        ValidateProblem(problem, cartonById, issues);

        var seenInstances = new HashSet<(string Id, int Instance)>();
        long totalWeight = 0;
        long totalValue = 0;
        long totalVolume = 0;

        foreach (var placement in result.Placements)
        {
            if (!cartonById.TryGetValue(placement.CartonId, out var carton))
            {
                issues.Add(new(
                    "unknown_carton",
                    $"Unknown carton ID '{placement.CartonId}'."));
                continue;
            }

            if (placement.Instance < 0 || placement.Instance >= carton.Quantity)
            {
                issues.Add(new(
                    "invalid_instance",
                    $"{placement.CartonId} instance {placement.Instance} is outside "
                    + $"0..{carton.Quantity - 1}."));
            }
            else if (!seenInstances.Add((placement.CartonId, placement.Instance)))
            {
                issues.Add(new(
                    "duplicate_instance",
                    $"{placement.CartonId} instance {placement.Instance} is duplicated."));
            }

            var oriented = new OrientedDimensions(
                placement.Width,
                placement.Depth,
                placement.Height);
            if (!OrientationGenerator.GetOrientations(carton).Contains(oriented))
            {
                issues.Add(new(
                    "invalid_orientation",
                    $"{placement.CartonId} uses invalid dimensions "
                    + $"{placement.Width}x{placement.Depth}x{placement.Height}."));
            }

            if (!IsInBounds(problem.Container, placement))
            {
                issues.Add(new(
                    "out_of_bounds",
                    $"{placement.CartonId} instance {placement.Instance} is out of bounds."));
            }

            totalWeight += carton.Weight;
            totalValue += carton.Value;
            totalVolume += oriented.Volume;
        }

        if (totalWeight > problem.Container.MaxWeight)
        {
            issues.Add(new(
                "weight_exceeded",
                $"Packed weight {totalWeight} exceeds {problem.Container.MaxWeight}."));
        }

        for (var i = 0; i < result.Placements.Count; i++)
        {
            for (var j = i + 1; j < result.Placements.Count; j++)
            {
                if (Overlaps(result.Placements[i], result.Placements[j]))
                {
                    issues.Add(new(
                        "overlap",
                        $"Placements {i} and {j} overlap."));
                }
            }
        }

        foreach (var placement in result.Placements.Where(p => p.Z > 0))
        {
            if (!HasFullBaseSupport(placement, result.Placements))
            {
                issues.Add(new(
                    "unsupported_carton",
                    $"{placement.CartonId} instance {placement.Instance} is not fully supported."));
            }
        }

        if (!result.Placements.SequenceEqual(
                result.Placements.OrderBy(p => p.CartonId, StringComparer.Ordinal)
                    .ThenBy(p => p.Instance)
                    .ThenBy(p => p.X)
                    .ThenBy(p => p.Y)
                    .ThenBy(p => p.Z)))
        {
            issues.Add(new(
                "noncanonical_order",
                "Placements must be sorted by carton ID, instance, X, Y, then Z."));
        }

        return new(issues, totalWeight, totalValue, totalVolume);
    }

    public static bool Overlaps(Placement left, Placement right) =>
        left.X < right.X + right.Width
        && right.X < left.X + left.Width
        && left.Y < right.Y + right.Depth
        && right.Y < left.Y + left.Depth
        && left.Z < right.Z + right.Height
        && right.Z < left.Z + left.Height;

    public static bool HasFullBaseSupport(
        Placement placement,
        IReadOnlyList<Placement> allPlacements)
    {
        if (placement.Z == 0)
        {
            return true;
        }

        var supports = allPlacements
            .Where(other =>
                !ReferenceEquals(other, placement)
                && other.Z + other.Height == placement.Z)
            .Select(other => IntersectFootprints(placement, other))
            .Where(rect => rect is not null)
            .Select(rect => rect!.Value)
            .ToArray();

        if (supports.Length == 0)
        {
            return false;
        }

        var xBreaks = new SortedSet<int>
        {
            placement.X,
            placement.X + placement.Width,
        };
        var yBreaks = new SortedSet<int>
        {
            placement.Y,
            placement.Y + placement.Depth,
        };
        foreach (var support in supports)
        {
            xBreaks.Add(support.X1);
            xBreaks.Add(support.X2);
            yBreaks.Add(support.Y1);
            yBreaks.Add(support.Y2);
        }

        var xs = xBreaks.ToArray();
        var ys = yBreaks.ToArray();
        long coveredArea = 0;
        for (var xIndex = 0; xIndex < xs.Length - 1; xIndex++)
        {
            for (var yIndex = 0; yIndex < ys.Length - 1; yIndex++)
            {
                var x1 = xs[xIndex];
                var x2 = xs[xIndex + 1];
                var y1 = ys[yIndex];
                var y2 = ys[yIndex + 1];
                if (supports.Any(rect =>
                        rect.X1 <= x1
                        && rect.X2 >= x2
                        && rect.Y1 <= y1
                        && rect.Y2 >= y2))
                {
                    coveredArea += (long)(x2 - x1) * (y2 - y1);
                }
            }
        }

        return coveredArea == (long)placement.Width * placement.Depth;
    }

    private static void ValidateProblem(
        PackingProblem problem,
        Dictionary<string, CartonType> cartonById,
        List<ValidationIssue> issues)
    {
        if (problem.Container.Width <= 0
            || problem.Container.Depth <= 0
            || problem.Container.Height <= 0
            || problem.Container.MaxWeight < 0)
        {
            issues.Add(new("invalid_container", "Container dimensions must be positive."));
        }

        foreach (var carton in problem.Cartons)
        {
            if (string.IsNullOrWhiteSpace(carton.Id)
                || carton.Width <= 0
                || carton.Depth <= 0
                || carton.Height <= 0
                || carton.Quantity < 0
                || carton.Weight < 0
                || carton.Value < 0)
            {
                issues.Add(new(
                    "invalid_carton",
                    $"Carton '{carton.Id}' has invalid fields."));
                continue;
            }

            if (!cartonById.TryAdd(carton.Id, carton))
            {
                issues.Add(new(
                    "duplicate_carton_id",
                    $"Carton ID '{carton.Id}' is duplicated."));
            }
        }
    }

    private static bool IsInBounds(ContainerSpec container, Placement placement) =>
        placement.X >= 0
        && placement.Y >= 0
        && placement.Z >= 0
        && placement.Width > 0
        && placement.Depth > 0
        && placement.Height > 0
        && placement.X + placement.Width <= container.Width
        && placement.Y + placement.Depth <= container.Depth
        && placement.Z + placement.Height <= container.Height;

    private static Footprint? IntersectFootprints(
        Placement upper,
        Placement lower)
    {
        var x1 = Math.Max(upper.X, lower.X);
        var x2 = Math.Min(upper.X + upper.Width, lower.X + lower.Width);
        var y1 = Math.Max(upper.Y, lower.Y);
        var y2 = Math.Min(upper.Y + upper.Depth, lower.Y + lower.Depth);
        return x1 < x2 && y1 < y2 ? new(x1, y1, x2, y2) : null;
    }

    private readonly record struct Footprint(int X1, int Y1, int X2, int Y2);
}
