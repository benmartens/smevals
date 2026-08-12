namespace CartonPacking;

public sealed class CartonPacker
{
    public PackingResult Pack(PackingProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var container = problem.Container;
        if (container.Width <= 0
            || container.Depth <= 0
            || container.Height <= 0
            || container.MaxWeight < 0)
        {
            return PackingResult.Empty;
        }

        var validCartons = (problem.Cartons ?? [])
            .Where(c =>
                !string.IsNullOrWhiteSpace(c.Id)
                && c.Quantity > 0
                && c.Width > 0
                && c.Depth > 0
                && c.Height > 0
                && c.Weight >= 0
                && c.Value >= 0)
            .ToList();

        if (validCartons.Count == 0)
        {
            return PackingResult.Empty;
        }

        List<InternalPlacement>? bestPlacements = null;
        var bestValue = -1L;
        var bestVolume = -1L;

        foreach (var order in BuildStrategies(validCartons))
        {
            var placements = RunGreedy(container, order);
            var (value, volume) = Totals(placements);
            if (value > bestValue || (value == bestValue && volume > bestVolume))
            {
                bestValue = value;
                bestVolume = volume;
                bestPlacements = placements;
            }
        }

        bestPlacements ??= [];

        var placementsById = bestPlacements
            .GroupBy(p => p.Carton.Id, StringComparer.Ordinal)
            .SelectMany(group => group
                .OrderBy(p => p.X)
                .ThenBy(p => p.Y)
                .ThenBy(p => p.Z)
                .Select((p, index) => new Placement(
                    p.Carton.Id, index, p.X, p.Y, p.Z, p.Width, p.Depth, p.Height)))
            .OrderBy(p => p.CartonId, StringComparer.Ordinal)
            .ThenBy(p => p.Instance)
            .ThenBy(p => p.X)
            .ThenBy(p => p.Y)
            .ThenBy(p => p.Z)
            .ToList();

        return new PackingResult(placementsById);
    }

    private static (long Value, long Volume) Totals(
        IReadOnlyList<InternalPlacement> placements)
    {
        var value = 0L;
        var volume = 0L;
        foreach (var p in placements)
        {
            value += p.Carton.Value;
            volume += (long)p.Width * p.Depth * p.Height;
        }

        return (value, volume);
    }

    /// <summary>
    /// Builds several deterministic carton-type orderings. Each ordering is
    /// simulated independently with a fresh container, and the run with the
    /// highest value (then volume) is kept. This mitigates greedy-choice traps
    /// that a single fixed ordering (e.g. pure value density) can fall into
    /// when weight or space limits bind differently than expected.
    /// </summary>
    private static List<List<CartonType>> BuildStrategies(List<CartonType> cartons)
    {
        static double ValuePerWeight(CartonType c) =>
            c.Weight == 0 ? double.PositiveInfinity : (double)c.Value / c.Weight;

        static double ValuePerVolume(CartonType c) =>
            (double)c.Value / Volume(c);

        static long Volume(CartonType c) => (long)c.Width * c.Depth * c.Height;

        List<CartonType> OrderBy(Func<CartonType, double> primary, bool volumeAscending = true) =>
            cartons
                .OrderByDescending(primary)
                .ThenByDescending(ValuePerVolume)
                .ThenByDescending(c => c.Value)
                .ThenBy(c => volumeAscending ? Volume(c) : -Volume(c))
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .ToList();

        return
        [
            OrderBy(ValuePerWeight),
            OrderBy(ValuePerVolume),
            OrderBy(c => (double)c.Value),
            OrderBy(c => -(double)Volume(c)),
            OrderBy(c => (double)Volume(c)),
        ];
    }

