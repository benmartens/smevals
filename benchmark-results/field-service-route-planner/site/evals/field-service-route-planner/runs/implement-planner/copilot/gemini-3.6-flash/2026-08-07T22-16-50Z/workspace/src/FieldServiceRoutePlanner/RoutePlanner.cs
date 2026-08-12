using System.Numerics;

namespace FieldServiceRoutePlanner;

public sealed class RoutePlanner
{
    public RoutePlan Plan(RoutePlanningProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        if (problem.Technicians.Count == 0)
        {
            return new RoutePlan([]);
        }

        var solver = new InternalSolver(problem);
        return solver.Solve();
    }
}

internal sealed class InternalSolver
{
    private const int MAX_BB_NODES = 300_000;

    private readonly RoutePlanningProblem _problem;
    private readonly TechData[] _techs;
    private readonly JobData[] _jobs;
    private readonly int[,] _travel;
    private readonly int _depotLoc;
    private readonly bool[,] _skillMatch;
    private readonly bool[] _isFeasibleSingleAny;
    private readonly long[] _techCanServeMask;
    private readonly long[] _remainingTechsMask;
    private readonly bool[] _isIdenticalTechToNext;

    private readonly Dictionary<MemoKey, List<(int Time, int Travel)>> _memo = new();

    private PlanState _bestPlan;
    private PlanState _currentPlan;
    private int _bbNodes;
    private bool _bbTimedOut;

    public InternalSolver(RoutePlanningProblem problem)
    {
        _problem = problem;

        var sortedTechs = problem.Technicians
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .ToList();

        _techs = new TechData[sortedTechs.Count];
        for (int k = 0; k < sortedTechs.Count; k++)
        {
            var t = sortedTechs[k];
            _techs[k] = new TechData(
                k,
                t.Id,
                t.ShiftStart,
                t.ShiftEnd,
                new HashSet<string>(t.Skills, StringComparer.Ordinal));
        }

        var locMap = new Dictionary<string, int>(StringComparer.Ordinal);
        int GetLocIndex(string loc)
        {
            if (locMap.TryGetValue(loc, out int idx)) return idx;
            int newIdx = locMap.Count;
            locMap[loc] = newIdx;
            return newIdx;
        }

        _depotLoc = GetLocIndex(problem.Depot);

        var sortedJobs = problem.Jobs
            .OrderByDescending(j => j.Value)
            .ThenBy(j => j.WindowStart)
            .ThenBy(j => j.Id, StringComparer.Ordinal)
            .ToList();

        _jobs = new JobData[sortedJobs.Count];
        for (int j = 0; j < sortedJobs.Count; j++)
        {
            var job = sortedJobs[j];
            _jobs[j] = new JobData(
                j,
                job.Id,
                GetLocIndex(job.Location),
                job.Location,
                job.Duration,
                job.WindowStart,
                job.WindowEnd,
                job.Value,
                new HashSet<string>(job.RequiredSkills, StringComparer.Ordinal));
        }

        int numLocs = locMap.Count;
        _travel = new int[numLocs, numLocs];
        foreach (var (fromLoc, row) in problem.TravelTimes)
        {
            if (locMap.TryGetValue(fromLoc, out int fromIdx))
            {
                foreach (var (toLoc, minutes) in row)
                {
                    if (locMap.TryGetValue(toLoc, out int toIdx))
                    {
                        _travel[fromIdx, toIdx] = minutes;
                    }
                }
            }
        }

        int K = _techs.Length;
        int N = _jobs.Length;
        _skillMatch = new bool[K, N];
        _isFeasibleSingleAny = new bool[N];
        _techCanServeMask = new long[K];
        _remainingTechsMask = new long[K];

        for (int k = 0; k < K; k++)
        {
            long mask = 0;
            for (int j = 0; j < N; j++)
            {
                bool match = _jobs[j].RequiredSkills.IsSubsetOf(_techs[k].Skills);
                _skillMatch[k, j] = match;

                if (match && IsFeasibleSingle(k, j))
                {
                    _isFeasibleSingleAny[j] = true;
                    if (j < 64)
                    {
                        mask |= (1L << j);
                    }
                }
            }
            _techCanServeMask[k] = mask;
        }

        long cumulative = 0;
        for (int k = K - 1; k >= 0; k--)
        {
            cumulative |= _techCanServeMask[k];
            _remainingTechsMask[k] = cumulative;
        }

        _isIdenticalTechToNext = new bool[K];
        for (int k = 0; k < K - 1; k++)
        {
            _isIdenticalTechToNext[k] = IsIdenticalTech(_techs[k], _techs[k + 1]);
        }

        _bestPlan = new PlanState(K);
        _currentPlan = new PlanState(K);
    }

