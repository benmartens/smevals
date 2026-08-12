namespace CartonPacking;

public sealed class CartonPacker
{
    private const int DynamicProgrammingWeightLimit = 20_000;
    private const int SearchCandidateLimit = 160;

    public PackingResult Pack(PackingProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        if (!IsUsableProblem(problem, out var items))
        {
            return PackingResult.Empty;
        }

        if (items.Count == 0)
        {
            return new PackingResult([]);
        }

        var selectedByWeight = SelectByWeight(items, problem.Container.MaxWeight);
        var best = new BestLayout();

        RunGreedy(problem.Container, items, selectedByWeight, GreedyMode.Selected, best);
        RunGreedy(problem.Container, items, selectedByWeight, GreedyMode.Value, best);
        RunGreedy(problem.Container, items, selectedByWeight, GreedyMode.Base, best);
        RunGreedy(
            problem.Container,
            items,
            selectedByWeight,
            GreedyMode.SmallBase,
            best);
        RunGreedy(problem.Container, items, selectedByWeight, GreedyMode.Volume, best);

        var searchBudget = items.Count <= 16
            ? 600_000
            : items.Count <= 32
                ? 220_000
                : 75_000;

        foreach (var order in BuildSearchOrders(items, selectedByWeight))
        {
            if (searchBudget <= 0)
            {
                break;
            }

            var search = new SearchContext(
                problem.Container,
                order,
                best,
                Math.Min(searchBudget, items.Count <= 16 ? 100_000 : 55_000));
            search.Run();
            searchBudget -= search.NodesVisited;
        }

        var placements = best.Placements
            .OrderBy(placement => placement.CartonId, StringComparer.Ordinal)
            .ThenBy(placement => placement.Instance)
            .ThenBy(placement => placement.X)
            .ThenBy(placement => placement.Y)
            .ThenBy(placement => placement.Z)
            .ToList();
        return new PackingResult(placements);
    }

    private static bool IsUsableProblem(
        PackingProblem problem,
        out List<Item> items)
    {
        items = [];
        var container = problem.Container;
        if (container is null
            || container.Width <= 0
            || container.Depth <= 0
            || container.Height <= 0
            || container.MaxWeight < 0
            || problem.Cartons is null)
        {
            return false;
        }

        var cartonIds = new HashSet<string>(StringComparer.Ordinal);
        var containerVolume = SafeVolume(container.Width, container.Depth, container.Height);
        for (var typeIndex = 0; typeIndex < problem.Cartons.Count; typeIndex++)
        {
            var carton = problem.Cartons[typeIndex];
            if (carton is null
                || string.IsNullOrWhiteSpace(carton.Id)
                || carton.Width <= 0
                || carton.Depth <= 0
                || carton.Height <= 0
                || carton.Quantity < 0
                || carton.Weight < 0
                || carton.Value < 0
                || !cartonIds.Add(carton.Id))
            {
                return false;
            }

            if (carton.Quantity == 0)
            {
                continue;
            }

            var cartonVolume = SafeVolume(carton.Width, carton.Depth, carton.Height);
            if (cartonVolume > containerVolume)
            {
                continue;
            }

            var usefulQuantity = (long)carton.Quantity;
            if (cartonVolume > 0 && containerVolume < long.MaxValue)
            {
                usefulQuantity = Math.Min(usefulQuantity, containerVolume / cartonVolume);
            }

            if (carton.Weight > 0)
            {
                usefulQuantity = Math.Min(
                    usefulQuantity,
                    container.MaxWeight / (long)carton.Weight);
            }

            for (var instance = 0L; instance < usefulQuantity; instance++)
            {
                var item = new Item(
                    carton,
                    typeIndex,
                    (int)instance,
                    cartonVolume);
                item.OriginalIndex = items.Count;
                items.Add(item);
            }
        }

        return true;
    }

    private static long SafeVolume(int width, int depth, int height)
    {
        var first = (long)width * depth;
        if (first > long.MaxValue / height)
        {
            return long.MaxValue;
        }

        return first * height;
    }

