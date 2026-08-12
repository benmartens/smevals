#:project src/ReplicatedShardRebalancer/ReplicatedShardRebalancer.csproj
using System.Diagnostics;
using ReplicatedShardRebalancer;

var iterations = args.Length > 0 ? int.Parse(args[0]) : 400;
var big = args.Length > 1 && args[1] == "big";
var random = new Random(20260807);
var checkedCount = 0;
var failures = 0;
var watch = new Stopwatch();
var worstMs = 0L;

for (var iteration = 0; iteration < iterations; iteration++)
{
    var problem = big ? MakeBig(random) : MakeSmall(random);
    watch.Restart();
    var result = new ReplicatedShardRebalancer.ReplicatedShardRebalancer().Rebalance(problem);
    watch.Stop();
    worstMs = Math.Max(worstMs, watch.ElapsedMilliseconds);
    var report = RebalanceValidator.Validate(problem, result);
    var problemIssues = RebalanceValidator.Validate(problem, new RebalanceResult([]))
        .Issues.Where(i => i.Code != "missing_shard").ToList();
    if (problemIssues.Count > 0)
    {
        continue; // generator produced a malformed problem; skip
    }

    if (!report.IsValid)
    {
        var brute = BruteForce(problem);
        if (brute is null)
        {
            continue; // genuinely infeasible instance
        }
        failures++;
        Console.WriteLine($"[{iteration}] INVALID: {string.Join("; ", report.Issues.Select(i => i.Code))}");
        Console.WriteLine(Describe(problem));
        if (failures > 3) { break; }
        continue;
    }

    if (big) { checkedCount++; continue; }

    var optimum = BruteForce(problem);
    if (optimum is null) { continue; }
    checkedCount++;
    var mine = (report.MaximumNodeUtilization, report.UtilizationSpread, report.MovedBytes, report.MovedReplicaCount);
    if (mine != optimum.Value.Objective)
    {
        failures++;
        Console.WriteLine($"[{iteration}] SUBOPTIMAL mine={mine} best={optimum.Value.Objective}");
        Console.WriteLine(Describe(problem));
        if (failures > 3) { break; }
        continue;
    }
    var mineText = Render(result);
    if (mineText != optimum.Value.Text)
    {
        failures++;
        Console.WriteLine($"[{iteration}] NON-CANONICAL mine={mineText} best={optimum.Value.Text}");
        Console.WriteLine(Describe(problem));
        if (failures > 3) { break; }
    }
}

Console.WriteLine($"checked={checkedCount} failures={failures} worstMs={worstMs}");
return failures == 0 ? 0 : 1;

static string Render(RebalanceResult result) =>
    string.Join("|", result.TargetPlacements.Select(p => $"{p.ShardId}:{string.Join(",", p.NodeIds)}"));

static string Describe(RebalanceProblem problem) =>
    "nodes=" + string.Join(",", problem.Nodes.Select(n => $"{n.Id}/{n.Zone}/{n.Capacity}"))
    + " shards=" + string.Join(",", problem.Shards.Select(s => $"{s.Id}/{s.Size}/{s.ReplicationFactor}"))
    + " current=" + string.Join(",", problem.CurrentPlacements.Select(p => $"{p.ShardId}:{string.Join("+", p.NodeIds)}"))
    + " excl=" + string.Join(",", problem.Exclusions.Select(e => $"{e.ShardId}/{e.NodeId}"));

static RebalanceProblem MakeSmall(Random random) => Make(random, 3, 6, 1, 4, 1, 3, 4, 14, 1, 5);

static RebalanceProblem MakeBig(Random random) => Make(random, 8, 24, 4, 30, 1, 4, 40, 160, 3, 30);

static RebalanceProblem Make(
    Random random,
    int minNodes, int maxNodes,
    int minShards, int maxShards,
    int minRf, int maxRf,
    int minCap, int maxCap,
    int minSize, int maxSize)
{
    var nodeCount = random.Next(minNodes, maxNodes + 1);
    var zoneCount = random.Next(1, Math.Min(4, nodeCount) + 1);
    var nodes = new List<NodeSpec>();
    for (var i = 0; i < nodeCount; i++)
    {
        nodes.Add(new($"n{i:00}", $"z{random.Next(zoneCount)}", random.Next(minCap, maxCap + 1)));
    }

    var shardCount = random.Next(minShards, maxShards + 1);
    var shards = new List<ShardSpec>();
    for (var j = 0; j < shardCount; j++)
    {
        shards.Add(new($"s{j:00}", random.Next(minSize, maxSize + 1), random.Next(minRf, Math.Min(maxRf, nodeCount) + 1)));
    }

    var exclusions = new List<PlacementExclusion>();
    foreach (var shard in shards)
    {
        foreach (var node in nodes)
        {
            if (random.NextDouble() < 0.12)
            {
                exclusions.Add(new(shard.Id, node.Id));
            }
        }
    }

    var current = new List<ShardPlacement>();
    foreach (var shard in shards)
    {
        var pool = nodes.Select(n => n.Id).OrderBy(_ => random.Next()).Take(shard.ReplicationFactor).ToList();
        pool.Sort(StringComparer.Ordinal);
        current.Add(new(shard.Id, pool));
    }

    return new(nodes, shards, current, exclusions);
}

