// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 318 — external-compute substrate: core types ─────────────────
//
// The missing abstraction: `IJobHandler.Execute` runs **in-process** on the
// web server, so there is no SDK concept of "run this on a machine other
// than the one serving the request". These types are that concept's value
// vocabulary — a submitted unit of work, an opaque handle to it, and the
// terminal outcome a poller reads.
//
// **The dispatcher brokers; it never runs.** A handler hands a
// `ExternalWorkSpec` to `IExternalComputeDispatcher.Submit`, receives an
// `ExternalHandle` immediately, and polls to a terminal `ExternalOutcome`.
// Nothing here executes heavy work, and nothing here describes how a
// backend schedules it — that is deliberately the backend's business.
//
// **No backend SDK types, by construction.** Every type below is BCL
// primitives + F# records/DUs, so the file ships in the Fable-packed source
// and a Client can hold an `ExternalHandle` opaquely (GP 10 + GP 12 rule 1).
// `NativeRef` is the one place a backend's own token lives, and it lives
// there as an opaque `string` the platform never parses.
//
// **Six portability rules (GP 12) — the type side.**
// 1. *Identity by value* — `ExternalHandle` is a record of `Guid` + strings
//    + `DateTime`; never a live job object, cluster reference, or SDK
//    client handle.
// 2. *Async at every boundary* — see `IExternalComputeDispatcher`
//    (Platform.Server); no type here carries a callback or a `Task`.
// 3. *Retry + supervision as data* — `ExternalWorkSpec.Timeout` and
//    `Idempotency` are fields, and a failure is a `ExternalComputeError`
//    record carrying `Retriable` as data. There is no `OnFailure` callback
//    and no exception path for a backend-reported failure.
// 4. *Stateless between invocations* — a spec carries its whole payload, so
//    a re-submitted spec needs nothing the dispatcher cached.
// 5. *No cross-shard ordering* — handles are independent; nothing here
//    promises submission order across two handles.
// 6. *Precision at the lower bound* — `Timeout` is a `TimeSpan` the backend
//    interprets as advisory-with-a-floor; `Running` progress is `float
//    option` precisely because a backend that cannot report progress says
//    `None` rather than inventing a number.

/// A structured external-compute failure. Retriability is **data**, not an
/// exception type (GP 12 rule 3) — a caller decides whether to re-submit by
/// reading `Retriable`, so the decision serialises across the wire and
/// survives a process restart.
type ExternalComputeError = {
    /// Operator-facing description of the failure. Backends should name
    /// themselves and the failing stage; never embed a credential.
    Message: string
    /// `true` when re-submitting the identical spec could plausibly
    /// succeed (transient backend saturation, a lease expiry, a network
    /// blip). `false` for a terminal refusal (a malformed payload, a
    /// backend that is not configured, an unknown `Kind`).
    Retriable: bool
}

module ExternalComputeError =
    /// A retriable failure — re-submitting the identical spec may succeed.
    let retriable (message: string) : ExternalComputeError = { Message = message; Retriable = true }

    /// A terminal failure — re-submitting the identical spec cannot help.
    let terminal (message: string) : ExternalComputeError = { Message = message; Retriable = false }

    /// The refusal a deployment with no external-compute backend composed
    /// returns from every `Submit` (GP 13 — the `NoExternalCompute`
    /// default). Terminal by construction: no amount of retrying composes
    /// a backend.
    let notConfigured: ExternalComputeError =
        terminal
            "No external-compute backend is configured. ServerConfig.ExternalCompute = NoExternalCompute (the default); set CustomExternalCompute and register an IExternalComputeDispatcher companion singleton to submit external work."

    /// One-line description for logs / audit payloads.
    let describe (error: ExternalComputeError) : string =
        if error.Retriable then
            sprintf "%s (retriable)" error.Message
        else
            sprintf "%s (terminal)" error.Message

// ─── Phase 478 — the isolated execution profile ──────────────────────────
//
// Computing *on* clean-room-gated data (split learning, private cohort
// modelling) needs a worker the substrate can trust not to leak. Phase 318
// gave the substrate a portable way to say WHERE work runs; it had no way
// to say anything at all about what that somewhere is allowed to do while
// it runs. `ExecutionProfile` is that missing axis, and it rides the spec
// as data (GP 12) precisely so it survives serialisation, a restart, and a
// hand-off to a backend forge has never heard of.
//
// **The default is `Standard`, and `Standard` is exactly Phase 318.** A
// spec built by `ExternalWorkSpec.create` carries `Standard`; every
// dispatcher answers it exactly as it did before this phase; no gate, no
// posture check, no branch beyond the one match that finds `Standard`
// (GP 11 / GP 13). Nothing about a non-federating deployment changes.

/// Phase 478 — how much the platform constrains the environment a unit of
/// external work runs in.
///
/// `[<RequireQualifiedAccess>]` for the reason `ExternalOutcome` carries
/// it: `Standard` is about as collision-prone as a case name gets in a
/// namespace this size, and an unqualified one is how a call site silently
/// binds the union you did not mean.
[<RequireQualifiedAccess>]
type ExecutionProfile =
    /// Phase 318's behaviour, unchanged. The backend runs the work however
    /// it normally does; the platform makes no isolation claim and checks
    /// none. Every spec is this unless it says otherwise.
    | Standard
    /// The work runs in a **clean-room-grade** environment: no egress
    /// beyond the completion callback, inputs limited to the refs the spec
    /// declares, and an ephemeral workspace destroyed with the worker.
    ///
    /// This is a *requirement on the backend*, not a request: a backend
    /// that does not declare the posture is refused the submission rather
    /// than handed it in the hope it behaves (see `IsolationPosture`).
    | Isolated

