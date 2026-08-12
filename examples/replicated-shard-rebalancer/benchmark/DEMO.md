# Demo

```powershell
Set-Location examples\replicated-shard-rebalancer\benchmark
.\Calibrate-ReplicatedShardRebalancer.ps1
.\Generate-HiddenCases.ps1 -Seed 8675309
$env:REPLICATED_SHARD_REBALANCER_HIDDEN_DIR = "$PWD\private\hidden"
```

The full runner normally generates hidden cases only after every model session
has finished, then grades all completed runs against the same bundle.
