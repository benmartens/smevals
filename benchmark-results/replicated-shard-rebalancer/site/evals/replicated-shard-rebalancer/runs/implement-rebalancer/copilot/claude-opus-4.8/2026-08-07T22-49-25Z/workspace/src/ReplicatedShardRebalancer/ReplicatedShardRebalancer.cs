using System.Numerics;

namespace ReplicatedShardRebalancer;

public sealed class ReplicatedShardRebalancer
{
    public RebalanceResult Rebalance(RebalanceProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new Solver(problem).Solve();
    }

    private sealed class Combo
    {
        public int[] NodeIndices = [];   // ascending by node id ordinal
        public string[] NodeIds = [];    // ascending by ordinal
        public int MovedReplicas;
        public long MovedBytes;
    }

    private sealed class Slot
    {
        public int ShardIndex;
        public long Size;
        public List<Combo> Combos = [];
    }

    private sealed class Candidate
    {
        public BigInteger MaxNum;
        public BigInteger MaxDen;
        public BigInteger SpreadNum;
        public BigInteger SpreadDen;
        public long MovedBytes;
        public int MovedReplicas;
        public string[][] Placement = [];   // output order (sorted shard id)
    }

    private sealed class Solver
    {
        private readonly int _n;
        private readonly string[] _nodeId;
        private readonly string[] _nodeZone;
        private readonly long[] _nodeCap;

        private readonly int _numShards;
        private readonly string[] _shardId;
        private readonly long[] _slotSizeByShard;
        private readonly int[] _outputOrder;      // shard indices sorted by id ordinal

        private readonly Slot[] _slots;           // processing order
        private readonly Combo?[] _chosenForShard;

        private readonly long[] _loads;
        private long _accMovedBytes;
        private int _accMovedReplicas;

        private readonly BigInteger _medNum;      // grand total load
        private readonly BigInteger _medDen;      // total capacity
        private readonly long[][] _suffixEligibleSize; // [slot+1][node]

        private Candidate? _best;

        public Solver(RebalanceProblem problem)
        {
            var nodes = problem.Nodes ?? [];
            _n = nodes.Count;
            _nodeId = new string[_n];
            _nodeZone = new string[_n];
            _nodeCap = new long[_n];
            var nodeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < _n; i++)
            {
                _nodeId[i] = nodes[i].Id;
                _nodeZone[i] = nodes[i].Zone;
                _nodeCap[i] = nodes[i].Capacity;
                nodeIndex[nodes[i].Id] = i;
            }

            var shards = problem.Shards ?? [];
            _numShards = shards.Count;
            _shardId = new string[_numShards];
            _slotSizeByShard = new long[_numShards];
            var shardRf = new int[_numShards];
            for (var s = 0; s < _numShards; s++)
            {
                _shardId[s] = shards[s].Id;
                _slotSizeByShard[s] = shards[s].Size;
                shardRf[s] = shards[s].ReplicationFactor;
            }

            var exclusions = new HashSet<(string, string)>();
            foreach (var ex in problem.Exclusions ?? [])
            {
                exclusions.Add((ex.ShardId, ex.NodeId));
            }

            var currentByShard = new HashSet<string>[_numShards];
            for (var s = 0; s < _numShards; s++)
            {
                currentByShard[s] = new HashSet<string>(StringComparer.Ordinal);
            }
            foreach (var placement in problem.CurrentPlacements ?? [])
            {
                var si = Array.IndexOf(_shardId, placement.ShardId);
                if (si >= 0 && placement.NodeIds is not null)
                {
                    foreach (var id in placement.NodeIds)
                    {
                        currentByShard[si].Add(id);
                    }
                }
            }

            _outputOrder = Enumerable.Range(0, _numShards)
                .OrderBy(s => _shardId[s], StringComparer.Ordinal)
                .ToArray();

            var combosByShard = new List<Combo>[_numShards];
            for (var s = 0; s < _numShards; s++)
            {
                combosByShard[s] = BuildCombos(
                    s, _slotSizeByShard[s], shardRf[s], exclusions,
                    currentByShard[s]);
            }

            // Processing order: largest shards first (tighter pruning), then id.
            var procOrder = Enumerable.Range(0, _numShards)
                .OrderByDescending(s => _slotSizeByShard[s])
                .ThenBy(s => _shardId[s], StringComparer.Ordinal)
                .ToArray();

            _slots = new Slot[_numShards];
            for (var d = 0; d < _numShards; d++)
            {
                var s = procOrder[d];
                _slots[d] = new Slot
                {
                    ShardIndex = s,
                    Size = _slotSizeByShard[s],
                    Combos = combosByShard[s],
                };
            }

