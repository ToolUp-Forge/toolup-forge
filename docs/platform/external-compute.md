# External compute

`IExternalComputeDispatcher` is the seam for handing a unit of work to compute that is **not this process** — a GPU box, a batch or training service, a worker pool.

Background jobs run **in-process**: `IJobHandler.Execute` is an F# `async` invoked on the server that scheduled it. That is the right model for the overwhelming majority of work, and it is the wrong model for a deep-learning fit, a physical simulation, or a scene render. Before this seam existed, a consumer with that shape of workload had to reach out to an external service by hand from inside a handler, invent its own submit / poll / result protocol, and hold a scheduler slot open for the duration of work the server was not doing.

The dispatcher makes that pattern typed and portable. `Submit` returns an opaque handle **immediately**; `Poll` maps a handle to a typed outcome; `Cancel` requests teardown.

## The handler brokers; it does not run

This is the whole design, and it is worth stating before the API: **nothing in this seam executes the payload.** A dispatcher accepts work, hands back a reference, and answers questions about it. The heavy compute happens somewhere the SDK knows nothing about, in a runtime the SDK does not host.

Two consequences follow, and both are the point rather than a limitation:

- **The payload is opaque.** `ExternalWorkSpec.Payload` is pre-serialised JSON the *backend* understands. The platform never parses, normalises, or re-hashes it — the same restraint `IModelFitProvider` applies to a model spec. A broker that understood its payloads would have to be taught every backend's schema.
- **The result is a reference, not a value.** A successful outcome carries `resultRef` — a blob key, an artefact URI, a content hash. The platform echoes it and never dereferences it. Work that produced a 40 GB checkpoint must not round-trip through the web server to be "finished".

## The seam

```fsharp skip=signature
type IExternalComputeDispatcher =
    abstract Backend: string
    abstract Submit: scopeId: string * spec: ExternalWorkSpec -> Async<Result<ExternalHandle, ExternalComputeError>>
    abstract Poll: handle: ExternalHandle -> Async<ExternalOutcome>
    abstract Cancel: handle: ExternalHandle -> Async<unit>
```

`Submit` returning `Ok` means **accepted, not completed**. The caller persists the handle and polls; the call never waits for the work.

`Poll` is non-destructive and idempotent — polling a terminal handle returns the same terminal outcome, so a poller that runs twice is harmless. A handle the backend no longer recognises is reported as `Failed` with a terminal error, never as a fabricated `Cancelled` or `Succeeded`.

`Cancel` is best-effort and idempotent: cancelling an already-terminal handle is not an error. It returns when the request has been *lodged*, not when teardown has finished — confirm via `Poll`.

## The value types

All four live in `ToolUp.Platform.Core` (`Shared/ExternalCompute.fs`) and are BCL primitives only, so they ship in the Fable-packed source and a client can hold a handle opaquely.

```fsharp skip=signature
type ExternalHandle = {
    HandleId: Guid       // platform-minted identity
    Backend: string      // which backend accepted it
    ScopeId: string      // the tenant scope (GP 4 rides the handle)
    NativeRef: string    // the backend's OWN token — opaque, never parsed
    SubmittedAt: DateTime
}

type ExternalWorkSpec = {
    Kind: string                        // backend-resolved work discriminator
    Payload: string                     // pre-serialised JSON, opaque
    ResourceHints: Map<string, string>  // advisory: "gpu" -> "1", "memory" -> "16Gi"
    Timeout: TimeSpan option            // advisory budget; None = backend default
    Idempotency: string option          // Some key => re-submit returns the existing handle
}

[<RequireQualifiedAccess>]
type ExternalOutcome =
    | Pending
    | Running of progress: float option
    | Succeeded of resultRef: string
    | Failed of ExternalComputeError
    | Cancelled

type ExternalComputeError = { Message: string; Retriable: bool }
```

### Why `ResourceHints` is a string map

This is the seam's genuine hot zone: `ResourceHints` has to describe "one A100 and 16 GiB" without encoding any particular scheduler's vocabulary. A typed record — `{ GpuCount: int; MemoryGiB: int }` — reads better and stops being portable the first time a backend expresses accelerators differently, or bills by node class, or has no notion of memory limits at all.

So hints are **advisory and stringly-typed**, and the contract sits on the behaviour rather than the shape: a backend that does not understand a hint **ignores** it; a backend that understands a hint but cannot honour it **refuses** with a terminal error rather than silently downgrading. Silent downgrade is the failure mode worth designing against — a fit that quietly ran on CPU for nine hours is worse than one that refused in a second.

