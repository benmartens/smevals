using System.Numerics;

namespace FieldServiceRoutePlanner;

public sealed class RoutePlanner
{
    public RoutePlan Plan(RoutePlanningProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var jobs = problem.Jobs
            .OrderBy(job => job.Id, StringComparer.Ordinal)
            .ToArray();
        var technicians = problem.Technicians
            .OrderBy(technician => technician.Id, StringComparer.Ordinal)
            .ToArray();

        var routeOptions = technicians
            .Select(technician => BuildRouteOptions(problem, technician, jobs))
            .ToArray();

        var plans = new Dictionary<BigInteger, CombinedPlan>
        {
            [BigInteger.Zero] = new(0, 0, []),
        };

        for (var technicianIndex = 0; technicianIndex < technicians.Length;
             technicianIndex++)
        {
            var nextPlans = new Dictionary<BigInteger, CombinedPlan>();
            foreach (var existing in plans)
            {
                foreach (var option in routeOptions[technicianIndex])
                {
                    if ((existing.Key & option.JobMask) != BigInteger.Zero)
                    {
                        continue;
                    }

                    var mask = existing.Key | option.JobMask;
                    var paths = new int[technicianIndex + 1][];
                    Array.Copy(existing.Value.Paths, paths, technicianIndex);
                    paths[technicianIndex] = option.Path;

                    var candidate = new CombinedPlan(
                        existing.Value.Value + option.Value,
                        existing.Value.Travel + option.Travel,
                        paths);
                    if (!nextPlans.TryGetValue(mask, out var current)
                        || IsBetterForMask(candidate, current))
                    {
                        nextPlans[mask] = candidate;
                    }
                }
            }

            plans = nextPlans;
        }

        CombinedPlan? best = null;
        foreach (var plan in plans.Values)
        {
            if (best is null || IsBetterOverall(plan, best))
            {
                best = plan;
            }
        }

        if (best is null)
        {
            return new RoutePlan(
                technicians
                    .Select(technician => new TechnicianRoute(
                        technician.Id,
                        []))
                    .ToList());
        }

        var routes = technicians
            .Select((technician, index) => new TechnicianRoute(
                technician.Id,
                best.Paths[index]
                    .Select(jobIndex => jobs[jobIndex].Id)
                    .ToList()))
            .ToList();
        return new RoutePlan(routes);
    }

    private static List<RouteOption> BuildRouteOptions(
        RoutePlanningProblem problem,
        Technician technician,
        ServiceJob[] jobs)
    {
        var skills = new HashSet<string>(
            technician.Skills,
            StringComparer.Ordinal);
        var eligibleJobIndices = Enumerable.Range(0, jobs.Length)
            .Where(index => jobs[index].RequiredSkills.All(skills.Contains))
            .ToArray();

        var options = new Dictionary<BigInteger, RouteOption>
        {
            [BigInteger.Zero] = new(
                BigInteger.Zero,
                0,
                0,
                []),
        };

        if (!TryGetTravel(
                problem,
                problem.Depot,
                problem.Depot,
                out var emptyReturn)
            || technician.ShiftStart + emptyReturn > technician.ShiftEnd)
        {
            options.Remove(BigInteger.Zero);
        }
        else
        {
            options[BigInteger.Zero] = new(
                BigInteger.Zero,
                0,
                emptyReturn,
                []);
        }

        var statesByMask = new Dictionary<BigInteger, List<RouteState>>();
        var masksByCount = new SortedSet<BigInteger>[eligibleJobIndices.Length + 1];

        for (var localIndex = 0; localIndex < eligibleJobIndices.Length; localIndex++)
        {
            var jobIndex = eligibleJobIndices[localIndex];
            var job = jobs[jobIndex];
            if (!TryGetTravel(
                    problem,
                    problem.Depot,
                    job.Location,
                    out var travel))
            {
                continue;
            }

            var arrival = technician.ShiftStart + travel;
            var serviceStart = Math.Max(arrival, (long)job.WindowStart);
            var serviceEnd = serviceStart + job.Duration;
            if (serviceEnd > job.WindowEnd)
            {
                continue;
            }

            var state = new RouteState(
                localIndex,
                serviceEnd,
                travel,
                BigInteger.One << jobIndex,
                [jobIndex]);
            AddParetoState(
                statesByMask,
                masksByCount,
                BigInteger.One << localIndex,
                1,
                state);
        }

        for (var count = 1; count < masksByCount.Length; count++)
        {
            var masks = masksByCount[count];
            if (masks is null)
            {
                continue;
            }

            foreach (var mask in masks)
            {
                if (!statesByMask.TryGetValue(mask, out var states))
                {
                    continue;
                }

                foreach (var state in states)
                {
                    var lastJob = jobs[eligibleJobIndices[state.LastLocalIndex]];
                    if (TryGetTravel(
                            problem,
                            lastJob.Location,
                            problem.Depot,
                            out var returnTravel)
                        && state.CurrentTime + returnTravel
                            <= technician.ShiftEnd)
                    {
                        AddRouteOption(
                            options,
                            new RouteOption(
                                state.JobMask,
                                state.Path.Sum(jobIndex => (long)jobs[jobIndex].Value),
                                state.Travel + returnTravel,
                                state.Path));
                    }

                    if (state.CurrentTime > technician.ShiftEnd)
                    {
                        continue;
                    }

                    for (var nextLocalIndex = 0;
                         nextLocalIndex < eligibleJobIndices.Length;
                         nextLocalIndex++)
                    {
                        var nextBit = BigInteger.One << nextLocalIndex;
                        if ((mask & nextBit) != BigInteger.Zero)
                        {
                            continue;
                        }

                        var nextJobIndex = eligibleJobIndices[nextLocalIndex];
                        var nextJob = jobs[nextJobIndex];
                        if (!TryGetTravel(
                                problem,
                                lastJob.Location,
                                nextJob.Location,
                                out var travel))
                        {
                            continue;
                        }

                        var arrival = state.CurrentTime + travel;
                        var serviceStart = Math.Max(
                            arrival,
                            (long)nextJob.WindowStart);
                        var serviceEnd = serviceStart + nextJob.Duration;
                        if (serviceEnd > nextJob.WindowEnd)
                        {
                            continue;
                        }

                        var path = new int[state.Path.Length + 1];
                        state.Path.CopyTo(path, 0);
                        path[^1] = nextJobIndex;
                        AddParetoState(
                            statesByMask,
                            masksByCount,
                            mask | nextBit,
                            count + 1,
                            new RouteState(
                                nextLocalIndex,
                                serviceEnd,
                                state.Travel + travel,
                                state.JobMask | (BigInteger.One << nextJobIndex),
                                path));
                    }
                }
            }
        }

        return options.Values
            .OrderBy(option => option.JobMask)
            .ThenBy(option => option.Travel)
            .ThenBy(option => option.Path, new PathComparer())
            .ToList();
    }