[<RequireQualifiedAccess>]
module ExecutionProfile =
    /// Stable lowercase label for logs / audit payloads / dev panels.
    let label =
        function
        | ExecutionProfile.Standard -> "standard"
        | ExecutionProfile.Isolated -> "isolated"

/// Phase 478 — what a backend guarantees about the environment it runs
/// `Isolated` work in. **The isolation posture contract, as data.**
///
/// Three clauses, deliberately no more. Each is a property an operator can
/// point at a concrete mechanism for, and together they are what makes
/// "the worker cannot leak the gated data it computed over" a claim rather
/// than a hope:
///
///   1. **No egress** beyond the completion callback. The worker reaches
///      no network destination of its own choosing — not a package index,
///      not a metrics endpoint, not an object store it was not handed.
///      Without this clause the other two buy nothing: a worker that can
///      open a socket does not need a durable workspace to exfiltrate.
///   2. **Inputs limited to the spec's declared refs.** The worker sees
///      the payload it was given and nothing else — no ambient credential,
///      no mounted host path, no sibling job's scratch space.
///   3. **Ephemeral workspace.** Storage is created with the worker and
///      destroyed with it, so an output that was withheld does not survive
///      the refusal on a disk somebody can later read.
///
/// `Enforcement` names the concrete mechanism — free text, because the
/// mechanism is the backend's business and a typed shape here would encode
/// one scheduler's vocabulary and stop being portable (the same argument
/// `ResourceHints` makes). It is recorded and echoed, never parsed.
///
/// **A backend declaring `true` is making an assertion the substrate
/// cannot verify from here, and that is the honest boundary.** What the
/// substrate CAN do — and does — is refuse to submit `Isolated` work to a
/// backend that has not made the assertion at all, and route the output
/// through the clean-room gate regardless of it. The posture narrows who
/// may be asked; the gate is what decides what may be seen.
type IsolationPosture = {
    /// Clause 1 — no network egress beyond the completion callback.
    NoEgress: bool
    /// Clause 2 — the worker's inputs are limited to the refs the
    /// `ExternalWorkSpec` declares.
    InputsRestrictedToDeclaredRefs: bool
    /// Clause 3 — the workspace is created with the worker and destroyed
    /// with it.
    EphemeralWorkspace: bool
    /// The concrete mechanism the backend enforces the clauses with (e.g.
    /// a network policy, a sandbox profile, a VM boundary). Opaque to the
    /// platform: recorded for audit and operator diagnosis, never parsed.
    Enforcement: string
}

[<RequireQualifiedAccess>]
module IsolationPosture =
    /// The posture an **undeclared** backend contributes: no clause
    /// asserted, so `Standard` work only.
    ///
    /// This is the identity value in the same sense
    /// `CompanionCapability.identity` is — a backend that says nothing is
    /// read as claiming nothing, never as claiming everything. A posture
    /// defaulting the other way would make forgetting to declare
    /// indistinguishable from declaring, which is the failure mode this
    /// whole phase exists to close.
    let standardOnly: IsolationPosture = {
        NoEgress = false
        InputsRestrictedToDeclaredRefs = false
        EphemeralWorkspace = false
        Enforcement = ""
    }

    /// A backend asserting all three clauses, enforced by the named
    /// mechanism. The only shape that honours `Isolated`.
    let clauses (enforcement: string) : IsolationPosture = {
        NoEgress = true
        InputsRestrictedToDeclaredRefs = true
        EphemeralWorkspace = true
        Enforcement = enforcement
    }

    /// The clauses this posture does NOT assert, named. Empty exactly when
    /// the posture honours `Isolated`.
    ///
    /// Returned as data rather than folded into a boolean so a refusal can
    /// tell an operator *which* guarantee is missing — "this backend
    /// declares no egress control" is actionable; "unsuitable" is not.
    let shortfall (posture: IsolationPosture) : string list = [
        if not posture.NoEgress then
            "no-egress (the worker may reach destinations beyond the completion callback)"
        if not posture.InputsRestrictedToDeclaredRefs then
            "declared-refs-only (the worker may read inputs the spec did not declare)"
        if not posture.EphemeralWorkspace then
            "ephemeral-workspace (the worker's storage may outlive it)"
    ]

    /// `true` when this posture can honour `profile`.
    ///
    /// `Standard` is honoured by everything, including the undeclared
    /// posture — that is what keeps Phase 318's path untouched. `Isolated`
    /// requires all three clauses: a partial posture is refused, because
    /// two of three is not a weaker clean room, it is a leak with a longer
    /// description.
    let honours (profile: ExecutionProfile) (posture: IsolationPosture) : bool =
        match profile with
        | ExecutionProfile.Standard -> true
        | ExecutionProfile.Isolated -> List.isEmpty (shortfall posture)

    /// One-line description for logs / audit payloads / operator panels.
    let describe (posture: IsolationPosture) : string =
        match shortfall posture with
        | [] ->
            let mechanism =
                if System.String.IsNullOrWhiteSpace posture.Enforcement then
                    "an undeclared mechanism"
                else
                    posture.Enforcement

            sprintf "isolated (no-egress + declared-refs-only + ephemeral-workspace, enforced by %s)" mechanism
        | missing -> sprintf "standard-only (missing: %s)" (String.concat "; " missing)

    /// The refusal a backend that has not declared the isolation posture
    /// returns when handed an `Isolated` spec.
    ///
    /// **Terminal, always.** Retrying an identical submission cannot make
    /// a backend isolating — that is a composition change, not a transient
    /// condition — and a `Retriable = true` here would have a caller
    /// re-offering gated work to a leaky worker on a timer.
    let refusal (backend: string) (posture: IsolationPosture) : ExternalComputeError =
        ExternalComputeError.terminal (
            sprintf
                "backend '%s' does not honour ExecutionProfile.Isolated: %s. Submit this work to a backend that declares the isolation posture, or drop the spec to ExecutionProfile.Standard if the payload is not clean-room data."
                backend
                (describe posture)
        )

