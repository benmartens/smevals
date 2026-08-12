namespace CartonPacking;

public sealed class CartonPacker
{
    public PackingResult Pack(PackingProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        if (problem.Container.Width <= 0
            || problem.Container.Depth <= 0
            || problem.Container.Height <= 0
            || problem.Container.MaxWeight < 0)
        {
            return PackingResult.Empty;
        }

        var orderedCartons = problem.Cartons
            .Where(carton => carton.Quantity > 0)
            .Select(carton => new
            {
                Carton = carton,
                Score = ComputePriorityScore(carton),
            })
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Carton.Id, StringComparer.Ordinal)
            .Select(entry => entry.Carton)
            .ToArray();

        if (orderedCartons.Length == 0)
        {
            return PackingResult.Empty;
        }

        var orientationsByIndex = orderedCartons
            .Select(carton => OrientationGenerator.GetOrientations(carton).ToArray())
            .ToArray();

        var remainingCounts = orderedCartons.Select(carton => carton.Quantity).ToArray();
        var nextInstances = new int[orderedCartons.Length];
        var occupied = new bool[problem.Container.Height, problem.Container.Width, problem.Container.Depth];
        var support = new int[problem.Container.Height + 1, problem.Container.Width, problem.Container.Depth];

        var bestPlacements = new List<Placement>();
        var bestValue = 0L;
        var bestVolume = 0L;

        var initialSolution = BuildGreedySolution(
            problem,
            orderedCartons,
            orientationsByIndex,
            remainingCounts,
            nextInstances,
            occupied,
            support);
        if (initialSolution is not null)
        {
            bestPlacements = initialSolution.Placements;
            bestValue = initialSolution.Value;
            bestVolume = initialSolution.Volume;
        }

        var currentPlacements = new List<Placement>();
        Search(
            problem,
            orderedCartons,
            orientationsByIndex,
            remainingCounts,
            nextInstances,
            occupied,
            support,
            0,
            0L,
            0L,
            currentPlacements,
            ref bestPlacements,
            ref bestValue,
            ref bestVolume);

        bestPlacements.Sort((left, right) =>
        {
            var idCompare = StringComparer.Ordinal.Compare(left.CartonId, right.CartonId);
            if (idCompare != 0)
            {
                return idCompare;
            }

            var instanceCompare = left.Instance.CompareTo(right.Instance);
            if (instanceCompare != 0)
            {
                return instanceCompare;
            }

            var xCompare = left.X.CompareTo(right.X);
            if (xCompare != 0)
            {
                return xCompare;
            }

            var yCompare = left.Y.CompareTo(right.Y);
            if (yCompare != 0)
            {
                return yCompare;
            }

            return left.Z.CompareTo(right.Z);
        });

