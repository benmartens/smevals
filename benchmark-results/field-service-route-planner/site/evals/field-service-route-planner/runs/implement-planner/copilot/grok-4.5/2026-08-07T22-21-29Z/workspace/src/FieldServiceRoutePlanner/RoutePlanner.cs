namespace FieldServiceRoutePlanner;

public sealed class RoutePlanner
{
    public RoutePlan Plan(RoutePlanningProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new Solver(problem).Solve();
    }

    private sealed class Solver
    {
        private readonly string _depot;
        private readonly Dictionary<string, Dictionary<string, int>> _travel;
        private readonly Technician[] _techs;
        private readonly ServiceJob[] _jobs;
        private readonly int _n;
        private readonly int _m;
        private readonly bool[][] _canServe;

        private int _bestValue = -1;
        private int _bestTravel = int.MaxValue;
        private List<string>[]? _bestRoutes;

        public Solver(RoutePlanningProblem problem)
        {
            _depot = problem.Depot;
            _travel = problem.TravelTimes;
            _techs = problem.Technicians
                .OrderBy(t => t.Id, StringComparer.Ordinal)
                .ToArray();
            _jobs = problem.Jobs
                .OrderByDescending(j => j.Value)
                .ThenBy(j => j.Id, StringComparer.Ordinal)
                .ToArray();
            _n = _jobs.Length;
            _m = _techs.Length;
            _canServe = new bool[_m][];
            for (var t = 0; t < _m; t++)
            {
                _canServe[t] = new bool[_n];
                var skills = new HashSet<string>(_techs[t].Skills, StringComparer.Ordinal);
                for (var j = 0; j < _n; j++)
                {
                    _canServe[t][j] = _jobs[j].RequiredSkills.All(skills.Contains);
                }
            }
        }

        public RoutePlan Solve()
        {
            var routes = new List<int>[_m];
            for (var t = 0; t < _m; t++)
            {
                routes[t] = [];
            }

            if (_m == 0)
            {
                return new RoutePlan([]);
            }

            ConstructiveSearch(routes);

            if (_n <= 12)
            {
                for (var t = 0; t < _m; t++)
                {
                    routes[t].Clear();
                }

                ExactDfs(0, routes, 0);
            }
            else
            {
                ImproveFromBest();
            }

            return ToPlan();
        }

        private void ConstructiveSearch(List<int>[] scratch)
        {
            foreach (var order in BuildJobOrders())
            {
                for (var t = 0; t < _m; t++)
                {
                    scratch[t].Clear();
                }

                GreedyInsert(order, scratch);
                LocalSearch(scratch);
                Consider(scratch);
            }

            for (var t = 0; t < _m; t++)
            {
                scratch[t].Clear();
            }

            SequentialNnBuild(scratch);
            LocalSearch(scratch);
            Consider(scratch);
        }

        private List<int[]> BuildJobOrders()
        {
            var result = new List<int[]>
            {
                Enumerable.Range(0, _n).ToArray(),
                Enumerable.Range(0, _n)
                    .OrderBy(j => _jobs[j].WindowStart)
                    .ThenByDescending(j => _jobs[j].Value)
                    .ThenBy(j => _jobs[j].Id, StringComparer.Ordinal)
                    .ToArray(),
                Enumerable.Range(0, _n)
                    .OrderBy(j => _jobs[j].WindowEnd - _jobs[j].Duration - _jobs[j].WindowStart)
                    .ThenByDescending(j => _jobs[j].Value)
                    .ThenBy(j => _jobs[j].Id, StringComparer.Ordinal)
                    .ToArray(),
                Enumerable.Range(0, _n)
                    .OrderBy(j => _jobs[j].Duration)
                    .ThenByDescending(j => _jobs[j].Value)
                    .ThenBy(j => _jobs[j].Id, StringComparer.Ordinal)
                    .ToArray(),
                Enumerable.Range(0, _n)
                    .OrderBy(j => _jobs[j].Location, StringComparer.Ordinal)
                    .ThenByDescending(j => _jobs[j].Value)
                    .ThenBy(j => _jobs[j].Id, StringComparer.Ordinal)
                    .ToArray(),
                Enumerable.Range(0, _n)
                    .OrderByDescending(j =>
                    {
                        var dep = Travel(_depot, _jobs[j].Location)
                            + Travel(_jobs[j].Location, _depot);
                        return _jobs[j].Value * 1000.0
                            / Math.Max(1, _jobs[j].Duration + dep);
                    })
                    .ThenBy(j => _jobs[j].Id, StringComparer.Ordinal)
                    .ToArray(),
            };
            return result;
        }