/// An opaque handle to one unit of work accepted by an external compute
/// backend. Identity by value (GP 12 rule 1) — every field is a primitive,
/// so the same handle resolves from any node, survives a restart, and can
/// be persisted or handed to a Fable client without the client learning
/// anything about the backend.
type ExternalHandle = {
    /// Platform-minted identity for this submission. Stable for the life of
    /// the work; the value the platform keys its own records on.
    HandleId: Guid
    /// Stable identifier of the backend that accepted the work (the
    /// dispatcher's `Backend`, e.g. `"http-worker-pool"`). Recorded on the
    /// handle so a poll routes to the same backend that submitted.
    Backend: string
    /// Scope the work was submitted under (GP 4 — tenant isolation rides
    /// the handle, so a poll cannot read across tenants by construction).
    ScopeId: string
    /// The backend's own token for the work — a queue receipt, a job name,
    /// a URL fragment. **Opaque**: the platform stores and echoes it and
    /// never parses, validates, or derives meaning from it.
    NativeRef: string
    /// When the platform accepted the submission (UTC).
    SubmittedAt: DateTime
}

/// One unit of work to hand to an external compute backend. The payload is
/// **pre-serialised JSON** the backend understands: forge is the broker and
/// never interprets a domain payload, exactly as `IModelFitProvider` treats
/// an opaque spec.
type ExternalWorkSpec = {
    /// Backend-resolved work discriminator (e.g. `"train-forecast"`,
    /// `"render-scene"`). The backend maps `Kind` to whatever it runs; an
    /// unrecognised `Kind` is a terminal `ExternalComputeError`.
    Kind: string
    /// The work's own payload, already serialised to JSON by the caller.
    /// Opaque to the platform — never parsed, normalised, or re-hashed.
    Payload: string
    /// **Advisory** resource requests the backend interprets in its own
    /// vocabulary — e.g. `"gpu" -> "1"`, `"memory" -> "16Gi"`,
    /// `"accelerator" -> "a100"`. Deliberately a string map rather than a
    /// typed record: any typed shape would encode one scheduler's
    /// vocabulary and stop being portable (GP 12 rule 1). A backend that
    /// does not understand a hint ignores it; a backend that cannot honour
    /// a hint it *does* understand refuses with a terminal error rather
    /// than silently downgrading.
    ResourceHints: Map<string, string>
    /// Advisory wall-clock budget. `None` leaves the backend's own default
    /// in force. Precision is the backend's lower bound (GP 12 rule 6) — a
    /// backend whose scheduler ticks per minute documents that; no
    /// sub-second promise is implied.
    Timeout: TimeSpan option
    /// Caller-minted idempotency key. When `Some`, a backend that has
    /// already accepted this key for this scope returns the **existing**
    /// handle rather than starting the work twice. `None` means every
    /// `Submit` is a fresh submission.
    Idempotency: string option
    /// Phase 478 — how much the platform constrains the environment this
    /// work runs in. `Standard` (the default `create` sets) is Phase 318
    /// exactly; `Isolated` is a *requirement* the backend must declare it
    /// honours, not a hint it may ignore.
    ///
    /// It is a field on the spec and not a dispatcher argument for the
    /// portability reason the rest of this file is built on (GP 12 rule
    /// 3): the requirement travels with the work, so it survives being
    /// persisted, re-read after a restart, and handed to a backend by a
    /// process that is not the one that authored it. A profile passed
    /// beside the spec would be a promise only the submitting call frame
    /// could keep.
    Profile: ExecutionProfile
    /// Phase 451 — who asked for this work. Read only by compute-budget
    /// policy, which can hold `AgentInitiated` submissions to a tighter
    /// ceiling than `Human` ones without forge learning what an agent
    /// exploration is.
    ///
    /// On the spec for exactly the reason `Profile` is, one field above:
    /// the declaration travels with the work, so it survives being
    /// persisted, re-read after a restart, and handed to a dispatcher by a
    /// process that is not the one that authored it.
    ///
    /// `create` sets `SubmitterClass.Human`, so every pre-451 call site
    /// builds the identical spec it always did, and a deployment that
    /// composes no budget never reads the field at all (GP 11 + GP 13).
    SubmitterClass: SubmitterClass
}

