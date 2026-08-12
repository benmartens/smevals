namespace ReplicatedShardRebalancer;

/// <summary>
/// Deterministic solver for the replicated shard placement problem.
/// Builds greedy seeds, refines them with local search, and finishes with a
/// bounded branch-and-bound that proves optimality on small instances.
/// </summary>
internal sealed class RebalanceSolver
{
    private const int MaxCandidatesPerShard = 250_000;
    private const long MaxTotalCandidates = 3_000_000;
    private const int LocalSearchCandidateLimit = 8192;
    private const long ExactBudget = 8_000_000;
    private const long ExactWorkBudget = 40_000_000;
    private const int MaxSwapShards = 220;
    private const int MaxLocalSearchRounds = 60;

    private readonly string[] _nodeIds;
    private readonly long[] _capacity;
    private readonly int[] _zoneOf;
    private readonly int _nodeCount;
    private readonly long _totalCapacity;

    private readonly string[] _shardIds;
    private readonly long[] _size;
    private readonly int[] _replicas;
    private readonly int[] _requiredZones;
    private readonly int _shardCount;

    private readonly bool[][] _allowed;
    private readonly int[][] _pool;
    private readonly bool[][] _isCurrent;

    private readonly int[][][]? _candidates;
    private readonly int[][]? _candidateMoved;

