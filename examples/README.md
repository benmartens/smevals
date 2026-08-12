# Examples

These Evals use the `smevals-copilot` Runner installed with smevals.

Authenticate Copilot CLI first:

```powershell
copilot login
```

Run the prompt-response example:

```powershell
smevals run examples\prompt-response -g
```

Run the isolated agentic example:

```powershell
smevals run examples\agentic-file-fix -g
```

The agentic task copies its fixture into each Run's `workspace` directory.
Copilot edits that copy, not the checked-in fixture.

The three examples inherited from upstream also include Copilot configs:

```powershell
smevals run examples\haiku -c copilot -g
smevals run examples\markdown-tables -c copilot -g
smevals run examples\pelican-riding-a-bicycle -c copilot -g svg-only
```

The haiku and markdown-table examples use their original deterministic
graders. The pelican `svg-only` grader extracts and validates the generated
SVG without requiring the original Unix-only renderer and `llm` vision judge.

The carton-packing example is a larger agentic coding benchmark:

```powershell
examples\carton-packing\benchmark\Run-CartonPackingBenchmark.ps1
```

It runs models against an incomplete .NET 10 packing engine, generates hidden
cases only after model execution, grades placement validity and optimization
quality, and builds a static demo site with SVG layouts and source patches.

The query-optimizer example uses the same isolated benchmark pattern for a
cost-based relational optimizer:

```powershell
examples\query-optimizer\benchmark\Run-QueryOptimizerBenchmark.ps1
```

Its independent grader validates physical plan trees and compares their
integer execution cost with an exact subset-DP reference optimizer.
