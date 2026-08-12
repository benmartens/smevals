using System.Collections.Generic;
using System.Linq;

namespace FieldServiceRoutePlanner;

public sealed class RoutePlanner
{
    public RoutePlan Plan(RoutePlanningProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var technicians = problem.Technicians
            .OrderBy(technician => technician.Id, StringComparer.Ordinal)
            .ToList();
        if (technicians.Count == 0)
        {
            return new RoutePlan([]);
        }

        var jobs = problem.Jobs
            .OrderBy(job => job.Id, StringComparer.Ordinal)
            .ToList();
        var jobIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < jobs.Count; index++)
        {
            jobIndex[jobs[index].Id] = index;
        }

        var routesByTechnician = new List<List<RouteCandidate>>();
        foreach (var technician in technicians)
        {
            routesByTechnician.Add(EnumerateRoutes(problem, technician, jobs, jobIndex));
        }

        var memo = new Dictionary<(int TechnicianIndex, ulong UsedMask), RouteSelection>();
        var routeChoice = new Dictionary<(int TechnicianIndex, ulong UsedMask), int>();

        RouteSelection Solve(int technicianIndex, ulong usedMask)
        {
            if (technicianIndex == routesByTechnician.Count)
            {
                return new RouteSelection(0, 0, -1);
            }

            var key = (technicianIndex, usedMask);
            if (memo.TryGetValue(key, out var cached))
            {
                return cached;
            }

            RouteSelection? best = null;
            var bestRouteIndex = -1;
            for (var routeIndex = 0; routeIndex < routesByTechnician[technicianIndex].Count; routeIndex++)
            {
                var route = routesByTechnician[technicianIndex][routeIndex];
                if ((route.JobMask & usedMask) != 0UL)
                {
                    continue;
                }

                var next = Solve(technicianIndex + 1, usedMask | route.JobMask);
                var candidateValue = route.Value + next.Value;
                var candidateTravel = route.Travel + next.Travel;
                if (best is null || IsBetter(candidateValue, candidateTravel, best.Value, best.Travel))
                {
                    best = new RouteSelection(candidateValue, candidateTravel, routeIndex);
                    bestRouteIndex = routeIndex;
                }
            }

            if (best is null)
            {
                best = new RouteSelection(0, 0, -1);
            }

            routeChoice[key] = bestRouteIndex;
            memo[key] = best;
            return best;
        }

        _ = Solve(0, 0UL);

        var planRoutes = new List<TechnicianRoute>(technicians.Count);
        ulong currentMask = 0UL;
        for (var technicianIndex = 0; technicianIndex < technicians.Count; technicianIndex++)
        {
            var routeIndex = routeChoice[(technicianIndex, currentMask)];
            var route = routesByTechnician[technicianIndex][routeIndex];
            planRoutes.Add(new TechnicianRoute(technicians[technicianIndex].Id, route.JobIds.ToList()));
            currentMask |= route.JobMask;
        }

