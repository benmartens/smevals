using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace FieldServiceRoutePlanner;

/// <summary>
/// Produces a deterministic plan that lexicographically maximizes served value
/// and then minimizes total directed travel. Small instances are solved exactly
/// (per technician label DP plus a set partition DP); larger instances use a
/// deterministic greedy construction with local search.
/// </summary>
internal sealed class PlanningSolver
{
    private const int Inf = PlanningModel.Inf;
    private const int ExactJobLimit = 16;
    private const int SingleTechnicianJobLimit = 18;
    private const double ExactWorkLimit = 3.0e8;
    private const int ReorderJobLimit = 13;

    private readonly PlanningModel _model;

    public PlanningSolver(PlanningModel model) => _model = model;

    public List<int>[] Solve()
    {
        var routes = CreateEmpty();
        if (_model.TechnicianCount == 0 || _model.JobCount == 0)
        {
            return routes;
        }

        var heuristic = BuildHeuristic();
        if (TrySolveExactly(out var exact) && Compare(exact, heuristic) < 0)
        {
            return exact;
        }

        return heuristic;
    }

    private List<int>[] CreateEmpty()
    {
        var routes = new List<int>[_model.TechnicianCount];
        for (var tech = 0; tech < routes.Length; tech++)
        {
            routes[tech] = [];
        }

        return routes;
    }

    private static List<int>[] Copy(List<int>[] routes)
    {
        var copy = new List<int>[routes.Length];
        for (var tech = 0; tech < routes.Length; tech++)
        {
            copy[tech] = [.. routes[tech]];
        }

        return copy;
    }

    private static void Adopt(List<int>[] target, List<int>[] source)
    {
        for (var tech = 0; tech < target.Length; tech++)
        {
            target[tech].Clear();
            target[tech].AddRange(source[tech]);
        }
    }

    /// <summary>Simulates a route and reports its directed travel minutes.</summary>
    private bool TryRoute(int technician, List<int> sequence, out int travelMinutes)
    {
        travelMinutes = 0;
        var location = 0;
        var time = _model.ShiftStart[technician];
        var total = 0;

        foreach (var job in sequence)
        {
            if (!_model.Eligible[technician][job])
            {
                return false;
            }

            var target = _model.JobLocation[job];
            var minutes = _model.Travel[location][target];
            if (minutes >= Inf)
            {
                return false;
            }

            total += minutes;
            var start = Math.Max(time + minutes, _model.WindowStart[job]);
            var end = start + _model.Duration[job];
            if (end > _model.WindowEnd[job])
            {
                return false;
            }

            location = target;
            time = end;
        }

        var back = _model.Travel[location][0];
        if (back >= Inf)
        {
            return false;
        }

        total += back;
        if (time + back > _model.ShiftEnd[technician])
        {
            return false;
        }

        travelMinutes = total;
        return true;
    }

    private (int Value, int Travel) Score(List<int>[] routes)
    {
        var value = 0;
        var travel = 0;
        for (var tech = 0; tech < routes.Length; tech++)
        {
            foreach (var job in routes[tech])
            {
                value += _model.Value[job];
            }

            travel += TryRoute(tech, routes[tech], out var minutes) ? minutes : Inf;
        }

        return (value, travel);
    }

    /// <summary>Orders plans by value descending, travel ascending, then content.</summary>
    private int Compare(List<int>[] left, List<int>[] right)
    {
        var leftScore = Score(left);
        var rightScore = Score(right);
        if (leftScore.Value != rightScore.Value)
        {
            return rightScore.Value - leftScore.Value;
        }

        if (leftScore.Travel != rightScore.Travel)
        {
            return leftScore.Travel - rightScore.Travel;
        }

        for (var tech = 0; tech < left.Length; tech++)
        {
            var shared = Math.Min(left[tech].Count, right[tech].Count);
            for (var index = 0; index < shared; index++)
            {
                if (left[tech][index] != right[tech][index])
                {
                    return left[tech][index] - right[tech][index];
                }
            }

            if (left[tech].Count != right[tech].Count)
            {
                return left[tech].Count - right[tech].Count;
            }
        }

        return 0;
    }