    private static List<InternalPlacement> RunGreedy(
        ContainerSpec container,
        IReadOnlyList<CartonType> order)
    {
        var heightMap = new HeightMap(container.Width, container.Depth);
        var anchors = new HashSet<(int X, int Y)> { (0, 0) };
        var placements = new List<InternalPlacement>();
        var weightRemaining = (long)container.MaxWeight;

        foreach (var carton in order)
        {
            var orientations = OrientationGenerator.GetOrientations(carton);
            if (orientations.Count == 0)
            {
                continue;
            }

            for (var instance = 0; instance < carton.Quantity; instance++)
            {
                if (carton.Weight > weightRemaining)
                {
                    break;
                }

                if (!TryFindBestPlacement(
                        heightMap, anchors, container, orientations, out var best))
                {
                    break;
                }

                heightMap.Apply(best.X, best.Y, best.Width, best.Depth, best.Z + best.Height);
                anchors.Add((best.X + best.Width, best.Y));
                anchors.Add((best.X, best.Y + best.Depth));
                anchors.Add((best.X + best.Width, best.Y + best.Depth));
                weightRemaining -= carton.Weight;
                placements.Add(new InternalPlacement(
                    carton, best.X, best.Y, best.Z, best.Width, best.Depth, best.Height));
            }
        }

        return placements;
    }

    private static bool TryFindBestPlacement(
        HeightMap heightMap,
        HashSet<(int X, int Y)> anchors,
        ContainerSpec container,
        IReadOnlyList<OrientedDimensions> orientations,
        out (int X, int Y, int Z, int Width, int Depth, int Height) best)
    {
        var found = false;
        best = default;

        foreach (var (x, y) in anchors)
        {
            if (x < 0 || y < 0 || x >= container.Width || y >= container.Depth)
            {
                continue;
            }

            foreach (var o in orientations)
            {
                if (x + o.Width > container.Width || y + o.Depth > container.Depth)
                {
                    continue;
                }

                if (!heightMap.TryGetSupportHeight(x, y, o.Width, o.Depth, out var z))
                {
                    continue;
                }

                if (z + o.Height > container.Height)
                {
                    continue;
                }

                if (found && !IsBetter(z, y, x, o, best))
                {
                    continue;
                }

                found = true;
                best = (x, y, z, o.Width, o.Depth, o.Height);
            }
        }

        return found;
    }

    /// <summary>
    /// Deterministic "deepest, bottom, left" tie-break: lowest z, then lowest
    /// y, then lowest x, then the smallest orientation. Comparing full tuples
    /// means the chosen placement never depends on hash-set iteration order.
    /// </summary>
    private static bool IsBetter(
        int z,
        int y,
        int x,
        OrientedDimensions o,
        (int X, int Y, int Z, int Width, int Depth, int Height) current)
    {
        if (z != current.Z)
        {
            return z < current.Z;
        }

        if (y != current.Y)
        {
            return y < current.Y;
        }

        if (x != current.X)
        {
            return x < current.X;
        }

        if (o.Width != current.Width)
        {
            return o.Width < current.Width;
        }

        if (o.Depth != current.Depth)
        {
            return o.Depth < current.Depth;
        }

        return o.Height < current.Height;
    }

    private sealed record InternalPlacement(
        CartonType Carton,
        int X,
        int Y,
        int Z,
        int Width,
        int Depth,
        int Height);

    /// <summary>
    /// Tracks the current top-surface height at every unit cell of the
    /// container floor. A footprint may host a new carton only when every
    /// cell underneath is already at the same height, which is exactly the
    /// README's "100% of the rectangular base covered by cartons whose top
    /// face is exactly at its bottom z" rule (floor cells start at height 0).
    /// </summary>
    private sealed class HeightMap
    {
        private readonly int[] _heights;
        private readonly int _width;

        public HeightMap(int width, int depth)
        {
            _width = width;
            _heights = new int[width * depth];
        }

        public bool TryGetSupportHeight(int x, int y, int width, int depth, out int z)
        {
            z = _heights[(y * _width) + x];
            for (var j = y; j < y + depth; j++)
            {
                var rowBase = j * _width;
                for (var i = x; i < x + width; i++)
                {
                    if (_heights[rowBase + i] != z)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public void Apply(int x, int y, int width, int depth, int newHeight)
        {
            for (var j = y; j < y + depth; j++)
            {
                var rowBase = j * _width;
                for (var i = x; i < x + width; i++)
                {
                    _heights[rowBase + i] = newHeight;
                }
            }
        }
    }
}
