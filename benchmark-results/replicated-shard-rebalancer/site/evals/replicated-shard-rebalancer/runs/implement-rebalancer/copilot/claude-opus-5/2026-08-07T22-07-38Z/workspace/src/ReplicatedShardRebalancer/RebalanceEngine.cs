namespace ReplicatedShardRebalancer;

/// <summary>
/// Deterministic lexicographic optimiser for <see cref="RebalanceProblem"/>.
/// </summary>
/// <remarks>
/// The engine minimises, in order, maximum node utilisation, utilisation
/// spread, moved bytes, moved replica count and finally the ordinal ranking of
/// the complete placement. Each objective is resolved by its own bounded
/// branch-and-bound pass so later passes search a heavily constrained space.
/// </remarks>
internal sealed class RebalanceEngine
{
    private const int MaxCandidatesPerShard = 200_000;
    private const long PhaseBudget = 4_000_000L;

    private readonly NodeSpec[] _nodes;
    private readonly ShardSpec[] _shards;
    private readonly int _n;
    private readonly int _s;
    private readonly long[] _cap;
    private readonly int[] _zone;
    private readonly int _zoneCount;
    private readonly long[] _size;
    private readonly int[] _rf;
    private readonly int[] _reqZones;
    private readonly bool[][] _allowed;
    private readonly int[][] _fallback;
    private readonly int[][][] _cand;
    private readonly int[][] _candMove;
    private readonly long[] _minMoveBytes;
    private readonly int[] _minMoveReps;
    private readonly bool _searchable;

    private long[] _load = [];
    private long[] _hi = [];
    private long[] _lo = [];
    private bool _hasLo;
    private int[] _pick = [];
    private int[] _bestPick = [];
    private int[] _activeOrder = [];
    private long[] _remaining = [];
    private long[] _sufBytes = [];
    private int[] _sufReps = [];
    private int[] _zoneStamp = [];
    private int _stamp;
    private long _budget;
    private long _boundVersion;
    private long _bestNum;
    private long _bestDen = 1;
    private long _bestBytes;
    private int _bestReps;
    private bool _hasIncumbent;
    private bool _exactHit;

    internal RebalanceEngine(RebalanceProblem problem)
    {
        var nodeMap = new Dictionary<string, NodeSpec>(StringComparer.Ordinal);
        foreach (var node in problem.Nodes ?? [])
        {
            if (node is null
                || string.IsNullOrWhiteSpace(node.Id)
                || string.IsNullOrWhiteSpace(node.Zone)
                || node.Capacity <= 0)
            {
                continue;
            }
            nodeMap.TryAdd(node.Id, node);
        }

        var shardMap = new Dictionary<string, ShardSpec>(StringComparer.Ordinal);
        foreach (var shard in problem.Shards ?? [])
        {
            if (shard is null
                || string.IsNullOrWhiteSpace(shard.Id)
                || shard.Size <= 0
                || shard.ReplicationFactor <= 0)
            {
                continue;
            }
            shardMap.TryAdd(shard.Id, shard);
        }

        _nodes = [.. nodeMap.Values.OrderBy(node => node.Id, StringComparer.Ordinal)];
        _shards = [.. shardMap.Values.OrderBy(shard => shard.Id, StringComparer.Ordinal)];
        _n = _nodes.Length;
        _s = _shards.Length;

        var nodeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _n; i++)
        {
            nodeIndex[_nodes[i].Id] = i;
        }

        var zoneIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        _cap = new long[_n];
        _zone = new int[_n];
        for (var i = 0; i < _n; i++)
        {
            _cap[i] = _nodes[i].Capacity;
            if (!zoneIndex.TryGetValue(_nodes[i].Zone, out var z))
            {
                z = zoneIndex.Count;
                zoneIndex[_nodes[i].Zone] = z;
            }
            _zone[i] = z;
        }
        _zoneCount = Math.Max(1, zoneIndex.Count);
        _zoneStamp = new int[_zoneCount];