    private bool TrySolveExactly([NotNullWhen(true)] out List<int>[]? plan)
    {
        plan = null;
        var jobCount = _model.JobCount;
        var technicianCount = _model.TechnicianCount;
        var limit = technicianCount == 1 ? SingleTechnicianJobLimit : ExactJobLimit;
        if (jobCount > limit)
        {
            return false;
        }

        if (technicianCount > 1)
        {
            var work = 0.0;
            for (var tech = 0; tech < technicianCount; tech++)
            {
                var eligible = _model.EligibleJobs[tech].Length;
                work += Math.Pow(3.0, eligible) * Math.Pow(2.0, jobCount - eligible);
                if (work > ExactWorkLimit)
                {
                    return false;
                }
            }
        }

        var solvers = new RouteLabelDp[technicianCount];
        for (var tech = 0; tech < technicianCount; tech++)
        {
            solvers[tech] = new RouteLabelDp(
                _model,
                tech,
                _model.EligibleJobs[tech]);
            if (!solvers[tech].Run())
            {
                return false;
            }
        }

        var routes = technicianCount == 1
            ? SolveSingle(solvers[0])
            : SolvePartition(solvers);
        if (routes is null)
        {
            return false;
        }

        for (var tech = 0; tech < technicianCount; tech++)
        {
            if (!TryRoute(tech, routes[tech], out _))
            {
                return false;
            }
        }

        plan = routes;
        return true;
    }

    private List<int>[]? SolveSingle(RouteLabelDp solver)
    {
        var jobs = _model.EligibleJobs[0];
        var size = 1 << jobs.Length;
        var bestMask = 0;
        var bestValue = -1;
        var bestTravel = Inf;

        for (var mask = 0; mask < size; mask++)
        {
            var travel = solver.BestTravel[mask];
            if (travel >= Inf)
            {
                continue;
            }

            var value = 0;
            for (var bit = mask; bit != 0; bit &= bit - 1)
            {
                value += _model.Value[jobs[BitOperations.TrailingZeroCount(bit)]];
            }

            if (value > bestValue || (value == bestValue && travel < bestTravel))
            {
                bestValue = value;
                bestTravel = travel;
                bestMask = mask;
            }
        }

        if (bestValue < 0)
        {
            return null;
        }

        var routes = CreateEmpty();
        routes[0] = solver.Reconstruct(bestMask);
        return routes;
    }

