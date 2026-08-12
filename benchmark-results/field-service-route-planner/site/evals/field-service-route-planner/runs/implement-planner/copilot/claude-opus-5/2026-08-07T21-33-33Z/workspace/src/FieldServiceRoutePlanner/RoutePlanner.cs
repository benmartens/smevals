namespace FieldServiceRoutePlanner;

public sealed class RoutePlanner
{
    public RoutePlan Plan(RoutePlanningProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var model = PlanningModel.Build(problem);
        var assignment = new PlanningSolver(model).Solve();

        var routes = new List<TechnicianRoute>(model.TechnicianCount);
        for (var tech = 0; tech < model.TechnicianCount; tech++)
        {
            var jobIds = new List<string>(assignment[tech].Count);
            foreach (var job in assignment[tech])
            {
                jobIds.Add(model.JobIds[job]);
            }

            routes.Add(new TechnicianRoute(model.TechnicianIds[tech], jobIds));
        }

        return routes.Count == 0 ? RoutePlan.Empty : new RoutePlan(routes);
    }
}
