namespace FieldServiceRoutePlanner;

public sealed class RoutePlanner
{
    public RoutePlan Plan(RoutePlanningProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        // TODO: Implement a deterministic assignment and sequencing strategy
        // that lexicographically maximizes served value and then minimizes
        // travel while satisfying every rule in README.md.
        return RoutePlan.Empty;
    }
}