    private List<int>[]? SolvePartition(RouteLabelDp[] solvers)
    {
        var jobCount = _model.JobCount;
        var technicianCount = _model.TechnicianCount;
        var full = 1 << jobCount;

        var values = new int[full];
        for (var mask = 1; mask < full; mask++)
        {
            var low = mask & -mask;
            values[mask] = values[mask ^ low]
                + _model.Value[BitOperations.TrailingZeroCount(low)];
        }

        var costs = new int[technicianCount][];
        var eligibleMasks = new int[technicianCount];
        var localIndex = new int[technicianCount][];
        for (var tech = 0; tech < technicianCount; tech++)
        {
            var jobs = _model.EligibleJobs[tech];
            var cost = new int[full];
            Array.Fill(cost, Inf);

            var mapping = new int[jobCount];
            Array.Fill(mapping, -1);
            var eligibleMask = 0;
            for (var index = 0; index < jobs.Length; index++)
            {
                eligibleMask |= 1 << jobs[index];
                mapping[jobs[index]] = index;
            }

            var localSize = 1 << jobs.Length;
            var globalMask = new int[localSize];
            for (var mask = 1; mask < localSize; mask++)
            {
                var low = mask & -mask;
                globalMask[mask] = globalMask[mask ^ low]
                    | (1 << jobs[BitOperations.TrailingZeroCount(low)]);
            }

            for (var mask = 0; mask < localSize; mask++)
            {
                var travel = solvers[tech].BestTravel[mask];
                if (travel < cost[globalMask[mask]])
                {
                    cost[globalMask[mask]] = travel;
                }
            }

            costs[tech] = cost;
            eligibleMasks[tech] = eligibleMask;
            localIndex[tech] = mapping;
        }

        var best = new int[full];
        Array.Fill(best, Inf);
        best[0] = 0;
        var choices = new int[technicianCount][];

        for (var tech = 0; tech < technicianCount; tech++)
        {
            var next = new int[full];
            Array.Fill(next, Inf);
            var choice = new int[full];
            var cost = costs[tech];
            var eligibleMask = eligibleMasks[tech];

            for (var mask = 0; mask < full; mask++)
            {
                var candidates = mask & eligibleMask;
                var bestTotal = Inf;
                var bestSubset = 0;
                var subset = candidates;
                while (true)
                {
                    var routeCost = cost[subset];
                    if (routeCost < Inf)
                    {
                        var previous = best[mask ^ subset];
                        if (previous < Inf && previous + routeCost < bestTotal)
                        {
                            bestTotal = previous + routeCost;
                            bestSubset = subset;
                        }
                    }

                    if (subset == 0)
                    {
                        break;
                    }

                    subset = (subset - 1) & candidates;
                }

                next[mask] = bestTotal;
                choice[mask] = bestSubset;
            }

            best = next;
            choices[tech] = choice;
        }

        var chosenMask = -1;
        var chosenValue = -1;
        var chosenTravel = Inf;
        for (var mask = 0; mask < full; mask++)
        {
            if (best[mask] >= Inf)
            {
                continue;
            }

            if (values[mask] > chosenValue
                || (values[mask] == chosenValue && best[mask] < chosenTravel))
            {
                chosenValue = values[mask];
                chosenTravel = best[mask];
                chosenMask = mask;
            }
        }

        if (chosenMask < 0)
        {
            return null;
        }

        var routes = CreateEmpty();
        var remaining = chosenMask;
        for (var tech = technicianCount - 1; tech >= 0; tech--)
        {
            var subset = choices[tech][remaining];
            remaining ^= subset;

            var localMask = 0;
            for (var bit = subset; bit != 0; bit &= bit - 1)
            {
                var job = BitOperations.TrailingZeroCount(bit);
                localMask |= 1 << localIndex[tech][job];
            }

            routes[tech] = solvers[tech].Reconstruct(localMask);
        }

        return routes;
    }

    private List<int>[] BuildHeuristic()
    {
        List<int>[]? best = null;
        for (var mode = 0; mode < 3; mode++)
        {
            var candidate = Construct(mode);
            Improve(candidate);
            if (best is null || Compare(candidate, best) < 0)
            {
                best = candidate;
            }
        }

        ReorderRoutes(best!);
        return best!;
    }

    private List<int>[] Construct(int mode)
    {
        var routes = CreateEmpty();
        while (InsertBest(routes, mode))
        {
        }

        return routes;
    }

    private bool[] ServedFlags(List<int>[] routes)
    {
        var served = new bool[_model.JobCount];
        foreach (var route in routes)
        {
            foreach (var job in route)
            {
                served[job] = true;
            }
        }

        return served;
    }

