# Migration — Phase 437 per-component resource envelopes (`ResourceEnvelope`)

**Status:** opt-in and additive. The one shipped-surface change is a new `ServerConfig.ResourceEnvelopes` field, which defaults to `Map.empty`; every consumer builds its config with `{ ServerConfig.defaults with … }`, so **no consumer action is required to upgrade**. A deployment that declares no envelope composes byte-for-byte what it did before and pays nothing at runtime (GP 11 / GP 13).

## Why

A composition can already say what each component *is* (Phase 280 manifest), what it *touches* (Phase 282/296), what it *needs* (Phase 432), what it *stores* (Phase 433) and what it *exposes* (Phase 438). It could not say what any of it is **allowed to consume**. In practice one module's runaway job loop or one endpoint's traffic spike is absorbed by whatever process-wide limits happen to exist, and the blast radius is discovered rather than declared.

`ResourceEnvelope` makes the budget a declared, per-`ComponentId` value — and enforces it at seams the SDK already owns, so nothing new runs.

## The declaration

```fsharp
open ToolUp.Platform

let envelopes =
    ResourceEnvelope.emptySignature
    |> ResourceEnvelope.declare
        (ComponentId.ofModule "Reports")
        (ResourceEnvelope.unconstrained
         |> ResourceEnvelope.withMaxJobConcurrency 2
         |> ResourceEnvelope.withMaxRequestsPerMinute 120
         |> ResourceEnvelope.withMemoryHint "512Mi")

let config = {
    ServerConfig.defaults with
        ResourceEnvelopes = envelopes
}
```

Four optional dimensions, `None` everywhere by default:

| Field | Enforced at | `None` means |
|---|---|---|
| `MaxJobConcurrency` | the Phase 9b job-handler seam | the scheduler's existing behaviour |
| `MaxRequestsPerMinute` | the Phase 56 rate-limit middleware | whatever policy is already declared |
| `MaxQueueDepth` | any queue that consults `admitQueueItem` | the queue's own capacity behaviour |
| `MemoryHint` | **nothing** — advisory, backend-interpreted | unset |

An `EnvelopeSignature` is `Map<ComponentId, ResourceEnvelope>` — the fourth sidecar beside `CapabilitySignature` (282), `RequirementsSignature` (432) and `FootprintSignature` (433), keyed against the same id space. An **absent id resolves to `ResourceEnvelope.unconstrained`**, so a stale or partial map never constrains anything by its mere existence.

## Ordering — declare the config before adding modules

Two of the three seams are wired inside `ServerApp.addModule`, because that is the only place a job declaration or a route prefix is still attached to its owning `ComponentId`. They read `app.Config.ResourceEnvelopes`, so use the canonical order:

```fsharp
ServerApp.empty
|> ServerApp.withConfig config          // envelopes declared here…
|> ServerApp.addModules [ … ]           // …are applied here
```

Setting `ResourceEnvelopes` *after* `addModules` leaves those two seams unenforced. The direct `ResourceEnvelopeEnforcement.admit*` functions are unaffected by ordering.

## What each seam does

**Job concurrency.** A budgeted component's `IJobHandler` is decorated at registration with a non-blocking gate. An over-budget attempt returns `TransientFailure`, which routes into the scheduler's **existing** `JobRetryPolicy` backoff — the job is *deferred, never dropped*, and no dispatch thread parks. An unbudgeted component's handler is returned as the **same reference**: no wrapper, no semaphore, no branch. That is also why fairness is unaffected — an unconstrained component never touches a shared gate.

The gate is process-local, matching the in-process scheduler it decorates (whose multi-instance use is already refused by `JobSchedulerInstanceValidator`). A distributed scheduler companion enforces its own ceiling from the same declared number.

**Request rate.** A declared `MaxRequestsPerMinute` *projects* onto the component's `RoutePrefixes` as ordinary `RouteLimit` entries appended to `ServerConfig.RateLimits` — so the shipped middleware, the shipped `IRateLimitStore` (Phase 56) and the shipped `RateLimitDecisionEvent` audit path do the work. The key is `ByComposite "component:<id>"`, i.e. **component-wide** rather than per-caller; over-budget requests get the typed 429.

A prefix an operator has already governed with an explicit `RouteLimit` is **skipped**, not shadowed — `matchPolicy` is first-match-wins, so an appended duplicate could never fire, and the operator's policy stays authoritative. `ResourceEnvelopeEnforcement.shadowedPrefixes` reports which prefixes that applied to.

**Queue depth.** `admitQueueItem` returns a typed `EnvelopeAdmission`; the caller applies back-pressure per its own overflow policy. A caller that discards the refusal and drops the item is using it wrong.

> **Known gap.** The SDK's own bounded queue (`IngestionQueue`, `ToolUp.RAG.Core`) is a **single shared** channel with a global `IngestionQueueCapacity` and no per-component partition, so there is no component-attributed depth for it to consult. Partitioning it would mean per-component counters — new runtime machinery, which this phase explicitly does not add. The adapter is therefore shipped and tested, and enforced wherever a queue *does* partition depth per component; the shipped ingestion queue keeps its existing capacity behaviour unchanged.

## Observability (GP 6)

Every refusal passes through `ResourceEnvelopeEnforcement.observeRefusal`: one `IMetricsSink` counter, `toolup.resource_envelope.refused`, tagged `component` + `dimension`, plus a `Warn` log worded through `ResourceEnvelope.describeRefusal` so two seams never describe the same refusal differently. Refusals are also *returned* as typed `EnvelopeRefusal` values carrying the component, dimension, limit and observed level — the observability is the sink's job, the honesty is the type's.

## Budget pressure on the Phase 290 rollup (437.D)

```fsharp
let! rollup = ComponentHealthRollup.forApp app

let withPressure =
    rollup |> ComponentHealthRollup.withPressure config.ResourceEnvelopes observedBy

ComponentHealthRollup.underPressure 90 withPressure   // at ≥90% of a ceiling
ComponentHealthRollup.describePressure withPressure   // one line per budgeted component
```

`observedBy : ComponentId -> EnvelopeDimension -> int` supplies the current level from wherever the deployment already tracks it; nothing here starts a probe or keeps a counter. A component with no envelope contributes **no entry** — not an empty list, and never a zero limit that would read as "budgeted at nothing". `ComponentPressureRollup` is a sidecar type; `ComponentHealthRollup` itself is unchanged.

## Verification

`dotnet run --project Build.fsproj -- VerifyAll` — `ResourceEnvelopeTests` asserts a `MaxJobConcurrency = 2` component never runs a third handler body concurrently (against a real peak-concurrency probe released by an event, not a timer), that an unbudgeted handler comes back as the same reference and an unbudgeted composition projects no `RouteLimit`, and that every refusal reaches the metric sink.

## Rollback

Remove the `ResourceEnvelopes` assignment from your `ServerConfig`. With the map empty, the gate is never applied, no `RouteLimit` is projected, and every admission short-circuits.
