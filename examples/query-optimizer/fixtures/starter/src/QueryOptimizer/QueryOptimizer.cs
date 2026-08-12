namespace QueryPlanning;

public sealed class QueryOptimizer
{
    public QueryPlan Optimize(QueryProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        // TODO: Implement a deterministic cost-based optimizer. Returning no
        // plan keeps the starter buildable but intentionally fails engine tests.
        return QueryPlan.Empty;
    }
}
