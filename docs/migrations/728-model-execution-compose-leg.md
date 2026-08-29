# Phase 728 — the model-execution compose leg (`IModelRegistry` registration)

**What changes.** `IModelRegistry` can now be composed. Until this phase no SDK compose path
registered one, so a deployment that mounted `ModelExecutionApi` had to hand-register the registry
and learned that from a `SubstrateDisabled "model registry"` refusal on its **first request** — the
same refusal a deployment gets when it has deliberately left a substrate off, which is exactly what
made the gap invisible.

Two additions, both opt-in and both absent by default (GP 13):

1. **`ServerApp.withModelExecution`** — a compose leg registering `IModelRegistry` (and optionally
   the scorer, the Phase 640 executor policy, and Phase 651 registration observers).
2. **The `model-execution-deps` preflight validator** — a startup **warning** naming the missing
   registration when `ServerConfig.ModelExecution = EnabledModelExecutionApi` and nothing composed a
   registry.

**Not breaking.** A deployment that does not call `withModelExecution` appends no registration at
all: the composed DI graph, the mounted routes and every refusal are byte-for-byte what they were.
`ServerApp.empty.ModelExecutionCompose` is `None`, and the validator returns `Ok` whenever the API
is not mounted.

**Version.** Additive minor bump under the SemVer-on-`0.x` policy.

## Adopting the leg — the diff

`Program.fs` (or wherever your composition root builds its `ServerApp`):

```fsharp
open ToolUp.Platform

ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        ModelExecution = EnabledModelExecutionApi
        ModelFitting = EnabledModelFitting
        Datasets = BlobDatasets
        JobScheduler = InProcessJobScheduler
   }
// NEW — registers IModelRegistry over the IDataObjectStore + IAuditLog
// already composed. Nothing else about the composition changes.
|> ServerApp.withModelExecution ComposeModelExecution.ModelExecutionComposeOptions.defaults
|> ServerApp.run
```

If you were already hand-registering a registry, **delete the hand-registration and keep the leg**,
or keep the hand-registration and skip the leg — either silences the validator. The leg uses
`TryAddSingleton`, so composing it over your own pre-registered `IModelRegistry` leaves yours in
place rather than overriding it.

Overriding individual legs, each independent of the others:

```fsharp
let modelExecution =
    ComposeModelExecution.ModelExecutionComposeOptions.defaults
    // A companion registry, or one you already decorated.
    |> ComposeModelExecution.ModelExecutionComposeOptions.withRegistry myRegistry
    // Phase 651 — observed registrations (a promotion-policy binding, a webhook).
    |> ComposeModelExecution.ModelExecutionComposeOptions.withObserver myObserver
    // Required for RequestScore — see below.
    |> ComposeModelExecution.ModelExecutionComposeOptions.withScorer myScorer
    // Phase 640 — refuse scoring against gate-failed artifacts.
    |> ComposeModelExecution.ModelExecutionComposeOptions.withPolicy ModelExecutionPolicy.refuseGateFailures
    // Phase 646 — the provenance attachment cap the default registry publishes.
    |> ComposeModelExecution.ModelExecutionComposeOptions.withAttachmentLimits myLimits
```

## What the leg composes, and what stays yours

| Resolution reached from `ModelExecutionApi` | Composed by |
|---|---|
| `AccessContext` | the SDK (scope resolver, per request) |
| `ITeamStore` / `IJobScheduler` / `IAuditLog` | the SDK, on their existing config modes |
| `IDatasetStore` | the SDK (`Datasets = BlobDatasets \| CustomDatasetStore`) |
| `ModelFitProviderRegistry` | the SDK (`ModelFitting = EnabledModelFitting`) |
| `ComputeBudgetGuard` | the SDK (`ComputeBudget = EnabledComputeBudget`) |
| `IModelRegistry` | **this leg** — blob-backed default, or your own |
| `ModelExecutionPolicy` | this leg, optionally; absent means `permissive` (unchanged behaviour) |
| `IModelScorer` | **you**, always — see below |

**`IModelScorer` stays consumer-supplied, deliberately.** The default registry needs only substrate
the SDK already composes (`IDataObjectStore` + `IAuditLog`, plus `ILineageStore` when present, which
adds the artifact → dataset-version lineage edge). A default *scorer* would need a
`ModelScoreProviderRegistry`, and one with no providers scores nothing — it would answer every
request with a refusal that reads as an SDK defect rather than an unconfigured deployment. So
`RequestScore` needs a scorer you compose (via `withScorer`, or your own DI registration); every
other method on the face works from the leg alone.

## Verification

1. **Startup.** With `ModelExecution = EnabledModelExecutionApi` and the leg composed, the preflight
   summary carries no `model-execution-deps` line. Remove the leg and it warns, naming the builder.
2. **First request.** `GetOutcome` against an unknown key returns `NotFound` (the registry answered)
   rather than `SubstrateDisabled "model registry"` (nothing to answer).
3. **Byte-parity.** A composition that never calls `withModelExecution` is unchanged — assert it by
   diffing your service-descriptor count before and after upgrading, or simply by observing that the
   pre-existing refusal is identical.

## Rollback

Delete the `|> ServerApp.withModelExecution …` line. Nothing else is required: the registration is
appended only by that call, no config field changed, no route moved, and the validator's finding
degrades to the warning it was designed to be rather than blocking startup. Reinstate any
hand-registration you removed in step one if you had one.

## See also

- `ComposeModelExecution` (`src/ToolUp.Platform.Server/Server/ComposeModelExecution.fs`) — the leg,
  with the full resolution inventory in its file header.
- `ModelExecutionDepsValidator` — the preflight finding and its `Warning`-not-`Error` rationale.
- [`docs/platform/which-store-for-what.md`](../platform/which-store-for-what.md) — which store to
  reach for, and how the registry is composed.