        return new RoutePlan(planRoutes);
    }

    private static bool IsBetter(int candidateValue, int candidateTravel, int currentValue, int currentTravel)
    {
        if (candidateValue != currentValue)
        {
            return candidateValue > currentValue;
        }

        return candidateTravel < currentTravel;
    }

    private static bool HasSkills(Technician technician, ServiceJob job)
    {
        var skills = new HashSet<string>(technician.Skills, StringComparer.Ordinal);
        return job.RequiredSkills.All(skill => skills.Contains(skill));
    }

    private static List<RouteCandidate> EnumerateRoutes(
        RoutePlanningProblem problem,
        Technician technician,
        List<ServiceJob> jobs,
        Dictionary<string, int> jobIndex)
    {
        var routes = new List<RouteCandidate>();
        routes.Add(new RouteCandidate(0UL, 0, 0, []));

        var feasibleJobs = jobs
            .Where(job => HasSkills(technician, job))
            .OrderBy(job => job.Id, StringComparer.Ordinal)
            .ToList();

        var prefix = new List<string>();
        void Search(
            string currentLocation,
            int currentTime,
            int currentTravel,
            int currentValue,
            ulong currentMask)
        {
            if (prefix.Count > 0)
            {
                var returnTravel = GetTravel(problem, currentLocation, problem.Depot);
                if (returnTravel >= 0 && currentTime + returnTravel <= technician.ShiftEnd)
                {
                    routes.Add(new RouteCandidate(
                        currentMask,
                        currentValue,
                        currentTravel + returnTravel,
                        new List<string>(prefix)));
                }
            }

            if (prefix.Count > 0)
            {
                var returnTravel = GetTravel(problem, currentLocation, problem.Depot);
                if (returnTravel < 0 || currentTime + returnTravel > technician.ShiftEnd)
                {
                    return;
                }
            }

            foreach (var job in feasibleJobs)
            {
                if ((currentMask & (1UL << jobIndex[job.Id])) != 0UL)
                {
                    continue;
                }

                if (!TryAppendJob(
                        problem,
                        technician,
                        job,
                        currentLocation,
                        currentTime,
                        currentTravel,
                        currentValue,
                        currentMask,
                        jobIndex,
                        out var nextLocation,
                        out var nextTime,
                        out var nextTravel,
                        out var nextValue,
                        out var nextMask))
                {
                    continue;
                }

                prefix.Add(job.Id);
                Search(nextLocation, nextTime, nextTravel, nextValue, nextMask);
                prefix.RemoveAt(prefix.Count - 1);
            }
        }

        Search(problem.Depot, technician.ShiftStart, 0, 0, 0UL);

        routes.Sort(CompareRouteCandidates);
        return routes;
    }

    private static bool TryAppendJob(
        RoutePlanningProblem problem,
        Technician technician,
        ServiceJob job,
        string currentLocation,
        int currentTime,
        int currentTravel,
        int currentValue,
        ulong currentMask,
        Dictionary<string, int> jobIndex,
        out string nextLocation,
        out int nextTime,
        out int nextTravel,
        out int nextValue,
        out ulong nextMask)
    {
        var travel = GetTravel(problem, currentLocation, job.Location);
        if (travel < 0)
        {
            nextLocation = string.Empty;
            nextTime = 0;
            nextTravel = 0;
            nextValue = 0;
            nextMask = 0UL;
            return false;
        }

        var arrival = currentTime + travel;
        var serviceStart = Math.Max(arrival, job.WindowStart);
        var serviceEnd = serviceStart + job.Duration;
        if (serviceEnd > job.WindowEnd)
        {
            nextLocation = string.Empty;
            nextTime = 0;
            nextTravel = 0;
            nextValue = 0;
            nextMask = 0UL;
            return false;
        }

        var returnTravel = GetTravel(problem, job.Location, problem.Depot);
        if (returnTravel < 0 || serviceEnd + returnTravel > technician.ShiftEnd)
        {
            nextLocation = string.Empty;
            nextTime = 0;
            nextTravel = 0;
            nextValue = 0;
            nextMask = 0UL;
            return false;
        }

        nextLocation = job.Location;
        nextTime = serviceEnd;
        nextTravel = currentTravel + travel;
        nextValue = currentValue + job.Value;
        nextMask = currentMask | (1UL << jobIndex[job.Id]);
        return true;
    }

    private static int GetTravel(RoutePlanningProblem problem, string from, string to)
    {
        if (!problem.TravelTimes.TryGetValue(from, out var row) || !row.TryGetValue(to, out var travel))
        {
            return -1;
        }

        return travel;
    }

    private static int CompareRouteCandidates(RouteCandidate left, RouteCandidate right)
    {
        var leftKey = string.Join("\u001f", left.JobIds);
        var rightKey = string.Join("\u001f", right.JobIds);
        return StringComparer.Ordinal.Compare(leftKey, rightKey);
    }

    private sealed record RouteCandidate(ulong JobMask, int Value, int Travel, List<string> JobIds);

    private sealed record RouteSelection(int Value, int Travel, int RouteIndex);
}
