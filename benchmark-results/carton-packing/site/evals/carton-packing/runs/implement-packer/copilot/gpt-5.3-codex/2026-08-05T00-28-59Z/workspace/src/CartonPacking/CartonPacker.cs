namespace CartonPacking;

public sealed class CartonPacker
{
        private const int BeamWidth = 96;
        private const int MaxChildrenPerState = 14;
        private const int MaxIterations = 384;

        public PackingResult Pack(PackingProblem problem)
        {
            ArgumentNullException.ThrowIfNull(problem);

            if (problem.Container.Width <= 0
                || problem.Container.Depth <= 0
                || problem.Container.Height <= 0
                || problem.Container.MaxWeight < 0
                || problem.Cartons.Count == 0)
            {
                return PackingResult.Empty;
            }

            var itemTypes = BuildItemTypes(problem);
            if (itemTypes.Count == 0)
            {
                return PackingResult.Empty;
            }

            var placementTypeOrder = Enumerable.Range(0, itemTypes.Count)
                .OrderByDescending(index => itemTypes[index].Value)
                .ThenByDescending(index => itemTypes[index].Volume)
                .ThenByDescending(index => itemTypes[index].ValueDensity)
                .ThenBy(index => itemTypes[index].Weight)
                .ThenBy(index => itemTypes[index].Id, StringComparer.Ordinal)
                .ToArray();

            var valueBoundOrder = Enumerable.Range(0, itemTypes.Count)
                .OrderByDescending(index => itemTypes[index].ValueDensity)
                .ThenByDescending(index => itemTypes[index].Value)
                .ThenByDescending(index => itemTypes[index].Volume)
                .ThenBy(index => itemTypes[index].Weight)
                .ThenBy(index => itemTypes[index].Id, StringComparer.Ordinal)
                .ToArray();

            var initialState = CreateInitialState(problem.Container, itemTypes);
            var bestState = initialState;
            var beam = new List<SearchState> { initialState };

            for (var iteration = 0; iteration < MaxIterations && beam.Count > 0; iteration++)
            {
                var nextStates = new List<SearchState>(beam.Count * (MaxChildrenPerState + 1));
                foreach (var state in beam)
                {
                    if (IsBetterObjective(state, bestState))
                    {
                        bestState = state;
                    }

                    if (state.Spaces.Count == 0)
                    {
                        continue;
                    }

                    var spaceIndex = 0;
                    var space = state.Spaces[spaceIndex];

                    var choices = EnumeratePlacementChoices(
                        state,
                        space,
                        itemTypes,
                        placementTypeOrder);
                    foreach (var choice in choices)
                    {
                        var child = ApplyPlacement(
                            state,
                            spaceIndex,
                            space,
                            choice,
                            itemTypes);
                        if (child is null || !CanBeatBest(child, bestState, itemTypes, valueBoundOrder))
                        {
                            continue;
                        }

                        nextStates.Add(child);
                    }

                    var skipped = SkipSpace(state, spaceIndex);
                    if (CanBeatBest(skipped, bestState, itemTypes, valueBoundOrder))
                    {
                        nextStates.Add(skipped);
                    }
                }

                if (nextStates.Count == 0)
                {
                    break;
                }

                beam = SelectNextBeam(nextStates, bestState, itemTypes, valueBoundOrder);
            }

            foreach (var state in beam)
            {
                if (IsBetterObjective(state, bestState))
                {
                    bestState = state;
                }
            }

            return BuildResult(bestState, itemTypes);
        }

        private static SearchState CreateInitialState(
            ContainerSpec container,
            IReadOnlyList<ItemType> itemTypes)
        {
            var remainingByType = itemTypes.Select(type => type.Quantity).ToArray();
            return new(
                [],
                [
                    new(
                        0,
                        0,
                        0,
                        container.Width,
                        container.Depth,
                        container.Height),
                ],
                remainingByType,
                container.MaxWeight,
                0,
                0);
        }

        private static List<ItemType> BuildItemTypes(PackingProblem problem)
        {
            return problem.Cartons
                .Where(carton =>
                    !string.IsNullOrWhiteSpace(carton.Id)
                    && carton.Quantity > 0
                    && carton.Weight >= 0
                    && carton.Value >= 0
                    && carton.Width > 0
                    && carton.Depth > 0
                    && carton.Height > 0)
                .Select(carton =>
                {
                    var fittingOrientations = OrientationGenerator.GetOrientations(carton)
                        .Where(orientation =>
                            orientation.Width <= problem.Container.Width
                            && orientation.Depth <= problem.Container.Depth
                            && orientation.Height <= problem.Container.Height)
                        .OrderByDescending(orientation => orientation.Volume)
                        .ThenByDescending(orientation =>
                            (long)orientation.Width * orientation.Depth)
                        .ThenBy(orientation => orientation.Height)
                        .ThenBy(orientation => orientation.Width)
                        .ThenBy(orientation => orientation.Depth)
                        .ToArray();

                    return new ItemType(
                        carton.Id,
                        carton.Quantity,
                        carton.Weight,
                        carton.Value,
                        (long)carton.Width * carton.Depth * carton.Height,
                        fittingOrientations);
                })
                .Where(type => type.Orientations.Length > 0)
                .OrderBy(type => type.Id, StringComparer.Ordinal)
                .ToList();
        }

