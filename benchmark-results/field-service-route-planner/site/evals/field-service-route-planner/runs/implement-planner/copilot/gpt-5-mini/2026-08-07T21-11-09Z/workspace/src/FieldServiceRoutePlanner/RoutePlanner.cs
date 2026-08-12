using System;
using System.Collections.Generic;
using System.Linq;

namespace FieldServiceRoutePlanner;

public sealed class RoutePlanner
{
    public RoutePlan Plan(RoutePlanningProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        // Deterministic ordering of jobs and technicians
        var jobs = problem.Jobs.OrderBy(j => j.Id, StringComparer.Ordinal).ToList();
        var technicians = problem.Technicians.OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
        var jobCount = jobs.Count;

        // Map job id -> index (global index)
        var jobIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < jobCount; i++) jobIndex[jobs[i].Id] = i;

        // Precompute job attributes for quick access
        var jobLocations = jobs.Select(j => j.Location).ToArray();
        var jobDurations = jobs.Select(j => j.Duration).ToArray();
        var jobWindowStart = jobs.Select(j => j.WindowStart).ToArray();
        var jobWindowEnd = jobs.Select(j => j.WindowEnd).ToArray();
        var jobValue = jobs.Select(j => j.Value).ToArray();
        var jobRequiredSkills = jobs.Select(j => new HashSet<string>(j.RequiredSkills, StringComparer.Ordinal)).ToArray();

        // Precompute travel lookup
        static int Travel(RoutePlanningProblem p, string a, string b) => p.TravelTimes[a][b];

        // For each technician, compute feasible subsets (as global job mask) and best route order + travel
        // Use long for global masks for safety (support >31 jobs)
        var perTechFeasible = new List<Dictionary<long, (int Value, int Travel, List<string> Order)>>();

        foreach (var tech in technicians)
        {
            // Find jobs that this technician has skills for
            var techSkills = new HashSet<string>(tech.Skills, StringComparer.Ordinal);
            var localGlobalIndices = new List<int>();
            for (int gi = 0; gi < jobCount; gi++)
            {
                if (jobRequiredSkills[gi].All(s => techSkills.Contains(s)))
                {
                    localGlobalIndices.Add(gi);
                }
            }

            var feasible = new Dictionary<long, (int Value, int Travel, List<string> Order)>();

            // Always include empty subset
            feasible[0L] = (0, 0, new List<string>());

            var m = localGlobalIndices.Count;
            if (m == 0)
            {
                perTechFeasible.Add(feasible);
                continue;
            }

            var maxLocalMask = 1 << m;
            var INF = int.MaxValue / 4;

            // dpEnd[mask][lastLocal] = earliest service end time
            var dpEnd = new int[maxLocalMask][];
            var dpTravel = new int[maxLocalMask][];
            var dpPrev = new int[maxLocalMask][]; // store previous local last index or -1
            for (int mask = 0; mask < maxLocalMask; mask++)
            {
                dpEnd[mask] = Enumerable.Repeat(INF, m).ToArray();
                dpTravel[mask] = Enumerable.Repeat(INF, m).ToArray();
                dpPrev[mask] = Enumerable.Repeat(-1, m).ToArray();
            }

            // Singleton starts
            for (int li = 0; li < m; li++)
            {
                var gj = localGlobalIndices[li];
                var travelTo = Travel(problem, problem.Depot, jobLocations[gj]);
                var arrival = tech.ShiftStart + travelTo;
                var serviceStart = Math.Max(arrival, jobWindowStart[gj]);
                var serviceEnd = serviceStart + jobDurations[gj];
                if (serviceEnd > jobWindowEnd[gj]) continue; // cannot serve
                dpEnd[1 << li][li] = serviceEnd;
                dpTravel[1 << li][li] = travelTo;
                dpPrev[1 << li][li] = -1;
            }

            // Build up DP for larger sets
            for (int mask = 1; mask < maxLocalMask; mask++)
            {
                for (int last = 0; last < m; last++)
                {
                    if ((mask & (1 << last)) == 0) continue;
                    var curEnd = dpEnd[mask][last];
                    var curTravel = dpTravel[mask][last];
                    if (curEnd == INF) continue;

                    // Try to extend by adding another local job 'next'
                    for (int next = 0; next < m; next++)
                    {
                        if ((mask & (1 << next)) != 0) continue;
                        var gjLast = localGlobalIndices[last];
                        var gjNext = localGlobalIndices[next];
                        var travelBetween = Travel(problem, jobLocations[gjLast], jobLocations[gjNext]);
                        var arrival = curEnd + travelBetween;
                        var serviceStart = Math.Max(arrival, jobWindowStart[gjNext]);
                        var serviceEnd = serviceStart + jobDurations[gjNext];
                        if (serviceEnd > jobWindowEnd[gjNext]) continue;
                        var newMask = mask | (1 << next);
                        var newTravel = curTravel + travelBetween;

                        var prevEnd = dpEnd[newMask][next];
                        var prevTravel = dpTravel[newMask][next];
                        // Prefer earlier serviceEnd, then smaller travel
                        if (serviceEnd < prevEnd || (serviceEnd == prevEnd && newTravel < prevTravel))
                        {
                            dpEnd[newMask][next] = serviceEnd;
                            dpTravel[newMask][next] = newTravel;
                            dpPrev[newMask][next] = last;
                        }
                    }
                }

                // Also consider transitions from singleton (handled above) - already covered
            }

            // Evaluate feasible global subsets from dp table: need to ensure return to depot before shift end
            for (int mask = 1; mask < maxLocalMask; mask++)
            {
                // Convert local mask to global mask
                long globalMask = 0L;
                int valueSum = 0;
                for (int li = 0; li < m; li++)
                {
                    if ((mask & (1 << li)) != 0)
                    {
                        var gj = localGlobalIndices[li];
                        globalMask |= (1L << gj);
                        valueSum += jobValue[gj];
                    }
                }

                // For each possible last, check return feasibility and pick minimal total travel
                int bestTotalTravel = INF;
                int bestLast = -1;
                int bestEnd = INF;
                for (int last = 0; last < m; last++)
                {
                    if ((mask & (1 << last)) == 0) continue;
                    var end = dpEnd[mask][last];
                    if (end == INF) continue;
                    var gjLast = localGlobalIndices[last];
                    var returnTravel = Travel(problem, jobLocations[gjLast], problem.Depot);
                    var returnTime = end + returnTravel;
                    if (returnTime > tech.ShiftEnd) continue;
                    var totalTravel = dpTravel[mask][last] + returnTravel;
                    if (totalTravel < bestTotalTravel || (totalTravel == bestTotalTravel && end < bestEnd))
                    {
                        bestTotalTravel = totalTravel;
                        bestLast = last;
                        bestEnd = end;
                    }
                }

                if (bestLast == -1) continue; // no feasible ending

                // Reconstruct order
                var orderLocal = new List<int>();
                var curMask = mask;
                var curLast = bestLast;
                while (curLast != -1)
                {
                    orderLocal.Add(localGlobalIndices[curLast]);
                    var prev = dpPrev[curMask][curLast];
                    curMask ^= (1 << curLast);
                    curLast = prev;
                }
                orderLocal.Reverse();
                var orderIds = orderLocal.Select(idx => jobs[idx].Id).ToList();

                // If this globalMask already present, pick the smaller travel (and deterministic tie-breaker)
                if (feasible.TryGetValue(globalMask, out var existing))
                {
                    if (valueSum > existing.Value || (valueSum == existing.Value && bestTotalTravel < existing.Travel))
                    {
                        feasible[globalMask] = (valueSum, bestTotalTravel, orderIds);
                    }
                }
                else
                {
                    feasible[globalMask] = (valueSum, bestTotalTravel, orderIds);
                }
            }

            perTechFeasible.Add(feasible);
        }

