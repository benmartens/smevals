namespace FieldServiceRoutePlanner;

public sealed record Technician(
    string Id,
    List<string> Skills,
    int ShiftStart,
    int ShiftEnd);

public sealed record ServiceJob(
    string Id,
    string Location,
    List<string> RequiredSkills,
    int Duration,
    int WindowStart,
    int WindowEnd,
    int Value);

public sealed record RoutePlanningProblem(
    string Depot,
    Dictionary<string, Dictionary<string, int>> TravelTimes,
    List<Technician> Technicians,
    List<ServiceJob> Jobs);

public sealed record TechnicianRoute(
    string TechnicianId,
    List<string> JobIds);

public sealed record RoutePlan(List<TechnicianRoute> Routes)
{
    public static RoutePlan Empty { get; } = new([]);
}

public sealed record RouteStopTiming(
    string JobId,
    int Arrival,
    int ServiceStart,
    int ServiceEnd);

public sealed record RouteTiming(
    string TechnicianId,
    List<RouteStopTiming> Stops,
    int ReturnTime,
    int TravelMinutes);

public sealed record ValidationIssue(string Code, string Message);

public sealed record RouteValidationReport(
    List<ValidationIssue> Issues,
    int ServedValue,
    int TotalTravel,
    List<RouteTiming> RouteTimings)
{
    public bool IsValid => Issues.Count == 0;
}
