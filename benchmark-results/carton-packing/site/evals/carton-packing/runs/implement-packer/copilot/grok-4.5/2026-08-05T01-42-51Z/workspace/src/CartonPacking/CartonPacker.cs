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

        var items = ExpandItems(problem.Cartons);
        if (items.Count == 0)
        {
            return PackingResult.Empty;
        }

        var best = PackingResult.Empty;
        long bestValue = -1;
        long bestVolume = -1;

        foreach (var candidate in EnumerateCandidates(container, items))
        {
            var report = PackingValidator.Validate(problem, candidate);
            if (!report.IsValid)
            {
                continue;
            }

            if (report.TotalValue > bestValue
                || (report.TotalValue == bestValue && report.TotalVolume > bestVolume))
            {
                best = candidate;
                bestValue = report.TotalValue;
                bestVolume = report.TotalVolume;
            }
        }

        return best;
    }

    private static List<Item> ExpandItems(IEnumerable<CartonType> cartons)
    {
        var items = new List<Item>();
        foreach (var carton in cartons.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            if (carton.Quantity <= 0
                || carton.Width <= 0
                || carton.Depth <= 0
                || carton.Height <= 0)
            {
                continue;
            }

            var orientations = OrientationGenerator.GetOrientations(carton)
                .Where(o => o.Width > 0 && o.Depth > 0 && o.Height > 0)
                .ToArray();
            if (orientations.Length == 0)
            {
                continue;
            }

            for (var instance = 0; instance < carton.Quantity; instance++)
            {
                items.Add(new Item(carton, instance, orientations));
            }
        }

        return items;
    }

    private static IEnumerable<PackingResult> EnumerateCandidates(
        ContainerSpec container,
        List<Item> items)
    {
        foreach (var order in BuildOrders(items))
        {
            yield return GreedyPack(container, order, allowSkip: true);
            yield return GreedyPack(container, order, allowSkip: false);
        }

        foreach (var subset in BuildWeightAwareSubsets(container, items))
        {
            foreach (var order in BuildOrders(subset))
            {
                yield return GreedyPack(container, order, allowSkip: true);
                yield return GreedyPack(container, order, allowSkip: false);
            }
        }

        if (items.Count <= 14)
        {
            var searchBest = SearchPack(container, items);
            if (searchBest.Placements.Count > 0)
            {
                yield return searchBest;
            }
        }
    }

    private static List<List<Item>> BuildOrders(List<Item> items)
    {
        var orders = new List<List<Item>>();

        void Add(IEnumerable<Item> seq)
        {
            var list = seq.ToList();
            if (list.Count == 0)
            {
                return;
            }

            if (orders.Any(existing =>
                    existing.Count == list.Count
                    && existing.Zip(list).All(pair =>
                        pair.First.Carton.Id == pair.Second.Carton.Id
                        && pair.First.Instance == pair.Second.Instance)))
            {
                return;
            }

            orders.Add(list);
        }

        Add(items.OrderByDescending(i => i.Carton.Value)
            .ThenByDescending(i => i.Volume)
            .ThenBy(i => i.Carton.Id, StringComparer.Ordinal)
            .ThenBy(i => i.Instance));

        Add(items.OrderByDescending(ValuePerWeight)
            .ThenByDescending(i => i.Carton.Value)
            .ThenByDescending(i => i.Volume)
            .ThenBy(i => i.Carton.Id, StringComparer.Ordinal)
            .ThenBy(i => i.Instance));

        Add(items.OrderByDescending(ValuePerVolume)
            .ThenByDescending(i => i.Carton.Value)
            .ThenByDescending(i => i.Volume)
            .ThenBy(i => i.Carton.Id, StringComparer.Ordinal)
            .ThenBy(i => i.Instance));

        Add(items.OrderByDescending(i => i.Volume)
            .ThenByDescending(i => i.Carton.Value)
            .ThenBy(i => i.Carton.Id, StringComparer.Ordinal)
            .ThenBy(i => i.Instance));

        Add(items.OrderByDescending(i => i.MaxDim)
            .ThenByDescending(i => i.Volume)
            .ThenByDescending(i => i.Carton.Value)
            .ThenBy(i => i.Carton.Id, StringComparer.Ordinal)
            .ThenBy(i => i.Instance));

        Add(items.OrderBy(i => i.Carton.Id, StringComparer.Ordinal)
            .ThenBy(i => i.Instance));

        Add(items.OrderByDescending(i => i.Carton.Weight)
            .ThenByDescending(i => i.Carton.Value)
            .ThenBy(i => i.Carton.Id, StringComparer.Ordinal)
            .ThenBy(i => i.Instance));

        Add(items.OrderBy(i => i.Carton.Weight)
            .ThenByDescending(i => i.Carton.Value)
            .ThenBy(i => i.Carton.Id, StringComparer.Ordinal)
            .ThenBy(i => i.Instance));

        return orders;
    }

    private static List<List<Item>> BuildWeightAwareSubsets(
        ContainerSpec container,
        List<Item> items)
    {
        var subsets = new List<List<Item>>();
        var maxWeight = container.MaxWeight;
        var containerVolume = (long)container.Width * container.Depth * container.Height;

        if (maxWeight <= 5000 && items.Count <= 40)
                {
                    var n = items.Count;
                    var curVal = new long[maxWeight + 1];
                    var curVol = new long[maxWeight + 1];
                    var parentItem = new int[maxWeight + 1];
                    var parentWeight = new int[maxWeight + 1];
                    Array.Fill(curVal, long.MinValue / 4);
                    Array.Fill(parentItem, -1);
                    curVal[0] = 0;

                    for (var i = 0; i < n; i++)
                    {
                        var item = items[i];
                        var weight = item.Carton.Weight;
                        var value = (long)item.Carton.Value;
                        var volume = item.Volume;
                        if (weight > maxWeight || volume > containerVolume || weight < 0)
                        {
                            continue;
                        }

                        var nextVal = (long[])curVal.Clone();
                        var nextVol = (long[])curVol.Clone();
                        var nextParentItem = (int[])parentItem.Clone();
                        var nextParentWeight = (int[])parentWeight.Clone();

                        for (var w = weight; w <= maxWeight; w++)
                        {
                            if (curVal[w - weight] <= long.MinValue / 8)
                            {
                                continue;
                            }

                            var newVal = curVal[w - weight] + value;
                            var newVol = curVol[w - weight] + volume;
                            if (newVal > nextVal[w] || (newVal == nextVal[w] && newVol > nextVol[w]))
                            {
                                nextVal[w] = newVal;
                                nextVol[w] = newVol;
                                nextParentItem[w] = i;
                                nextParentWeight[w] = w - weight;
                            }
                        }

                        curVal = nextVal;
                        curVol = nextVol;
                        parentItem = nextParentItem;
                        parentWeight = nextParentWeight;
                    }

                    var bestW = 0;
                    for (var w = 1; w <= maxWeight; w++)
                    {
                        if (curVal[w] > curVal[bestW]
                            || (curVal[w] == curVal[bestW] && curVol[w] > curVol[bestW]))
                        {
                            bestW = w;
                        }
                    }

                    if (curVal[bestW] > 0)
                    {
                        var chosen = ReconstructSubset(items, parentItem, parentWeight, bestW);
                        if (chosen.Count > 0)
                        {
                            subsets.Add(chosen);

                            subsets.Add(chosen
                                .OrderByDescending(ValuePerVolume)
                                .ThenByDescending(i => i.Carton.Value)
                                .ThenBy(i => i.Carton.Id, StringComparer.Ordinal)
                                .ThenBy(i => i.Instance)
                                .ToList());

                            if (chosen.Count > 1)
                            {
                                var dropWorst = chosen
                                    .OrderBy(ValuePerVolume)
                                    .ThenBy(i => i.Carton.Value)
                                    .ThenBy(i => i.Carton.Id, StringComparer.Ordinal)
                                    .ThenBy(i => i.Instance)
                                    .Skip(1)
                                    .ToList();
                                if (dropWorst.Count > 0)
                                {
                                    subsets.Add(dropWorst);
                                }
                            }
                        }
                    }
                }

        var greedy = new List<Item>();
        long gw = 0;
        long gv = 0;
        foreach (var item in items
                     .OrderByDescending(ValuePerWeight)
                     .ThenByDescending(i => i.Carton.Value)
                     .ThenByDescending(i => i.Volume)
                     .ThenBy(i => i.Carton.Id, StringComparer.Ordinal)
                     .ThenBy(i => i.Instance))
        {
            if (gw + item.Carton.Weight <= maxWeight
                && gv + item.Volume <= containerVolume)
            {
                greedy.Add(item);
                gw += item.Carton.Weight;
                gv += item.Volume;
            }
        }

        if (greedy.Count > 0)
        {
            subsets.Add(greedy);
        }

        return subsets;
    }

    private static List<Item> ReconstructSubset(
            List<Item> items,
            int[] parentItem,
            int[] parentWeight,
            int weight)
        {
            var selected = new bool[items.Count];
            var w = weight;
            var guard = 0;
            while (w > 0 && parentItem[w] >= 0 && guard++ < items.Count + 5)
            {
                var idx = parentItem[w];
                if (idx < 0 || idx >= items.Count || selected[idx])
                {
                    break;
                }

                selected[idx] = true;
                w = parentWeight[w];
            }

            var result = new List<Item>();
            for (var i = 0; i < items.Count; i++)
            {
                if (selected[i])
                {
                    result.Add(items[i]);
                }
            }

            return result;
        }

    private static PackingResult GreedyPack(
        ContainerSpec container,
        List<Item> orderedItems,
        bool allowSkip)
    {
        var placements = new List<Placement>();
        long usedWeight = 0;

        foreach (var item in orderedItems)
        {
            if (usedWeight + item.Carton.Weight > container.MaxWeight)
            {
                if (allowSkip)
                {
                    continue;
                }

                break;
            }

            if (!TryPlace(container, placements, item, out var placement))
            {
                if (allowSkip)
                {
                    continue;
                }

                break;
            }

            placements.Add(placement);
            usedWeight += item.Carton.Weight;
        }

        return Canonical(placements);
    }

    private static PackingResult SearchPack(ContainerSpec container, List<Item> items)
    {
        var ordered = items
            .OrderByDescending(ValuePerWeight)
            .ThenByDescending(i => i.Carton.Value)
            .ThenByDescending(i => i.Volume)
            .ThenBy(i => i.Carton.Id, StringComparer.Ordinal)
            .ThenBy(i => i.Instance)
            .ToList();

        var bestPlacements = new List<Placement>();
        long bestValue = 0;
        long bestVolume = 0;
        var nodeCount = 0;
        const int maxNodes = 50000;

        var current = new List<Placement>();
        var used = new bool[ordered.Count];
        var itemValue = ordered.Select(i => (long)i.Carton.Value).ToArray();
        var itemWeight = ordered.Select(i => i.Carton.Weight).ToArray();
        var itemLookup = new Dictionary<(string, int), Item>();
        foreach (var item in ordered)
        {
            itemLookup[(item.Carton.Id, item.Instance)] = item;
        }

        void Consider()
        {
            long value = 0;
            long volume = 0;
            foreach (var p in current)
            {
                var item = itemLookup[(p.CartonId, p.Instance)];
                value += item.Carton.Value;
                volume += (long)p.Width * p.Depth * p.Height;
            }

            if (value > bestValue || (value == bestValue && volume > bestVolume))
            {
                bestValue = value;
                bestVolume = volume;
                bestPlacements = current.ToList();
            }
        }

        void Dfs(long usedWeight, long currentValue)
        {
            if (++nodeCount > maxNodes)
            {
                return;
            }

            Consider();

            long remainingValue = 0;
            long remainingWeight = container.MaxWeight - usedWeight;
            for (var i = 0; i < ordered.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }

                if (itemWeight[i] <= remainingWeight)
                {
                    remainingValue += itemValue[i];
                    remainingWeight -= itemWeight[i];
                }
            }

            if (currentValue + remainingValue < bestValue)
            {
                return;
            }

            var attempts = 0;
            var maxAttempts = ordered.Count <= 10 ? ordered.Count * 6 : Math.Max(ordered.Count * 2, 8);

            for (var i = 0; i < ordered.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }

                if (usedWeight + itemWeight[i] > container.MaxWeight)
                {
                    continue;
                }

                if (!TryPlace(container, current, ordered[i], out var placement))
                {
                    continue;
                }

                used[i] = true;
                current.Add(placement);
                Dfs(usedWeight + itemWeight[i], currentValue + itemValue[i]);
                current.RemoveAt(current.Count - 1);
                used[i] = false;

                if (nodeCount > maxNodes)
                {
                    return;
                }

                attempts++;
                if (attempts >= maxAttempts)
                {
                    break;
                }
            }
        }

        Dfs(0, 0);
        return Canonical(bestPlacements);
    }

    private static bool TryPlace(
        ContainerSpec container,
        List<Placement> existing,
        Item item,
        out Placement placement)
    {
        placement = null!;
        Placement? best = null;
        var candidates = GenerateCandidatePositions(container, existing);

        foreach (var orient in item.Orientations)
        {
            if (orient.Width > container.Width
                || orient.Depth > container.Depth
                || orient.Height > container.Height)
            {
                continue;
            }

            foreach (var (x, y, z) in candidates)
            {
                if (x + orient.Width > container.Width
                    || y + orient.Depth > container.Depth
                    || z + orient.Height > container.Height)
                {
                    continue;
                }

                var candidate = new Placement(
                    item.Carton.Id,
                    item.Instance,
                    x,
                    y,
                    z,
                    orient.Width,
                    orient.Depth,
                    orient.Height);

                if (!IsValidPlacement(existing, candidate))
                {
                    continue;
                }

                if (best is null || IsBetterPosition(candidate, best))
                {
                    best = candidate;
                }
            }
        }

        if (best is null)
        {
            return false;
        }

        placement = best;
        return true;
    }

    private static bool IsBetterPosition(Placement a, Placement b)
    {
        if (a.Z != b.Z)
        {
            return a.Z < b.Z;
        }

        if (a.Y != b.Y)
        {
            return a.Y < b.Y;
        }

        if (a.X != b.X)
        {
            return a.X < b.X;
        }

        if (a.Height != b.Height)
        {
            return a.Height < b.Height;
        }

        if (a.Width != b.Width)
        {
            return a.Width < b.Width;
        }

        return a.Depth < b.Depth;
    }

    private static bool IsValidPlacement(List<Placement> existing, Placement candidate)
    {
        foreach (var other in existing)
        {
            if (PackingValidator.Overlaps(candidate, other))
            {
                return false;
            }
        }

        if (candidate.Z == 0)
        {
            return true;
        }

        return PackingValidator.HasFullBaseSupport(candidate, existing);
    }

    private static List<(int X, int Y, int Z)> GenerateCandidatePositions(
        ContainerSpec container,
        List<Placement> existing)
    {
        var xs = new SortedSet<int> { 0 };
        var ys = new SortedSet<int> { 0 };
        var zs = new SortedSet<int> { 0 };

        foreach (var p in existing)
        {
            xs.Add(p.X);
            xs.Add(p.X + p.Width);
            ys.Add(p.Y);
            ys.Add(p.Y + p.Depth);
            zs.Add(p.Z);
            zs.Add(p.Z + p.Height);
        }

        var xList = xs.Where(v => v < container.Width).ToArray();
        var yList = ys.Where(v => v < container.Depth).ToArray();
        var zList = zs.Where(v => v < container.Height).ToArray();

        var points = new List<(int X, int Y, int Z)>(xList.Length * yList.Length * zList.Length);
        foreach (var z in zList)
        {
            foreach (var y in yList)
            {
                foreach (var x in xList)
                {
                    points.Add((x, y, z));
                }
            }
        }

        return points;
    }

    private static PackingResult Canonical(List<Placement> placements)
    {
        var sorted = placements
            .OrderBy(p => p.CartonId, StringComparer.Ordinal)
            .ThenBy(p => p.Instance)
            .ThenBy(p => p.X)
            .ThenBy(p => p.Y)
            .ThenBy(p => p.Z)
            .ToList();
        return new PackingResult(sorted);
    }

    private static double ValuePerWeight(Item item) =>
        item.Carton.Weight == 0
            ? double.PositiveInfinity
            : (double)item.Carton.Value / item.Carton.Weight;

    private static double ValuePerVolume(Item item) =>
        item.Volume == 0
            ? double.PositiveInfinity
            : (double)item.Carton.Value / item.Volume;

    private sealed class Item
    {
        public Item(CartonType carton, int instance, IReadOnlyList<OrientedDimensions> orientations)
        {
            Carton = carton;
            Instance = instance;
            Orientations = orientations;
            Volume = (long)carton.Width * carton.Depth * carton.Height;
            MaxDim = Math.Max(carton.Width, Math.Max(carton.Depth, carton.Height));
        }

        public CartonType Carton { get; }
        public int Instance { get; }
        public IReadOnlyList<OrientedDimensions> Orientations { get; }
        public long Volume { get; }
        public int MaxDim { get; }
    }
}
