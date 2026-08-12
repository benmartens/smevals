using System.Runtime.InteropServices;

namespace CartonPacking;

/// <summary>
/// Deterministic multi-start constructive packer.
///
/// Candidate positions are corner points derived from the right/front faces of
/// already placed cartons. A carton may only rest on a footprint whose surface
/// is perfectly flat, which simultaneously guarantees no overlap and 100% base
/// support (possibly shared between several lower cartons). Several
/// deterministic item orderings and placement rules are simulated and the
/// layout with the highest (value, volume) is returned.
/// </summary>
public sealed class CartonPacker
{
    public PackingResult Pack(PackingProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var container = problem.Container;
        var cartons = problem.Cartons;
        if (container is null || cartons is null)
        {
            return PackingResult.Empty;
        }

        if (container.Width <= 0
            || container.Depth <= 0
            || container.Height <= 0
            || container.MaxWeight < 0)
        {
            return PackingResult.Empty;
        }

        var kinds = BuildKinds(container, cartons);
        return kinds.Length == 0
            ? PackingResult.Empty
            : new Solver(container, kinds).Solve();
    }

    private static Kind[] BuildKinds(
        ContainerSpec container,
        List<CartonType> cartons)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kinds = new List<Kind>();
        var containerVolume =
            (long)container.Width * container.Depth * container.Height;

        foreach (var carton in cartons)
        {
            if (carton is null || string.IsNullOrWhiteSpace(carton.Id))
            {
                continue;
            }

            if (carton.Width <= 0 || carton.Depth <= 0 || carton.Height <= 0)
            {
                continue;
            }

            if (carton.Quantity <= 0 || carton.Weight < 0 || carton.Value < 0)
            {
                continue;
            }

            if (!seen.Add(carton.Id) || carton.Weight > container.MaxWeight)
            {
                continue;
            }

            var orientations = OrientationGenerator.GetOrientations(carton)
                .Where(o => o.Width <= container.Width
                    && o.Depth <= container.Depth
                    && o.Height <= container.Height)
                .ToArray();
            if (orientations.Length == 0)
            {
                continue;
            }

            var unitVolume = (long)carton.Width * carton.Depth * carton.Height;
            var quantity = Math.Min(carton.Quantity, containerVolume / unitVolume);
            if (carton.Weight > 0)
            {
                quantity = Math.Min(quantity, container.MaxWeight / carton.Weight);
            }

            if (quantity <= 0)
            {
                continue;
            }

            kinds.Add(new Kind(
                kinds.Count,
                carton.Id,
                (int)quantity,
                carton.Weight,
                carton.Value,
                orientations,
                unitVolume));
        }

