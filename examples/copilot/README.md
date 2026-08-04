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
