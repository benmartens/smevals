# Carton Packing Challenge

Implement `CartonPacker.Pack` in `src/CartonPacking/CartonPacker.cs`.

The solution is dependency-free and targets .NET 10. The public domain types,
JSON CLI, orientation helper, and visible validator are provided. Hidden
grading uses an independent validator and generated scenarios.

## Run the visible tests

```powershell
dotnet run --project visible-tests\CartonPacking.VisibleTests
```

The starter intentionally fails the engine tests until `Pack` is implemented.

## Try the CLI

```powershell
dotnet run --project src\CartonPacking.Cli -- `
  scenarios\exact-fit.json result.json
```

The CLI reads a `PackingProblem` JSON file and writes:

```json
{
  "placements": [
    {
      "cartonId": "cube",
      "instance": 0,
      "x": 0,
      "y": 0,
      "z": 0,
      "width": 5,
      "depth": 5,
      "height": 5
    }
  ]
}
```

## Rules

- Coordinates and dimensions are non-negative integers.
- Placements are axis-aligned.
- Normal cartons may use any distinct permutation of their dimensions.
- `keepUpright` cartons must retain their original height; width/depth may
  swap.
- Placements may touch but may not overlap by positive volume.
- Quantity and container weight limits must be respected.
- A carton on the floor (`z == 0`) is supported.
- A carton above the floor must have 100% of its rectangular base covered by
  cartons whose top face is exactly at its bottom `z`.
- Support may be shared by multiple cartons. Shared edges have zero area.
- Return placements sorted by carton ID, instance, then coordinates.

## Objective

1. Maximize total shipment value.
2. Among equal-value layouts, maximize packed volume.

The hidden benchmark awards partial credit for valid layouts relative to a
strong reference solution. A simple valid heuristic is better than an invalid
attempt, but the hidden cases include rotation, support, weight, quantity, and
greedy-choice traps.

## Full-base support example

Two cartons at `(0,0,0)` sized `2x4x2` and `(2,0,0)` sized `2x4x2` jointly
support a carton at `(0,0,2)` sized `4x4x1`. If either lower carton is absent,
the upper carton is only partially supported and is invalid.
