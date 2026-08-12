namespace ReplicatedShardRebalancer;

public sealed class ReplicatedShardRebalancer
{
    public RebalanceResult Rebalance(RebalanceProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        // TODO: Implement a deterministic rebalancer satisfying every hard
        // rule in README.md and optimizing the lexicographic objective.
        // Returning an empty result keeps the starter buildable but
        // intentionally fails the visible engine tests.
        return RebalanceResult.Empty;
    }
}
