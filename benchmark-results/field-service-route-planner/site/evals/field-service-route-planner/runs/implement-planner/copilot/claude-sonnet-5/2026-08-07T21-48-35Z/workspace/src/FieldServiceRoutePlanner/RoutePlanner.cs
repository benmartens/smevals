using System.Diagnostics;

namespace FieldServiceRoutePlanner;

/// <summary>
/// Deterministic heuristic solver for a multi-technician vehicle routing
/// problem with time windows (VRPTW) and skill constraints. Builds an
/// initial value-greedy insertion solution, then improves it with a
/// bounded, deterministic local search (job-exclusion trials, relocate,
/// 2-opt/or-opt) to lexicographically maximize served value and then
/// minimize total travel.
/// </summary>
public sealed class RoutePlanner
{
    // Pair-exclusion trials are O(n^2) fills; only attempt for modestly
    // sized instances so pathological inputs cannot blow up runtime.
    private const int PairExclusionJobLimit = 40;
    private static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(8);

    public RoutePlan Plan(RoutePlanningProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var technicians = BuildTechnicians(problem);
        var jobs = BuildJobs(problem);
        var jobsById = jobs.ToDictionary(j => j.Id, j => j, StringComparer.Ordinal);

        var routes = technicians.ToDictionary(
            t => t.Id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        var assigned = new HashSet<string>(StringComparer.Ordinal);

        if (technicians.Count == 0 || jobs.Count == 0)
        {
            return BuildResult(technicians, routes);
        }

        var allJobIds = jobs.Select(j => j.Id).ToList();
        GreedyFill(problem, technicians, jobsById, routes, assigned, allJobIds);

        var stopwatch = Stopwatch.StartNew();
        var changed = true;
        while (changed && stopwatch.Elapsed < TimeBudget)
        {
            changed = false;

            if (TryExclusionImprovement(
                    problem, technicians, jobs, jobsById, routes, assigned,
                    comboSize: 1, stopwatch))
            {
                changed = true;
                continue;
            }

            if (jobs.Count <= PairExclusionJobLimit
                && stopwatch.Elapsed < TimeBudget
                && TryExclusionImprovement(
                    problem, technicians, jobs, jobsById, routes, assigned,
                    comboSize: 2, stopwatch))
            {
                changed = true;
                continue;
            }

            if (stopwatch.Elapsed < TimeBudget
                && TryRelocateAndFillImprovement(
                    problem, technicians, jobsById, routes, assigned, stopwatch))
            {
                changed = true;
                continue;
            }

            if (stopwatch.Elapsed < TimeBudget
                && TryTravelOptimization(problem, technicians, jobsById, routes))
            {
                changed = true;
                continue;
            }
        }

        return BuildResult(technicians, routes);
    }

    private static RoutePlan BuildResult(
        List<Technician> technicians,
        Dictionary<string, List<string>> routes) =>
        new(technicians
            .Select(t => new TechnicianRoute(t.Id, routes[t.Id]))
            .ToList());

    private static List<Technician> BuildTechnicians(RoutePlanningProblem problem)
    {
        var seen = new Dictionary<string, Technician>(StringComparer.Ordinal);
        foreach (var technician in problem.Technicians)
        {
            if (string.IsNullOrWhiteSpace(technician.Id)
                || technician.ShiftStart < 0
                || technician.ShiftEnd < technician.ShiftStart
                || technician.Skills.Any(string.IsNullOrWhiteSpace))
            {
                continue;
            }
            seen.TryAdd(technician.Id, technician);
        }
        return seen.Values.OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
    }

    private static List<ServiceJob> BuildJobs(RoutePlanningProblem problem)
    {
        var seen = new Dictionary<string, ServiceJob>(StringComparer.Ordinal);
        foreach (var job in problem.Jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Id)
                || string.IsNullOrWhiteSpace(job.Location)
                || job.Duration < 0
                || job.WindowStart < 0
                || job.WindowEnd < job.WindowStart
                || job.Value < 0
                || job.RequiredSkills.Any(string.IsNullOrWhiteSpace))
            {
                continue;
            }
            seen.TryAdd(job.Id, job);
        }
        return seen.Values.OrderBy(j => j.Id, StringComparer.Ordinal).ToList();
    }

    // ----- Core timing / feasibility -----------------------------------

    private static bool TryGetTravel(
        RoutePlanningProblem problem,
        string from,
        string to,
        out int minutes)
    {
        if (problem.TravelTimes.TryGetValue(from, out var row)
            && row.TryGetValue(to, out minutes)
            && minutes >= 0)
        {
            return true;
        }
        minutes = 0;
        return false;
    }

    /// <summary>
    /// Mirrors RouteValidator's timing rules: depot start at shiftStart,
    /// directed travel, waiting until windowStart allowed, service must
    /// finish by windowEnd, and the final return to depot must be no
    /// later than shiftEnd.
    /// </summary>
    private static bool TryEvaluateRoute(
        RoutePlanningProblem problem,
        Technician technician,
        IReadOnlyList<ServiceJob> jobsInOrder,
        out int totalTravel,
        out int returnTime)
    {
        totalTravel = 0;
        var currentLocation = problem.Depot;
        var currentTime = technician.ShiftStart;

        foreach (var job in jobsInOrder)
        {
            if (!TryGetTravel(problem, currentLocation, job.Location, out var travel))
            {
                returnTime = 0;
                return false;
            }
            totalTravel += travel;
            var arrival = currentTime + travel;
            var serviceStart = Math.Max(arrival, job.WindowStart);
            var serviceEnd = serviceStart + job.Duration;
            if (serviceEnd > job.WindowEnd)
            {
                returnTime = 0;
                return false;
            }
            currentTime = serviceEnd;
            currentLocation = job.Location;
        }

        if (!TryGetTravel(problem, currentLocation, problem.Depot, out var returnTravel))
        {
            returnTime = 0;
            return false;
        }
        totalTravel += returnTravel;
        returnTime = currentTime + returnTravel;
        return returnTime <= technician.ShiftEnd;
    }

    private static bool TryEvaluateRouteIds(
        RoutePlanningProblem problem,
        Technician technician,
        List<string> jobIds,
        Dictionary<string, ServiceJob> jobsById,
        out int totalTravel)
    {
        var jobsInOrder = new List<ServiceJob>(jobIds.Count);
        foreach (var id in jobIds)
        {
            jobsInOrder.Add(jobsById[id]);
        }
        return TryEvaluateRoute(problem, technician, jobsInOrder, out totalTravel, out _);
    }

    // ----- Insertion search ----------------------------------------------

    /// <summary>
    /// Finds the feasible insertion position for <paramref name="candidate"/>
    /// within <paramref name="route"/> that yields the least added travel,
    /// breaking ties by the smallest position index for determinism.
    /// </summary>
    private static bool TryBestPositionInRoute(
        RoutePlanningProblem problem,
        Technician technician,
        List<string> route,
        Dictionary<string, ServiceJob> jobsById,
        ServiceJob candidate,
        out int bestPos,
        out int bestDelta)
    {
        bestPos = -1;
        bestDelta = int.MaxValue;

        var currentJobs = new List<ServiceJob>(route.Count);
        foreach (var id in route)
        {
            currentJobs.Add(jobsById[id]);
        }
        if (!TryEvaluateRoute(problem, technician, currentJobs, out var baseTravel, out _))
        {
            baseTravel = 0;
        }

        var trial = new List<ServiceJob>(currentJobs.Count + 1);
        for (var pos = 0; pos <= currentJobs.Count; pos++)
        {
            trial.Clear();
            trial.AddRange(currentJobs.Take(pos));
            trial.Add(candidate);
            trial.AddRange(currentJobs.Skip(pos));
            if (TryEvaluateRoute(problem, technician, trial, out var newTravel, out _))
            {
                var delta = newTravel - baseTravel;
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestPos = pos;
                }
            }
        }
        return bestPos != -1;
    }

    /// <summary>
    /// Value-first, cheapest-insertion construction: repeatedly inserts the
    /// single (job, technician, position) combination that serves the
    /// highest-value job feasibly, breaking ties by minimal added travel and
    /// then by ID for determinism, until no further feasible insertion
    /// exists among <paramref name="candidateJobIds"/>.
    /// </summary>
    private static void GreedyFill(
        RoutePlanningProblem problem,
        List<Technician> technicians,
        Dictionary<string, ServiceJob> jobsById,
        Dictionary<string, List<string>> routes,
        HashSet<string> assigned,
        List<string> candidateJobIds)
    {
        while (true)
        {
            ServiceJob? bestJob = null;
            Technician? bestTech = null;
            var bestPos = -1;
            var bestDelta = int.MaxValue;

            foreach (var jobId in candidateJobIds)
            {
                if (assigned.Contains(jobId))
                {
                    continue;
                }
                var job = jobsById[jobId];
                foreach (var tech in technicians)
                {
                    if (!RouteValidator.HasSkills(tech, job))
                    {
                        continue;
                    }
                    if (!TryBestPositionInRoute(
                            problem, tech, routes[tech.Id], jobsById, job,
                            out var pos, out var delta))
                    {
                        continue;
                    }

                    if (IsBetterCandidate(
                            job, tech, pos, delta,
                            bestJob, bestTech, bestPos, bestDelta))
                    {
                        bestJob = job;
                        bestTech = tech;
                        bestPos = pos;
                        bestDelta = delta;
                    }
                }
            }

            if (bestJob is null)
            {
                break;
            }
            routes[bestTech!.Id].Insert(bestPos, bestJob.Id);
            assigned.Add(bestJob.Id);
        }
    }

    private static bool IsBetterCandidate(
        ServiceJob job,
        Technician tech,
        int pos,
        int delta,
        ServiceJob? bestJob,
        Technician? bestTech,
        int bestPos,
        int bestDelta)
    {
        if (bestJob is null)
        {
            return true;
        }
        if (job.Value != bestJob.Value)
        {
            return job.Value > bestJob.Value;
        }
        if (delta != bestDelta)
        {
            return delta < bestDelta;
        }
        var techCompare = string.CompareOrdinal(tech.Id, bestTech!.Id);
        if (techCompare != 0)
        {
            return techCompare < 0;
        }
        if (pos != bestPos)
        {
            return pos < bestPos;
        }
        return string.CompareOrdinal(job.Id, bestJob.Id) < 0;
    }

    // ----- Objective evaluation ------------------------------------------

    private static (int Value, int Travel) Evaluate(
        RoutePlanningProblem problem,
        List<Technician> technicians,
        Dictionary<string, List<string>> routes,
        Dictionary<string, ServiceJob> jobsById)
    {
        var value = 0;
        var travel = 0;
        foreach (var tech in technicians)
        {
            var route = routes[tech.Id];
            if (TryEvaluateRouteIds(problem, tech, route, jobsById, out var routeTravel))
            {
                travel += routeTravel;
                foreach (var id in route)
                {
                    value += jobsById[id].Value;
                }
            }
        }
        return (value, travel);
    }

    private static Dictionary<string, List<string>> CloneRoutes(
        Dictionary<string, List<string>> routes) =>
        routes.ToDictionary(
            kv => kv.Key,
            kv => new List<string>(kv.Value),
            StringComparer.Ordinal);

    private static void RestoreRoutes(
        Dictionary<string, List<string>> routes,
        Dictionary<string, List<string>> snapshot)
    {
        foreach (var key in snapshot.Keys)
        {
            routes[key] = snapshot[key];
        }
    }

    private static void RemoveJob(
        Dictionary<string, List<string>> routes,
        HashSet<string> assigned,
        string jobId)
    {
        foreach (var route in routes.Values)
        {
            var idx = route.IndexOf(jobId);
            if (idx >= 0)
            {
                route.RemoveAt(idx);
                break;
            }
        }
        assigned.Remove(jobId);
    }

    // ----- Local search: exclusion trials --------------------------------

    /// <summary>
    /// Tests whether permanently dropping a combination of currently served
    /// jobs (size <paramref name="comboSize"/>) and greedily refilling with
    /// every other job unlocks a lexicographically better plan. This is the
    /// key move for "value trap" scenarios where one high-value job blocks
    /// several smaller jobs whose combined value is greater.
    /// </summary>
    private static bool TryExclusionImprovement(
        RoutePlanningProblem problem,
        List<Technician> technicians,
        List<ServiceJob> jobs,
        Dictionary<string, ServiceJob> jobsById,
        Dictionary<string, List<string>> routes,
        HashSet<string> assigned,
        int comboSize,
        Stopwatch stopwatch)
    {
        var (currentValue, currentTravel) = Evaluate(problem, technicians, routes, jobsById);
        var assignedOrdered = jobs
            .Where(j => assigned.Contains(j.Id))
            .Select(j => j.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var combos = comboSize == 1
            ? assignedOrdered.Select(id => new[] { id })
            : Pairs(assignedOrdered);

        foreach (var combo in combos)
        {
            if (stopwatch.Elapsed >= TimeBudget)
            {
                return false;
            }

            var snapshot = CloneRoutes(routes);
            var previouslyUnassigned = jobs
                .Where(j => !assigned.Contains(j.Id))
                .Select(j => j.Id)
                .ToList();

            foreach (var id in combo)
            {
                RemoveJob(routes, assigned, id);
            }

            GreedyFill(problem, technicians, jobsById, routes, assigned, previouslyUnassigned);

            var (newValue, newTravel) = Evaluate(problem, technicians, routes, jobsById);
            if (newValue > currentValue
                || (newValue == currentValue && newTravel < currentTravel))
            {
                return true;
            }

            RestoreRoutes(routes, snapshot);
            assigned.Clear();
            foreach (var route in routes.Values)
            {
                foreach (var id in route)
                {
                    assigned.Add(id);
                }
            }
        }
        return false;
    }

    private static IEnumerable<string[]> Pairs(List<string> ids)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            for (var j = i + 1; j < ids.Count; j++)
            {
                yield return [ids[i], ids[j]];
            }
        }
    }

    /// <summary>
    /// Tests whether relocating a currently served job to a different
    /// eligible technician (at its best position there) frees enough
    /// capacity on its original technician to admit additional currently
    /// unserved jobs, improving the lexicographic objective. Targets
    /// cross-technician reassignment opportunities that plain exclusion
    /// trials (which drop jobs outright) cannot find.
    /// </summary>
    private static bool TryRelocateAndFillImprovement(
        RoutePlanningProblem problem,
        List<Technician> technicians,
        Dictionary<string, ServiceJob> jobsById,
        Dictionary<string, List<string>> routes,
        HashSet<string> assigned,
        Stopwatch stopwatch)
    {
        var (currentValue, currentTravel) = Evaluate(problem, technicians, routes, jobsById);

        var ownerByJob = new List<(string JobId, string TechId)>();
        foreach (var tech in technicians)
        {
            foreach (var jobId in routes[tech.Id])
            {
                ownerByJob.Add((jobId, tech.Id));
            }
        }
        ownerByJob.Sort((a, b) => string.CompareOrdinal(a.JobId, b.JobId));

        foreach (var (jobId, ownerTechId) in ownerByJob)
        {
            if (stopwatch.Elapsed >= TimeBudget)
            {
                return false;
            }

            var job = jobsById[jobId];
            foreach (var targetTech in technicians)
            {
                if (string.Equals(targetTech.Id, ownerTechId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!RouteValidator.HasSkills(targetTech, job))
                {
                    continue;
                }
                if (!TryBestPositionInRoute(
                        problem, targetTech, routes[targetTech.Id], jobsById, job,
                        out var pos, out _))
                {
                    continue;
                }

                var snapshot = CloneRoutes(routes);
                var previouslyUnassigned = jobsById.Keys
                    .Where(id => !assigned.Contains(id))
                    .ToList();

                RemoveJob(routes, assigned, jobId);
                routes[targetTech.Id].Insert(pos, jobId);
                assigned.Add(jobId);

                GreedyFill(
                    problem, technicians, jobsById, routes, assigned,
                    previouslyUnassigned);

                var (newValue, newTravel) = Evaluate(problem, technicians, routes, jobsById);
                if (newValue > currentValue
                    || (newValue == currentValue && newTravel < currentTravel))
                {
                    return true;
                }

                RestoreRoutes(routes, snapshot);
                assigned.Clear();
                foreach (var route in routes.Values)
                {
                    foreach (var id in route)
                    {
                        assigned.Add(id);
                    }
                }
            }
        }
        return false;
    }

    // ----- Local search: travel-only optimization ------------------------

    /// <summary>
    /// Reduces total travel without changing the served job set: or-opt
    /// relocation (within or across technician routes) and 2-opt segment
    /// reversal (within a route). Applies the first strictly improving move
    /// found, in deterministic scan order.
    /// </summary>
    private static bool TryTravelOptimization(
        RoutePlanningProblem problem,
        List<Technician> technicians,
        Dictionary<string, ServiceJob> jobsById,
        Dictionary<string, List<string>> routes)
    {
        // Or-opt / relocate (same-route or cross-route).
        foreach (var ownerTech in technicians)
        {
            var ownerRoute = routes[ownerTech.Id];
            for (var i = 0; i < ownerRoute.Count; i++)
            {
                var jobId = ownerRoute[i];
                var job = jobsById[jobId];
                var without = new List<string>(ownerRoute);
                without.RemoveAt(i);
                if (!TryEvaluateRouteIds(problem, ownerTech, without, jobsById, out var ownerTravelWithout))
                {
                    continue;
                }
                TryEvaluateRouteIds(problem, ownerTech, ownerRoute, jobsById, out var ownerTravelOriginal);

                foreach (var targetTech in technicians)
                {
                    if (!RouteValidator.HasSkills(targetTech, job))
                    {
                        continue;
                    }
                    var sameRoute = string.Equals(targetTech.Id, ownerTech.Id, StringComparison.Ordinal);
                    var baseRoute = sameRoute ? without : routes[targetTech.Id];
                    var baseTravel = sameRoute
                        ? ownerTravelWithout
                        : (TryEvaluateRouteIds(problem, targetTech, routes[targetTech.Id], jobsById, out var t)
                            ? t
                            : 0);
                    var originalCombined = sameRoute
                        ? ownerTravelOriginal
                        : ownerTravelOriginal + baseTravel;

                    for (var pos = 0; pos <= baseRoute.Count; pos++)
                    {
                        if (sameRoute && pos == i)
                        {
                            continue;
                        }
                        var trial = new List<string>(baseRoute);
                        trial.Insert(pos, jobId);
                        if (!TryEvaluateRouteIds(problem, targetTech, trial, jobsById, out var trialTravel))
                        {
                            continue;
                        }
                        var newCombined = sameRoute ? trialTravel : ownerTravelWithout + trialTravel;
                        if (newCombined < originalCombined)
                        {
                            ownerRoute.RemoveAt(i);
                            if (sameRoute)
                            {
                                routes[ownerTech.Id].Insert(pos, jobId);
                            }
                            else
                            {
                                routes[targetTech.Id].Insert(pos, jobId);
                            }
                            return true;
                        }
                    }
                }
            }
        }

        // 2-opt: reverse a segment within a route.
        foreach (var tech in technicians)
        {
            var route = routes[tech.Id];
            if (route.Count < 3)
            {
                continue;
            }
            TryEvaluateRouteIds(problem, tech, route, jobsById, out var originalTravel);
            for (var i = 0; i < route.Count - 1; i++)
            {
                for (var j = i + 1; j < route.Count; j++)
                {
                    var trial = new List<string>(route);
                    trial.Reverse(i, j - i + 1);
                    if (TryEvaluateRouteIds(problem, tech, trial, jobsById, out var trialTravel)
                        && trialTravel < originalTravel)
                    {
                        routes[tech.Id] = trial;
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
