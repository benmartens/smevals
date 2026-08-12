using System.Numerics;

namespace FieldServiceRoutePlanner;

public sealed class RoutePlanner
{
    public RoutePlan Plan(RoutePlanningProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(problem.Technicians);
        ArgumentNullException.ThrowIfNull(problem.Jobs);
        ArgumentNullException.ThrowIfNull(problem.TravelTimes);

        var technicians = problem.Technicians
            .OrderBy(technician => technician.Id, StringComparer.Ordinal)
            .ToArray();
        var jobs = problem.Jobs
            .Where(job => technicians.Any(
                technician => RouteValidator.HasSkills(technician, job)))
            .OrderBy(job => job.Id, StringComparer.Ordinal)
            .ToArray();
        var jobBits = Enumerable.Range(0, jobs.Length)
            .Select(index => BigInteger.One << index)
            .ToArray();
        var routesByTechnician = technicians
            .Select(technician => EnumerateRoutes(problem, technician, jobs, jobBits))
            .ToArray();

        var bestPlan = SelectRoutes(routesByTechnician);
        if (bestPlan is null)
        {
            throw new InvalidOperationException(
                "The problem has no feasible route for every technician.");
        }

        var selectedRoutes = new RouteCandidate?[technicians.Length];
        for (var node = bestPlan; node.Previous is not null; node = node.Previous)
        {
            selectedRoutes[node.TechnicianIndex] = node.Route;
        }

        var result = new List<TechnicianRoute>(technicians.Length);
        for (var technicianIndex = 0;
            technicianIndex < technicians.Length;
            technicianIndex++)
        {
            var route = selectedRoutes[technicianIndex]
                ?? throw new InvalidOperationException("Route selection was incomplete.");
            result.Add(new(
                technicians[technicianIndex].Id,
                route.JobIndexes.Select(index => jobs[index].Id).ToList()));
        }

        return new(result);
    }

    private static List<RouteCandidate> EnumerateRoutes(
        RoutePlanningProblem problem,
        Technician technician,
        ServiceJob[] jobs,
        BigInteger[] jobBits)
    {
        var candidates = new Dictionary<BigInteger, RouteCandidate>();
        var labelsByState = new Dictionary<RouteState, List<RouteLabel>>();
        var statesByLength = Enumerable.Range(0, jobs.Length + 1)
            .Select(_ => new List<RouteState>())
            .ToArray();
        var eligibleJobs = jobs.Select(
            job => RouteValidator.HasSkills(technician, job)).ToArray();

        var emptyReturnTravel = GetTravel(problem, problem.Depot, problem.Depot);
        if ((long)technician.ShiftStart + emptyReturnTravel <= technician.ShiftEnd)
        {
            candidates.Add(BigInteger.Zero, new(
                BigInteger.Zero,
                0,
                emptyReturnTravel,
                Array.Empty<int>()));
        }

        for (var jobIndex = 0; jobIndex < jobs.Length; jobIndex++)
        {
            if (!eligibleJobs[jobIndex])
            {
                continue;
            }

            var travel = GetTravel(problem, problem.Depot, jobs[jobIndex].Location);
            if (!TrySchedule(
                    technician,
                    jobs[jobIndex],
                    (long)technician.ShiftStart + travel,
                    out var serviceEnd))
            {
                continue;
            }

            var label = new RouteLabel(
                serviceEnd,
                travel,
                jobs[jobIndex].Value,
                [jobIndex]);
            var state = new RouteState(jobBits[jobIndex], jobIndex);
            if (TryAddLabel(
                    labelsByState,
                    statesByLength[1],
                    state,
                    label))
            {
                ConsiderCandidate(
                    problem,
                    technician,
                    jobs,
                    candidates,
                    state.JobMask,
                    label);
            }
        }

        for (var length = 1; length < jobs.Length; length++)
        {
            foreach (var state in statesByLength[length])
            {
                var labels = labelsByState[state];
                foreach (var label in labels)
                {
                    for (var jobIndex = 0; jobIndex < jobs.Length; jobIndex++)
                    {
                        if ((state.JobMask & jobBits[jobIndex]) != BigInteger.Zero
                            || !eligibleJobs[jobIndex])
                        {
                            continue;
                        }

                        var travel = GetTravel(
                            problem,
                            jobs[state.LastJobIndex].Location,
                            jobs[jobIndex].Location);
                        if (!TrySchedule(
                                technician,
                                jobs[jobIndex],
                                label.ServiceEnd + travel,
                                out var serviceEnd))
                        {
                            continue;
                        }

                        var nextLabel = new RouteLabel(
                            serviceEnd,
                            label.TravelMinutes + travel,
                            label.Value + jobs[jobIndex].Value,
                            Append(label.JobIndexes, jobIndex));
                        var nextState = new RouteState(
                            state.JobMask | jobBits[jobIndex],
                            jobIndex);
                        if (TryAddLabel(
                                labelsByState,
                                statesByLength[length + 1],
                                nextState,
                                nextLabel))
                        {
                            ConsiderCandidate(
                                problem,
                                technician,
                                jobs,
                                candidates,
                                nextState.JobMask,
                                nextLabel);
                        }
                    }
                }
            }
        }

        var routes = candidates.Values.ToList();
        routes.Sort(CompareCandidates);
        return routes;
    }

