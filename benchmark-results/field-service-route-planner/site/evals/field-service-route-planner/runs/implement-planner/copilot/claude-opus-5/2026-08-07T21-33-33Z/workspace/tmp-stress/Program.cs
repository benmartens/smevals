using System.Diagnostics;
using FieldServiceRoutePlanner;

// Temporary stress harness: compares RoutePlanner against brute force.
var failures = 0;
var checkedCases = 0;
var planner = new RoutePlanner();

for (var seed = 1; seed <= 400; seed++)
{
    var random = new Random(seed);
    var techCount = random.Next(1, 4);
    var jobCount = random.Next(1, 9);
    var problem = Generate(random, techCount, jobCount);

    var plan = planner.Plan(problem);
    var report = RouteValidator.Validate(problem, plan);
    if (!report.IsValid)
    {
        Console.WriteLine($"seed {seed}: INVALID {string.Join("; ", report.Issues.Select(i => i.Code))}");
        failures++;
        continue;
    }

    var second = planner.Plan(problem);
    if (!plan.Routes.Zip(second.Routes).All(pair =>
        pair.First.TechnicianId == pair.Second.TechnicianId
        && pair.First.JobIds.SequenceEqual(pair.Second.JobIds, StringComparer.Ordinal)))
    {
        Console.WriteLine($"seed {seed}: NONDETERMINISTIC");
        failures++;
        continue;
    }

    var (bestValue, bestTravel) = BruteForce(problem);
    checkedCases++;
    if (report.ServedValue != bestValue || report.TotalTravel != bestTravel)
    {
        Console.WriteLine(
            $"seed {seed}: got ({report.ServedValue},{report.TotalTravel}) "
            + $"want ({bestValue},{bestTravel}) techs={techCount} jobs={jobCount}");
        failures++;
    }
}

Console.WriteLine($"small cases compared: {checkedCases}, failures: {failures}");

var worst = 0L;
for (var seed = 1000; seed <= 1030; seed++)
{
    var random = new Random(seed);
    var techCount = random.Next(2, 6);
    var jobCount = random.Next(12, 30);
    var problem = Generate(random, techCount, jobCount);
    var watch = Stopwatch.StartNew();
    var plan = planner.Plan(problem);
    watch.Stop();
    worst = Math.Max(worst, watch.ElapsedMilliseconds);
    var report = RouteValidator.Validate(problem, plan);
    if (!report.IsValid)
    {
        Console.WriteLine($"large seed {seed}: INVALID {string.Join("; ", report.Issues.Select(i => i.Code))}");
        failures++;
    }
}

Console.WriteLine($"large worst elapsed: {worst} ms, total failures: {failures}");

// Edge cases.
var empty = new RoutePlanningProblem(
    "depot",
    new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal)
    {
        ["depot"] = new(StringComparer.Ordinal) { ["depot"] = 0 },
    },
    [new Technician("b", [], 0, 10), new Technician("a", ["x"], 0, 10)],
    []);
var emptyPlan = planner.Plan(empty);
var emptyReport = RouteValidator.Validate(empty, emptyPlan);
if (!emptyReport.IsValid || emptyPlan.Routes.Count != 2)
{
    Console.WriteLine("edge: empty jobs failed");
    failures++;
}

var noTechs = new RoutePlanningProblem(
    "depot",
    new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal)
    {
        ["depot"] = new(StringComparer.Ordinal) { ["depot"] = 0, ["a"] = 3 },
        ["a"] = new(StringComparer.Ordinal) { ["depot"] = 3, ["a"] = 0 },
    },
    [],
    [new ServiceJob("j", "a", ["x"], 5, 0, 50, 9)]);
if (!RouteValidator.Validate(noTechs, planner.Plan(noTechs)).IsValid)
{
    Console.WriteLine("edge: no technicians failed");
    failures++;
}

// Non metric shortcut: depot -> far is slow, but depot -> near -> far is fast.
var shortcut = new RoutePlanningProblem(
    "depot",
    new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal)
    {
        ["depot"] = new(StringComparer.Ordinal) { ["depot"] = 0, ["near"] = 5, ["far"] = 90 },
        ["near"] = new(StringComparer.Ordinal) { ["depot"] = 5, ["near"] = 0, ["far"] = 5 },
        ["far"] = new(StringComparer.Ordinal) { ["depot"] = 90, ["near"] = 5, ["far"] = 0 },
    },
    [new Technician("t", ["x"], 0, 60)],
    [
        new ServiceJob("near", "near", ["x"], 5, 0, 60, 1),
        new ServiceJob("far", "far", ["x"], 5, 0, 60, 50),
    ]);
var shortcutReport = RouteValidator.Validate(shortcut, planner.Plan(shortcut));
if (!shortcutReport.IsValid || shortcutReport.ServedValue != 1)
{
    Console.WriteLine($"edge: shortcut served {shortcutReport.ServedValue}, want 1");
    failures++;
}

