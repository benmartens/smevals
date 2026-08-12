#:project src/ReplicatedShardRebalancer/ReplicatedShardRebalancer.csproj

using ReplicatedShardRebalancer;

var engine = new ReplicatedShardRebalancer.ReplicatedShardRebalancer();
int checkedCases = 0, skipped = 0, mismatches = 0, invalid = 0;

for (int seed = 1; seed <= 4000; seed++)
{
    var rng = new Random(seed);
    int n = rng.Next(3, 7);
    int s = rng.Next(1, 5);
    int zoneCount = rng.Next(1, 4);

    var nodes = new List<NodeSpec>();
    for (int i = 0; i < n; i++)
    {
        nodes.Add(new($"n{(char)('a' + i)}", $"z{rng.Next(zoneCount)}", rng.Next(6, 26)));
    }
    var shards = new List<ShardSpec>();
    for (int i = 0; i < s; i++)
    {
        shards.Add(new($"s{i}", rng.Next(1, 9), rng.Next(1, Math.Min(4, n) + 1)));
    }
    var exclusions = new List<PlacementExclusion>();
    foreach (var sh in shards)
    {
        foreach (var nd in nodes)
        {
            if (rng.NextDouble() < 0.12) exclusions.Add(new(sh.Id, nd.Id));
        }
    }
    var current = new List<ShardPlacement>();
    foreach (var sh in shards)
    {
        var picked = nodes.OrderBy(_ => rng.Next()).Take(sh.ReplicationFactor)
            .Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        current.Add(new(sh.Id, picked));
    }

    var problem = new RebalanceProblem(nodes, shards, current, exclusions);
    var exSet = new HashSet<(string, string)>(exclusions.Select(e => (e.ShardId, e.NodeId)));

    // Enumerate candidate sets per shard.
    var cands = new List<List<string[]>>();
    bool feasibleShape = true;
    foreach (var sh in shards)
    {
        var eligible = nodes.Where(nd => nd.Capacity >= sh.Size && !exSet.Contains((sh.Id, nd.Id))).ToArray();
        int req = RebalanceValidator.MaximumZoneDiversity(sh, nodes, exSet);
        var list = new List<string[]>();
        void Rec(int start, List<NodeSpec> acc)
        {
            if (acc.Count == sh.ReplicationFactor)
            {
                if (acc.Select(a => a.Zone).Distinct(StringComparer.Ordinal).Count() >= req)
                    list.Add(acc.Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray());
                return;
            }
            for (int i = start; i < eligible.Length; i++)
            {
                acc.Add(eligible[i]);
                Rec(i + 1, acc);
                acc.RemoveAt(acc.Count - 1);
            }
        }
        Rec(0, []);
        list.Sort((a, b) => CompareSeq(a, b));
        if (list.Count == 0) feasibleShape = false;
        cands.Add(list);
    }
    if (!feasibleShape) { skipped++; continue; }

    long total = 1;
    foreach (var c in cands) { total *= c.Count; if (total > 400_000) break; }
    if (total > 400_000) { skipped++; continue; }

    // Brute force lexicographic optimum.
    var capOf = nodes.ToDictionary(x => x.Id, x => x.Capacity, StringComparer.Ordinal);
    var curOf = current.ToDictionary(x => x.ShardId, x => new HashSet<string>(x.NodeIds, StringComparer.Ordinal), StringComparer.Ordinal);
    string[][]? best = null;
    (long, long, long, long, long, int) bestKey = default;
    var choice = new string[shards.Count][];

    void Enumerate(int depth)
    {
        if (depth == shards.Count)
        {
            var loads = nodes.ToDictionary(x => x.Id, _ => 0L, StringComparer.Ordinal);
            long bytes = 0; int reps = 0;
            for (int i = 0; i < shards.Count; i++)
            {
                foreach (var nd in choice[i])
                {
                    loads[nd] += shards[i].Size;
                    if (!curOf[shards[i].Id].Contains(nd)) { bytes += shards[i].Size; reps++; }
                }
            }
            foreach (var nd in nodes) if (loads[nd.Id] > nd.Capacity) return;

            long maxN = 0, maxD = 1, minN = long.MaxValue, minD = 1; bool first = true;
            foreach (var nd in nodes)
            {
                long l = loads[nd.Id], c = nd.Capacity;
                if (Cmp(l, c, maxN, maxD) > 0) { maxN = l; maxD = c; }
                if (first || Cmp(l, c, minN, minD) < 0) { minN = l; minD = c; first = false; }
            }
            var key = (maxN, maxD, minN, minD, bytes, reps);
            var snapshot = choice.Select(x => x).ToArray();
            if (best is null || CompareKey(key, bestKey) < 0
                || (CompareKey(key, bestKey) == 0 && CompareAssign(snapshot, best) < 0))
            {
                bestKey = key; best = snapshot;
            }
            return;
        }
        foreach (var c in cands[depth]) { choice[depth] = c; Enumerate(depth + 1); }
    }
    Enumerate(0);
    if (best is null) { skipped++; continue; }

    var result = engine.Rebalance(problem);
    var report = RebalanceValidator.Validate(problem, result);
    if (!report.IsValid)
    {
        invalid++;
        Console.WriteLine($"seed {seed}: INVALID -> {string.Join("; ", report.Issues.Select(i => i.Code))}");
        continue;
    }
    var got = result.TargetPlacements.Select(p => p.NodeIds.ToArray()).ToArray();
    if (CompareAssign(got, best) != 0)
    {
        mismatches++;
        if (mismatches <= 5)
        {
            Console.WriteLine($"seed {seed}: MISMATCH");
            Console.WriteLine($"  got  {Fmt(got)}");
            Console.WriteLine($"  want {Fmt(best)}");
        }
    }
    checkedCases++;
}

Console.WriteLine($"checked={checkedCases} skipped={skipped} invalid={invalid} mismatches={mismatches}");
return mismatches == 0 && invalid == 0 ? 0 : 1;

static string Fmt(string[][] a) => string.Join(" | ", a.Select(x => string.Join(",", x)));

static int Cmp(long a, long b, long c, long d) => ((Int128)a * d).CompareTo((Int128)c * b);

static int CompareKey((long, long, long, long, long, int) x, (long, long, long, long, long, int) y)
{
    int r = Cmp(x.Item1, x.Item2, y.Item1, y.Item2);
    if (r != 0) return r;
    r = -Cmp(x.Item3, x.Item4, y.Item3, y.Item4);
    if (r != 0) return r;
    r = x.Item5.CompareTo(y.Item5);
    return r != 0 ? r : x.Item6.CompareTo(y.Item6);
}

static int CompareSeq(string[] a, string[] b)
{
    for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
    {
        int r = string.CompareOrdinal(a[i], b[i]);
        if (r != 0) return r;
    }
    return a.Length.CompareTo(b.Length);
}

static int CompareAssign(string[][] a, string[][] b)
{
    for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
    {
        int r = CompareSeq(a[i], b[i]);
        if (r != 0) return r;
    }
    return a.Length.CompareTo(b.Length);
}
