namespace FieldServiceRoutePlanner;

/// <summary>
/// Normalized, index based view of a planning problem. Only technicians and
/// jobs that the validator accepts are kept, technicians are ordered by ID and
/// jobs are ordered by ID so planning is fully deterministic.
/// </summary>
internal sealed class PlanningModel
{
    public const int Inf = int.MaxValue / 4;

    public string[] TechnicianIds = [];
    public int[] ShiftStart = [];
    public int[] ShiftEnd = [];

    public string[] JobIds = [];
    public int[] JobLocation = [];
    public int[] Duration = [];
    public int[] WindowStart = [];
    public int[] WindowEnd = [];
    public int[] Value = [];

    /// <summary>Directed travel minutes; index 0 is the depot.</summary>
    public int[][] Travel = [];

    /// <summary>Shortest directed travel minutes, used for valid pruning bounds.</summary>
    public int[][] Shortest = [];

    public bool[][] Eligible = [];
    public int[][] EligibleJobs = [];

    public int TechnicianCount => TechnicianIds.Length;

    public int JobCount => JobIds.Length;

    public int DepotLoop => Travel.Length == 0 ? 0 : Travel[0][0];

    public static PlanningModel Build(RoutePlanningProblem problem)
    {
        var technicians = CollectTechnicians(problem);
        var jobs = CollectJobs(problem);
        var locations = BuildLocationIndex(problem.Depot ?? string.Empty, jobs);
        var travel = BuildTravelMatrix(problem, locations);
        var shortest = BuildShortestPaths(travel);

        var jobLocation = new int[jobs.Count];
        for (var i = 0; i < jobs.Count; i++)
        {
            jobLocation[i] = locations[jobs[i].Location];
        }

        var keep = new List<int>(jobs.Count);
        var eligibility = new bool[technicians.Count][];
        for (var tech = 0; tech < technicians.Count; tech++)
        {
            eligibility[tech] = new bool[jobs.Count];
        }

        for (var job = 0; job < jobs.Count; job++)
        {
            var reachable = false;
            for (var tech = 0; tech < technicians.Count; tech++)
            {
                var ok = IsEligible(
                    technicians[tech],
                    jobs[job],
                    shortest,
                    jobLocation[job]);
                eligibility[tech][job] = ok;
                reachable |= ok;
            }

            if (reachable)
            {
                keep.Add(job);
            }
        }

        var model = new PlanningModel
        {
            TechnicianIds = technicians.Select(technician => technician.Id).ToArray(),
            ShiftStart = technicians.Select(technician => technician.ShiftStart).ToArray(),
            ShiftEnd = technicians.Select(technician => technician.ShiftEnd).ToArray(),
            JobIds = keep.Select(index => jobs[index].Id).ToArray(),
            JobLocation = keep.Select(index => jobLocation[index]).ToArray(),
            Duration = keep.Select(index => jobs[index].Duration).ToArray(),
            WindowStart = keep.Select(index => jobs[index].WindowStart).ToArray(),
            WindowEnd = keep.Select(index => jobs[index].WindowEnd).ToArray(),
            Value = keep.Select(index => jobs[index].Value).ToArray(),
            Travel = travel,
            Shortest = shortest,
        };

        model.Eligible = new bool[technicians.Count][];
        model.EligibleJobs = new int[technicians.Count][];
        for (var tech = 0; tech < technicians.Count; tech++)
        {
            var flags = new bool[keep.Count];
            var eligibleJobs = new List<int>(keep.Count);
            for (var index = 0; index < keep.Count; index++)
            {
                flags[index] = eligibility[tech][keep[index]];
                if (flags[index])
                {
                    eligibleJobs.Add(index);
                }
            }

            model.Eligible[tech] = flags;
            model.EligibleJobs[tech] = eligibleJobs.ToArray();
        }

        return model;
    }