    public RebalanceSolver(RebalanceProblem problem)
    {
        var nodes = new List<NodeSpec>();
        var nodeSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in problem.Nodes ?? [])
        {
            if (node is null
                || string.IsNullOrWhiteSpace(node.Id)
                || string.IsNullOrWhiteSpace(node.Zone)
                || node.Capacity <= 0
                || !nodeSeen.Add(node.Id))
            {
                continue;
            }
            nodes.Add(node);
        }
        nodes.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));

        _nodeCount = nodes.Count;
        _nodeIds = new string[_nodeCount];
        _capacity = new long[_nodeCount];
        _zoneOf = new int[_nodeCount];
        var zoneIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var nodeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _nodeCount; i++)
        {
            _nodeIds[i] = nodes[i].Id;
            _capacity[i] = nodes[i].Capacity;
            if (!zoneIds.TryGetValue(nodes[i].Zone, out var zone))
            {
                zone = zoneIds.Count;
                zoneIds[nodes[i].Zone] = zone;
            }
            _zoneOf[i] = zone;
            nodeIndex[nodes[i].Id] = i;
            _totalCapacity += nodes[i].Capacity;
        }

        var shards = new List<ShardSpec>();
        var shardSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shard in problem.Shards ?? [])
        {
            if (shard is null
                || string.IsNullOrWhiteSpace(shard.Id)
                || shard.Size <= 0
                || shard.ReplicationFactor <= 0
                || !shardSeen.Add(shard.Id))
            {
                continue;
            }
            shards.Add(shard);
        }
        shards.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));

        _shardCount = shards.Count;
        _shardIds = new string[_shardCount];
        _size = new long[_shardCount];
        _replicas = new int[_shardCount];
        _requiredZones = new int[_shardCount];
        _allowed = new bool[_shardCount][];
        _pool = new int[_shardCount][];
        _isCurrent = new bool[_shardCount][];
        var shardIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var j = 0; j < _shardCount; j++)
        {
            _shardIds[j] = shards[j].Id;
            _size[j] = shards[j].Size;
            _replicas[j] = Math.Min(shards[j].ReplicationFactor, Math.Max(_nodeCount, 1));
            _allowed[j] = new bool[_nodeCount];
            _isCurrent[j] = new bool[_nodeCount];
            shardIndex[shards[j].Id] = j;
        }

        var excluded = new bool[_shardCount][];
        for (var j = 0; j < _shardCount; j++)
        {
            excluded[j] = new bool[_nodeCount];
        }
        foreach (var exclusion in problem.Exclusions ?? [])
        {
            if (exclusion is null
                || !shardIndex.TryGetValue(exclusion.ShardId, out var j)
                || !nodeIndex.TryGetValue(exclusion.NodeId, out var i))
            {
                continue;
            }
            excluded[j][i] = true;
        }

        var currentSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var placement in problem.CurrentPlacements ?? [])
        {
            if (placement is null
                || !shardIndex.TryGetValue(placement.ShardId, out var j)
                || !currentSeen.Add(placement.ShardId))
            {
                continue;
            }
            foreach (var nodeId in placement.NodeIds ?? [])
            {
                if (nodeId is not null && nodeIndex.TryGetValue(nodeId, out var i))
                {
                    _isCurrent[j][i] = true;
                }
            }
        }

        for (var j = 0; j < _shardCount; j++)
        {
            BuildPool(j, excluded[j]);
        }

        _candidates = BuildCandidates(out _candidateMoved);
    }

    public RebalanceResult Solve()
    {
        if (_shardCount == 0)
        {
            return RebalanceResult.Empty;
        }

        var best = Seed(0);
        for (var mode = 1; mode <= 2; mode++)
        {
            var candidate = Seed(mode);
            if (candidate.Objective.CompareTo(best.Objective) < 0)
            {
                best = candidate;
            }
        }

        if (_candidates is not null)
        {
            best = ExactSearch(best);
        }

        var placements = new List<ShardPlacement>(_shardCount);
        for (var j = 0; j < _shardCount; j++)
        {
            var nodeIds = new List<string>(best.Assignment[j].Length);
            foreach (var node in best.Assignment[j])
            {
                nodeIds.Add(_nodeIds[node]);
            }
            nodeIds.Sort(StringComparer.Ordinal);
            placements.Add(new(_shardIds[j], nodeIds));
        }
        return new(placements);
    }

    private void BuildPool(int shard, bool[] excluded)
    {
        var allowed = _allowed[shard];
        var pool = new List<int>();
        for (var i = 0; i < _nodeCount; i++)
        {
            if (!excluded[i] && _capacity[i] >= _size[shard])
            {
                allowed[i] = true;
                pool.Add(i);
            }
        }

        var zones = new HashSet<int>();
        foreach (var node in pool)
        {
            zones.Add(_zoneOf[node]);
        }
        _requiredZones[shard] = Math.Min(_replicas[shard], zones.Count);

        // Degenerate inputs (guaranteed absent from benchmarks) still need an
        // answer, so widen the pool until enough distinct nodes exist.
        if (pool.Count < _replicas[shard])
        {
            for (var i = 0; i < _nodeCount && pool.Count < _replicas[shard]; i++)
            {
                if (!allowed[i] && !excluded[i])
                {
                    allowed[i] = true;
                    pool.Add(i);
                }
            }
            for (var i = 0; i < _nodeCount && pool.Count < _replicas[shard]; i++)
            {
                if (!allowed[i])
                {
                    allowed[i] = true;
                    pool.Add(i);
                }
            }
            pool.Sort();
            _requiredZones[shard] = Math.Min(
                _requiredZones[shard],
                pool.Select(node => _zoneOf[node]).Distinct().Count());
        }

        _pool[shard] = [.. pool];
    }

    private int[][][]? BuildCandidates(out int[][]? moved)
    {
        moved = null;
        var all = new int[_shardCount][][];
        var movedCounts = new int[_shardCount][];
        long total = 0;
        for (var j = 0; j < _shardCount; j++)
        {
            var list = Enumerate(j);
            if (list is null)
            {
                return null;
            }
            total += list.Length;
            if (total > MaxTotalCandidates || list.Length == 0)
            {
                return null;
            }
            all[j] = list;
            var counts = new int[list.Length];
            for (var c = 0; c < list.Length; c++)
            {
                counts[c] = MovedCount(j, list[c]);
            }
            movedCounts[j] = counts;
        }
        moved = movedCounts;
        return all;
    }

    private int[][]? Enumerate(int shard)
    {
        var pool = _pool[shard];
        var wanted = _replicas[shard];
        var required = _requiredZones[shard];
        if (wanted > pool.Length)
        {
            return null;
        }

        var suffixZones = new int[pool.Length + 1];
        var seen = new HashSet<int>();
        for (var idx = pool.Length - 1; idx >= 0; idx--)
        {
            seen.Add(_zoneOf[pool[idx]]);
            suffixZones[idx] = seen.Count;
        }

        var results = new List<int[]>();
        var chosen = new int[wanted];
        var zoneUse = new Dictionary<int, int>();
        var overflow = false;

        void Recurse(int start, int depth, int zonesUsed)
        {
            if (overflow)
            {
                return;
            }
            if (depth == wanted)
            {
                if (zonesUsed == required)
                {
                    results.Add((int[])chosen.Clone());
                    if (results.Count > MaxCandidatesPerShard)
                    {
                        overflow = true;
                    }
                }
                return;
            }

            var slots = wanted - depth;
            for (var idx = start; idx <= pool.Length - slots; idx++)
            {
                var node = pool[idx];
                var zone = _zoneOf[node];
                var used = zoneUse.GetValueOrDefault(zone);
                var isNew = used == 0;
                if (!isNew && zonesUsed >= required && required == wanted)
                {
                    continue;
                }
                var nextZones = isNew ? zonesUsed + 1 : zonesUsed;
                if (nextZones > required)
                {
                    continue;
                }
                var slotsLeft = slots - 1;
                if (nextZones + Math.Min(suffixZones[idx], slotsLeft) < required)
                {
                    continue;
                }

                chosen[depth] = node;
                zoneUse[zone] = used + 1;
                Recurse(idx + 1, depth + 1, nextZones);
                zoneUse[zone] = used;
                if (overflow)
                {
                    return;
                }
            }
        }

        Recurse(0, 0, 0);
        return overflow || results.Count == 0 ? null : [.. results];
    }

    private int MovedCount(int shard, int[] set)
    {
        var moved = 0;
        foreach (var node in set)
        {
            if (!_isCurrent[shard][node])
            {
                moved++;
            }
        }
        return moved;
    }

    private readonly struct Objective(
        long overflow,
        double max,
        double spread,
        long bytes,
        int replicas)
    {
        public readonly long Overflow = overflow;
        public readonly double Max = max;
        public readonly double Spread = spread;
        public readonly long Bytes = bytes;
        public readonly int Replicas = replicas;

        public static Objective Worst { get; } = new(
            long.MaxValue,
            double.MaxValue,
            double.MaxValue,
            long.MaxValue,
            int.MaxValue);

        public int CompareTo(in Objective other)
        {
            var order = Overflow.CompareTo(other.Overflow);
            if (order != 0)
            {
                return order;
            }
            order = Max.CompareTo(other.Max);
            if (order != 0)
            {
                return order;
            }
            order = Spread.CompareTo(other.Spread);
            if (order != 0)
            {
                return order;
            }
            order = Bytes.CompareTo(other.Bytes);
            return order != 0 ? order : Replicas.CompareTo(other.Replicas);
        }
    }

    private sealed class Solution
    {
        public required int[][] Assignment { get; init; }
        public required long[] Loads { get; init; }
        public required long Bytes { get; init; }
        public required int Replicas { get; init; }
        public required Objective Objective { get; init; }
    }

    private Objective Measure(long[] loads, long bytes, int replicas)
    {
        if (_nodeCount == 0)
        {
            return new(0, 0, 0, bytes, replicas);
        }
        double max = 0;
        var min = double.MaxValue;
        long overflow = 0;
        for (var i = 0; i < _nodeCount; i++)
        {
            var utilization = (double)loads[i] / _capacity[i];
            if (utilization > max)
            {
                max = utilization;
            }
            if (utilization < min)
            {
                min = utilization;
            }
            if (loads[i] > _capacity[i])
            {
                overflow += loads[i] - _capacity[i];
            }
        }
        return new(overflow, max, max - min, bytes, replicas);
    }

    private Solution Seed(int mode)
    {
        var assignment = new int[_shardCount][];
        var loads = new long[_nodeCount];
        var order = Enumerable.Range(0, _shardCount).ToArray();
        Array.Sort(order, (left, right) =>
        {
            var compare = _size[right].CompareTo(_size[left]);
            return compare != 0
                ? compare
                : string.CompareOrdinal(_shardIds[left], _shardIds[right]);
        });

        foreach (var shard in order)
        {
            assignment[shard] = Choose(shard, loads, mode);
            foreach (var node in assignment[shard])
            {
                loads[node] += _size[shard];
            }
        }

        long bytes = 0;
        var replicas = 0;
        for (var j = 0; j < _shardCount; j++)
        {
            var moved = MovedCount(j, assignment[j]);
            bytes += _size[j] * moved;
            replicas += moved;
        }

        var solution = new Solution
        {
            Assignment = assignment,
            Loads = loads,
            Bytes = bytes,
            Replicas = replicas,
            Objective = Measure(loads, bytes, replicas),
        };
        return LocalSearch(solution);
    }

    private int[] Choose(int shard, long[] loads, int mode)
    {
        var wanted = _replicas[shard];
        var required = _requiredZones[shard];
        var pool = _pool[shard];
        var chosen = new List<int>(wanted);
        var zoneUse = new HashSet<int>();
        var size = _size[shard];

        if (mode == 2)
        {
            foreach (var node in pool)
            {
                if (chosen.Count >= wanted || !_isCurrent[shard][node])
                {
                    continue;
                }
                if (required == wanted && !zoneUse.Add(_zoneOf[node]))
                {
                    continue;
                }
                zoneUse.Add(_zoneOf[node]);
                chosen.Add(node);
            }
        }

        while (chosen.Count < wanted)
        {
            var slots = wanted - chosen.Count;
            var missing = required - zoneUse.Count;
            var forceNewZone = missing >= slots;
            var best = -1;
            var bestOverflow = 0;
            var bestUtilization = 0d;
            var bestSlack = 0L;
            var bestCurrent = 0;

            foreach (var node in pool)
            {
                if (chosen.Contains(node))
                {
                    continue;
                }
                var isNew = !zoneUse.Contains(_zoneOf[node]);
                if (required == wanted && !isNew)
                {
                    continue;
                }
                if (forceNewZone && !isNew)
                {
                    continue;
                }

                var load = loads[node] + size;
                var overflow = load > _capacity[node] ? 1 : 0;
                var utilization = (double)load / _capacity[node];
                var slack = _capacity[node] - load;
                var current = _isCurrent[shard][node] ? 0 : 1;
                if (best < 0
                    || Better(
                        mode,
                        overflow,
                        utilization,
                        slack,
                        current,
                        bestOverflow,
                        bestUtilization,
                        bestSlack,
                        bestCurrent))
                {
                    best = node;
                    bestOverflow = overflow;
                    bestUtilization = utilization;
                    bestSlack = slack;
                    bestCurrent = current;
                }
            }

            if (best < 0)
            {
                foreach (var node in pool)
                {
                    if (!chosen.Contains(node))
                    {
                        best = node;
                        break;
                    }
                }
            }
            if (best < 0)
            {
                for (var i = 0; i < _nodeCount && chosen.Count < wanted; i++)
                {
                    if (!chosen.Contains(i))
                    {
                        best = i;
                        break;
                    }
                }
            }
            if (best < 0)
            {
                break;
            }

            zoneUse.Add(_zoneOf[best]);
            chosen.Add(best);
        }

        chosen.Sort();
        return [.. chosen];
    }

    private static bool Better(
        int mode,
        int overflow,
        double utilization,
        long slack,
        int current,
        int bestOverflow,
        double bestUtilization,
        long bestSlack,
        int bestCurrent)
    {
        if (overflow != bestOverflow)
        {
            return overflow < bestOverflow;
        }
        if (mode == 2 && current != bestCurrent)
        {
            return current < bestCurrent;
        }
        if (mode == 1)
        {
            if (slack != bestSlack)
            {
                return slack > bestSlack;
            }
        }
        else if (utilization != bestUtilization)
        {
            return utilization < bestUtilization;
        }
        return current < bestCurrent;
    }

    private bool ZonesValid(int shard, int[] set)
    {
        var zones = 0;
        for (var a = 0; a < set.Length; a++)
        {
            var duplicate = false;
            for (var b = 0; b < a; b++)
            {
                if (_zoneOf[set[b]] == _zoneOf[set[a]])
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
            {
                zones++;
            }
        }
        return zones == _requiredZones[shard];
    }

    private Solution LocalSearch(Solution start)
    {
        var assignment = start.Assignment;
        var loads = start.Loads;
        var bytes = start.Bytes;
        var replicas = start.Replicas;
        var objective = start.Objective;

        for (var round = 0; round < MaxLocalSearchRounds; round++)
        {
            var improved = false;
            for (var shard = 0; shard < _shardCount; shard++)
            {
                if (TryReassign(
                    shard,
                    assignment,
                    loads,
                    ref bytes,
                    ref replicas,
                    ref objective))
                {
                    improved = true;
                }
            }

            if (!improved
                && _shardCount <= MaxSwapShards
                && TrySwap(assignment, loads, ref bytes, ref replicas, ref objective))
            {
                improved = true;
            }

            if (!improved)
            {
                break;
            }
        }

        return new Solution
        {
            Assignment = assignment,
            Loads = loads,
            Bytes = bytes,
            Replicas = replicas,
            Objective = objective,
        };
    }

    private bool TryReassign(
        int shard,
        int[][] assignment,
        long[] loads,
        ref long bytes,
        ref int replicas,
        ref Objective objective)
    {
        var current = assignment[shard];
        var size = _size[shard];
        var currentMoved = MovedCount(shard, current);
        var best = objective;
        int[]? bestSet = null;
        var bestMoved = currentMoved;

        foreach (var node in current)
        {
            loads[node] -= size;
        }

        var options = Options(shard, current);
        foreach (var option in options)
        {
            var moved = MovedCount(shard, option);
            foreach (var node in option)
            {
                loads[node] += size;
            }
            var candidate = Measure(
                loads,
                bytes + (size * (moved - currentMoved)),
                replicas + moved - currentMoved);
            if (candidate.CompareTo(best) < 0)
            {
                best = candidate;
                bestSet = option;
                bestMoved = moved;
            }
            foreach (var node in option)
            {
                loads[node] -= size;
            }
        }

        var chosen = bestSet ?? current;
        foreach (var node in chosen)
        {
            loads[node] += size;
        }
        if (bestSet is null)
        {
            return false;
        }

        bytes += size * (bestMoved - currentMoved);
        replicas += bestMoved - currentMoved;
        assignment[shard] = bestSet;
        objective = best;
        return true;
    }

    private IEnumerable<int[]> Options(int shard, int[] current)
    {
        var candidates = _candidates;
        if (candidates is not null
            && candidates[shard].Length <= LocalSearchCandidateLimit)
        {
            foreach (var option in candidates[shard])
            {
                if (!option.SequenceEqual(current))
                {
                    yield return option;
                }
            }
            yield break;
        }

        var pool = _pool[shard];
        for (var slot = 0; slot < current.Length; slot++)
        {
            foreach (var node in pool)
            {
                if (Array.IndexOf(current, node) >= 0)
                {
                    continue;
                }
                var option = (int[])current.Clone();
                option[slot] = node;
                Array.Sort(option);
                if (ZonesValid(shard, option))
                {
                    yield return option;
                }
            }
        }
    }

    private bool TrySwap(
        int[][] assignment,
        long[] loads,
        ref long bytes,
        ref int replicas,
        ref Objective objective)
    {
        for (var first = 0; first < _shardCount; first++)
        {
            for (var second = first + 1; second < _shardCount; second++)
            {
                var left = assignment[first];
                var right = assignment[second];
                foreach (var outgoing in left)
                {
                    if (Array.IndexOf(right, outgoing) >= 0
                        || !_allowed[second][outgoing])
                    {
                        continue;
                    }
                    foreach (var incoming in right)
                    {
                        if (Array.IndexOf(left, incoming) >= 0
                            || !_allowed[first][incoming])
                        {
                            continue;
                        }

                        var newLeft = Replace(left, outgoing, incoming);
                        var newRight = Replace(right, incoming, outgoing);
                        if (!ZonesValid(first, newLeft)
                            || !ZonesValid(second, newRight))
                        {
                            continue;
                        }

                        var leftMoved = MovedCount(first, left);
                        var rightMoved = MovedCount(second, right);
                        var newLeftMoved = MovedCount(first, newLeft);
                        var newRightMoved = MovedCount(second, newRight);
                        var newBytes = bytes
                            + (_size[first] * (newLeftMoved - leftMoved))
                            + (_size[second] * (newRightMoved - rightMoved));
                        var newReplicas = replicas
                            + newLeftMoved - leftMoved
                            + newRightMoved - rightMoved;

                        loads[outgoing] += _size[second] - _size[first];
                        loads[incoming] += _size[first] - _size[second];
                        var candidate = Measure(loads, newBytes, newReplicas);
                        if (candidate.CompareTo(objective) < 0)
                        {
                            assignment[first] = newLeft;
                            assignment[second] = newRight;
                            bytes = newBytes;
                            replicas = newReplicas;
                            objective = candidate;
                            return true;
                        }
                        loads[outgoing] -= _size[second] - _size[first];
                        loads[incoming] -= _size[first] - _size[second];
                    }
                }
            }
        }
        return false;
    }

    private static int[] Replace(int[] set, int outgoing, int incoming)
    {
        var copy = (int[])set.Clone();
        copy[Array.IndexOf(copy, outgoing)] = incoming;
        Array.Sort(copy);
        return copy;
    }

    private long[] _searchLoads = [];
    private long[] _searchBudget = [];
    private long[] _suffixLoad = [];
    private long[] _suffixMinBytes = [];
    private int[] _suffixMinReplicas = [];
    private long[][] _suffixEligible = [];
    private int[][] _searchAssignment = [];
    private int[][]? _searchBest;
    private Objective _searchObjective;
    private long _searchSteps;
    private long _searchLimit;
    private bool _searchExhausted;
    private double _budgetFor = double.NaN;

    private Solution ExactSearch(Solution incumbent)
    {
        var candidates = _candidates;
        var moved = _candidateMoved;
        if (candidates is null || moved is null || incumbent.Objective.Overflow > 0)
        {
            return incumbent;
        }

        PrepareSuffixes(moved);
        _searchLoads = new long[_nodeCount];
        _searchAssignment = new int[_shardCount][];
        _searchBudget = new long[_nodeCount];
        _searchObjective = incumbent.Objective;
        _searchBest = null;
        _searchSteps = 0;
        _searchExhausted = false;
        _budgetFor = double.NaN;

        Explore(0, 0, 0, 0, candidates, moved, strict: true);
        var improved = _searchBest;
        if (_searchExhausted)
        {
            return improved is null ? incumbent : Rebuild(improved);
        }

        // The optimal objective is known; replay in lexicographic order so the
        // canonical (ordinally smallest) placement wins remaining ties.
        var target = _searchObjective;
        _searchLoads = new long[_nodeCount];
        _searchBest = null;
        _searchSteps = 0;
        _searchExhausted = false;
        _budgetFor = double.NaN;
        _searchObjective = target;
        Explore(0, 0, 0, 0, candidates, moved, strict: false);
        if (_searchBest is not null)
        {
            return Rebuild(_searchBest);
        }
        return improved is null ? incumbent : Rebuild(improved);
    }

    private Solution Rebuild(int[][] assignment)
    {
        var loads = new long[_nodeCount];
        long bytes = 0;
        var replicas = 0;
        for (var j = 0; j < _shardCount; j++)
        {
            foreach (var node in assignment[j])
            {
                loads[node] += _size[j];
            }
            var moved = MovedCount(j, assignment[j]);
            bytes += _size[j] * moved;
            replicas += moved;
        }
        return new Solution
        {
            Assignment = assignment,
            Loads = loads,
            Bytes = bytes,
            Replicas = replicas,
            Objective = Measure(loads, bytes, replicas),
        };
    }

    private void PrepareSuffixes(int[][] moved)
    {
        _suffixLoad = new long[_shardCount + 1];
        _suffixMinBytes = new long[_shardCount + 1];
        _suffixMinReplicas = new int[_shardCount + 1];
        _suffixEligible = new long[_nodeCount][];
        for (var i = 0; i < _nodeCount; i++)
        {
            _suffixEligible[i] = new long[_shardCount + 1];
        }

        for (var j = _shardCount - 1; j >= 0; j--)
        {
            var minMoved = int.MaxValue;
            foreach (var count in moved[j])
            {
                if (count < minMoved)
                {
                    minMoved = count;
                }
            }
            _suffixLoad[j] = _suffixLoad[j + 1] + (_size[j] * _replicas[j]);
            _suffixMinBytes[j] = _suffixMinBytes[j + 1] + (_size[j] * minMoved);
            _suffixMinReplicas[j] = _suffixMinReplicas[j + 1] + minMoved;
            for (var i = 0; i < _nodeCount; i++)
            {
                _suffixEligible[i][j] = _suffixEligible[i][j + 1]
                    + (_allowed[j][i] ? _size[j] : 0);
            }
        }
    }

    private void RefreshBudget(double max)
    {
        if (_budgetFor.Equals(max))
        {
            return;
        }
        _budgetFor = max;
        for (var i = 0; i < _nodeCount; i++)
        {
            var capacity = _capacity[i];
            var limit = (long)Math.Floor(max * capacity);
            if (limit > capacity)
            {
                limit = capacity;
            }
            if (limit < 0)
            {
                limit = 0;
            }
            while (limit > 0 && (double)limit / capacity > max)
            {
                limit--;
            }
            while (limit < capacity && (double)(limit + 1) / capacity <= max)
            {
                limit++;
            }
            _searchBudget[i] = limit;
        }
    }

    private void Explore(
        int depth,
        long placed,
        long bytes,
        int replicas,
        int[][][] candidates,
        int[][] moved,
        bool strict)
    {
        if (_searchExhausted)
        {
            return;
        }
        if (++_searchSteps > ExactBudget)
        {
            _searchExhausted = true;
            return;
        }

        if (depth == _shardCount)
        {
            var objective = Measure(_searchLoads, bytes, replicas);
            var order = objective.CompareTo(_searchObjective);
            if (order < 0 || (!strict && order == 0))
            {
                _searchObjective = objective;
                _searchBest = new int[_shardCount][];
                for (var j = 0; j < _shardCount; j++)
                {
                    _searchBest[j] = _searchAssignment[j];
                }
                if (!strict)
                {
                    _searchExhausted = true;
                }
            }
            return;
        }

        if (Prune(depth, placed, bytes, replicas, strict))
        {
            return;
        }

        var options = candidates[depth];
        var counts = moved[depth];
        var size = _size[depth];
        for (var c = 0; c < options.Length; c++)
        {
            var option = options[c];
            var fits = true;
            foreach (var node in option)
            {
                if (_searchLoads[node] + size > _capacity[node])
                {
                    fits = false;
                    break;
                }
            }
            if (!fits)
            {
                continue;
            }

            foreach (var node in option)
            {
                _searchLoads[node] += size;
            }
            _searchAssignment[depth] = option;
            Explore(
                depth + 1,
                placed + (size * option.Length),
                bytes + (size * counts[c]),
                replicas + counts[c],
                candidates,
                moved,
                strict);
            foreach (var node in option)
            {
                _searchLoads[node] -= size;
            }
            if (_searchExhausted)
            {
                return;
            }
        }
    }

    private bool Prune(
        int depth,
        long placed,
        long bytes,
        int replicas,
        bool strict)
    {
        var remaining = _suffixLoad[depth];
        double partialMax = 0;
        for (var i = 0; i < _nodeCount; i++)
        {
            var utilization = (double)_searchLoads[i] / _capacity[i];
            if (utilization > partialMax)
            {
                partialMax = utilization;
            }
        }

        var average = _totalCapacity > 0
            ? (double)(placed + remaining) / _totalCapacity
            : 0;
        var lowerMax = Math.Max(partialMax, average);
        var target = _searchObjective;
        var order = lowerMax.CompareTo(target.Max);
        if (order > 0)
        {
            return true;
        }

        RefreshBudget(target.Max);
        long available = 0;
        var minUpper = double.MaxValue;
        for (var i = 0; i < _nodeCount; i++)
        {
            var room = _searchBudget[i] - _searchLoads[i];
            if (room < 0)
            {
                return true;
            }
            var reachable = Math.Min(room, _suffixEligible[i][depth]);
            available += reachable;
            var bound = (double)(_searchLoads[i] + reachable) / _capacity[i];
            if (bound < minUpper)
            {
                minUpper = bound;
            }
        }
        if (available < remaining)
        {
            return true;
        }

        if (order < 0 && partialMax < target.Max && average < target.Max)
        {
            return false;
        }

        if (average < minUpper)
        {
            minUpper = average;
        }
        var lowerSpread = target.Max - minUpper;
        order = lowerSpread.CompareTo(target.Spread);
        if (order > 0)
        {
            return true;
        }
        if (order < 0)
        {
            return false;
        }

        var lowerBytes = bytes + _suffixMinBytes[depth];
        if (lowerBytes > target.Bytes)
        {
            return true;
        }
        if (lowerBytes < target.Bytes)
        {
            return false;
        }

        var lowerReplicas = replicas + _suffixMinReplicas[depth];
        if (lowerReplicas > target.Replicas)
        {
            return true;
        }
        return strict && lowerReplicas == target.Replicas;
    }
}
