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

The option is load-bearing. A backend that cannot report progress returns `None`; it does not invent a number. This is portability rule 6 (precision at the lower bound) applied to progress rather than to time — a `float` field would force every backend to fabricate a figure, and a fabricated figure is indistinguishable from a real one at the call site. [`IJobProgressSink`](#progress-checkpoints) is where a backend that *can* report progress surfaces it richly; this field is the coarse signal every backend can honour, and the reconciliation poll forwards it to the sink automatically.

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

## The completion callback

Polling is the universal fallback, and it stays the fallback. But it costs a tick interval of latency and a poll per outstanding handle, and a backend that can call a webhook can do better than being asked. `POST /_platform/external-compute/callback` is that path: the backend pushes its terminal outcome, and the run resolves immediately.

Nothing about correctness depends on it. A callback that never arrives, arrives twice, or arrives forged all end with the run resolved **exactly once** — the callback is a latency optimisation layered on a poll loop that was already sufficient.

### The contract

```
POST /_platform/external-compute/callback
Content-Type: application/json
X-ToolUp-External-Callback-Secret: <the per-handle secret>

{ "handleId": "3f2b...", "status": "succeeded", "resultRef": "s3://bucket/out.bin" }
```

| Field | Required | Meaning |
|---|---|---|
| `handleId` | always | `ExternalHandle.HandleId` of the finished work. The **only** routing input taken from the caller. |
| `status` | always | `succeeded`, `failed` or `cancelled`. Case- and whitespace-insensitive. |
| `resultRef` | for `succeeded` | Opaque backend reference to the result. The platform echoes it and never dereferences it. |
| `error` | for `failed` | Failure description. Becomes `ExternalComputeError.Message`. |
| `retriable` | optional | Read only for `failed`. **Absent means terminal** — a backend that does not say a failure is worth retrying is not asserting that it is, and defaulting the other way would re-submit external work on a backend's silence. |

Five flat primitives, deliberately, rather than a serialised `ExternalOutcome`: the caller is a GPU or batch service that is almost certainly not written in F#, and a DU with a nested error record is only serialisable through a converter set no third-party backend has. Same reasoning that put JSON-RPC 2.0 on the peer seam — an open contract at the boundary, typed values inside it.

**Only terminal statuses are accepted.** `pending` and `running` are refused by name. A callback exists to deliver an outcome; accepting a non-outcome would mean the ingress could be handed a status that resolves nothing while still consuming the handle's one-shot terminal claim.

### Responses

| Status | When | What the backend should do |
|---|---|---|
| `200` | Resolved, already resolved, or the run was no longer awaiting. The body's `resolution` field says which (`resolved` / `already-resolved` / `no-awaiting-run`). | Nothing. All three are terminal for the backend. |
| `403` | Any refusal — malformed body, missing or wrong secret, unknown handle, non-terminal status, scope mismatch, or (where signed outcomes are composed) a signature that did not verify. | Check the credential and the payload. The response deliberately does not say which. |
| `429` | Too many refused callbacks from this address recently. | Back off. |
| `503` | The deployment opted into external compute but composed no usable handle store. | Nothing; the run resolves by poll. |

**A duplicate is `200`, not `409`,** and that is a deliberate choice about failure modes rather than a shrug: idempotency is the guarantee this endpoint is built to provide, and a backend that retries on non-2xx would retry a correct duplicate forever. The body distinguishes the cases for a backend that cares.

### Where the secret comes from

The platform mints a fresh 256-bit secret per hand-off and stores **only its SHA-256**. The cleartext exists once, in the `ExternalCallbackCredential` handed to the backend:

```fsharp skip=fragment
type MyDispatcher() =
    interface IExternalComputeDispatcher with
        member _.Backend = "gpu-pool"
        member _.Submit(scopeId, spec) = submitToBackend scopeId spec
        member _.Poll(handle) = pollBackend handle
        member _.Cancel(handle) = cancelBackend handle

    // Opt in to the push path by ALSO implementing this.
    interface IExternalCallbackCapableBackend with
        member _.AcceptCallbackCredential(handle, credential) = async {
            // Make the credential reachable by whatever will POST the
            // callback. Do not log the secret.
            do! setWebhook handle.NativeRef credential.CallbackPath credential.Secret
        }
```

A second interface a backend *also* implements, exactly like `IIsolatedComputeBackend` above, and for the same reason: F# cannot author a default interface member, so a new member on the dispatcher seam would break every implementation that exists — including shipped consumer ones — for a capability most backends do not have. **A backend that does not implement it is never handed a secret and is reconciled by polling, exactly as before (GP 11).**

One ordering property, stated because it is real rather than hidden: the credential is keyed by `ExternalHandle.HandleId`, which does not exist until `Submit` has returned it, so the credential necessarily arrives *after* the backend accepted the work. A backend fast enough to finish before that call lands cannot call back — and that run resolves by poll on the next tick. The window is one blob write wide, and correctness never depends on the credential arriving; only latency does.

### Idempotency, and where the guarantee actually comes from

`IExternalHandleStore.MarkTerminal` is an **atomic compare-and-set**: the first caller to claim a handle gets `true`, every other caller gets `false`. Both the callback ingress and the reconciliation poll go through it, in one shared code path — so whichever arrives first resolves the run and the other is a no-op.

It matters to be precise about which failure this closes, because a single-instance deployment was already safe:

- **Within one process**, the per-`JobId` dispatch lease plus the "is the run still `AwaitingExternal`" re-verify already serialise the callback against the poll. Measured, not assumed: an *ungated* single-process race produced zero double resolutions across 40 rounds.
- **Across replicas** there is no shared lease and no shared awaiting view. Both callers pass their own re-verify, and the CAS is the only thing between them. The platform test pack models exactly this — two schedulers over one job store with **separate** locks — and it *places* the interleave rather than racing for it: the second replica's poll is held inside `Poll`, past its own awaiting re-verify, while the callback resolves the run underneath it. Deterministic in both directions, so the paired control (same construction, no handle store) does not merely show a double resolution is possible — it shows one happens, every run.

That is why the store demands `IConditionalBlobStorage` and **refuses to construct without it**. A download-modify-upload fallback races precisely where the callback and the poll meet, and a gate that is racy under load is worse than no gate because it reads as defended. For the same reason, compose does **not** quietly fall back to the in-memory store when the blob backend lacks conditional writes: a per-replica gate on a multi-replica deployment lets both callers win. It composes no store at all, logs the shortfall, and leaves the poll loop — correct, just slower.

### Observability

- **Every resolution is audited** (`ExternalCallbackResolved`), including the idempotent duplicate. "This handle was resolved twice and the second was a no-op" is exactly the fact an incident reconstruction needs, and a trail that recorded only the first could not distinguish a well-behaved retrying backend from a forged replay.
- **Every refusal is audited** (`ExternalCallbackRejected`) *and* emits a **rate-limited warning** naming the internal reason. A forged callback is something to alert on, and a bare 403 in an access log is not an alert. The warning is throttled per source address so a scripted probe cannot turn the log into the denial-of-service; the audit event is **not** throttled, because the trail has to stay complete precisely when the log has gone quiet.

Neither the secret nor the work payload appears in either event.

### Enabling it

The route is mounted only when `ServerConfig.ExternalCompute = CustomExternalCompute`. A deployment on the `NoExternalCompute` default has no such path — the Giraffe terminal middleware answers a clean 404, and there is no hosted service, no middleware and no allocation (GP 13).

**The path is `/_platform/...` and not `/api/...`, deliberately — do not move it.** Both the surface-enforcement middleware and the CSRF middleware are scoped to `/api/*`, so this route is outside the session-auth and double-submit-token envelope. That is what lets a GPU service POST to it with nothing but its per-handle secret; relocating it under `/api/` would demand an XSRF token from a caller that has no browser, no cookie jar and no way to obtain one, and every backend would start failing with `csrf_validation_failed`. The endpoint pays for that exemption itself: it authenticates every request against the handle's own secret hash, refuses uniformly, throttles per source address, and audits both outcomes. Same posture as the anonymous `/_platform/signing-key/` route.

Handle records live under `_platform/external-compute/handles/{scopeId}/{handleId}.json`, **scope-partitioned** (GP 4), with a small `external-compute/handle-index/{handleId}` pointer so a callback carrying only a handle id resolves in one read rather than a listing. A record whose own `ScopeId` disagrees with the partition it was read from is refused rather than followed, so a mis-pointed index produces "unknown handle" and never a cross-scope read.

## Signed worker outcomes

The per-handle secret answers **"may this caller resolve this handle"**. It cannot answer **"which worker produced this result"**, because the secret is handed to a *backend* and every node behind that backend presents the same one. A compromised relay, a mis-routed queue consumer, and a pool node re-using a sibling's credential all satisfy the callback contract above and are indistinguishable from the honest case.

Signed outcomes close that gap. A worker signs `(handleId, artifactHash, diagnosticsHash, timestamp)` with a **registered per-worker key**, and the ingress verifies the signature — and the artifact hash — before the outcome is accepted. Two properties, and the second is the one that makes the first worth having:

- **Attribution** — the verified worker identity is recorded on the outcome.
- **Integrity** — `artifactHash` is the digest of the *canonical descriptor of the outcome the callback delivers*, so a relay that replays a genuine envelope against a substituted `resultRef` produces a body that no longer hashes to the signed digest. Without that binding the signature would attest only that some worker said *something* about this handle.

Entirely opt-in. A deployment that composes no `SignedOutcomeVerification` reads no header, resolves no registry, and performs no crypto — the request, the parse, the record and the resolution are byte-for-byte what the section above describes (GP 11 / GP 13).

### The envelope

One header, not six body fields:

```
POST /_platform/external-compute/callback
X-ToolUp-External-Callback-Secret: <the per-handle secret>
X-ToolUp-Worker-Signature: v=1,worker=gpu-node-7,key=k-2026-08,t=2026-08-04T10:00:00.0000000Z,
                           artifact=<64 lowercase hex>,diagnostics=<64 lowercase hex>,sig=<base64url>

{ "handleId": "3f2b...", "status": "succeeded", "resultRef": "s3://bucket/out.bin" }
```

| Parameter | Meaning |
|---|---|
| `v` | Envelope version. `1`. |
| `worker` | Worker identity, resolved against the key registry. Charset `A-Z a-z 0-9 . _ : -`, ≤ 128 chars. |
| `key` | Which of that worker's registered keys signed — so rotation needs no flag day. |
| `t` | The signing timestamp, **as signed**. Verified for freshness (±5 minutes by default). |
| `artifact` | Lowercase hex SHA-256 over the outcome descriptor. The binding to the body. |
| `diagnostics` | Lowercase hex SHA-256 over the worker's diagnostics bundle. A **commitment**, recorded and never dereferenced. |
| `sig` | Unpadded base64url signature over the canonical signing payload. |

**A header rather than body fields**, for three reasons in increasing order of importance: a credential is not an outcome (the per-handle secret is already a header); six additive record fields would retype the wire contract for every consumer that builds one; and, decisively, it makes the GP 11 claim *structural* — an unsigned deployment sends no header and the ingress reads none, so there is no "the field was `None`" path to reason about.

Unknown parameters are **ignored**, so the envelope can gain a TEE-attestation parameter later without a version bump for every existing worker. The trade is explicit: a parameter an older server does not know is a parameter it does not verify, which is why the *signed tuple* is closed and versioned even though the parameter list is open. Duplicate parameters are refused rather than last-wins — a duplicate is how a parser-differential attack gets two sides to read different values.

### What gets signed

```
toolup.signed-outcome.v1
<handleId, lowercase D-format>
<artifactHash>
<diagnosticsHash>
<t, byte-for-byte as it appears in the header>
```

Newline-separated; the domain tag prevents a signature minted for one ToolUp protocol being replayed as another; the handle id means a genuine envelope cannot be moved between handles. `t` is never reformatted — a normalising round-trip through `DateTimeOffset` would change the bytes and break every signature it touched.

The artifact descriptor is one line per terminal outcome, prefixed by the outcome label so no two cases can collide (`Succeeded ""` and `Cancelled` must not be interconvertible under a valid signature):

| Outcome | Descriptor |
|---|---|
| `Succeeded resultRef` | `succeeded:<resultRef>` |
| `Failed { Message; Retriable }` | `failed:<true\|false>:<message>` |
| `Cancelled` | `cancelled:` |

A .NET worker should call `SignedOutcomeVerifier.artifactHash` and `WorkerOutcomeSignature.signingPayload` / `render` rather than re-derive any of this — two implementations of one digest is how a signature scheme stops interoperating.

### Algorithms — and an honest limitation

| Algorithm | Verified by | Notes |
|---|---|---|
| `es256` | the in-tree verifier | ECDSA P-256 / SHA-256, **IEEE P1363 (`r \|\| s`) encoding**, BCL only. |
| `ed25519` | a composed `WorkerSignatureVerifier` | Registrable; the in-tree verifier refuses it **by name**. |

**.NET 10's BCL ships no Ed25519 primitive** — `System.Security.Cryptography` 10.0.0.0 exposes four `ECDsa*` types and zero `Ed*` — and pulling BouncyCastle or NSec into `ToolUp.Platform.Server` would put a third-party crypto stack in the SDK core (GP 1) for a capability most deployments never compose. So `es256` is the default, and `ed25519` is reachable through a structural function seam, the same GP 1 decoupling the detached-JWS verifier uses:

```fsharp skip=fragment
let verify: WorkerSignatureVerifier =
    fun key payload signature ->
        match key.Algorithm with
        | WorkerKeyAlgorithm.Ed25519 -> myEd25519Verify key.PublicKey payload signature
        | _ -> WorkerSignature.bclVerifier key payload signature
```

An algorithm that silently never verified would be strictly worse than one that says why, which is why the refusal names the missing dependency rather than reporting a key problem.

The `es256` encoding is stated explicitly to `VerifyData` rather than left to an overload default: OpenSSL emits ASN.1 DER by default, JWS `ES256` uses P1363, and an implicit choice here presents as "signatures from my worker never verify" with nothing naming the encoding. A DER-length signature is refused with the encoding named.

### The worker key registry

`IWorkerKeyRegistry` maps worker identity to public key. **Only public keys live here**, which is why there is no `ISecretStore` on the surface: the private half never leaves the worker, and a registry that accepted private material could forge the attribution it exists to prove. Custody here is *integrity* custody, not confidentiality custody.

Two enrolment paths, one of which cannot self-authorise:

| Path | Lands | Usable? |
|---|---|---|
| `Register` — the operator states the key out of band | `Approved` | yes |
| `EnrolOnFirstContact` — a worker presents a key the registry has never seen | `PendingApproval` | **no** |

First contact **verifies nothing**: an unapproved key is exactly as useless as an unregistered one. What it buys is that an operator approves a key they can *see* rather than transcribing one — the difference between a five-second admin action and a manual key-distribution project nobody completes. A repeat first contact can neither demote an approved key nor overwrite its material.

Three laws worth stating because they are what make the path safe:

- **Revocation is sticky.** A revoked `(worker, key)` pair can never return to `Approved` — not by re-enrolment, not by re-registration, not by a second approval. A restorable revocation is a speed bump: the compromise that caused it is precisely a capability to re-present that key. Rotation is a **new key id**, which is why the envelope names the key and not only the worker.
- **Material is never overwritten.** Presenting different material under a known key id is a `KeyConflict` — the key-substitution shape — and the stored key is left untouched.
- **Key material is validated at registration, not at first callback.** A malformed key discovered when a signature arrives is discovered inside an unauthenticated request path, hours after the mistake, and presents as "this worker's signatures are all rejected". Validated at registration it presents as "that is not a P-256 SPKI", to the operator who just typed it. The curve is checked too — a P-384 key is valid SPKI, valid ECDSA, and still wrong for `es256`.

Two implementations, and the difference matters for revocation specifically: `InMemoryWorkerKeyRegistry` (`IsDistributed = false`) loses registrations on restart, which fails *closed*; but it also holds them **per replica**, which fails *open* in one direction — a revocation applied on replica A leaves replica B still accepting the revoked key. Multi-replica deployments want `BlobWorkerKeyRegistry` (`IsDistributed = true`), which stores one blob per key under `_platform/external-compute/worker-keys/{workerId}/{keyId}.json` and **refuses to construct without `IConditionalBlobStorage`**: every mutation is a read-decide-write whose decision is a security decision, and a download-modify-upload fallback lets a concurrent enrolment overwrite a revocation.

The registry is **deployment-scoped, not tenant-scoped**, and GP 4 is unaffected. A worker is a piece of the deployment's compute fleet, not a tenant's asset — the same GPU node legitimately serves many scopes — so keying by scope would either duplicate every key per tenant or invent a tenancy the fleet does not have. The tenant boundary stays exactly where the callback ingress put it: the *handle record's* `ScopeId`, read from platform state and never from the request. This registry answers "who signed", never "what may they touch".

### Policy

```fsharp skip=fragment
services.AddSingleton(
    SignedOutcomeVerification.create RequireForIsolatingBackends myWorkerKeyRegistry)
```

| Policy | Unsigned outcome | Presented signature |
|---|---|---|
| `NoSignedOutcomes` (the default, and what an **absent** registration means) | accepted | **not examined at all** |
| `VerifyWhenPresented` | accepted | must verify |
| `RequireForIsolatingBackends` | refused for a backend declaring the isolation posture; accepted otherwise | must verify |
| `RequireForAllBackends` | refused | must verify |

Four modes rather than a `bool` because the middle pair is how the feature is rolled out: turn on `VerifyWhenPresented`, watch the audit trail until every worker signs, then tighten. A binary switch would make adoption a flag day.

**`RequireForIsolatingBackends` is the `ExecutionProfile.Isolated` clause, keyed to the BACKEND rather than to the individual spec — a deliberate reading, for two reasons.** The first is what the ingress actually has: neither `ExternalHandle` nor the stored handle record carries the profile, and `JobResult.HandedOff` carries only the handle, so recovering the submitted profile at callback time would mean a field on a persisted record, a parameter on `IExternalHandleStore.Register`, and a payload on a DU case — all breaking, for one boolean. The second is that the posture-keyed form is *stronger*: it cannot be dodged by mislabelling a spec `Standard`, because it covers every outcome a clean-room-capable backend produces. A backend that declares nothing reads as non-isolating, so forgetting to declare is never mistaken for declaring.

### Where the gate sits, and what a refusal costs

The signature gate runs **after** the per-handle secret check. Deliberately: it resolves a registry and does curve arithmetic, and doing that for an unauthenticated caller would hand a prober a free oracle on the key registry. Transport auth first, attribution second — and a caller with a wrong secret gets the `secret-mismatch` refusal, never a signature one.

Within the gate the **artifact-hash match precedes the signature check**. Both must pass, so the order is not a correctness question; it is a question of what a rejection *means*. `signature-artifact-mismatch` says "a possibly-genuine signature arrived over a substituted result"; `signature-invalid` says "this is not a signature by that key". Checking the hash first means a relay-substitution incident is reported as itself rather than reading in the audit trail as a key problem.

A refusal **never falls through to acceptance**, and it costs latency rather than the job: the run stays `AwaitingExternal` and the reconciliation poll resolves it from the backend's own report.

### Observability

- **Attribution rides the resolution event.** `ExternalCallbackResolved` gained `WorkerId` / `WorkerKeyId` / `SignatureAlgorithm` / `ArtifactHash`, all `string option`. They are `None` together or `Some` together — there is no path that records a worker id without having verified a signature for it, because a presented-but-unverified signature refuses the callback rather than reaching this event. The audit trail is the queryable attribution surface, since `IExternalCompletionSink` cannot gain a field without breaking every implementation of it. The verified attribution is also stamped into `HttpContext.Items` under `SignedOutcomeVerifier.ContextItemKey`, and the `200` body echoes `workerId` so the backend can confirm the platform attributed the outcome to the node it believes produced it.
- **Every signature refusal is audited** under the `signature-*` reason family on `ExternalCallbackRejected`, and logged with the full typed cause. That warning is deliberately *not* rate-limited by the forged-callback throttle: reaching this gate means the caller already presented a valid per-handle secret, so it is not the scripted-prober traffic the throttle bounds — it is the deployment's own worker misbehaving, or a credential-holding party signing wrongly, and both are low-volume and worth one line each. The refusal still counts toward the throttle, so a flood is still bounded.
- **`signature-artifact-mismatch` is the one to alert on hardest.** Every other signature refusal is a key or a clock problem. That one is a substituting relay.

Neither the key material nor the work payload appears in any event.

## Memoizing a repeated submission

`MemoizedComputeDispatcher` is an opt-in decorator over any
`IExternalComputeDispatcher`: an identical idempotent submission returns the
cached terminal outcome instead of re-running the work. Where
`ExternalWorkSpec.Idempotency` is a *backend* promise about a window the
backend chooses, this is the platform-side one — and it is what makes a
re-submission cost nothing rather than merely produce one handle.

```fsharp skip=fragment
// Outermost. A hit returns before anything below it is consulted.
let dispatcher = MemoizedComputeDispatcher(backend, blobs = blobStorage) :> IExternalComputeDispatcher
services.AddSingleton<IExternalComputeDispatcher>(dispatcher)
```

Four things worth knowing before composing it:

- **Only a spec carrying an `Idempotency` key is memoizable.** Its presence
  is the caller's explicit assertion that re-submitting *this* work is the
  same request rather than a second one, so work without it is passed
  straight through and never cached. Forge cannot tell "render that scene
  again" from "charge that card again", and a cache that guessed would
  silently deduplicate side effects.
- **Only `Succeeded` caches.** A `Failed` outcome is frequently transient —
  `ExternalComputeError.Retriable` says so as data — and caching it would
  turn a blip into a TTL-long outage for that spec. A `Cancelled` outcome
  is a decision about one submission, not a fact about the work.
- **Two windows, two mechanisms.** The cache covers a duplicate arriving
  *after* the first submission finished. A duplicate arriving *while* it is
  still running cannot be served from the cache at all (nothing has
  succeeded yet), so it is instead joined to the first caller's in-flight
  `Submit` and handed the same handle: N concurrent duplicates produce one
  dispatch and one execution.
- **Compose it outermost.** Above a budget or quota decorator, a hit spends
  nothing because the decorator below never sees a submission. Below one,
  every hit is charged — which is the feature not working.

Entries are keyed on `(scopeId, Kind, SHA-256(payload), Idempotency)` plus
the execution profile, and are scope-partitioned in both tiers: the
in-memory key carries the scope and the blob path is
`_platform/compute-memo/{scopeId}/{digest}.json`, so a lookup for one scope
never constructs a path under another (GP 4). The stored envelope repeats
the whole key and a read whose envelope does not match is treated as a
miss, so a mis-derived path degrades to a re-dispatch rather than to one
tenant reading another's result. Supplying `blobs` makes hits survive a
restart; omitting it keeps the memo in-process. The in-memory indexes are
capped and drain FIFO in bounded batches, reporting cap pressure through
`Stats.OverCapRecoveries` rather than clearing themselves — the same
discipline the in-process idempotency store follows, and for the same
reason: a silent mass wipe under pressure turns every discarded entry back
into work already paid for.

## Progress checkpoints

A terminal outcome is the only thing the scheduler surfaced before this: a multi-hour run was an opaque `Running` row until it finished, so an operator staring at it could not tell a healthy job from a hung one. `IJobProgressSink` is the checkpoint API that closes the gap, and it serves in-process handlers and externally-run work through the same type.

```fsharp skip=signature
ProgressCheckpoint = {
    Fraction: float option   // [0, 1], or None when the stage cannot estimate
    Message:  string         // "materialising embeddings"
    Stage:    string option  // "epoch 4/10"
    At:       DateTime       // when the REPORTER observed it, not when the sink received it
    Durable:  bool           // also persist to IEventStore, and never shed
}
```

### Two legs, two durability postures

| Leg | Destination | Which checkpoints | Why |
|---|---|---|---|
| **transient** | `INotificationChannel`, under the reserved `CustomNotification` key `_platform.jobs.progress` | every checkpoint, scope-gated, **coalesced** under load | drives a live progress bar; losing one intermediate frame is imperceptible |
| **durable** | `IEventStore`, `SourceModule = _platform.jobs`, `EventType = JobProgressCheckpoint` | `Durable = true` **and every terminal checkpoint** | drives the audit timeline that answers "how long did epoch 4 take"; each write is a blob, so it is opt-in *per checkpoint* rather than per deployment |

Progress events share `_platform.jobs` with the five [lifecycle events](jobs.md#lifecycle-events), so one `ReadBySource` returns a run's whole story rather than two streams a reader has to join.

A **reserved `CustomNotification` key** rather than a new `Notification` case, deliberately: a new DU case would force every `match` over `Notification` in every consumer to grow an arm — a source-breaking change for a purely additive feature (GP 11). The literal `_platform.jobs.progress` *is* the wire contract, and the `_platform.` prefix is what keeps it from colliding with a module-owned key.

### Coalescing — and the one checkpoint that is never shed

A chatty handler can emit thousands of checkpoints a second, and publishing each floods every SSE connection in the scope. The transient leg therefore rate-limits per job (`ProgressCoalescePolicy.MinInterval`, default one second): a checkpoint arriving inside the window is **superseded** by the next one that clears it, so what a subscriber sees is the latest value rather than a replayed backlog.

Three classes are **never** shed, and `ProgressCoalescer.shouldPublish` checks them *before* it checks the interval:

1. a **terminal** checkpoint (`Fraction >= 1.0`);
2. a **`Durable = true`** checkpoint — it is already paying for a blob write, and suppressing its live twin would make the audit timeline and the progress bar disagree;
3. the **first** checkpoint for a job, so a UI gets an immediate frame instead of waiting out one window.

That ordering is the contract, not an implementation detail. A progress bar that drops intermediate frames is imperceptible; one that drops the *final* frame sits at 94% forever on a job that succeeded, and no later checkpoint arrives to correct it. Because the asymmetry is what matters, the rule lives in a pure function in the Core tier with its own tests — including two mutation controls that assert a terminal and a durable checkpoint publish at *zero* elapsed time inside an hour-long window, which is precisely the input that distinguishes the correct ordering from the naive one. Reversing the branches turns those cases red.

The durable leg does not shed at all. `Durable = true` is the caller declaring a checkpoint worth keeping; a rate limiter that silently dropped some of them would turn the timeline into a sample while still reading as a record.

### Reporting from a handler

A handler reports through `ctx.Progress` and never resolves the sink:

```fsharp skip=fragment
// inside IJobHandler.Execute
do! ctx.Progress.Report(ProgressCheckpoint.create (Some 0.37) "materialising embeddings")

// a stage boundary worth keeping after the run ends
do!
    ctx.Progress.Report(
        ProgressCheckpoint.create (Some 0.4) "epoch 4/10"
        |> ProgressCheckpoint.withStage "epoch"
        |> ProgressCheckpoint.durable
    )
```

The reporter is **bound to the running job's id and scope**, so a handler cannot report progress into another tenant's scope even by accident (GP 4) — the seam offers no way to name a different job. It rides the async chain (the same ambient mechanism the correlation-id scope uses) rather than sitting as a `JobContext` field: `JobContext` is a public record with no field defaults, so a new field would source-break every handler test harness that constructs one, which is exactly what GP 11 exists to prevent. The scope is established immediately before `Execute` and torn down immediately after, including when the handler throws, so nothing survives an attempt and portability rule 4 still holds.

Reporting **never throws and never needs a guard**. A progress report is observability about the work, not part of it, so a channel that is down or an event store that refuses a write is logged at `Warn` and swallowed. The two legs are independent: one failing does not take the other with it.

### Externally-run jobs get progress for free

When the reconciliation poll sees `ExternalOutcome.Running (Some p)` it emits a checkpoint with `Fraction = p`, labelled `Stage = "external"` and naming the backend — so a job handed off to a GPU service reports progress with **no handler code at all**. Those checkpoints are transient: the poll produces one per tick for the whole remote duration, and the terminal outcome is already recorded by the ordinary resolution path. A backend answering `Running None` yields **no** checkpoint fraction — "running, cannot estimate" is not turned into a number.

### Reading the latest checkpoint

`IJobProgressSink.Latest jobId` returns the most recent checkpoint **as reported**, not as published — so a checkpoint shed by the rate limiter still updates it. `None` when a job has reported nothing, or when an implementation keeps no cache.

This is the read a typed long-running-operation handle needs to populate a `Running of progress` arm, and the read an SSE progress stream needs per frame. Both bridges are **deliberately not wired here**: the typed-handle substrate mints its own job identity independent of `IJobScheduler`'s `JobId`, so nothing can currently map one to the other, and inventing that mapping belongs to the phase that fuses the two identity spaces rather than to this one. `Latest` is the whole platform-side surface those bridges need; when the identity fusion lands, each is a few lines against it.

### Enabling it

```fsharp
let config = {
    ServerConfig.defaults with
        JobScheduler = InProcessJobScheduler
        JobProgress = EnabledJobProgress
}
```

Default is `NoJobProgress`. On the default, `ctx.Progress` is a no-op reporter, no `IJobProgressSink` is registered in DI, and not one notification or event is generated — a handler can report unconditionally and an opted-out deployment pays one interface dispatch per call (GP 13). Nothing is registered under `NoJobProgress` on purpose: a resolvable no-op sink would invite consumer code to resolve and report against a deployment that asked for none.

`compose` takes the sink **from the scheduler** rather than building a second one. That matters: the fan-out sink holds the per-job rate-limit state, so two instances would each keep their own window and a chatty handler would publish at twice the configured rate — the flood the coalescer exists to prevent, reintroduced by duplication.

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

## Conformance — the stability gate for a companion

**A dispatcher companion is not stable until it passes the conformance pack unmodified.** Same discipline the `IJobScheduler` / `IJobStore` / `IModuleQueryBus` packs impose: the pack is the executable definition of what a dispatcher must do, independent of *how*, and "unmodified" is the whole of it. A companion that needs a case relaxed, skipped, or reworded has found either a defect in itself or a defect in the contract — and the second possibility is the reason the pack exists. A case that passes for an HTTP backend but is *unexpressible* for a declarative-watch one means the seam has acquired an HTTP-ism, and the fix belongs in the interface, not in the companion.

Two packs, in `ToolUp.Platform.Tests`:

| Pack | Binds | Covers |
|---|---|---|
| `IExternalComputeDispatcherContract.contractFor` | any `IExternalComputeDispatcher` | submit → poll → terminal, a resolvable result ref, idempotent re-poll, error classification in both directions, `ResourceHints` acceptance, independence of un-keyed submissions, idempotent resubmit, cancel (in-flight, already-terminal, repeated), scope isolation |
| `IExternalHandleStoreContract.contractFor` | any `IExternalHandleStore` | register / resolve round-trip (hash, never cleartext), non-destructive `Resolve`, exactly-once `MarkTerminal`, 32-way concurrent single-winner, the callback-vs-poll race, scope partitioning, `Register`'s overwrite clause |

### Binding it

`contractFor` takes a name, a declaration of what the backend honours, and a factory. The factory is called **per law**, so one case's units never leak into another's — which matters when the backend is a shared stub server or a real cluster.

```fsharp skip=fragment
open ToolUp.Platform.Tests.Contracts

let myBackendTests =
    IExternalComputeDispatcherContract.contractFor
        "MyComputeBackend"
        { ExternalComputeConformance.strict with
            SettleBudget = TimeSpan.FromSeconds 30.0 }
        (fun () ->
            let backend = MyComputeBackend.create config
            {
                Dispatcher = backend
                Drive = fun handle outcome -> MyComputeBackend.forceStatus backend handle outcome
            })
```

`Drive` is the one thing a companion must supply beyond the dispatcher itself: *make the unit behind this handle reach this outcome*, however that backend expresses it — a table write for a request/response service, a status patch on a declared object for a watch-based one. It says nothing about *when*, because **no law polls once and asserts**: every terminal assertion goes through `settle`, which polls to a terminal outcome within `SettleBudget`. That is what lets one law text cover an immediately-consistent stub and an eventually-consistent watch with no branch. `SettleBudget` is a ceiling, never an asserted deadline — no case claims something happened *within* a time, so a slow machine can only push these toward "polled more often than needed".

### What the pack declares rather than demands

Two clauses in this seam are deliberately not absolute, and a pack that demanded them anyway would have stopped describing the contract and started describing the first implementation:

- **`HonoursIdempotency`.** `Submit`'s contract says an implementation *should* return the existing handle for a key it has already accepted. The platform cannot enforce it — it holds no record of the backend's own accepted keys, which is exactly why [the memoization decorator](#memoizing-a-repeated-submission) exists as the portable answer. Declare `true` and the pack demands same-key-same-handle (and that both handles report the one unit's single outcome); declare `false` and it demands only that the key is still *accepted*, never refused.
- **`ValidatesHandleScope`.** `Poll` and `Cancel` take a handle and no scope parameter — the scope rides the handle — so there is no scope argument for a caller to lie about. The one cross-scope shape this seam can express is a **re-scoped handle**: the same `HandleId` and `NativeRef` presented under a different `ScopeId`. A backend that keys its own record by scope refuses that; one that keys purely by the opaque `NativeRef` cannot tell, and demanding it would invent a requirement no HTTP or container backend can honour from the information it holds.

  **So GP 4 is enforced a layer up, and that layer has its own pack.** The completion-callback ingress takes the scope from the platform's stored record and never from the request, and `IExternalHandleStore` is scope-partitioned with a cross-check the record must survive. `IExternalHandleStoreContract` holds that under contract for every store implementation. The dispatcher law asserts what the dispatcher seam can honestly promise, and — where a backend declares it cannot check the scope — asserts instead the *precondition* the layer above depends on: that the handle carries its scope faithfully.

Declaring `false` on either is not a failing grade. It is a statement about where a guarantee comes from, and it is checked: the pack asserts the fallback rather than skipping the case.

### The packs have teeth, and that is tested

Both packs ship a `selfTests` list that runs the laws against deliberately non-conformant implementations and requires each to **fail** — a read-then-write `MarkTerminal`, a backend that forgets a terminal outcome after one read, one whose `Cancel` does nothing, one whose `Cancel` clobbers a completed result, one that flattens a retriable failure into a terminal one, one that ignores the handle's scope, one that refuses resource hints, one that mints a single shared handle.

Several of those cases assert the *inverse* as well: that the broken implementation still **passes** the laws which do not cover its defect. That is the evidence a given law is load-bearing rather than decorative, and it is not a formality — a read-then-write `MarkTerminal` passes every sequential case in the store pack, so without the two concurrency laws the pack would certify the one defect the gate exists to prevent.

A conformance pack that has never been shown to reject anything is a list of things that happened to be true of the implementation it was written beside.

### Bound implementations

The dispatcher pack runs against a reference request/response backend and against both shipped decorator stacks over it — the routing dispatcher (which restamps `ExternalHandle.Backend`) and the memoization decorator (which short-circuits `Poll` from a cache). The store pack runs against both shipped stores, whose atomicity primitives genuinely differ: a `ConcurrentDictionary` compare-and-set and an ETag conditional write.

Binding more than one is the point rather than a bonus. An interface only one implementation has ever satisfied is a description of that implementation, and a pack cannot distinguish a portable clause from an accidental one until a second backend with a different primitive has run through it.

## The shipped HTTP/REST backend

`ToolUp.ExternalCompute.Http` is the reference `IExternalComputeDispatcher` (GP 2): it hands work to **any** HTTP compute service — a self-hosted training server, an inference endpoint, a container/batch API with a REST facade, a Flask wrapper around a fit script — using nothing but BCL `HttpClient` and `System.Text.Json`. There is no vendor SDK to isolate, because "POST a job, GET its status" does not need one.

Everything service-specific is configuration: three URLs, an auth seam, the request-body field names, and the selectors that read a job id / status / progress / result reference back out of the service's own JSON.

### A worked example

An internal GPU training service. It answers a submit with `{"job":{"id":"…"}}`, a status read with `{"job":{"state":"RUNNING","percentComplete":40,…}}`, and it can POST a webhook when a run finishes.

```fsharp
open System.Net.Http
open ToolUp.Platform.ExternalCompute.Http
open ToolUp.Platform.Secrets
open ToolUp.Platform.Server

let composeTrainingCompute (secrets: ISecretStore) (httpClient: HttpClient) (logger: ILogger) =
    let jobs = "https://training.internal/api/jobs"

    let config =
        HttpComputeConfig.create
            "gpu-training" // the label stamped onto every handle's Backend
            jobs // POST here to submit
            (jobs + "/" + HttpComputeConfig.JobIdPlaceholder) // GET here to poll
            (JsonPath.ofString "job.id") // the job id, in the submit response
            (JsonPath.ofString "job.state") // the status, in the status response
        |> HttpComputeConfig.withAuth (HttpComputeAuth.bearer "training-api-token")
        |> HttpComputeConfig.withResultRef (JsonPath.ofString "job.artifact.uri")
        // The service reports a PERCENTAGE, so declare the scale rather
        // than let 40 be read as 4000%.
        |> HttpComputeConfig.withProgress 100.0 (JsonPath.ofString "job.percentComplete")
        |> HttpComputeConfig.withFailureDetail
            (JsonPath.ofString "job.failure.message")
            (Some(JsonPath.ofString "job.failure.retriable"))
        |> HttpComputeConfig.withCancel "DELETE" (jobs + "/" + HttpComputeConfig.JobIdPlaceholder)
        |> HttpComputeConfig.withHealthUrl "https://training.internal/healthz"
        |> HttpComputeConfig.withCallback {
            PublicBaseUrl = "https://app.example.com"
            RegistrationUrlTemplate = jobs + "/" + HttpComputeConfig.JobIdPlaceholder + "/webhook"
            RegistrationMethod = "POST"
            UrlField = "callbackUrl"
            SecretField = "callbackSecret"
            HandleIdField = Some "handleId"
        }

    ServerApp.empty
    |> HttpComputeCompose.withHttpCompute config secrets httpClient logger
```

`withHttpCompute` folds in everything the companion contributes: the dispatcher singleton, the readiness probe, the startup preflight, and `ServerConfig.ExternalCompute = CustomExternalCompute`. **That last part is not cosmetic** — under the `NoExternalCompute` default compose registers the no-op dispatcher, and a later registration of the same service type is what `GetService` resolves, so leaving the mode alone would make whether real work is submitted depend on registration order. A deployment that never calls the helper keeps the default and pays nothing (GP 11 + GP 13).

Environment-bound alternative: `HttpComputeConfig.fromEnv ()` returns `None` unless `TOOLUP_EXTERNAL_COMPUTE=http`, and otherwise `Ok config` or `Error problems` — every problem at once, rather than one exception per restart — read from `TOOLUP_EXTERNAL_COMPUTE_HTTP_*` variables.

### What goes on the wire

A handler's `Submit` becomes one POST. Field names come from `HttpComputeSubmitFields`, and an `option` field that is `None` is simply omitted:

```json
{
  "kind": "train-forecast",
  "payload": { "series": "sales", "horizon": 12 },
  "scope": "team-42",
  "resources": { "gpu": "1", "accelerator": "a100" },
  "timeoutSeconds": 5400,
  "idempotencyKey": "sales-h12-v3",
  "callbackUrl": "https://app.example.com/_platform/external-compute/callback"
}
```

The **callback URL** rides the submit request because it is deployment-static. The per-handle **secret** cannot: it does not exist until the platform has durably registered the handle this very request is about to return. So it arrives in a second request, to `RegistrationUrlTemplate`, the moment `AcceptCallbackCredential` fires:

```json
{
  "callbackUrl": "https://app.example.com/_platform/external-compute/callback",
  "callbackSecret": "…",
  "handleId": "6f1d5b0e-3a1c-4c8f-9f3e-2b7a5d4c1e08"
}
```

That delivery is best-effort by contract. If it fails, the failure is logged (never the secret) and the run resolves by poll — the fallback that was always there. A service that cannot call back at all simply omits `withCallback` and is reconciled by polling.

### The selector grammar, and where it deliberately stops

A selector is a **dotted path**: a `.`-separated sequence of property names, each optionally followed by one or more `[n]` array indices. `state`, `job.status`, `items[0].phase`, `result.refs[1]`. That is the entire grammar.

No wildcards, no filters, no expressions, no `$` root. Each of those buys a rarer response shape at the price of a second language inside the configuration — with its own parser, its own error messages, its own semantics to document and its own bugs. The line is drawn where the grammar would stop being describable in one sentence.

A response shape a dotted path cannot describe is a signal to write a companion that knows the service, **not** to grow the grammar: `IExternalComputeDispatcher` is twenty lines, and a service whose status hides behind a query expression is better served by an implementation that understands it than by a configuration pretending to be generic.

A malformed selector is refused with a reason rather than reinterpreted — `a..b`, `.a`, `a.`, `a[x]` and `a[-1]` are all errors, because a selector that quietly means something other than what was written is the worst outcome available.

### Status vocabulary, and the label that is never guessed

`HttpComputeStatusMap` maps the service's own status labels onto the five outcomes, case- and whitespace-insensitively. The defaults already cover most REST compute services (`queued` / `running` / `succeeded` / `failed` / `cancelled` plus the usual synonyms); a service that says `WORKING` adds it.

A label the map does not declare is reported as a **terminal failure naming the label**. It is not guessed, because every guess available is a claim about whether the work finished. A label declared under two classes is refused at compose, since which one won would be an accident of list order deciding whether a job is reported as complete.

The same restraint applies twice more. A `Succeeded` status with no readable result reference is a terminal failure, not a success — the caller's whole reason for polling is to learn where the result is. And a progress value that is absent, unreadable or negative yields `Running None` rather than a fabricated figure.

### Error classification is the retry contract

| Condition | `Retriable` |
|---|---|
| transport failure, or the per-request budget expired | **yes** — the request was never answered, so nothing was learned about the work |
| `5xx` | **yes** — the service's own admission that this is its problem |
| `408 Request Timeout`, `429 Too Many Requests` | **yes** — the two `4xx` codes that literally mean "ask again" |
| any other non-`2xx` | no — a statement about the request, which re-sending cannot change |
| `404` on a status read | no — the service has forgotten the unit |

The `408` / `429` rows are the general rule ("`5xx` and timeouts retriable, `4xx` not") with its two well-known exceptions named. Treating a rate-limit as terminal abandons perfectly good work exactly when a queue is deepest.

`Poll` returns `ExternalOutcome`, which has no error channel, so a transport failure has to be expressed as an outcome: it is reported as `Failed` carrying a **retriable** error — terminal in shape so the poller stops, with retriability as data so the scheduler decides. It never answers `Running` (which would keep a dead handle alive forever) and never a fabricated `Cancelled`.

An accepted submission whose job id is unreadable is a **terminal** error, not a retriable one, and the distinction is deliberate: the work may well be running, and a retry flag there would start a second unit while the first ran on unobserved.

### What this backend does not claim

- **It does not honour `ExternalWorkSpec.Idempotency` itself.** The key is forwarded when the config names a field for it, and a service that dedupes returns the same `NativeRef` — but the handle id is platform-minted per `Submit`, so a resubmit yields a second handle for the one unit. That is why Phase 318 words idempotent resubmit as a *should*, and why [the memoization decorator](#memoizing-a-repeated-submission) is the portable answer. The conformance binding declares `HonoursIdempotency = false`.
- **It does not validate the presented handle's scope.** `Poll` and `Cancel` address the service by the opaque `NativeRef`, which is all the service ever gave us, so a re-scoped handle is indistinguishable from here. GP 4 is enforced a layer up, where it is structural. Declared `ValidatesHandleScope = false`.
- **It declares no isolation posture**, so an `ExecutionProfile.Isolated` spec is refused by the gate rather than handed to a service that has made no guarantee. A generic HTTP endpoint cannot honestly assert no-egress.

### Health and preflight

The readiness probe exists **only** when the config names a dedicated health URL. Nothing else is safe to probe: the submit URL would submit work on every readiness poll, and a status URL needs a job id there is no safe value for. An absent probe is honest; a probe that queued a job every fifteen seconds would be a defect wearing a health check's name.

An unreachable service reports `Degraded`, not `Unhealthy`. External compute is a hand-off destination rather than a request path — the deployment still serves every page, every API and every in-process job; what it cannot do is accept new external work. Failing readiness would turn a partial outage into a total one.

The startup validator answers the two questions only a running deployment can. Is the configured credential actually in `ISecretStore`? — the most common deployment miss, which otherwise surfaces as a `401` on the first submission hours after deploy. And is the service reachable? Both report rather than abort: a compute service that is briefly down must not take the whole deployment with it.

### Conformance

It passes the `IExternalComputeDispatcher` contract pack unmodified, bound against an in-process stub compute service on a **real socket** rather than an `HttpMessageHandler` stub. That choice is the point: a fake transport would verify the half that is not in doubt while eliding the half that is — header names actually reaching the wire, a `404` arriving as a status code rather than a thrown exception, a budget expiring against a server that genuinely does not answer. The push path is exercised the same way: the stub service POSTs to the real callback ingress over a socket, with a forged-secret control beside it proving the ingress is not simply accepting anything.

## See also

- [`jobs.md`](jobs.md) — the in-process job scheduler this seam extends beyond the process.
- [`portability-rules.md`](portability-rules.md) — the six rules in full.
- [`which-store-for-what.md`](which-store-for-what.md) — choosing among the substrate seams.