            _chosenForShard = new Combo?[_numShards];
            _loads = new long[_n];

            BigInteger grandLoad = 0;
            for (var s = 0; s < _numShards; s++)
            {
                grandLoad += (BigInteger)_slotSizeByShard[s] * shardRf[s];
            }
            BigInteger totalCap = 0;
            for (var i = 0; i < _n; i++)
            {
                totalCap += _nodeCap[i];
            }
            _medNum = grandLoad;
            _medDen = totalCap > 0 ? totalCap : 1;

            // Suffix sums: max size addable to each node from remaining slots.
            _suffixEligibleSize = new long[_numShards + 1][];
            _suffixEligibleSize[_numShards] = new long[_n];
            for (var d = _numShards - 1; d >= 0; d--)
            {
                var row = (long[])_suffixEligibleSize[d + 1].Clone();
                var eligible = new bool[_n];
                foreach (var combo in _slots[d].Combos)
                {
                    foreach (var idx in combo.NodeIndices)
                    {
                        eligible[idx] = true;
                    }
                }
                for (var i = 0; i < _n; i++)
                {
                    if (eligible[i])
                    {
                        row[i] += _slots[d].Size;
                    }
                }
                _suffixEligibleSize[d] = row;
            }
        }

        private List<Combo> BuildCombos(
            int shardIdx,
            long size,
            int rf,
            HashSet<(string, string)> exclusions,
            HashSet<string> current)
        {
            var shard = _shardId[shardIdx];
            var eligible = new List<int>();
            for (var i = 0; i < _n; i++)
            {
                if (_nodeCap[i] >= size && !exclusions.Contains((shard, _nodeId[i])))
                {
                    eligible.Add(i);
                }
            }
            eligible.Sort((a, b) => string.CompareOrdinal(_nodeId[a], _nodeId[b]));

            var distinctZones = eligible
                .Select(i => _nodeZone[i])
                .Distinct(StringComparer.Ordinal)
                .Count();
            var requiredZones = Math.Min(rf, distinctZones);

            var combos = new List<Combo>();
            var pick = new int[rf];
            void Recurse(int start, int depth)
            {
                if (depth == rf)
                {
                    var zones = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var idx in pick)
                    {
                        zones.Add(_nodeZone[idx]);
                    }
                    if (zones.Count != requiredZones)
                    {
                        return;
                    }
                    var indices = (int[])pick.Clone();
                    var ids = indices.Select(i => _nodeId[i]).ToArray();
                    var moved = ids.Count(id => !current.Contains(id));
                    combos.Add(new Combo
                    {
                        NodeIndices = indices,
                        NodeIds = ids,
                        MovedReplicas = moved,
                        MovedBytes = moved * size,
                    });
                    return;
                }
                for (var i = start; i < eligible.Count; i++)
                {
                    pick[depth] = eligible[i];
                    Recurse(i + 1, depth + 1);
                }
            }
            if (rf > 0 && rf <= eligible.Count)
            {
                Recurse(0, 0);
            }
            return combos;
        }

        public RebalanceResult Solve()
        {
            if (_numShards == 0)
            {
                return new RebalanceResult([]);
            }

            GreedyWarmStart();
            Dfs(0);

            if (_best is null)
            {
                return RebalanceResult.Empty;
            }

            var placements = new List<ShardPlacement>(_numShards);
            for (var k = 0; k < _outputOrder.Length; k++)
            {
                var s = _outputOrder[k];
                placements.Add(new ShardPlacement(
                    _shardId[s],
                    _best.Placement[k].ToList()));
            }
            return new RebalanceResult(placements);
        }

        private void GreedyWarmStart()
        {
            var loads = new long[_n];
            var chosen = new Combo?[_numShards];
            for (var d = 0; d < _numShards; d++)
            {
                Combo? bestCombo = null;
                BigInteger bMaxNum = 0, bMaxDen = 1;
                long bBytes = 0;
                var bReps = 0;
                foreach (var combo in _slots[d].Combos)
                {
                    var feasible = true;
                    foreach (var idx in combo.NodeIndices)
                    {
                        if (loads[idx] + _slots[d].Size > _nodeCap[idx])
                        {
                            feasible = false;
                            break;
                        }
                    }
                    if (!feasible)
                    {
                        continue;
                    }
                    foreach (var idx in combo.NodeIndices)
                    {
                        loads[idx] += _slots[d].Size;
                    }
                    ComputeMax(loads, out var mNum, out var mDen);
                    foreach (var idx in combo.NodeIndices)
                    {
                        loads[idx] -= _slots[d].Size;
                    }

                    var take = bestCombo is null;
                    if (!take)
                    {
                        var c = CompareRatio(mNum, mDen, bMaxNum, bMaxDen);
                        if (c < 0)
                        {
                            take = true;
                        }
                        else if (c == 0)
                        {
                            if (combo.MovedBytes < bBytes)
                            {
                                take = true;
                            }
                            else if (combo.MovedBytes == bBytes
                                && combo.MovedReplicas < bReps)
                            {
                                take = true;
                            }
                        }
                    }
                    if (take)
                    {
                        bestCombo = combo;
                        bMaxNum = mNum;
                        bMaxDen = mDen;
                        bBytes = combo.MovedBytes;
                        bReps = combo.MovedReplicas;
                    }
                }
                if (bestCombo is null)
                {
                    return; // dead-end; leave incumbent unset
                }
                chosen[_slots[d].ShardIndex] = bestCombo;
                foreach (var idx in bestCombo.NodeIndices)
                {
                    loads[idx] += _slots[d].Size;
                }
            }
            _best = EvaluateFromChosen(chosen);
        }

        private void Dfs(int depth)
        {
            if (depth == _numShards)
            {
                EvaluateLeaf();
                return;
            }

            var slot = _slots[depth];
            foreach (var combo in slot.Combos)
            {
                var feasible = true;
                foreach (var idx in combo.NodeIndices)
                {
                    if (_loads[idx] + slot.Size > _nodeCap[idx])
                    {
                        feasible = false;
                        break;
                    }
                }
                if (!feasible)
                {
                    continue;
                }

                foreach (var idx in combo.NodeIndices)
                {
                    _loads[idx] += slot.Size;
                }
                _accMovedBytes += combo.MovedBytes;
                _accMovedReplicas += combo.MovedReplicas;
                _chosenForShard[slot.ShardIndex] = combo;

                if (!Prune(depth))
                {
                    Dfs(depth + 1);
                }

                foreach (var idx in combo.NodeIndices)
                {
                    _loads[idx] -= slot.Size;
                }
                _accMovedBytes -= combo.MovedBytes;
                _accMovedReplicas -= combo.MovedReplicas;
            }
        }

        private bool Prune(int depth)
        {
            if (_best is null)
            {
                return false;
            }

            ComputeMax(_loads, out var pMaxNum, out var pMaxDen);
            // LB on final max utilization.
            BigInteger lbMaxNum, lbMaxDen;
            if (CompareRatio(pMaxNum, pMaxDen, _medNum, _medDen) >= 0)
            {
                lbMaxNum = pMaxNum;
                lbMaxDen = pMaxDen;
            }
            else
            {
                lbMaxNum = _medNum;
                lbMaxDen = _medDen;
            }

            // UB on final minimum utilization (allows most remaining load).
            var add = _suffixEligibleSize[depth + 1];
            BigInteger ubMinNum = 0, ubMinDen = 1;
            var first = true;
            for (var i = 0; i < _n; i++)
            {
                BigInteger num, den;
                if (_nodeCap[i] > 0)
                {
                    num = _loads[i] + add[i];
                    den = _nodeCap[i];
                }
                else
                {
                    num = 0;
                    den = 1;
                }
                if (first || CompareRatio(num, den, ubMinNum, ubMinDen) < 0)
                {
                    ubMinNum = num;
                    ubMinDen = den;
                    first = false;
                }
            }

            // LB on spread = LB(max) - UB(min), clamped at 0.
            var spNum = lbMaxNum * ubMinDen - ubMinNum * lbMaxDen;
            var spDen = lbMaxDen * ubMinDen;
            if (spNum < 0)
            {
                spNum = 0;
                spDen = 1;
            }

            return CompareFirst4(
                lbMaxNum, lbMaxDen, spNum, spDen,
                _accMovedBytes, _accMovedReplicas, _best) > 0;
        }

        private void EvaluateLeaf()
        {
            ComputeMaxMin(
                _loads,
                out var maxNum, out var maxDen,
                out var minNum, out var minDen);
            var spNum = maxNum * minDen - minNum * maxDen;
            var spDen = maxDen * minDen;

            if (_best is not null
                && CompareFirst4(
                    maxNum, maxDen, spNum, spDen,
                    _accMovedBytes, _accMovedReplicas, _best) > 0)
            {
                return;
            }

            var placement = new string[_numShards][];
            for (var k = 0; k < _outputOrder.Length; k++)
            {
                placement[k] = _chosenForShard[_outputOrder[k]]!.NodeIds;
            }

            if (_best is null
                || CompareFull(
                    maxNum, maxDen, spNum, spDen,
                    _accMovedBytes, _accMovedReplicas, placement, _best) < 0)
            {
                _best = new Candidate
                {
                    MaxNum = maxNum,
                    MaxDen = maxDen,
                    SpreadNum = spNum,
                    SpreadDen = spDen,
                    MovedBytes = _accMovedBytes,
                    MovedReplicas = _accMovedReplicas,
                    Placement = placement,
                };
            }
        }

        private Candidate EvaluateFromChosen(Combo?[] chosen)
        {
            var loads = new long[_n];
            long bytes = 0;
            var reps = 0;
            for (var s = 0; s < _numShards; s++)
            {
                var combo = chosen[s]!;
                foreach (var idx in combo.NodeIndices)
                {
                    loads[idx] += _slotSizeByShard[s];
                }
                bytes += combo.MovedBytes;
                reps += combo.MovedReplicas;
            }
            ComputeMaxMin(
                loads,
                out var maxNum, out var maxDen,
                out var minNum, out var minDen);
            var spNum = maxNum * minDen - minNum * maxDen;
            var spDen = maxDen * minDen;
            var placement = new string[_numShards][];
            for (var k = 0; k < _outputOrder.Length; k++)
            {
                placement[k] = chosen[_outputOrder[k]]!.NodeIds;
            }
            return new Candidate
            {
                MaxNum = maxNum,
                MaxDen = maxDen,
                SpreadNum = spNum,
                SpreadDen = spDen,
                MovedBytes = bytes,
                MovedReplicas = reps,
                Placement = placement,
            };
        }

        private void ComputeMax(long[] loads, out BigInteger num, out BigInteger den)
        {
            num = 0;
            den = 1;
            var first = true;
            for (var i = 0; i < _n; i++)
            {
                BigInteger cn, cd;
                if (_nodeCap[i] > 0)
                {
                    cn = loads[i];
                    cd = _nodeCap[i];
                }
                else
                {
                    cn = 0;
                    cd = 1;
                }
                if (first || CompareRatio(cn, cd, num, den) > 0)
                {
                    num = cn;
                    den = cd;
                    first = false;
                }
            }
        }

        private void ComputeMaxMin(
            long[] loads,
            out BigInteger maxNum, out BigInteger maxDen,
            out BigInteger minNum, out BigInteger minDen)
        {
            maxNum = 0;
            maxDen = 1;
            minNum = 0;
            minDen = 1;
            var first = true;
            for (var i = 0; i < _n; i++)
            {
                BigInteger cn, cd;
                if (_nodeCap[i] > 0)
                {
                    cn = loads[i];
                    cd = _nodeCap[i];
                }
                else
                {
                    cn = 0;
                    cd = 1;
                }
                if (first)
                {
                    maxNum = cn;
                    maxDen = cd;
                    minNum = cn;
                    minDen = cd;
                    first = false;
                    continue;
                }
                if (CompareRatio(cn, cd, maxNum, maxDen) > 0)
                {
                    maxNum = cn;
                    maxDen = cd;
                }
                if (CompareRatio(cn, cd, minNum, minDen) < 0)
                {
                    minNum = cn;
                    minDen = cd;
                }
            }
        }

        private static int CompareRatio(
            BigInteger aNum, BigInteger aDen, BigInteger bNum, BigInteger bDen)
        {
            // Denominators are always positive.
            return (aNum * bDen).CompareTo(bNum * aDen);
        }

        private static int CompareFirst4(
            BigInteger maxNum, BigInteger maxDen,
            BigInteger spNum, BigInteger spDen,
            long movedBytes, int movedReplicas,
            Candidate other)
        {
            var c = CompareRatio(maxNum, maxDen, other.MaxNum, other.MaxDen);
            if (c != 0)
            {
                return c;
            }
            c = CompareRatio(spNum, spDen, other.SpreadNum, other.SpreadDen);
            if (c != 0)
            {
                return c;
            }
            c = movedBytes.CompareTo(other.MovedBytes);
            if (c != 0)
            {
                return c;
            }
            return movedReplicas.CompareTo(other.MovedReplicas);
        }

        private int CompareFull(
            BigInteger maxNum, BigInteger maxDen,
            BigInteger spNum, BigInteger spDen,
            long movedBytes, int movedReplicas,
            string[][] placement,
            Candidate other)
        {
            var c = CompareFirst4(
                maxNum, maxDen, spNum, spDen,
                movedBytes, movedReplicas, other);
            if (c != 0)
            {
                return c;
            }
            for (var k = 0; k < placement.Length; k++)
            {
                var a = placement[k];
                var b = other.Placement[k];
                for (var j = 0; j < a.Length; j++)
                {
                    var cc = string.CompareOrdinal(a[j], b[j]);
                    if (cc != 0)
                    {
                        return cc;
                    }
                }
            }
            return 0;
        }
    }
}