        private static IReadOnlyList<PlacementChoice> EnumeratePlacementChoices(
            SearchState state,
            FreeSpace space,
            IReadOnlyList<ItemType> itemTypes,
            IReadOnlyList<int> placementTypeOrder)
        {
            var choices = new List<PlacementChoice>();
            foreach (var typeIndex in placementTypeOrder)
            {
                var remaining = state.RemainingByType[typeIndex];
                if (remaining <= 0)
                {
                    continue;
                }

                var type = itemTypes[typeIndex];
                if (type.Weight > state.RemainingWeight)
                {
                    continue;
                }

                foreach (var orientation in type.Orientations)
                {
                    if (orientation.Width > space.Width
                        || orientation.Depth > space.Depth
                        || orientation.Height > space.Height)
                    {
                        continue;
                    }

                    var slack = (space.Width - orientation.Width)
                        + (space.Depth - orientation.Depth)
                        + (space.Height - orientation.Height);
                    choices.Add(new(typeIndex, orientation, 0, slack));

                    if (space.Width > orientation.Width && space.Depth > orientation.Depth)
                    {
                        choices.Add(new(typeIndex, orientation, 1, slack));
                    }
                }
            }

            choices.Sort((left, right) =>
            {
                var leftType = itemTypes[left.TypeIndex];
                var rightType = itemTypes[right.TypeIndex];

                var compare = rightType.Value.CompareTo(leftType.Value);
                if (compare != 0)
                {
                    return compare;
                }

                compare = rightType.Volume.CompareTo(leftType.Volume);
                if (compare != 0)
                {
                    return compare;
                }

                compare = rightType.ValueDensity.CompareTo(leftType.ValueDensity);
                if (compare != 0)
                {
                    return compare;
                }

                compare = left.Slack.CompareTo(right.Slack);
                if (compare != 0)
                {
                    return compare;
                }

                compare = left.Orientation.Height.CompareTo(right.Orientation.Height);
                if (compare != 0)
                {
                    return compare;
                }

                compare = left.Orientation.Width.CompareTo(right.Orientation.Width);
                if (compare != 0)
                {
                    return compare;
                }

                compare = left.Orientation.Depth.CompareTo(right.Orientation.Depth);
                if (compare != 0)
                {
                    return compare;
                }

                compare = StringComparer.Ordinal.Compare(leftType.Id, rightType.Id);
                if (compare != 0)
                {
                    return compare;
                }

                return left.SplitMode.CompareTo(right.SplitMode);
            });

            if (choices.Count > MaxChildrenPerState)
            {
                choices.RemoveRange(MaxChildrenPerState, choices.Count - MaxChildrenPerState);
            }

            return choices;
        }

        private static SearchState? ApplyPlacement(
            SearchState state,
            int spaceIndex,
            FreeSpace space,
            PlacementChoice choice,
            IReadOnlyList<ItemType> itemTypes)
        {
            var type = itemTypes[choice.TypeIndex];
            if (state.RemainingByType[choice.TypeIndex] <= 0
                || type.Weight > state.RemainingWeight)
            {
                return null;
            }

            var orientation = choice.Orientation;
            if (orientation.Width > space.Width
                || orientation.Depth > space.Depth
                || orientation.Height > space.Height)
            {
                return null;
            }

            var placements = new List<PackedPlacement>(state.Placements.Count + 1);
            placements.AddRange(state.Placements);
            placements.Add(new(
                choice.TypeIndex,
                space.X,
                space.Y,
                space.Z,
                orientation.Width,
                orientation.Depth,
                orientation.Height));

            var remainingByType = (int[])state.RemainingByType.Clone();
            remainingByType[choice.TypeIndex]--;

            var spaces = new List<FreeSpace>(state.Spaces.Count + 3);
            for (var i = 0; i < state.Spaces.Count; i++)
            {
                if (i != spaceIndex)
                {
                    spaces.Add(state.Spaces[i]);
                }
            }

            AddSplitSpaces(spaces, space, orientation, choice.SplitMode);
            spaces = NormalizeSpaces(spaces);

            return new(
                placements,
                spaces,
                remainingByType,
                state.RemainingWeight - type.Weight,
                state.TotalValue + type.Value,
                state.TotalVolume + orientation.Volume);
        }