    private static bool[] SelectByWeight(IReadOnlyList<Item> items, int maxWeight)
    {
        var selected = new bool[items.Count];
        var bounds = BuildBounds(items, maxWeight);
        if (bounds is not null)
        {
            var remainingWeight = bounds.Capacity;
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var skip = bounds.Get(index + 1, remainingWeight);
                if (item.Carton.Weight > remainingWeight)
                {
                    continue;
                }

                var include = bounds.Get(index + 1, remainingWeight - item.Carton.Weight);
                include = new Bound(
                    include.Value + item.Carton.Value,
                    include.Volume + item.Volume);
                if (include.Value > skip.Value
                    || (include.Value == skip.Value && include.Volume >= skip.Volume))
                {
                    selected[index] = true;
                    remainingWeight -= item.Carton.Weight;
                }
            }

            return selected;
        }

        var remaining = (long)maxWeight;
        foreach (var item in items
                     .OrderBy(item => item, ItemDensityComparer.Instance)
                     .ThenByDescending(item => item.Carton.Value)
                     .ThenByDescending(item => item.Volume)
                     .ThenBy(item => item.OriginalIndex))
        {
            if (item.Carton.Weight <= remaining)
            {
                selected[item.OriginalIndex] = true;
                remaining -= item.Carton.Weight;
            }
        }

