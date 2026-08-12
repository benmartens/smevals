using System.Text;

namespace CartonPacking;

public sealed class CartonPacker
{
    private const int MaxGreedyPlacements = 1_000;
    private const int MaxBeamDepth = 160;

    public PackingResult Pack(PackingProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var context = SearchContext.Create(problem);
        if (context is null || context.Items.Length == 0)
        {
            return PackingResult.Empty;
        }

        var root = SearchState.CreateRoot(context);
        var best = root;

        for (var strategy = 0; strategy < 7; strategy++)
        {
            var greedy = CompleteGreedily(context, root, strategy);
            if (IsBetterSolution(greedy, best))
            {
                best = greedy;
            }
        }

        var estimatedCapacity = EstimateCapacity(context);
        var beamWidth = estimatedCapacity switch
        {
            <= 12 => 1_200,
            <= 25 => 600,
            <= 60 => 240,
            <= 120 => 100,
            _ => 40,
        };
        var candidateLimit = estimatedCapacity switch
        {
            <= 12 => 12,
            <= 25 => 10,
            <= 60 => 6,
            _ => 6,
        };
        var depthLimit = (int)Math.Min(estimatedCapacity, MaxBeamDepth);

        var beam = new List<SearchState> { root };
        for (var depth = 0; depth < depthLimit && beam.Count > 0; depth++)
        {
            var children = new List<SearchState>(
                Math.Min(beam.Count * context.Items.Length * candidateLimit, 200_000));

            foreach (var state in beam)
            {
                for (var itemIndex = 0; itemIndex < context.Items.Length; itemIndex++)
                {
                    var item = context.Items[itemIndex];
                    if (state.Counts[itemIndex] >= item.Carton.Quantity
                        || state.Weight + item.Carton.Weight
                            > context.Container.MaxWeight)
                    {
                        continue;
                    }

                    foreach (var candidate in FindCandidates(
                                 context,
                                 state,
                                 item,
                                 candidateLimit))
                    {
                        var child = state.Add(context, item, candidate);
                        children.Add(child);
                        if (IsBetterSolution(child, best))
                        {
                            best = child;
                        }
                    }
                }
            }

            if (children.Count == 0)
            {
                break;
            }

            children.Sort(SearchStateComparer.Instance);
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            var nextBeam = new List<SearchState>(beamWidth);
            foreach (var child in children)
            {
                if (signatures.Add(child.Signature))
                {
                    nextBeam.Add(child);
                    if (nextBeam.Count == beamWidth)
                    {
                        break;
                    }
                }
            }

            beam = nextBeam;
        }

        return ToResult(context, best);
    }

    private static SearchState CompleteGreedily(
        SearchContext context,
        SearchState root,
        int strategy)
    {
        var state = root;
        var itemOrder = Enumerable.Range(0, context.Items.Length).ToArray();
        Array.Sort(itemOrder, (left, right) =>
            CompareItems(context.Items[left], context.Items[right], strategy));

        var placementLimit = (int)Math.Min(
            EstimateCapacity(context),
            MaxGreedyPlacements);
        for (var placed = 0; placed < placementLimit; placed++)
        {
            SearchState? next = null;
            foreach (var itemIndex in itemOrder)
            {
                var item = context.Items[itemIndex];
                if (state.Counts[itemIndex] >= item.Carton.Quantity
                    || state.Weight + item.Carton.Weight
                        > context.Container.MaxWeight)
                {
                    continue;
                }

                var candidate = FindCandidates(context, state, item, 1)
                    .FirstOrDefault();
                if (candidate is not null)
                {
                    next = state.Add(context, item, candidate);
                    break;
                }
            }

            if (next is null)
            {
                break;
            }

            state = next;
        }

        return state;
    }