    /// <summary>Inserts the single most attractive unserved job, if any fits.</summary>
    private bool InsertBest(List<int>[] routes, int mode)
    {
        var served = ServedFlags(routes);
        var travels = new int[routes.Length];
        for (var tech = 0; tech < routes.Length; tech++)
        {
            travels[tech] = TryRoute(tech, routes[tech], out var minutes)
                ? minutes
                : Inf;
        }

        long bestPrimary = long.MinValue;
        var bestValue = -1;
        var bestDelta = 0;
        var bestJob = -1;
        var bestTech = -1;
        var bestPosition = -1;

        for (var job = 0; job < _model.JobCount; job++)
        {
            if (served[job])
            {
                continue;
            }

            for (var tech = 0; tech < routes.Length; tech++)
            {
                if (!_model.Eligible[tech][job] || travels[tech] >= Inf)
                {
                    continue;
                }

                var route = routes[tech];
                for (var position = 0; position <= route.Count; position++)
                {
                    route.Insert(position, job);
                    var feasible = TryRoute(tech, route, out var minutes);
                    route.RemoveAt(position);
                    if (!feasible)
                    {
                        continue;
                    }

                    var value = _model.Value[job];
                    var delta = minutes - travels[tech];
                    var primary = mode switch
                    {
                        0 => value,
                        1 => value - delta,
                        _ => (long)value * 1000 / Math.Max(1, delta + 1),
                    };

                    if (primary > bestPrimary
                        || (primary == bestPrimary
                            && (value > bestValue
                                || (value == bestValue && delta < bestDelta))))
                    {
                        bestPrimary = primary;
                        bestValue = value;
                        bestDelta = delta;
                        bestJob = job;
                        bestTech = tech;
                        bestPosition = position;
                    }
                }
            }
        }

        if (bestJob < 0)
        {
            return false;
        }

        routes[bestTech].Insert(bestPosition, bestJob);
        return true;
    }

    private void Improve(List<int>[] routes)
    {
        for (var round = 0; round < 64; round++)
        {
            var changed = false;
            while (InsertBest(routes, 0))
            {
                changed = true;
            }

            changed |= ReorderRoutes(routes);
            changed |= Relocate(routes);
            changed |= Swap(routes);
            changed |= EjectAndRefill(routes);
            if (!changed)
            {
                return;
            }
        }
    }

    /// <summary>Re-sequences each route optimally for its assigned job set.</summary>
    private bool ReorderRoutes(List<int>[] routes)
    {
        var improved = false;
        for (var tech = 0; tech < routes.Length; tech++)
        {
            var route = routes[tech];
            if (route.Count < 2 || route.Count > ReorderJobLimit)
            {
                continue;
            }

            if (!TryRoute(tech, route, out var current))
            {
                continue;
            }

            var jobs = route.ToArray();
            Array.Sort(jobs);
            var solver = new RouteLabelDp(_model, tech, jobs, 400_000);
            if (!solver.Run())
            {
                continue;
            }

            var full = (1 << jobs.Length) - 1;
            if (solver.BestTravel[full] < current)
            {
                var order = solver.Reconstruct(full);
                route.Clear();
                route.AddRange(order);
                improved = true;
            }
        }

        return improved;
    }

    private bool Relocate(List<int>[] routes)
    {
        var travels = new int[routes.Length];
        for (var tech = 0; tech < routes.Length; tech++)
        {
            travels[tech] = TryRoute(tech, routes[tech], out var minutes)
                ? minutes
                : Inf;
        }

        var bestGain = 0;
        var bestSource = -1;
        var bestPosition = -1;
        var bestTarget = -1;
        var bestSlot = -1;

        for (var source = 0; source < routes.Length; source++)
        {
            if (travels[source] >= Inf)
            {
                continue;
            }

            for (var position = 0; position < routes[source].Count; position++)
            {
                var job = routes[source][position];
                routes[source].RemoveAt(position);
                if (!TryRoute(source, routes[source], out var trimmed))
                {
                    routes[source].Insert(position, job);
                    continue;
                }

                for (var target = 0; target < routes.Length; target++)
                {
                    if (!_model.Eligible[target][job]
                        || (target != source && travels[target] >= Inf))
                    {
                        continue;
                    }

                    for (var slot = 0; slot <= routes[target].Count; slot++)
                    {
                        routes[target].Insert(slot, job);
                        var feasible = TryRoute(target, routes[target], out var minutes);
                        routes[target].RemoveAt(slot);
                        if (!feasible)
                        {
                            continue;
                        }

                        var gain = target == source
                            ? travels[source] - minutes
                            : travels[source] - trimmed + travels[target] - minutes;
                        if (gain > bestGain)
                        {
                            bestGain = gain;
                            bestSource = source;
                            bestPosition = position;
                            bestTarget = target;
                            bestSlot = slot;
                        }
                    }
                }

                routes[source].Insert(position, job);
            }
        }

        if (bestSource < 0)
        {
            return false;
        }

        var moved = routes[bestSource][bestPosition];
        routes[bestSource].RemoveAt(bestPosition);
        routes[bestTarget].Insert(bestSlot, moved);
        return true;
    }