        return new PackingResult(bestPlacements);
    }

    private static long ComputePriorityScore(CartonType carton)
    {
        var volume = (long)carton.Width * carton.Depth * carton.Height;
        var weightDenominator = Math.Max(1L, carton.Weight + 1L);
        var volumeDenominator = Math.Max(1L, volume);
        return carton.Value * 1_000_000L / (weightDenominator * volumeDenominator);
    }

    private static SearchSolution? BuildGreedySolution(
        PackingProblem problem,
        IReadOnlyList<CartonType> orderedCartons,
        IReadOnlyList<IReadOnlyList<OrientedDimensions>> orientationsByIndex,
        int[] remainingCounts,
        int[] nextInstances,
        bool[,,] occupied,
        int[,,] support)
    {
        var placements = new List<Placement>();
        var value = 0L;
        var volume = 0L;
        var weight = 0;
        var currentRemainingCounts = remainingCounts.ToArray();
        var currentNextInstances = nextInstances.ToArray();
        var currentOccupied = CloneOccupied(occupied);
        var currentSupport = CloneSupport(support);

        while (true)
        {
            var placedAny = false;
            foreach (var typeIndex in Enumerable.Range(0, orderedCartons.Count))
            {
                if (currentRemainingCounts[typeIndex] <= 0)
                {
                    continue;
                }

                var carton = orderedCartons[typeIndex];
                if (weight + carton.Weight > problem.Container.MaxWeight)
                {
                    continue;
                }

                var placement = TryFindFirstPlacement(
                    problem,
                    carton,
                    orientationsByIndex[typeIndex],
                    typeIndex,
                    currentOccupied,
                    currentSupport,
                    currentNextInstances[typeIndex]);
                if (placement is null)
                {
                    continue;
                }

                ApplyPlacement(
                    problem.Container,
                    placement,
                    currentOccupied,
                    currentSupport);
                placements.Add(placement);
                value += carton.Value;
                volume += placement.Width * (long)placement.Depth * placement.Height;
                weight += carton.Weight;
                currentRemainingCounts[typeIndex]--;
                currentNextInstances[typeIndex]++;
                placedAny = true;
                break;
            }

            if (!placedAny)
            {
                break;
            }
        }

        return placements.Count == 0 ? null : new SearchSolution(placements, value, volume);
    }

    private static Placement? TryFindFirstPlacement(
        PackingProblem problem,
        CartonType carton,
        IReadOnlyList<OrientedDimensions> orientations,
        int typeIndex,
        bool[,,] occupied,
        int[,,] support,
        int instance)
    {
        var container = problem.Container;
        foreach (var orientation in orientations)
        {
            for (var z = 0; z + orientation.Height <= container.Height; z++)
            {
                for (var x = 0; x + orientation.Width <= container.Width; x++)
                {
                    for (var y = 0; y + orientation.Depth <= container.Depth; y++)
                    {
                        if (!CanPlace(container, x, y, z, orientation, occupied, support))
                        {
                            continue;
                        }

                        return new Placement(
                            carton.Id,
                            instance,
                            x,
                            y,
                            z,
                            orientation.Width,
                            orientation.Depth,
                            orientation.Height);
                    }
                }
            }
        }

        return null;
    }

    private static bool CanPlace(
        ContainerSpec container,
        int x,
        int y,
        int z,
        OrientedDimensions orientation,
        bool[,,] occupied,
        int[,,] support)
    {
        if (x < 0 || y < 0 || z < 0)
        {
            return false;
        }

        if (x + orientation.Width > container.Width
            || y + orientation.Depth > container.Depth
            || z + orientation.Height > container.Height)
        {
            return false;
        }

        if (z > 0)
        {
            for (var dx = 0; dx < orientation.Width; dx++)
            {
                for (var dy = 0; dy < orientation.Depth; dy++)
                {
                    if (support[z, x + dx, y + dy] <= 0)
                    {
                        return false;
                    }
                }
            }
        }

        for (var dz = 0; dz < orientation.Height; dz++)
        {
            for (var dx = 0; dx < orientation.Width; dx++)
            {
                for (var dy = 0; dy < orientation.Depth; dy++)
                {
                    if (occupied[z + dz, x + dx, y + dy])
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static void ApplyPlacement(
        ContainerSpec container,
        Placement placement,
        bool[,,] occupied,
        int[,,] support)
    {
        for (var dz = 0; dz < placement.Height; dz++)
        {
            for (var dx = 0; dx < placement.Width; dx++)
            {
                for (var dy = 0; dy < placement.Depth; dy++)
                {
                    occupied[placement.Z + dz, placement.X + dx, placement.Y + dy] = true;
                }
            }
        }

        for (var dx = 0; dx < placement.Width; dx++)
        {
            for (var dy = 0; dy < placement.Depth; dy++)
            {
                support[placement.Z + placement.Height, placement.X + dx, placement.Y + dy] += 1;
            }
        }
    }

    private static void RemovePlacement(
        Placement placement,
        bool[,,] occupied,
        int[,,] support)
    {
        for (var dz = 0; dz < placement.Height; dz++)
        {
            for (var dx = 0; dx < placement.Width; dx++)
            {
                for (var dy = 0; dy < placement.Depth; dy++)
                {
                    occupied[placement.Z + dz, placement.X + dx, placement.Y + dy] = false;
                }
            }
        }

        for (var dx = 0; dx < placement.Width; dx++)
        {
            for (var dy = 0; dy < placement.Depth; dy++)
            {
                support[placement.Z + placement.Height, placement.X + dx, placement.Y + dy] -= 1;
            }
        }
    }

    private static bool[,,] CloneOccupied(bool[,,] source)
    {
        var clone = new bool[source.GetLength(0), source.GetLength(1), source.GetLength(2)];
        Array.Copy(source, clone, source.Length);
        return clone;
    }

    private static int[,,] CloneSupport(int[,,] source)
    {
        var clone = new int[source.GetLength(0), source.GetLength(1), source.GetLength(2)];
        Array.Copy(source, clone, source.Length);
        return clone;
    }

    private static void Search(
        PackingProblem problem,
        IReadOnlyList<CartonType> orderedCartons,
        IReadOnlyList<IReadOnlyList<OrientedDimensions>> orientationsByIndex,
        int[] remainingCounts,
        int[] nextInstances,
        bool[,,] occupied,
        int[,,] support,
        int currentWeight,
        long currentValue,
        long currentVolume,
        List<Placement> placements,
        ref List<Placement> bestPlacements,
        ref long bestValue,
        ref long bestVolume)
    {
        var remainingValueUpperBound = 0L;
        var remainingVolumeUpperBound = 0L;
        for (var index = 0; index < orderedCartons.Count; index++)
        {
            if (remainingCounts[index] <= 0)
            {
                continue;
            }

            var carton = orderedCartons[index];
            remainingValueUpperBound += (long)carton.Value * remainingCounts[index];
            remainingVolumeUpperBound += (long)carton.Width * carton.Depth * carton.Height * remainingCounts[index];
        }

        if (currentValue + remainingValueUpperBound < bestValue
            || (currentValue + remainingValueUpperBound == bestValue
                && currentVolume + remainingVolumeUpperBound <= bestVolume))
        {
            return;
        }

        var bestLocal = placements.Count > 0 ? placements.ToList() : [];
        var updatedBest = false;

        for (var typeIndex = 0; typeIndex < orderedCartons.Count; typeIndex++)
        {
            if (remainingCounts[typeIndex] <= 0)
            {
                continue;
            }

            var carton = orderedCartons[typeIndex];
            if (currentWeight + carton.Weight > problem.Container.MaxWeight)
            {
                continue;
            }

            var orientations = orientationsByIndex[typeIndex];
            for (var orientationIndex = 0; orientationIndex < orientations.Count; orientationIndex++)
            {
                var orientation = orientations[orientationIndex];
                for (var z = 0; z + orientation.Height <= problem.Container.Height; z++)
                {
                    for (var x = 0; x + orientation.Width <= problem.Container.Width; x++)
                    {
                        for (var y = 0; y + orientation.Depth <= problem.Container.Depth; y++)
                        {
                            if (!CanPlace(problem.Container, x, y, z, orientation, occupied, support))
                            {
                                continue;
                            }

                            var placement = new Placement(
                                carton.Id,
                                nextInstances[typeIndex],
                                x,
                                y,
                                z,
                                orientation.Width,
                                orientation.Depth,
                                orientation.Height);

                            ApplyPlacement(problem.Container, placement, occupied, support);
                            placements.Add(placement);
                            remainingCounts[typeIndex]--;
                            nextInstances[typeIndex]++;
                            currentWeight += carton.Weight;
                            currentValue += carton.Value;
                            currentVolume += placement.Width * (long)placement.Depth * placement.Height;

                            Search(
                                problem,
                                orderedCartons,
                                orientationsByIndex,
                                remainingCounts,
                                nextInstances,
                                occupied,
                                support,
                                currentWeight,
                                currentValue,
                                currentVolume,
                                placements,
                                ref bestPlacements,
                                ref bestValue,
                                ref bestVolume);

                            if (ShouldReplaceBest(
                                    currentValue,
                                    currentVolume,
                                    placements,
                                    bestValue,
                                    bestVolume,
                                    bestPlacements))
                            {
                                bestValue = currentValue;
                                bestVolume = currentVolume;
                                bestPlacements = placements.ToList();
                                updatedBest = true;
                            }

                            currentWeight -= carton.Weight;
                            currentValue -= carton.Value;
                            currentVolume -= placement.Width * (long)placement.Depth * placement.Height;
                            nextInstances[typeIndex]--;
                            remainingCounts[typeIndex]++;
                            RemovePlacement(placement, occupied, support);
                            placements.RemoveAt(placements.Count - 1);
                        }
                    }
                }
            }
        }

        if (!updatedBest && placements.Count == 0)
        {
            if (bestPlacements.Count == 0)
            {
                bestValue = 0L;
                bestVolume = 0L;
                bestPlacements = [];
            }
        }
    }

    private static bool ShouldReplaceBest(
        long currentValue,
        long currentVolume,
        IReadOnlyList<Placement> placements,
        long bestValue,
        long bestVolume,
        IReadOnlyList<Placement> bestPlacements)
    {
        if (currentValue > bestValue)
        {
            return true;
        }

        if (currentValue < bestValue)
        {
            return false;
        }

        if (currentVolume > bestVolume)
        {
            return true;
        }

        if (currentVolume < bestVolume)
        {
            return false;
        }

        if (placements.Count == 0 && bestPlacements.Count == 0)
        {
            return false;
        }

        return ComparePlacements(placements, bestPlacements) < 0;
    }

    private static int ComparePlacements(
        IReadOnlyList<Placement> left,
        IReadOnlyList<Placement> right)
    {
        var sharedCount = Math.Min(left.Count, right.Count);
        for (var index = 0; index < sharedCount; index++)
        {
            var leftPlacement = left[index];
            var rightPlacement = right[index];
            var idCompare = StringComparer.Ordinal.Compare(leftPlacement.CartonId, rightPlacement.CartonId);
            if (idCompare != 0)
            {
                return idCompare;
            }

            var instanceCompare = leftPlacement.Instance.CompareTo(rightPlacement.Instance);
            if (instanceCompare != 0)
            {
                return instanceCompare;
            }

            var xCompare = leftPlacement.X.CompareTo(rightPlacement.X);
            if (xCompare != 0)
            {
                return xCompare;
            }

            var yCompare = leftPlacement.Y.CompareTo(rightPlacement.Y);
            if (yCompare != 0)
            {
                return yCompare;
            }

            var zCompare = leftPlacement.Z.CompareTo(rightPlacement.Z);
            if (zCompare != 0)
            {
                return zCompare;
            }
        }

        return left.Count.CompareTo(right.Count);
    }

    private sealed record SearchSolution(List<Placement> Placements, long Value, long Volume);
}