        private void GreedyInsert(int[] order, List<int>[] routes)
        {
            foreach (var j in order)
            {
                TryBestInsertion(j, routes);
            }
        }

        private void SequentialNnBuild(List<int>[] routes)
        {
            var remaining = new bool[_n];
            Array.Fill(remaining, true);

            for (var t = 0; t < _m; t++)
            {
                while (true)
                {
                    var bestJ = -1;
                    var bestTravel = int.MaxValue;
                    var bestPos = -1;
                    var bestValue = -1;

                    for (var j = 0; j < _n; j++)
                    {
                        if (!remaining[j] || !_canServe[t][j])
                        {
                            continue;
                        }

                        if (!FindBestPos(t, j, routes, out var pos, out var delta))
                        {
                            continue;
                        }

                        var val = _jobs[j].Value;
                        if (val > bestValue
                            || (val == bestValue && delta < bestTravel)
                            || (val == bestValue && delta == bestTravel && (bestJ < 0
                                || string.CompareOrdinal(_jobs[j].Id, _jobs[bestJ].Id) < 0)))
                        {
                            bestValue = val;
                            bestTravel = delta;
                            bestJ = j;
                            bestPos = pos;
                        }
                    }

                    if (bestJ < 0)
                    {
                        break;
                    }

                    routes[t].Insert(bestPos, bestJ);
                    remaining[bestJ] = false;
                }
            }
        }

        private bool TryBestInsertion(int j, List<int>[] routes)
        {
            var bestT = -1;
            var bestPos = -1;
            var bestDelta = int.MaxValue;

            for (var t = 0; t < _m; t++)
            {
                if (!_canServe[t][j])
                {
                    continue;
                }

                if (!FindBestPos(t, j, routes, out var pos, out var delta))
                {
                    continue;
                }

                if (delta < bestDelta
                    || (delta == bestDelta && (bestT < 0
                        || string.CompareOrdinal(_techs[t].Id, _techs[bestT].Id) < 0)))
                {
                    bestDelta = delta;
                    bestT = t;
                    bestPos = pos;
                }
            }

            if (bestT < 0)
            {
                return false;
            }

            routes[bestT].Insert(bestPos, j);
            return true;
        }

        private bool FindBestPos(
            int t,
            int j,
            List<int>[] routes,
            out int bestPos,
            out int bestDelta)
        {
            bestPos = -1;
            bestDelta = int.MaxValue;
            var route = routes[t];
            var before = RouteTravel(t, route);

            for (var pos = 0; pos <= route.Count; pos++)
            {
                route.Insert(pos, j);
                if (IsFeasible(t, route, out var after))
                {
                    var delta = after - before;
                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        bestPos = pos;
                    }
                }

                route.RemoveAt(pos);
            }

            return bestPos >= 0;
        }

        private void ExactDfs(int index, List<int>[] routes, int curValue)
        {
            var remainingValue = 0;
            for (var j = index; j < _n; j++)
            {
                for (var t = 0; t < _m; t++)
                {
                    if (_canServe[t][j])
                    {
                        remainingValue += _jobs[j].Value;
                        break;
                    }
                }
            }

            if (curValue + remainingValue < _bestValue)
            {
                return;
            }

            if (index == _n)
            {
                Consider(routes);
                return;
            }

            ExactDfs(index + 1, routes, curValue);

            if (curValue + remainingValue < _bestValue)
            {
                return;
            }

            var job = index;
            for (var t = 0; t < _m; t++)
            {
                if (!_canServe[t][job])
                {
                    continue;
                }

                var route = routes[t];
                for (var pos = 0; pos <= route.Count; pos++)
                {
                    route.Insert(pos, job);
                    if (IsFeasible(t, route, out _))
                    {
                        ExactDfs(index + 1, routes, curValue + _jobs[job].Value);
                    }

                    route.RemoveAt(pos);
                }
            }
        }

        private void ImproveFromBest()
        {
            if (_bestRoutes is null)
            {
                return;
            }

            var routes = FromBest();
            LocalSearch(routes);
            Consider(routes);

            var served = new bool[_n];
            for (var t = 0; t < _m; t++)
            {
                foreach (var j in routes[t])
                {
                    served[j] = true;
                }
            }

            var improved = true;
            while (improved)
            {
                improved = false;
                for (var j = 0; j < _n; j++)
                {
                    if (served[j])
                    {
                        continue;
                    }

                    if (TryBestInsertion(j, routes))
                    {
                        served[j] = true;
                        improved = true;
                        LocalSearch(routes);
                        Consider(routes);
                    }
                }
            }
        }

