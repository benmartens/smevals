namespace FieldServiceRoutePlanner;

public static class RouteValidator
{
    public static RouteValidationReport Validate(
        RoutePlanningProblem problem,
        RoutePlan result)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(result);

        var issues = new List<ValidationIssue>();
        var technicians = new Dictionary<string, Technician>(
            StringComparer.Ordinal);
        var jobs = new Dictionary<string, ServiceJob>(StringComparer.Ordinal);
        ValidateProblem(problem, technicians, jobs, issues);

        var expectedTechnicianIds = technicians.Keys
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualTechnicianIds = result.Routes
            .Select(route => route.TechnicianId)
            .ToArray();
        if (!actualTechnicianIds.SequenceEqual(
                actualTechnicianIds.Order(StringComparer.Ordinal)))
        {
            issues.Add(new(
                "noncanonical_routes",
                "Routes must be ordered by technician ID."));
        }
        if (!actualTechnicianIds.SequenceEqual(expectedTechnicianIds))
        {
            issues.Add(new(
                "technician_routes",
                "Return exactly one route for every technician."));
        }

        var assignedJobs = new HashSet<string>(StringComparer.Ordinal);
        var routeTimings = new List<RouteTiming>();
        var servedValue = 0;
        var totalTravel = 0;

        foreach (var route in result.Routes)
        {
            if (!technicians.TryGetValue(route.TechnicianId, out var technician))
            {
                issues.Add(new(
                    "unknown_technician",
                    $"Unknown technician ID '{route.TechnicianId}'."));
                continue;
            }

            var currentLocation = problem.Depot;
            var currentTime = technician.ShiftStart;
            var routeTravel = 0;
            var stops = new List<RouteStopTiming>();

            foreach (var jobId in route.JobIds)
            {
                if (!jobs.TryGetValue(jobId, out var job))
                {
                    issues.Add(new(
                        "unknown_job",
                        $"Unknown job ID '{jobId}'."));
                    continue;
                }
                if (!assignedJobs.Add(jobId))
                {
                    issues.Add(new(
                        "duplicate_job",
                        $"Job '{jobId}' is assigned more than once."));
                }
                if (!HasSkills(technician, job))
                {
                    issues.Add(new(
                        "missing_skills",
                        $"Technician '{technician.Id}' lacks skills for '{job.Id}'."));
                }
                if (!TryGetTravel(
                        problem,
                        currentLocation,
                        job.Location,
                        issues,
                        out var travel))
                {
                    continue;
                }

                routeTravel += travel;
                var arrival = currentTime + travel;
                var serviceStart = Math.Max(arrival, job.WindowStart);
                var serviceEnd = serviceStart + job.Duration;
                if (serviceEnd > job.WindowEnd)
                {
                    issues.Add(new(
                        "time_window",
                        $"Job '{job.Id}' finishes at {serviceEnd}, after "
                        + $"its window ends at {job.WindowEnd}."));
                }

                stops.Add(new(job.Id, arrival, serviceStart, serviceEnd));
                currentLocation = job.Location;
                currentTime = serviceEnd;
                servedValue += job.Value;
            }

            if (TryGetTravel(
                    problem,
                    currentLocation,
                    problem.Depot,
                    issues,
                    out var returnTravel))
            {
                routeTravel += returnTravel;
                currentTime += returnTravel;
            }
            if (currentTime > technician.ShiftEnd)
            {
                issues.Add(new(
                    "shift_return",
                    $"Technician '{technician.Id}' returns at {currentTime}, "
                    + $"after shift end {technician.ShiftEnd}."));
            }

            totalTravel += routeTravel;
            routeTimings.Add(new(
                technician.Id,
                stops,
                currentTime,
                routeTravel));
        }

        return new(issues, servedValue, totalTravel, routeTimings);
    }

    public static bool HasSkills(Technician technician, ServiceJob job)
    {
        var skills = new HashSet<string>(
            technician.Skills,
            StringComparer.Ordinal);
        return job.RequiredSkills.All(skills.Contains);
    }

    private static void ValidateProblem(
        RoutePlanningProblem problem,
        Dictionary<string, Technician> technicians,
        Dictionary<string, ServiceJob> jobs,
        List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(problem.Depot))
        {
            issues.Add(new("invalid_depot", "Depot ID must be non-empty."));
        }

        foreach (var technician in problem.Technicians)
        {
            if (string.IsNullOrWhiteSpace(technician.Id)
                || technician.ShiftStart < 0
                || technician.ShiftEnd < technician.ShiftStart
                || technician.Skills.Any(string.IsNullOrWhiteSpace)
                || technician.Skills.Distinct(StringComparer.Ordinal).Count()
                    != technician.Skills.Count)
            {
                issues.Add(new(
                    "invalid_technician",
                    $"Technician '{technician.Id}' has invalid fields."));
                continue;
            }
            if (!technicians.TryAdd(technician.Id, technician))
            {
                issues.Add(new(
                    "duplicate_technician",
                    $"Technician ID '{technician.Id}' is duplicated."));
            }
        }

        foreach (var job in problem.Jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Id)
                || string.IsNullOrWhiteSpace(job.Location)
                || job.Duration < 0
                || job.WindowStart < 0
                || job.WindowEnd < job.WindowStart
                || job.Value < 0
                || job.RequiredSkills.Any(string.IsNullOrWhiteSpace)
                || job.RequiredSkills.Distinct(StringComparer.Ordinal).Count()
                    != job.RequiredSkills.Count)
            {
                issues.Add(new(
                    "invalid_job",
                    $"Job '{job.Id}' has invalid fields."));
                continue;
            }
            if (!jobs.TryAdd(job.Id, job))
            {
                issues.Add(new(
                    "duplicate_job_id",
                    $"Job ID '{job.Id}' is duplicated."));
            }
        }

        var locations = jobs.Values.Select(job => job.Location)
            .Append(problem.Depot)
            .Distinct(StringComparer.Ordinal);
        foreach (var from in locations)
        {
            foreach (var to in locations)
            {
                if (!problem.TravelTimes.TryGetValue(from, out var row)
                    || !row.TryGetValue(to, out var minutes)
                    || minutes < 0)
                {
                    issues.Add(new(
                        "travel_matrix",
                        $"Missing or invalid travel time '{from}' -> '{to}'."));
                }
            }
        }
    }

    private static bool TryGetTravel(
        RoutePlanningProblem problem,
        string from,
        string to,
        List<ValidationIssue> issues,
        out int minutes)
    {
        if (problem.TravelTimes.TryGetValue(from, out var row)
            && row.TryGetValue(to, out minutes)
            && minutes >= 0)
        {
            return true;
        }
        minutes = 0;
        issues.Add(new(
            "travel_matrix",
            $"Missing or invalid travel time '{from}' -> '{to}'."));
        return false;
    }
}