        return selected;
    }

    private static void RunGreedy(
        ContainerSpec container,
        IReadOnlyList<Item> items,
        IReadOnlyList<bool> selectedByWeight,
        GreedyMode mode,
        BestLayout best)
    {
        var used = new bool[items.Count];
        var placed = new List<Placement>();
        long weight = 0;
        long value = 0;
        long volume = 0;

        while (true)
        {
            GreedyChoice? choice = null;
            for (var index = 0; index < items.Count; index++)
            {
                if (used[index])
                {
                    continue;
                }

                var item = items[index];
                if (weight + item.Carton.Weight > container.MaxWeight)
                {
                    continue;
                }

                var candidates = GetCandidates(
                    item,
                    placed,
                    container,
                    maxCandidates: 512);
                if (candidates.Count == 0)
                {
                    continue;
                }

                var next = new GreedyChoice(item, candidates[0]);
                if (choice is null
                    || CompareGreedyChoices(
                        next,
                        choice,
                        selectedByWeight,
                        mode) < 0)
                {
                    choice = next;
                }
            }

            if (choice is null)
            {
                break;
            }

            used[choice.Item.OriginalIndex] = true;
            placed.Add(choice.Placement);
            weight += choice.Item.Carton.Weight;
            value += choice.Item.Carton.Value;
            volume += choice.Item.Volume;
            best.Consider(placed, weight, value, volume);
        }
    }

    private static int CompareGreedyChoices(
        GreedyChoice left,
        GreedyChoice right,
        IReadOnlyList<bool> selectedByWeight,
        GreedyMode mode)
    {
        var comparison = 0;
        switch (mode)
        {
            case GreedyMode.Selected:
                comparison = selectedByWeight[right.Item.OriginalIndex]
                    .CompareTo(selectedByWeight[left.Item.OriginalIndex]);
                if (comparison == 0)
                {
                    comparison = CompareValue(left.Item, right.Item);
                }

                if (comparison == 0)
                {
                    comparison = CompareDensity(left.Item, right.Item);
                }

                if (comparison == 0)
                {
                    comparison = CompareVolume(left.Item, right.Item);
                }

                break;
            case GreedyMode.Value:
                comparison = CompareValue(left.Item, right.Item);
                if (comparison == 0)
                {
                    comparison = CompareDensity(left.Item, right.Item);
                }

                if (comparison == 0)
                {
                    comparison = CompareVolume(left.Item, right.Item);
                }

                break;
            case GreedyMode.Base:
                comparison = right.Item.BaseArea.CompareTo(left.Item.BaseArea);
                if (comparison == 0)
                {
                    comparison = left.Item.MinimumHeight.CompareTo(
                        right.Item.MinimumHeight);
                }

                if (comparison == 0)
                {
                    comparison = CompareValue(left.Item, right.Item);
                }

                break;
            case GreedyMode.SmallBase:
                comparison = left.Item.BaseArea.CompareTo(right.Item.BaseArea);
                if (comparison == 0)
                {
                    comparison = left.Item.MinimumHeight.CompareTo(
                        right.Item.MinimumHeight);
                }

                if (comparison == 0)
                {
                    comparison = CompareValue(left.Item, right.Item);
                }

                break;
            case GreedyMode.Volume:
                comparison = CompareVolume(left.Item, right.Item);
                if (comparison == 0)
                {
                    comparison = CompareValue(left.Item, right.Item);
                }

                break;
        }

        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareCandidates(left.Placement, right.Placement);
        return comparison != 0
            ? comparison
            : left.Item.OriginalIndex.CompareTo(right.Item.OriginalIndex);
    }

    private static int CompareValue(Item left, Item right) =>
        right.Carton.Value.CompareTo(left.Carton.Value);

    private static int CompareVolume(Item left, Item right) =>
        right.Volume.CompareTo(left.Volume);

    private static int CompareDensity(Item left, Item right)
    {
        if (left.Carton.Weight == 0 || right.Carton.Weight == 0)
        {
            if (left.Carton.Weight == right.Carton.Weight)
            {
                return CompareValue(left, right);
            }

            return left.Carton.Weight == 0 ? -1 : 1;
        }

        var leftProduct = (long)left.Carton.Value * right.Carton.Weight;
        var rightProduct = (long)right.Carton.Value * left.Carton.Weight;
        return rightProduct.CompareTo(leftProduct);
    }

    private static List<List<Item>> BuildSearchOrders(
        IReadOnlyList<Item> items,
        IReadOnlyList<bool> selectedByWeight)
    {
        var orders = new List<List<Item>>();
        AddSearchOrder(
            orders,
            items,
            (left, right) =>
            {
                var comparison = left.BaseArea.CompareTo(right.BaseArea);
                return comparison != 0
                    ? comparison
                    : left.MinimumHeight.CompareTo(right.MinimumHeight);
            });
        AddSearchOrder(
            orders,
            items,
            (left, right) =>
            {
                var comparison = selectedByWeight[right.OriginalIndex]
                    .CompareTo(selectedByWeight[left.OriginalIndex]);
                return comparison != 0
                    ? comparison
                    : CompareValue(left, right);
            });
        AddSearchOrder(
            orders,
            items,
            (left, right) =>
            {
                var comparison = selectedByWeight[right.OriginalIndex]
                    .CompareTo(selectedByWeight[left.OriginalIndex]);
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = right.BaseArea.CompareTo(left.BaseArea);
                return comparison != 0
                    ? comparison
                    : left.MinimumHeight.CompareTo(right.MinimumHeight);
            });
        AddSearchOrder(
            orders,
            items,
            (left, right) =>
            {
                var comparison = right.BaseArea.CompareTo(left.BaseArea);
                return comparison != 0
                    ? comparison
                    : CompareValue(left, right);
            });
        AddSearchOrder(
            orders,
            items,
            (left, right) =>
            {
                var comparison = CompareValue(left, right);
                return comparison != 0
                    ? comparison
                    : CompareDensity(left, right);
            });
        AddSearchOrder(
            orders,
            items,
            (left, right) =>
            {
                var comparison = CompareVolume(left, right);
                return comparison != 0
                    ? comparison
                    : CompareValue(left, right);
            });
        return orders;
    }

    private static void AddSearchOrder(
        ICollection<List<Item>> orders,
        IReadOnlyList<Item> items,
        Comparison<Item> comparison)
    {
        var order = items.ToList();
        order.Sort((left, right) =>
        {
            var result = comparison(left, right);
            return result != 0
                ? result
                : left.OriginalIndex.CompareTo(right.OriginalIndex);
        });

        if (!orders.Any(existing =>
                existing.Select(item => item.OriginalIndex)
                    .SequenceEqual(order.Select(item => item.OriginalIndex))))
        {
            orders.Add(order);
        }
    }

    private static List<Placement> GetCandidates(
        Item item,
        IReadOnlyList<Placement> placed,
        ContainerSpec container,
        int maxCandidates)
    {
        var xBreaks = new SortedSet<int> { 0 };
        var yBreaks = new SortedSet<int> { 0 };
        var zBreaks = new SortedSet<int> { 0 };

        foreach (var existing in placed)
        {
            AddBreak(xBreaks, existing.X, container.Width);
            AddBreak(xBreaks, existing.X + existing.Width, container.Width);
            AddBreak(yBreaks, existing.Y, container.Depth);
            AddBreak(yBreaks, existing.Y + existing.Depth, container.Depth);
            AddBreak(zBreaks, existing.Z + existing.Height, container.Height);
        }

        var candidates = new HashSet<Placement>();
        foreach (var orientation in item.Orientations)
        {
            foreach (var z in zBreaks)
            {
                foreach (var x in xBreaks)
                {
                    foreach (var y in yBreaks)
                    {
                        if (x + orientation.Width > container.Width
                            || y + orientation.Depth > container.Depth
                            || z + orientation.Height > container.Height)
                        {
                            continue;
                        }

                        var candidate = new Placement(
                            item.Carton.Id,
                            item.Instance,
                            x,
                            y,
                            z,
                            orientation.Width,
                            orientation.Depth,
                            orientation.Height);
                        if (z > 0
                            && !HasFullSupport(candidate, placed))
                        {
                            continue;
                        }

                        if (placed.Any(existing =>
                                PackingValidator.Overlaps(candidate, existing)))
                        {
                            continue;
                        }

                        candidates.Add(candidate);
                    }
                }
            }
        }

        var ordered = candidates.ToList();
        ordered.Sort(CompareCandidates);
        return LimitCandidates(ordered, maxCandidates);
    }

    private static void AddBreak(SortedSet<int> breaks, int value, int maximum)
    {
        if (value >= 0 && value <= maximum)
        {
            breaks.Add(value);
        }
    }

    private static List<Placement> LimitCandidates(
        List<Placement> candidates,
        int maxCandidates)
    {
        if (maxCandidates <= 0 || candidates.Count <= maxCandidates)
        {
            return candidates;
        }

        var chosen = new List<Placement>(maxCandidates);
        var seen = new HashSet<Placement>();

        void AddCandidate(Placement candidate)
        {
            if (chosen.Count < maxCandidates && seen.Add(candidate))
            {
                chosen.Add(candidate);
            }
        }

        var primaryCount = Math.Max(1, maxCandidates / 2);
        for (var index = 0; index < primaryCount; index++)
        {
            AddCandidate(candidates[index]);
        }

        foreach (var group in candidates.GroupBy(candidate => candidate.Z))
        {
            foreach (var candidate in group.Take(3))
            {
                AddCandidate(candidate);
            }
        }

        foreach (var group in candidates.GroupBy(
                     candidate => (
                         candidate.Width,
                         candidate.Depth,
                         candidate.Height)))
        {
            foreach (var candidate in group.Take(3))
            {
                AddCandidate(candidate);
            }
        }

        foreach (var candidate in candidates)
        {
            AddCandidate(candidate);
            if (chosen.Count == maxCandidates)
            {
                break;
            }
        }

        chosen.Sort(CompareCandidates);
        return chosen;
    }

    private static int CompareCandidates(Placement left, Placement right)
    {
        var comparison = left.Z.CompareTo(right.Z);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.X.CompareTo(right.X);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Y.CompareTo(right.Y);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Width.CompareTo(right.Width);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Depth.CompareTo(right.Depth);
        return comparison != 0
            ? comparison
            : left.Height.CompareTo(right.Height);
    }

    private static bool HasFullSupport(
        Placement upper,
        IReadOnlyList<Placement> placed)
    {
        if (upper.Z == 0)
        {
            return true;
        }

        var supports = new List<Footprint>();
        var xBreaks = new SortedSet<int>
        {
            upper.X,
            upper.X + upper.Width,
        };
        var yBreaks = new SortedSet<int>
        {
            upper.Y,
            upper.Y + upper.Depth,
        };

        foreach (var lower in placed)
        {
            if (lower.Z + lower.Height != upper.Z)
            {
                continue;
            }

            var x1 = Math.Max(upper.X, lower.X);
            var x2 = Math.Min(
                upper.X + upper.Width,
                lower.X + lower.Width);
            var y1 = Math.Max(upper.Y, lower.Y);
            var y2 = Math.Min(
                upper.Y + upper.Depth,
                lower.Y + lower.Depth);
            if (x1 >= x2 || y1 >= y2)
            {
                continue;
            }

            supports.Add(new Footprint(x1, y1, x2, y2));
            xBreaks.Add(x1);
            xBreaks.Add(x2);
            yBreaks.Add(y1);
            yBreaks.Add(y2);
        }

        if (supports.Count == 0)
        {
            return false;
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
                if (supports.Any(support =>
                        support.X1 <= x1
                        && support.X2 >= x2
                        && support.Y1 <= y1
                        && support.Y2 >= y2))
                {
                    coveredArea += (long)(x2 - x1) * (y2 - y1);
                }
            }
        }

        return coveredArea == (long)upper.Width * upper.Depth;
    }

    private static BoundTable? BuildBounds(
        IReadOnlyList<Item> items,
        int maxWeight)
    {
        var totalWeight = 0L;
        foreach (var item in items)
        {
            totalWeight = Math.Min(
                (long)maxWeight,
                totalWeight + item.Carton.Weight);
        }

        var capacity = (int)totalWeight;
        if (capacity > DynamicProgrammingWeightLimit
            || (long)(items.Count + 1) * (capacity + 1) > 8_000_000)
        {
            return null;
        }

        var values = new long[items.Count + 1][];
        var volumes = new long[items.Count + 1][];
        for (var index = 0; index <= items.Count; index++)
        {
            values[index] = new long[capacity + 1];
            volumes[index] = new long[capacity + 1];
        }

        for (var index = items.Count - 1; index >= 0; index--)
        {
            var item = items[index];
            for (var weight = 0; weight <= capacity; weight++)
            {
                var best = new Bound(
                    values[index + 1][weight],
                    volumes[index + 1][weight]);
                if (item.Carton.Weight <= weight)
                {
                    var nextWeight = weight - item.Carton.Weight;
                    var include = new Bound(
                        values[index + 1][nextWeight] + item.Carton.Value,
                        volumes[index + 1][nextWeight] + item.Volume);
                    if (include.Value > best.Value
                        || (include.Value == best.Value
                            && include.Volume > best.Volume))
                    {
                        best = include;
                    }
                }

                values[index][weight] = best.Value;
                volumes[index][weight] = best.Volume;
            }
        }

        return new BoundTable(values, volumes, capacity);
    }

    private sealed class SearchContext
    {
        private readonly ContainerSpec _container;
        private readonly IReadOnlyList<Item> _order;
        private readonly BestLayout _best;
        private readonly int _nodeLimit;
        private readonly BoundTable? _bounds;
        private readonly long[] _remainingValues;
        private readonly long[] _remainingVolumes;
        private int _nodesVisited;

        public SearchContext(
            ContainerSpec container,
            IReadOnlyList<Item> order,
            BestLayout best,
            int nodeLimit)
        {
            _container = container;
            _order = order;
            _best = best;
            _nodeLimit = nodeLimit;
            _bounds = BuildBounds(order, container.MaxWeight);
            _remainingValues = new long[order.Count + 1];
            _remainingVolumes = new long[order.Count + 1];
            for (var index = order.Count - 1; index >= 0; index--)
            {
                _remainingValues[index] =
                    _remainingValues[index + 1] + order[index].Carton.Value;
                _remainingVolumes[index] =
                    _remainingVolumes[index + 1] + order[index].Volume;
            }
        }

        public int NodesVisited => _nodesVisited;

        public void Run()
        {
            Explore(0, [], 0, 0, 0);
        }

        private void Explore(
            int index,
            List<Placement> placed,
            long weight,
            long value,
            long volume)
        {
            if (_nodesVisited++ >= _nodeLimit)
            {
                return;
            }

            _best.Consider(placed, weight, value, volume);
            if (index >= _order.Count
                || !CanImprove(index, weight, value, volume))
            {
                return;
            }

            var item = _order[index];
            if (weight + item.Carton.Weight <= _container.MaxWeight)
            {
                var candidates = GetCandidates(
                    item,
                    placed,
                    _container,
                    SearchCandidateLimit);
                foreach (var candidate in candidates)
                {
                    placed.Add(candidate);
                    Explore(
                        index + 1,
                        placed,
                        weight + item.Carton.Weight,
                        value + item.Carton.Value,
                        volume + item.Volume);
                    placed.RemoveAt(placed.Count - 1);
                    if (_nodesVisited >= _nodeLimit)
                    {
                        return;
                    }
                }
            }

            Explore(index + 1, placed, weight, value, volume);
        }

        private bool CanImprove(
            int index,
            long weight,
            long value,
            long volume)
        {
            var remainingWeight = _container.MaxWeight - weight;
            Bound bound;
            if (_bounds is not null)
            {
                bound = _bounds.Get(
                    index,
                    Math.Min((int)remainingWeight, _bounds.Capacity));
            }
            else
            {
                bound = new Bound(
                    _remainingValues[index],
                    _remainingVolumes[index]);
            }

            var possibleValue = value + bound.Value;
            if (possibleValue > _best.Value)
            {
                return true;
            }

            return possibleValue == _best.Value
                && volume + bound.Volume > _best.Volume;
        }
    }

    private sealed class BestLayout
    {
        public List<Placement> Placements { get; private set; } = [];
        public long Weight { get; private set; }
        public long Value { get; private set; }
        public long Volume { get; private set; }

        public void Consider(
            IReadOnlyList<Placement> placements,
            long weight,
            long value,
            long volume)
        {
            if (value < Value
                || (value == Value && volume <= Volume))
            {
                return;
            }

            Placements = placements.ToList();
            Weight = weight;
            Value = value;
            Volume = volume;
        }
    }

    private sealed class Item
    {
        public Item(
            CartonType carton,
            int typeIndex,
            int instance,
            long volume)
        {
            Carton = carton;
            TypeIndex = typeIndex;
            Instance = instance;
            OriginalIndex = -1;
            Volume = volume;
            Orientations = OrientationGenerator.GetOrientations(carton);
            BaseArea = Orientations.Count == 0
                ? 0
                : Orientations.Max(
                    orientation => (long)orientation.Width * orientation.Depth);
            MinimumHeight = Orientations.Count == 0
                ? int.MaxValue
                : Orientations.Min(orientation => orientation.Height);
        }

        public CartonType Carton { get; }
        public int TypeIndex { get; }
        public int Instance { get; }
        public int OriginalIndex { get; set; }
        public long Volume { get; }
        public IReadOnlyList<OrientedDimensions> Orientations { get; }
        public long BaseArea { get; }
        public int MinimumHeight { get; }
    }

    private sealed class GreedyChoice
    {
        public GreedyChoice(Item item, Placement placement)
        {
            Item = item;
            Placement = placement;
        }

        public Item Item { get; }
        public Placement Placement { get; }
    }

    private enum GreedyMode
    {
        Selected,
        Value,
        Base,
        Volume,
        SmallBase,
    }

    private sealed class BoundTable
    {
        public BoundTable(long[][] values, long[][] volumes, int capacity)
        {
            Values = values;
            Volumes = volumes;
            Capacity = capacity;
        }

        private long[][] Values { get; }
        private long[][] Volumes { get; }
        public int Capacity { get; }

        public Bound Get(int index, int weight) =>
            new(Values[index][weight], Volumes[index][weight]);
    }

    private readonly record struct Bound(long Value, long Volume);

    private readonly record struct Footprint(int X1, int Y1, int X2, int Y2);

    private sealed class ItemDensityComparer : IComparer<Item>
    {
        public static ItemDensityComparer Instance { get; } = new();

        public int Compare(Item? left, Item? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return 1;
            }

            if (right is null)
            {
                return -1;
            }

            var comparison = CompareDensity(left, right);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareValue(left, right);
            return comparison != 0
                ? comparison
                : left.OriginalIndex.CompareTo(right.OriginalIndex);
        }
    }
}
