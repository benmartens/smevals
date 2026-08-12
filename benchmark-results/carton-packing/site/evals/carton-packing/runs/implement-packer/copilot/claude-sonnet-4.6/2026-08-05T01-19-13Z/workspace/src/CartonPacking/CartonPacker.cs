namespace CartonPacking;

public sealed class CartonPacker
{
    public PackingResult Pack(PackingProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var container = problem.Container;
        var placements = new List<Placement>();
        var remaining = problem.Cartons.ToDictionary(c => c.Id, c => c.Quantity);
        var instanceCount = problem.Cartons.ToDictionary(c => c.Id, _ => 0);
        long remainingWeight = container.MaxWeight;

        // Sort carton types by value density desc, then value desc, then id asc (deterministic)
        var sortedCartons = problem.Cartons
            .OrderByDescending(c => c.Weight > 0 ? (double)c.Value / c.Weight : double.MaxValue)
            .ThenByDescending(c => c.Value)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

        // Extreme points: sorted by (Z, Y, X) so we fill bottom-front-left first
        var extremePoints = new SortedSet<(int Z, int Y, int X)> { (0, 0, 0) };

        bool progress = true;
        while (progress)
        {
            progress = false;
            foreach (var carton in sortedCartons)
            {
                if (remaining[carton.Id] == 0) continue;
                if (carton.Weight > remainingWeight) continue;

                var orientations = OrientationGenerator.GetOrientations(carton);
                Placement? bestPlacement = null;

                foreach (var (z, y, x) in extremePoints)
                {
                    if (bestPlacement != null) break;
                    foreach (var orient in orientations)
                    {
                        if (x + orient.Width > container.Width) continue;
                        if (y + orient.Depth > container.Depth) continue;
                        if (z + orient.Height > container.Height) continue;

                        var candidate = new Placement(
                            carton.Id, instanceCount[carton.Id],
                            x, y, z,
                            orient.Width, orient.Depth, orient.Height);

                        if (placements.Any(p => PackingValidator.Overlaps(p, candidate))) continue;

                        if (z > 0)
                        {
                            // Build a temporary list including candidate to reuse the validator
                            var allWithCandidate = new List<Placement>(placements.Count + 1);
                            allWithCandidate.AddRange(placements);
                            allWithCandidate.Add(candidate);
                            if (!PackingValidator.HasFullBaseSupport(candidate, allWithCandidate)) continue;
                        }

                        bestPlacement = candidate;
                        break;
                    }
                }

                if (bestPlacement != null)
                {
                    placements.Add(bestPlacement);
                    remainingWeight -= carton.Weight;
                    remaining[carton.Id]--;
                    instanceCount[carton.Id]++;

                    // Generate new extreme points at the three faces of the placed box
                    extremePoints.Add((bestPlacement.Z, bestPlacement.Y, bestPlacement.X + bestPlacement.Width));
                    extremePoints.Add((bestPlacement.Z, bestPlacement.Y + bestPlacement.Depth, bestPlacement.X));
                    extremePoints.Add((bestPlacement.Z + bestPlacement.Height, bestPlacement.Y, bestPlacement.X));

                    progress = true;
                }
            }
        }

        var sorted = placements
            .OrderBy(p => p.CartonId, StringComparer.Ordinal)
            .ThenBy(p => p.Instance)
            .ThenBy(p => p.X)
            .ThenBy(p => p.Y)
            .ThenBy(p => p.Z)
            .ToList();

        return new PackingResult(sorted);
    }
}