        private static SearchState SkipSpace(SearchState state, int spaceIndex)
        {
            var spaces = new List<FreeSpace>(state.Spaces.Count - 1);
            for (var i = 0; i < state.Spaces.Count; i++)
            {
                if (i != spaceIndex)
                {
                    spaces.Add(state.Spaces[i]);
                }
            }

            return new(
                new List<PackedPlacement>(state.Placements),
                spaces,
                (int[])state.RemainingByType.Clone(),
                state.RemainingWeight,
                state.TotalValue,
                state.TotalVolume);
        }

        private static void AddSplitSpaces(
            List<FreeSpace> spaces,
            FreeSpace space,
            OrientedDimensions orientation,
            int splitMode)
        {
            var remainingWidth = space.Width - orientation.Width;
            var remainingDepth = space.Depth - orientation.Depth;
            var remainingHeight = space.Height - orientation.Height;

            if (splitMode == 0)
            {
                if (remainingWidth > 0)
                {
                    spaces.Add(new(
                        space.X + orientation.Width,
                        space.Y,
                        space.Z,
                        remainingWidth,
                        space.Depth,
                        space.Height));
                }

                if (remainingDepth > 0)
                {
                    spaces.Add(new(
                        space.X,
                        space.Y + orientation.Depth,
                        space.Z,
                        orientation.Width,
                        remainingDepth,
                        space.Height));
                }
            }
            else
            {
                if (remainingDepth > 0)
                {
                    spaces.Add(new(
                        space.X,
                        space.Y + orientation.Depth,
                        space.Z,
                        space.Width,
                        remainingDepth,
                        space.Height));
                }

                if (remainingWidth > 0)
                {
                    spaces.Add(new(
                        space.X + orientation.Width,
                        space.Y,
                        space.Z,
                        remainingWidth,
                        orientation.Depth,
                        space.Height));
                }
            }

            if (remainingHeight > 0)
            {
                spaces.Add(new(
                    space.X,
                    space.Y,
                    space.Z + orientation.Height,
                    orientation.Width,
                    orientation.Depth,
                    remainingHeight));
            }
        }

        private static List<FreeSpace> NormalizeSpaces(List<FreeSpace> spaces)
        {
            spaces = spaces
                .Where(space =>
                    space.Width > 0
                    && space.Depth > 0
                    && space.Height > 0)
                .Distinct()
                .ToList();

            var changed = true;
            while (changed)
            {
                changed = false;
                spaces.Sort(CompareSpaces);

                for (var i = 0; i < spaces.Count; i++)
                {
                    for (var j = i + 1; j < spaces.Count; j++)
                    {
                        if (!TryMergeSpaces(spaces[i], spaces[j], out var merged))
                        {
                            continue;
                        }

                        spaces[i] = merged;
                        spaces.RemoveAt(j);
                        changed = true;
                        break;
                    }

                    if (changed)
                    {
                        break;
                    }
                }
            }

            spaces.Sort(CompareSpaces);
            return spaces;
        }

        private static bool TryMergeSpaces(
            FreeSpace left,
            FreeSpace right,
            out FreeSpace merged)
        {
            if (left.Z == right.Z
                && left.Height == right.Height
                && left.Y == right.Y
                && left.Depth == right.Depth
                && left.X + left.Width == right.X)
            {
                merged = new(
                    left.X,
                    left.Y,
                    left.Z,
                    left.Width + right.Width,
                    left.Depth,
                    left.Height);
                return true;
            }

            if (left.Z == right.Z
                && left.Height == right.Height
                && left.Y == right.Y
                && left.Depth == right.Depth
                && right.X + right.Width == left.X)
            {
                merged = new(
                    right.X,
                    right.Y,
                    right.Z,
                    right.Width + left.Width,
                    right.Depth,
                    right.Height);
                return true;
            }

            if (left.Z == right.Z
                && left.Height == right.Height
                && left.X == right.X
                && left.Width == right.Width
                && left.Y + left.Depth == right.Y)
            {
                merged = new(
                    left.X,
                    left.Y,
                    left.Z,
                    left.Width,
                    left.Depth + right.Depth,
                    left.Height);
                return true;
            }

            if (left.Z == right.Z
                && left.Height == right.Height
                && left.X == right.X
                && left.Width == right.Width
                && right.Y + right.Depth == left.Y)
            {
                merged = new(
                    right.X,
                    right.Y,
                    right.Z,
                    right.Width,
                    right.Depth + left.Depth,
                    right.Height);
                return true;
            }

            merged = default;
            return false;
        }