    private static void AddParetoState(
        Dictionary<BigInteger, List<RouteState>> statesByMask,
        SortedSet<BigInteger>[] masksByCount,
        BigInteger mask,
        int count,
        RouteState candidate)
    {
        if (!statesByMask.TryGetValue(mask, out var states))
        {
            states = [];
            statesByMask[mask] = states;
        }

        foreach (var existing in states)
        {
            if (existing.LastLocalIndex != candidate.LastLocalIndex)
            {
                continue;
            }

            if (existing.CurrentTime <= candidate.CurrentTime
                && existing.Travel <= candidate.Travel
                && (existing.CurrentTime < candidate.CurrentTime
                    || existing.Travel < candidate.Travel
                    || ComparePaths(existing.Path, candidate.Path) <= 0))
            {
                return;
            }
        }

        for (var index = states.Count - 1; index >= 0; index--)
        {
            var existing = states[index];
            if (existing.LastLocalIndex != candidate.LastLocalIndex)
            {
                continue;
            }

            var candidateDominates = candidate.CurrentTime <= existing.CurrentTime
                && candidate.Travel <= existing.Travel
                && (candidate.CurrentTime < existing.CurrentTime
                    || candidate.Travel < existing.Travel
                    || ComparePaths(candidate.Path, existing.Path) < 0);
            if (candidateDominates)
            {
                states.RemoveAt(index);
            }
        }

        states.Add(candidate);
        (masksByCount[count] ??= new()).Add(mask);
    }

    private static void AddRouteOption(
        Dictionary<BigInteger, RouteOption> options,
        RouteOption candidate)
    {
        if (!options.TryGetValue(candidate.JobMask, out var current)
            || candidate.Travel < current.Travel
            || (candidate.Travel == current.Travel
                && ComparePaths(candidate.Path, current.Path) < 0))
        {
            options[candidate.JobMask] = candidate;
        }
    }

    private static bool IsBetterForMask(
        CombinedPlan candidate,
        CombinedPlan current) =>
        candidate.Travel < current.Travel
        || (candidate.Travel == current.Travel
            && CompareRoutePaths(candidate.Paths, current.Paths) < 0);

    private static bool IsBetterOverall(
        CombinedPlan candidate,
        CombinedPlan current) =>
        candidate.Value > current.Value
        || (candidate.Value == current.Value
            && (candidate.Travel < current.Travel
                || (candidate.Travel == current.Travel
                    && CompareRoutePaths(candidate.Paths, current.Paths) < 0)));

    private static int CompareRoutePaths(int[][] left, int[][] right)
    {
        for (var index = 0; index < left.Length; index++)
        {
            var comparison = ComparePaths(left[index], right[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static int ComparePaths(int[] left, int[] right)
    {
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var comparison = left[index].CompareTo(right[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static bool TryGetTravel(
        RoutePlanningProblem problem,
        string from,
        string to,
        out long minutes)
    {
        if (problem.TravelTimes.TryGetValue(from, out var row)
            && row.TryGetValue(to, out var value)
            && value >= 0)
        {
            minutes = value;
            return true;
        }

        minutes = 0;
        return false;
    }

    private sealed record RouteState(
        int LastLocalIndex,
        long CurrentTime,
        long Travel,
        BigInteger JobMask,
        int[] Path);

    private sealed record RouteOption(
        BigInteger JobMask,
        long Value,
        long Travel,
        int[] Path);

    private sealed record CombinedPlan(
        long Value,
        long Travel,
        int[][] Paths);

    private sealed class PathComparer : IComparer<int[]>
    {
        public int Compare(int[]? left, int[]? right)
        {
            if (left is null)
            {
                return right is null ? 0 : -1;
            }

            if (right is null)
            {
                return 1;
            }

            return ComparePaths(left, right);
        }
    }
}