    private static IReadOnlyList<PlacementCandidate> FindCandidates(
        SearchContext context,
        SearchState state,
        SearchItem item,
        int limit)
    {
        var candidates = new List<PlacementCandidate>(
            item.Orientations.Length * 3);
        var seen = new HashSet<PlacementCandidateKey>();
        var targetPerOrientation = limit == 1 ? 1 : 3;

        foreach (var dimensions in item.Orientations)
        {
            var foundForOrientation = 0;
            var zLevels = GetZLevels(context.Container, state, dimensions.Height);
            foreach (var z in zLevels)
            {
                var foundAtLevel = 0;
                var checkedPositions = 0;
                var positionLimit = state.Boxes.Count <= 30 ? 12_000 : 4_000;
                var anchors = GetAnchorPositions(
                    context.Container,
                    state,
                    dimensions,
                    z);

                foreach (var (x, y) in anchors.Primary)
                {
                    checkedPositions++;
                    if (CanPlace(state, dimensions, x, y, z))
                    {
                        AddCandidate(x, y);
                        foundAtLevel++;
                        if (foundAtLevel == 2)
                        {
                            break;
                        }
                    }

                    if (checkedPositions >= positionLimit)
                    {
                        break;
                    }
                }

                if (foundAtLevel == 0 && checkedPositions < positionLimit)
                {
                    foreach (var y in anchors.Ys)
                    {
                        foreach (var x in anchors.Xs)
                        {
                            if (anchors.PrimarySet.Contains((x, y)))
                            {
                                continue;
                            }

                            checkedPositions++;
                            if (CanPlace(state, dimensions, x, y, z))
                            {
                                AddCandidate(x, y);
                                foundAtLevel++;
                                if (foundAtLevel == 2)
                                {
                                    break;
                                }
                            }

                            if (checkedPositions >= positionLimit)
                            {
                                break;
                            }
                        }

                        if (foundAtLevel == 2
                            || checkedPositions >= positionLimit)
                        {
                            break;
                        }
                    }
                }

                if (foundAtLevel > 0)
                {
                    foundForOrientation++;
                    if (foundForOrientation == targetPerOrientation)
                    {
                        break;
                    }
                }

                void AddCandidate(int x, int y)
                {
                    var key = new PlacementCandidateKey(
                        x,
                        y,
                        z,
                        dimensions.Width,
                        dimensions.Depth,
                        dimensions.Height);
                    if (!seen.Add(key))
                    {
                        return;
                    }

                    candidates.Add(new(
                        x,
                        y,
                        z,
                        dimensions,
                        GetContactArea(state, dimensions, x, y, z),
                        Math.Max(state.MaxX, x + dimensions.Width),
                        Math.Max(state.MaxY, y + dimensions.Depth),
                        Math.Max(state.MaxZ, z + dimensions.Height)));
                }
            }
        }

        candidates.Sort(PlacementCandidateComparer.Instance);
        if (candidates.Count <= limit)
        {
            return candidates;
        }

        var selected = new List<PlacementCandidate>(limit);
        if (limit > 1)
        {
            foreach (var orientationGroup in candidates
                         .GroupBy(candidate => candidate.Dimensions))
            {
                selected.Add(orientationGroup.First());
            }

            selected.Sort(PlacementCandidateComparer.Instance);
            if (selected.Count > limit)
            {
                selected.RemoveRange(limit, selected.Count - limit);
            }
        }

        if (selected.Count < limit)
        {
            var selectedKeys = selected
                .Select(candidate => new PlacementCandidateKey(
                    candidate.X,
                    candidate.Y,
                    candidate.Z,
                    candidate.Dimensions.Width,
                    candidate.Dimensions.Depth,
                    candidate.Dimensions.Height))
                .ToHashSet();
            foreach (var candidate in candidates)
            {
                var key = new PlacementCandidateKey(
                    candidate.X,
                    candidate.Y,
                    candidate.Z,
                    candidate.Dimensions.Width,
                    candidate.Dimensions.Depth,
                    candidate.Dimensions.Height);
                if (selectedKeys.Add(key))
                {
                    selected.Add(candidate);
                    if (selected.Count == limit)
                    {
                        break;
                    }
                }
            }
        }

        selected.Sort(PlacementCandidateComparer.Instance);
        return selected;
    }