### `Running of progress: float option`

The option is load-bearing. A backend that cannot report progress returns `None`; it does not invent a number. This is portability rule 6 (precision at the lower bound) applied to progress rather than to time — a `float` field would force every backend to fabricate a figure, and a fabricated figure is indistinguishable from a real one at the call site. A dedicated job-progress sink is where a backend that *can* report progress surfaces it richly; this field is the coarse signal every backend can honour.

### Retriability is data

`ExternalComputeError.Retriable` is a field, not an exception type. The caller reads it to decide whether to re-submit, which means the decision serialises across the wire, persists in a job payload, and survives a process restart. Construct errors with `ExternalComputeError.retriable` / `ExternalComputeError.terminal`.

## The `NoExternalCompute` default

`ServerConfig.ExternalCompute` defaults to `NoExternalCompute`, and a bare deployment composes without noticing this substrate exists:

```fsharp skip=fragment
// Builds and runs unchanged — ExternalCompute = NoExternalCompute.
ServerApp.empty |> ServerApp.run
```

Under the default, `compose` registers `NoExternalComputeDispatcher` lazily. That is deliberately *not* the same as registering nothing: the seam always resolves, so a module that submits work gets a **typed refusal** rather than a DI resolution exception.

```fsharp
/// Broker a unit of work and return immediately with the handle to persist.
/// Note what this does NOT do: it does not loop on `Poll`.
let brokerForecastFit
    (dispatcher: IExternalComputeDispatcher)
    (logger: ILogger)
    (scopeId: string)
    : Async<ExternalHandle option> =
    async {
        let spec =
            ExternalWorkSpec.create "train-forecast" """{"series":"sales","horizon":12}"""
            |> ExternalWorkSpec.withHint "gpu" "1"
            |> ExternalWorkSpec.withHint "memory" "16Gi"
            |> ExternalWorkSpec.withTimeout (TimeSpan.FromMinutes 90.0)
            |> ExternalWorkSpec.withIdempotency "sales-h12-v3"

        match! dispatcher.Submit(scopeId, spec) with
        | Ok handle -> return Some handle
        | Error e ->
            // On a bare deployment this is ExternalComputeError.notConfigured
            // — terminal, because no amount of retrying composes a backend.
            logger.Warn(ExternalComputeError.describe e)
            return None
    }
```

The default costs nothing (GP 13): no background service, no connection, no vendor dependency, and no allocation until the first resolve. `Submit` returns `Error ExternalComputeError.notConfigured`, `Poll` reports the same refusal as a terminal `Failed`, and `Cancel` is a no-op that honours the idempotent-cancel contract.

Selecting a backend is one config line plus the companion's own registration:

```fsharp
let config = {
    ServerConfig.defaults with
        ExternalCompute = CustomExternalCompute
}
```

`CustomExternalCompute` makes `compose` register **nothing**, leaving the deployment's own `IExternalComputeDispatcher` singleton in DI — the same shape as `CustomTelemetrySink` and `CustomDatasetStore`. Backends live as companions under `src/ExternalCompute/`, so the backend's transport or SDK never reaches `ToolUp.Platform.*` (GP 1).

## The isolated execution profile

Every `ExternalWorkSpec` carries an `ExecutionProfile`:

```fsharp skip=signature
type ExecutionProfile =
    | Standard    // the default — this seam exactly as it was before the profile existed
    | Isolated    // a clean-room-grade worker: the backend must declare it can honour this
```

`ExternalWorkSpec.create` sets `Standard`, so an existing deployment is unchanged (GP 11): the same spec is built, the same dispatcher answers it, and no posture is checked. `Isolated` is for the case the profile exists to serve — computing **over data a clean-room gate protects** (split learning, private cohort modelling), where "run this somewhere else" is only safe if the somewhere else cannot leak it.

It is a field on the spec rather than an argument beside it because the requirement has to survive being persisted, re-read after a restart, and handed to a backend by a process that is not the one that authored it. A profile passed alongside would be a promise only the submitting call frame could keep.

### The isolation posture contract

`Isolated` is a **requirement on the backend**, not a hint it may ignore. A backend states what it guarantees by implementing `IIsolatedComputeBackend` alongside `IExternalComputeDispatcher`:

```fsharp skip=signature
type IIsolatedComputeBackend =
    abstract IsolationPosture: IsolationPosture
```

`IsolationPosture` has three clauses, and a backend must assert **all three** to be handed `Isolated` work:

| Clause | What the backend must guarantee |
|---|---|
| `NoEgress` | The worker reaches no network destination of its own choosing — not a package index, not a metrics endpoint, not an object store it was not handed. Only the completion callback leaves. |
| `InputsRestrictedToDeclaredRefs` | The worker sees the payload it was given and nothing else: no ambient credential, no mounted host path, no sibling job's scratch space. |
| `EphemeralWorkspace` | Storage is created with the worker and destroyed with it, so an output that was withheld does not survive the refusal on a disk somebody can later read. |

`Enforcement` names the concrete mechanism as free text — a network policy, a sandbox profile, a VM boundary. It is recorded and echoed for audit, never parsed, for the same portability reason `ResourceHints` is a string map: a typed shape would encode one scheduler's vocabulary.

Two of three is not a weaker clean room, so a partial posture is refused rather than downgraded. A dispatcher that does **not** implement `IIsolatedComputeBackend` reads as `IsolationPosture.standardOnly` — claiming nothing — so forgetting to declare is never mistaken for declaring.

**What the substrate can and cannot check.** A posture is an assertion forge cannot verify from inside the process. What it *does* do is refuse to submit `Isolated` work to a backend that has not made the assertion at all, and route the output through the clean-room gate regardless of it. The posture narrows who may be asked; the gate decides what may be seen.

### The refusal happens before the submission

`ExecutionProfileGate.enforce` wraps a dispatcher so an `Isolated` spec it cannot honour is refused **before** `Submit` reaches the backend:

```fsharp skip=fragment
let dispatcher = ExecutionProfileGate.enforce myBackend

// Standard: unchanged, on any backend.
let! ok = dispatcher.Submit(scopeId, ExternalWorkSpec.create "render-scene" payload)

// Isolated on a backend that declared no posture: refused here. The
// payload never leaves the process, and the error is terminal — a
// backend does not become isolating by being asked twice.
let! refused = dispatcher.Submit(scopeId, ExternalWorkSpec.create "fit-model" payload |> ExternalWorkSpec.isolated)
```

The ordering is the point. A check performed after the backend accepted the work is a check on something that has already left: no subsequent refusal recalls a payload. `Poll` and `Cancel` pass straight through — they act on a handle the backend already minted — and the decorator re-declares the inner posture, so stacking it cannot silently downgrade a genuinely isolating backend.

Composing the decorator is opt-in: `ServerConfig.ExternalCompute` is untouched by the profile, so a deployment that composes nothing keeps Phase 318's registration byte-for-byte. Where the profile matters, wrap the companion at composition time — or use the gated-output pipeline below, which checks the posture itself.

### Gated output — `ToolUp.InterPlatform`

An `Isolated` worker's output is not a result the caller receives; it is a candidate the clean-room gate has not yet ruled on. `GatedComputeOutput` (in the `ToolUp.InterPlatform` companion, which is where the gate lives) is that hold:

```fsharp skip=fragment
// The only route into a held output. Refuses a Standard spec, and
// refuses a backend that declared no posture.
let held = GatedComputeOutput.hold (ExecutionProfileGate.postureOf dispatcher) spec handle workerOutput

// The only route to anything readable. Runs the composed clean-room gate.
let deps =
    GatedComputeDeps.create broker template
    |> GatedComputeDeps.withAudit auditSink

let! released = GatedComputeOutput.release deps held
```

`GatedComputeOutput` has a private representation and no accessor for the payload: `release` is the only function that can see the bytes, and it runs the gate first. A withheld release is a typed `ComputeReleaseRefusal` — every case of which is a diagnostic string or an `ExternalComputeError`, with no case that could carry a partial result.

The pipeline **dispatches through** the clean-room gate rather than re-implementing it, so it inherits every invariant the gate has: bilateral approval, the cumulative ε reservation, the surface check, the checkability check, the release post-condition, and calibrated noise. Two consequences follow:

- **The work kind is the gated method.** The template's `AllowedMethods` is the set of `ExternalWorkSpec.Kind` values whose outputs it releases, so an output from a kind the template never authorised is withheld even though the work ran. Running is not releasing.
- **An uncheckable output is withheld, not passed through.** A worker that answered with rows, a scalar, or a record of some other shape produced something the floor cannot be evaluated against. Row-level worker output does not bypass the gate; it fails it.