module ExternalWorkSpec =
    /// A spec with no resource hints, no timeout, and no idempotency key —
    /// the minimum shape.
    ///
    /// Phase 478 — `Profile` defaults to `ExecutionProfile.Standard`, so
    /// every pre-478 call site builds the identical spec it always did and
    /// every dispatcher answers it identically (GP 11).
    let create (kind: string) (payload: string) : ExternalWorkSpec = {
        Kind = kind
        Payload = payload
        ResourceHints = Map.empty
        Timeout = None
        Idempotency = None
        Profile = ExecutionProfile.Standard
        // Phase 451 — the permissive class, so an existing call site is
        // never silently reclassified as agent traffic by an upgrade.
        SubmitterClass = SubmitterClass.Human
    }

    /// Add (or overwrite) one advisory resource hint.
    let withHint (key: string) (value: string) (spec: ExternalWorkSpec) : ExternalWorkSpec = {
        spec with
            ResourceHints = spec.ResourceHints |> Map.add key value
    }

    /// Declare an advisory wall-clock budget.
    let withTimeout (timeout: TimeSpan) (spec: ExternalWorkSpec) : ExternalWorkSpec = {
        spec with
            Timeout = Some timeout
    }

    /// Declare a caller-minted idempotency key, so a re-`Submit` of the
    /// identical spec returns the existing handle.
    let withIdempotency (key: string) (spec: ExternalWorkSpec) : ExternalWorkSpec = { spec with Idempotency = Some key }

    /// Phase 478 — declare the execution profile this work requires.
    let withProfile (profile: ExecutionProfile) (spec: ExternalWorkSpec) : ExternalWorkSpec = {
        spec with
            Profile = profile
    }

    /// Phase 451 — declare who asked for this work, so compute-budget
    /// policy can gate it by class.
    let withSubmitterClass (submitter: SubmitterClass) (spec: ExternalWorkSpec) : ExternalWorkSpec = {
        spec with
            SubmitterClass = submitter
    }

    /// Phase 478 — require a clean-room-grade worker: the submission is
    /// **refused** by any backend that has not declared the isolation
    /// posture, rather than run on one that might leak.
    let isolated (spec: ExternalWorkSpec) : ExternalWorkSpec =
        withProfile ExecutionProfile.Isolated spec

/// The state of one submitted unit of work, as read by `Poll`.
/// `[<RequireQualifiedAccess>]` for namespace hygiene — `Pending` /
/// `Running` / `Succeeded` / `Failed` / `Cancelled` are already case names
/// on `JobStatus`, `IngestionStatus` and the remoting long-running envelope,
/// and an unqualified fifth set in the same namespace would shadow them
/// (the `DatasetTypes` / `CompanionCapability` precedent).
///
/// Three terminal cases (`Succeeded` / `Failed` / `Cancelled`) and two
/// non-terminal (`Pending` / `Running`) — see `ExternalOutcome.isTerminal`.
[<RequireQualifiedAccess>]
type ExternalOutcome =
    /// Accepted by the backend, not yet started (queued / awaiting a slot).
    | Pending
    /// Executing. `progress` is a `0.0 .. 1.0` fraction when the backend
    /// reports one, and `None` when it cannot — a backend never invents a
    /// number to fill the field.
    | Running of progress: float option
    /// Terminal success. `resultRef` is an opaque backend reference to the
    /// result (a blob key, an artefact URI, a content hash); the platform
    /// echoes it and never dereferences or parses it.
    | Succeeded of resultRef: string
    /// Terminal failure, carrying the structured error (retriability as
    /// data — GP 12 rule 3).
    | Failed of ExternalComputeError
    /// Terminal cancellation, whether requested via `Cancel` or decided by
    /// the backend (pre-emption, a timeout, an operator action).
    | Cancelled

module ExternalOutcome =
    /// `true` for the three terminal cases. A poller stops on terminal;
    /// `Pending` / `Running` mean poll again.
    let isTerminal =
        function
        | ExternalOutcome.Succeeded _
        | ExternalOutcome.Failed _
        | ExternalOutcome.Cancelled -> true
        | ExternalOutcome.Pending
        | ExternalOutcome.Running _ -> false

    /// Stable lowercase label for logs / audit payloads / dev panels.
    let label =
        function
        | ExternalOutcome.Pending -> "pending"
        | ExternalOutcome.Running _ -> "running"
        | ExternalOutcome.Succeeded _ -> "succeeded"
        | ExternalOutcome.Failed _ -> "failed"
        | ExternalOutcome.Cancelled -> "cancelled"

/// Phase 318 — selects the external-compute substrate
/// (`IExternalComputeDispatcher`). Default: `NoExternalCompute` — the
/// `NoExternalComputeDispatcher` is registered, so the seam always resolves
/// and every `Submit` returns `ExternalComputeError.notConfigured`; no
/// background service runs and no dependency is pulled (GP 13). Mirrors
/// `TelemetrySinkMode` / `DatasetStoreMode` (no-op default + custom
/// companion), not `ModelFittingMode` (binary enable) — the seam has a
/// meaningful, resolvable default and the "enabled" form is always a
/// companion.
type ExternalComputeMode =
    /// No backend composed (default). `IExternalComputeDispatcher` resolves
    /// to `NoExternalComputeDispatcher`, whose `Submit` returns a clean
    /// not-configured `Error`. A bare `ServerApp.empty |> ServerApp.run`
    /// builds + runs unchanged (GP 11 + GP 13).
    | NoExternalCompute
    /// A companion under `src/ExternalCompute/` (an HTTP worker pool, a
    /// container/batch backend, a future managed service) is registered in
    /// DI by the deployment; `compose` registers no default and leaves the
    /// consumer's `IExternalComputeDispatcher` singleton in place.
    | CustomExternalCompute

// ─── Phase 320 — the completion-callback wire contract ───────────────
//
// Polling (Phase 319) is the universal fallback and stays the fallback.
// This is the push path: a backend that supports webhooks calls the
// platform the moment its work reaches a terminal state, and the run
// resolves with no poll latency.
//
// **Why a flat record rather than `ExternalOutcome` on the wire.** The
// caller is a GPU / batch service that is almost certainly not written
// in F#, and `ExternalOutcome` is a DU with a nested
// `ExternalComputeError` record — serialisable, but only through a
// converter set no third-party backend has. The wire shape is therefore
// five primitive fields a shell script can emit with `curl`, and
// `ExternalCallback.toOutcome` is the one place that lifts it back into
// the typed outcome the scheduler already knows how to apply. Same
// reasoning that put JSON-RPC 2.0 on the peer seam rather than the
// in-tree typed transport: an open contract at the boundary, typed
// values inside it.
//
// **Only terminal statuses are accepted.** A callback exists to deliver
// an outcome, and `Pending` / `Running` are not outcomes — a backend
// reporting progress is Phase 321's surface, and accepting it here
// would mean the ingress could be handed a status that resolves
// nothing while still consuming the handle's one-shot terminal claim.
// `toOutcome` refuses them by name.