        private static int CompareSpaces(FreeSpace left, FreeSpace right)
        {
            var compare = left.Z.CompareTo(right.Z);
            if (compare != 0)
            {
                return compare;
            }

            compare = left.Y.CompareTo(right.Y);
            if (compare != 0)
            {
                return compare;
            }

            compare = left.X.CompareTo(right.X);
            if (compare != 0)
            {
                return compare;
            }

            compare = right.Height.CompareTo(left.Height);
            if (compare != 0)
            {
                return compare;
            }

            compare = right.Depth.CompareTo(left.Depth);
            if (compare != 0)
            {
                return compare;
            }

            return right.Width.CompareTo(left.Width);
        }

        private static bool IsBetterObjective(SearchState candidate, SearchState best) =>
            candidate.TotalValue > best.TotalValue
            || (candidate.TotalValue == best.TotalValue
                && candidate.TotalVolume > best.TotalVolume);

        private static bool CanBeatBest(
            SearchState state,
            SearchState best,
            IReadOnlyList<ItemType> itemTypes,
            IReadOnlyList<int> valueBoundOrder)
        {
            var upperValue = ComputeUpperValueBound(state, itemTypes, valueBoundOrder);
            if (upperValue < best.TotalValue)
            {
                return false;
            }

            if (upperValue > best.TotalValue)
            {
                return true;
            }

            var upperVolume = ComputeUpperVolumeBound(state, itemTypes);
            return upperVolume > best.TotalVolume;
        }

        private static long ComputeUpperValueBound(
            SearchState state,
            IReadOnlyList<ItemType> itemTypes,
            IReadOnlyList<int> valueBoundOrder)
        {
            long value = state.TotalValue;
            var remainingWeight = state.RemainingWeight;
            foreach (var typeIndex in valueBoundOrder)
            {
                var type = itemTypes[typeIndex];
                var remaining = state.RemainingByType[typeIndex];
                if (remaining <= 0 || type.Value <= 0)
                {
                    continue;
                }

                if (type.Weight == 0)
                {
                    value += (long)remaining * type.Value;
                    continue;
                }

                if (remainingWeight <= 0)
                {
                    break;
                }

                var byWeight = remainingWeight / type.Weight;
                if (byWeight <= 0)
                {
                    continue;
                }

                var take = Math.Min(remaining, byWeight);
                value += (long)take * type.Value;
                remainingWeight -= take * type.Weight;
            }

            return value;
        }

        private static long ComputeUpperVolumeBound(
            SearchState state,
            IReadOnlyList<ItemType> itemTypes)
        {
            var freeVolume = state.Spaces.Sum(space => space.Volume);
            var remainingVolume = 0L;
            for (var i = 0; i < itemTypes.Count; i++)
            {
                if (state.RemainingByType[i] <= 0)
                {
                    continue;
                }

                remainingVolume += (long)state.RemainingByType[i] * itemTypes[i].Volume;
            }

            return state.TotalVolume + Math.Min(freeVolume, remainingVolume);
        }

        private static List<SearchState> SelectNextBeam(
            IReadOnlyList<SearchState> nextStates,
            SearchState currentBest,
            IReadOnlyList<ItemType> itemTypes,
            IReadOnlyList<int> valueBoundOrder)
        {
            var uniqueBySignature = new Dictionary<string, RankedState>(StringComparer.Ordinal);
            foreach (var state in nextStates)
            {
                var ranked = new RankedState(
                    state,
                    ComputeUpperValueBound(state, itemTypes, valueBoundOrder),
                    ComputeUpperVolumeBound(state, itemTypes));
                var signature = BuildStateSignature(state);

                if (uniqueBySignature.TryGetValue(signature, out var existing)
                    && CompareRankedStates(ranked, existing) <= 0)
                {
                    continue;
                }

                uniqueBySignature[signature] = ranked;
            }

            return uniqueBySignature.Values
                .Where(ranked =>
                    ranked.UpperValue > currentBest.TotalValue
                    || (ranked.UpperValue == currentBest.TotalValue
                        && ranked.UpperVolume > currentBest.TotalVolume)
                    || IsBetterObjective(ranked.State, currentBest))
                .OrderByDescending(ranked => ranked.UpperValue)
                .ThenByDescending(ranked => ranked.State.TotalValue)
                .ThenByDescending(ranked => ranked.UpperVolume)
                .ThenByDescending(ranked => ranked.State.TotalVolume)
                .ThenByDescending(ranked => ranked.State.Placements.Count)
                .ThenBy(ranked => ranked.State.Spaces.Count)
                .ThenBy(ranked => BuildStateSignature(ranked.State), StringComparer.Ordinal)
                .Take(BeamWidth)
                .Select(ranked => ranked.State)
                .ToList();
        }