### Backend implementation notes

A backend companion honouring `Isolated` implements the three clauses with whatever its platform provides — a container backend with a deny-all network policy plus an ephemeral volume, a batch service with a locked-down task role, a VM-isolated worker — and reports the mechanism in `Enforcement`. The declaration is constant for the life of the composed dispatcher: it describes how the backend was configured, not the state of any one job.

## Relationship to `IJobScheduler`

Orthogonal, and designed to compose. The job scheduler owns **when** something runs on this deployment; the dispatcher owns **where** the heavy part runs.

Since Phase 319 the composition is a **first-class job outcome**: a handler submits and returns `JobResult.HandedOff handle`, and the scheduler owns everything after that — the waiting, the polling, the retry decision, the restart recovery. See [the hand-off state machine](#the-hand-off-state-machine) below; it is the shape to reach for.

A handler that owns its own polling is still expressible, and is the right shape when the waiting belongs to a domain record rather than to the job:

1. A scheduled or event-triggered job handler builds an `ExternalWorkSpec` and calls `Submit`.
2. It persists the returned `ExternalHandle` (a `Guid` + strings — any store will hold it) and **returns**. It does not loop on `Poll`, and it does not hold its scheduler slot for the duration of the external work.
3. A later poll — its own scheduled job — reads the handle, calls `Poll`, and advances or completes the domain record when the outcome is terminal.

Step 3 reads as a total match over the outcome, which is what makes the "poll again vs advance" decision explicit rather than incidental:

```fsharp
/// What the poll job does with each outcome. `ExternalOutcome.isTerminal`
/// answers the same question in one call when the branches do not differ.
let classify (outcome: ExternalOutcome) : string =
    match outcome with
    | ExternalOutcome.Pending -> "queued — poll again"
    | ExternalOutcome.Running None -> "running, progress unknown — poll again"
    | ExternalOutcome.Running(Some fraction) -> sprintf "running at %.0f%% — poll again" (fraction * 100.0)
    | ExternalOutcome.Succeeded resultRef -> sprintf "done; result at %s" resultRef
    | ExternalOutcome.Failed e when e.Retriable -> sprintf "transient: %s — re-submit" e.Message
    | ExternalOutcome.Failed e -> sprintf "terminal: %s — do not re-submit" e.Message
    | ExternalOutcome.Cancelled -> "cancelled"
```

Step 2 is the part worth being deliberate about. A handler that submits and then polls in a loop has moved the blocking from the compute to the scheduler slot, which is the problem this seam exists to remove.

Distinct from **`IContainerScheduler`** (the Layer 3 deploy plane), which launches a container and owns its lifecycle — you asked for a process and you get one. The dispatcher hands work to a backend that owns its own execution and gives you a reference. A container backend is a perfectly reasonable *implementation* of a dispatcher; it is not the same abstraction.

Distinct from **`IModelFitProvider`**, which executes a fit in-process through a provider companion and returns a `FitOutcome` synchronously-shaped (`Async<FitOutcome>`, one call). Where the fit is too heavy for the serving process, a provider can be written over a dispatcher — that is the intended layering, not a conflict.

## The hand-off state machine

A handler may return **`JobResult.HandedOff handle`** instead of a result: "I did not do the work, I arranged for it to be done elsewhere, here is the receipt." The scheduler records the run as `AwaitingExternal` with the handle persisted, **releases its dispatch slot immediately**, and reconciles the handle on subsequent ticks until the backend reports a terminal outcome.

```fsharp skip=fragment
// The whole handler. It submits and returns — no loop, no sleep, no slot held.
let trainHandler (dispatcher: IExternalComputeDispatcher) =
    { new IJobHandler with
        member _.Execute ctx = async {
            let spec =
                ExternalWorkSpec.create "train-forecast" ctx.Payload
                |> ExternalWorkSpec.withHint "gpu" "1"
                // The key the backend dedups on — see "Submission idempotency".
                |> ExternalWorkSpec.withIdempotency $"fit-{ctx.JobId}-{ctx.Attempt}"

            match! dispatcher.Submit(ctx.ScopeId, spec) with
            | Ok handle -> return HandedOff handle
            | Error e when e.Retriable -> return TransientFailure e.Message
            | Error e -> return PermanentFailure e.Message
        }
    }
```

Note what the handler does **not** do: it does not persist the handle itself, poll it, or arrange a second job. Submitting failed? That is an ordinary `TransientFailure` / `PermanentFailure` and the existing `RetryPolicy` covers it. Submitting succeeded? Hand back the handle and stop.

### The states

```
                  handler returns HandedOff
   Running ─────────────────────────────────► AwaitingExternal
                                                    │
                                        Poll on each scheduler tick
                                                    │
        ┌──────────────┬──────────────┬─────────────┴──────────────┐
        │              │              │                            │
  Pending/Running   Succeeded    Failed (retriable)         Failed (terminal)
        │              │              │                            │
   stay awaiting    Succeeded    attempts left?              DeadLettered
   (no write)      JobCompleted   ├─ yes → Failed, then                  Cancelled
                                  │        re-dispatch at attempt+1          │
                                  └─ no  → DeadLettered            ExternallyCancelled
```

| `ExternalOutcome` | Run status | Lifecycle event | Notification |
|---|---|---|---|
| `Pending` / `Running _` | stays `AwaitingExternal` | none | none |
| `Succeeded resultRef` | `Succeeded` | `JobCompleted` + `JobExternalReconciled` | none |
| `Failed` retriable, attempts left | `Failed`, then a fresh attempt | `JobFailed` + `JobExternalReconciled` | none |
| `Failed` retriable, budget spent | `DeadLettered` | `JobDeadLettered` + `JobExternalReconciled` | `SystemMessage` at `Warning` |
| `Failed` terminal | `DeadLettered` (remaining attempts **skipped**) | `JobDeadLettered` + `JobExternalReconciled` | `SystemMessage` at `Warning` |
| `Cancelled` | `ExternallyCancelled` | `JobExternalReconciled` | none |

**The terminal events are the ordinary ones.** A run that completed on a GPU box emits exactly the `JobCompleted` payload an in-process run emits — same fields, same shape — so nothing downstream needs to learn a second vocabulary to count a completion or alert on a dead-letter. The external-only detail (backend, handle, `NativeRef`, the result reference, how long the run waited) rides on an additional `JobExternalReconciled` event emitted alongside it. `JobExternalHandedOff` marks entry into the awaiting state.

`ExternallyCancelled` is deliberately **not** a failure: no notification, and no `ConsecutiveFailures` bump. A pre-empted or operator-cancelled attempt says nothing about the job's health and must not push it toward an auto-disable threshold. It is also distinct from `JobStatus.Cancelled`, which cancels the *job definition* rather than one *attempt*.

### The slot is genuinely freed

The scheduler's occupancy for a job is its **per-`JobId` dispatch lease**, held across the whole retry loop. A handler that submitted and then polled in its own body held that lease for the entire remote duration — eight hours, for an eight-hour training run. `HandedOff` exits the loop, so the lease is released while the run is still `AwaitingExternal`, and other work for that job can proceed. The platform test pack asserts exactly this: it acquires the dispatch lock while a run is awaiting, which fails if anything is still holding it.

### Restart durability

The handle is persisted on the `JobRun` (`JobRun.ExternalHandle`), not held in memory. This is not a refinement — it is the difference between a feature and a leak. External work outlives the process by design, so a handle kept only in memory would strand the remote job on the first deploy: nothing left to ask the backend what became of it, and a run stuck awaiting forever.

After a restart the reconciliation pass finds the awaiting run through `IJobStore.AwaitingExternalRuns` and polls its handle, with **no re-submission** — the recovery path is "ask what happened", never "run it again", so a restart cannot launch a second GPU job. The handler need not even be registered on the new instance for the run to complete.

### Submission idempotency

Two independent defences, because neither covers the other's case:

1. **The scheduler will not re-enter a handler whose hand-off is outstanding.** Before dispatching, it checks for a run of that job in `AwaitingExternal` and skips if it finds one. This is what covers a cron job whose external work outlives its own interval, an admin re-trigger, and a restart's recovery re-queue — including for a handler that submits with no idempotency key at all.
2. **`ExternalWorkSpec.Idempotency`, honoured by the backend.** A dispatcher that has already accepted a key for a scope returns the *existing* handle rather than starting the work twice. This covers what the scheduler cannot see: a handler that submits more than once itself, and a handler re-entered after its run row was lost.

Set the key. The guard is not a substitute for it — the scheduler cannot see inside a handler, and the backend cannot see the scheduler's run rows.

### Enabling it

Reconciliation runs only when the deployment composed an actual backend — `ServerConfig.ExternalCompute = CustomExternalCompute` plus an `IExternalComputeDispatcher` singleton in DI. A deployment on the `NoExternalCompute` default pays nothing: the pass short-circuits before touching the store, so the tick gains no queries at all (GP 13), and no existing handler's behaviour changes (GP 11).

One wiring caveat worth knowing, because it fails quietly otherwise: compose finds the dispatcher by inspecting the service collection, so an **instance** registration is seen and a **factory** registration is not (a factory cannot be invoked before the provider is built without constructing a second instance). Register the instance:

```fsharp skip=fragment
services.AddSingleton<IExternalComputeDispatcher>(myDispatcher)
```

If compose cannot find it, it logs a warning naming both remedies rather than silently leaving reconciliation off — the alternative being a deployment that hands off successfully and then waits forever. The other remedy is to construct the scheduler directly with `JobScheduler.createWithExternalCompute`.

Two operational notes. Reconciliation is batched per scope (`AwaitingExternalRuns`' `limit`) so one saturated scope cannot starve another's reconciliation; a scope with more outstanding hand-offs than the batch size gets the remainder on the next tick, and nothing is dropped. And each run is reconciled under its job's dispatch lease, so a deployment that composed a store-backed [`IDistributedLock`](../platform/jobs.md) gets multi-instance double-poll protection for free.

### What a store implementation owes

`IJobStore` gained `AwaitingExternalRuns`, and the contract pack tests it. Two obligations:

- **Round-trip `JobRun.ExternalHandle`.** Every field — a converter that reconstructs the record with defaults passes an `isSome` check and fails a real one.
- **Answer from the awaiting set, and take runs OUT of it as they go terminal.** The removal half is the one that fails silently: a store that only ever adds looks correct until the scheduler is re-polling handles for work that finished days ago. Do not satisfy the query by scanning run history — run rows are unbounded, and this is on the tick path. Index the status transition, as the blob-backed default does with its `_awaiting-external` secondary index.

## Portability-rule conformance

`IExternalComputeDispatcher` satisfies all six [portability rules](portability-rules.md). The audit is executable — `IExternalComputeDispatcherContract.portabilityAudit` in the platform test pack asserts each rule against the shipped default, because a prose audit in a file header cannot fail.

| Rule | How the seam satisfies it |
|---|---|
| **1 — Identity by value** | `ExternalHandle` is a record of `Guid` + `string`s + `DateTime`, compared and hashed by value. No `IActorRef`, `IGrainReference`, live job object, or backend SDK client crosses the surface. The backend's own token rides as the opaque `NativeRef` string, so any node can poll a handle any other node minted. |
| **2 — Async at every boundary** | All three methods return `Async<_>`. No sync method, no `Tell`-shaped fire-and-forget signature, and therefore no carve-out of the kind `IMetricsSink` documents. |
| **3 — Retry + supervision as data** | `Timeout` and `Idempotency` are fields on the spec; failure is an `ExternalComputeError` record whose `Retriable` flag *is* the retry decision. No `OnFailure: exn -> unit` parameter, no supervision-strategy object, and a backend-reported failure is a `Result` / `ExternalOutcome` rather than an exception across the boundary. |
| **4 — Stateless between invocations** | A dispatcher receives the whole spec per `Submit` and the whole handle per `Poll` / `Cancel`, and caches nothing between calls — so a recycled worker or a re-activated grain answers identically to a warm one. |
| **5 — No cross-shard ordering** | Two handles are independent. Ordering is promised only within one handle's own state progression (`Pending` / `Running` → a terminal case), never across submissions. |
| **6 — Precision at the lower bound** | `Timeout` is advisory with the backend's own scheduling granularity as its floor — no implicit sub-second promise is encoded in the type. `Running of progress: float option` lets a backend that cannot report progress say so. |

No framework type appears on the seam: no `Microsoft.Extensions.*`, no `HttpContext`, no `CancellationToken`, no `Task`. The whole interface is expressible from `ToolUp.Platform` value types alone, and a reflection case in the contract pack holds it that way.

## See also

- [`jobs.md`](jobs.md) — the in-process job scheduler this seam extends beyond the process.
- [`portability-rules.md`](portability-rules.md) — the six rules in full.
- [`which-store-for-what.md`](which-store-for-what.md) — choosing among the substrate seams.