/// Phase 320 — the per-handle callback credential the platform mints at
/// hand-off and hands to a callback-capable backend, so the backend can
/// authenticate itself when it calls back.
///
/// The platform stores only `Secret`'s **hash**; this record is the one
/// and only time the cleartext exists outside the backend. Identity by
/// value (GP 12 rule 1) — three primitives, so it survives being
/// persisted by the backend alongside its own job record.
type ExternalCallbackCredential = {
    /// The handle the callback resolves. Echoed back as
    /// `ExternalCallbackPayload.HandleId`.
    HandleId: Guid
    /// High-entropy per-handle secret, presented in the
    /// `ExternalCallback.SecretHeader` request header. **Never logged,
    /// never audited, never persisted platform-side in cleartext.**
    Secret: string
    /// Platform path the callback is POSTed to
    /// (`ExternalCallback.Route`). Carried on the credential rather than
    /// left for the backend to hardcode, so a deployment that mounts the
    /// platform under a prefix hands the right path out.
    CallbackPath: string
}

/// Phase 320 — the callback request body. Flat primitives by design
/// (see the section header); `ExternalCallback.toOutcome` validates and
/// lifts it into an `ExternalOutcome`.
type ExternalCallbackPayload = {
    /// `ExternalHandle.HandleId` of the work that finished. The **only**
    /// routing input the platform accepts from the caller — the scope,
    /// the job run, and the backend all come from the platform's own
    /// stored record, never from the request (GP 4: a caller cannot name
    /// a scope it does not already own a handle in).
    HandleId: Guid
    /// Terminal status label, matching `ExternalOutcome.label`:
    /// `"succeeded"` | `"failed"` | `"cancelled"`. Case-insensitive.
    Status: string
    /// Opaque backend reference to the result, required for
    /// `"succeeded"` and ignored otherwise.
    ResultRef: string option
    /// Failure description, required for `"failed"` and ignored
    /// otherwise.
    Error: string option
    /// Whether a re-submission could plausibly succeed. Read only for
    /// `"failed"`; `None` is treated as `false` (terminal), because a
    /// backend that does not say is not asserting the work is worth
    /// retrying, and defaulting the other way would re-submit work on a
    /// backend's silence.
    Retriable: bool option
}

[<RequireQualifiedAccess>]
module ExternalCallback =
    /// The ingress path. Mounted only when the deployment composed an
    /// external-compute backend (GP 13) — absent, and therefore a clean
    /// 404, on every deployment that did not.
    [<Literal>]
    let Route = "/_platform/external-compute/callback"

    /// Request header carrying `ExternalCallbackCredential.Secret`.
    ///
    /// A header, not a query parameter: query strings land in access
    /// logs, proxy logs and `Referer` headers, and a per-handle bearer
    /// secret in an access log is a credential leak that survives in
    /// whatever log aggregator the deployment ships to.
    [<Literal>]
    let SecretHeader = "X-ToolUp-External-Callback-Secret"

    /// Lift a wire payload into the typed outcome, or explain why it is
    /// not one.
    ///
    /// Refusals are all **shape** problems the caller can fix, and are
    /// deliberately distinct from an authentication failure: a backend
    /// that posts `"running"` has a bug, not a forged credential, and
    /// conflating the two would bury a real forged-callback signal under
    /// a misconfigured integration's noise.
    let toOutcome (payload: ExternalCallbackPayload) : Result<ExternalOutcome, string> =
        match
            (if isNull payload.Status then
                 ""
             else
                 payload.Status.Trim().ToLowerInvariant())
        with
        | "succeeded" ->
            match payload.ResultRef with
            | Some r when not (String.IsNullOrWhiteSpace r) -> Ok(ExternalOutcome.Succeeded r)
            | _ -> Error "status 'succeeded' requires a non-empty resultRef"
        | "failed" ->
            match payload.Error with
            | Some e when not (String.IsNullOrWhiteSpace e) ->
                Ok(
                    ExternalOutcome.Failed {
                        Message = e
                        Retriable = payload.Retriable |> Option.defaultValue false
                    }
                )
            | _ -> Error "status 'failed' requires a non-empty error"
        | "cancelled" -> Ok ExternalOutcome.Cancelled
        | "pending"
        | "running" ->
            Error
                "status 'pending'/'running' is not a terminal outcome; the completion callback delivers terminal outcomes only (progress reporting is a separate surface)"
        | other -> Error $"unrecognised status '%s{other}'; expected one of succeeded, failed, cancelled"

    /// Build the wire payload for a terminal outcome — the shape a
    /// backend emits. `Error` for a non-terminal outcome, which has no
    /// wire form here by construction.
    let ofOutcome (handleId: Guid) (outcome: ExternalOutcome) : Result<ExternalCallbackPayload, string> =
        let baseline = {
            HandleId = handleId
            Status = ExternalOutcome.label outcome
            ResultRef = None
            Error = None
            Retriable = None
        }

        match outcome with
        | ExternalOutcome.Succeeded resultRef ->
            Ok {
                baseline with
                    ResultRef = Some resultRef
            }
        | ExternalOutcome.Failed error ->
            Ok {
                baseline with
                    Error = Some error.Message
                    Retriable = Some error.Retriable
            }
        | ExternalOutcome.Cancelled -> Ok baseline
        | ExternalOutcome.Pending
        | ExternalOutcome.Running _ ->
            Error $"%s{ExternalOutcome.label outcome} is not terminal and has no completion-callback form"

