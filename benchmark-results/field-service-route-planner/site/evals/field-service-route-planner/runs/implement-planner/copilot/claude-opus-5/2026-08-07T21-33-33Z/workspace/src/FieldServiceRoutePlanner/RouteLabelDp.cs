namespace FieldServiceRoutePlanner;

/// <summary>
/// Exact single-technician sequencing over a fixed candidate job set.
/// Labels are (mask, last job) states carrying Pareto optimal
/// (service end time, accumulated travel) pairs, so waiting, asymmetric
/// travel and time windows are all handled without heuristics.
/// </summary>
internal sealed class RouteLabelDp
{
    private const int Inf = PlanningModel.Inf;

    private readonly PlanningModel _model;
    private readonly int _technician;
    private readonly int[] _jobs;
    private readonly int _size;
    private readonly int[] _head;
    private readonly int _budget;

    private int[] _time;
    private int[] _travel;
    private int[] _parent;
    private int[] _next;
    private int[] _last;
    private bool[] _dead;
    private int _count;
    private bool _overflow;

    public RouteLabelDp(
        PlanningModel model,
        int technician,
        int[] jobs,
        int labelBudget = 2_000_000)
    {
        _model = model;
        _technician = technician;
        _jobs = jobs;
        _size = 1 << jobs.Length;
        _budget = labelBudget;
        _head = new int[_size * jobs.Length];
        Array.Fill(_head, -1);

        var capacity = Math.Min(Math.Max(1024, _size), labelBudget);
        _time = new int[capacity];
        _travel = new int[capacity];
        _parent = new int[capacity];
        _next = new int[capacity];
        _last = new int[capacity];
        _dead = new bool[capacity];

        BestTravel = new int[_size];
        BestLabel = new int[_size];
        Array.Fill(BestTravel, Inf);
        Array.Fill(BestLabel, -1);
        BestTravel[0] = model.Travel[0][0];
    }

    /// <summary>Minimum travel, return leg included, to serve exactly a mask.</summary>
    public int[] BestTravel { get; }

    private int[] BestLabel { get; }

    public bool Run()
    {
        var jobCount = _jobs.Length;
        var shiftStart = _model.ShiftStart[_technician];
        var shiftEnd = _model.ShiftEnd[_technician];
        var travel = _model.Travel;

        for (var job = 0; job < jobCount; job++)
        {
            var global = _jobs[job];
            var location = _model.JobLocation[global];
            var minutes = travel[0][location];
            if (minutes >= Inf)
            {
                continue;
            }

            var start = Math.Max(shiftStart + minutes, _model.WindowStart[global]);
            var end = start + _model.Duration[global];
            if (end > _model.WindowEnd[global]
                || end + _model.Shortest[location][0] > shiftEnd)
            {
                continue;
            }

            Add(1 << job, job, end, minutes, -1);
        }

        for (var mask = 1; mask < _size && !_overflow; mask++)
        {
            for (var last = 0; last < jobCount; last++)
            {
                if ((mask & (1 << last)) == 0)
                {
                    continue;
                }

                var fromLocation = _model.JobLocation[_jobs[last]];
                for (var label = _head[(mask * jobCount) + last];
                    label >= 0;
                    label = _next[label])
                {
                    if (_dead[label])
                    {
                        continue;
                    }

                    var time = _time[label];
                    var used = _travel[label];
                    for (var job = 0; job < jobCount; job++)
                    {
                        if ((mask & (1 << job)) != 0)
                        {
                            continue;
                        }

                        var global = _jobs[job];
                        var location = _model.JobLocation[global];
                        var minutes = travel[fromLocation][location];
                        if (minutes >= Inf)
                        {
                            continue;
                        }

                        var start = Math.Max(
                            time + minutes,
                            _model.WindowStart[global]);
                        var end = start + _model.Duration[global];
                        if (end > _model.WindowEnd[global]
                            || end + _model.Shortest[location][0] > shiftEnd)
                        {
                            continue;
                        }

                        Add(mask | (1 << job), job, end, used + minutes, label);
                        if (_overflow)
                        {
                            return false;
                        }
                    }
                }
            }
        }

        return !_overflow;
    }

    /// <summary>Minimum travel ordering for a mask, in operational order.</summary>
    public List<int> Reconstruct(int mask)
    {
        var order = new List<int>();
        for (var label = BestLabel[mask]; label >= 0; label = _parent[label])
        {
            order.Add(_jobs[_last[label]]);
        }

        order.Reverse();
        return order;
    }

    private void Add(int mask, int last, int time, int travelUsed, int parent)
    {
        var slot = (mask * _jobs.Length) + last;
        for (var label = _head[slot]; label >= 0; label = _next[label])
        {
            if (!_dead[label]
                && _time[label] <= time
                && _travel[label] <= travelUsed)
            {
                return;
            }
        }

        if (_count == _time.Length && !Grow())
        {
            _overflow = true;
            return;
        }

        var id = _count++;
        _time[id] = time;
        _travel[id] = travelUsed;
        _parent[id] = parent;
        _last[id] = last;
        _dead[id] = false;
        _next[id] = _head[slot];
        _head[slot] = id;

        for (var label = _next[id]; label >= 0; label = _next[label])
        {
            if (!_dead[label]
                && time <= _time[label]
                && travelUsed <= _travel[label])
            {
                _dead[label] = true;
            }
        }

        var back = _model.Travel[_model.JobLocation[_jobs[last]]][0];
        if (back >= Inf || time + back > _model.ShiftEnd[_technician])
        {
            return;
        }

        var total = travelUsed + back;
        if (total < BestTravel[mask])
        {
            BestTravel[mask] = total;
            BestLabel[mask] = id;
        }
    }

    private bool Grow()
    {
        if (_count >= _budget)
        {
            return false;
        }

        var capacity = Math.Min(Math.Max(_count * 2, 1024), _budget);
        if (capacity <= _count)
        {
            return false;
        }

        Array.Resize(ref _time, capacity);
        Array.Resize(ref _travel, capacity);
        Array.Resize(ref _parent, capacity);
        Array.Resize(ref _next, capacity);
        Array.Resize(ref _last, capacity);
        Array.Resize(ref _dead, capacity);
        return true;
    }
}