    private bool Swap(List<int>[] routes)
    {
        var travels = new int[routes.Length];
        for (var tech = 0; tech < routes.Length; tech++)
        {
            travels[tech] = TryRoute(tech, routes[tech], out var minutes)
                ? minutes
                : Inf;
        }

        var bestGain = 0;
        var bestLeft = -1;
        var bestLeftPos = -1;
        var bestRight = -1;
        var bestRightPos = -1;

        for (var left = 0; left < routes.Length; left++)
        {
            if (travels[left] >= Inf)
            {
                continue;
            }

            for (var leftPos = 0; leftPos < routes[left].Count; leftPos++)
            {
                for (var right = left; right < routes.Length; right++)
                {
                    if (travels[right] >= Inf)
                    {
                        continue;
                    }

                    var start = right == left ? leftPos + 1 : 0;
                    for (var rightPos = start; rightPos < routes[right].Count; rightPos++)
                    {
                        var first = routes[left][leftPos];
                        var second = routes[right][rightPos];
                        if (!_model.Eligible[left][second]
                            || !_model.Eligible[right][first])
                        {
                            continue;
                        }

                        routes[left][leftPos] = second;
                        routes[right][rightPos] = first;
                        var feasible = TryRoute(left, routes[left], out var leftMinutes);
                        var gain = 0;
                        if (feasible)
                        {
                            if (right == left)
                            {
                                gain = travels[left] - leftMinutes;
                            }
                            else if (TryRoute(right, routes[right], out var rightMinutes))
                            {
                                gain = travels[left] - leftMinutes
                                    + travels[right] - rightMinutes;
                            }
                            else
                            {
                                feasible = false;
                            }
                        }

                        routes[left][leftPos] = first;
                        routes[right][rightPos] = second;
                        if (feasible && gain > bestGain)
                        {
                            bestGain = gain;
                            bestLeft = left;
                            bestLeftPos = leftPos;
                            bestRight = right;
                            bestRightPos = rightPos;
                        }
                    }
                }
            }
        }

        if (bestLeft < 0)
        {
            return false;
        }

        (routes[bestLeft][bestLeftPos], routes[bestRight][bestRightPos]) =
            (routes[bestRight][bestRightPos], routes[bestLeft][bestLeftPos]);
        return true;
    }

    /// <summary>Drops one served job and refills, escaping low value traps.</summary>
    private bool EjectAndRefill(List<int>[] routes)
    {
        if (_model.JobCount > 160)
        {
            return false;
        }

        var current = Score(routes);
        for (var tech = 0; tech < routes.Length; tech++)
        {
            for (var position = 0; position < routes[tech].Count; position++)
            {
                var candidate = Copy(routes);
                candidate[tech].RemoveAt(position);
                while (InsertBest(candidate, 0))
                {
                }

                ReorderRoutes(candidate);
                var score = Score(candidate);
                if (score.Value > current.Value
                    || (score.Value == current.Value && score.Travel < current.Travel))
                {
                    Adopt(routes, candidate);
                    return true;
                }
            }
        }

        return false;
    }
}