// ─── Phase 486 — signed worker outcomes (the wire half) ──────────────
//
// Phase 440 and Phase 320 authenticate the **transport**: a caller that
// holds the per-handle secret may resolve that handle. Neither says
// anything about *which worker* produced the result, nor binds the result
// to the worker that computed it. A compromised relay, a
// mis-routed queue consumer, or a backend node re-using another node's
// credential all satisfy Phase 320 and are indistinguishable from the
// honest case. This is the missing attribution: the worker signs its own
// outcome with a **registered per-worker key**, and the ingress verifies
// the signature before the outcome is accepted.
//
// **It rides a HEADER, not the body — and that is load-bearing.** The
// obvious design adds six fields to `ExternalCallbackPayload`. It was
// rejected for three reasons, in increasing order of importance:
//   1. `ExternalCallbackPayload` is the *outcome* contract, and a
//      credential is not an outcome. The per-handle secret already sits
//      in a header for exactly this reason.
//   2. Six additive fields retype the record's constructor, which is a
//      break for every consumer that builds one and a churn on the
//      public-API baseline, for a capability most deployments never use.
//   3. **GP 11 becomes structural rather than argued.** An unsigned
//      deployment sends no header, and the ingress reads no header — the
//      request, the parse, the record and the resolution are
//      byte-for-byte what Phase 320 produced. There is no "the field was
//      `None`" path to reason about.
//
// **The signature binds the BODY, via the artifact hash.** The signed
// tuple is `(handleId, artifactHash, diagnosticsHash, timestamp)`, and
// `artifactHash` is the hash of the *canonical descriptor of the outcome
// the callback delivers* (`outcomeDescriptor` below). So a relay that
// replays a genuine envelope against a substituted `resultRef` produces a
// descriptor that no longer hashes to the signed `artifactHash`, and the
// ingress refuses it. Without that binding the signature would attest
// only that *some* worker said *something* about this handle, which is
// attribution without integrity — the failure mode this phase exists to
// close.
//
// **The algorithm is NOT read from the wire.** It comes from the
// registered key. A caller-supplied algorithm field is a downgrade
// oracle: an attacker who can name the algorithm can name the weakest one
// the server implements. The envelope records which algorithm verified it
// *after* the fact, for attribution; it never selects one.
//
// **Forward compatibility is deliberate.** Unknown parameters in the
// header are ignored, so the envelope can gain a TEE-attestation
// parameter later without a redesign or a version bump for every existing
// worker. The trade is explicit: a parameter an older server does not know
// is a parameter it does not verify, which is why the *signed* tuple is
// closed and versioned (`v=1`) even though the *parameter list* is open.

/// Phase 486 — the signature algorithm a registered worker key uses.
///
/// Two cases, and the asymmetry between them is a fact about the runtime,
/// not a preference: **.NET 10's BCL has no Ed25519 primitive** (verified
/// by scanning `System.Security.Cryptography` 10.0.0.0 — four `ECDsa*`
/// types, zero `Ed*`), so `Es256` is the only algorithm the platform can
/// verify without a third-party crypto dependency (GP 1 / GP 2).
/// `Ed25519` is therefore expressible, registrable, and verified by a
/// composed companion verifier — never by the in-tree default, which
/// refuses it by name rather than pretending.
[<RequireQualifiedAccess>]
type WorkerKeyAlgorithm =
    /// ECDSA on NIST P-256 with SHA-256, IEEE-P1363 (`r || s`) signature
    /// encoding — the JWS `ES256` shape. Verified in-tree with BCL
    /// `System.Security.Cryptography`; no vendor dependency.
    | Es256
    /// EdDSA on Curve25519 (Ed25519). No BCL primitive exists on .NET 10,
    /// so verification requires a composed companion verifier; the
    /// in-tree default refuses it with a message naming that.
    | Ed25519

[<RequireQualifiedAccess>]
module WorkerKeyAlgorithm =
    /// Stable lowercase label for logs / audit payloads / stored records.
    let label =
        function
        | WorkerKeyAlgorithm.Es256 -> "es256"
        | WorkerKeyAlgorithm.Ed25519 -> "ed25519"

    /// Parse a stored / operator-supplied label. `None` for anything else
    /// — an unrecognised algorithm is never coerced to a default, because
    /// the default would be the one an attacker picks.
    let parse (value: string) : WorkerKeyAlgorithm option =
        match (if isNull value then "" else value.Trim().ToLowerInvariant()) with
        | "es256" -> Some WorkerKeyAlgorithm.Es256
        | "ed25519" -> Some WorkerKeyAlgorithm.Ed25519
        | _ -> None

/// Phase 486 — the signature envelope a worker presents alongside its
/// completion callback, as parsed from the request header.
///
/// Identity by value (GP 12 rule 1): six strings, no live handle, no key
/// material. `SignedAt` is the **literal text the worker signed** and is
/// never reformatted — a normalising round-trip through `DateTimeOffset`
/// would change the bytes and break every signature it touched, which is
/// the classic canonicalisation defect.
type WorkerOutcomeSignature = {
    /// Stable identity of the worker that produced the outcome. Resolved
    /// against the worker key registry; never trusted on its own.
    WorkerId: string
    /// Which of that worker's registered keys signed. Present so a worker
    /// can rotate without a flag day: the old and new keys are both
    /// registered, and each signature names the one it used.
    KeyId: string
    /// The signing timestamp, exactly as signed. Verified for freshness
    /// against the platform clock; the *text* is what enters the signing
    /// payload.
    SignedAt: string
    /// Lowercase hex SHA-256 over `outcomeDescriptor` of the terminal
    /// outcome this callback delivers. The binding between the signature
    /// and the body.
    ArtifactHash: string
    /// Lowercase hex SHA-256 over the worker's diagnostics bundle. A
    /// **commitment**, not a reference: the platform records it and never
    /// dereferences it, so what the signature buys is that the
    /// diagnostics a worker later produces can be checked against what it
    /// committed to at completion time.
    DiagnosticsHash: string
    /// Base64url (unpadded) signature over `signingPayload`.
    Signature: string
}