        var excluded = new HashSet<(string, string)>();
        foreach (var exclusion in problem.Exclusions ?? [])
        {
            if (exclusion is not null)
            {
                excluded.Add((exclusion.ShardId, exclusion.NodeId));
            }
        }

        var currentByShard = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var placement in problem.CurrentPlacements ?? [])
        {
            if (placement is null || currentByShard.ContainsKey(placement.ShardId))
            {
                continue;
            }
            currentByShard[placement.ShardId] = new HashSet<string>(
                placement.NodeIds ?? [],
                StringComparer.Ordinal);
        }

        _size = new long[_s];
        _rf = new int[_s];
        _reqZones = new int[_s];
        _allowed = new bool[_s][];
        _fallback = new int[_s][];
        _cand = new int[_s][][];
        _candMove = new int[_s][];
        _minMoveBytes = new long[_s];
        _minMoveReps = new int[_s];
        var current = new bool[_s][];
        var searchable = _s > 0 && _n > 0;

        for (var s = 0; s < _s; s++)
        {
            var shard = _shards[s];
            _size[s] = shard.Size;
            _rf[s] = shard.ReplicationFactor;
            _allowed[s] = new bool[_n];
            current[s] = new bool[_n];
            var currentNodes = currentByShard.GetValueOrDefault(shard.Id, []);
            foreach (var nodeId in currentNodes)
            {
                if (nodeIndex.TryGetValue(nodeId, out var idx))
                {
                    current[s][idx] = true;
                }
            }

            var pool = new List<int>();
            var zonesSeen = new HashSet<int>();
            for (var i = 0; i < _n; i++)
            {
                if (_cap[i] >= shard.Size && !excluded.Contains((shard.Id, _nodes[i].Id)))
                {
                    _allowed[s][i] = true;
                    pool.Add(i);
                    zonesSeen.Add(_zone[i]);
                }
            }
            _reqZones[s] = Math.Min(_rf[s], zonesSeen.Count);
            _fallback[s] = BuildFallback(s, excluded);

            if (pool.Count < _rf[s])
            {
                searchable = false;
                _cand[s] = [];
                _candMove[s] = [];
                continue;
            }

            var reduced = ReducePool(pool, _rf[s], current[s]);
            var sets = BuildCandidates(s, reduced);
            if (sets.Length == 0)
            {
                searchable = false;
            }
            _cand[s] = sets;
            _candMove[s] = new int[sets.Length];
            var bestOverlap = -1;
            for (var k = 0; k < sets.Length; k++)
            {
                var overlap = 0;
                foreach (var node in sets[k])
                {
                    if (current[s][node])
                    {
                        overlap++;
                    }
                }
                _candMove[s][k] = _rf[s] - overlap;
                bestOverlap = Math.Max(bestOverlap, overlap);
            }
            _minMoveReps[s] = sets.Length == 0 ? 0 : _rf[s] - bestOverlap;
            _minMoveBytes[s] = _minMoveReps[s] * _size[s];
        }

        _searchable = searchable;
    }

    internal RebalanceResult Solve()
    {
        if (_s == 0)
        {
            return RebalanceResult.Empty;
        }
        if (!_searchable)
        {
            return BuildResult(null);
        }

        _load = new long[_n];
        _hi = new long[_n];
        _lo = new long[_n];
        _pick = new int[_s];
        _bestPick = new int[_s];

        var greedy = GreedyThenImprove();
        _hasIncumbent = greedy is not null;
        if (greedy is not null)
        {
            Array.Copy(greedy, _bestPick, _s);
        }

        SolveMaximumUtilisation();
        if (!_hasIncumbent)
        {
            return BuildResult(null);
        }

        SolveUtilisationSpread();
        SolveMovement();
        return BuildResult(_bestPick);
    }

    // ---------------------------------------------------------------- phase 1

    private void SolveMaximumUtilisation()
    {
        _activeOrder = OrderBySizeDescending();
        BuildSuffixTables();
        _hasLo = false;
        Array.Fill(_lo, 0L);
        Array.Clear(_load);
        _budget = PhaseBudget;
        _boundVersion = 0;

        if (_hasIncumbent)
        {
            var loads = LoadsOf(_bestPick);
            (_bestNum, _bestDen) = MaxRatio(loads);
            if (!TightenUpperBounds(_bestNum, _bestDen))
            {
                Array.Copy(loads, _load, _n);
                Finalise(_bestNum, _bestDen);
                return;
            }
        }
        else
        {
            _bestNum = -1;
            _bestDen = 1;
            Array.Copy(_cap, _hi, _n);
        }

        if (Viable(0))
        {
            SearchMaximumUtilisation(0);
        }
        Finalise(_bestNum, _bestDen);
    }

    private void Finalise(long num, long den)
    {
        if (!_hasIncumbent)
        {
            return;
        }
        for (var i = 0; i < _n; i++)
        {
            _hi[i] = (long)((Int128)num * _cap[i] / den);
        }
    }

    private bool TightenUpperBounds(long num, long den)
    {
        if (num <= 0)
        {
            return false;
        }
        for (var i = 0; i < _n; i++)
        {
            _hi[i] = (long)(((Int128)num * _cap[i] - 1) / den);
        }
        _boundVersion++;
        return true;
    }

    private void SearchMaximumUtilisation(int depth)
    {
        if (--_budget <= 0)
        {
            return;
        }
        if (depth == _s)
        {
            var (num, den) = MaxRatio(_load);
            if (!_hasIncumbent || CompareRatio(num, den, _bestNum, _bestDen) < 0)
            {
                _hasIncumbent = true;
                _bestNum = num;
                _bestDen = den;
                Array.Copy(_pick, _bestPick, _s);
                TightenUpperBounds(num, den);
            }
            return;
        }

        var shard = _activeOrder[depth];
        var sets = _cand[shard];
        var version = _boundVersion;
        for (var k = 0; k < sets.Length; k++)
        {
            if (_boundVersion != version)
            {
                version = _boundVersion;
                if (!Viable(depth))
                {
                    return;
                }
            }
            if (!Fits(sets[k], shard))
            {
                continue;
            }
            Apply(sets[k], shard, k);
            if (Viable(depth + 1))
            {
                SearchMaximumUtilisation(depth + 1);
            }
            Undo(sets[k], shard);
            if (_budget <= 0)
            {
                return;
            }
        }
    }

    // ---------------------------------------------------------------- phase 2

    private void SolveUtilisationSpread()
    {
        Array.Clear(_load);
        _budget = PhaseBudget;
        _boundVersion = 0;
        _hasLo = true;

        var loads = LoadsOf(_bestPick);
        var (num, den) = MinRatio(loads);
        var bestMinNum = num;
        var bestMinDen = den;
        for (var i = 0; i < _n; i++)
        {
            _lo[i] = (long)((Int128)num * _cap[i] / den) + 1;
        }

        if (Viable(0))
        {
            SearchUtilisationSpread(0, ref bestMinNum, ref bestMinDen);
        }

        for (var i = 0; i < _n; i++)
        {
            var scaled = (Int128)bestMinNum * _cap[i];
            _lo[i] = (long)((scaled + bestMinDen - 1) / bestMinDen);
        }
    }

    private void SearchUtilisationSpread(int depth, ref long bestNum, ref long bestDen)
    {
        if (--_budget <= 0)
        {
            return;
        }
        if (depth == _s)
        {
            var (num, den) = MinRatio(_load);
            if (CompareRatio(num, den, bestNum, bestDen) > 0)
            {
                bestNum = num;
                bestDen = den;
                Array.Copy(_pick, _bestPick, _s);
                for (var i = 0; i < _n; i++)
                {
                    _lo[i] = (long)((Int128)num * _cap[i] / den) + 1;
                }
                _boundVersion++;
            }
            return;
        }

        var shard = _activeOrder[depth];
        var sets = _cand[shard];
        var version = _boundVersion;
        for (var k = 0; k < sets.Length; k++)
        {
            if (_boundVersion != version)
            {
                version = _boundVersion;
                if (!Viable(depth))
                {
                    return;
                }
            }
            if (!Fits(sets[k], shard))
            {
                continue;
            }
            Apply(sets[k], shard, k);
            if (Viable(depth + 1))
            {
                SearchUtilisationSpread(depth + 1, ref bestNum, ref bestDen);
            }
            Undo(sets[k], shard);
            if (_budget <= 0)
            {
                return;
            }
        }
    }

    // ------------------------------------------------------------- phases 3-5

    private void SolveMovement()
    {
        _activeOrder = new int[_s];
        for (var i = 0; i < _s; i++)
        {
            _activeOrder[i] = i;
        }
        BuildSuffixTables();

        (_bestBytes, _bestReps) = MovementOf(_bestPick);
        Array.Clear(_load);
        _budget = PhaseBudget;
        _boundVersion = 0;
        if (Viable(0))
        {
            SearchMovement(0, 0, 0);
        }

        Array.Clear(_load);
        _budget = PhaseBudget;
        _exactHit = false;
        if (Viable(0))
        {
            SearchOrdinal(0, 0, 0);
        }
    }

    private void SearchMovement(int depth, long bytes, int reps)
    {
        if (--_budget <= 0)
        {
            return;
        }
        if (depth == _s)
        {
            if (!MeetsLowerBounds())
            {
                return;
            }
            if (bytes < _bestBytes || (bytes == _bestBytes && reps < _bestReps))
            {
                _bestBytes = bytes;
                _bestReps = reps;
                Array.Copy(_pick, _bestPick, _s);
            }
            return;
        }

        var shard = _activeOrder[depth];
        var sets = _cand[shard];
        for (var k = 0; k < sets.Length; k++)
        {
            var moves = _candMove[shard][k];
            var bytesNext = bytes + moves * _size[shard];
            var repsNext = reps + moves;
            var boundBytes = bytesNext + _sufBytes[depth + 1];
            var boundReps = repsNext + _sufReps[depth + 1];
            if (boundBytes > _bestBytes
                || (boundBytes == _bestBytes && boundReps >= _bestReps))
            {
                continue;
            }
            if (!Fits(sets[k], shard))
            {
                continue;
            }
            Apply(sets[k], shard, k);
            if (Viable(depth + 1))
            {
                SearchMovement(depth + 1, bytesNext, repsNext);
            }
            Undo(sets[k], shard);
            if (_budget <= 0)
            {
                return;
            }
        }
    }

    private void SearchOrdinal(int depth, long bytes, int reps)
    {
        if (--_budget <= 0 || _exactHit)
        {
            return;
        }
        if (depth == _s)
        {
            if (bytes == _bestBytes && reps == _bestReps && MeetsLowerBounds())
            {
                Array.Copy(_pick, _bestPick, _s);
                _exactHit = true;
            }
            return;
        }

        var shard = _activeOrder[depth];
        var sets = _cand[shard];
        for (var k = 0; k < sets.Length; k++)
        {
            var moves = _candMove[shard][k];
            var bytesNext = bytes + moves * _size[shard];
            var repsNext = reps + moves;
            if (bytesNext + _sufBytes[depth + 1] > _bestBytes
                || repsNext + _sufReps[depth + 1] > _bestReps)
            {
                continue;
            }
            if (!Fits(sets[k], shard))
            {
                continue;
            }
            Apply(sets[k], shard, k);
            if (Viable(depth + 1))
            {
                SearchOrdinal(depth + 1, bytesNext, repsNext);
            }
            Undo(sets[k], shard);
            if (_exactHit || _budget <= 0)
            {
                return;
            }
        }
    }

    // ------------------------------------------------------------- heuristics

    private int[]? GreedyThenImprove()
    {
        var pick = new int[_s];
        var loads = new long[_n];
        foreach (var shard in OrderBySizeDescending())
        {
            var sets = _cand[shard];
            if (sets.Length == 0)
            {
                return null;
            }
            var chosen = -1;
            var bestPeak = double.MaxValue;
            var bestTotal = double.MaxValue;
            for (var k = 0; k < sets.Length; k++)
            {
                double peak = 0;
                double total = 0;
                foreach (var node in sets[k])
                {
                    var ratio = (double)(loads[node] + _size[shard]) / _cap[node];
                    peak = Math.Max(peak, ratio);
                    total += ratio;
                }
                if (peak < bestPeak - 1e-12
                    || (peak <= bestPeak + 1e-12 && total < bestTotal - 1e-12))
                {
                    bestPeak = peak;
                    bestTotal = total;
                    chosen = k;
                }
            }
            pick[shard] = chosen;
            foreach (var node in sets[chosen])
            {
                loads[node] += _size[shard];
            }
        }

        var work = 0L;
        for (var pass = 0; pass < 32 && work < 4_000_000L; pass++)
        {
            var improved = false;
            for (var shard = 0; shard < _s; shard++)
            {
                var sets = _cand[shard];
                var currentPick = pick[shard];
                var chosen = currentPick;
                foreach (var node in sets[currentPick])
                {
                    loads[node] -= _size[shard];
                }
                var baseline = Score(loads, pick, shard, currentPick);
                for (var k = 0; k < sets.Length; k++)
                {
                    work++;
                    if (k == currentPick)
                    {
                        continue;
                    }
                    var trial = Score(loads, pick, shard, k);
                    if (Compare(trial, baseline) < 0)
                    {
                        baseline = trial;
                        chosen = k;
                    }
                }
                pick[shard] = chosen;
                foreach (var node in sets[chosen])
                {
                    loads[node] += _size[shard];
                }
                improved |= chosen != currentPick;
            }
            if (!improved)
            {
                break;
            }
        }

        foreach (var node in Enumerable.Range(0, _n))
        {
            if (loads[node] > _cap[node])
            {
                return null;
            }
        }
        return pick;
    }

    private readonly record struct Objective(
        long MaxNum,
        long MaxDen,
        long MinNum,
        long MinDen,
        long Bytes,
        int Reps);

    private Objective Score(long[] loadsWithoutShard, int[] pick, int shard, int candidate)
    {
        var sets = _cand[shard];
        foreach (var node in sets[candidate])
        {
            loadsWithoutShard[node] += _size[shard];
        }
        var (maxNum, maxDen) = MaxRatio(loadsWithoutShard);
        var (minNum, minDen) = MinRatio(loadsWithoutShard);
        foreach (var node in sets[candidate])
        {
            loadsWithoutShard[node] -= _size[shard];
        }

        long bytes = 0;
        var reps = 0;
        for (var i = 0; i < _s; i++)
        {
            var moves = _candMove[i][i == shard ? candidate : pick[i]];
            bytes += moves * _size[i];
            reps += moves;
        }
        return new(maxNum, maxDen, minNum, minDen, bytes, reps);
    }

    private static int Compare(Objective left, Objective right)
    {
        var cmp = CompareRatio(left.MaxNum, left.MaxDen, right.MaxNum, right.MaxDen);
        if (cmp != 0)
        {
            return cmp;
        }
        cmp = -CompareRatio(left.MinNum, left.MinDen, right.MinNum, right.MinDen);
        if (cmp != 0)
        {
            return cmp;
        }
        cmp = left.Bytes.CompareTo(right.Bytes);
        return cmp != 0 ? cmp : left.Reps.CompareTo(right.Reps);
    }

    // ------------------------------------------------------------ search core

    private bool Fits(int[] set, int shard)
    {
        foreach (var node in set)
        {
            if (_load[node] + _size[shard] > _hi[node])
            {
                return false;
            }
        }
        return true;
    }

    private void Apply(int[] set, int shard, int candidate)
    {
        foreach (var node in set)
        {
            _load[node] += _size[shard];
        }
        _pick[shard] = candidate;
    }

    private void Undo(int[] set, int shard)
    {
        foreach (var node in set)
        {
            _load[node] -= _size[shard];
        }
    }

    private bool MeetsLowerBounds()
    {
        for (var i = 0; i < _n; i++)
        {
            if (_load[i] < _lo[i])
            {
                return false;
            }
        }
        return true;
    }

    private bool Viable(int depth)
    {
        long slack = 0;
        long deficit = 0;
        for (var i = 0; i < _n; i++)
        {
            var free = _hi[i] - _load[i];
            if (free < 0)
            {
                return false;
            }
            slack += free;
            if (_hasLo && _load[i] < _lo[i])
            {
                deficit += _lo[i] - _load[i];
            }
        }
        var demand = _remaining[depth];
        if (slack < demand || (_hasLo && deficit > demand))
        {
            return false;
        }

        for (var d = depth; d < _s; d++)
        {
            var shard = _activeOrder[d];
            var allowed = _allowed[shard];
            var size = _size[shard];
            var count = 0;
            var zones = 0;
            _stamp++;
            for (var i = 0; i < _n; i++)
            {
                if (!allowed[i] || _load[i] + size > _hi[i])
                {
                    continue;
                }
                count++;
                if (_zoneStamp[_zone[i]] != _stamp)
                {
                    _zoneStamp[_zone[i]] = _stamp;
                    zones++;
                }
            }
            if (count < _rf[shard] || zones < _reqZones[shard])
            {
                return false;
            }
        }

        if (!_hasLo)
        {
            return true;
        }

        for (var i = 0; i < _n; i++)
        {
            if (_load[i] >= _lo[i])
            {
                continue;
            }
            var reachable = _load[i];
            for (var d = depth; d < _s && reachable < _lo[i]; d++)
            {
                var shard = _activeOrder[d];
                if (_allowed[shard][i])
                {
                    reachable += _size[shard];
                }
            }
            if (reachable < _lo[i])
            {
                return false;
            }
        }
        return true;
    }

    private void BuildSuffixTables()
    {
        _remaining = new long[_s + 1];
        _sufBytes = new long[_s + 1];
        _sufReps = new int[_s + 1];
        for (var d = _s - 1; d >= 0; d--)
        {
            var shard = _activeOrder[d];
            _remaining[d] = _remaining[d + 1] + _size[shard] * _rf[shard];
            _sufBytes[d] = _sufBytes[d + 1] + _minMoveBytes[shard];
            _sufReps[d] = _sufReps[d + 1] + _minMoveReps[shard];
        }
    }

    private int[] OrderBySizeDescending() =>
        [.. Enumerable.Range(0, _s)
            .OrderByDescending(shard => _size[shard] * _rf[shard])
            .ThenBy(shard => shard)];

    private long[] LoadsOf(int[] pick)
    {
        var loads = new long[_n];
        for (var shard = 0; shard < _s; shard++)
        {
            foreach (var node in _cand[shard][pick[shard]])
            {
                loads[node] += _size[shard];
            }
        }
        return loads;
    }

    private (long Bytes, int Reps) MovementOf(int[] pick)
    {
        long bytes = 0;
        var reps = 0;
        for (var shard = 0; shard < _s; shard++)
        {
            var moves = _candMove[shard][pick[shard]];
            bytes += moves * _size[shard];
            reps += moves;
        }
        return (bytes, reps);
    }

    private (long Num, long Den) MaxRatio(long[] loads)
    {
        long num = 0;
        long den = 1;
        for (var i = 0; i < _n; i++)
        {
            if (CompareRatio(loads[i], _cap[i], num, den) > 0)
            {
                num = loads[i];
                den = _cap[i];
            }
        }
        return (num, den);
    }

    private (long Num, long Den) MinRatio(long[] loads)
    {
        if (_n == 0)
        {
            return (0, 1);
        }
        var num = loads[0];
        var den = _cap[0];
        for (var i = 1; i < _n; i++)
        {
            if (CompareRatio(loads[i], _cap[i], num, den) < 0)
            {
                num = loads[i];
                den = _cap[i];
            }
        }
        return (num, den);
    }

    private static int CompareRatio(long leftNum, long leftDen, long rightNum, long rightDen)
        => ((Int128)leftNum * rightDen).CompareTo((Int128)rightNum * leftDen);

    // ------------------------------------------------------------ preparation

    private int[] BuildFallback(int shard, HashSet<(string, string)> excluded)
    {
        var ranked = Enumerable.Range(0, _n)
            .OrderBy(i => _allowed[shard][i] ? 0 : 1)
            .ThenBy(i => excluded.Contains((_shards[shard].Id, _nodes[i].Id)) ? 1 : 0)
            .ThenBy(i => i)
            .Take(Math.Min(_rf[shard], _n))
            .OrderBy(i => i);
        return [.. ranked];
    }

    private List<int> ReducePool(List<int> pool, int replicas, bool[] current)
    {
        if (CombinationCount(pool.Count, replicas) <= MaxCandidatesPerShard)
        {
            return pool;
        }

        var limit = replicas;
        while (limit < pool.Count
            && CombinationCount(limit + 1, replicas) <= MaxCandidatesPerShard)
        {
            limit++;
        }

        var keep = new SortedSet<int>();
        foreach (var node in pool.Where(node => current[node]))
        {
            keep.Add(node);
        }
        foreach (var group in pool.GroupBy(node => _zone[node]).OrderBy(group => group.Key))
        {
            keep.Add(group.OrderByDescending(node => _cap[node]).ThenBy(node => node).First());
        }
        foreach (var node in pool.OrderByDescending(node => _cap[node]).ThenBy(node => node))
        {
            if (keep.Count >= limit)
            {
                break;
            }
            keep.Add(node);
        }
        return [.. keep];
    }

    private static double CombinationCount(int total, int choose)
    {
        if (choose > total)
        {
            return 0;
        }
        double result = 1;
        for (var i = 1; i <= choose; i++)
        {
            result = result * (total - choose + i) / i;
            if (result > 1e15)
            {
                return 1e15;
            }
        }
        return result;
    }

    private int[][] BuildCandidates(int shard, List<int> pool)
    {
        var replicas = _rf[shard];
        var required = _reqZones[shard];
        var sets = new List<int[]>();
        var buffer = new int[replicas];
        var zoneUse = new int[_zoneCount];
        var distinct = 0;

        void Recurse(int start, int depth)
        {
            if (distinct + (replicas - depth) < required)
            {
                return;
            }
            if (depth == replicas)
            {
                if (distinct >= required)
                {
                    sets.Add((int[])buffer.Clone());
                }
                return;
            }
            for (var i = start; i <= pool.Count - (replicas - depth); i++)
            {
                var node = pool[i];
                buffer[depth] = node;
                if (zoneUse[_zone[node]]++ == 0)
                {
                    distinct++;
                }
                Recurse(i + 1, depth + 1);
                if (--zoneUse[_zone[node]] == 0)
                {
                    distinct--;
                }
            }
        }

        Recurse(0, 0);
        return [.. sets];
    }

    private RebalanceResult BuildResult(int[]? pick)
    {
        var placements = new List<ShardPlacement>(_s);
        for (var shard = 0; shard < _s; shard++)
        {
            var nodes = pick is not null && _cand[shard].Length > 0
                ? _cand[shard][pick[shard]]
                : _fallback[shard];
            placements.Add(new(
                _shards[shard].Id,
                [.. nodes.Select(node => _nodes[node].Id)]));
        }
        return new(placements);
    }
}