    private static bool IsEligible(
        Technician technician,
        ServiceJob job,
        int[][] shortest,
        int location)
    {
        var skills = new HashSet<string>(
            technician.Skills ?? [],
            StringComparer.Ordinal);
        foreach (var skill in job.RequiredSkills ?? [])
        {
            if (!skills.Contains(skill))
            {
                return false;
            }
        }

        // Shortest path bounds stay valid even when travel breaks the
        // triangle inequality, so a rejected job truly cannot be served.
        var outbound = shortest[0][location];
        var inbound = shortest[location][0];
        if (outbound >= Inf || inbound >= Inf)
        {
            return false;
        }

        var serviceStart = Math.Max(technician.ShiftStart + outbound, job.WindowStart);
        var serviceEnd = serviceStart + job.Duration;
        return serviceEnd <= job.WindowEnd
            && serviceEnd + inbound <= technician.ShiftEnd;
    }

    private static int[][] BuildShortestPaths(int[][] travel)
    {
        var count = travel.Length;
        var shortest = new int[count][];
        for (var from = 0; from < count; from++)
        {
            shortest[from] = (int[])travel[from].Clone();
            shortest[from][from] = Math.Min(shortest[from][from], 0);
        }

        for (var via = 0; via < count; via++)
        {
            for (var from = 0; from < count; from++)
            {
                var first = shortest[from][via];
                if (first >= Inf)
                {
                    continue;
                }

                for (var to = 0; to < count; to++)
                {
                    var second = shortest[via][to];
                    if (second < Inf && first + second < shortest[from][to])
                    {
                        shortest[from][to] = first + second;
                    }
                }
            }
        }

        return shortest;
    }

    private static List<Technician> CollectTechnicians(RoutePlanningProblem problem)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var technicians = new List<Technician>();
        foreach (var technician in problem.Technicians ?? [])
        {
            if (technician is null)
            {
                continue;
            }

            var skills = technician.Skills ?? [];
            if (string.IsNullOrWhiteSpace(technician.Id)
                || technician.ShiftStart < 0
                || technician.ShiftEnd < technician.ShiftStart
                || skills.Any(string.IsNullOrWhiteSpace)
                || skills.Distinct(StringComparer.Ordinal).Count() != skills.Count
                || !seen.Add(technician.Id))
            {
                continue;
            }

            technicians.Add(technician);
        }

        technicians.Sort(
            (left, right) => string.CompareOrdinal(left.Id, right.Id));
        return technicians;
    }

    private static List<ServiceJob> CollectJobs(RoutePlanningProblem problem)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var jobs = new List<ServiceJob>();
        foreach (var job in problem.Jobs ?? [])
        {
            if (job is null)
            {
                continue;
            }

            var required = job.RequiredSkills ?? [];
            if (string.IsNullOrWhiteSpace(job.Id)
                || string.IsNullOrWhiteSpace(job.Location)
                || job.Duration < 0
                || job.WindowStart < 0
                || job.WindowEnd < job.WindowStart
                || job.Value < 0
                || required.Any(string.IsNullOrWhiteSpace)
                || required.Distinct(StringComparer.Ordinal).Count() != required.Count
                || !seen.Add(job.Id))
            {
                continue;
            }

            jobs.Add(job);
        }

        jobs.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return jobs;
    }

    private static Dictionary<string, int> BuildLocationIndex(
        string depot,
        List<ServiceJob> jobs)
    {
        var locations = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [depot] = 0,
        };
        foreach (var job in jobs)
        {
            if (!locations.ContainsKey(job.Location))
            {
                locations[job.Location] = locations.Count;
            }
        }

        return locations;
    }

    private static int[][] BuildTravelMatrix(
        RoutePlanningProblem problem,
        Dictionary<string, int> locations)
    {
        var count = locations.Count;
        var travel = new int[count][];
        for (var from = 0; from < count; from++)
        {
            travel[from] = new int[count];
            Array.Fill(travel[from], Inf);
        }

        foreach (var row in problem.TravelTimes ?? [])
        {
            if (row.Key is null
                || !locations.TryGetValue(row.Key, out var from)
                || row.Value is null)
            {
                continue;
            }

            foreach (var cell in row.Value)
            {
                if (cell.Key is not null
                    && locations.TryGetValue(cell.Key, out var to)
                    && cell.Value >= 0
                    && cell.Value < Inf)
                {
                    travel[from][to] = cell.Value;
                }
            }
        }

        for (var index = 0; index < count; index++)
        {
            if (travel[index][index] >= Inf)
            {
                travel[index][index] = 0;
            }
        }

        return travel;
    }
}
