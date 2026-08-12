namespace CartonPacking;

/// <summary>
/// Deterministic 3D carton packer.
///
/// Strategy:
///   1. Selection: a bounded knapsack on container weight chooses how many of
///      each carton type to attempt, maximizing total value and then total
///      volume. This resolves greedy weight/value traps (a set of light cartons
///      can beat a single heavy one that "looks" better by raw value).
///   2. Placement: a constructive "drop to lowest fully-supported surface"
///      heuristic places cartons one at a time. A carton is only ever placed on
///      a perfectly flat region (floor, or the coplanar tops of one or more
///      cartons), which guarantees 100% base support and prevents overlaps.
///      Among all feasible (orientation, position) choices the lowest, most
///      back-left slot wins, producing a tight, deterministic layout.
///   3. Space fill: any leftover cartons that still fit (space and weight) are
///      added to recover value/volume the knapsack target could not seat.
/// </summary>
public sealed class CartonPacker
{
    private const int KnapsackCapLimit = 200_000;
    private const long KnapsackOpsLimit = 60_000_000;

    public PackingResult Pack(PackingProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var container = problem.Container;
        if (container.Width <= 0 || container.Depth <= 0 ||
            container.Height <= 0 || container.MaxWeight < 0)
        {
            return PackingResult.Empty;
        }

        var types = NormalizeTypes(problem.Cartons);
        if (types.Count == 0)
        {
            return PackingResult.Empty;
        }

        var targets = SelectTargets(container, types);
        var engine = new PlacementEngine(container);

        foreach (var type in BuildPass(types, t => targets[t.Id], Pass1Compare))
        {
            engine.TryPlace(type);
        }

        foreach (var type in BuildPass(
            types, t => t.Quantity - engine.PlacedCount(t.Id), Pass2Compare))
        {
            engine.TryPlace(type);
        }

        return engine.BuildResult();
    }

    private static List<CartonType> NormalizeTypes(IEnumerable<CartonType> cartons)
    {
        var result = new List<CartonType>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var carton in cartons)
        {
            if (string.IsNullOrWhiteSpace(carton.Id)) continue;
            if (carton.Width <= 0 || carton.Depth <= 0 || carton.Height <= 0) continue;
            if (carton.Quantity <= 0 || carton.Weight < 0 || carton.Value < 0) continue;
            if (!seen.Add(carton.Id)) continue;
            result.Add(carton);
        }