        private static int CompareRankedStates(RankedState left, RankedState right)
        {
            var compare = left.UpperValue.CompareTo(right.UpperValue);
            if (compare != 0)
            {
                return compare;
            }

            compare = left.State.TotalValue.CompareTo(right.State.TotalValue);
            if (compare != 0)
            {
                return compare;
            }

            compare = left.UpperVolume.CompareTo(right.UpperVolume);
            if (compare != 0)
            {
                return compare;
            }

            compare = left.State.TotalVolume.CompareTo(right.State.TotalVolume);
            if (compare != 0)
            {
                return compare;
            }

            compare = left.State.Placements.Count.CompareTo(right.State.Placements.Count);
            if (compare != 0)
            {
                return compare;
            }

            return right.State.Spaces.Count.CompareTo(left.State.Spaces.Count);
        }

        private static string BuildStateSignature(SearchState state)
        {
            var buffer = new System.Text.StringBuilder();
            buffer.Append(state.RemainingWeight).Append('|');
            foreach (var quantity in state.RemainingByType)
            {
                buffer.Append(quantity).Append(',');
            }

            buffer.Append('|');
            foreach (var space in state.Spaces)
            {
                buffer.Append(space.X).Append(':')
                    .Append(space.Y).Append(':')
                    .Append(space.Z).Append(':')
                    .Append(space.Width).Append(':')
                    .Append(space.Depth).Append(':')
                    .Append(space.Height).Append(';');
            }

            return buffer.ToString();
        }

        private static PackingResult BuildResult(
            SearchState state,
            IReadOnlyList<ItemType> itemTypes)
        {
            var orderedGeometry = state.Placements
                .Select(placement => new
                {
                    CartonId = itemTypes[placement.TypeIndex].Id,
                    placement.X,
                    placement.Y,
                    placement.Z,
                    placement.Width,
                    placement.Depth,
                    placement.Height,
                })
                .OrderBy(placement => placement.CartonId, StringComparer.Ordinal)
                .ThenBy(placement => placement.X)
                .ThenBy(placement => placement.Y)
                .ThenBy(placement => placement.Z)
                .ThenBy(placement => placement.Width)
                .ThenBy(placement => placement.Depth)
                .ThenBy(placement => placement.Height)
                .ToArray();

            var nextInstanceById = new Dictionary<string, int>(StringComparer.Ordinal);
            var placements = new List<Placement>(orderedGeometry.Length);
            foreach (var placement in orderedGeometry)
            {
                nextInstanceById.TryGetValue(placement.CartonId, out var instance);
                nextInstanceById[placement.CartonId] = instance + 1;
                placements.Add(new(
                    placement.CartonId,
                    instance,
                    placement.X,
                    placement.Y,
                    placement.Z,
                    placement.Width,
                    placement.Depth,
                    placement.Height));
            }

            placements = placements
                .OrderBy(placement => placement.CartonId, StringComparer.Ordinal)
                .ThenBy(placement => placement.Instance)
                .ThenBy(placement => placement.X)
                .ThenBy(placement => placement.Y)
                .ThenBy(placement => placement.Z)
                .ToList();
            return new(placements);
        }

        private sealed record ItemType(
            string Id,
            int Quantity,
            int Weight,
            int Value,
            long Volume,
            OrientedDimensions[] Orientations)
        {
            public double ValueDensity =>
                Weight == 0
                    ? double.PositiveInfinity
                    : (double)Value / Weight;
        }

        private sealed record SearchState(
            List<PackedPlacement> Placements,
            List<FreeSpace> Spaces,
            int[] RemainingByType,
            int RemainingWeight,
            long TotalValue,
            long TotalVolume);

        private readonly record struct PackedPlacement(
            int TypeIndex,
            int X,
            int Y,
            int Z,
            int Width,
            int Depth,
            int Height);

        private readonly record struct FreeSpace(
            int X,
            int Y,
            int Z,
            int Width,
            int Depth,
            int Height)
        {
            public long Volume => (long)Width * Depth * Height;
        }

        private readonly record struct PlacementChoice(
            int TypeIndex,
            OrientedDimensions Orientation,
            int SplitMode,
            int Slack);

        private readonly record struct RankedState(
            SearchState State,
            long UpperValue,
            long UpperVolume);
}