        // Global DP combining technicians sequentially to choose disjoint subsets maximizing total value then minimizing travel
        var dp = new Dictionary<long, (int Value, int Travel, List<long> Chosen)>();
        dp[0L] = (0, 0, new List<long>());

        for (int ti = 0; ti < technicians.Count; ti++)
        {
            var feasible = perTechFeasible[ti];
            // Ensure deterministic iteration order of feasible subsets
            var feasibleItems = feasible.OrderBy(kv => kv.Key).ToList();

            var next = new Dictionary<long, (int Value, int Travel, List<long>)>();
            foreach (var kv in dp.OrderBy(k => k.Key)) // deterministic
            {
                var baseMask = kv.Key;
                var baseVal = kv.Value.Value;
                var baseTravel = kv.Value.Travel;
                var baseChosen = kv.Value.Chosen;

                // For each feasible subset for this technician
                foreach (var f in feasibleItems)
                {
                    var subsetMask = f.Key;
                    if ((baseMask & subsetMask) != 0) continue; // overlap
                    var newMask = baseMask | subsetMask;
                    var newVal = baseVal + f.Value.Value;
                    var newTravel = baseTravel + f.Value.Travel;

                    // Build new chosen list
                    var newChosen = new List<long>(baseChosen) { subsetMask };

                    if (next.TryGetValue(newMask, out var prev))
                    {
                        if (newVal > prev.Value || (newVal == prev.Value && newTravel < prev.Travel))
                        {
                            next[newMask] = (newVal, newTravel, newChosen);
                        }
                    }
                    else
                    {
                        next[newMask] = (newVal, newTravel, newChosen);
                    }
                }
            }

            dp = next;
        }

        // Select best final plan: max value, then min travel. Deterministic tie-break by mask order.
        int bestValue = -1;
        int bestTravel = int.MaxValue;
        long bestMaskKey = -1;
        List<long>? bestChosen = null;
        foreach (var kv in dp.OrderBy(k => k.Key))
        {
            var mask = kv.Key;
            var val = kv.Value.Value;
            var travel = kv.Value.Travel;
            if (val > bestValue || (val == bestValue && travel < bestTravel))
            {
                bestValue = val;
                bestTravel = travel;
                bestMaskKey = mask;
                bestChosen = kv.Value.Chosen;
            }
        }

        // If no dp entries (shouldn't happen), return empty routes for every technician
        if (bestChosen == null)
        {
            // return one empty route per technician (canonical order)
            var emptyRoutes = technicians.Select(t => new TechnicianRoute(t.Id, new List<string>())).ToList();
            return new RoutePlan(emptyRoutes);
        }

        // Build final routes in technician order
        var routes = new List<TechnicianRoute>();
        for (int ti = 0; ti < technicians.Count; ti++)
        {
            long subsetMask = bestChosen[ti];
            if (subsetMask == 0)
            {
                routes.Add(new TechnicianRoute(technicians[ti].Id, new List<string>()));
                continue;
            }
            var order = perTechFeasible[ti][subsetMask].Order;
            routes.Add(new TechnicianRoute(technicians[ti].Id, new List<string>(order)));
        }

        return new RoutePlan(routes);
    }
}