        return result;
    }

    private static List<CartonType> BuildPass(
        List<CartonType> types,
        Func<CartonType, int> countSelector,
        Comparison<CartonType> order)
    {
        var pass = new List<CartonType>();
        foreach (var type in types)
        {
            var count = countSelector(type);
            for (var i = 0; i < count; i++)
            {
                pass.Add(type);
            }
        }

        pass.Sort(order);
        return pass;
    }

    private static long BoundingVolume(CartonType t) =>
        (long)t.Width * t.Depth * t.Height;

    private static int MaxSide(CartonType t) =>
        Math.Max(t.Width, Math.Max(t.Depth, t.Height));

    private static int Pass1Compare(CartonType a, CartonType b)
    {
        var c = b.Value.CompareTo(a.Value);
        if (c != 0) return c;
        c = BoundingVolume(b).CompareTo(BoundingVolume(a));
        if (c != 0) return c;
        c = MaxSide(b).CompareTo(MaxSide(a));
        if (c != 0) return c;
        c = b.Weight.CompareTo(a.Weight);
        if (c != 0) return c;
        return string.CompareOrdinal(a.Id, b.Id);
    }

    private static int Pass2Compare(CartonType a, CartonType b)
    {
        var c = b.Value.CompareTo(a.Value);
        if (c != 0) return c;
        c = BoundingVolume(b).CompareTo(BoundingVolume(a));
        if (c != 0) return c;
        c = MaxSide(b).CompareTo(MaxSide(a));
        if (c != 0) return c;
        c = a.Weight.CompareTo(b.Weight);
        if (c != 0) return c;
        return string.CompareOrdinal(a.Id, b.Id);
    }

    private static Dictionary<string, int> SelectTargets(
        ContainerSpec container, List<CartonType> types)
    {
        var targets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            targets[type.Id] = 0;
        }

        long totalWeight = 0;
        foreach (var type in types)
        {
            totalWeight += (long)type.Quantity * type.Weight;
        }

        if (totalWeight <= container.MaxWeight)
        {
            foreach (var type in types)
            {
                targets[type.Id] = type.Quantity;
            }

            return targets;
        }

        var positive = new List<CartonType>();
        foreach (var type in types)
        {
            if (type.Weight == 0)
            {
                targets[type.Id] = type.Quantity;
            }
            else
            {
                positive.Add(type);
            }
        }

        var cap = container.MaxWeight;
        if (cap <= 0 || positive.Count == 0)
        {
            return targets;
        }

        long ops = 0;
        foreach (var type in positive)
        {
            ops += (long)(cap + 1) * (Math.Min(type.Quantity, cap / type.Weight) + 1);
        }

        if (cap > KnapsackCapLimit || ops > KnapsackOpsLimit)
        {
            GreedyFill(positive, cap, targets);
            return targets;
        }

        Knapsack(positive, cap, targets);
        return targets;
    }

    private static void Knapsack(
        List<CartonType> positive, int cap, Dictionary<string, int> targets)
    {
        var n = positive.Count;
        var prevValue = new long[cap + 1];
        var prevVolume = new long[cap + 1];
        var curValue = new long[cap + 1];
        var curVolume = new long[cap + 1];
        var choose = new int[n][];

        for (var i = 0; i < n; i++)
        {
            choose[i] = new int[cap + 1];
            var weight = positive[i].Weight;
            long value = positive[i].Value;
            var volume = BoundingVolume(positive[i]);
            var quantity = positive[i].Quantity;

            for (var w = 0; w <= cap; w++)
            {
                var bestValue = prevValue[w];
                var bestVolume = prevVolume[w];
                var bestK = 0;
                var maxK = Math.Min(quantity, w / weight);
                for (var k = 1; k <= maxK; k++)
                {
                    var prior = w - (k * weight);
                    var candidateValue = prevValue[prior] + (k * value);
                    var candidateVolume = prevVolume[prior] + (k * volume);
                    if (candidateValue > bestValue ||
                        (candidateValue == bestValue && candidateVolume > bestVolume))
                    {
                        bestValue = candidateValue;
                        bestVolume = candidateVolume;
                        bestK = k;
                    }
                }

                curValue[w] = bestValue;
                curVolume[w] = bestVolume;
                choose[i][w] = bestK;
            }

            Array.Copy(curValue, prevValue, cap + 1);
            Array.Copy(curVolume, prevVolume, cap + 1);
        }

        var remaining = cap;
        for (var i = n - 1; i >= 0; i--)
        {
            var k = choose[i][remaining];
            targets[positive[i].Id] = k;
            remaining -= k * positive[i].Weight;
        }
    }

    private static void GreedyFill(
        List<CartonType> positive, int cap, Dictionary<string, int> targets)
    {
        var ordered = new List<CartonType>(positive);
        ordered.Sort((a, b) =>
        {
            var left = (long)a.Value * b.Weight;
            var right = (long)b.Value * a.Weight;
            var c = right.CompareTo(left);
            if (c != 0) return c;
            c = b.Value.CompareTo(a.Value);
            if (c != 0) return c;
            c = BoundingVolume(b).CompareTo(BoundingVolume(a));
            if (c != 0) return c;
            return string.CompareOrdinal(a.Id, b.Id);
        });

        var remaining = cap;
        foreach (var type in ordered)
        {
            if (remaining <= 0) break;
            var count = Math.Min(type.Quantity, remaining / type.Weight);
            targets[type.Id] = count;
            remaining -= count * type.Weight;
        }
    }

    private sealed class PlacementEngine
    {
        private readonly ContainerSpec _container;
        private readonly List<Placement> _placed = new();
        private readonly Dictionary<string, int> _count =
            new(StringComparer.Ordinal);
        private readonly SortedSet<int> _xs = new() { 0 };
        private readonly SortedSet<int> _ys = new() { 0 };
        private long _weight;

        public PlacementEngine(ContainerSpec container) => _container = container;

        public int PlacedCount(string id) =>
            _count.TryGetValue(id, out var value) ? value : 0;

        public bool TryPlace(CartonType type)
        {
            var placedSoFar = PlacedCount(type.Id);
            if (placedSoFar >= type.Quantity) return false;
            if (_weight + type.Weight > _container.MaxWeight) return false;

            var orientations = OrientationGenerator.GetOrientations(type);
            var found = false;
            int bx = 0, by = 0, bz = 0, bw = 0, bd = 0, bh = 0, bIndex = 0;

            for (var index = 0; index < orientations.Count; index++)
            {
                var o = orientations[index];
                if (o.Width > _container.Width ||
                    o.Depth > _container.Depth ||
                    o.Height > _container.Height)
                {
                    continue;
                }

                foreach (var x in _xs)
                {
                    if (x + o.Width > _container.Width) break;
                    foreach (var y in _ys)
                    {
                        if (y + o.Depth > _container.Depth) break;
                        if (!FootprintLevel(x, y, o.Width, o.Depth, out var z)) continue;
                        if (z + o.Height > _container.Height) continue;

                        if (!found || IsBetter(
                                z, y, x, o.Height, o.Width, o.Depth, index,
                                bz, by, bx, bh, bw, bd, bIndex))
                        {
                            found = true;
                            bx = x;
                            by = y;
                            bz = z;
                            bw = o.Width;
                            bd = o.Depth;
                            bh = o.Height;
                            bIndex = index;
                        }
                    }
                }
            }

            if (!found) return false;

            _placed.Add(new Placement(type.Id, placedSoFar, bx, by, bz, bw, bd, bh));
            _count[type.Id] = placedSoFar + 1;
            _weight += type.Weight;
            _xs.Add(bx);
            _xs.Add(bx + bw);
            _ys.Add(by);
            _ys.Add(by + bd);
            return true;
        }

        public PackingResult BuildResult()
        {
            var ordered = _placed
                .OrderBy(p => p.CartonId, StringComparer.Ordinal)
                .ThenBy(p => p.Instance)
                .ThenBy(p => p.X)
                .ThenBy(p => p.Y)
                .ThenBy(p => p.Z)
                .ToList();
            return new PackingResult(ordered);
        }

        /// <summary>
        /// Determines the resting z for a footprint and whether that resting
        /// surface is perfectly flat (fully supported, no overlap). A footprint
        /// is placeable only when every carton it lands on has its top face at
        /// the same z and together they cover the whole base (or it sits on the
        /// empty floor).
        /// </summary>
        private bool FootprintLevel(int x, int y, int w, int d, out int z)
        {
            z = 0;
            var x2 = x + w;
            var y2 = y + d;
            var top = 0;
            var hasIntersection = false;

            foreach (var p in _placed)
            {
                if (p.X < x2 && x < p.X + p.Width &&
                    p.Y < y2 && y < p.Y + p.Depth)
                {
                    hasIntersection = true;
                    var candidateTop = p.Z + p.Height;
                    if (candidateTop > top) top = candidateTop;
                }
            }

            if (!hasIntersection)
            {
                z = 0;
                return true;
            }

            z = top;
            long area = (long)w * d;
            return CoveredArea(x, y, x2, y2, top) == area;
        }

        private long CoveredArea(int fx1, int fy1, int fx2, int fy2, int level)
        {
            var rects = new List<(int X1, int Y1, int X2, int Y2)>();
            var xset = new SortedSet<int> { fx1, fx2 };
            var yset = new SortedSet<int> { fy1, fy2 };

            foreach (var p in _placed)
            {
                if (p.Z + p.Height != level) continue;
                var rx1 = Math.Max(fx1, p.X);
                var ry1 = Math.Max(fy1, p.Y);
                var rx2 = Math.Min(fx2, p.X + p.Width);
                var ry2 = Math.Min(fy2, p.Y + p.Depth);
                if (rx1 >= rx2 || ry1 >= ry2) continue;
                rects.Add((rx1, ry1, rx2, ry2));
                xset.Add(rx1);
                xset.Add(rx2);
                yset.Add(ry1);
                yset.Add(ry2);
            }

            if (rects.Count == 0) return 0;

            var xs = xset.ToArray();
            var ys = yset.ToArray();
            long covered = 0;
            for (var xi = 0; xi < xs.Length - 1; xi++)
            {
                var cx1 = xs[xi];
                var cx2 = xs[xi + 1];
                for (var yi = 0; yi < ys.Length - 1; yi++)
                {
                    var cy1 = ys[yi];
                    var cy2 = ys[yi + 1];
                    foreach (var r in rects)
                    {
                        if (r.X1 <= cx1 && r.X2 >= cx2 &&
                            r.Y1 <= cy1 && r.Y2 >= cy2)
                        {
                            covered += (long)(cx2 - cx1) * (cy2 - cy1);
                            break;
                        }
                    }
                }
            }

            return covered;
        }

        private static bool IsBetter(
            int z, int y, int x, int h, int w, int d, int index,
            int bz, int by, int bx, int bh, int bw, int bd, int bIndex)
        {
            if (z != bz) return z < bz;
            if (y != by) return y < by;
            if (x != bx) return x < bx;
            if (h != bh) return h < bh;
            if (w != bw) return w < bw;
            if (d != bd) return d < bd;
            return index < bIndex;
        }
    }
}