    private static void ConsiderCandidate(
        RoutePlanningProblem problem,
        Technician technician,
        ServiceJob[] jobs,
        Dictionary<BigInteger, RouteCandidate> candidates,
        BigInteger jobMask,
        RouteLabel label)
    {
        var returnTravel = GetTravel(
            problem,
            jobs[label.JobIndexes[^1]].Location,
            problem.Depot);
        if (label.ServiceEnd + returnTravel > technician.ShiftEnd)
        {
            return;
        }

        var travelMinutes = label.TravelMinutes + returnTravel;
        var candidate = new RouteCandidate(
            jobMask,
            label.Value,
            travelMinutes,
            label.JobIndexes);
        if (!candidates.TryGetValue(jobMask, out var existing)
            || CompareCandidates(candidate, existing) < 0)
        {
            candidates[jobMask] = candidate;
        }
    }

    private static bool TryAddLabel(
        Dictionary<RouteState, List<RouteLabel>> labelsByState,
        List<RouteState> statesAtLength,
        RouteState state,
        RouteLabel label)
    {
        if (!labelsByState.TryGetValue(state, out var labels))
        {
            labelsByState.Add(state, [label]);
            statesAtLength.Add(state);
            return true;
        }

        foreach (var existing in labels)
        {
            if (existing.ServiceEnd <= label.ServiceEnd
                && existing.TravelMinutes <= label.TravelMinutes)
            {
                if (existing.ServiceEnd < label.ServiceEnd
                    || existing.TravelMinutes < label.TravelMinutes
                    || CompareOrders(existing.JobIndexes, label.JobIndexes) <= 0)
                {
                    return false;
                }
            }
        }

        for (var index = labels.Count - 1; index >= 0; index--)
        {
            var existing = labels[index];
            if (label.ServiceEnd <= existing.ServiceEnd
                && label.TravelMinutes <= existing.TravelMinutes)
            {
                labels.RemoveAt(index);
            }
        }
        labels.Add(label);
        return true;
    }

    private static GlobalNode? SelectRoutes(
        IReadOnlyList<RouteCandidate>[] routesByTechnician)
    {
        var current = new Dictionary<BigInteger, GlobalNode>
        {
            [BigInteger.Zero] = new(null, null, -1, 0, 0),
        };

        for (var technicianIndex = 0;
            technicianIndex < routesByTechnician.Length;
            technicianIndex++)
        {
            var next = new Dictionary<BigInteger, GlobalNode>();
            foreach (var entry in current.OrderBy(entry => entry.Key))
            {
                foreach (var route in routesByTechnician[technicianIndex])
                {
                    if ((entry.Key & route.JobMask) != BigInteger.Zero)
                    {
                        continue;
                    }

                    var jobMask = entry.Key | route.JobMask;
                    var value = entry.Value.Value + route.Value;
                    var travelMinutes = entry.Value.TravelMinutes + route.TravelMinutes;
                    if (next.TryGetValue(jobMask, out var existing)
                        && (existing.Value > value
                            || (existing.Value == value
                                && existing.TravelMinutes <= travelMinutes)))
                    {
                        continue;
                    }

                    next[jobMask] = new(
                        entry.Value,
                        route,
                        technicianIndex,
                        value,
                        travelMinutes);
                }
            }

            current = next;
        }

        GlobalNode? best = null;
        foreach (var entry in current.OrderBy(entry => entry.Key))
        {
            var candidate = entry.Value;
            if (best is null
                || candidate.Value > best.Value
                || (candidate.Value == best.Value
                    && candidate.TravelMinutes < best.TravelMinutes))
            {
                best = candidate;
            }
        }
        return best;
    }

    private static bool TrySchedule(
        Technician technician,
        ServiceJob job,
        long arrival,
        out long serviceEnd)
    {
        var serviceStart = Math.Max(arrival, (long)job.WindowStart);
        serviceEnd = serviceStart + job.Duration;
        return serviceEnd <= job.WindowEnd && serviceEnd <= technician.ShiftEnd;
    }

    private static long GetTravel(
        RoutePlanningProblem problem,
        string from,
        string to)
    {
        if (problem.TravelTimes.TryGetValue(from, out var row)
            && row.TryGetValue(to, out var minutes)
            && minutes >= 0)
        {
            return minutes;
        }

        throw new ArgumentException(
            $"Missing or invalid travel time '{from}' -> '{to}'.",
            nameof(problem));
    }

    private static int[] Append(int[] jobIndexes, int jobIndex)
    {
        var result = new int[jobIndexes.Length + 1];
        Array.Copy(jobIndexes, result, jobIndexes.Length);
        result[^1] = jobIndex;
        return result;
    }

    private static int CompareCandidates(RouteCandidate left, RouteCandidate right)
    {
        var result = left.JobMask.CompareTo(right.JobMask);
        if (result != 0)
        {
            return result;
        }

        result = left.TravelMinutes.CompareTo(right.TravelMinutes);
        return result != 0
            ? result
            : CompareOrders(left.JobIndexes, right.JobIndexes);
    }

    private static int CompareOrders(int[] left, int[] right)
    {
        var commonLength = Math.Min(left.Length, right.Length);
        for (var index = 0; index < commonLength; index++)
        {
            var result = left[index].CompareTo(right[index]);
            if (result != 0)
            {
                return result;
            }
        }
        return left.Length.CompareTo(right.Length);
    }

    private readonly record struct RouteState(BigInteger JobMask, int LastJobIndex);

    private sealed record RouteLabel(
        long ServiceEnd,
        long TravelMinutes,
        long Value,
        int[] JobIndexes);

    private sealed record RouteCandidate(
        BigInteger JobMask,
        long Value,
        long TravelMinutes,
        int[] JobIndexes);

    private sealed record GlobalNode(
        GlobalNode? Previous,
        RouteCandidate? Route,
        int TechnicianIndex,
        long Value,
        long TravelMinutes);
}
