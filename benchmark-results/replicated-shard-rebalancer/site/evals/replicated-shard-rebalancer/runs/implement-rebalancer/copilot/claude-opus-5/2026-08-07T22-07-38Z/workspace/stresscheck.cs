#:project src/ReplicatedShardRebalancer/ReplicatedShardRebalancer.csproj

using System.Diagnostics;
using ReplicatedShardRebalancer;

var engine = new ReplicatedShardRebalancer.ReplicatedShardRebalancer();
int bad = 0, nondet = 0;
double worst = 0;
var sw = new Stopwatch();

for (int seed = 1; seed <= 60; seed++)
{
    var rng = new Random(seed);
    int n = rng.Next(8, 21);
    int s = rng.Next(6, 21);
    int zoneCount = rng.Next(2, 5);
    var nodes = new List<NodeSpec>();
    for (int i = 0; i < n; i++)
        nodes.Add(new($"node-{i:D2}", $"z{rng.Next(zoneCount)}", rng.Next(400, 1200)));
    var shards = new List<ShardSpec>();
    for (int i = 0; i < s; i++)
        shards.Add(new($"shard-{i:D2}", rng.Next(10, 120), rng.Next(2, 4)));
    var exclusions = new List<PlacementExclusion>();
    foreach (var sh in shards)
        foreach (var nd in nodes)
            if (rng.NextDouble() < 0.08) exclusions.Add(new(sh.Id, nd.Id));
    var exSet = new HashSet<(string, string)>(exclusions.Select(e => (e.ShardId, e.NodeId)));
    var current = new List<ShardPlacement>();
    foreach (var sh in shards)
    {
        var pool = nodes.Where(nd => nd.Capacity >= sh.Size && !exSet.Contains((sh.Id, nd.Id))).ToList();
        if (pool.Count < sh.ReplicationFactor) pool = nodes.ToList();
        current.Add(new(sh.Id, pool.OrderBy(_ => rng.Next()).Take(sh.ReplicationFactor)
            .Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList()));
    }
    var problem = new RebalanceProblem(nodes, shards, current, exclusions);

    sw.Restart();
    var r1 = engine.Rebalance(problem);
    sw.Stop();
    worst = Math.Max(worst, sw.Elapsed.TotalSeconds);
    var r2 = engine.Rebalance(problem);
    if (!r1.TargetPlacements.Zip(r2.TargetPlacements).All(p =>
            p.First.ShardId == p.Second.ShardId && p.First.NodeIds.SequenceEqual(p.Second.NodeIds)))
    {
        nondet++;
    }
    var rep = RebalanceValidator.Validate(problem, r1);
    var pre = RebalanceValidator.Validate(problem, new RebalanceResult(problem.CurrentPlacements));
    if (!rep.IsValid)
    {
        bad++;
        Console.WriteLine($"seed {seed} n={n} s={s}: {string.Join("; ", rep.Issues.Select(i => i.Code).Distinct())}");
    }
    else if (seed <= 6)
    {
        Console.WriteLine($"seed {seed} n={n} s={s} maxUtil {pre.MaximumNodeUtilization:F3}->{rep.MaximumNodeUtilization:F3} "
            + $"spread {rep.UtilizationSpread:F3} bytes {rep.MovedBytes} reps {rep.MovedReplicaCount} {sw.ElapsedMilliseconds}ms");
    }
}
Console.WriteLine($"invalid={bad} nondeterministic={nondet} worstSeconds={worst:F2}");
return bad == 0 && nondet == 0 ? 0 : 1;