    private static AnchorPositions GetAnchorPositions(
        ContainerSpec container,
        SearchState state,
        OrientedDimensions dimensions,
        int z)
    {
        var maxX = container.Width - dimensions.Width;
        var maxY = container.Depth - dimensions.Depth;
        var primary = new HashSet<(int X, int Y)>();
        var xs = new SortedSet<int>();
        var ys = new SortedSet<int>();

        AddX(0);
        AddX(maxX);
        AddY(0);
        AddY(maxY);
        AddPrimary(0, 0);
        AddPrimary(maxX, 0);
        AddPrimary(0, maxY);
        AddPrimary(maxX, maxY);

        foreach (var box in state.Boxes)
        {
            var verticallyRelevant =
                box.Z < z + dimensions.Height
                && z < box.Z + box.Height;
            var supportsAtLevel = box.Z + box.Height == z;
            if (!verticallyRelevant && !supportsAtLevel)
            {
                continue;
            }

            Span<int> boxXs =
            [
                box.X,
                box.X + box.Width,
                box.X - dimensions.Width,
                box.X + box.Width - dimensions.Width,
            ];
            Span<int> boxYs =
            [
                box.Y,
                box.Y + box.Depth,
                box.Y - dimensions.Depth,
                box.Y + box.Depth - dimensions.Depth,
            ];

            foreach (var x in boxXs)
            {
                AddX(x);
                AddPrimary(x, 0);
                AddPrimary(x, maxY);
                foreach (var y in boxYs)
                {
                    AddPrimary(x, y);
                }
            }

            foreach (var y in boxYs)
            {
                AddY(y);
                AddPrimary(0, y);
                AddPrimary(maxX, y);
            }
        }

        var orderedPrimary = primary
            .OrderBy(point => point.Y)
            .ThenBy(point => point.X)
            .ToArray();
        return new(orderedPrimary, primary, xs.ToArray(), ys.ToArray());

        void AddX(int x)
        {
            if (x >= 0 && x <= maxX)
            {
                xs.Add(x);
            }
        }

        void AddY(int y)
        {
            if (y >= 0 && y <= maxY)
            {
                ys.Add(y);
            }
        }

        void AddPrimary(int x, int y)
        {
            if (x >= 0 && x <= maxX && y >= 0 && y <= maxY)
            {
                primary.Add((x, y));
                xs.Add(x);
                ys.Add(y);
            }
        }
    }

    private static int[] GetZLevels(
        ContainerSpec container,
        SearchState state,
        int height)
    {
        var levels = new SortedSet<int> { 0 };
        foreach (var box in state.Boxes)
        {
            var top = box.Z + box.Height;
            if (top + (long)height <= container.Height)
            {
                levels.Add(top);
            }
        }

        return levels.ToArray();
    }

    private static bool CanPlace(
        SearchState state,
        OrientedDimensions dimensions,
        int x,
        int y,
        int z)
    {
        foreach (var box in state.Boxes)
        {
            if (x < box.X + box.Width
                && box.X < x + dimensions.Width
                && y < box.Y + box.Depth
                && box.Y < y + dimensions.Depth
                && z < box.Z + box.Height
                && box.Z < z + dimensions.Height)
            {
                return false;
            }
        }

        if (z == 0)
        {
            return true;
        }

        long coveredArea = 0;
        foreach (var box in state.Boxes)
        {
            if (box.Z + box.Height != z)
            {
                continue;
            }

            var overlapWidth = Math.Min(x + dimensions.Width, box.X + box.Width)
                - Math.Max(x, box.X);
            var overlapDepth = Math.Min(y + dimensions.Depth, box.Y + box.Depth)
                - Math.Max(y, box.Y);
            if (overlapWidth > 0 && overlapDepth > 0)
            {
                coveredArea += (long)overlapWidth * overlapDepth;
            }
        }

        return coveredArea == (long)dimensions.Width * dimensions.Depth;
    }