    public RoutePlan Solve()
    {
        _bestPlan = new PlanState(_techs.Length);
        TryEvaluatePlan(_bestPlan);

        if (_jobs.Length <= 64)
        {
            _bbNodes = 0;
            _bbTimedOut = false;
            _memo.Clear();

            BranchAndBound(
                0,
                _depotLoc,
                _techs[0].ShiftStart,
                0,
                0,
                0,
                0L,
                new List<int>());
        }
        else
        {
            _bbTimedOut = true;
        }

        if (_bbTimedOut)
        {
            RunGreedyConstruction();
            RunALNS();
        }

        RunLocalSearch(_bestPlan);

        return BuildRoutePlan(_bestPlan);
    }

    private void BranchAndBound(
        int techIdx,
        int currentLoc,
        int currentTime,
        int currentTechTravel,
        int totalTravelSoFar,
        int currentServedValue,
        long assignedMask,
        List<int> currentTechRoute)
    {
        _bbNodes++;
        if (_bbNodes > MAX_BB_NODES)
        {
            _bbTimedOut = true;
            return;
        }

        int upperVal = currentServedValue + GetUpperValueBound(techIdx, assignedMask);
        if (upperVal < _bestPlan.ServedValue)
        {
            return;
        }
        if (upperVal == _bestPlan.ServedValue)
        {
            int lowerTravel = totalTravelSoFar + currentTechTravel + _travel[currentLoc, _depotLoc];
            if (lowerTravel > _bestPlan.TotalTravel)
            {
                return;
            }
        }

        var key = new MemoKey(techIdx, currentLoc, assignedMask);
        if (IsDominated(key, currentTime, currentTechTravel))
        {
            return;
        }
        AddMemo(key, currentTime, currentTechTravel);

        var tech = _techs[techIdx];
        long candidateMask = (~assignedMask) & _techCanServeMask[techIdx];
        long tempMask = candidateMask;

        while (tempMask != 0)
        {
            int j = BitOperations.TrailingZeroCount((ulong)tempMask);
            tempMask &= tempMask - 1;

            var job = _jobs[j];
            int t = _travel[currentLoc, job.LocIndex];
            int arr = currentTime + t;
            int start = Math.Max(arr, job.WindowStart);
            int end = start + job.Duration;

            if (end <= job.WindowEnd)
            {
                int retT = _travel[job.LocIndex, _depotLoc];
                if (end + retT <= tech.ShiftEnd)
                {
                    currentTechRoute.Add(j);
                    BranchAndBound(
                        techIdx,
                        job.LocIndex,
                        end,
                        currentTechTravel + t,
                        totalTravelSoFar,
                        currentServedValue + job.Value,
                        assignedMask | (1L << j),
                        currentTechRoute);
                    currentTechRoute.RemoveAt(currentTechRoute.Count - 1);

                    if (_bbTimedOut) return;
                }
            }
        }

        int returnTravel = _travel[currentLoc, _depotLoc];
        int finalRouteTravel = currentTechTravel + returnTravel;
        _currentPlan.Routes[techIdx] = currentTechRoute;

        if (techIdx + 1 < _techs.Length)
        {
            bool isSymmetricEmpty = _isIdenticalTechToNext[techIdx] && currentTechRoute.Count == 0;
            if (!isSymmetricEmpty)
            {
                BranchAndBound(
                    techIdx + 1,
                    _depotLoc,
                    _techs[techIdx + 1].ShiftStart,
                    0,
                    totalTravelSoFar + finalRouteTravel,
                    currentServedValue,
                    assignedMask,
                    new List<int>());
            }
        }
        else
        {
            _currentPlan.ServedValue = currentServedValue;
            _currentPlan.TotalTravel = totalTravelSoFar + finalRouteTravel;

            if (IsBetter(_currentPlan, _bestPlan, _jobs))
            {
                _bestPlan = _currentPlan.Clone();
            }
        }
    }

