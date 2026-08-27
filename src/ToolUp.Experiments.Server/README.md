# ToolUp.Experiments.Server

Server-side defaults for [`ToolUp.Experiments.Core`](../ToolUp.Experiments.Core/README.md):
the in-memory experiment store, dev exposure sinks, and the assign-and-log-once service.

## What's here

- **`InMemoryExperimentStore`** — dev / single-instance `IExperimentStore`. Swap for a durable
  store under multiple replicas.
- **`ExperimentService`** — `Assign(scope, experimentId, principal)`: resolves the experiment,
  assigns deterministically (only `Running` experiments assign), and logs **one** exposure per
  `(scope, experiment, principal)` triple via the injected `IExposureSink`.
- **`NoOpExposureSink`** / **`CollectingExposureSink`** — the discard default and a dev/test
  collecting sink. Production wires a sink that writes to the shipped `IEventStore`.

## Example

```fsharp skip=fragment
open ToolUp.Experiments
open ToolUp.Experiments.Server

let store = InMemoryExperimentStore() :> IExperimentStore
let svc = ExperimentService(store, NoOpExposureSink())
let! variant = svc.Assign("team-a", "checkout-cta", "user-123")
```