    private static long GetContactArea(
        SearchState state,
        OrientedDimensions dimensions,
        int x,
        int y,
        int z)
    {
        long contact = (long)dimensions.Width * dimensions.Depth;
        foreach (var box in state.Boxes)
        {
            var zOverlap = PositiveOverlap(
                z,
                z + dimensions.Height,
                box.Z,
                box.Z + box.Height);
            var yOverlap = PositiveOverlap(
                y,
                y + dimensions.Depth,
                box.Y,
                box.Y + box.Depth);
            if (zOverlap > 0 && yOverlap > 0
                && (x + dimensions.Width == box.X
                    || box.X + box.Width == x))
            {
                contact += (long)zOverlap * yOverlap;
            }

            var xOverlap = PositiveOverlap(
                x,
                x + dimensions.Width,
                box.X,
                box.X + box.Width);
            if (zOverlap > 0 && xOverlap > 0
                && (y + dimensions.Depth == box.Y
                    || box.Y + box.Depth == y))
            {
                contact += (long)zOverlap * xOverlap;
            }
        }

        return contact;
    }

    private static int PositiveOverlap(int start1, int end1, int start2, int end2) =>
        Math.Max(0, Math.Min(end1, end2) - Math.Max(start1, start2));

    private static int CompareItems(
        SearchItem left,
        SearchItem right,
        int strategy)
    {
        var comparison = strategy switch
        {
            0 => CompareDensity(
                left.Carton.Value,
                left.Carton.Weight,
                right.Carton.Value,
                right.Carton.Weight),
            1 => right.Carton.Value.CompareTo(left.Carton.Value),
            2 => CompareDensity(
                left.Carton.Value,
                left.Volume,
                right.Carton.Value,
                right.Volume),
            3 => left.Volume.CompareTo(right.Volume),
            4 => right.Volume.CompareTo(left.Volume),
            5 => CompareDensity(
                left.Carton.Value,
                Math.Max(1L, left.Carton.Weight * left.Volume),
                right.Carton.Value,
                Math.Max(1L, right.Carton.Weight * right.Volume)),
            _ => right.MaxBaseArea.CompareTo(left.MaxBaseArea),
        };

        if (comparison != 0)
        {
            return comparison;
        }

        comparison = right.Carton.Value.CompareTo(left.Carton.Value);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = right.Volume.CompareTo(left.Volume);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Carton.Id, right.Carton.Id);
    }

    private static int CompareDensity(
        long leftValue,
        long leftCost,
        long rightValue,
        long rightCost)
    {
        if (leftCost == 0 || rightCost == 0)
        {
            if (leftCost == rightCost)
            {
                return rightValue.CompareTo(leftValue);
            }

            return leftCost == 0 ? -1 : 1;
        }

        var leftRatio = (decimal)leftValue / leftCost;
        var rightRatio = (decimal)rightValue / rightCost;
        return rightRatio.CompareTo(leftRatio);
    }

    private static long EstimateCapacity(SearchContext context)
    {
        long quantity = 0;
        var minimumVolume = long.MaxValue;
        foreach (var item in context.Items)
        {
            quantity = SaturatingAdd(quantity, item.Carton.Quantity);
            minimumVolume = Math.Min(minimumVolume, item.Volume);
        }

        return Math.Min(quantity, context.ContainerVolume / minimumVolume);
    }

    private static bool IsBetterSolution(
        SearchState candidate,
        SearchState current)
    {
        if (candidate.Value != current.Value)
        {
            return candidate.Value > current.Value;
        }

        if (candidate.Volume != current.Volume)
        {
            return candidate.Volume > current.Volume;
        }

        if (candidate.MaxZ != current.MaxZ)
        {
            return candidate.MaxZ < current.MaxZ;
        }

        var candidateBounds = (long)candidate.MaxX
            * candidate.MaxY
            * candidate.MaxZ;
        var currentBounds = (long)current.MaxX
            * current.MaxY
            * current.MaxZ;
        if (candidateBounds != currentBounds)
        {
            return candidateBounds < currentBounds;
        }

        return StringComparer.Ordinal.Compare(
            candidate.Signature,
            current.Signature) < 0;
    }

    private static PackingResult ToResult(
        SearchContext context,
        SearchState state)
    {
        var placements = new List<Placement>(state.Boxes.Count);
        foreach (var item in context.Items)
        {
            var boxes = state.Boxes
                .Where(box => box.ItemIndex == item.Index)
                .OrderBy(box => box.X)
                .ThenBy(box => box.Y)
                .ThenBy(box => box.Z)
                .ThenBy(box => box.Width)
                .ThenBy(box => box.Depth)
                .ThenBy(box => box.Height)
                .ToArray();
            for (var instance = 0; instance < boxes.Length; instance++)
            {
                var box = boxes[instance];
                placements.Add(new(
                    item.Carton.Id,
                    instance,
                    box.X,
                    box.Y,
                    box.Z,
                    box.Width,
                    box.Depth,
                    box.Height));
            }
        }

        placements.Sort(PlacementComparer.Instance);
        return new(placements);
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private sealed class SearchContext
    {
        private SearchContext(
            ContainerSpec container,
            SearchItem[] items,
            long containerVolume,
            int[] valuePerWeightOrder,
            int[] valuePerVolumeOrder)
        {
            Container = container;
            Items = items;
            ContainerVolume = containerVolume;
            ValuePerWeightOrder = valuePerWeightOrder;
            ValuePerVolumeOrder = valuePerVolumeOrder;
        }

        public ContainerSpec Container { get; }

        public SearchItem[] Items { get; }

        public long ContainerVolume { get; }

        public int[] ValuePerWeightOrder { get; }

        public int[] ValuePerVolumeOrder { get; }

        public static SearchContext? Create(PackingProblem problem)
        {
            var container = problem.Container;
            if (container is null
                || problem.Cartons is null
                || container.Width <= 0
                || container.Depth <= 0
                || container.Height <= 0
                || container.MaxWeight < 0)
            {
                return null;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var carton in problem.Cartons)
            {
                if (carton is null
                    || string.IsNullOrWhiteSpace(carton.Id)
                    || !ids.Add(carton.Id)
                    || carton.Width <= 0
                    || carton.Depth <= 0
                    || carton.Height <= 0
                    || carton.Quantity < 0
                    || carton.Weight < 0
                    || carton.Value < 0)
                {
                    return null;
                }
            }

            var items = new List<SearchItem>();
            foreach (var carton in problem.Cartons.OrderBy(
                         carton => carton.Id,
                         StringComparer.Ordinal))
            {
                if (carton.Quantity == 0 || carton.Weight > container.MaxWeight)
                {
                    continue;
                }

                var orientations = OrientationGenerator.GetOrientations(carton)
                    .Where(dimensions =>
                        dimensions.Width <= container.Width
                        && dimensions.Depth <= container.Depth
                        && dimensions.Height <= container.Height)
                    .OrderBy(dimensions => dimensions.Height)
                    .ThenByDescending(dimensions =>
                        (long)dimensions.Width * dimensions.Depth)
                    .ThenBy(dimensions => dimensions.Width)
                    .ThenBy(dimensions => dimensions.Depth)
                    .ToArray();
                if (orientations.Length == 0)
                {
                    continue;
                }

                var index = items.Count;
                var volume = orientations[0].Volume;
                items.Add(new(
                    index,
                    carton,
                    orientations,
                    volume,
                    orientations.Max(dimensions =>
                        (long)dimensions.Width * dimensions.Depth)));
            }

            var itemArray = items.ToArray();
            var weightOrder = Enumerable.Range(0, itemArray.Length).ToArray();
            Array.Sort(weightOrder, (left, right) =>
                CompareDensity(
                    itemArray[left].Carton.Value,
                    itemArray[left].Carton.Weight,
                    itemArray[right].Carton.Value,
                    itemArray[right].Carton.Weight));
            var volumeOrder = Enumerable.Range(0, itemArray.Length).ToArray();
            Array.Sort(volumeOrder, (left, right) =>
                CompareDensity(
                    itemArray[left].Carton.Value,
                    itemArray[left].Volume,
                    itemArray[right].Carton.Value,
                    itemArray[right].Volume));

            var containerVolume = (long)container.Width
                * container.Depth
                * container.Height;
            return new(
                container,
                itemArray,
                containerVolume,
                weightOrder,
                volumeOrder);
        }
    }

    private sealed record SearchItem(
        int Index,
        CartonType Carton,
        OrientedDimensions[] Orientations,
        long Volume,
        long MaxBaseArea);

    private sealed class SearchState
    {
        private string? _signature;

        private SearchState(
            List<PlacedBox> boxes,
            int[] counts,
            long weight,
            long value,
            long volume,
            int maxX,
            int maxY,
            int maxZ,
            long contactArea,
            decimal upperValue,
            long upperVolume)
        {
            Boxes = boxes;
            Counts = counts;
            Weight = weight;
            Value = value;
            Volume = volume;
            MaxX = maxX;
            MaxY = maxY;
            MaxZ = maxZ;
            ContactArea = contactArea;
            UpperValue = upperValue;
            UpperVolume = upperVolume;
        }

        public List<PlacedBox> Boxes { get; }

        public int[] Counts { get; }

        public long Weight { get; }

        public long Value { get; }

        public long Volume { get; }

        public int MaxX { get; }

        public int MaxY { get; }

        public int MaxZ { get; }

        public long ContactArea { get; }

        public decimal UpperValue { get; }

        public long UpperVolume { get; }

        public string Signature => _signature ??= BuildSignature();

        public static SearchState CreateRoot(SearchContext context)
        {
            var counts = new int[context.Items.Length];
            var bounds = ComputeBounds(context, counts, 0, 0, 0);
            return new(
                [],
                counts,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                bounds.Value,
                bounds.Volume);
        }

        public SearchState Add(
            SearchContext context,
            SearchItem item,
            PlacementCandidate candidate)
        {
            var boxes = new List<PlacedBox>(Boxes.Count + 1);
            boxes.AddRange(Boxes);
            boxes.Add(new(
                item.Index,
                candidate.X,
                candidate.Y,
                candidate.Z,
                candidate.Dimensions.Width,
                candidate.Dimensions.Depth,
                candidate.Dimensions.Height));

            var counts = (int[])Counts.Clone();
            counts[item.Index]++;
            var weight = Weight + item.Carton.Weight;
            var value = Value + item.Carton.Value;
            var volume = Volume + item.Volume;
            var bounds = ComputeBounds(context, counts, weight, value, volume);
            return new(
                boxes,
                counts,
                weight,
                value,
                volume,
                candidate.ResultingMaxX,
                candidate.ResultingMaxY,
                candidate.ResultingMaxZ,
                ContactArea + candidate.ContactArea,
                bounds.Value,
                bounds.Volume);
        }

        private static (decimal Value, long Volume) ComputeBounds(
            SearchContext context,
            int[] counts,
            long weight,
            long value,
            long volume)
        {
            var weightCapacity = context.Container.MaxWeight - weight;
            var volumeCapacity = context.ContainerVolume - volume;
            var weightBound = FractionalValueBound(
                context,
                counts,
                value,
                weightCapacity,
                context.ValuePerWeightOrder,
                useWeight: true);
            var volumeBound = FractionalValueBound(
                context,
                counts,
                value,
                volumeCapacity,
                context.ValuePerVolumeOrder,
                useWeight: false);

            long remainingVolume = 0;
            foreach (var item in context.Items)
            {
                var remaining = item.Carton.Quantity - counts[item.Index];
                if (remaining <= 0)
                {
                    continue;
                }

                var addition = remaining > long.MaxValue / item.Volume
                    ? long.MaxValue
                    : remaining * item.Volume;
                remainingVolume = SaturatingAdd(remainingVolume, addition);
            }

            return (
                Math.Min(weightBound, volumeBound),
                SaturatingAdd(volume, Math.Min(volumeCapacity, remainingVolume)));
        }

        private static decimal FractionalValueBound(
            SearchContext context,
            int[] counts,
            long currentValue,
            long capacity,
            int[] order,
            bool useWeight)
        {
            decimal result = currentValue;
            foreach (var itemIndex in order)
            {
                var item = context.Items[itemIndex];
                var remaining = item.Carton.Quantity - counts[itemIndex];
                if (remaining <= 0)
                {
                    continue;
                }

                var cost = useWeight ? item.Carton.Weight : item.Volume;
                if (cost == 0)
                {
                    result += (decimal)remaining * item.Carton.Value;
                    continue;
                }

                var wholeUnits = Math.Min((long)remaining, capacity / cost);
                result += (decimal)wholeUnits * item.Carton.Value;
                capacity -= wholeUnits * cost;
                remaining -= (int)wholeUnits;
                if (remaining > 0 && capacity > 0)
                {
                    result += (decimal)capacity * item.Carton.Value / cost;
                    break;
                }
            }

            return result;
        }

        private string BuildSignature()
        {
            var builder = new StringBuilder(Counts.Length * 4 + Boxes.Count * 28);
            foreach (var count in Counts)
            {
                builder.Append(count).Append(',');
            }

            builder.Append('|');
            foreach (var box in Boxes
                         .OrderBy(box => box.ItemIndex)
                         .ThenBy(box => box.X)
                         .ThenBy(box => box.Y)
                         .ThenBy(box => box.Z)
                         .ThenBy(box => box.Width)
                         .ThenBy(box => box.Depth)
                         .ThenBy(box => box.Height))
            {
                builder.Append(box.ItemIndex).Append(':')
                    .Append(box.X).Append(',')
                    .Append(box.Y).Append(',')
                    .Append(box.Z).Append(',')
                    .Append(box.Width).Append(',')
                    .Append(box.Depth).Append(',')
                    .Append(box.Height).Append(';');
            }

            return builder.ToString();
        }
    }

    private sealed class SearchStateComparer : IComparer<SearchState>
    {
        public static SearchStateComparer Instance { get; } = new();

        public int Compare(SearchState? left, SearchState? right)
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

            var comparison = right.UpperValue.CompareTo(left.UpperValue);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = right.UpperVolume.CompareTo(left.UpperVolume);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = right.Value.CompareTo(left.Value);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = right.Volume.CompareTo(left.Volume);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.MaxZ.CompareTo(right.MaxZ);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = right.ContactArea.CompareTo(left.ContactArea);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.Signature, right.Signature);
        }
    }

    private sealed record AnchorPositions(
        (int X, int Y)[] Primary,
        HashSet<(int X, int Y)> PrimarySet,
        int[] Xs,
        int[] Ys);

    private sealed record PlacementCandidate(
        int X,
        int Y,
        int Z,
        OrientedDimensions Dimensions,
        long ContactArea,
        int ResultingMaxX,
        int ResultingMaxY,
        int ResultingMaxZ);

    private readonly record struct PlacementCandidateKey(
        int X,
        int Y,
        int Z,
        int Width,
        int Depth,
        int Height);

    private sealed class PlacementCandidateComparer
        : IComparer<PlacementCandidate>
    {
        public static PlacementCandidateComparer Instance { get; } = new();

        public int Compare(PlacementCandidate? left, PlacementCandidate? right)
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

            var comparison = left.ResultingMaxZ.CompareTo(right.ResultingMaxZ);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Z.CompareTo(right.Z);
            if (comparison != 0)
            {
                return comparison;
            }

            var leftBounds = (long)left.ResultingMaxX
                * left.ResultingMaxY
                * left.ResultingMaxZ;
            var rightBounds = (long)right.ResultingMaxX
                * right.ResultingMaxY
                * right.ResultingMaxZ;
            comparison = leftBounds.CompareTo(rightBounds);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = right.ContactArea.CompareTo(left.ContactArea);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Y.CompareTo(right.Y);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.X.CompareTo(right.X);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Dimensions.Height.CompareTo(right.Dimensions.Height);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Dimensions.Depth.CompareTo(right.Dimensions.Depth);
            return comparison != 0
                ? comparison
                : left.Dimensions.Width.CompareTo(right.Dimensions.Width);
        }
    }

    private sealed class PlacementComparer : IComparer<Placement>
    {
        public static PlacementComparer Instance { get; } = new();

        public int Compare(Placement? left, Placement? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var comparison = StringComparer.Ordinal.Compare(
                left.CartonId,
                right.CartonId);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Instance.CompareTo(right.Instance);
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
            return comparison != 0
                ? comparison
                : left.Z.CompareTo(right.Z);
        }
    }

    private readonly record struct PlacedBox(
        int ItemIndex,
        int X,
        int Y,
        int Z,
        int Width,
        int Depth,
        int Height);
}