        return kinds.ToArray();
    }

    private sealed record Kind(
        int Index,
        string Id,
        int Quantity,
        int Weight,
        int Value,
        OrientedDimensions[] Orientations,
        long UnitVolume);

    private readonly record struct Box(
        int Kind,
        int X,
        int Y,
        int Z,
        int W,
        int D,
        int H)
    {
        public int X2 => X + W;

        public int Y2 => Y + D;

        public int Top => Z + H;
    }

    /// <summary>Lexicographic placement score; lower is better.</summary>
    private readonly record struct Key(
        long A,
        long B,
        long C,
        long D,
        long E,
        long F)
    {
        public bool IsBetterThan(in Key other)
        {
            if (A != other.A) return A < other.A;
            if (B != other.B) return B < other.B;
            if (C != other.C) return C < other.C;
            if (D != other.D) return D < other.D;
            if (E != other.E) return E < other.E;
            return F < other.F;
        }
    }

    private sealed record Strategy(
        int[] Order,
        int Rule,
        int[] Caps,
        bool Interleave);

    private sealed class Layout
    {
        public Layout(int kindCount)
        {
            Used = new int[kindCount];
            Blocked = new bool[kindCount];
        }

        public List<Box> Boxes { get; } = [];

        public int[] Used { get; }

        public bool[] Blocked { get; }

        public List<int> Xs { get; } = [0];

        public List<int> Ys { get; } = [0];

        public long Weight { get; set; }

        public long Value { get; set; }

        public long Volume { get; set; }
    }

    private sealed class Solver
    {
        private const long SoftWorkBudget = 60_000_000;
        private const long HardWorkBudget = 220_000_000;
        private const int RuleDeepBottomLeft = 0;
        private const int RuleLeftBottomDeep = 1;
        private const int RuleMaxContact = 2;

        private readonly ContainerSpec container;
        private readonly Kind[] kinds;
        private long work;

        public Solver(ContainerSpec container, Kind[] kinds)
        {
            this.container = container;
            this.kinds = kinds;
        }

        public PackingResult Solve()
        {
            Layout? best = null;
            foreach (var strategy in BuildStrategies())
            {
                if (best is not null && work >= SoftWorkBudget)
                {
                    break;
                }

                var layout = Run(strategy);
                if (best is null
                    || layout.Value > best.Value
                    || (layout.Value == best.Value && layout.Volume > best.Volume))
                {
                    best = layout;
                }
            }

            return ToResult(best);
        }

        private IEnumerable<Strategy> BuildStrategies()
        {
            var full = kinds.Select(k => k.Quantity).ToArray();
            var orderings = BuildOrderings();
            var knapsack = BuildWeightKnapsackCaps();

            foreach (var order in orderings)
            {
                yield return new(order, RuleDeepBottomLeft, full, false);
            }

            if (knapsack is not null)
            {
                foreach (var order in orderings)
                {
                    yield return new(order, RuleDeepBottomLeft, knapsack, false);
                }
            }

            foreach (var order in orderings)
            {
                yield return new(order, RuleMaxContact, full, false);
            }

            foreach (var order in orderings)
            {
                yield return new(order, RuleLeftBottomDeep, full, false);
            }

            yield return new(orderings[0], RuleDeepBottomLeft, full, true);
            yield return new(orderings[^1], RuleDeepBottomLeft, full, true);

            if (knapsack is not null)
            {
                foreach (var order in orderings)
                {
                    yield return new(order, RuleMaxContact, knapsack, false);
                }
            }

            if (kinds.Length > 1)
            {
                foreach (var order in orderings.Take(2))
                {
                    for (var i = 0; i < kinds.Length; i++)
                    {
                        yield return new(
                            Promote(order, i), RuleDeepBottomLeft, full, false);
                    }
                }

                for (var i = 0; i < kinds.Length; i++)
                {
                    var caps = (int[])full.Clone();
                    caps[i] = 0;
                    yield return new(orderings[0], RuleDeepBottomLeft, caps, false);
                }
            }
        }

        private static int[] Promote(int[] order, int kindIndex)
        {
            var promoted = new int[order.Length];
            promoted[0] = kindIndex;
            var next = 1;
            foreach (var index in order)
            {
                if (index != kindIndex)
                {
                    promoted[next++] = index;
                }
            }

            return promoted;
        }

        private int[][] BuildOrderings()
        {
            var containerVolume =
                (long)container.Width * container.Depth * container.Height;

            return
            [
                Sorted((a, b) =>
                {
                    var r = b.Value.CompareTo(a.Value);
                    return r != 0 ? r : b.UnitVolume.CompareTo(a.UnitVolume);
                }),
                Sorted((a, b) =>
                {
                    var r = CompareRatio(a.Value, a.Weight, b.Value, b.Weight);
                    return r != 0 ? r : b.Value.CompareTo(a.Value);
                }),
                Sorted((a, b) =>
                {
                    var r = CompareRatio(
                        a.Value, a.UnitVolume, b.Value, b.UnitVolume);
                    return r != 0 ? r : b.Value.CompareTo(a.Value);
                }),
                Sorted((a, b) =>
                {
                    var r = b.UnitVolume.CompareTo(a.UnitVolume);
                    return r != 0 ? r : b.Value.CompareTo(a.Value);
                }),
                Sorted((a, b) =>
                {
                    var r = Density(b, containerVolume)
                        .CompareTo(Density(a, containerVolume));
                    return r != 0 ? r : b.Value.CompareTo(a.Value);
                }),
                Sorted((a, b) =>
                {
                    var r = MaxDimension(b).CompareTo(MaxDimension(a));
                    return r != 0 ? r : b.UnitVolume.CompareTo(a.UnitVolume);
                }),
            ];
        }

        private double Density(Kind kind, long containerVolume)
        {
            var cost = (double)kind.UnitVolume / containerVolume;
            if (container.MaxWeight > 0)
            {
                cost += (double)kind.Weight / container.MaxWeight;
            }

            return cost <= 0 ? double.MaxValue : kind.Value / cost;
        }

        private static int MaxDimension(Kind kind)
        {
            var orientation = kind.Orientations[0];
            return Math.Max(
                orientation.Width,
                Math.Max(orientation.Depth, orientation.Height));
        }

        /// <summary>
        /// Descending comparison of leftValue/leftCost against
        /// rightValue/rightCost, treating a zero cost as unbounded density.
        /// </summary>
        private static int CompareRatio(
            long leftValue,
            long leftCost,
            long rightValue,
            long rightCost)
        {
            if (leftCost <= 0 && rightCost <= 0)
            {
                return rightValue.CompareTo(leftValue);
            }

            if (leftCost <= 0)
            {
                return -1;
            }

            if (rightCost <= 0)
            {
                return 1;
            }

            return (rightValue * leftCost).CompareTo(leftValue * rightCost);
        }

        private int[] Sorted(Comparison<Kind> comparison)
        {
            var ordered = (Kind[])kinds.Clone();
            Array.Sort(ordered, (a, b) =>
            {
                var result = comparison(a, b);
                return result != 0 ? result : string.CompareOrdinal(a.Id, b.Id);
            });
            return ordered.Select(k => k.Index).ToArray();
        }

        /// <summary>
        /// Bounded knapsack over the weight limit, used to cap quantities when
        /// weight rather than space is the binding constraint.
        /// </summary>
        private int[]? BuildWeightKnapsackCaps()
        {
            long totalWeight = 0;
            foreach (var kind in kinds)
            {
                totalWeight += (long)kind.Weight * kind.Quantity;
            }

            if (totalWeight <= container.MaxWeight)
            {
                return null;
            }

            var capacity = (int)Math.Min(container.MaxWeight, totalWeight);
            if (capacity <= 0 || capacity > 200_000)
            {
                return null;
            }

            var caps = new int[kinds.Length];
            var items =
                new List<(int Kind, int Count, int Weight, long Value, long Volume)>();
            foreach (var kind in kinds)
            {
                if (kind.Weight == 0)
                {
                    caps[kind.Index] = kind.Quantity;
                    continue;
                }

                var remaining = kind.Quantity;
                var chunk = 1;
                while (remaining > 0)
                {
                    var take = Math.Min(chunk, remaining);
                    var chunkWeight = (long)kind.Weight * take;
                    if (chunkWeight <= capacity)
                    {
                        items.Add((
                            kind.Index,
                            take,
                            (int)chunkWeight,
                            (long)kind.Value * take,
                            kind.UnitVolume * take));
                    }

                    remaining -= take;
                    chunk <<= 1;
                }

                if (items.Count > 400)
                {
                    return null;
                }
            }

            if (items.Count == 0)
            {
                return caps;
            }

            var bestValue = new long[capacity + 1];
            var bestVolume = new long[capacity + 1];
            var taken = new bool[items.Count][];
            for (var i = 0; i < items.Count; i++)
            {
                taken[i] = new bool[capacity + 1];
                var item = items[i];
                for (var w = capacity; w >= item.Weight; w--)
                {
                    var previous = w - item.Weight;
                    var value = bestValue[previous] + item.Value;
                    var volume = bestVolume[previous] + item.Volume;
                    if (value > bestValue[w]
                        || (value == bestValue[w] && volume > bestVolume[w]))
                    {
                        bestValue[w] = value;
                        bestVolume[w] = volume;
                        taken[i][w] = true;
                    }
                }
            }

            var cursor = capacity;
            for (var i = items.Count - 1; i >= 0; i--)
            {
                if (!taken[i][cursor])
                {
                    continue;
                }

                var item = items[i];
                caps[item.Kind] += item.Count;
                cursor -= item.Weight;
            }

            return caps;
        }

        private Layout Run(Strategy strategy)
        {
            var layout = new Layout(kinds.Length);
            if (strategy.Interleave)
            {
                var progressed = true;
                while (progressed)
                {
                    progressed = false;
                    foreach (var index in strategy.Order)
                    {
                        if (layout.Blocked[index]
                            || layout.Used[index] >= strategy.Caps[index])
                        {
                            continue;
                        }

                        if (TryPlace(layout, index, strategy.Rule))
                        {
                            progressed = true;
                        }
                        else
                        {
                            layout.Blocked[index] = true;
                        }
                    }
                }

                return layout;
            }

            foreach (var index in strategy.Order)
            {
                while (!layout.Blocked[index]
                    && layout.Used[index] < strategy.Caps[index])
                {
                    if (!TryPlace(layout, index, strategy.Rule))
                    {
                        layout.Blocked[index] = true;
                    }
                }
            }

            return layout;
        }

        private bool TryPlace(Layout layout, int kindIndex, int rule)
        {
            var kind = kinds[kindIndex];
            if (layout.Weight + kind.Weight > container.MaxWeight
                || work >= HardWorkBudget)
            {
                return false;
            }

            var found = false;
            var bestKey = default(Key);
            var bestX = 0;
            var bestY = 0;
            var bestZ = 0;
            var bestOrientation = default(OrientedDimensions);

            var outerIsX = rule == RuleLeftBottomDeep;
            var outer = outerIsX ? layout.Xs : layout.Ys;
            var inner = outerIsX ? layout.Ys : layout.Xs;

            foreach (var orientation in kind.Orientations)
            {
                var w = orientation.Width;
                var d = orientation.Depth;
                var h = orientation.Height;
                var outerLimit = outerIsX
                    ? container.Width - w
                    : container.Depth - d;
                var innerLimit = outerIsX
                    ? container.Depth - d
                    : container.Width - w;
                var settled = false;

                for (var oi = 0; oi < outer.Count && !settled; oi++)
                {
                    var outerValue = outer[oi];
                    if (outerValue > outerLimit)
                    {
                        continue;
                    }

                    for (var ii = 0; ii < inner.Count; ii++)
                    {
                        var innerValue = inner[ii];
                        if (innerValue > innerLimit)
                        {
                            continue;
                        }

                        var x = outerIsX ? outerValue : innerValue;
                        var y = outerIsX ? innerValue : outerValue;
                        if (!TryFit(layout, x, y, w, d, h, out var z))
                        {
                            continue;
                        }

                        var key = rule switch
                        {
                            RuleLeftBottomDeep => new Key(z, x, y, h, w, d),
                            RuleMaxContact => new Key(
                                z,
                                -Contact(layout, x, y, z, w, d, h),
                                y,
                                x,
                                h,
                                w),
                            _ => new Key(z, y, x, h, w, d),
                        };

                        if (!found || key.IsBetterThan(bestKey))
                        {
                            found = true;
                            bestKey = key;
                            bestX = x;
                            bestY = y;
                            bestZ = z;
                            bestOrientation = orientation;
                        }

                        if (z == 0 && rule != RuleMaxContact)
                        {
                            settled = true;
                            break;
                        }
                    }
                }
            }

            if (!found)
            {
                return false;
            }

            Add(layout, kind, bestOrientation, bestX, bestY, bestZ);
            return true;
        }

        private void Add(
            Layout layout,
            Kind kind,
            OrientedDimensions orientation,
            int x,
            int y,
            int z)
        {
            layout.Boxes.Add(new(
                kind.Index,
                x,
                y,
                z,
                orientation.Width,
                orientation.Depth,
                orientation.Height));
            layout.Used[kind.Index]++;
            layout.Weight += kind.Weight;
            layout.Value += kind.Value;
            layout.Volume += orientation.Volume;
            Insert(layout.Xs, x + orientation.Width, container.Width);
            Insert(layout.Ys, y + orientation.Depth, container.Depth);
        }

        private static void Insert(List<int> values, int value, int limit)
        {
            if (value >= limit)
            {
                return;
            }

            var index = values.BinarySearch(value);
            if (index < 0)
            {
                values.Insert(~index, value);
            }
        }

        /// <summary>
        /// Finds the resting height for a footprint. A position is usable only
        /// when the supporting surface is perfectly flat across the whole
        /// footprint, which rules out overlaps and partial support at once.
        /// </summary>
        private bool TryFit(
            Layout layout,
            int x,
            int y,
            int w,
            int d,
            int h,
            out int z)
        {
            z = 0;
            var x2 = x + w;
            var y2 = y + d;
            var boxes = CollectionsMarshal.AsSpan(layout.Boxes);
            work += boxes.Length + 1;

            var top = 0;
            for (var i = 0; i < boxes.Length; i++)
            {
                ref readonly var box = ref boxes[i];
                if (box.X < x2 && x < box.X2 && box.Y < y2 && y < box.Y2
                    && box.Top > top)
                {
                    top = box.Top;
                }
            }

            if (top + h > container.Height)
            {
                return false;
            }

            if (top == 0)
            {
                return true;
            }

            work += boxes.Length;
            long covered = 0;
            for (var i = 0; i < boxes.Length; i++)
            {
                ref readonly var box = ref boxes[i];
                if (box.Top != top)
                {
                    continue;
                }

                var ix1 = x > box.X ? x : box.X;
                var ix2 = x2 < box.X2 ? x2 : box.X2;
                if (ix1 >= ix2)
                {
                    continue;
                }

                var iy1 = y > box.Y ? y : box.Y;
                var iy2 = y2 < box.Y2 ? y2 : box.Y2;
                if (iy1 >= iy2)
                {
                    continue;
                }

                covered += (long)(ix2 - ix1) * (iy2 - iy1);
            }

            if (covered != (long)w * d)
            {
                return false;
            }

            z = top;
            return true;
        }

        private long Contact(
            Layout layout,
            int x,
            int y,
            int z,
            int w,
            int d,
            int h)
        {
            var x2 = x + w;
            var y2 = y + d;
            var z2 = z + h;
            long score = 0;
            if (x == 0) score += (long)d * h;
            if (x2 == container.Width) score += (long)d * h;
            if (y == 0) score += (long)w * h;
            if (y2 == container.Depth) score += (long)w * h;
            if (z2 == container.Height) score += (long)w * d;

            var boxes = CollectionsMarshal.AsSpan(layout.Boxes);
            work += boxes.Length;
            for (var i = 0; i < boxes.Length; i++)
            {
                ref readonly var box = ref boxes[i];
                var oz1 = z > box.Z ? z : box.Z;
                var oz2 = z2 < box.Top ? z2 : box.Top;
                if (oz1 >= oz2)
                {
                    continue;
                }

                if (x2 == box.X || box.X2 == x)
                {
                    var oy1 = y > box.Y ? y : box.Y;
                    var oy2 = y2 < box.Y2 ? y2 : box.Y2;
                    if (oy1 < oy2)
                    {
                        score += (long)(oy2 - oy1) * (oz2 - oz1);
                    }
                }

                if (y2 == box.Y || box.Y2 == y)
                {
                    var ox1 = x > box.X ? x : box.X;
                    var ox2 = x2 < box.X2 ? x2 : box.X2;
                    if (ox1 < ox2)
                    {
                        score += (long)(ox2 - ox1) * (oz2 - oz1);
                    }
                }
            }

            return score;
        }

        private PackingResult ToResult(Layout? layout)
        {
            if (layout is null || layout.Boxes.Count == 0)
            {
                return PackingResult.Empty;
            }

            var placements = new List<Placement>(layout.Boxes.Count);
            foreach (var kind in kinds.OrderBy(k => k.Id, StringComparer.Ordinal))
            {
                var instance = 0;
                var owned = layout.Boxes
                    .Where(b => b.Kind == kind.Index)
                    .OrderBy(b => b.X)
                    .ThenBy(b => b.Y)
                    .ThenBy(b => b.Z);
                foreach (var box in owned)
                {
                    placements.Add(new(
                        kind.Id,
                        instance++,
                        box.X,
                        box.Y,
                        box.Z,
                        box.W,
                        box.D,
                        box.H));
                }
            }

            return new(placements);
        }
    }
}