[<RequireQualifiedAccess>]
module WorkerOutcomeSignature =
    /// Request header carrying the envelope.
    ///
    /// A header for the same reason `ExternalCallback.SecretHeader` is one
    /// — and additionally so that an unsigned deployment's request is
    /// byte-for-byte a Phase 320 request (see the section header).
    [<Literal>]
    let Header = "X-ToolUp-Worker-Signature"

    /// Domain separation tag opening the signing payload. Prevents a
    /// signature minted for one ToolUp protocol being replayed as another
    /// — the reason every signed shape in the SDK carries one.
    ///
    /// **Phase 654 — taken from `SignedShape`, not written out here.**
    /// Two consequences worth stating, because both are visible changes:
    ///
    ///   * It is a `let`, not a `[<Literal>]` — a literal cannot hold a
    ///     function call. The value is only ever used as a string (it is
    ///     concatenated into `signingPayload`), never in a pattern match
    ///     or an attribute argument, so nothing needed it to be constant.
    ///   * Its VALUE moved, from `toolup.signed-outcome.v1` to
    ///     `toolup.signed-outcome/1`. That is a **breaking wire change**:
    ///     a worker signature minted under the old tag no longer
    ///     verifies. The `toolup` branding is deliberate and unchanged —
    ///     this names a ToolUp-specific protocol whose header is literally
    ///     `X-ToolUp-Worker-Signature` — and only the version suffix moved
    ///     into the scheme every other signed shape already used. See
    ///     `docs/migrations/2026-08-18-federation-wire-rename.md`.
    let Domain = SignedShape.separator SignedShape.WorkerSignedOutcome

    /// The envelope-format version the `v` parameter must carry. The
    /// *signed tuple* is closed and versioned; the parameter list is open
    /// (unknown parameters ignored). See the section header.
    [<Literal>]
    let Version = "1"

    /// Longest header this parser will look at. A bound, not a policy:
    /// the envelope is ~300 characters and a megabyte of `k=v` pairs is a
    /// parser-pressure probe, not a worker.
    [<Literal>]
    let MaxHeaderLength = 2048

    /// Longest accepted `worker` / `key` identifier.
    [<Literal>]
    let MaxIdentifierLength = 128

    /// Is `value` a safe identifier — `A-Z a-z 0-9 . _ : -`, non-empty,
    /// within `MaxIdentifierLength`?
    ///
    /// Restrictive **by intent**. These two values are echoed into logs,
    /// audit payloads and a JSON response, and they arrive from an
    /// unauthenticated request: a permissive charset here is how a newline
    /// gets into a log line and a `,` gets into the parameter list it was
    /// split on.
    let isSafeIdentifier (value: string) : bool =
        not (String.IsNullOrEmpty value)
        && value.Length <= MaxIdentifierLength
        && value
           |> Seq.forall (fun c ->
               (c >= 'a' && c <= 'z')
               || (c >= 'A' && c <= 'Z')
               || (c >= '0' && c <= '9')
               || c = '.'
               || c = '_'
               || c = ':'
               || c = '-')

    /// Is `value` a lowercase hex SHA-256 digest (64 hex characters)?
    ///
    /// Lowercase is *required*, not normalised: the digest text enters the
    /// signing payload, so accepting either case would make two distinct
    /// payloads verify against one signature and turn the hash comparison
    /// into a case-folding question.
    let isHexDigest (value: string) : bool =
        not (isNull value)
        && value.Length = 64
        && value |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

    /// Is `value` unpadded base64url (`A-Z a-z 0-9 - _`)?
    ///
    /// Unpadded, so the value cannot contain `=` — which is what lets the
    /// parameter parser split on the FIRST `=` and reject any value
    /// containing another, closing the "smuggle a second parameter inside
    /// a value" shape without a stateful tokeniser.
    let isBase64Url (value: string) : bool =
        not (String.IsNullOrEmpty value)
        && value
           |> Seq.forall (fun c ->
               (c >= 'a' && c <= 'z')
               || (c >= 'A' && c <= 'Z')
               || (c >= '0' && c <= '9')
               || c = '-'
               || c = '_')

    /// The canonical descriptor of a **terminal** outcome — the text the
    /// `ArtifactHash` is taken over, and therefore the exact part of the
    /// callback body the signature binds.
    ///
    /// One line per case, prefixed by the outcome label so no two cases
    /// can ever produce the same descriptor (a `Succeeded ""` and a
    /// `Cancelled` must not collide, or a relay could convert one into the
    /// other under a valid signature). `Error` for a non-terminal outcome,
    /// which has no completion form at all.
    let outcomeDescriptor (outcome: ExternalOutcome) : Result<string, string> =
        match outcome with
        | ExternalOutcome.Succeeded resultRef -> Ok $"succeeded:%s{resultRef}"
        | ExternalOutcome.Failed error ->
            let retriable = if error.Retriable then "true" else "false"
            Ok $"failed:%s{retriable}:%s{error.Message}"
        | ExternalOutcome.Cancelled -> Ok "cancelled:"
        | ExternalOutcome.Pending
        | ExternalOutcome.Running _ ->
            Error $"%s{ExternalOutcome.label outcome} is not terminal and has no signed-outcome descriptor"

    /// The exact text a worker signs, and the exact text the platform
    /// verifies against. Newline-separated so no field's content can
    /// consume another's boundary (`worker` and `key` are charset-limited
    /// and the two digests are hex, so none of them can contain `\n`).
    ///
    /// `handleId` comes from the callback body and is rendered
    /// lowercase-`D` — the platform's own canonical `Guid` text — so a
    /// worker that upper-cases or brace-wraps its handle id still signs
    /// the same bytes the platform verifies.
    let signingPayload (handleId: Guid) (envelope: WorkerOutcomeSignature) : string =
        String.concat "\n" [
            Domain
            (string handleId).ToLowerInvariant()
            envelope.ArtifactHash
            envelope.DiagnosticsHash
            envelope.SignedAt
        ]

    /// Render an envelope into its header form — the shape a worker emits
    /// and the shape `parse` accepts. Shipped (rather than left to each
    /// worker) so the emitting and parsing halves cannot drift, and so a
    /// test signs what the ingress reads by construction.
    let render (envelope: WorkerOutcomeSignature) : string =
        String.concat "," [
            $"v=%s{Version}"
            $"worker=%s{envelope.WorkerId}"
            $"key=%s{envelope.KeyId}"
            $"t=%s{envelope.SignedAt}"
            $"artifact=%s{envelope.ArtifactHash}"
            $"diagnostics=%s{envelope.DiagnosticsHash}"
            $"sig=%s{envelope.Signature}"
        ]

    /// Parse a header into an envelope, or say why it is not one.
    ///
    /// Every refusal is a **shape** problem the emitting worker can fix,
    /// and is deliberately distinct from a verification failure for the
    /// reason `ExternalCallback.toOutcome`'s refusals are: a worker with a
    /// malformed header has a bug, not a forged key, and conflating the
    /// two buries the forgery signal under integration noise. The ingress
    /// still refuses both uniformly on the wire.
    let parse (header: string) : Result<WorkerOutcomeSignature, string> =
        if String.IsNullOrWhiteSpace header then
            Error "the signature header is empty"
        elif header.Length > MaxHeaderLength then
            Error $"the signature header exceeds %d{MaxHeaderLength} characters"
        else
            let parameters =
                header.Split ','
                |> Array.map _.Trim()
                |> Array.filter (fun part -> part <> "")
                |> Array.fold
                    (fun acc part ->
                        match acc with
                        | Error _ -> acc
                        | Ok pairs ->
                            match part.IndexOf '=' with
                            | -1 -> Error $"parameter '%s{part}' is not a key=value pair"
                            | at ->
                                let key = part.Substring(0, at).Trim().ToLowerInvariant()
                                let value = part.Substring(at + 1).Trim()

                                if key = "" then
                                    Error "a parameter has an empty name"
                                elif value.Contains "=" then
                                    Error $"parameter '%s{key}' has a value containing '='"
                                else
                                    // Last wins is NOT acceptable here: a
                                    // duplicate parameter is exactly how a
                                    // parser-differential attack gets one
                                    // side to read `worker=a` and the other
                                    // `worker=b`.
                                    Ok(pairs |> Map.add key value))
                    (Ok Map.empty)

            let duplicated =
                header.Split ','
                |> Array.choose (fun part ->
                    match part.Trim().IndexOf '=' with
                    | -1 -> None
                    | at -> Some(part.Trim().Substring(0, at).Trim().ToLowerInvariant()))
                |> Array.countBy id
                |> Array.filter (fun (_, count) -> count > 1)
                |> Array.map fst

            match parameters with
            | Error e -> Error e
            | Ok _ when duplicated.Length > 0 -> Error $"""duplicate parameter(s): %s{String.concat ", " duplicated}"""
            | Ok pairs ->
                let required (name: string) =
                    match pairs.TryFind name with
                    | Some v when v <> "" -> Ok v
                    | _ -> Error $"parameter '%s{name}' is missing or empty"

                match required "v" with
                | Error e -> Error e
                | Ok version when version <> Version ->
                    Error $"unsupported signature envelope version '%s{version}'; this platform accepts v=%s{Version}"
                | Ok _ ->
                    match
                        required "worker", required "key", required "t", required "artifact", required "diagnostics"
                    with
                    | Ok workerId, Ok keyId, Ok signedAt, Ok artifact, Ok diagnostics ->
                        match required "sig" with
                        | Error e -> Error e
                        | Ok signature ->
                            if not (isSafeIdentifier workerId) then
                                Error "parameter 'worker' is not a valid identifier"
                            elif not (isSafeIdentifier keyId) then
                                Error "parameter 'key' is not a valid identifier"
                            elif not (isHexDigest artifact) then
                                Error "parameter 'artifact' is not a lowercase hex SHA-256 digest"
                            elif not (isHexDigest diagnostics) then
                                Error "parameter 'diagnostics' is not a lowercase hex SHA-256 digest"
                            elif not (isBase64Url signature) then
                                Error "parameter 'sig' is not unpadded base64url"
                            elif String.IsNullOrWhiteSpace signedAt || signedAt.Length > MaxIdentifierLength then
                                Error "parameter 't' is missing or implausibly long"
                            else
                                Ok {
                                    WorkerId = workerId
                                    KeyId = keyId
                                    SignedAt = signedAt
                                    ArtifactHash = artifact
                                    DiagnosticsHash = diagnostics
                                    Signature = signature
                                }
                    | Error e, _, _, _, _
                    | _, Error e, _, _, _
                    | _, _, Error e, _, _
                    | _, _, _, Error e, _
                    | _, _, _, _, Error e -> Error e