        private List<int>[] FromBest()
        {
            var idToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var j = 0; j < _n; j++)
            {
                idToIndex[_jobs[j].Id] = j;
            }

            var routes = new List<int>[_m];
            for (var t = 0; t < _m; t++)
            {
                routes[t] = _bestRoutes![t].Select(id => idToIndex[id]).ToList();
            }

            return routes;
        }

        private void LocalSearch(List<int>[] routes)
        {
            var improved = true;
            var guard = 0;
            while (improved && guard++ < 200)
            {
                improved = false;
                if (Relocate(routes) || SwapJobs(routes) || TwoOpt(routes) || CrossExchange(routes))
                {
                    improved = true;
                }
            }
        }

        private bool Relocate(List<int>[] routes)
        {
            var curValue = TotalValue(routes);
            var curTravel = TotalTravel(routes);

            for (var tFrom = 0; tFrom < _m; tFrom++)
            {
                for (var i = 0; i < routes[tFrom].Count; i++)
                {
                    var job = routes[tFrom][i];
                    routes[tFrom].RemoveAt(i);

                    for (var tTo = 0; tTo < _m; tTo++)
                    {
                        if (!_canServe[tTo][job])
                        {
                            continue;
                        }

                        for (var pos = 0; pos <= routes[tTo].Count; pos++)
                        {
                            if (tFrom == tTo && pos == i)
                            {
                                continue;
                            }

                            routes[tTo].Insert(pos, job);
                            if (AllFeasible(routes)
                                && IsBetter(TotalValue(routes), TotalTravel(routes), curValue, curTravel))
                            {
                                return true;
                            }

                            routes[tTo].RemoveAt(pos);
                        }
                    }

                    routes[tFrom].Insert(i, job);
                }
            }

            return false;
        }

        private bool SwapJobs(List<int>[] routes)
        {
            var curValue = TotalValue(routes);
            var curTravel = TotalTravel(routes);
            var positions = new List<(int T, int I, int J)>();
            for (var t = 0; t < _m; t++)
            {
                for (var i = 0; i < routes[t].Count; i++)
                {
                    positions.Add((t, i, routes[t][i]));
                }
            }

            for (var a = 0; a < positions.Count; a++)
            {
                for (var b = a + 1; b < positions.Count; b++)
                {
                    var (t1, i1, j1) = positions[a];
                    var (t2, i2, j2) = positions[b];
                    if (!_canServe[t1][j2] || !_canServe[t2][j1])
                    {
                        continue;
                    }

                    routes[t1][i1] = j2;
                    routes[t2][i2] = j1;
                    if (AllFeasible(routes)
                        && IsBetter(TotalValue(routes), TotalTravel(routes), curValue, curTravel))
                    {
                        return true;
                    }

                    routes[t1][i1] = j1;
                    routes[t2][i2] = j2;
                }
            }

            return false;
        }

        private bool TwoOpt(List<int>[] routes)
        {
            var curTravel = TotalTravel(routes);
            var curValue = TotalValue(routes);

            for (var t = 0; t < _m; t++)
            {
                var route = routes[t];
                if (route.Count < 2)
                {
                    continue;
                }

                for (var i = 0; i < route.Count - 1; i++)
                {
                    for (var k = i + 1; k < route.Count; k++)
                    {
                        route.Reverse(i, k - i + 1);
                        if (IsFeasible(t, route, out _)
                            && IsBetter(TotalValue(routes), TotalTravel(routes), curValue, curTravel))
                        {
                            return true;
                        }

                        route.Reverse(i, k - i + 1);
                    }
                }
            }

            return false;
        }

        private bool CrossExchange(List<int>[] routes)
        {
            var curTravel = TotalTravel(routes);
            var curValue = TotalValue(routes);

            for (var t1 = 0; t1 < _m; t1++)
            {
                for (var t2 = t1 + 1; t2 < _m; t2++)
                {
                    var r1 = routes[t1];
                    var r2 = routes[t2];
                    for (var i = 0; i <= r1.Count; i++)
                    {
                        for (var j = 0; j <= r2.Count; j++)
                        {
                            if (i == r1.Count && j == r2.Count)
                            {
                                continue;
                            }

                            var tail1 = r1.Skip(i).ToList();
                            var tail2 = r2.Skip(j).ToList();
                            if (tail2.Any(job => !_canServe[t1][job])
                                || tail1.Any(job => !_canServe[t2][job]))
                            {
                                continue;
                            }

                            r1.RemoveRange(i, tail1.Count);
                            r2.RemoveRange(j, tail2.Count);
                            r1.AddRange(tail2);
                            r2.AddRange(tail1);

                            if (AllFeasible(routes)
                                && IsBetter(TotalValue(routes), TotalTravel(routes), curValue, curTravel))
                            {
                                return true;
                            }

                            r1.RemoveRange(i, tail2.Count);
                            r2.RemoveRange(j, tail1.Count);
                            r1.AddRange(tail1);
                            r2.AddRange(tail2);
                        }
                    }
                }
            }

            return false;
        }

        private static bool IsBetter(int value, int travel, int bestValue, int bestTravel) =>
            value > bestValue || (value == bestValue && travel < bestTravel);

        private void Consider(List<int>[] routes)
        {
            if (!AllFeasible(routes))
            {
                return;
            }

            var value = TotalValue(routes);
            var travel = TotalTravel(routes);
            if (value > _bestValue || (value == _bestValue && travel < _bestTravel))
            {
                _bestValue = value;
                _bestTravel = travel;
                _bestRoutes = new List<string>[_m];
                for (var t = 0; t < _m; t++)
                {
                    _bestRoutes[t] = routes[t].Select(j => _jobs[j].Id).ToList();
                }
            }
            else if (value == _bestValue && travel == _bestTravel && _bestRoutes is not null)
            {
                var candidate = new List<string>[_m];
                for (var t = 0; t < _m; t++)
                {
                    candidate[t] = routes[t].Select(j => _jobs[j].Id).ToList();
                }

                if (CompareRoutes(candidate, _bestRoutes) < 0)
                {
                    _bestRoutes = candidate;
                }
            }
        }

        private static int CompareRoutes(List<string>[] a, List<string>[] b)
        {
            for (var t = 0; t < a.Length; t++)
            {
                var n = Math.Min(a[t].Count, b[t].Count);
                for (var i = 0; i < n; i++)
                {
                    var c = string.CompareOrdinal(a[t][i], b[t][i]);
                    if (c != 0)
                    {
                        return c;
                    }
                }

                if (a[t].Count != b[t].Count)
                {
                    return a[t].Count.CompareTo(b[t].Count);
                }
            }

            return 0;
        }

        private bool AllFeasible(List<int>[] routes)
        {
            for (var t = 0; t < _m; t++)
            {
                if (!IsFeasible(t, routes[t], out _))
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsFeasible(int t, List<int> route, out int travelMinutes)
        {
            travelMinutes = 0;
            var tech = _techs[t];
            var loc = _depot;
            var time = tech.ShiftStart;

            foreach (var j in route)
            {
                if (!_canServe[t][j])
                {
                    return false;
                }

                var job = _jobs[j];
                var travel = Travel(loc, job.Location);
                travelMinutes += travel;
                var arrival = time + travel;
                var start = Math.Max(arrival, job.WindowStart);
                var end = start + job.Duration;
                if (end > job.WindowEnd)
                {
                    return false;
                }

                loc = job.Location;
                time = end;
            }

            var ret = Travel(loc, _depot);
            travelMinutes += ret;
            time += ret;
            return time <= tech.ShiftEnd;
        }

        private int TotalTravel(List<int>[] routes)
        {
            var sum = 0;
            for (var t = 0; t < _m; t++)
            {
                if (!IsFeasible(t, routes[t], out var tr))
                {
                    return int.MaxValue / 4;
                }

                sum += tr;
            }

            return sum;
        }

        private int TotalValue(List<int>[] routes)
        {
            var sum = 0;
            for (var t = 0; t < _m; t++)
            {
                foreach (var j in routes[t])
                {
                    sum += _jobs[j].Value;
                }
            }

            return sum;
        }

        private int RouteTravel(int t, List<int> route) =>
            IsFeasible(t, route, out var tr) ? tr : int.MaxValue / 4;

        private int Travel(string from, string to) => _travel[from][to];

        private RoutePlan ToPlan()
        {
            var list = new List<TechnicianRoute>(_m);
            for (var t = 0; t < _m; t++)
            {
                var jobs = _bestRoutes is null ? new List<string>() : [.. _bestRoutes[t]];
                list.Add(new TechnicianRoute(_techs[t].Id, jobs));
            }

            return new RoutePlan(list);
        }
    }
}
