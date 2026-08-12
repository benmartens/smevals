namespace CartonPacking;

public sealed class CartonPacker
{
    private const int MaximumGridCells = 16_384;
    private const int ExactSearchGridCells = 225;
    private const int ExactSearchItemLimit = 14;
    private const int ExactSearchNodeLimit = 200_000;
    private const int ExactSearchStateLimit = 75_000;
    private const int KnapsackCapacityLimit = 20_000;
    private const int KnapsackItemLimit = 256;

    private static readonly PackingRanking[] Rankings =
    [
        PackingRanking.Value,
        PackingRanking.ValuePerWeight,
        PackingRanking.ValuePerVolume,
        PackingRanking.ValueThenLight,
        PackingRanking.Volume,
    ];

    private static readonly PlacementStyle[] PlacementStyles =
    [
        PlacementStyle.BottomLeft,
        PlacementStyle.LowTop,
        PlacementStyle.WideBase,
        PlacementStyle.EdgeAligned,
    ];

    public PackingResult Pack(PackingProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var data = CreateProblemData(problem);
        if (data is null || data.Types.Count == 0)
        {
            return PackingResult.Empty;
        }

        LayoutResult best = LayoutResult.Empty;
        var canUseGrid = data.FootprintArea <= MaximumGridCells;

        if (canUseGrid
            && data.FootprintArea <= ExactSearchGridCells
            && data.TotalMaximumCount <= ExactSearchItemLimit)
        {
            Consider(ref best, new ExactSearch(data).Solve());
        }

        var knapsackTarget = BuildKnapsackTarget(data);
        foreach (var style in PlacementStyles)
        {
            foreach (var ranking in Rankings)
            {
                var candidate = canUseGrid
                    ? PackWithGrid(data, ranking, style, requestedCounts: null)
                    : PackWithFreeSpaces(data, ranking, style, requestedCounts: null);
                Consider(ref best, candidate);
            }

            if (knapsackTarget is not null)
            {
                Consider(
                    ref best,
                    canUseGrid
                        ? PackWithGrid(
                            data,
                            PackingRanking.Value,
                            style,
                            knapsackTarget)
                        : PackWithFreeSpaces(
                            data,
                            PackingRanking.Value,
                            style,
                            knapsackTarget));

                Consider(
                    ref best,
                    canUseGrid
                        ? PackWithGrid(
                            data,
                            PackingRanking.ValuePerWeight,
                            style,
                            knapsackTarget)
                        : PackWithFreeSpaces(
                            data,
                            PackingRanking.ValuePerWeight,
                            style,
                            knapsackTarget));
            }
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

    private static ProblemData? CreateProblemData(PackingProblem problem)
    {
        if (problem.Container is null
            || problem.Cartons is null
            || problem.Container.Width <= 0
            || problem.Container.Depth <= 0
            || problem.Container.Height <= 0
            || problem.Container.MaxWeight < 0)
        {
            return null;
        }

        var container = problem.Container;
        var containerVolume = Volume(container.Width, container.Depth, container.Height);
        var cartonIds = new HashSet<string>(StringComparer.Ordinal);
        var types = new List<TypeInfo>();

        foreach (var carton in problem.Cartons)
        {
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
                return null;
            }

            if (carton.Quantity == 0)
            {
                continue;
            }

            var orientations = OrientationGenerator.GetOrientations(carton)
                .Where(orientation =>
                    orientation.Width <= container.Width
                    && orientation.Depth <= container.Depth
                    && orientation.Height <= container.Height)
                .ToArray();
            if (orientations.Length == 0)
            {
                continue;
            }

            var cartonVolume = Volume(carton.Width, carton.Depth, carton.Height);
            var maximumCount = Math.Min((long)carton.Quantity, containerVolume / cartonVolume);
            if (carton.Weight > 0)
            {
                maximumCount = Math.Min(
                    maximumCount,
                    container.MaxWeight / carton.Weight);
            }

            if (maximumCount > 0)
            {
                types.Add(new TypeInfo(
                    carton,
                    orientations,
                    (int)maximumCount,
                    cartonVolume));
            }
        }

        types.Sort((left, right) =>
            StringComparer.Ordinal.Compare(left.Carton.Id, right.Carton.Id));

        var totalMaximumCount = 0;
        foreach (var type in types)
        {
            if (type.MaximumCount > ExactSearchItemLimit - totalMaximumCount)
            {
                totalMaximumCount = ExactSearchItemLimit + 1;
                break;
            }

            totalMaximumCount += type.MaximumCount;
        }

        return new ProblemData(
            container,
            types,
            (long)container.Width * container.Depth,
            totalMaximumCount);
    }

    private static LayoutResult PackWithGrid(
        ProblemData data,
        PackingRanking ranking,
        PlacementStyle style,
        int[]? requestedCounts)
    {
        var layout = new HeightMap(data.Container);
        var remaining = requestedCounts?.ToArray()
            ?? data.Types.Select(type => type.MaximumCount).ToArray();
        var placedCounts = new int[data.Types.Count];
        var placements = new List<Placement>();
        long totalWeight = 0;
        long totalValue = 0;
        long totalVolume = 0;

        while (true)
        {
            var chosenType = -1;
            var hasCandidate = false;
            var chosenCandidate = default(GridCandidate);

            for (var typeIndex = 0; typeIndex < data.Types.Count; typeIndex++)
            {
                var type = data.Types[typeIndex];
                if (remaining[typeIndex] == 0
                    || !CanAddWeight(totalWeight, type.Carton.Weight, data.Container.MaxWeight)
                    || !layout.TryFindBest(type, style, out var candidate))
                {
                    continue;
                }

                if (!hasCandidate
                    || CompareTypePreference(
                        type,
                        data.Types[chosenType],
                        ranking) > 0
                    || (CompareTypePreference(
                            type,
                            data.Types[chosenType],
                            ranking) == 0
                        && CompareGridCandidates(
                            candidate,
                            chosenCandidate,
                            style,
                            data.Container) < 0))
                {
                    chosenType = typeIndex;
                    chosenCandidate = candidate;
                    hasCandidate = true;
                }
            }

            if (!hasCandidate)
            {
                break;
            }

            var selected = data.Types[chosenType];
            layout.Place(chosenCandidate);
            placements.Add(new Placement(
                selected.Carton.Id,
                placedCounts[chosenType],
                chosenCandidate.X,
                chosenCandidate.Y,
                chosenCandidate.Z,
                chosenCandidate.Dimensions.Width,
                chosenCandidate.Dimensions.Depth,
                chosenCandidate.Dimensions.Height));
            remaining[chosenType]--;
            placedCounts[chosenType]++;
            totalWeight += selected.Carton.Weight;
            totalValue = SaturatingAdd(totalValue, selected.Carton.Value);
            totalVolume = SaturatingAdd(totalVolume, selected.Volume);
        }

        return new LayoutResult(placements, totalWeight, totalValue, totalVolume);
    }

    private static LayoutResult PackWithFreeSpaces(
        ProblemData data,
        PackingRanking ranking,
        PlacementStyle style,
        int[]? requestedCounts)
    {
        var layout = new FreeSpaceLayout(data.Container);
        var remaining = requestedCounts?.ToArray()
            ?? data.Types.Select(type => type.MaximumCount).ToArray();
        var placedCounts = new int[data.Types.Count];
        var placements = new List<Placement>();
        long totalWeight = 0;
        long totalValue = 0;
        long totalVolume = 0;

        while (true)
        {
            var chosenType = -1;
            var hasCandidate = false;
            var chosenCandidate = default(FreeSpaceCandidate);

            for (var typeIndex = 0; typeIndex < data.Types.Count; typeIndex++)
            {
                var type = data.Types[typeIndex];
                if (remaining[typeIndex] == 0
                    || !CanAddWeight(totalWeight, type.Carton.Weight, data.Container.MaxWeight)
                    || !layout.TryFindBest(type, style, out var candidate))
                {
                    continue;
                }

                if (!hasCandidate
                    || CompareTypePreference(
                        type,
                        data.Types[chosenType],
                        ranking) > 0
                    || (CompareTypePreference(
                            type,
                            data.Types[chosenType],
                            ranking) == 0
                        && CompareFreeSpaceCandidates(
                            candidate,
                            chosenCandidate,
                            style,
                            data.Container) < 0))
                {
                    chosenType = typeIndex;
                    chosenCandidate = candidate;
                    hasCandidate = true;
                }
            }

            if (!hasCandidate)
            {
                break;
            }

            var selected = data.Types[chosenType];
            var space = layout.Place(chosenCandidate);
            placements.Add(new Placement(
                selected.Carton.Id,
                placedCounts[chosenType],
                space.X,
                space.Y,
                space.Z,
                chosenCandidate.Dimensions.Width,
                chosenCandidate.Dimensions.Depth,
                chosenCandidate.Dimensions.Height));
            remaining[chosenType]--;
            placedCounts[chosenType]++;
            totalWeight += selected.Carton.Weight;
            totalValue = SaturatingAdd(totalValue, selected.Carton.Value);
            totalVolume = SaturatingAdd(totalVolume, selected.Volume);
        }

        return new LayoutResult(placements, totalWeight, totalValue, totalVolume);
    }

    private static int[]? BuildKnapsackTarget(ProblemData data)
    {
        if (data.Container.MaxWeight > KnapsackCapacityLimit)
        {
            return null;
        }

        var itemTypes = new List<int>();
        for (var typeIndex = 0; typeIndex < data.Types.Count; typeIndex++)
        {
            for (var count = 0;
                 count < data.Types[typeIndex].MaximumCount;
                 count++)
            {
                itemTypes.Add(typeIndex);
                if (itemTypes.Count > KnapsackItemLimit)
                {
                    return null;
                }
            }
        }

        var states = new KnapsackNode?[data.Container.MaxWeight + 1];
        states[0] = KnapsackNode.Root;

        foreach (var typeIndex in itemTypes)
        {
            var type = data.Types[typeIndex];
            var carton = type.Carton;
            if (carton.Weight == 0)
            {
                for (var weight = 0; weight < states.Length; weight++)
                {
                    var previous = states[weight];
                    if (previous is null)
                    {
                        continue;
                    }

                    var value = SaturatingAdd(previous.Value, carton.Value);
                    var volume = SaturatingAdd(previous.Volume, type.Volume);
                    if (IsBetterScore(value, volume, previous.Value, previous.Volume))
                    {
                        states[weight] = new KnapsackNode(
                            previous,
                            typeIndex,
                            value,
                            volume);
                    }
                }

                continue;
            }

            for (var weight = states.Length - 1;
                 weight >= carton.Weight;
                 weight--)
            {
                var previous = states[weight - carton.Weight];
                if (previous is null)
                {
                    continue;
                }

                var value = SaturatingAdd(previous.Value, carton.Value);
                var volume = SaturatingAdd(previous.Volume, type.Volume);
                var current = states[weight];
                if (current is null
                    || IsBetterScore(value, volume, current.Value, current.Volume))
                {
                    states[weight] = new KnapsackNode(
                        previous,
                        typeIndex,
                        value,
                        volume);
                }
            }
        }

        var best = KnapsackNode.Root;
        foreach (var state in states)
        {
            if (state is not null
                && IsBetterScore(state.Value, state.Volume, best.Value, best.Volume))
            {
                best = state;
            }
        }

        var requestedCounts = new int[data.Types.Count];
        for (var node = best; node.Previous is not null; node = node.Previous)
        {
            requestedCounts[node.TypeIndex]++;
        }

        return requestedCounts;
    }

    private static void Consider(ref LayoutResult best, LayoutResult candidate)
    {
        if (IsBetterScore(
                candidate.TotalValue,
                candidate.TotalVolume,
                best.TotalValue,
                best.TotalVolume))
        {
            best = candidate;
        }
    }

    private static bool IsBetterScore(
        long candidateValue,
        long candidateVolume,
        long currentValue,
        long currentVolume) =>
        candidateValue > currentValue
        || (candidateValue == currentValue && candidateVolume > currentVolume);

    private static bool CanAddWeight(long currentWeight, int cartonWeight, int maximumWeight) =>
        currentWeight <= maximumWeight - (long)cartonWeight;

    private static int CompareTypePreference(
        TypeInfo left,
        TypeInfo right,
        PackingRanking ranking)
    {
        var comparison = ranking switch
        {
            PackingRanking.Value => left.Carton.Value.CompareTo(right.Carton.Value),
            PackingRanking.ValuePerWeight => CompareValuePerWeight(left, right),
            PackingRanking.ValuePerVolume => CompareRatio(
                left.Carton.Value,
                left.Volume,
                right.Carton.Value,
                right.Volume),
            PackingRanking.ValueThenLight =>
                left.Carton.Value != right.Carton.Value
                    ? left.Carton.Value.CompareTo(right.Carton.Value)
                    : right.Carton.Weight.CompareTo(left.Carton.Weight),
            PackingRanking.Volume => left.Volume.CompareTo(right.Volume),
            _ => 0,
        };

        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Carton.Value.CompareTo(right.Carton.Value);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Volume.CompareTo(right.Volume);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = right.Carton.Weight.CompareTo(left.Carton.Weight);
        if (comparison != 0)
        {
            return comparison;
        }

        return -StringComparer.Ordinal.Compare(left.Carton.Id, right.Carton.Id);
    }

    private static int CompareValuePerWeight(TypeInfo left, TypeInfo right)
    {
        if (left.Carton.Weight == 0 || right.Carton.Weight == 0)
        {
            if (left.Carton.Weight == 0 && right.Carton.Weight == 0)
            {
                return left.Carton.Value.CompareTo(right.Carton.Value);
            }

            if (left.Carton.Weight == 0)
            {
                if (left.Carton.Value != 0 || right.Carton.Value == 0)
                {
                    return left.Carton.Value > 0 ? 1 : 0;
                }

                return -1;
            }

            if (right.Carton.Value != 0 || left.Carton.Value == 0)
            {
                return right.Carton.Value > 0 ? -1 : 0;
            }

            return 1;
        }

        return CompareRatio(
            left.Carton.Value,
            left.Carton.Weight,
            right.Carton.Value,
            right.Carton.Weight);
    }

    private static int CompareRatio(
        long leftNumerator,
        long leftDenominator,
        long rightNumerator,
        long rightDenominator) =>
        ((decimal)leftNumerator * rightDenominator).CompareTo(
            (decimal)rightNumerator * leftDenominator);

    private static int CompareGridCandidates(
        GridCandidate left,
        GridCandidate right,
        PlacementStyle style,
        ContainerSpec container)
    {
        var comparison = style switch
        {
            PlacementStyle.BottomLeft => CompareCoordinates(left, right),
            PlacementStyle.LowTop => CompareLowTop(left, right),
            PlacementStyle.WideBase => CompareWideBase(left, right),
            PlacementStyle.EdgeAligned => CompareEdgeAligned(left, right, container),
            _ => 0,
        };

        if (comparison != 0)
        {
            return comparison;
        }

        return CompareDimensions(left.Dimensions, right.Dimensions);
    }

    private static int CompareFreeSpaceCandidates(
        FreeSpaceCandidate left,
        FreeSpaceCandidate right,
        PlacementStyle style,
        ContainerSpec container) =>
        CompareGridCandidates(
            new GridCandidate(left.Space.X, left.Space.Y, left.Space.Z, left.Dimensions),
            new GridCandidate(right.Space.X, right.Space.Y, right.Space.Z, right.Dimensions),
            style,
            container);

    private static int CompareCoordinates(GridCandidate left, GridCandidate right)
    {
        var comparison = left.Z.CompareTo(right.Z);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Y.CompareTo(right.Y);
        return comparison != 0 ? comparison : left.X.CompareTo(right.X);
    }

    private static int CompareLowTop(GridCandidate left, GridCandidate right)
    {
        var comparison = (left.Z + left.Dimensions.Height)
            .CompareTo(right.Z + right.Dimensions.Height);
        return comparison != 0 ? comparison : CompareCoordinates(left, right);
    }

    private static int CompareWideBase(GridCandidate left, GridCandidate right)
    {
        var comparison = left.Z.CompareTo(right.Z);
        if (comparison != 0)
        {
            return comparison;
        }

        var leftArea = (long)left.Dimensions.Width * left.Dimensions.Depth;
        var rightArea = (long)right.Dimensions.Width * right.Dimensions.Depth;
        comparison = rightArea.CompareTo(leftArea);
        return comparison != 0 ? comparison : CompareCoordinates(left, right);
    }

    private static int CompareEdgeAligned(
        GridCandidate left,
        GridCandidate right,
        ContainerSpec container)
    {
        var comparison = left.Z.CompareTo(right.Z);
        if (comparison != 0)
        {
            return comparison;
        }

        var leftSlack = Math.Min(left.X, container.Width - left.X - left.Dimensions.Width)
            + Math.Min(left.Y, container.Depth - left.Y - left.Dimensions.Depth);
        var rightSlack = Math.Min(right.X, container.Width - right.X - right.Dimensions.Width)
            + Math.Min(right.Y, container.Depth - right.Y - right.Dimensions.Depth);
        comparison = leftSlack.CompareTo(rightSlack);
        return comparison != 0 ? comparison : CompareCoordinates(left, right);
    }

    private static int CompareDimensions(
        OrientedDimensions left,
        OrientedDimensions right)
    {
        var comparison = left.Height.CompareTo(right.Height);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Depth.CompareTo(right.Depth);
        return comparison != 0 ? comparison : left.Width.CompareTo(right.Width);
    }

    private static long Volume(int width, int depth, int height) =>
        SaturatingMultiply(SaturatingMultiply(width, depth), height);

    private static long SaturatingMultiply(long left, long right)
    {
        if (left == 0 || right == 0)
        {
            return 0;
        }

        return left > long.MaxValue / right ? long.MaxValue : left * right;
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private sealed class ExactSearch
    {
        private readonly ProblemData _data;
        private readonly HeightMap _layout;
        private readonly int[] _remaining;
        private readonly int[] _placedCounts;
        private readonly int[] _typeOrder;
        private readonly List<Placement> _placements = [];
        private readonly HashSet<string> _seenStates = new(StringComparer.Ordinal);
        private LayoutResult _best = LayoutResult.Empty;
        private int _nodesVisited;

        public ExactSearch(ProblemData data)
        {
            _data = data;
            _layout = new HeightMap(data.Container);
            _remaining = data.Types.Select(type => type.MaximumCount).ToArray();
            _placedCounts = new int[data.Types.Count];
            _typeOrder = Enumerable.Range(0, data.Types.Count)
                .OrderByDescending(index => data.Types[index].Carton.Value)
                .ThenByDescending(index => data.Types[index].Volume)
                .ThenBy(index => data.Types[index].Carton.Weight)
                .ThenBy(index => data.Types[index].Carton.Id, StringComparer.Ordinal)
                .ToArray();
        }

        public LayoutResult Solve()
        {
            Search(totalWeight: 0, totalValue: 0, totalVolume: 0);
            return _best;
        }

        private void Search(long totalWeight, long totalValue, long totalVolume)
        {
            if (_nodesVisited++ >= ExactSearchNodeLimit)
            {
                return;
            }

            if (IsBetterScore(
                    totalValue,
                    totalVolume,
                    _best.TotalValue,
                    _best.TotalVolume))
            {
                _best = new LayoutResult(
                    [.. _placements],
                    totalWeight,
                    totalValue,
                    totalVolume);
            }

            if (CannotImprove(totalValue, totalVolume))
            {
                return;
            }

            if (_seenStates.Count < ExactSearchStateLimit
                && !_seenStates.Add(_layout.CreateStateKey(_remaining)))
            {
                return;
            }

            foreach (var typeIndex in _typeOrder)
            {
                var type = _data.Types[typeIndex];
                if (_remaining[typeIndex] == 0
                    || !CanAddWeight(
                        totalWeight,
                        type.Carton.Weight,
                        _data.Container.MaxWeight))
                {
                    continue;
                }

                var candidates = _layout.GetCandidates(type);
                candidates.Sort((left, right) => CompareGridCandidates(
                    left,
                    right,
                    PlacementStyle.WideBase,
                    _data.Container));

                foreach (var candidate in candidates)
                {
                    _layout.Place(candidate);
                    _remaining[typeIndex]--;
                    var instance = _placedCounts[typeIndex]++;
                    _placements.Add(new Placement(
                        type.Carton.Id,
                        instance,
                        candidate.X,
                        candidate.Y,
                        candidate.Z,
                        candidate.Dimensions.Width,
                        candidate.Dimensions.Depth,
                        candidate.Dimensions.Height));

                    Search(
                        totalWeight + type.Carton.Weight,
                        SaturatingAdd(totalValue, type.Carton.Value),
                        SaturatingAdd(totalVolume, type.Volume));

                    _placements.RemoveAt(_placements.Count - 1);
                    _placedCounts[typeIndex]--;
                    _remaining[typeIndex]++;
                    _layout.Undo(candidate);
                }
            }
        }

        private bool CannotImprove(long totalValue, long totalVolume)
        {
            var upperValue = totalValue;
            var upperVolume = totalVolume;
            for (var typeIndex = 0; typeIndex < _data.Types.Count; typeIndex++)
            {
                var type = _data.Types[typeIndex];
                upperValue = SaturatingAdd(
                    upperValue,
                    SaturatingMultiply(
                        _remaining[typeIndex],
                        type.Carton.Value));
                upperVolume = SaturatingAdd(
                    upperVolume,
                    SaturatingMultiply(_remaining[typeIndex], type.Volume));
            }

            return !IsBetterScore(
                upperValue,
                upperVolume,
                _best.TotalValue,
                _best.TotalVolume);
        }
    }

    private sealed class HeightMap
    {
        private readonly ContainerSpec _container;
        private readonly int[] _tops;

        public HeightMap(ContainerSpec container)
        {
            _container = container;
            _tops = new int[checked(container.Width * container.Depth)];
        }

        public bool TryFindBest(
            TypeInfo type,
            PlacementStyle style,
            out GridCandidate candidate)
        {
            candidate = default;
            var found = false;

            foreach (var dimensions in type.Orientations)
            {
                for (var y = 0; y <= _container.Depth - dimensions.Depth; y++)
                {
                    for (var x = 0; x <= _container.Width - dimensions.Width; x++)
                    {
                        if (!TryGetBaseHeight(x, y, dimensions, out var z))
                        {
                            continue;
                        }

                        var current = new GridCandidate(x, y, z, dimensions);
                        if (!found
                            || CompareGridCandidates(
                                current,
                                candidate,
                                style,
                                _container) < 0)
                        {
                            candidate = current;
                            found = true;
                        }
                    }
                }
            }

            return found;
        }

        public List<GridCandidate> GetCandidates(TypeInfo type)
        {
            var candidates = new List<GridCandidate>();
            foreach (var dimensions in type.Orientations)
            {
                for (var y = 0; y <= _container.Depth - dimensions.Depth; y++)
                {
                    for (var x = 0; x <= _container.Width - dimensions.Width; x++)
                    {
                        if (TryGetBaseHeight(x, y, dimensions, out var z))
                        {
                            candidates.Add(new GridCandidate(x, y, z, dimensions));
                        }
                    }
                }
            }

            return candidates;
        }

        public void Place(GridCandidate candidate) =>
            SetHeight(
                candidate.X,
                candidate.Y,
                candidate.Dimensions,
                candidate.Z + candidate.Dimensions.Height);

        public void Undo(GridCandidate candidate) =>
            SetHeight(
                candidate.X,
                candidate.Y,
                candidate.Dimensions,
                candidate.Z);

        public string CreateStateKey(int[] remaining)
        {
            if (_container.Height <= char.MaxValue)
            {
                var characters = new char[remaining.Length + _tops.Length];
                for (var index = 0; index < remaining.Length; index++)
                {
                    characters[index] = (char)remaining[index];
                }

                for (var index = 0; index < _tops.Length; index++)
                {
                    characters[remaining.Length + index] = (char)_tops[index];
                }

                return new string(characters);
            }

            return string.Join(',', remaining)
                + "|"
                + string.Join(',', _tops);
        }

        private bool TryGetBaseHeight(
            int x,
            int y,
            OrientedDimensions dimensions,
            out int z)
        {
            z = _tops[y * _container.Width + x];
            if (z > _container.Height - dimensions.Height)
            {
                return false;
            }

            for (var yOffset = 0; yOffset < dimensions.Depth; yOffset++)
            {
                var rowStart = (y + yOffset) * _container.Width + x;
                for (var xOffset = 0; xOffset < dimensions.Width; xOffset++)
                {
                    if (_tops[rowStart + xOffset] != z)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private void SetHeight(
            int x,
            int y,
            OrientedDimensions dimensions,
            int height)
        {
            for (var yOffset = 0; yOffset < dimensions.Depth; yOffset++)
            {
                var rowStart = (y + yOffset) * _container.Width + x;
                for (var xOffset = 0; xOffset < dimensions.Width; xOffset++)
                {
                    _tops[rowStart + xOffset] = height;
                }
            }
        }
    }

    private sealed class FreeSpaceLayout
    {
        private readonly ContainerSpec _container;
        private readonly List<FreeSpace> _spaces;

        public FreeSpaceLayout(ContainerSpec container)
        {
            _container = container;
            _spaces =
            [
                new FreeSpace(
                    X: 0,
                    Y: 0,
                    Z: 0,
                    Width: container.Width,
                    Depth: container.Depth,
                    Height: container.Height),
            ];
        }

        public bool TryFindBest(
            TypeInfo type,
            PlacementStyle style,
            out FreeSpaceCandidate candidate)
        {
            candidate = default;
            var found = false;

            for (var spaceIndex = 0; spaceIndex < _spaces.Count; spaceIndex++)
            {
                var space = _spaces[spaceIndex];
                foreach (var dimensions in type.Orientations)
                {
                    if (dimensions.Width > space.Width
                        || dimensions.Depth > space.Depth
                        || dimensions.Height > space.Height)
                    {
                        continue;
                    }

                    var current = new FreeSpaceCandidate(spaceIndex, space, dimensions);
                    if (!found
                        || CompareFreeSpaceCandidates(
                            current,
                            candidate,
                            style,
                            _container) < 0)
                    {
                        candidate = current;
                        found = true;
                    }
                }
            }

            return found;
        }

        public FreeSpace Place(FreeSpaceCandidate candidate)
        {
            var space = _spaces[candidate.SpaceIndex];
            _spaces.RemoveAt(candidate.SpaceIndex);

            var dimensions = candidate.Dimensions;
            AddIfPositive(new FreeSpace(
                space.X + dimensions.Width,
                space.Y,
                space.Z,
                space.Width - dimensions.Width,
                space.Depth,
                space.Height));
            AddIfPositive(new FreeSpace(
                space.X,
                space.Y + dimensions.Depth,
                space.Z,
                dimensions.Width,
                space.Depth - dimensions.Depth,
                space.Height));
            AddIfPositive(new FreeSpace(
                space.X,
                space.Y,
                space.Z + dimensions.Height,
                dimensions.Width,
                dimensions.Depth,
                space.Height - dimensions.Height));

            MergeAdjacentSpaces();
            return space;
        }

        private void AddIfPositive(FreeSpace space)
        {
            if (space.Width > 0 && space.Depth > 0 && space.Height > 0)
            {
                _spaces.Add(space);
            }
        }

        private void MergeAdjacentSpaces()
        {
            if (_spaces.Count > 512)
            {
                return;
            }

            var merged = true;
            while (merged)
            {
                merged = false;
                for (var leftIndex = 0; leftIndex < _spaces.Count && !merged; leftIndex++)
                {
                    for (var rightIndex = leftIndex + 1;
                         rightIndex < _spaces.Count;
                         rightIndex++)
                    {
                        if (!TryMerge(
                                _spaces[leftIndex],
                                _spaces[rightIndex],
                                out var replacement))
                        {
                            continue;
                        }

                        _spaces.RemoveAt(rightIndex);
                        _spaces.RemoveAt(leftIndex);
                        _spaces.Add(replacement);
                        merged = true;
                        break;
                    }
                }
            }
        }

        private static bool TryMerge(
            FreeSpace left,
            FreeSpace right,
            out FreeSpace merged)
        {
            if (left.Z == right.Z
                && left.Height == right.Height
                && left.Y == right.Y
                && left.Depth == right.Depth
                && (left.X + left.Width == right.X
                    || right.X + right.Width == left.X))
            {
                merged = new FreeSpace(
                    Math.Min(left.X, right.X),
                    left.Y,
                    left.Z,
                    left.Width + right.Width,
                    left.Depth,
                    left.Height);
                return true;
            }

            if (left.Z == right.Z
                && left.Height == right.Height
                && left.X == right.X
                && left.Width == right.Width
                && (left.Y + left.Depth == right.Y
                    || right.Y + right.Depth == left.Y))
            {
                merged = new FreeSpace(
                    left.X,
                    Math.Min(left.Y, right.Y),
                    left.Z,
                    left.Width,
                    left.Depth + right.Depth,
                    left.Height);
                return true;
            }

            merged = default;
            return false;
        }
    }

    private sealed class KnapsackNode
    {
        public static KnapsackNode Root { get; } = new(null, -1, 0, 0);

        public KnapsackNode(
            KnapsackNode? previous,
            int typeIndex,
            long value,
            long volume)
        {
            Previous = previous;
            TypeIndex = typeIndex;
            Value = value;
            Volume = volume;
        }

        public KnapsackNode? Previous { get; }

        public int TypeIndex { get; }

        public long Value { get; }

        public long Volume { get; }
    }

    private sealed class ProblemData
    {
        public ProblemData(
            ContainerSpec container,
            List<TypeInfo> types,
            long footprintArea,
            int totalMaximumCount)
        {
            Container = container;
            Types = types;
            FootprintArea = footprintArea;
            TotalMaximumCount = totalMaximumCount;
        }

        public ContainerSpec Container { get; }

        public List<TypeInfo> Types { get; }

        public long FootprintArea { get; }

        public int TotalMaximumCount { get; }
    }

    private sealed class TypeInfo
    {
        public TypeInfo(
            CartonType carton,
            OrientedDimensions[] orientations,
            int maximumCount,
            long volume)
        {
            Carton = carton;
            Orientations = orientations;
            MaximumCount = maximumCount;
            Volume = volume;
        }

        public CartonType Carton { get; }

        public OrientedDimensions[] Orientations { get; }

        public int MaximumCount { get; }

        public long Volume { get; }
    }

    private sealed class LayoutResult
    {
        public static LayoutResult Empty { get; } = new([], 0, 0, 0);

        public LayoutResult(
            List<Placement> placements,
            long totalWeight,
            long totalValue,
            long totalVolume)
        {
            Placements = placements;
            TotalWeight = totalWeight;
            TotalValue = totalValue;
            TotalVolume = totalVolume;
        }

        public List<Placement> Placements { get; }

        public long TotalWeight { get; }

        public long TotalValue { get; }

        public long TotalVolume { get; }
    }

    private readonly record struct GridCandidate(
        int X,
        int Y,
        int Z,
        OrientedDimensions Dimensions);

    private readonly record struct FreeSpace(
        int X,
        int Y,
        int Z,
        int Width,
        int Depth,
        int Height);

    private readonly record struct FreeSpaceCandidate(
        int SpaceIndex,
        FreeSpace Space,
        OrientedDimensions Dimensions);

    private enum PackingRanking
    {
        Value,
        ValuePerWeight,
        ValuePerVolume,
        ValueThenLight,
        Volume,
    }

    private enum PlacementStyle
    {
        BottomLeft,
        LowTop,
        WideBase,
        EdgeAligned,
    }
}
