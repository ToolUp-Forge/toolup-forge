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