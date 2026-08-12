# Field-Service Route Planner Challenge

Implement `RoutePlanner.Plan` in
`src/FieldServiceRoutePlanner/RoutePlanner.cs`.

The dependency-free solution targets .NET 10. Public models, a JSON CLI, an
independent visible validator, scenarios, and a console test harness are
provided. Hidden grading uses a separate Python validator and generated cases.

```powershell
dotnet run --project visible-tests\FieldServiceRoutePlanner.VisibleTests
dotnet run --project src\FieldServiceRoutePlanner.Cli -- `
  scenarios\basic.json result.json
```

## JSON contract

Times and travel durations are non-negative integer minutes. `travelTimes` is
a complete directed matrix over the depot and every job location, so travel
may be asymmetric. A result contains exactly one route per technician:

```json
{
  "routes": [
    {"technicianId": "tech-a", "jobIds": ["job-1", "job-2"]}
  ]
}
```

## Rules

- IDs and skills are case-sensitive ordinal strings.
- Every technician appears exactly once, ordered by technician ID.
- Each job is assigned at most once.
- A technician must contain every skill required by an assigned job.
- A technician leaves the depot at `shiftStart`.
- For each job, add directed travel from the current location. Waiting until
  `windowStart` is allowed.
- Service must finish no later than `windowEnd`.
- After the last job, include directed travel back to the depot; return must
  be no later than `shiftEnd`.
- Job order within each route is operational and must be preserved.
- Repeated calls with the same input must return identical routes.

## Objective

Lexicographically:

1. maximize total value of distinct served jobs;
2. among plans with that value, minimize total directed travel minutes,
   including every return to the depot.

The starter intentionally returns an invalid empty plan. Hidden cases exercise
skills, waiting, tight windows, clustering, value traps, asymmetric travel,
and cross-technician assignment.
