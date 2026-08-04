# Copilot CLI examples

These Evals use the `smevals-copilot` Runner installed with smevals.

Authenticate Copilot CLI first:

```powershell
copilot login
```

Run the prompt-response example:

```powershell
smevals run examples\copilot\prompt-response -g
```

Run the isolated agentic example:

```powershell
smevals run examples\copilot\agentic-file-fix -g
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
