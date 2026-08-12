# Query Optimizer Demo

This benchmark asks coding models to implement a deterministic physical query
optimizer in a dependency-free .NET 10 solution.

The independent grader validates every plan tree, recomputes its integer cost,
compares it with an exact subset-DP reference, checks repeated output for
determinism, and renders a showcase plan as SVG.