    private int GetUpperValueBound(int techIdx, long assignedMask)
    {
        if (techIdx >= _techs.Length) return 0;
        long eligibleMask = (~assignedMask) & _remainingTechsMask[techIdx];
        int bound = 0;
        while (eligibleMask != 0)
        {
            int j = BitOperations.TrailingZeroCount((ulong)eligibleMask);
            bound += _jobs[j].Value;
            eligibleMask &= eligibleMask - 1;
        }
        return bound;
    }

    private bool IsDominated(MemoKey key, int time, int travel)
    {
        if (_memo.TryGetValue(key, out var list))
        {
            foreach (var (t, tr) in list)
            {
                if (t <= time && tr <= travel)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void AddMemo(MemoKey key, int time, int travel)
    {
        if (!_memo.TryGetValue(key, out var list))
        {
            list = new List<(int, int)>(2);
            _memo[key] = list;
        }
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (time <= list[i].Time && travel <= list[i].Travel)
            {
                list.RemoveAt(i);
            }
        }
        list.Add((time, travel));
    }

    private bool IsFeasibleSingle(int k, int j)
    {
        var tech = _techs[k];
        var job = _jobs[j];
        int t1 = _travel[_depotLoc, job.LocIndex];
        int t2 = _travel[job.LocIndex, _depotLoc];
        int arr = tech.ShiftStart + t1;
        int start = Math.Max(arr, job.WindowStart);
        int end = start + job.Duration;
        if (end > job.WindowEnd) return false;
        int ret = end + t2;
        return ret <= tech.ShiftEnd;
    }

    private static bool IsIdenticalTech(TechData t1, TechData t2)
    {
        return t1.ShiftStart == t2.ShiftStart
            && t1.ShiftEnd == t2.ShiftEnd
            && t1.Skills.SetEquals(t2.Skills);
    }

    private void RunGreedyConstruction()
    {
        var plan = new PlanState(_techs.Length);
        var assigned = new HashSet<int>();

        while (true)
        {
            int bestJ = -1;
            int bestK = -1;
            int bestPos = -1;
            double bestScore = -1.0;
            PlanState? bestCandPlan = null;

            for (int j = 0; j < _jobs.Length; j++)
            {
                if (assigned.Contains(j) || !_isFeasibleSingleAny[j]) continue;

                for (int k = 0; k < _techs.Length; k++)
                {
                    if (!_skillMatch[k, j]) continue;

                    var route = plan.Routes[k];
                    for (int pos = 0; pos <= route.Count; pos++)
                    {
                        var candRoute = new List<int>(route);
                        candRoute.Insert(pos, j);

                        if (TryEvaluateRoute(k, candRoute, out int newTravel, out _))
                        {
                            TryEvaluateRoute(k, route, out int oldTravel, out _);
                            int deltaTravel = newTravel - oldTravel;
                            double score = (double)_jobs[j].Value / (deltaTravel + 1.0);

                            var candPlan = plan.Clone();
                            candPlan.Routes[k] = candRoute;
                            TryEvaluatePlan(candPlan);

                            if (score > bestScore || (score == bestScore && IsBetter(candPlan, bestCandPlan ?? plan, _jobs)))
                            {
                                bestScore = score;
                                bestJ = j;
                                bestK = k;
                                bestPos = pos;
                                bestCandPlan = candPlan;
                            }
                        }
                    }
                }
            }

            if (bestJ != -1 && bestCandPlan != null)
            {
                plan = bestCandPlan;
                assigned.Add(bestJ);
            }
            else
            {
                break;
            }
        }

        if (IsBetter(plan, _bestPlan, _jobs))
        {
            _bestPlan = plan;
        }
    }

    private void RunALNS()
    {
        var rng = new Random(42);
        var currentPlan = _bestPlan.Clone();

        for (int iter = 0; iter < 500; iter++)
        {
            var candPlan = currentPlan.Clone();
            int totalAssigned = candPlan.Routes.Sum(r => r.Count);

            if (totalAssigned > 0)
            {
                int q = rng.Next(1, Math.Min(totalAssigned, 6) + 1);
                int destroyType = rng.Next(3);

                if (destroyType == 0)
                {
                    for (int i = 0; i < q; i++)
                    {
                        var nonSeqRoutes = candPlan.Routes.Where(r => r.Count > 0).ToList();
                        if (nonSeqRoutes.Count == 0) break;
                        var targetRoute = nonSeqRoutes[rng.Next(nonSeqRoutes.Count)];
                        targetRoute.RemoveAt(rng.Next(targetRoute.Count));
                    }
                }
                else if (destroyType == 1)
                {
                    for (int i = 0; i < q; i++)
                    {
                        int lowestValJ = -1;
                        int targetK = -1;
                        int targetIdx = -1;
                        int minVal = int.MaxValue;

                        for (int k = 0; k < candPlan.Routes.Length; k++)
                        {
                            var r = candPlan.Routes[k];
                            for (int idx = 0; idx < r.Count; idx++)
                            {
                                int val = _jobs[r[idx]].Value;
                                if (val < minVal)
                                {
                                    minVal = val;
                                    lowestValJ = r[idx];
                                    targetK = k;
                                    targetIdx = idx;
                                }
                            }
                        }

                        if (targetK != -1)
                        {
                            candPlan.Routes[targetK].RemoveAt(targetIdx);
                        }
                    }
                }
                else
                {
                    var nonSeqRoutes = candPlan.Routes.Where(r => r.Count > 0).ToList();
                    if (nonSeqRoutes.Count > 0)
                    {
                        nonSeqRoutes[rng.Next(nonSeqRoutes.Count)].Clear();
                    }
                }

                TryEvaluatePlan(candPlan);
            }

            GreedyRepair(candPlan);
            RunLocalSearch(candPlan);

            if (IsBetter(candPlan, currentPlan, _jobs))
            {
                currentPlan = candPlan.Clone();
            }
            if (IsBetter(candPlan, _bestPlan, _jobs))
            {
                _bestPlan = candPlan.Clone();
            }
        }
    }

    private void GreedyRepair(PlanState plan)
    {
        var assigned = new HashSet<int>();
        foreach (var r in plan.Routes)
        {
            foreach (int j in r) assigned.Add(j);
        }

        while (true)
        {
            int bestJ = -1;
            int bestK = -1;
            PlanState? bestCandPlan = null;

            for (int j = 0; j < _jobs.Length; j++)
            {
                if (assigned.Contains(j) || !_isFeasibleSingleAny[j]) continue;

                for (int k = 0; k < _techs.Length; k++)
                {
                    if (!_skillMatch[k, j]) continue;

                    var route = plan.Routes[k];
                    for (int pos = 0; pos <= route.Count; pos++)
                    {
                        var candRoute = new List<int>(route);
                        candRoute.Insert(pos, j);

                        if (TryEvaluateRoute(k, candRoute, out _, out _))
                        {
                            var candPlan = plan.Clone();
                            candPlan.Routes[k] = candRoute;
                            TryEvaluatePlan(candPlan);

                            if (bestCandPlan == null || IsBetter(candPlan, bestCandPlan, _jobs))
                            {
                                bestJ = j;
                                bestK = k;
                                bestCandPlan = candPlan;
                            }
                        }
                    }
                }
            }

            if (bestJ != -1 && bestCandPlan != null)
            {
                plan.Routes[bestK] = bestCandPlan.Routes[bestK];
                TryEvaluatePlan(plan);
                assigned.Add(bestJ);
            }
            else
            {
                break;
            }
        }
    }

    private void RunLocalSearch(PlanState plan)
    {
        bool improved = true;
        int pass = 0;

        while (improved && pass++ < 50)
        {
            improved = false;

            if (TryInsertUnassignedJobs(plan)) { improved = true; continue; }
            if (TryIntraRoute2Opt(plan)) { improved = true; continue; }
            if (TryRelocateJob(plan)) { improved = true; continue; }
            if (TrySwapJobs(plan)) { improved = true; continue; }
            if (TryReplaceAssignedWithUnassigned(plan)) { improved = true; continue; }
        }
    }

    private bool TryInsertUnassignedJobs(PlanState plan)
    {
        var assigned = new HashSet<int>();
        foreach (var r in plan.Routes) foreach (int j in r) assigned.Add(j);

        for (int j = 0; j < _jobs.Length; j++)
        {
            if (assigned.Contains(j) || !_isFeasibleSingleAny[j]) continue;

            for (int k = 0; k < _techs.Length; k++)
            {
                if (!_skillMatch[k, j]) continue;

                var route = plan.Routes[k];
                for (int pos = 0; pos <= route.Count; pos++)
                {
                    var candRoute = new List<int>(route);
                    candRoute.Insert(pos, j);

                    if (TryEvaluateRoute(k, candRoute, out _, out _))
                    {
                        var candPlan = plan.Clone();
                        candPlan.Routes[k] = candRoute;
                        TryEvaluatePlan(candPlan);

                        if (IsBetter(candPlan, plan, _jobs))
                        {
                            plan.Routes[k] = candRoute;
                            TryEvaluatePlan(plan);
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    private bool TryIntraRoute2Opt(PlanState plan)
    {
        for (int k = 0; k < _techs.Length; k++)
        {
            var route = plan.Routes[k];
            if (route.Count < 3) continue;

            for (int i = 0; i < route.Count - 1; i++)
            {
                for (int j = i + 1; j < route.Count; j++)
                {
                    var candRoute = new List<int>(route);
                    candRoute.Reverse(i, j - i + 1);

                    if (TryEvaluateRoute(k, candRoute, out _, out _))
                    {
                        var candPlan = plan.Clone();
                        candPlan.Routes[k] = candRoute;
                        TryEvaluatePlan(candPlan);

                        if (IsBetter(candPlan, plan, _jobs))
                        {
                            plan.Routes[k] = candRoute;
                            TryEvaluatePlan(plan);
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    private bool TryRelocateJob(PlanState plan)
    {
        for (int k1 = 0; k1 < _techs.Length; k1++)
        {
            var r1 = plan.Routes[k1];
            if (r1.Count == 0) continue;

            for (int p1 = 0; p1 < r1.Count; p1++)
            {
                int j = r1[p1];

                for (int k2 = 0; k2 < _techs.Length; k2++)
                {
                    if (!_skillMatch[k2, j]) continue;

                    var r2 = plan.Routes[k2];
                    int maxP2 = (k1 == k2) ? r2.Count - 1 : r2.Count;

                    for (int p2 = 0; p2 <= maxP2; p2++)
                    {
                        if (k1 == k2 && (p2 == p1 || p2 == p1 + 1)) continue;

                        var newR1 = new List<int>(r1);
                        newR1.RemoveAt(p1);

                        var newR2 = (k1 == k2) ? newR1 : new List<int>(r2);
                        newR2.Insert(p2, j);

                        if (TryEvaluateRoute(k1, newR1, out _, out _) && TryEvaluateRoute(k2, newR2, out _, out _))
                        {
                            var candPlan = plan.Clone();
                            candPlan.Routes[k1] = newR1;
                            candPlan.Routes[k2] = newR2;
                            TryEvaluatePlan(candPlan);

                            if (IsBetter(candPlan, plan, _jobs))
                            {
                                plan.Routes[k1] = newR1;
                                plan.Routes[k2] = newR2;
                                TryEvaluatePlan(plan);
                                return true;
                            }
                        }
                    }
                }
            }
        }
        return false;
    }

    private bool TrySwapJobs(PlanState plan)
    {
        for (int k1 = 0; k1 < _techs.Length; k1++)
        {
            var r1 = plan.Routes[k1];
            for (int p1 = 0; p1 < r1.Count; p1++)
            {
                int j1 = r1[p1];

                for (int k2 = k1; k2 < _techs.Length; k2++)
                {
                    var r2 = plan.Routes[k2];
                    int startP2 = (k1 == k2) ? p1 + 1 : 0;

                    for (int p2 = startP2; p2 < r2.Count; p2++)
                    {
                        int j2 = r2[p2];

                        if (!_skillMatch[k1, j2] || !_skillMatch[k2, j1]) continue;

                        var newR1 = new List<int>(r1);
                        var newR2 = (k1 == k2) ? newR1 : new List<int>(r2);

                        newR1[p1] = j2;
                        newR2[p2] = j1;

                        if (TryEvaluateRoute(k1, newR1, out _, out _) && TryEvaluateRoute(k2, newR2, out _, out _))
                        {
                            var candPlan = plan.Clone();
                            candPlan.Routes[k1] = newR1;
                            candPlan.Routes[k2] = newR2;
                            TryEvaluatePlan(candPlan);

                            if (IsBetter(candPlan, plan, _jobs))
                            {
                                plan.Routes[k1] = newR1;
                                plan.Routes[k2] = newR2;
                                TryEvaluatePlan(plan);
                                return true;
                            }
                        }
                    }
                }
            }
        }
        return false;
    }

    private bool TryReplaceAssignedWithUnassigned(PlanState plan)
    {
        var assigned = new HashSet<int>();
        foreach (var r in plan.Routes) foreach (int j in r) assigned.Add(j);

        for (int unassignedJ = 0; unassignedJ < _jobs.Length; unassignedJ++)
        {
            if (assigned.Contains(unassignedJ) || !_isFeasibleSingleAny[unassignedJ]) continue;

            for (int k = 0; k < _techs.Length; k++)
            {
                if (!_skillMatch[k, unassignedJ]) continue;

                var route = plan.Routes[k];
                for (int p = 0; p < route.Count; p++)
                {
                    var candRoute = new List<int>(route);
                    candRoute[p] = unassignedJ;

                    if (TryEvaluateRoute(k, candRoute, out _, out _))
                    {
                        var candPlan = plan.Clone();
                        candPlan.Routes[k] = candRoute;
                        TryEvaluatePlan(candPlan);

                        if (IsBetter(candPlan, plan, _jobs))
                        {
                            plan.Routes[k] = candRoute;
                            TryEvaluatePlan(plan);
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    private bool TryEvaluateRoute(int k, List<int> route, out int travelTime, out int returnTime)
    {
        travelTime = 0;
        returnTime = 0;
        var tech = _techs[k];
        int currLoc = _depotLoc;
        int currTime = tech.ShiftStart;

        foreach (int jIdx in route)
        {
            if (!_skillMatch[k, jIdx]) return false;
            var job = _jobs[jIdx];
            int t = _travel[currLoc, job.LocIndex];
            travelTime += t;
            int arr = currTime + t;
            int start = Math.Max(arr, job.WindowStart);
            int end = start + job.Duration;
            if (end > job.WindowEnd) return false;
            currLoc = job.LocIndex;
            currTime = end;
        }

        int retTravel = _travel[currLoc, _depotLoc];
        travelTime += retTravel;
        returnTime = currTime + retTravel;
        if (returnTime > tech.ShiftEnd) return false;

        return true;
    }

    private bool TryEvaluatePlan(PlanState plan)
    {
        int totalVal = 0;
        int totalTrav = 0;
        var assigned = new HashSet<int>();

        for (int k = 0; k < _techs.Length; k++)
        {
            var route = plan.Routes[k];
            if (!TryEvaluateRoute(k, route, out int trav, out _))
                return false;

            totalTrav += trav;
            foreach (int jIdx in route)
            {
                if (!assigned.Add(jIdx)) return false;
                totalVal += _jobs[jIdx].Value;
            }
        }

        plan.ServedValue = totalVal;
        plan.TotalTravel = totalTrav;
        return true;
    }

    private RoutePlan BuildRoutePlan(PlanState plan)
    {
        var routes = new List<TechnicianRoute>(_techs.Length);
        for (int k = 0; k < _techs.Length; k++)
        {
            var jobIds = plan.Routes[k].Select(jIdx => _jobs[jIdx].Id).ToList();
            routes.Add(new TechnicianRoute(_techs[k].Id, jobIds));
        }
        return new RoutePlan(routes);
    }

    private static bool IsBetter(PlanState cand, PlanState target, JobData[] jobs)
    {
        if (cand.ServedValue != target.ServedValue)
            return cand.ServedValue > target.ServedValue;

        if (cand.TotalTravel != target.TotalTravel)
            return cand.TotalTravel < target.TotalTravel;

        for (int k = 0; k < cand.Routes.Length; k++)
        {
            var rCand = cand.Routes[k];
            var rTarg = target.Routes[k];

            int minLen = Math.Min(rCand.Count, rTarg.Count);
            for (int i = 0; i < minLen; i++)
            {
                int cmp = string.Compare(jobs[rCand[i]].Id, jobs[rTarg[i]].Id, StringComparison.Ordinal);
                if (cmp != 0) return cmp < 0;
            }
            if (rCand.Count != rTarg.Count)
                return rCand.Count < rTarg.Count;
        }

        return false;
    }
}

internal sealed record TechData(
    int Index,
    string Id,
    int ShiftStart,
    int ShiftEnd,
    HashSet<string> Skills);

internal sealed record JobData(
    int Index,
    string Id,
    int LocIndex,
    string Location,
    int Duration,
    int WindowStart,
    int WindowEnd,
    int Value,
    HashSet<string> RequiredSkills);

internal sealed class PlanState
{
    public List<int>[] Routes { get; set; }
    public int ServedValue { get; set; }
    public int TotalTravel { get; set; }

    public PlanState(int numTechs)
    {
        Routes = new List<int>[numTechs];
        for (int k = 0; k < numTechs; k++)
        {
            Routes[k] = new List<int>();
        }
        ServedValue = 0;
        TotalTravel = 0;
    }

    public PlanState Clone()
    {
        var copy = new PlanState(Routes.Length)
        {
            ServedValue = ServedValue,
            TotalTravel = TotalTravel
        };
        for (int k = 0; k < Routes.Length; k++)
        {
            copy.Routes[k] = new List<int>(Routes[k]);
        }
        return copy;
    }
}

internal readonly struct MemoKey : IEquatable<MemoKey>
{
    public readonly int TechIdx;
    public readonly int CurrentLoc;
    public readonly long AssignedMask;

    public MemoKey(int techIdx, int currentLoc, long assignedMask)
    {
        TechIdx = techIdx;
        CurrentLoc = currentLoc;
        AssignedMask = assignedMask;
    }

    public bool Equals(MemoKey other) =>
        TechIdx == other.TechIdx && CurrentLoc == other.CurrentLoc && AssignedMask == other.AssignedMask;

    public override bool Equals(object? obj) => obj is MemoKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(TechIdx, CurrentLoc, AssignedMask);
}