Console.WriteLine($"total failures: {failures}");
return failures == 0 ? 0 : 1;

static RoutePlanningProblem Generate(Random random, int techCount, int jobCount)
{
    var skillPool = new[] { "repair", "install", "inspect" };
    var locations = new List<string> { "depot" };
    var locationCount = random.Next(1, jobCount + 1);
    for (var i = 0; i < locationCount; i++)
    {
        locations.Add($"loc-{i}");
    }

    var travel = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
    foreach (var from in locations)
    {
        var row = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var to in locations)
        {
            row[to] = from == to ? 0 : random.Next(1, 30);
        }

        travel[from] = row;
    }

    var technicians = new List<Technician>();
    for (var i = 0; i < techCount; i++)
    {
        var skills = skillPool.Where(_ => random.Next(2) == 0).ToList();
        if (skills.Count == 0)
        {
            skills.Add(skillPool[random.Next(skillPool.Length)]);
        }

        var start = random.Next(0, 20);
        technicians.Add(new Technician($"tech-{i}", skills, start, start + random.Next(60, 200)));
    }

    var jobs = new List<ServiceJob>();
    for (var i = 0; i < jobCount; i++)
    {
        var required = skillPool.Where(_ => random.Next(3) == 0).ToList();
        var windowStart = random.Next(0, 120);
        var duration = random.Next(0, 30);
        var windowEnd = windowStart + duration + random.Next(0, 80);
        jobs.Add(new ServiceJob(
            $"job-{i:00}",
            locations[random.Next(1, locations.Count)],
            required,
            duration,
            windowStart,
            windowEnd,
            random.Next(0, 30)));
    }

    return new RoutePlanningProblem("depot", travel, technicians, jobs);
}

static (int Value, int Travel) BruteForce(RoutePlanningProblem problem)
{
    var techs = problem.Technicians.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
    var jobs = problem.Jobs.ToArray();
    var cache = new Dictionary<(int Tech, int Mask), int>();

    int RouteCost(int tech, int mask)
    {
        if (cache.TryGetValue((tech, mask), out var cached))
        {
            return cached;
        }

        var members = new List<int>();
        for (var i = 0; i < jobs.Length; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                members.Add(i);
            }
        }

        var best = int.MaxValue;
        if (members.Count == 0)
        {
            best = problem.TravelTimes["depot"]["depot"];
        }
        else
        {
            foreach (var order in Permutations(members))
            {
                var location = "depot";
                var time = techs[tech].ShiftStart;
                var travel = 0;
                var ok = true;
                foreach (var index in order)
                {
                    var job = jobs[index];
                    if (!RouteValidator.HasSkills(techs[tech], job))
                    {
                        ok = false;
                        break;
                    }

                    var minutes = problem.TravelTimes[location][job.Location];
                    travel += minutes;
                    var start = Math.Max(time + minutes, job.WindowStart);
                    var end = start + job.Duration;
                    if (end > job.WindowEnd)
                    {
                        ok = false;
                        break;
                    }

                    location = job.Location;
                    time = end;
                }

                if (!ok)
                {
                    continue;
                }

                travel += problem.TravelTimes[location]["depot"];
                time += problem.TravelTimes[location]["depot"];
                if (time <= techs[tech].ShiftEnd && travel < best)
                {
                    best = travel;
                }
            }
        }

        cache[(tech, mask)] = best;
        return best;
    }

    var bestValue = -1;
    var bestTravel = int.MaxValue;
    var masks = new int[techs.Length];

    void Recurse(int job)
    {
        if (job == jobs.Length)
        {
            var travel = 0;
            for (var tech = 0; tech < techs.Length; tech++)
            {
                var cost = RouteCost(tech, masks[tech]);
                if (cost == int.MaxValue)
                {
                    return;
                }

                travel += cost;
            }

            var value = 0;
            for (var tech = 0; tech < techs.Length; tech++)
            {
                for (var i = 0; i < jobs.Length; i++)
                {
                    if ((masks[tech] & (1 << i)) != 0)
                    {
                        value += jobs[i].Value;
                    }
                }
            }

            if (value > bestValue || (value == bestValue && travel < bestTravel))
            {
                bestValue = value;
                bestTravel = travel;
            }

            return;
        }

        Recurse(job + 1);
        for (var tech = 0; tech < techs.Length; tech++)
        {
            masks[tech] |= 1 << job;
            Recurse(job + 1);
            masks[tech] &= ~(1 << job);
        }
    }

    Recurse(0);
    return (bestValue, bestTravel);
}

static IEnumerable<List<int>> Permutations(List<int> items)
{
    if (items.Count <= 1)
    {
        yield return items;
        yield break;
    }

    for (var i = 0; i < items.Count; i++)
    {
        var rest = new List<int>(items);
        rest.RemoveAt(i);
        foreach (var tail in Permutations(rest))
        {
            var result = new List<int> { items[i] };
            result.AddRange(tail);
            yield return result;
        }
    }
}
