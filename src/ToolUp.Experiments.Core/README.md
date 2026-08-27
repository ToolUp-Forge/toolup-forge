# ToolUp.Experiments.Core

A/B **experiment substrate** — the honest minimal experimentation floor over the Phase 5c
feature-flag layer: a scoped experiment store contract, **deterministic** weight-respecting
variant assignment, and exposure-event types.

Statistical-significance **verdicts are out of scope** — the exposure stream is the input a
downstream analytics / telemetry sink consumes. Pure F# (FSharp.Core only).

## What's here

- **`Experiment`** / **`Variant`** / **`ExperimentStatus`** — immutable definitions (GP 5).
- **`Assignment.assign`** — deterministic, weight-respecting: the same principal always lands
  in the same variant for an experiment (stable SHA-256 bucket of `"{id}:{principal}"`), no
  stored state.
- **`IExperimentStore`** — scoped CRUD over definitions.
- **`IExposureSink`** — `Record(scopeId, exposure)`; the substrate names no event store, so a
  deployment adapts this to the shipped `IEventStore` at compose time.

The in-memory store, the assign-and-log-once service, and dev sinks live in
`ToolUp.Experiments.Server`.

## Example

```fsharp skip=fragment
open ToolUp.Experiments

let exp = { Id = "checkout-cta"; Status = Running
            Variants = [ { Key = "control"; Weight = 0.5 }; { Key = "blue"; Weight = 0.5 } ] }

match Assignment.assign exp "user-123" with
| Some v -> // v.Key — stable for user-123
| None -> ()
```