static ((double, double, long, int) Objective, string Text)? BruteForce(RebalanceProblem problem)
{
    var nodes = problem.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal).ToArray();
    var shards = problem.Shards.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
    var exclusions = problem.Exclusions.Select(e => (e.ShardId, e.NodeId)).ToHashSet();
    var current = problem.CurrentPlacements.ToDictionary(
        p => p.ShardId,
        p => p.NodeIds.ToHashSet(StringComparer.Ordinal),
        StringComparer.Ordinal);

    var candidates = new List<string[]>[shards.Length];
    long product = 1;
    for (var j = 0; j < shards.Length; j++)
    {
        var shard = shards[j];
        var eligible = nodes
            .Where(n => n.Capacity >= shard.Size && !exclusions.Contains((shard.Id, n.Id)))
            .ToArray();
        var required = Math.Min(shard.ReplicationFactor, eligible.Select(n => n.Zone).Distinct(StringComparer.Ordinal).Count());
        var list = new List<string[]>();
        foreach (var combo in Combinations(eligible, shard.ReplicationFactor))
        {
            if (combo.Select(n => n.Zone).Distinct(StringComparer.Ordinal).Count() == required)
            {
                list.Add(combo.Select(n => n.Id).ToArray());
            }
        }
        if (list.Count == 0) { return null; }
        candidates[j] = list;
        product *= list.Count;
        if (product > 4_000_000) { return null; }
    }

    ((double, double, long, int), string)? best = null;
    var pick = new string[shards.Length][];

    void Search(int depth)
    {
        if (best is not null && depth == 0 && false) { return; }
        if (depth == shards.Length)
        {
            var loads = nodes.ToDictionary(n => n.Id, _ => 0L, StringComparer.Ordinal);
            long bytes = 0;
            var moved = 0;
            for (var j = 0; j < shards.Length; j++)
            {
                foreach (var id in pick[j])
                {
                    loads[id] += shards[j].Size;
                    if (!current[shards[j].Id].Contains(id)) { bytes += shards[j].Size; moved++; }
                }
            }
            foreach (var node in nodes)
            {
                if (loads[node.Id] > node.Capacity) { return; }
            }
            var utils = nodes.Select(n => (double)loads[n.Id] / n.Capacity).ToArray();
            var max = utils.Max();
            var objective = (max, max - utils.Min(), bytes, moved);
            var text = string.Join("|", shards.Select((s, j) => $"{s.Id}:{string.Join(",", pick[j])}"));
            if (best is null || Compare(objective, best.Value.Item1) < 0)
            {
                best = (objective, text);
            }
            return;
        }
        foreach (var option in candidates[depth])
        {
            pick[depth] = option;
            Search(depth + 1);
        }
    }

    Search(0);
    return best;
}

static int Compare((double, double, long, int) a, (double, double, long, int) b)
{
    var c = a.Item1.CompareTo(b.Item1);
    if (c != 0) { return c; }
    c = a.Item2.CompareTo(b.Item2);
    if (c != 0) { return c; }
    c = a.Item3.CompareTo(b.Item3);
    return c != 0 ? c : a.Item4.CompareTo(b.Item4);
}

static IEnumerable<NodeSpec[]> Combinations(NodeSpec[] source, int size)
{
    if (size > source.Length) { yield break; }
    var indices = Enumerable.Range(0, size).ToArray();
    while (true)
    {
        yield return indices.Select(i => source[i]).ToArray();
        var position = size - 1;
        while (position >= 0 && indices[position] == source.Length - size + position) { position--; }
        if (position < 0) { yield break; }
        indices[position]++;
        for (var k = position + 1; k < size; k++) { indices[k] = indices[k - 1] + 1; }
    }
}
